// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Material;

/// <summary>
/// The host side of material status: it asks, and it is told.
/// </summary>
/// <remarks>
/// <para>
/// Five questions out - what have you got, how long has it left, where is this one, move that one,
/// may I send a large body - and four things in that the equipment says without being asked.
/// </para>
/// <para>
/// <strong>Why the inbound four are events and the equipment's five are an interface.</strong>
/// The equipment has to <em>produce</em> what it is asked for, so
/// <see cref="IMaterialSource"/> is an interface with a return value per question. A host being
/// told that material was found has nothing to produce: it has a byte to send back, and
/// <see cref="MaterialAcknowledgement.Accepted"/> is already the right one. So these are events
/// with a settable acknowledgement, which is the same shape the GEM package uses for an event
/// report arriving - and a host that subscribes to none of them still answers correctly.
/// </para>
/// </remarks>
public sealed class MaterialHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host side over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public MaterialHost(HsmsConnection connection, ILogger<MaterialHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<MaterialHost>.Instance;
    }

    /// <summary>Every primary this end answers: the four the equipment sends unprompted.</summary>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (3, 5),    // Material Found Send
        (3, 7),    // Material Lost Send
        (3, 13),   // Material Status Change Send
        (3, 35),   // Material Movement Event Report Send
    ];

    /// <summary>Raised when material turns up - S3F5.</summary>
    public event EventHandler<MaterialLocationEventArgs>? MaterialFound;

    /// <summary>Raised when material goes missing - S3F7.</summary>
    /// <remarks>The location is where it was last seen. A machine that knew where it is would not have lost it.</remarks>
    public event EventHandler<MaterialLocationEventArgs>? MaterialLost;

    /// <summary>Raised when material starts doing something else - S3F13.</summary>
    public event EventHandler<MaterialStatusChangeEventArgs>? MaterialStatusChanged;

    /// <summary>Raised when material has moved - S3F35.</summary>
    /// <remarks>The message to wait for after an accepted S3F11: the transfer was agreed to then, and this is it arriving.</remarks>
    public event EventHandler<MaterialMovementEventArgs>? MaterialMoved;

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 3 ? BuildReply(transaction) : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the material host handler with nothing to answer it.",
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

    /// <summary>Asks what material the machine holds - S3F1, answered by S3F2.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The material and where it is. An empty list means the machine is empty.</returns>
    /// <exception cref="SecsException">The reply was not material status data.</exception>
    public async ValueTask<IReadOnlyList<MaterialStatus>> RequestStatusAsync(
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(3, 1, replyExpected: true) { Name = "MaterialStatusRequest" },
            cancellationToken).ConfigureAwait(false);

        if (!MaterialCodec.TryReadStatus(reply.Item, out var material))
        {
            throw new SecsException("S3F2 did not carry material status data.");
        }

        return material;
    }

    /// <summary>Asks how long each piece of material has left - S3F3, answered by S3F4.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The material and its remaining time.</returns>
    /// <exception cref="SecsException">The reply was not time to completion data.</exception>
    /// <remarks>
    /// <c>TTC</c> comes back as the number the machine sent, in whatever unit the machine counts
    /// in; see <see cref="MaterialCompletion.TimeToCompletion"/> for why this library does not
    /// turn it into a duration.
    /// </remarks>
    public async ValueTask<IReadOnlyList<MaterialCompletion>> RequestTimeToCompletionAsync(
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(3, 3, replyExpected: true) { Name = "TimeToCompletionRequest" },
            cancellationToken).ConfigureAwait(false);

        if (!MaterialCodec.TryReadCompletion(reply.Item, out var material))
        {
            throw new SecsException("S3F4 did not carry time to completion data.");
        }

        return material;
    }

    /// <summary>Asks where one piece of material is - S3F9, answered by S3F10.</summary>
    /// <param name="materialId">What to look for.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Where it is, or <see langword="null"/> when the machine does not have it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not material location data.</exception>
    /// <remarks>
    /// <see langword="null"/> is not a failure: S3F10 has no acknowledgement code, so a machine
    /// that does not have the material answers the zero-length list E5 uses for no data.
    /// </remarks>
    public async ValueTask<MaterialLocation?> FindMaterialAsync(
        string materialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialId);

        using var reply = await RequestAsync(
            new SecsMessage(3, 9, replyExpected: true)
            {
                Name = "MaterialLocationRequest",
                Item = MaterialCodec.MaterialIdBody(materialId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!MaterialCodec.TryReadLocation(reply.Item, out var material))
        {
            throw new SecsException("S3F10 did not carry material location data.");
        }

        return material;
    }

    /// <summary>Asks the machine to move material - S3F11, answered by S3F12.</summary>
    /// <param name="transfer">What to move and where to.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine will do it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transfer"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <see cref="MaterialAcknowledgement.Accepted"/> means the request was taken, not that the
    /// material has arrived. <see cref="MaterialMoved"/> is the arrival.
    /// </remarks>
    public async ValueTask<MaterialAcknowledgement> RequestTransferAsync(
        MaterialTransfer transfer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transfer);

        using var reply = await RequestAsync(
            new SecsMessage(3, 11, replyExpected: true)
            {
                Name = "MaterialTransferRequest",
                Item = MaterialCodec.TransferBody(transfer),
            },
            cancellationToken).ConfigureAwait(false);

        var code = MaterialCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S3F12 carried no ACKC3.");

        if (code > (byte)MaterialAcknowledgement.Denied)
        {
            _logger.LogWarning("S3F12 answered with ACKC3 {Code}, which E5 does not define; reading it as denied.", code);
        }

        return code == 0 ? MaterialAcknowledgement.Accepted : MaterialAcknowledgement.Denied;
    }

    /// <summary>Asks whether a large body may be sent - S3F15, answered by S3F16.</summary>
    /// <param name="dataId">The data identifier it will carry.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="SecsException">The reply carried no grant code.</exception>
    public async ValueTask<MaterialGrant> InquireMultiBlockAsync(
        uint dataId,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(3, 15, replyExpected: true)
            {
                Name = "MaterialsMultiBlockInquire",
                Item = MaterialCodec.DataIdBody(dataId),
            },
            cancellationToken).ConfigureAwait(false);

        var code = MaterialCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S3F16 carried no GRANT.");

        if (code <= (byte)MaterialGrant.DuplicateOrInvalid)
        {
            return (MaterialGrant)code;
        }

        _logger.LogInformation("S3F16 refused the block with GRANT {Code}, which E5 reserves and does not name.", code);

        return MaterialGrant.Other;
    }

    // --------------------------------------------------------------- the pieces

    private SecsMessage? BuildReply(SecsTransaction transaction)
    {
        switch (transaction.F)
        {
            case 5:
            case 7:
            {
                if (!MaterialCodec.TryReadLocation(transaction.Message.Item, out var material) || material is null)
                {
                    return Refuse((byte)(transaction.F + 1), $"S3F{transaction.F}");
                }

                var args = new MaterialLocationEventArgs { Material = material };

                (transaction.F == 5 ? MaterialFound : MaterialLost)?.Invoke(this, args);

                return Reply(
                    (byte)(transaction.F + 1),
                    transaction.F == 5 ? "MaterialFoundAcknowledge" : "MaterialLostAcknowledge",
                    MaterialCodec.CodeBody((byte)args.Acknowledgement, "ACKC3"));
            }

            case 13:
            {
                if (!MaterialCodec.TryReadStatusChange(transaction.Message.Item, out var change) || change is null)
                {
                    return Refuse(14, "S3F13");
                }

                var args = new MaterialStatusChangeEventArgs { Change = change };

                MaterialStatusChanged?.Invoke(this, args);

                return Reply(
                    14,
                    "MaterialStatusChangeAcknowledge",
                    MaterialCodec.CodeBody((byte)args.Acknowledgement, "ACKC3"));
            }

            case 35:
            {
                if (!MaterialCodec.TryReadMovement(transaction.Message.Item, out var movement) || movement is null)
                {
                    return Refuse(36, "S3F35");
                }

                var args = new MaterialMovementEventArgs { Movement = movement };

                MaterialMoved?.Invoke(this, args);

                return Reply(
                    36,
                    "MaterialMovementEventAcknowledge",
                    MaterialCodec.CodeBody((byte)args.Acknowledgement, "ACKC3"));
            }

            default:
                return null;
        }
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(3, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Refuse(byte function, string primary)
    {
        _logger.LogWarning("{Primary} arrived with a body this end could not read; answering ACKC3 1.", primary);

        return Reply(function, $"MaterialAcknowledge{function}", MaterialCodec.CodeBody((byte)MaterialAcknowledgement.Denied, "ACKC3"));
    }
}
