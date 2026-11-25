# Barbatos.SecsGem - working agreement

A family of NuGet packages implementing SECS-II (SEMI E5), HSMS-SS (E37) and GEM (E30) for
.NET 8/9/10. Everything here is a **library**: the public API is the product, and a careless
signature is a breaking change in somebody's fab.

## Repository map

```
src/          twenty-seven shipping packages, one folder each (+ README.md, API-REFERENCE.md)
src/Shared/   compile-linked source, not a package: the serialization hub registration, and the
              durable record directory seven stores in five packages keep their files with
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
                               Fourteen of twenty-seven pairs. Nine have a primary and a reply made
                               entirely of defined items - which is what overturned chapter 42's
                               sweep of this stream into stream 19's refusal, since stream 19 has
                               none and provenance is not a veto. The body is PPBODY, stream 7's
                               own defined B in rows the reference documented, so a recipe is plain
                               bytes like a control program and LENGTH can be checked against what
                               arrives. RMACK is defined WITH a table, so the enum's Other is
                               honest the way GRANT's is. The other FOUR carry one undefined name
                               each and were refused for six chapters on the rival measure, which
                               reports a relation and was read as a verdict: RCPIDNEW, RCPSPECDST
                               and RCPIDDST each extend a name that is IN THE SAME BODY two
                               positions earlier, and RCPLOCKID is a lock token the machine mints
                               and reads back, carried as the encoded item. The FIFTEENTH went in
                               later and on a shape rather than an item: S15F36 is the current
                               recipe, two items and no acknowledgement, and the sibling a nesting
                               comes from need not be in this package - S7F6 is an identifier and a
                               second field, flat. Its RCPVER is carried untouched where S15F49's
                               owner could not be, because a version is a label a host prints and
                               an owner is a field the far end must parse. The thirteen still out
                               are refused one of four ways and the README gives them row by row.
                               Only stream here with a TWO-LEVEL space, so the store checks the
                               namespace at the grant AND at the send, and refuses to delete one
                               that still holds recipes - and the only one with a LOCK, whose
                               every consequence is the store's invention because E5 defines the
                               transaction and never says what it prevents.)
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
                               MaterialHandoff's handoff half - so one registration, the same six primaries in
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

**`RecipeFileStore` is the second of those, so it is a pattern now and not an exception**: a store
is durable through an implementation, and gets no snapshot pair either. What it added is a rule the
spool had no need of - **identity belongs inside the record, not in the file name.** A spooled
message is the machine's own; a recipe is named by the host, and `RCPID` is an `A[n]` that nothing
constrains. So a file name is a sequence number and nothing else, which buys two things at once: no
name a host chose is ever part of a path, so `CON`, `../escape` and two identifiers differing only
in case are three ordinary recipes rather than three defects; and because the name says nothing
about which recipe the file holds, a rename is **one atomic rewrite** where moving a file would be
two operations with a window between them, and a recipe can never have two records for a later start
to choose between. Reach for this whenever a durable store keys on something the far end chose.

**And the three rules above apply to a store and not only to a state machine.** The one that bites
is the second: a download grant is a transaction in flight, so nothing is written for it and
`GrantedCount` is zero on every open - an S15F5 answered and an S15F7 that never came ends the way
an interruption ends, and the host asks again. Everything else there is what a host told the machine
and comes back: the recipes, the namespaces, the locks and the selection. The judgement stays in the
reading rather than the writing, too - a lock or a selection naming a recipe that is no longer on
the disk is dropped on adoption, where filtering at the write would have hidden the rule in the
file.

**Every store here that keeps what a host sent is durable now, and the mechanism is one file:
`src/Shared/DurableRecordDirectory.cs`.** Eight of them in five packages: streams 8, 13, 15 and 21
have one apiece, stream 7 has three, and stream 2's service programs are the eighth. Nothing a host
sends this library is held only in memory any more, which is a claim worth keeping true - the next
store added to this repository is the one that breaks it.

**Service programs are where the rule about what comes back had to be applied to something the
machine made.** The text is `SPD`, which the reference *defines*, so it is text a host wrote and the
record keeps it. The **results of a run are not written down at all**: a result is what the machine
produced by running code, and a restarted machine has run nothing, so answering S2F9 out of a file
would be claiming a run that never happened - the host sends S2F7 again, and the program is there to
run. The in-memory store already made the smaller version of that argument, clearing a program's
results when the program is replaced because they are the output of code that is no longer there; a
restart is the same argument one step further. And the `Run` delegate is not durable either, for a
different reason: it is not state but the machine's own wiring, supplied on every start like the
directory. What they share is a mechanism rather than a rule -
write through under a temporary name, commit with a rename, name the file by a sequence number
alone, adopt the directory on the next construction. It is **shared source and not a package**
because no one of the five may depend on another: the four transfer packages sit side by side on
Hsms and a machine serving recipes must not drag in GEM, so the only package all of them could take
it from is the transport, which has no business holding a file format.
`SecsSerializationRegistration` is the same arrangement for a related reason. The argument for
sharing rather than copying is not taste either - the first draft of `RecipeFileStore.TryDelete`
caught `IOException` where `GemFileSpool`'s catches `UnauthorizedAccessException` too, which is a
read-only file throwing out of a transaction in one store and not the other.

**The spool shares the bottom half of it and not the top, which took two re-reads to get right.**
The structural argument holds and always did: a spool is a queue with an order, a head, a capacity
and an overflow policy that never parses a record to know what it holds, where the seven keyed
stores parse every record on the way in because the key is inside it. So **the record format stays
the spool's own** - a twenty-four byte header, its own reader - and none of the field-and-tail layer
is any use to it. What that argument never covered is the **atomic write and the directory scan**,
which are identical, and which the spool duplicated for two chapters. It takes `Records()`,
`PathFor`, `Delete` and `WriteAtomicAsync` now, and is eighty lines shorter for it. The line to draw
is therefore: **the disk mechanics are shared, the record format is not.**

The move cost exactly what the previous re-read said it would, which is why that paragraph was worth
writing down: `GemFileSpool.PartialExtension` is `.spooling` and **public**, so the shared directory
took an optional `partialExtension` parameter rather than the constant being changed. One
consequence is visible on a disk and nowhere else - a partial is named by appending, so it reads
`<sequence>.spool.spooling` where it used to read `<sequence>.spooling`. Both shapes are swept,
because the sweep is a pattern on the extension, and a test covers each: the older one is what a
directory written by an earlier version of this library contains.

**A record is a list of fields and an optional payload, which is what lets one format serve four
stores.** A control program is an identifier and a revision, a data set a number and a declared
length, a recipe a namespace and an identifier, an item an identifier, a type, a length and any
number of attributes - no fixed header fits all four, and a count plus length-prefixed fields fits
each. A field's meaning is its position, which is what a SECS-II body is. The **tail** is the one
field written straight from the caller's memory rather than copied in beside the others, and whether
a record has one is in the header - so stream 21 can tell an item that was listed from one whose
contents are empty.

**What each store writes down differs, and in every case the question is which end said it.** The
grants (stream 8's single anonymous one, stream 21's per item) and stream 13's open are transactions
in flight, so none of them is written. Stream 13 went further and is the sharpest case: between an
S13F1 and the data there is **a declared length and no data set**, and that length is written *with
the payload or not at all*, because a record whose length field disagrees with its own payload is a
machine reporting S13F16 figures it cannot stand behind. Where a store is deliberately tidier than
its in-memory sibling - `DataSetFileStore.CloseAsync` forgetting the declaration, `Count` being what
is on the disk - it says so and no host can see the difference.

**And two of the three found something the stream cannot do.** Stream 8 has **no row that names a
program**: S8F3 is anonymous, so a named control program can only come from the store's own
`AddAsync`, and nothing a host sends will ever create one. Stream 21 has no row that sets an
**attribute**: S21F3 only asks. So `AddAsync` on those two is not the convenience `Add` is on the
in-memory stores - it is the only door, and a durable store without one could serve only what was
already on the disk. Which is also why the equipment sample seeds through the store API and asks
first: a seed that overwrote what the last host left would make the durability pointless.

**Stream 7 has three stores, and on a disk their records are indistinguishable - so the extension
is load-bearing.** A plain program, a formatted one and a matrix are each an identifier and a
payload, which is why the records carry `.processprogram`, `.formattedprogram` and `.matrix`: a
store pointed at the wrong directory then finds **nothing** rather than finding somebody else's
recipes and answering S7F5 with them. That is the matrix chapter's separation - a duplicate body
separates the stores rather than refusing the rows - carried onto the disk, where the same mistake
is available again and quieter. One directory per store is still the recommendation, because the
sweep of unfinished writes is directory-wide; sharing one is tested and safe at start-up.

**Two more things stream 7 settled.** Its grant is **computed, not remembered** - `InquireAsync`
answers from what is held - so unlike every other store here it has nothing in flight for a restart
to end, and a machine that comes back says `AlreadyHave` for a recipe it still has. And a
**formatted program is written down as the body S7F23 carries**, through the codec that already
writes it for the wire: the in-memory store's remarks had said structure would be *easier* to
persist than bytes, and it is the other way round - bytes are one field and a payload, where
structure needs a nesting, so the honest format for structure is the one the standard already
defines. The alternative was a second format for the disk, kept in step with the first by hand.

**And writing a second implementation is what finds a code nothing ever sent.** `ACKC7` code 3 is
`MatrixOverflow`, the one code in that table belonging to the matrix rows rather than the plain
ones; the member had been in the enum since the sweep that matched every enum to its table, and the
in-memory matrix store answered the generic `PermissionNotGranted` at capacity, so nothing in the
repository ever sent it. Both stores answer it now and a test drives it over the wire. The rule
that found it is the mirror of the reserved-code one: **compare what a new implementation answers
with what its sibling answers, member by member**, because a member nothing sends passes every
comparison of an enum against a table.

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
rival is a field the source wrote down under another spelling - and the next question is the one
that took six chapters to ask: **`IsDefinitionUsedElsewhere`.** Four of the reference's eight rivals
name a definition the messages THEMSELVES use, and in three of those the definition sits in the same
body two positions earlier: that is a **modifier, not a misspelling**, and its format is read off
the row rather than chosen. Only a rival whose definition nothing names is a second spelling of one
field, and even then the question is whether the two readings **differ** - `RCPIDNEW` has both
relations at once and both of its candidates are an `A[n]`, so the rivalry puts no byte in doubt. A
SECS-II body is positional; item names never travel, so a wrong spelling mislabels a comment.
The measure has exactly **two textual relations**, anagram and prefix in either direction, and it
is blind in two directions because of it. A definition that extends a used name at the *front* is
invisible: `SPACK` against `RSPACK` is a suffix pair and the measure reports nothing. So is a
**tail-divergent twin**, a used name and a definition sharing a long stem and then parting, where
neither is a prefix of the other - there are **ten** of those, seven of them recipe names, and they
are what keeps seven of stream 15's pairs out, because `RCPATTRVAL` against `RCPATTRDATA` and
`RCPOWNER` against `RCPOWCODE` are readings that DIFFER: a list against text, text against a
BOOLEAN. Ask `i.Streams` as well, which is what found the front-extension one. There are eight
rivals in twenty streams and every one is a recipe name, which is why stream 15 ships fourteen pairs
of twenty-seven and why it shipped only nine for six chapters.

`Provenance.DetailedUnusedDefinitions` is the companion:
44 definitions carry a full table of values and no message names them, so a capability being
**specified** in the reference does not mean anything can carry it.

**And when a refusal quotes a statistic, ask what it was measured over.** Stream 15's rested on a
real figure: of the thirty pairs where the dictionary defines a name and that name plus a suffix,
only thirteen keep the format, which is worse than a coin flip. But **exactly one of those thirty is
ever named by a single message** - `SV` and `SVNAME`, in S1F8 - and it keeps its format. The
statistic is about names that never stand beside each other, where a format has to be guessed from
the dictionary instead of read off the row, and all three of stream 15's cases stand beside their
base. A figure can be true of its population and silent about the case in hand.

**An undefined field may be carried opaquely when the end that must read it is the end that produced
it.** That is what let stream 15's lock in and keeps S15F49's owner out: a lock token is minted by
the machine in S15F24 and handed back to it in S15F25, so nothing in between needs to know what it
is - the same shape as `DSDATA` and `ITEMDATA`, which are the sender's own data stored whole. An
owner travels the other way, so the host would choose a format the machine has to parse, and there
the two readings differ. Ask which end writes the field and which end reads it before carrying it
untouched.

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

**All seven gapless streams are now served whole**, with no exceptions left: streams 1, 4, 5, 7,
9, 10 and 12 have every row served. The last one out was S1F9/F10, refused because its two items
are text with no tables named by stream 1 alone - and that is `TRACK`'s mistake again, since both
are *defined*. Everything still unimplemented is in a stream with gaps, which means **the group
measure is spent and what is left needs measuring row by row**.
Stream 2 had seven gaps and stream 6 four; neither could be done as a batch the way the gapless
streams were. Both are finished now - stream 6 served five of its last seven pairs, stream 2 nine
of its last ten - so **every row of every stream has either been served or refused with a reason on
the record**, and there is nothing left that nobody has argued about.

**And the record is one table now: `SecsCatalogRefusalPartitionTests`.** 215 of the reference's 274
primaries are declared and the other **fifty-nine are a partition** - each appears exactly once,
under the one measure that decides it, and a row with two reasons is filed under the one that would
still refuse it if the other were answered. The **nine** measures and their counts: the field the
row turns on (15), producer and reader (12), two readings that differ (7), a shape (6), a name with
no second instance (1), a firmer row does the job (12), the addition is unobservable (1), the
standard is contradicted (4), the addressing is missing (1). Eleven streams hold them and the other nine are served whole. **Five of
the fifty-nine are gap-free** - S7F21, S17F9, S17F13, S20F17 and S20F33 - and four of those five
are refused because a firmer row does their job, so no gap-free row here is refused on a field or on
a shape. Re-read a refusal by starting from its row in that table, and if the table is what turns
out to be wrong, that is the chapter - which is how it came to have nine measures instead of the
seven it shipped with, and why the figure above reads five where it read twenty-five. Two of the
nine new or re-read measures were stream-level verdicts holding thirty-eight rows between them, and
both collapsed when somebody read their rows one at a time: **a measure that names a stream rather
than a row is the one to re-read next.**

What a new chapter does is
therefore re-read a refusal rather than find a row, and four of those have been overturned
already - stream 4's job half, stream 7's command cancel, formatted directory and matrix family,
five pairs of stream 15, stream 1's material transfer status and stream 6's notification report.

**A refusal with two reasons needs both of them re-read, and the weaker one is usually the one
that was doing the work.** S1F9/F10 had two. The first was an item argument and wrong the way
`TRACK` was wrong. The second was that the subject - material transfer - is served properly in
stream 3 and in E87, so two strings nobody gives a vocabulary would be an API with nothing behind
it. That half is where the chapter had to think, and it loses to the tie-break from stream 17:
E87's port status row carries `PORTSTATUS`, which has **no dictionary entry at all**, and
`Barbatos.SecsGem.Carriers` serves it by choosing a meaning and saying so. Two rows, one job, and
the firmer source wins - so the repository was serving the weaker of the pair and refusing the
better. **Ask what the row this one duplicates is made of before refusing it as a duplicate.**

**Two of the three measures do not scale into verdicts, which a sweep of all 66 remaining
refusals established.** `RivalSpellings` reads, because its relations are exact: an anagram or a
prefix either way. The **tail-divergent twin** does not - it pairs a used name with any definition
sharing a long stem, and half of those pairs are family members rather than spellings of one field.
`RCPATTRVAL` against `RCPATTRID` pairs a value with an identifier. So a twin **marks a place to
look** and settles nothing, and a chapter that read it mechanically would reopen seven refusals it
should not. `SecsCatalogMeasureLimitTests` pins that, and the companion finding: the
**producer-and-reader** question does scale, and it explains a standing refusal that had been
argued from a count. No blocked transfer is implemented here because `BLKNUM`, `BLKEND` and
`BLKSIZE` are written by the end that is sending and read by the end that is receiving - the one
thing an opaque field cannot be. Stream 20's lock passes that test exactly as stream 15's does,
and is refused for what its stream claims instead, plus an `OWNERID` the host would mint.
Streams 19 and 20 have had that re-read and neither yielded a row, but **both stated reasons were
wrong and have been replaced** - which is the normal outcome and still worth the chapter, because a
refusal argued from the wrong place is the thing that gets re-derived.

**A refusal states a name, not a figure - and then somebody has to check the name.** Stream 19 was
refused as rootless, which was true of it and of six other streams, so the reason was restated as
one name: `RCPNS`, the recipe namespace, undefined, with no rival spelling the measure can see, and
addressing all ten of its pairs. Every word of that is still true and it is **still not the
reason**. The reference writes **six row names in both stream 15 and stream 19**, and in every one
of the six `RCPNS` stands exactly where the defined `RCPSPEC` stands - so the format is read off
the row, which is what stream 8 did for `CPBODY` off `PPBODY`'s slot, and item names never travel
anyway. What keeps stream 19 out is per row: **four of its ten are rows this repository already
answers** under stream 15's numbers, and the other six carry a checksum under a third spelling, a
state their row turns on, or a list whose nesting no served sibling settles. So re-reading a
refusal has three steps and not two: ask which name stops it, ask whether anything here already
does the job - and **ask what is in that name's slot in the row next door**.

**The same question emptied the measure's other half.** Stream 17's control-job four were filed on
`CJID`, undefined and addressing every control-job transaction, and `CTLJOBID` is the dictionary's
own control-job identifier - a defined `A[n]` that no row names. So the name was not the reason
there either. What refuses those rows is that they are not E94: the bodies are stream 16's with
`PJ` rewritten to `CJ`, S17F1 builds a control job from a `PJID` where E94 builds one from its
material, and the two control-job items the dictionary defines are named by no row at all. That
argument was in the Catalog README at length and the partition had no box for it, which is the
second time a stated rule with nowhere to be filed turned into a mis-filed row. `AddressingMissing`
is down to **one** row, S17F7, whose `DCID` has neither a definition nor a twin row to read a slot
off - and that is the whole of what the measure now claims.

**Stream 20 has been re-read twice and the second time moved two of its own reasons.** Its readable
rows are **three** and not two: S20F17 and S20F33 are item for item the delete and the namespace
create this repository serves, and **S20F15 is readable without being gap-free** - `NEWRCPSPEC` and
`NEWRCPID` are each a defined name with a word in front, sitting in the same body two positions
earlier, which is `RCPIDNEW`'s position exactly. The rival measure sees neither, because its
relations are anagram and prefix and these extend at the **front**: the `RSPACK` blindness again.
And the sentence that said *a rival makes a refusal firmer* is gone, because two chapters overturned
it for the identical cases - `RCPIDNEW` is served and `RCPVER` is carried untouched at S15F36. Of
stream 20's three rival-spelled gaps only `RCPSTATE` still stands, and for the other reason: its
twin `RCPSTAT` is a `U1[1]` with **no code table**, so the two readings differ and one is a number
with no meanings.

**What keeps stream 20 out is therefore one argument and not a mix.** S20F15 is the only one of the
three that is not a duplicate - a rename *and* a move, where stream 15's rename cannot leave its
namespace and its copy leaves the original behind - so a host does it as a copy and a delete. What
the single transaction adds is **atomicity**, and `RMACK` has one refusal code: a host told Rejected
cannot learn whether the source survived. **The advantage is unobservable on the wire**, which is
`ACKC7`'s missing mismatch code in another stream. So the verdict rests on the routing table alone -
three of seventeen primaries - and the gap count decides nothing either way: eighteen of stream 20's
thirty-four rows carry an undefined name against twenty-two of stream 15's fifty-four, so the stream
this repository serves has *more* gapped rows than the one it refuses.

**When every row of a stream carries the same items, the item measures go quiet and the row's name
is the evidence.** Stream 7 is where that happens: nine of its rows carry `PPID, PPBODY` and six are
served, seven of its primaries carry `PPID` alone and six are served. So "every item in this row is
defined" was true of all six of its refusals and discriminated none of them. What decides is
whether the name names an **action on the row's fields**: S7F43's does - cancel the command that
`PPID` and `RCMD` named - and it went in. S7F21 is the one row of the stream still out, and the
measure had to be sharpened for it, because its name has a verb too - **Send**. What it has no
second instance of is its **object**: of the reference's 524 row names, exactly two say
*Reference*, and they are this row and its own acknowledgement. The dictionary wrote a name for the
idea and no message carries it - `PDEREF`, defined as text, one of the 213 - so there is no sibling
row and no item anywhere to read the word off, where the matrix family had three rows to read its
verb off. So ask whether the name's **object** has a second instance, not only whether the name has
a verb.

**And a row answered by nothing but an acknowledgement has to name what it does**, because the
acknowledgement is the whole of what the host gets back. That is the rule a stale comparison had
been hiding: S7F21's refusal used to end "the same position S1F9 is in", and S1F9 has been served
since the chapter that read `TRACK` properly - its reply carries two items of data, so that row
*reports* where this one *acts*. Serving S7F21 would mean answering `ACKC7` 0 - done - to an
instruction this library cannot perform, where S9F5 says "I do not do that function" and is true.

**A family was also refused for what it has no rows for, and that one was wrong - because a verify
is a read.** The matrix pairs are a send, a load inquire and a verify with **no request and no
directory anywhere**, which was taken for "a matrix can be written and never read". But the refusal
had measured whether any row's *name* says Request or Directory; S7F15 says Verify, and a verify
answers the only question a host that already holds the bytes has. The plain and formatted halves
are served for exactly that row, at S7F9 and S7F27 - and a capability with no read-back at all has
been here since stream 10, where `IGemTerminal` has one method and nothing reads a terminal. So ask
what the family's rows *do* before concluding nothing can come back out of it.

**The second half of that refusal was true of a shared store and not of the rows.** A matrix kept
in the plain store would come back from S7F5 under its own `PPID` as a plain program, because the
rows are item for item the plain ones. Giving the matrix its own store, its own opt-in list and no
place in S7F17, S7F19 or S7F31 is what makes that not happen - the same separation the formatted
half has had since it was written, and the reason `GemEquipment` now takes six optional
capabilities. So **a duplicate body is a reason to separate the stores, not to refuse the rows**;
and once they are separate, S7F13 and S7F1 no longer do one job, which is what the firmer-source
tie-break had been applied to.

**And the item measures were not spent in that stream after all - the question was which item, not
whether it is defined.** S7F35's reply names `PPBODY` where the reference's other two directory
replies name `PPID` alone, and all three primaries are the same row three times: no items at all.
`PPBODY` is named by nine rows of stream 7 and seven are served, in every one of them carrying the
program - three unformatted rows carrying bytes and four formatted ones, where this library writes
`PPID`, `MDLN`, `SOFTREV` and a list of commands under that one recorded name. So the record speaks
at this row and is silent at its siblings, and the silence is evidence about them. The other
reading would have refused the row instead of shaping it: a formatted directory of identifiers
alone does the job S7F20 does, S7F20 is a definition where this is an illustration, and the firmer
source wins - so **a reading that makes a row redundant is a reading to doubt**, because the
reference wrote the row anyway. Being flagged representative is no veto, and this stream settled
that too: S7F33 was left out for exactly that reason, and the paragraph saying so sat in
`IGemFormattedProcessProgramStore`'s remarks for chapters after the row was served.

**"A routing table is a claim about the stream, not about the row" has been retired, and it held
twenty-four of the fifty-nine refusals** - every primary of streams 17 and 20 - on the ground that
declaring any one of them answers S9F5 for the rest and invites a host to keep asking. Reading those
twenty-four one at a time dissolved it, and two things killed it. It was true of every partially
served stream here: stream 15 answers S9F5 for thirteen of its twenty-seven pairs and nobody calls
that a claim. And it could not be stated without a ratio - one row of a stream served almost
entirely against one pair of seven - where **a refusal states a name**. Every one of the twenty-four
has a name now: five of stream 17's were filed on `CJID` and have since moved again, because that
name had a definition to be read against too; eight have their job done already by a
better-documented row here, six of them item for item under stream 15's numbers; the rest turn on a
name an item measure already decided. **Twenty of
the twenty-four carry an undefined name**, which the sentence "every field of these rows reads" had
asserted of all of them without once looking at an item. So before refusing a row for what its
stream would claim, ask whether the claim is also true of a stream you already serve.

**The row that measure was really carrying is S17F13, and its name is the acknowledgement.** The
S1F19 precedent is the same "it can say why" argument and it did let that row in - because S14F2,
the row S1F19 duplicates, carries **no acknowledgement item at all**, so `ERRCODE` and `ERRTEXT`
were the difference between silence and an answer. S2F24 carries one, and in the reading this
repository puts on a wire it carries six codes with five distinguishable refusals, not the two of a
dictionary entry the reference says it inferred from the item's name. The addition is therefore free
text over five named refusals. And the single row left on nothing else, S20F15, is refused because
what it adds - atomicity for a rename that also moves - cannot be seen on a wire at all, `RMACK`
having one refusal code. Two new boxes in the partition, both names: a firmer row does the job, and
the addition is unobservable.

**And where two rows do one job, the firmer source wins.** S17F9 carries S2F23's five items in
S2F23's order, and S2F23 is one of the reference's definitions where S17F9 is one of its
illustrations - the same tie-break that gave E39's half of stream 2 priority over stream 18's
re-addressed rows. "It duplicates something we serve" is not the end of the argument; which of the
two the reference documents better is. This was a rule here for chapters before the partition had a
box for it, which is how twenty-four rows ended up filed under a stream instead: **eight** of them
were this, and a measure the working agreement states and the record cannot file is a measure that
will be re-derived.

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
from the catalogue, which is fine while the siblings agree. S6F25 was the one row where they did
not - recorded exactly as S6F16 is - and it was refused on that, the first refusal here about a
*shape* instead of a field, on the ground that **a body guessed wrong is unparseable rather than
merely uninterpretable**.

**The way to break a shape tie is to ask why the sibling's body exceeds its record.** S6F16's does
because S6F15 is the Event Report *Request*: a reply to a request for a report has to carry one,
and S6F16 is the only row of the stream served carrying an `RPTID` its own record does not name.
Nothing asks a primary for anything, and no primary of the stream is served that way - and three
rows of stream 6 carry S6F25's record, the only secondary among them being the explained one, while
the primary among them is S6F3, served as exactly those two items since the chapter that measured
`DVNAME` and `DVVAL`. The second half is the item that would do the carrying: `RPTID` is named by
S6F11, by S6F13 and - in S6F25's own family - by S6F28, and by none of that family's other three
rows, which also kills the reading that would have given the row a list of identifiers. So
**compare a row with the sibling that plays the same part in its pair, and make the odd one explain
itself**; where nothing explains it the shape really is open. S6F25/F26 is served, and S7F35 one
chapter later, so **no refusal among the gap-free rows rests on a shape any more** - which is not
the same as none anywhere, and the first draft of this paragraph said the stronger thing. Stream 15
had three shape refusals of its own, all on rows that carry an item gap as well, and the current
recipe was one of them.

**A shape needs a size, which is what that chapter found.** S15F36 is a flat body of two items and
has one reading; stream 15's directory and revision history are **lists of entries**, and a list has
as many readings as there are nestings - which is why those two are still out while the current
recipe went in. The other half of the same finding: **a reply with no acknowledgement item says no
with a zero-length body**, and that is not an invention anywhere - S7F6, S7F26, S6F16, S6F20 and
S6F28 all do it, in three packages. A refusal that says "there is no way to read no" has to count
those first.

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
out, and replace the wrong ones rather than deleting them.

**That paragraph's own example went stale, which is the best argument for it there is.** It used to
end "the job half of stream 4 is still refused, on `TRACK`, and saying so is what stops the next
chapter re-deriving it." The next chapter but four asked what `TRACK` is and found an `A[n]` in the
dictionary: **untabled is not undefined.** All four remaining pairs of stream 4 went in, and stream
4 is now served whole. Three refusals, three wrong reasons, all three argued from the message rows
while the answer sat in the dictionary - so when a refusal names an item, look the item up before
believing it.

**And the half-right part of that refusal became a design rule.** `TRACK` has no values and
`ERRCODE` has none either, so S4F20 carries no tabled item and a host cannot read a verdict off it.
That is a reason to invent no verdict, not a reason to leave the row out: carrying a field nobody
defined is what this repository does for every own gap, and a row is refused for an uninterpretable
field only when that field is the one the message turns on - `ENABLE`, `NRED`, `CHKSUM`. What S4F19
turns on is its job and its link, and both are defined. So `ERRCODE` is a **reason, never a
verdict**: where a row carries a tabled item, read that and put `ERRCODE` beside it, as
`GemExceptionAbortResult` does with `ACKA`; where it carries none, hand all the fields back
untouched. `Barbatos.SecsGem.Objects` and `Barbatos.SecsGem.Material` both said so already, and the
first draft of the transfer job code contradicted them before a re-read of their own remarks caught
it.

**Check the item NAME before the table, because a true table can belong to another row.** This has
now happened twice and both times the comparison passed. GEM documents its trace acknowledgement
as `TRACK` where S2F24 carries `TIAACK`; and the spool reply was read as `RSPACK`, whose table is
real and whose row is S2F44, which nothing here serves - while S6F24 carries `SPACK`, undefined,
and this package's own two ends called the byte `RSDA` on the wire and `RSPACK` in the reader. One
byte, three names, and a green suite. So when a package says which item it is reading, ask
`i.Streams` on that name and check the answer contains the stream the row is in; where the row's
own item has no table, say which sibling the codes are grounded on - `ACKC6`, here, four rows of
the same stream with an identical table, which is why the correction moved a name and not a byte.

**Read that item's own note too, which is the finding of the audit the next paragraph is about.**
The reference hedges itself in prose the data file cannot carry: 151 of its 375 entries carry a note
saying the type, the table or the meaning is best-effort or unconfirmed, and 33 of those give a full
table of values anyway. The hedge is deliberately not imported - a per-item flag would make the
other 224 entries look verified - so the rule is to read the note at the source wherever a decision
rests on one entry. `TIAACK` is the case that paid for it. Its two codes were read here as a rival
reading of E5 that `Barbatos.SecsGem.Gem` overrules with E30's six; the note says the meaning was
*inferred from the item's name*, and inferred as "Track-In Acknowledge", where S2F24 is Trace
Initialize Acknowledge and the item's own `Streams` reads `[2]`. A placeholder, not a rival - which
makes the six codes the better reading rather than the local one, and is one more stated reason that
was right about the bytes and wrong about why.

**A whole-table audit earns its chapter even when it finds nothing, because what it leaves behind is
the check it had to do by hand.** All 524 rows and all 375 dictionary entries were compared against
the reference they came from - whether the row exists, which way it travels, primary or secondary,
which function answers which, the item list, and every type, length and coded value down to the
wording of each meaning. 520 rows and the whole dictionary agree, and the four that do not are the
S6F11 fault that was already corrected and pinned - for which the audit found a second witness that
does not depend on this repository's code: the reference's own dictionary puts `SMPLN` and `STIME`
in S6F1, so the reversed reading leaves two tabled definitions named by no row. The data being
right is what made the rest of it worth keeping: three of the ten invariants the audit computed were
guarded nowhere, the sharpest being that **a reply travels the opposite way from its primary**,
across all 250 pairs, where every direction assertion in the suite had named its own rows. Reversing
one reply fails exactly one test of 213 now and failed none at all before `SecsCatalogPairTests`. So
an invariant that holds over every row is not tested by assertions about some of them.

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

**And counting the reader sites is the only thing that finds an undriven one.** The matrix chapter
added two `ACKC7` readers and one `PPGNT` reader, counted what was there, and found **four readers
that had never been driven at all**: the plain verify, the formatted verify, the availability
question and the formatted load inquire. None of them was wrong - a comparison of enum against
table passes whether or not a call exists - so what was missing was the proof, and four chapters
had each added a reader without adding its line. The counts are in the Gem package's API reference
so a sweep can check them: `ACKC7` at seven sites, `PPGNT` at four, `CMDA` at four, `GRANT` at two.

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
reply's, which no drive had ever covered, and the matrix chapter then found four at once. The
counts live in that package's API reference so a sweep can check them, and in the paragraph that
records how those four were found.

**And `GemReservedAcknowledgementTests` could itself hang rather than fail**, which the stream 7
chapter found the way everything here gets found: a control made a host send the wrong function, the
hand-built machine had nothing to answer it, and the run died on a T3 the `FakeTimeProvider` never
advances - so the file built to catch an undriven reader reported nothing at all. Its `DriveAsync`
is bounded on the real clock now. That is the fourth place this trap has been found and the pattern
is always the same, so it is worth stating as a rule: **in a suite on a fake clock, every await of a
reply needs a real-clock bound**, because the failure mode of not having one is silence rather than
a red test. A control runner that counts "Failed: 0" must also notice a run with no summary line.

**There is a blanket version of that bound and it costs one token per test**: `[Fact(Timeout = n)]`.
xUnit v3 races the test against a delay and reports a real failure - verified here, not assumed -
and it also cancels `TestContext.Current.CancellationToken`, so it catches awaits inside the library
as well as the ones a test can see. The one cost is xUnit1069, which wants each such test to name
`TestContext.Current.CancellationToken` itself and cannot see that a pair helper already is it, so
the project suppresses the analyser in its `.csproj` with the reason written down.

**`Barbatos.SecsGem.Recipes.UnitTests` and `Barbatos.SecsGem.Gem.UnitTests` both carry it on every
test now**, the second because the **fifth** instance turned up there one chapter later and could
not be fixed per-await: a control that removed S1F9 from the equipment's routing table left the
host waiting on a T3, and the await was a plain `GemHost` call in a test rather than a pair helper.
**A per-await bound has to be added at every site, and that is why it keeps failing** - this trap
has never once been caught by the bound someone remembered to write. Put the attribute on a new
test project from the start, and on an old one the first time it bites. **And bound the control
runner's own `dotnet build` and test exe**, because a hung run makes the runner sit forever: mine
sat for fifty minutes before anybody looked.

**A wait on the harness's record of a message is not a wait on the handler's log**, and that is a
flake eight tests in four packages were carrying. The pair helpers enqueue `(stream, function)` in
the route *before* calling the end under test, so a test that waits for `Asked.Count > 0` and then
asserts a warning is asserting something the handler may not have written yet. The suite won that
race every time for months and lost it once under load, in a full 24-project sweep - which is the
only place it can be found, because each file passes on its own however often it is run. **Wait for
the thing the assertion reads.** Every one of those waits now has a second one after it, on the
log's own count.

**A log assertion matched on a short identifier can be satisfied by the temporary directory's own
name.** The durable stores log the identifier they could not read, and the shared record directory
logs the *path* beside it - and a directory named `barbatos-sp-<guid>` contains almost any digit you
care to look for. So `line.Contains("7")` passed with the store's own warning deleted, and only a
control found it. Match on the store's own words - `"service program 7"`, `"data set 42"` - and treat
any assertion a stray hex character could satisfy as no assertion at all.

**Rebuild after a control run before trusting the suite.** A runner restores the *sources* it
mutated and leaves the build output of its last control behind, and the catalogue's data file is
embedded in the assembly - so the first run after a control sweep can report a failure that is not
in the tree, and could as easily hide one that is. The S7F21 chapter spent a confused minute on
exactly that.

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