// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The equipment side of carrier management: the eight requests a host sends, answered out of the
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
/// slot map lives. A machine doing E87 properly declares both: these eight, and object services.
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
    /// Eight of E87's nine. S3F19 is the one missing: <c>CARRIERSTATUS</c> is a single item where
    /// a carrier has three state machines and nothing in E87 crowns one of them, so a value here
    /// would be this library choosing for a fab. The other two undefined items -
    /// <c>PORTSTATUS</c> on S3F22 and S3F32, <c>SLOTMAP</c> on S3F28 - are carried, because this
    /// package already serves those same values as stream 14 attributes and so had a reading to
    /// check against. <see cref="CarrierObjectProvider"/> serves all of them by name as well.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (3, 17),   // Carrier Action Request
        (3, 21),   // Storage Port Status Request
        (3, 23),   // Carrier Location Request
        (3, 25),   // Carrier ID Read Request
        (3, 27),   // Slot Map Request
        (3, 29),   // Carrier Reservation Request
        (3, 31),   // Storage Status Request
        (3, 33),   // Carrier Out Request
    ];

    /// <summary>Gets or sets where a carrier is, for the one request that asks.</summary>
    /// <remarks>
    /// <para>
    /// S3F24 wants a <c>LOC</c>, and E87 does not say what one is: a machine is free to report a
    /// shelf, a load port or an internal station. The default reports the storage location a
    /// carrier is in, falling back to the port it is at - the two places this package knows about
    /// - and zero for a carrier that is at neither.
    /// </para>
    /// <para>
    /// <strong>The order is the point, because zero is a location.</strong> This used to read
    /// <c>carrier.PortId ?? 0u</c>, which was honest while a port was the only place a carrier
    /// could be: a carrier the host has merely announced is nowhere, and S3F24 has no
    /// acknowledgement code to say "I do not know" with. Once
    /// <see cref="CarrierManager.AddStorageLocation"/> exists, that same expression starts
    /// reporting a carrier sitting on a shelf as being nowhere - the shrug turns into a wrong
    /// answer.
    /// </para>
    /// <para>
    /// <c>LOC</c> is a <c>U4</c> and <c>PTN</c> a <c>U1</c>, and nothing in E87 stops a machine
    /// numbering a shelf 1 while it also has a load port 1. This package cannot keep two number
    /// spaces apart on a machine's behalf, and S3F24 has room for only one number: a machine whose
    /// numbering overlaps replaces this, which is what it is for.
    /// </para>
    /// </remarks>
    public Func<Carrier, uint> LocationOf { get; set; }
        = carrier => carrier.StorageLocation ?? carrier.PortId ?? 0u;

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

            case 21:
            {
                var portId = CarrierCodec.TryReadPort(transaction.Message.Item);
                var port = portId is null ? null : _manager.FindPort(portId.Value);

                // Like S3F24, S3F22 has no acknowledgement code in it, so a port this machine does
                // not have is the zero-length list rather than a status nobody can act on.
                return Reply(
                    22,
                    "StoragePortStatusData",
                    port is null
                        ? SecsItem.L()
                        : CarrierCodec.PortStatusBody(
                            port.PortId,
                            port.TransferState.ToString(),
                            port.AssociatedCarrierId ?? string.Empty));
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

            case 27:
            {
                var carrierId = CarrierCodec.TryReadText(transaction.Message.Item);
                var carrier = carrierId is null ? null : _manager.FindCarrier(carrierId);

                // A carrier whose map nobody has agreed answers the zero-length list, not an
                // empty map: E87 gives the slot map a state machine precisely so that "not read"
                // and "read as empty" are different answers, and a host that got zero bytes for
                // an unverified carrier would have no way to tell which it had.
                return Reply(
                    28,
                    "SlotMapData",
                    carrier is null || carrier.SlotMapStatus is not CarrierSlotMapStatus.SlotMapVerificationOk
                        ? SecsItem.L()
                        : CarrierCodec.SlotMapBody(carrier.CarrierId, carrier.SlotMap));
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

            case 31:
            {
                // S3F31 carries nothing at all - "tell me about all of your storage locations" -
                // so there is no body to read and nothing a host could have named that this
                // machine might not have. Which makes the zero-length list an answer here rather
                // than the refusal it is in S3F22, S3F24 and S3F28: a machine with no storage has
                // none to report.
                var locations = _manager.StorageLocations;
                var reported = new (uint Location, string Status, string CarrierId)[locations.Count];
                var next = 0;

                foreach (var location in locations)
                {
                    reported[next++] = (
                        location.Location,
                        location.TransferState.ToString(),
                        location.CarrierId ?? string.Empty);
                }

                return Reply(32, "StorageStatusData", CarrierCodec.StorageStatusBody(reported));
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
