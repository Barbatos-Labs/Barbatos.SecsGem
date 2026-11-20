// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.DataSets;

/// <summary>
/// Where the equipment keeps its data sets, and where arriving ones go.
/// </summary>
/// <remarks>
/// <para>
/// Six methods, and between them they are the whole of what an equipment does in stream 13: one
/// decides whether a data set may be opened at all, one takes what the host sent, one hands back
/// what the host asked for, one lists what there is, one closes, and one answers how big a data
/// set is and whether it is there.
/// </para>
/// <para>
/// <strong>This is the arrival point, not a notification.</strong> There is no event for "a data
/// set came in" beside these methods, deliberately - the same choice <c>IWaferMapStore</c> makes
/// one package over, for the same reason: an application that cares about a data set needs the
/// data set, and a store method already has it, can refuse it, and is awaited before the
/// acknowledgement goes back, none of which an event raised beside the transaction could do.
/// </para>
/// <para>
/// <strong>The sequence is the store's to enforce, not this package's.</strong> E5 names the steps
/// - open, then send or get, then close - and gives no state model for them, so
/// <see cref="DataSetEquipment"/> passes each transaction straight through rather than policing an
/// order nothing in the reference defines. What makes that liveable here, and did not in stream 4,
/// is that <c>ACKC13</c> has a word for the refusal: a store that wants the sequence kept answers
/// <see cref="DataSetAcknowledgement.InvalidDataSet"/> for a data set nothing opened, which is the
/// same thing <c>GemProcessProgramAcknowledgement.PermissionNotGranted</c> is for in stream 7.
/// <see cref="DataSetInMemoryStore"/> does exactly that.
/// </para>
/// <para>
/// Every method is asked inside a transaction the host is waiting on, so none of them should take
/// long. Writing a megabyte to a database while a host holds its T3 open is how a data set
/// transfer times out; keep the bytes and acknowledge.
/// </para>
/// </remarks>
public interface IDataSetStore
{
    /// <summary>Decides whether a data set may be opened - S13F1 and S13F5.</summary>
    /// <param name="request">Which data set, which direction, and how long the host says it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant, and for a read the length this equipment says the data set is.</returns>
    /// <remarks>
    /// <para>
    /// This is the cheap place to say no: after the grant, the next message is the whole data set.
    /// </para>
    /// <para>
    /// <strong><c>GRANT6</c> has no code for "no such data set"</strong>, which matters on a
    /// <see cref="DataSetOpenMode.Request"/>. Its three names are permission, busy, and not
    /// interested or no space - so an equipment asked to open a data set it does not have can only
    /// answer <see cref="DataSetGrant.NoSpace"/>, in the table's own second reading of it. That is
    /// also why S13F15 exists and why a host that wants a straight answer asks it first: the
    /// status reply carries an <c>ACKC13</c>, which does have a word for a data set that is not
    /// there.
    /// </para>
    /// </remarks>
    ValueTask<DataSetOpening> OpenAsync(DataSetOpenRequest request, CancellationToken cancellationToken);

    /// <summary>Takes a data set the host sent - S13F3.</summary>
    /// <param name="content">The data set, as the encoded item that carried it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    /// <remarks>
    /// <see cref="DataSetContent.Data"/> is the store's to keep: it was copied out of the arriving
    /// message before this call, because the message's items go back to the pool when it is
    /// disposed.
    /// </remarks>
    ValueTask<DataSetAcknowledgement> SaveAsync(DataSetContent content, CancellationToken cancellationToken);

    /// <summary>Hands back a data set the host asked for - S13F7.</summary>
    /// <param name="request">Which data set, and the correlation number to be echoed.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The data set, or <see langword="null"/> when there is none.</returns>
    /// <remarks>
    /// <see langword="null"/> is answered with the zero-length list E5 uses for "no data", not with
    /// a refusal: S13F8 carries a <c>DSID</c>, a <c>DATAID</c> and a <c>DSDATA</c> and no code to
    /// put one in. A host that needs to be told why asks S13F15.
    /// </remarks>
    ValueTask<DataSetPayload?> LoadAsync(DataSetRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the data sets this equipment has - S13F11.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Their identifiers. Empty is a legitimate answer and is sent as a zero-length list.</returns>
    /// <remarks>S13F11 carries no body at all - it is the one message in stream 13 that asks nothing in particular.</remarks>
    ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Closes a data set - S13F13.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was closed.</returns>
    /// <remarks>Closing is not deleting. E5 gives stream 13 no message that removes a data set, and this package does not invent one.</remarks>
    ValueTask<DataSetAcknowledgement> CloseAsync(uint dataSetId, CancellationToken cancellationToken);

    /// <summary>Answers how big a data set is and whether it is there - S13F15.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The status.</returns>
    /// <remarks>
    /// The one reply in this stream that carries a length and an acknowledgement together, which
    /// makes it the only transaction here that can say "I do not have that" in so many words.
    /// </remarks>
    ValueTask<DataSetStatus> StatusAsync(uint dataSetId, CancellationToken cancellationToken);
}

/// <summary>
/// A data set store that keeps everything in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or an equipment that passes data sets straight on to something
/// else. Anything that must survive a restart wants its own store - and that is the mechanism,
/// rather than a snapshot: a data set is not state a machine claims about itself, so it has no
/// <c>Capture</c> and <c>Restore</c>, the same answer <c>IGemSpool</c> gives to the same question.
/// </para>
/// <para>
/// <strong>It enforces the sequence, and that is this class's choice rather than E5's.</strong> A
/// data set must be opened for send before <see cref="SaveAsync"/> will take it, and opened for
/// read before <see cref="LoadAsync"/> will hand it over; otherwise the answer is
/// <see cref="DataSetAcknowledgement.InvalidDataSet"/> and a zero-length list respectively. A
/// store that does not care may simply not do this - see <see cref="IDataSetStore"/> for why the
/// decision sits here and not in <see cref="DataSetEquipment"/>.
/// </para>
/// <para>
/// <strong>The length it reports is the length it was given.</strong> On a data set the host sent,
/// that is the <c>LENGTH</c> the open declared; on one handed to <see cref="Add"/>, it is whatever
/// the caller passed, and zero if they passed nothing. It is never computed from the payload,
/// because the encoded size of an item is this library's measure of a thing the dictionary never
/// defined - see <see cref="DataSetOpenRequest.Length"/>.
/// </para>
/// </remarks>
public sealed class DataSetInMemoryStore : IDataSetStore
{
    private readonly ConcurrentDictionary<uint, Held> _dataSets = new();
    private readonly ConcurrentDictionary<uint, DataSetOpenMode> _open = new();

    /// <summary>Gets or sets how many data sets may be held. Default 64.</summary>
    /// <remarks>An open for send beyond this is refused with <see cref="DataSetGrant.NoSpace"/>, which is what the code is for.</remarks>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many data sets are held.</summary>
    public int Count => _dataSets.Count;

    /// <summary>Gets how many data sets are open.</summary>
    public int OpenCount => _open.Count;

    /// <summary>Puts a data set in without a transaction, for a simulator or a test.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <param name="data">Its contents.</param>
    /// <param name="length">What to report as its length. Zero when the caller has no number to give.</param>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <remarks>This is how an equipment offers a data set a host can go and fetch, which stream 13 has no message for: there is no "I have a new data set" primary.</remarks>
    public void Add(uint dataSetId, DataSetPayload data, uint length = 0)
    {
        ArgumentNullException.ThrowIfNull(data);

        _dataSets[dataSetId] = new Held(data, length);
    }

    /// <summary>Gets a data set, or <see langword="null"/> when there is none.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <returns>Its contents.</returns>
    public DataSetPayload? Get(uint dataSetId)
        => _dataSets.TryGetValue(dataSetId, out var held) ? held.Data : null;

    /// <summary>Gets whether a data set is open.</summary>
    /// <param name="dataSetId">Which data set.</param>
    /// <param name="mode">How it is open.</param>
    /// <returns>Whether it is.</returns>
    public bool IsOpen(uint dataSetId, out DataSetOpenMode mode) => _open.TryGetValue(dataSetId, out mode);

    /// <inheritdoc />
    public ValueTask<DataSetOpening> OpenAsync(DataSetOpenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Mode == DataSetOpenMode.Request)
        {
            // GRANT6 has no "no such data set", so "not interested" is the only reading left. A
            // host that wants the difference asks S13F15, whose reply has an ACKC13 in it.
            if (!_dataSets.TryGetValue(request.DataSetId, out var held))
            {
                return ValueTask.FromResult(new DataSetOpening { Grant = DataSetGrant.NoSpace });
            }

            _open[request.DataSetId] = DataSetOpenMode.Request;

            return ValueTask.FromResult(new DataSetOpening
            {
                Grant = DataSetGrant.Ok,
                Length = held.Length,
            });
        }

        if (!_dataSets.ContainsKey(request.DataSetId) && _dataSets.Count >= Capacity)
        {
            return ValueTask.FromResult(new DataSetOpening { Grant = DataSetGrant.NoSpace });
        }

        _open[request.DataSetId] = DataSetOpenMode.Send;

        // The declared length is recorded now rather than when the data arrives, because S13F3
        // does not carry one - the open is the only place the host says how long it is.
        _dataSets.AddOrUpdate(
            request.DataSetId,
            _ => new Held(null, request.Length),
            (_, existing) => existing with { Length = request.Length });

        return ValueTask.FromResult(new DataSetOpening { Grant = DataSetGrant.Ok });
    }

    /// <inheritdoc />
    public ValueTask<DataSetAcknowledgement> SaveAsync(DataSetContent content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_open.TryGetValue(content.DataSetId, out var mode) || mode != DataSetOpenMode.Send)
        {
            return ValueTask.FromResult(DataSetAcknowledgement.InvalidDataSet);
        }

        _dataSets.AddOrUpdate(
            content.DataSetId,
            _ => new Held(content.Data, 0),
            (_, existing) => existing with { Data = content.Data });

        return ValueTask.FromResult(DataSetAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<DataSetPayload?> LoadAsync(DataSetRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_open.TryGetValue(request.DataSetId, out var mode) || mode != DataSetOpenMode.Request)
        {
            return ValueTask.FromResult<DataSetPayload?>(null);
        }

        return ValueTask.FromResult(
            _dataSets.TryGetValue(request.DataSetId, out var held) ? held.Data : null);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Sorted, because a directory a host can diff between two calls is worth more than one
        // that comes back in whatever order a hash table happens to hold.
        IReadOnlyList<uint> identifiers =
        [
            .. _dataSets
                .Where(pair => pair.Value.Data is not null)
                .Select(pair => pair.Key)
                .Order(),
        ];

        return ValueTask.FromResult(identifiers);
    }

    /// <inheritdoc />
    public ValueTask<DataSetAcknowledgement> CloseAsync(uint dataSetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            _open.TryRemove(dataSetId, out _)
                ? DataSetAcknowledgement.Accepted
                : DataSetAcknowledgement.InvalidDataSet);
    }

    /// <inheritdoc />
    public ValueTask<DataSetStatus> StatusAsync(uint dataSetId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_dataSets.TryGetValue(dataSetId, out var held) || held.Data is null)
        {
            return ValueTask.FromResult(new DataSetStatus
            {
                DataSetId = dataSetId,
                Acknowledgement = DataSetAcknowledgement.InvalidDataSet,
            });
        }

        return ValueTask.FromResult(new DataSetStatus
        {
            DataSetId = dataSetId,
            Length = held.Length,
            Acknowledgement = DataSetAcknowledgement.Accepted,
        });
    }

    /// <summary>A held data set: its contents, and the length somebody said it was.</summary>
    /// <remarks>
    /// <see cref="Data"/> is null between an open for send and the data arriving, which is the one
    /// state a data set can be in here that a host cannot see - S13F16 reports such an entry as
    /// not there, because a declared length with nothing behind it is not a data set.
    /// </remarks>
    private sealed record Held(DataSetPayload? Data, uint Length);
}
