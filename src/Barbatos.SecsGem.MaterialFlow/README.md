# Barbatos.SecsGem.MaterialFlow

The join between **SEMI E87** and **SEMI E90** for **.NET 8, 9 and 10**: a carrier's verified slot
map becomes one tracked substrate per occupied slot, and the substrates are forgotten again when
the carrier is done with.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.MaterialFlow
```

Depends on `Barbatos.SecsGem.Carriers` and `Barbatos.SecsGem.Substrates`. It is the only package in
this repository that depends on two standards at once, and that is the whole reason it exists.

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

## What it does not do

- **It does not touch a wire.** The bridge is two state machines meeting; every test in its suite
  runs without a link, and the package declares no message. A join that invented a transaction
  would be inventing a standard.
- **It does not schedule anything.** Which wafer goes to a chuck, and when, is the machine's.
- **It does not bridge E40 or E94.** A process job's material and a control job's carriers are
  lists of identifiers in those packages, deliberately, and correlating them is the same kind of
  application decision this package makes explicit for E87 and E90. If the same convention turns
  out to be wanted there, it belongs here.
- **It keeps no history.** `Close` forgets, and E90's own `SubstrateStateChanged` event is where an
  application builds a history in whatever it already stores.

## Locks, and the one order that matters

The bridge holds a lock of its own only over its own bookkeeping; the work is two calls into two
managers that each hold theirs. Attached, it runs **inside the carrier manager's lock** - its
handler is called from `CarrierStateChanged` - and from there it calls the substrate tracker. So
the order is always carriers, then substrates, and never the other way.

An application that adds its own handlers should keep that order. Two locks taken in two orders is
the one deadlock a stack like this can build for itself, and this package is the only place where
two of them meet.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
