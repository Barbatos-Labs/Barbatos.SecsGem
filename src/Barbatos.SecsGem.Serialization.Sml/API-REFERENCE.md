# Barbatos.SecsGem.Serialization.Sml - API reference

Namespace `Barbatos.SecsGem.Serialization.Sml`, except where noted. Concepts and worked examples
live in [`README.md`](README.md); this page is member-by-member.

- [`SmlConvert`](#smlconvert)
- [`SmlFormat`](#smlformat)
- [`SmlFormatReader`](#smlformatreader)
- [`SmlFormatWriter`](#smlformatwriter)
- [`AddSecsSml`](#addsecssml)

---

## `SmlConvert`

`public static class SmlConvert`

The convenience layer, for a console tool or a test. It allocates strings, which is why nothing
inside the stack uses it.

| Member | Signature | Notes |
| --- | --- | --- |
| `ToSml` | `static string ToSml(this SecsMessage message, SecsWriteOptions? options = null)` | The whole message, terminator included. |
| `ToSml` | `static string ToSml(this SecsItem item, SecsWriteOptions? options = null)` | The item tree alone, with no header and no terminator. |
| `ParseSml` | `static SecsMessage ParseSml(string sml, SecsReadOptions? options = null)` | The first message. **The caller owns it.** |
| `ParseSmlMessages` | `static List<SecsMessage> ParseSmlMessages(string sml, SecsReadOptions? options = null)` | Every message. **The caller owns each one.** |
| `ParseSmlItem` | `static SecsItem ParseSmlItem(string sml, SecsReadOptions? options = null)` | One item tree with no header. **The caller owns it.** |

All three parse methods throw `SecsFormatException` with a line number on malformed input.

## `SmlFormat`

`public static class SmlFormat`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `static SecsFormatDescriptor { get; }` | Id `sml`, extension `.sml`, media type `text/plain`. |
| `MessageTerminator` | `const string MessageTerminator = "."` | A line holding just this ends a message, which is what lets a file hold several. |

Capabilities: `SecsFormatCapabilities.Lossless` - `MultipleMessages`, `MessageNames`, `ItemLabels`,
`Comments`, `ExactIntegers`, `BinaryPayload` and `Indentation`. A conversion into SML never warns.

## `SmlFormatReader`

`public sealed class SmlFormatReader : ISecsFormatReader`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `ReadAllAsync` | `IAsyncEnumerable<SecsMessage> ReadAllAsync(PipeReader input, SecsReadOptions? options = null, CancellationToken cancellationToken = default)` | Lazy, line by line. **The caller owns every message.** |
| `Parse` | `static List<SecsMessage> Parse(string sml, SecsReadOptions? options = null)` | Synchronous, for a string already in hand. |

`SecsReadOptions.Strict` is what decides whether a declared count that disagrees with the contents
is an error or a shrug. It defaults to an error.

`SecsReadOptions.ReadItemLabels` controls whether trailing `//` comments are attached to items as
labels or discarded.

## `SmlFormatWriter`

`public sealed class SmlFormatWriter : ISecsFormatWriter`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `WriteAsync` | `ValueTask WriteAsync(PipeWriter output, SecsMessage message, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | Flushes `output`, does not complete it. |
| `WriteAllAsync` | `ValueTask<int> WriteAllAsync(PipeWriter output, IAsyncEnumerable<SecsMessage> messages, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | Returns the count. |
| `Write` | `static void Write(IBufferWriter<byte> output, SecsMessage message, SecsWriteOptions? options = null)` | Synchronous, into any buffer writer. No `string` anywhere. |

Options that change the output: `Indent`, `IndentSize`, `NewLine`, `WriteItemLabels` and
`BinaryBytesPerLine`.

## Hosting

Namespace `Microsoft.Extensions.DependencyInjection`.

### `AddSecsSml`

`public static IServiceCollection AddSecsSml(this IServiceCollection services)`

Adds `SmlFormatReader` and `SmlFormatWriter` to the reader and writer collections, and the hub
they plug into, so one call is enough to resolve a `SecsTranscoder`. The registry itself is built
from `IEnumerable<ISecsFormatReader>` whenever it is resolved, which is why a spoke only ever has
to add itself to the collection.

| Service | Lifetime | Implementation |
| --- | --- | --- |
| `ISecsFormatReader` | Singleton (enumerable) | `SmlFormatReader` |
| `ISecsFormatWriter` | Singleton (enumerable) | `SmlFormatWriter` |
| `SecsCodecOptions` | Singleton | `SecsCodecOptions.Default` |
| `ISecsFormatReader` | Singleton (enumerable) | `Secs2BinaryFormatReader` |
| `ISecsFormatWriter` | Singleton (enumerable) | `Secs2BinaryFormatWriter` |
| `ISecsFormatRegistry` | Singleton | `SecsFormatRegistry` over every registered reader and writer |
| `SecsTranscoder` | Singleton | |

Every row goes in through `TryAdd`, so calling this twice - or beside another `AddSecsXxx()` -
builds one hub and not two, and anything already registered wins. The last five rows *are* the
hub: those types live in `Barbatos.SecsGem.Core`, which is not allowed a dependency on a
container, so the packages that already need one are the packages that register them.
