// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceTracking.UnitTests;

/// <summary>
/// A tracker that outlives the process: the ledger comes back and the claim does not.
/// </summary>
/// <remarks>
/// E116's accumulated times and task counts are a record of what happened, so a restart keeps
/// them. What a module was <em>doing</em> is a statement about now, and a restarted module is
/// doing nothing - so every one comes back idle, with no task and no reason, and a task that was
/// running is counted as abandoned because nobody is ever going to end it.
/// <para>
/// The interesting half is what happens to the time the machine was off. E116 has three states and
/// none of them means switched off, so it is charged to none of them and handed back instead -
/// which is the opposite of what <c>Barbatos.SecsGem.Productivity</c> does, for a reason that is
/// about the standards rather than about taste, and there is a test here for the consequence.
/// </para>
/// </remarks>
public class PerformanceStateTests
{
    private const string Chamber = "PM1";
    private const string Second = "PM2";

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private static (PerformanceTracker Tracker, FakeTimeProvider Clock) Build(params string[] modules)
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new PerformanceTracker(clock);

        foreach (var module in modules.Length > 0 ? modules : [Chamber])
        {
            tracker.AddModule(module);
        }

        return (tracker, clock);
    }

    [Fact]
    public void The_ledger_comes_back()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(tracker.BeginTask(Chamber, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        var before = tracker.Statistics(Chamber)!;
        var snapshot = tracker.Capture();

        var (restarted, _) = Build();
        var report = restarted.Restore(snapshot);

        Assert.True(report.IsAccepted);
        Assert.Equal(Chamber, Assert.Single(report.Restored));

        var after = restarted.Statistics(Chamber)!;

        Assert.Equal(before.TimeIdle, after.TimeIdle);
        Assert.Equal(before.TimeExecuting, after.TimeExecuting);
        Assert.Equal(before.TimeBlocked, after.TimeBlocked);
        Assert.Equal(before.TasksCompleted, after.TasksCompleted);
        Assert.Equal(before.TasksAbandoned, after.TasksAbandoned);
        Assert.Equal(before.Since, after.Since);
    }

    [Fact]
    public void A_task_the_restart_interrupted_was_abandoned_and_the_count_says_so()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.BeginTask(Chamber, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(5));

        var snapshot = tracker.Capture();

        Assert.Equal("RECIPE-7", snapshot.Modules[0].TaskName);

        var (restarted, _) = Build();
        var report = restarted.Restore(snapshot);
        var module = restarted.FindModule(Chamber)!;

        // Nobody is ever going to call EndTask for that recipe, so leaving the count alone would
        // make a machine that reboots mid-recipe look like one that always finishes.
        Assert.Equal(1, restarted.Statistics(Chamber)!.TasksAbandoned);
        Assert.Equal(0, restarted.Statistics(Chamber)!.TasksCompleted);
        Assert.False(module.HasTask);
        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Contains("abandoned", report.Adjusted[Chamber], StringComparison.Ordinal);
    }

    [Fact]
    public void A_blocked_module_comes_back_idle_with_no_reason()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.Block(Chamber, "INTERLOCK", "the door is open").IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(7));

        var snapshot = tracker.Capture();

        Assert.Equal("INTERLOCK", snapshot.Modules[0].BlockedReasonCode);

        var (restarted, _) = Build();
        var report = restarted.Restore(snapshot);
        var module = restarted.FindModule(Chamber)!;

        // A restarted machine has not looked at the door. Claiming the interlock is still closed
        // would be a reading the fab would act on, and claiming it is open would be another.
        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Null(module.BlockedReason);
        Assert.Equal(PerformanceState.Blocked, module.PreviousState);
        Assert.Contains("INTERLOCK", report.Adjusted[Chamber], StringComparison.Ordinal);
    }

    [Fact]
    public void The_time_the_machine_was_off_is_handed_back_and_charged_to_nothing()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromMinutes(30));

        var snapshot = tracker.Capture();

        clock.Advance(TimeSpan.FromHours(2));

        var restarted = new PerformanceTracker(clock);

        restarted.AddModule(Chamber);

        var report = restarted.Restore(snapshot);

        Assert.Equal(TimeSpan.FromHours(2), report.Unaccounted);

        var after = restarted.Statistics(Chamber)!;

        // Half an hour of idle, and not two and a half: the two hours went nowhere, because E116
        // has no state that means switched off.
        Assert.Equal(TimeSpan.FromMinutes(30), after.TimeIdle);
        Assert.Equal(TimeSpan.Zero, after.TimeBlocked);
        Assert.Equal(TimeSpan.Zero, after.TimeExecuting);

        // Which is the consequence worth knowing rather than discovering: the three times no
        // longer add up to the window, by exactly the outage.
        Assert.Equal(
            TimeSpan.FromHours(2),
            (clock.GetUtcNow() - after.Since) - (after.TimeIdle + after.TimeExecuting + after.TimeBlocked)
                - after.TimeInState);
    }

    [Fact]
    public void A_restored_module_carries_on_from_where_the_ledger_left_off()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(tracker.BeginTask(Chamber, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        var (restarted, restartedClock) = Build();

        Assert.True(restarted.Restore(tracker.Capture()).IsAccepted);
        Assert.Equal(1, restarted.Statistics(Chamber)!.TasksCompleted);

        Assert.True(restarted.BeginTask(Chamber, "RECIPE-8", PerformanceTaskTypes.Process).IsAccepted);
        restartedClock.Advance(TimeSpan.FromMinutes(15));
        Assert.True(restarted.EndTask(Chamber).IsAccepted);

        // Two tasks, one from before the restart and one from after, and thirty-five minutes of
        // execution across both - which is what a ledger that came back means.
        Assert.Equal(2, restarted.Statistics(Chamber)!.TasksCompleted);
        Assert.Equal(TimeSpan.FromMinutes(35), restarted.Statistics(Chamber)!.TimeExecuting);
    }

    [Fact]
    public void A_snapshot_does_not_invent_hardware()
    {
        var (tracker, clock) = Build(Chamber, Second);

        clock.Advance(TimeSpan.FromMinutes(5));

        var snapshot = tracker.Capture();
        var (oneChamber, _) = Build(Chamber);

        var report = oneChamber.Restore(snapshot);

        Assert.True(report.IsAccepted);
        Assert.Equal(Chamber, Assert.Single(report.Restored));
        Assert.Contains("declared from", report.Adjusted[Second], StringComparison.Ordinal);
        Assert.Null(oneChamber.FindModule(Second));
    }

    [Fact]
    public void A_tracker_that_has_already_accounted_for_something_refuses()
    {
        var (tracker, clock) = Build();

        clock.Advance(TimeSpan.FromMinutes(5));

        var snapshot = tracker.Capture();
        var (busy, busyClock) = Build();

        Assert.True(busy.BeginTask(Chamber, "RECIPE-1").IsAccepted);
        busyClock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(busy.EndTask(Chamber).IsAccepted);

        var report = busy.Restore(snapshot);

        Assert.False(report.IsAccepted);
        Assert.Contains("already accounted", report.Reason, StringComparison.Ordinal);
        Assert.Equal(1, busy.Statistics(Chamber)!.TasksCompleted);
    }

    [Fact]
    public void A_tracker_with_a_task_in_progress_refuses_too()
    {
        var (tracker, _) = Build();
        var (busy, _) = Build();

        Assert.True(busy.BeginTask(Chamber, "RECIPE-1").IsAccepted);

        Assert.False(busy.Restore(tracker.Capture()).IsAccepted);
    }

    [Fact]
    public void An_empty_tracker_takes_a_snapshot_of_nothing()
    {
        var (tracker, _) = Build();

        var report = tracker.Restore(new PerformanceTrackerSnapshot());

        Assert.True(report.IsAccepted);
        Assert.Empty(report.Restored);
        Assert.Equal(TimeSpan.Zero, report.Unaccounted);
    }

    [Fact]
    public void A_snapshot_nobody_passed_is_rejected()
    {
        var (tracker, _) = Build();

        Assert.Throws<ArgumentNullException>(() => tracker.Restore(null!));
    }

    [Fact]
    public void A_capture_is_a_faithful_record_even_of_what_a_restore_will_drop()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.Block(Chamber, "NO_MATERIAL").IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(3));

        var snapshot = tracker.Capture();
        var module = snapshot.Modules[0];

        // The file says what was true. Restore decides what a restarted machine may claim, and
        // keeping those two apart is what makes the second one testable at all.
        Assert.Equal(PerformanceState.Blocked, module.State);
        Assert.Equal("NO_MATERIAL", module.BlockedReasonCode);
        Assert.Equal(Start, module.AccountingSince);
        Assert.Equal(Start, module.StateSince);
        Assert.Equal(Start.AddMinutes(3), snapshot.CapturedAt);
    }
}
