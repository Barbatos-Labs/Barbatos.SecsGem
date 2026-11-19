# Barbatos.SecsGem.Productivity

Equipment productivity - **SEMI E10** and **SEMI E79** - for **.NET 8, 9 and 10**: the six basic
equipment states, how they add up, and the arithmetic both standards do over them. Availability,
utilisation, MTBF, MTBI, MTTR, MCBF, and overall equipment efficiency with all four of its factors.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Productivity
```

Depends on `Barbatos.SecsGem.Hsms` for one thing only - the container's `TimeProvider` - and on
nothing else in the stack. Not on GEM, not on `Objects`, not on `PerformanceTracking`.

## Neither standard has a wire

E10 and E79 define **measurements**: six state names, how they roll up, and the formulae. There is
no message, no item and no code value, and the catalogue that checks every message in this stack
has no mention of either standard - which for once is not thin reference data but the right answer,
because there is nothing for SECS-II to carry.

So this package has **no object type and no stream of its own**, and that is deliberate. The
numbers reach a host as whatever the application already publishes:

- as GEM **status variables**, which is how most fabs do it - `Availability` and
  `OverallEquipmentEfficiency` are status variables in the ordinary sense, read with S1F3 and
  reportable on an event;
- or per module through E39's enhanced stream 2, if a fab wants a figure per chamber -
  see [`Barbatos.SecsGem.Objects`](../Barbatos.SecsGem.Objects/README.md).

A library that invented an object type for E10 would be inventing a wire presence the standard
does not have.

## The six states, and the one distinction that pays for the standard

```
Total Time
|-- Non-Scheduled Time                  nobody asked the tool to be available
`-- Operations Time
    |-- Equipment Downtime
    |   |-- Unscheduled Downtime        it broke
    |   `-- Scheduled Downtime          planned: maintenance, setup, a consumable
    `-- Equipment Uptime
        |-- Engineering Time            able to produce, being used to find something out
        |-- Standby Time                able to produce, nothing to produce with
        `-- Productive Time             producing
```

**Standby is not downtime.** A tool with nothing to do is the factory's problem and a tool that
cannot work is the tool's, and a figure that adds them together cannot be acted on by anybody -
the same distinction [`PerformanceTracking`](../Barbatos.SecsGem.PerformanceTracking/README.md)
draws one level down for E116. **Non-scheduled is a third thing again**: not a complaint about
anybody, just a shift nobody staffed, and it is why every ratio here comes in two forms.

**It is not a sequence.** Every other behaviour package in this stack says which transition may
follow which; this one says nothing of the kind, because E10 classifies time rather than
describing a life cycle. A tool can go from producing to broken to not-scheduled in whatever order
a day takes, so there is one `MoveTo` and no per-state method - there is no per-state precondition
to hang one on.

```csharp
var tracker = new ProductivityTracker(timeProvider);

tracker.MoveTo(EquipmentState.ScheduledDowntime, "preventive maintenance");
// ... two hours ...
tracker.MoveTo(EquipmentState.Standby, "no material");
// ... four hours ...
tracker.MoveTo(EquipmentState.Productive);
// ... ten hours, and a hundred wafers at five minutes each ...
tracker.RecordUnits(actual: 100, effective: 90, theoreticalEach: TimeSpan.FromMinutes(5));
tracker.MoveTo(EquipmentState.UnscheduledDowntime, "pump failed");
```

Two refusals, and both are a caller saying nothing: a downtime with no reason, and the state the
tool is already in for the reason it is already in it. The second matters more than it looks -
forgiving it would restart the clock on the current stretch every time a polling loop reported the
same state again, and every duration in the report would come out short. The same state for a
*different* reason is accepted and reported as `ReasonChanged` with no transition, because "pump
failed" becoming "awaiting parts" is one failure with two phases.

## The counts are the transitions

Nobody counts failures by hand:

| What E10 counts | What this package watches |
| --- | --- |
| a failure | an entry into `UnscheduledDowntime` |
| a scheduled interrupt | an entry into `ScheduledDowntime` |
| an interrupt | either of those |
| a repair | an exit from `UnscheduledDowntime` **into a state the tool can work in** |

Which means the counts cannot drift away from the time buckets, and `MTBF` cannot disagree with
the availability computed over the same window.

**A repair is only a repair once the tool can work again**, and that qualifier is the whole
difference between an honest MTTR and a flattering one. A shift that ends while the tool is still
broken is not a completed repair: its stretch stays in the downtime bucket and out of the mean. And
`MeanTimeToRepair` divides only *completed* repair time, never `UnscheduledDowntime` - because that
figure includes a repair still running, and dividing one by the other would make a tool that is
broken right now look quicker at being fixed the longer it stays broken.

## Two denominators, and confusing them is the commonest mistake

```csharp
var report = tracker.Report();

Console.WriteLine($"available {report.Availability:P0} of the time we asked for it");
Console.WriteLine($"available {report.AvailabilityEfficiency:P0} of the whole day");
```

A tool that ran perfectly through one staffed shift of a three-shift day is at **100%** on the
operations denominator and **33%** on the total one, and both are correct answers to different
questions. The names say which: `Availability` and `OperationalUtilisation` are over operations
time; `AvailabilityEfficiency` and `TotalUtilisation` are over the whole window. E79's OEE chain
uses the total denominator, so its first factor is `AvailabilityEfficiency` and **not**
`Availability`.

Everything is on the report - all six buckets, all four roll-ups, every count - so a fab whose own
definition of a figure differs computes it from the same numbers. That matters most for `MTBF`:
E10 offers more than one denominator for it, and this package names one convention rather than
pretending there is only one.

## E79's OEE, and the identity that checks it

```csharp
Console.WriteLine($"OEE {report.OverallEquipmentEfficiency:P1}");
Console.WriteLine($"  availability  {report.AvailabilityEfficiency:P1}");
Console.WriteLine($"  operational   {report.OperationalEfficiency:P1}");
Console.WriteLine($"  rate          {report.RateEfficiency:P1}");
Console.WriteLine($"  quality       {report.QualityEfficiency:P1}");
```

The four factors **telescope**:

```
(Uptime / Total) x (Productive / Uptime) x (Theoretical / Productive) x (Effective / Theoretical)
                                                               = Effective / Total
```

So `OverallEquipmentEfficiency` is computed the direct way - one division rather than four, which
also stops it collecting four roundings - and the product of the factors is a free check on every
one of them. A test asserts the two agree, on a worked day and again on forty random moves with
awkward durations, so no arithmetic mistake in a factor can hide.

`RateEfficiency` is **not clamped**. Above one means the fab's theoretical rate is wrong, which is
a real finding about the fab's own figure, and hiding it would hide that.

**The theoretical time is an argument, not a property of the tracker**, and that is why
`RecordUnits` takes three things. E79's theoretical production time is a sum over units, and a tool
running two recipes has two rates - a single rate on the tracker would be wrong for every lot but
one.

## Reporting a period

`Reset()` starts the accounting again - a shift boundary, a day, a quarter - without moving the
tool or raising anything, and `StateSince` still says when the tool really entered the state it is
in. Durations come from `TimeProvider.GetElapsedTime` and only the timestamps a person reads come
off the wall clock, so a shift report survives somebody correcting the machine's clock by two
hours; there is a test with a clock that slips backwards to prove it.

One tracker is **one tool**, because E10 measures a piece of equipment. An application accounting
for four tools builds four. That is the opposite of E116's tracker, which holds many modules
because E116 is explicitly per module - the two standards sit one level apart: E116 says what a
chamber was doing this minute, E10 says what the tool was for this shift.

## What is not here, and why

- **No mapping from E116's three states onto these six**, and this is the one worth reading. It is
  the obvious next thing to want, and it cannot be done without the factory: E116 `Executing`
  could be productive, engineering or a production test depending on what the task *was for*;
  `Idle` could be standby or non-scheduled depending on whether anybody staffed the shift;
  `Blocked` could be standby (no material) or unscheduled downtime (a failure). Every one of those
  needs intent or a schedule, and neither is observable from the tool. When a fab has settled its
  own answer it is the shape of a join - see
  [`Barbatos.SecsGem.MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md) for how this
  repository writes those down, with every convention replaceable and nothing guessed.
- **No E10 state sub-categories.** E10 subdivides each state into reason categories; here a reason
  is free text, because the categories a fab reports against are the fab's own and a closed list
  would cap what a machine can say about itself - the same choice the carrier actions in E87 made.
- **No cost model.** E79 relates productivity to cost of ownership (E35); that is a different
  standard again and wants numbers no equipment has.
- **No history.** The tracker keeps the current window and raises an event per transition, which is
  what an application needs to build a history in whatever it already stores.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
