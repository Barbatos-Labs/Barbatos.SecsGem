# Barbatos.SecsGem.Productivity - API reference

Namespace `Barbatos.SecsGem.Productivity`. Concepts, the state hierarchy and the note on why there
is no wire presence live in [`README.md`](README.md); this page is member-by-member.

- [`ProductivityTracker`](#productivitytracker)
- [`EquipmentState`](#equipmentstate)
- [`ProductivityResult`](#productivityresult)
- [`ProductivityReport`](#productivityreport)
- [Events](#events)
- [Registration](#registration)

## `ProductivityTracker`

`sealed class`. One tool's time in E10's six states, and the arithmetic E10 and E79 do over it. No
connection, no messages and no object type, because neither standard defines any.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ProductivityTracker(TimeProvider? clock = null)` | `null` means `TimeProvider.System`. Starts in `NonScheduled`. |
| `State` | `EquipmentState { get; }` | |
| `StateReason` | `string { get; }` | Empty for a state with no stated reason. |
| `StateSince` | `DateTimeOffset { get; }` | Wall clock. A reset does not move it. |
| `MoveTo` | `ProductivityResult MoveTo(EquipmentState state, string reason = "")` | The one mover. See below. |
| `RecordCycles` | `void RecordCycles(long cycles = 1)` | Whatever the fab counts as a cycle. Feeds `MeanCyclesBetweenFailures` and nothing else. |
| `RecordUnits` | `void RecordUnits(long actual, long effective, TimeSpan theoreticalEach)` | Units out, good ones, and what one should have taken. |
| `Report` | `ProductivityReport Report()` | A snapshot taken now. |
| `Reset` | `ProductivityResult Reset()` | Starts the window again. Moves no state and raises nothing. |
| `StateChanged` | `event EventHandler<EquipmentStateChangedEventArgs>?` | After the move, inside the lock. |
| `ReasonChanged` | `event EventHandler<ProductivityReasonChangedEventArgs>?` | Never with `StateChanged`: the tool did not move. |

**`MoveTo`** refuses exactly two things, both of them a caller saying nothing: a downtime with an
empty reason, and the state the tool is already in for the reason it is already in it. The same
state for a different reason is accepted as a reason change with no transition, so the stretch is
not broken in two and nothing is counted twice. A `reason` given for `Productive` is dropped -
producing needs no excuse. A state outside the six throws `ArgumentOutOfRangeException`; a `null`
reason throws `ArgumentNullException`.

**`RecordUnits`** throws `ArgumentOutOfRangeException` for a negative count, a negative theoretical
time, or more effective units than actual ones. It does not check what state the tool is in: E79
divides units by *productive* time, so counting an engineering wafer here makes `RateEfficiency`
come out above one, which is the number telling you the fab meant not to.

**One tracker is one tool**, because E10 measures a piece of equipment. Contrast
`PerformanceTracking`, whose tracker holds many modules because E116 is explicitly per module.

Durations come from `TimeProvider.GetElapsedTime`; timestamps come from `GetUtcNow`. A clock
correction cannot move the accounting.

## `EquipmentState`

| Member | Value | Rolls up into |
| --- | --- | --- |
| `NonScheduled` | 0 | Total only |
| `UnscheduledDowntime` | 1 | Downtime, Operations, Total |
| `ScheduledDowntime` | 2 | Downtime, Operations, Total |
| `Engineering` | 3 | Uptime, Operations, Total |
| `Standby` | 4 | Uptime, Operations, Total |
| `Productive` | 5 | Uptime, Operations, Total |

Not a sequence: E10 classifies time, so every state can follow every other. `NonScheduled` is zero
because it is where a tracker starts - claiming the tool was on standby would put time nobody
scheduled into the denominator of every operations ratio.

## `ProductivityResult`

`sealed class`. `IsAccepted` (`required bool`) and `Reason` (`string`). No acknowledgement code,
because neither standard has a message to carry one.

## `ProductivityReport`

`sealed class`, all `required init` except the derived members. A snapshot at the moment it was
asked for: the current state's elapsed time is counted in, so the six buckets add up to `Total`.

| Group | Members |
| --- | --- |
| window | `Since`, `State`, `StateReason`, `TimeInState` |
| the six buckets | `NonScheduled`, `UnscheduledDowntime`, `ScheduledDowntime`, `Engineering`, `Standby`, `Productive` |
| roll-ups | `Downtime`, `Uptime`, `Operations`, `Total` |
| counts | `Failures`, `ScheduledInterrupts`, `Interrupts`, `Repairs`, `Cycles`, `ActualUnits`, `EffectiveUnits` |
| E79 inputs | `TheoreticalActualTime`, `TheoreticalEffectiveTime` |

### E10's metrics

| Member | Definition |
| --- | --- |
| `Availability` | `Uptime / Operations` |
| `OperationalUtilisation` | `Productive / Operations` |
| `TotalUtilisation` | `Productive / Total` |
| `MeanTimeBetweenFailures` | `Operations / Failures` |
| `MeanTimeBetweenInterrupts` | `Operations / Interrupts` |
| `MeanTimeToRepair` | completed repair time `/ Repairs` - **not** `UnscheduledDowntime / Repairs` |
| `MeanCyclesBetweenFailures` | `Cycles / Failures` |

E10 offers more than one denominator for the means, and these are one convention. Every bucket and
count is on the report, so a fab whose definition differs computes its own.

### E79's OEE

| Member | Definition |
| --- | --- |
| `AvailabilityEfficiency` | `Uptime / Total` - the total denominator, not `Availability`'s |
| `OperationalEfficiency` | `Productive / Uptime` |
| `RateEfficiency` | `TheoreticalActualTime / Productive` - not clamped; above one means the rate is wrong |
| `QualityEfficiency` | `TheoreticalEffectiveTime / TheoreticalActualTime` |
| `PerformanceEfficiency` | `OperationalEfficiency * RateEfficiency` |
| `OverallEquipmentEfficiency` | `TheoreticalEffectiveTime / Total` |

The four factors telescope to the last line, so their product is a free check on each of them -
and that identity is asserted both on a worked day and on forty random moves.

Every ratio is zero when its denominator is, rather than a division by zero.

## Events

| Type | Members |
| --- | --- |
| `EquipmentStateChangedEventArgs` | `Previous`, `Current`, `Reason`, `Elapsed` - the stretch that just ended, not the total |
| `ProductivityReasonChangedEventArgs` | `State`, `Previous`, `Current` |

Raised after the state has moved and inside the tracker's lock, so a handler must queue rather than
call back in. Neither standard defines a collection event, because neither defines anything on a
wire; these are here so an application can publish the state as a status variable or write its own
history row.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddProductivityTracking(Action<ProductivityTracker>? configure = null)` | One `ProductivityTracker`, taking the container's `TimeProvider`. |

**It declares nothing in the routing table**, and adds no handler, because neither standard has a
message. The only reason it hangs off the SECS builder is the clock: `AddSecsGem` already registers
a `TimeProvider`, and a package whose whole output is durations should measure with the one the
application chose. Calling it twice is not a start-up failure, and the first configuration survives.
