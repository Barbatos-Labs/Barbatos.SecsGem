# Barbatos.SecsGem.Serialization.Xml - API reference

Namespace `Barbatos.SecsGem.Serialization.Xml`, except where noted. Concepts and worked examples
live in [`README.md`](README.md); this page is member-by-member.

- [`XmlFormat`](#xmlformat)
- [`XmlFormatReader`](#xmlformatreader)
- [`XmlFormatWriter`](#xmlformatwriter)
- [`AddSecsXml`](#addsecsxml)

---

## `XmlFormat`

`public static class XmlFormat`

The element and attribute names, in one place.

| Member | Signature | Value |
| --- | --- | --- |
| `Descriptor` | `static SecsFormatDescriptor { get; }` | Id `xml`, extension `.xml`, media type `application/xml`. |
| `RootElement` | `const string` | `"SecsMessages"` |
| `MessageElement` | `const string` | `"SecsMessage"` |
| `NameAttribute` | `const string` | `"name"` |
| `StreamAttribute` | `const string` | `"s"` |
| `FunctionAttribute` | `const string` | `"f"` |
| `ReplyExpectedAttribute` | `const string` | `"w"` |
| `LabelAttribute` | `const string` | `"label"` |

There is no element name for an item, because the item's element name **is** its format - `<U4>`,
`<A>`, `<List>`. That is what makes an unknown element an error rather than something to skip.

Capabilities: `SecsFormatCapabilities.Lossless` - every capability the hub defines. A conversion
into XML never warns.

## `XmlFormatReader`

`public sealed class XmlFormatReader : ISecsFormatReader`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `ReadAllAsync` | `IAsyncEnumerable<SecsMessage> ReadAllAsync(PipeReader input, SecsReadOptions? options = null, CancellationToken cancellationToken = default)` | **The caller owns every message.** |

**Reader hardening**

The underlying `XmlReaderSettings` is fixed, with no option to change it:

| Setting | Value | Why |
| --- | --- | --- |
| `DtdProcessing` | `Prohibit` | A DTD is the way an XML external entity attack gets in, and a recipe file is exactly the kind of document that arrives from outside. |
| `XmlResolver` | `null` | Nothing is fetched from a file path or a URL while parsing. |
| `Async` | `true` | So the reader streams off the `PipeReader` rather than buffering. |

A document with a `<!DOCTYPE>` or an `<!ENTITY>` fails with `SecsFormatException`. Both cases have
a test.

## `XmlFormatWriter`

`public sealed class XmlFormatWriter : ISecsFormatWriter`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `WriteAsync` | `ValueTask WriteAsync(PipeWriter output, SecsMessage message, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | Writes a whole document, root element included. Flushes `output`, does not complete it. |
| `WriteAllAsync` | `ValueTask<int> WriteAllAsync(PipeWriter output, IAsyncEnumerable<SecsMessage> messages, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | One `<SecsMessages>` root over every message. Returns the count. |

Options that change the output: `Indent`, `IndentSize`, `NewLine` and `WriteItemLabels`.

## Hosting

Namespace `Microsoft.Extensions.DependencyInjection`.

### `AddSecsXml`

`public static IServiceCollection AddSecsXml(this IServiceCollection services)`

| Service | Lifetime | Implementation |
| --- | --- | --- |
| `ISecsFormatReader` | Singleton (enumerable) | `XmlFormatReader` |
| `ISecsFormatWriter` | Singleton (enumerable) | `XmlFormatWriter` |
