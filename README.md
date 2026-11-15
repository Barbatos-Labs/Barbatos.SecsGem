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

## Licence

MIT. See [LICENSE.md](LICENSE.md).