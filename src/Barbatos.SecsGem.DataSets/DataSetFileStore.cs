// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.DataSets;

/// <summary>
/// A data set store that outlives the process, by keeping each data set in a file.
/// </summary>
/// <remarks>
/// <para>
/// A data set is whatever the two ends agreed it is - <c>DSDATA</c> is defined nowhere - and this
/// package carries it as the encoded item that arrived. That is exactly what makes it worth
/// writing down: the machine cannot regenerate a host's data set and the host has no reason to
/// think it needs sending twice. <see cref="DataSetInMemoryStore"/> loses them all; this writes one
/// to disk before it answers S13F4, and adopts what is still there when it is next constructed.
/// </para>
/// <para>
/// <strong>The mechanism is <c>DurableRecordDirectory</c>'s</strong>, shared as source with the
/// three other transfer stores: one file per data set, written through, committed with a rename,
/// and a file name that is a sequence number and nothing else. Here that last part is not about
/// escaping - <c>DATAID</c> is a number, and a number makes a perfectly good file name - but about
/// the rewrite: the identifier is <em>inside</em> the record, so replacing a data set is one atomic
/// write of one file and a data set never has two records.
/// </para>
/// <para>
/// <strong>What a restart keeps is a data set with contents, and nothing else.</strong> An open is
/// a transaction in flight, so <see cref="OpenCount"/> is zero on every open of the store; and so
/// is the one state this store will not write down at all - <strong>a declared length with nothing
/// behind it</strong>. S13F1 is where a host says how long a data set will be, because S13F3 does
/// not carry a length, so between the open and the data there is a number and no data set. The
/// in-memory store keeps that as an entry whose contents are null. This keeps it in memory only,
/// and writes the length <em>with</em> the payload or not at all - because a record whose length
/// field disagreed with its own payload is a machine telling a host S13F16 figures it cannot
/// stand behind.
/// </para>
/// <para>
/// <strong>Two differences from the in-memory store, both deliberate and neither visible to a
/// host.</strong> <see cref="Count"/> is what is on the disk, so an open for send with no data yet
/// is not in it, where the in-memory store counts that entry. And <see cref="CloseAsync"/> forgets
/// the declaration as well as the open, which that store does not: an open closed without data is
/// finished, and a declaration left behind would count against <see cref="Capacity"/> for the life
/// of the process with nothing behind it. Every rule a host can observe - what an open grants, what
/// a save needs, what the directory lists, what the status reports - is the same in both.
/// </para>
/// <para>
/// No <c>Capture</c>/<c>Restore</c> pair, for the reason the in-memory store gives: a data set is
/// not state a machine claims about itself, so it survives a restart through an implementation.
/// </para>
/// <para>
/// One store per directory. Two would fight over the sequence numbers.
/// </para>
/// </remarks>
public sealed class DataSetFileStore : IDataSetStore
{
    /// <summary>The extension a finished data set record carries.</summary>
    public const string RecordExtension = ".dataset";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<uint, Held> _dataSets = new();
    private readonly ConcurrentDictionary<uint, DataSetOpenMode> _open = new();
    private readonly ConcurrentDictionary<uint, uint> _declared = new();
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
    /// fails at start-up where an operator is looking. An unfinished write is swept, a file that is
    /// not this store's is left alone, and so is a record this version cannot read.
    /// <see cref="Capacity"/> is not enforced on what is adopted: a directory holding more than
    /// this machine is configured for is a fact, and a store that refused to see it would be
    /// hiding data sets a host can still ask for by number.
    /// </remarks>
    public DataSetFileStore(string directory, ILogger<DataSetFileStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<DataSetFileStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            if (_dataSets.TryGetValue(record.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold data set {Id}; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Id,
                    Math.Max(held.Sequence, sequence),
                    Math.Min(held.Sequence, sequence));

                _records.Delete(Math.Min(held.Sequence, sequence));

                if (sequence < held.Sequence)
                {
                    continue;
                }
            }

            _dataSets[record.Id] = new Held(sequence, record.Length);
            _next = Math.Max(_next, sequence + 1);
        }

        if (_dataSets.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Data set directory {Directory} opened with {DataSets} data set(s), {Partial} unfinished "
                    + "write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _dataSets.Count,
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets how many data sets may be held. Default 64.</summary>
    /// <remarks>
    /// An open for send beyond this is refused with <see cref="DataSetGrant.NoSpace"/>, which is
    /// what the code is for. A declaration with no data yet counts, as it does in the in-memory
    /// store - a host that opened three and sent none has asked for three slots.
    /// </remarks>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many data sets are held.</summary>
    /// <remarks>What is on the disk. An open for send whose data has not arrived is not one.</remarks>
    public int Count => _dataSets.Count;

    /// <summary>Gets how many data sets are open.</summary>
    /// <remarks>Zero on every open of the store: an open is a transaction in flight, so a restart ends it.</remarks>
    public int OpenCount => _open.Count;

    /// <summary>Gets whether a data set is open.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <param name="mode">How it is open.</param>
    /// <returns>Whether it is.</returns>
    public bool IsOpen(uint dataSetId, out DataSetOpenMode mode) => _open.TryGetValue(dataSetId, out mode);

    /// <summary>Gets whether a data set is held, contents and all.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <returns>Whether it is.</returns>
    /// <remarks>
    /// Where <c>DataSetInMemoryStore.Get</c> hands back the contents: they are in a file here, so
    /// asking whether one exists and asking for its bytes are two different costs and two different
    /// members. <see cref="LoadAsync"/> is the second, and it wants an open for read first.
    /// </remarks>
    public bool Has(uint dataSetId) => _dataSets.ContainsKey(dataSetId);

    /// <summary>Puts a data set in without a transaction, and on the disk.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <param name="data">Its contents.</param>
    /// <param name="length">What to report as its length. Zero when the caller has no number to give.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the data set is on the disk.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The write failed. Nothing was stored.</exception>
    /// <remarks>
    /// How an equipment offers a data set a host can go and fetch, which stream 13 has no message
    /// for - there is no "I have a new data set" primary, so a host finds out with S13F13.
    /// Asynchronous where the in-memory store's <c>Add</c> is not, because writing it is what makes
    /// it survive.
    /// </remarks>
    public async ValueTask AddAsync(
        uint dataSetId,
        DataSetPayload data,
        uint length = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteAsync(dataSetId, length, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DataSetOpening> OpenAsync(DataSetOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (request.Mode == DataSetOpenMode.Request)
            {
                // GRANT6 has no "no such data set", so "not interested" is the only reading left. A
                // host that wants the difference asks S13F15, whose reply has an ACKC13 in it.
                if (!_dataSets.TryGetValue(request.DataSetId, out var held))
                {
                    return new DataSetOpening { Grant = DataSetGrant.NoSpace };
                }

                _open[request.DataSetId] = DataSetOpenMode.Request;

                return new DataSetOpening
                {
                    Grant = DataSetGrant.Ok,
                    Length = held.Length,
                };
            }

            // A declaration with no data yet counts against capacity, which is what the in-memory
            // store's null-contents entry does by sitting in the same dictionary.
            var taken = _dataSets.Count + _declared.Keys.Count(id => !_dataSets.ContainsKey(id));

            if (!_dataSets.ContainsKey(request.DataSetId) && !_declared.ContainsKey(request.DataSetId)
                && taken >= Capacity)
            {
                return new DataSetOpening { Grant = DataSetGrant.NoSpace };
            }

            _open[request.DataSetId] = DataSetOpenMode.Send;

            // Remembered rather than written: S13F1 is the only place the host says how long it is,
            // and until the data arrives this is a number about a data set that does not exist yet.
            // Writing it now would put a length on the disk beside a payload it does not describe.
            _declared[request.DataSetId] = request.Length;

            return new DataSetOpening { Grant = DataSetGrant.Ok };
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The record goes down before the acknowledgement, so an interruption between the two leaves a
    /// data set the next open adopts - the right way round, because the host was not told it
    /// arrived and may send it again, onto the same record.
    /// </remarks>
    public async ValueTask<DataSetAcknowledgement> SaveAsync(DataSetContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_open.TryGetValue(content.DataSetId, out var mode) || mode != DataSetOpenMode.Send)
            {
                return DataSetAcknowledgement.InvalidDataSet;
            }

            // The declared length if the open gave one, otherwise whatever this data set was
            // already reporting: a second send under one open must not silently lose the number.
            var length = _declared.TryGetValue(content.DataSetId, out var declared)
                ? declared
                : _dataSets.TryGetValue(content.DataSetId, out var held) ? held.Length : 0u;

            await WriteAsync(content.DataSetId, length, content.Data, cancellationToken).ConfigureAwait(false);

            return DataSetAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<DataSetPayload?> LoadAsync(DataSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_open.TryGetValue(request.DataSetId, out var mode) || mode != DataSetOpenMode.Request)
            {
                return null;
            }

            if (!_dataSets.TryGetValue(request.DataSetId, out var held))
            {
                return null;
            }

            if (Read(_records.PathFor(held.Sequence)) is not { } record)
            {
                // The index says it is here and the file says otherwise, so somebody has been in
                // the directory. S13F6 has no acknowledgement code at all - an empty DSDATA is the
                // whole of the refusal - so the log is the only place this is distinguishable from
                // a data set nobody opened.
                _logger.LogWarning(
                    "The record for data set {Id} could not be read, so the send was refused.",
                    request.DataSetId);

                return null;
            }

            return record.Data;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Every data set on the disk has contents, so this needs no filter where the in-memory store
    /// has one: a record is written when the data arrives, never at the open.
    /// </remarks>
    public ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sorted, because a directory a host can diff between two calls is worth more than one
        // that comes back in whatever order a hash table happens to hold.
        IReadOnlyList<uint> identifiers = [.. _dataSets.Keys.Order()];

        return ValueTask.FromResult(identifiers);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forgets the declaration with the open, which the in-memory store does not: an open that
    /// closed without data is finished, and the number the host gave describes nothing. Nothing is
    /// written, because neither was ever on the disk.
    /// </remarks>
    public async ValueTask<DataSetAcknowledgement> CloseAsync(uint dataSetId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_open.TryRemove(dataSetId, out _))
            {
                return DataSetAcknowledgement.InvalidDataSet;
            }

            if (!_dataSets.ContainsKey(dataSetId))
            {
                _declared.TryRemove(dataSetId, out _);
            }

            return DataSetAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Out of the index rather than off the disk: the length is four bytes beside a sequence number,
    /// and this is the one reply in the stream that carries a length <em>and</em> an
    /// acknowledgement, so it is the transaction a host is advised to send first.
    /// </remarks>
    public ValueTask<DataSetStatus> StatusAsync(uint dataSetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _dataSets.TryGetValue(dataSetId, out var held)
                ? new DataSetStatus
                {
                    DataSetId = dataSetId,
                    Length = held.Length,
                    Acknowledgement = DataSetAcknowledgement.Accepted,
                }
                : new DataSetStatus
                {
                    DataSetId = dataSetId,
                    Acknowledgement = DataSetAcknowledgement.InvalidDataSet,
                });
    }

    // ---------------------------------------------------------------- the files

    /// <summary>Writes one data set's record and moves the index onto it.</summary>
    /// <remarks>
    /// The identifier and the length as four bytes each, then the encoded item. An existing data
    /// set keeps its sequence number, which is what makes a replacement one atomic rewrite.
    /// </remarks>
    private async ValueTask WriteAsync(
        uint dataSetId,
        uint length,
        DataSetPayload data,
        CancellationToken cancellationToken)
    {
        var sequence = _dataSets.TryGetValue(dataSetId, out var held) ? held.Sequence : _next;

        await _records.WriteRecordAsync(
            sequence,
            [DurableRecordDirectory.Bytes(dataSetId), DurableRecordDirectory.Bytes(length)],
            data.Encoded,
            cancellationToken).ConfigureAwait(false);

        _next = Math.Max(_next, sequence + 1);
        _dataSets[dataSetId] = new Held(sequence, length);
        _declared.TryRemove(dataSetId, out _);
    }

    /// <summary>Reads one data set's record.</summary>
    /// <remarks>
    /// The contents come back as <see cref="DataSetPayload.FromEncoded"/>, which is the same way in
    /// they took off the wire: <c>DSDATA</c> is defined nowhere, so what is stored is the item that
    /// arrived and nothing here has ever looked inside it. The format and the count come back null,
    /// which costs nothing - a host gets the item, not a description of it.
    /// </remarks>
    private (uint Id, uint Length, DataSetPayload Data)? Read(string path)
    {
        if (_records.TryReadRecord(path) is not { Fields.Count: 2, Tail: { } encoded } record)
        {
            return null;
        }

        return DurableRecordDirectory.Number(record.Fields[0]) is { } id
            && DurableRecordDirectory.Number(record.Fields[1]) is { } length
                ? (id, length, DataSetPayload.FromEncoded(encoded))
                : null;
    }

    /// <summary>What the index holds: where the record is, and the length it reports.</summary>
    private sealed record Held(long Sequence, uint Length);
}
