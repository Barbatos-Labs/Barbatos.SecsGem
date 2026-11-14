# Barbatos.SecsGem

A modern SECS-II / HSMS-SS / GEM stack for **.NET 8, 9 and 10** - allocation-conscious,
Native AOT friendly, and built around `System.IO.Pipelines`, `System.Threading.Channels`,
`Span<T>` and `ReadOnlySequence<byte>`.

Two things set it apart from what came before:

1. **A protocol layer that finishes the job.** Every HSMS timer is real and tested, T8
   included - scoped to the gap *between chunks of one message*, never to an idle link.
   The connection state machine is a single-threaded actor, so no transition can re-enter
   another. S9F1/F3/F5/F7/F9/F11/F13 are emitted automatically.
2. **Universal serialization.** One hub (`SecsMessage`/`SecsItem`), many spokes:
   SECS-II binary, `.sml`, `.json`, `.xml`, `.ini` and plain C# objects, each
   convertible to any other. Adding a format never touches `Barbatos.SecsGem.Core`.

## Packages

| Package | What it owns |
| --- | --- |
| `Barbatos.SecsGem.Core` | `SecsItem`/`SecsMessage` AST, binary codec, serialization contracts. No sockets. |
| `Barbatos.SecsGem.Hsms` | HSMS-SS state machine, dual-path pipe decoder, T3-T8, S9Fx, hosting and DI. |
| `Barbatos.SecsGem` | Metapackage: Core + Hsms + SML. |
| `Barbatos.SecsGem.Serialization.Sml` | `.sml` reader/writer. |
| `Barbatos.SecsGem.Serialization.Json` | `.json` reader/writer on `Utf8JsonReader`. |
| `Barbatos.SecsGem.Serialization.Xml` | `.xml` reader/writer. |
| `Barbatos.SecsGem.Serialization.Ini` | `.ini` reader/writer, path-addressed keys. |
| `Barbatos.SecsGem.Mapper` | `SecsSerializer`: C# objects <-> `SecsItem`, convention over configuration. |
| `Barbatos.SecsGem.SourceGenerators` | Roslyn component behind the mapper's exact property order. |
| `Barbatos.SecsGem.Gem` | GEM (E30) layer. Phase 7. |

## Quick start

```csharp
// The engine and its transport. Binds "Barbatos:SecsGem" and validates on start, so a T8
// longer than T3 fails the host rather than the first stalled message.
services.AddSecsGem(builder.Configuration).UseTcp();

// The format spokes and the object mapper. Each lives in its own package and extends
// IServiceCollection rather than the builder - which is exactly why adding a format never
// needs a change to Barbatos.SecsGem.Core.
services.AddSecsSml().AddSecsJson().AddSecsXml().AddSecsIni().AddSecsMapper();
```

Converting files needs none of the protocol stack:

```csharp
services.AddSecsSerialization().AddSecsSml().AddSecsJson();

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

See the runnable end-to-end demo:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Transcode
```

## Roadmap

| Phase | Scope |
| --- | --- |
| 0 | Repository, build infrastructure, 22 projects. |
| 1 | Core AST, serialization contracts + `SecsTranscoder`, `SecsSerializer`, `AddSecsGem`. |
| 2 | Binary codec, fast/chunked decoder, chunk-boundary tests. |
| 3 | HSMS state machine, T3-T8, S9Fx, loopback integration tests. |
| 4 | Four format spokes, 25-pair round-trip matrix. |
| 5 | Source generator + analyzer BSG001. |
| 6 | Benchmarks, samples, per-package documentation. |
| 7 | GEM layer. |

## Licence

MIT. See [LICENSE.md](LICENSE.md).