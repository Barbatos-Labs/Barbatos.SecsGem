// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Substrates.UnitTests;

/// <summary>
/// A tracker that outlives the process, and the one place in this stack where a snapshot keeps
/// something the machine observed.
/// </summary>
/// <remarks>
/// Everywhere else the rule is that a host's instructions survive a restart and the machine's
/// observations do not: a carrier's label is read again, a load port's association is sensed
/// again. A wafer's position cannot be. Most tools have no way to find out what is on a chuck
/// without moving it, and a tracker that came back empty would have lost twenty-five wafers
/// somewhere inside a machine with nothing recording that they exist. So the location comes back,
/// and that exception is tested here rather than left to be discovered.
/// </remarks>
public class SubstrateStateTests
{
    private const string Chuck = "CHUCK";
    private const string Slot = "CAR-001-SLOT-01";
    private const string Wafer = "CAR-001-01";
    private const string Other = "CAR-001-02";

    private static SubstrateTracker Build(bool declare = true)
    {
        var tracker = new SubstrateTracker();

        if (declare)
        {
            tracker.AddLocation(Chuck);
            tracker.AddLocation(Slot);
        }

        return tracker;
    }

    private static SubstrateTracker WithAWaferOnTheChuck()
    {
        var tracker = Build();

        Assert.True(tracker.Add(Wafer, Slot, slotId: 1).IsAccepted);
        Assert.True(tracker.Describe(Wafer, lotId: "LOT-7", substrateType: "300mm", destinationLocationId: Slot).IsAccepted);
        Assert.True(tracker.MoveTo(Wafer, Chuck).IsAccepted);
        Assert.True(tracker.BeginProcessing(Wafer).IsAccepted);

        return tracker;
    }

    [Fact]
    public void A_wafer_comes_back_where_it_was()
    {
        var tracker = WithAWaferOnTheChuck();
        var restarted = Build();

        Assert.True(restarted.Restore(tracker.Capture()).IsAccepted);

        var wafer = restarted.FindSubstrate(Wafer)!;

        // The exception, and the reason for it: nothing here can look inside the chamber, so a
        // tracker that forgot would lose the wafer rather than re-sense it.
        Assert.Equal(Chuck, wafer.LocationId);
        Assert.Equal(SubstrateTransportState.AtWork, wafer.TransportState);
        Assert.Equal(Slot, wafer.SourceLocationId);
        Assert.Equal(Slot, wafer.DestinationLocationId);
        Assert.Equal("LOT-7", wafer.LotId);
        Assert.Equal("300mm", wafer.SubstrateType);
        Assert.Equal((byte)1, wafer.SlotId);
    }

    [Fact]
    public void Work_that_was_happening_when_the_power_went_did_not_finish()
    {
        var tracker = WithAWaferOnTheChuck();

        Assert.Equal(SubstrateProcessingState.InProcess, tracker.FindSubstrate(Wafer)!.ProcessingState);

        var restarted = Build();
        var report = restarted.Restore(tracker.Capture());

        // E90 ends work only from InProcess, so a wafer restored as in process is one nothing is
        // ever coming back for: the recipe that would have ended it stopped running before the
        // restart. Stopped is E90's own word for work that ended early.
        Assert.Equal(SubstrateProcessingState.Stopped, restarted.FindSubstrate(Wafer)!.ProcessingState);
        Assert.Contains("did not finish", report.Adjusted[Wafer], StringComparison.Ordinal);
    }

    [Fact]
    public void A_wafer_that_was_already_done_keeps_its_record()
    {
        var tracker = WithAWaferOnTheChuck();

        Assert.True(tracker.EndProcessing(Wafer, SubstrateProcessingState.Processed).IsAccepted);
        Assert.Equal(SubstrateProcessingState.Processed, tracker.FindSubstrate(Wafer)!.ProcessingState);

        var restarted = Build();
        var report = restarted.Restore(tracker.Capture());

        Assert.Equal(SubstrateProcessingState.Processed, restarted.FindSubstrate(Wafer)!.ProcessingState);
        Assert.Empty(report.Adjusted);
    }

    [Fact]
    public void A_wafer_nobody_got_to_still_needs_processing()
    {
        var tracker = Build();

        Assert.True(tracker.Add(Wafer, Slot, slotId: 1).IsAccepted);

        var restarted = Build();

        Assert.True(restarted.Restore(tracker.Capture()).IsAccepted);
        Assert.Equal(SubstrateProcessingState.NeedsProcessing, restarted.FindSubstrate(Wafer)!.ProcessingState);
        Assert.Equal(SubstrateTransportState.AtSource, restarted.FindSubstrate(Wafer)!.TransportState);
    }

    [Fact]
    public void The_locations_come_back_and_their_occupancy_is_worked_out_again()
    {
        var tracker = WithAWaferOnTheChuck();
        var restarted = Build();

        Assert.True(restarted.Restore(tracker.Capture()).IsAccepted);

        var chuck = restarted.FindLocation(Chuck)!;
        var slot = restarted.FindLocation(Slot)!;

        // Worked out from the substrates rather than read from the snapshot, so the two can never
        // disagree about which location holds what.
        Assert.Equal(SubstrateLocationState.Occupied, chuck.State);
        Assert.Equal(Wafer, chuck.SubstrateId);
        Assert.Equal(SubstrateLocationState.Unoccupied, slot.State);
    }

    [Fact]
    public void The_snapshots_geography_is_declared_even_if_the_tracker_has_none()
    {
        var tracker = WithAWaferOnTheChuck();
        var snapshot = tracker.Capture();

        // A tracker whose start-up declares a different geography - a chamber renamed between
        // releases, say. The wafer is still somewhere, and an extra name is better than a wafer
        // nothing is tracking.
        var restarted = new SubstrateTracker();

        Assert.True(restarted.Restore(snapshot).IsAccepted);
        Assert.NotNull(restarted.FindLocation(Chuck));
        Assert.NotNull(restarted.FindLocation(Slot));
        Assert.Equal(Chuck, restarted.FindSubstrate(Wafer)!.LocationId);
    }

    /// <summary>
    /// A snapshot whose location list does not mention where one of its wafers is.
    /// </summary>
    /// <remarks>
    /// Which a capture from this package never writes - it records every declared location - so
    /// this is a hand-written or hand-edited one, or one from a version that did not keep the
    /// geography. A negative control found it: deleting the fallback that declares the location
    /// left the whole suite green, because every other test's snapshot lists the locations its
    /// wafers are at. Without the fallback the wafer comes back at a location the tracker has
    /// never heard of, which is a wafer nothing can move.
    /// </remarks>
    [Fact]
    public void A_wafer_at_a_location_the_snapshot_forgot_still_gets_one()
    {
        var restarted = Build(declare: false);

        var report = restarted.Restore(new SubstrateTrackerSnapshot
        {
            Substrates =
            [
                new SubstrateSnapshot
                {
                    SubstrateId = Wafer,
                    LocationId = Chuck,
                    TransportState = SubstrateTransportState.AtWork,
                    ProcessingState = SubstrateProcessingState.NeedsProcessing,
                },
            ],
        });

        Assert.True(report.IsAccepted);

        var chuck = restarted.FindLocation(Chuck);

        Assert.NotNull(chuck);
        Assert.Equal(SubstrateLocationState.Occupied, chuck!.State);
        Assert.Equal(Wafer, chuck.SubstrateId);

        // And the wafer can be got out, which is the whole reason to declare rather than drop.
        restarted.AddLocation(Slot);

        Assert.True(restarted.MoveTo(Wafer, Slot).IsAccepted);
    }

    [Fact]
    public void A_tracker_that_already_has_substrates_refuses()
    {
        var tracker = WithAWaferOnTheChuck();
        var busy = Build();

        Assert.True(busy.Add(Other, Slot, slotId: 2).IsAccepted);

        var report = busy.Restore(tracker.Capture());

        Assert.False(report.IsAccepted);
        Assert.Contains("already has", report.Reason, StringComparison.Ordinal);
        Assert.Null(busy.FindSubstrate(Wafer));
        Assert.NotNull(busy.FindSubstrate(Other));
    }

    [Fact]
    public void Restoring_raises_nothing()
    {
        var tracker = WithAWaferOnTheChuck();
        var restarted = Build();
        var moves = 0;

        restarted.SubstrateStateChanged += (_, _) => moves++;
        restarted.LocationChanged += (_, _) => moves++;

        Assert.True(restarted.Restore(tracker.Capture()).IsAccepted);

        Assert.Equal(0, moves);
    }

    [Fact]
    public void A_restored_wafer_can_still_be_got_home()
    {
        var tracker = WithAWaferOnTheChuck();
        var restarted = Build();

        Assert.True(restarted.Restore(tracker.Capture()).IsAccepted);

        // Which is what the whole exception is for: the lot can be finished, or at least taken
        // out, instead of being stuck on a chuck nothing knows about.
        Assert.True(restarted.MoveTo(Wafer, Slot).IsAccepted);
        Assert.Equal(SubstrateTransportState.AtDestination, restarted.FindSubstrate(Wafer)!.TransportState);
        Assert.True(restarted.Remove(Wafer).IsAccepted);
    }

    [Fact]
    public void An_empty_tracker_takes_a_snapshot_of_nothing()
    {
        var report = Build().Restore(new SubstrateTrackerSnapshot());

        Assert.True(report.IsAccepted);
        Assert.Empty(report.Restored);
    }

    [Fact]
    public void A_snapshot_nobody_passed_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => new SubstrateTracker().Restore(null!));
}
