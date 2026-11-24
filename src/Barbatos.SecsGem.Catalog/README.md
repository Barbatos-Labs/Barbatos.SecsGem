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

#### And the three rows that are not about control jobs

All of the above is about four of stream 17's seven primaries. The other three are a different
subject: S17F7 is a data collection plan and S17F9 and S17F13 are **tracing**, which is E5's own
and which `Barbatos.SecsGem.Gem` already serves. Those two are the only pairs in the stream whose
every item is defined, so the argument above does not reach them and a re-read was owed.

| row | what decides it |
| --- | --- |
| S17F7/F8 | `DCID` is its whole body, undefined, and named by stream 17 alone - the row's address, the way `RCPNS` is stream 19's |
| S17F9/F10 | the same five items as **S2F23**, in the same order. One capability, two stream numbers, so serve one or the other - and S2F23 is one of the reference's **definitions** where S17F9 is one of its illustrations |
| S17F13/F14 | stopping a trace is S2F23 asking for no samples, which `GemHost.StopTraceAsync` sends. What this would add is a *reason* - `ACKA` with `ERRCODE` and `ERRTEXT`, where `TIAACK` has two codes and nowhere to put one |

That last one is the S1F19 argument, and it did let that row in: the only attribute get in the
reference that can say why it failed. What differs is the cost of declaring the stream. S1F19 is one
row of a stream this library serves almost entirely; this would be one pair of seven in the stream
argued above. **A routing table is a claim about the stream**, so declaring it would answer S9F5 for
S17F1 - "I do stream 17, but not that function" - and tell a host to keep asking for rows nothing
here believes in. Silence answers S9F3, and that is true.

`TRACK`, S17F10's acknowledgement, is also the last loose end of the `TIAACK` correction two
chapters earlier: GEM documents its trace acknowledgement under that name, S2F24 carries `TIAACK`,
and `TRACK` is a real `A[n]` with no table that belongs to **streams 4 and 17 and means something
different in each** - a transfer job acknowledgement twice in stream 4, a trace acknowledgement once
here. In stream 4 it at least sits beside an `ERRCODE` and an `ERRTEXT`. In S17F10 it is alone.

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

## The third question: whose vocabulary is it?

Two measures were here before this one. `SecsStreamProvenance` answers *how much* of a stream the
source documented, and `SecsDataItemInfo.IsNamedByNoMessage` answers whether anything uses a
definition. Every chapter of this repository then went on to compute a third thing by hand, so it
is a property now:

```csharp
var nineteen = SecsCatalog.Streams.Single(stream => stream.Number == 19);

nineteen.Anchoring;                 // S19 5 borrowed anchor(s), 0 own, 1 shared gap(s), 5 own
nineteen.Anchoring.OwnGaps;         // CKSUM, RCPNS, RCPPNAME, RCPPVAL, RCPREV
nineteen.Anchoring.BorrowedAnchors; // LENGTH, PPBODY, RCPID, RMACK, TIME
```

Two independent facts about each item name a stream uses - does the dictionary define it, and does
any other stream name it - make four buckets that partition exactly:

| | defined | undefined |
| --- | --- | --- |
| **named by this stream alone** | `OwnAnchors` - its own documented vocabulary | `OwnGaps` - nothing anywhere to read them against |
| **shared with other streams** | `BorrowedAnchors` - firm ground, somebody else's | `SharedGaps` - the source's habit, not this stream's hole |

**The gap split has already decided two questions.** `BLKNUM` is a shared gap - one of seven
block-transfer items this reference defines nowhere - so the blocked transfers of streams 8 and 13
are left alone, because that vocabulary was never written down. `DSDATA` is an own gap, a hole in
one row of a stream whose siblings' payloads *are* defined, so a data set is carried untouched
instead. The older measure called both "undefined"; this one says which kind.

**The anchor split is evidence and not a verdict**, the same caveat the other two carry. Six
streams have no defined item of their own - 8, 15, 16, 17, 19 and 20 - and two of them are
implemented here: stream 8 because it is stream 7's design and its items read off `PPID` and
`PPBODY`, stream 16 because E40 defines the behaviour its rows carry. A stream with no vocabulary
of its own needs somewhere else to stand; it does not follow that there is nowhere.

And the per-item direction, which is the single most-reused measurement in this repository:

```csharp
SecsCatalog.TryGetDataItem("GRANT", out var grant);
grant.Streams;      // 2, 3, 4, 8, 21 - which is why two packages here serve it and a test agrees them

SecsCatalog.TryGetDataItem("RCPSPEC", out var specification);
specification.Streams;   // 15, 20 - and stream 19 calls the same thing RCPNS, which is undefined
```

## Three streams called Recipe Management, and two of them by the same name

The catalogue has one pair of streams that share a name, and finding it closed a question rather
than opening one:

```csharp
SecsCatalog.Provenance.StreamNameClashes;   // "Recipe Management" is S15 and S19
```

A shared name is not a fault. What sits underneath this one is:

- **108 rows across streams 15, 19 and 20, and not one of them a definition.** The source
  documented none of the recipe management it lists.
- **Streams 15 and 19 use all twenty of the same function numbers.** Four carry the identically
  named transaction - the directory pair and the download send pair - and two more differ by one
  word. The rest are shifted: **S19F11 is a recipe delete where S15F11 is a recipe verify.**
- **Four roles, two vocabularies.** The namespace is `RCPSPEC` in streams 15 and 20 and `RCPNS` in
  stream 19; the revision is `RCPVER` or `RCPREV`; the parameter pair is `RCPATTRNAME`/`RCPATTRVAL`
  or `RCPPNAME`/`RCPPVAL`; the checksum is `CHECKSUM` or `CKSUM`. Of those eight names the
  dictionary defines exactly one - `RCPSPEC`, the one stream 19 does not use.

So a library that implemented stream 19 from this reference would be choosing the undefined
vocabulary over the defined one, and would answer S19F11 as a delete while a machine built from the
other half of the same reference means it as a verify. That is a recipe deleted when a host asked
to check one, which is why streams 19 and 20 are left alone.

**Stream 15 is not**, and the difference came out of the measure below rather than out of this
section. The figure these three streams share - 108 rows, no definitions - is provenance, and
provenance is not a verdict: counting the pairs whose primary *and* reply are made entirely of
defined items gives stream 15 nine, stream 20 two and stream 19 none. So
[`Barbatos.SecsGem.Recipes`](../Barbatos.SecsGem.Recipes/README.md) serves those nine, and four
more whose single gap the rival measure below turned out to settle rather than block - thirteen of
twenty-seven. It says in its own README which standard it is, at length, because the name collision
is a hazard for whoever reads a tool's manual rather than for anything on the wire.

The one piece of good news is that all three carry a recipe as `PPBODY`, which stream 7 defines as
a `B` - so the payload was never the problem; the vocabulary around it was.

## The fourth question: did the source name it twice?

The three measures above are all about what is *missing*. This one is about what is **present
twice**, and it is the only measure here that has gone on to find a fault in shipping code.

```csharp
SecsCatalog.Provenance.RivalSpellings;
// RCPIDNEW (S15) is an anagram of RCPNEWID, which no message names
// RCPSTATE (S15,S19,S20) extends RCPSTAT, which no message names
// VALACK (S15) is an anagram of VLAACK, which messages also use
// ... eight in all

SecsCatalog.Streams.Single(stream => stream.Number == 15).RivalSpellings.Count;   // 7
SecsCatalog.Streams.Single(stream => stream.Number == 21).RivalSpellings.Count;   // 0
```

`UndefinedDataItems` and `UnusedDefinitions` are the two unanchored sets - 74 names messages use
with no definition, 213 definitions no message names. This measure is **the overlap the names
themselves reveal**: a gap whose field the source did write down, under a different spelling.

**That changes what a gap means.** A gap with no rival is a field the source never recorded, so a
package can carry it untouched and say so - which is what `Barbatos.SecsGem.DataSets` does with
`DSDATA` and `Barbatos.SecsGem.ItemTransfer` with `ITEMDATA`. A gap *with* a rival cannot be
treated that way: reading a format off a sibling would mean choosing between two spellings of one
item while the source's own spelling sits in the dictionary unused, and that is correcting the
reference rather than reading it.

**Eight rivals in twenty streams, and every one of them is a recipe name.** That is the finding,
and it is why nine packages were built before anybody met this hazard. Stream 15 carries seven of
the eight, which is what kept its rename and its copy out of `Barbatos.SecsGem.Recipes` after the
chapter had planned to implement both.

Two relations are reported, and the difference matters:

| relation | example | worth |
| --- | --- | --- |
| `Anagram` | `RCPIDNEW` / `RCPNEWID` | hard to reach by coincidence - three in the whole reference |
| prefix, either way | `RCPSTATE` / `RCPSTAT` | a candidate, not a conclusion |

And `IsDefinitionUsedElsewhere` splits them again. Four of the eight name a definition that
messages *also* use, which is worse: then both spellings are live and nothing suggests they mean
the same thing. `VALACK` against `VLAACK` is an anagram of an item in active service.

### Specified in full, wired to nothing

The companion figure, and the one that explains several standing refusals at once:

```csharp
SecsCatalog.Provenance.DetailedUnusedDefinitions.Count;   // 44
```

A bare unused name might be an editing leftover. **Forty-four of the 213 carry a full table of code
values** - a capability the source specified in detail and then gave no message. Three worth
knowing about:

- **`ACKC15`**: accepted, denied busy, denied invalid recipe ID, denied invalid data. The reasons a
  recipe download can be refused - and no row names the item, so stream 15 carries `RMACK` instead,
  which has one refusal and no reason at all.
- **`RMNSCMD`**: create, delete, rename and list a namespace, in one item. Stream 15 spends four
  separate pairs reaching three of those four and has no clean pair for the rename.
- **`PRSTATE`** and **`CTLJOBCMD`**: the process job states and the control job commands, which
  `Barbatos.SecsGem.ProcessJobs` and `Barbatos.SecsGem.ControlJobs` model from E40 and E94 instead.

### The first measure that audits this repository

Those last two are why this chapter reaches outside the Catalog. Both packages state a vocabulary
the reference also states, from a different source, with nothing connecting the two - which is the
shape of mistake that let a stale `GRANT` figure survive a chapter. So two test files now make them
meet:

- `PRSTATE` has ten rows with **5 reserved**, and `ProcessJobState` is numbered `0..4, 6..9` -
  it agrees exactly, hole and all. An enum written by counting would have put `Pausing` at 5 and
  every later state one too low.
- `CTLJOBCMD` names six commands, and `ControlJobCommands` already said of two of its members that
  they are "not one of `CTLJOBCMD`'s six" - a claim in a doc comment with nothing asserting it.
  Now the six are driven through `ControlJobManager.Command` and the two extras are named.

Those test projects reference this package; the libraries themselves do not and must not. The
dependency belongs to the test, not to the layer.

### All forty-four, filed

The sweep that followed is written down rather than left to be redone, because a sweep nobody
recorded is one the next reader has to repeat. `SecsCatalogSweepTests` partitions the forty-four
into three groups and asserts the partition, so a refreshed reference with a forty-fifth makes
somebody classify it instead of quietly enlarging a count:

| group | how many | what they are |
| --- | --- | --- |
| audited against shipped code | 6 | a package here states the same vocabulary from its own standard, and the two now meet in a test |
| the reference's unused alternative | 4 | a package here serves a *different* item in the same role, and the rows name the one in service |
| describes nothing implemented here | 34 | checked and passed over - mostly acknowledgements for messages no package serves |

**Five of the six audited agreed; one did not.** `ACCESSMODE` is numbered 0 automatic and 1 manual
where `Barbatos.SecsGem.Carriers` has `Manual = 0`. The numbers never cross the link - that package
carries an access mode as text, which its own test establishes - so the disagreement cannot bite,
and saying so is the point: an undefended numbering is how a later chapter serialises it as the
`U1` the dictionary describes and inverts every load port in a fab.

The four alternatives are the group worth checking first in any future sweep. `ACKC15` names four
reasons a recipe download can be refused where the `RMACK` the rows carry has one; `RMGRNT`
separates space from authority where `RMACK` does not; `ACKC7A` is a second acknowledgement for
stream 7, whose every row carries `ACKC7`; `WRACK` is a data-write acknowledgement where S18F8
carries `SSACK`. Each time, the package here had to pick the item the rows name rather than the
fuller one the dictionary defines - and each time it did.

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

## What this repository does not serve, and which measure decides it

Fifty-nine of the reference's **274 primaries** are declared by nothing in this repository, and 215
are. After seven chapters spent re-reading those refusals one group at a time, the list is written
down in one place and asserted as a **partition**: every refused primary appears exactly once, under
the one measure that decides it. A row with two reasons is filed under the one that would still
refuse it if the other were answered - which is the lesson of the refusals that fell, because in
every one of those the weaker reason was doing the work.

| the measure | rows | and what it says |
| --- | --- | --- |
| the field the row turns on | 9 | The undefined name is what the message is *for* - `RSTC`, `CELLNAME`, `NRED`, `ENABLE`, `AUTOSEL` - so carrying it untouched would leave a library unable to tell apart the two things its row distinguishes |
| producer and reader | 9 | The undefined field is written by one end and must be *understood* by the other: `BLKNUM`, `BLKEND`, `BLKSIZE` in every blocked transfer, and a checksum the receiver has to compute and compare |
| two readings, different bytes | 4 | A rival or a twin gives the name a second reading and the two disagree - an `A[n]` against E39's list for a recipe attribute, an anagram whose table belongs to another row |
| a shape | 2 | The body is a list of entries and no served row settles its nesting. **A flat body of two items has one reading; a list has as many as there are nestings** |
| a name with no second instance | 1 | S7F21: its object, *Reference*, appears in two of 524 row names - this row and its own acknowledgement - and its reply is only an acknowledgement |
| what declaring the stream claims | 24 | Streams 17 and 20, whole. **Every field of these rows reads**; what keeps them out is that declaring one turns an honest S9F3 for the stream into an S9F5 for the rest, which invites a host to keep asking |
| the addressing is missing | 10 | Stream 19, whole: `RCPNS` is undefined, has no rival anywhere, and addresses every transaction in the stream |

**Twenty-five of the fifty-nine carry no field this library cannot read** - the twenty-four stream
claims and the one name - so the frontier is not "rows nobody has got to". It is two kinds of claim,
and neither is about effort. Four of the five rows refused on a stream claim whose every field is
defined are S17F9, S17F13, S20F17 and S20F33, and the fifth, S20F15, is readable without being
gap-free.

The eleven streams that hold them are 2, 6, 7, 8, 13, 14, 15, 17, 19, 20 and 21; the other nine are
served whole. `SecsCatalogRefusalPartitionTests` holds the table and the arithmetic, and each
package asserts separately that its own leave-outs reach no routing table.

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
