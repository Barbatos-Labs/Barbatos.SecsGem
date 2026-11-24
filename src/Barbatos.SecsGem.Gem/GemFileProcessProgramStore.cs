// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// A process program store that outlives the process, by keeping each program in a file.
/// </summary>
/// <remarks>
/// <para>
/// Stream 7 is the most used transfer in this stack and the one whose store had no durable version:
/// <see cref="GemInMemoryProcessProgramStore"/> says in its own remarks that real equipment wants
/// its own store, and this is it. A recipe a host spent a shift validating is not something to ask
/// for twice because the controller was upgraded.
/// </para>
/// <para>
/// <strong>The mechanism is <c>DurableRecordDirectory</c>'s</strong>, shared as source with the four
/// transfer packages: one file per program, written through, committed with a rename, and a file
/// name that is a sequence number and nothing else - so <c>PPID</c>, which E5 caps at 120
/// characters and otherwise leaves to the two ends, never becomes part of a path. The identifier is
/// <em>inside</em> the record, which is also what makes replacing a program one atomic rewrite of
/// one file.
/// </para>
/// <para>
/// <strong>The extension is the thing to notice.</strong> This store's records are
/// <c>.processprogram</c>, the formatted store's are <c>.formattedprogram</c> and the matrix
/// store's are <c>.matrix</c>, and that is not decoration: the three record formats are
/// indistinguishable - an identifier and a payload - so a store pointed at another's directory
/// would otherwise adopt its programs and answer S7F5 with them. Separate stores was the matrix
/// chapter's finding; separate extensions is what makes it hold on a disk. A directory per store is
/// still the recommendation, because the sweep of unfinished writes is directory-wide.
/// </para>
/// <para>
/// <strong>What a restart keeps is the programs, and the grant is not a thing this store has.</strong>
/// S7F1 asks and S7F3 sends, and the answer to S7F1 is computed from what is held rather than
/// remembered - <see cref="InquireAsync"/> keeps no state at all, which is why there is nothing in
/// flight here to end the way an interruption ends. <see cref="Capacity"/> and
/// <see cref="MaxBodyLength"/> are this machine's configuration rather than anything a host said,
/// so they are not written down.
/// </para>
/// <para>
/// One store per directory. Two would fight over the sequence numbers.
/// </para>
/// </remarks>
public sealed class GemFileProcessProgramStore : IGemProcessProgramStore
{
    /// <summary>The extension a finished program record carries.</summary>
    public const string RecordExtension = ".processprogram";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _programs = new(StringComparer.Ordinal);
    private readonly DurableRecordDirectory _records;
    private readonly ILogger _logger;

    private long _next;

    /// <summary>Opens a store over a directory, adopting whatever is already in it.</summary>
    /// <param name="directory">Where the records live. Created if it does not exist.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    /// <remarks>
    /// The directory is created and scanned here rather than on first use, so a path that is wrong
    /// fails at start-up where an operator is looking and not when a host finally sends a recipe.
    /// An unfinished write is swept, a file that is not this store's is left alone, and so is a
    /// record this version cannot read - a downgrade should not throw away a fab's recipes.
    /// <see cref="Capacity"/> is not enforced on what is adopted: a directory holding more than this
    /// machine is configured for is a fact, and a store that hid some of it would answer S7F19 with
    /// a directory the disk disagrees with.
    /// </remarks>
    public GemFileProcessProgramStore(string directory, ILogger<GemFileProcessProgramStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<GemFileProcessProgramStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            // Two records for one program cannot be left behind by this store - a replacement is one
            // file - so this is somebody having copied files in. The higher sequence is the later
            // write, and keeping both would make the answer depend on enumeration order.
            if (_programs.TryGetValue(record.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold process program '{Id}'; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Id,
                    Math.Max(held, sequence),
                    Math.Min(held, sequence));

                _records.Delete(Math.Min(held, sequence));

                if (sequence < held)
                {
                    continue;
                }
            }

            _programs[record.Id] = sequence;
            _next = Math.Max(_next, sequence + 1);
        }

        if (_programs.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Process program directory {Directory} opened with {Programs} program(s), {Partial} unfinished "
                    + "write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _programs.Count,
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets how many programs may be stored at once. Default 64.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets the largest body this store will accept. Default one mebibyte.</summary>
    public int MaxBodyLength { get; set; } = 1024 * 1024;

    /// <summary>Gets how many programs are stored.</summary>
    public int Count => _programs.Count;

    /// <summary>Gets whether a program is held.</summary>
    /// <param name="id">PPID.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    /// The cheap question, where <see cref="LoadAsync"/> reads a file: a machine deciding whether to
    /// answer S7F1 with <see cref="GemProcessProgramGrant.AlreadyHave"/> should not have to read a
    /// megabyte to find out.
    /// </remarks>
    public bool Has(string id) => _programs.ContainsKey(id);

    /// <inheritdoc/>
    /// <remarks>
    /// The in-memory store's four answers, in its order, and nothing is remembered: the grant is a
    /// computation over what is held, so there is no in-flight state for a restart to end. A host
    /// that was granted room and then lost the link simply asks again.
    /// </remarks>
    public ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();

        GemProcessProgramGrant grant;

        if (!IsValidId(id))
        {
            grant = GemProcessProgramGrant.InvalidId;
        }
        else if (_programs.ContainsKey(id))
        {
            // Not an error. The host is told it already sent this one, and may delete and resend.
            grant = GemProcessProgramGrant.AlreadyHave;
        }
        else if (length > MaxBodyLength || _programs.Count >= Capacity)
        {
            grant = GemProcessProgramGrant.NoSpace;
        }
        else
        {
            grant = GemProcessProgramGrant.Ok;
        }

        return ValueTask.FromResult(grant);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The record goes down before the acknowledgement, so an interruption between the two leaves a
    /// program the next open adopts - the right way round, because the host was not told it arrived
    /// and will send it again, onto the same record.
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!IsValidId(id))
            {
                return GemProcessProgramAcknowledgement.NotFound;
            }

            if (body.Length > MaxBodyLength)
            {
                return GemProcessProgramAcknowledgement.LengthError;
            }

            if (!_programs.ContainsKey(id) && _programs.Count >= Capacity)
            {
                return GemProcessProgramAcknowledgement.PermissionNotGranted;
            }

            // No copy, which the in-memory store has to make: the bytes go to the disk inside this
            // call, so nothing of the inbound message is kept after the handler returns.
            await WriteAsync(id, body, cancellationToken).ConfigureAwait(false);

            return GemProcessProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Written out rather than as a conditional, for the reason the in-memory store spells
            // out at its own LoadAsync: the obvious one-liner turns "no such program" into a
            // program of zero bytes, because the ternary's type drags the null through the
            // byte[]-to-ReadOnlyMemory conversion. The same trap caught the matrix store's first
            // draft, which is why the comment is in all three.
            if (!_programs.TryGetValue(id, out var sequence))
            {
                return null;
            }

            if (Read(_records.PathFor(sequence)) is not { } record)
            {
                // The index says it is here and the file says otherwise, so somebody has been in the
                // directory. S7F6 carries no acknowledgement at all - an empty PPBODY is the whole
                // of the refusal - so the log is the only place this differs from a program a host
                // asked for by the wrong name.
                _logger.LogWarning(
                    "The record for process program '{Id}' could not be read, so the upload was refused.",
                    id);

                return null;
            }

            return record.Body;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <strong>A record the disk will not delete is a refusal, not a silent success.</strong> The
    /// index keeps the program when its file survives, so the store and the disk agree and the next
    /// open does not quietly bring it back - and the host is told
    /// <see cref="GemProcessProgramAcknowledgement.PermissionNotGranted"/>, which is the nearest
    /// honest code E5 has: <c>ACKC7</c> has nothing meaning "I could not".
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(
        string id,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_programs.TryGetValue(id, out var sequence))
            {
                return GemProcessProgramAcknowledgement.NotFound;
            }

            if (!Remove(id, sequence))
            {
                return GemProcessProgramAcknowledgement.PermissionNotGranted;
            }

            return GemProcessProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One file per program means an S7F17 with an empty list is as many deletes as there are
    /// programs. Any that the disk refuses are kept, and the answer is then
    /// <see cref="GemProcessProgramAcknowledgement.PermissionNotGranted"/> rather than an acceptance
    /// of something that did not happen - a host that reads the directory afterwards sees exactly
    /// what is left.
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var refused = 0;

            foreach (var (id, sequence) in _programs.ToArray())
            {
                if (!Remove(id, sequence))
                {
                    refused++;
                }
            }

            if (refused > 0)
            {
                _logger.LogWarning(
                    "{Refused} process program record(s) would not delete, so S7F17 was refused.",
                    refused);

                return GemProcessProgramAcknowledgement.PermissionNotGranted;
            }

            return GemProcessProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered as the in-memory store's is, so a host can diff two answers to S7F19. No gate:
        // the questions that only read cost nothing here.
        return ValueTask.FromResult<IReadOnlyList<string>>(
            [.. _programs.Keys.Order(StringComparer.Ordinal)]);
    }

    // ---------------------------------------------------------------- the files

    private static bool IsValidId(string id)
        => id.Length is > 0 and <= GemInMemoryProcessProgramStore.MaxIdLength;

    /// <summary>Writes one program's record and moves the index onto it.</summary>
    /// <remarks>
    /// One field and a tail. An existing program keeps its sequence number, so a replacement is one
    /// atomic rewrite - the identifier is inside the record, so there is nothing in the file name to
    /// disagree with.
    /// </remarks>
    private async ValueTask WriteAsync(string id, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var sequence = _programs.TryGetValue(id, out var held) ? held : _next;

        await _records.WriteRecordAsync(
            sequence,
            [DurableRecordDirectory.Bytes(id)],
            body,
            cancellationToken).ConfigureAwait(false);

        _next = Math.Max(_next, sequence + 1);
        _programs[id] = sequence;
    }

    /// <summary>Takes a program out, and says whether the disk agreed.</summary>
    private bool Remove(string id, long sequence)
    {
        _records.Delete(sequence);

        if (System.IO.File.Exists(_records.PathFor(sequence)))
        {
            return false;
        }

        _programs.TryRemove(id, out _);

        return true;
    }

    /// <summary>Reads one program's record.</summary>
    /// <remarks>
    /// Null for a record this version cannot read, for one a tool truncated and for one that will
    /// not open - three different disks that mean one thing to a store, and never "delete it".
    /// </remarks>
    private (string Id, ReadOnlyMemory<byte> Body)? Read(string path)
        => _records.TryReadRecord(path) is { Fields.Count: 1, Tail: { } body } record
            ? (DurableRecordDirectory.Text(record.Fields[0]), body)
            : null;
}
