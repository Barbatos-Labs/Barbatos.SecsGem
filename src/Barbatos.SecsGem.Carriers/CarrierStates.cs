// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// Whether a carrier's identifier has been read, and whether anybody believes it - E87.
/// </summary>
/// <remarks>
/// <para>
/// The first of E87's state machines, and the one that explains the standard. A carrier arrives at
/// a load port with a label on it. The equipment reads the label. From there the question is not
/// "what does it say" but "does the host agree that this is the carrier it was expecting", and
/// until that is settled nothing may be processed.
/// </para>
/// <code>
/// IdNotRead ------- read, and the equipment can verify alone ------&gt; IdVerificationOk
///     |     ------- read, but only the host can say -------------&gt; WaitingForHost
///     |     ------- not readable -----------------------------------&gt; IdVerificationFailed
///
/// WaitingForHost -- ProceedWithCarrier ----------------------------&gt; IdVerificationOk
///     |     ------- CancelCarrier --------------------------------&gt; IdVerificationFailed
/// </code>
/// <para>
/// <strong>On provenance.</strong> Unlike the SECS-II message set, nothing in this repository can
/// check these states: the catalogue covers E5's messages and items and says nothing about E87.
/// These are E87 as this library understands it, and <see cref="CarrierManager"/> is the one file
/// to change if a fab's integrator disagrees - which is a real possibility worth naming, because
/// E87 is a standard about behaviour and behaviour is where implementations differ.
/// </para>
/// </remarks>
public enum CarrierIdStatus
{
    /// <summary>Nobody has read the label yet.</summary>
    IdNotRead = 0,

    /// <summary>Read, and the equipment cannot settle it on its own.</summary>
    WaitingForHost = 1,

    /// <summary>Read and agreed.</summary>
    IdVerificationOk = 2,

    /// <summary>Read and refused, or not readable at all.</summary>
    IdVerificationFailed = 3,
}

/// <summary>
/// Whether a carrier's slot map has been read, and whether anybody believes it - E87.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="CarrierIdStatus"/>, for a different question: the identifier says
/// which carrier this is, and the slot map says what is inside it. A host that has a lot of
/// twenty-five wafers and a carrier that maps twenty-three has something to decide, and
/// <see cref="WaitingForHost"/> is where the carrier waits while it decides.
/// </para>
/// <code>
/// SlotMapNotRead -- mapped, and the equipment can verify alone ----&gt; SlotMapVerificationOk
///     |     ------- mapped, but only the host can say ------------&gt; WaitingForHost
///     |     ------- not mappable ---------------------------------&gt; SlotMapVerificationFailed
///
/// WaitingForHost -- ProceedWithCarrier ----------------------------&gt; SlotMapVerificationOk
///     |     ------- CancelCarrier --------------------------------&gt; SlotMapVerificationFailed
/// </code>
/// <para>
/// The map itself is not here. It is an attribute of the carrier, read over stream 14, because
/// that is where E87 puts it - and because <c>SLOTMAP</c> has no format in the catalogue, so the
/// alternative would be inventing one. See <see cref="Carrier.SlotMap"/>.
/// </para>
/// </remarks>
public enum CarrierSlotMapStatus
{
    /// <summary>Nobody has mapped it yet.</summary>
    SlotMapNotRead = 0,

    /// <summary>Mapped, and the equipment cannot settle it on its own.</summary>
    WaitingForHost = 1,

    /// <summary>Mapped and agreed.</summary>
    SlotMapVerificationOk = 2,

    /// <summary>Mapped and refused, or not mappable.</summary>
    SlotMapVerificationFailed = 3,
}

/// <summary>
/// How far through a carrier the equipment has got - E87.
/// </summary>
/// <remarks>
/// <code>
/// NotAccessed ----- the first substrate is taken ------------------&gt; InAccess
/// InAccess -------- every substrate has been dealt with -----------&gt; CarrierComplete
///     |     ------- stopped before that, by fault or by the host -&gt; CarrierStopped
/// </code>
/// <para>
/// <see cref="CarrierComplete"/> and <see cref="CarrierStopped"/> are both ends: a carrier does
/// not go back to <see cref="NotAccessed"/>, because the substrates in it have been touched. A
/// carrier that is to be run again is a new carrier as far as this state machine is concerned,
/// which is what <c>CarrierReCreate</c> is for.
/// </para>
/// </remarks>
public enum CarrierAccessStatus
{
    /// <summary>Nothing in it has been touched.</summary>
    NotAccessed = 0,

    /// <summary>Being worked on.</summary>
    InAccess = 1,

    /// <summary>Finished, everything dealt with.</summary>
    CarrierComplete = 2,

    /// <summary>Finished early: a fault, or a host that said stop.</summary>
    CarrierStopped = 3,
}

/// <summary>
/// What a load port is ready to do - E87.
/// </summary>
/// <remarks>
/// <para>
/// The state a material handler actually looks at. <see cref="OutOfService"/> is a port nobody may
/// use; the three in-service states say whether the port can take a carrier, give one back, or
/// neither just now.
/// </para>
/// <code>
/// OutOfService &lt;---- ChangeServiceStatus ----&gt; TransferBlocked
///
/// TransferBlocked -- the port can accept a carrier -----&gt; ReadyToLoad
///     |     -------- the carrier on it may be taken ----&gt; ReadyToUnload
///
/// ReadyToLoad ------ a carrier is placed ---------------&gt; TransferBlocked
/// ReadyToUnload ---- the carrier is removed ------------&gt; TransferBlocked
/// </code>
/// <para>
/// <see cref="TransferBlocked"/> is the resting state in service, not an error: a port holding a
/// carrier that is being processed is blocked for transfer and perfectly healthy.
/// </para>
/// </remarks>
public enum PortTransferState
{
    /// <summary>Not available to anybody.</summary>
    OutOfService = 0,

    /// <summary>In service, and no transfer may happen right now.</summary>
    TransferBlocked = 1,

    /// <summary>In service, and a carrier may be put on it.</summary>
    ReadyToLoad = 2,

    /// <summary>In service, and the carrier on it may be taken away.</summary>
    ReadyToUnload = 3,
}

/// <summary>Who drives a load port - E87's access mode.</summary>
/// <remarks>
/// <para>
/// <see cref="Manual"/> is an operator putting carriers on by hand; <see cref="Auto"/> is a
/// material handling system doing it. The host changes it with the <c>ChangeAccess</c> port
/// action, and E87 does not let it change while a transfer is in progress - which is a rule this
/// library enforces rather than documents.
/// </para>
/// </remarks>
public enum PortAccessMode
{
    /// <summary>An operator loads this port.</summary>
    Manual = 0,

    /// <summary>A material handling system loads this port.</summary>
    Auto = 1,
}

/// <summary>Whether a carrier is tied to a load port - E87's association state.</summary>
public enum PortAssociationState
{
    /// <summary>No carrier belongs to this port.</summary>
    NotAssociated = 0,

    /// <summary>A carrier belongs to this port.</summary>
    Associated = 1,
}

/// <summary>Whether a load port is held for a carrier that has not arrived yet - E87.</summary>
/// <remarks>
/// A reservation is how a host stops a port being used for anything else while a carrier is on its
/// way to it. <c>ReserveAtPort</c> takes it, <c>CancelReservationAtPort</c> gives it back, and a
/// carrier arriving at a port reserved for a different carrier is the kind of mistake the state
/// machine exists to refuse.
/// </remarks>
public enum PortReservationState
{
    /// <summary>Free.</summary>
    NotReserved = 0,

    /// <summary>Held for a carrier.</summary>
    Reserved = 1,
}
