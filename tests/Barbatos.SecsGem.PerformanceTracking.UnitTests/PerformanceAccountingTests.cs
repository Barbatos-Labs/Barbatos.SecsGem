// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceTracking.UnitTests;

/// <summary>
/// Where the time went, which is the only reason E116 exists.
/// </summary>
/// <remarks>
/// Every figure here is asserted by advancing a fake clock, because a test that waits on a wall
/// clock is a flaky test and a package whose whole output is durations would be nothing but flaky
/// tests. The invariant the file leans on is that the three buckets always add up to the window.
/// </remarks>
public class PerformanceAccountingTests
{
    private const string Chamber = "PM1";
    private const string Recipe = "RECIPE-7";
    private const string NoMaterial = "NO_MATERIAL";

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private static (PerformanceTracker Tracker, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new PerformanceTracker(clock);

        tracker.AddModule(Chamber);

        return (tracker, clock);
    }

    private static void AssertAddsUp(PerformanceStatistics statistics, FakeTimeProvider clock)
        => Assert.Equal(clock.GetUtcNow() - statistics.Since, statistics.Total);

    [Fact]
    public void An_untouched_module_has_spent_its_whole_window_idle()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromHours(2));

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(PerformanceState.Idle, statistics.State);
        Assert.Equal(Start, statistics.Since);
        Assert.Equal(TimeSpan.FromHours(2), statistics.TimeIdle);
        Assert.Equal(TimeSpan.FromHours(2), statistics.TimeInState);
        Assert.Equal(TimeSpan.Zero, statistics.TimeExecuting);
        Assert.Equal(TimeSpan.Zero, statistics.TimeBlocked);
        AssertAddsUp(statistics, clock);
    }

    [Fact]
    public void The_three_times_always_add_up_to_the_window()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromMinutes(10));
        AssertAddsUp(tracker.Statistics(Chamber)!, clock);

        Assert.True(tracker.BeginTask(Chamber, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(20));
        AssertAddsUp(tracker.Statistics(Chamber)!, clock);

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(5));
        AssertAddsUp(tracker.Statistics(Chamber)!, clock);

        Assert.True(tracker.Unblock(Chamber).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(25));
        AssertAddsUp(tracker.Statistics(Chamber)!, clock);

        Assert.True(tracker.EndTask(Chamber).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(40));

        var statistics = tracker.Statistics(Chamber)!;

        AssertAddsUp(statistics, clock);
        Assert.Equal(TimeSpan.FromMinutes(50), statistics.TimeIdle);
        Assert.Equal(TimeSpan.FromMinutes(45), statistics.TimeExecuting);
        Assert.Equal(TimeSpan.FromMinutes(5), statistics.TimeBlocked);
        Assert.Equal(TimeSpan.FromMinutes(40), statistics.TimeInState);
    }

    [Fact]
    public void A_task_is_charged_the_block_as_well_as_the_module()
    {
        var (tracker, clock) = Build();
        PerformanceTaskEndedEventArgs? report = null;
        tracker.TaskEnded += (_, e) => report = e;

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(tracker.Unblock(Chamber).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        Assert.NotNull(report);

        // Twenty minutes to run a recipe that needs fifteen, and the difference has a name.
        Assert.Equal(TimeSpan.FromMinutes(20), report.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(5), report.Blocked);

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(TimeSpan.FromMinutes(15), statistics.TimeExecuting);
        Assert.Equal(TimeSpan.FromMinutes(5), statistics.TimeBlocked);
    }

    [Fact]
    public void Two_blocks_in_one_task_add_up()
    {
        var (tracker, clock) = Build();
        PerformanceTaskEndedEventArgs? report = null;
        tracker.TaskEnded += (_, e) => report = e;

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(4));
            Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
            clock.Advance(TimeSpan.FromMinutes(3));
            Assert.True(tracker.Unblock(Chamber).IsAccepted);
        }

        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        Assert.NotNull(report);
        Assert.Equal(TimeSpan.FromMinutes(14), report.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(6), report.Blocked);
    }

    [Fact]
    public void A_reason_that_changes_mid_block_does_not_restart_the_blocked_clock()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.True(tracker.Block(Chamber, "INTERLOCK").IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(4));

        var statistics = tracker.Statistics(Chamber)!;

        // Ten minutes stuck, not four: the module never stopped being blocked, whatever it blames.
        Assert.Equal(TimeSpan.FromMinutes(10), statistics.TimeBlocked);
        Assert.Equal(TimeSpan.FromMinutes(10), statistics.TimeInState);
        AssertAddsUp(statistics, clock);
    }

    [Fact]
    public void A_task_ended_while_blocked_is_charged_to_that_moment_and_no_further()
    {
        var (tracker, clock) = Build();
        PerformanceTaskEndedEventArgs? report = null;
        tracker.TaskEnded += (_, e) => report = e;

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        Assert.NotNull(report);
        Assert.Equal(TimeSpan.FromMinutes(15), report.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(5), report.Blocked);

        // The module is still blocked and still counting, and the stretch is one stretch: the
        // five minutes already charged to the task must not be charged to the module twice when
        // it finally comes unstuck.
        clock.Advance(TimeSpan.FromMinutes(7));

        Assert.True(tracker.Unblock(Chamber).IsAccepted);

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(TimeSpan.FromMinutes(10), statistics.TimeExecuting);
        Assert.Equal(TimeSpan.FromMinutes(12), statistics.TimeBlocked);
        AssertAddsUp(statistics, clock);
    }

    [Fact]
    public void The_executing_share_is_the_number_the_standard_is_for()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(tracker.EndTask(Chamber).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(0.5d, tracker.Statistics(Chamber)!.ExecutingShare);
    }

    [Fact]
    public void A_window_with_no_time_in_it_divides_by_nothing()
    {
        var (tracker, _) = Build();

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(TimeSpan.Zero, statistics.Total);
        Assert.Equal(0d, statistics.ExecutingShare);
    }

    [Fact]
    public void Tasks_are_counted_by_how_they_ended()
    {
        var (tracker, clock) = Build();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(tracker.BeginTask(Chamber, $"{Recipe}-{i}").IsAccepted);
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.True(tracker.EndTask(Chamber).IsAccepted);
        }

        Assert.True(tracker.BeginTask(Chamber, "RECIPE-X").IsAccepted);
        Assert.True(tracker.GoIdle(Chamber).IsAccepted);

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(3, statistics.TasksCompleted);
        Assert.Equal(1, statistics.TasksAbandoned);
    }

    [Fact]
    public void Resetting_the_accounting_starts_the_window_again_without_moving_the_module()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        var enteredIdleAt = tracker.FindModule(Chamber)!.StateSince;

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(tracker.ResetStatistics(Chamber).IsAccepted);

        clock.Advance(TimeSpan.FromMinutes(15));

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(Start + TimeSpan.FromMinutes(30), statistics.Since);
        Assert.Equal(TimeSpan.FromMinutes(15), statistics.TimeIdle);
        Assert.Equal(TimeSpan.Zero, statistics.TimeExecuting);
        Assert.Equal(TimeSpan.FromMinutes(15), statistics.TimeInState);
        Assert.Equal(0, statistics.TasksCompleted);
        AssertAddsUp(statistics, clock);

        // The reset is about the counters and not about the machine: the module did not change
        // state, and it does not claim to have.
        Assert.Equal(PerformanceState.Idle, statistics.State);
        Assert.Equal(enteredIdleAt, tracker.FindModule(Chamber)!.StateSince);
    }

    [Fact]
    public void A_task_running_across_a_reset_keeps_its_own_start()
    {
        var (tracker, clock) = Build();
        PerformanceTaskEndedEventArgs? report = null;
        tracker.TaskEnded += (_, e) => report = e;

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(tracker.ResetStatistics(Chamber).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        Assert.NotNull(report);

        // Twenty minutes is the truth about the task even though the window is only ten; a task
        // that reported ten would be a lie about a wafer.
        Assert.Equal(TimeSpan.FromMinutes(20), report.Elapsed);
        Assert.Equal(TimeSpan.FromMinutes(10), tracker.Statistics(Chamber)!.Total);
    }

    [Fact]
    public void Resetting_one_module_leaves_the_others_alone()
    {
        var (tracker, clock) = Build();

        tracker.AddModule("PM2");
        clock.Advance(TimeSpan.FromMinutes(30));

        Assert.True(tracker.ResetStatistics(Chamber).IsAccepted);

        Assert.Equal(TimeSpan.Zero, tracker.Statistics(Chamber)!.Total);
        Assert.Equal(TimeSpan.FromMinutes(30), tracker.Statistics("PM2")!.Total);
    }

    [Fact]
    public void Statistics_for_a_module_nobody_declared_is_null()
    {
        var (tracker, _) = Build();

        Assert.Null(tracker.Statistics("PM9"));
    }

    [Fact]
    public void A_wall_clock_that_slips_cannot_touch_the_accounting()
    {
        var clock = new SlippingClock { Now = Start };
        var tracker = new PerformanceTracker(clock);
        List<PerformanceTaskEndedEventArgs> reports = [];
        tracker.TaskEnded += (_, e) => reports.Add(e);

        tracker.AddModule(Chamber);

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        var startedAt = tracker.FindModule(Chamber)!.StateSince;

        clock.Tick(TimeSpan.FromMinutes(30));

        // Somebody notices the machine's clock is two hours fast and corrects it. Nothing about
        // how long the chamber has been running has changed, and the figures must not pretend
        // otherwise - which is why durations are measured from the monotonic timestamp and only
        // the timestamps a host reads come from the wall clock.
        clock.Now = Start - TimeSpan.FromHours(2);

        var statistics = tracker.Statistics(Chamber)!;

        Assert.Equal(TimeSpan.FromMinutes(30), statistics.TimeExecuting);
        Assert.Equal(TimeSpan.FromMinutes(30), statistics.TimeInState);
        Assert.Equal(1d, statistics.ExecutingShare);
        Assert.Equal(startedAt, tracker.FindModule(Chamber)!.StateSince);

        // And a transition after the correction, which is the half this test did not cover until
        // a control broke the buckets and it passed anyway: the time charged to a state when the
        // module leaves it has to be the time it was really there, not the difference between two
        // readings of a clock somebody moved.
        Assert.True(tracker.EndTask(Chamber).IsAccepted);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(reports).Elapsed);

        clock.Tick(TimeSpan.FromMinutes(10));

        var after = tracker.Statistics(Chamber)!;

        Assert.Equal(TimeSpan.FromMinutes(30), after.TimeExecuting);
        Assert.Equal(TimeSpan.FromMinutes(10), after.TimeIdle);
        Assert.Equal(TimeSpan.FromMinutes(40), after.Total);
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
