# Barbatos.SecsGem.Substrates

Substrate tracking - **SEMI E90** - for **.NET 8, 9 and 10**: the substrate and substrate location
objects, the state machines E90 gives them, and the rules about which transition may follow which.
Enforced, with a refusal that names the state that refused it.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Substrates
```

Depends on `Barbatos.SecsGem.Objects` and `Barbatos.SecsGem.Hsms`. Not on GEM, and **not on E87**:
a machine that tracks wafers without carriers can use this alone, and one that does both calls
both registrations.

## E90 has no messages

That is the fact the whole package is shaped by. E87 at least had S3F17; E90 has nothing. A
substrate is an *object*: its attributes reach a host through SECS-II stream 14 object services,
and the only other thing a host sees is whichever collection events the equipment chose to define
for the transitions.

So there is no codec in this package, no equipment end answering primaries and no host end sending
them. There is a state machine that keeps the truth, and `SubstrateObjectProvider`, which is E90's
entire wire presence.

It also means the provenance caveat is the strongest in this repository. The catalogue that checks
every message in this stack **contains no mention of a substrate at all** - not a message, not an
item. These states and transitions are E90 as this library understands it, checked by tests rather
than against reference data, and [`SubstrateTracker.cs`](SubstrateTracker.cs) is the one file to
change when a fab disagrees.

## The state machines

```
SubstrateTransportState   AtSource <-> AtWork -> AtDestination
                          (and nothing out of AtDestination: the machine has let go)

SubstrateProcessingState  NeedsProcessing -> InProcess -> Processed | Aborted | Stopped | Rejected
                          NeedsProcessing -> Skipped
                          anything        -> Lost          (and nothing out of Lost)

SubstrateLocationState    Unoccupied <-> Occupied          (follows the substrates; never set)
```

Five of the processing states are endings, and the distinctions between them are why E90 exists: a
host that only knows "done" cannot tell a wafer that was processed from one that was skipped, and
the two mean different things for the lot.

## What "enforced" means

```csharp
var tracker = new SubstrateTracker();

tracker.AddLocation("SLOT-1");                 // geography is declared, not messaged into being
tracker.AddLocation("CHUCK");
tracker.Add("W01", "SLOT-1", slotId: 3);       // a mapped carrier turns into one of these per slot

var tooEarly = tracker.BeginProcessing("W01");

// "substrate 'W01' is AtSource and work can only start on one that is AtWork."
Console.WriteLine(tooEarly.Reason);

tracker.MoveTo("W01", "CHUCK");                // now AtWork, derived from where it went
tracker.BeginProcessing("W01");                // and now allowed
```

The rules this package keeps:

- Work can only **start** on a substrate that is `AtWork`. A wafer still in its carrier is not
  being processed, whatever a recipe thinks.
- A substrate that is `InProcess` cannot be **moved**. A wafer on a chuck with the recipe running
  is not available to a robot.
- A substrate cannot move to an **occupied** location, and a location holds one substrate: anything
  with slots is several locations.
- A substrate that has reached its **destination** cannot be moved again - the machine has let go
  of it - and one that is **lost** cannot do anything at all.
- Work cannot **start twice** or **end** without starting. A second pass is a new substrate.
- **Skipping** is only for a substrate that still needs processing; one being worked on is
  *stopped*, which is a different thing to whoever reads the lot.
- A substrate can only be **forgotten** once it is at its destination or lost. Forgetting one still
  on a chuck is how a machine ends up with a wafer nobody is tracking.

The transport state is **derived** rather than set: its source makes it `AtSource`, its destination
`AtDestination`, anywhere else `AtWork`. Asking a caller to keep the geography and the state in
step is asking for them to disagree.

Occupancy is kept, not set. A move empties the location left and fills the one arrived at, in one
step under one lock, so there is no moment where a wafer is in two places or in none.

## Attributes over stream 14

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddSubstrateTracking(tracker =>
    {
        tracker.AddLocation("SLOT-1");
        tracker.AddLocation("CHUCK");
    })
    .RunAsHostedService();
```

That registers the tracker, the object provider, and the eleven primaries of object services -
which are the only messages E90 has, because they are stream 14's.

A host reads a substrate the way it reads anything else on stream 14:

```csharp
await objects.GetAttributesAsync("W01", "Substrate", ["SubstrateState", "SubstrateProcessingState", "LotID"], values =>
{
    foreach (var attribute in values.Attributes)
    {
        Console.WriteLine($"{attribute.Name} = {attribute.Value.GetString()}");
    }
});
```

Substrate attributes: `SubstrateID`, `SubstrateState` (the transport state, as E90 names it on the
wire), `SubstrateProcessingState`, `SubstrateLocID`, `SubstrateSourceLocID`,
`SubstrateDestinationLocID`, `SlotID`, `LotID`, `SubstrateUsage`, `SubstrateType`, `AcquiredID`.
Location attributes: `SubstrateLocID`, `SubstrateLocState`, `SubstrateID`.

`AcquiredID` is not `SubstrateID`, and E90 keeps them apart for a reason: one is what the system
calls this wafer and the other is what is scribed on it. A fab discovers they disagree exactly
once, and then wants both.

**States are readable and not writable.** Writing `SubstrateProcessingState` over S14F3 is refused
with `InvalidAttribute` - it would mark a wafer processed without processing it. The settable
attributes are the labels a host owns: `LotID`, `SubstrateUsage`, `SubstrateType`,
`SubstrateDestinationLocID` and `AcquiredID`. The destination goes through the tracker, so one that
names no declared location is refused rather than stored.

## With E87

A machine that does both calls both, and stream 14 answers for carriers, load ports, substrates and
substrate locations together:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddCarrierEquipment(manager => manager.AddPort(1))
    .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
    .RunAsHostedService();
```

This did not work before this package existed, and the reason is worth knowing: stream 14 has one
provider per connection, and the registration used to keep the first provider and drop the second
silently. It now composes them - see `CompositeObjectProvider` in the object services package - and
a test in this one registers both standards and asserts that each answers.

The two standards stay independent in code. A carrier's slot map is what the machine *read*; a
substrate is what the machine is *tracking*; correlating them is the application's business, and a
library that did it for you would be guessing which slot map a given wafer came from.

If your fab's guess is the usual one, it is written down:
[`Barbatos.SecsGem.MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md) turns a verified slot
map into one substrate per occupied slot and forgets them again when the carrier is done, with the
naming, the occupancy rule and the destination all replaceable. It depends on both packages, which
is why it is a third one - neither of these two may depend on the other.

## Events, and why this does not depend on GEM

```csharp
tracker.SubstrateStateChanged += (_, args) =>
{
    if (args.StateMachine is SubstrateStateMachine.ProcessingState
        && (SubstrateProcessingState)args.Current is SubstrateProcessingState.Processed)
    {
        _ = equipment.TriggerEventAsync(WaferProcessedEvent);
    }
};
```

A collection event needs a `CEID`, a `CEID` belongs to the equipment that defines it, and nothing
here can know which number a machine gave to "substrate processed". So the wiring is a few lines in
an application that has a GEM layer, and a machine without one can still track substrates.

Events are raised **after** the state has moved and **inside** the tracker's lock, so a handler
reading the substrate sees the state the event announces - and must not call back in. Queue the
work.

## What is not here yet

- **Substrate history.** E90 defines one; this package keeps the current state and raises an event
  per transition, which is what an application needs to build a history in whatever it already
  stores.
- **E40 process jobs**, which is what gives a substrate its recipe. Another object model over the
  same stream 14. E94 control jobs - a substrate's place in a lot - now have a package of their
  own: [`Barbatos.SecsGem.ControlJobs`](../Barbatos.SecsGem.ControlJobs/README.md).
- A tracker that outlives the process. The states are in memory; a machine that reboots re-reads
  its carriers, which is what E87 and E90 both expect.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
