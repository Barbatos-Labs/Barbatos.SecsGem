// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.Productivity;

namespace Barbatos.SecsGem.PerformanceFlow;

/// <summary>
/// How one module's E116 state becomes the tool's E10 state.
/// </summary>
/// <remarks>
/// <para>
/// Every convention here is replaceable, as in <c>Barbatos.SecsGem.MaterialFlow</c>, and the
/// reason there are conventions at all is worth reading. Both packages this one joins say that
/// nobody can map E116's three states onto E10's six, and that is true of the <em>states</em>: a
/// module that is executing could be producing, learning or being tested, and nothing about the
/// word "executing" tells you which.
/// </para>
/// <para>
/// <strong>What makes the join possible is the task type.</strong> E116 does not only say that a
/// module is executing; it says what it is executing - and that is where the machine recorded the
/// intent E10 needs. So the mapping is from a task type to an E10 state rather than from a state
/// to a state, and two of its entries need no choosing at all:
/// <c>Process</c> is productive work and <c>Engineering</c> is engineering time, in both standards'
/// own words. <see cref="PerformanceTaskTypes.Support"/> and
/// <see cref="PerformanceTaskTypes.Maintenance"/> are deliberately <em>not</em> filled in - a
/// pump-down may be part of producing a lot or part of a setup, and a maintenance task may be a
/// planned service or an emergency repair, which are different E10 states and only a fab knows
/// which its own task types mean.
/// </para>
/// <para>
/// <strong>Where a default cannot be avoided, it is the one that cannot flatter the tool.</strong>
/// An unmapped task type or blocked reason is charged to
/// <see cref="EquipmentState.UnscheduledDowntime"/> and reported on
/// <see cref="PerformanceProductivityBridge.MappingMissing"/>, because an unmapped reason showing
/// up in a fab's availability as a problem is correct: an unmapped reason <em>is</em> a problem,
/// and the alternative - leaving the tool wherever it was, very likely productive - would overstate
/// the one number this join exists to get right.
/// </para>
/// </remarks>
public sealed class PerformanceProductivityOptions
{
    /// <summary>What each of E116's task types means in E10's terms, while a module is executing.</summary>
    /// <remarks>
    /// <para>
    /// Two entries, and both are the two standards using the same word:
    /// <see cref="PerformanceTaskTypes.Process"/> is <see cref="EquipmentState.Productive"/> and
    /// <see cref="PerformanceTaskTypes.Engineering"/> is <see cref="EquipmentState.Engineering"/>.
    /// Add your own - a fab whose support tasks are part of a lot writes
    /// <c>TaskStates[PerformanceTaskTypes.Support] = EquipmentState.Productive</c> and one whose
    /// support tasks are setup writes <see cref="EquipmentState.ScheduledDowntime"/> instead.
    /// </para>
    /// <para>
    /// Ordinal comparison, because a task type is a string a machine sends and
    /// <c>"process"</c> is not <c>"Process"</c> on a wire.
    /// </para>
    /// </remarks>
    public IDictionary<string, EquipmentState> TaskStates { get; } =
        new Dictionary<string, EquipmentState>(StringComparer.Ordinal)
        {
            [PerformanceTaskTypes.Process] = EquipmentState.Productive,
            [PerformanceTaskTypes.Engineering] = EquipmentState.Engineering,
        };

    /// <summary>What each of the machine's blocked reason codes means in E10's terms.</summary>
    /// <remarks>
    /// <strong>Empty, and that is not an omission.</strong> A blocked reason code is the machine's
    /// own vocabulary - E116 does not enumerate them and neither does this package - so there is
    /// nothing here to get right by guessing. The distinction it decides is the one E10 exists
    /// for: <c>NO_MATERIAL</c> is <see cref="EquipmentState.Standby"/> and the factory's problem,
    /// while <c>INTERLOCK</c> is <see cref="EquipmentState.UnscheduledDowntime"/> and the tool's,
    /// and a fab that maps neither gets both charged to the tool.
    /// </remarks>
    public IDictionary<string, EquipmentState> BlockedStates { get; } =
        new Dictionary<string, EquipmentState>(StringComparer.Ordinal);

    /// <summary>What to charge an executing module whose task type is in neither map.</summary>
    /// <remarks>
    /// <see cref="EquipmentState.UnscheduledDowntime"/> by default: see the remarks on this class
    /// for why the unavoidable default is the pessimistic one. Set it to something else once your
    /// task types are mapped and you know what the remainder are.
    /// </remarks>
    public EquipmentState UnmappedTaskState { get; set; } = EquipmentState.UnscheduledDowntime;

    /// <summary>What to charge a blocked module whose reason is not in <see cref="BlockedStates"/>.</summary>
    /// <remarks>
    /// <see cref="EquipmentState.UnscheduledDowntime"/> by default, for the same reason - and here
    /// it is the likelier of the two to be right, since a module blocked for a reason nobody
    /// wrote down is more often broken than idle.
    /// </remarks>
    public EquipmentState UnmappedBlockedState { get; set; } = EquipmentState.UnscheduledDowntime;

    /// <summary>What an executing module with no task type at all is charged to.</summary>
    /// <remarks>
    /// Separate from <see cref="UnmappedTaskState"/> because the two are different mistakes: a
    /// task type this fab has not mapped is a configuration gap, and a task with no type at all is
    /// a machine that did not say. <see langword="null"/> - the default - falls through to
    /// <see cref="UnmappedTaskState"/>, so a fab that does not care about the difference need not
    /// think about it.
    /// </remarks>
    public EquipmentState? UntypedTaskState { get; set; }

    /// <summary>Whether the tool is scheduled for use right now.</summary>
    /// <remarks>
    /// <para>
    /// The one thing neither standard can tell you, because it is not about the tool: it is about
    /// whether anybody staffed the shift. It decides what an <see cref="PerformanceState.Idle"/>
    /// module means - <see cref="EquipmentState.Standby"/> when the tool was wanted and nothing
    /// came, <see cref="EquipmentState.NonScheduled"/> when it was not wanted at all.
    /// </para>
    /// <para>
    /// Always scheduled by default, which puts every hour in the operations denominator and
    /// therefore reports the <em>lower</em> availability of the two - the pessimistic choice again.
    /// A fab with a calendar replaces it with one line.
    /// </para>
    /// </remarks>
    public Func<bool> IsScheduled { get; set; } = static () => true;

    /// <summary>Whether E116's own words for a state are carried into E10's reason.</summary>
    /// <remarks>
    /// On by default. A blocked reason code and a task name are exactly what somebody reading an
    /// E10 downtime wants to see, and E10 requires a reason for its two downtime states anyway -
    /// so with this off the bridge has to supply one of its own, which is a worse answer than the
    /// machine's.
    /// </remarks>
    public bool CarriesReasons { get; set; } = true;

    /// <summary>Whether a finished E116 task counts as an E10 cycle.</summary>
    /// <remarks>
    /// Off by default, because a cycle is whatever the fab counts and this package will not decide
    /// that for it - <c>MeanCyclesBetweenFailures</c> over a count nobody chose is a number nobody
    /// should read. On, every task that ends - finished or abandoned - is one cycle.
    /// </remarks>
    public bool CountsTasksAsCycles { get; set; }
}

/// <summary>What the bridge did, or why it did not.</summary>
public sealed class PerformanceProductivityResult
{
    /// <summary>Whether the tool's state was brought in line.</summary>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not. Empty when accepted.</summary>
    /// <remarks>
    /// Includes the case nobody should treat as a failure: the tool is already in the state the
    /// module implies, which E10's own tracker refuses and this bridge passes straight on.
    /// </remarks>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The E10 state the module's state mapped to, whether or not the move was accepted.</summary>
    public required EquipmentState State { get; init; }

    /// <summary>Whether that state came from a map or from a fallback.</summary>
    public bool FromFallback { get; init; }
}

/// <summary>The bridge had to fall back, because something was not mapped.</summary>
/// <remarks>
/// Raised every time, not once - a fab whose task types are half mapped should see the count grow
/// rather than get one warning at start-up and then a quietly wrong report. Raised inside the
/// performance tracker's lock when the bridge is attached, so queue the work.
/// </remarks>
public sealed class PerformanceProductivityMappingEventArgs : EventArgs
{
    /// <summary>Which module.</summary>
    public required string ModuleId { get; init; }

    /// <summary>What it was doing, in E116's terms.</summary>
    public required PerformanceState PerformanceState { get; init; }

    /// <summary>The task type that was not mapped, or empty.</summary>
    public string TaskType { get; init; } = string.Empty;

    /// <summary>The blocked reason code that was not mapped, or empty.</summary>
    public string BlockedReason { get; init; } = string.Empty;

    /// <summary>What it was charged to instead.</summary>
    public required EquipmentState Charged { get; init; }
}

/// <summary>The bridge moved the tool between E10's states.</summary>
public sealed class PerformanceProductivityAppliedEventArgs : EventArgs
{
    /// <summary>Which module drove it.</summary>
    public required string ModuleId { get; init; }

    /// <summary>What the module was doing, in E116's terms.</summary>
    public required PerformanceState PerformanceState { get; init; }

    /// <summary>What came of it.</summary>
    public required PerformanceProductivityResult Result { get; init; }
}
