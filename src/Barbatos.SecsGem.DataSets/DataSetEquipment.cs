// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.DataSets;

/// <summary>
/// The equipment side of data set transfer: the end that answers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stream 13 runs entirely one way.</strong> All seven
/// primaries are the host's, all seven replies are the equipment's, and there is no message the
/// equipment may start - not even an error report. Stream 12 is the mirror of that, the equipment
/// sending every primary; stream 3 has both ends sending; stream 4 has every row travelling either
/// way. Six streams, four answers to the question of who begins, and this is one of the two that
/// leave the equipment with nothing to say for itself - stream 8, in
/// <c>Barbatos.SecsGem.ControlPrograms</c>, is the other.
/// </para>
/// <para>
/// So this class takes no <see cref="HsmsConnection"/>. It has no primary to send, no timer, no
/// state and nothing to dispose: it decodes seven primaries, hands each to an
/// <see cref="IDataSetStore"/> and sends back what the store decided. There are no events either,
/// because the store's own methods are the arrival point - see <see cref="IDataSetStore"/>.
/// </para>
/// <para>
/// <strong>A body this end cannot read is refused, not dropped</strong> - but what it can be
/// refused with varies by row, and the differences are E5's. S13F4 and S13F14 carry an
/// <c>ACKC13</c>, so an unreadable send or close is answered in the transaction the host is
/// already waiting on. S13F2 and S13F6 carry a <c>GRANT6</c>, whose only final refusal is
/// <see cref="DataSetGrant.NoSpace"/> - "not interested" rather than "that made no sense", because
/// the table has no word for the second and <see cref="DataSetGrant.Busy"/> would invite a retry
/// of a message that will break again. And S13F8 and S13F16 carry no code at all, so an unreadable
/// request is answered with a zero-length body: the reply cannot name a data set this end failed
/// to read the identifier of, and inventing data set zero would answer a question nobody asked.
/// </para>
/// </remarks>
public sealed class DataSetEquipment
{
    private readonly IDataSetStore _store;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a store.</summary>
    /// <param name="store">Where data sets are kept and where arriving ones go.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// No connection, because this end never sends a primary. Every reply goes out through the
    /// transaction it is answering.
    /// </remarks>
    public DataSetEquipment(IDataSetStore store, ILogger<DataSetEquipment>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<DataSetEquipment>.Instance;
    }

    /// <summary>Every primary this end answers: all seven of them.</summary>
    /// <remarks>
    /// The whole of stream 13 as the host sends it, less the blocked send of S13F9. Every one of
    /// the seven has a reply, which is unusual in this stack - stream 4 has seven primaries with no
    /// reply at all.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (13, 1),   // Open Data Set Send
        (13, 3),   // Data Set Send
        (13, 5),   // Open Data Set Request
        (13, 7),   // Data Set Get
        (13, 11),  // Data Set Directory Request
        (13, 13),  // Data Set Close
        (13, 15),  // Data Set Status Request
    ];

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The store is asked before the reply bit is looked at, so a data set sent without the reply
    /// bit is still stored. A message that asked for no acknowledgement is not a message that asked
    /// to be ignored.
    /// </remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 13
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the data set equipment handler with nothing to answer it.",
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

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
        => transaction.F switch
        {
            1 => await OnOpenSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            3 => await OnSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            5 => await OnOpenRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            7 => await OnGetAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            11 => await OnDirectoryAsync(cancellationToken).ConfigureAwait(false),
            13 => await OnCloseAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            15 => await OnStatusAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    // -------------------------------------------------------------------- opening

    private async ValueTask<SecsMessage> OnOpenSendAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!DataSetCodec.TryReadOpenSend(message.Item, out var dataSetId, out var length))
        {
            return RefuseOpen(2, "OpenDataSetSendAcknowledge", withLength: false, "S13F1");
        }

        var opening = await _store.OpenAsync(
            new DataSetOpenRequest
            {
                DataSetId = dataSetId,
                Mode = DataSetOpenMode.Send,
                Length = length,
            },
            cancellationToken).ConfigureAwait(false);

        // S13F2 is the grant on its own: the host declared the length, so sending it back would be
        // this end agreeing with a number it was given rather than reporting one it knows.
        return Reply(2, "OpenDataSetSendAcknowledge", DataSetCodec.CodeBody((byte)opening.Grant, "GRANT6"));
    }

    private async ValueTask<SecsMessage> OnOpenRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!DataSetCodec.TryReadDataSetId(message.Item, out var dataSetId))
        {
            return RefuseOpen(6, "OpenDataSetRequestAcknowledge", withLength: true, "S13F5");
        }

        var opening = await _store.OpenAsync(
            new DataSetOpenRequest
            {
                DataSetId = dataSetId,
                Mode = DataSetOpenMode.Request,
            },
            cancellationToken).ConfigureAwait(false);

        return Reply(
            6,
            "OpenDataSetRequestAcknowledge",
            DataSetCodec.OpeningBody((byte)opening.Grant, opening.Length));
    }

    private async ValueTask<SecsMessage> OnCloseAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!DataSetCodec.TryReadDataSetId(message.Item, out var dataSetId))
        {
            return Refuse(14, "DataSetCloseAcknowledge", DataSetAcknowledgement.InvalidDataSet, "S13F13");
        }

        var acknowledgement = await _store.CloseAsync(dataSetId, cancellationToken).ConfigureAwait(false);

        return Reply(
            14,
            "DataSetCloseAcknowledge",
            DataSetCodec.CodeBody((byte)acknowledgement, "ACKC13"));
    }

    // ------------------------------------------------------------------- the data

    private async ValueTask<SecsMessage> OnSendAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!DataSetCodec.TryReadContent(message.Item, out var dataSetId, out var dataId, out var data)
            || data is null)
        {
            return Refuse(4, "DataSetSendAcknowledge", DataSetAcknowledgement.InvalidData, "S13F3");
        }

        var acknowledgement = await _store.SaveAsync(
            new DataSetContent
            {
                DataSetId = dataSetId,
                DataId = dataId,
                Data = data,
            },
            cancellationToken).ConfigureAwait(false);

        return Reply(
            4,
            "DataSetSendAcknowledge",
            DataSetCodec.CodeBody((byte)acknowledgement, "ACKC13"));
    }

    private async ValueTask<SecsMessage> OnGetAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!DataSetCodec.TryReadRequest(message.Item, out var dataSetId, out var dataId))
        {
            return Nothing(8, "DataSetData", "S13F7");
        }

        var data = await _store.LoadAsync(
            new DataSetRequest { DataSetId = dataSetId, DataId = dataId },
            cancellationToken).ConfigureAwait(false);

        // The correlation number is echoed from the request and not from the store, because it is
        // the host's and the store was only shown it.
        return Reply(
            8,
            "DataSetData",
            DataSetCodec.ContentBody(dataSetId, dataId, data?.Decode()));
    }

    private async ValueTask<SecsMessage> OnDirectoryAsync(CancellationToken cancellationToken)
    {
        var dataSetIds = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        return Reply(12, "DataSetDirectoryData", DataSetCodec.DirectoryBody(dataSetIds));
    }

    private async ValueTask<SecsMessage> OnStatusAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!DataSetCodec.TryReadDataSetId(message.Item, out var dataSetId))
        {
            return Nothing(16, "DataSetStatusData", "S13F15");
        }

        var status = await _store.StatusAsync(dataSetId, cancellationToken).ConfigureAwait(false);

        return Reply(
            16,
            "DataSetStatusData",
            DataSetCodec.StatusBody(status.DataSetId, status.Length, (byte)status.Acknowledgement));
    }

    // ---------------------------------------------------------------- the pieces

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(13, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Refuse(byte function, string name, DataSetAcknowledgement code, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read; answering ACKC13 {Code}.",
            primary,
            code);

        return Reply(function, name, DataSetCodec.CodeBody((byte)code, "ACKC13"));
    }

    private SecsMessage RefuseOpen(byte function, string name, bool withLength, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read; answering GRANT6 {Code}, which is the only final refusal that table has.",
            primary,
            DataSetGrant.NoSpace);

        return Reply(
            function,
            name,
            withLength
                ? DataSetCodec.OpeningBody((byte)DataSetGrant.NoSpace, 0)
                : DataSetCodec.CodeBody((byte)DataSetGrant.NoSpace, "GRANT6"));
    }

    private SecsMessage Nothing(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering a zero-length list.",
            primary);

        return Reply(function, name, SecsItem.L());
    }
}
