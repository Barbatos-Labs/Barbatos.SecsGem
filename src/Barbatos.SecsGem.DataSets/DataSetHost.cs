// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.DataSets;

/// <summary>
/// The host side of data set transfer: the end that drives every transaction.
/// </summary>
/// <remarks>
/// <para>
/// Six methods for seven primaries - the open is one method because S13F1 and S13F5 differ by a
/// field and a direction, and both are answered by a grant.
/// </para>
/// <para>
/// <strong>The sequence is documented and not enforced.</strong> E5 names the steps and this class
/// will send them in whatever order it is asked to, which is the choice
/// <c>Barbatos.SecsGem.Material</c> makes about streams 3 and 4 and the opposite of the one
/// <c>Barbatos.SecsGem.Carriers</c> makes about E87. The difference is always what there is to
/// check against: E87 defines state machines, and stream 13 is sixteen message rows with no state
/// model anywhere in this reference. A host that refused to send S13F3 before S13F1 would be
/// enforcing this library's reading of the function names.
/// </para>
/// <para>
/// What this end does do is read its replies properly. A reply that carries no code at all is a
/// <see cref="SecsException"/> rather than a guess, and S13F8 is checked for echoing back the
/// identifiers it was asked with - the rule <c>Barbatos.SecsGem.Objects</c> already keeps for
/// S2F64, and the only correlation stream 13 offers.
/// </para>
/// <para>
/// One instance sits on one <see cref="HsmsConnection"/>. It holds nothing that needs releasing
/// and is not disposable.
/// </para>
/// </remarks>
public sealed class DataSetHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;
    private int _dataId;

    /// <summary>Initialises the host side over a connection.</summary>
    /// <param name="connection">The link.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public DataSetHost(HsmsConnection connection, ILogger<DataSetHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<DataSetHost>.Instance;
    }

    /// <summary>Every primary this end answers: none.</summary>
    /// <remarks>
    /// <para>
    /// Empty, and that is the fact rather than an omission. All seven of stream 13's primaries are
    /// marked host to equipment in the catalogue and all seven replies the other way, so nothing in
    /// this stream ever arrives at a host unasked - not a report, not an error, not a notification
    /// that a new data set exists. This is the only end in this repository with nothing to declare,
    /// and <c>AddDataSetHost</c> accordingly adds no route to the message table.
    /// </para>
    /// <para>
    /// It is still here, and still public, because that claim is worth asserting in a test rather
    /// than leaving in a comment.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } = [];

    /// <summary>Opens a data set - S13F1 or S13F5, answered by S13F2 or S13F6.</summary>
    /// <param name="dataSetId">Which data set - <c>DSID</c>.</param>
    /// <param name="mode">Whether this is an open to send into or an open to read from.</param>
    /// <param name="length">How long the data set is - <c>LENGTH</c>. Carried by S13F1 only, and ignored for an open to read.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant, and for an open to read the length the equipment says the data set is.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is not one of the two.</exception>
    /// <exception cref="SecsException">The reply carried no grant code.</exception>
    /// <remarks>
    /// Worth doing before a large data set rather than after: the open carries the length, so an
    /// equipment with no room can refuse for the price of two small messages. On an open to read,
    /// a refusal is <see cref="DataSetGrant.NoSpace"/> whether the equipment is unwilling or simply
    /// does not have that data set - <c>GRANT6</c> has no code for the second, which is what
    /// <see cref="StatusAsync"/> is for.
    /// </remarks>
    public async ValueTask<DataSetOpening> OpenAsync(
        uint dataSetId,
        DataSetOpenMode mode,
        uint length = 0,
        CancellationToken cancellationToken = default)
    {
        var (function, name, body) = mode switch
        {
            DataSetOpenMode.Send => (
                (byte)1,
                "OpenDataSetSend",
                DataSetCodec.OpenSendBody(dataSetId, length)),
            DataSetOpenMode.Request => (
                (byte)5,
                "OpenDataSetRequest",
                DataSetCodec.DataSetIdBody(dataSetId)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Stream 13 has two opens: S13F1 and S13F5."),
        };

        using var reply = await RequestAsync(
            new SecsMessage(13, function, replyExpected: true) { Name = name, Item = body },
            cancellationToken).ConfigureAwait(false);

        if (!DataSetCodec.TryReadOpening(reply.Item, out var code, out var declared))
        {
            throw new SecsException($"S13F{function + 1} carried no GRANT6.");
        }

        return new DataSetOpening
        {
            Grant = ReadGrant(code, function),
            Length = declared,
        };
    }

    /// <summary>Sends a data set - S13F3, answered by S13F4.</summary>
    /// <param name="dataSetId">Which data set - <c>DSID</c>.</param>
    /// <param name="data">The contents - <c>DSDATA</c>. <strong>Taken</strong>: it goes into the outgoing message and is disposed with it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the equipment took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <para>
    /// The item goes out as it is given. This library has no opinion about what a data set is,
    /// because the dictionary defines no format for <c>DSDATA</c> - a list, a block of bytes, a
    /// string, all of them cross unaltered.
    /// </para>
    /// <para>
    /// <strong>The item is consumed</strong>, which is the contract
    /// <c>Barbatos.SecsGem.Objects</c> already states for an attribute value handed to this
    /// library: it becomes part of the message, and the message releases it. A caller who wants to
    /// send the same data set twice sends a <see cref="DataSetPayload"/> instead, which decodes a
    /// fresh item each time.
    /// </para>
    /// <para>
    /// The <c>DATAID</c> is this end's own, incremented per send, and is not a parameter: it is a
    /// correlation number rather than a name for anything, so letting a caller choose it would
    /// invite two sends with the same one. See <see cref="DataSetRequest.DataId"/> for why that is
    /// what the item is taken to mean.
    /// </para>
    /// </remarks>
    public async ValueTask<DataSetAcknowledgement> SendAsync(
        uint dataSetId,
        SecsItem data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var reply = await RequestAsync(
            new SecsMessage(13, 3, replyExpected: true)
            {
                Name = "DataSetSend",
                Item = DataSetCodec.ContentBody(dataSetId, NextDataId(), data),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S13F4");
    }

    /// <summary>Sends a data set that is already held as bytes - S13F3, answered by S13F4.</summary>
    /// <param name="dataSetId">Which data set - <c>DSID</c>.</param>
    /// <param name="data">The contents. Stays the caller's, and may be sent again.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the equipment took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code, or the payload is not a well-formed item.</exception>
    /// <remarks>
    /// The overload to use when forwarding: a data set that came back from
    /// <see cref="GetAsync"/> goes straight into this one and crosses the second link as the same
    /// bytes it crossed the first. A fresh item is decoded for the message, so the payload is
    /// untouched and reusable.
    /// </remarks>
    public async ValueTask<DataSetAcknowledgement> SendAsync(
        uint dataSetId,
        DataSetPayload data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var reply = await RequestAsync(
            new SecsMessage(13, 3, replyExpected: true)
            {
                Name = "DataSetSend",
                Item = DataSetCodec.ContentBody(dataSetId, NextDataId(), data.Decode()),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S13F4");
    }

    /// <summary>Asks for a data set - S13F7, answered by S13F8.</summary>
    /// <param name="dataSetId">Which data set - <c>DSID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The data set, or <see langword="null"/> when the equipment has none.</returns>
    /// <exception cref="SecsException">The reply was not a data set.</exception>
    /// <remarks>
    /// <para>
    /// <see langword="null"/> is what a zero-length <c>DSDATA</c> means: S13F8 has no
    /// acknowledgement code, so "I do not have it" and "here it is" arrive in the same message
    /// shape and are told apart by the payload being empty. A host that needs the difference
    /// spelled out asks <see cref="StatusAsync"/>, whose reply carries an <c>ACKC13</c>.
    /// </para>
    /// <para>
    /// The returned payload is the caller's and outlives the reply message, which is the whole
    /// point of <see cref="DataSetPayload"/>: the item in the reply is released when the reply is,
    /// so what comes back is a copy.
    /// </para>
    /// </remarks>
    public async ValueTask<DataSetPayload?> GetAsync(
        uint dataSetId,
        CancellationToken cancellationToken = default)
    {
        var dataId = NextDataId();

        using var reply = await RequestAsync(
            new SecsMessage(13, 7, replyExpected: true)
            {
                Name = "DataSetGet",
                Item = DataSetCodec.RequestBody(dataSetId, dataId),
            },
            cancellationToken).ConfigureAwait(false);

        if (reply.Item is { IsList: true, Count: 0 })
        {
            // The shape this library's own equipment end sends for a request it could not read.
            _logger.LogWarning("S13F8 for data set {DataSetId} carried a zero-length body.", dataSetId);
            return null;
        }

        if (!DataSetCodec.TryReadContent(reply.Item, out var echoedDataSet, out var echoedDataId, out var data)
            || data is null)
        {
            throw new SecsException("S13F8 did not carry a data set.");
        }

        if (echoedDataSet != dataSetId || echoedDataId != dataId)
        {
            // The one correlation stream 13 offers, and the same check Objects keeps on S2F64: a
            // machine that echoed something else has answered a different question, and acting on
            // it would file one data set's contents under another's identifier.
            _logger.LogWarning(
                "S13F8 echoed data set {EchoedDataSet} #{EchoedDataId} for a request that asked for {DataSetId} #{DataId}.",
                echoedDataSet,
                echoedDataId,
                dataSetId,
                dataId);
        }

        return data.IsEmpty ? null : data;
    }

    /// <summary>Asks what data sets the equipment has - S13F11, answered by S13F12.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Their identifiers. Empty when the equipment has none.</returns>
    /// <remarks>
    /// The only primary in stream 13 with no body: it asks nothing in particular, so there is
    /// nothing to get wrong. An unreadable reply comes back empty rather than throwing, because a
    /// directory that could not be parsed and a machine with no data sets are not worth
    /// distinguishing by an exception - and the log says which happened.
    /// </remarks>
    public async ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(13, 11, replyExpected: true) { Name = "DataSetDirectoryRequest" },
            cancellationToken).ConfigureAwait(false);

        var dataSetIds = DataSetCodec.ReadDirectory(reply.Item);

        if (dataSetIds.Count == 0 && reply.Item is { Count: > 0 })
        {
            _logger.LogWarning("S13F12 carried a body with no readable DSID in it.");
        }

        return dataSetIds;
    }

    /// <summary>Closes a data set - S13F13, answered by S13F14.</summary>
    /// <param name="dataSetId">Which data set - <c>DSID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was closed.</returns>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>Closing is not deleting: stream 13 has no message that removes a data set from an equipment.</remarks>
    public async ValueTask<DataSetAcknowledgement> CloseAsync(
        uint dataSetId,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(13, 13, replyExpected: true)
            {
                Name = "DataSetClose",
                Item = DataSetCodec.DataSetIdBody(dataSetId),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S13F14");
    }

    /// <summary>Asks whether a data set is there and how big - S13F15, answered by S13F16.</summary>
    /// <param name="dataSetId">Which data set - <c>DSID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The status.</returns>
    /// <exception cref="SecsException">The reply was not a data set status.</exception>
    /// <remarks>
    /// The transaction to send first. Of stream 13's seven replies this is the only one that
    /// carries a length and an acknowledgement together, so it is the only way to tell "I have not
    /// got that" from "I will not give it to you" before committing to an open.
    /// </remarks>
    public async ValueTask<DataSetStatus> StatusAsync(
        uint dataSetId,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(13, 15, replyExpected: true)
            {
                Name = "DataSetStatusRequest",
                Item = DataSetCodec.DataSetIdBody(dataSetId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!DataSetCodec.TryReadStatus(reply.Item, out var echoed, out var length, out var code))
        {
            throw new SecsException("S13F16 did not carry a data set status.");
        }

        if (echoed != dataSetId)
        {
            _logger.LogWarning(
                "S13F16 reported on data set {Echoed} for a request that asked about {DataSetId}.",
                echoed,
                dataSetId);
        }

        return new DataSetStatus
        {
            DataSetId = echoed,
            Length = length,
            Acknowledgement = ReadAcknowledgement(code, "S13F16"),
        };
    }

    // ---------------------------------------------------------------- the pieces

    private uint NextDataId() => (uint)Interlocked.Increment(ref _dataId);

    private DataSetGrant ReadGrant(byte code, byte function)
    {
        if (code < (byte)DataSetGrant.Other)
        {
            return (DataSetGrant)code;
        }

        _logger.LogInformation(
            "S13F{Function} refused the open with GRANT6 {Code}, which the table reserves and does not name.",
            function + 1,
            code);

        return DataSetGrant.Other;
    }

    private DataSetAcknowledgement ReadAcknowledgement(SecsMessage reply, string what)
        => ReadAcknowledgement(
            DataSetCodec.ReadCode(reply.Item) ?? throw new SecsException($"{what} carried no ACKC13."),
            what);

    private DataSetAcknowledgement ReadAcknowledgement(byte code, string what)
    {
        if (code < (byte)DataSetAcknowledgement.Other)
        {
            return (DataSetAcknowledgement)code;
        }

        _logger.LogInformation(
            "{What} carried ACKC13 {Code}, which that table does not name at all - it lists four codes and declares no reserved range.",
            what,
            code);

        return DataSetAcknowledgement.Other;
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        // The primary is built by the caller and of no use afterwards, so it is disposed here. The
        // reply belongs to whoever reads it, which is this class in every case.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
