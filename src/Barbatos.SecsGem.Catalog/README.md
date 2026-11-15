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

Three things follow, and all three are visible in the API rather than buried here.

### 405 of the 524 entries are a representative subset

The reference flags them itself, and the flag is carried through:

```csharp
info.IsRepresentative;   // true for 405 of 524
```

They cluster in the higher streams - all 54 of S15, 34 of S20, 28 of S16 - while the streams this
repository implements against are the well-covered ones. S9 has none flagged at all.

Treat a representative entry as a good index into E5, not as a definition to implement against. A
catalogue that presented all 524 with equal confidence would be claiming more than it knows.

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
