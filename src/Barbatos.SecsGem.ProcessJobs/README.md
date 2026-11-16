# Barbatos.SecsGem.ProcessJobs

Processing management - **SEMI E40** - for **.NET 8, 9 and 10**: the process job, the nine-state
machine E40 gives it, and SECS-II **stream 16** at both ends. Every transition is enforced, and
every refusal names the state that refused it.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.ProcessJobs
```

Depends on `Barbatos.SecsGem.Objects` and `Barbatos.SecsGem.Hsms`. **Not on GEM, E87, E90 or
E94**: a machine can run process jobs over whatever material and lot model it already has.

## What a process job is

One piece of work: *this material, through this recipe, once*. A control job (E94) is a host
asking for a lot to run; a process job is the machine actually running part of it. They are
separate standards because a lot can be several jobs, a job can outlive the reason it was asked
for, and a tool with four chambers runs four jobs at a time while running one lot.

## The state machine

```
Queued ---------- the machine takes it up ------------> SettingUp
SettingUp ------- set up, host-initiated -------------> WaitingForStart
    |      ------ set up, starts by itself -----------> Processing

WaitingForStart - START ------------------------------> Processing
Processing ------ PAUSE -------> Pausing -------------> Paused
Paused ---------- RESUME -----------------------------> Processing

Processing ------ its material is all processed ------> ProcessComplete
Processing, Pausing, Paused   STOP ----> Stopping ---> ProcessComplete
Processing, Pausing, Paused, Stopping   ABORT ----> Aborting ---> ProcessComplete
Queued, SettingUp, WaitingForStart   CANCEL ---------> ProcessComplete
```

**These names and numbers are the standard's own.** E87, E90 and E94 left this library inferring a
state model from the shape of the problem. E40 does not: the catalogue defines `PRSTATE` as `U1`
with ten code values, and nine of them are the states above - spelled as it spells them, numbered
as it numbers them. Code 5 is reserved there, so `ProcessJobState` has a gap where 5 would be,
because those numbers go on the wire in every S16F6, S16F8, S16F11, S16F20 and S16F29.

`ProcessComplete` is the only ending, with how it ended in `ProcessJob.Completion` - `Completed`,
`Stopped`, `Aborted` or `Cancelled`, which are this library's four names for a thing E40 has no
item for.

### The transient states are states, not moments

`Pausing`, `Stopping` and `Aborting` are where this reading of E40 does most of its work. A machine
asked to pause has to reach a point where pausing is safe, and the interval between the command and
that point is the one a host most wants to see. So a pause lands in `Pausing` and only the machine
takes it further:

```csharp
manager.Pause("PJ-1");            // Pausing - the command was accepted, not completed
manager.PauseComplete("PJ-1");    // Paused  - the machine says it is there
```

A transient state leaves either that way or by being overtaken: a **stop may overtake a pause**,
and an **abort may overtake either** - including a stop. That last transition is in this package
because of what goes wrong in a fab rather than because of the shape of the standard: a tidy stop
that hangs is exactly when somebody reaches for the other kind.

## What "enforced" means

```csharp
var manager = new ProcessJobManager { JobSpace = 4 };   // PRJOBSPACE: how many jobs this tool holds

manager.Create("PJ-1", "RECIPE-7", ["W01", "W02"]);

var tooEarly = manager.Start("PJ-1");

// "process job 'PJ-1' is Queued; only one that is WaitingForStart can be started."
Console.WriteLine(tooEarly.Reason);

manager.BeginSetup("PJ-1");       // the machine takes it up: recipe down, material in
manager.SetupComplete("PJ-1");    // and it starts itself, because StartsAutomatically is the default
manager.MaterialProcessed("PJ-1");
```

The rules this package keeps:

- A job starts only from `WaitingForStart`, pauses only from `Processing`, resumes only from
  `Paused`, and only the machine finishes a pause, a stop or an abort.
- A job is **cancelled** only before it has processed and **stopped or aborted** only after - E40
  keeping the record honest, since a cancelled job touched no material.
- A **finished** job cannot be moved at all, and keeps the ending it has.
- Only a job that is still **queued**, or one that is **over**, can be deselected. Anything in
  between holds material.
- A **queued** job can be described; one the machine has taken up cannot, because the recipe is
  going down and the material is coming in.
- **Material** is bound before processing and added up to and including it; the same piece twice is
  refused, and nothing is added when one entry is wrong.
- A **create** is refused four ways, all of them a host can put right: no identifier, no recipe, a
  name already taken, or a machine already holding `JobSpace` jobs.
- A **reorder** must name every queued job exactly once.

`JobSpace` is `null` by default, which is "no stated limit" rather than zero: a machine that has
not said how many jobs it can hold has not said it can hold none. It counts jobs, not queued jobs -
a job being set up takes the room it took while it waited.

**And no "one at a time".** E94's manager refuses to select a second control job. Nothing here
refuses a second process job, because a four-chamber tool runs four.

### The one rule a code value decides

```csharp
manager.Create("PJ-1", "RECIPE-7", recipeMethod: ProcessJobRecipeMethod.WithVariableTuning);
// ... while it is Processing:
manager.ChangeParameter("PJ-1", "TEMP", "300");     // allowed
```

A parameter change is allowed while a job is queued, setting up, waiting or paused. While it is
**processing** it is allowed only when the job's `RecipeMethod` is `WithVariableTuning` - which is
not this library's idea of a sensible rule, it is what the two code values of `PRRECIPEMETHOD`
are *for*, and the one place in these four standards where a defined code value decides a
behaviour.

A parameter the job does not already have is `NameDoesNotExist`: a change cannot invent a
parameter. The parameters a job has are the ones it was created with.

## Stream 16, both ends

E40 is the first of the four material-flow standards here with a message set of its own - E87
borrowed S3F17, E90 and E94 had nothing but stream 14 - and the second standard in the whole stack
where **both ends send primaries**, after stream 3.

```csharp
// The machine
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddProcessJobEquipment(manager => manager.JobSpace = 4)
    .RunAsHostedService();

// The host
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddProcessJobHost()
    .RunAsHostedService();
```

Twelve transactions start at the host:

| Out | In | What |
| --- | --- | --- |
| S16F1 | S16F2 | create a job - identifier, recipe, material |
| S16F3 | S16F4 | a command: `START`, `PAUSE`, `RESUME`, `STOP`, `ABORT`, `CANCEL` |
| S16F5 | S16F6 | one job's status |
| S16F7 | S16F8 | every job the machine has |
| S16F9 | S16F10 | take a job off the machine |
| S16F15 | S16F16 | add material to a job |
| S16F17 | S16F18 | change recipe parameters |
| S16F19 | S16F20 | where a job is in the queue |
| S16F21 | S16F22 | reorder the queue |
| S16F23 | S16F24 | one command, several jobs |
| S16F25 | S16F26 | bind material to a job |
| S16F27 | S16F28 | read a job's parameters |

And two start at the equipment: **S16F11**, a job changed state, and **S16F29**, the whole queue.
S16F11 is the one thing E40 has that E90 and E94 do not - a state change as a transaction of its
own, rather than a collection event somebody had to define and number.

```csharp
var acknowledgement = await host.CommandAsync("PJ-1", ProcessJobCommands.Pause);

if (acknowledgement is ProcessJobCommandAcknowledgement.DoesNotExist)
{
    // Not the same thing as CannotPerformNow, and that distinction is E40's.
}
```

### Every refusal says as much as its message can carry

| Message | Code | What it can say |
| --- | --- | --- |
| S16F2, F10, F16, F26, F12, F30 | `PJACK` | accepted, rejected. That is all. |
| S16F4, S16F24 | `CMDA` | performed, **no such command**, cannot perform now |
| S16F18 | `CPACK` | accepted, no such parameter, illegal value, illegal format, cannot change now |
| S16F22 | `HCACK` | done, bad parameter, **no such object**, and four more E5 defines |

`CMDA`, `CPACK` and `HCACK` are all defined in the catalogue *with their meanings*, which makes
E40's refusals the most informative in these four standards: E94's every no arrives as one
`OBJACK` code. `PJACK` is the exception - it has no dictionary entry at all, so this package gives
it `PREACK`'s two values and leaves the reason on the equipment side, in
`ProcessJobResult.Reason`, where a log can have it.

Partial success is normal and not an error. S16F18 carries one `CPACK` per name and S16F24 one
`CMDA` per job, so a change naming four parameters where one is misspelt leaves the other three
changed and says which failed. Nothing is rolled back: a job that started is started.

## Stream 14 describes what stream 16 drives

S16F1 carries three items - identifier, recipe, material - so everything else a job has is an
attribute, and that is E40's own division of labour:

```csharp
await objects.SetAttributesAsync("PJ-1", "ProcessJob",
[
    new ObjectAttribute { Name = "PRRECIPEMETHOD", Value = SecsItem.U1(2) },      // tuning allowed
    new ObjectAttribute { Name = "PRPROCESSSTART", Value = SecsItem.Boolean(false) },  // wait for START
    new ObjectAttribute { Name = "PRPAUSEEVENTID", Value = SecsItem.U4(2500u) },
]);
```

Attributes: `PRJOBID`, `PRSTATE`, `PPID`, `PRRECIPEMETHOD`, `PRPROCESSSTART`, `PRMTRLORDER`,
`PRPAUSEEVENTID`. **Every one of those names is a data item the catalogue defines**, five of them
with their code values - which is the first time that has been true of a provider in this stack. A
host still discovers them with S14F17 rather than assuming them.

Settable: the recipe and the three that say how it is to be run, while the job is still `Queued`;
and `PRPAUSEEVENTID` any time before the job is over, because a pause event is a reaction rather
than a plan. `PRJOBID` and `PRSTATE` are the machine's.

**No create and no delete over stream 14.** S14F5 and S14F7 are refused for a process job - the
one place in this stack a provider turns down the actionable half on purpose, because E40 has
S16F1 and S16F9 and a create over stream 14 would have to invent the recipe S16F1 insists on. An
action does work, for a host that has stream 14 and not stream 16, and it says less.

A job with no pause event reads as a **zero-length** `PRPAUSEEVENTID`, not as event zero, which is
a `CEID` somebody could have defined.

## With E87, E90 and E94

A machine that does all four calls all four, and stream 14 answers for six object types:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddCarrierEquipment(manager => manager.AddPort(1))
    .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
    .AddControlJobs()
    .AddProcessJobEquipment()
    .RunAsHostedService();
```

Carriers, load ports, substrates, substrate locations, control jobs and process jobs - four
providers, one stream, one connection. `ProcessJob.Material` is a list of plain strings rather than
E90 substrates, deliberately: correlating the two is the application's business.

Where that correlation is the usual one, it is written down for E87 and E90 in
[`Barbatos.SecsGem.MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md), which turns a
verified slot map into substrates whose identifiers are exactly what a process job's material list
wants. Nothing here depends on it.

## Telling a host what happened

```csharp
manager.JobStateChanged += (_, args) =>
{
    // Copied inside the handler, because the arguments are only valid here - and sent off the
    // manager's lock, because the event is raised inside it.
    var (job, state) = (args.Job.ProcessJobId, args.Current);

    _ = Task.Run(() => equipment.ReportStateAsync(job, state).AsTask());
};
```

Three lines, and they are the application's rather than the package's: the manager raises its event
inside its own lock, so starting a transaction from there would hold that lock across the first
stretch of it. `ReportStateAsync` takes the identifier and the state rather than the job, because a
report is what was true at a moment and the job keeps moving.

S16F11 carries a `TIME`, which makes this the only one of these four standards with a timestamp on
the wire. It comes from a `TimeProvider`, so a test can assert the sixteen characters without
waiting for midnight.

## On provenance

The states, their numbers, and most of a job's attributes are in the catalogue's data item
dictionary - `PRSTATE`, `PRJOBID`, `PRRECIPEMETHOD`, `PRPROCESSSTART`, `PRMTRLORDER`,
`PRPAUSEEVENTID`, `PRJOBSPACE`, and `PPID`, `RCMD`, `CMDA`, `CPNAME`, `CPVAL`, `CPACK`, `HCACK`,
`TIME` besides. That is far more than E87, E90 or E94 had.

The message bodies are another matter. **Not one of stream 16's twenty-eight rows is a definition**
rather than part of a representative subset - the same as stream 3, and against stream 12's
seventeen of nineteen - and five of the thirteen item names those rows use have no dictionary entry
at all. Each of those five was inferred from a defined item doing the same job:

```
PJID      written A, as PRJOBID is - the dictionary's own name for a process job identifier
MTRLID    written A, as MID is - the dictionary's own name for a material identifier
PJACK     written B[1] with PREACK's two values: 0 accepted, 1 rejected
JOBSTATE  written U1 with PRSTATE's ten code values, which are E40's state model outright
QPOSN     written U2, as PRJOBSPACE is - the one item E40 defines for counting process jobs
```

Everything is read far more loosely than it is written: a state or a code in any numeric format, a
state as its name in text, a repeating body as a list of entries or a flat run of fields, a reply
carrying only its acknowledgement code, a timestamp in any of three lengths. It is only what this
library *writes* that the inferences decide, and
[`Internal/ProcessJobCodec.cs`](Internal/ProcessJobCodec.cs) is the only file to change when a
machine disagrees. The *order* of the transitions is held by tests alone, and
[`ProcessJobManager.cs`](ProcessJobManager.cs) is the one file to change when a fab disagrees about
that.

## What is not here yet

- **S16F13 and S16F14**, which are absent from the catalogue's table altogether, so there is
  nothing to implement them from.
- **E30 remote commands** as a second way in. A GEM machine may also accept `PP-SELECT`, `START`
  and `STOP` over S2F41; mapping those onto this manager is a few lines in an application that has
  both.
- **Queue persistence.** The jobs are in memory; a machine that reboots is given its jobs again by
  its host, which is what E40 expects.
- **A scheduler.** `BeginSetup` is the machine's decision and this package does not make it: which
  job to take up next, and whether two may process at once, is the equipment's own business.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
