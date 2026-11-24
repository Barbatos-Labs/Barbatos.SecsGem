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

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DurableRecordDirectory _records;
    private readonly ConcurrentDictionary<string, bool> _namespaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Namespace, string Id), long> _recipes = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), uint> _granted = new();
    private readonly ConcurrentDictionary<(string Namespace, string Id), RecipeLockToken> _locks = new();
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
            if (Read(path) is not { } record)
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
                    Math.Max(held, sequence),
                    Math.Min(held, sequence));

                _records.Delete(Math.Min(held, sequence));

                if (sequence < held)
                {
                    continue;
                }
            }

            _recipes[key] = sequence;
            _namespaces[record.Namespace] = true;
            _next = Math.Max(_next, sequence + 1);
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
            var sequence = _recipes.TryGetValue((recipeNamespace, recipeId), out var held) ? held : _next;

            await WriteRecordAsync(sequence, recipeNamespace, recipeId, body, cancellationToken)
                .ConfigureAwait(false);

            _next = Math.Max(_next, sequence + 1);
            _recipes[(recipeNamespace, recipeId)] = sequence;
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
            if (!_recipes.TryGetValue((recipeNamespace, recipeId), out var sequence))
            {
                return (RecipeAcknowledgement.Rejected, default);
            }

            if (Read(sequence) is not { } record)
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

            if (!_recipes.TryRemove((recipeNamespace, recipeId), out var sequence))
            {
                return RecipeAcknowledgement.Rejected;
            }

            _records.Delete(sequence);

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
                || !_recipes.TryGetValue((recipeNamespace, recipeId), out var sequence)
                || _locks.ContainsKey((recipeNamespace, recipeId))
                || _recipes.ContainsKey((recipeNamespace, newRecipeId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            if (Read(sequence) is not { } record)
            {
                return RecipeAcknowledgement.Rejected;
            }

            await WriteRecordAsync(sequence, recipeNamespace, newRecipeId, record.Body, cancellationToken)
                .ConfigureAwait(false);

            _recipes[(recipeNamespace, newRecipeId)] = sequence;
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
                || !_recipes.TryGetValue((sourceNamespace, sourceId), out var sequence)
                || !_namespaces.ContainsKey(destinationNamespace)
                || _recipes.ContainsKey((destinationNamespace, destinationId)))
            {
                return RecipeAcknowledgement.Rejected;
            }

            if (Read(sequence) is not { } record)
            {
                return RecipeAcknowledgement.Rejected;
            }

            var copy = _next;

            await WriteRecordAsync(copy, destinationNamespace, destinationId, record.Body, cancellationToken)
                .ConfigureAwait(false);

            _next = copy + 1;
            _recipes[(destinationNamespace, destinationId)] = copy;

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

    /// <summary>Whether a recipe is the selected one, which four of the fourteen methods ask.</summary>
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
        => _records.WriteRecordAsync(
            sequence,
            [DurableRecordDirectory.Bytes(recipeNamespace), DurableRecordDirectory.Bytes(recipeId)],
            body,
            cancellationToken);

    /// <summary>Reads one recipe's record.</summary>
    /// <remarks>
    /// Null for a record this version cannot read, for one a tool truncated and for one that will
    /// not open - three different disks that mean one thing to a store, and never "delete it".
    /// </remarks>
    private (string Namespace, string Id, ReadOnlyMemory<byte> Body)? Read(long sequence)
        => Read(_records.PathFor(sequence));

    private (string Namespace, string Id, ReadOnlyMemory<byte> Body)? Read(string path)
        => _records.TryReadRecord(path) is { Fields.Count: 2, Tail: { } body } record
            ? (Text(record.Fields[0]), Text(record.Fields[1]), body)
            : null;


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
