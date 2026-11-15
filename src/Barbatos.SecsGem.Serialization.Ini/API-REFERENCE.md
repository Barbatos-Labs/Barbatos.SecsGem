# Barbatos.SecsGem.Serialization.Ini - API reference

Namespace `Barbatos.SecsGem.Serialization.Ini`, except where noted. Concepts and worked examples
live in [`README.md`](README.md); this page is member-by-member.

- [`IniFormat`](#iniformat)
- [`IniFormatReader`](#iniformatreader)
- [`IniFormatWriter`](#iniformatwriter)
- [`AddSecsIni`](#addsecsini)

---

## `IniFormat`

`public static class IniFormat`

The key names and the path grammar, in one place.

| Member | Signature | Value |
| --- | --- | --- |
| `Descriptor` | `static SecsFormatDescriptor { get; }` | Id `ini`, extension `.ini`, media type `text/plain`. |
| `NameKey` | `const string` | `"name"` |
| `StreamKey` | `const string` | `"stream"` |
| `FunctionKey` | `const string` | `"function"` |
| `ReplyExpectedKey` | `const string` | `"replyExpected"` |
| `ItemPrefix` | `const string` | `"item."` |
| `LabelSuffix` | `const string` | `".$label"` |
| `RootPath` | `const string` | `"1"` |

Capabilities: `MultipleMessages | MessageNames | ItemLabels | Comments | ExactIntegers |
BinaryPayload`. Everything except `Indentation`, which a path-addressed format has no use for and
which has no warning code.

**The path grammar**

| Key | Means |
| --- | --- |
| `item.1` | The root item. |
| `item.1.$label` | The root item's label. |
| `item.1.3` | The third child of the root. |
| `item.1.3.2.1` | The first child of the second child of the third child of the root. |

The value is the item's format, its count and its payload: `U4[1] 7001`, `L[3]`, `A[8] 'PRESSURE'`.
A list's value carries only the format and the count, because its contents are other keys.

Section names are message numbers, so `[1]`, `[2]` and so on are the messages of the document.

## `IniFormatReader`

`public sealed class IniFormatReader : ISecsFormatReader`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `ReadAllAsync` | `IAsyncEnumerable<SecsMessage> ReadAllAsync(PipeReader input, SecsReadOptions? options = null, CancellationToken cancellationToken = default)` | **The caller owns every message.** |
| `Parse` | `static List<SecsMessage> Parse(string ini, SecsReadOptions? options = null)` | Synchronous, for a string already in hand. |

**What it tolerates**

| Input | Result |
| --- | --- |
| Keys in any order | The tree is built from the paths, not the line order. |
| `;` or `#` at the start of a line | A comment. |
| Blank lines, whitespace around `=` | Ignored. |
| `item.1 = A[5] 'a=b'` | A quoted value may contain `=` and the path delimiter. |
| Several `[n]` sections | Several messages. |

Order independence is the property that matters: it is what makes an INI capture safe to merge,
and it has a test of its own.

## `IniFormatWriter`

`public sealed class IniFormatWriter : ISecsFormatWriter`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `WriteAsync` | `ValueTask WriteAsync(PipeWriter output, SecsMessage message, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | One section, numbered `1`. Flushes `output`, does not complete it. |
| `WriteAllAsync` | `ValueTask<int> WriteAllAsync(PipeWriter output, IAsyncEnumerable<SecsMessage> messages, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | One section per message, numbered from `1`. Returns the count. |
| `Write` | `static void Write(IBufferWriter<byte> output, SecsMessage message, int section = 1, SecsWriteOptions? options = null)` | Synchronous, and `section` lets a caller place the message in a document it is assembling itself. |

Options that change the output: `NewLine`, `WriteItemLabels` and `BinaryBytesPerLine`. `Indent` and
`IndentSize` are ignored - there is nothing to indent.

## Hosting

Namespace `Microsoft.Extensions.DependencyInjection`.

### `AddSecsIni`

`public static IServiceCollection AddSecsIni(this IServiceCollection services)`

| Service | Lifetime | Implementation |
| --- | --- | --- |
| `ISecsFormatReader` | Singleton (enumerable) | `IniFormatReader` |
| `ISecsFormatWriter` | Singleton (enumerable) | `IniFormatWriter` |
| `SecsCodecOptions` | Singleton | `SecsCodecOptions.Default` |
| `ISecsFormatReader` | Singleton (enumerable) | `Secs2BinaryFormatReader` |
| `ISecsFormatWriter` | Singleton (enumerable) | `Secs2BinaryFormatWriter` |
| `ISecsFormatRegistry` | Singleton | `SecsFormatRegistry` over every registered reader and writer |
| `SecsTranscoder` | Singleton | |

Every row goes in through `TryAdd`, so calling this twice - or beside another `AddSecsXxx()` -
builds one hub and not two, and anything already registered wins. The last five rows *are* the
hub: those types live in `Barbatos.SecsGem.Core`, which is not allowed a dependency on a
container, so the packages that already need one are the packages that register them.
