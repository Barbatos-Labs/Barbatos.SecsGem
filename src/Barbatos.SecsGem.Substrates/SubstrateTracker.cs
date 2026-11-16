// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;

namespace Barbatos.SecsGem.Substrates;

/// <summary>
/// The substrates a machine is tracking, and E90's rules about what may happen to them.
/// </summary>
/// <remarks>
/// <para>
/// The whole package, and it has no connection, no transport and no messages - because E90 has
/// none either. A substrate reaches a host as an object with attributes on stream 14 and as
/// whatever collection events the equipment defines; what this class does is keep the states true
/// while that happens.
/// </para>
/// <para>
/// <strong>The rules.</strong> A substrate cannot be processed unless it is
/// <see cref="SubstrateTransportState.AtWork"/>; it cannot be processed twice; it cannot move to a
/// location that already holds something; it cannot move once it has been handed back at its
/// destination; and a substrate that has been lost cannot do anything at all. Each of those is a
/// test that names the rule and shows the refusal.
/// </para>
/// <para>
/// <strong>Occupancy is kept, not set.</strong> Moving a substrate empties the location it left
/// and fills the one it arrived at, in one step under one lock, so there is no moment where a
/// wafer is in two places or in none. A location state nobody can assign is a location state that
/// cannot disagree with the substrates.
/// </para>
/// <para>
/// Thread safety: one lock around every transition. The events are raised inside it, after the
/// state has moved, so a handler must queue rather than call back in.
/// </para>
/// </remarks>
public sealed class SubstrateTracker
{
    private readonly Dictionary<string, Substrate> _substrates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubstrateLocation> _locations = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    /// <summary>Raised after one of a substrate's state machines moves.</summary>
    /// <remarks>Raised inside the tracker's lock. Queue the work; do not call back in.</remarks>
    public event EventHandler<SubstrateStateChangedEventArgs>? SubstrateStateChanged;

    /// <summary>Raised after a location fills up or empties.</summary>
    public event EventHandler<SubstrateLocationChangedEventArgs>? LocationChanged;

    /// <summary>Gets the substrates being tracked.</summary>
    public IReadOnlyCollection<Substrate> Substrates
    {
        get
        {
            lock (_gate)
            {
                return [.. _substrates.Values];
            }
        }
    }

    /// <summary>Gets the locations a substrate can be at.</summary>
    public IReadOnlyCollection<SubstrateLocation> Locations
    {
        get
        {
            lock (_gate)
            {
                return [.. _locations.Values];
            }
        }
    }

    /// <summary>Declares a place a substrate can be.</summary>
    /// <param name="locationId">What to call it.</param>
    /// <returns>The location.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="locationId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Locations are the machine's own geography - a slot, an aligner, a chuck - so they are
    /// declared rather than created by a message. Declaring one twice returns the same location and
    /// resets nothing.
    /// </remarks>
    public SubstrateLocation AddLocation(string locationId)
    {
        ArgumentNullException.ThrowIfNull(locationId);

        lock (_gate)
        {
            if (_locations.TryGetValue(locationId, out var existing))
            {
                return existing;
            }

            var location = new SubstrateLocation(locationId);
            _locations[locationId] = location;

            return location;
        }
    }

    /// <summary>Finds a substrate.</summary>
    /// <param name="substrateId">Which one.</param>
    /// <returns>The substrate, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    public Substrate? FindSubstrate(string substrateId)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        lock (_gate)
        {
            return _substrates.GetValueOrDefault(substrateId);
        }
    }

    /// <summary>Finds a location.</summary>
    /// <param name="locationId">Which one.</param>
    /// <returns>The location, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="locationId"/> is <see langword="null"/>.</exception>
    public SubstrateLocation? FindLocation(string locationId)
    {
        ArgumentNullException.ThrowIfNull(locationId);

        lock (_gate)
        {
            return _locations.GetValueOrDefault(locationId);
        }
    }

    /// <summary>A substrate has been found at its source - the start of everything E90 tracks.</summary>
    /// <param name="substrateId">What to call it.</param>
    /// <param name="sourceLocationId">Where it is, which must be a declared and empty location.</param>
    /// <param name="slotId">Which slot of its carrier it is in, or zero.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// This is what a mapped carrier turns into: one of these per occupied slot. A substrate
    /// starts <see cref="SubstrateTransportState.AtSource"/> and
    /// <see cref="SubstrateProcessingState.NeedsProcessing"/>, which are the only states E90 lets
    /// one begin in.
    /// </remarks>
    public SubstrateResult Add(string substrateId, string sourceLocationId, byte slotId = 0)
    {
        ArgumentNullException.ThrowIfNull(substrateId);
        ArgumentNullException.ThrowIfNull(sourceLocationId);

        lock (_gate)
        {
            if (_substrates.ContainsKey(substrateId))
            {
                return SubstrateResult.Refused($"substrate '{substrateId}' is already being tracked.");
            }

            if (!_locations.TryGetValue(sourceLocationId, out var location))
            {
                return SubstrateResult.Refused($"there is no location '{sourceLocationId}'.");
            }

            if (location.State is SubstrateLocationState.Occupied)
            {
                return SubstrateResult.Refused(
                    $"location '{sourceLocationId}' already holds substrate '{location.SubstrateId}'.");
            }

            var substrate = new Substrate(substrateId)
            {
                LocationId = sourceLocationId,
                SourceLocationId = sourceLocationId,
                SlotId = slotId,
            };

            _substrates[substrateId] = substrate;

            Fill(location, substrateId);

            return SubstrateResult.Ok;
        }
    }

    /// <summary>Sets the labels a host owns: the lot, the usage, the type, the destination, the acquired identifier.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <param name="lotId">The lot, or <see langword="null"/> to leave it.</param>
    /// <param name="usage">What it is for, or <see langword="null"/> to leave it.</param>
    /// <param name="substrateType">What kind it is, or <see langword="null"/> to leave it.</param>
    /// <param name="destinationLocationId">Where it is going, or <see langword="null"/> to leave it.</param>
    /// <param name="acquiredId">What was read off the substrate, or <see langword="null"/> to leave it.</param>
    /// <returns>Whether the substrate is there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Labels rather than states, which is why they are settable at all and why they are settable
    /// at any time: a host that learns the lot after the wafer has come out of the carrier is the
    /// normal case, not a protocol error. A destination that names no declared location is refused,
    /// because a substrate cannot be sent somewhere that does not exist.
    /// </remarks>
    public SubstrateResult Describe(
        string substrateId,
        string? lotId = null,
        string? usage = null,
        string? substrateType = null,
        string? destinationLocationId = null,
        string? acquiredId = null)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (destinationLocationId is not null
                && destinationLocationId.Length > 0
                && !_locations.ContainsKey(destinationLocationId))
            {
                return SubstrateResult.Refused($"there is no location '{destinationLocationId}'.");
            }

            substrate.LotId = lotId ?? substrate.LotId;
            substrate.Usage = usage ?? substrate.Usage;
            substrate.SubstrateType = substrateType ?? substrate.SubstrateType;
            substrate.DestinationLocationId = destinationLocationId ?? substrate.DestinationLocationId;
            substrate.AcquiredId = acquiredId ?? substrate.AcquiredId;

            return SubstrateResult.Ok;
        }
    }

    /// <summary>Moves a substrate to a location.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <param name="locationId">Where to.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The transport state follows from where it went: its source makes it
    /// <see cref="SubstrateTransportState.AtSource"/>, its destination makes it
    /// <see cref="SubstrateTransportState.AtDestination"/>, and anywhere else is
    /// <see cref="SubstrateTransportState.AtWork"/>. That is the whole of E90's transport state
    /// machine, and deriving it from the geography beats asking a caller to keep the two in step.
    /// </para>
    /// <para>
    /// Refused while <see cref="SubstrateProcessingState.InProcess"/> - a wafer on a chuck with the
    /// recipe running is not available to a robot - and refused once the substrate is
    /// <see cref="SubstrateTransportState.AtDestination"/>, which is the machine letting go of it.
    /// </para>
    /// </remarks>
    public SubstrateResult MoveTo(string substrateId, string locationId)
    {
        ArgumentNullException.ThrowIfNull(substrateId);
        ArgumentNullException.ThrowIfNull(locationId);

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (substrate.ProcessingState is SubstrateProcessingState.Lost)
            {
                return SubstrateResult.Refused($"substrate '{substrateId}' is {SubstrateProcessingState.Lost}.");
            }

            if (substrate.ProcessingState is SubstrateProcessingState.InProcess)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {SubstrateProcessingState.InProcess} and cannot be moved.");
            }

            if (substrate.TransportState is SubstrateTransportState.AtDestination)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {SubstrateTransportState.AtDestination}; the machine has let go of it.");
            }

            if (!_locations.TryGetValue(locationId, out var destination))
            {
                return SubstrateResult.Refused($"there is no location '{locationId}'.");
            }

            if (string.Equals(substrate.LocationId, locationId, StringComparison.Ordinal))
            {
                return SubstrateResult.Ok;
            }

            if (destination.State is SubstrateLocationState.Occupied)
            {
                return SubstrateResult.Refused(
                    $"location '{locationId}' already holds substrate '{destination.SubstrateId}'.");
            }

            if (_locations.TryGetValue(substrate.LocationId, out var leaving))
            {
                Empty(leaving);
            }

            substrate.LocationId = locationId;
            Fill(destination, substrateId);

            MoveTransport(substrate, TransportStateFor(substrate, locationId));

            return SubstrateResult.Ok;
        }
    }

    /// <summary>The machine has started work on a substrate.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Allowed only from <see cref="SubstrateProcessingState.NeedsProcessing"/> and only while
    /// <see cref="SubstrateTransportState.AtWork"/>. A substrate still in its carrier is not being
    /// processed, whatever a recipe thinks, and a substrate that has already been through is not
    /// going through again - a second pass is a new substrate as far as E90 is concerned.
    /// </remarks>
    public SubstrateResult BeginProcessing(string substrateId)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (substrate.ProcessingState is not SubstrateProcessingState.NeedsProcessing)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {substrate.ProcessingState} and only one that "
                    + $"{SubstrateProcessingState.NeedsProcessing} can start.");
            }

            if (substrate.TransportState is not SubstrateTransportState.AtWork)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {substrate.TransportState} and work can only start on one that is "
                    + $"{SubstrateTransportState.AtWork}.");
            }

            MoveProcessing(substrate, SubstrateProcessingState.InProcess);

            return SubstrateResult.Ok;
        }
    }

    /// <summary>The machine has finished work on a substrate.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <param name="state">How it ended.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is not one of the four endings work can have.</exception>
    /// <remarks>
    /// The four endings work can have are <see cref="SubstrateProcessingState.Processed"/>,
    /// <see cref="SubstrateProcessingState.Aborted"/>, <see cref="SubstrateProcessingState.Stopped"/>
    /// and <see cref="SubstrateProcessingState.Rejected"/>. <see cref="Skip"/> and
    /// <see cref="Lose"/> are the other two endings and are not reachable from here, because
    /// neither of them is a way for work to finish.
    /// </remarks>
    public SubstrateResult EndProcessing(string substrateId, SubstrateProcessingState state)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        if (state is not (SubstrateProcessingState.Processed
            or SubstrateProcessingState.Aborted
            or SubstrateProcessingState.Stopped
            or SubstrateProcessingState.Rejected))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "work ends as Processed, Aborted, Stopped or Rejected; Skipped and Lost are not endings of work.");
        }

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (substrate.ProcessingState is not SubstrateProcessingState.InProcess)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {substrate.ProcessingState} and only one that is "
                    + $"{SubstrateProcessingState.InProcess} can finish.");
            }

            MoveProcessing(substrate, state);

            return SubstrateResult.Ok;
        }
    }

    /// <summary>A substrate is not to be processed after all.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only from <see cref="SubstrateProcessingState.NeedsProcessing"/>: a substrate that is being
    /// worked on is stopped rather than skipped, and the difference matters to whoever reads the
    /// lot afterwards.
    /// </remarks>
    public SubstrateResult Skip(string substrateId)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (substrate.ProcessingState is not SubstrateProcessingState.NeedsProcessing)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {substrate.ProcessingState}; only one that "
                    + $"{SubstrateProcessingState.NeedsProcessing} can be skipped.");
            }

            MoveProcessing(substrate, SubstrateProcessingState.Skipped);

            return SubstrateResult.Ok;
        }
    }

    /// <summary>The machine does not know where a substrate is.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Reachable from every state, including the endings, because losing track of a wafer is not
    /// something a machine plans and can be noticed at any time. The location it was at is emptied:
    /// a location that holds a substrate nobody can find is worse than an empty one, because a
    /// robot will believe it.
    /// </remarks>
    public SubstrateResult Lose(string substrateId)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (substrate.ProcessingState is SubstrateProcessingState.Lost)
            {
                return SubstrateResult.Ok;
            }

            if (_locations.TryGetValue(substrate.LocationId, out var location)
                && string.Equals(location.SubstrateId, substrateId, StringComparison.Ordinal))
            {
                Empty(location);
            }

            substrate.LocationId = string.Empty;

            MoveProcessing(substrate, SubstrateProcessingState.Lost);

            return SubstrateResult.Ok;
        }
    }

    /// <summary>Forgets a substrate the machine has handed back.</summary>
    /// <param name="substrateId">Which substrate.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="substrateId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A machine does not track a wafer for ever, and E90 ends a substrate's life when it leaves.
    /// Allowed once it is <see cref="SubstrateTransportState.AtDestination"/> or
    /// <see cref="SubstrateProcessingState.Lost"/>, and refused otherwise - forgetting a substrate
    /// that is still on a chuck is how a machine ends up with a wafer nobody is tracking.
    /// </remarks>
    public SubstrateResult Remove(string substrateId)
    {
        ArgumentNullException.ThrowIfNull(substrateId);

        lock (_gate)
        {
            if (!TryFind(substrateId, out var substrate, out var refusal))
            {
                return refusal;
            }

            if (substrate.TransportState is not SubstrateTransportState.AtDestination
                && substrate.ProcessingState is not SubstrateProcessingState.Lost)
            {
                return SubstrateResult.Refused(
                    $"substrate '{substrateId}' is {substrate.TransportState} and "
                    + $"{substrate.ProcessingState}; only one at its destination or lost can be forgotten.");
            }

            if (_locations.TryGetValue(substrate.LocationId, out var location)
                && string.Equals(location.SubstrateId, substrateId, StringComparison.Ordinal))
            {
                Empty(location);
            }

            _substrates.Remove(substrateId);

            return SubstrateResult.Ok;
        }
    }

    // ------------------------------------------------------------------- the pieces

    /// <summary>Works out the transport state from where the substrate has arrived.</summary>
    /// <remarks>
    /// Its source is <see cref="SubstrateTransportState.AtSource"/>, its destination is
    /// <see cref="SubstrateTransportState.AtDestination"/>, and everywhere else is work. A
    /// substrate with no destination set can therefore never reach
    /// <see cref="SubstrateTransportState.AtDestination"/>, which is correct: nobody has said where
    /// it is going.
    /// </remarks>
    private static SubstrateTransportState TransportStateFor(Substrate substrate, string locationId)
    {
        if (substrate.DestinationLocationId.Length > 0
            && string.Equals(substrate.DestinationLocationId, locationId, StringComparison.Ordinal))
        {
            return SubstrateTransportState.AtDestination;
        }

        return string.Equals(substrate.SourceLocationId, locationId, StringComparison.Ordinal)
            ? SubstrateTransportState.AtSource
            : SubstrateTransportState.AtWork;
    }

    private bool TryFind(
        string substrateId,
        [NotNullWhen(true)] out Substrate? substrate,
        out SubstrateResult refusal)
    {
        if (!_substrates.TryGetValue(substrateId, out substrate))
        {
            refusal = SubstrateResult.Refused($"there is no substrate '{substrateId}'.");

            return false;
        }

        refusal = SubstrateResult.Ok;

        return true;
    }

    private void Fill(SubstrateLocation location, string substrateId)
    {
        var previous = location.State;

        location.State = SubstrateLocationState.Occupied;
        location.SubstrateId = substrateId;

        if (previous != SubstrateLocationState.Occupied)
        {
            Raise(location, previous, SubstrateLocationState.Occupied);
        }
    }

    private void Empty(SubstrateLocation location)
    {
        var previous = location.State;

        location.State = SubstrateLocationState.Unoccupied;
        location.SubstrateId = null;

        if (previous != SubstrateLocationState.Unoccupied)
        {
            Raise(location, previous, SubstrateLocationState.Unoccupied);
        }
    }

    private void MoveTransport(Substrate substrate, SubstrateTransportState next)
    {
        if (substrate.TransportState == next)
        {
            return;
        }

        var previous = substrate.TransportState;
        substrate.TransportState = next;

        Raise(substrate, SubstrateStateMachine.TransportState, (int)previous, (int)next);
    }

    private void MoveProcessing(Substrate substrate, SubstrateProcessingState next)
    {
        if (substrate.ProcessingState == next)
        {
            return;
        }

        var previous = substrate.ProcessingState;
        substrate.ProcessingState = next;

        Raise(substrate, SubstrateStateMachine.ProcessingState, (int)previous, (int)next);
    }

    /// <summary>Raises a substrate transition, after the substrate has already moved.</summary>
    /// <remarks>After, so a handler reading the substrate sees the state the event announces.</remarks>
    private void Raise(Substrate substrate, SubstrateStateMachine machine, int previous, int current)
        => SubstrateStateChanged?.Invoke(this, new SubstrateStateChangedEventArgs
        {
            Substrate = substrate,
            StateMachine = machine,
            Previous = previous,
            Current = current,
        });

    private void Raise(SubstrateLocation location, SubstrateLocationState previous, SubstrateLocationState current)
        => LocationChanged?.Invoke(this, new SubstrateLocationChangedEventArgs
        {
            Location = location,
            Previous = previous,
            Current = current,
        });
}
