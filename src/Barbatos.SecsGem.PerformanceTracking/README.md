# Barbatos.SecsGem.PerformanceTracking

Equipment performance tracking - **SEMI E116** - for **.NET 8, 9 and 10**: a machine's modules, the
three states E116 gives each of them, and where the time actually went. Every duration measured
through a `TimeProvider`, from a monotonic timestamp, so an operator correcting the machine's clock
cannot push a chamber's utilisation above one.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.PerformanceTracking
```

Depends on `Barbatos.SecsGem.Objects` and `Barbatos.SecsGem.Hsms`. Not on GEM, and not on any of
the material-flow standards: a machine that wants to account for its time does not have to
implement carriers, substrates or jobs first.

## The one distinction the standard is for

A machine that is not working is either not working because nobody gave it anything to do, or not
working because something is stopping it. Those two have **different owners** - the first is the
factory's problem and the second is the machine's - and a number that adds them together cannot be
acted on by anybody. That is the whole of E116:

```
Idle         nothing to do, nothing in the way          the factory's problem
Executing    doing something, and it has a name         making product, or not: see task types
Blocked      something is in the way                    the machine's problem
```

```
Idle ---------- given something to do ---------------> Executing
Idle ---------- something is in the way -------------> Blocked

Executing ----- finished, or abandoned --------------> Idle
Executing ----- something got in the way ------------> Blocked    (the task is kept)

Blocked ------- the way is clear --------------------> Executing  (a task was kept)
    |      ---- the way is clear, nothing to do -----> Idle       (no task was kept)
    |      ---- the task is over anyway -------------> Idle
```

## E116 has no messages

A module is an *object*. Its state and its accounting reach a host through SECS-II stream 14 object
services, and the only other thing a host sees is whichever collection events the equipment chose
to define for the transitions. So there is no codec here, no equipment end answering primaries and
no host end sending them: there is a state machine that keeps the truth and a clock that keeps the
time, plus `PerformanceObjectProvider`, which is E116's entire wire presence.

Which also means the provenance caveat is as thin as E90's. The catalogue that checks every message
in this stack **has no mention of equipment performance tracking at all** - not a message, not an
item. The three state names and the ideas of a task and a blocked reason are E116's; their numbers
here, the spelling of every attribute, and each rule about which call is refused from which state
are this library's reading of it, checked by tests rather than against reference data.
[`PerformanceTracker.cs`](PerformanceTracker.cs) is the one file to change when a fab disagrees.

## A block keeps the task

The rule worth reading this package for. A chamber stopped half way through a job is still that
job's chamber, so the time it stands there belongs to the job:

```csharp
var tracker = new PerformanceTracker(timeProvider);

tracker.AddModule("PM1");                               // hardware is declared, not messaged into being

tracker.BeginTask("PM1", "RECIPE-7", PerformanceTaskTypes.Process);
// ... ten minutes of etching ...
tracker.Block("PM1", "NO_MATERIAL", "waiting for the next wafer");
// ... five minutes of standing there ...
tracker.Unblock("PM1");                                 // back to Executing: the task survived
// ... five more minutes ...

tracker.TaskEnded += (_, e) =>
    Console.WriteLine($"{e.Task.Name} took {e.Elapsed}, {e.Blocked} of it blocked");

tracker.EndTask("PM1");                                 // RECIPE-7 took 00:20:00, 00:05:00 of it blocked
```

Twenty minutes to run a recipe that needs fifteen, and the difference has a name. That is the
difference between a recipe that is slow and a recipe that keeps waiting for something, and nothing
else in this stack can tell them apart.

## What "enforced" means

```csharp
var refused = tracker.BeginTask("PM1", "RECIPE-8");

// "module 'PM1' is Executing task 'RECIPE-7'; end that task before beginning another."
Console.WriteLine(refused.Reason);
```

The rules this package keeps:

- A task can only **begin** on an idle module. One already executing must end its task first -
  swapping would end a task's accounting without reporting it, and that accounting is the whole
  output of this package. A blocked one must be unblocked first, because a blocked module is not
  available to be given work; that is what blocked means.
- A task needs a **name**, and a block needs a **reason**. An unnamed task and an unexplained block
  are both a machine refusing to say anything useful.
- A task can be **ended** by a blocked module: a job cancelled while the chamber is stuck has to be
  endable without first pretending the chamber came unstuck. The module **stays blocked** - an
  interlock does not close because a host cancelled a job - keeps its reason, loses its task, and
  reaches idle through `Unblock` when the obstruction really does go.
- **Blocking twice for the same reason** is refused: it says nothing, and a caller doing it has a
  bug. For a *different* reason it is accepted as a reason change with **no transition**, so the
  blocked clock runs continuously across it. A chamber that goes from waiting for material to
  waiting for an interlock never stopped being blocked.
- **Unblocking** goes back to executing when a task survived and to idle when none did - derived
  rather than asked for, because a caller that could choose could say the module resumed a task it
  does not have.
- `GoIdle` is the way out of anywhere and the only call that **abandons** a task, reported with
  `Completed` false. Refused when the module is already idle, because forgiving it is a clock that
  restarts whenever somebody asks a question.

## Where the time went

```csharp
var statistics = tracker.Statistics("PM1")!;

Console.WriteLine($"{statistics.TimeExecuting} working, {statistics.TimeBlocked} stuck, "
                + $"{statistics.TimeIdle} waiting for work - {statistics.ExecutingShare:P0} utilised");
```

A snapshot taken at the moment it is asked for, so the state the module is in **now** has its
elapsed time counted in: the three figures add up to `Total`. `ResetStatistics` starts the window
again - a shift boundary, say - without moving the module or raising anything, and `StateSince`
still says when the module really entered the state it is in.

**Durations come from the monotonic timestamp; timestamps come from the wall clock.** Both are the
same `TimeProvider`, so a test drives them together, but they are read differently on purpose:
`StateSince` is a time of day because a host asked what time it was, while every figure in
`PerformanceStatistics` is measured with `TimeProvider.GetElapsedTime`. An operator correcting the
machine's clock by ten minutes must not be able to make a chamber's utilisation exceed one, or go
negative - and there is a test with a clock that slips backwards to prove it cannot. The honest
other half: `Since` is a wall-clock label, so after such a correction it is out by the correction
while the durations are not.

## Task types, and why they are strings

```csharp
tracker.BeginTask("PM1", "RECIPE-7", PerformanceTaskTypes.Process);
tracker.BeginTask("PM1", "PUMP-DOWN", PerformanceTaskTypes.Support);
tracker.BeginTask("PM1", "CHAMBER-CLEAN", PerformanceTaskTypes.Maintenance);
tracker.BeginTask("PM1", "SPIN-DRY", "HouseKeeping");          // also fine
```

`PerformanceTaskTypes` holds four spellings and closes nothing off, for the same reason the carrier
actions in the E87 package are strings: the vocabulary belongs to the standard and to the fab, and
a closed enum would cap what a machine can say about itself. They matter because they are what
splits *executing* into time a wafer benefited from and time it did not - a module running a recipe
and a module running its own calibration are both executing, and only one is making product.

## Attributes over stream 14

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddPerformanceTracking(tracker =>
    {
        tracker.AddModule("EQUIPMENT");
        tracker.AddModule("PM1");
        tracker.AddModule("PM2");
    })
    .RunAsHostedService();
```

That registers the tracker, the object provider, and the eleven primaries of object services -
which are the only messages E116 has, because they are stream 14's. The tracker takes the
container's `TimeProvider`, which `AddSecsGem` already registers as `TimeProvider.System` unless
the application put its own there first.

A host reads a module the way it reads anything else on stream 14:

```csharp
await objects.GetAttributesAsync("PM1", "EPTModule", ["EPTState", "TaskName", "TimeExecuting"], values =>
{
    foreach (var attribute in values.Attributes)
    {
        Console.WriteLine($"{attribute.Name} = {attribute.Value}");
    }
});
```

Module attributes: `ObjID`, `EPTState`, `PreviousEPTState`, `EPTStateEntryTime`,
`BlockedReasonCode`, `BlockedReasonText`, `TaskName`, `TaskType`, `TaskStartTime`, `TimeInState`,
`TimeIdle`, `TimeExecuting`, `TimeBlocked`, `TasksCompleted`, `TasksAbandoned`. The times are whole
seconds as `U4`; the two timestamps are the 16-character form the catalogue gives `TIMESTAMP`;
everything a module does not have - no block, no task - comes back as a zero-length item rather
than a word a host would have to know not to believe.

**Nothing here is writable, and nothing here is actionable.** Every other provider in this stack has
at least a label a host owns; this one has none, and the refusal is the feature. A module's EPT
state is an observation of a machine, not an instruction to it: a host that could set `EPTState`
could record a chamber as executing while it stood empty, and then the utilisation this standard
exists to produce would be a number the fab wrote down itself. A set is refused with
`InvalidAttribute`, an action with `Error` - explicitly, rather than by answering nothing, because
answering nothing means "no object of mine" and the module *is* this provider's.

## Events, and why this does not depend on GEM

```csharp
tracker.StateChanged += (_, e) =>
{
    if (e.Current is PerformanceState.Blocked)
    {
        _ = equipment.TriggerEventAsync(ModuleBlockedEvent);
    }
};
```

A collection event needs a `CEID`, a `CEID` belongs to the equipment that defines it, and nothing
here can know which number a machine gave to "module blocked". So the wiring is a few lines in an
application that has a GEM layer, and a machine without one can still account for its time.

Three events, one fact each: `StateChanged` for a move, `TaskEnded` for the accounting a finished
or abandoned task produced, and `BlockedReasonChanged` for a reason that replaced another without a
transition. They are raised **after** the state has moved and **inside** the tracker's lock, so a
handler reading the module sees the state the event announces - and must not call back in. Queue
the work.

## What is not here, and why

- **No module hierarchy.** A cluster tool's modules are a flat set, and the tool itself is another
  module beside them rather than their parent. E116 does not say how a parent's state follows from
  its children's, and it has more than one sensible answer - a tool with one chamber blocked is
  still executing, unless that chamber is the only one the lot needs - so this package keeps the
  modules it is told about and leaves the sum to whoever knows which answer their fab wants.
- **No E10 states, and no OEE.** E116 is the machine-observable layer underneath
  [E10](https://www.semi.org)'s six equipment states, and the step between them is not an
  observation: *engineering* versus *productive* is a statement about intent, and only the factory
  knows which one a given hour was. Mapping them is a decision with more than one right answer, and
  this package will not make it quietly. If a fab's answer is settled, it is the shape of a join -
  see [`Barbatos.SecsGem.MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md) for how this
  repository writes those down.
- **No history.** The tracker keeps the current state and the running totals and raises an event per
  transition, which is what an application needs to build a history in whatever it already stores.
- **No E116 collection events by number.** See above: the `CEID`s are the equipment's.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
