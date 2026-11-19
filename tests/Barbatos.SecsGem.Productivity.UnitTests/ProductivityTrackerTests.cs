// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Productivity.UnitTests;

/// <summary>
/// E10's six states, and the counting that comes out of moving between them.
/// </summary>
/// <remarks>
/// The arithmetic is in <see cref="ProductivityMetricsTests"/>; this file is about the state model
/// and what it counts. The two rules worth reading it for are that a failure is an entry into
/// unscheduled downtime - so nobody has to count failures by hand - and that a repair is an exit
/// into a state the tool can work in, which is the only honest evidence that it was fixed.
/// </remarks>
public class ProductivityTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private static (ProductivityTracker Tracker, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(Start);

        return (new ProductivityTracker(clock), clock);
    }

    [Fact]
    public void A_new_tracker_is_not_scheduled()
    {
        var (tracker, _) = Build();

        // Not standby: a tracker that has just been created has been told nothing, and claiming
        // the tool was available would put time nobody scheduled into every operations ratio.
        Assert.Equal(EquipmentState.NonScheduled, tracker.State);
        Assert.Equal(string.Empty, tracker.StateReason);
        Assert.Equal(Start, tracker.StateSince);
        Assert.Equal(Start, tracker.Report().Since);
    }

    [Fact]
    public void Any_state_can_follow_any_other()
    {
        var (tracker, clock) = Build();

        // E10 is a classification of time and not a life cycle, so there is no ordering to
        // enforce and this walk through all six in a silly order is a legal Tuesday.
        EquipmentState[] walk =
        [
            EquipmentState.Productive,
            EquipmentState.UnscheduledDowntime,
            EquipmentState.NonScheduled,
            EquipmentState.Engineering,
            EquipmentState.ScheduledDowntime,
            EquipmentState.Standby,
            EquipmentState.Productive,
        ];

        foreach (var state in walk)
        {
            clock.Advance(TimeSpan.FromMinutes(10));

            var result = tracker.MoveTo(state, "because");

            Assert.True(result.IsAccepted, $"{state} was refused: {result.Reason}");
            Assert.Equal(state, tracker.State);
        }
    }

    [Theory]
    [InlineData(EquipmentState.UnscheduledDowntime)]
    [InlineData(EquipmentState.ScheduledDowntime)]
    public void A_downtime_needs_a_reason(EquipmentState state)
    {
        var (tracker, _) = Build();

        var refusal = tracker.MoveTo(state);

        Assert.False(refusal.IsAccepted);
        Assert.Equal($"{state} needs a reason.", refusal.Reason);
        Assert.Equal(EquipmentState.NonScheduled, tracker.State);
    }

    [Theory]
    [InlineData(EquipmentState.Engineering)]
    [InlineData(EquipmentState.Standby)]
    [InlineData(EquipmentState.Productive)]
    public void Everything_else_does_not(EquipmentState state)
    {
        var (tracker, _) = Build();

        Assert.True(tracker.MoveTo(state).IsAccepted);
        Assert.Equal(state, tracker.State);
    }

    [Fact]
    public void Producing_needs_no_excuse_and_keeps_none()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.Productive, "running RECIPE-7").IsAccepted);

        // A reason stored against Productive would turn up in a report as though producing were
        // something to explain.
        Assert.Equal(string.Empty, tracker.StateReason);
        Assert.Equal(string.Empty, tracker.Report().StateReason);
    }

    [Fact]
    public void The_state_the_tool_is_already_in_is_refused()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);

        clock.Advance(TimeSpan.FromHours(1));

        var refusal = tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed");

        Assert.False(refusal.IsAccepted);
        Assert.Equal("the tool is already UnscheduledDowntime (pump failed).", refusal.Reason);

        // The point of refusing: forgiving it would restart the clock on the current stretch every
        // time a polling loop said the same thing again, and every duration would come out short.
        Assert.Equal(TimeSpan.FromHours(1), tracker.Report().UnscheduledDowntime);
        Assert.Equal(Start, tracker.StateSince);
    }

    [Fact]
    public void A_reasonless_state_the_tool_is_already_in_is_refused_too()
    {
        var (tracker, _) = Build();

        var refusal = tracker.MoveTo(EquipmentState.NonScheduled);

        Assert.False(refusal.IsAccepted);
        Assert.Equal("the tool is already NonScheduled.", refusal.Reason);
    }

    [Fact]
    public void A_different_reason_for_the_same_state_does_not_break_the_stretch()
    {
        var (tracker, clock) = Build();
        List<EquipmentStateChangedEventArgs> moves = [];
        List<ProductivityReasonChangedEventArgs> reasons = [];
        tracker.StateChanged += (_, e) => moves.Add(e);
        tracker.ReasonChanged += (_, e) => reasons.Add(e);

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);

        clock.Advance(TimeSpan.FromHours(2));

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "awaiting parts").IsAccepted);

        clock.Advance(TimeSpan.FromHours(3));

        var report = tracker.Report();

        // One failure with two phases. The first is the tool's fault and the second is the
        // stockroom's, which E10 cares about - but it is still one failure and one stretch.
        Assert.Equal(1, report.Failures);
        Assert.Equal(TimeSpan.FromHours(5), report.UnscheduledDowntime);
        Assert.Equal("awaiting parts", report.StateReason);
        Assert.Equal(Start, tracker.StateSince);
        Assert.Single(moves);

        var change = Assert.Single(reasons);

        Assert.Equal(EquipmentState.UnscheduledDowntime, change.State);
        Assert.Equal("pump failed", change.Previous);
        Assert.Equal("awaiting parts", change.Current);
    }

    [Fact]
    public void A_failure_is_an_entry_into_unscheduled_downtime()
    {
        var (tracker, clock) = Build();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, $"failure {i}").IsAccepted);
            clock.Advance(TimeSpan.FromMinutes(30));
            Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
            clock.Advance(TimeSpan.FromHours(1));
        }

        var report = tracker.Report();

        // Nobody counted these by hand, which is why they cannot drift away from the buckets.
        Assert.Equal(3, report.Failures);
        Assert.Equal(3, report.Repairs);
        Assert.Equal(0, report.ScheduledInterrupts);
        Assert.Equal(3, report.Interrupts);
    }

    [Fact]
    public void A_scheduled_interrupt_is_counted_apart_from_a_failure()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.ScheduledDowntime, "preventive maintenance").IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);

        var report = tracker.Report();

        Assert.Equal(0, report.Failures);
        Assert.Equal(1, report.ScheduledInterrupts);
        Assert.Equal(1, report.Interrupts);

        // And no repair: planned work is not a repair, so it has no business in MTTR.
        Assert.Equal(0, report.Repairs);
        Assert.Equal(TimeSpan.Zero, report.MeanTimeToRepair);
    }

    [Fact]
    public void A_repair_is_only_counted_once_the_tool_can_work_again()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));

        var midRepair = tracker.Report();

        // Two hours of downtime and no repair yet: a repair that has not finished has no duration
        // to average, and dividing the one by the other would make a tool that is broken right now
        // look quicker at being fixed the longer it stays broken.
        Assert.Equal(TimeSpan.FromHours(2), midRepair.UnscheduledDowntime);
        Assert.Equal(1, midRepair.Failures);
        Assert.Equal(0, midRepair.Repairs);
        Assert.Equal(TimeSpan.Zero, midRepair.MeanTimeToRepair);

        Assert.True(tracker.MoveTo(EquipmentState.Standby, "no material").IsAccepted);

        var fixedUp = tracker.Report();

        Assert.Equal(1, fixedUp.Repairs);
        Assert.Equal(TimeSpan.FromHours(2), fixedUp.MeanTimeToRepair);
    }

    [Fact]
    public void A_shift_that_ends_mid_repair_is_not_a_completed_repair()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(tracker.MoveTo(EquipmentState.Standby).IsAccepted);
        clock.Advance(TimeSpan.FromHours(1));

        // Second failure, and the shift ends before anybody fixes it.
        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "heater failed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(4));
        Assert.True(tracker.MoveTo(EquipmentState.NonScheduled, "shift over").IsAccepted);

        var report = tracker.Report();

        // Six hours broken, two failures, one repair - because only one of them ended with a tool
        // that could work. The unfinished four hours stay in the downtime bucket and out of the
        // mean, which is the difference between an honest MTTR and a flattering one.
        Assert.Equal(TimeSpan.FromHours(6), report.UnscheduledDowntime);
        Assert.Equal(2, report.Failures);
        Assert.Equal(1, report.Repairs);
        Assert.Equal(TimeSpan.FromHours(2), report.MeanTimeToRepair);
    }

    [Theory]
    [InlineData(EquipmentState.Engineering)]
    [InlineData(EquipmentState.Standby)]
    [InlineData(EquipmentState.Productive)]
    public void Any_state_the_tool_can_work_in_ends_a_repair(EquipmentState state)
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(tracker.MoveTo(state).IsAccepted);

        Assert.Equal(1, tracker.Report().Repairs);
    }

    [Fact]
    public void Going_from_a_failure_to_planned_work_does_not_end_the_repair()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(tracker.MoveTo(EquipmentState.ScheduledDowntime, "while it is open, do the PM").IsAccepted);

        // Still not a tool that can work, so still not evidence of a repair.
        Assert.Equal(0, tracker.Report().Repairs);
    }

    [Fact]
    public void Every_move_reports_the_stretch_that_just_ended()
    {
        var (tracker, clock) = Build();
        List<string> seen = [];

        tracker.StateChanged += (_, e) =>
        {
            Assert.Equal(e.Current, tracker.State);
            seen.Add($"{e.Previous}->{e.Current} after {e.Elapsed.TotalMinutes:F0}m");
        };

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(tracker.MoveTo(EquipmentState.Standby).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(90));
        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);

        Assert.Equal(
            [
                "NonScheduled->Standby after 30m",
                "Standby->Productive after 10m",
                "Productive->UnscheduledDowntime after 90m",
            ],
            seen);
    }

    [Fact]
    public void A_refused_move_raises_nothing()
    {
        var (tracker, _) = Build();
        var raised = 0;

        tracker.StateChanged += (_, _) => raised++;
        tracker.ReasonChanged += (_, _) => raised++;

        Assert.False(tracker.MoveTo(EquipmentState.NonScheduled).IsAccepted);
        Assert.False(tracker.MoveTo(EquipmentState.UnscheduledDowntime).IsAccepted);
        Assert.False(tracker.MoveTo(EquipmentState.ScheduledDowntime, string.Empty).IsAccepted);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_state_outside_the_six_throws()
    {
        var (tracker, _) = Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.MoveTo((EquipmentState)6, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.MoveTo((EquipmentState)(-1), "x"));
        Assert.Throws<ArgumentNullException>(() => tracker.MoveTo(EquipmentState.Standby, null!));
    }

    [Fact]
    public void Nonsense_counts_throw_rather_than_being_recorded()
    {
        var (tracker, _) = Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordCycles(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordUnits(-1, 0, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordUnits(5, -1, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordUnits(5, 6, TimeSpan.FromMinutes(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.RecordUnits(5, 5, TimeSpan.FromMinutes(-5)));

        var report = tracker.Report();

        Assert.Equal(0, report.Cycles);
        Assert.Equal(0, report.ActualUnits);
    }

    [Fact]
    public void A_tracker_with_no_clock_still_works()
    {
        // TimeProvider.System by default, which is what an application that never thought about
        // the clock gets - and it must not be a null reference.
        var tracker = new ProductivityTracker();

        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        tracker.RecordUnits(1, 1, TimeSpan.FromMinutes(5));

        Assert.Equal(1, tracker.Report().ActualUnits);
    }
}
