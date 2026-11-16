# Barbatos.SecsGem

A modern SECS-II / HSMS-SS / GEM stack for **.NET 8, 9 and 10** - allocation-conscious,
Native AOT friendly, and built around `System.IO.Pipelines`, `System.Threading.Channels`,
`Span<T>` and `ReadOnlySequence<byte>`.

Two things set it apart from what came before:

1. **A protocol layer that finishes the job.** Every HSMS timer is real and tested, T8 included -
   scoped the way SEMI E37 scopes it, to the gap *between bytes of one message*, never to an idle
   link and never to a peer that is merely slow. That is the timer secs4net documents and never
   wires up; its `StartT8Timer` has no callers at all. The connection state machine is a
   single-threaded actor, so no transition can re-enter another - which is exactly what secs4net
   does when a timer callback calls `Disconnect()` and then `Start()`. S9F1/F3/F5/F7/F9/F11 are
   emitted automatically, driven by the routing table rather than by guesswork.
2. **Universal serialization.** One hub (`SecsMessage`/`SecsItem`), many spokes:
   SECS-II binary, `.sml`, `.json`, `.xml`, `.ini` and plain C# objects, each
   convertible to any other. All twenty-five ordered pairs are tested, and every conversion
   reports what the target format could not carry rather than losing it quietly. Adding a format
   never touches `Barbatos.SecsGem.Core`.

## Packages

| Package | What it owns |
| --- | --- |
| `Barbatos.SecsGem.Core` | `SecsItem`/`SecsMessage` AST, binary codec, serialization contracts. No sockets. |
| `Barbatos.SecsGem.Hsms` | HSMS-SS state machine, dual-path pipe codec, T3-T8, automatic S9Fx, hosting and DI. |
| `Barbatos.SecsGem` | Metapackage: Core + Hsms + SML. |
| `Barbatos.SecsGem.Serialization.Sml` | `.sml` reader/writer. |
| `Barbatos.SecsGem.Serialization.Json` | `.json` reader/writer on `Utf8JsonReader`. |
| `Barbatos.SecsGem.Serialization.Xml` | `.xml` reader/writer. |
| `Barbatos.SecsGem.Serialization.Ini` | `.ini` reader/writer, path-addressed keys. |
| `Barbatos.SecsGem.Catalog` | Reference data: 524 stream/function pairs, 375 data items with formats and code values. Optional. |
| `Barbatos.SecsGem.Mapper` | `SecsSerializer`: C# objects <-> `SecsItem`, convention over configuration. |
| `Barbatos.SecsGem.SourceGenerators` | Roslyn generator and analyzer BSG001 behind the mapper's exact property order. Optional. |
| `Barbatos.SecsGem.Gem` | GEM (E30), both ends: the equipment's state models, host-configured reporting, alarms, remote commands, process programs, spooling - and `GemHost`, which drives all of it from the other side. |
| `Barbatos.SecsGem.WaferMap` | Wafer mapping (E5 stream 12), both ends: map setup, the three map data formats, the transmit handshake, maps in either direction. Depends on Hsms, not on Gem. Optional. |
| `Barbatos.SecsGem.Material` | Material status (E5 stream 3), both ends - and here both ends send: what the machine holds, time to completion, found and lost, location and transfer, status changes and movements. The carrier half of the stream is E87's and is not here. Optional. |
| `Barbatos.SecsGem.Objects` | Object services (E5 stream 14): `GetAttr` and `SetAttr` for one object or many, the three namelists, and the actionable half - create, delete, act. The protocol only; the objects are your application's, behind one interface, because they belong to E87, E90 and E94. Optional. |
| `Barbatos.SecsGem.Carriers` | Carrier management (**SEMI E87**): the carrier and load port objects, the seven state machines, and the actions that drive them. States are enforced, not described - a refusal names the state that refused it. Attributes, the slot map among them, ride on stream 14. Optional. |
| `Barbatos.SecsGem.Substrates` | Substrate tracking (**SEMI E90**): where each wafer is and what has happened to it, enforced. E90 has no messages of its own, so a substrate is an object and stream 14 is its whole wire presence. Composes with E87. Optional. |
| `Barbatos.SecsGem.ControlJobs` | Control jobs (**SEMI E94**): the host's instruction to run some material - select, start, pause, abort - with every transition enforced and one job running at a time. E94 has no messages either: a job is created with S14F5 and commanded with S14F9. Composes with E87 and E90. Optional. |
| `Barbatos.SecsGem.ProcessJobs` | Processing management (**SEMI E40**): one piece of work - this material, this recipe, once - its nine-state machine, and **stream 16** at both ends: create, command, status, list, material, parameters, queue. The states and their numbers are the catalogue's own. Composes with E87, E90 and E94. Optional. |

## Quick start

```csharp
// The engine and its transport. Binds "Barbatos:SecsGem" and validates on start, so a T8
// longer than T3 fails the host rather than the first stalled message. The routing table is
// also the declaration of what this entity understands, which is what lets the connection
// answer S9F5 for anything else without being told to.
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .OnPrimary(1, 1, async (transaction, token) =>
    {
        using var reply = new SecsMessage(1, 2, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A("BRBTOS", 6), SecsItem.A("0.1.0")),
        };

        await transaction.ReplyAsync(reply, token);   // the system bytes are not yours to copy
    })
    .RunAsHostedService();

// The format spokes and the object mapper. Each lives in its own package and extends
// IServiceCollection rather than the builder - which is exactly why adding a format never
// needs a change to Barbatos.SecsGem.Core.
services.AddSecsSml().AddSecsJson().AddSecsXml().AddSecsIni().AddSecsMapper();
```

`services` there belongs to a host builder, and `Microsoft.Extensions.Hosting` brings the container
with it. A console tool that builds its own `ServiceCollection` references
`Microsoft.Extensions.DependencyInjection` itself: every package here depends on the DI
*abstractions* and never on a container - and `Barbatos.SecsGem.Core` depends on neither.

Converting files needs none of the protocol stack:

```csharp
services.AddSecsSml().AddSecsJson();

var transcoder = provider.GetRequiredService<SecsTranscoder>();
var report = await transcoder.TranscodeFileAsync("recipe.sml", "recipe.json");
Console.WriteLine(report);   // sml -> json: 1 message(s), 2 warning(s)
```

Mapping an object needs no attributes:

```csharp
public sealed record Person(string Name, int Age, string[] Emails);

using var item = SecsSerializer.Serialize(new Person("Hung", 36, ["a@x.com"]));
// <L [3] <A [4] 'Hung'> <I4 [1] 36> <L [1] <A [7] 'a@x.com'> > >
```

Add `Barbatos.SecsGem.SourceGenerators` and that same mapping is read from the syntax tree at
compile time instead of from reflection - exact declaration order, direct delegates, trim-safe.
No attribute, no partial class, no context type, and not one line of the code above changes.

## Build

```
dotnet build Barbatos.SecsGem.slnx
dotnet test  Barbatos.SecsGem.slnx
dotnet pack  Barbatos.SecsGem.slnx -c Release -o artifacts
```

Do not pass `-f` at the solution level: it forces that framework onto every project, including
the `netstandard2.0` Roslyn component, which then fails with `NETSDK1005`. To narrow to one
runtime, pass `-f` to a single project:

```
dotnet test tests/Barbatos.SecsGem.Core.UnitTests -f net10.0
```

Runnable demos. A host and a piece of equipment, both running their own state machine, selecting
each other and completing a GEM opening sequence in one process with no port:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host
```

The same two over a real socket, in two terminals:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Equipment
```

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Host -- --tcp
```

Format conversion and object mapping, end to end:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Transcode
```

The benchmarks, which are where the allocation claims above are kept honest - read the allocation
column before the timings, and see [benchmarks/README.md](benchmarks/README.md) for the invariants
that are supposed to hold on any machine:

```
dotnet run -c Release --project benchmarks/Barbatos.SecsGem.Benchmarks -- --filter "*"
```

## Roadmap

| Phase | Scope | Status |
| --- | --- | --- |
| 0 | Repository, build infrastructure, 22 projects. | Done |
| 1 | Core AST, serialization contracts + `SecsTranscoder`, `SecsSerializer`, `AddSecsGem`, and all four format spokes - which ran ahead of schedule. | Done |
| 2 | HSMS frame codec: two decode paths, T8 on `TimeProvider`, chunk-boundary and framing tests. | Done |
| 3 | Connection state machine, transaction manager, T3/T5/T6/T7, automatic S9Fx, loopback integration tests. | Done |
| 4 | The 25-pair format round-trip matrix, committed conformance fixtures, per-spoke reader tests. | Done |
| 5 | Source generator, analyzer BSG001, generated-against-reflection parity tests. | Done |
| 6 | BenchmarkDotNet suite, and a README plus an API reference for every shipping package. The benchmarks found a missing contract cache that made the source generator slower than the reflection it replaces. | Done |
| 7 | GEM (E30) equipment layer: both state models as actors, the standard message set, host-configured report definitions, alarms, remote commands gated on the control state. Brought `DeselectAsync` and container-resolved message handlers back into the HSMS package. | Done |
| 8 | The deferred E30 messages - namelists, alarm listings, host-requested reports, and opt-in process program management (stream 7) - plus `Barbatos.SecsGem.Catalog`, the 524-message reference table. | Done |
| 9 | Spooling: holding reports while the host is away, and handing them over - or throwing them away - when it asks with S6F23. | Done |
| 10 | A consumer that restores the packed `.nupkg` files rather than the projects, on all three runtimes and both CI legs. The first check of `lib/` layout, declared dependencies, embedded resources, and whether the compiler loads the generator out of `analyzers/dotnet/cs`. | Done |
| 11 | Formatted process programs (S7F23-S7F26): a recipe as the structure E5 defines rather than a block of bytes, refused rather than reshaped when it carries something this stack cannot represent. Completes the stream 7 that E30 asks for - what is left of the stream is outside the GEM capability and is flagged representative in the catalogue. | Done |
| 12 | A spool that survives the equipment's own restart: one file per held message, committed with an atomic rename, written through to the disk. The equipment adopts what it finds, reports `SpoolStartTime` from the oldest message rather than from the restart, and stays up when the filesystem does not. | Done |
| 13 | `GemHost`: the host side of every primary the equipment answers, with a test that asserts "every" rather than a comment claiming it. Acknowledgement codes come back as enums, replies E5 pins down come back decoded, and the host sample now drives the conversation through it instead of hand-building bodies. | Done |
| 14 | Terminal services (stream 10), both ends: text in front of the operator and text back from them, behind a one-method `IGemTerminal`. Three acknowledgement codes a host can act on differently, and S10F1 deliberately not spooled. | Done |
| 15 | Trace data collection (S2F23/S6F1), both ends: variables sampled on the equipment's own loop, reported in groups, every sample carrying the value at its own moment. It also corrected the catalogue - S6F1 and S6F2 carried S6F11's name and items - and added the invariant that found it. | Done |
| 16 | Limits monitoring (S2F45-S2F48), both ends, which completes E30's data collection: a band per variable, a crossing reported as a collection event, and a deadband rather than a threshold so a wobble is silent. Three of its four message rows are representative in the catalogue, and the implementation says where to check. | Done |
| 17 | Wafer mapping, stream 12, as its own package: all nineteen functions at both ends, the three map data formats, and the directions inverted - here the *equipment* sends every odd function and the host answers. E5 rather than E30, so it depends on Hsms and not on Gem, and a prober that maps wafers without implementing GEM can use it alone. The one thing a flat item list cannot pin down is nesting, so the four repeating bodies are written one way and read three. | Done |
| 18 | Material status, stream 3, as its own package - the first stream here where *both* ends send primaries: the host asks five questions and the equipment reports four things unasked. It also comes with a warning the other chapters did not need. Not one of stream 3's thirty-six catalogue rows is a definition rather than a representative subset, and five of its item names have no dictionary entry at all, so two fields are inferred outright and the package says which. S3F17-S3F34, the carrier half, is E87's and is left out rather than guessed at. | Done |
| 19 | Object services, stream 14: `GetAttr` and `SetAttr` for one object or many, and the three namelists - one class, because E5 lets either end ask and either answer, and a pair of them can each ask the other. The package ships the protocol and no object types at all: those belong to E87, E90 and E94, so they live behind an interface the application implements. `ATTRDATA` has no format of its own, so an attribute value is a whole item, and the two ownership rules for one are written down where they can be read. One of the stream's twenty-eight rows is a definition; the implemented subset touches no undefined item. | Done |
| 20 | Carrier management, SEMI E87 - the first standard here that is behaviour rather than a message set. The carrier and load port objects, their seven state machines, and the actions that drive them over S3F17, with every transition enforced and every refusal naming the state that refused it. It also resolves what stream 3 could not: S3F20, S3F22, S3F28 and S3F32 need items the catalogue never defines, and E87 puts that content in object attributes on stream 14, which the previous chapter built. The caveat is sharper than elsewhere and the package says so - E87 is not in the catalogue, so these transitions are checked by tests rather than against reference data. | Done |
| 21 | Substrate tracking, SEMI E90: where each wafer is, what has happened to it, and which transitions may follow which - enforced, with the transport state derived from the geography rather than asked for twice. E90 has no message set at all, so a substrate is an object and the stream 14 of chapter 19 is its whole wire presence; the catalogue has no mention of a substrate, which makes this the thinnest provenance in the repository and the package says so. Building it also found two real faults in the previous chapters: two packages each registering an object provider silently kept one and dropped the other, and declaring stream 14 twice threw. Providers now compose. | Done |
| 22 | Control jobs, SEMI E94 - the host's instruction to run some material, and the standard that completes the material-flow set. One job at a time, cancel only before it has run and abort or stop only after, one terminal state with the reason beside it, and every refusal naming the state that refused it. E94 has no messages either, so this chapter had to build the *actionable* half of stream 14 first - S14F5 create, S14F7 delete, S14F9 and S14F27 act - which is the half E87 and E90 never needed, and E94 is the standard that asked for it rather than completeness. Three packages now answer one stream for five object types, which is also the first real check that composition was not fixed for exactly two. | Done |
| 23 | Processing management, SEMI E40, and the first standard in this group with a message set of its own: stream 16's fourteen transactions at both ends - create, command, status, list, deselect, event report, additional material, parameter change, queue position, reorder, multi-command, material binding, parameter report, queue status. Nine states, and this time the names and the numbers are quoted rather than read: the catalogue defines `PRSTATE` as `U1` with ten code values, and the gap at 5 in the enum is the one it reserves. Most of a job's attributes are defined too, which makes this the first provider in the stack whose whole vocabulary is the dictionary's. What is not defined is any of stream 16's twenty-eight rows - none is a definition - so five item names were inferred, each from a defined item doing the same job. Measuring that also turned up a mistake in chapter 22, fixed in its own commit first: E94's commands are `CTLJOBCMD`'s six spellings, not PascalCase. | Done |

## Licence

MIT. See [LICENSE.md](LICENSE.md).