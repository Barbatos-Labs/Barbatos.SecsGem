# Barbatos.SecsGem.MaterialFlow

The joins between the material-flow standards for **.NET 8, 9 and 10** - the ones none of those
standards makes itself:

- **E87 to E90**: a carrier's verified slot map becomes one tracked substrate per occupied slot,
  and they are forgotten again when the carrier is done with.
- **E40 to E90**: a process job's ending reaches the wafers it was running, so a stopped job stops
  the work in progress and skips what nobody got to.
- **E87 to E94**: a control job finds out that the carriers it named have arrived and been agreed,
  which is the one transition E94 leaves to the machine and cannot see for itself.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.MaterialFlow
```

Depends on `Barbatos.SecsGem.Carriers`, `Barbatos.SecsGem.Substrates`,
`Barbatos.SecsGem.ProcessJobs` and `Barbatos.SecsGem.ControlJobs` - four standards, none of which
depends on another, which is the whole reason this package exists. The three bridges are
independent: a machine may have any of them, all of them or none.

## Why this is a package and not a method

E87 reads a slot map: one byte a slot, the codes the machine's own, `SLOTMAP` with no dictionary
entry anywhere in the catalogue. E90 tracks substrates: one object a wafer, at a location, with a
state. **Neither standard refers to the other**, and the two packages here do not either - a
machine can implement carrier management without substrate tracking, or the other way round, and
making either depend on the other would take that away.

So every application that implements both writes the same loop: walk the slot map, name a location
for each occupied slot, name the wafer in it, add it to the tracker. This repository's own sample
wrote it twice before this package existed. The E87 and E90 READMEs say correlating the two is the
application's business, and that is still true - this is one application's answer, written down,
tested, and replaceable in the places where fabs differ.

## What it does

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddCarrierEquipment(manager => manager.AddPort(1))
    .AddSubstrateTracking(tracker => tracker.AddLocation("CHUCK"))
    .AddCarrierSubstrateBridge()
    .RunAsHostedService();
```

That is the whole wiring. From then on, a carrier whose slot map the host has agreed turns into
substrates by itself:

```csharp
carriers.PlaceCarrier(1, "CAR-001");
carriers.ReadCarrierId("CAR-001", verified: true);
carriers.ReadSlotMap("CAR-001", slotMap, verified: true);   // 25 bytes, two of them zero

substrates.Substrates.Count;        // 23
substrates.FindSubstrate("CAR-001-03");
// at CAR-001-SLOT-03, slot 3, lot LOT-7, destination CAR-001-SLOT-03, AtSource, NeedsProcessing
```

By hand, for a machine that would rather decide when:

```csharp
var result = bridge.Open("CAR-001");

result.Count;        // 23
result.Refused;      // slot -> why, for the ones the tracker would not take

// ... the lot runs, and every wafer gets back to its slot ...

bridge.Close("CAR-001");    // forgets them, and reports the ones E90 will not let go of yet
```

## The rule it keeps

**Substrates are only created from a slot map the host has agreed** -
`CarrierSlotMapStatus.SlotMapVerificationOk`. A map that is unread, waiting or refused is not a
bill of materials, and tracking wafers from one would have the machine believing in wafers nobody
confirmed. That is E87's whole reason for giving a slot map a state machine, carried over to this
side of the join.

Everything else it refuses is of the same kind: a carrier the manager does not have, and a carrier
this bridge has already opened - opening twice would either collide with its own substrates or
quietly track a wafer twice.

A slot the tracker refuses is **reported and the rest go on**. Nothing is rolled back to tidy it
up: a wafer already tracked under that name is somebody else's wafer, and removing it to make room
would be worse than saying so. A carrier where one slot was refused is still twenty-four wafers a
machine needs to know about.

## The convention, and how to replace it

Every decision this package makes is a property of `CarrierSubstrateOptions`, because every one of
them is a convention rather than a rule:

| Option | Default | What it decides |
| --- | --- | --- |
| `SlotLocationName` | `CAR-001-SLOT-03` | What a slot is called as an E90 location |
| `SubstrateName` | `CAR-001-03` | What the wafer in it is called |
| `IsOccupied` | anything but zero | Which `SLOTMAP` byte means a wafer is there |
| `ReturnsToItsOwnSlot` | `true` | Whether the wafer's destination is the slot it came out of |
| `CopiesLotId` | `true` | Whether the carrier's lot identifier goes onto each wafer |
| `CopiesUsage` | `true` | Whether its usage does |
| `SubstrateType` | empty | What kind of substrate these are, in the fab's own words |
| `OpensWhenSlotMapVerified` | `true` | Whether a verified map creates the substrates by itself |
| `ClosesWhenCarrierFinished` | `false` | Whether a finished carrier forgets them |

```csharp
.AddCarrierSubstrateBridge(options =>
{
    options.SlotLocationName = (carrier, slot) => $"{carrier}/{slot}";
    options.SubstrateName = (carrier, slot) => $"W{slot:00}@{carrier}";
    options.IsOccupied = code => code == 2;        // this mapper says 2 for a wafer
    options.SubstrateType = "300mm Si";
})
```

Three of those defaults are worth a sentence each.

**`IsOccupied` is non-zero**, which is what the E87 package itself counts when it works out
`SubstrateCount`, and the only reading this stack takes anywhere. `SLOTMAP` has no dictionary
entry, so the codes belong to the equipment - a fab whose mapper reports zero for a present wafer
changes one line here rather than its mapper.

**`ReturnsToItsOwnSlot` is on**, because a carrier in is usually the carrier out. It sets the
wafer's destination, which is what lets E90 refuse a move after it gets back - and it does *not*
make the wafer `AtDestination` on the spot, because a destination is where a wafer is going and
E90 derives the transport state from a move. A machine that sorts wafers into other carriers turns
this off and sets each destination itself.

**`SubstrateType` is empty, not `"Wafer"`.** E90 does not define the vocabulary, and a package that
guessed would put its guess in a host's reports.

**`ClosesWhenCarrierFinished` is off**, which is the cautious way round: a finished carrier still
has its records, and plenty of applications want them. Turned on, the substrates still have to be
somewhere E90 allows forgetting from - at their destination, or lost - so a close while the lot is
being worked on forgets nothing and says which slots it kept. It can be called again later.

## Slot locations are declared as they are needed

An application does not have to know a carrier's capacity up front: `Open` declares the location
for each occupied slot before adding the wafer to it, and declaring a location twice is harmless.
A twenty-five slot carrier with two empties leaves twenty-three locations, not twenty-five -
because a slot with no wafer needs no location, and two empty ones would show up in every report.

## The second join: a job's ending reaches its wafers

E40 ends a job as completed, stopped, aborted or cancelled. E90 ends work on a wafer as processed,
aborted, stopped or rejected, or lets it be skipped. **Two of those words are the same word** - and
nothing joins them, so a job somebody stopped leaves the wafer on the chuck `InProcess` and the
rest of the lot `NeedsProcessing`, for ever: E90's own rules mean nothing can move them afterwards,
because work ends only from `InProcess` and skipping is only from `NeedsProcessing`, and nobody is
coming back to those on behalf of a job that is already over.

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddSubstrateTracking(tracker => tracker.AddLocation("CHUCK"))
    .AddProcessJobEquipment()
    .AddProcessJobSubstrateBridge()
    .RunAsHostedService();
```

From then on:

```csharp
// W01 is on the chuck, being processed; W02 and W03 are still in the carrier.
jobs.Command("PJ-1", ProcessJobCommands.Stop);
jobs.StopComplete("PJ-1");

substrates.FindSubstrate("W01")!.ProcessingState;   // Stopped  - the job's own word
substrates.FindSubstrate("W02")!.ProcessingState;   // Skipped  - nobody got to it
```

Stopped for stopped and aborted for aborted is the one mapping in this package that needed no
choosing. Skipping the untouched ones is E90's word for exactly that case, and both halves are
options: `EndsWorkInProgress` and `SkipsUntouched`.

**What it will not do is tidy up.** A job the machine called complete with a wafer still in
process, or a job cancelled before it ran with a wafer already processed, is a machine telling two
different stories - and those come back in `Unexpected`, untouched:

```csharp
var result = bridge.Settle("PJ-1");

result.Ended;        // the wafers whose work this bridge ended
result.Skipped;      // the ones it skipped
result.Unexpected;   // substrate -> what does not fit, and nothing done about it
```

A bridge that quietly picked one story would destroy the evidence of the other, and the evidence is
the only thing anybody can act on.

It also checks, when a job starts setting up, that the tracker has the material the job names - E40
carries material as identifiers and knows nothing about substrates, so a job can name a wafer
nothing is tracking and the machine would process material it cannot account for. The check cannot
*refuse* the transition, because it hears about it afterwards; it raises `MaterialMissing`, and a
host that wants to refuse calls `Check` before it tells the machine to set the job up.

## The third join: a lot learning that its boxes turned up

E94 gives a control job two transitions the machine makes rather than the host - *its material is
ready*, and *its material is all dealt with*. The E94 package has both of them,
`MaterialReady` and `MaterialFinished`, and **nothing that calls either**, because E94 does not say
what the material is. E87 does: a carrier at a port, its identifier agreed, its slot map agreed.

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddCarrierEquipment(manager => manager.AddPort(1))
    .AddControlJobs()
    .AddControlJobCarrierBridge()
    .RunAsHostedService();
```

From then on a selected job starts when the last of its carriers is agreed, and not before:

```csharp
jobs.Create("CJ-1", ["CAR-001", "CAR-002"]);
jobs.Select("CJ-1");

carriers.PlaceCarrier(1, "CAR-001");
carriers.ReadCarrierId("CAR-001", verified: true);
carriers.ReadSlotMap("CAR-001", slotMap, verified: true);

jobs.Find("CJ-1")!.State;      // Selected - one box short

carriers.PlaceCarrier(2, "CAR-002");
carriers.ReadCarrierId("CAR-002", verified: true);
carriers.ReadSlotMap("CAR-002", slotMap, verified: true);

jobs.Find("CJ-1")!.State;      // Executing
```

And when a job will not start, the answer says which box:

```csharp
var answer = bridge.Check("CJ-1");

answer.IsAccepted;   // false
answer.Carriers;     // CAR-001
answer.Refused;      // CAR-002 -> "carrier 'CAR-002' is not verified: identifier WaitingForHost, ..."
```

This bullet used to read *"They do not bridge E94 ... correlating them is a decision with more than
one sensible answer, and until one of them is clearly the usual one this package is not the place
to guess."* That is still true of the parts a fab has to decide, and those are now
`ControlJobCarrierOptions` rather than a refusal. What turned out to need no guessing is the shape:
E94 asks whether the material is ready, E87 is the only thing in this stack that can answer, and
the answer is a quorum over a list of identifiers E94 already keeps.

| Option | Default | What it decides |
| --- | --- | --- |
| `IsCarrier` | everything is | Which of a job's identifiers name E87 carriers at all |
| `RequiresCarrierAtPort` | `true` | Whether a verified carrier has to be docked to count as arrived |
| `ReadiesWhenCarriersVerified` | `true` | Whether the last carrier to be agreed starts the lot |
| `FinishesWhenCarriersComplete` | `true` | Whether the last carrier to be finished ends the lot |
| `CountsStoppedCarriers` | `false` | Whether a stopped carrier counts as dealt with |
| `EndsCarriersEarly` | anything but `Completed` | Which endings leave a carrier `CarrierStopped` |

**`IsCarrier` is a predicate and not a flag, and that is the one thing this bridge got wrong
first.** It began life as `RequiresEveryCarrier`, a flag meaning "tolerate identifiers you do not
know", for the machine whose jobs name a reticle as well as a box. But the carrier manager cannot
tell an identifier it is not responsible for from **a carrier that has not arrived yet** - both are
simply absent from it. So the flag also tolerated a lot whose second carrier was still on a
conveyor somewhere, and started it. A negative control found that by deleting the flag's effect and
watching six tests fail instead of one. A predicate puts the distinction where the only thing that
knows it lives, which is how the fab spells its own material:

```csharp
.AddControlJobCarrierBridge(options =>
    options.IsCarrier = id => id.StartsWith("CAR-", StringComparison.Ordinal))
```

Everything the default does not know about is therefore a carrier that has not turned up, and the
lot waits. That is the cautious way round in every one of these: `RequiresCarrierAtPort` is on
because a carrier a host merely said was coming is not material anybody can process, and
`CountsStoppedCarriers` is off because `MaterialFinished` records `Completed` and a stopped carrier
says the opposite.

### A stopped carrier does not quietly finish a lot

Every carrier accounted for and one of them stopped is **not** a completed lot, so the job stays
where it is and waits for the host to stop or abort it - which is the ending that tells the truth.
The bridge does not keep that to itself, because a job that quietly never ends is worse than one
that complains:

```csharp
bridge.Stalled += (_, args) => log.LogWarning(
    "control job {Job} is waiting on {Reason}", args.Result.ControlJobId, args.Result.Reason);
```

### The other direction is called, not listened for

`ClaimCarriers` puts a running job's carriers into access and `ReleaseCarriers` ends that access
the way the job ended - complete for a completed job, stopped for every other ending:

```csharp
bridge.ClaimCarriers("CJ-1");            // both carriers InAccess
// ... the lot runs, and the host aborts it ...
jobs.Command("CJ-1", ControlJobCommands.Abort);
bridge.ReleaseCarriers("CJ-1");          // both carriers CarrierStopped
```

Those are methods and not an event subscription, and the next section is why.

## What none of the bridges does

- **They do not touch a wire.** All three are state machines meeting; every test in this package's
  suite runs without a link, and the package declares no message. A join that invented a
  transaction would be inventing a standard.
- **They do not schedule anything.** Which wafer goes to a chuck, and when, is the machine's.
- **They do not join E94 to E90.** A control job is a lot and a wafer is a wafer, and between them
  sit E87's box and E40's job, both of which are already joined here. Going straight from one to
  the other would be a fourth convention duplicating two that exist.
- **They keep no history.** `Close` forgets, and E90's own `SubstrateStateChanged` event is where an
  application builds a history in whatever it already stores.

## Locks, and the one order that matters

Each bridge holds a lock of its own only over its own bookkeeping; the work is calls into two
managers that each hold theirs. Attached, a bridge runs **inside the lock of the manager it
listens to** - the carrier-to-substrate one is called from `CarrierStateChanged`, the process job
one from `JobStateChanged`, the control-job one from `CarrierStateChanged` again - and from there
it calls the other manager. So there are exactly two orders in this package, **substrates last**
and **control jobs last**, and never the other way round.

That is the rule to keep if an application adds handlers of its own. Two locks taken in two orders
is the one deadlock a stack like this can build for itself, and this package is the only place
where its locks meet.

### Why one bridge is half event and half method

The E87-to-E94 bridge subscribes to the carrier manager and **not** to the control job manager,
which is not an omission. Both managers raise their events *inside their own lock*, and both say so
in their own documentation: queue the work, do not call back in. So a handler of
`CarrierStateChanged` holds E87's lock and takes E94's, and a handler of `JobStateChanged` would
hold E94's and take E87's. On a machine those are two real threads - the link's receive loop
carrying a host command into the job manager, and the machine's own control software reading a
carrier at a port - and a bridge that listened to both would be the deadlock above, built by the
very package that exists to prevent it.

So the direction that listens is the one whose trigger nobody outside can see: a job's material
becomes ready when the **last** of its carriers is agreed, and only something watching every
transition knows which one that was. The other direction's triggers are calls the application just
made itself - `Start`, `Command(ABORT)` - so `ClaimCarriers` and `ReleaseCarriers` are methods, and
they hold no lock at all when they begin: they read the job, E94's lock is released, and only then
do they call E87.

An application that wants a host-driven ending to reach the carriers by itself puts the job
identifier on a queue from its own `JobStateChanged` handler and calls `ReleaseCarriers` from
outside it - which is what both managers already ask of every handler they have.

**The rule costs exactly one thing, and here it is.** A job selected when its carriers are
*already* at their ports and agreed has no carrier transition left to happen, so nothing wakes the
bridge and the job sits in `Selected`. Noticing that would mean listening for the selection, which
is the direction that cannot listen. So call `Ready` once after selecting:

```csharp
jobs.Select("CJ-1");
bridge.Ready("CJ-1");     // quiet when the material is not there, so it needs no condition
```

That is the whole of it. Both orders then work: material first, or lot first.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
