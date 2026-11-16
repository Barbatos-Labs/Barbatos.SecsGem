// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The equipment side of carrier management: the five requests a host sends, answered out of the
/// state machines.
/// </summary>
/// <remarks>
/// <para>
/// Thin on purpose. Every decision this class makes is one <see cref="CarrierManager"/> made for
/// it - which is why most of this package's tests never open a link: a state machine with a
/// transport bolted to it is testable, and a state machine implemented inside a message handler is
/// not.
/// </para>
/// <para>
/// E87's carrier and port attributes do not come through here. They are object attributes on
/// stream 14, and <see cref="CarrierObjectProvider"/> answers those - which is also where the
/// slot map lives. A machine doing E87 properly declares both: these five, and object services.
/// </para>
/// </remarks>
public sealed class CarrierEquipment
{
    private readonly CarrierManager _manager;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a set of state machines.</summary>
    /// <param name="manager">The carriers and ports.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> is <see langword="null"/>.</exception>
    public CarrierEquipment(CarrierManager manager, ILogger<CarrierEquipment>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _logger = logger ?? NullLogger<CarrierEquipment>.Instance;
    }

    /// <summary>Every primary this end answers.</summary>
    /// <remarks>
    /// The five whose items the catalogue defines in full. S3F19, S3F21, S3F27 and S3F31 are not
    /// here: each needs an item with no format - <c>CARRIERSTATUS</c>, <c>PORTSTATUS</c>,
    /// <c>SLOTMAP</c> - and E87 carries all three as object attributes instead.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (3, 17),   // Carrier Action Request
        (3, 23),   // Carrier Location Request
        (3, 25),   // Carrier ID Read Request
        (3, 29),   // Carrier Reservation Request
        (3, 33),   // Carrier Out Request
    ];

    /// <summary>Gets or sets where a carrier is, for the one request that asks.</summary>
    /// <remarks>
    /// S3F24 wants a <c>LOC</c>, and a location is the machine's own numbering rather than
    /// anything E87 defines - a port is not a location. A machine that tracks locations sets this
    /// to its own lookup; the default reports the port a carrier is at, which is the best a
    /// package that knows only about ports can do, and zero for a carrier that is not at one.
    /// </remarks>
    public Func<Carrier, uint> LocationOf { get; set; } = carrier => carrier.PortId ?? 0u;

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
                "S{S}F{F} reached the carrier handler with nothing to answer it.",
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

    private SecsMessage? BuildReply(SecsTransaction transaction)
    {
        switch (transaction.F)
        {
            case 17:
            {
                if (!CarrierCodec.TryReadAction(transaction.Message.Item, out var request) || request is null)
                {
                    return Reply(
                        18,
                        "CarrierActionAcknowledge",
                        CarrierCodec.ActionResultBody(CarrierActionResult.Refused(
                            CarrierAcknowledgement.InvalidData,
                            "S3F17 did not carry a carrier action.")));
                }

                var result = _manager.Perform(request);

                if (!result.IsAccepted)
                {
                    // Logged as information rather than as a warning: a refusal is E87 working.
                    // A host asking for something the state does not allow is the normal way a
                    // fab finds out what the state is.
                    _logger.LogInformation(
                        "{Action} on carrier '{Carrier}' port {Port} was refused with {Code}: {Reason}",
                        request.Action,
                        request.CarrierId,
                        request.PortId,
                        result.Acknowledgement,
                        result.ErrorText);
                }

                return Reply(18, "CarrierActionAcknowledge", CarrierCodec.ActionResultBody(result));
            }

            case 23:
            {
                var carrierId = CarrierCodec.TryReadText(transaction.Message.Item);
                var carrier = carrierId is null ? null : _manager.FindCarrier(carrierId);

                // S3F24 has no acknowledgement code in it, so a carrier this machine does not have
                // is the zero-length list - the same answer a body it could not read gets.
                return Reply(
                    24,
                    "CarrierLocationData",
                    carrier is null
                        ? SecsItem.L()
                        : CarrierCodec.LocationBody(carrier.CarrierId, LocationOf(carrier)));
            }

            case 25:
            {
                var portId = CarrierCodec.TryReadPort(transaction.Message.Item);

                if (portId is null)
                {
                    return Reply(
                        26,
                        "CarrierIdReadAcknowledge",
                        CarrierCodec.IdReadBody(CarrierRequestAcknowledgement.Denied, string.Empty));
                }

                var port = _manager.FindPort(portId.Value);
                var carrier = port?.AssociatedCarrierId is { } associated ? _manager.FindCarrier(associated) : null;

                // Denied when there is nothing on the port, or when its label has not been read
                // and believed: handing back an identifier that is still WaitingForHost would be
                // telling the host something this machine does not know yet.
                return Reply(
                    26,
                    "CarrierIdReadAcknowledge",
                    carrier is null || carrier.IdStatus is CarrierIdStatus.IdNotRead
                        ? CarrierCodec.IdReadBody(CarrierRequestAcknowledgement.Denied, string.Empty)
                        : CarrierCodec.IdReadBody(CarrierRequestAcknowledgement.Accepted, carrier.CarrierId));
            }

            case 29:
            {
                if (!CarrierCodec.TryReadReservation(transaction.Message.Item, out var carrierId, out var portId))
                {
                    return Reply(30, "CarrierReservationAcknowledge", CarrierCodec.RequestResultBody(CarrierRequestAcknowledgement.Denied));
                }

                // The same action S3F17 would reach, so the rules are the same rules - but ACKC3
                // cannot say which state refused it. A host that wants the reason asks with S3F17.
                var result = _manager.Perform(new CarrierActionRequest
                {
                    Action = CarrierActions.ReserveAtPort,
                    CarrierId = carrierId,
                    PortId = portId,
                });

                if (!result.IsAccepted)
                {
                    _logger.LogInformation(
                        "S3F29 reserving port {Port} for '{Carrier}' was refused: {Reason}",
                        portId,
                        carrierId,
                        result.ErrorText);
                }

                return Reply(
                    30,
                    "CarrierReservationAcknowledge",
                    CarrierCodec.RequestResultBody(result.IsAccepted
                        ? CarrierRequestAcknowledgement.Accepted
                        : CarrierRequestAcknowledgement.Denied));
            }

            case 33:
            {
                var carrierId = CarrierCodec.TryReadText(transaction.Message.Item);

                if (carrierId is null)
                {
                    return Reply(34, "CarrierOutAcknowledge", CarrierCodec.RequestResultBody(CarrierRequestAcknowledgement.Denied));
                }

                var result = _manager.Perform(new CarrierActionRequest
                {
                    Action = CarrierActions.CarrierOut,
                    CarrierId = carrierId,
                });

                if (!result.IsAccepted)
                {
                    _logger.LogInformation("S3F33 for carrier '{Carrier}' was refused: {Reason}", carrierId, result.ErrorText);
                }

                return Reply(
                    34,
                    "CarrierOutAcknowledge",
                    CarrierCodec.RequestResultBody(result.IsAccepted
                        ? CarrierRequestAcknowledgement.Accepted
                        : CarrierRequestAcknowledgement.Denied));
            }

            default:
                return null;
        }
    }

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(3, function, replyExpected: false) { Name = name, Item = body };
}
