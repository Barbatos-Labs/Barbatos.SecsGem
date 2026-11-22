# Barbatos.SecsGem.Material

Material status and material handoff - SECS-II **streams 3 and 4** - for **.NET 8, 9 and 10**:
what the machine holds and where, how long it has left, material found and lost, location and
transfer requests, status changes and movement events, and the interlocked handshake that moves a
piece of material from one end to the other. Both ends, and in these streams both ends send.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Material
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 3 is E5, not
E30: no state model, no control state, nothing GEM owns. It sits beside `AddGemEquipment` on the
same connection, or on its own.

## Read this before you ship it

The reference table in `Barbatos.SecsGem.Catalog` carries a flag for entries whose source marks
them as a representative subset rather than a definition. For stream 3 it is set on **all
thirty-six rows**, and five of the item names stream 3 uses have no dictionary entry at all:

```
CARRIERSTATUS  S3F20          DESTLOC  S3F11 S3F35
PORTSTATUS     S3F22 S3F32    SLOTMAP  S3F28
MATLSTATUS     S3F13
```

Compare stream 12, where seventeen of nineteen rows are definitions. So the bodies this package
writes are a careful reading of the item lists and the formats of the items that *are* defined -
not a quotation of E5. Two fields had to be inferred outright:

| Item | Written as | Why |
| --- | --- | --- |
| `DESTLOC` | `U4[1]` | It is a location, `LOC` is `U4`, and the two sit side by side in S3F35. |
| `MATLSTATUS` | `B[1]` | Every other single-byte field in this stream is `B[1]`. Nothing names its codes, so nothing here does either. |

**Both are read out of any numeric format**, so a machine that disagrees about the format is still
understood; the inference only decides what this library *writes*. If a machine disagrees,
`Internal/MaterialCodec.cs` is the only file to change, and it says so in its own remarks.

What is deliberately absent: **S3F17 to S3F34**, the carrier and storage half of stream 3 -
`CARRIERACTION`, `CARRIERID`, `PTN`, `SLOTMAP`, `PORTSTATUS`. Those belong to SEMI E87, which
defines the carrier object they act on. Implementing them here would mean inventing a wire format
for an object model this library does not have, and then breaking it when the object model arrives.

## Both ends send, which is new here

| | |
| --- | --- |
| S3F1 / F2 | host asks what material is loaded, equipment answers |
| S3F3 / F4 | host asks how long it has left, equipment answers |
| S3F9 / F10 | host asks where one piece is, equipment answers |
| S3F11 / F12 | host asks for material to be moved, equipment accepts or denies |
| S3F15 / F16 | host asks whether a large body may be sent, equipment grants or refuses |
| S3F5 / F6 | **equipment** says material turned up, host acknowledges |
| S3F7 / F8 | **equipment** says material went missing, host acknowledges |
| S3F13 / F14 | **equipment** says material changed status, host acknowledges |
| S3F35 / F36 | **equipment** says material moved, host acknowledges |

Nine primaries, and each end owns some. Stream 12 was one-way and GEM is host-asks-equipment-
answers; this is the first stream in the library where neither end is passive.

## The host side

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddMaterialHost()
    .RunAsHostedService();
```

```csharp
var material = provider.GetRequiredService<MaterialHost>();

// What is in the machine, and where.
foreach (var piece in await material.RequestStatusAsync())
{
    Console.WriteLine($"{piece.MaterialId} at {piece.Location}, quantity {piece.Quantity}");
}

// Where is this one? Null means the machine does not have it - S3F10 has no acknowledgement
// code in it, so E5 leaves only the zero-length list to say so.
var found = await material.FindMaterialAsync("LOT-2026-1115");

// Move it. Accepted means the request was taken, not that the material has arrived.
var taken = await material.RequestTransferAsync(
    new MaterialTransfer { MaterialId = "LOT-2026-1115", Destination = 9 });
```

And the four things the machine says without being asked. They are events with a settable
acknowledgement, and a host that subscribes to none of them still answers correctly:

```csharp
material.MaterialMoved += (_, args) =>
{
    // This is the arrival the accepted S3F11 above promised.
    Log($"{args.Movement.MaterialId}: {args.Movement.Location} -> {args.Movement.Destination}");
};

material.MaterialLost += (_, args) =>
{
    // The location is where it was last seen. A machine that knew where it is would not have
    // lost it.
    Alert($"{args.Material.MaterialId} last seen at {args.Material.Location}");
    args.Acknowledgement = MaterialAcknowledgement.Accepted;   // already the default
};
```

## The equipment side

Five questions behind one interface, and the answers are data the machine has to produce - which
is why this is an interface and the host's four are events:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddMaterialEquipment<MyInventory>()
    .RunAsHostedService();
```

```csharp
public sealed class MyInventory : IMaterialSource
{
    public ValueTask<IReadOnlyList<MaterialStatus>> GetStatusAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<MaterialStatus>>(_loaded);

    public ValueTask<MaterialLocation?> FindAsync(string materialId, CancellationToken cancellationToken)
        => ValueTask.FromResult(_loaded.FirstOrDefault(m => m.MaterialId == materialId) is { } found
            ? new MaterialLocation { MaterialId = found.MaterialId, Location = found.Location }
            : null);

    // ... three more: time to completion, the transfer decision, and the multi-block grant
}
```

Each one is asked inside a transaction the host is waiting on, so none of them should take long. A
machine whose inventory lives behind a database query wants that query cached, not awaited while
T3 runs.

`AddMaterialEquipment()` with no type argument uses `MaterialInMemorySource`. It is enough for a
simulator, and a transfer there **moves** the material rather than only acknowledging it - so a
host that asks S3F9 afterwards gets the new location, and the two messages agree without anything
outside that class arranging it.

Then the machine reports:

```csharp
var equipment = provider.GetRequiredService<MaterialEquipment>();

await equipment.ReportFoundAsync(new MaterialLocation { MaterialId = "LOT-A", Location = 4 });
await equipment.ReportStatusChangeAsync(new MaterialStatusChange { MaterialId = "LOT-A", Location = 4, Status = 3 });
await equipment.ReportMovementAsync(new MaterialMovement { MaterialId = "LOT-A", Location = 4, Destination = 9 });
```

Each returns what the host answered. A reply that carries no acknowledgement code is a
`SecsException` and never `Accepted`: a reader that takes the first byte of an empty body gets
nought, and nought is `ACKC3` accepted.

## The interlocked handoff, stream 4

Stream 3 says what material is where. **Stream 4 moves it**, one offer and one acceptance at a
time:

```csharp
var handoff = provider.GetRequiredService<MaterialHandoff>();

if (await handoff.OfferAsync(portId: 1, materialId: "LOT-7") == MaterialSendAcknowledgement.WillAccept)
{
    await handoff.ReportAsync(MaterialHandoffStep.SendMaterial, 1, "LOT-7");        // no reply exists
    await handoff.ReportAsync(MaterialHandoffStep.HandshakeComplete, 1, "LOT-7");
}
```

**One type, not two, and the catalogue is why.** Every other message pair in this stack has a
direction, which is how this repository ends up with a `…Host` and an `…Equipment` that answer each
other's primaries. All eleven rows of S4F1 to S4F18 are marked as travelling **either** way,
because the two parties to a handoff are a *sender* and a *receiver* of material - which may be a
machine and a handling system, or two machines, and need not involve a host at all. So
`MaterialHandoff` is the first type here that is neither end of anything: both ends of a link call
`AddMaterialHandoff()`, both declare the same nine primaries, and a test runs the whole handshake
in each direction.

**Seven of the nine primaries have no reply at all.** E5 gives S4F3, F5, F7, F9, F11, F13 and F15
no secondary, so `ReportAsync` posts them and there is no T3 to wait on: what tells a sender it was
heard is the next step coming back, which is what *interlocked* means. Only the offer and the
request to receive are answered, and those two have methods of their own because their answers have
different types.

| | |
| --- | --- |
| S4F1 / F2 | an offer of material, answered with `RSACK` |
| S4F17 / F18 | a request to be sent material, answered with `RRACK` |
| S4F3, F5, F15 | sending, finished, received - the happy path, unanswered |
| S4F7 | the offer is withdrawn |
| S4F9, F11, F13 | stuck at the sending end, stuck at the receiving end, never finished |

### For once, nothing is inferred

Stream 4 is the **best-covered stream this package's siblings had not implemented**: 27 rows, and
every item name in all of them is defined in the dictionary - which is true of no other stream
here, the implemented ones included. `PTN` is a `U1[1]`, `MID` an `A[n]`, and both acknowledgements
arrive **with their own tables of values**, so these enums are quoted rather than read off a
format:

```
RSACK  0 will accept | 1 invalid port | 2 port occupied | 3 busy | 4 not in remote mode | 5-63 equipment
RRACK  0 granted     | 1 denied                                                        | 2-63 equipment
```

Those four refusals are worth sending. A bare denial is what the rest of this stream has, and it
sends somebody to a log file; "port occupied" does not. The equipment-specific ranges read as
`Other` with the arriving code logged, the same rule `MaterialGrant` keeps - and never as the zero
of the enum, which here would turn somebody else's refusal into *will accept*.

What the source does **not** mark as a definition is any of the 27 rows, exactly as for stream 3.
So the bodies are read off the item lists the way the rest of this package's are.

### The sequence is documented and not enforced

This package will send the steps in any order it is asked to. That is the same choice it makes
about stream 3 and the opposite of the one
[`Barbatos.SecsGem.Carriers`](../Barbatos.SecsGem.Carriers/README.md) makes about E87, and the
difference is what there is to check against: E87 defines state machines, and stream 4 is 27
message rows with **no state model anywhere in this reference**. A package that invented one would
be enforcing its own rules while appearing to enforce E5's.

## A denial cannot say why

`ACKC3` has two codes - accepted and denied - and there is no error code anywhere in the material
half of stream 3. That is E5, not an omission here. A machine with something to explain raises an
alarm in stream 5 or an error report in stream 9; this package has nothing to offer in between, and
pretending otherwise would mean inventing a field.

## Time to completion is a number, not a duration

`TTC` is a `U4` and the catalogue gives it no unit. Implementations in the field generally mean
seconds, but `MaterialCompletion.TimeToCompletion` is a `uint` rather than a `TimeSpan`, because a
property typed as a duration would be this library asserting the unit - and a machine that counts
in minutes would be read wrong by a factor of sixty with nothing on screen to show it.

## The handoff protocol, stream 4's other half

S4F1 to S4F18 interlock a *piece of material* across a port. S4F27 to S4F41 agree a **handoff**:
two ends name a link between them, run commands on it, verify it, and either side may cancel or
halt. Eight primaries, none of them answered, every one travelling either way - the same design one
stream further on, so the same type serves it.

```csharp
await handoff.AnnounceLinkAsync(new MaterialHandoffLink
{
    Link = "LINK-1",                          // what every later message addresses
    EquipmentName = "EQ1",
    ObjectName = "CAR-001", ObjectType = "Carrier",
    Port = "LP1", Role = MaterialHandoffRole.Source,
    PartnerName = "AGV3", PartnerPort = "ARM",
    Location = "BAY-4",
});

await handoff.SendCommandAsync("LINK-1", 1, "LOAD", [("SLOT", "7")]);   // S4F29
await handoff.ReportVerifiedAsync("LINK-1", MaterialHandoffAcknowledgement.Accepted);
```

| Message | What it says | Carries |
| --- | --- | --- |
| S4F27 | here is a link | the nine descriptor items |
| S4F29 | run this on it | `TRLINK`, `MCINDEX`, `HOCMDNAME`, `CPNAME`/`CPVAL` |
| S4F31 | that command finished | `TRLINK`, `MCINDEX`, `HOACK` |
| S4F33 | the handoff is verified, or is not | `TRLINK`, `HOACK`, `ERRCODE`, `ERRTEXT` |
| S4F35 / S4F37 | cancel, and the answer | `TRLINK`, and `HOCANCELACK` |
| S4F39 / S4F41 | halt, and the answer | `TRLINK`, and `HOHALTACK` |

**S4F27 is where a link comes from**, and that is the finding that let this half in at all. This
README used to say a `TRLINK` was minted by S4F19, the transfer job create, and that the handoff
rows therefore could not be served before the job rows. The item dictionary says otherwise: S4F27
carries the same descriptor the job does, it travels either way where the job is the host's alone,
and nothing in the reference reserves link naming to a job. Two ends can agree a handoff between
themselves - and a stream whose every row travels either way is the shape that says so.

**`TRROLE` is a text item that arrives with its table**: SOURCE and DESTINATION, spelled out. So
the wire carries the word, this package compares it ordinally, and a word the table does not name
reads as `MaterialHandoffRole.Other` rather than as a guess at which end is holding the material.
That one item also carries the argument for the refusal below, because it proves the source does
write values down for a text item when it knows them.

**`HOACK`, `HOCANCELACK` and `HOHALTACK` have no tables at all** - three `B[1]` items with nothing
in them. So one enum serves all three and zero is the favourable answer, read off this stream's own
`RSACK` and `RRACK` rather than invented; the same reading
[`Barbatos.SecsGem.ControlPrograms`](../Barbatos.SecsGem.ControlPrograms/README.md) gives `ACKC8`.
Every non-zero code is a refusal the reference gives no reason for, and the arriving code is
logged. S4F33 is the one row that *can* say why, in `ERRCODE` and `ERRTEXT` beside the code -
neither tabled either, so both pass through untouched.

**The order is documented and not enforced**, as it is for the handshake above and for stream 3:
nothing here tracks which commands are outstanding or which links are open. A link is an agreement
between two ends and this type is one end of it, not a registry.

## What is not here yet

- **S3F17 to S3F34**, the carrier and storage half of that stream. See above: that is E87's.
- **S4F19 to S4F22**, stream 4's transfer *jobs* - and the reason here is `TRACK`. Those two rows
  are the only answers to a job create and a job command, and `TRACK` is the whole of the outcome
  in both: an `A` with **no table of values**, beside an `ERRCODE` that is a `U4` with none either.
  So a host cannot tell whether its job was created. That the silence is meaningful rather than a
  habit is what `TRROLE` settles - an `A` in the same stream that does arrive with its table.
  S4F23, the alert, is out with them: it reports a job's state and there are no jobs.
  <br/>This entry used to give a different reason - that "a transfer job has no state machine
  anywhere in this reference", against E94's and E40's. That is true of the message rows and false
  of the dictionary: `TRJOBMS` names six states, Queued through Cancelled, and S4F23 carries it.
  Six state names are not a transition model, which is a reason not to *enforce* a sequence - a
  choice this package had already made for stream 4 - and not a reason to leave the messages
  unserved. The measurements are in
  [`SecsCatalogStream4Tests`](../../tests/Barbatos.SecsGem.Catalog.UnitTests/SecsCatalogStream4Tests.cs).
- **S4F25 and S4F26**, the multi-block inquire, and its format is not the reason: `DATAID` and
  `DATALENGTH` are defined and `GRANT` is a tabled item three packages here already read. It is the
  handshake in front of a message too large for one block, and the only row in this stream large
  enough to need one is the job create that stays out - and it is the host's alone, where this
  package's stream 4 type is one type for both ends by design. Reopenable the moment the job half
  is, and not before.
- A source that outlives the process. `MaterialInMemorySource` is in memory, as it says; the
  interface is five methods and a database behind it is an afternoon.
- `QUA` has no code table in the catalogue, so a quantity is a byte and nothing here interprets it.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
