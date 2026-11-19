// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceTracking.UnitTests;

/// <summary>
/// E116's three states and the rules about which call may follow which.
/// </summary>
/// <remarks>
/// The accounting is in <see cref="PerformanceAccountingTests"/>; this file is about the machine
/// itself, and mostly about what it refuses. The two rules worth reading the file for are that a
/// block keeps the task it interrupted, and that ending that task does not clear the block.
/// </remarks>
public class PerformanceTrackerTests
{
    private const string Chamber = "PM1";
    private const string Recipe = "RECIPE-7";
    private const string NoMaterial = "NO_MATERIAL";
    private const string Interlock = "INTERLOCK";

    private static (PerformanceTracker Tracker, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));
        var tracker = new PerformanceTracker(clock);

        tracker.AddModule(Chamber);

        return (tracker, clock);
    }

    [Fact]
    public void A_declared_module_is_idle_with_nothing_in_hand()
    {
        var (tracker, clock) = Build();

        var module = tracker.FindModule(Chamber);

        Assert.NotNull(module);
        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Equal(PerformanceState.Idle, module.PreviousState);
        Assert.Equal(clock.GetUtcNow(), module.StateSince);
        Assert.Null(module.CurrentTask);
        Assert.Null(module.BlockedReason);
        Assert.False(module.HasTask);
    }

    [Fact]
    public void Declaring_a_module_twice_returns_the_same_one_and_resets_nothing()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        var again = tracker.AddModule(Chamber);

        // The whole point: a second AddModule in a start-up path must not throw away what the
        // module was doing, which is the fault the DeclareOnce change fixed one chapter over.
        Assert.Same(tracker.FindModule(Chamber), again);
        Assert.Equal(PerformanceState.Executing, again.State);
        Assert.Single(tracker.Modules);
    }

    [Fact]
    public void A_module_nobody_declared_is_refused_by_every_call()
    {
        var (tracker, _) = Build();

        List<PerformanceResult> refusals =
        [
            tracker.BeginTask("PM9", Recipe),
            tracker.EndTask("PM9"),
            tracker.Block("PM9", NoMaterial),
            tracker.Unblock("PM9"),
            tracker.GoIdle("PM9"),
            tracker.ResetStatistics("PM9"),
        ];

        Assert.All(refusals, result =>
        {
            Assert.False(result.IsAccepted);
            Assert.Equal("there is no module 'PM9'.", result.Reason);
        });

        Assert.Null(tracker.Statistics("PM9"));
        Assert.Null(tracker.FindModule("PM9"));
    }

    [Fact]
    public void Beginning_a_task_moves_an_idle_module_to_executing()
    {
        var (tracker, clock) = Build();
        List<PerformanceStateChangedEventArgs> seen = [];
        tracker.StateChanged += (_, e) => seen.Add(e);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(tracker.BeginTask(Chamber, Recipe, PerformanceTaskTypes.Process).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Executing, module.State);
        Assert.Equal(PerformanceState.Idle, module.PreviousState);
        Assert.Equal(clock.GetUtcNow(), module.StateSince);
        Assert.True(module.HasTask);
        Assert.Equal(Recipe, module.CurrentTask!.Name);
        Assert.Equal(PerformanceTaskTypes.Process, module.CurrentTask.TaskType);
        Assert.Equal(clock.GetUtcNow(), module.CurrentTask.StartedAt);

        var change = Assert.Single(seen);

        Assert.Equal(PerformanceState.Idle, change.Previous);
        Assert.Equal(PerformanceState.Executing, change.Current);
        Assert.Same(module, change.Module);
    }

    [Fact]
    public void A_task_needs_a_name()
    {
        var (tracker, _) = Build();

        var refusal = tracker.BeginTask(Chamber, string.Empty, PerformanceTaskTypes.Process);

        Assert.False(refusal.IsAccepted);
        Assert.Equal("module 'PM1' cannot begin a task with no name.", refusal.Reason);
        Assert.Equal(PerformanceState.Idle, tracker.FindModule(Chamber)!.State);
    }

    [Fact]
    public void A_module_already_executing_will_not_begin_another_task()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        var refusal = tracker.BeginTask(Chamber, "RECIPE-8");

        Assert.False(refusal.IsAccepted);

        // The whole refusal, because a swap would end one task's accounting without reporting it
        // and that is the one thing this package exists to do.
        Assert.Equal(
            "module 'PM1' is Executing task 'RECIPE-7'; end that task before beginning another.",
            refusal.Reason);
        Assert.Equal(Recipe, tracker.FindModule(Chamber)!.CurrentTask!.Name);
    }

    [Fact]
    public void A_blocked_module_will_not_begin_a_task()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.Block(Chamber, NoMaterial, "the load port is empty").IsAccepted);

        var refusal = tracker.BeginTask(Chamber, Recipe);

        Assert.False(refusal.IsAccepted);
        Assert.Equal(
            "module 'PM1' is Blocked (NO_MATERIAL); unblock it before it can begin a task.",
            refusal.Reason);
        Assert.False(tracker.FindModule(Chamber)!.HasTask);
    }

    [Fact]
    public void Ending_a_task_leaves_an_executing_module_idle()
    {
        var (tracker, clock) = Build();
        List<PerformanceTaskEndedEventArgs> ended = [];
        tracker.TaskEnded += (_, e) => ended.Add(e);

        Assert.True(tracker.BeginTask(Chamber, Recipe, PerformanceTaskTypes.Process).IsAccepted);

        clock.Advance(TimeSpan.FromMinutes(12));

        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Equal(PerformanceState.Executing, module.PreviousState);
        Assert.Null(module.CurrentTask);

        var report = Assert.Single(ended);

        Assert.Equal(Recipe, report.Task.Name);
        Assert.Equal(TimeSpan.FromMinutes(12), report.Elapsed);
        Assert.Equal(TimeSpan.Zero, report.Blocked);
        Assert.True(report.Completed);
    }

    [Fact]
    public void A_module_with_no_task_has_nothing_to_end()
    {
        var (tracker, _) = Build();

        var idle = tracker.EndTask(Chamber);

        Assert.False(idle.IsAccepted);
        Assert.Equal("module 'PM1' is Idle and has no task to end.", idle.Reason);

        Assert.True(tracker.Block(Chamber, Interlock).IsAccepted);

        var blocked = tracker.EndTask(Chamber);

        Assert.False(blocked.IsAccepted);
        Assert.Equal("module 'PM1' is Blocked and has no task to end.", blocked.Reason);
    }

    [Fact]
    public void Blocking_needs_a_reason()
    {
        var (tracker, _) = Build();

        var refusal = tracker.Block(Chamber, string.Empty, "something is wrong");

        Assert.False(refusal.IsAccepted);
        Assert.Equal("module 'PM1' cannot be Blocked for no reason.", refusal.Reason);
        Assert.Equal(PerformanceState.Idle, tracker.FindModule(Chamber)!.State);
    }

    [Fact]
    public void Blocking_an_executing_module_keeps_its_task()
    {
        var (tracker, clock) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        var task = tracker.FindModule(Chamber)!.CurrentTask;

        clock.Advance(TimeSpan.FromMinutes(3));

        Assert.True(tracker.Block(Chamber, NoMaterial, "waiting for the next wafer").IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        // The rule the standard is for: a chamber stopped half way through a job is still that
        // job's chamber, so the time it stands there belongs to the job.
        Assert.Equal(PerformanceState.Blocked, module.State);
        Assert.True(module.HasTask);
        Assert.Same(task, module.CurrentTask);
        Assert.Equal(NoMaterial, module.BlockedReason!.Code);
        Assert.Equal("waiting for the next wafer", module.BlockedReason.Text);
        Assert.Equal(clock.GetUtcNow(), module.BlockedReason.Since);
    }

    [Fact]
    public void Blocking_for_the_same_reason_twice_is_refused()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);

        var refusal = tracker.Block(Chamber, NoMaterial, "and still no material");

        Assert.False(refusal.IsAccepted);
        Assert.Equal("module 'PM1' is already Blocked (NO_MATERIAL).", refusal.Reason);

        // Nothing was written, so the text of the first block still stands.
        Assert.Equal(string.Empty, tracker.FindModule(Chamber)!.BlockedReason!.Text);
    }

    [Fact]
    public void Blocking_for_a_different_reason_changes_the_reason_and_not_the_state()
    {
        var (tracker, clock) = Build();
        List<PerformanceStateChangedEventArgs> moves = [];
        List<BlockedReasonChangedEventArgs> reasons = [];
        tracker.StateChanged += (_, e) => moves.Add(e);
        tracker.BlockedReasonChanged += (_, e) => reasons.Add(e);

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);

        var blockedAt = tracker.FindModule(Chamber)!.StateSince;

        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.True(tracker.Block(Chamber, Interlock, "the door is open").IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Blocked, module.State);
        Assert.Equal(Interlock, module.BlockedReason!.Code);

        // The module never stopped being blocked, so its clock did not restart - only the reason
        // has a new Since.
        Assert.Equal(blockedAt, module.StateSince);
        Assert.Equal(clock.GetUtcNow(), module.BlockedReason.Since);

        Assert.Single(moves);

        var change = Assert.Single(reasons);

        Assert.Equal(NoMaterial, change.Previous.Code);
        Assert.Equal(Interlock, change.Current.Code);
    }

    [Fact]
    public void Unblocking_resumes_a_task_that_survived()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        var task = tracker.FindModule(Chamber)!.CurrentTask;

        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        Assert.True(tracker.Unblock(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Executing, module.State);
        Assert.Same(task, module.CurrentTask);
        Assert.Null(module.BlockedReason);
    }

    [Fact]
    public void Unblocking_a_module_that_had_nothing_in_hand_goes_back_to_idle()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.Block(Chamber, Interlock).IsAccepted);
        Assert.True(tracker.Unblock(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        // Derived rather than asked for: a caller that could choose could say the module resumed
        // a task it does not have.
        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Null(module.CurrentTask);
        Assert.Null(module.BlockedReason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_module_that_is_not_blocked_cannot_be_unblocked(bool executing)
    {
        var (tracker, _) = Build();

        if (executing)
        {
            Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        }

        var refusal = tracker.Unblock(Chamber);

        Assert.False(refusal.IsAccepted);
        Assert.Equal(
            executing ? "module 'PM1' is Executing, not Blocked." : "module 'PM1' is Idle, not Blocked.",
            refusal.Reason);
    }

    [Fact]
    public void Ending_a_task_on_a_blocked_module_leaves_it_blocked()
    {
        var (tracker, clock) = Build();
        List<PerformanceStateChangedEventArgs> moves = [];
        List<PerformanceTaskEndedEventArgs> ended = [];
        tracker.StateChanged += (_, e) => moves.Add(e);
        tracker.TaskEnded += (_, e) => ended.Add(e);

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.True(tracker.Block(Chamber, Interlock, "the door is open").IsAccepted);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        // An interlock does not close because a host cancelled a job. The work is over; the
        // obstruction is not, and saying otherwise would hide the only fact anybody can act on.
        Assert.Equal(PerformanceState.Blocked, module.State);
        Assert.Equal(Interlock, module.BlockedReason!.Code);
        Assert.Null(module.CurrentTask);
        Assert.False(module.HasTask);

        // Two moves so far - into Executing and into Blocked - and the ending was neither.
        Assert.Equal(2, moves.Count);
        Assert.True(Assert.Single(ended).Completed);

        Assert.True(tracker.Unblock(Chamber).IsAccepted);
        Assert.Equal(PerformanceState.Idle, module.State);
    }

    [Fact]
    public void Going_idle_abandons_the_task_and_says_so()
    {
        var (tracker, clock) = Build();
        List<PerformanceTaskEndedEventArgs> ended = [];
        tracker.TaskEnded += (_, e) => ended.Add(e);

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);

        clock.Advance(TimeSpan.FromMinutes(7));

        Assert.True(tracker.GoIdle(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Null(module.CurrentTask);

        var report = Assert.Single(ended);

        Assert.False(report.Completed);
        Assert.Equal(TimeSpan.FromMinutes(7), report.Elapsed);
        Assert.Equal(1, tracker.Statistics(Chamber)!.TasksAbandoned);
        Assert.Equal(0, tracker.Statistics(Chamber)!.TasksCompleted);
    }

    [Fact]
    public void Going_idle_clears_a_block_because_it_is_a_claim_about_the_end_state()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.Block(Chamber, Interlock).IsAccepted);
        Assert.True(tracker.GoIdle(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Null(module.BlockedReason);
    }

    [Fact]
    public void Going_idle_while_blocked_half_way_through_a_task_abandons_it_and_clears_the_block()
    {
        var (tracker, _) = Build();
        List<PerformanceTaskEndedEventArgs> ended = [];
        tracker.TaskEnded += (_, e) => ended.Add(e);

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        Assert.True(tracker.GoIdle(Chamber).IsAccepted);

        var module = tracker.FindModule(Chamber)!;

        Assert.Equal(PerformanceState.Idle, module.State);
        Assert.Null(module.BlockedReason);
        Assert.Null(module.CurrentTask);
        Assert.False(Assert.Single(ended).Completed);
    }

    [Fact]
    public void A_module_that_is_already_idle_is_refused()
    {
        var (tracker, _) = Build();

        var refusal = tracker.GoIdle(Chamber);

        Assert.False(refusal.IsAccepted);

        // Refused rather than forgiven, because forgiving it is a clock that restarts whenever
        // somebody asks a question.
        Assert.Equal("module 'PM1' is already Idle.", refusal.Reason);
    }

    [Fact]
    public void Every_state_event_sees_the_state_it_announces()
    {
        var (tracker, _) = Build();
        List<string> readings = [];

        tracker.StateChanged += (_, e) =>
        {
            Assert.Equal(e.Current, e.Module.State);
            Assert.Equal(e.Previous, e.Module.PreviousState);
            readings.Add($"{e.Previous}->{e.Current}");
        };

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.True(tracker.Block(Chamber, NoMaterial).IsAccepted);
        Assert.True(tracker.Unblock(Chamber).IsAccepted);
        Assert.True(tracker.EndTask(Chamber).IsAccepted);
        Assert.True(tracker.Block(Chamber, Interlock).IsAccepted);
        Assert.True(tracker.GoIdle(Chamber).IsAccepted);

        Assert.Equal(
            [
                "Idle->Executing",
                "Executing->Blocked",
                "Blocked->Executing",
                "Executing->Idle",
                "Idle->Blocked",
                "Blocked->Idle",
            ],
            readings);
    }

    [Fact]
    public void A_refused_call_raises_nothing()
    {
        var (tracker, _) = Build();
        var raised = 0;

        tracker.StateChanged += (_, _) => raised++;
        tracker.TaskEnded += (_, _) => raised++;
        tracker.BlockedReasonChanged += (_, _) => raised++;

        Assert.False(tracker.EndTask(Chamber).IsAccepted);
        Assert.False(tracker.Unblock(Chamber).IsAccepted);
        Assert.False(tracker.GoIdle(Chamber).IsAccepted);
        Assert.False(tracker.Block(Chamber, string.Empty).IsAccepted);
        Assert.False(tracker.BeginTask(Chamber, string.Empty).IsAccepted);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void A_task_type_nobody_standardised_is_not_an_error()
    {
        var (tracker, _) = Build();

        // The vocabulary belongs to the fab, as it does for a carrier action: a closed enum here
        // would cap what a machine can say about itself.
        Assert.True(tracker.BeginTask(Chamber, "SPIN-DRY", "HouseKeeping").IsAccepted);
        Assert.Equal("HouseKeeping", tracker.FindModule(Chamber)!.CurrentTask!.TaskType);
    }

    [Fact]
    public void A_task_with_no_type_is_allowed_and_reads_as_empty()
    {
        var (tracker, _) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.Equal(string.Empty, tracker.FindModule(Chamber)!.CurrentTask!.TaskType);
    }

    [Fact]
    public void Modules_are_kept_apart()
    {
        var (tracker, _) = Build();

        tracker.AddModule("PM2");

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.True(tracker.Block("PM2", NoMaterial).IsAccepted);

        Assert.Equal(PerformanceState.Executing, tracker.FindModule(Chamber)!.State);
        Assert.Equal(PerformanceState.Blocked, tracker.FindModule("PM2")!.State);
        Assert.Equal(2, tracker.Modules.Count);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var (tracker, _) = Build();

        Assert.Throws<ArgumentNullException>(() => tracker.AddModule(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.FindModule(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.BeginTask(null!, Recipe));
        Assert.Throws<ArgumentNullException>(() => tracker.BeginTask(Chamber, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.EndTask(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Block(null!, NoMaterial));
        Assert.Throws<ArgumentNullException>(() => tracker.Block(Chamber, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Block(Chamber, NoMaterial, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Unblock(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.GoIdle(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Statistics(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.ResetStatistics(null!));
    }

    [Fact]
    public void A_tracker_with_no_clock_still_works()
    {
        // TimeProvider.System by default, which is what an application that never thought about
        // the clock gets - and it must not be a null reference.
        var tracker = new PerformanceTracker();

        tracker.AddModule(Chamber);

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.True(tracker.EndTask(Chamber).IsAccepted);
        Assert.Equal(1, tracker.Statistics(Chamber)!.TasksCompleted);
    }
}
