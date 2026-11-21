// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ControlPrograms;

/// <summary>
/// The equipment side of control program transfer: the end that answers.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stream 8 runs one way.</strong> All eight primaries are the host's and all eight replies
/// the equipment's, so nothing in this stream ever arrives at a host unasked - no report, and no
/// message saying a new control program exists. Stream 13 works the same way, which makes these the
/// two streams in this stack that do; stream 12 is the mirror of them with the equipment sending
/// everything, and streams 3 and 18 have both ends sending.
/// </para>
/// <para>
/// So this class takes no <see cref="HsmsConnection"/>: it has no primary to send, no timer, no
/// state and nothing to dispose. It decodes six primaries, hands each to an
/// <see cref="IControlProgramStore"/> and sends back what the store decided.
/// </para>
/// <para>
/// <strong>What a body it cannot read is refused with varies by row, and the differences are
/// E5's.</strong> S8F2 carries a <c>GRANT</c>, so an unreadable inquire is refused
/// <see cref="ControlProgramGrant.DuplicateOrInvalid"/> - the table's own word for "or not one this
/// end recognises", and the only final refusal it has that is not about space. S8F4 and S8F14 carry
/// an <c>ACKC8</c>, whose single refusal carries no reason because the item has no table to put one
/// in. S8F6, S8F12 and S8F16 carry no code at all, so an unreadable request is answered with an
/// empty body: the reply cannot name a program whose identifier this end failed to read.
/// </para>
/// </remarks>
public sealed class ControlProgramEquipment
{
    private readonly IControlProgramStore _store;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a store.</summary>
    /// <param name="store">Where control programs are kept and where an arriving one goes.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is <see langword="null"/>.</exception>
    /// <remarks>No connection, because this end never sends a primary. Every reply goes out through the transaction it is answering.</remarks>
    public ControlProgramEquipment(IControlProgramStore store, ILogger<ControlProgramEquipment>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<ControlProgramEquipment>.Instance;
    }

    /// <summary>Every primary this end answers: the six this package implements.</summary>
    /// <remarks>
    /// Stream 8 has eight pairs and this is six of them. S8F7/F8 and S8F9/F10 - the blocked send
    /// and the blocked request - stay out, because their <c>BLKNUM</c> and <c>BLKEND</c> have no
    /// dictionary entries and neither does any other block-transfer item in the reference.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (8, 1),    // Boot/Control Program Load Inquire
        (8, 3),    // Boot/Control Program Send
        (8, 5),    // Control Program Request
        (8, 11),   // Control Program Directory Request
        (8, 13),   // Control Program Transfer Abort
        (8, 15),   // Control Program Status Request
    ];

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The store is asked before the reply bit is looked at, so a program sent without the reply
    /// bit is still stored. A message that asked for no acknowledgement is not a message that asked
    /// to be ignored.
    /// </remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 8
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the control program equipment handler with nothing to answer it.",
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
            1 => await OnInquireAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            3 => await OnSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            5 => await OnRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            11 => await OnDirectoryAsync(cancellationToken).ConfigureAwait(false),
            13 => await OnAbortAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            15 => await OnRevisionAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    // ------------------------------------------------------------- the boot program

    private async ValueTask<SecsMessage> OnInquireAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ControlProgramCodec.TryReadInquire(message.Item, out var length))
        {
            _logger.LogWarning(
                "S8F1 arrived with a body this end could not read; answering GRANT {Code}.",
                ControlProgramGrant.DuplicateOrInvalid);

            return Reply(
                2,
                "BootControlProgramLoadGrant",
                ControlProgramCodec.CodeBody((byte)ControlProgramGrant.DuplicateOrInvalid, "GRANT"));
        }

        var grant = await _store.InquireAsync(length, cancellationToken).ConfigureAwait(false);

        return Reply(2, "BootControlProgramLoadGrant", ControlProgramCodec.CodeBody((byte)grant, "GRANT"));
    }

    private async ValueTask<SecsMessage> OnSendAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        // S8F3's whole body is a CPBODY, so there is no identifier to fail to read - but an item
        // this end cannot take bytes out of still has to be refused rather than stored as nothing.
        // One check and not two: an earlier version tested for a list here as well, and a control
        // proved the codec's own guard was then unreachable, which is a rule in two places and
        // live in one.
        if (!ControlProgramCodec.TryReadProgramBody(message.Item, out var body))
        {
            _logger.LogWarning("S8F3 carried a CPBODY this end cannot read bytes out of; answering ACKC8 Denied.");

            return Reply(
                4,
                "BootControlProgramSendAcknowledge",
                ControlProgramCodec.CodeBody((byte)ControlProgramAcknowledgement.Denied, "ACKC8"));
        }

        var acknowledgement = await _store.SaveAsync(body, cancellationToken).ConfigureAwait(false);

        return Reply(
            4,
            "BootControlProgramSendAcknowledge",
            ControlProgramCodec.CodeBody((byte)acknowledgement, "ACKC8"));
    }

    // ------------------------------------------------------- named control programs

    private async ValueTask<SecsMessage> OnRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ControlProgramCodec.TryReadId(message.Item, out var controlProgramId))
        {
            return Nothing(6, "ControlProgramData", "S8F5");
        }

        var body = await _store.LoadAsync(controlProgramId, cancellationToken).ConfigureAwait(false);

        // An empty CPBODY is "no such program": S8F6 has no code to say it with, so the identifier
        // comes back and the program does not.
        return Reply(
            6,
            "ControlProgramData",
            ControlProgramCodec.ProgramBody(controlProgramId, body ?? default));
    }

    private async ValueTask<SecsMessage> OnDirectoryAsync(CancellationToken cancellationToken)
    {
        var controlProgramIds = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        return Reply(12, "ControlProgramDirectoryData", ControlProgramCodec.DirectoryBody(controlProgramIds));
    }

    private async ValueTask<SecsMessage> OnAbortAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ControlProgramCodec.TryReadId(message.Item, out var controlProgramId))
        {
            _logger.LogWarning("S8F13 arrived with a body this end could not read; answering ACKC8 Denied.");

            return Reply(
                14,
                "ControlProgramTransferAbortAcknowledge",
                ControlProgramCodec.CodeBody((byte)ControlProgramAcknowledgement.Denied, "ACKC8"));
        }

        var acknowledgement = await _store.AbortAsync(controlProgramId, cancellationToken).ConfigureAwait(false);

        return Reply(
            14,
            "ControlProgramTransferAbortAcknowledge",
            ControlProgramCodec.CodeBody((byte)acknowledgement, "ACKC8"));
    }

    private async ValueTask<SecsMessage> OnRevisionAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ControlProgramCodec.TryReadId(message.Item, out var controlProgramId))
        {
            return Nothing(16, "ControlProgramStatusData", "S8F15");
        }

        var revision = await _store.RevisionAsync(controlProgramId, cancellationToken).ConfigureAwait(false);

        return Reply(
            16,
            "ControlProgramStatusData",
            ControlProgramCodec.RevisionBody(controlProgramId, revision ?? string.Empty));
    }

    // ---------------------------------------------------------------- the pieces

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(8, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Nothing(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering a zero-length list.",
            primary);

        return Reply(function, name, SecsItem.L());
    }
}
