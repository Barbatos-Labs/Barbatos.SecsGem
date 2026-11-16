// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Substrates;

/// <summary>
/// A substrate, as E90 models one.
/// </summary>
/// <remarks>
/// <para>
/// Public getters and internal setters, the same rule E87's carrier follows: the only thing that
/// can move a substrate is <see cref="SubstrateTracker"/>, where the transitions are. An
/// application that could assign <see cref="ProcessingState"/> directly could mark a wafer
/// <see cref="SubstrateProcessingState.Processed"/> without processing it, and a state machine
/// nobody has to go through is a diagram rather than a guarantee.
/// </para>
/// <para>
/// The labels - lot, usage, type - are set when the substrate is created and through
/// <see cref="SubstrateTracker.Describe"/>, because they are what a host tells the machine rather
/// than what the machine works out.
/// </para>
/// </remarks>
public sealed class Substrate
{
    internal Substrate(string substrateId) => SubstrateId = substrateId;

    /// <summary>What the substrate is called.</summary>
    public string SubstrateId { get; }

    /// <summary>Where it is in its journey.</summary>
    public SubstrateTransportState TransportState { get; internal set; }

    /// <summary>What has happened to it.</summary>
    public SubstrateProcessingState ProcessingState { get; internal set; }

    /// <summary>The location it is at now.</summary>
    public string LocationId { get; internal set; } = string.Empty;

    /// <summary>The location it started at.</summary>
    public string SourceLocationId { get; internal set; } = string.Empty;

    /// <summary>The location it is going to.</summary>
    /// <remarks>Empty until a host says, and a substrate with no destination goes back where it came from.</remarks>
    public string DestinationLocationId { get; internal set; } = string.Empty;

    /// <summary>Which slot of its carrier it came out of - <c>SLOTID</c>.</summary>
    /// <remarks>
    /// The one field in this package that the catalogue does define, as a <c>U1</c>. A substrate
    /// that never came out of a carrier leaves it at zero, which is why this is a plain byte
    /// rather than something that pretends not to know.
    /// </remarks>
    public byte SlotId { get; internal set; }

    /// <summary>The lot it belongs to.</summary>
    public string LotId { get; internal set; } = string.Empty;

    /// <summary>What it is for, in the fab's own words.</summary>
    public string Usage { get; internal set; } = string.Empty;

    /// <summary>What kind of substrate it is.</summary>
    public string SubstrateType { get; internal set; } = string.Empty;

    /// <summary>The identifier read off the substrate itself, when one was read.</summary>
    /// <remarks>
    /// Not the same thing as <see cref="SubstrateId"/>, and E90 keeps them apart for a reason: one
    /// is what the system calls this wafer and the other is what is scribed on it. A fab finds out
    /// they disagree exactly once, and then wants both.
    /// </remarks>
    public string AcquiredId { get; internal set; } = string.Empty;

    /// <summary>Gets a value indicating whether the machine is finished with this substrate.</summary>
    /// <remarks>
    /// True for every ending E90 names, <see cref="SubstrateProcessingState.Skipped"/> and
    /// <see cref="SubstrateProcessingState.Lost"/> included. The one derived property worth having,
    /// because an application that writes the condition out itself will eventually write it with
    /// four of the five endings in it.
    /// </remarks>
    public bool IsFinished => ProcessingState
        is SubstrateProcessingState.Processed
        or SubstrateProcessingState.Aborted
        or SubstrateProcessingState.Stopped
        or SubstrateProcessingState.Rejected
        or SubstrateProcessingState.Lost
        or SubstrateProcessingState.Skipped;
}

/// <summary>
/// A place a substrate can be, as E90 models one.
/// </summary>
/// <remarks>
/// One substrate at a time - a location is not a carrier, and anything with slots is modelled as
/// several locations. The state follows the substrates rather than being set, so a location cannot
/// be occupied by nothing.
/// </remarks>
public sealed class SubstrateLocation
{
    internal SubstrateLocation(string locationId) => LocationId = locationId;

    /// <summary>What the location is called.</summary>
    public string LocationId { get; }

    /// <summary>Whether something is there.</summary>
    public SubstrateLocationState State { get; internal set; } = SubstrateLocationState.Unoccupied;

    /// <summary>What is there, or <see langword="null"/>.</summary>
    public string? SubstrateId { get; internal set; }
}

/// <summary>The answer to anything that would move a substrate.</summary>
/// <remarks>
/// E90 has no acknowledgement code, because E90 has no messages: it is an object model, and the
/// only things on a wire are its attributes on stream 14 and whatever collection events the
/// equipment defines. So a refusal here is a reason in words rather than a number - and an
/// application that has to answer a host maps it to whatever code that message carries.
/// </remarks>
public sealed class SubstrateResult
{
    /// <summary>Whether it was allowed.</summary>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not.</summary>
    /// <remarks>Every refusal names the state that refused it. Empty when accepted.</remarks>
    public string Reason { get; init; } = string.Empty;

    /// <summary>An accepted answer.</summary>
    internal static SubstrateResult Ok { get; } = new() { IsAccepted = true };

    /// <summary>A refusal.</summary>
    /// <param name="reason">What was wrong, naming the state.</param>
    /// <returns>The result.</returns>
    internal static SubstrateResult Refused(string reason) => new() { IsAccepted = false, Reason = reason };
}

/// <summary>A substrate changed state - E90.</summary>
/// <remarks>
/// <para>
/// E90 defines collection events for these, and this is where they come out. As with E87, this
/// package does not publish them: a <c>CEID</c> belongs to the equipment that defines it, so an
/// application wires these to its own reports in a few lines, and a machine with no GEM layer can
/// still track substrates.
/// </para>
/// <para>
/// Raised after the state has moved and inside the tracker's lock. A handler reading the substrate
/// sees the state the event announces, and must not call back in.
/// </para>
/// </remarks>
public sealed class SubstrateStateChangedEventArgs : EventArgs
{
    /// <summary>The substrate.</summary>
    public required Substrate Substrate { get; init; }

    /// <summary>Which of its state machines moved.</summary>
    public required SubstrateStateMachine StateMachine { get; init; }

    /// <summary>What it was.</summary>
    public required int Previous { get; init; }

    /// <summary>What it is.</summary>
    public required int Current { get; init; }
}

/// <summary>A substrate location filled up or emptied - E90.</summary>
public sealed class SubstrateLocationChangedEventArgs : EventArgs
{
    /// <summary>The location.</summary>
    public required SubstrateLocation Location { get; init; }

    /// <summary>What it was.</summary>
    public required SubstrateLocationState Previous { get; init; }

    /// <summary>What it is.</summary>
    public required SubstrateLocationState Current { get; init; }
}
