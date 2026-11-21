# Barbatos.SecsGem - working agreement

A family of NuGet packages implementing SECS-II (SEMI E5), HSMS-SS (E37) and GEM (E30) for
.NET 8/9/10. Everything here is a **library**: the public API is the product, and a careless
signature is a breaking change in somebody's fab.

## Repository map

```
src/          twenty-six shipping packages, one folder each (+ README.md, API-REFERENCE.md)
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
must then agree with. `IsEntirelyRepresentative` is true for nine streams
and two of them are implemented here, so it is not a veto: check the stream against the item
dictionary and against its standard's subject matter, and record which check it passed. Stream 17
is the one that failed both.

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