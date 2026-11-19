// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.PerformanceTracking;

/// <summary>
/// A part of a machine whose time is accounted for - E116's module.
/// </summary>
/// <remarks>
/// <para>
/// A module is whatever the machine is prepared to be honest about separately: a chamber, an
/// aligner, a robot, a load port, or the machine as a whole. It is declared rather than created by
/// a message, because it is hardware - the same rule a substrate location follows in the E90
/// package, and for the same reason: a host cannot bring a chamber into existence by asking.
/// </para>
/// <para>
/// <strong>No hierarchy, deliberately.</strong> A cluster tool's modules are a flat set here, and
/// the tool itself is another module beside them rather than their parent. E116 does not say how a
/// parent's state follows from its children's, and it has more than one sensible answer - a tool
/// with one chamber blocked is still executing, unless the blocked chamber is the only one the lot
/// needs - so this package keeps the modules it is told about and leaves that sum to whoever knows
/// which answer their fab wants. The same reasoning kept the E87-E94 join out of
/// <c>Barbatos.SecsGem.MaterialFlow</c>.
/// </para>
/// <para>
/// Public getters and internal setters, as everywhere else in this stack: the only thing that can
/// move a module is <see cref="PerformanceTracker"/>, where the transitions and the clock are. A
/// module whose state could be assigned could be made to look productive while it stood still,
/// and then the one number this standard exists to produce would be worth nothing.
/// </para>
/// </remarks>
public sealed class PerformanceModule
{
    internal PerformanceModule(string moduleId, DateTimeOffset since, long stamp)
    {
        ModuleId = moduleId;
        StateSince = since;
        AccountingSince = since;
        StateStamp = stamp;
    }

    /// <summary>What the module is called.</summary>
    public string ModuleId { get; }

    /// <summary>What it is doing with its time.</summary>
    public PerformanceState State { get; internal set; } = PerformanceState.Idle;

    /// <summary>What it was doing before that.</summary>
    /// <remarks>
    /// The same as <see cref="State"/> until the module first moves, because a module that has
    /// never changed state has no previous one and E116's own attribute has to answer anyway.
    /// </remarks>
    public PerformanceState PreviousState { get; internal set; } = PerformanceState.Idle;

    /// <summary>When it entered <see cref="State"/>.</summary>
    /// <remarks>
    /// Wall clock, from the tracker's <see cref="TimeProvider"/>, because this one is read by a
    /// host and a host wants a time of day. The durations in <see cref="PerformanceStatistics"/>
    /// are measured from the monotonic timestamp instead, so a clock that is corrected under the
    /// machine's feet cannot make a module's accounting go backwards.
    /// </remarks>
    public DateTimeOffset StateSince { get; internal set; }

    /// <summary>What it is working on, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Not <see langword="null"/> exactly when <see cref="State"/> is
    /// <see cref="PerformanceState.Executing"/>, or when it is
    /// <see cref="PerformanceState.Blocked"/> and the module was executing when it got blocked.
    /// Always <see langword="null"/> when <see cref="PerformanceState.Idle"/>.
    /// </remarks>
    public PerformanceTask? CurrentTask { get; internal set; }

    /// <summary>Why it is blocked, or <see langword="null"/>.</summary>
    /// <remarks>Not <see langword="null"/> exactly when <see cref="State"/> is <see cref="PerformanceState.Blocked"/>.</remarks>
    public BlockedReason? BlockedReason { get; internal set; }

    /// <summary>Gets a value indicating whether the module is in the middle of a task.</summary>
    /// <remarks>
    /// True while blocked half way through one, which is the point: an application that writes
    /// <c>State == Executing</c> where it meant "has work in hand" loses every wafer that was
    /// sitting in a stuck chamber.
    /// </remarks>
    public bool HasTask => CurrentTask is not null;

    internal DateTimeOffset AccountingSince { get; set; }

    internal long StateStamp { get; set; }

    internal TimeSpan IdleTotal { get; set; }

    internal TimeSpan ExecutingTotal { get; set; }

    internal TimeSpan BlockedTotal { get; set; }

    internal int TasksCompleted { get; set; }

    internal int TasksAbandoned { get; set; }
}

/// <summary>
/// A piece of work a module is executing - E116's task.
/// </summary>
/// <remarks>
/// A name and a kind, and the kind is what makes the accounting useful: see
/// <see cref="PerformanceTaskTypes"/>. The task outlives a block, so one task can gather up
/// several stretches of being stuck, and the total comes out on
/// <see cref="PerformanceTaskEndedEventArgs.Blocked"/>.
/// </remarks>
public sealed class PerformanceTask
{
    internal PerformanceTask(string name, string taskType, DateTimeOffset startedAt, long stamp)
    {
        Name = name;
        TaskType = taskType;
        StartedAt = startedAt;
        StartStamp = stamp;
    }

    /// <summary>What the work is called.</summary>
    /// <remarks>A recipe, a step, a job - whatever the machine would put in an operator log.</remarks>
    public string Name { get; }

    /// <summary>What kind of work it is.</summary>
    /// <remarks>One of <see cref="PerformanceTaskTypes"/> by convention, and anything at all in fact. Empty when the machine did not say.</remarks>
    public string TaskType { get; }

    /// <summary>When it began.</summary>
    public DateTimeOffset StartedAt { get; }

    internal long StartStamp { get; }

    internal TimeSpan BlockedTotal { get; set; }
}

/// <summary>
/// What is stopping a module - E116's blocked reason.
/// </summary>
/// <remarks>
/// A code and some words, like an alarm, because that is what a blocked reason is for: the code is
/// what a report groups by and the text is what an operator reads. Both belong to the machine -
/// E116 does not enumerate them, and a library that did would be telling a fab which of its own
/// problems it is allowed to have.
/// </remarks>
public sealed class BlockedReason
{
    internal BlockedReason(string code, string text, DateTimeOffset since)
    {
        Code = code;
        Text = text;
        Since = since;
    }

    /// <summary>What kind of obstruction it is, in the machine's own vocabulary.</summary>
    public string Code { get; }

    /// <summary>What to tell somebody about it. Empty when the machine said only the code.</summary>
    public string Text { get; }

    /// <summary>When this reason started applying.</summary>
    /// <remarks>
    /// A reason that replaces another while the module stays blocked gets its own
    /// <see cref="Since"/>, while the module's <see cref="PerformanceModule.StateSince"/> does not
    /// move - because the module did not stop being blocked, and the time it has been stuck is
    /// continuous whatever the machine blames.
    /// </remarks>
    public DateTimeOffset Since { get; }
}

/// <summary>The answer to anything that would move a module.</summary>
/// <remarks>
/// A reason in words rather than a number, as in the E87, E90 and E94 packages, and for the same
/// reason: E116 has no messages and so no acknowledgement code. Every refusal names the state that
/// refused it.
/// </remarks>
public sealed class PerformanceResult
{
    /// <summary>Whether it was allowed.</summary>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not. Empty when accepted.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>An accepted answer.</summary>
    internal static PerformanceResult Ok { get; } = new() { IsAccepted = true };

    /// <summary>A refusal.</summary>
    /// <param name="reason">What was wrong, naming the state.</param>
    /// <returns>The result.</returns>
    internal static PerformanceResult Refused(string reason) => new() { IsAccepted = false, Reason = reason };
}

/// <summary>
/// How a module has spent a stretch of time - the thing E116 is for.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot taken at the moment it is asked for, so the state the module is in now has its
/// elapsed time counted in already: <see cref="TimeIdle"/>, <see cref="TimeExecuting"/> and
/// <see cref="TimeBlocked"/> add up to <see cref="Total"/>, which is the time since
/// <see cref="Since"/>. That is an invariant worth leaning on, and there is a test that leans on
/// it.
/// </para>
/// <para>
/// With one caveat, and it is the honest half of measuring durations monotonically. The three
/// times are measured from <see cref="TimeProvider.GetTimestamp"/> and <see cref="Since"/> is a
/// wall-clock reading, so if somebody corrects the machine's clock mid-window the figures stay
/// right and their agreement with <c>now - Since</c> does not: it is out by the correction.
/// <see cref="Since"/> is a label saying which stretch of the day this is about. The durations are
/// the measurement.
/// </para>
/// <para>
/// <see cref="Since"/> is when the module was declared, or when
/// <see cref="PerformanceTracker.ResetStatistics"/> was last called on it - a shift boundary, say.
/// A reset does not pretend the module changed state: <see cref="PerformanceModule.StateSince"/>
/// still says when it really did, while <see cref="TimeInState"/> counts only the part of that
/// which falls inside this window.
/// </para>
/// </remarks>
public sealed class PerformanceStatistics
{
    /// <summary>Which module.</summary>
    public required string ModuleId { get; init; }

    /// <summary>What it is doing now.</summary>
    public required PerformanceState State { get; init; }

    /// <summary>When this accounting started.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>How long it has been in <see cref="State"/>, counting only time inside this window.</summary>
    public required TimeSpan TimeInState { get; init; }

    /// <summary>How long it has had nothing to do.</summary>
    public required TimeSpan TimeIdle { get; init; }

    /// <summary>How long it has been working.</summary>
    public required TimeSpan TimeExecuting { get; init; }

    /// <summary>How long something has been in its way.</summary>
    public required TimeSpan TimeBlocked { get; init; }

    /// <summary>How many tasks it finished.</summary>
    public required int TasksCompleted { get; init; }

    /// <summary>How many tasks were given up part way through.</summary>
    public required int TasksAbandoned { get; init; }

    /// <summary>Gets the whole window: the three times added together.</summary>
    public TimeSpan Total => TimeIdle + TimeExecuting + TimeBlocked;

    /// <summary>Gets the share of the window the module spent working, between zero and one.</summary>
    /// <remarks>
    /// The one derived number worth having here, because it is the number the standard exists to
    /// produce and an application that computes it itself will eventually compute it over a
    /// different denominator. Zero for a window with no time in it, rather than a division by
    /// zero - a module asked about the instant it was declared has not been idle either.
    /// </remarks>
    public double ExecutingShare => Total > TimeSpan.Zero ? TimeExecuting / Total : 0d;
}

/// <summary>A module changed state - E116.</summary>
/// <remarks>
/// <para>
/// E116 defines collection events for these, and this is where they come out. As in the E87, E90
/// and E94 packages, this one does not publish them: a <c>CEID</c> belongs to the equipment that
/// defines it, so an application wires these to its own reports in a few lines and a machine with
/// no GEM layer can still account for its time.
/// </para>
/// <para>
/// Raised after the state has moved and inside the tracker's lock. A handler reading the module
/// sees the state the event announces, and must not call back in.
/// </para>
/// </remarks>
public sealed class PerformanceStateChangedEventArgs : EventArgs
{
    /// <summary>The module.</summary>
    public required PerformanceModule Module { get; init; }

    /// <summary>What it was.</summary>
    public required PerformanceState Previous { get; init; }

    /// <summary>What it is.</summary>
    public required PerformanceState Current { get; init; }
}

/// <summary>
/// A task is over, and here is what it cost - E116.
/// </summary>
/// <remarks>
/// The event this package exists to raise. Every task's end is one of these, because a module
/// cannot begin a second task without ending the first - which is why
/// <see cref="PerformanceTracker.BeginTask"/> refuses from
/// <see cref="PerformanceState.Executing"/> rather than quietly swapping the task and losing its
/// accounting.
/// </remarks>
public sealed class PerformanceTaskEndedEventArgs : EventArgs
{
    /// <summary>The module.</summary>
    public required PerformanceModule Module { get; init; }

    /// <summary>The task that ended.</summary>
    public required PerformanceTask Task { get; init; }

    /// <summary>How long it took, start to finish.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>How much of <see cref="Elapsed"/> the module spent blocked.</summary>
    /// <remarks>
    /// The difference between a recipe that is slow and a recipe that keeps waiting for something,
    /// and nothing else in this stack can tell them apart.
    /// </remarks>
    public required TimeSpan Blocked { get; init; }

    /// <summary>Whether it ran to its end.</summary>
    /// <remarks><see langword="false"/> when the module was told to go idle with the task still in hand.</remarks>
    public required bool Completed { get; init; }
}

/// <summary>A blocked module is blocked for a different reason now - E116.</summary>
/// <remarks>
/// Not a state change, which is the point of having its own event: a chamber that goes from
/// waiting for material to waiting for an interlock never stopped being blocked, and reporting it
/// as a transition would restart a clock that should keep running.
/// </remarks>
public sealed class BlockedReasonChangedEventArgs : EventArgs
{
    /// <summary>The module.</summary>
    public required PerformanceModule Module { get; init; }

    /// <summary>What was blocking it.</summary>
    public required BlockedReason Previous { get; init; }

    /// <summary>What is blocking it now.</summary>
    public required BlockedReason Current { get; init; }
}
