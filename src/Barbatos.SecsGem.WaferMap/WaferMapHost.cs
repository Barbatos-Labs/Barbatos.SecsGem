// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.WaferMap;

/// <summary>
/// The host side of wafer mapping: the end that answers.
/// </summary>
/// <remarks>
/// <para>
/// Stream 12 is the one stream where the host is the passive end. The equipment sends maps and asks
/// for maps; this class decodes the ten primaries, hands each to an <see cref="IWaferMapStore"/>
/// and sends back what the store decided. There is nothing else to it - no state, no timer, and no
/// primary of its own except the error report, which either end may send.
/// </para>
/// <para>
/// <strong>A body this end cannot read is refused, not dropped.</strong> S12F1 is answered
/// <see cref="WaferMapSetupAcknowledgement.Rejected"/> and the three map data sends are answered
/// <see cref="WaferMapDataAcknowledgement.Rejected"/>, so the equipment learns in the transaction
/// it is already waiting on. The four requests have no such code in their replies - E5 gives
/// S12F4, S12F14, S12F16 and S12F18 nothing but data - so an unreadable request is answered with
/// the zero-length list that means "no map", and <see cref="ReportErrorAsync"/> with
/// <see cref="WaferMapError.FormatError"/> is there for an application that wants to say why. It is
/// not sent automatically: an unasked-for primary is a decision, not a detail.
/// </para>
/// <para>
/// One instance sits on one <see cref="HsmsConnection"/> and one store. Like
/// <see cref="WaferMapEquipment"/> it holds nothing that needs releasing and is not disposable.
/// </para>
/// </remarks>
public sealed class WaferMapHost
{
    private readonly IWaferMapStore _store;
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host side over a connection and a store.</summary>
    /// <param name="store">Where maps are kept and where arriving ones go.</param>
    /// <param name="connection">The link, for the one primary this end may send - S12F19.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="connection"/> is <see langword="null"/>.</exception>
    public WaferMapHost(
        IWaferMapStore store,
        HsmsConnection connection,
        ILogger<WaferMapHost>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<WaferMapHost>.Instance;
    }

    /// <summary>Every primary this end answers: all ten of them.</summary>
    /// <remarks>
    /// The whole of stream 12 as the equipment sends it. S12F19 is in the list and is the only one
    /// of the ten that is answered with nothing, because E5 defines no reply to it.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (12, 1),   // Map Setup Data Send
        (12, 3),   // Map Setup Data Request
        (12, 5),   // Map Transmit Inquire
        (12, 7),   // Map Data Type 1 Send
        (12, 9),   // Map Data Type 2 Send
        (12, 11),  // Map Data Type 3 Send
        (12, 13),  // Map Data Type 1 Request
        (12, 15),  // Map Data Type 2 Request
        (12, 17),  // Map Data Type 3 Request
        (12, 19),  // Map Error Report Send
    ];

    /// <summary>Raised when the equipment reports on a map - S12F19.</summary>
    /// <remarks>Including a report that the map arrived correctly; see <see cref="WaferMapError.ReceivedCorrectly"/>.</remarks>
    public event EventHandler<WaferMapErrorEventArgs>? ErrorReported;

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The store is asked before the reply bit is looked at, so a map sent without the reply bit is
    /// still stored. A message that asked for no acknowledgement is not a message that asked to be
    /// ignored.
    /// </remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        // S12F19 before the switch, because it is the one function here that is complete without a
        // reply - and the one either end may send.
        if (transaction.S == 12 && transaction.F == 19)
        {
            if (transaction.ReplyExpected)
            {
                _logger.LogWarning("S12F19 arrived with the reply bit set, and E5 defines no reply to it.");
            }

            WaferMapEquipment.RaiseErrorReport(transaction.Message, _logger, ErrorReported, this);
            return;
        }

        var reply = transaction.S == 12
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the wafer map host handler with nothing to answer it.",
                transaction.S,
                transaction.F);

            return;
        }

        using (reply)
        {
            if (transaction.ReplyExpected)
            {
                await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reports on a map the equipment sent - S12F19. Expects no reply.</summary>
    /// <param name="error">What to report.</param>
    /// <param name="location">Where in the data the trouble is - <c>DATLC</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the report is on the wire.</returns>
    /// <remarks>
    /// The only primary this end sends, and the only way to tell the equipment that a map was
    /// wrong in a way an acknowledgement code cannot say - a bin count that does not match the
    /// setup, for one, which arrives as a perfectly well-formed message.
    /// </remarks>
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

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
        => transaction.F switch
        {
            1 => await OnSetupSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            3 => await OnSetupRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            5 => await OnTransmitInquiryAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            7 => await OnRowDataAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            9 => await OnArrayDataAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            11 => await OnPointDataAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            13 => await OnRowDataRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            15 => await OnArrayDataRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            17 => await OnPointDataRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    // ---------------------------------------------------------------- setup data

    private async ValueTask<SecsMessage> OnSetupSendAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadSetupSend(message.Item, out var setup) || setup is null)
        {
            return Refuse(2, "MapSetupDataAcknowledge", "SDACK", (byte)WaferMapSetupAcknowledgement.Rejected, "S12F1");
        }

        var acknowledgement = await _store.SaveSetupAsync(setup, cancellationToken).ConfigureAwait(false);

        return Reply(2, "MapSetupDataAcknowledge", WaferMapCodec.CodeBody((byte)acknowledgement, "SDACK"));
    }

    private async ValueTask<SecsMessage> OnSetupRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadSetupRequest(message.Item, out var request) || request is null)
        {
            return Empty(4, "MapSetupData", "S12F3");
        }

        var setup = await _store.LoadSetupAsync(request, cancellationToken).ConfigureAwait(false);

        return Reply(4, "MapSetupData", WaferMapCodec.SetupBody(setup));
    }

    // ------------------------------------------------------------- the handshake

    private async ValueTask<SecsMessage> OnTransmitInquiryAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadTransmitInquiry(message.Item, out var inquiry) || inquiry is null)
        {
            return Refuse(6, "MapTransmitGrant", "GRNT1", (byte)WaferMapTransmitGrant.InvalidMaterial, "S12F5");
        }

        var grant = await _store.InquireTransmitAsync(inquiry, cancellationToken).ConfigureAwait(false);

        return Reply(6, "MapTransmitGrant", WaferMapCodec.CodeBody((byte)grant, "GRNT1"));
    }

    // ----------------------------------------------------------- maps arriving

    private async ValueTask<SecsMessage> OnRowDataAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadRowData(message.Item, out var data) || data is null)
        {
            return Refuse(8, "MapDataType1Acknowledge", "MDACK", (byte)WaferMapDataAcknowledgement.Rejected, "S12F7");
        }

        var acknowledgement = await _store.SaveRowDataAsync(data, cancellationToken).ConfigureAwait(false);

        return Reply(8, "MapDataType1Acknowledge", WaferMapCodec.CodeBody((byte)acknowledgement, "MDACK"));
    }

    private async ValueTask<SecsMessage> OnArrayDataAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadArrayData(message.Item, out var data) || data is null)
        {
            return Refuse(10, "MapDataType2Acknowledge", "MDACK", (byte)WaferMapDataAcknowledgement.Rejected, "S12F9");
        }

        var acknowledgement = await _store.SaveArrayDataAsync(data, cancellationToken).ConfigureAwait(false);

        return Reply(10, "MapDataType2Acknowledge", WaferMapCodec.CodeBody((byte)acknowledgement, "MDACK"));
    }

    private async ValueTask<SecsMessage> OnPointDataAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadPointData(message.Item, out var data) || data is null)
        {
            return Refuse(12, "MapDataType3Acknowledge", "MDACK", (byte)WaferMapDataAcknowledgement.Rejected, "S12F11");
        }

        var acknowledgement = await _store.SavePointDataAsync(data, cancellationToken).ConfigureAwait(false);

        return Reply(12, "MapDataType3Acknowledge", WaferMapCodec.CodeBody((byte)acknowledgement, "MDACK"));
    }

    // -------------------------------------------------------------- maps asked for

    private async ValueTask<SecsMessage> OnRowDataRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadDataRequest(message.Item, out var request) || request is null)
        {
            return Empty(14, "MapDataType1", "S12F13");
        }

        var data = await _store.LoadRowDataAsync(request, cancellationToken).ConfigureAwait(false);

        return Reply(14, "MapDataType1", WaferMapCodec.RowDataBody(data));
    }

    private async ValueTask<SecsMessage> OnArrayDataRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadDataRequest(message.Item, out var request) || request is null)
        {
            return Empty(16, "MapDataType2", "S12F15");
        }

        var data = await _store.LoadArrayDataAsync(request, cancellationToken).ConfigureAwait(false);

        return Reply(16, "MapDataType2", WaferMapCodec.ArrayDataBody(data));
    }

    private async ValueTask<SecsMessage> OnPointDataRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!WaferMapCodec.TryReadDataRequest(message.Item, out var request) || request is null)
        {
            return Empty(18, "MapDataType3", "S12F17");
        }

        var data = await _store.LoadPointDataAsync(request, cancellationToken).ConfigureAwait(false);

        return Reply(18, "MapDataType3", WaferMapCodec.PointDataBody(data));
    }

    // --------------------------------------------------------------- the pieces

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(12, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Refuse(byte function, string name, string item, byte code, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read; answering {Item} {Code}.",
            primary,
            item,
            code);

        return Reply(function, name, WaferMapCodec.CodeBody(code, item));
    }

    private SecsMessage Empty(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering a zero-length list.",
            primary);

        return Reply(function, name, SecsItem.L());
    }
}
