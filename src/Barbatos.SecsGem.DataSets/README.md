# Barbatos.SecsGem.DataSets

Data set transfer - SECS-II stream 13 - for **.NET 8, 9 and 10**: open for send or for read, send,
get, close, the directory and the status request, with both code tables as enums rather than bytes.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.DataSets
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 13 is E5 and is
no part of E30: GEM asks for streams 1, 2, 5, 6, 7, 9 and 10 and never mentions a data set. A
machine that moves files to a host without implementing GEM can use this on its own, and one that
does both adds it beside `AddGemEquipment` on the same connection.

## Stream 13 runs one way

Six streams in this stack, four answers to the question of who begins:

| | who sends the primaries |
| --- | --- |
| Stream 3, material status | both ends |
| Stream 18, subsystem control | both ends - six at the equipment and two at the host |
| Stream 4, material handoff | either end - every row travels both ways |
| Stream 12, wafer mapping | the equipment |
| **Stream 13, data set transfer** | **the host, and only the host** |
| Stream 8, control program transfer | the host, and only the host |

All seven primaries are marked host to equipment in the catalogue and all seven replies the other
way. Nothing in stream 13 ever arrives at a host unasked: there is no report, no error message, and
**no primary that says a new data set exists**. So `DataSetHost.HandledMessages` is empty and
`AddDataSetHost` adds no route to the message table - the only registration in this repository that
does not. A host finds out what is there by asking.

The other consequence is quieter: `DataSetEquipment` takes **no connection**. It has no primary to
send, so there is no cycle between the end, the connection and the router for the DI registration
to break.

| | |
| --- | --- |
| S13F1 / F2 | host offers a data set and its length, equipment grants or refuses |
| S13F3 / F4 | host sends it, equipment acknowledges |
| S13F5 / F6 | host asks to read one, equipment grants and says how long it is |
| S13F7 / F8 | host fetches it, equipment hands it over |
| S13F11 / F12 | host asks what there is, equipment lists it |
| S13F13 / F14 | host closes, equipment acknowledges |
| S13F15 / F16 | host asks whether one is there and how big, equipment says |

## The host

```csharp
services
    .AddSecsGem(configure: link =>
    {
        link.DeviceId = 0;
        link.Mode = HsmsConnectionMode.Active;
    })
    .UseTcpTransport("192.168.0.30", 5000)
    .AddDataSetHost();
```

Ask before committing, which is what the status request is for:

```csharp
var host = provider.GetRequiredService<DataSetHost>();

var status = await host.StatusAsync(dataSetId: 41, cancellationToken);

if (!status.Exists)
{
    // The one transaction in stream 13 that can say "I have not got that" in so many words.
    return;
}

var opening = await host.OpenAsync(41, DataSetOpenMode.Request, cancellationToken: cancellationToken);

if (opening.IsGranted)
{
    var payload = await host.GetAsync(41, cancellationToken);   // null when there is none
    await host.CloseAsync(41, cancellationToken);
}
```

Sending is the same shape in reverse, and the length goes in the open rather than with the data:

```csharp
using var contents = SecsItem.L(
    SecsItem.A("recipe-7"),
    SecsItem.B(File.ReadAllBytes("recipe-7.bin")));

var opening = await host.OpenAsync(41, DataSetOpenMode.Send, length: 4096, cancellationToken);

if (opening.IsGranted)
{
    var acknowledgement = await host.SendAsync(41, contents, cancellationToken);
    await host.CloseAsync(41, cancellationToken);
}
```

`SendAsync` **takes** the item: it becomes part of the outgoing message and is released with it,
the contract `Barbatos.SecsGem.Objects` already states for an attribute value handed to this
library. To send the same data set twice, hand it a `DataSetPayload` instead - that overload
decodes a fresh item each time and leaves the payload alone.

## The equipment

```csharp
services
    .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
    .UseTcpTransport(listenPort: 5000)
    .AddDataSetEquipment();                 // or AddDataSetEquipment<MyStore>()
```

`IDataSetStore` is six methods and they are the whole of what an equipment does in this stream:
`OpenAsync`, `SaveAsync`, `LoadAsync`, `ListAsync`, `CloseAsync`, `StatusAsync`. Each is awaited
inside the transaction the host is waiting on, so none of them should be slow - writing a megabyte
to a database while a host holds its T3 open is how a transfer times out.

There is no event beside those methods. An application that cares about a data set needs the data
set, and a store method already has it, can refuse it, and is awaited before the acknowledgement
goes back - none of which an event raised beside the transaction could do.

## A data set has no format, so this library never gives it one

This is the thing to understand about the package, and it comes out of the provenance rather than
out of taste.

The catalogue marks four of stream 13's sixteen rows as definitions and twelve as a representative
subset, which is the firmest showing of any stream nothing in this repository had implemented. Of
the seven item names the stream uses, five have dictionary entries and they cover every field this
package writes except one:

```
DSID     U4   defined
DATAID   U4   defined - and already served as a U4 by three other packages here
LENGTH   U4   defined
GRANT6   B[1] defined, three codes and a reserved range
ACKC13   B[1] defined, four codes and no reserved range
DSDATA   --   defined nowhere
```

`DSDATA` is the payload, and nothing in the reference says what shape it is. The catalogue *does*
define payloads when it has them - `PPBODY` is a `B`, `ATTRDATA` is an `L` - so this is a gap in
the row rather than a habit of the source. But a payload needs no definition to be carried
honestly: carry the item that arrived, untouched. A list, a block of bytes, a string, a nested
structure - all of them cross unaltered, and this library has no opinion about which one a data set
is.

So a data set travels as a **`DataSetPayload`**: the item *as encoded*, format header and length
header and payload, exactly the bytes that crossed the wire. `Decode()` turns them back into a
`SecsItem` whenever anybody wants to look.

```csharp
var payload = await host.GetAsync(41, cancellationToken);

using var item = payload?.Decode();      // the caller's to dispose

if (item?.Format == SecsFormat.List)
{
    // whatever the machine at the other end decided a data set is
}
```

### Why a copy, and not the item that arrived

A `SecsItem` decoded off the wire borrows pooled memory and is released when its message is
disposed, and there is no clone on it. A store that kept the item would be holding a buffer the
next message is about to write into. The spool in `Barbatos.SecsGem.Gem` met this first and
answered it the same way - `GemSpooledMessage.Body` is the encoded item tree for exactly this
reason - and `Barbatos.SecsGem.Objects` makes the same copy to keep an attribute value past its
message.

Taking ownership costs one copy, which is the price of the item not being yours.
`DataSetPayload.FromEncoded` is the way in for a store that already owns its bytes and should not
pay it twice.

### It is not a process program body, and the same C# type for both is a trap

`Barbatos.SecsGem.Gem` hands a process program about as a `ReadOnlyMemory<byte>` because `PPBODY`
**is** defined, as a `B` - so there those bytes are the recipe. Here they are an item tree, header
and all. Code that assumed stream 7's meaning would find two spare bytes in front of its data and a
format byte it never wrote. Hence a named type rather than a bare `ReadOnlyMemory<byte>`.

### And it is why `LENGTH` is not checked

S13F1 declares how long the data set is so the equipment can refuse for space before the bytes are
on the wire, and that part works: the number is handed to the store, which is the only thing that
knows whether it has room.

What cannot be done is comparing it with what then arrives. `LENGTH` is a `U4` and nothing says
what it counts; `DSDATA` has no format to measure. Stream 7 *does* check the equivalent, and can,
because `PPBODY` is a `B` and `ACKC7` has a **length error** code to report it with. `ACKC13` has
no such code. A store with its own definition of length refuses a mismatch with
`DataSetAcknowledgement.InvalidData`; this library does not compute one.

### An empty payload is how "I have not got it" arrives

S13F8 carries a `DSID`, a `DATAID` and a `DSDATA` and **no acknowledgement code at all**, so an
empty `DSDATA` is the only way an equipment can say it does not have the data set asked for.
`GetAsync` returns `null` for one.

The trap in implementing that: an encoded empty item is **not zero bytes**. A zero-length list
still has a format byte and a length byte in front of it, so it encodes to two.
`DataSetPayload.IsEmpty` asks about the values, not the bytes - and the first version of this
package got it the other way round, which a test caught on the first run.

## `GRANT6` has no word for "no such data set"

Its three codes are permission granted, busy, and not interested or no space. So an equipment asked
to open a data set it has never heard of can only answer `NoSpace`, in the table's second reading
of it - and a refusal to a `DataSetOpenMode.Request` tells a host nothing about which of the two
happened.

That is why **S13F15 is the transaction to send first**. Of stream 13's seven replies it is the
only one carrying a length and an acknowledgement together, and `ACKC13` *does* have a word for a
data set that is not there.

The two tables differ in one more way worth knowing, because it changes what `Other` means:

- `GRANT6` declares **3 to 63** reserved and equipment-specific, so `DataSetGrant.Other` is that
  range, quoted.
- `ACKC13` lists four codes and **stops**. `DataSetAcknowledgement.Other` is therefore this library
  covering for a table rather than quoting one. It is not evidence that 4 cannot arrive: of the 88
  code tables in `Barbatos.SecsGem.Catalog`, 53 declare no range either, so silence is this
  source's habit. The item is a `B[1]`, and 252 of its 256 values are unnamed.

Either way a code outside the table never reads as the zero of its enum, which for `GRANT6` would
turn somebody else's refusal into permission to send.

**`GRANT6` is now served from two packages.** `Barbatos.SecsGem.Gem` reads it for S6F6, the
multi-block inquire, and its `GemMultiBlockGrant` has these four members under these four names -
`Ok`, `Busy`, `NoSpace`, `Other`. The repository's rule for an item two packages read is that each
side is asserted against the reference's own table rather than against the other enum, which is
what keeps the agreement standing if a third ever serves it; the member *names* are the one half a
table cannot settle, so a test on each side pins those directly.

And it is still not `GRANT`, which four packages here read: four named codes and a range from 4,
against this item's three and a range from 3. One code's difference, and an enum copied from the
wrong one puts its catch-all a code too high.

## The sequence is documented and not enforced

E5 names the steps - open, then send or get, then close - and gives stream 13 **no state model**.
So neither end here polices an order nothing in the reference defines: that is the choice
`Barbatos.SecsGem.Material` makes about streams 3 and 4, and the opposite of the one
`Barbatos.SecsGem.Carriers` makes about E87, and the difference is always what there is to check
against. E87 defines state machines; stream 13 is sixteen message rows.

What makes that liveable here, and did not in stream 4, is that `ACKC13` has a word for the
refusal. A store that wants the sequence kept answers `InvalidDataSet` for a data set nothing
opened - which is what `GemProcessProgramAcknowledgement.PermissionNotGranted` is for in stream 7,
and what `DataSetInMemoryStore` does:

- `SaveAsync` refuses a data set that was not opened for send, with `InvalidDataSet`.
- `LoadAsync` hands back nothing for one that was not opened for read.
- A **close is not a delete**. Stream 13 has no message that removes a data set, so one that
  deleted would be this library inventing one.
- The length it reports is **the length it was given** - the `LENGTH` the open declared, or
  whatever `Add` was passed. Never computed from the payload, because the encoded size of an item
  is this library's measure of a thing the dictionary never defined.

A store that does not care about any of this simply does not do it. The decision sits in the store
and not in `DataSetEquipment` on purpose.

## A body that cannot be read is refused, not dropped - where there is somewhere to refuse

The differences here are E5's, not this package's:

- **S13F4 and S13F14** carry an `ACKC13`, so an unreadable send or close is answered in the
  transaction the host is already waiting on - `InvalidData` and `InvalidDataSet`.
- **S13F2 and S13F6** carry a `GRANT6`, whose only final refusal is `NoSpace`. Not `Busy`, which
  would invite a retry of a message that will break again.
- **S13F8 and S13F16** carry no code at all, so an unreadable request is answered with a
  zero-length body. The reply cannot name a data set whose identifier this end failed to read, and
  naming data set zero would answer a question nobody asked.

Reading is more generous than writing, as everywhere in this stack. Each body is written one way
and read in every shape a machine might plausibly send it in: a grant with or without a length
beside it, an open that declared no length, a directory as a list of items or as one numeric item
carrying every identifier at once. One place is deliberately **not** generous - a send body of two
items is refused rather than guessed at, because the second of two could be the correlation number
or it could be the data, and `DSDATA` has no format to tell them apart by.

## `DATAID`, and the one inference

The dictionary gives `DATAID` a format and no description, so two readings are open: a number the
host invents to match a reply to its request, or a name for one piece of data inside the set.

The rows settle it. Stream 13 has a **separate item for which piece** - `BLKNUM`, in the blocked
send this package leaves out - so `DATAID` is not that. And every one of the thirty-two messages in
the catalogue that names `DATAID` is a report, a request or a multi-block inquire, which is where a
correlation number belongs; three packages in this repository already serve it that way, all three
as a `U4`, agreeing with the dictionary.

So `DataSetHost` generates it, one per send or get, and it is not a parameter. S13F8 is checked for
echoing back the identifiers it was asked with - the rule `Barbatos.SecsGem.Objects` keeps on
S2F64, and the only correlation stream 13 offers. A mismatch is logged and the data is still
returned: a host that wants to act on it has the log, and one that wanted the data has the data.

## What is not here yet

- **S13F9 / F10, the blocked send**, and for a measured reason rather than a shrug. Its block
  number has no dictionary entry - and neither does any other block-transfer item in the
  catalogue: `BLKNUM`, `BLKSIZE`, `BLKACK`, `BLKEND`, `CHECKSUM`, `CHKSUM` and `CKSUM` are
  undefined, seven for seven. That is a vocabulary the source never wrote down, not one row it
  missed, and it is the same measurement that keeps streams 8 and 21 out of this repository
  altogether. A data set too large for one message is, for now, several data sets.
- A store that outlives the process. `DataSetInMemoryStore` is in memory, as it says. There is no
  `Capture` and `Restore` here on purpose: a data set is not state a machine claims about itself,
  so it survives a restart through an implementation rather than through a snapshot - the same
  answer `IGemSpool` gives to the same question. Six methods, and a file or a database behind them
  is an afternoon.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
