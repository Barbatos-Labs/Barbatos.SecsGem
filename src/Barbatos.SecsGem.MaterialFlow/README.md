# Barbatos.SecsGem.MaterialFlow

The joins between the material-flow standards for **.NET 8, 9 and 10** - the ones none of those
standards makes itself:

- **E87 to E90**: a carrier's verified slot map becomes one tracked substrate per occupied slot,
  and they are forgotten again when the carrier is done with.
- **E40 to E90**: a process job's ending reaches the wafers it was running, so a stopped job stops
  the work in progress and skips what nobody got to.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.MaterialFlow
```

Depends on `Barbatos.SecsGem.Carriers`, `Barbatos.SecsGem.Substrates` and
`Barbatos.SecsGem.ProcessJobs`. It is the only package in this repository that depends on several
standards at once, and that is the whole reason it exists. The two bridges are independent: a
machine may have either, both or neither.

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

## What neither bridge does

- **They do not touch a wire.** Both are state machines meeting; every test in this package's suite
  runs without a link, and the package declares no message. A join that invented a transaction
  would be inventing a standard.
- **They do not schedule anything.** Which wafer goes to a chuck, and when, is the machine's.
- **They do not bridge E94.** A control job's carriers are a list of identifiers in that package,
  deliberately. A control job is a lot and a carrier is a box; correlating them is a decision with
  more than one sensible answer, and until one of them is clearly the usual one this package is not
  the place to guess.
- **They keep no history.** `Close` forgets, and E90's own `SubstrateStateChanged` event is where an
  application builds a history in whatever it already stores.

## Locks, and the one order that matters

Each bridge holds a lock of its own only over its own bookkeeping; the work is calls into two
managers that each hold theirs. Attached, a bridge runs **inside the lock of the manager it
listens to** - the carrier one is called from `CarrierStateChanged`, the process job one from
`JobStateChanged` - and from there it calls the substrate tracker. So the order is always the other
standard first and **substrates last**, and never the other way.

That is the rule to keep if an application adds handlers of its own, and the reason both bridges go
the same direction: the substrate tracker is the one thing both of them touch, so as long as
nothing calls back out of it, two locks can never be taken in two orders. That is the one deadlock
a stack like this can build for itself, and this package is the only place where its locks meet.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
