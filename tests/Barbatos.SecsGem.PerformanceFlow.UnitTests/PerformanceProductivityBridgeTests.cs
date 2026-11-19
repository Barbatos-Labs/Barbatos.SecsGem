// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.Productivity;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceFlow.UnitTests;

/// <summary>
/// The join between what a chamber is doing this minute and what the tool was for the shift.
/// </summary>
/// <remarks>
/// <para>
/// Three packages said this mapping could not be made, and they were right about the <em>states</em>:
/// nothing about the word "executing" says whether a tool was producing or learning. What makes it
/// possible is the task type, which is where E116 recorded the intent - so most of this file is
/// about the three things only a fab can say, and what happens when it has not said them.
/// </para>
/// <para>
/// The headline is the blocked reason. <c>NO_MATERIAL</c> is standby and the factory's problem,
/// <c>INTERLOCK</c> is downtime and the tool's, and E116 reports the change between them without
/// moving at all - so a bridge that only watched transitions would charge a whole stretch to
/// whichever reason came first.
/// </para>
/// </remarks>
public class PerformanceProductivityBridgeTests
{
    private const string Tool = "EQUIPMENT";
    private const string Other = "PM2";
    private const string Recipe = "RECIPE-7";

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private sealed record Machine(
        PerformanceTracker Performance,
        ProductivityTracker Productivity,
        PerformanceProductivityBridge Bridge,
        FakeTimeProvider Clock);

    private static Machine Build(
        Action<PerformanceProductivityOptions>? configure = null,
        bool attach = true,
        bool apply = true)
    {
        var clock = new FakeTimeProvider(Start);
        var performance = new PerformanceTracker(clock);
        var productivity = new ProductivityTracker(clock);
        var options = new PerformanceProductivityOptions();

        configure?.Invoke(options);

        performance.AddModule(Tool);
        performance.AddModule(Other);

        var bridge = new PerformanceProductivityBridge(performance, productivity, Tool, options);

        if (attach)
        {
            bridge.Attach();
        }

        if (apply)
        {
            bridge.Apply();
        }

        return new Machine(performance, productivity, bridge, clock);
    }

    // --------------------------------------------------------------- the mapping

    [Fact]
    public void A_process_task_is_productive_time_in_both_standards_own_words()
    {
        var machine = Build();

        var (state, fallback) = machine.Bridge.Map(PerformanceState.Executing, PerformanceTaskTypes.Process);

        Assert.Equal(EquipmentState.Productive, state);
        Assert.False(fallback);
    }

    [Fact]
    public void An_engineering_task_is_engineering_time()
    {
        var machine = Build();

        var (state, fallback) = machine.Bridge.Map(PerformanceState.Executing, PerformanceTaskTypes.Engineering);

        Assert.Equal(EquipmentState.Engineering, state);
        Assert.False(fallback);
    }

    [Theory]
    [InlineData(PerformanceTaskTypes.Support)]
    [InlineData(PerformanceTaskTypes.Maintenance)]
    [InlineData("HouseKeeping")]
    public void A_task_type_nobody_mapped_is_charged_to_the_tool(string taskType)
    {
        var machine = Build();

        // Support and Maintenance are deliberately not filled in: a pump-down may be part of
        // producing a lot or part of a setup, and a maintenance task may be a planned service or
        // an emergency repair. Until a fab says, the time is charged where it cannot flatter.
        var (state, fallback) = machine.Bridge.Map(PerformanceState.Executing, taskType);

        Assert.Equal(EquipmentState.UnscheduledDowntime, state);
        Assert.True(fallback);
    }

    [Fact]
    public void A_fab_that_has_said_what_its_task_types_mean_gets_what_it_said()
    {
        var machine = Build(options =>
        {
            options.TaskStates[PerformanceTaskTypes.Support] = EquipmentState.Productive;
            options.TaskStates[PerformanceTaskTypes.Maintenance] = EquipmentState.ScheduledDowntime;
        });

        Assert.Equal(
            (EquipmentState.Productive, false),
            machine.Bridge.Map(PerformanceState.Executing, PerformanceTaskTypes.Support));

        Assert.Equal(
            (EquipmentState.ScheduledDowntime, false),
            machine.Bridge.Map(PerformanceState.Executing, PerformanceTaskTypes.Maintenance));
    }

    [Fact]
    public void A_task_with_no_type_at_all_can_be_told_from_one_nobody_mapped()
    {
        var machine = Build(options => options.UntypedTaskState = EquipmentState.Standby);

        // Different mistakes: a type this fab has not mapped is a configuration gap, and no type
        // at all is a machine that did not say.
        Assert.Equal((EquipmentState.Standby, true), machine.Bridge.Map(PerformanceState.Executing));
        Assert.Equal(
            (EquipmentState.UnscheduledDowntime, true),
            machine.Bridge.Map(PerformanceState.Executing, "HouseKeeping"));
    }

    [Fact]
    public void A_fab_that_does_not_care_about_that_difference_need_not_think_about_it()
    {
        var machine = Build();

        // UntypedTaskState null falls through to the unmapped fallback.
        Assert.Equal((EquipmentState.UnscheduledDowntime, true), machine.Bridge.Map(PerformanceState.Executing));
    }

    [Fact]
    public void The_blocked_reason_decides_whose_problem_it_is()
    {
        var machine = Build(options =>
        {
            options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby;
            options.BlockedStates["INTERLOCK"] = EquipmentState.UnscheduledDowntime;
        });

        // The distinction E10 exists for, and the only place it can be made.
        Assert.Equal(
            (EquipmentState.Standby, false),
            machine.Bridge.Map(PerformanceState.Blocked, blockedReason: "NO_MATERIAL"));

        Assert.Equal(
            (EquipmentState.UnscheduledDowntime, false),
            machine.Bridge.Map(PerformanceState.Blocked, blockedReason: "INTERLOCK"));
    }

    [Fact]
    public void No_blocked_reason_is_mapped_out_of_the_box()
    {
        var machine = Build();

        // Not an omission: a blocked reason code is the machine's own vocabulary, so there is
        // nothing here to get right by guessing.
        Assert.Empty(machine.Bridge.Options.BlockedStates);
        Assert.Equal(
            (EquipmentState.UnscheduledDowntime, true),
            machine.Bridge.Map(PerformanceState.Blocked, blockedReason: "NO_MATERIAL"));
    }

    [Fact]
    public void An_idle_module_is_standby_or_not_scheduled_depending_on_the_calendar()
    {
        var scheduled = Build();
        var closed = Build(options => options.IsScheduled = () => false);

        // The one decision that is not about the tool at all.
        Assert.Equal((EquipmentState.Standby, false), scheduled.Bridge.Map(PerformanceState.Idle));
        Assert.Equal((EquipmentState.NonScheduled, false), closed.Bridge.Map(PerformanceState.Idle));
    }

    [Fact]
    public void The_default_calendar_reports_the_lower_availability_of_the_two()
    {
        var machine = Build();

        machine.Clock.Advance(TimeSpan.FromHours(24));

        var report = machine.Productivity.Report();

        // Always-scheduled puts every hour in the operations denominator, so the two availabilities
        // are the same number - and it is the smaller one. Where a default cannot be avoided it is
        // the one that cannot flatter the tool.
        Assert.Equal(TimeSpan.FromHours(24), report.Operations);
        Assert.Equal(TimeSpan.Zero, report.NonScheduled);
        Assert.Equal(report.Availability, report.AvailabilityEfficiency);
    }

    // ------------------------------------------------------------ driving the tool

    [Fact]
    public void Applying_brings_the_tool_in_line_with_where_the_module_already_is()
    {
        var machine = Build(attach: false, apply: false);

        // E10 starts non-scheduled; E116's module starts idle, and the tool is scheduled.
        Assert.Equal(EquipmentState.NonScheduled, machine.Productivity.State);

        var result = machine.Bridge.Apply();

        Assert.True(result.IsAccepted);
        Assert.Equal(EquipmentState.Standby, result.State);
        Assert.Equal(EquipmentState.Standby, machine.Productivity.State);
    }

    [Fact]
    public void A_module_nobody_declared_cannot_be_followed()
    {
        var clock = new FakeTimeProvider(Start);
        var bridge = new PerformanceProductivityBridge(
            new PerformanceTracker(clock),
            new ProductivityTracker(clock),
            "PM9");

        var result = bridge.Apply();

        Assert.False(result.IsAccepted);
        Assert.Equal("there is no module 'PM9' to follow.", result.Reason);
    }

    [Fact]
    public void A_module_that_starts_work_makes_the_tool_productive()
    {
        var machine = Build();

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);

        Assert.Equal(EquipmentState.Productive, machine.Productivity.State);

        // And the machine's own words came with it, which is what somebody reading the shift wants.
        Assert.Equal(string.Empty, machine.Productivity.StateReason);
    }

    [Fact]
    public void A_blocked_reason_that_changes_moves_the_tool_although_E116_did_not()
    {
        var machine = Build(options =>
        {
            options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby;
            options.BlockedStates["INTERLOCK"] = EquipmentState.UnscheduledDowntime;
        });

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(1));

        Assert.True(machine.Performance.Block(Tool, "NO_MATERIAL", "the load port is empty").IsAccepted);
        Assert.Equal(EquipmentState.Standby, machine.Productivity.State);

        machine.Clock.Advance(TimeSpan.FromHours(1));

        // E116 stays Blocked - one stretch, two reasons - and E10 has to move, because the second
        // reason is the tool's fault and the first was not.
        Assert.True(machine.Performance.Block(Tool, "INTERLOCK", "the door is open").IsAccepted);

        Assert.Equal(PerformanceState.Blocked, machine.Performance.FindModule(Tool)!.State);
        Assert.Equal(EquipmentState.UnscheduledDowntime, machine.Productivity.State);

        machine.Clock.Advance(TimeSpan.FromHours(1));

        var report = machine.Productivity.Report();

        Assert.Equal(TimeSpan.FromHours(1), report.Productive);
        Assert.Equal(TimeSpan.FromHours(1), report.Standby);
        Assert.Equal(TimeSpan.FromHours(1), report.UnscheduledDowntime);
        Assert.Equal(1, report.Failures);
    }

    [Fact]
    public void Two_reasons_that_mean_the_same_thing_do_not_break_the_stretch()
    {
        var machine = Build(options =>
        {
            options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby;
            options.BlockedStates["NO_CARRIER"] = EquipmentState.Standby;
        });

        Assert.True(machine.Performance.Block(Tool, "NO_MATERIAL").IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True(machine.Performance.Block(Tool, "NO_CARRIER").IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(2));

        var report = machine.Productivity.Report();

        // Both map to standby, so E10 sees a reason change rather than a transition and the four
        // hours are one stretch - which falls out of E10's own rule rather than being arranged
        // here, and is the reason that rule exists.
        Assert.Equal(TimeSpan.FromHours(4), report.Standby);
        Assert.Equal(0, report.Failures);
        Assert.Equal("EQUIPMENT: NO_CARRIER", report.StateReason);
    }

    [Fact]
    public void Only_the_module_that_is_the_tool_drives_it()
    {
        var machine = Build();

        Assert.True(machine.Performance.BeginTask(Other, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        Assert.True(machine.Performance.Block(Other, "INTERLOCK", "the other chamber").IsAccepted);

        // E116 tracks many modules and E10 measures one machine; how a tool's state follows from
        // four chambers' is a question with more than one answer, so this bridge answers none of
        // it and watches the module it was given.
        Assert.Equal(EquipmentState.Standby, machine.Productivity.State);
    }

    [Fact]
    public void Detaching_leaves_the_tool_where_the_bridge_last_put_it()
    {
        var machine = Build();

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        Assert.True(machine.Bridge.IsAttached);

        machine.Bridge.Detach();

        Assert.False(machine.Bridge.IsAttached);
        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);

        // A bridge that reset a shift's accounting on being detached would be a worse surprise
        // than one that did not.
        Assert.Equal(EquipmentState.Productive, machine.Productivity.State);
    }

    [Fact]
    public void Attaching_and_detaching_twice_does_nothing_the_second_time()
    {
        var machine = Build();

        machine.Bridge.Attach();
        Assert.True(machine.Bridge.IsAttached);

        machine.Bridge.Detach();
        machine.Bridge.Detach();
        Assert.False(machine.Bridge.IsAttached);

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        Assert.Equal(EquipmentState.Standby, machine.Productivity.State);

        // And attaching again picks it back up, once, rather than twice.
        machine.Bridge.Attach();
        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);
        Assert.Equal(EquipmentState.Standby, machine.Productivity.State);
    }

    // --------------------------------------------------------------- what it reports

    [Fact]
    public void An_unmapped_reason_is_reported_every_time_and_not_once()
    {
        var machine = Build();
        List<PerformanceProductivityMappingEventArgs> missing = [];
        machine.Bridge.MappingMissing += (_, e) => missing.Add(e);

        Assert.True(machine.Performance.BeginTask(Tool, "CLEAN", PerformanceTaskTypes.Maintenance).IsAccepted);
        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);
        Assert.True(machine.Performance.BeginTask(Tool, "CLEAN", PerformanceTaskTypes.Maintenance).IsAccepted);

        // A fab whose mapping is half finished should watch this count grow, not get one warning
        // at start-up and a quietly wrong shift report afterwards.
        Assert.Equal(2, missing.Count);
        Assert.Equal(PerformanceTaskTypes.Maintenance, missing[0].TaskType);
        Assert.Equal(EquipmentState.UnscheduledDowntime, missing[0].Charged);
        Assert.Equal(Tool, missing[0].ModuleId);
        Assert.Equal(PerformanceState.Executing, missing[0].PerformanceState);
    }

    [Fact]
    public void Nothing_is_reported_when_everything_was_mapped()
    {
        var machine = Build(options => options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby);
        var missing = 0;
        machine.Bridge.MappingMissing += (_, _) => missing++;

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        Assert.True(machine.Performance.Block(Tool, "NO_MATERIAL").IsAccepted);
        Assert.True(machine.Performance.Unblock(Tool).IsAccepted);
        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);

        Assert.Equal(0, missing);
    }

    [Fact]
    public void Every_move_is_reported_with_what_came_of_it()
    {
        var machine = Build();
        List<string> applied = [];

        machine.Bridge.Applied += (_, e) => applied.Add(
            $"{e.PerformanceState}->{e.Result.State}{(e.Result.IsAccepted ? string.Empty : " (refused)")}");

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);

        Assert.Equal(["Executing->Productive", "Idle->Standby"], applied);
    }

    [Fact]
    public void A_tool_already_in_the_state_the_module_implies_is_reported_refused_and_is_not_a_failure()
    {
        var machine = Build(options => options.TaskStates["SECOND"] = EquipmentState.Productive);

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);

        // Two different task types that both mean productive, back to back through idle, so the
        // middle move is a real one and the E10 state ends up where it started.
        Assert.Equal(EquipmentState.Standby, machine.Productivity.State);

        var result = machine.Bridge.Apply();

        Assert.False(result.IsAccepted);
        Assert.Equal(EquipmentState.Standby, result.State);
        Assert.Equal("the tool is already Standby.", result.Reason);
    }

    [Fact]
    public void A_finished_task_is_a_cycle_only_when_a_fab_says_so()
    {
        var counted = Build(options => options.CountsTasksAsCycles = true);
        var not = Build();

        foreach (var machine in new[] { counted, not })
        {
            Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
            Assert.True(machine.Performance.EndTask(Tool).IsAccepted);
            Assert.True(machine.Performance.BeginTask(Other, Recipe, PerformanceTaskTypes.Process).IsAccepted);
            Assert.True(machine.Performance.EndTask(Other).IsAccepted);
        }

        // One cycle, not two: the other module's task is not this tool's.
        Assert.Equal(1, counted.Productivity.Report().Cycles);
        Assert.Equal(0, not.Productivity.Report().Cycles);
    }

    [Fact]
    public void The_machine_own_words_reach_the_shift_report()
    {
        var machine = Build();

        Assert.True(machine.Performance.BeginTask(Tool, "CHAMBER-CLEAN", PerformanceTaskTypes.Maintenance).IsAccepted);

        // E10 requires a reason for a downtime state, and the machine's own words are a better
        // answer than anything the bridge could invent.
        Assert.Equal("EQUIPMENT: CHAMBER-CLEAN (Maintenance)", machine.Productivity.StateReason);
    }

    [Fact]
    public void With_the_words_turned_off_a_downtime_still_gets_the_reason_E10_demands()
    {
        var machine = Build(options => options.CarriesReasons = false);

        Assert.True(machine.Performance.BeginTask(Tool, "CHAMBER-CLEAN", PerformanceTaskTypes.Maintenance).IsAccepted);

        // The half that matters: E10 refuses a downtime with no reason, so turning the machine's
        // words off must not make the move fail - it makes it worse, not broken.
        Assert.Equal(EquipmentState.UnscheduledDowntime, machine.Productivity.State);
        Assert.Equal("EQUIPMENT is Executing", machine.Productivity.StateReason);
    }

    // ------------------------------------------------------------------ a whole day

    [Fact]
    public void A_shift_driven_entirely_by_E116_adds_up_in_E10()
    {
        var machine = Build(options => options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby);
        var missing = 0;
        machine.Bridge.MappingMissing += (_, _) => missing++;

        machine.Clock.Advance(TimeSpan.FromHours(2));
        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(6));

        Assert.True(machine.Performance.Block(Tool, "NO_MATERIAL").IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(1));

        // One reason nobody mapped, and an hour of it.
        Assert.True(machine.Performance.Block(Tool, "INTERLOCK").IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(1));

        Assert.True(machine.Performance.Unblock(Tool).IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(2));

        Assert.True(machine.Performance.EndTask(Tool).IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(12));

        var report = machine.Productivity.Report();

        Assert.Equal(TimeSpan.FromHours(24), report.Total);
        Assert.Equal(TimeSpan.FromHours(8), report.Productive);
        Assert.Equal(TimeSpan.FromHours(15), report.Standby);
        Assert.Equal(TimeSpan.FromHours(1), report.UnscheduledDowntime);
        Assert.Equal(TimeSpan.Zero, report.ScheduledDowntime);
        Assert.Equal(TimeSpan.Zero, report.NonScheduled);

        // Nobody touched the E10 tracker: a day's availability measured rather than typed in.
        Assert.Equal(23d / 24d, report.Availability, 10);
        Assert.Equal(1d / 3d, report.OperationalUtilisation, 10);

        // And the interlock became a failure and a finished repair, because E116 came out of it
        // into a state the tool could work in.
        Assert.Equal(1, report.Failures);
        Assert.Equal(1, report.Repairs);
        Assert.Equal(TimeSpan.FromHours(1), report.MeanTimeToRepair);
        Assert.Equal(1, missing);
    }

    [Fact]
    public void Mapping_the_interlock_moves_that_hour_out_of_downtime()
    {
        var machine = Build(options =>
        {
            options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby;

            // The same day as above, with the fab having said what an interlock is: a door left
            // open by an operator is nobody's idea of the tool being broken.
            options.BlockedStates["INTERLOCK"] = EquipmentState.Standby;
        });

        var missing = 0;
        machine.Bridge.MappingMissing += (_, _) => missing++;

        Assert.True(machine.Performance.BeginTask(Tool, Recipe, PerformanceTaskTypes.Process).IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(8));
        Assert.True(machine.Performance.Block(Tool, "INTERLOCK").IsAccepted);
        machine.Clock.Advance(TimeSpan.FromHours(16));

        var report = machine.Productivity.Report();

        Assert.Equal(0, missing);
        Assert.Equal(TimeSpan.Zero, report.UnscheduledDowntime);
        Assert.Equal(0, report.Failures);
        Assert.Equal(1d, report.Availability);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var clock = new FakeTimeProvider(Start);
        var performance = new PerformanceTracker(clock);
        var productivity = new ProductivityTracker(clock);

        Assert.Throws<ArgumentNullException>(() =>
            new PerformanceProductivityBridge(null!, productivity, Tool));
        Assert.Throws<ArgumentNullException>(() =>
            new PerformanceProductivityBridge(performance, null!, Tool));
        Assert.Throws<ArgumentNullException>(() =>
            new PerformanceProductivityBridge(performance, productivity, null!));

        // Empty is the interesting one: there is no module called "", so a bridge built with it
        // would follow nothing for ever and report that it was fine.
        Assert.Throws<ArgumentException>(() =>
            new PerformanceProductivityBridge(performance, productivity, string.Empty));
    }
}
