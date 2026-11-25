// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Globalization;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Recipes;

/// <summary>
/// A recipe store that outlives the process, by keeping each recipe in a file.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RecipeInMemoryStore"/> loses everything when the equipment is upgraded, crashes or
/// loses power, and a recipe is the thing in this library a host would least like to send twice.
/// This writes a recipe to disk before it answers S15F8, and adopts what is still there when it is
/// next constructed.
/// </para>
/// <para>
/// <strong>The shape is <c>GemFileSpool</c>'s and so are its reasons.</strong> One file per recipe
/// rather than one log: a log needs a durable cursor, compaction and a checksum to find the record
/// a power cut caught halfway, where a file per recipe replaces all three with a rename - the
/// record is written under a temporary name and <c>File.Move</c> within one directory is atomic, so
/// a recipe is either a whole record or not a record at all. Writes go through
/// <see cref="FileOptions.WriteThrough"/>, a disk round trip per recipe, which is the point: a
/// buffered write the operating system has not flushed is exactly what a power cut takes, and a
/// store that lost a recipe it had acknowledged would be worse than no store.
/// </para>
/// <para>
/// <strong>A file name carries nothing but a sequence number, and the recipe's namespace and
/// identifier live inside the record.</strong> Both halves of that matter. Identity inside the
/// record is what lets the constructor adopt a directory it has never seen, and it means no name a
/// host chose is ever part of a path - so there is nothing to escape, no reserved device name to
/// collide with, and two identifiers that differ only in case stay two recipes on a file system
/// that would have made them one. And because the file name says nothing about which recipe it
/// holds, <see cref="RenameAsync"/> and an overwriting <see cref="SaveAsync"/> are each a single
/// atomic rewrite of one file: <strong>a recipe never has two records</strong>, so no interruption
/// can leave a duplicate for the next start to choose between.
/// </para>
/// <para>
/// <strong>The index is in memory and the files are the durable copy</strong>, so listing, locking
/// and refusing cost no directory scan. Unlike the spool the bodies are <em>not</em> held: a
/// machine with a hundred recipes of a megabyte should not carry a hundred megabytes to answer
/// S15F9, so <see cref="LoadAsync"/> reads the one file it needs. The scan happens once, in the
/// constructor.
/// </para>
/// <para>
/// <strong>What comes back after a restart is what a host asked for.</strong> Namespaces, recipes,
/// locks and the selection are all things a host told the machine, so all four are durable - the
/// rule every state machine in this repository keeps. <strong>Download grants are not.</strong> A
/// grant is a transaction in flight: the S15F5 was answered and the S15F7 never arrived, so it ends
/// the way an interruption ends and the host asks again. That is the judgement E87's and E94's
/// snapshots make about anything in progress, and <see cref="GrantedCount"/> is zero on every open.
/// </para>
/// <para>
/// A lock therefore outlives the host that took it, and that consequence is E5's rather than this
/// class's: stream 15 has no message that asks which recipes are locked and none that forces one
/// open, so a host that crashes holding a token leaves a recipe locked.
/// <see cref="ForgetLocksAsync"/> is the door the standard does not give, for the operator standing
/// at the machine.
/// </para>
/// <para>
/// <strong>No <c>Capture</c> and no <c>Restore</c>.</strong> This is durable through an
/// implementation, like the GEM spool and for its reason: a store is not a state machine claiming
/// something about itself, and a snapshot pair here would be a third mechanism over two.
/// </para>
/// <para>
/// One store per directory. Two over the same one would fight over the sequence numbers and hand
/// each other's recipes to the same host.
/// </para>
/// </remarks>
public sealed class RecipeFileStore : IRecipeStore
{
    /// <summary>The extension a finished recipe record carries.</summary>
    public const string RecordExtension = ".recipe";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    /// <summary>The file the namespaces are listed in.</summary>
    public const string NamespacesFile = "namespaces";

    /// <summary>The file the locks are kept in.</summary>
    public const string LocksFile = "locks";

    /// <summary>The file the selection is kept in.</summary>
    public const string SelectedFile = "selected";

    /// <summary>The file the recipe owners are kept in.</summary>
    /// <remarks>
    /// A side file and not a field of the record, because the record's fields are two and then a
    /// name-and-value pair per attribute - an <strong>even</strong> count, which is what lets a
    /// store read a directory an earlier version of this library wrote. A single extra field would
    /// break that parity for every record ever written. The locks are kept outside the record for
    /// the same reason and adopted by the same rule.
    /// </remarks>
    public const string OwnersFile = "owners";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DurableRecordDirectory _records;
    private readonly ConcurrentDictionary<string, bool> _namespaces = new(StringComparer.Ordinal);
    // The sequence number of the record AND the length of the body in it. Both are facts about
    // the one record, so they travel together: S15F4 reports a length, and reading every file to
    // find one would undo the reason this store holds no bodies in the first place.
    private readonly ConcurrentDictionary<(string Namespace, string Id), (long Sequence, uint Length)> _recipes = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), uint> _granted = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), RecipeLockToken> _locks = new();

    // Encoded, not as a SecsItem, for the reason ItemFileStore keeps stream 21's attributes that
    // way: the bytes are what was written down, and decoding fresh per request is what stops one
    // caller's item being handed to another.
    private readonly ConcurrentDictionary<(string Namespace, string Id, string Attribute), byte[]> _attributes = new();

    // The owner a host set at S15F49, encoded, for the reason the attributes are encoded.
    private readonly ConcurrentDictionary<(string Namespace, string Id), byte[]> _owners = new();

    // And the attributes of a NAMESPACE, which are NOT written down. No row in the reference sets
    // one, so this is not what a host told the machine: it is the application's own wiring,
    // supplied on every start like the directory path and the logger - which is exactly why the
    // service program store does not write its Run delegate down either. An application that sets
    // these once and expects a restart to remember them is the one case where this store answers
    // differently from its in-memory sibling, and the README says so.
    private readonly ConcurrentDictionary<(string Namespace, string Attribute), SecsItem> _namespaceAttributes = new();

    private readonly ILogger _logger;

    private Selection? _selected;
    private long _next;
    private long _minted;

    /// <summary>Opens a store over a directory, adopting whatever is already in it.</summary>
    /// <param name="directory">Where the records live. Created if it does not exist.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    /// <remarks>
    /// <para>
    /// The directory is created and scanned here rather than on first use, so a path that is wrong
    /// fails at start-up where an operator is looking and not when a host finally sends a recipe.
    /// </para>
    /// <para>
    /// Any leftover <c>.writing</c> file is deleted: its write never finished, so nothing was ever
    /// told the recipe arrived. A file whose name is not a sequence number is left alone - this
    /// store did not write it - and so is a record this version cannot read, because a downgrade
    /// should not throw away a fab's recipes. Both are counted and logged.
    /// </para>
    /// <para>
    /// A namespace holding a recipe exists whether or not the small files survived, because the
    /// record carries its own namespace: losing a list is not a reason to lose a recipe. A lock or a
    /// selection naming a recipe that is not there is dropped, which is <c>Restore</c>'s rule in the
    /// packages that have one - the record is faithful and the judgement is in the reading.
    /// </para>
    /// </remarks>
    public RecipeFileStore(string directory, ILogger<RecipeFileStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<RecipeFileStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (ReadFull(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            var key = (record.Namespace, record.Id);

            // Two records for one recipe cannot be left behind by this store - a rewrite is one
            // file - so this is somebody having copied files in. The higher sequence is the later
            // write and the lower one goes, because keeping both would make the next open's answer
            // depend on the order the file system happened to enumerate them in.
            if (_recipes.TryGetValue(key, out var held))
            {
                _logger.LogWarning(
                    "Two records hold {Namespace}/{Id}; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Namespace,
                    record.Id,
                    Math.Max(held.Sequence, sequence),
                    Math.Min(held.Sequence, sequence));

                _records.Delete(Math.Min(held.Sequence, sequence));

                if (sequence < held.Sequence)
                {
                    continue;
                }
            }

            _recipes[key] = (sequence, (uint)record.Body.Length);
            _namespaces[record.Namespace] = true;
            _next = Math.Max(_next, sequence + 1);

            // Attributes come back because a host set them, which is the rule: what the host told
            // the machine returns and what the machine observed does not. They are in the recipe's
            // own record rather than in a side file, so a recipe and its attributes cannot be
            // adopted apart - and the one that loses the duplicate check above loses its
            // attributes with it.
            foreach (var (name, value) in record.Attributes)
            {
                _attributes[(record.Namespace, record.Id, name)] = value;
            }
        }

        foreach (var name in _records.ReadEntries(NamespacesFile))
        {
            _namespaces[Text(name)] = true;
        }

        foreach (var entry in _records.ReadEntries(LocksFile).Chunk(3))
        {
            if (entry.Length == 3 && _recipes.ContainsKey((Text(entry[0]), Text(entry[1]))))
            {
                _locks[(Text(entry[0]), Text(entry[1]))] = RecipeLockToken.FromEncoded(entry[2]);
            }
        }

        // An owner naming a recipe that is no longer on the disk is dropped, which is the locks'
        // rule and Restore's: the record is faithful and the judgement is in the reading.
        foreach (var entry in _records.ReadEntries(OwnersFile).Chunk(3))
        {
            if (entry.Length == 3 && _recipes.ContainsKey((Text(entry[0]), Text(entry[1]))))
            {
                _owners[(Text(entry[0]), Text(entry[1]))] = entry[2];
            }
        }

        var selection = _records.ReadEntries(SelectedFile);

        if (selection.Count == 2 && _recipes.ContainsKey((Text(selection[0]), Text(selection[1]))))
        {
            _selected = new Selection(Text(selection[0]), Text(selection[1]));
        }

        if (_recipes.Count > 0 || _namespaces.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Recipe directory {Directory} opened with {Recipes} recipe(s) in {Namespaces} namespace(s), "
                    + "{Locks} lock(s), {Partial} unfinished write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _recipes.Count,
                _namespaces.Count,
                _locks.Count,
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets the largest recipe this store will take. Default one mebibyte.</summary>
    /// <remarks>
    /// A declared length beyond this is refused at S15F5, which is what the request exists for.
    /// Not durable, deliberately: it is this machine's configuration rather than anything a host
    /// said, so it comes from wherever the application sets it on the next start.
    /// </remarks>
    public uint MaximumLength { get; set; } = 1024 * 1024;

    /// <summary>Gets how many recipes are held, across every namespace.</summary>
    public int Count => _recipes.Count;

    /// <summary>Gets how many downloads have been granted and not yet sent.</summary>
    /// <remarks>Zero on every open: a grant is a transaction in flight, so a restart ends it.</remarks>
    public int GrantedCount => _granted.Count;

    /// <summary>Gets how many recipes are locked.</summary>
    public int LockedCount => _locks.Count;

    /// <summary>Gets which recipe is selected, or <see langword="null"/> when none is.</summary>
    /// <remarks>
    /// The namespace and the identifier, where <see cref="RecipeInMemoryStore.Selected"/> is a whole
    /// <see cref="Recipe"/> - because the body is in a file, and a property that read a megabyte off
    /// a disk would be a trap rather than a convenience. <see cref="LoadAsync"/> fetches it.
    /// </remarks>
    public (string Namespace, string Id)? SelectedId
        => _selected is { } selected ? (selected.Namespace, selected.Id) : null;

    /// <summary>Gets whether a namespace is there.</summary>
    /// <param name="recipeNamespace">Which one.</param>
    /// <returns>Whether it is.</returns>
    public bool HasNamespace(string recipeNamespace) => _namespaces.ContainsKey(recipeNamespace);

    /// <summary>Gets whether a recipe is locked.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>Says nothing about <em>which</em> lock, for the reason <see cref="RecipeInMemoryStore.IsLocked"/> gives.</remarks>
    public bool IsLocked(string recipeNamespace, string recipeId)
        => _locks.ContainsKey((recipeNamespace, recipeId));

    /// <summary>Forgets every lock, which no message of stream 15 can do.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many were forgotten.</returns>
    /// <remarks>
    /// <strong>The door E5 does not give.</strong> A lock is released by the token it was taken with
    /// and by nothing else, and stream 15 has no row that asks which recipes are locked - so a host
    /// that crashed holding a token leaves a recipe locked for as long as the store lasts, which
    /// with this store is until somebody intervenes. This is that intervention, for the operator who
    /// has to get the machine working again, and it is deliberately not reachable from the wire:
    /// <see cref="RecipeEquipment"/> routes no message to it.
    /// </remarks>
    public async ValueTask<int> ForgetLocksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var forgotten = _locks.Count;

            if (forgotten > 0)
            {
                _locks.Clear();
                await WriteLocksAsync(cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "{Count} recipe lock(s) were forgotten by hand, which no host asked for.",
                    forgotten);
            }

            return forgotten;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered so a host can diff two answers, as the in-memory store's is. No gate: the
        // dictionaries are concurrent precisely so the questions that only read cost nothing.
        IReadOnlyList<string> listed = [.. _namespaces.Keys.OrderBy(name => name, StringComparer.Ordinal)];

        return ValueTask.FromResult(listed);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Answered out of the index, not off the disk.</strong> The length comes from the
    /// record that was written or adopted, which is why it is kept beside the sequence number: a
    /// directory that read every file to measure it would undo the reason this store holds no
    /// bodies. So a file somebody deleted behind this store is still listed, with the length it had
    /// - and <see cref="LoadAsync"/> is where that is discovered, refused and logged.
    /// </para>
    /// <para>
    /// No gate, like the other questions that only read. The attribute values are decoded fresh, so
    /// two hosts asking at once never share one item - the rule
    /// <see cref="GetAttributeAsync"/> states, and the one difference from the in-memory store,
    /// which hands out its own.
    /// </para>
    /// </remarks>
    public ValueTask<IReadOnlyList<RecipeDirectoryEntry>?> ListRecipesAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_namespaces.ContainsKey(recipeNamespace))
        {
            return ValueTask.FromResult<IReadOnlyList<RecipeDirectoryEntry>?>(null);
        }

        List<RecipeDirectoryEntry> entries = [];

        foreach (var key in _recipes.Keys
            .Where(key => string.Equals(key.Namespace, recipeNamespace, StringComparison.Ordinal))
            .OrderBy(key => key.Id, StringComparer.Ordinal))
        {
            if (!_recipes.TryGetValue(key, out var entry))
            {
                // Deleted while this answer was being built. A directory is what is there now.
                continue;
            }

            entries.Add(new RecipeDirectoryEntry
            {
                Id = key.Id,
                Length = entry.Length,
                Attributes = AttributesOf(key.Namespace, key.Id),
            });
        }

        return ValueTask.FromResult<IReadOnlyList<RecipeDirectoryEntry>?>(entries);
    }

    // The attributes of one recipe, sorted by name so two directories can be diffed, and decoded
    // out of the bytes this store keeps rather than shared.
    private IReadOnlyList<RecipeAttribute> AttributesOf(string recipeNamespace, string recipeId)
    {
        List<RecipeAttribute> attributes = [];

        foreach (var key in _attributes.Keys
            .Where(key =>
                string.Equals(key.Namespace, recipeNamespace, StringComparison.Ordinal)
                && string.Equals(key.Id, recipeId, StringComparison.Ordinal))
            .OrderBy(key => key.Attribute, StringComparer.Ordinal))
        {
            if (_attributes.TryGetValue(key, out var value))
            {
                attributes.Add(new RecipeAttribute { Name = key.Attribute, Value = SecsItem.Decode(value) });
            }
        }

        return attributes;
    }

    /// <inheritdoc />
    public async ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_namespaces.TryAdd(recipeNamespace, true))
            {
                return RecipeAcknowledgement.Rejected;
            }

            await WriteNamespacesAsync(cancellationToken).ConfigureAwait(false);

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_namespaces.ContainsKey(recipeNamespace))
            {
                return RecipeAcknowledgement.Rejected;
            }

            // The rule the in-memory store makes, kept here rather than shared: S15F17 names no
            // recipe, so a namespace that still holds one is not deleted. It matters more with a
            // store that persists, because the message would be taking a directory off a disk.
            if (_recipes.Keys.Any(key => string.Equals(key.Namespace, recipeNamespace, StringComparison.Ordinal)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _namespaces.TryRemove(recipeNamespace, out _);
            await WriteNamespacesAsync(cancellationToken).ConfigureAwait(false);

            // Nothing to write for these: a namespace attribute is the application's wiring and
            // was never on the disk. Dropped all the same, so a namespace created again under the
            // same name does not answer S15F51 with what the last one had.
            foreach (var key in _namespaceAttributes.Keys)
            {
                if (string.Equals(key.Namespace, recipeNamespace, StringComparison.Ordinal))
                {
                    _namespaceAttributes.TryRemove(key, out _);
                }
            }

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing is written: a grant is the one thing here deliberately less durable than it could be,
    /// because an S15F5 answered and an S15F7 that never came is a transaction a restart ends.
    /// </remarks>
    public async ValueTask<RecipeAcknowledgement> RequestDownloadAsync(
        RecipeDownloadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_namespaces.ContainsKey(request.Namespace)
                || request.Length > MaximumLength
                || _locks.ContainsKey((request.Namespace, request.Id)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _granted[(request.Namespace, request.Id)] = request.Length;

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The four refusals are the in-memory store's four and they are all made before anything
    /// reaches the disk. The record goes down before the grant is spent and before the index moves,
    /// so an interruption between the write and the acknowledgement leaves a recipe the next open
    /// adopts - the right way round, because the host was not told it arrived and will send it
    /// again, onto the same sequence.
    /// </remarks>
    public async ValueTask<RecipeAcknowledgement> SaveAsync(
        string recipeNamespace,
        string recipeId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_granted.TryGetValue((recipeNamespace, recipeId), out var declared))
            {
                return RecipeAcknowledgement.Rejected;
            }

            // The namespace is checked again and the grant dropped with it; the lock is checked
            // again and the grant kept. Both for the reasons RecipeInMemoryStore.SaveAsync writes
            // out - a lock is temporary where a deleted namespace is not coming back.
            if (!_namespaces.ContainsKey(recipeNamespace))
            {
                _granted.TryRemove((recipeNamespace, recipeId), out _);

                return RecipeAcknowledgement.Rejected;
            }

            if (_locks.ContainsKey((recipeNamespace, recipeId)) || declared != body.Length)
            {
                return RecipeAcknowledgement.Rejected;
            }

            // An overwrite rewrites the recipe's own file and a new recipe takes the next sequence,
            // which is what keeps one recipe to one record: the identifier is inside the record, so
            // there is nothing in the file name to disagree with.
            var sequence = _recipes.TryGetValue((recipeNamespace, recipeId), out var held) ? held.Sequence : _next;

            await WriteRecordAsync(sequence, recipeNamespace, recipeId, body, cancellationToken)
                .ConfigureAwait(false);

            _next = Math.Max(_next, sequence + 1);
            _recipes[(recipeNamespace, recipeId)] = (sequence, (uint)body.Length);
            _granted.TryRemove((recipeNamespace, recipeId), out _);

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_recipes.TryGetValue((recipeNamespace, recipeId), out var entry))
            {
                return (RecipeAcknowledgement.Rejected, default);
            }

            if (Read(entry.Sequence) is not { } record)
            {
                // The index says it is here and the file says otherwise, so somebody has been in
                // the directory. Refused rather than guessed at, and logged because S15F10's one
                // code cannot say which of the two refusals this is.
                _logger.LogWarning(
                    "The record for {Namespace}/{Id} could not be read, so the upload was refused.",
                    recipeNamespace,
                    recipeId);

                return (RecipeAcknowledgement.Rejected, default);
            }

            return (RecipeAcknowledgement.Accepted, record.Body);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RecipeAcknowledgement> DeleteAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (_locks.ContainsKey((recipeNamespace, recipeId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _granted.TryRemove((recipeNamespace, recipeId), out _);

            if (!_recipes.TryRemove((recipeNamespace, recipeId), out var entry))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _records.Delete(entry.Sequence);

            if (MoveAttributes(recipeNamespace, recipeId, null, null))
            {
                await WriteOwnersAsync(cancellationToken).ConfigureAwait(false);
            }

            // A recipe that was selected and is now gone leaves nothing selected, where a rename
            // moves the selection with it: the in-memory store's two rules. The small file has to
            // agree with the index, or a restart would report a selection that is not there.
            if (IsSelected(recipeNamespace, recipeId))
            {
                _selected = null;
                await WriteSelectedAsync(cancellationToken).ConfigureAwait(false);
            }

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RecipeAcknowledgement> SelectAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_recipes.ContainsKey((recipeNamespace, recipeId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _selected = new Selection(recipeNamespace, recipeId);
            await WriteSelectedAsync(cancellationToken).ConfigureAwait(false);

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RecipeAcknowledgement> AbortAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Nothing to write: the grant was never on the disk, so forgetting it is not either.
            return _granted.TryRemove((recipeNamespace, recipeId), out _)
                ? RecipeAcknowledgement.Accepted
                : RecipeAcknowledgement.Rejected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// One atomic rewrite of the recipe's own file, because the identifier is inside the record and
    /// not in the file name. A rename that moved a file would be two operations with a window
    /// between them, and an interruption in that window would leave the fab with two recipes where
    /// the host asked for one.
    /// </remarks>
    public async ValueTask<RecipeAcknowledgement> RenameAsync(
        string recipeNamespace,
        string recipeId,
        string newRecipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(newRecipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (newRecipeId.Length == 0
                || !_recipes.TryGetValue((recipeNamespace, recipeId), out var entry)
                || _locks.ContainsKey((recipeNamespace, recipeId))
                || _recipes.ContainsKey((recipeNamespace, newRecipeId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            if (Read(entry.Sequence) is not { } record)
            {
                return RecipeAcknowledgement.Rejected;
            }

            // Before the write and not after, because WriteRecordAsync reads the index to decide
            // what goes in the record: moving them afterwards would write the new name with the old
            // name's attributes still filed under it, and a restart would read none.
            var moved = MoveAttributes(recipeNamespace, recipeId, recipeNamespace, newRecipeId);

            await WriteRecordAsync(entry.Sequence, recipeNamespace, newRecipeId, record.Body, cancellationToken)
                .ConfigureAwait(false);

            if (moved)
            {
                await WriteOwnersAsync(cancellationToken).ConfigureAwait(false);
            }

            // The whole entry moves: a rename does not touch the body, so the length it recorded is
            // still the length.
            _recipes[(recipeNamespace, newRecipeId)] = entry;
            _recipes.TryRemove((recipeNamespace, recipeId), out _);
            _granted.TryRemove((recipeNamespace, recipeId), out _);

            if (IsSelected(recipeNamespace, recipeId))
            {
                _selected = new Selection(recipeNamespace, newRecipeId);
                await WriteSelectedAsync(cancellationToken).ConfigureAwait(false);
            }

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<RecipeAcknowledgement> CopyAsync(
        string sourceNamespace,
        string sourceId,
        string destinationNamespace,
        string destinationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceNamespace);
        ArgumentNullException.ThrowIfNull(sourceId);
        ArgumentNullException.ThrowIfNull(destinationNamespace);
        ArgumentNullException.ThrowIfNull(destinationId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A locked source is copied happily, as the in-memory store's is: a lock stops a recipe
            // being changed, and reading one is what S15F9 does to a locked recipe too.
            if (destinationId.Length == 0
                || !_recipes.TryGetValue((sourceNamespace, sourceId), out var entry)
                || !_namespaces.ContainsKey(destinationNamespace)
                || _recipes.ContainsKey((destinationNamespace, destinationId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            if (Read(entry.Sequence) is not { } record)
            {
                return RecipeAcknowledgement.Rejected;
            }

            var copy = _next;

            var copied = CopyAttributes(sourceNamespace, sourceId, destinationNamespace, destinationId);

            await WriteRecordAsync(copy, destinationNamespace, destinationId, record.Body, cancellationToken)
                .ConfigureAwait(false);

            if (copied)
            {
                await WriteOwnersAsync(cancellationToken).ConfigureAwait(false);
            }

            _next = copy + 1;
            _recipes[(destinationNamespace, destinationId)] = (copy, entry.Length);

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(
        string recipeNamespace,
        string recipeId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_recipes.ContainsKey((recipeNamespace, recipeId))
                || _locks.ContainsKey((recipeNamespace, recipeId)))
            {
                return (RecipeAcknowledgement.Rejected, null);
            }

            // Text and counted, as the in-memory store's is - and the recipe is in it, which that
            // store does not need. The counter starts again with the process where the locks do
            // not, so after a restart a plain number would mint a token a recipe locked before the
            // restart is already holding, and whichever host owned the other one could release this
            // lock. The pair cannot repeat while the recipe is locked.
            var token = RecipeLockToken.FromText(
                "LOCK-"
                    + recipeId
                    + "-"
                    + Interlocked.Increment(ref _minted).ToString(CultureInfo.InvariantCulture));

            _locks[(recipeNamespace, recipeId)] = token;
            await WriteLocksAsync(cancellationToken).ConfigureAwait(false);

            return (RecipeAcknowledgement.Accepted, token);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// A token restored from the disk is the bytes that went out, so it releases the lock it was
    /// taken with and <see cref="RecipeLockToken.Matches"/> is the whole comparison across a restart
    /// as well as within one. Its <see cref="RecipeLockToken.Format"/> and
    /// <see cref="RecipeLockToken.Count"/> come back null, which is what
    /// <see cref="RecipeLockToken.FromEncoded"/> says they are and costs nothing: nothing in this
    /// package looks inside a token.
    /// </remarks>
    public async ValueTask<RecipeAcknowledgement> UnlockAsync(
        string recipeNamespace,
        string recipeId,
        RecipeLockToken token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(token);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_locks.TryGetValue((recipeNamespace, recipeId), out var held) || !held.Matches(token))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _locks.TryRemove((recipeNamespace, recipeId), out _);
            await WriteLocksAsync(cancellationToken).ConfigureAwait(false);

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// No version, for <see cref="RecipeInMemoryStore.CurrentAsync"/>'s reason rather than a storage
    /// one: nothing in stream 15 <em>sets</em> a recipe's version, so a store holding only what the
    /// wire gave it has none to report and there would be nothing to write down. An application
    /// whose recipes do have versions implements <see cref="IRecipeStore.CurrentAsync"/> itself and
    /// keeps them wherever it keeps the rest of what this store cannot know.
    /// </remarks>
    public ValueTask<(string RecipeId, RecipeVersion? Version)?> CurrentAsync(
        string recipeNamespace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        cancellationToken.ThrowIfCancellationRequested();

        // Per namespace, because S15F35 carries one RCPSPEC and asks about it.
        return ValueTask.FromResult(
            _selected is { } selected
                && string.Equals(selected.Namespace, recipeNamespace, StringComparison.Ordinal)
                    ? ((string, RecipeVersion?)?)(selected.Id, null)
                    : null);
    }

    // ---------------------------------------------------------------- the files

    private static string Text(byte[] entry) => DurableRecordDirectory.Text(entry);

    /// <summary>Whether a recipe is the selected one, which the delete and the rename ask.</summary>
    private bool IsSelected(string recipeNamespace, string recipeId)
        => _selected is { } selected
            && string.Equals(selected.Namespace, recipeNamespace, StringComparison.Ordinal)
            && string.Equals(selected.Id, recipeId, StringComparison.Ordinal);

    private ValueTask WriteNamespacesAsync(CancellationToken cancellationToken)
        => _records.WriteEntriesAsync(
            NamespacesFile,
            [.. _namespaces.Keys
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => (ReadOnlyMemory<byte>)DurableRecordDirectory.Bytes(name))],
            cancellationToken);

    private ValueTask WriteSelectedAsync(CancellationToken cancellationToken)
        => _records.WriteEntriesAsync(
            SelectedFile,
            _selected is { } selected
                ? [DurableRecordDirectory.Bytes(selected.Namespace), DurableRecordDirectory.Bytes(selected.Id)]
                : [],
            cancellationToken);

    private ValueTask WriteLocksAsync(CancellationToken cancellationToken)
    {
        List<ReadOnlyMemory<byte>> flat = [];

        foreach (var ((space, id), token) in _locks.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal))
        {
            flat.Add(DurableRecordDirectory.Bytes(space));
            flat.Add(DurableRecordDirectory.Bytes(id));

            // The encoded item and not its text, which is what makes a restored lock the same lock:
            // RCPLOCKID is carried opaquely on the wire, so it is carried opaquely to the disk, and
            // a store that wrote the text would have chosen a format for a name the reference never
            // defines - exactly what this package refuses to do with RCPOWNER.
            flat.Add(token.Encoded);
        }

        return _records.WriteEntriesAsync(LocksFile, flat, cancellationToken);
    }

    private ValueTask WriteOwnersAsync(CancellationToken cancellationToken)
    {
        List<ReadOnlyMemory<byte>> flat = [];

        foreach (var ((space, id), owner) in _owners.OrderBy(pair => pair.Key.Id, StringComparer.Ordinal))
        {
            flat.Add(DurableRecordDirectory.Bytes(space));
            flat.Add(DurableRecordDirectory.Bytes(id));

            // The encoded item and not its text, which is the whole reason the row could be
            // served: RCPOWNER is undefined and its rival RCPOWCODE is a BOOLEAN, so a store that
            // wrote the text would have chosen one of the two readings. A host that set a boolean
            // reads a boolean back.
            flat.Add(owner);
        }

        return _records.WriteEntriesAsync(OwnersFile, flat, cancellationToken);
    }

    /// <summary>Writes one recipe's record: the namespace and the identifier, then the body.</summary>
    /// <remarks>
    /// Two fields and a tail, which is the whole of this store's framing - the rest of what a
    /// record is belongs to <c>DurableRecordDirectory</c>, shared with the three other transfer
    /// stores because it is a mechanism and not a rule.
    /// </remarks>
    private ValueTask WriteRecordAsync(
        long sequence,
        string recipeNamespace,
        string recipeId,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        // The attributes come out of the index rather than off the caller, so every path that
        // rewrites a record keeps them without having to remember to - an overwrite at S15F7, a
        // rename, a copy and the attribute set itself. Move or copy the index first; the write
        // reads it.
        List<ReadOnlyMemory<byte>> fields =
        [
            DurableRecordDirectory.Bytes(recipeNamespace),
            DurableRecordDirectory.Bytes(recipeId),
        ];

        foreach (var key in _attributes.Keys)
        {
            if (string.Equals(key.Namespace, recipeNamespace, StringComparison.Ordinal)
                && string.Equals(key.Id, recipeId, StringComparison.Ordinal)
                && _attributes.TryGetValue(key, out var value))
            {
                fields.Add(DurableRecordDirectory.Bytes(key.Attribute));
                fields.Add(value);
            }
        }

        return _records.WriteRecordAsync(sequence, fields, body, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask<RecipeAcknowledgement> SetAttributeAsync(
        string recipeNamespace,
        string recipeId,
        string attributeName,
        SecsItem value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(attributeName);
        ArgumentNullException.ThrowIfNull(value);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_recipes.TryGetValue((recipeNamespace, recipeId), out var entry)
                || _locks.ContainsKey((recipeNamespace, recipeId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            var previous = _attributes.TryGetValue((recipeNamespace, recipeId, attributeName), out var held)
                ? held
                : null;

            _attributes[(recipeNamespace, recipeId, attributeName)] = value.ToByteArray();

            try
            {
                // One atomic rewrite of the recipe's own record, which is what this store does for
                // a rename as well: the attribute is inside the record, so there is no second file
                // to get out of step with it and no window where one is written and the other not.
                var body = Read(entry.Sequence)?.Body ?? ReadOnlyMemory<byte>.Empty;

                await WriteRecordAsync(entry.Sequence, recipeNamespace, recipeId, body, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (Rollback(recipeNamespace, recipeId, attributeName, previous))
            {
                throw;
            }

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<SecsItem?> GetAttributeAsync(
        string recipeNamespace,
        string recipeId,
        string attributeName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(attributeName);
        cancellationToken.ThrowIfCancellationRequested();

        // Decoded fresh, so two hosts asking at once never share one item - the reason this store
        // keeps the bytes where the in-memory one keeps the caller's own SecsItem.
        return ValueTask.FromResult(
            _attributes.TryGetValue((recipeNamespace, recipeId, attributeName), out var value)
                ? SecsItem.Decode(value)
                : null);
    }

    /// <summary>Gets who owns a recipe, or <see langword="null"/> when nobody has said.</summary>
    /// <param name="recipeNamespace">Which namespace.</param>
    /// <param name="recipeId">Which recipe.</param>
    /// <returns>The item a host set at S15F49, decoded fresh, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException">Either argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// The read-back no row in the reference has, and the caller's item to dispose - decoded out
    /// of what was written down, so two callers never share one, which is
    /// <see cref="GetAttributeAsync"/>'s rule and the one difference from the in-memory store.
    /// </remarks>
    public SecsItem? Owner(string recipeNamespace, string recipeId)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);

        return _owners.TryGetValue((recipeNamespace, recipeId), out var owner) ? SecsItem.Decode(owner) : null;
    }

    /// <summary>Sets an attribute of a namespace - the only door there is, and not a durable one.</summary>
    /// <param name="recipeNamespace">Which namespace. Created if it is not there, and the list is not written.</param>
    /// <param name="attributeName">Which attribute - <c>RCPATTRNAME</c>.</param>
    /// <param name="value">The value - <c>RCPATTRVAL</c>. The store's from here on.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// S15F51 asks for a namespace attribute and no row in the reference sets one, so this is the
    /// only way a store can answer with anything - <c>ItemStore.Add</c>'s position in stream 21.
    /// </para>
    /// <para>
    /// <strong>Nothing here is written to the disk, and that is the one place this store answers a
    /// host differently after a restart.</strong> What a host told the machine comes back; a
    /// namespace attribute is not that - it is the application's own wiring, like the directory
    /// this store is pointed at, and the service program store leaves its <c>Run</c> delegate out
    /// for the identical reason. An application sets these on every start, as it sets everything
    /// else it configures.
    /// </para>
    /// </remarks>
    public void SetNamespaceAttribute(string recipeNamespace, string attributeName, SecsItem value)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(attributeName);
        ArgumentNullException.ThrowIfNull(value);

        _namespaces[recipeNamespace] = true;
        _namespaceAttributes[(recipeNamespace, attributeName)] = value;
    }

    /// <inheritdoc/>
    public async ValueTask<RecipeAcknowledgement> SetOwnerAsync(
        string recipeNamespace,
        string recipeId,
        SecsItem owner,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(recipeId);
        ArgumentNullException.ThrowIfNull(owner);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_recipes.ContainsKey((recipeNamespace, recipeId))
                || _locks.ContainsKey((recipeNamespace, recipeId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            var previous = _owners.TryGetValue((recipeNamespace, recipeId), out var held) ? held : null;

            _owners[(recipeNamespace, recipeId)] = owner.ToByteArray();

            try
            {
                await WriteOwnersAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (RollbackOwner(recipeNamespace, recipeId, previous))
            {
                throw;
            }

            return RecipeAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<SecsItem?> GetNamespaceAttributeAsync(
        string recipeNamespace,
        string attributeName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipeNamespace);
        ArgumentNullException.ThrowIfNull(attributeName);
        cancellationToken.ThrowIfCancellationRequested();

        // No gate, like the other questions that only read - and no decode either, because
        // nothing here came off a disk. These are the application's items and it keeps them.
        return ValueTask.FromResult(
            _namespaceAttributes.TryGetValue((recipeNamespace, attributeName), out var value) ? value : null);
    }

    // Puts an owner back when the write failed, for the reason Rollback does it for an attribute:
    // the index must never claim something the disk does not hold.
    private bool RollbackOwner(string recipeNamespace, string recipeId, byte[]? previous)
    {
        if (previous is null)
        {
            _owners.TryRemove((recipeNamespace, recipeId), out _);
        }
        else
        {
            _owners[(recipeNamespace, recipeId)] = previous;
        }

        return false;
    }

    // Puts an attribute back when the write failed, so the index never claims something the disk
    // does not have. Returns false so the filter does not swallow the exception.
    private bool Rollback(string recipeNamespace, string recipeId, string attributeName, byte[]? previous)
    {
        if (previous is null)
        {
            _attributes.TryRemove((recipeNamespace, recipeId, attributeName), out _);
        }
        else
        {
            _attributes[(recipeNamespace, recipeId, attributeName)] = previous;
        }

        return false;
    }

    // Both movers return whether the OWNER moved, and the caller writes the owners file when it
    // did. The attributes need no such answer because they are inside the record the caller is
    // about to rewrite; an owner is in a side file, so the two have to be kept in step by hand -
    // the one cost of keeping the record's field count even.
    private bool MoveAttributes(string fromSpace, string fromId, string? toSpace, string? toId)
    {
        foreach (var key in _attributes.Keys)
        {
            if (!string.Equals(key.Namespace, fromSpace, StringComparison.Ordinal)
                || !string.Equals(key.Id, fromId, StringComparison.Ordinal)
                || !_attributes.TryRemove(key, out var value))
            {
                continue;
            }

            if (toSpace is not null && toId is not null)
            {
                _attributes[(toSpace, toId, key.Attribute)] = value;
            }
        }

        if (!_owners.TryRemove((fromSpace, fromId), out var owner))
        {
            return false;
        }

        if (toSpace is not null && toId is not null)
        {
            _owners[(toSpace, toId)] = owner;
        }

        return true;
    }

    private bool CopyAttributes(string fromSpace, string fromId, string toSpace, string toId)
    {
        foreach (var key in _attributes.Keys)
        {
            if (string.Equals(key.Namespace, fromSpace, StringComparison.Ordinal)
                && string.Equals(key.Id, fromId, StringComparison.Ordinal)
                && _attributes.TryGetValue(key, out var value))
            {
                _attributes[(toSpace, toId, key.Attribute)] = value;
            }
        }

        if (!_owners.TryGetValue((fromSpace, fromId), out var owner))
        {
            return false;
        }

        _owners[(toSpace, toId)] = owner;

        return true;
    }

    /// <summary>Reads one recipe's record.</summary>
    /// <remarks>
    /// Null for a record this version cannot read, for one a tool truncated and for one that will
    /// not open - three different disks that mean one thing to a store, and never "delete it".
    /// </remarks>
    private (string Namespace, string Id, ReadOnlyMemory<byte> Body)? Read(long sequence)
        => Read(_records.PathFor(sequence));

    private (string Namespace, string Id, ReadOnlyMemory<byte> Body)? Read(string path)
        => ReadFull(path) is { } record ? (record.Namespace, record.Id, record.Body) : null;

    /// <summary>Reads one recipe's record, attributes and all.</summary>
    /// <remarks>
    /// Two fields and then a name-and-value pair per attribute, so an <strong>even</strong> count
    /// of at least two is a record this version can read. A record written before attributes
    /// existed has exactly two and comes back with none, which is why the count is a floor and a
    /// parity rather than a number: a store pointed at a directory an earlier version wrote finds
    /// its recipes rather than finding nothing.
    /// </remarks>
    private (string Namespace, string Id, ReadOnlyMemory<byte> Body, IReadOnlyList<(string Name, byte[] Value)> Attributes)? ReadFull(string path)
    {
        if (_records.TryReadRecord(path) is not { Tail: { } body } record
            || record.Fields.Count < 2
            || record.Fields.Count % 2 != 0)
        {
            return null;
        }

        List<(string Name, byte[] Value)> attributes = [];

        for (var i = 2; i + 1 < record.Fields.Count; i += 2)
        {
            attributes.Add((Text(record.Fields[i]), record.Fields[i + 1]));
        }

        return (Text(record.Fields[0]), Text(record.Fields[1]), body, attributes);
    }


    /// <summary>What is selected, as one reference.</summary>
    /// <remarks>
    /// A type rather than the <c>(string, string)?</c> this obviously wants to be, because
    /// <see cref="CurrentAsync"/> and <see cref="SelectedId"/> read it <strong>without the
    /// gate</strong> - the questions that only read are meant to cost nothing here. A nullable
    /// tuple of two references is not read or written atomically, so a reader could in principle
    /// see one selection's namespace beside another's identifier, and answer S15F36 with a recipe
    /// that is not in the namespace that was asked about. One reference cannot tear.
    /// <see cref="RecipeInMemoryStore.Selected"/> needs none of this: a <see cref="Recipe"/> is
    /// already one reference.
    /// </remarks>
    private sealed record Selection(string Namespace, string Id);
}
