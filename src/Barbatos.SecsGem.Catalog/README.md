# Barbatos.SecsGem.Catalog

The SECS-II message set as reference data for **.NET 8, 9 and 10**: 524 stream/function pairs
across 20 streams, and 375 data items with their formats and standard code values.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Catalog
```

## What it is for

A capture that reads `S6F11` makes you look it up. One that reads `S6F11 W Event Report Send` does
not.

```csharp
using var message = new SecsMessage(6, 11, replyExpected: true);

message.ToString();    // S6F11 W
message.Describe();    // S6F11 W Event Report Send
```

That is the whole point of the package. It is a lookup table, not a protocol: nothing here sends,
receives or validates anything.

```csharp
SecsCatalog.NameOf(2, 41);        // Host Command Send
SecsCatalog.Describe(5, 1);       // S5F1 Alarm Report Send

SecsCatalog.TryGetMessage(6, 11, out var info);
info.Direction;                   // EquipmentToHost
info.IsPrimary;                   // true
info.ReplyFunction;               // 12
info.DataItems;                   // DATAID, CEID, RPTID, V

SecsCatalog.TryGetDataItem("HCACK", out var hcack);
hcack.Format;                     // SecsFormat.Binary
hcack.MeaningOf("4");             // Acknowledge, command will be performed with completion signaled later
```

It also fills in the one field the binary wire format cannot carry:

```csharp
using var message = new SecsMessage(6, 11) { Name = SecsCatalog.NameOf(6, 11) };
Console.WriteLine(message.ToSml());   // EventReportSend: 'S6F11' W ...
```

## What it costs

One embedded 40 KB resource, parsed on the first call into frozen lookups. Referencing the package
costs nothing until you ask it something, and nothing uses reflection, so it trims and AOT-compiles
like the rest of the stack.

524 messages as generated C# would have been thousands of lines for the compiler to chew through on
every build, to produce a table that is read at most once per process.

## Where the data came from, and what that means

**It was not built from SEMI E5.** It was built from a public SECS-II reference tool, and E5 is the
standard - buy it if you are implementing against it. Provenance is in
[ThirdPartyNotices.txt](../../ThirdPartyNotices.txt).

Five things follow, and all five are visible in the API rather than buried here.

### 405 of the 524 entries are a representative subset

The reference flags them itself, and the flag is carried through:

```csharp
info.IsRepresentative;   // true for 405 of 524
```

They cluster in the higher streams - all 54 of S15, 34 of S20, 28 of S16 - and S9 has none flagged
at all.

Treat a representative entry as a good index into E5, not as a definition to implement against. A
catalogue that presented all 524 with equal confidence would be claiming more than it knows.

### Ask a stream, because that is the form the question comes in

```csharp
SecsCatalog.TryGetStream(16, out var stream);

stream.Provenance.MessageCount;              // 28
stream.Provenance.DefinedCount;              // 0
stream.Provenance.IsEntirelyRepresentative;  // true
stream.Provenance.UndefinedDataItems;        // JOBSTATE, MTRLID, PJACK, PJID, QPOSN
```

Nine streams have **no definitions at all** - 3, 4, 15, 16, 17, 18, 19, 20 and 21 - and this README
used to say that the streams this repository implements against were the well-covered ones. That
was wrong, and the property is what made it obvious: **stream 3 is E87's carrier actions and
stream 16 is E40's process jobs**, both implemented here, both entirely representative.

So `IsEntirelyRepresentative` is not a veto. It means *check this against something else*, and
there are two things to check against:

- **the dictionary**, through `UndefinedDataItems` - a body naming items the same catalogue defines
  can be read against them;
- **the standard's own subject matter**, which is a human reading and the reason this section
  exists rather than a confidence score.

Stream 16 passes both: eight of its thirteen item names are defined - `CMDA`, `CPNAME`, `CPVAL`,
`HCACK`, `PPID`, `RCMD` among them - and its 28 rows cover additional material, parameter change,
queue position, queue reorder, material binding and queue status, which is E40's subject and far
more than a template would produce.

### Stream 17 is the one that failed that check

The catalogue calls S17F1 a *Control Job Create Request*, and SEMI E94 is the control job
standard, so the table reads at a glance like E94's missing message set. **It is not one**, and
this repository implements none of it. Three findings, each pinned by a test:

**It is stream 16 with the nouns swapped.** Every function number, direction, reply partner and
item list in the control job half is stream 16's with `PJ` rewritten to `CJ`:

| | S16 | S17 |
| --- | --- | --- |
| F1 | Process Job Create Request · `PJID,PPID,MTRLID` | Control Job Create Request · `CJID,PJID` |
| F3 | Process Job Command Send · `PJID,RCMD` | Control Job Command Send · `CJID,RCMD` |
| F5 | Process Job Status Request · `PJID` | Control Job Status Request · `CJID` |
| F11 | Process Job Event Report Send · `PJID,JOBSTATE,TIME` | Control Job Event Report Send · `CJID,CJSTATE,TIME` |

And the other half is stream 2 the same way: `S17F9 Trace Request` carries `TRID,DSPER,TOTSMP,REPGSZ,SVID`,
which is `S2F23 Trace Initialize`'s body unchanged, same direction. Nothing in the stream goes
beyond either template.

**Its key items are defined nowhere.** `CJID`, `CJACK`, `CJSTATE` and `DCID` have no dictionary
entry - so there is no format to encode them as and no code table to read them against.

**And the two control job items the dictionary does define are named by no message at all.**
`CTLJOBID` is there, and `CTLJOBCMD` is there with E94's six command values - `START`, `STOP`,
`PAUSE`, `RESUME`, `CANCEL`, `ABORT`. No message row in the whole catalogue uses either. Two halves
of one reference disagreeing about one standard's own item names is what an extrapolation looks
like from the outside.

The content settles it. E94 creates a control job from its **material** - a carrier input
specification - and `S17F1` creates one from a `PJID`, which is backwards: process jobs are made
from a control job's material, not the other way round. `ControlJobManager.Create` takes carriers, a
process order and a processing specification, and none of those appears anywhere in stream 17.

So [`Barbatos.SecsGem.ControlJobs`](../Barbatos.SecsGem.ControlJobs/README.md) drives E94 over
stream 14 as object services, which is how E94 says a control job is created, commanded and read -
and a codec for stream 17 would be this library inventing a wire and calling it E94.

### One entry was wrong, and is corrected

Of the 39 messages this repository implements, 38 matched the reference exactly. The exception was
S6F11 and S6F12, which it had reversed - naming S6F11 an "Event Report Request" travelling host to
equipment.

It is the other way round: GEM equipment **sends** S6F11 and the host acknowledges with S6F12
carrying ACKC6. The GEM layer in this repository does exactly that, under test, and the sample does
it over a socket. So the catalogue carries the corrected pair, and a test pins it so that
refreshing the data cannot quietly put the error back.

That ratio is the useful number to carry away: on the part that could be checked against working
code, the source was 97% right. Good enough to index E5 with; not good enough to implement from.

### One entry disagrees and is left alone

S6F23 and S6F24 are the spooling pair. The reference flags both as a representative subset and
gives S6F23's body as `DATAID`, with `RSDC` listed as `U2`. E30 has S6F23 carry `RSDC` - 0 to
transmit the spooled data, 1 to purge it - and `RSDA` comes back in S6F24.

`Barbatos.SecsGem.Gem` implements the E30 reading, and the catalogue still says what the reference
says. The difference from the S6F11 case is evidence: there, the whole GEM flow working end to end
against a host proved the direction, and the correction rests on something. Here there is only my
reading of E30 against theirs, and silently rewriting their data on that basis would make the
catalogue look more authoritative than it is. The GEM layer reads RSDC in any numeric format for
the same reason.

### 74 names appear in bodies with no dictionary entry

`ACKC2`, `RIACK`, `PRACK` and 71 others are named by a message body and not defined anywhere in the
reference. `TryGetDataItem` returns `false` for them. The count is pinned by a test, so a refresh
has to be looked at rather than skimmed.

### 213 definitions are named by no message, which is the other direction

```csharp
SecsCatalog.Provenance;
// 20 streams, 524 messages (405 representative), 375 items: 213 unused, 74 undefined

SecsCatalog.TryGetDataItem("LOCID", out var item);
item.IsNamedByNoMessage;   // true
```

Ask a stream how firm it is before implementing against it; ask this before **quoting the reference
at anybody**. The two are different questions, and this one cannot be assembled out of the
per-stream measures: whether a definition is attached to any message at all is a fact about the
whole table, because an item nothing names is invisible from every stream at once.

| | |
| --- | --- |
| 375 | item definitions |
| 236 | distinct item names the messages use |
| **162** | both defined and used |
| **213** | defined, and named by no message |
| **74** | named by a message, and defined nowhere |

So both unanchored sets are larger than the anchored one, and that is worth knowing before anybody
treats a dictionary entry as a specification.

**It is evidence, not a verdict.** A definition no message names may simply be an item this
reference documented without documenting its transactions, and several are exactly that:
`CARRIERSPEC`, `CATTRID`, `CATTRDATA`, `PORTACTION` and `PODID` are E87's object items, and
[`Barbatos.SecsGem.Carriers`](../Barbatos.SecsGem.Carriers/README.md) ships on other grounds -
stream 3's carrier half and the stream 14 object services, neither of which needs those five names.
What the flag does rule out is reading such an item **in context**, because the reference supplies
no context: no neighbours, no direction, no reply.

### The storage service standard that this measurement sank

E88 - a stocker, as a standard of its own - was asked for and is not here, and the reason is a
measurement rather than a preference:

- **No stream in the catalogue carries a storage service.** Twenty streams and 524 messages, and
  nothing names a stocker, a zone, a shelf or a storage capacity.
- **The two items it would be built on are defined and named by nothing.** `LOCID` (an `A` item)
  and `SLOTID` (a `U1`) are both in the 213. That is the same signature stream 17's control job
  items have, and it means there is no message to read either item *in*.
- **The storage model this stack has already exists**, in `Barbatos.SecsGem.Carriers`, because
  S3F31 demanded one: that transaction means *tell me about all of your storage locations*, so a
  package implementing it needed somewhere for a carrier to be that is not a load port. A second
  storage model in a second package would mean a machine with two answers about one shelf, and
  E87's stream 3 cannot hand storage over to E88 while S3F31/F32 is still its transaction to
  answer.

So a stocker here is a `CarrierStorageLocation`, addressed by `LOC`, reported by S3F31 and readable
as a stream 14 object - and what E88 adds on top of that, zones and capacities and a storage
service's own commands, is not in this reference in any form. That is a standard to buy and read,
not one to extrapolate, and [stream 17](#stream-17-is-the-one-that-failed-that-check) is what
extrapolating looks like when somebody else has done it.

## Not a structural template

`DataItems` is a list of names, in the order they appear in the body. It does **not** record
nesting, repetition or which items are optional:

```csharp
SecsCatalog.TryGetMessage(2, 41, out var hostCommand);
hostCommand.DataItems;   // RCMD, CPNAME, CPVAL
```

The real S2F41 body is `<L[2] <A RCMD> <L[n] <L[2] <A CPNAME> <CPVAL>>>>`. The catalogue answers
"what appears in an S2F41 body", not "what shape is an S2F41 body", and a reader that treated it as
the second would build the wrong message. Use [`Barbatos.SecsGem.Gem`](../Barbatos.SecsGem.Gem/README.md)
for bodies that are actually built and parsed.

## Internal consistency, checked

The data is third-party, so the tests assert the invariants rather than assuming them. All of these
hold across the 524:

| Invariant | |
| --- | --- |
| An odd function is a primary; an even one is a secondary | no violations |
| A primary's reply is the next function up, and that message exists | no violations |
| A secondary points back at the primary it answers | no violations |
| A Stream 9 report has no reply | no violations |
| Every message belongs to a stream the catalogue knows | no violations |
| Every data item carries a parseable E5 format token | no violations |

There is no stream 11, because E5 does not define one. A test asserts that too, so the gap reads as
the standard's rather than the parser's.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

The host sample uses it to make an error report legible. It asks the equipment for a stream that is
not there, and prints what comes back:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host
```

```
S9F3 Unrecognized Stream Type came back: S7F1 Process Program Load Inquire is not implemented
```

Without the catalogue that line reads `S9F3 came back: S7F1 [0x7B2D347A] is not implemented` -
correct, and it makes you go and look both of them up.

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
