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
/// A process program matrix store that outlives the process, by keeping each matrix in a file.
/// </summary>
/// <remarks>
/// <para>
/// The durable half of <see cref="GemInMemoryProcessProgramMatrixStore"/>. A matrix arrives the way
/// a plain process program does and is stored the same way - an identifier and bytes - so this is
/// the shortest of the three stores here, and the one with the sharpest reason for existing apart.
/// </para>
/// <para>
/// <strong>Separate store, separate directory, separate extension.</strong> The matrix chapter's
/// finding was that a matrix kept in the plain store would come back from S7F5 under its own
/// <c>PPID</c> as a plain program, because the rows are item for item the plain ones - so giving the
/// matrix its own store is what makes that not happen. On a disk the same mistake is available
/// again, and more quietly: the records of all three stream 7 stores are an identifier and a
/// payload and nothing distinguishes them. Records here are therefore <c>.matrix</c>, where the
/// plain store's are <c>.processprogram</c>, so a store pointed at the wrong directory finds
/// nothing rather than finding somebody else's programs. A directory per store is still the
/// recommendation, because the sweep of unfinished writes is directory-wide.
/// </para>
/// <para>
/// <strong>There is no delete, which is the stream and not an omission.</strong> S7F17 names
/// process programs, and this store has no place in it - that is what the matrix chapter decided
/// and what <see cref="IGemProcessProgramMatrixStore"/> records. So a matrix on a disk stays there
/// until the application removes it: <see cref="Forget"/> is the door for that, and nothing on the
/// wire reaches it.
/// </para>
/// <para>
/// The mechanism is <c>DurableRecordDirectory</c>'s, shared as source with the four transfer
/// packages and the two stores beside this one. One store per directory.
/// </para>
/// </remarks>
public sealed class GemFileProcessProgramMatrixStore : IGemProcessProgramMatrixStore
{
    /// <summary>The extension a finished matrix record carries.</summary>
    public const string RecordExtension = ".matrix";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, long> _matrices = new(StringComparer.Ordinal);
    private readonly DurableRecordDirectory _records;
    private readonly ILogger _logger;

    private long _next;

    /// <summary>Opens a store over a directory, adopting whatever is already in it.</summary>
    /// <param name="directory">Where the records live. Created if it does not exist.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    public GemFileProcessProgramMatrixStore(
        string directory,
        ILogger<GemFileProcessProgramMatrixStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<GemFileProcessProgramMatrixStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            if (_matrices.TryGetValue(record.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold matrix '{Id}'; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Id,
                    Math.Max(held, sequence),
                    Math.Min(held, sequence));

                _records.Delete(Math.Min(held, sequence));

                if (sequence < held)
                {
                    continue;
                }
            }

            _matrices[record.Id] = sequence;
            _next = Math.Max(_next, sequence + 1);
        }

        if (_matrices.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Matrix directory {Directory} opened with {Matrices} matrix/matrices, {Partial} unfinished "
                    + "write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _matrices.Count,
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets how many matrices may be stored at once. Default 64.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets or sets the largest body this store will accept. Default one mebibyte.</summary>
    public int MaxBodyLength { get; set; } = 1024 * 1024;

    /// <summary>Gets how many matrices are stored.</summary>
    public int Count => _matrices.Count;

    /// <summary>Gets whether a matrix is held.</summary>
    /// <param name="id">PPID.</param>
    /// <returns>Whether it is.</returns>
    public bool Has(string id) => _matrices.ContainsKey(id);

    /// <summary>Removes a matrix, which no message of stream 7 can do.</summary>
    /// <param name="id">PPID.</param>
    /// <returns>Whether there was one, and the disk let it go.</returns>
    /// <remarks>
    /// <strong>The door the stream does not give.</strong> S7F17 deletes process programs and this
    /// store is deliberately not in it, so without this a matrix would live on a disk for ever and
    /// the only way out would be an operator with a file manager. It is not reachable from the wire:
    /// <see cref="GemEquipment"/> routes no message here.
    /// </remarks>
    public bool Forget(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        _gate.Wait();

        try
        {
            if (!_matrices.TryGetValue(id, out var sequence))
            {
                return false;
            }

            _records.Delete(sequence);

            if (System.IO.File.Exists(_records.PathFor(sequence)))
            {
                _logger.LogWarning("The record for matrix '{Id}' would not delete, so it is still held.", id);

                return false;
            }

            _matrices.TryRemove(id, out _);
            _logger.LogInformation("Matrix '{Id}' was removed by hand, which no host asked for.", id);

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    /// <remarks>The in-memory store's four answers, in its order, and nothing is remembered between the inquire and the send.</remarks>
    public ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();

        GemProcessProgramGrant grant;

        if (!IsValidId(id))
        {
            grant = GemProcessProgramGrant.InvalidId;
        }
        else if (_matrices.ContainsKey(id))
        {
            grant = GemProcessProgramGrant.AlreadyHave;
        }
        else if (length > MaxBodyLength || _matrices.Count >= Capacity)
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

            if (!_matrices.ContainsKey(id) && _matrices.Count >= Capacity)
            {
                // MatrixOverflow, which is ACKC7 code 3 and the table's own word for this - the one
                // code in it that belongs to these rows rather than to the plain ones. Writing this
                // store is what found it: the member had been in the enum since the sweep that
                // matched every enum to its table, and nothing in the repository ever sent it,
                // because the in-memory store answered the generic refusal. It answers this now too.
                return GemProcessProgramAcknowledgement.MatrixOverflow;
            }

            var sequence = _matrices.TryGetValue(id, out var held) ? held : _next;

            await _records.WriteRecordAsync(
                sequence,
                [DurableRecordDirectory.Bytes(id)],
                body,
                cancellationToken).ConfigureAwait(false);

            _next = Math.Max(_next, sequence + 1);
            _matrices[id] = sequence;

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
            // Written out rather than as a conditional, for the reason the in-memory stores give at
            // their own LoadAsync: the one-liner turns "no such matrix" into a matrix of zero bytes,
            // and that one has already been caught twice in this repository.
            if (!_matrices.TryGetValue(id, out var sequence))
            {
                return null;
            }

            if (Read(_records.PathFor(sequence)) is not { } record)
            {
                _logger.LogWarning("The record for matrix '{Id}' could not be read, so there is none to verify.", id);

                return null;
            }

            return record.Body;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- the files

    private static bool IsValidId(string id)
        => id.Length is > 0 and <= GemInMemoryProcessProgramMatrixStore.MaxIdLength;

    /// <summary>Reads one matrix's record.</summary>
    private (string Id, ReadOnlyMemory<byte> Body)? Read(string path)
        => _records.TryReadRecord(path) is { Fields.Count: 1, Tail: { } body } record
            ? (DurableRecordDirectory.Text(record.Fields[0]), body)
            : null;
}
