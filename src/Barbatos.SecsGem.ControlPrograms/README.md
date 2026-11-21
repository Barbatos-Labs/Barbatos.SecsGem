# Barbatos.SecsGem.ControlPrograms

Control program transfer - SECS-II stream 8 - for **.NET 8, 9 and 10**: the load inquire and grant,
the boot program send, a named control program requested back, the directory, the transfer abort
and the software revision.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.ControlPrograms
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 8 is E5 and no
part of E30 - GEM asks for streams 1, 2, 5, 6, 7, 9 and 10 - so a controller that takes firmware
over a SECS link without implementing GEM can use this on its own. A machine that does both adds it
beside `AddGemEquipment`, which answers stream 7: the design stream 8 borrows.

## Stream 8 is stream 7 for firmware, and that is where the undefined items come from

This is the whole of the chapter that produced the package, so it comes first.

Stream 8 has sixteen rows, four of which the reference documented and twelve of which are a
representative subset - and eight item names, **five defined and three not**. The three are
`CPID`, `CPBODY` and `ACKC8`, which is to say the identifier, the program and the acknowledgement:
on the face of it, everything that matters.

What rescues it is that stream 8 is **stream 7's transfer design applied to a different kind of
program**:

| stream 8 | stream 7 |
| --- | --- |
| S8F1 `LENGTH` → S8F2 `GRANT` | S7F1 `PPID, LENGTH` → S7F2 `PPGNT` |
| S8F3 `CPBODY` → S8F4 `ACKC8` | S7F3 `PPID, PPBODY` → S7F4 `ACKC7` |
| S8F5 `CPID` → S8F6 `CPID, CPBODY` | S7F5 `PPID` → S7F6 `PPID, PPBODY` |
| S8F11 → S8F12 `CPID` | S7F7 → S7F8 `PPID` |
| S8F13 `CPID` → S8F14 `ACKC8` | S7F17 `PPID` → S7F18 `ACKC7` |
| S8F15 `CPID` → S8F16 `CPID, SOFTREV` | — |
| S8F7 to S8F10, blocked | — |

So `CPID` sits where `PPID` sits and `CPBODY` sits where `PPBODY` sits. **`PPID` is an `A[120]` and
`PPBODY` is a `B`**, and those are the formats this package writes. That is an inference, it is
named as one, and two tests pin it on the wire rather than through this package's own reader -
because a round trip through a tolerant reader cannot tell a `B` from an `A`, which is how the
first version of this suite let a negative control find nothing.

### Why that is a different answer from stream 13's

`Barbatos.SecsGem.DataSets` has the same problem and solves it differently: `DSDATA` is also an
undefined payload, and there a data set travels as the **encoded item** that carried it. The
difference is the template. Stream 13 is nobody's copy, so there was no slot to read a format off
and the honest move was to choose none. Here there is one.

The consequence is in the types, and it is the thing most easily got wrong by anybody moving
between the two packages:

```csharp
// stream 13: bytes that are an encoded SecsItem, header and all
DataSetPayload payload = await dataSetHost.GetAsync(41, token);
using var item = payload?.Decode();

// stream 8: bytes that are the program
ControlProgram? program = await controlProgramHost.RequestAsync("BOOT-A", token);
await File.WriteAllBytesAsync("boot.bin", program!.Body.ToArray(), token);
```

Same C# type, two different things, one package apart.

### And it is not `CPNAME`

Those two letters mean **control program** here and **command parameter** in S2F41 and S18F1. The
catalogue defines `CPNAME` and `CPVAL` while leaving `CPID` and `CPBODY` undefined. Unrelated items
that happen to share a prefix.

## The firm half of the stream is the half with no identifier

Worth noticing, because it is not a coincidence: **S8F1 to S8F4 are exactly the four rows the
reference documented, and they are exactly the rows that carry no `CPID`.**

- **S8F1 to S8F4** are the *boot* program. The inquire carries only a `LENGTH` and the send carries
  only a `CPBODY` - so the program being sent is **anonymous**, because a machine has one of it.
- **S8F5 to S8F16** name a `CPID`, and every one of them is a representative subset.

The practical shape of that: **you cannot name what you send, and you must name what you ask for.**
`IControlProgramStore.SaveAsync` therefore takes bytes and no identifier, and
`ControlProgramInMemoryStore` holds the result under the empty identifier - which is that class's
choice, not E5's, and is why the boot program is **not** in the directory. S8F12 lists what a host
can ask for by name, and a nameless program is not one of those.

## Stream 8 runs one way

All eight primaries are the host's and all eight replies the equipment's. Nothing in this stream
ever arrives at a host unasked - no report, and no message saying a machine's firmware changed. So
`ControlProgramHost.HandledMessages` is empty and `AddControlProgramHost` adds no route;
`Barbatos.SecsGem.DataSets` is the other package here that can say that, those two being the
one-way streams. `ControlProgramEquipment` accordingly takes **no connection**.

## The host

```csharp
services
    .AddSecsGem(configure: link =>
    {
        link.DeviceId = 0;
        link.Mode = HsmsConnectionMode.Active;
    })
    .UseTcpTransport("192.168.0.30", 5000)
    .AddControlProgramHost();
```

Sending is two transactions, and this package keeps them two:

```csharp
var host = provider.GetRequiredService<ControlProgramHost>();
var firmware = await File.ReadAllBytesAsync("boot-1.4.2.bin", token);

var grant = await host.InquireAsync((uint)firmware.Length, token);

if (grant == ControlProgramGrant.Accepted)
{
    var acknowledgement = await host.SendAsync(firmware, token);
}
```

`InquireAsync` and `SendAsync` are separate because a host that wants to see the grant before
committing megabytes is the entire reason the inquire exists, and a method that did both would
throw the grant away. `SendProgramAsync` is there for a caller who has the whole program in hand
and nothing to decide in between - it returns the grant and, when there was one to get, the
acknowledgement:

```csharp
var (grant, acknowledgement) = await host.SendProgramAsync(firmware, token);

// acknowledgement is null when the inquire was refused: there was no S8F3, so there is no ACKC8.
```

Fetching is one:

```csharp
foreach (var id in await host.ListAsync(token))
{
    Console.WriteLine($"{id} is at {await host.RevisionAsync(id, token)}");
}

var program = await host.RequestAsync("BOOT-A", token);   // null when the machine has none
await host.AbortAsync("BOOT-A", token);
```

**Ask the revision first** when all you need is whether a program is there. S8F16 carries a
`SOFTREV` that is either present or not, where S8F6 carries no acknowledgement code at all - so a
request for a program the machine does not have looks exactly like a successful one with an empty
body.

## The equipment

```csharp
services
    .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
    .UseTcpTransport(listenPort: 5000)
    .AddControlProgramEquipment();          // or AddControlProgramEquipment<MyStore>()
```

`IControlProgramStore` is six methods, one per primary: `InquireAsync`, `SaveAsync`, `LoadAsync`,
`ListAsync`, `AbortAsync`, `RevisionAsync`. Each is awaited inside the transaction the host is
waiting on, so none should be slow - writing a megabyte to flash while a host holds its T3 open is
how a transfer times out. There is no event beside them: an application that cares about a program
needs the program, and a store method already has it, can refuse it, and is awaited before the
acknowledgement goes back.

A body read off the wire is **copied** before it reaches the store, because the item it arrived in
borrows pooled memory and is released with its message. That is the same trap stream 13 met; here
it costs a `byte[]` rather than a type, because the format is known.

## `LENGTH` can be checked here, and in stream 13 it could not

S8F1 declares how long the program is so the equipment can refuse for space before the bytes
arrive - and, unlike stream 13, this package can also compare it with what then **arrives**,
because `CPBODY` has a format and therefore a byte count. `ControlProgramInMemoryStore` does, and
refuses a mismatch.

That is the clearest single consequence of the two streams' different provenance.
`Barbatos.SecsGem.DataSets` documents the opposite: `LENGTH` goes to the store and is never
compared, because `DSDATA` has no format to measure and `ACKC13` has no length-error code anyway.

## What can be refused, and what cannot

- **S8F2** carries a `GRANT`, whose four codes are quoted - and from a row that is a definition.
  `GRANT` is named by seven messages across five streams and **S8F2 is the only one of the seven the
  reference documented**, so the table is anchored on this stream's own row. An unreadable inquire
  is refused `DuplicateOrInvalid`, the second half of whose meaning - "or not one this end
  recognises" - is the nearest the table comes to "that made no sense". Not `Busy`, which would
  invite a retry of a message that will break again, and not `NoSpace`, which would be a lie about
  the machine.
- **S8F4 and S8F14** carry an `ACKC8`, whose single refusal carries **no reason**, because the item
  has no table to put one in.
- **S8F6, S8F12 and S8F16** carry no code at all. A program, a directory and a revision are data
  and nothing else, so an unreadable request is answered with an empty body: the reply cannot name
  a program whose identifier this end failed to read.

`Barbatos.SecsGem.Material` serves the same `GRANT` item for S3F16, and these five names and
numbers match `MaterialGrant` member for member. One item cannot mean two things in one repository,
so a test asserts it rather than leaving it to the eye.

## `ACKC8` has no table at all, so zero is read off its siblings

This is the one reading in the package that is quoted from nowhere. `ACKC8` has no dictionary entry
and therefore no code values - and three of the nine `ACKCn` items messages use are like that, the
three belonging to streams 2, 8 and 21.

What makes it a small inference rather than a guess is a family measurement: the catalogue defines
**eight** items named `ACKCn` - `ACKC3`, `ACKC5`, `ACKC6`, `ACKC7`, `ACKC7A`, `ACKC10`, `ACKC13`
and `ACKC15` - and **every one of them has 0 = accepted**. Eight for eight.

So zero is accepted, and that is as far as the evidence goes. Nothing names anything above it, so
every non-zero code is a refusal with no reason and the arriving code goes to the log, which is the
only place left for it. `ControlProgramAcknowledgement` has **two members and deliberately no
`Other`**: with one named code, a third member would be inventing a distinction between kinds of
silence.

## What is not here yet

- **S8F7 to S8F10, the blocked send and the blocked request**, and for a measured reason rather
  than a shrug. Their `BLKNUM` and `BLKEND` have no dictionary entries - and neither does any other
  block-transfer item in the catalogue: `BLKNUM`, `BLKSIZE`, `BLKACK`, `BLKEND`, `CHECKSUM`,
  `CHKSUM` and `CKSUM` are undefined, **seven for seven**. That is a vocabulary the source never
  wrote down, and it is the same measurement that keeps S13F9 and S13F10 out of
  `Barbatos.SecsGem.DataSets`. A program too large for one message is, for now, one message.
- Any message that deletes a control program. Stream 7 has S7F17 for a process program and stream 8
  has no equivalent, so an abort that threw one away would be this library inventing a message.
- A store that outlives the process, and no snapshot pair on purpose: a control program is not state
  a machine claims about itself, so it survives a restart through an implementation - the same
  answer `IGemSpool` and `IDataSetStore` give. Six methods, and a file behind them is an afternoon.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
