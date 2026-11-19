// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.Productivity;

namespace Barbatos.SecsGem.PerformanceFlow;

/// <summary>
/// One module's E116 state driving the tool's E10 accounting.
/// </summary>
/// <remarks>
/// <para>
/// E116 says what a chamber is doing this minute and E10 says what the tool was for the shift, and
/// nothing in either standard carries the first into the second. Without this a fab runs the two
/// side by side and types the second one in by hand off the first - which is where a day's
/// availability stops being a measurement.
/// </para>
/// <para>
/// <strong>Three things only a fab can say, and the bridge asks for all three.</strong> Which
/// module <em>is</em> the tool, which is this class's one required argument because E116 tracks
/// many modules and E10 measures one piece of equipment, and deriving a tool's state from four
/// chambers' has more than one sensible answer. What the machine's task types and blocked reason
/// codes mean, which is <see cref="PerformanceProductivityOptions"/>. And whether the tool is
/// scheduled at all, which is not about the tool.
/// </para>
/// <para>
/// What it does <em>not</em> ask for is the part the machine already knows, and that is the whole
/// reason this chapter was possible after three saying it was not: <strong>E116 records the task
/// type</strong>, and a task type is the intent E10 wants. A module executing a
/// <c>Process</c> task is productive and one executing an <c>Engineering</c> task is engineering
/// time, in both standards' own words. The states alone could never have said that; the task
/// behind the state can.
/// </para>
/// <para>
/// <strong>Lock order: performance first, productivity last, always.</strong> E116 raises its
/// events inside its own lock, so an attached bridge runs there and then takes E10's. Nothing in
/// this stack goes the other way and nothing may start - the same rule the two material joins
/// follow, for the same reason.
/// </para>
/// </remarks>
public sealed class PerformanceProductivityBridge
{
    private readonly PerformanceTracker _performance;
    private readonly ProductivityTracker _productivity;
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private EventHandler<PerformanceStateChangedEventArgs>? _stateHandler;
    private EventHandler<BlockedReasonChangedEventArgs>? _reasonHandler;
    private EventHandler<PerformanceTaskEndedEventArgs>? _taskHandler;

    /// <summary>Joins one E116 module to an E10 tool.</summary>
    /// <param name="performance">E116's modules.</param>
    /// <param name="productivity">E10's accounting for the tool.</param>
    /// <param name="moduleId">
    /// Which module <em>is</em> the tool. Required, and there is no default that could be right:
    /// E116 tracks many modules, E10 measures one machine, and how a tool's state follows from its
    /// chambers' is a question with more than one sensible answer. A machine that declares itself
    /// as a module - as the samples here do - names that one.
    /// </param>
    /// <param name="options">The conventions. <see langword="null"/> takes the defaults.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="moduleId"/> is empty.</exception>
    public PerformanceProductivityBridge(
        PerformanceTracker performance,
        ProductivityTracker productivity,
        string moduleId,
        PerformanceProductivityOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(performance);
        ArgumentNullException.ThrowIfNull(productivity);
        ArgumentException.ThrowIfNullOrEmpty(moduleId);

        _performance = performance;
        _productivity = productivity;
        ModuleId = moduleId;
        Options = options ?? new PerformanceProductivityOptions();
    }

    /// <summary>Raised after the bridge has moved the tool, or tried to.</summary>
    /// <remarks>Raised inside the performance tracker's lock while attached. Queue the work.</remarks>
    public event EventHandler<PerformanceProductivityAppliedEventArgs>? Applied;

    /// <summary>Raised whenever a task type or a blocked reason was not mapped.</summary>
    /// <remarks>
    /// Every time, not once. A fab whose mapping is half finished should watch this count grow
    /// rather than get one warning at start-up and a quietly wrong shift report afterwards.
    /// </remarks>
    public event EventHandler<PerformanceProductivityMappingEventArgs>? MappingMissing;

    /// <summary>Gets the module this bridge watches.</summary>
    public string ModuleId { get; }

    /// <summary>Gets the conventions this bridge follows.</summary>
    public PerformanceProductivityOptions Options { get; }

    /// <summary>Gets a value indicating whether this bridge is listening to the performance tracker.</summary>
    public bool IsAttached => _stateHandler is not null;

    /// <summary>Listens to the module, so the tool's E10 state follows it by itself.</summary>
    /// <remarks>
    /// <para>
    /// Attaching twice does nothing the second time. It listens for three things: the module
    /// moving, the module's blocked reason changing - which is a different E10 state even though
    /// E116 did not move, since <c>NO_MATERIAL</c> and <c>INTERLOCK</c> are standby and downtime -
    /// and, when <see cref="PerformanceProductivityOptions.CountsTasksAsCycles"/> is on, a task
    /// ending.
    /// </para>
    /// <para>
    /// Attaching does not bring the tool in line with where the module already is; call
    /// <see cref="Apply"/> for that, which is what the registration does.
    /// </para>
    /// </remarks>
    public void Attach()
    {
        lock (_gate)
        {
            if (_stateHandler is not null)
            {
                return;
            }

            _stateHandler = OnStateChanged;
            _reasonHandler = OnReasonChanged;
            _taskHandler = OnTaskEnded;

            _performance.StateChanged += _stateHandler;
            _performance.BlockedReasonChanged += _reasonHandler;
            _performance.TaskEnded += _taskHandler;
        }
    }

    /// <summary>Stops listening.</summary>
    /// <remarks>
    /// The tool stays where the bridge last put it. A bridge that reset a shift's accounting on
    /// being detached would be a worse surprise than one that did not - the same rule the material
    /// joins follow.
    /// </remarks>
    public void Detach()
    {
        lock (_gate)
        {
            if (_stateHandler is null)
            {
                return;
            }

            _performance.StateChanged -= _stateHandler;
            _performance.BlockedReasonChanged -= _reasonHandler;
            _performance.TaskEnded -= _taskHandler;

            _stateHandler = null;
            _reasonHandler = null;
            _taskHandler = null;
        }
    }

    /// <summary>Brings the tool's E10 state in line with where the module is now.</summary>
    /// <returns>What came of it, including the state it mapped to when the move was refused.</returns>
    /// <remarks>
    /// For start-up, and for a bridge driven by hand rather than attached. A tool already in the
    /// state the module implies comes back refused, with E10's own words for why - which is not a
    /// failure and <see cref="PerformanceProductivityResult.State"/> says what it is.
    /// </remarks>
    public PerformanceProductivityResult Apply()
    {
        if (_performance.FindModule(ModuleId) is not { } module)
        {
            return new PerformanceProductivityResult
            {
                IsAccepted = false,
                Reason = $"there is no module '{ModuleId}' to follow.",
                State = _productivity.State,
            };
        }

        return Follow(module.State, module.CurrentTask?.TaskType, module.BlockedReason?.Code, module.CurrentTask?.Name);
    }

    /// <summary>Works out what an E116 state means in E10's terms, without moving anything.</summary>
    /// <param name="state">What the module is doing.</param>
    /// <param name="taskType">Its task's type, when it has one.</param>
    /// <param name="blockedReason">Its blocked reason code, when it has one.</param>
    /// <returns>The E10 state, and whether it came from a map or a fallback.</returns>
    /// <remarks>
    /// Public because it is the whole of the decision, and a fab checking that its mapping says
    /// what it thinks should not have to drive a clock to find out.
    /// </remarks>
    public (EquipmentState State, bool FromFallback) Map(
        PerformanceState state,
        string? taskType = null,
        string? blockedReason = null)
    {
        switch (state)
        {
            case PerformanceState.Executing:
            {
                if (taskType is null or "" && Options.UntypedTaskState is { } untyped)
                {
                    return (untyped, true);
                }

                return Options.TaskStates.TryGetValue(taskType ?? string.Empty, out var mapped)
                    ? (mapped, false)
                    : (Options.UnmappedTaskState, true);
            }

            case PerformanceState.Blocked:
            {
                return Options.BlockedStates.TryGetValue(blockedReason ?? string.Empty, out var mapped)
                    ? (mapped, false)
                    : (Options.UnmappedBlockedState, true);
            }

            default:
            {
                // The one decision that is not about the tool at all: an idle module is on standby
                // if the factory wanted the tool and non-scheduled if it did not.
                return (Options.IsScheduled() ? EquipmentState.Standby : EquipmentState.NonScheduled, false);
            }
        }
    }

    private void OnStateChanged(object? sender, PerformanceStateChangedEventArgs e)
    {
        if (!string.Equals(e.Module.ModuleId, ModuleId, StringComparison.Ordinal))
        {
            return;
        }

        Follow(e.Current, e.Module.CurrentTask?.TaskType, e.Module.BlockedReason?.Code, e.Module.CurrentTask?.Name);
    }

    private void OnReasonChanged(object? sender, BlockedReasonChangedEventArgs e)
    {
        if (!string.Equals(e.Module.ModuleId, ModuleId, StringComparison.Ordinal))
        {
            return;
        }

        // E116 did not move, and E10 very likely has to: NO_MATERIAL is the factory's problem and
        // INTERLOCK is the tool's, and a bridge that only watched transitions would charge the
        // whole stretch to whichever reason came first.
        Follow(PerformanceState.Blocked, e.Module.CurrentTask?.TaskType, e.Current.Code, e.Module.CurrentTask?.Name);
    }

    private void OnTaskEnded(object? sender, PerformanceTaskEndedEventArgs e)
    {
        if (!Options.CountsTasksAsCycles
            || !string.Equals(e.Module.ModuleId, ModuleId, StringComparison.Ordinal))
        {
            return;
        }

        _productivity.RecordCycles();
    }

    private PerformanceProductivityResult Follow(
        PerformanceState state,
        string? taskType,
        string? blockedReason,
        string? taskName)
    {
        var (mapped, fromFallback) = Map(state, taskType, blockedReason);

        if (fromFallback)
        {
            MappingMissing?.Invoke(this, new PerformanceProductivityMappingEventArgs
            {
                ModuleId = ModuleId,
                PerformanceState = state,
                TaskType = taskType ?? string.Empty,
                BlockedReason = blockedReason ?? string.Empty,
                Charged = mapped,
            });
        }

        var moved = _productivity.MoveTo(mapped, Reason(state, mapped, taskType, blockedReason, taskName));

        var result = new PerformanceProductivityResult
        {
            IsAccepted = moved.IsAccepted,
            Reason = moved.Reason,
            State = mapped,
            FromFallback = fromFallback,
        };

        Applied?.Invoke(this, new PerformanceProductivityAppliedEventArgs
        {
            ModuleId = ModuleId,
            PerformanceState = state,
            Result = result,
        });

        return result;
    }

    /// <summary>
    /// The words E10 shows for a state the bridge chose.
    /// </summary>
    /// <remarks>
    /// E10 requires a reason for its two downtime states, so this cannot return nothing for them.
    /// With <see cref="PerformanceProductivityOptions.CarriesReasons"/> on it is the machine's own
    /// words - a blocked reason code, a task name - because that is what somebody reading a
    /// downtime wants. With it off the bridge says only where the figure came from, which is worse
    /// but still answers E10.
    /// </remarks>
    private string Reason(
        PerformanceState state,
        EquipmentState mapped,
        string? taskType,
        string? blockedReason,
        string? taskName)
    {
        if (!Options.CarriesReasons)
        {
            return mapped is EquipmentState.UnscheduledDowntime or EquipmentState.ScheduledDowntime
                ? $"{ModuleId} is {state}"
                : string.Empty;
        }

        return state switch
        {
            PerformanceState.Blocked => $"{ModuleId}: {Words(blockedReason) ?? "blocked"}",
            PerformanceState.Executing => $"{ModuleId}: {Words(taskName) ?? "executing"}"
                + (Words(taskType) is { } type ? $" ({type})" : string.Empty),
            _ => mapped is EquipmentState.UnscheduledDowntime or EquipmentState.ScheduledDowntime
                ? $"{ModuleId} is {state}"
                : string.Empty,
        };
    }

    private static string? Words(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
