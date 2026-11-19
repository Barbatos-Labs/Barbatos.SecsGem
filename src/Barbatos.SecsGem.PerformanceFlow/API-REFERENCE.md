# Barbatos.SecsGem.PerformanceFlow - API reference

Namespace `Barbatos.SecsGem.PerformanceFlow`. Concepts, the mapping table and the note on why this
is a third package live in [`README.md`](README.md); this page is member-by-member.

- [`PerformanceProductivityBridge`](#performanceproductivitybridge)
- [`PerformanceProductivityOptions`](#performanceproductivityoptions)
- [`PerformanceProductivityResult`](#performanceproductivityresult)
- [Events](#events)
- [Registration](#registration)

## `PerformanceProductivityBridge`

`sealed class`. One E116 module's state driving one E10 tool's accounting.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `PerformanceProductivityBridge(PerformanceTracker performance, ProductivityTracker productivity, string moduleId, PerformanceProductivityOptions? options = null)` | `moduleId` is required and has no default that could be right. Empty throws `ArgumentException`. |
| `ModuleId` | `string { get; }` | |
| `Options` | `PerformanceProductivityOptions { get; }` | |
| `IsAttached` | `bool { get; }` | |
| `Attach` | `void Attach()` | Listens for the module moving, its blocked reason changing, and - when configured - its tasks ending. Twice does nothing. |
| `Detach` | `void Detach()` | The tool stays where it was put. |
| `Apply` | `PerformanceProductivityResult Apply()` | Brings the tool in line with where the module is now. |
| `Map` | `(EquipmentState State, bool FromFallback) Map(PerformanceState state, string? taskType = null, string? blockedReason = null)` | The whole decision, with nothing moved. |
| `Applied` | `event EventHandler<PerformanceProductivityAppliedEventArgs>?` | After every move or attempt. |
| `MappingMissing` | `event EventHandler<PerformanceProductivityMappingEventArgs>?` | Every time a fallback was used, not once. |

`Map` is public because it *is* the decision, and a fab checking that its mapping says what it
thinks should not have to drive a clock to find out.

**Lock order: performance first, productivity last, always.** E116 raises its events inside its own
lock, so an attached bridge runs there and then takes E10's. Nothing in this stack goes the other
way, and nothing may start - the rule the two material joins follow.

## `PerformanceProductivityOptions`

| Member | Type | Default |
| --- | --- | --- |
| `TaskStates` | `IDictionary<string, EquipmentState>` | `Process` → `Productive`, `Engineering` → `Engineering`. Ordinal keys. |
| `BlockedStates` | `IDictionary<string, EquipmentState>` | **empty** - a blocked reason code is the machine's own vocabulary |
| `UnmappedTaskState` | `EquipmentState` | `UnscheduledDowntime` |
| `UnmappedBlockedState` | `EquipmentState` | `UnscheduledDowntime` |
| `UntypedTaskState` | `EquipmentState?` | `null` - falls through to `UnmappedTaskState` |
| `IsScheduled` | `Func<bool>` | always scheduled, which reports the lower availability |
| `CarriesReasons` | `bool` | `true` - the machine's own words reach E10's reason |
| `CountsTasksAsCycles` | `bool` | `false` - a cycle is whatever the fab counts |

`Support` and `Maintenance` are deliberately absent from `TaskStates`: a pump-down may be part of
producing a lot or part of a setup, and a maintenance task may be a planned service or an
emergency repair, which are different E10 states.

With `CarriesReasons` off, a downtime still gets a reason - E10 refuses one without - but it is the
bridge's words rather than the machine's, which is worse and not broken.

## `PerformanceProductivityResult`

`sealed class`. `IsAccepted`, `Reason`, `State` (the E10 state it mapped to, whether or not the
move was accepted) and `FromFallback`.

A refusal is usually not a failure: E10 refuses a move to the state the tool is already in for the
reason it is already in it, and the bridge passes that straight on. `State` says where it is.

## Events

| Type | Members |
| --- | --- |
| `PerformanceProductivityAppliedEventArgs` | `ModuleId`, `PerformanceState`, `Result` |
| `PerformanceProductivityMappingEventArgs` | `ModuleId`, `PerformanceState`, `TaskType`, `BlockedReason`, `Charged` |

Both are raised inside the performance tracker's lock while attached, so queue the work.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddPerformanceProductivityBridge(string moduleId, Action<PerformanceProductivityOptions>? configure = null)` | One bridge, attached, and already in line with the module. |

It resolves both trackers with `GetRequiredService`, so a bridge over a tracker nobody registered
is a start-up failure rather than a quietly dead join - the fault the first material join taught
this repository to test for. It declares nothing in the routing table, and a test asserts that by
building the graph twice and comparing, because `AddPerformanceTracking` *does* declare stream 14
and "nothing is declared" would be false about the container.
