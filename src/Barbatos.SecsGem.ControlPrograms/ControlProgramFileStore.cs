// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ControlPrograms;

/// <summary>
/// A control program store that outlives the process, by keeping each program in a file.
/// </summary>
/// <remarks>
/// <para>
/// Firmware is the thing in this stack that would hurt most to lose: a machine that came back from
/// a power cut with no boot program would need a host to send it again before it could run, and
/// <see cref="ControlProgramInMemoryStore"/> loses the lot. This writes a program to disk before it
/// answers S8F4, and adopts what is still there when it is next constructed.
/// </para>
/// <para>
/// <strong>The mechanism is <c>DurableRecordDirectory</c>'s</strong>, shared as source with the
/// three other transfer stores here: one file per program, written through, committed with a
/// rename, and a file name that is a sequence number and nothing else - so <c>CPID</c>, which is an
/// <c>A[n]</c> the reference gives no vocabulary, never becomes part of a path. The identifier and
/// the revision are <em>inside</em> the record, which is also what makes an overwrite one atomic
/// rewrite of one file.
/// </para>
/// <para>
/// <strong>Its own API is the only way a named program gets in, and that is stream 8's doing
/// rather than this class's.</strong> S8F3 sends a program with <strong>no name</strong> - the
/// anonymous download this package holds under <see cref="BootProgramId"/> - and no row of the
/// stream names a program a host can create. So <see cref="AddAsync"/> is not a convenience like
/// <c>RecipeInMemoryStore.Add</c>: without it a machine could never offer a named program for
/// S8F11 to find, and the directory would be whatever was already on the disk.
/// </para>
/// <para>
/// <strong>What a restart keeps is what was sent, and the grant is not.</strong> The programs and
/// their revisions come back; <see cref="GrantedLength"/> does not, because an S8F1 answered and an
/// S8F3 that never arrived is a transaction in flight, and anything in progress ends the way an
/// interruption ends. The host inquires again. <see cref="Capacity"/> and
/// <see cref="MaxLength"/> are this machine's configuration rather than anything a host said, so
/// they are not written down either.
/// </para>
/// <para>
/// No <c>Capture</c>/<c>Restore</c> pair, on purpose and for the reason the in-memory store already
/// gives: a control program is not state a machine claims about itself, so it survives a restart
/// through an implementation - this one.
/// </para>
/// <para>
/// One store per directory. Two would fight over the sequence numbers.
/// </para>
/// </remarks>
public sealed class ControlProgramFileStore : IControlProgramStore
{
    /// <summary>The extension a finished program record carries.</summary>
    public const string RecordExtension = ".program";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    /// <summary>The identifier the boot program is held under, because S8F3 does not carry one.</summary>
    /// <remarks>
    /// The same convention <see cref="ControlProgramInMemoryStore.BootProgramId"/> makes, and
    /// defined from it rather than beside it: a machine that swapped one store for the other must
    /// not find its boot program somewhere else.
    /// </remarks>
    public const string BootProgramId = ControlProgramInMemoryStore.BootProgramId;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Held> _programs = new(StringComparer.Ordinal);
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
    /// fails at start-up where an operator is looking and not when a host finally sends firmware.
    /// An unfinished write is swept, a file that is not this store's is left alone, and so is a
    /// record this version cannot read - a downgrade should not throw away a machine's firmware.
    /// <see cref="Capacity"/> is <em>not</em> enforced on what is adopted: a directory holding more
    /// than this machine is configured for is a fact, and refusing to see it would be worse than
    /// reporting it.
    /// </remarks>
    public ControlProgramFileStore(string directory, ILogger<ControlProgramFileStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<ControlProgramFileStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            // Two records for one program cannot be left behind by this store - an overwrite is one
            // file - so this is somebody having copied files in. The higher sequence is the later
            // write, and keeping both would make the answer depend on enumeration order.
            if (_programs.TryGetValue(record.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold control program '{Id}'; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Id,
                    Math.Max(held.Sequence, sequence),
                    Math.Min(held.Sequence, sequence));

                _records.Delete(Math.Min(held.Sequence, sequence));

                if (sequence < held.Sequence)
                {
                    continue;
                }
            }

            _programs[record.Id] = new Held(sequence, record.Revision);
            _next = Math.Max(_next, sequence + 1);
        }

        if (_programs.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Control program directory {Directory} opened with {Programs} program(s), the boot program "
                    + "{Boot} among them, {Partial} unfinished write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _programs.Count,
                _programs.ContainsKey(BootProgramId) ? "is" : "is not",
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets how many named programs may be held, not counting the boot program. Default 16.</summary>
    /// <remarks>An inquire beyond this is refused with <see cref="ControlProgramGrant.NoSpace"/>, which is what the code is for.</remarks>
    public int Capacity { get; set; } = 16;

    /// <summary>Gets or sets how large a program may be, in bytes. Default 4 MiB. Zero means no limit.</summary>
    /// <remarks>
    /// Checked against the <c>LENGTH</c> the inquire declares and again against what arrives, which
    /// stream 13 cannot do: <c>CPBODY</c> has a format and therefore a byte count where
    /// <c>DSDATA</c> has neither.
    /// </remarks>
    public int MaxLength { get; set; } = 4 * 1024 * 1024;

    /// <summary>Gets how many programs are held, the boot program included.</summary>
    public int Count => _programs.Count;

    /// <summary>Gets how long the last inquire said the program would be, or <see langword="null"/> when none has been granted.</summary>
    /// <remarks>Never restored: a grant is a transaction in flight, so a restart ends it and the host inquires again.</remarks>
    public uint? GrantedLength { get; private set; }

    /// <summary>Gets whether a program is held.</summary>
    /// <param name="controlProgramId">Which program. Empty for the boot program.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    /// Where <c>ControlProgramInMemoryStore.Find</c> hands back the program itself: the body is in a
    /// file here, so asking whether one exists and asking for its bytes are two different costs and
    /// two different members. <see cref="LoadAsync"/> is the second.
    /// </remarks>
    public bool Has(string controlProgramId) => _programs.ContainsKey(controlProgramId);

    /// <summary>Puts a named program in without a transaction, and on the disk.</summary>
    /// <param name="controlProgramId">What it is called.</param>
    /// <param name="body">Its bytes.</param>
    /// <param name="revision">What revision it is - <c>SOFTREV</c>. Empty for none.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the program is on the disk.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlProgramId"/> or <paramref name="revision"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The write failed. Nothing was stored.</exception>
    /// <remarks>
    /// <para>
    /// <strong>Not a convenience, and not a back door.</strong> No row of stream 8 names a program a
    /// host can create - S8F3 is anonymous - so this is the only way a named program ever gets into
    /// a machine, and S8F11 can only list what somebody put here. The in-memory store's <c>Add</c>
    /// is the same method without the disk, and asynchronous here because writing one is what makes
    /// it survive.
    /// </para>
    /// <para>
    /// Replaces a program of the same name by rewriting its own record, so the program a host
    /// fetched yesterday and the one it fetches now are never both on the disk.
    /// <see cref="Capacity"/> is not checked: it is the answer to an inquire from a host, not a
    /// limit on what the machine's own software may load.
    /// </para>
    /// </remarks>
    public async ValueTask AddAsync(
        string controlProgramId,
        ReadOnlyMemory<byte> body,
        string revision = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);
        ArgumentNullException.ThrowIfNull(revision);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteAsync(controlProgramId, revision, body, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ControlProgramGrant> InquireAsync(uint length, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (MaxLength > 0 && length > (uint)MaxLength)
            {
                return ControlProgramGrant.NoSpace;
            }

            // The in-memory store's rule, kept here rather than shared: the boot program does not
            // count against Capacity, so a machine already holding one has room for the next.
            if (!_programs.ContainsKey(BootProgramId) && _programs.Count >= Capacity + 1)
            {
                return ControlProgramGrant.NoSpace;
            }

            GrantedLength = length;

            return ControlProgramGrant.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The record goes down before the grant is spent, so an interruption between the write and the
    /// acknowledgement leaves firmware the next open adopts - the right way round, because the host
    /// was not told it arrived and will send it again, onto the same record.
    /// </remarks>
    public async ValueTask<ControlProgramAcknowledgement> SaveAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (GrantedLength is null)
            {
                // Nothing was granted, so nothing was offered. ACKC8 has one refusal and no way to
                // say which of these happened, which is the item's limit and not this store's.
                return ControlProgramAcknowledgement.Denied;
            }

            if (GrantedLength != (uint)body.Length)
            {
                GrantedLength = null;

                return ControlProgramAcknowledgement.Denied;
            }

            // The boot program keeps an empty revision: S8F3 carries no SOFTREV, and a store that
            // invented one would be answering S8F10 with a number nobody sent.
            await WriteAsync(BootProgramId, string.Empty, body, cancellationToken).ConfigureAwait(false);

            GrantedLength = null;

            return ControlProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>?> LoadAsync(
        string controlProgramId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_programs.TryGetValue(controlProgramId, out var held))
            {
                return null;
            }

            if (Read(_records.PathFor(held.Sequence)) is not { } record)
            {
                // The index says it is here and the file says otherwise, so somebody has been in
                // the directory. S8F6 carries no acknowledgement at all - an empty CPBODY is the
                // whole of the refusal - so the log is the only place this can be told from a
                // program a host asked for by the wrong name.
                _logger.LogWarning(
                    "The record for control program '{Id}' could not be read, so the upload was refused.",
                    controlProgramId);

                return null;
            }

            return record.Body;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sorted so a host can diff two listings, and the boot program left out because it has no
        // name for a host to ask with. No gate: the questions that only read cost nothing here.
        IReadOnlyList<string> identifiers =
        [
            .. _programs.Keys
                .Where(id => id.Length > 0)
                .Order(StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(identifiers);
    }

    /// <inheritdoc />
    public async ValueTask<ControlProgramAcknowledgement> AbortAsync(
        string controlProgramId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (GrantedLength is null)
            {
                return ControlProgramAcknowledgement.Denied;
            }

            // Forgets the grant and keeps what is stored, and writes nothing: stream 8 has no
            // message that deletes a program, and the grant was never on the disk to be removed.
            GrantedLength = null;

            return ControlProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Out of the index rather than off the disk: a revision is a few characters kept beside the
    /// sequence number, and this is the question stream 8 advises a host to ask <em>first</em> -
    /// S8F6 cannot say no, so a host that cares which firmware it is asks S8F9 before S8F5.
    /// </remarks>
    public ValueTask<string?> RevisionAsync(string controlProgramId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _programs.TryGetValue(controlProgramId, out var held) && held.Revision.Length > 0
                ? held.Revision
                : null);
    }

    // ---------------------------------------------------------------- the files

    /// <summary>Writes one program's record and moves the index onto it.</summary>
    /// <remarks>
    /// Two fields and a tail. An existing program keeps its sequence number, which is what makes a
    /// replacement one atomic rewrite - the identifier is inside the record, so there is nothing in
    /// the file name to disagree with.
    /// </remarks>
    private async ValueTask WriteAsync(
        string controlProgramId,
        string revision,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        var sequence = _programs.TryGetValue(controlProgramId, out var held) ? held.Sequence : _next;

        await _records.WriteRecordAsync(
            sequence,
            [DurableRecordDirectory.Bytes(controlProgramId), DurableRecordDirectory.Bytes(revision)],
            body,
            cancellationToken).ConfigureAwait(false);

        _next = Math.Max(_next, sequence + 1);
        _programs[controlProgramId] = new Held(sequence, revision);
    }

    /// <summary>Reads one program's record.</summary>
    /// <remarks>
    /// Null for a record this version cannot read, for one a tool truncated and for one that will
    /// not open - three different disks that mean one thing to a store, and never "delete it".
    /// </remarks>
    private (string Id, string Revision, ReadOnlyMemory<byte> Body)? Read(string path)
        => _records.TryReadRecord(path) is { Fields.Count: 2, Tail: { } body } record
            ? (DurableRecordDirectory.Text(record.Fields[0]),
                DurableRecordDirectory.Text(record.Fields[1]),
                body)
            : null;

    /// <summary>What the index holds: where the record is, and the revision read out of it.</summary>
    /// <remarks>
    /// The revision is kept in memory where the body is not, because <see cref="RevisionAsync"/> is
    /// the question a host is told to ask first and a disk read per S8F9 would be a strange price
    /// for a few characters.
    /// </remarks>
    private sealed record Held(long Sequence, string Revision);
}
