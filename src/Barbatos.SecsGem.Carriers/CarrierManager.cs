// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The carriers and load ports a machine has, and E87's rules about what may happen to them.
/// </summary>
/// <remarks>
/// <para>
/// This is the package. Everything else - the message handlers, the object provider, the
/// registration - is plumbing around this class, and this class has no connection, no transport
/// and no dependency on anything that does. That is deliberate: a state machine is the one part of
/// a protocol stack that can be tested exhaustively without a wire, and most of the tests for this
/// package never open a link at all.
/// </para>
/// <para>
/// <strong>What "enforced" means.</strong> Every method that would move a state checks whether E87
/// allows that move from the state the thing is in, and refuses with the code the standard gives -
/// <see cref="CarrierAcknowledgement.CannotPerformNow"/> - and an <c>ERRTEXT</c> that names the
/// refusing state. A carrier cannot be accessed before its identifier is agreed; a port cannot
/// change access mode mid-transfer; a finished carrier cannot be accessed again without being
/// re-created. Those are the rules worth having a library for, because an application that
/// implements them in its own event handlers implements them four times and differently.
/// </para>
/// <para>
/// <strong>On provenance, and this matters more here than anywhere else in the repository.</strong>
/// The SECS-II message set in this stack is checked against the catalogue, which is reference data
/// somebody else published. E87 is not in the catalogue: it defines behaviour, not messages, and
/// there is nothing in this repository to check these transitions against. They are E87 as this
/// library understands it. The states and the transitions are in one place - here and
/// <c>CarrierStates.cs</c> - so an integrator who finds a fab that disagrees has one file to
/// change, and the tests name the rule they are checking rather than the line number.
/// </para>
/// <para>
/// Thread safety: one lock around every transition, because a state machine that can be observed
/// half-moved is not one. The events are raised inside it, so a handler that calls back into the
/// manager will deadlock - handlers should queue, not transact.
/// </para>
/// </remarks>
public sealed class CarrierManager
{
    private readonly Dictionary<string, Carrier> _carriers = new(StringComparer.Ordinal);
    private readonly Dictionary<byte, LoadPort> _ports = [];
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    /// <summary>Raised after one of a carrier's state machines moves.</summary>
    /// <remarks>Raised inside the manager's lock. Queue the work; do not call back in.</remarks>
    public event EventHandler<CarrierStateChangedEventArgs>? CarrierStateChanged;

    /// <summary>Raised after one of a load port's state machines moves.</summary>
    /// <remarks>Raised inside the manager's lock. Queue the work; do not call back in.</remarks>
    public event EventHandler<PortStateChangedEventArgs>? PortStateChanged;

    /// <summary>Gets the carriers this machine knows about.</summary>
    public IReadOnlyCollection<Carrier> Carriers
    {
        get
        {
            lock (_gate)
            {
                return [.. _carriers.Values];
            }
        }
    }

    /// <summary>Gets the load ports.</summary>
    public IReadOnlyCollection<LoadPort> Ports
    {
        get
        {
            lock (_gate)
            {
                return [.. _ports.Values];
            }
        }
    }

    // ------------------------------------------------------------ what the machine has

    /// <summary>Declares a load port. Out of service until something says otherwise.</summary>
    /// <param name="portId">Which port - <c>PTN</c>.</param>
    /// <returns>The port.</returns>
    /// <remarks>
    /// Ports are the machine's own hardware, so they are declared rather than created by a
    /// message: a host cannot invent a load port, and an action naming one that does not exist is
    /// answered <see cref="CarrierAcknowledgement.InvalidData"/>.
    /// </remarks>
    public LoadPort AddPort(byte portId)
    {
        lock (_gate)
        {
            if (_ports.TryGetValue(portId, out var existing))
            {
                return existing;
            }

            var port = new LoadPort(portId);
            _ports[portId] = port;

            return port;
        }
    }

    /// <summary>Finds a carrier.</summary>
    /// <param name="carrierId">Which one.</param>
    /// <returns>The carrier, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public Carrier? FindCarrier(string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            return _carriers.GetValueOrDefault(carrierId);
        }
    }

    /// <summary>Finds a load port.</summary>
    /// <param name="portId">Which one.</param>
    /// <returns>The port, or <see langword="null"/>.</returns>
    public LoadPort? FindPort(byte portId)
    {
        lock (_gate)
        {
            return _ports.GetValueOrDefault(portId);
        }
    }

    // ------------------------------------------------- what the machine itself does

    /// <summary>A carrier has been put on a port.</summary>
    /// <param name="portId">Which port.</param>
    /// <param name="carrierId">Which carrier, as far as the machine can tell. Empty when it has not read the label yet.</param>
    /// <returns>The carrier, or a refusal when the port cannot take one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The transition a load port sensor drives. E87 allows it only from
    /// <see cref="PortTransferState.ReadyToLoad"/>: a carrier appearing on a port that was not
    /// ready for one is a material handling fault, and reporting it as normal would hide the
    /// fault.
    /// </para>
    /// <para>
    /// A reserved port takes only the carrier it is reserved for. That is the rule reservations
    /// exist for, and it is the one this package refuses most often in a fab that has its
    /// scheduling slightly wrong.
    /// </para>
    /// </remarks>
    public CarrierActionResult PlaceCarrier(byte portId, string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (port.TransferState is not PortTransferState.ReadyToLoad)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is {port.TransferState} and a carrier may only be placed on one that is {PortTransferState.ReadyToLoad}.");
            }

            if (port.ReservationState is PortReservationState.Reserved
                && !string.Equals(port.ReservedForCarrierId, carrierId, StringComparison.Ordinal))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is reserved for '{port.ReservedForCarrierId}'.");
            }

            var carrier = GetOrCreate(carrierId);

            carrier.PortId = portId;

            MoveAssociation(port, PortAssociationState.Associated, carrierId);
            MoveTransfer(port, PortTransferState.TransferBlocked);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>The carrier has been taken off a port.</summary>
    /// <param name="portId">Which port.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <remarks>Allowed only from <see cref="PortTransferState.ReadyToUnload"/>, for the same reason placing is allowed only from ready to load.</remarks>
    public CarrierActionResult RemoveCarrier(byte portId)
    {
        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (port.TransferState is not PortTransferState.ReadyToUnload)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is {port.TransferState} and a carrier may only be removed from one that is {PortTransferState.ReadyToUnload}.");
            }

            if (port.AssociatedCarrierId is { } carrierId && _carriers.TryGetValue(carrierId, out var carrier))
            {
                carrier.PortId = null;
            }

            MoveAssociation(port, PortAssociationState.NotAssociated, carrierId: null);
            MoveTransfer(port, PortTransferState.TransferBlocked);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>Moves a port between the three in-service transfer states.</summary>
    /// <param name="portId">Which port.</param>
    /// <param name="state">What it is ready to do.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <remarks>
    /// <see cref="PortTransferState.OutOfService"/> is not reachable this way - that is a service
    /// decision rather than a readiness one, and <see cref="SetServiceStatus"/> is where it lives.
    /// A port that is out of service refuses all three.
    /// </remarks>
    public CarrierActionResult SetTransferState(byte portId, PortTransferState state)
    {
        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (state is PortTransferState.OutOfService)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.InvalidData,
                    $"{PortTransferState.OutOfService} is a service decision; use ChangeServiceStatus.");
            }

            if (!port.IsInService)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is {PortTransferState.OutOfService}.");
            }

            if (state is PortTransferState.ReadyToUnload && port.AssociationState is PortAssociationState.NotAssociated)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} has no carrier on it to unload.");
            }

            if (state is PortTransferState.ReadyToLoad && port.AssociationState is PortAssociationState.Associated)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} already has carrier '{port.AssociatedCarrierId}' on it.");
            }

            MoveTransfer(port, state);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>Puts a port in or out of service.</summary>
    /// <param name="portId">Which port.</param>
    /// <param name="inService">Whether it is usable.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <remarks>A port holding a carrier cannot be taken out of service: the carrier would be stranded on a port nobody is allowed to touch.</remarks>
    public CarrierActionResult SetServiceStatus(byte portId, bool inService)
    {
        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (!inService && port.AssociationState is PortAssociationState.Associated)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} still has carrier '{port.AssociatedCarrierId}' on it.");
            }

            var target = inService ? PortTransferState.TransferBlocked : PortTransferState.OutOfService;

            if (port.TransferState == target)
            {
                return CarrierActionResult.Ok;
            }

            MoveTransfer(port, target);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>The machine has read a carrier's label.</summary>
    /// <param name="carrierId">What the label says.</param>
    /// <param name="verified">
    /// <see langword="true"/> when the machine can agree on its own - the identifier was bound to
    /// the port, or this machine does not verify identifiers at all. <see langword="false"/> when
    /// it cannot read it. <see langword="null"/> when only the host can say, which leaves the
    /// carrier in <see cref="CarrierIdStatus.WaitingForHost"/>.
    /// </param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public CarrierActionResult ReadCarrierId(string carrierId, bool? verified)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            var carrier = GetOrCreate(carrierId);

            if (carrier.IdStatus is not CarrierIdStatus.IdNotRead)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is already {carrier.IdStatus}.");
            }

            var next = verified switch
            {
                true => CarrierIdStatus.IdVerificationOk,
                false => CarrierIdStatus.IdVerificationFailed,
                null => CarrierIdStatus.WaitingForHost,
            };

            MoveId(carrier, next);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>The machine has mapped a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="slotMap">What is in each slot. The bytes are copied.</param>
    /// <param name="verified">As <see cref="ReadCarrierId"/>: agree, refuse, or leave it to the host.</param>
    /// <returns>The outcome.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public CarrierActionResult ReadSlotMap(string carrierId, ReadOnlyMemory<byte> slotMap, bool? verified)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            if (!_carriers.TryGetValue(carrierId, out var carrier))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no carrier '{carrierId}'.");
            }

            if (carrier.SlotMapStatus is not CarrierSlotMapStatus.SlotMapNotRead)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is already {carrier.SlotMapStatus}.");
            }

            // Copied, because the caller's buffer is the caller's and this object outlives the
            // call. The same rule every store in this stack follows.
            carrier.SlotMap = slotMap.ToArray();
            carrier.SubstrateCount = (byte)Math.Min(CountOccupied(carrier.SlotMap.Span), byte.MaxValue);

            if (carrier.Capacity == 0)
            {
                carrier.Capacity = (byte)Math.Min(slotMap.Length, byte.MaxValue);
            }

            var next = verified switch
            {
                true => CarrierSlotMapStatus.SlotMapVerificationOk,
                false => CarrierSlotMapStatus.SlotMapVerificationFailed,
                null => CarrierSlotMapStatus.WaitingForHost,
            };

            MoveSlotMap(carrier, next);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>The machine has started on a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The rule this package exists for: a carrier whose identifier or slot map is not agreed
    /// cannot be accessed. A machine that starts probing a carrier the host has not confirmed is
    /// how a lot ends up processed against the wrong recipe.
    /// </remarks>
    public CarrierActionResult BeginAccess(string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            if (!_carriers.TryGetValue(carrierId, out var carrier))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no carrier '{carrierId}'.");
            }

            if (carrier.AccessStatus is not CarrierAccessStatus.NotAccessed)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is {carrier.AccessStatus}.");
            }

            if (!carrier.IsVerified)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is not verified: identifier {carrier.IdStatus}, slot map {carrier.SlotMapStatus}.");
            }

            MoveAccess(carrier, CarrierAccessStatus.InAccess);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>The machine has finished a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <param name="stopped">Whether it ended early - a fault, or a host that said stop.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public CarrierActionResult EndAccess(string carrierId, bool stopped = false)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            if (!_carriers.TryGetValue(carrierId, out var carrier))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no carrier '{carrierId}'.");
            }

            if (carrier.AccessStatus is not CarrierAccessStatus.InAccess)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is {carrier.AccessStatus} and only one {CarrierAccessStatus.InAccess} can finish.");
            }

            MoveAccess(carrier, stopped ? CarrierAccessStatus.CarrierStopped : CarrierAccessStatus.CarrierComplete);

            return CarrierActionResult.Ok;
        }
    }

    // --------------------------------------------------------- what the host asks for

    /// <summary>Performs a carrier or port action - the S3F17 side of E87.</summary>
    /// <param name="request">What the host asked for.</param>
    /// <returns>The answer, with the refusing state named when it is a refusal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// An action this method does not know is <see cref="CarrierAcknowledgement.InvalidCommand"/>
    /// rather than an exception, because <c>CARRIERACTION</c> is free text and a host is entitled
    /// to ask for something this library has never heard of. An application that knows better can
    /// look at the request first and only hand over what it does not handle itself.
    /// </remarks>
    public CarrierActionResult Perform(CarrierActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.Action switch
        {
            CarrierActions.ProceedWithCarrier => Settle(request.CarrierId, proceed: true),
            CarrierActions.CancelCarrier => Settle(request.CarrierId, proceed: false),
            CarrierActions.CarrierNotification => Notify(request),
            CarrierActions.CancelCarrierNotification => Forget(request.CarrierId),
            CarrierActions.BindCarrier => Bind(request),
            CarrierActions.CancelBind => Unbind(request),
            CarrierActions.CarrierOut => Out(request),
            CarrierActions.CancelCarrierOut => Blocked(request, CarrierActions.CancelCarrierOut),
            CarrierActions.CarrierIn => In(request),
            CarrierActions.CancelCarrierIn => Blocked(request, CarrierActions.CancelCarrierIn),
            CarrierActions.CarrierReCreate => ReCreate(request.CarrierId),
            CarrierActions.ChangeAccess => ChangeAccess(request),
            CarrierActions.ReserveAtPort => Reserve(request),
            CarrierActions.CancelReservationAtPort => CancelReservation(request),
            CarrierActions.ChangeServiceStatus => ChangeService(request),
            _ => CarrierActionResult.Refused(
                CarrierAcknowledgement.InvalidCommand,
                $"'{request.Action}' is not an action this equipment knows."),
        };
    }

    // ------------------------------------------------------------------ the actions

    private CarrierActionResult Settle(string carrierId, bool proceed)
    {
        lock (_gate)
        {
            if (!TryFind(carrierId, out var carrier, out var refusal))
            {
                return refusal;
            }

            var waitingOnId = carrier.IdStatus is CarrierIdStatus.WaitingForHost;
            var waitingOnMap = carrier.SlotMapStatus is CarrierSlotMapStatus.WaitingForHost;

            if (!waitingOnId && !waitingOnMap)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is not waiting for the host: identifier {carrier.IdStatus}, slot map {carrier.SlotMapStatus}.");
            }

            // Both, when both are waiting. E87 has one action for the carrier rather than one per
            // verification, so a host that proceeds is proceeding with everything outstanding.
            if (waitingOnId)
            {
                MoveId(carrier, proceed ? CarrierIdStatus.IdVerificationOk : CarrierIdStatus.IdVerificationFailed);
            }

            if (waitingOnMap)
            {
                MoveSlotMap(
                    carrier,
                    proceed ? CarrierSlotMapStatus.SlotMapVerificationOk : CarrierSlotMapStatus.SlotMapVerificationFailed);
            }

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult Notify(CarrierActionRequest request)
    {
        if (request.CarrierId.Length == 0)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, "a carrier notification needs a carrier identifier.");
        }

        lock (_gate)
        {
            if (_carriers.ContainsKey(request.CarrierId))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{request.CarrierId}' is already known here.");
            }

            var carrier = GetOrCreate(request.CarrierId);

            if (request.Argument.Length > 0)
            {
                carrier.LotId = request.Argument;
            }

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult Forget(string carrierId)
    {
        lock (_gate)
        {
            if (!TryFind(carrierId, out var carrier, out var refusal))
            {
                return refusal;
            }

            if (carrier.PortId is not null)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is at port {carrier.PortId} and has arrived; a notification can only be cancelled before that.");
            }

            _carriers.Remove(carrierId);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult Bind(CarrierActionRequest request)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, "a bind needs a port.");
        }

        if (request.CarrierId.Length == 0)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, "a bind needs a carrier identifier.");
        }

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (port.AssociationState is PortAssociationState.Associated)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} already has carrier '{port.AssociatedCarrierId}' on it.");
            }

            // A bind is a reservation with an expectation attached: the port is held, and the
            // carrier that turns up there is checked against the name rather than taken on trust.
            _ = GetOrCreate(request.CarrierId);

            MoveReservation(port, PortReservationState.Reserved, request.CarrierId);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult Unbind(CarrierActionRequest request)
        => ReleaseReservation(request, CarrierActions.CancelBind);

    private CarrierActionResult Reserve(CarrierActionRequest request)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, "a reservation needs a port.");
        }

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (port.ReservationState is PortReservationState.Reserved)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is already reserved for '{port.ReservedForCarrierId}'.");
            }

            if (port.AssociationState is PortAssociationState.Associated)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} already has carrier '{port.AssociatedCarrierId}' on it.");
            }

            MoveReservation(port, PortReservationState.Reserved, request.CarrierId.Length == 0 ? null : request.CarrierId);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult CancelReservation(CarrierActionRequest request)
        => ReleaseReservation(request, CarrierActions.CancelReservationAtPort);

    private CarrierActionResult ReleaseReservation(CarrierActionRequest request, string action)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"{action} needs a port.");
        }

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (port.ReservationState is PortReservationState.NotReserved)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is not reserved.");
            }

            MoveReservation(port, PortReservationState.NotReserved, carrierId: null);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult In(CarrierActionRequest request)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"{CarrierActions.CarrierIn} needs a port.");
        }

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (!port.IsInService)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is {PortTransferState.OutOfService}.");
            }

            if (port.AssociationState is PortAssociationState.Associated)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} already has carrier '{port.AssociatedCarrierId}' on it.");
            }

            // The host is telling the machine to expect a carrier in: the port becomes ready to
            // load, and the carrier itself arrives later through PlaceCarrier.
            MoveTransfer(port, PortTransferState.ReadyToLoad);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult Out(CarrierActionRequest request)
    {
        lock (_gate)
        {
            if (!TryFind(request.CarrierId, out var carrier, out var refusal))
            {
                return refusal;
            }

            if (carrier.PortId is not { } portId || !_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrier.CarrierId}' is not at a port.");
            }

            if (carrier.AccessStatus is CarrierAccessStatus.InAccess)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrier.CarrierId}' is {CarrierAccessStatus.InAccess}.");
            }

            MoveTransfer(port, PortTransferState.ReadyToUnload);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult Blocked(CarrierActionRequest request, string action)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"{action} needs a port.");
        }

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            if (port.TransferState is PortTransferState.TransferBlocked or PortTransferState.OutOfService)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is {port.TransferState} and there is no transfer to cancel.");
            }

            MoveTransfer(port, PortTransferState.TransferBlocked);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult ReCreate(string carrierId)
    {
        lock (_gate)
        {
            if (!TryFind(carrierId, out var carrier, out var refusal))
            {
                return refusal;
            }

            if (carrier.AccessStatus is CarrierAccessStatus.InAccess)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is {CarrierAccessStatus.InAccess}; stop it before re-creating it.");
            }

            // Everything back to where a carrier starts, which is what makes this the only way out
            // of a finished state: the substrates have been touched, so what is being created is a
            // new carrier that happens to have the same name.
            MoveAccess(carrier, CarrierAccessStatus.NotAccessed);
            MoveId(carrier, CarrierIdStatus.IdNotRead);
            MoveSlotMap(carrier, CarrierSlotMapStatus.SlotMapNotRead);

            carrier.SlotMap = ReadOnlyMemory<byte>.Empty;
            carrier.SubstrateCount = 0;

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult ChangeAccess(CarrierActionRequest request)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"{CarrierActions.ChangeAccess} needs a port.");
        }

        if (!Enum.TryParse<PortAccessMode>(request.Argument, ignoreCase: true, out var mode))
        {
            return CarrierActionResult.Refused(
                CarrierAcknowledgement.InvalidData,
                $"'{request.Argument}' is not an access mode; it is {PortAccessMode.Manual} or {PortAccessMode.Auto}.");
        }

        lock (_gate)
        {
            if (!_ports.TryGetValue(portId, out var port))
            {
                return CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no port {portId}.");
            }

            // E87 does not allow the mode to change under a transfer: a handler that was told the
            // port is automatic has started moving, and an operator taking it manual mid-move is
            // how a carrier gets dropped.
            if (port.TransferState is PortTransferState.ReadyToLoad or PortTransferState.ReadyToUnload)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"port {portId} is {port.TransferState}, so a transfer is in progress.");
            }

            if (port.AccessMode == mode)
            {
                return CarrierActionResult.Ok;
            }

            MoveAccessMode(port, mode);

            return CarrierActionResult.Ok;
        }
    }

    private CarrierActionResult ChangeService(CarrierActionRequest request)
    {
        if (request.PortId is not { } portId)
        {
            return CarrierActionResult.Refused(
                CarrierAcknowledgement.InvalidData,
                $"{CarrierActions.ChangeServiceStatus} needs a port.");
        }

        if (!bool.TryParse(request.Argument, out var inService)
            && !string.Equals(request.Argument, "InService", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(request.Argument, "OutOfService", StringComparison.OrdinalIgnoreCase))
        {
            return CarrierActionResult.Refused(
                CarrierAcknowledgement.InvalidData,
                $"'{request.Argument}' is not a service status; it is InService or OutOfService.");
        }

        inService = inService || string.Equals(request.Argument, "InService", StringComparison.OrdinalIgnoreCase);

        return SetServiceStatus(portId, inService);
    }

    // ------------------------------------------------------------------- the pieces

    private Carrier GetOrCreate(string carrierId)
    {
        if (_carriers.TryGetValue(carrierId, out var existing))
        {
            return existing;
        }

        var carrier = new Carrier(carrierId);
        _carriers[carrierId] = carrier;

        return carrier;
    }

    private bool TryFind(
        string carrierId,
        [NotNullWhen(true)] out Carrier? carrier,
        out CarrierActionResult refusal)
    {
        if (carrierId.Length == 0)
        {
            carrier = null;
            refusal = CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, "that action needs a carrier identifier.");

            return false;
        }

        if (!_carriers.TryGetValue(carrierId, out carrier))
        {
            refusal = CarrierActionResult.Refused(CarrierAcknowledgement.InvalidData, $"there is no carrier '{carrierId}'.");

            return false;
        }

        refusal = CarrierActionResult.Ok;

        return true;
    }

    private void MoveId(Carrier carrier, CarrierIdStatus next)
    {
        if (carrier.IdStatus == next)
        {
            return;
        }

        var previous = carrier.IdStatus;
        carrier.IdStatus = next;

        Raise(carrier, CarrierStateMachine.IdStatus, (int)previous, (int)next);
    }

    private void MoveSlotMap(Carrier carrier, CarrierSlotMapStatus next)
    {
        if (carrier.SlotMapStatus == next)
        {
            return;
        }

        var previous = carrier.SlotMapStatus;
        carrier.SlotMapStatus = next;

        Raise(carrier, CarrierStateMachine.SlotMapStatus, (int)previous, (int)next);
    }

    private void MoveAccess(Carrier carrier, CarrierAccessStatus next)
    {
        if (carrier.AccessStatus == next)
        {
            return;
        }

        var previous = carrier.AccessStatus;
        carrier.AccessStatus = next;

        Raise(carrier, CarrierStateMachine.AccessStatus, (int)previous, (int)next);
    }

    private void Raise(Carrier carrier, CarrierStateMachine machine, int previous, int current)
        => CarrierStateChanged?.Invoke(this, new CarrierStateChangedEventArgs
        {
            Carrier = carrier,
            StateMachine = machine,
            Previous = previous,
            Current = current,
        });

    private void MoveTransfer(LoadPort port, PortTransferState next)
    {
        if (port.TransferState == next)
        {
            return;
        }

        var previous = port.TransferState;
        port.TransferState = next;

        Raise(port, PortStateMachine.TransferState, (int)previous, (int)next);
    }

    private void MoveAccessMode(LoadPort port, PortAccessMode next)
    {
        if (port.AccessMode == next)
        {
            return;
        }

        var previous = port.AccessMode;
        port.AccessMode = next;

        Raise(port, PortStateMachine.AccessMode, (int)previous, (int)next);
    }

    private void MoveAssociation(LoadPort port, PortAssociationState next, string? carrierId)
    {
        var previous = port.AssociationState;

        port.AssociationState = next;
        port.AssociatedCarrierId = carrierId;

        if (previous != next)
        {
            Raise(port, PortStateMachine.AssociationState, (int)previous, (int)next);
        }
    }

    private void MoveReservation(LoadPort port, PortReservationState next, string? carrierId)
    {
        var previous = port.ReservationState;

        port.ReservationState = next;
        port.ReservedForCarrierId = carrierId;

        if (previous != next)
        {
            Raise(port, PortStateMachine.ReservationState, (int)previous, (int)next);
        }
    }

    /// <summary>Raises a port transition, after the port has already moved.</summary>
    /// <remarks>
    /// After, and that is the whole reason these four methods exist rather than a line of
    /// assignment beside a line of notification: a handler reading the port it was just told about
    /// has to see the state the event is announcing. The same order the carrier transitions use.
    /// </remarks>
    private void Raise(LoadPort port, PortStateMachine machine, int previous, int current)
        => PortStateChanged?.Invoke(this, new PortStateChangedEventArgs
        {
            Port = port,
            StateMachine = machine,
            Previous = previous,
            Current = current,
        });

    /// <summary>Counts the slots that hold something.</summary>
    /// <remarks>
    /// Zero means empty and anything else means occupied, which is the only reading available: the
    /// codes in a slot map are the machine's own and the catalogue has no entry for
    /// <c>SLOTMAP</c> at all. A machine whose vocabulary differs sets
    /// <see cref="Carrier.SubstrateCount"/> through its own attribute instead.
    /// </remarks>
    private static int CountOccupied(ReadOnlySpan<byte> slotMap)
    {
        var count = 0;

        foreach (var slot in slotMap)
        {
            if (slot != 0)
            {
                count++;
            }
        }

        return count;
    }
}
