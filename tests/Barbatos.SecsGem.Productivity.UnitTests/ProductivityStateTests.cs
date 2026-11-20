// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Productivity.UnitTests;

/// <summary>
/// A tracker that outlives the process, and the one thing a restart must not do to an accounting
/// standard.
/// </summary>
/// <remarks>
/// <para>
/// E10 divides by its buckets. Every ratio it defines has a denominator that is some sum of the
/// six, so an hour that is in none of them is an hour that silently improves whichever ratio it
/// would have reduced - and a machine being switched off is exactly such an hour. The test that
/// matters most here is therefore not the round trip but the identity: before a restore and after
/// one, the six buckets add up to the window.
/// </para>
/// <para>
/// The continuity test is the other one worth reading. It runs a day twice - once straight
/// through, once through a capture and a restore in the middle - and asserts the two reports are
/// the same figure for figure. That is a stronger claim than any property-by-property round trip,
/// because it fails if the snapshot is missing anything the metrics are computed from.
/// </para>
/// </remarks>
public class ProductivityStateTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private static (ProductivityTracker Tracker, FakeTimeProvider Clock) Build(DateTimeOffset? at = null)
    {
        var clock = new FakeTimeProvider(at ?? Start);

        return (new ProductivityTracker(clock), clock);
    }

    /// <summary>Half a day of a real shift, so there is something to lose.</summary>
    private static void WorkTheMorning(ProductivityTracker tracker, FakeTimeProvider clock)
    {
        tracker.MoveTo(EquipmentState.Standby);
        clock.Advance(TimeSpan.FromHours(1));
        tracker.MoveTo(EquipmentState.Productive);
        tracker.RecordCycles(40);
        tracker.RecordUnits(40, 36, TimeSpan.FromMinutes(4));
        clock.Advance(TimeSpan.FromHours(4));
        tracker.MoveTo(EquipmentState.UnscheduledDowntime, "PUMP");
        clock.Advance(TimeSpan.FromHours(1));
        tracker.MoveTo(EquipmentState.Productive);
    }

    [Fact]
    public void The_buckets_and_the_counts_come_back()
    {
        var (tracker, clock) = Build();

        WorkTheMorning(tracker, clock);

        var before = tracker.Report();
        var snapshot = tracker.Capture();

        var (restarted, _) = Build(clock.GetUtcNow());
        var report = restarted.Restore(snapshot);

        Assert.True(report.IsAccepted);
        Assert.Equal(TimeSpan.Zero, report.Unaccounted);

        var after = restarted.Report();

        Assert.Equal(before.Since, after.Since);
        Assert.Equal(before.Productive, after.Productive);
        Assert.Equal(before.Standby, after.Standby);
        Assert.Equal(before.UnscheduledDowntime, after.UnscheduledDowntime);
        Assert.Equal(before.Failures, after.Failures);
        Assert.Equal(before.Repairs, after.Repairs);
        Assert.Equal(before.MeanTimeToRepair, after.MeanTimeToRepair);
        Assert.Equal(before.Cycles, after.Cycles);
        Assert.Equal(before.ActualUnits, after.ActualUnits);
        Assert.Equal(before.EffectiveUnits, after.EffectiveUnits);
        Assert.Equal(before.TheoreticalEffectiveTime, after.TheoreticalEffectiveTime);
    }

    /// <summary>
    /// The claim the whole design turns on.
    /// </summary>
    /// <remarks>
    /// E10's buckets have to add up to the window, and a restore that dropped the hours the tool
    /// was off would break that in the direction that flatters: the missing time would come out of
    /// whichever denominator it belonged in.
    /// </remarks>
    [Fact]
    public void The_six_buckets_still_add_up_to_the_window_across_a_restart()
    {
        var (tracker, clock) = Build();

        WorkTheMorning(tracker, clock);

        var snapshot = tracker.Capture();

        // Three hours with the machine off, and then it comes back.
        clock.Advance(TimeSpan.FromHours(3));

        var restarted = new ProductivityTracker(clock);
        var report = restarted.Restore(snapshot);

        Assert.Equal(TimeSpan.FromHours(3), report.Unaccounted);
        Assert.Equal(EquipmentState.UnscheduledDowntime, report.UnaccountedChargedTo);

        clock.Advance(TimeSpan.FromHours(2));

        var after = restarted.Report();

        Assert.Equal(after.Total, after.NonScheduled + after.UnscheduledDowntime + after.ScheduledDowntime
            + after.Engineering + after.Standby + after.Productive);

        // And the window really does span the outage: six in the morning to one in the afternoon.
        Assert.Equal(TimeSpan.FromHours(11), after.Total);
    }

    [Fact]
    public void The_hours_the_tool_was_off_are_charged_where_they_cannot_flatter()
    {
        var (tracker, clock) = Build();

        tracker.MoveTo(EquipmentState.Productive);
        clock.Advance(TimeSpan.FromHours(4));

        var snapshot = tracker.Capture();

        clock.Advance(TimeSpan.FromHours(4));

        var restarted = new ProductivityTracker(clock);

        Assert.True(restarted.Restore(snapshot).IsAccepted);

        var after = restarted.Report();

        // Four hours producing and four hours broken, which is the reading that cannot be used to
        // make a tool look better than it was.
        Assert.Equal(TimeSpan.FromHours(4), after.Productive);
        Assert.Equal(TimeSpan.FromHours(4), after.UnscheduledDowntime);
        Assert.Equal(0.5, after.AvailabilityEfficiency);
    }

    [Fact]
    public void A_fab_that_powers_its_tools_down_says_so()
    {
        var (tracker, clock) = Build();

        tracker.MoveTo(EquipmentState.Productive);
        clock.Advance(TimeSpan.FromHours(4));

        var snapshot = tracker.Capture();

        clock.Advance(TimeSpan.FromHours(8));

        var restarted = new ProductivityTracker(clock);
        var report = restarted.Restore(snapshot, EquipmentState.NonScheduled);

        Assert.Equal(EquipmentState.NonScheduled, report.UnaccountedChargedTo);

        var after = restarted.Report();

        Assert.Equal(TimeSpan.FromHours(8), after.NonScheduled);
        Assert.Equal(TimeSpan.Zero, after.UnscheduledDowntime);

        // Which is the whole point of E10 having both: the tool was available all the time it was
        // scheduled, and it was only scheduled for the four hours it worked.
        Assert.Equal(1d, after.Availability);
        Assert.Equal(1d / 3d, after.TotalUtilisation, 10);
    }

    /// <summary>A day run twice, once through a restart, with the two reports compared.</summary>
    [Fact]
    public void A_day_through_a_restart_reports_the_same_as_a_day_without_one()
    {
        var (straight, straightClock) = Build();
        var (interrupted, clock) = Build();

        WorkTheMorning(straight, straightClock);
        WorkTheMorning(interrupted, clock);

        // The restart happens here, and takes no time at all - so the only difference between the
        // two trackers is that one of them went through a snapshot.
        var restarted = new ProductivityTracker(clock);

        // No gap, so this is a hand-over rather than an outage: the tool comes back producing,
        // exactly as it was, and nothing is counted for the restart itself.
        Assert.True(restarted.Restore(interrupted.Capture()).IsAccepted);
        Assert.Equal(EquipmentState.Productive, restarted.State);

        foreach (var tracker in (ProductivityTracker[])[straight, restarted])
        {
            tracker.RecordCycles(20);
            tracker.RecordUnits(20, 20, TimeSpan.FromMinutes(4));
        }

        straightClock.Advance(TimeSpan.FromHours(2));
        clock.Advance(TimeSpan.FromHours(2));

        var expected = straight.Report();
        var actual = restarted.Report();

        Assert.Equal(expected.Total, actual.Total);
        Assert.Equal(expected.Productive, actual.Productive);
        Assert.Equal(expected.Standby, actual.Standby);
        Assert.Equal(expected.UnscheduledDowntime, actual.UnscheduledDowntime);
        Assert.Equal(expected.Failures, actual.Failures);
        Assert.Equal(expected.Repairs, actual.Repairs);
        Assert.Equal(expected.Cycles, actual.Cycles);
        Assert.Equal(expected.ActualUnits, actual.ActualUnits);
        Assert.Equal(expected.EffectiveUnits, actual.EffectiveUnits);
        Assert.Equal(expected.Availability, actual.Availability, 10);
        Assert.Equal(expected.OverallEquipmentEfficiency, actual.OverallEquipmentEfficiency, 10);
        Assert.Equal(expected.MeanTimeBetweenFailures, actual.MeanTimeBetweenFailures);
        Assert.Equal(expected.MeanTimeToRepair, actual.MeanTimeToRepair);
    }

    [Fact]
    public void After_an_outage_the_tool_is_in_the_state_it_was_charged_to_since_it_went_off()
    {
        var (tracker, clock) = Build();

        tracker.MoveTo(EquipmentState.Productive);
        clock.Advance(TimeSpan.FromHours(1));

        var snapshot = tracker.Capture();
        var wentOff = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromHours(1));

        var restarted = new ProductivityTracker(clock);

        Assert.True(restarted.Restore(snapshot).IsAccepted);

        // So a report taken before the application has told the tracker anything still describes
        // something true, rather than claiming the tool is producing - and the stretch is dated
        // from when the machine went off rather than from when it came back, which is what lets a
        // repair finishing later include the whole outage.
        Assert.Equal(EquipmentState.UnscheduledDowntime, restarted.State);
        Assert.Equal(wentOff, restarted.StateSince);
        Assert.Equal("the machine restarted", restarted.Report().StateReason);
    }

    /// <summary>
    /// The one the continuity test turned up, and the reason the outage stays an open stretch.
    /// </summary>
    /// <remarks>
    /// A restart that is charged to unscheduled downtime is one interruption: a failure when the
    /// machine went off and a repair when somebody brings it back, with the whole outage inside
    /// the repair. The first version of this counted the repair and not the failure, because it
    /// set the state's clock running at the restore rather than at the capture - so a tool could
    /// end a day with more repairs than failures, which E10 can never allow.
    /// </remarks>
    [Fact]
    public void An_outage_is_one_failure_and_one_repair_with_the_outage_inside_it()
    {
        var (tracker, clock) = Build();

        tracker.MoveTo(EquipmentState.Productive);
        clock.Advance(TimeSpan.FromHours(2));

        var snapshot = tracker.Capture();

        clock.Advance(TimeSpan.FromHours(3));

        var restarted = new ProductivityTracker(clock);

        Assert.True(restarted.Restore(snapshot).IsAccepted);
        Assert.Equal(1, restarted.Report().Failures);

        // Somebody fixes it an hour after it comes back.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(restarted.MoveTo(EquipmentState.Productive).IsAccepted);

        var after = restarted.Report();

        Assert.Equal(1, after.Failures);
        Assert.Equal(1, after.Repairs);
        Assert.Equal(TimeSpan.FromHours(4), after.MeanTimeToRepair);
        Assert.Equal(TimeSpan.FromHours(4), after.UnscheduledDowntime);
    }

    [Fact]
    public void A_tool_that_was_already_down_when_it_was_captured_has_one_stretch_and_not_two()
    {
        var (tracker, clock) = Build();

        tracker.MoveTo(EquipmentState.UnscheduledDowntime, "PUMP");
        clock.Advance(TimeSpan.FromHours(1));

        var snapshot = tracker.Capture();

        clock.Advance(TimeSpan.FromHours(2));

        var restarted = new ProductivityTracker(clock);

        Assert.True(restarted.Restore(snapshot).IsAccepted);

        // One failure, counted when the pump went, and not a second one for the restart: the tool
        // never came back up in between.
        Assert.Equal(1, restarted.Report().Failures);
        Assert.Equal(TimeSpan.FromHours(3), restarted.Report().UnscheduledDowntime);
    }

    [Fact]
    public void A_capture_folds_the_open_stretch_in_so_a_snapshot_is_a_whole_window()
    {
        var (tracker, clock) = Build();

        tracker.MoveTo(EquipmentState.Productive);
        clock.Advance(TimeSpan.FromHours(3));

        var snapshot = tracker.Capture();

        // Not zero, which it would be if Capture had only read the stored bucket and left the
        // stretch that is still running out of it.
        Assert.Equal(TimeSpan.FromHours(3), snapshot.Productive);
        Assert.Equal(clock.GetUtcNow(), snapshot.CapturedAt);
    }

    [Fact]
    public void A_tracker_that_has_already_accounted_for_something_refuses()
    {
        var (tracker, clock) = Build();

        WorkTheMorning(tracker, clock);

        var snapshot = tracker.Capture();
        var (busy, busyClock) = Build();

        busy.MoveTo(EquipmentState.Productive);
        busyClock.Advance(TimeSpan.FromMinutes(1));
        busy.MoveTo(EquipmentState.Standby);

        var report = busy.Restore(snapshot);

        Assert.False(report.IsAccepted);
        Assert.Contains("already accounted", report.Reason, StringComparison.Ordinal);

        // And nothing was half applied.
        Assert.Equal(TimeSpan.FromMinutes(1), busy.Report().Productive);
    }

    [Fact]
    public void A_tracker_that_has_only_counted_something_refuses_too()
    {
        var (tracker, clock) = Build();

        WorkTheMorning(tracker, clock);

        var (counted, _) = Build();

        counted.RecordCycles(1);

        var report = counted.Restore(tracker.Capture());

        Assert.False(report.IsAccepted);
        Assert.Contains("already counted", report.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_snapshot_nobody_passed_is_rejected()
    {
        var (tracker, _) = Build();

        Assert.Throws<ArgumentNullException>(() => tracker.Restore(null!));
    }

    [Fact]
    public void A_snapshot_with_no_stamp_charges_nothing_and_starts_its_window_now()
    {
        var (tracker, clock) = Build();

        // What a hand-written or truncated snapshot looks like. Charging an interval measured
        // from the zero of DateTimeOffset would put twenty centuries into a bucket.
        var report = tracker.Restore(new ProductivityTrackerSnapshot { Productive = TimeSpan.FromHours(2) });

        Assert.True(report.IsAccepted);
        Assert.Equal(TimeSpan.Zero, report.Unaccounted);
        Assert.Equal(clock.GetUtcNow(), tracker.Report().Since);
        Assert.Equal(TimeSpan.FromHours(2), tracker.Report().Productive);
    }
}
