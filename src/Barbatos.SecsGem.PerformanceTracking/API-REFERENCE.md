# Barbatos.SecsGem.PerformanceTracking - API reference

Namespace `Barbatos.SecsGem.PerformanceTracking`. Concepts, the state diagram and the provenance
warning live in [`README.md`](README.md); this page is member-by-member.

- [`PerformanceTracker`](#performancetracker)
- [The objects](#the-objects)
  - [`PerformanceModule`](#performancemodule)
  - [`PerformanceTask`](#performancetask)
  - [`BlockedReason`](#blockedreason)
- [The states](#the-states)
- [`PerformanceResult`](#performanceresult)
- [`PerformanceStatistics`](#performancestatistics)
- [`PerformanceObjectProvider`](#performanceobjectprovider)
- [Events](#events)
- [Registration](#registration)

## `PerformanceTracker`

`sealed class`. The modules, E116's rules, and the clock. No connection and no messages, because
E116 has none: this is the package, and every one of its tests runs without a link.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `PerformanceTracker(TimeProvider? clock = null)` | `null` means `TimeProvider.System`. A test passes `FakeTimeProvider` and advances it. |
| `Modules` | `IReadOnlyCollection<PerformanceModule> { get; }` | A snapshot, taken under the lock. |
| `AddModule` | `PerformanceModule AddModule(string moduleId)` | Declares hardware. Declaring twice returns the same module and resets nothing. |
| `FindModule` | `PerformanceModule? FindModule(string moduleId)` | |
| `BeginTask` | `PerformanceResult BeginTask(string moduleId, string taskName, string taskType = "")` | Only from `Idle`. Refused while `Executing` (end the task first) or `Blocked` (unblock first), and for an empty name. |
| `EndTask` | `PerformanceResult EndTask(string moduleId)` | Only for a module that has a task. From `Executing` it lands `Idle`; from `Blocked` the module **stays blocked** and keeps its reason. |
| `Block` | `PerformanceResult Block(string moduleId, string reasonCode, string reasonText = "")` | Keeps a task in hand. Refused for an empty code, and for the same code twice. A different code is a reason change with no transition. |
| `Unblock` | `PerformanceResult Unblock(string moduleId)` | Only from `Blocked`. Goes to `Executing` when a task survived and `Idle` when none did - derived, not chosen. |
| `GoIdle` | `PerformanceResult GoIdle(string moduleId)` | The way out of anywhere. Abandons a task in hand (`Completed` false) and clears a block. Refused when already `Idle`. |
| `Statistics` | `PerformanceStatistics? Statistics(string moduleId)` | A snapshot taken now; `null` for a module nobody declared. |
| `ResetStatistics` | `PerformanceResult ResetStatistics(string moduleId)` | Starts the window again. Moves no state and raises nothing. |
| `StateChanged` | `event EventHandler<PerformanceStateChangedEventArgs>?` | After the move, inside the lock. |
| `TaskEnded` | `event EventHandler<PerformanceTaskEndedEventArgs>?` | After `StateChanged` when the ending moved the module; alone when it did not. |
| `BlockedReasonChanged` | `event EventHandler<BlockedReasonChangedEventArgs>?` | Never with `StateChanged`: the module did not move. |

Every refusal carries a `Reason` naming the state that refused it. Handlers must not call back into
the tracker - the events are raised under its lock.

**Durations come from `TimeProvider.GetElapsedTime`; timestamps come from `GetUtcNow`.** A clock
correction cannot move the accounting; it can move `Statistics.Since`, which is a label.

## The objects

### `PerformanceModule`

`sealed class`. Public getters, **internal setters**: the only thing that can move a module is the
tracker. A module whose state could be assigned could be made to look productive while it stood
still.

| Member | Type | Notes |
| --- | --- | --- |
| `ModuleId` | `string` | |
| `State` | `PerformanceState` | |
| `PreviousState` | `PerformanceState` | Equal to `State` until the module first moves. |
| `StateSince` | `DateTimeOffset` | Wall clock, because a host asked what time it was. |
| `CurrentTask` | `PerformanceTask?` | Not `null` exactly when `Executing`, or `Blocked` having been `Executing`. |
| `BlockedReason` | `BlockedReason?` | Not `null` exactly when `Blocked`. |
| `HasTask` | `bool` | True while blocked half way through one, which is the point. |

### `PerformanceTask`

`sealed class`. `Name`, `TaskType` (one of `PerformanceTaskTypes` by convention, anything in fact,
empty when the machine did not say), `StartedAt`. A task survives a block, so one task can gather
several stretches of being stuck; the total comes out on `PerformanceTaskEndedEventArgs.Blocked`.

### `BlockedReason`

`sealed class`. `Code`, `Text`, `Since`. A reason that replaces another while the module stays
blocked gets its own `Since`, while the module's `StateSince` does not move.

## The states

| Type | Members |
| --- | --- |
| `PerformanceState` | `Idle` = 0, `Executing` = 1, `Blocked` = 2 |
| `PerformanceTaskTypes` | `const string` `Process`, `Support`, `Maintenance`, `Engineering` - conventions, not a closed set |

`Idle` is zero because it is where a module starts. The numbers are this library's; the names are
E116's.

## `PerformanceResult`

`sealed class`. `IsAccepted` (`required bool`) and `Reason` (`string`).

No acknowledgement code, because E116 has no messages to carry one. An application that must answer
a host maps the reason to whatever code that message has.

## `PerformanceStatistics`

`sealed class`, all `required init`. A snapshot at the moment it was asked for.

| Member | Type | Notes |
| --- | --- | --- |
| `ModuleId` | `string` | |
| `State` | `PerformanceState` | |
| `Since` | `DateTimeOffset` | When the module was declared, or last reset. |
| `TimeInState` | `TimeSpan` | Only the part of the current state inside this window. |
| `TimeIdle` / `TimeExecuting` / `TimeBlocked` | `TimeSpan` | The current state's elapsed time is counted in, so these add up to `Total`. |
| `TasksCompleted` / `TasksAbandoned` | `int` | |
| `Total` | `TimeSpan` | The three added together. |
| `ExecutingShare` | `double` | Between zero and one; zero for an empty window rather than a division by zero. |

## `PerformanceObjectProvider`

`sealed class : IObjectProvider` (from `Barbatos.SecsGem.Objects`). E116's entire wire presence.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `PerformanceObjectProvider(PerformanceTracker tracker)` | |
| `ModuleType` | `const string` = `"EPTModule"` | The `OBJTYPE` on the wire. |

Readable attributes are listed in the README. **Writable: none.** A set on a module is
`InvalidAttribute`, a set on anything else `InvalidObjectInstance`, an action `Error` - returned
explicitly rather than by answering nothing, because answering nothing means "no object of mine".
`CreateAsync` and `DeleteAsync` are the interface defaults (`InvalidObjectType`): a module is
hardware.

## Events

| Type | Members |
| --- | --- |
| `PerformanceStateChangedEventArgs` | `Module`, `Previous`, `Current` |
| `PerformanceTaskEndedEventArgs` | `Module`, `Task`, `Elapsed`, `Blocked`, `Completed` |
| `BlockedReasonChangedEventArgs` | `Module`, `Previous`, `Current` |

Raised after the state has moved and inside the tracker's lock. `TaskEnded` is the one this package
exists to raise: every task's end is one of these, because a module cannot begin a second task
without ending the first.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddPerformanceTracking(Action<PerformanceTracker>? configure = null)` | `PerformanceTracker` (modules declared in `configure`, clock from the container), `PerformanceObjectProvider` as an `IObjectProvider`, and the eleven primaries of object services. |

One method, because E116 has one side. It composes with `AddCarrierEquipment`,
`AddSubstrateTracking`, `AddControlJobs` and `AddProcessJobEquipment`: every provider answers,
through `CompositeObjectProvider`. Calling it twice is not a start-up failure.

## Outliving the process

| Member | Signature | Notes |
| --- | --- | --- |
| `PerformanceTracker.Capture` | `PerformanceTrackerSnapshot Capture()` | Folds each module's open stretch into its total, so a snapshot is a whole ledger. |
| `PerformanceTracker.Restore` | `PerformanceRestoreReport Restore(PerformanceTrackerSnapshot snapshot)` | Into a tracker that has accounted for nothing. Raises nothing. |

`PerformanceTrackerSnapshot` holds `CapturedAt` (`DateTimeOffset`) and `Modules`
(`IReadOnlyList<PerformanceModuleSnapshot>`). `PerformanceModuleSnapshot` carries the three totals,
the two counts, `AccountingSince`, the state and its entry time, and the task and blocked reason
flattened to strings.

`PerformanceRestoreReport`: `IsAccepted`, `Reason`, `Restored`, `Adjusted`, `Count` and
**`Unaccounted`** (`TimeSpan`) - the interval between the capture and the restore, which is charged
to **nothing**, because E116 has no state that means switched off. See the
[README](README.md#outliving-the-process-the-ledger-comes-back-the-claim-does-not), which also says
why `Barbatos.SecsGem.Productivity` answers that the other way.
