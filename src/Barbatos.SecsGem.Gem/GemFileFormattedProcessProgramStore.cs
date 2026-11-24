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
/// A formatted process program store that outlives the process, by keeping each program in a file.
/// </summary>
/// <remarks>
/// <para>
/// The durable half of <see cref="GemInMemoryFormattedProcessProgramStore"/>, whose remarks have
/// said since they were written that real equipment wants its own store - and that <em>a formatted
/// program is far easier to persist than an unformatted one, because it is structure rather than
/// bytes</em>. This chapter is the test of that sentence, and it came out the other way round: the
/// bytes needed one field and a payload, where a structure needs somewhere to put four levels of
/// nesting.
/// </para>
/// <para>
/// <strong>So it is written down as the body S7F23 carries.</strong>
/// <c>GemFormattedProcessProgramCodec</c> already turns a program into that item and reads it back,
/// on the wire, under test - so the record's payload is that item encoded, and the alternative was
/// a second format invented for the disk which would have had to be kept in step with the first by
/// hand. It is the answer <c>Barbatos.SecsGem.DataSets</c> gives for <c>DSDATA</c> and
/// <c>Barbatos.SecsGem.ItemTransfer</c> for <c>ITEMDATA</c>, reached from the opposite direction:
/// there the item was stored whole because nothing defines what is inside it, here because
/// something does.
/// </para>
/// <para>
/// <strong>One consequence worth stating: a program this version cannot read is refused rather than
/// mangled.</strong> If the stored item is not the shape the codec expects - a tool edited it, or a
/// later version wrote a program with a command this one does not represent - the record reads as
/// nothing at all and the identifier stays out of the directory. That is the codec's own rule,
/// quoted: <em>mangling a recipe into something storable would be worse than refusing it, because
/// the sender would be told its recipe landed.</em>
/// </para>
/// <para>
/// Records are <c>.formattedprogram</c>, where the plain store's are <c>.processprogram</c> and the
/// matrix store's are <c>.matrix</c>. The three formats are indistinguishable - an identifier and a
/// payload - so the extension is what stops one store adopting another's programs, and a formatted
/// program coming back from S7F5 as a plain one would be the matrix chapter's mistake in a new
/// place.
/// </para>
/// <para>
/// One store per directory. Two would fight over the sequence numbers.
/// </para>
/// </remarks>
public sealed class GemFileFormattedProcessProgramStore : IGemFormattedProcessProgramStore
{
    /// <summary>The extension a finished program record carries.</summary>
    public const string RecordExtension = ".formattedprogram";

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
    /// The directory is created and scanned here rather than on first use. A record whose item is
    /// not a formatted program is counted as unreadable and left where it is - and that is a wider
    /// net than the other stores cast, because this one has to decode its payload to know whether
    /// it has a program at all.
    /// </remarks>
    public GemFileFormattedProcessProgramStore(
        string directory,
        ILogger<GemFileFormattedProcessProgramStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<GemFileFormattedProcessProgramStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } program)
            {
                unreadable++;
                continue;
            }

            if (_programs.TryGetValue(program.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold formatted process program '{Id}'; keeping sequence {Kept} and deleting {Dropped}.",
                    program.Id,
                    Math.Max(held, sequence),
                    Math.Min(held, sequence));

                _records.Delete(Math.Min(held, sequence));

                if (sequence < held)
                {
                    continue;
                }
            }

            _programs[program.Id] = sequence;
            _next = Math.Max(_next, sequence + 1);
        }

        if (_programs.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Formatted process program directory {Directory} opened with {Programs} program(s), {Partial} "
                    + "unfinished write(s) removed and {Unreadable} unreadable record(s) left alone.",
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

    /// <summary>Gets how many programs are stored.</summary>
    public int Count => _programs.Count;

    /// <summary>Gets whether a program is held.</summary>
    /// <param name="id">PPID.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>The cheap question, where <see cref="LoadAsync"/> reads a file and decodes an item.</remarks>
    public bool Has(string id) => _programs.ContainsKey(id);

    /// <inheritdoc/>
    /// <remarks>
    /// The two refusals the in-memory store makes, in its order, and then the record goes down
    /// before the acknowledgement - so an interruption between the two leaves a program the next
    /// open adopts, which is the right way round because the host was not told it arrived.
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> SaveAsync(
        GemFormattedProcessProgram program,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (program.Id.Length is 0 or > GemInMemoryProcessProgramStore.MaxIdLength)
            {
                return GemProcessProgramAcknowledgement.NotFound;
            }

            if (!_programs.ContainsKey(program.Id) && _programs.Count >= Capacity)
            {
                return GemProcessProgramAcknowledgement.PermissionNotGranted;
            }

            var sequence = _programs.TryGetValue(program.Id, out var held) ? held : _next;

            using var body = GemFormattedProcessProgramCodec.Write(program);

            await _records.WriteRecordAsync(
                sequence,
                [DurableRecordDirectory.Bytes(program.Id)],
                body.ToByteArray(),
                cancellationToken).ConfigureAwait(false);

            _next = Math.Max(_next, sequence + 1);
            _programs[program.Id] = sequence;

            return GemProcessProgramAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<GemFormattedProcessProgram?> LoadAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_programs.TryGetValue(id, out var sequence))
            {
                return null;
            }

            if (Read(_records.PathFor(sequence)) is not { } program)
            {
                _logger.LogWarning(
                    "The record for formatted process program '{Id}' could not be read, so the upload was refused.",
                    id);

                return null;
            }

            return program;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A record the disk will not delete keeps its program in the index, so the store and the disk
    /// agree, and the host is told <see cref="GemProcessProgramAcknowledgement.PermissionNotGranted"/>
    /// - the nearest honest code, <c>ACKC7</c> having nothing that means "I could not".
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

            return Remove(id, sequence)
                ? GemProcessProgramAcknowledgement.Accepted
                : GemProcessProgramAcknowledgement.PermissionNotGranted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
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
                    "{Refused} formatted process program record(s) would not delete, so S7F17 was refused.",
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

        return ValueTask.FromResult<IReadOnlyList<string>>(
            [.. _programs.Keys.Order(StringComparer.Ordinal)]);
    }

    // ---------------------------------------------------------------- the files

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

    /// <summary>Reads one program's record, through the codec that wrote it.</summary>
    /// <remarks>
    /// Three ways to get nothing, and they are deliberately one answer: the record's framing is
    /// wrong, its payload is not a well-formed item, or it is an item the codec will not read as a
    /// program. A machine cannot act on any of them, and none of them is a reason to delete a file.
    /// </remarks>
    private GemFormattedProcessProgram? Read(string path)
    {
        if (_records.TryReadRecord(path) is not { Fields.Count: 1, Tail: { } encoded })
        {
            return null;
        }

        try
        {
            using var item = SecsItem.Decode(encoded);

            // The acknowledgement is kept rather than compared away, because it says which of the
            // two refusals this is and the codec distinguishes them for a reason: LengthError is a
            // body that is not the shape E5 defines, ModeUnsupported is the right shape carrying a
            // CCODE or a PPARM this stack does not represent - which is what a record written by a
            // later version looks like. TryRead nulls the program either way, so returning it alone
            // would behave the same and tell an operator nothing.
            var acknowledgement = GemFormattedProcessProgramCodec.TryRead(item, out var program);

            if (acknowledgement != GemProcessProgramAcknowledgement.Accepted)
            {
                _logger.LogWarning(
                    "A formatted process program record is not one this version reads ({Acknowledgement}): {Path}.",
                    acknowledgement,
                    path);

                return null;
            }

            return program;
        }
        catch (SecsException exception)
        {
            _logger.LogWarning(exception, "A formatted process program record did not decode: {Path}.", path);

            return null;
        }
    }
}
