// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Productivity.UnitTests;

/// <summary>
/// E10's reliability figures and E79's OEE, which is the only reason either standard exists.
/// </summary>
/// <remarks>
/// Two invariants carry this file. The six buckets always add up to the window, and E79's four
/// factors always multiply out to the overall efficiency computed directly - the chain telescopes,
/// so any arithmetic mistake in a factor shows up as the product disagreeing with the whole.
/// </remarks>
public class ProductivityMetricsTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private static (ProductivityTracker Tracker, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(Start);

        return (new ProductivityTracker(clock), clock);
    }

    /// <summary>A whole day: 4h not scheduled, 2h planned down, 2h broken, 4h idle, 2h learning, 10h producing.</summary>
    private static (ProductivityTracker Tracker, FakeTimeProvider Clock) ADay()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromHours(4));
        Assert.True(tracker.MoveTo(EquipmentState.ScheduledDowntime, "preventive maintenance").IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(tracker.MoveTo(EquipmentState.Standby, "no material").IsAccepted);
        clock.Advance(TimeSpan.FromHours(4));
        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        clock.Advance(TimeSpan.FromHours(10));
        Assert.True(tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(tracker.MoveTo(EquipmentState.Engineering, "qualifying the new pump").IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));

        // A hundred wafers out, ninety good, five minutes each at the tool's theoretical rate.
        tracker.RecordUnits(100, 90, TimeSpan.FromMinutes(5));
        tracker.RecordCycles(100);

        return (tracker, clock);
    }

    private static void AssertAddsUp(ProductivityReport report, FakeTimeProvider clock)
    {
        Assert.Equal(
            report.NonScheduled
            + report.UnscheduledDowntime
            + report.ScheduledDowntime
            + report.Engineering
            + report.Standby
            + report.Productive,
            report.Total);

        Assert.Equal(clock.GetUtcNow() - report.Since, report.Total);
        Assert.Equal(report.Downtime + report.Uptime, report.Operations);
        Assert.Equal(report.NonScheduled + report.Operations, report.Total);
    }

    [Fact]
    public void The_six_buckets_always_add_up_to_the_window()
    {
        var (tracker, clock) = Build();

        AssertAddsUp(tracker.Report(), clock);

        foreach (var state in new[]
        {
            EquipmentState.Standby,
            EquipmentState.Productive,
            EquipmentState.UnscheduledDowntime,
            EquipmentState.ScheduledDowntime,
            EquipmentState.Engineering,
            EquipmentState.NonScheduled,
        })
        {
            Assert.True(tracker.MoveTo(state, "because").IsAccepted);
            clock.Advance(TimeSpan.FromMinutes(17));
            AssertAddsUp(tracker.Report(), clock);
        }
    }

    [Fact]
    public void A_day_lands_in_the_buckets_it_was_spent_in()
    {
        var (tracker, clock) = ADay();

        var report = tracker.Report();

        Assert.Equal(TimeSpan.FromHours(4), report.NonScheduled);
        Assert.Equal(TimeSpan.FromHours(2), report.ScheduledDowntime);
        Assert.Equal(TimeSpan.FromHours(2), report.UnscheduledDowntime);
        Assert.Equal(TimeSpan.FromHours(4), report.Standby);
        Assert.Equal(TimeSpan.FromHours(10), report.Productive);
        Assert.Equal(TimeSpan.FromHours(2), report.Engineering);

        Assert.Equal(TimeSpan.FromHours(4), report.Downtime);
        Assert.Equal(TimeSpan.FromHours(16), report.Uptime);
        Assert.Equal(TimeSpan.FromHours(20), report.Operations);
        Assert.Equal(TimeSpan.FromHours(24), report.Total);
        AssertAddsUp(report, clock);
    }

    [Fact]
    public void The_two_denominators_answer_different_questions()
    {
        var (tracker, clock) = Build();

        // One staffed shift of a three-shift day, and the tool ran perfectly through it.
        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        clock.Advance(TimeSpan.FromHours(8));
        Assert.True(tracker.MoveTo(EquipmentState.NonScheduled, "two shifts unstaffed").IsAccepted);
        clock.Advance(TimeSpan.FromHours(16));

        var report = tracker.Report();

        // Both numbers are correct answers to different questions, and confusing them is the
        // commonest mistake made against E10.
        Assert.Equal(1d, report.Availability);
        Assert.Equal(1d, report.OperationalUtilisation);
        Assert.Equal(1d / 3d, report.AvailabilityEfficiency, 10);
        Assert.Equal(1d / 3d, report.TotalUtilisation, 10);
    }

    [Fact]
    public void E10_reliability_figures_come_off_the_same_window()
    {
        var (tracker, _) = ADay();

        var report = tracker.Report();

        // 16h up out of 20h asked for; 10h producing out of 20h and out of 24h.
        Assert.Equal(0.8d, report.Availability, 10);
        Assert.Equal(0.5d, report.OperationalUtilisation, 10);
        Assert.Equal(10d / 24d, report.TotalUtilisation, 10);

        // One failure, one scheduled interrupt, one completed repair.
        Assert.Equal(1, report.Failures);
        Assert.Equal(1, report.ScheduledInterrupts);
        Assert.Equal(2, report.Interrupts);
        Assert.Equal(1, report.Repairs);

        Assert.Equal(TimeSpan.FromHours(20), report.MeanTimeBetweenFailures);
        Assert.Equal(TimeSpan.FromHours(10), report.MeanTimeBetweenInterrupts);
        Assert.Equal(TimeSpan.FromHours(2), report.MeanTimeToRepair);
        Assert.Equal(100d, report.MeanCyclesBetweenFailures);
    }

    [Fact]
    public void E79_four_factors_multiply_out_to_the_whole()
    {
        var (tracker, _) = ADay();

        var report = tracker.Report();

        Assert.Equal(16d / 24d, report.AvailabilityEfficiency, 10);
        Assert.Equal(10d / 16d, report.OperationalEfficiency, 10);

        // A hundred wafers at five minutes each is 500 minutes of theoretical work in 600 minutes
        // of productive time; ninety of them were good.
        Assert.Equal(500d / 600d, report.RateEfficiency, 10);
        Assert.Equal(0.9d, report.QualityEfficiency, 10);
        Assert.Equal((10d / 16d) * (500d / 600d), report.PerformanceEfficiency, 10);

        // 450 theoretical minutes of good product out of a 1,440 minute day.
        Assert.Equal(450d / 1_440d, report.OverallEquipmentEfficiency, 10);
        Assert.Equal(0.3125d, report.OverallEquipmentEfficiency, 10);

        // And the identity that makes the whole chain checkable: the four factors telescope, so
        // any arithmetic mistake in one of them shows up here.
        Assert.Equal(
            report.AvailabilityEfficiency
            * report.OperationalEfficiency
            * report.RateEfficiency
            * report.QualityEfficiency,
            report.OverallEquipmentEfficiency,
            10);
    }

    [Fact]
    public void The_identity_holds_for_a_day_nobody_designed()
    {
        var (tracker, clock) = Build();
        var random = new Random(20_260_301);

        EquipmentState[] states =
        [
            EquipmentState.NonScheduled,
            EquipmentState.UnscheduledDowntime,
            EquipmentState.ScheduledDowntime,
            EquipmentState.Engineering,
            EquipmentState.Standby,
            EquipmentState.Productive,
        ];

        // Forty moves with awkward durations, so the identity is not being carried by numbers
        // chosen to be kind to it.
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(random.Next(37, 4_801)));
            tracker.MoveTo(states[random.Next(states.Length)], $"reason {i}");

            if (random.Next(3) == 0)
            {
                tracker.RecordUnits(random.Next(1, 8), random.Next(0, 2), TimeSpan.FromSeconds(random.Next(11, 400)));
            }
        }

        clock.Advance(TimeSpan.FromMinutes(13));

        var report = tracker.Report();

        AssertAddsUp(report, clock);
        Assert.Equal(
            report.AvailabilityEfficiency
            * report.OperationalEfficiency
            * report.RateEfficiency
            * report.QualityEfficiency,
            report.OverallEquipmentEfficiency,
            10);
    }

    [Fact]
    public void A_window_with_no_time_in_it_divides_by_nothing()
    {
        var (tracker, _) = Build();

        var report = tracker.Report();

        Assert.Equal(TimeSpan.Zero, report.Total);
        Assert.Equal(0d, report.Availability);
        Assert.Equal(0d, report.AvailabilityEfficiency);
        Assert.Equal(0d, report.OperationalEfficiency);
        Assert.Equal(0d, report.RateEfficiency);
        Assert.Equal(0d, report.QualityEfficiency);
        Assert.Equal(0d, report.OverallEquipmentEfficiency);
        Assert.Equal(TimeSpan.Zero, report.MeanTimeBetweenFailures);
        Assert.Equal(TimeSpan.Zero, report.MeanTimeBetweenInterrupts);
        Assert.Equal(TimeSpan.Zero, report.MeanTimeToRepair);
        Assert.Equal(0d, report.MeanCyclesBetweenFailures);
    }

    [Fact]
    public void A_tool_that_produced_nothing_has_no_productivity_however_available_it_was()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        clock.Advance(TimeSpan.FromHours(8));

        var report = tracker.Report();

        // Perfectly available, perfectly utilised, and nothing came out of it.
        Assert.Equal(1d, report.Availability);
        Assert.Equal(1d, report.AvailabilityEfficiency);
        Assert.Equal(1d, report.OperationalEfficiency);
        Assert.Equal(0d, report.RateEfficiency);
        Assert.Equal(0d, report.OverallEquipmentEfficiency);
    }

    [Fact]
    public void A_rate_above_one_is_the_theoretical_rate_being_wrong()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        clock.Advance(TimeSpan.FromHours(1));

        // Twenty wafers at five minutes each is a hundred minutes of theoretical work done in
        // sixty. The number is not clamped, because a rate efficiency above one is a real finding
        // about the fab's own figure and hiding it would hide that.
        tracker.RecordUnits(20, 20, TimeSpan.FromMinutes(5));

        Assert.Equal(100d / 60d, tracker.Report().RateEfficiency, 10);
    }

    [Fact]
    public void Units_from_several_recipes_each_keep_their_own_rate()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);
        clock.Advance(TimeSpan.FromHours(2));

        // The reason the theoretical time is an argument and not a property of the tracker: a
        // tool running two recipes has two rates, and one rate on the tracker would be wrong for
        // every lot but one.
        tracker.RecordUnits(10, 10, TimeSpan.FromMinutes(3));
        tracker.RecordUnits(10, 8, TimeSpan.FromMinutes(6));

        var report = tracker.Report();

        Assert.Equal(TimeSpan.FromMinutes(90), report.TheoreticalActualTime);
        Assert.Equal(TimeSpan.FromMinutes(78), report.TheoreticalEffectiveTime);
        Assert.Equal(20, report.ActualUnits);
        Assert.Equal(18, report.EffectiveUnits);
        Assert.Equal(78d / 90d, report.QualityEfficiency, 10);
    }

    [Fact]
    public void Resetting_starts_the_window_again_without_moving_the_tool()
    {
        var (tracker, clock) = ADay();

        var enteredEngineeringAt = tracker.StateSince;

        Assert.True(tracker.Reset().IsAccepted);

        clock.Advance(TimeSpan.FromHours(3));

        var report = tracker.Report();

        Assert.Equal(Start + TimeSpan.FromHours(24), report.Since);
        Assert.Equal(TimeSpan.FromHours(3), report.Total);
        Assert.Equal(TimeSpan.FromHours(3), report.Engineering);
        Assert.Equal(TimeSpan.Zero, report.Productive);
        Assert.Equal(0, report.Failures);
        Assert.Equal(0, report.Repairs);
        Assert.Equal(0, report.ActualUnits);
        Assert.Equal(TimeSpan.Zero, report.TheoreticalActualTime);
        AssertAddsUp(report, clock);

        // The reset is about the counters, not the machine: the tool did not change state and does
        // not claim to have.
        Assert.Equal(EquipmentState.Engineering, report.State);
        Assert.Equal(enteredEngineeringAt, tracker.StateSince);
    }

    [Fact]
    public void A_wall_clock_that_slips_cannot_touch_the_accounting()
    {
        var clock = new SlippingClock { Now = Start };
        var tracker = new ProductivityTracker(clock);

        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);

        Assert.Equal(Start, tracker.StateSince);

        clock.Tick(TimeSpan.FromHours(8));

        // Somebody notices the machine's clock is two hours fast and corrects it. Nothing about
        // how long the tool has been producing has changed, and a shift report must not pretend
        // otherwise - which is why durations are measured from the monotonic timestamp.
        clock.Now = Start - TimeSpan.FromHours(2);

        Assert.Equal(TimeSpan.FromHours(8), tracker.Report().Productive);

        // And a transition after the correction charges the time the tool was really there.
        Assert.True(tracker.MoveTo(EquipmentState.Standby).IsAccepted);
        clock.Tick(TimeSpan.FromHours(2));

        var report = tracker.Report();

        Assert.Equal(TimeSpan.FromHours(8), report.Productive);
        Assert.Equal(TimeSpan.FromHours(2), report.Standby);
        Assert.Equal(0.8d, report.OperationalUtilisation, 10);

        // The other half of the rule, and the half that makes it a split rather than a workaround:
        // StateSince is a wall-clock reading and does follow the correction, because it is there to
        // tell a person what time the tool went on standby.
        Assert.Equal(Start - TimeSpan.FromHours(2), tracker.StateSince);
    }

    /// <summary>A clock whose wall time can be corrected while its timestamp only goes forwards.</summary>
    /// <remarks>
    /// Which is what a real one is: <see cref="TimeProvider.GetTimestamp"/> is documented as
    /// monotonic and <see cref="TimeProvider.GetUtcNow"/> is not. <c>FakeTimeProvider</c> will not
    /// go back in time, so the only way to test the difference is a provider that will.
    /// </remarks>
    private sealed class SlippingClock : TimeProvider
    {
        private long _stamp;

        public DateTimeOffset Now { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => Now;

        public override long GetTimestamp() => _stamp;

        public void Tick(TimeSpan by) => _stamp += by.Ticks;
    }
}
