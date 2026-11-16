// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Substrates.UnitTests;

/// <summary>
/// E90's state machines, with no link, no transport and no message anywhere near them.
/// </summary>
/// <remarks>
/// <para>
/// E90 has no messages at all, so this is not most of the package's tests - it is nearly all of
/// them. Each one names a rule and shows the refusal.
/// </para>
/// <para>
/// Nothing in this repository can check these transitions the way the catalogue checks a message
/// body: the catalogue contains no mention of a substrate. They are E90 as this library
/// understands it, and the point of writing them out here is that the behaviour is explicit enough
/// for a fab that disagrees to point at.
/// </para>
/// </remarks>
public class SubstrateTrackerTests
{
    private const string Slot = "SLOT-1";
    private const string Chuck = "CHUCK";
    private const string Out = "SLOT-OUT";

    private static SubstrateTracker Loaded(string substrateId = "W01", bool withDestination = false)
    {
        var tracker = new SubstrateTracker();

        tracker.AddLocation(Slot);
        tracker.AddLocation(Chuck);
        tracker.AddLocation(Out);

        Assert.True(tracker.Add(substrateId, Slot, slotId: 1).IsAccepted);

        if (withDestination)
        {
            Assert.True(tracker.Describe(substrateId, destinationLocationId: Out).IsAccepted);
        }

        return tracker;
    }

    // --------------------------------------------------------------- the beginning

    [Fact]
    public void A_substrate_starts_at_its_source_needing_processing()
    {
        var tracker = Loaded();
        var substrate = tracker.FindSubstrate("W01");

        Assert.NotNull(substrate);
        Assert.Equal(SubstrateTransportState.AtSource, substrate.TransportState);
        Assert.Equal(SubstrateProcessingState.NeedsProcessing, substrate.ProcessingState);
        Assert.Equal(Slot, substrate.LocationId);
        Assert.Equal(Slot, substrate.SourceLocationId);
        Assert.Equal(1, substrate.SlotId);
        Assert.False(substrate.IsFinished);
    }

    [Fact]
    public void Adding_a_substrate_fills_the_location_it_is_at()
    {
        var tracker = Loaded();
        var location = tracker.FindLocation(Slot);

        Assert.NotNull(location);
        Assert.Equal(SubstrateLocationState.Occupied, location.State);
        Assert.Equal("W01", location.SubstrateId);
    }

    [Fact]
    public void A_substrate_cannot_be_added_where_there_is_no_location()
    {
        var tracker = new SubstrateTracker();

        var refusal = tracker.Add("W01", "NOWHERE");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("no location 'NOWHERE'", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_substrate_cannot_be_added_where_one_already_is()
    {
        var tracker = Loaded();

        var refusal = tracker.Add("W02", Slot);

        // One substrate per location: that is what separates a location from a carrier.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("already holds substrate 'W01'", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_substrate_cannot_be_added_twice()
    {
        var tracker = Loaded();

        var refusal = tracker.Add("W01", Chuck);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("already being tracked", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_location_declared_twice_is_the_same_location()
    {
        var tracker = Loaded();

        var again = tracker.AddLocation(Slot);

        Assert.Equal(SubstrateLocationState.Occupied, again.State);
        Assert.Equal(3, tracker.Locations.Count);
    }

    // ------------------------------------------------------------------ the moving

    [Fact]
    public void Moving_a_substrate_empties_where_it_was_and_fills_where_it_went()
    {
        var tracker = Loaded();

        Assert.True(tracker.MoveTo("W01", Chuck).IsAccepted);

        var from = tracker.FindLocation(Slot);
        var to = tracker.FindLocation(Chuck);

        // One step, one lock: there is no moment where the wafer is in both places or neither.
        Assert.NotNull(from);
        Assert.NotNull(to);
        Assert.Equal(SubstrateLocationState.Unoccupied, from.State);
        Assert.Null(from.SubstrateId);
        Assert.Equal(SubstrateLocationState.Occupied, to.State);
        Assert.Equal("W01", to.SubstrateId);
        Assert.Equal(Chuck, tracker.FindSubstrate("W01")?.LocationId);
    }

    [Fact]
    public void Moving_somewhere_that_is_neither_source_nor_destination_is_at_work()
    {
        var tracker = Loaded(withDestination: true);

        tracker.MoveTo("W01", Chuck);

        Assert.Equal(SubstrateTransportState.AtWork, tracker.FindSubstrate("W01")?.TransportState);
    }

    [Fact]
    public void Moving_back_to_its_source_is_at_its_source_again()
    {
        var tracker = Loaded(withDestination: true);

        tracker.MoveTo("W01", Chuck);

        Assert.True(tracker.MoveTo("W01", Slot).IsAccepted);

        // A substrate that could not be processed goes back in the carrier it came out of, which
        // is why this transition exists at all.
        Assert.Equal(SubstrateTransportState.AtSource, tracker.FindSubstrate("W01")?.TransportState);
    }

    [Fact]
    public void Moving_to_its_destination_is_at_its_destination()
    {
        var tracker = Loaded(withDestination: true);

        tracker.MoveTo("W01", Chuck);

        Assert.True(tracker.MoveTo("W01", Out).IsAccepted);
        Assert.Equal(SubstrateTransportState.AtDestination, tracker.FindSubstrate("W01")?.TransportState);
    }

    [Fact]
    public void A_substrate_with_no_destination_never_reaches_one()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Out);

        // Nobody has said where it is going, so arriving at an arbitrary location is work rather
        // than an ending. A machine that called this AtDestination would be deciding for the host.
        Assert.Equal(SubstrateTransportState.AtWork, tracker.FindSubstrate("W01")?.TransportState);
    }

    [Fact]
    public void A_substrate_at_its_destination_cannot_be_moved_again()
    {
        var tracker = Loaded(withDestination: true);

        tracker.MoveTo("W01", Out);

        var refusal = tracker.MoveTo("W01", Chuck);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("let go of it", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_substrate_cannot_move_to_an_occupied_location()
    {
        var tracker = Loaded();

        tracker.Add("W02", Chuck);

        var refusal = tracker.MoveTo("W01", Chuck);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("already holds substrate 'W02'", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(Slot, tracker.FindSubstrate("W01")?.LocationId);
    }

    [Fact]
    public void A_substrate_cannot_move_where_there_is_no_location()
    {
        var tracker = Loaded();

        var refusal = tracker.MoveTo("W01", "NOWHERE");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("no location", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_a_substrate_to_where_it_already_is_changes_nothing_and_is_not_an_error()
    {
        var tracker = Loaded();
        var moves = 0;

        tracker.LocationChanged += (_, _) => moves++;

        Assert.True(tracker.MoveTo("W01", Slot).IsAccepted);
        Assert.Equal(0, moves);
        Assert.Equal(SubstrateLocationState.Occupied, tracker.FindLocation(Slot)?.State);
    }

    // --------------------------------------------------------------- the processing

    [Fact]
    public void Work_can_only_start_on_a_substrate_that_is_at_work()
    {
        var tracker = Loaded();

        var tooEarly = tracker.BeginProcessing("W01");

        // A substrate still in its carrier is not being processed, whatever a recipe thinks.
        Assert.False(tooEarly.IsAccepted);
        Assert.Contains("AtSource", tooEarly.Reason, StringComparison.Ordinal);
        Assert.Contains("AtWork", tooEarly.Reason, StringComparison.Ordinal);

        tracker.MoveTo("W01", Chuck);

        Assert.True(tracker.BeginProcessing("W01").IsAccepted);
        Assert.Equal(SubstrateProcessingState.InProcess, tracker.FindSubstrate("W01")?.ProcessingState);
    }

    [Fact]
    public void A_substrate_in_process_cannot_be_moved()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");

        var refusal = tracker.MoveTo("W01", Slot);

        // A wafer on a chuck with the recipe running is not available to a robot.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("InProcess", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Work_cannot_start_twice()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");

        var refusal = tracker.BeginProcessing("W01");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("InProcess", refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SubstrateProcessingState.Processed)]
    [InlineData(SubstrateProcessingState.Aborted)]
    [InlineData(SubstrateProcessingState.Stopped)]
    [InlineData(SubstrateProcessingState.Rejected)]
    public void Work_ends_as_any_of_the_four_endings_work_can_have(SubstrateProcessingState ending)
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");

        Assert.True(tracker.EndProcessing("W01", ending).IsAccepted);

        var substrate = tracker.FindSubstrate("W01");

        Assert.NotNull(substrate);
        Assert.Equal(ending, substrate.ProcessingState);
        Assert.True(substrate.IsFinished);
    }

    [Theory]
    [InlineData(SubstrateProcessingState.Skipped)]
    [InlineData(SubstrateProcessingState.Lost)]
    [InlineData(SubstrateProcessingState.NeedsProcessing)]
    [InlineData(SubstrateProcessingState.InProcess)]
    public void An_ending_that_is_not_an_ending_of_work_is_refused_at_the_call(SubstrateProcessingState notAnEnding)
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");

        // Thrown rather than refused, because this one is the caller's mistake rather than the
        // machine's state: Skip and Lose are their own methods, and the other two are not endings.
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => tracker.EndProcessing("W01", notAnEnding));

        Assert.Equal("state", thrown.ParamName);
    }

    [Fact]
    public void Work_cannot_end_unless_it_started()
    {
        var tracker = Loaded();

        var refusal = tracker.EndProcessing("W01", SubstrateProcessingState.Processed);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("NeedsProcessing", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("InProcess", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_processed_substrate_cannot_be_processed_again()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");
        tracker.EndProcessing("W01", SubstrateProcessingState.Processed);

        var refusal = tracker.BeginProcessing("W01");

        // A second pass is a new substrate as far as E90 is concerned.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("Processed", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Skipping_is_only_for_a_substrate_that_still_needs_processing()
    {
        var tracker = Loaded();

        Assert.True(tracker.Skip("W01").IsAccepted);
        Assert.Equal(SubstrateProcessingState.Skipped, tracker.FindSubstrate("W01")?.ProcessingState);

        // A second wafer, taken out of its own source and put to work.
        Assert.True(tracker.Add("W02", Out).IsAccepted);
        Assert.True(tracker.MoveTo("W02", Chuck).IsAccepted);
        Assert.True(tracker.BeginProcessing("W02").IsAccepted);

        var refusal = tracker.Skip("W02");

        // A substrate being worked on is stopped rather than skipped, and the difference matters
        // to whoever reads the lot afterwards.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("InProcess", refusal.Reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- losing track

    [Fact]
    public void Losing_a_substrate_empties_the_location_it_was_at()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);

        Assert.True(tracker.Lose("W01").IsAccepted);

        var chuck = tracker.FindLocation(Chuck);
        var substrate = tracker.FindSubstrate("W01");

        // A location holding a substrate nobody can find is worse than an empty one, because a
        // robot will believe it.
        Assert.NotNull(chuck);
        Assert.Equal(SubstrateLocationState.Unoccupied, chuck.State);
        Assert.NotNull(substrate);
        Assert.Equal(SubstrateProcessingState.Lost, substrate.ProcessingState);
        Assert.Equal(string.Empty, substrate.LocationId);
        Assert.True(substrate.IsFinished);
    }

    [Fact]
    public void A_lost_substrate_cannot_move()
    {
        var tracker = Loaded();

        tracker.Lose("W01");

        var refusal = tracker.MoveTo("W01", Chuck);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("Lost", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Losing_is_reachable_from_every_state_including_the_endings()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");
        tracker.EndProcessing("W01", SubstrateProcessingState.Processed);

        // Noticing afterwards that a processed wafer is not where it should be is exactly when
        // this happens.
        Assert.True(tracker.Lose("W01").IsAccepted);
        Assert.Equal(SubstrateProcessingState.Lost, tracker.FindSubstrate("W01")?.ProcessingState);
    }

    [Fact]
    public void Losing_a_substrate_twice_is_not_an_error()
    {
        var tracker = Loaded();

        tracker.Lose("W01");

        Assert.True(tracker.Lose("W01").IsAccepted);
    }

    // --------------------------------------------------------------- the ending

    [Fact]
    public void A_substrate_can_only_be_forgotten_once_it_is_gone()
    {
        var tracker = Loaded(withDestination: true);

        var tooEarly = tracker.Remove("W01");

        // Forgetting a substrate that is still on a chuck is how a machine ends up with a wafer
        // nobody is tracking.
        Assert.False(tooEarly.IsAccepted);
        Assert.Contains("AtSource", tooEarly.Reason, StringComparison.Ordinal);

        tracker.MoveTo("W01", Out);

        Assert.True(tracker.Remove("W01").IsAccepted);
        Assert.Null(tracker.FindSubstrate("W01"));
        Assert.Equal(SubstrateLocationState.Unoccupied, tracker.FindLocation(Out)?.State);
    }

    [Fact]
    public void A_lost_substrate_can_be_forgotten_wherever_it_was()
    {
        var tracker = Loaded();

        tracker.Lose("W01");

        Assert.True(tracker.Remove("W01").IsAccepted);
        Assert.Null(tracker.FindSubstrate("W01"));
    }

    // ------------------------------------------------------------------- labels

    [Fact]
    public void Labels_can_be_set_at_any_time_because_they_are_not_states()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");

        // A host that learns the lot after the wafer has come out of the carrier is the normal
        // case, not a protocol error - so this works even mid-process.
        Assert.True(tracker.Describe(
            "W01",
            lotId: "LOT-7",
            usage: "Production",
            substrateType: "Wafer",
            acquiredId: "SCRIBE-123").IsAccepted);

        var substrate = tracker.FindSubstrate("W01");

        Assert.NotNull(substrate);
        Assert.Equal("LOT-7", substrate.LotId);
        Assert.Equal("Production", substrate.Usage);
        Assert.Equal("Wafer", substrate.SubstrateType);
        Assert.Equal("SCRIBE-123", substrate.AcquiredId);
    }

    [Fact]
    public void A_destination_that_names_no_location_is_refused()
    {
        var tracker = Loaded();

        var refusal = tracker.Describe("W01", destinationLocationId: "NOWHERE");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("no location 'NOWHERE'", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(string.Empty, tracker.FindSubstrate("W01")?.DestinationLocationId);
    }

    [Fact]
    public void Describing_a_substrate_that_is_not_there_is_refused()
    {
        var tracker = Loaded();

        var refusal = tracker.Describe("W99", lotId: "LOT-7");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("no substrate 'W99'", refusal.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------- refusals and events

    [Fact]
    public void Every_refusal_this_package_produces_says_something()
    {
        var tracker = Loaded();

        List<SubstrateResult> refusals =
        [
            tracker.Add("W01", Slot),
            tracker.Add("W02", "NOWHERE"),
            tracker.MoveTo("W99", Chuck),
            tracker.MoveTo("W01", "NOWHERE"),
            tracker.BeginProcessing("W01"),
            tracker.EndProcessing("W01", SubstrateProcessingState.Processed),
            tracker.Remove("W01"),
            tracker.Describe("W01", destinationLocationId: "NOWHERE"),
        ];

        Assert.All(refusals, refusal =>
        {
            Assert.False(refusal.IsAccepted);
            Assert.NotEqual(string.Empty, refusal.Reason);
        });
    }

    [Fact]
    public void A_state_change_is_announced_after_the_state_has_moved()
    {
        var tracker = Loaded();

        SubstrateProcessingState? seen = null;
        SubstrateStateChangedEventArgs? args = null;

        tracker.SubstrateStateChanged += (_, e) =>
        {
            if (e.StateMachine is SubstrateStateMachine.ProcessingState)
            {
                args = e;
                seen = e.Substrate.ProcessingState;
            }
        };

        tracker.MoveTo("W01", Chuck);
        tracker.BeginProcessing("W01");

        Assert.NotNull(args);
        Assert.Equal((int)SubstrateProcessingState.NeedsProcessing, args.Previous);
        Assert.Equal((int)SubstrateProcessingState.InProcess, args.Current);

        // The assertion that matters: a handler reading the substrate sees the state the event is
        // announcing, not the one before it.
        Assert.Equal(SubstrateProcessingState.InProcess, seen);
    }

    [Fact]
    public void A_location_filling_and_emptying_is_announced()
    {
        var tracker = Loaded();

        List<(string Location, SubstrateLocationState State)> changes = [];

        tracker.LocationChanged += (_, args) => changes.Add((args.Location.LocationId, args.Current));

        tracker.MoveTo("W01", Chuck);

        // Emptied first, then filled: a wafer leaves before it arrives.
        Assert.Equal(2, changes.Count);
        Assert.Equal((Slot, SubstrateLocationState.Unoccupied), changes[0]);
        Assert.Equal((Chuck, SubstrateLocationState.Occupied), changes[1]);
    }

    [Fact]
    public void A_transport_state_that_does_not_move_is_not_announced()
    {
        var tracker = Loaded();

        tracker.MoveTo("W01", Chuck);

        var moves = 0;

        tracker.SubstrateStateChanged += (_, args) =>
        {
            if (args.StateMachine is SubstrateStateMachine.TransportState)
            {
                moves++;
            }
        };

        // Another work location: still AtWork, so nothing to announce.
        tracker.AddLocation("ALIGNER");
        tracker.MoveTo("W01", "ALIGNER");

        Assert.Equal(0, moves);
        Assert.Equal(SubstrateTransportState.AtWork, tracker.FindSubstrate("W01")?.TransportState);
    }

    [Fact]
    public void A_whole_wafer_goes_through_in_order()
    {
        var tracker = Loaded(withDestination: true);

        List<string> story = [];

        tracker.SubstrateStateChanged += (_, args) => story.Add(args.StateMachine switch
        {
            SubstrateStateMachine.TransportState => ((SubstrateTransportState)args.Current).ToString(),
            _ => ((SubstrateProcessingState)args.Current).ToString(),
        });

        Assert.True(tracker.MoveTo("W01", Chuck).IsAccepted);
        Assert.True(tracker.BeginProcessing("W01").IsAccepted);
        Assert.True(tracker.EndProcessing("W01", SubstrateProcessingState.Processed).IsAccepted);
        Assert.True(tracker.MoveTo("W01", Out).IsAccepted);
        Assert.True(tracker.Remove("W01").IsAccepted);

        Assert.Equal(["AtWork", "InProcess", "Processed", "AtDestination"], story);
        Assert.Empty(tracker.Substrates);
        Assert.All(tracker.Locations, location => Assert.Equal(SubstrateLocationState.Unoccupied, location.State));
    }
}
