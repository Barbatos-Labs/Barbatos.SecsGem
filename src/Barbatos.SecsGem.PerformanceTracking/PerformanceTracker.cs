// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.PerformanceTracking;

/// <summary>
/// The modules a machine accounts for, E116's rules about what each may do next, and where its
/// time went.
/// </summary>
/// <remarks>
/// <para>
/// The whole package, and it has no connection, no transport and no messages - because E116 has
/// none either. A module reaches a host as an object with attributes on stream 14 and as whatever
/// collection events the equipment defines; what this class does is keep the states true and the
/// clock honest while that happens.
/// </para>
/// <para>
/// <strong>The rules.</strong> A task can only begin on an <see cref="PerformanceState.Idle"/>
/// module: one already executing must end its task first, and a blocked one must be unblocked
/// first. A task can only be ended by a module that has one - which includes a blocked module,
/// because a job cancelled while a chamber is stuck is a thing that happens, and ending it does
/// not unstick the chamber. Blocking needs a reason; blocking an already-blocked module for the
/// same reason is refused, and for a different one it is a reason change rather than a transition.
/// Unblocking returns a module to <see cref="PerformanceState.Executing"/> when a task survived
/// and to <see cref="PerformanceState.Idle"/> when none did, which is derived rather than asked
/// for - the same choice the E90 package made for a substrate's transport state. Each of those is
/// a test that names the rule and shows the refusal.
/// </para>
/// <para>
/// <strong>Durations come from the monotonic timestamp, timestamps from the wall clock.</strong>
/// Both are the same <see cref="TimeProvider"/>, so a test drives them together, but they are read
/// differently on purpose: <see cref="PerformanceModule.StateSince"/> is a time of day because a
/// host asked what time it was, while every figure in <see cref="PerformanceStatistics"/> is
/// measured with <see cref="TimeProvider.GetElapsedTime(long, long)"/>. An operator correcting the
/// machine's clock by ten minutes must not be able to make a chamber's utilisation exceed one, or
/// go negative.
/// </para>
/// <para>
/// Thread safety: one lock around every transition. The events are raised inside it, after the
/// state has moved, so a handler must queue rather than call back in.
/// </para>
/// </remarks>
public sealed class PerformanceTracker
{
    private readonly Dictionary<string, PerformanceModule> _modules = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    /// <summary>Tracks a machine's modules.</summary>
    /// <param name="clock">
    /// The clock, which is every duration this package reports.
    /// <see langword="null"/> means <see cref="TimeProvider.System"/>.
    /// </param>
    /// <remarks>
    /// The one constructor parameter, and it is not optional in spirit: a package whose whole
    /// output is durations has to be testable without waiting, so a test passes
    /// <c>FakeTimeProvider</c> and advances it.
    /// </remarks>
    public PerformanceTracker(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Raised after a module moves between E116's three states.</summary>
    /// <remarks>Raised inside the tracker's lock. Queue the work; do not call back in.</remarks>
    public event EventHandler<PerformanceStateChangedEventArgs>? StateChanged;

    /// <summary>Raised after a task ends, finished or abandoned, with what it cost.</summary>
    /// <remarks>
    /// Raised after <see cref="StateChanged"/> when the ending moved the module, because the state
    /// is the fact and the accounting is the consequence. A task ended on a module that stays
    /// blocked raises this one alone.
    /// </remarks>
    public event EventHandler<PerformanceTaskEndedEventArgs>? TaskEnded;

    /// <summary>Raised when a module that is blocked becomes blocked for a different reason.</summary>
    /// <remarks>Never accompanied by <see cref="StateChanged"/>: the module did not move.</remarks>
    public event EventHandler<BlockedReasonChangedEventArgs>? BlockedReasonChanged;

    /// <summary>Gets the modules being accounted for.</summary>
    public IReadOnlyCollection<PerformanceModule> Modules
    {
        get
        {
            lock (_gate)
            {
                return [.. _modules.Values];
            }
        }
    }

    /// <summary>Declares a part of the machine whose time is to be accounted for.</summary>
    /// <param name="moduleId">What to call it.</param>
    /// <returns>The module.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Modules are hardware, so they are declared rather than created by a message. Declaring one
    /// twice returns the same module and resets nothing - a second <c>AddModule</c> in a start-up
    /// path must not silently throw away a shift's accounting.
    /// </remarks>
    public PerformanceModule AddModule(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            if (_modules.TryGetValue(moduleId, out var existing))
            {
                return existing;
            }

            var module = new PerformanceModule(moduleId, _clock.GetUtcNow(), _clock.GetTimestamp());
            _modules[moduleId] = module;

            return module;
        }
    }

    /// <summary>Finds a module.</summary>
    /// <param name="moduleId">Which one.</param>
    /// <returns>The module, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    public PerformanceModule? FindModule(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            return _modules.GetValueOrDefault(moduleId);
        }
    }

    /// <summary>A module starts working on something.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <param name="taskName">What the work is called.</param>
    /// <param name="taskType">What kind of work it is - one of <see cref="PerformanceTaskTypes"/> by convention.</param>
    /// <returns>Whether it was allowed, and why not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> or <paramref name="taskName"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Refused from <see cref="PerformanceState.Executing"/> rather than swapping the task,
    /// because a swap would end one task's accounting without reporting it and this package's
    /// whole output is that accounting. Refused from <see cref="PerformanceState.Blocked"/>
    /// because a blocked module is not available to be given work - that is what blocked means.
    /// </remarks>
    public PerformanceResult BeginTask(string moduleId, string taskName, string taskType = "")
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(taskName);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return Missing(moduleId);
            }

            if (taskName.Length == 0)
            {
                return PerformanceResult.Refused($"module '{moduleId}' cannot begin a task with no name.");
            }

            // Being blocked and having a reason are the same fact, so this is one check and not
            // two - the redundancy the E40 join taught us to look for.
            if (module.BlockedReason is { } blocking)
            {
                return PerformanceResult.Refused(
                    $"module '{moduleId}' is {PerformanceState.Blocked} ({blocking.Code}); unblock it before it can begin a task.");
            }

            if (module.State is PerformanceState.Executing)
            {
                return PerformanceResult.Refused(
                    $"module '{moduleId}' is {PerformanceState.Executing} task '{module.CurrentTask?.Name}'; end that task before beginning another.");
            }

            var now = _clock.GetUtcNow();
            var stamp = _clock.GetTimestamp();
            var previous = Move(module, PerformanceState.Executing, now, stamp);

            module.CurrentTask = new PerformanceTask(taskName, taskType, now, stamp);

            RaiseStateChanged(module, previous);

            return PerformanceResult.Ok;
        }
    }

    /// <summary>A module finishes what it was working on.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <returns>Whether it was allowed, and why not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Allowed from <see cref="PerformanceState.Blocked"/> as well as from
    /// <see cref="PerformanceState.Executing"/>, and that is not a loosening: a job cancelled
    /// while the chamber is stuck has to be endable without first pretending the chamber came
    /// unstuck.
    /// </para>
    /// <para>
    /// <strong>Which is why a blocked module stays blocked.</strong> Ending a task says the work
    /// is over and says nothing about whatever is in the way - an interlock does not close because
    /// a host cancelled a job - so the module keeps its state and its reason, loses its task, and
    /// reaches <see cref="PerformanceState.Idle"/> through <see cref="Unblock"/> when the
    /// obstruction really does go. From <see cref="PerformanceState.Executing"/> it lands
    /// <see cref="PerformanceState.Idle"/> at once.
    /// </para>
    /// </remarks>
    public PerformanceResult EndTask(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return Missing(moduleId);
            }

            if (module.CurrentTask is not { } task)
            {
                return PerformanceResult.Refused($"module '{moduleId}' is {module.State} and has no task to end.");
            }

            Finish(module, task, completed: true, landIdle: module.State is not PerformanceState.Blocked);

            return PerformanceResult.Ok;
        }
    }

    /// <summary>Something is in a module's way.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <param name="reasonCode">What kind of obstruction, in the machine's own vocabulary.</param>
    /// <param name="reasonText">What to tell somebody about it.</param>
    /// <returns>Whether it was allowed, and why not.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A task in hand survives, so the time spent stuck is charged to the task as well as to the
    /// module - see <see cref="PerformanceTaskEndedEventArgs.Blocked"/>.
    /// </para>
    /// <para>
    /// Blocking a module that is already blocked for the <em>same</em> reason is refused, because
    /// it would say nothing and a caller doing it twice has a bug. For a different reason it is
    /// accepted and reported as <see cref="BlockedReasonChanged"/> with no transition, so the
    /// module's blocked clock runs continuously across the change.
    /// </para>
    /// </remarks>
    public PerformanceResult Block(string moduleId, string reasonCode, string reasonText = "")
    {
        ArgumentNullException.ThrowIfNull(moduleId);
        ArgumentNullException.ThrowIfNull(reasonCode);
        ArgumentNullException.ThrowIfNull(reasonText);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return Missing(moduleId);
            }

            if (reasonCode.Length == 0)
            {
                return PerformanceResult.Refused(
                    $"module '{moduleId}' cannot be {PerformanceState.Blocked} for no reason.");
            }

            var now = _clock.GetUtcNow();

            if (module.BlockedReason is { } standing)
            {
                if (string.Equals(standing.Code, reasonCode, StringComparison.Ordinal))
                {
                    return PerformanceResult.Refused(
                        $"module '{moduleId}' is already {PerformanceState.Blocked} ({reasonCode}).");
                }

                module.BlockedReason = new BlockedReason(reasonCode, reasonText, now);

                BlockedReasonChanged?.Invoke(this, new BlockedReasonChangedEventArgs
                {
                    Module = module,
                    Previous = standing,
                    Current = module.BlockedReason,
                });

                return PerformanceResult.Ok;
            }

            var previous = Move(module, PerformanceState.Blocked, now, _clock.GetTimestamp());

            module.BlockedReason = new BlockedReason(reasonCode, reasonText, now);

            RaiseStateChanged(module, previous);

            return PerformanceResult.Ok;
        }
    }

    /// <summary>Whatever was in the module's way is gone.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <returns>Whether it was allowed, and why not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Where the module goes is derived from whether it still has a task, rather than being an
    /// argument: a module blocked half way through a job resumes
    /// <see cref="PerformanceState.Executing"/> it, and one with nothing in hand - because it was
    /// idle when it got blocked, or because its task was ended while it was - goes to
    /// <see cref="PerformanceState.Idle"/>. A caller that could choose could say the module
    /// resumed a task it does not have.
    /// </remarks>
    public PerformanceResult Unblock(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return Missing(moduleId);
            }

            if (module.BlockedReason is null)
            {
                return PerformanceResult.Refused(
                    $"module '{moduleId}' is {module.State}, not {PerformanceState.Blocked}.");
            }

            var to = module.HasTask ? PerformanceState.Executing : PerformanceState.Idle;
            var previous = Move(module, to, _clock.GetUtcNow(), _clock.GetTimestamp());

            module.BlockedReason = null;

            RaiseStateChanged(module, previous);

            return PerformanceResult.Ok;
        }
    }

    /// <summary>A module has nothing to do.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <returns>Whether it was allowed, and why not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// An assertion about the end state rather than about the work, which is what separates it
    /// from <see cref="EndTask"/>: it always lands <see cref="PerformanceState.Idle"/>, it drops
    /// any blocked reason, and it is the only call that abandons a task - reported with
    /// <see cref="PerformanceTaskEndedEventArgs.Completed"/> false, which is how an aborted recipe
    /// is told from a finished one in the accounting. Refused when the module is already idle,
    /// because the alternative is a clock that restarts every time somebody asks a question.
    /// </remarks>
    public PerformanceResult GoIdle(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return Missing(moduleId);
            }

            if (module.State is PerformanceState.Idle)
            {
                return PerformanceResult.Refused($"module '{moduleId}' is already {PerformanceState.Idle}.");
            }

            if (module.CurrentTask is { } task)
            {
                Finish(module, task, completed: false, landIdle: true);

                return PerformanceResult.Ok;
            }

            var previous = Move(module, PerformanceState.Idle, _clock.GetUtcNow(), _clock.GetTimestamp());

            module.BlockedReason = null;

            RaiseStateChanged(module, previous);

            return PerformanceResult.Ok;
        }
    }

    /// <summary>Where a module's time has gone.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <returns>A snapshot taken now, or <see langword="null"/> when there is no such module.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The current state's elapsed time is counted in, so the three figures always add up to the
    /// window. Asking twice without the module moving gives two different answers, and both are
    /// right.
    /// </remarks>
    public PerformanceStatistics? Statistics(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return null;
            }

            var elapsed = _clock.GetElapsedTime(module.StateStamp, _clock.GetTimestamp());

            return new PerformanceStatistics
            {
                ModuleId = module.ModuleId,
                State = module.State,
                Since = module.AccountingSince,
                TimeInState = elapsed,
                TimeIdle = module.IdleTotal + Only(module, PerformanceState.Idle, elapsed),
                TimeExecuting = module.ExecutingTotal + Only(module, PerformanceState.Executing, elapsed),
                TimeBlocked = module.BlockedTotal + Only(module, PerformanceState.Blocked, elapsed),
                TasksCompleted = module.TasksCompleted,
                TasksAbandoned = module.TasksAbandoned,
            };
        }
    }

    /// <summary>Starts a module's accounting again from now.</summary>
    /// <param name="moduleId">Which module.</param>
    /// <returns>Whether there was such a module.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="moduleId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a shift boundary, or a report that covers a period. It changes nothing about what the
    /// module is doing: no state moves, no event is raised, and
    /// <see cref="PerformanceModule.StateSince"/> still says when the module really entered the
    /// state it is in. A task running across the reset keeps its own start, so the figures it
    /// reports when it ends can be longer than the window - which is the truth about that task.
    /// </remarks>
    public PerformanceResult ResetStatistics(string moduleId)
    {
        ArgumentNullException.ThrowIfNull(moduleId);

        lock (_gate)
        {
            if (!_modules.TryGetValue(moduleId, out var module))
            {
                return Missing(moduleId);
            }

            module.IdleTotal = TimeSpan.Zero;
            module.ExecutingTotal = TimeSpan.Zero;
            module.BlockedTotal = TimeSpan.Zero;
            module.TasksCompleted = 0;
            module.TasksAbandoned = 0;
            module.AccountingSince = _clock.GetUtcNow();
            module.StateStamp = _clock.GetTimestamp();

            return PerformanceResult.Ok;
        }
    }

    private static PerformanceResult Missing(string moduleId)
        => PerformanceResult.Refused($"there is no module '{moduleId}'.");

    private static TimeSpan Only(PerformanceModule module, PerformanceState state, TimeSpan elapsed)
        => module.State == state ? elapsed : TimeSpan.Zero;

    /// <summary>
    /// Ends a task and reports it, moving the module to idle only when it should.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="EndTask"/> and <see cref="GoIdle"/>, because the difference between
    /// them is where the module lands and one boolean on the event. When it does not land - a
    /// blocked module whose task was ended - the task's share of the blocked time is settled here
    /// instead, since after this there is no task for <see cref="Move"/> to charge, while the
    /// module's own blocked clock keeps running from the timestamp it already had.
    /// </remarks>
    private void Finish(PerformanceModule module, PerformanceTask task, bool completed, bool landIdle)
    {
        var now = _clock.GetUtcNow();
        var stamp = _clock.GetTimestamp();
        PerformanceState? moved = null;

        if (landIdle)
        {
            moved = Move(module, PerformanceState.Idle, now, stamp);
            module.BlockedReason = null;
        }
        else
        {
            task.BlockedTotal += _clock.GetElapsedTime(module.StateStamp, stamp);
        }

        module.CurrentTask = null;

        if (completed)
        {
            module.TasksCompleted++;
        }
        else
        {
            module.TasksAbandoned++;
        }

        if (moved is { } previous)
        {
            RaiseStateChanged(module, previous);
        }

        TaskEnded?.Invoke(this, new PerformanceTaskEndedEventArgs
        {
            Module = module,
            Task = task,
            Elapsed = _clock.GetElapsedTime(task.StartStamp, stamp),
            Blocked = task.BlockedTotal,
            Completed = completed,
        });
    }

    /// <summary>
    /// Charges the time since the last transition to the state being left, then moves.
    /// </summary>
    /// <returns>The state the module was in.</returns>
    /// <remarks>
    /// The one place a bucket is added to, which is why the three of them cannot drift apart from
    /// the window. Time spent <see cref="PerformanceState.Blocked"/> while a task was in hand is
    /// charged twice over - once to the module and once to the task - and that is deliberate: they
    /// answer different questions.
    /// </remarks>
    private PerformanceState Move(
        PerformanceModule module,
        PerformanceState to,
        DateTimeOffset now,
        long stamp)
    {
        var previous = module.State;
        var elapsed = _clock.GetElapsedTime(module.StateStamp, stamp);

        switch (previous)
        {
            case PerformanceState.Idle:
                module.IdleTotal += elapsed;
                break;

            case PerformanceState.Executing:
                module.ExecutingTotal += elapsed;
                break;

            default:
                module.BlockedTotal += elapsed;

                if (module.CurrentTask is { } blocked)
                {
                    blocked.BlockedTotal += elapsed;
                }

                break;
        }

        module.PreviousState = previous;
        module.State = to;
        module.StateSince = now;
        module.StateStamp = stamp;

        return previous;
    }

    private void RaiseStateChanged(PerformanceModule module, PerformanceState previous)
        => StateChanged?.Invoke(this, new PerformanceStateChangedEventArgs
        {
            Module = module,
            Previous = previous,
            Current = module.State,
        });
}
