# Barbatos.SecsGem - working agreement

A family of NuGet packages implementing SECS-II (SEMI E5), HSMS-SS (E37) and GEM (E30) for
.NET 8/9/10. Everything here is a **library**: the public API is the product, and a careless
signature is a breaking change in somebody's fab.

## Repository map

```
src/          twenty-seven shipping packages, one folder each (+ README.md, API-REFERENCE.md)
src/Shared/   compile-linked source, not a package: the serialization hub registration
samples/      equipment (passive), host (active), transcode CLI
tests/        one xUnit project per package + IntegrationTests
benchmarks/   BenchmarkDotNet
build/        package icons, strong-name key (git-ignored), the package smoke test
assets/       SML/JSON conformance fixtures
```

## Layering - do not break this

```
Core  <-  Hsms  <-  Gem
            ^----  Subsystems (E5 stream 18, and nothing else. Both ends send primaries, and the
                               two sets are DISJOINT - six and two - so the routing table cannot
                               catch a machine that declares the wrong one; a test does. Six of
                               its eight pairs are GEM's own rows re-addressed by an SSID, which
                               is the same capability Objects serves on the enhanced half of
                               stream 2 by an OBJSPEC: serve one or the other, and E39's is the
                               firmer of the two because S2F49/F50 are definitions where every
                               stream 18 row is representative. SSID and SSSTATE are defined
                               nowhere; SubsystemId carries both readings of the first, and the
                               second is written as text and says so.)
            ^----  Recipes    (E5 stream 15, and nothing else - NOT stream 19, which is also called
                               "Recipe Management", uses all twenty of the same function numbers
                               and means different transactions: S15F11 verifies where S19F11
                               deletes, and S20F11 sends. Nothing here declares a stream 19 OR a
                               stream 20 route and a test says so - stream 20 being the sharper
                               half, because two of its rows COULD be routed here: S20F17 and
                               S20F33 have every item defined and are item for item the S15F13 and
                               S15F15 this package already serves. Two stream numbers, one
                               capability, serve one or the other - and declaring two rows of
                               seventeen would answer S9F5 for the other fifteen functions where
                               leaving the stream alone answers S9F3.
                               Nine of twenty-seven pairs, the nine whose primary and reply are
                               made entirely of defined items - which is what overturned chapter
                               42's sweep of this stream into stream 19's refusal, since stream 19
                               has none and provenance is not a veto. The body is PPBODY, stream
                               7's own defined B in rows the reference documented, so a recipe is
                               plain bytes like a control program and LENGTH can be checked against
                               what arrives. RMACK is defined WITH a table, so the enum's Other is
                               honest the way GRANT's is. The other eighteen pairs stay out because
                               the reference defines 24 RCP* items and its messages use 2: the
                               names those rows use are rival spellings of the unused definitions,
                               RCPIDNEW against RCPNEWID being one of only three exact anagrams in
                               the whole reference. Do not read a format off a sibling when the
                               sibling is a rival spelling. Only stream here with a TWO-LEVEL
                               space, so the store checks the namespace at the grant AND at the
                               send, and refuses to delete one that still holds recipes.)
            ^----  ControlPrograms (E5 stream 8: stream 7's transfer design applied to firmware
                               rather than recipes, which is where its two undefined items come
                               from - CPID is read off PPID's slot and CPBODY off PPBODY's, so the
                               body is plain bytes here where stream 13's is an encoded item. The
                               blocked half, S8F7-S8F10, stays out on the same block-item
                               measurement that kept S13F9/F10 out. GRANT agrees with Material's
                               enum member for member and a test says so; ACKC8 has no table at
                               all, so zero is accepted - read off eight defined ACKCn siblings -
                               and everything else is a refusal with no reason.)
            ^----  ItemTransfer (E5 stream 21, and nothing else. ONE end type, because every row
                               travels either way - the second such type here after stream 4's
                               MaterialHandoff - so one registration, the same six primaries in
                               both routing tables, and a store at both ends: a process that only
                               sends still answers a directory request. Three inferences, each by
                               a route already in use. ITEMDATA is the encoded item that arrived,
                               stream 13's answer rather than stream 8's, because stream 21 is
                               nobody's copy and there is no slot to read a format off. The S21F4
                               body follows S14F2's nesting, since ATTRID and ATTRDATA are E39's
                               and Objects already writes them that way. ACKC21/ABTACK/DELACK have
                               no tables, so zero is accepted off 41 of the reference's 42 tabled
                               *ACK items - the exception, CPACK, names a bad parameter rather
                               than a refused message. LENGTH has no units anywhere, so the store
                               reports a value count and says so, and ToString does not print
                               "bytes". S21F9-F14 stay out on the block-item measurement - the
                               status pair with them, because what it reports is a block number -
                               and S21F19/F20 on CHKSUM, the one undefined value that cannot be
                               carried opaquely. Every figure is pinned in
                               SecsCatalogStream21Tests.)
            ^----  DataSets   (E5 stream 13, and nothing else: one of two streams here whose every
                               primary is the host's, stream 8 being the other, so the host end
                               declares no message at all.
                               Its payload item DSDATA is defined nowhere, which is why a data set
                               is carried as the encoded item rather than as a chosen format - and
                               why S13F9/F10, the blocked send, stays out: all seven of the
                               catalogue's block-transfer items are undefined.)
            ^----  WaferMap, Material, Objects   (E5 streams 12 and 3, and E39's two - stream 14
                                                 plus the OBJSPEC half of stream 2, S2F49-S2F64;
                                                 all three deliberately not on Gem, so the
                                                 enhanced commands' control-state gate is the
                                                 application's and says so)
                                 ^-- (Objects also serves stream 1's ONE E39 row, S1F19/F20, whose
                                     items are stream 14's - so it is E39's and not GEM's, and Gem
                                     must not depend on this package. Worth serving beside S14F1
                                     rather than refusing as a duplicate because S1F20 carries
                                     ERRCODE and ERRTEXT where S14F2 carries no acknowledgement at
                                     all: it is the only attribute get in the reference that can
                                     say why it failed.)
                                 ^-- Carriers, Substrates, ControlJobs, ProcessJobs
                                     (E87, E90, E94, E40, on Objects: their attributes are
                                     stream 14; E90 and E94 have no messages of their own -
                                     the catalogue's stream 17 "control job" rows are an
                                     extrapolation of stream 16, see Catalog's README - E87
                                     borrows S3F17, and E40 has stream 16 of its own)
                                            ^-- MaterialFlow   (the E87-E90, E40-E90 and
                                                                E87-E94 joins)
                                 ^-- PerformanceTracking  (E116, on Objects and on nothing else
                                     here: not a material standard, no messages, and no mapping
                                     onto E10 - that one is the factory's to make, not ours)
            ^----  Productivity   (E10 + E79, on Hsms for one thing: the container's TimeProvider.
                                   No messages, no items, no object type - these two standards
                                   define measurements, so there is nothing to put on a wire.
                                   Deliberately NOT on PerformanceTracking: see below.)
                       ^-- PerformanceFlow  (the E116-E10 join, on PerformanceTracking and
                           Productivity)
  ^-- Serialization.{Sml,Json,Xml,Ini}
  ^-- Mapper  <-- SourceGenerators (analyzer only)
```

**Two join packages**, `MaterialFlow` and `PerformanceFlow`, and they are the only packages here
that depend on several standards. The rule both keep: **none of the standards a join package joins
may depend on another.** A join lives in its own package precisely so the standards it connects
stay usable apart, and so the conventions it has to invent are visible in one place instead of
buried in whichever package got there first. A new join between standards one of them already
joins goes **into that package**, not into a new one - `MaterialFlow` holds three.

A join package also owns the lock order between the managers it connects, because that is a fact
about the pair and about nothing else. Every manager in this repository raises its events inside
its own lock and says so. So a bridge may listen to **one** of the two it joins, never both: one
that listened to both would hold each manager's lock while taking the other's, in two orders, from
two real threads. The direction that listens is the one whose trigger nothing outside can see;
the other direction is methods, which hold no lock when they begin.

- `Core` must never reference a socket, a pipe transport or `Microsoft.Extensions.*`.
  Its only third-party dependency is `CommunityToolkit.HighPerformance`.
- A new format is a new assembly implementing `ISecsFormatReader`/`ISecsFormatWriter` plus an
  `AddSecsXxx()` method. **If adding a format edits `Core`, the design is wrong.**

## State that outlives the process

Every state machine here - E87's carriers, E90's substrates, E94's and E40's queues, E116's modules,
E10's buckets - has `Capture()` and `Restore()`. Two rules hold across all six, and a new one must
keep them:

- **The snapshot is faithful; the judgement is in `Restore`.** The record says what was true,
  including the states a restart will not claim again. Putting the filtering in `Capture` would hide
  the rules in the file instead of in testable code.
- **What a host told the machine comes back; what the machine observed does not.** A reboot does not
  change what anybody asked for, and it says nothing about where the material is now. There is
  exactly one exception, E90's wafer positions, because nothing can re-sense them - and it is
  documented as an exception rather than left to be found.

Three corollaries worth stating because each has a test: a snapshot **does not invent hardware**
(ports and modules come from configuration, so unknown ones are reported and skipped); anything that
was **in progress ends the way an interruption ends**, never the way it started; and `Restore`
**raises nothing**, because a host hearing those as transitions would believe the machine had just
made them. What was not restored as recorded comes back in the report's `Adjusted`.

Where the time a machine was switched off goes is the standard's answer, not a house style: E10
charges it to a bucket the caller names, because every E10 ratio divides by some sum of them, and
E116 charges it to nothing and hands it back, because its three states have nowhere honest to put
it. Both are written down in those packages' READMEs.

**The GEM spool is not one of these, and is already durable.** A spooled report is not state the
machine is claiming about itself; it is a message it owes a host, so it survives a restart through
an implementation rather than through a snapshot - `GemFileSpool` writes one record per message,
write-through and renamed into place, adopts what it finds on reopen, and reports
`SpoolStartTime` as the oldest held message's own timestamp rather than the restart. An application
that keeps its own store writes an `IGemSpool` instead; that interface and `GemSpooledMessage` are
the whole contract. Do not add `Capture`/`Restore` to a spool - it would be a third mechanism over
two, duplicating the drain and refill `IGemSpool` already has. The Gem README's *"A spool that
outlives the process"* heading introduces this, and is not an open gap.

## Non-negotiables

- **.NET 8, 9, 10 only.** Never add `netstandard` or a `net6`/`net7` shim. TFMs come from
  `$(BarbatosTargetFrameworks)` in `Directory.Build.props` - never hard-coded in a `.csproj`.
  `net11.0` lands there once the SDK is installed.
- **No obsolete I/O.** No `BinaryFormatter`, no blocking `NetworkStream`, no `byte[]`
  allocation that can land on the Large Object Heap. Socket async, `System.IO.Pipelines`,
  `System.Threading.Channels`, `Span<T>`, `ReadOnlySequence<byte>`, `ArrayPool`.
- **Timers go through `TimeProvider`.** A test that waits on a wall clock is a flaky test.
- **C# 14, three runtimes.** Compiler-only features are fine everywhere; a type that ships with
  the runtime (`System.Threading.Lock`) needs `#if NET9_0_OR_GREATER`.
- **No `InternalsVisibleTo`.** If a test needs internal behaviour, that behaviour becomes
  documented public API, or the test goes through the public surface.
- **Public API discipline.** XML docs on every public member, `<remarks>` for the *why*; new
  types are `sealed`; a rename keeps the old member as `[Obsolete]`.
- **Central Package Management.** Every version lives in `Directory.Packages.props`.
- `.editorconfig` is the style authority, including the MIT header on every new `.cs` file.

## Build and test

```
dotnet build Barbatos.SecsGem.slnx
dotnet test tests/Barbatos.SecsGem.Core.UnitTests -f net10.0
```

Run one target framework at a time.

Before implementing against a stream, ask the catalogue how firm it is -
`SecsCatalog.TryGetStream(n, out var s)` then `s.Provenance` - and say so in the package's docs
rather than implying the standard was quoted. Then ask `s.Anchoring`, which is the question that
has actually decided the last several chapters: of the names the stream uses, which are its own and
which are borrowed, and are its gaps its own or the whole reference's. A **shared** gap is a
vocabulary the source never wrote down, so leave those rows alone - that is why no blocked transfer
is implemented here. An **own** gap is a hole in one row, so carry the field untouched and say so.
And `i.Streams` on an item answers the other direction: one named by a single stream has nothing to
check against, and one named by several usually has a package here serving it already, which a test
must then agree with.

Then ask `s.RivalSpellings`, which is the newest measure and the one that changes what a gap means.
A gap with no rival is a field the source never wrote down, so carry it untouched. A gap **with** a
rival is a field the source wrote down under another spelling, and reading a format off a sibling
would then be correcting the reference rather than reading it - and the measure has exactly **two
textual relations**, anagram and prefix in either direction, so a definition that extends a used
name at the *front* is invisible to it: `SPACK` against `RSPACK` is a suffix pair and the measure
reports nothing. Ask `i.Streams` as well, which is what found that one. There are eight in twenty
streams
and every one is a recipe name, which is why stream 15 ships nine pairs of twenty-seven and the
rename and the copy are not among them. `Provenance.DetailedUnusedDefinitions` is the companion:
44 definitions carry a full table of values and no message names them, so a capability being
**specified** in the reference does not mean anything can carry it.

`IsEntirelyRepresentative` is true for nine streams and three of them are implemented here, so it
is not a veto: check the stream against the item dictionary and against its standard's subject
matter, and record which check it passed. Stream 17 is the one that failed both - **as a stream,
which is the trap, because stream 17 is not one thing**: four of its seven primaries are the control
job extrapolation those checks are about, and the other three are a data collection plan and two
tracing rows whose every item is defined. A verdict about a stream does not reach the rows that are
not what the stream is about, so re-read it per row. And provenance
alone is not a verdict either - stream 15 was swept into stream 19's refusal on a shared figure and
the dictionary overturned it, and two more refusals have been reopened since.

**The pair of measures that has actually decided it is gaps and own anchors.** Measured across all
twenty streams they never disagree: seven streams have no gaps of either kind, six have no
vocabulary of their own, and **no stream is both rootless and whole**. Every stream refused here is
in the second list - 15, 17, 19, 20 - and streams 4 and 7 are in the first, which is why both had
refusals overturned once somebody asked. So the question to ask of a stream is not how much of it
the reference documents; it is whether its gaps and its anchors agree, and `SecsCatalogGaplessTests`
asserts the whole partition so a later chapter cannot drift from it.

**All seven gapless streams are now finished**, which makes the measure's job done rather than
ongoing: streams 1, 4, 5, 7, 9, 10 and 12 have every row served that anything here is going to
serve. One row of the seven stayed out and it is worth knowing which, because it is the shape of
every refusal left - S1F9/F10, whose two items are text with no tables named by stream 1 alone, so
there is no sibling anywhere to read a reading off. Everything still unimplemented is in a stream
with gaps, which means **the group measure is spent and what is left needs measuring row by row**.
Stream 2 had seven gaps and stream 6 four; neither could be done as a batch the way the gapless
streams were. Both are finished now - stream 6 served four of its last seven pairs, stream 2 nine
of its last ten - so **every row of every stream has either been served or refused with a reason on
the record**, and there is nothing left that nobody has argued about. What a new chapter does is
therefore re-read a refusal rather than find a row, and two of those have been overturned already.
Streams 19 and 20 have had that re-read and neither yielded a row, but **both stated reasons were
wrong and have been replaced** - which is the normal outcome and still worth the chapter, because a
refusal argued from the wrong place is the thing that gets re-derived.

**A refusal states a name, not a figure.** Stream 19 was refused as rootless, which was true of it
and of six other streams. The reason is that `RCPNS` - the recipe namespace - is undefined, has no
rival spelling anywhere, and addresses every transaction in the stream: nine of its ten primaries
carry it, and the tenth is the request that asks for the *list* of namespaces, whose reply is a
list of them. Stream 19 is not a worse-documented stream 15; it is a stream whose addressing is
missing. Stream 20 was refused on the same figure, and its real reason is that its only two
gap-free rows are rows this repository already serves under other numbers. So when re-reading a
refusal, ask which name stops it and whether anything here already does the job - and if the answer
is a count, keep reading.

**A routing table is a claim about the stream, not about the row.** This is what keeps the last
serveable rows out of streams 17 and 20 once the item measures have run out of objections. S17F13
would add something real - a trace reset that can say why it refused, where `TIAACK` has two codes
and nowhere to put a reason - and it is still refused, because declaring it answers S9F5 for stream
17's other six primaries and tells a host to keep asking for rows this repository documents as
somebody else's invention. Silence answers S9F3 and is true. The S1F19 row shows the other side of
the same rule: the same "it can say why" argument let that one in, because it is one row of a stream
served almost entirely. So weigh the row against what declaring its stream would claim.

**And where two rows do one job, the firmer source wins.** S17F9 carries S2F23's five items in
S2F23's order, and S2F23 is one of the reference's definitions where S17F9 is one of its
illustrations - the same tie-break that gave E39's half of stream 2 priority over stream 18's
re-addressed rows. "It duplicates something we serve" is not the end of the argument; which of the
two the reference documents better is.

**Three streams here are called Recipe Management and one concept can be spelled three ways.** The
rename is `RCPIDNEW` in stream 15, `NEWRCPID` in stream 20 and `RCPNEWID` in the definition neither
uses. The verify is `CHECKSUM` in streams 15 and 20, `CKSUM` in stream 19 and `CHKSUM` in stream 21
- and the reference defines **none** of those three, so unlike the rename there is nothing to be a
rival of and nothing to correct: it simply never says what a recipe checksum is. All three verify
rows are refused, consistently, under three names, and `SecsCatalogRecipeRegionTests` is where that
is said in one breath rather than once per package.

**Stream 2 is where the gap count stopped predicting anything.** It has the most undefined names of
any stream in the reference and nine of its last ten rows went in, because six carried no gap at all
and three carried one that was only the acknowledgement. Three of its seven gaps were already being
carried before the chapter started, by `Barbatos.SecsGem.Objects` on the enhanced half. So the
number to look at was never the stream's total; it was where each gap sits in its row. The one
refusal, S2F19/F20, is the clearest in the reference: `RSTC` is the **entire body** of S2F19 and
`RIACK` is undefined too, the only row in the stream where both halves are gaps - so there is no
second field to anchor a reading against and the undefined name is the thing the message is for.

**Stream 6 is where an item list stopped being readable as a body.** The catalogue records, per
row, the names that row's table mentions - and that is not a description of the message. S6F11 is
recorded with four items and S6F16 with two, and both carry a whole event report; this repository
builds them from one method. So a row's nesting has always come from a served sibling and never
from the catalogue, which is fine while the siblings agree. S6F25 is the first row where they do
not: it is recorded exactly as S6F16 is, so whether a report list follows cannot be settled, and
**a body guessed wrong is unparseable rather than merely uninterpretable**. That is the first
refusal here about a *shape* instead of a field, and it ranks below an item gap and above nothing:
when the siblings agree, serve the row and say which sibling gave the body - three packages do.

**An undefined item's format is sometimes stated elsewhere and sometimes not, and the difference is
worth checking before inventing anything.** Stream 8's `CPBODY` has no definition, so
`Barbatos.SecsGem.ControlPrograms` read its format off `PPBODY`'s slot. Stream 2's `SPD` *is*
defined - an `A[n]` - which is why a service program is text in this library and a control program
is bytes, and why nothing had to be inferred for it. Ask the dictionary for the payload item by
name before reaching for a sibling; the answer is not predictable from the stream.

**A refusal that gets overturned leaves a table row behind it.** The spool chapter removed `RSPACK`
from `GemAcknowledgementsAgreeWithTheReferenceTests`' guarded table, because the table it asserted
belonged to S2F44 - a row nothing served - while the row being read carried an undefined `SPACK`.
The next chapter served S2F44, so `RSPACK` is read at the row that defines it and the entry belongs
there again. When a chapter serves a row that an earlier one cited as unreachable, grep that row's
items across the test projects: the citation will be stale in both directions.

**Stream 14 is the worked example of a gap measured per row**, and it is why the rule above is a
rule rather than a nuisance: it has exactly one undefined name in twenty-eight rows, so two of its
last three went in and the third did not. Which means an own gap is read twice - once for whether
the field is defined, and once for **what the field is to the message**. The normal answer is to
carry it untouched, and three packages here do. It does not apply when the undefined field is the
one the message turns on: `ENABLE` is the enable-or-disable flag of S14F23, so carrying it opaquely
would leave the library unable to tell the two apart, which is the same reason stream 21's
S21F19 stays out on `CHKSUM`. Look at where the gap sits in the row, not just at how many there
are.

**A refusal's stated reason is part of the API and goes stale like the rest of it.** Both stream 4
refusals were argued from the message rows when the answer was in the item dictionary: one said a
transfer job has no state model anywhere in the reference, where `TRJOBMS` names six states; the
other said only S4F19 can mint a `TRLINK`, where S4F27 carries one and travels either way. So when
a chapter reopens a stream, re-read every reason that stream's packages give for leaving something
out, and replace the wrong ones rather than deleting them - the job half of stream 4 is still
refused, on `TRACK`, and saying so is what stops the next chapter re-deriving it.

**Check the item NAME before the table, because a true table can belong to another row.** This has
now happened twice and both times the comparison passed. GEM documents its trace acknowledgement
as `TRACK` where S2F24 carries `TIAACK`; and the spool reply was read as `RSPACK`, whose table is
real and whose row is S2F44, which nothing here serves - while S6F24 carries `SPACK`, undefined,
and this package's own two ends called the byte `RSDA` on the wire and `RSPACK` in the reader. One
byte, three names, and a green suite. So when a package says which item it is reading, ask
`i.Streams` on that name and check the answer contains the stream the row is in; where the row's
own item has no table, say which sibling the codes are grounded on - `ACKC6`, here, four rows of
the same stream with an identical table, which is why the correction moved a name and not a byte.

**Where a package states a vocabulary the reference also states, make them meet** - against the
reference's own table, never against another package's enum, because that is what makes the
agreement transitive when three packages serve one item. All six of the tabled items served from
more than one package here are asserted that way: GRANT in Material, ControlPrograms and
ItemTransfer; HCACK in Gem, Subsystems and ProcessJobs; CPACK in Subsystems and ProcessJobs; CMDA
in Gem and ProcessJobs; GRANT6 in Gem and DataSets; and ALCD in the one package that reads it. So
are `PRSTATE` against `PRSTATE`, `ControlJobCommands` against `CTLJOBCMD`, and every one of GEM's
acknowledgement enums against its item's code table.

The last two of those became shared when the coverage sweep served S2F22 and S6F6, **and the
comparison found a defect in both halves of CMDA** - neither `Barbatos.SecsGem.Gem`'s new enum nor
`Barbatos.SecsGem.ProcessJobs`' older one had a member at 3, where the table reserves 3 to 63, and
ProcessJobs folded the range onto its refusal with the reasoning written down. The rule that found
it is the one above. So: when a chapter brings a second reader to a tabled item, compare the two
*names* as well as the two tables, because member names are the half a table cannot settle - and
re-read whatever each side says about the other, since one of those remarks will now be stale.

**Two packages may read one item differently if each says so and each is asserted.** There is one:
CPACK has no code zero, because the list in S18F2 and S2F42 carries the parameters that were wrong -
so `Barbatos.SecsGem.Subsystems` has no member at zero. `Barbatos.SecsGem.ProcessJobs` reads S16F18
as one code per parameter named, successes included, so it has one. Neither shape can be had from
the catalogue, which records the items a row carries and not how the row is built. What would be
wrong is one package reading it both ways, or either side not mentioning the other. Those test projects reference
`Barbatos.SecsGem.Catalog` while the libraries do not and must not - the dependency is the test's,
not the layer's.

**An acknowledgement item with a reserved range needs a member at the first reserved code**, and the
reader has to fold the range onto it rather than casting. Sixteen of GEM's sixteen such items do;
twelve of them did not until a sweep found them all at once, and `(SomeAcknowledgement)ReadCode(...)`
is the shape to look for. Comparing the enum with the table is not enough on its own - drive a
machine that answers a reserved code, because a boundary that is right in the enum and unused in the
reader passes every comparison.

**And a new enum escapes both checks**, which the chapter after that sweep proved by writing CMDA
without the member. The comparison file is a list somebody has to add a row to, and the author of a
fresh enum is the author of its reader in the same sitting, so nothing disagrees with them. Adding
the row is therefore part of adding the enum, not a follow-up - and folding a reserved code onto an
existing refusal is not a substitute for the member, because it reports a refusal the machine never
made.

**So does a new READER of an old item**, which the stream 7 chapter proved the very next turn: CMDA
had its member and its row in the comparison, and the two new sites that read it were undriven, so
a control moved the boundary at one of them with the whole suite green. ACKC7 had the same hole
once, with one of three sites driven. The rule is per site and not per item - `ReadNamedCode` and
its siblings are easy to grep for, and `GemReservedAcknowledgementTests` is where each new one gets
a line, and the stream 6 chapter found one that had been undriven since it was written - the spool
reply's, which no drive had ever covered. The count is in that package's API reference, so a sweep
can check it: ACKC7 is read at
four sites, CMDA at three and GRANT at two.

Older files here are **CRLF** where the newer ones are LF, `GemHost.cs` among them. A multi-line
mini-patch against one of those matches zero times until the patch text is converted, which has now
cost two chapters a confusing round of "the control found nothing".

After touching a `.csproj`, `Directory.Build.*`, `Directory.Packages.props` or anything else that
changes what gets packed:

```
pwsh build/package-smoke/verify-packages.ps1
```

It packs, then builds a consumer against the `.nupkg` files. A `ProjectReference` build resolves
assemblies from `bin/` and cannot see a packaging mistake - both found so far were invisible to the
whole test suite.

## Documentation is part of the change

A public API change is not done until the package's `README.md` (the nuget.org landing page)
and `API-REFERENCE.md` are both updated, plus the sample that demonstrates it.