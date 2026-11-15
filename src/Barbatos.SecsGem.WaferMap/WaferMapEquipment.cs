// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.WaferMap;

/// <summary>
/// The equipment side of wafer mapping: the end that sends maps and asks for them.
/// </summary>
/// <remarks>
/// <para>
/// Stream 12 runs the other way round from the rest of this library. In GEM the host asks and the
/// equipment answers; here the <em>equipment</em> sends every odd function and the host answers
/// every even one. A prober knows which die it probed and a host knows which wafer it wants
/// mapped, so each end of this package is the other end of what a GEM reader expects.
/// </para>
/// <para>
/// There is no state model here and no timer. E5 gives stream 12 no states to defend: a map setup
/// is sent or it is not, a transmit is granted or it is not, and the one thing that would need
/// remembering - whether a grant has been given - belongs to the application, which knows what it
/// is about to send. So this class is the message set written down once, with the acknowledgement
/// codes decoded, and nothing else.
/// </para>
/// <para>
/// <strong>What a refusal looks like.</strong> Every send returns what the other end answered, as
/// an enum. A reply whose body is not an acknowledgement at all throws
/// <see cref="SecsException"/> rather than returning
/// <see cref="WaferMapSetupAcknowledgement.Accepted"/>, because a zero that means "nothing arrived"
/// is the one failure a caller cannot see. The same goes for a request whose answer is not map
/// data; a host that has no map for the material answers a zero-length list, and that comes back
/// as <see langword="null"/> rather than as an exception.
/// </para>
/// <para>
/// One instance sits on one <see cref="HsmsConnection"/>. It does not run the connection and holds
/// nothing that needs releasing, which is why it is not disposable: a map transfer is a
/// transaction, and when it ends there is nothing left over.
/// </para>
/// </remarks>
public sealed class WaferMapEquipment
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics, including the acknowledgement codes E5 leaves to the vendor. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public WaferMapEquipment(HsmsConnection connection, ILogger<WaferMapEquipment>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<WaferMapEquipment>.Instance;
    }

    /// <summary>Every primary this end answers.</summary>
    /// <remarks>
    /// One, and it is the only message in stream 12 that travels in both directions: a host may
    /// report that the map it was sent was no good. Everything else here is a primary this end
    /// sends.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (12, 19),  // Map Error Report Send
    ];

    /// <summary>Raised when the host reports on a map - S12F19.</summary>
    /// <remarks>
    /// Including when it reports that the map arrived correctly, which
    /// <see cref="WaferMapError.ReceivedCorrectly"/> is for. A handler that treats every S12F19 as
    /// a failure would report a successful transfer as a fault.
    /// </remarks>
    public event EventHandler<WaferMapErrorEventArgs>? ErrorReported;

    /// <summary>Answers an incoming S12F19.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the report has been raised.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Nothing is sent back. E5 gives S12F19 no reply function, so a sender that set the reply bit
    /// has asked for a message that does not exist; that is logged and the sender is left to its
    /// own T3.
    /// </remarks>
    public ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        _ = cancellationToken;

        if (transaction.ReplyExpected)
        {
            _logger.LogWarning("S12F19 arrived with the reply bit set, and E5 defines no reply to it.");
        }

        RaiseErrorReport(transaction.Message, _logger, ErrorReported, this);

        return ValueTask.CompletedTask;
    }

    // ---------------------------------------------------------------- setup data

    /// <summary>Sends map setup data - S12F1, answered by S12F2.</summary>
    /// <param name="setup">The setup. <see cref="WaferMapSetup.BinCodeEquivalents"/> and <see cref="WaferMapSetup.MapLocationCount"/> are not carried by this message.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setup"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<WaferMapSetupAcknowledgement> SendSetupAsync(
        WaferMapSetup setup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(setup);

        using var reply = await RequestAsync(
            new SecsMessage(12, 1, replyExpected: true)
            {
                Name = "MapSetupDataSend",
                Item = WaferMapCodec.SetupSendBody(setup),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadSetupAcknowledgement(reply);
    }

    /// <summary>Asks the host for map setup data - S12F3, answered by S12F4.</summary>
    /// <param name="request">What is wanted, and the conventions the answer should use.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The setup, or <see langword="null"/> when the host has none for that material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not map setup data.</exception>
    public async ValueTask<WaferMapSetup?> RequestSetupAsync(
        WaferMapSetupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reply = await RequestAsync(
            new SecsMessage(12, 3, replyExpected: true)
            {
                Name = "MapSetupDataRequest",
                Item = WaferMapCodec.SetupRequestBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!WaferMapCodec.TryReadSetup(reply.Item, out var setup))
        {
            throw new SecsException("S12F4 did not carry map setup data.");
        }

        return setup;
    }

    // ------------------------------------------------------------- the handshake

    /// <summary>Asks whether a map may be sent - S12F5, answered by S12F6.</summary>
    /// <param name="inquiry">What is about to be sent, and how large it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The host's answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="inquiry"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no grant code.</exception>
    /// <remarks>
    /// Worth doing before a large map rather than after: the inquire carries the location count, so
    /// a host that is not ready can say <see cref="WaferMapTransmitGrant.Busy"/> for the price of
    /// two small messages instead of a quarter of a million bins.
    /// </remarks>
    public async ValueTask<WaferMapTransmitGrant> InquireTransmitAsync(
        WaferMapTransmitInquiry inquiry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inquiry);

        using var reply = await RequestAsync(
            new SecsMessage(12, 5, replyExpected: true)
            {
                Name = "MapTransmitInquire",
                Item = WaferMapCodec.TransmitInquiryBody(inquiry),
            },
            cancellationToken).ConfigureAwait(false);

        var code = WaferMapCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S12F6 carried no GRNT1.");

        if (code <= (byte)WaferMapTransmitGrant.InvalidMaterial)
        {
            return (WaferMapTransmitGrant)code;
        }

        _logger.LogInformation("S12F6 refused the transmit with GRNT1 {Code}, which E5 reserves and does not name.", code);

        return WaferMapTransmitGrant.Other;
    }

    // -------------------------------------------------------------- maps out

    /// <summary>Sends a map as rows - S12F7, answered by S12F8.</summary>
    /// <param name="data">The map, as map data type 1.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A row, start column or increment does not fit the <c>I2</c> that E5 carries it in.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<WaferMapDataAcknowledgement> SendRowDataAsync(
        WaferMapRowData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return SendMapDataAsync(7, "MapDataType1Send", WaferMapCodec.RowDataBody(data), cancellationToken);
    }

    /// <summary>Sends a map as one run of bins - S12F9, answered by S12F10.</summary>
    /// <param name="data">The map, as map data type 2.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The starting coordinate does not fit the <c>I2</c> that E5 carries it in.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<WaferMapDataAcknowledgement> SendArrayDataAsync(
        WaferMapArrayData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return SendMapDataAsync(9, "MapDataType2Send", WaferMapCodec.ArrayDataBody(data), cancellationToken);
    }

    /// <summary>Sends a map as coordinates - S12F11, answered by S12F12.</summary>
    /// <param name="data">The map, as map data type 3.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate does not fit the <c>I2</c> that E5 carries it in.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<WaferMapDataAcknowledgement> SendPointDataAsync(
        WaferMapPointData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        return SendMapDataAsync(11, "MapDataType3Send", WaferMapCodec.PointDataBody(data), cancellationToken);
    }

    // --------------------------------------------------------------- maps in

    /// <summary>Asks the host for a map as rows - S12F13, answered by S12F14.</summary>
    /// <param name="request">What is wanted.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The map, or <see langword="null"/> when the host has none for that material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not map data.</exception>
    public async ValueTask<WaferMapRowData?> RequestRowDataAsync(
        WaferMapDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reply = await RequestAsync(
            new SecsMessage(12, 13, replyExpected: true)
            {
                Name = "MapDataType1Request",
                Item = WaferMapCodec.DataRequestBody(request, withBinSelection: false),
            },
            cancellationToken).ConfigureAwait(false);

        if (!WaferMapCodec.TryReadRowData(reply.Item, out var data))
        {
            throw new SecsException("S12F14 did not carry map data type 1.");
        }

        return data;
    }

    /// <summary>Asks the host for a map as one run of bins - S12F15, answered by S12F16.</summary>
    /// <param name="request">What is wanted.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The map, or <see langword="null"/> when the host has none for that material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not map data.</exception>
    public async ValueTask<WaferMapArrayData?> RequestArrayDataAsync(
        WaferMapDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reply = await RequestAsync(
            new SecsMessage(12, 15, replyExpected: true)
            {
                Name = "MapDataType2Request",
                Item = WaferMapCodec.DataRequestBody(request, withBinSelection: false),
            },
            cancellationToken).ConfigureAwait(false);

        if (!WaferMapCodec.TryReadArrayData(reply.Item, out var data))
        {
            throw new SecsException("S12F16 did not carry map data type 2.");
        }

        return data;
    }

    /// <summary>Asks the host for a map as coordinates - S12F17, answered by S12F18.</summary>
    /// <param name="request">What is wanted, including whether null locations are wanted with it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The map, or <see langword="null"/> when the host has none for that material.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not map data.</exception>
    /// <remarks>
    /// The one request that carries <see cref="WaferMapDataRequest.BinSelection"/>, because
    /// coordinate data is the one format that can leave a location out.
    /// </remarks>
    public async ValueTask<WaferMapPointData?> RequestPointDataAsync(
        WaferMapDataRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var reply = await RequestAsync(
            new SecsMessage(12, 17, replyExpected: true)
            {
                Name = "MapDataType3Request",
                Item = WaferMapCodec.DataRequestBody(request, withBinSelection: true),
            },
            cancellationToken).ConfigureAwait(false);

        if (!WaferMapCodec.TryReadPointData(reply.Item, out var data))
        {
            throw new SecsException("S12F18 did not carry map data type 3.");
        }

        return data;
    }

    // ------------------------------------------------------------ error reports

    /// <summary>Reports on a map - S12F19. Expects no reply.</summary>
    /// <param name="error">What to report. <see cref="WaferMapError.ReceivedCorrectly"/> confirms a map rather than faulting it.</param>
    /// <param name="location">Where in the data the trouble is - <c>DATLC</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the report is on the wire.</returns>
    public async ValueTask ReportErrorAsync(
        WaferMapError error,
        byte location = 0,
        CancellationToken cancellationToken = default)
    {
        using var message = new SecsMessage(12, 19, replyExpected: false)
        {
            Name = "MapErrorReportSend",
            Item = WaferMapCodec.ErrorReportBody(error, location),
        };

        await _connection.PostAsync(message, cancellationToken).ConfigureAwait(false);
    }

    // --------------------------------------------------------------- the pieces

    /// <summary>Raises an S12F19 that arrived, at whichever end received it.</summary>
    /// <remarks>
    /// Shared by the two ends because the message is shared by the two ends - it is the one
    /// function in stream 12 that either may send - and a report read one way at one end and
    /// another way at the other would be a bug nobody could see from either side.
    /// </remarks>
    internal static void RaiseErrorReport<TSender>(
        SecsMessage message,
        ILogger logger,
        EventHandler<WaferMapErrorEventArgs>? handler,
        TSender sender)
    {
        if (!WaferMapCodec.TryReadErrorReport(message.Item, out var error, out var location))
        {
            logger.LogWarning("S12F19 arrived with a body that is not a map error report.");
            return;
        }

        logger.LogDebug("S12F19 reports {Error} at data location {Location}.", error, location);

        handler?.Invoke(sender, new WaferMapErrorEventArgs { Error = error, Location = location });
    }

    private async ValueTask<WaferMapDataAcknowledgement> SendMapDataAsync(
        byte function,
        string name,
        SecsItem body,
        CancellationToken cancellationToken)
    {
        using var reply = await RequestAsync(
            new SecsMessage(12, function, replyExpected: true) { Name = name, Item = body },
            cancellationToken).ConfigureAwait(false);

        var code = WaferMapCodec.ReadCode(reply.Item)
            ?? throw new SecsException($"S12F{function + 1} carried no MDACK.");

        if (code > (byte)WaferMapDataAcknowledgement.Rejected)
        {
            _logger.LogWarning("S12F{Function} was answered with MDACK {Code}, which E5 does not define; reading it as rejected.", function + 1, code);
        }

        return code == 0 ? WaferMapDataAcknowledgement.Accepted : WaferMapDataAcknowledgement.Rejected;
    }

    private WaferMapSetupAcknowledgement ReadSetupAcknowledgement(SecsMessage reply)
    {
        var code = WaferMapCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S12F2 carried no SDACK.");

        if (code > (byte)WaferMapSetupAcknowledgement.Rejected)
        {
            // E5 makes every code from 1 to 63 a rejection and leaves the reason to the vendor, so
            // this is the only place the exact code can be of use to anybody.
            _logger.LogInformation("S12F2 rejected the setup with SDACK {Code}; E5 leaves that reason to the equipment or host.", code);
        }

        return code == 0 ? WaferMapSetupAcknowledgement.Accepted : WaferMapSetupAcknowledgement.Rejected;
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
