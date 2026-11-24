# Barbatos.SecsGem.ItemTransfer

Item transfer - SECS-II stream 21 - for **.NET 8, 9 and 10**: the item directory, an item's
attributes, the transfer request and grant, the data send, the transfer abort and the item delete.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.ItemTransfer
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 21 is E5 and no
part of E30, so a process that moves named blobs over a SECS link without implementing GEM can use
this on its own.

## One kind of end, because every row travels either way

All twenty rows of stream 21 are marked as travelling in both directions. So this package has
**one type**, `ItemTransferPeer`, and one registration that both ends call - which makes it the
second such package here after `Barbatos.SecsGem.Material` for stream 4, and for the same reason.

```csharp
services
    .AddSecsGem(configure: link =>
    {
        link.DeviceId = 0;
        link.Mode = HsmsConnectionMode.Active;     // or Passive at the other end
    })
    .UseTcpTransport("192.168.0.30", 5000)
    .AddItemTransfer();                            // or AddItemTransfer<MyStore>()
```

Both ends have items, either may ask the other for its directory, and **a process that only ever
sends still needs an `IItemStore`**, because the other end may ask it what it has. That is what
being a peer means here, and it is the thing most easily got wrong by anybody arriving from the
host-and-equipment packages.

## What the provenance is, and why this stream split cleanly

**Not one of stream 21's twenty rows is a definition** - the whole stream is a representative
subset. That is true of nine streams in the reference and it is not by itself a veto: stream 18 is
implemented here on the same footing, and stream 17 was refused. What decides it is the item
dictionary.

Of the **eighteen item names** stream 21 uses, **seven are defined**:

| item | format | whose |
| --- | --- | --- |
| `ITEMID` | `A` | stream 21's own - no other stream names it |
| `ITEMTYPE` | `A` | likewise |
| `ATTRID` | `A` | E39's - streams 1, 14 and 21 |
| `ATTRDATA` | `L` | E39's likewise |
| `DATAID` | `U4` | borrowed, named by seven streams |
| `LENGTH` | `U4` | borrowed, named by eight |
| `GRANT` | `B` | borrowed, four codes and a reserved range |

**Three pairs need no inference at all** - the directory, the attributes and the transfer request -
which is the first time in this repository that a stream has split that cleanly. The eleven
undefined names fall almost entirely in the half that stays out.

### The two kinds of gap, both of them in one stream

Stream 21 is the first stream here to contain both, so it is where the difference stops being
theoretical:

- **A shared gap** is a name undefined in *every* stream that uses it - a vocabulary the source
  never wrote down. `BLKNUM` and `BLKEND` are stream 21's two, and the rule is to leave those rows
  alone. That is why no blocked transfer is implemented anywhere in this repository.
- **An own gap** is a hole in one row whose other fields are defined. `ITEMDATA`, `ACKC21`,
  `ABTACK` and `DELACK` are the four the implemented half meets, one field each, and the rule is to
  carry the field untouched and say so.

Eighteen of the twenty-two fields the six implemented pairs carry are defined, and those eighteen
use **all seven** of stream 21's defined names - so the half left out contributes no firm ground
this package is missing.

Every figure above is asserted in `SecsCatalogStream21Tests`, so a refresh of the catalogue data has
to walk past them rather than quietly make this file wrong.

## The payload is carried, not interpreted

`ITEMDATA` is named by stream 21 and by nothing else, so there is no sibling slot to read a format
off. An item therefore travels as **the encoded item that carried it**:

```csharp
var items = await peer.ListAsync(cancellationToken: token);          // S21F1

var (grant, dataId) = await peer.RequestAsync("LOG-2026-11-21", length: 4096, token);   // S21F5

if (grant == ItemTransferGrant.Accepted)
{
    using var contents = SecsItem.A(File.ReadAllText("log.txt"));
    var acknowledgement = await peer.SendAsync("LOG-2026-11-21", dataId, contents, token);  // S21F7
}
```

That is **stream 13's answer, not stream 8's**, and the difference is worth knowing if you use more
than one of these packages:

```csharp
// stream 21 and stream 13: bytes that are an encoded SecsItem, header and all
ItemPayload payload = store.Contents("LOG-2026-11-21")!;
using var item = payload.Decode();

// stream 8: bytes that are the program
ControlProgram? program = await controlProgramHost.RequestAsync("BOOT-A", token);
await File.WriteAllBytesAsync("boot.bin", program!.Body.ToArray(), token);
```

Stream 8 could read `CPBODY`'s format off `PPBODY`'s slot because stream 8 *is* stream 7's design
for firmware. There is no stream 21 was copied from, so the honest move is to choose no format -
the same place `DSDATA` ended up.

`ItemPayload` is a deliberate near-copy of `DataSetPayload` rather than a shared type, because the
two packages must stay usable apart; the duplication is named in both files so nobody thinks it was
an accident.

## The attributes are E39's, so the shape is S14F2's

`ATTRID` and `ATTRDATA` are stream 14's items, and `Barbatos.SecsGem.Objects` already serves them.
A flat item list cannot say what the nesting is, so the body comes from the stream that owns the
vocabulary rather than from here:

```
S21F4   L,2 { ITEMID, L,n { L,2 { ATTRID, ATTRDATA } } }
```

The reader is more generous than the writer, and that asymmetry is on purpose - a machine that laid
three fields per row end to end is reading the same row differently, not wrongly.

```csharp
var temperature = await peer.AttributesAsync(
    new ItemAttributeRequest { ItemId = "LOG-2026-11-21", Attributes = ["Temperature"] },
    attributes => attributes.FirstOrDefault()?.Value.FirstValueOrDefault<double>(),
    token);
```

**`AttributesAsync` takes a callback** because each `ItemAttribute.Value` is a view into the reply,
which is disposed when the call returns. Copy anything you keep. That is the same contract
`Barbatos.SecsGem.Objects` states for the same two items.

## The store

`IItemStore` is six methods, one per implemented primary: `ListAsync`, `AttributesAsync`,
`RequestAsync`, `SaveAsync`, `AbortAsync`, `DeleteAsync`. Each is awaited inside the transaction the
other end is waiting on, so none should be slow.

`ItemInMemoryStore` is enough for a simulator or a peer that passes items straight on. Three of its
rules are **its own choices and not E5's**, so each is tested where it is made:

- **A transfer must be requested before it is sent.** `SaveAsync` refuses an item nobody granted,
  and refuses one quoting a correlation number the grant did not carry - two transfers of one item
  can be in flight, so forgetting which would overwrite the wrong one.
- **`LENGTH` counts values, not bytes.** The reference says nothing about what it counts, so the
  store reports the value count of the item it was sent rather than quietly measuring a header.
- **A received item keeps the type it was listed with.** Nothing in stream 21 carries `ITEMTYPE` on
  a send, so a store that rebuilt the row on arrival would silently unfile the item.

There is no snapshot pair, for the reason the other two transfer packages give: an item is not state
a machine claims about itself, so it survives a restart through an implementation.

`ITEMTYPE` has no vocabulary in the reference at all, so what a type means is the two ends' own
business and nothing here interprets it.

## What can be refused, and what cannot

- **S21F6** carries a `GRANT`, whose four codes are quoted. An unreadable request is refused
  `DuplicateOrInvalid`, the second half of whose meaning - "or not one this end recognises" - is the
  nearest the table comes to "that made no sense". Not `Busy`, which invites a retry of a message
  that will break again, and not `NoSpace`, which would be a lie about the machine. Codes 4 to 63
  are reserved and read back as `Other`.
- **S21F8, S21F16 and S21F18** carry an acknowledgement whose single refusal has **no reason**,
  because none of the three items has a table to put one in.
- **S21F2 and S21F4** carry no code at all. A directory and an attribute list are data and nothing
  else, so an unreadable request is answered with an empty list and a log line - the reply cannot
  name an item whose identifier this end failed to read.

`GRANT` is named by seven messages across five streams, two of them stream 21's, and this package is
the **third** here to serve it after `Barbatos.SecsGem.Material` (S3F16) and
`Barbatos.SecsGem.ControlPrograms` (S8F2). One item cannot mean two things in one repository, so a
test asserts all three agree member for member rather than leaving it to the eye.

## Zero is accepted, read off the family

`ACKC21`, `ABTACK` and `DELACK` have no dictionary entries and therefore no code values - and
stream 21 holds **three of the eight** `*ACK` items in the whole reference that are like that.

What makes a zero a small inference rather than a guess is a family measurement: the catalogue
defines **fifty** items whose names end in `ACK`, **forty-two** of them with a table of values, and
**forty-one of those forty-two have 0 = accepted** - in the words "accepted", "acknowledge", "OK",
"done", "completed successfully" or "successful".

The one exception is `CPACK`, and it strengthens the inference rather than weakening it: its table
starts at 1 because its job is to say *which command parameter was wrong*, and a per-parameter item
has no use for "all fine" - that answer is `HCACK`'s. Nothing in stream 21 has that role.

So zero is accepted and that is as far as the evidence goes. Everything above it is a refusal with
no reason, logged, and `ItemAcknowledgement` has **two members and deliberately no `Other`**: with
one named code, a third member would be inventing a distinction between kinds of silence.

An acknowledgement item with **no value in it** throws rather than falling through to a zero, which
matters more here than anywhere else in this stack: a machine that sent `B,0` said nothing, and
nothing is not yes.

## The one destructive message

S21F17 deletes an item, and it is the only message in the stream that does. Neither
`Barbatos.SecsGem.DataSets` nor `Barbatos.SecsGem.ControlPrograms` has an equivalent, which is why
neither of those packages can remove anything and this one can.

**Aborting is not deleting.** S21F15 forgets a transfer and keeps whatever is already stored; an
abort that deleted would be doing S21F17's job.

## What is not here yet

- **S21F9 to S21F14, the blocked transfer and its status.** They need `BLKSIZE`, `BLKNUM`,
  `BLKEND`, `BLKACK` and `XFRSTAT`, and **every one of the catalogue's seven block-transfer items is
  undefined** - `BLKNUM`, `BLKSIZE`, `BLKACK`, `BLKEND`, `CHECKSUM`, `CHKSUM` and `CKSUM`, seven for
  seven. That is the same measurement that keeps S8F7 to S8F10 and S13F9/F10 out. The status pair is
  not separable from them either, because what it reports is a block number. An item too large for
  one message is, for now, one message.
- **S21F19/F20, the transfer complete.** It needs `CHKSUM` and `XFRACK`, both undefined - and a
  checksum is the one value that carrying opaquely does not rescue, because what two ends have to
  agree on is the algorithm and the span, and the reference gives neither.

## A store that outlives the process

```csharp
services
    .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
    .UseTcpTransport(listenPort: 5000)
    .AddItemFileTransfer(Path.Combine(AppContext.BaseDirectory, "items"));
```

`ItemFileStore` keeps each item in a file - written through, committed with a rename, the identifier
*inside* the record so `ITEMID` never becomes part of a path - and adopts whatever is in the
directory when it is constructed. Stream 21 is the one stream here where **either end** may be the
one that was switched off, because both ends call the same registration; two peers in one process
need two directories.

**This is the store with the most to write down, and the record shape is what makes it possible.** A
control program is bytes and a revision, a data set a number and a payload; an item is an
identifier, a type, a length, *any number of attributes* and contents that may not exist yet. A
record is a list of fields and an **optional** payload, so the attributes are fields and "listed but
never sent" is a record with no payload at all - a real state here rather than a half-finished one,
since S21F1 lists an item, S21F3 describes it and S21F7 sends it, three transactions a host may make
hours apart.

| | survives | why |
| --- | --- | --- |
| the items, their types and lengths | yes | what the other end sent, or what this end offered |
| the attributes | yes | and nothing else can put them back - see below |
| the contents, where there are any | yes | the encoded item that arrived |
| the grants | **no** | an S21F5 answered and an S21F7 that never came is a transaction in flight |

**`AddAsync` is the only way an item gets attributes**, because no row of stream 21 sets one - S21F3
only asks. It is also how a peer offers an item for the other end to fetch, there being no "I have a
new item" primary. And it **copies** the attribute values it is given, where
`ItemInMemoryStore.Add` keeps the caller's own items: a record is bytes, so a caller may dispose what
it built, and every attribute request decodes a fresh item. That is worth knowing because
`AttributeBody` puts a value *into* the reply it builds and a disposed reply disposes its children -
harmless for a small value, which owns nothing to release, but an attribute past the codec's pooling
threshold would go back to the pool after the first request.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
