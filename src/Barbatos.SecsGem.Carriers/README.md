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
| S3F17/F18, F23/F24, F25/F26, F29/F30, F33/F34 | every item defined; these five are implemented |
| S3F19/F20, F21/F22, F27/F28, F31/F32 | need `CARRIERSTATUS`, `PORTSTATUS` or `SLOTMAP`, which have **no definition at all** |

Those last four are not missing. See [Attributes](#attributes-the-slot-map-among-them).

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

One call registers both halves of E87: the five carrier primaries **and** the eleven of object
services, because the attributes are stream 14's business. A machine that registered only the
first five would answer every action and never give up a slot map.

Then the machine drives its own states as its hardware does things:

```csharp
var manager = provider.GetRequiredService<CarrierManager>();

manager.SetTransferState(1, PortTransferState.ReadyToLoad);     // the port is clear
manager.PlaceCarrier(1, scannedId);                             // a carrier arrived
manager.ReadCarrierId(scannedId, verified: boundToThisPort);    // the label was read
manager.ReadSlotMap(scannedId, slots, verified: true);          // the mapper ran
manager.BeginAccess(scannedId);                                 // refused unless both agreed
```

## Attributes, the slot map among them

E87's carrier and load port attributes travel on **stream 14**, and that is where the four
unimplementable transactions went. `CarrierObjectProvider` exposes both object types:

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
`ReservedForCarrierID`. A host discovers them with S14F17 rather than assuming them.

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

## What is not here yet

- **The E87 messages that need an undefined item**: S3F19/F20, S3F21/F22, S3F27/F28, S3F31/F32.
  Their content is on stream 14 instead, which is where E87 puts it.
- A manager that outlives the process. The states are in memory; a machine that reboots with a
  carrier on a port re-reads the label, which is also what E87 expects.
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
