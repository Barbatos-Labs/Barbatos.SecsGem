// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// A carrier, as E87 models one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every state here is read-only from outside.</strong> That is the point of the type: the
/// setters are internal, so the only way a carrier changes state is through
/// <see cref="CarrierManager"/>, which is where E87's rules about what may follow what are
/// written. A property bag with public setters would let an application put a carrier into
/// <see cref="CarrierAccessStatus.CarrierComplete"/> without ever accessing it, and then the state
/// machine would be a diagram in a document rather than something the code keeps true.
/// </para>
/// <para>
/// The attributes - slot map, lot, capacity - are the ones E87 names, and they travel to a host
/// through stream 14 rather than through stream 3. See <see cref="CarrierObjectProvider"/>.
/// </para>
/// </remarks>
public sealed class Carrier
{
    internal Carrier(string carrierId) => CarrierId = carrierId;

    /// <summary>What the carrier is called - <c>CARRIERID</c>.</summary>
    public string CarrierId { get; }

    /// <summary>Whether the identifier has been read, and whether it is believed.</summary>
    public CarrierIdStatus IdStatus { get; internal set; }

    /// <summary>Whether the slot map has been read, and whether it is believed.</summary>
    public CarrierSlotMapStatus SlotMapStatus { get; internal set; }

    /// <summary>How far through the carrier the equipment has got.</summary>
    public CarrierAccessStatus AccessStatus { get; internal set; }

    /// <summary>Which load port it is at, or <see langword="null"/> when it is not at one.</summary>
    /// <remarks>A carrier can exist without a port: that is what <c>CarrierNotification</c> creates, a carrier the host says is coming.</remarks>
    public byte? PortId { get; internal set; }

    /// <summary>What is in each slot.</summary>
    /// <remarks>
    /// One byte per slot, and the codes are the machine's: E87 leaves the vocabulary to the
    /// equipment, and <c>SLOTMAP</c> has no entry in the catalogue at all, so nothing here reads
    /// a meaning into a value. It reaches a host as a carrier attribute over stream 14, where an
    /// attribute may be any item - which is how this package carries a slot map without inventing
    /// a format for one.
    /// </remarks>
    public ReadOnlyMemory<byte> SlotMap { get; internal set; }

    /// <summary>How many slots the carrier has.</summary>
    public byte Capacity { get; internal set; }

    /// <summary>How many substrates are in it.</summary>
    public byte SubstrateCount { get; internal set; }

    /// <summary>The lot this carrier belongs to, when the host has said.</summary>
    public string LotId { get; internal set; } = string.Empty;

    /// <summary>What the carrier is for, in the fab's own words.</summary>
    public string Usage { get; internal set; } = string.Empty;

    /// <summary>Gets a value indicating whether both verifications have been settled in the carrier's favour.</summary>
    /// <remarks>
    /// The one derived property worth having: E87 will not let a carrier be accessed until its
    /// identifier and its slot map are both agreed, and an application that has to write that
    /// condition out itself will eventually write it with an <c>or</c>.
    /// </remarks>
    public bool IsVerified
        => IdStatus is CarrierIdStatus.IdVerificationOk
            && SlotMapStatus is CarrierSlotMapStatus.SlotMapVerificationOk;
}

/// <summary>
/// A load port, as E87 models one.
/// </summary>
/// <remarks>
/// Four of E87's state machines live on a port rather than on a carrier, and three of them are
/// about permission rather than progress: who may use the port, whether it is held for somebody,
/// and whether a carrier belongs to it. As with <see cref="Carrier"/>, every state is read-only
/// from outside and changes only through <see cref="CarrierManager"/>.
/// </remarks>
public sealed class LoadPort
{
    internal LoadPort(byte portId) => PortId = portId;

    /// <summary>Which port this is - <c>PTN</c>.</summary>
    public byte PortId { get; }

    /// <summary>What the port is ready to do.</summary>
    public PortTransferState TransferState { get; internal set; } = PortTransferState.OutOfService;

    /// <summary>Who drives it.</summary>
    public PortAccessMode AccessMode { get; internal set; } = PortAccessMode.Manual;

    /// <summary>Whether a carrier belongs to it.</summary>
    public PortAssociationState AssociationState { get; internal set; } = PortAssociationState.NotAssociated;

    /// <summary>Whether it is held for a carrier that has not arrived.</summary>
    public PortReservationState ReservationState { get; internal set; } = PortReservationState.NotReserved;

    /// <summary>The carrier that belongs to it, or <see langword="null"/>.</summary>
    public string? AssociatedCarrierId { get; internal set; }

    /// <summary>The carrier it is held for, or <see langword="null"/>.</summary>
    public string? ReservedForCarrierId { get; internal set; }

    /// <summary>Gets a value indicating whether the port is in service at all.</summary>
    public bool IsInService => TransferState is not PortTransferState.OutOfService;
}

/// <summary>A carrier changed state - E87.</summary>
/// <remarks>
/// <para>
/// E87 defines collection events for these changes, and this is where they come out. What it does
/// not do is publish them: a collection event needs a <c>CEID</c>, a <c>CEID</c> belongs to the
/// equipment that defines it, and this package has no way of knowing which number a machine gave
/// to "carrier identifier read". So an application wires these to its own
/// <c>TriggerEventAsync</c>, which is five lines and keeps this package off the GEM layer.
/// </para>
/// <para>
/// The carrier is live rather than a snapshot - it is the same object the manager holds - so a
/// handler reading it sees the state it has now, which during a burst of transitions may already
/// be past the one that raised this event. The two state properties here are the transition
/// itself, and they do not change.
/// </para>
/// </remarks>
public sealed class CarrierStateChangedEventArgs : EventArgs
{
    /// <summary>The carrier.</summary>
    public required Carrier Carrier { get; init; }

    /// <summary>Which of the carrier's state machines moved.</summary>
    public required CarrierStateMachine StateMachine { get; init; }

    /// <summary>What it was.</summary>
    public required int Previous { get; init; }

    /// <summary>What it is.</summary>
    public required int Current { get; init; }
}

/// <summary>A load port changed state - E87.</summary>
public sealed class PortStateChangedEventArgs : EventArgs
{
    /// <summary>The port.</summary>
    public required LoadPort Port { get; init; }

    /// <summary>Which of the port's state machines moved.</summary>
    public required PortStateMachine StateMachine { get; init; }

    /// <summary>What it was.</summary>
    public required int Previous { get; init; }

    /// <summary>What it is.</summary>
    public required int Current { get; init; }
}

/// <summary>Which of a carrier's state machines an event is about.</summary>
/// <remarks>
/// The states themselves come back as <see cref="int"/> on the event arguments, because three
/// different enums cannot share one property. Cast to the enum this names:
/// <see cref="CarrierIdStatus"/>, <see cref="CarrierSlotMapStatus"/> or
/// <see cref="CarrierAccessStatus"/>.
/// </remarks>
public enum CarrierStateMachine
{
    /// <summary>The states are <see cref="CarrierIdStatus"/>.</summary>
    IdStatus = 0,

    /// <summary>The states are <see cref="CarrierSlotMapStatus"/>.</summary>
    SlotMapStatus = 1,

    /// <summary>The states are <see cref="CarrierAccessStatus"/>.</summary>
    AccessStatus = 2,
}

/// <summary>Which of a load port's state machines an event is about.</summary>
/// <remarks>
/// Cast the states on the event arguments to the enum this names:
/// <see cref="PortTransferState"/>, <see cref="PortAccessMode"/>,
/// <see cref="PortAssociationState"/> or <see cref="PortReservationState"/>.
/// </remarks>
public enum PortStateMachine
{
    /// <summary>The states are <see cref="PortTransferState"/>.</summary>
    TransferState = 0,

    /// <summary>The states are <see cref="PortAccessMode"/>.</summary>
    AccessMode = 1,

    /// <summary>The states are <see cref="PortAssociationState"/>.</summary>
    AssociationState = 2,

    /// <summary>The states are <see cref="PortReservationState"/>.</summary>
    ReservationState = 3,
}
