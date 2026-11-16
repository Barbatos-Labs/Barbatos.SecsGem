# Barbatos.SecsGem.ControlJobs

Control job management - **SEMI E94** - for **.NET 8, 9 and 10**: the control job object, the state
machine E94 gives it, and the commands that drive it. Enforced, with a refusal that names the state
that refused it.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.ControlJobs
```

Depends on `Barbatos.SecsGem.Objects` and `Barbatos.SecsGem.Hsms`. **Not on GEM, not on E87 and not
on E90**: a machine can run control jobs over whatever material model it already has, and a package
that insisted on carriers would be useless to a machine that does not implement them.

## What a control job is

A host's instruction to run some material: *these carriers, in this order, with that recipe*. The
state machine is how a host and a machine agree on whether it has started, whether it may be
paused, and whether it is over. It is the piece that turns E87's carriers and E90's substrates into
a lot somebody asked for.

## E94 has no messages

E87 had five transactions of its own and E90 had attributes. E94 has neither: a control job is an
*object*, and a host

- **creates** one with S14F5,
- **commands** it with S14F9 or S14F27,
- **reads** it with S14F1 or S14F19,
- **changes** a queued one with S14F3,
- and **forgets** a finished one with S14F7.

Which is why this package exists on top of
[`Barbatos.SecsGem.Objects`](../Barbatos.SecsGem.Objects/README.md) rather than beside it, and why
that package had to learn the *actionable* half of stream 14 - create, delete, act - before this
one could be written. E87 and E90 only ever read.

The provenance caveat is the same as those two, and worth repeating: the catalogue that checks
every message in this stack **has no E94 rows at all**. These states, transitions and attribute
names are E94 as this library understands it, checked by tests rather than against reference data,
and [`ControlJobManager.cs`](ControlJobManager.cs) is the one file to change when a fab disagrees.
What *is* checked is the wire underneath: every item an S14F5, an S14F9 or an S14F1 carries is
defined in the catalogue, which is more than either chapter before this one could say.

## The state machine

```
Queued ---------- Select ------------------------------> Selected
Selected -------- Deselect ----------------------------> Queued
    |     ------- its material is ready, host-initiated > WaitingForStart
    |     ------- its material is ready, automatic -----> Executing

WaitingForStart - Start -------------------------------> Executing
Executing <----- Pause / Resume ----------------------> Paused

Executing ------- all its material is done ------------> Completed
Executing, Paused  Abort / Stop ----------------------> Completed
Queued, Selected, WaitingForStart   Cancel -----------> Completed
```

`Completed` is the only ending, and *how* it ended is `ControlJob.Completion` -
`Completed`, `Aborted`, `Stopped` or `Cancelled` - rather than four terminal states. Nothing is
reachable from it.

The difference between those four is the whole point. A job that worked through its material is not
a job somebody aborted; a job that was cancelled before it ran touched no material at all. A host
that only knew "finished" would chase wafers that were never loaded.

## What "enforced" means

```csharp
var manager = new ControlJobManager();

manager.Create("CJ-1", ["CAR-001", "CAR-002"], ControlJobStartMethod.HostInitiated);

var tooEarly = manager.Start("CJ-1");

// "control job 'CJ-1' is Queued; only one that is WaitingForStart can be started."
Console.WriteLine(tooEarly.Reason);

manager.Select("CJ-1");          // chosen to run next; its carriers are being brought in
manager.MaterialReady("CJ-1");   // the machine's own transition - now WaitingForStart
manager.Start("CJ-1");           // and now allowed
```

The rules this package keeps:

- **One job at a time.** A job cannot be selected while another is selected, waiting or running.
  Two selected jobs are two things believing they own the material, which is the difference between
  a queue and a race.
- A job can only be **started** from `WaitingForStart`, **paused** from `Executing` and **resumed**
  from `Paused`.
- A job can only be **cancelled** before it has run, and only **aborted** or **stopped** after it
  has. That is E94 keeping the record honest.
- A **finished** job cannot be moved at all, and the ending it has is the ending it keeps.
- A **running** job cannot be deselected: it is stopped or aborted, which says *why*. Deselecting
  would leave material half processed with nothing recording it.
- Only a **finished** job can be deleted. Deleting a live one would leave its material with nothing
  to account for it.
- A queued job can be **changed**; a selected one cannot. Changing the material under a job whose
  carriers are already arriving is the kind of edit that loses wafers.
- A **reorder** must name every queued job exactly once - one that left a job out would be a
  deletion wearing a reorder's clothes.

`MaterialReady` is the machine's own transition, not a host command, and the one place
`StartMethod` decides anything: an automatic job starts there, a host-initiated one waits.

Thread safety: one lock around every transition, with the event raised inside it after the state
has moved. A handler reading the job sees the state the event announces, and must not call back in.

## Over the wire

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddControlJobs()
    .RunAsHostedService();
```

That registers the manager, the object provider, and the eleven primaries of object services -
which are the only messages E94 has, because they are stream 14's. `AddControlJobs(configure)`
takes a callback for a machine that keeps its queue across a restart; usually it is left empty,
because a control job arrives as an S14F5.

A host then runs a whole job without a single message outside stream 14:

```csharp
// Queue it - S14F5. The machine names it, unless the host asked for a name.
var created = await objects.CreateAsync("ControlJob",
[
    new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-1") },
    new ObjectAttribute { Name = "CarrierInputSpec", Value = SecsItem.L(SecsItem.A("CAR-001")) },
    new ObjectAttribute { Name = "StartMethod", Value = SecsItem.A("HostInitiated") },
    new ObjectAttribute { Name = "ProcessingCtrlSpec", Value = SecsItem.A("RECIPE-A") },
]);

// Drive it - S14F9.
await objects.ActAsync(created.ObjectId, "ControlJob", ControlJobCommands.Select);
await objects.ActAsync(created.ObjectId, "ControlJob", ControlJobCommands.Start);

// Watch it - S14F1.
await objects.GetAttributesAsync(created.ObjectId, "ControlJob", ["State", "Completion"], values =>
{
    foreach (var attribute in values.Attributes)
    {
        Console.WriteLine($"{attribute.Name} = {attribute.Value.GetString()}");
    }
});

// Several at once - S14F27, which carries no object type at all.
await objects.ActAsync(["CJ-1", "CJ-2"], ControlJobCommands.Stop);

// Forget it, once it is over - S14F7.
await objects.DeleteAsync("CJ-1", "ControlJob");
```

Attributes: `ObjID`, `State`, `Completion`, `CarrierInputSpec`, `StartMethod`, `ProcessOrderMgmt`,
`ProcessingCtrlSpec`. A host discovers them with S14F17 rather than assuming them - with E94's
spelling unverifiable here, that is the only honest answer.

A job that has not ended says so with a **zero-length** `Completion`, not with a word that would
read as an ending.

**States are readable and not writable.** The settable attributes are `CarrierInputSpec`,
`StartMethod`, `ProcessOrderMgmt` and `ProcessingCtrlSpec`, and only while the job is `Queued`;
everything else is refused with `InvalidAttribute`. A host that could write `State` would start a
job by saying it had started.

A refusal from the state machine comes back as `OBJACK = Error`, because `OBJACK` has five codes and
none of them means *not from that state*. The reason stays on the equipment side, in
`ControlJobResult.Reason`, where a log can have it. That is the one thing E94 gives up by having no
message set of its own.

## Commands

`ControlJobCommands` spells the eight E94 defines - `Select`, `Deselect`, `Start`, `Pause`,
`Resume`, `Abort`, `Stop`, `Cancel` - as `const string`, because `RCMD` is an `A` item. A
convenience and not a limit: a host may send a command this list does not name, and the manager
refuses what it does not know rather than making it impossible to ask. Matching is exact, so
`"pause"` is not `Pause`.

## With E87 and E90

A machine that does all three calls all three, and stream 14 answers for carriers, load ports,
substrates, substrate locations and control jobs together:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddCarrierEquipment(manager => manager.AddPort(1))
    .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
    .AddControlJobs()
    .RunAsHostedService();
```

Three providers, one stream, one connection. `ControlJob.Carriers` is a list of plain strings
rather than E87 carrier objects, deliberately: correlating a control job's material with the
carriers a machine has read is the application's business, and a library that did it for you would
be guessing.

## Events

```csharp
manager.JobStateChanged += (_, args) =>
{
    if (args.Current is ControlJobState.Completed)
    {
        _ = equipment.TriggerEventAsync(ControlJobCompletedEvent);   // the CEID is the machine's
    }
};
```

E94 defines collection events for these transitions, and this is where they come out. As with E87
and E90, this package does not publish them: a `CEID` belongs to the equipment that defines it, and
nothing here can know which number a machine gave to "control job completed". The completion rides
along on the one transition that is an ending, so a handler needs no second lookup.

## What is not here yet

- **E40 process jobs**, which are what a control job delegates the actual processing to. Another
  object model over the same stream 14.
- **A pause event.** E94 lets a host nominate an event that pauses a job when it happens; here a
  pause is something somebody asks for. The hook is there - handle the collection event and call
  `Pause` - and it is a few lines in an application that has a GEM layer, which is the same shape
  the events below have.
- **E30 remote commands** as a second way in. A GEM machine may also accept `PP-SELECT`, `START`
  and `STOP` over S2F41; mapping those onto this manager is a few lines in an application that has
  both.
- **Queue persistence.** The jobs are in memory; a machine that reboots is given its queue again by
  its host, which is what E94 expects.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
