# Barbatos.SecsGem.PerformanceFlow

The join between the **equipment-performance standards** - **SEMI E116** and **SEMI E10/E79** - for
**.NET 8, 9 and 10**, which neither of them makes itself. One module's EPT state drives the tool's
six E10 states, and every convention is replaceable.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.PerformanceFlow
```

Depends on `Barbatos.SecsGem.PerformanceTracking` and `Barbatos.SecsGem.Productivity`, which stay
independent of each other - that is what makes this a third package rather than a method on one of
them, the same rule [`MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md) follows.

## Why neither standard does this itself

E116 says what a chamber is doing this minute. E10 says what the tool was for the shift. Nothing
carries the first into the second, so a fab runs both and types the second one in off the first -
and that is where a day's availability stops being a measurement and becomes somebody's opinion of
one.

Both of those packages say, in writing, that the mapping cannot be made. **They are right about
the states.** Nothing about the word *executing* tells you whether a tool was producing, learning
or being tested, and nothing about *idle* tells you whether anybody wanted it.

**What makes the join possible is the task type.** E116 does not only record that a module is
executing; it records *what it is executing* - and a task type is exactly the intent E10 is asking
after. So the mapping is from a task to a state rather than from a state to a state, and two of its
entries need no choosing at all:

| E116 | E10 | Who decided |
| --- | --- | --- |
| `Executing` a `Process` task | `Productive` | both standards, using the same word |
| `Executing` an `Engineering` task | `Engineering` | both standards, using the same word |
| `Executing` a `Support` or `Maintenance` task | **you** | a pump-down may be part of a lot or part of a setup; a maintenance task may be a service or an emergency |
| `Executing` anything else | **you** | your task types are yours |
| `Blocked` | **you**, by reason code | `NO_MATERIAL` is standby and the factory's problem; `INTERLOCK` is downtime and the tool's |
| `Idle` | `Standby` or `NonScheduled` | **you**, by calendar - it is not about the tool at all |

## Three things only a fab can say, and it is asked for all three

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddPerformanceTracking(tracker => { tracker.AddModule("EQUIPMENT"); tracker.AddModule("PM1"); })
    .AddProductivityTracking()
    .AddPerformanceProductivityBridge("EQUIPMENT", options =>
    {
        // What this machine's blocked reasons mean. There are no defaults here, because a
        // blocked reason code is the machine's own vocabulary.
        options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby;
        options.BlockedStates["NO_OPERATOR"] = EquipmentState.Standby;
        options.BlockedStates["INTERLOCK"] = EquipmentState.UnscheduledDowntime;

        // What its task types mean, beyond the two that need no choosing.
        options.TaskStates[PerformanceTaskTypes.Support] = EquipmentState.Productive;
        options.TaskStates[PerformanceTaskTypes.Maintenance] = EquipmentState.ScheduledDowntime;

        // And whether anybody staffed the shift.
        options.IsScheduled = () => Shifts.IsStaffedNow();
    })
    .RunAsHostedService();
```

1. **Which module *is* the tool** - the one required argument. E116 tracks many modules and E10
   measures one piece of equipment, and how a tool's state follows from four chambers' is a
   question with more than one sensible answer, which the E116 package already declines to answer.
   A machine that declares itself as a module names that one.
2. **What its task types and blocked reasons mean.** The vocabulary is the machine's, by design in
   both packages, so it is the fab's to translate.
3. **Whether the tool is scheduled.** Not about the tool: about whether anybody staffed the shift.

After that, nobody touches the E10 tracker again:

```csharp
// The machine's own code, reporting what the chamber is doing - which it was doing anyway.
performance.BeginTask("EQUIPMENT", "RECIPE-7", PerformanceTaskTypes.Process);
// ... six hours ...
performance.Block("EQUIPMENT", "NO_MATERIAL", "the load port is empty");

// And the shift report is a measurement.
var report = productivity.Report();
Console.WriteLine($"{report.Availability:P1} available, OEE {report.OverallEquipmentEfficiency:P1}");
```

## Where a default cannot be avoided, it is the one that cannot flatter

An unmapped task type or blocked reason is charged to `UnscheduledDowntime` and reported on
`MappingMissing` - **every time, not once.** That is deliberate in both halves:

- An unmapped reason showing up in a fab's availability as a problem is correct, because an
  unmapped reason *is* a problem. The alternative - leaving the tool wherever it was, very likely
  productive - would overstate the one number this join exists to get right.
- One warning at start-up and a quietly wrong report afterwards is how a half-finished mapping
  survives a year. A count that grows does not.

`IsScheduled` defaults to always-scheduled for the same reason: it puts every hour in the
operations denominator and so reports the **lower** of E10's two availabilities.

## The blocked reason is the headline

E116 reports a reason changing **without moving** - `pump failed` becoming `awaiting parts` is one
stretch of being blocked, which is exactly right for E116 and exactly wrong for E10 when the two
reasons mean different things. So the bridge listens to `BlockedReasonChanged` as well as
`StateChanged`:

```
E116:  Blocked(NO_MATERIAL) ------------- Blocked(INTERLOCK) -------------
E10:   Standby              ------------- UnscheduledDowntime ------------
                                          ^ a failure, and a repair when it clears
```

A bridge that only watched transitions would charge the whole stretch to whichever reason came
first. And when two reasons map to the *same* E10 state, E10's own rule takes over and treats it as
a reason change rather than a transition - so the stretch is not broken in two and no second
failure is counted. That falls out rather than being arranged, which is the nicest kind of
agreement between two packages.

## What it does not do

- **It does not derive a tool's state from several modules.** One module drives it, named by you.
  Deriving one is a question with more than one answer - a tool with one chamber blocked is still
  producing, unless that chamber is the only one the lot needs - and this package guesses nothing.
  An application that has an answer applies it by maintaining its own module and pointing the
  bridge at that.
- **It does not record units.** E79's rate and quality factors need units out and good units, and
  E116 has no idea how many wafers a task made. Call `RecordUnits` where your machine knows -
  which is where E90 or E40 knows, not here.
- **It does not add a message.** Neither standard it joins has one, and a test asserts that the
  registration changes the routing table by nothing at all.
- **It does not reset anything on `Detach`.** The tool stays where the bridge last put it: a bridge
  that threw away a shift's accounting on being detached would be a worse surprise than one that
  did not.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
