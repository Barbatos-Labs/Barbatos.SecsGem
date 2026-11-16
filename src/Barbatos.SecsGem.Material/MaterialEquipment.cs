// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Material;

/// <summary>
/// The equipment side of material status: it answers what it holds, and says when that changes.
/// </summary>
/// <remarks>
/// <para>
/// Stream 3 is the first stream in this library where <em>both</em> ends send primaries. The host
/// asks five questions - what have you got, how long has it left, where is this one, move that
/// one, may I send a large body - and the equipment answers them out of an
/// <see cref="IMaterialSource"/>. The equipment then tells the host four things it did not ask
/// for: material found, material lost, a status change, and a movement. Neither end is the passive
/// one.
/// </para>
/// <para>
/// There is no state model and no timer, as with every E5 stream outside GEM. What there is
/// instead is an inventory, and this class does not own it: <see cref="IMaterialSource"/> does.
/// </para>
/// <para>
/// <strong>What a denial means here.</strong> <c>ACKC3</c> has two codes and no error text
/// anywhere in this half of stream 3, so a denial cannot say why. That is E5, not an omission: a
/// machine with something to explain raises an alarm in stream 5. A reply with no acknowledgement
/// code at all is a <see cref="SecsException"/> and never
/// <see cref="MaterialAcknowledgement.Accepted"/>, because a zero read out of an empty body is
/// exactly the wrong answer.
/// </para>
/// </remarks>
public sealed class MaterialEquipment
{
    private readonly IMaterialSource _source;
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a connection and an inventory.</summary>
    /// <param name="source">What this machine knows about its material.</param>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="connection"/> is <see langword="null"/>.</exception>
    public MaterialEquipment(
        IMaterialSource source,
        HsmsConnection connection,
        ILogger<MaterialEquipment>? logger = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<MaterialEquipment>.Instance;
    }

    /// <summary>Every primary this end answers: the five the host sends.</summary>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (3, 1),    // Material Status Request
        (3, 3),    // Time to Completion Request
        (3, 9),    // Material Location Request
        (3, 11),   // Material Transfer Request
        (3, 15),   // Materials Multi-block Inquire
    ];

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 3
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the material equipment handler with nothing to answer it.",
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

    /// <summary>Tells the host where material turned up - S3F5, answered by S3F6.</summary>
    /// <param name="material">What was found, and where.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<MaterialAcknowledgement> ReportFoundAsync(
        MaterialLocation material,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);

        return SendAsync(5, "MaterialFoundSend", MaterialCodec.LocationBody(material), cancellationToken);
    }

    /// <summary>Tells the host material has gone missing - S3F7, answered by S3F8.</summary>
    /// <param name="material">What was lost, and where it was.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="material"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <c>LOC</c> is where it was last seen, not where it is - a machine that knew that would not
    /// have lost it.
    /// </remarks>
    public ValueTask<MaterialAcknowledgement> ReportLostAsync(
        MaterialLocation material,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);

        return SendAsync(7, "MaterialLostSend", MaterialCodec.LocationBody(material), cancellationToken);
    }

    /// <summary>Tells the host material is now doing something else - S3F13, answered by S3F14.</summary>
    /// <param name="change">What changed.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<MaterialAcknowledgement> ReportStatusChangeAsync(
        MaterialStatusChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        return SendAsync(13, "MaterialStatusChangeSend", MaterialCodec.StatusChangeBody(change), cancellationToken);
    }

    /// <summary>Tells the host material has moved - S3F35, answered by S3F36.</summary>
    /// <param name="movement">What moved, from where, to where.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the host took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="movement"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// The message a host waits for after an accepted S3F11: the transfer was agreed to then, and
    /// this is the material actually arriving.
    /// </remarks>
    public ValueTask<MaterialAcknowledgement> ReportMovementAsync(
        MaterialMovement movement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(movement);

        return SendAsync(35, "MaterialMovementEventReportSend", MaterialCodec.MovementBody(movement), cancellationToken);
    }

    // --------------------------------------------------------------- the pieces

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
    {
        switch (transaction.F)
        {
            case 1:
            {
                var material = await _source.GetStatusAsync(cancellationToken).ConfigureAwait(false);

                return Reply(2, "MaterialStatusData", MaterialCodec.StatusBody(material ?? []));
            }

            case 3:
            {
                var material = await _source.GetTimeToCompletionAsync(cancellationToken).ConfigureAwait(false);

                return Reply(4, "TimeToCompletionData", MaterialCodec.CompletionBody(material ?? []));
            }

            case 9:
            {
                var materialId = MaterialCodec.TryReadMaterialId(transaction.Message.Item);

                if (materialId is null)
                {
                    _logger.LogWarning("S3F9 arrived with a body that is not a material identifier; answering an empty list.");

                    return Reply(10, "MaterialLocationData", SecsItem.L());
                }

                var found = await _source.FindAsync(materialId, cancellationToken).ConfigureAwait(false);

                // S3F10 has no acknowledgement code in it, so "I do not have that one" is the
                // zero-length list E5 uses for no data, and so is a body this end could not read.
                return Reply(
                    10,
                    "MaterialLocationData",
                    found is null ? SecsItem.L() : MaterialCodec.LocationBody(found));
            }

            case 11:
            {
                if (!MaterialCodec.TryReadTransfer(transaction.Message.Item, out var transfer) || transfer is null)
                {
                    return Refuse(12, "S3F11");
                }

                var acknowledgement = await _source.TransferAsync(transfer, cancellationToken).ConfigureAwait(false);

                return Reply(12, "MaterialTransferAcknowledge", MaterialCodec.CodeBody((byte)acknowledgement, "ACKC3"));
            }

            case 15:
            {
                var dataId = MaterialCodec.TryReadDataId(transaction.Message.Item);

                if (dataId is null)
                {
                    _logger.LogWarning("S3F15 arrived with a body that is not a data identifier; refusing the block.");

                    return Reply(
                        16,
                        "MaterialsMultiBlockGrant",
                        MaterialCodec.CodeBody((byte)MaterialGrant.DuplicateOrInvalid, "GRANT"));
                }

                var grant = await _source.InquireMultiBlockAsync(dataId.Value, cancellationToken).ConfigureAwait(false);

                return Reply(16, "MaterialsMultiBlockGrant", MaterialCodec.CodeBody((byte)grant, "GRANT"));
            }

            default:
                return null;
        }
    }

    private async ValueTask<MaterialAcknowledgement> SendAsync(
        byte function,
        string name,
        SecsItem body,
        CancellationToken cancellationToken)
    {
        using var primary = new SecsMessage(3, function, replyExpected: true) { Name = name, Item = body };
        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, function);
    }

    private MaterialAcknowledgement ReadAcknowledgement(SecsMessage reply, byte primary)
    {
        var code = MaterialCodec.ReadCode(reply.Item)
            ?? throw new SecsException($"S3F{primary + 1} carried no ACKC3.");

        if (code > (byte)MaterialAcknowledgement.Denied)
        {
            _logger.LogWarning(
                "S3F{Function} was answered with ACKC3 {Code}, which E5 does not define; reading it as denied.",
                primary + 1,
                code);
        }

        return code == 0 ? MaterialAcknowledgement.Accepted : MaterialAcknowledgement.Denied;
    }

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(3, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Refuse(byte function, string primary)
    {
        _logger.LogWarning("{Primary} arrived with a body this end could not read; answering ACKC3 1.", primary);

        return Reply(
            function,
            "MaterialTransferAcknowledge",
            MaterialCodec.CodeBody((byte)MaterialAcknowledgement.Denied, "ACKC3"));
    }
}
