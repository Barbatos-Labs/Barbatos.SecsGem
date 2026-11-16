// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Substrates;

/// <summary>
/// Where a substrate is in its journey through the machine - E90.
/// </summary>
/// <remarks>
/// <para>
/// Three states, and they are about the <em>job</em> rather than about geography: a substrate is
/// at the place it came from, at the place the work happens, or at the place it is going. A
/// twelve-chamber cluster tool has one <see cref="AtWork"/> for all twelve, because what a host
/// tracks is whether the wafer has left its carrier and whether it has come back.
/// </para>
/// <code>
/// AtSource ------ picked up for processing -----------&gt; AtWork
/// AtWork -------- put back where it came from --------&gt; AtSource
///     |    ------ put down at its destination --------&gt; AtDestination
/// </code>
/// <para>
/// <see cref="AtWork"/> back to <see cref="AtSource"/> is allowed because it happens: a substrate
/// that could not be processed goes back in the carrier it came out of. What is refused is
/// <see cref="AtDestination"/> to anywhere - a substrate the machine has handed back is no longer
/// the machine's to move.
/// </para>
/// <para>
/// <strong>On provenance, and it is thinner here than anywhere else in this repository.</strong>
/// The catalogue that checks every message in this stack contains no mention of a substrate at
/// all - not a message, not an item - because E90 defines an object model and no messages. So
/// these states and transitions are E90 as this library understands it, checked by tests rather
/// than against reference data, and <see cref="SubstrateTracker"/> is the one file to change when
/// a fab disagrees.
/// </para>
/// </remarks>
public enum SubstrateTransportState
{
    /// <summary>Where it came from: in its carrier, in a slot, waiting.</summary>
    AtSource = 0,

    /// <summary>Out of the carrier and in the machine's hands.</summary>
    AtWork = 1,

    /// <summary>Put down where it was going. The machine is finished with it.</summary>
    AtDestination = 2,
}

/// <summary>
/// What has happened to a substrate - E90.
/// </summary>
/// <remarks>
/// <para>
/// Eight states, five of which are endings, and the distinctions between the endings are the
/// reason E90 exists. A host that only knows "done" cannot tell a wafer that was processed from
/// one that was skipped, and the two have different consequences for the lot.
/// </para>
/// <code>
/// NeedsProcessing -- work started -------------------&gt; InProcess
///     |     -------- not to be processed after all -&gt; Skipped
///
/// InProcess ------- finished --------------------------&gt; Processed
///     |     ------- the recipe failed -----------------&gt; Aborted
///     |     ------- stopped by a host or an operator --&gt; Stopped
///     |     ------- processed and found wanting -------&gt; Rejected
///
/// anything -------- the machine cannot find it -------&gt; Lost
/// </code>
/// <para>
/// <see cref="Lost"/> is reachable from every state including the endings, because losing track of
/// a substrate is not a thing a machine does on purpose and it can be noticed at any time. Nothing
/// is reachable <em>from</em> it: a found substrate is a new one as far as this state machine is
/// concerned, which is the same rule E87 has for a re-created carrier.
/// </para>
/// </remarks>
public enum SubstrateProcessingState
{
    /// <summary>Nothing has been done to it yet.</summary>
    NeedsProcessing = 0,

    /// <summary>Being worked on.</summary>
    InProcess = 1,

    /// <summary>Done, as intended.</summary>
    Processed = 2,

    /// <summary>The work failed part way through.</summary>
    Aborted = 3,

    /// <summary>Somebody stopped it part way through.</summary>
    Stopped = 4,

    /// <summary>Processed, and then found not good enough.</summary>
    Rejected = 5,

    /// <summary>The machine does not know where it is.</summary>
    /// <remarks>Reachable from anywhere, and nothing is reachable from it.</remarks>
    Lost = 6,

    /// <summary>Deliberately not processed.</summary>
    Skipped = 7,
}

/// <summary>Whether something is in a substrate location - E90.</summary>
/// <remarks>
/// A location holds one substrate, which is what separates it from a carrier: a carrier has slots
/// and a location does not. The state follows the substrates rather than being set, so a location
/// cannot be <see cref="Occupied"/> by nothing.
/// </remarks>
public enum SubstrateLocationState
{
    /// <summary>Empty.</summary>
    Unoccupied = 0,

    /// <summary>A substrate is there.</summary>
    Occupied = 1,
}

/// <summary>Which of a substrate's state machines an event is about.</summary>
/// <remarks>
/// The states come back as <see cref="int"/> on the event arguments, because two different enums
/// cannot share one property. Cast to the enum this names.
/// </remarks>
public enum SubstrateStateMachine
{
    /// <summary>The states are <see cref="SubstrateTransportState"/>.</summary>
    TransportState = 0,

    /// <summary>The states are <see cref="SubstrateProcessingState"/>.</summary>
    ProcessingState = 1,
}
