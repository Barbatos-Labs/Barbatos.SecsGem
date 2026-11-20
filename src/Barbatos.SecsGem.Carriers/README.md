# Barbatos.SecsGem.Carriers

Carrier management - **SEMI E87** - for **.NET 8, 9 and 10**: the carrier and load port objects,
the seven state machines E87 defines for them, and the actions that drive those states. The states
are *enforced*, not described.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Carriers
```

Depends on `Barbatos.SecsGem.Objects` and `Barbatos.SecsGem.Hsms`. **Not on GEM** - see
[Events](#events-and-why-this-does-not-depend-on-gem).

## Read this first: where these rules come from

Every other package in this stack is checked against `Barbatos.SecsGem.Catalog`, which is
third-party reference data for the SECS-II message set. **E87 is not in it, and could not be**:
E87 defines behaviour - which state may follow which - and the catalogue covers messages and items.

So the state machines here are **E87 as this library understands it**. There is nothing in this
repository to check them against, and that is a sharper caveat than the ones on the message
packages. They live in two files, [`CarrierStates.cs`](CarrierStates.cs) and
[`CarrierManager.cs`](CarrierManager.cs), each transition commented with the rule it is keeping, so
an integrator who finds a fab that disagrees has one place to go.

What *is* checkable, and was checked:

| | |
| --- | --- |
| `CAACK` | four codes, defined in the catalogue - accepted, invalid command, cannot perform now, invalid data |
| `ACKC3` | two codes, defined |
| `CARRIERACTION` | an `A` item: free text, which is why the action names here are strings and not an enum |
| `CARRIERID`, `PTN`, `ERRCODE`, `ERRTEXT`, `LOC` | all defined |
| S3F17/F18, F23/F24, F25/F26, F29/F30, F33/F34 | every item defined; all five implemented |
| S3F19/F20, F21/F22, F27/F28, F31/F32 | need `CARRIERSTATUS`, `PORTSTATUS` or `SLOTMAP`, which have **no definition at all** - implemented anyway, and [the next section](#the-transactions-whose-item-nobody-defined) gives the grounds, which differ for each |

**All nine of E87's half of stream 3 are here.** S3F17 to S3F34, both ends.

Stream 3 is one of the nine streams the catalogue has **no definitions for at all** -
`SecsCatalog.TryGetStream(3, out var s)` then `s.Provenance` says so. That is not a veto, and the
[`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) README explains why: it means
check the stream against the item dictionary and against its standard's own subject matter. This
one passes both, which is why E87 ships and stream 17 does not.

## The transactions whose item nobody defined

Four of the nine carry an item with no entry in the catalogue's dictionary: `CARRIERSTATUS` on
S3F20, `PORTSTATUS` on S3F22 and S3F32, `SLOTMAP` on S3F28. All four are implemented and **none of
the four is a guess** - but the grounds are not the same, they get weaker down the list, and each
is written down where the code is:

| | |
| --- | --- |
| S3F28 `SLOTMAP` | nothing to choose: a `B` item of one byte a slot is what `Carrier.SlotMap` holds and what stream 14 already serves |
| S3F22 `PORTSTATUS` | the transfer state, which E87 calls the one a material handler looks at - and the value stream 14 already serves, so there was a reading to check against |
| S3F32 `PORTSTATUS` | no stream 14 attribute to check against; the ground is the item name it shares with S3F22, three functions earlier |
| S3F20 `CARRIERSTATUS` | the item's name settles nothing, so the choice is `CarrierEquipment.StatusOf` - argued below, defaulted, and replaceable |

**A number is not a state name.** All of these travel as the state's own name in an `A` item, and
reading one back has to be the exact inverse. `Enum.TryParse` is not: it also accepts the number,
so the text `"2"` used to come back as whichever state happens to be 2. That is a second
inference - about numbering - stacked on reading an item nobody defined, and the one numeric
vocabulary here that the catalogue *does* define disagrees with this library's: `ACCESSMODE` has 0
for automatic where `PortAccessMode.Manual` is 0. A host sending the dictionary's number for manual
would have been put into automatic. Names only, in E87's own spelling, everywhere a state is read.

### S3F22, S3F28 and S3F32, which had something to check against

The two readings that are barely inferences at all: **this same package already serves both values
as stream 14 attributes**, so there was an existing answer to agree with rather than a format to
invent.

```csharp
var status = await host.ReadPortStatusAsync(1);

status.Status;          // "ReadyToUnload" - what PORTSTATUS carried, as text
status.TransferState;   // PortTransferState.ReadyToUnload - this library's reading of it
status.CarrierId;       // CAR-001

var map = await host.ReadSlotMapAsync("CAR-001");   // 25 bytes, or null
```

**`PORTSTATUS` is the transfer state**, out of a port's four state machines, because that is the one
E87 calls what a material handler actually looks at - and because it is the one the `CARRIERID`
beside it is about: a port that can be unloaded has something on it. It goes out as an `A` item
holding the state's name, byte for byte the `TransferState` attribute stream 14 already serves, and
a test asserts the two agree so the machine cannot answer differently depending on which question
it was asked.

**`SLOTMAP` needed no choosing at all**: a `B` item of one byte a slot is what `Carrier.SlotMap`
already holds and what stream 14 already serves. The codes inside stay the equipment's, as they are
everywhere else E87 touches a slot map.

**An unfamiliar status word comes back as text with no reading.** `PORTSTATUS` being undefined means
another vendor's equipment is entitled to a vocabulary of its own, so `TransferState` is `null`
rather than `OutOfService` when the word is not one of E87's four. `OutOfService` is the zero of the
enum, and a port nobody may use is a thing a host acts on.

**Not read and read as empty are different answers.** S3F28 gives the zero-length list for a carrier
the machine does not have *and* for one whose slot map nobody has agreed - E87 gives the slot map a
state machine precisely so those are distinguishable - and an agreed map that is genuinely empty
comes back as zero bytes. So `null` means "will not say" and an empty value means "nothing in it".

**S3F32 is one hop further from firm ground, and says so.** S3F22's reading is checked against a
stream 14 attribute; a storage location has no stream 14 attribute to check against, because this
package does not claim an E87 object type for one. What it has instead is the item name:
`PORTSTATUS` means in S3F32 what it means in S3F22, three functions earlier in the same standard's
half of the same stream. A test asserts that a port and a shelf in the same state report the same
word, so a host never needs two vocabularies for one machine. See
[storage locations](#storage-locations).

### S3F20, and the one word that names two state machines

`CARRIERSTATUS` is one item, and a carrier has three state machines - and all three of them are
called *carrier something status*: `CarrierIdStatus`, `CarrierSlotMapStatus`,
`CarrierAccessStatus`. Unlike `PORTSTATUS`, where E87 does say which of a port's four states is
the operative one, the item's own name settles nothing. That is why this one is a hook:

```csharp
equipment.StatusOf = carrier => carrier.IsVerified ? "READY" : "NOT_READY";   // a fab's own word
```

**The default is the access status, on two grounds that can be checked rather than argued.**

The first is the vocabulary. A state can travel as a single word only if the word says which
machine it came from, and across those three enums eleven of the twelve names do - the twelfth is
`WaitingForHost`, which belongs to the identifier *and* to the slot map. A host told
`WaitingForHost` cannot tell whether the label or the map is waiting, so **the two verification
states cannot honestly be one item at all**, while every access state names itself. There is a
test for exactly that, because a rename would break the argument.

The second is subject matter: stream 3 is *Material Status*, and of a carrier's three states the
access status is the one about the material rather than about what this machine has managed to
read. E87 treats the other two as a joint precondition, which is why `Carrier.IsVerified` exists.

```csharp
var status = await host.ReadCarrierStatusAsync("CAR-001");

status.Status;          // "InAccess" - what CARRIERSTATUS carried, as text
status.AccessStatus;    // CarrierAccessStatus.InAccess, or null for a word this library did not write
status.PortId;          // 1, or null for a carrier on no port
```

So **S3F20 says how far through the boxes a machine has got, and does not say whether a carrier is
verified.** A host that needs that asks stream 14 for `IDStatus` and `SlotMapStatus` by name, where
they cannot be confused with one another - which is why E87 put them there. A test asserts S3F20
and the stream 14 `AccessStatus` attribute agree, as the other two undefined items have to.

**`PortId` is `null` rather than 0 for a carrier on no port**, the rule S3F17 already keeps for an
action naming no port: port 0 is a port. A carrier the host has only announced has not arrived
anywhere, and one in storage is somewhere `PTN` cannot describe - that one is a `LOC`, which S3F23
answers with.

## The seven state machines

Three belong to a carrier:

```
CarrierIdStatus       IdNotRead -> IdVerificationOk | WaitingForHost | IdVerificationFailed
                      WaitingForHost -> Ok (ProceedWithCarrier) | Failed (CancelCarrier)

CarrierSlotMapStatus  the same shape, for what is inside the carrier rather than its name

CarrierAccessStatus   NotAccessed -> InAccess -> CarrierComplete | CarrierStopped
```

Four belong to a load port:

```
PortTransferState     OutOfService <-> TransferBlocked <-> ReadyToLoad | ReadyToUnload
PortAccessMode        Manual <-> Auto                     (ChangeAccess)
PortAssociationState  NotAssociated <-> Associated        (a carrier placed or removed)
PortReservationState  NotReserved <-> Reserved            (ReserveAtPort, BindCarrier)
```

`CarrierManager` owns them, and nothing else can move them: every state on `Carrier` and
`LoadPort` has a public getter and an internal setter. A property bag with public setters would
let an application put a carrier into `CarrierComplete` without ever accessing it, and the state
machine would be a diagram in a document.

## What "enforced" means

```csharp
var manager = new CarrierManager();

manager.AddPort(1);                                  // hardware is declared, not messaged into being
manager.SetServiceStatus(1, inService: true);
manager.SetTransferState(1, PortTransferState.ReadyToLoad);
manager.PlaceCarrier(1, "CAR-001");                  // only from ReadyToLoad
manager.ReadCarrierId("CAR-001", verified: null);    // the machine cannot say; the host must

var refusal = manager.BeginAccess("CAR-001");

// CannotPerformNow: "carrier 'CAR-001' is not verified: identifier WaitingForHost,
//                    slot map SlotMapNotRead."
Console.WriteLine($"{refusal.Acknowledgement}: {refusal.ErrorText}");
```

Every refusal names the state that refused it, because `CannotPerformNow` on its own sends
somebody to a log file for an afternoon. The rules this package keeps:

- A carrier cannot be **accessed** until its identifier *and* its slot map are agreed.
- A carrier can only be **placed** on a port that is `ReadyToLoad`, and only **removed** from one
  that is `ReadyToUnload`. A carrier appearing on a blocked port is a material handling fault, and
  accepting it would hide the fault.
- A **reserved** port takes only the carrier it is reserved for.
- A port holding a carrier cannot be taken **out of service** - the carrier would be stranded.
- **Access mode** cannot change mid-transfer: a handler that was told the port is automatic has
  started moving.
- A **finished** carrier cannot be accessed again without `CarrierReCreate`. Its substrates have
  been touched; what gets re-created is a new carrier with an old name.
- A **notification** cannot be cancelled once the carrier has arrived.

Each of those is a test that names the rule and shows the refusal. Most of them never open a link:
the state machine has no connection, which is what makes it testable as a state machine.

## The actions are strings

`CARRIERACTION` is an `A` item, so `CarrierActions` is a class of constants rather than an enum:

```csharp
await host.PerformAsync(new CarrierActionRequest
{
    Action = CarrierActions.ProceedWithCarrier,
    CarrierId = "CAR-001",
});

// Or something this library has never heard of, which a fab may well have agreed on. The answer
// is InvalidCommand - the code E5 has for exactly that - rather than an impossibility.
await host.PerformAsync(new CarrierActionRequest { Action = "ReprobeCarrier", CarrierId = "CAR-001" });
```

An enum would have made this library's vocabulary the ceiling for a standard that keeps growing.

The host side has a method for the ones sent often - `ProceedWithCarrierAsync`,
`CancelCarrierAsync`, `BindCarrierAsync`, `ReserveAtPortAsync`, `NotifyCarrierAsync` - and
`PerformAsync` for everything else.

## The equipment side

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddCarrierEquipment(manager =>
    {
        manager.AddPort(1);
        manager.AddPort(2);
    })
    .RunAsHostedService();
```

One call registers both halves of E87: the nine carrier primaries **and** the eleven of object
services, because the attributes are stream 14's business. A machine that registered only the
actions would answer every S3F17 and never give up a slot map.

Then the machine drives its own states as its hardware does things:

```csharp
var manager = provider.GetRequiredService<CarrierManager>();

manager.SetTransferState(1, PortTransferState.ReadyToLoad);     // the port is clear
manager.PlaceCarrier(1, scannedId);                             // a carrier arrived
manager.ReadCarrierId(scannedId, verified: boundToThisPort);    // the label was read
manager.ReadSlotMap(scannedId, slots, verified: true);          // the mapper ran
manager.BeginAccess(scannedId);                                 // refused unless both agreed
```

## Storage locations

A load port is where a carrier crosses the boundary of the machine. A **storage location** is
somewhere inside it - a stocker shelf, a buffer, an internal station - and E87 addresses the two
differently: S3F21 asks about one port by `PTN`, and S3F31 asks about every storage location at once
by asking about nothing.

```csharp
manager.AddStorageLocation(101);                                     // declared, as a port is
manager.SetStorageServiceStatus(101, inService: true);
manager.SetStorageTransferState(101, PortTransferState.ReadyToLoad);
manager.StoreCarrier(101, "CAR-001");                                // only from ReadyToLoad

manager.SetStorageTransferState(101, PortTransferState.ReadyToUnload);
manager.FetchCarrier(101);                                           // only from ReadyToUnload
```

**One state machine, and not an eighth.** E87 defines seven and a storage location gets none of its
own: its state is a `PortTransferState`, the same type a port's is, because S3F32 carries
`PORTSTATUS` - the same item name S3F22 carries. An item means one thing in a message set, so
giving a shelf a vocabulary of its own would be this library inventing a second meaning for an item
that already has one here. There is no access mode (nobody is being told who may drive a shelf), no
reservation (`ReserveAtPort` names a port), and no association state - what is in it is just
`CarrierId`, because a second state machine saying the same thing could disagree with it.

The rules are a port's rules for a port's reasons: a carrier goes in only from `ReadyToLoad` and
comes out only from `ReadyToUnload`, because a carrier appearing somewhere that was not ready for
one is a material handling fault; a location holding a carrier cannot be taken out of service,
because the carrier would be stranded; and `OutOfService` is a service decision rather than a
readiness one.

**A carrier is in one place.** `StoreCarrier` refuses a carrier that is on a port, and
`PlaceCarrier` refuses one that is in storage:

```
CannotPerformNow: carrier 'CAR-001' is on port 1; a carrier is in one place.
```

Two answers to *where is CAR-001* is one more than S3F24 can give. A machine moves a carrier by
taking it off the port first - `RemoveCarrier` - and then storing it, which is the order the
hardware does it in anyway.

**No host action reaches a storage location.** Every carrier action E87 defines names a `CARRIERID`
or a `PTN`, never a `LOC`, so a host can read storage and cannot drive it. The methods above are the
machine's own hardware talking, which is also why their refusals name a method where a port's name
the `ChangeServiceStatus` action.

### What S3F31 asks, and what zero turned out to mean

```csharp
foreach (var location in await host.ReadStorageStatusAsync())
{
    location.Location;        // 101 - LOC
    location.Status;          // "ReadyToUnload" - what PORTSTATUS carried, as text
    location.TransferState;   // ReadyToUnload, or null for a word not among E87's four
    location.CarrierId;       // CAR-001, or empty
}
```

S3F31 is the only request in E87's half of stream 3 that **names nothing**, and that removes a
distinction every other reader here has to keep: an empty answer means the machine has no storage,
not that it will not say, because there was nothing named that it could be missing.

It also moved `LOC` under this package's feet. `CarrierEquipment.LocationOf` is what answers S3F24,
and its default used to be `carrier.PortId ?? 0`, which was honest while a port was the only place a
carrier could be - a carrier the host has merely announced is nowhere, and S3F24 has no
acknowledgement code to say *I do not know* with. Once a carrier can be on a shelf, that same
expression reports a carrier that is **somewhere** as being **nowhere**, and the shrug becomes a
wrong answer: zero is a location. So the default now reads the storage location first and the port
second, and a test asserts that S3F24 and S3F32 name the same number for the same carrier.

What this package cannot do on a machine's behalf is keep two number spaces apart. `LOC` is a `U4`
and `PTN` a `U1`, nothing in E87 stops a shelf being numbered 1 beside a load port 1, and S3F24 has
room for one number. A machine like that replaces `LocationOf`, which is what the hook is for - or
asks on stream 14 instead, where [a storage location is an object](#the-storage-location-object-which-s3f32-defined)
and the two places have two names.

## Attributes, the slot map among them

E87's attributes travel on **stream 14**, where an attribute may be any item at all - which is why
the four items the catalogue defines no format for are readable without a format being invented for
them, and why each of their stream 3 readings had something to be checked against.
`CarrierObjectProvider` exposes three object types:

```csharp
// On the host, with Barbatos.SecsGem.Objects:
await objects.GetAttributesAsync("CAR-001", "Carrier", ["SlotMap", "AccessStatus", "LotID"], values =>
{
    foreach (var attribute in values.Attributes)
    {
        // SlotMap comes back as the bytes it is. ATTRDATA has no format of its own, so a slot map
        // needs no format invented for it - which is exactly why S3F28 was not worth guessing at.
    }
});
```

Carrier attributes: `CarrierID`, `IDStatus`, `SlotMapStatus`, `AccessStatus`, `SlotMap`,
`Capacity`, `SubstrateCount`, `LotID`, `Usage`, `PortID`. Port attributes: `PortID`,
`TransferState`, `AccessMode`, `AssociationState`, `ReservationState`, `AssociatedCarrierID`,
`ReservedForCarrierID`. Storage location attributes: `LocationID`, `TransferState`, `CarrierID`. A
host discovers them with S14F17 rather than assuming them.

### The storage location object, which S3F32 defined

```csharp
var shelves = await objects.GetInstancesAsync("StorageLocation");   // StorageLocation101, ...

await objects.GetAttributesAsync(
    CarrierObjectProvider.StorageSpecification(101),
    CarrierObjectProvider.StorageLocationType,
    ["LocationID", "TransferState", "CarrierID"],
    values => { /* 101, "ReadyToUnload", "CAR-009" */ });
```

**Those three attributes are S3F32's three items** - `LOC`, `PORTSTATUS`, `CARRIERID` - which is an
attribute list written by the one transaction E5 has on the subject. So the type is read off a wire
format rather than invented, and the direction is the opposite of S3F22's: there, stream 14 came
first and the transaction had to agree with it; here the transaction came first. Either way the
test is the same, two routes and one answer. The shape is also the one
[`Barbatos.SecsGem.Substrates`](../Barbatos.SecsGem.Substrates/README.md) already serves for E90's
substrate location - an identity, a state, and what is in it.

**The type is advertised only by a machine that has one**, unlike the other two, and that is the
asymmetry S3F31 already keeps: a load port is hardware E87 assumes every machine has, and a shelf
is hardware it does not.

**It is also the one object type here with nothing a host may write.** A carrier has `LotID` and
`Usage`, a port has `AccessMode`; a shelf has an identity it was configured with, a state that
means material has moved, and the name of whatever is sitting in it. None of those is a host's to
assign.

One thing the objspec convention fixes that `LOC` cannot: a shelf numbered 1 beside a load port 1
is `StorageLocation1` and `LoadPort1`, two names that cannot be confused, where S3F24 has room for
one number and no way to say which kind of place it means.

**A state attribute is not a back door.** Writing `AccessStatus` with S14F3 is refused with
`InvalidAttribute`: it would put a carrier into `CarrierComplete` without accessing it. The
exceptions are the two E87 means a host to set - `LotID` and `Usage`, which are labels - and
`AccessMode`, which is a state and so is routed through the `ChangeAccess` action, where it still
cannot happen during a transfer. A host gets the same refusal whichever message it asks with.

## Events, and why this does not depend on GEM

E87 defines collection events for its transitions, and they come out as .NET events:

```csharp
manager.CarrierStateChanged += (_, args) =>
{
    if (args.StateMachine is CarrierStateMachine.IdStatus
        && (CarrierIdStatus)args.Current is CarrierIdStatus.WaitingForHost)
    {
        // This machine cannot verify that label. Ask the host.
        _ = equipment.TriggerEventAsync(CarrierWaitingForHostEvent);
    }
};
```

What this package does not do is publish them. A collection event needs a `CEID`, a `CEID` belongs
to the equipment that defines it, and nothing here can know which number a machine gave to
"carrier identifier read". So the wiring is five lines in an application that has a GEM layer, and
a prober that has no GEM layer can still use all of this.

Events are raised **after** the state has moved and **inside** the manager's lock, so a handler
reading the carrier sees the state the event announces - and must not call back into the manager.
Queue the work.

## Outliving the process

The states are in memory, and `Capture` writes them down:

```csharp
var snapshot = carriers.Capture();          // a plain object; serialize it with whatever you use

// ... the machine is upgraded, crashes, or is simply switched off ...

var report = carriers.Restore(snapshot);
```

**The rule: what a host told this machine comes back, and what the machine observed does not.** A
reboot does not change what anybody asked for, and it says nothing at all about where the boxes are
now.

| Comes back | Does not |
| --- | --- |
| whether a port is in service | whether a carrier is on it, and its transfer state beyond being in service |
| who drives a port - `AccessMode` | which carrier it is associated with |
| a reservation, and who it is for | which port or storage location a carrier was at |
| whether a storage location is in service | what was in it, and its transfer state beyond being in service |
| a carrier's identity, lot, usage, capacity | `IdStatus`, `SlotMapStatus`, the slot map itself, the substrate count |

The identifier and the slot map go back to unread because **E87 wants a carrier re-read after a
restart**, and a machine that claimed a verification it never performed would process a lot nobody
confirmed. The lot identifier survives, so the sequence runs again knowing what it is looking at:

```csharp
carriers.FindCarrier("CAR-001")!.LotId;      // LOT-7
carriers.FindCarrier("CAR-001")!.IsVerified; // false

carriers.SetTransferState(1, PortTransferState.ReadyToLoad);
carriers.PlaceCarrier(1, "CAR-001");
carriers.ReadCarrierId("CAR-001", verified: true);   // and on as normal
```

### A carrier that was in access comes back stopped

Not `NotAccessed`, which E87 rules out itself once the substrates have been touched - that is
exactly why its access state machine has no way back. And not `InAccess`, which would have a
machine that has just started claiming it is working on something. `CarrierStopped` is E87's own
"finished early: a fault, or a host that said stop", and losing power part way through a carrier is
a fault. The other three states are history rather than a claim about now, and come back as
themselves.

### Two more rules worth knowing

**A snapshot does not invent hardware.** Ports are declared from configuration with `AddPort` and
storage locations with `AddStorageLocation`, so one in the snapshot this manager does not have is
reported in `Adjusted` and skipped. A machine that came back with a load port its configuration no
longer mentions would be a worse surprise than one that said so.

**Nothing is raised while restoring.** A host that heard "carrier stopped" as a transition would
believe the machine had just stopped one. `CarrierRestoreReport.Adjusted` is how an application
tells it something true instead - every entry names a carrier or a port and says why it is not what
the file said.

Restoring is refused outright for a manager that already has carriers: merging two beliefs about
the same box has no right answer.

## What is not here yet

- **A `Location` attribute on a carrier, to go with its `PortID`.** It would be a third route to a
  question that already has two, and the only one of the three that could disagree: `LOC` for a
  carrier is decided by `CarrierEquipment.LocationOf`, a hook an application may replace, and the
  object provider is built from the manager and cannot see it. Serving it here would mean either
  coupling stream 14 to a stream 3 decision or duplicating the fallback - and a duplicate is wrong
  the moment somebody replaces the hook. The two routes that exist are S3F23 for one carrier, and
  the `StorageLocation` object's `CarrierID` for the other direction.
- **A stocker as a standard of its own - E88.** The storage locations here are what S3F31 needs: a
  `LOC`, a transfer state and what is in it. What E88 adds on top - zones, capacities, a storage
  service's own commands - is not in the catalogue in any form, and the two items it would be
  built on, `LOCID` and `SLOTID`, are defined and named by no message at all.
  [`Barbatos.SecsGem.Catalog`](../Barbatos.SecsGem.Catalog/README.md) has the measurement and the
  rest of the argument, the short version of which is that a second storage model in a second
  package would be two answers about one shelf.
- **A reservation or an access mode for a storage location.** `ReserveAtPort` names a port, and
  S3F32 has room for one status. Both would be this library deciding what E87 left unsaid about a
  place it barely describes.
- Writing the snapshot anywhere. `Capture` hands back a plain object and this package depends on no
  serializer; where it goes is the application's, the same way its container is.
- `ContentMap`, as distinct from `SlotMap`. One byte per slot covers what a machine knows; naming
  which substrate is in which slot is E90's job - and
  [`Barbatos.SecsGem.MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md) is where the two
  are joined up, because a verified slot map is exactly the moment a wafer becomes something to
  track.

The other three material-flow standards now have packages of their own -
[`Barbatos.SecsGem.Substrates`](../Barbatos.SecsGem.Substrates/README.md) for E90,
[`Barbatos.SecsGem.ControlJobs`](../Barbatos.SecsGem.ControlJobs/README.md) for E94 and
[`Barbatos.SecsGem.ProcessJobs`](../Barbatos.SecsGem.ProcessJobs/README.md) for E40 - and a machine
can register all four, which was the point of building them in this order.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
