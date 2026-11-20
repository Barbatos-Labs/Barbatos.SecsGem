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
    private readonly Dictionary<uint, CarrierStorageLocation> _storage = [];
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

    /// <summary>Raised after a storage location's transfer state moves.</summary>
    /// <remarks>
    /// Raised inside the manager's lock. Queue the work; do not call back in. One event rather
    /// than four, because a storage location has one state machine - see
    /// <see cref="CarrierStorageLocation"/>.
    /// </remarks>
    public event EventHandler<StorageStateChangedEventArgs>? StorageStateChanged;

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

    /// <summary>Gets the storage locations, ordered by <c>LOC</c>.</summary>
    /// <remarks>
    /// Ordered, where <see cref="Ports"/> is not, because this is what S3F32 reports: a host that
    /// asks twice and gets the same shelves in a different order has to sort them itself to see
    /// whether anything moved.
    /// </remarks>
    public IReadOnlyCollection<CarrierStorageLocation> StorageLocations
    {
        get
        {
            lock (_gate)
            {
                return [.. _storage.Values.OrderBy(location => location.Location)];
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

    /// <summary>Declares a storage location. Out of service until something says otherwise.</summary>
    /// <param name="location">Which location - <c>LOC</c>.</param>
    /// <returns>The location.</returns>
    /// <remarks>
    /// <para>
    /// A shelf, a buffer, an internal station: somewhere inside the machine a carrier can sit that
    /// is not a load port. Declared from configuration for the same reason ports are - a host
    /// cannot invent one - and reported by S3F31, which asks for all of them at once and so has no
    /// way of naming one that does not exist.
    /// </para>
    /// <para>
    /// <strong>No host action reaches a storage location.</strong> Every carrier action E87 defines
    /// names a <c>CARRIERID</c> or a <c>PTN</c>, never a <c>LOC</c>, so a host can read storage and
    /// cannot drive it. The methods on this class are the machine's own hardware talking, which is
    /// also why <see cref="SetStorageServiceStatus"/> has no action name in its refusals the way
    /// <see cref="SetServiceStatus"/> has <c>ChangeServiceStatus</c>.
    /// </para>
    /// <para>
    /// Nothing here stops a location being numbered like a port - <c>LOC</c> is a <c>U4</c> and
    /// <c>PTN</c> a <c>U1</c>, and E87 says nothing about how a machine numbers its own places. It
    /// matters only for S3F24, which answers with one <c>LOC</c> for either kind of place; see
    /// <see cref="CarrierEquipment.LocationOf"/>, which is the hook for a machine whose numbering
    /// overlaps.
    /// </para>
    /// </remarks>
    public CarrierStorageLocation AddStorageLocation(uint location)
    {
        lock (_gate)
        {
            if (_storage.TryGetValue(location, out var existing))
            {
                return existing;
            }

            var declared = new CarrierStorageLocation(location);
            _storage[location] = declared;

            return declared;
        }
    }

    // --------------------------------------------------------- outliving the process

    /// <summary>Writes down everything this manager holds.</summary>
    /// <returns>A faithful record, including the states a restart will not claim again.</returns>
    /// <remarks>
    /// A consistent snapshot: taken under the manager's lock, so it is never half of one
    /// transition and half of the next. What is done with it afterwards - a file, a row, a key -
    /// is the application's, and this package depends on no serializer to make that possible.
    /// </remarks>
    public CarrierManagerSnapshot Capture()
    {
        lock (_gate)
        {
            return new CarrierManagerSnapshot
            {
                Ports = [.. _ports.Values.OrderBy(port => port.PortId).Select(port => new LoadPortSnapshot
                {
                    PortId = port.PortId,
                    TransferState = port.TransferState,
                    AccessMode = port.AccessMode,
                    AssociationState = port.AssociationState,
                    ReservationState = port.ReservationState,
                    AssociatedCarrierId = port.AssociatedCarrierId,
                    ReservedForCarrierId = port.ReservedForCarrierId,
                })],
                StorageLocations = [.. _storage.Values.OrderBy(location => location.Location).Select(location => new CarrierStorageSnapshot
                {
                    Location = location.Location,
                    TransferState = location.TransferState,
                    CarrierId = location.CarrierId,
                })],
                Carriers = [.. _carriers.Values.Select(carrier => new CarrierSnapshot
                {
                    CarrierId = carrier.CarrierId,
                    IdStatus = carrier.IdStatus,
                    SlotMapStatus = carrier.SlotMapStatus,
                    AccessStatus = carrier.AccessStatus,
                    PortId = carrier.PortId,
                    StorageLocation = carrier.StorageLocation,
                    SlotMap = [.. carrier.SlotMap.Span],
                    Capacity = carrier.Capacity,
                    SubstrateCount = carrier.SubstrateCount,
                    LotId = carrier.LotId,
                    Usage = carrier.Usage,
                })],
            };
        }
    }

    /// <summary>Reads a snapshot back into a manager that has no carriers yet.</summary>
    /// <param name="snapshot">What <see cref="Capture"/> wrote.</param>
    /// <returns>What came back, and what a restarted machine was not allowed to claim.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The rule: what the host told this machine comes back, and what the machine
    /// observed does not.</strong> A reboot does not change what a host asked for, and it says
    /// nothing at all about where the boxes are now.
    /// </para>
    /// <para>
    /// So a port's access mode, whether it is in service, and any reservation a host placed all
    /// come back. Its association with a carrier does not, and nor does its transfer state beyond
    /// being in service: a port holding a carrier is a thing the machine senses, and after a
    /// restart it has not sensed anything yet. Every port comes back
    /// <see cref="PortTransferState.TransferBlocked"/> when it was in service, which is the
    /// resting state in service and not an error.
    /// </para>
    /// <para>
    /// A storage location is a port's twin here: whether it is in service comes back, what it was
    /// ready to do beyond that does not, and what was in it does not either - a shelf's contents
    /// are sensed, so a machine that has just started has not sensed them. That is also why
    /// <see cref="CarrierSnapshot.StorageLocation"/> is recorded and not restored, exactly like
    /// <see cref="CarrierSnapshot.PortId"/>.
    /// </para>
    /// <para>
    /// A carrier's identity, lot, usage and capacity come back - a host said those. Its
    /// <see cref="CarrierIdStatus"/> and <see cref="CarrierSlotMapStatus"/> do not: they go back
    /// to <see cref="CarrierIdStatus.IdNotRead"/> and
    /// <see cref="CarrierSlotMapStatus.SlotMapNotRead"/>, with the slot map itself dropped,
    /// because E87 wants a carrier re-read after a restart and a machine that claimed a
    /// verification it did not perform would process a lot nobody confirmed. Which port it was at
    /// goes too, for the same reason.
    /// </para>
    /// <para>
    /// <strong>A carrier that was in access comes back
    /// <see cref="CarrierAccessStatus.CarrierStopped"/>.</strong> Not
    /// <see cref="CarrierAccessStatus.NotAccessed"/>, which would be a lie E87 names itself - the
    /// substrates in it have been touched, and that is exactly why its access state machine has no
    /// way back. Stopped is "finished early: a fault, or a host that said stop", and losing power
    /// part way through a carrier is a fault. The two endings come back as themselves, because
    /// they are history rather than a claim about now.
    /// </para>
    /// <para>
    /// <strong>A snapshot does not invent hardware.</strong> Ports are declared with
    /// <see cref="AddPort"/> and storage locations with <see cref="AddStorageLocation"/>, both from
    /// configuration, so one in the snapshot that this manager does not have is reported and
    /// skipped rather than created: a machine that came back with a load port its configuration no
    /// longer mentions would be a worse surprise than one that said so.
    /// </para>
    /// <para>
    /// Refused outright for a manager that already has carriers, because merging two beliefs about
    /// the same box has no right answer. Nothing is raised while restoring: these are not
    /// transitions a host should hear about as though the machine had just made them.
    /// </para>
    /// </remarks>
    public CarrierRestoreReport Restore(CarrierManagerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_carriers.Count > 0)
            {
                return CarrierRestoreReport.Denied(
                    $"this manager already has {_carriers.Count} carrier(s); a snapshot is read into an empty one.");
            }

            List<string> restored = [];
            Dictionary<string, string> adjusted = new(StringComparer.Ordinal);

            foreach (var port in snapshot.Ports)
            {
                if (!_ports.TryGetValue(port.PortId, out var existing))
                {
                    adjusted[$"port {port.PortId}"] =
                        "the snapshot has this port and this manager does not; ports are declared from "
                        + "configuration, so it was skipped rather than invented.";
                    continue;
                }

                existing.AccessMode = port.AccessMode;
                existing.TransferState = port.TransferState is PortTransferState.OutOfService
                    ? PortTransferState.OutOfService
                    : PortTransferState.TransferBlocked;
                existing.ReservationState = port.ReservationState;
                existing.ReservedForCarrierId = port.ReservedForCarrierId;
                existing.AssociationState = PortAssociationState.NotAssociated;
                existing.AssociatedCarrierId = null;

                if (port.TransferState is not (PortTransferState.OutOfService or PortTransferState.TransferBlocked))
                {
                    adjusted[$"port {port.PortId}"] =
                        $"was {port.TransferState}; a restarted machine has sensed nothing, so it is "
                        + $"{PortTransferState.TransferBlocked}.";
                }
                else if (port.AssociationState is PortAssociationState.Associated)
                {
                    adjusted[$"port {port.PortId}"] =
                        $"was associated with '{port.AssociatedCarrierId}'; which box is on a port is sensed, not remembered.";
                }

                restored.Add($"port {port.PortId}");
            }

            foreach (var location in snapshot.StorageLocations)
            {
                if (!_storage.TryGetValue(location.Location, out var shelf))
                {
                    adjusted[$"location {location.Location}"] =
                        "the snapshot has this storage location and this manager does not; locations are "
                        + "declared from configuration, so it was skipped rather than invented.";
                    continue;
                }

                shelf.TransferState = location.TransferState is PortTransferState.OutOfService
                    ? PortTransferState.OutOfService
                    : PortTransferState.TransferBlocked;
                shelf.CarrierId = null;

                if (location.TransferState is not (PortTransferState.OutOfService or PortTransferState.TransferBlocked))
                {
                    adjusted[$"location {location.Location}"] =
                        $"was {location.TransferState}; a restarted machine has sensed nothing, so it is "
                        + $"{PortTransferState.TransferBlocked}.";
                }
                else if (location.CarrierId is not null)
                {
                    adjusted[$"location {location.Location}"] =
                        $"had '{location.CarrierId}' in it; what is in a storage location is sensed, not remembered.";
                }

                restored.Add($"location {location.Location}");
            }

            foreach (var carrier in snapshot.Carriers)
            {
                if (carrier.CarrierId.Length == 0)
                {
                    continue;
                }

                var access = carrier.AccessStatus is CarrierAccessStatus.InAccess
                    ? CarrierAccessStatus.CarrierStopped
                    : carrier.AccessStatus;

                _carriers[carrier.CarrierId] = new Carrier(carrier.CarrierId)
                {
                    AccessStatus = access,
                    Capacity = carrier.Capacity,
                    LotId = carrier.LotId,
                    Usage = carrier.Usage,
                };

                if (access != carrier.AccessStatus)
                {
                    adjusted[carrier.CarrierId] =
                        $"was {carrier.AccessStatus} when the machine stopped, so it comes back "
                        + $"{access}: losing power part way through a carrier is a fault, and E87 has no way "
                        + "back to never-accessed once the substrates have been touched.";
                }
                else if (carrier.IdStatus is not CarrierIdStatus.IdNotRead
                    || carrier.SlotMapStatus is not CarrierSlotMapStatus.SlotMapNotRead)
                {
                    adjusted[carrier.CarrierId] =
                        $"was identifier {carrier.IdStatus}, slot map {carrier.SlotMapStatus}; both go back to "
                        + "unread, because E87 wants a carrier re-read after a restart.";
                }

                restored.Add(carrier.CarrierId);
            }

            return new CarrierRestoreReport
            {
                IsAccepted = true,
                Restored = restored,
                Adjusted = adjusted,
            };
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

    /// <summary>Finds a storage location.</summary>
    /// <param name="location">Which one - <c>LOC</c>.</param>
    /// <returns>The location, or <see langword="null"/>.</returns>
    public CarrierStorageLocation? FindStorageLocation(uint location)
    {
        lock (_gate)
        {
            return _storage.GetValueOrDefault(location);
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

            // A carrier is in one place. The mirror of the rule StoreCarrier keeps, and refused
            // here rather than quietly moved: two answers to "where is CAR-001" is one more than
            // S3F24 can give.
            if (_carriers.TryGetValue(carrierId, out var stored) && stored.StorageLocation is { } held)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"carrier '{carrierId}' is in storage location {held}; a carrier is in one place.");
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

    /// <summary>A carrier has been put into a storage location.</summary>
    /// <param name="location">Which location - <c>LOC</c>.</param>
    /// <param name="carrierId">Which carrier.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The storage half of <see cref="PlaceCarrier"/>, and it keeps the same rule: allowed only
    /// from <see cref="PortTransferState.ReadyToLoad"/>, because a carrier appearing somewhere
    /// that was not ready for one is a material handling fault and accepting it quietly would
    /// hide the fault.
    /// </para>
    /// <para>
    /// <strong>A carrier is in one place.</strong> One that is on a load port, or already in
    /// another location, is refused rather than moved: this manager would otherwise hold two
    /// answers to "where is CAR-001" and S3F24 can give only one. The machine takes the carrier
    /// off the port it is on - <see cref="RemoveCarrier"/> - and then stores it, which is also
    /// the order the hardware does it in.
    /// </para>
    /// </remarks>
    public CarrierActionResult StoreCarrier(uint location, string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            if (!_storage.TryGetValue(location, out var shelf))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.InvalidData,
                    $"there is no storage location {location}.");
            }

            if (shelf.TransferState is not PortTransferState.ReadyToLoad)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"storage location {location} is {shelf.TransferState} and a carrier may only be put into one that is {PortTransferState.ReadyToLoad}.");
            }

            // Looked up rather than created, because the refusals below must not leave a carrier
            // behind that this manager did not have before the call.
            if (_carriers.TryGetValue(carrierId, out var existing))
            {
                if (existing.PortId is { } port)
                {
                    return CarrierActionResult.Refused(
                        CarrierAcknowledgement.CannotPerformNow,
                        $"carrier '{carrierId}' is on port {port}; a carrier is in one place.");
                }

                if (existing.StorageLocation is { } held)
                {
                    return CarrierActionResult.Refused(
                        CarrierAcknowledgement.CannotPerformNow,
                        $"carrier '{carrierId}' is in storage location {held}; a carrier is in one place.");
                }
            }

            var carrier = GetOrCreate(carrierId);

            carrier.StorageLocation = location;
            shelf.CarrierId = carrierId;

            MoveStorage(shelf, PortTransferState.TransferBlocked);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>The carrier has been taken out of a storage location.</summary>
    /// <param name="location">Which location - <c>LOC</c>.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <remarks>Allowed only from <see cref="PortTransferState.ReadyToUnload"/>, for the same reason storing is allowed only from ready to load.</remarks>
    public CarrierActionResult FetchCarrier(uint location)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(location, out var shelf))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.InvalidData,
                    $"there is no storage location {location}.");
            }

            if (shelf.TransferState is not PortTransferState.ReadyToUnload)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"storage location {location} is {shelf.TransferState} and a carrier may only be taken out of one that is {PortTransferState.ReadyToUnload}.");
            }

            if (shelf.CarrierId is { } carrierId && _carriers.TryGetValue(carrierId, out var carrier))
            {
                carrier.StorageLocation = null;
            }

            shelf.CarrierId = null;

            MoveStorage(shelf, PortTransferState.TransferBlocked);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>Moves a storage location between the three in-service transfer states.</summary>
    /// <param name="location">Which location - <c>LOC</c>.</param>
    /// <param name="state">What it is ready to do.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <remarks>
    /// The same three rules <see cref="SetTransferState"/> keeps for a port:
    /// <see cref="PortTransferState.OutOfService"/> is a service decision and lives in
    /// <see cref="SetStorageServiceStatus"/>, a location out of service refuses all three, and
    /// ready to load and ready to unload each need the location to be in the matching condition -
    /// there is nothing to take out of an empty shelf, and nowhere to put anything in a full one.
    /// </remarks>
    public CarrierActionResult SetStorageTransferState(uint location, PortTransferState state)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(location, out var shelf))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.InvalidData,
                    $"there is no storage location {location}.");
            }

            if (state is PortTransferState.OutOfService)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.InvalidData,
                    $"{PortTransferState.OutOfService} is a service decision; use {nameof(SetStorageServiceStatus)}.");
            }

            if (!shelf.IsInService)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"storage location {location} is {PortTransferState.OutOfService}.");
            }

            if (state is PortTransferState.ReadyToUnload && !shelf.IsOccupied)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"storage location {location} has nothing in it to unload.");
            }

            if (state is PortTransferState.ReadyToLoad && shelf.IsOccupied)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"storage location {location} already has carrier '{shelf.CarrierId}' in it.");
            }

            MoveStorage(shelf, state);

            return CarrierActionResult.Ok;
        }
    }

    /// <summary>Puts a storage location in or out of service.</summary>
    /// <param name="location">Which location - <c>LOC</c>.</param>
    /// <param name="inService">Whether it is usable.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <remarks>
    /// A location holding a carrier cannot be taken out of service, the same rule
    /// <see cref="SetServiceStatus"/> keeps for a port: the carrier would be stranded somewhere
    /// nobody is allowed to touch. Unlike a port's, this is not reachable by a host - no E87
    /// carrier action names a <c>LOC</c>.
    /// </remarks>
    public CarrierActionResult SetStorageServiceStatus(uint location, bool inService)
    {
        lock (_gate)
        {
            if (!_storage.TryGetValue(location, out var shelf))
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.InvalidData,
                    $"there is no storage location {location}.");
            }

            if (!inService && shelf.IsOccupied)
            {
                return CarrierActionResult.Refused(
                    CarrierAcknowledgement.CannotPerformNow,
                    $"storage location {location} still has carrier '{shelf.CarrierId}' in it.");
            }

            var target = inService ? PortTransferState.TransferBlocked : PortTransferState.OutOfService;

            if (shelf.TransferState == target)
            {
                return CarrierActionResult.Ok;
            }

            MoveStorage(shelf, target);

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

    /// <summary>Moves a storage location, and raises after it has moved.</summary>
    /// <remarks>
    /// The same order as the port and carrier transitions, for the same reason: a handler reading
    /// the location it was just told about has to see the state the event is announcing.
    /// </remarks>
    private void MoveStorage(CarrierStorageLocation location, PortTransferState next)
    {
        if (location.TransferState == next)
        {
            return;
        }

        var previous = location.TransferState;
        location.TransferState = next;

        StorageStateChanged?.Invoke(this, new StorageStateChangedEventArgs
        {
            StorageLocation = location,
            Previous = previous,
            Current = next,
        });
    }

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
