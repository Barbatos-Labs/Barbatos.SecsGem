# Barbatos.SecsGem.Serialization.Json - API reference

Namespace `Barbatos.SecsGem.Serialization.Json`, except where noted. Concepts and worked examples
live in [`README.md`](README.md); this page is member-by-member.

- [`JsonFormat`](#jsonformat)
- [`JsonFormatReader`](#jsonformatreader)
- [`JsonFormatWriter`](#jsonformatwriter)
- [`AddSecsJson`](#addsecsjson)

---

## `JsonFormat`

`public static class JsonFormat`

The property names, in one place, so a reader and a writer cannot drift apart.

| Member | Signature | Value |
| --- | --- | --- |
| `Descriptor` | `static SecsFormatDescriptor { get; }` | Id `json`, extension `.json`, media type `application/json`. |
| `NameProperty` | `const string` | `"name"` |
| `StreamProperty` | `const string` | `"s"` |
| `FunctionProperty` | `const string` | `"f"` |
| `ReplyExpectedProperty` | `const string` | `"w"` |
| `ItemProperty` | `const string` | `"item"` |
| `LabelProperty` | `const string` | `"$label"` |

Capabilities: `MultipleMessages | MessageNames | ItemLabels | BinaryPayload | Indentation`. Not
`Comments` - JSON has no comment syntax - and not `ExactIntegers`, which is a statement about what
other JSON consumers do with a 64-bit number rather than about this reader.

`$label` is prefixed because a format name is a property name here, and an item object therefore has
exactly one property that is not metadata. The `$` keeps the two namespaces from colliding if E5
ever gains a format called `label`.

## `JsonFormatReader`

`public sealed class JsonFormatReader : ISecsFormatReader`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `ReadAllAsync` | `IAsyncEnumerable<SecsMessage> ReadAllAsync(PipeReader input, SecsReadOptions? options = null, CancellationToken cancellationToken = default)` | **The caller owns every message.** |
| `Parse` | `static List<SecsMessage> Parse(ReadOnlySpan<byte> utf8Json, SecsReadOptions? options = null)` | The allocation-free entry point: no `string` is created. |
| `Parse` | `static List<SecsMessage> Parse(string json, SecsReadOptions? options = null)` | Convenience. |

**What it accepts**

| Input | Result |
| --- | --- |
| `[ { ... }, { ... } ]` | Every message. |
| `{ ... }` | One message - a bare object is accepted, not just an array. |
| `{ "U4": [1] }` | A numeric item. The array is required even for one element. |
| `{ "U4": 1 }` | `SecsFormatException`. |
| `{ "A": "PRESSURE" }` | A text item from a bare string. |
| `{ "A": ["PRESSURE"] }` | The same text item. |
| `{ "List": [] }` | An empty list. |

## `JsonFormatWriter`

`public sealed class JsonFormatWriter : ISecsFormatWriter`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `WriteAsync` | `ValueTask WriteAsync(PipeWriter output, SecsMessage message, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | Flushes `output`, does not complete it. |
| `WriteAllAsync` | `ValueTask<int> WriteAllAsync(PipeWriter output, IAsyncEnumerable<SecsMessage> messages, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | Wraps the messages in an array. |
| `WriteMessage` | `static void WriteMessage(Utf8JsonWriter writer, SecsMessage message, SecsWriteOptions? options = null)` | For embedding a message in a larger document you are already writing. |
| `WriteItem` | `static void WriteItem(Utf8JsonWriter writer, SecsItem item, SecsWriteOptions? options = null)` | The item tree alone. |

The two static methods take a `Utf8JsonWriter` the caller owns, so a SECS message can be one
property of some larger payload without this package deciding anything about the rest of it.

**Options that change the output**

| Option | Effect |
| --- | --- |
| `Indent`, `IndentSize` | Pretty-printed or compact. |
| `WriteItemLabels` | Whether `$label` is emitted. |
| `WriteLargeIntegersAsStrings` | `true` (default) writes `U8` and `I8` as quoted text so a JavaScript consumer cannot round them; `false` writes bare numbers. Either way this package reads them back exactly. |

## Hosting

Namespace `Microsoft.Extensions.DependencyInjection`.

### `AddSecsJson`

`public static IServiceCollection AddSecsJson(this IServiceCollection services)`

| Service | Lifetime | Implementation |
| --- | --- | --- |
| `ISecsFormatReader` | Singleton (enumerable) | `JsonFormatReader` |
| `ISecsFormatWriter` | Singleton (enumerable) | `JsonFormatWriter` |
