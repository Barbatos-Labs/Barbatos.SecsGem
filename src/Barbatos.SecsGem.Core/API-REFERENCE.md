# Barbatos.SecsGem.Core - API reference

Namespace `Barbatos.SecsGem`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [Items](#items)
  - [`SecsItem`](#secsitem)
  - [`SecsFormat`](#secsformat)
  - [`SecsFormatExtensions`](#secsformatextensions)
- [Messages](#messages)
  - [`SecsMessage`](#secsmessage)
  - [`MessageHeader`](#messageheader)
  - [`MessageType`](#messagetype)
  - [`SelectStatus`](#selectstatus)
  - [`DeselectStatus`](#deselectstatus)
  - [`RejectReason`](#rejectreason)
- [Codec](#codec)
  - [`SecsCodecOptions`](#secscodecoptions)
- [Exceptions](#exceptions)
  - [`SecsException`](#secsexception)
  - [`SecsDecodeException`](#secsdecodeexception)
  - [`SecsTimeoutException`](#secstimeoutexception)
  - [`SecsTimer`](#secstimer)
- [Serialization contracts](#serialization-contracts)
  - [`ISecsFormatReader`](#isecsformatreader)
  - [`ISecsFormatWriter`](#isecsformatwriter)
  - [`SecsFormatDescriptor`](#secsformatdescriptor)
  - [`SecsFormatCapabilities`](#secsformatcapabilities)
  - [`SecsFormatIds`](#secsformatids)
  - [`SecsFormatException`](#secsformatexception)
- [The registry and the transcoder](#the-registry-and-the-transcoder)
  - [`ISecsFormatRegistry`](#isecsformatregistry)
  - [`SecsFormatRegistry`](#secsformatregistry)
  - [`SecsTranscoder`](#secstranscoder)
  - [`SecsTranscodeReport`](#secstranscodereport)
  - [`SecsTranscodeWarning`](#secstranscodewarning)
  - [`SecsReadOptions`](#secsreadoptions)
  - [`SecsWriteOptions`](#secswriteoptions)
- [The binary spoke](#the-binary-spoke)
  - [`Secs2BinaryFormat`](#secs2binaryformat)
  - [`Secs2BinaryFormatReader`](#secs2binaryformatreader)
  - [`Secs2BinaryFormatWriter`](#secs2binaryformatwriter)
- [Writing a spoke](#writing-a-spoke)
  - [`SecsItemText`](#secsitemtext)
  - [`SecsUtf8Writer`](#secsutf8writer)
  - [`SecsUtf8`](#secsutf8)
  - [`SecsLineReader`](#secslinereader)
- [No hosting in this package](#no-hosting-in-this-package)
  - [Registering the hub from a spoke of your own](#registering-the-hub-from-a-spoke-of-your-own)

---

## Items

### `SecsItem`

`public abstract partial class SecsItem : IEquatable<SecsItem>, IDisposable`

One SECS-II item: a format, a count and either a payload or children. Abstract, with the concrete
shapes private - you build one through the factories below and never name its runtime type.

**Instance members**

| Member | Signature | Notes |
| --- | --- | --- |
| `MaxDepth` | `const int MaxDepth = 256` | The nesting limit the decoder enforces. |
| `Format` | `SecsFormat { get; }` | |
| `Label` | `string? { get; set; }` | Carried by SML, JSON, XML and INI; the binary wire format has nowhere to put it. |
| `Count` | `abstract int { get; }` | Elements for a value item, children for a list. For text it is **bytes**, not characters. |
| `IsList` | `virtual bool { get; }` | |
| `this[int]` | `virtual SecsItem { get; }` | Children. Throws for a value item. |
| `Items` | `virtual ReadOnlySpan<SecsItem> { get; }` | Children as a span. Throws for a value item. |
| `GetString()` | `virtual string` | Text items only. |
| `GetSpan<T>()` | `virtual ReadOnlySpan<T>` | The payload reinterpreted. `T` must match the element width. |
| `GetMemory<T>()` | `virtual Memory<T>` | As `GetSpan<T>`, when a `Memory<T>` is needed. |
| `TryGetSpan<T>(out ReadOnlySpan<T>)` | `virtual bool` | The non-throwing form, for when the format is not known up front. |
| `FirstValue<T>()` | `virtual ref T` | A reference to element zero, so a caller can read or write in place. |
| `FirstValueOrDefault<T>(T defaultValue = default)` | `virtual T` | Element zero, or `defaultValue` for an empty item. |

Every one of those five generic accessors is constrained `where T : unmanaged, IEquatable<T>`. The
`unmanaged` half is what makes reinterpreting the payload sound; `IEquatable<T>` is what lets the
comparison in `Equals` stay allocation-free rather than boxing each element.
| `Named(string?)` | `SecsItem` | Sets `Label` and returns the same instance - except on a shared empty, which is cloned instead so a label cannot leak between callers. |
| `EncodeTo(IBufferWriter<byte>)` | `void` | Uses `SecsCodecOptions.Default`. |
| `EncodeTo(IBufferWriter<byte>, SecsCodecOptions)` | `abstract void` | |
| `ToByteArray()` | `byte[]` | Convenience. Allocates; prefer `EncodeTo`. |
| `Dispose()` | `void` | Releases pooled payloads and disposes children. A no-op on a shared empty and on a second call. |
| `DisposeCore()` | `protected virtual void` | For a derived item that owns something. The default owns nothing. |
| `Equals(SecsItem?)` | `bool` | Structural: format, count and payload, children compared recursively. `Label` is **not** part of equality. |
| `Equals(object?)` / `GetHashCode()` / `ToString()` | `sealed override` | `ToString()` renders `<A [4]>`, not the payload. |
| `operator ==` / `operator !=` | `static bool` | Null-safe, and forwards to `Equals`. |

**Factories**

Every value format has the same four overloads, so `U4` stands for all of them:

| Signature | Ownership |
| --- | --- |
| `static SecsItem U4(params uint[] value)` | Copies into the item. |
| `static SecsItem U4(Memory<uint> value)` | Copies, or wraps when it is large enough to be worth pooling. |
| `static SecsItem U4(IEnumerable<uint> value)` | Materialises, then copies. |
| `static SecsItem U4(IMemoryOwner<uint> owner)` | **Takes ownership.** The item disposes the owner. |

| Factory | Signature | Notes |
| --- | --- | --- |
| `L` | `static SecsItem L(params SecsItem[] items)` / `L(IEnumerable<SecsItem>)` | **Takes ownership** of the children: disposing the list disposes them. |
| `A` | `static SecsItem A(string? value)` | ASCII. `null` yields the shared empty. |
| `A` | `static SecsItem A(string? value, int length, char padding = ' ')` | Fixed width - what GEM's timestamps and most equipment expect. Truncates or pads. |
| `J` | `static SecsItem J(string? value)` | JIS-8. |
| `B` | `static SecsItem B(params byte[])`, `B(Memory<byte>)`, `B(IEnumerable<byte>)`, `B(IMemoryOwner<byte>)` | |
| `Boolean` | four overloads over `bool` | |
| `I1` `I2` `I4` `I8` | four overloads each | |
| `U1` `U2` `U4` `U8` | four overloads each | |
| `F4` `F8` | four overloads each | |
| `Empty` | `static SecsItem Empty(SecsFormat format)` | A **process-wide singleton** per format. `Dispose()` on one does nothing, so a caller that disposes an empty cannot spoil a later one. Throws `ArgumentOutOfRangeException` for a format that is not an item format. |

**Decoding**

| Member | Signature | Notes |
| --- | --- | --- |
| `Decode` | `static SecsItem Decode(ReadOnlyMemory<byte> source, SecsCodecOptions? options = null)` | One whole item from a flat buffer. |
| `Decode` | `static SecsItem Decode(ref ReadOnlySequence<byte> source, SecsCodecOptions? options = null)` | Advances `source` past what it consumed, so it composes inside a pipe loop. |
| `DecodeValue` | `static SecsItem DecodeValue(SecsFormat format, in ReadOnlySequence<byte> payload, SecsCodecOptions options)` | A payload whose header was already read elsewhere. |
| `IsDefinedFormat` | `static bool IsDefinedFormat(SecsFormat format)` | Whether the value is one E5 defines, rather than any `enum` cast. |
| `TryReadItemHeaderByte` | `static bool TryReadItemHeaderByte(byte headerByte, out SecsFormat format, out byte lengthByteCount)` | Splits the format bits from the length-byte count. |
| `ReadItemLength` | `static int ReadItemLength(in ReadOnlySequence<byte> lengthBytes)` | One, two or three big-endian bytes. |

The caller owns whatever `Decode` returns. A payload past `SecsCodecOptions.PooledPayloadThreshold`
holds rented memory, so dropping it on the floor is a leak the garbage collector will not report.

### `SecsFormat`

`public enum SecsFormat`

The E5 format byte, already shifted into the high six bits of an item header.

| Member | Member | Member |
| --- | --- | --- |
| `List` | `Binary` | `Boolean` |
| `ASCII` | `JIS8` | |
| `I1` `I2` `I4` `I8` | `U1` `U2` `U4` `U8` | `F4` `F8` |

### `SecsFormatExtensions`

`public static class SecsFormatExtensions`

| Member | Signature | Notes |
| --- | --- | --- |
| `GetName` | `static string GetName(this SecsFormat)` | The .NET name, for example `U4`. |
| `ToSmlToken` | `static string ToSmlToken(this SecsFormat)` | The SML token, which is not always the name: `Boolean` writes `BOOLEAN`, `Binary` writes `B`. |
| `GetElementSize` | `static int GetElementSize(this SecsFormat)` | Bytes per element; `0` for a list. |
| `IsText` | `static bool IsText(this SecsFormat)` | `ASCII` or `JIS8`. |
| `TryParse` | `static bool TryParse(ReadOnlySpan<char> name, out SecsFormat format)` | Accepts a name or an SML token. |

---

## Messages

### `SecsMessage`

`public sealed class SecsMessage : IDisposable, IEquatable<SecsMessage>`

A stream, a function, the W bit and a body. Everything here is E5: there is no session id and no
system bytes, because those belong to a transport and this type is not one.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsMessage(byte s, byte f, bool replyExpected = true)` | |
| `S` / `F` | `byte { get; }` | |
| `ReplyExpected` | `bool { get; set; }` | The W bit. |
| `Name` | `string? { get; init; }` | Carried by SML, JSON and XML; dropped by the binary wire format and by INI. |
| `Item` | `SecsItem? { get; set; }` | **Owned.** Disposing the message disposes the body, and assigning over a body disposes the old one. |
| `IsPrimary` | `bool { get; }` | An odd function number. |
| `S9ErrorFunction` | `byte { get; }` | `F` when `S` is 9, otherwise `0`. Lets a handler test for an error report without testing twice. |
| `Dispose()` | `void` | |
| `Equals(SecsMessage?)` | `bool` | S, F, W, `Name` and the body tree. |
| `ToString()` | `override string` | For example `S6F11 W`. |

### `MessageHeader`

`public struct MessageHeader`

The ten bytes E37 puts in front of a message, as a mutable struct over an inline array - no
allocation, and `WriteTo` is a copy rather than a serialisation pass.

| Member | Signature | Notes |
| --- | --- | --- |
| `Length` | `const int Length = 10` | |
| `SessionId` | `ushort { get; set; }` | Bytes 0-1. |
| `DeviceId` | `ushort { get; }` | `SessionId` without the top bit. |
| `Stream` | `byte { get; set; }` | Byte 2 without the W bit. |
| `ReplyExpected` | `bool { get; set; }` | The top bit of byte 2. Set `Stream` first: assigning it rewrites the whole byte. |
| `Function` | `byte { get; set; }` | Byte 3 - for a data message. |
| `PType` | `byte { get; set; }` | Byte 4. `0` is SECS-II. |
| `MessageType` | `MessageType { get; set; }` | Byte 5, the SType. |
| `SystemBytes` | `int { get; set; }` | Bytes 6-9, big-endian. |
| `SelectStatus` | `SelectStatus { get; }` | Byte 3 read as a Select response status. |
| `DeselectStatus` | `DeselectStatus { get; }` | Byte 3 read as a Deselect response status. |
| `RejectReason` | `RejectReason { get; }` | Byte 3 read as a Reject reason. |
| `IsDataMessage` | `bool { get; }` | SType 0 **and** PType 0. |
| `IsPrimary` | `bool { get; }` | An odd function number. |
| `Parse` | `static MessageHeader Parse(ReadOnlySpan<byte>)` / `Parse(in ReadOnlySequence<byte>)` | The sequence overload is for a header split across reads. |
| `WriteTo` | `void WriteTo(Span<byte> destination)` | Needs ten bytes. |
| `ToString()` | `override string` | For example `S6F11 W [session 1, system 0x00000001]`. |

### `MessageType`

`public enum MessageType : byte` - the SType.

| Member | Value | Member | Value |
| --- | --- | --- | --- |
| `DataMessage` | 0 | `LinkTestRequest` | 5 |
| `SelectRequest` | 1 | `LinkTestResponse` | 6 |
| `SelectResponse` | 2 | `RejectRequest` | 7 |
| `DeselectRequest` | 3 | `SeparateRequest` | 9 |
| `DeselectResponse` | 4 | | |

### `SelectStatus`

`public enum SelectStatus : byte`

| Member | Value |
| --- | --- |
| `Success` | 0 |
| `AlreadyActive` | 1 |
| `NotReady` | 2 |
| `ConnectionExhaust` | 3 |

### `DeselectStatus`

`public enum DeselectStatus : byte`

| Member | Value |
| --- | --- |
| `Success` | 0 |
| `NotSelected` | 1 |
| `Busy` | 2 |

### `RejectReason`

`public enum RejectReason : byte`

| Member | Value |
| --- | --- |
| `StypeNotSupported` | 1 |
| `PtypeNotSupported` | 2 |
| `TransactionNotOpen` | 3 |
| `EntityNotSelected` | 4 |

---

## Codec

### `SecsCodecOptions`

`public sealed class SecsCodecOptions`

How the binary codec spends memory and which encodings it uses. Immutable; build one with an object
initialiser.

| Member | Signature | Default | Notes |
| --- | --- | --- | --- |
| `Default` | `static SecsCodecOptions { get; }` | | Shared. |
| `AsciiEncoding` | `Encoding { get; init; }` | `Encoding.ASCII` | |
| `Jis8Encoding` | `Encoding { get; init; }` | `Encoding.UTF8` | Real JIS-8 needs a code-page provider; UTF-8 is the safe default. |
| `PooledPayloadThreshold` | `int { get; init; }` | `1024` | At or above this, a decoded payload comes from `ArrayPool` and the item owns it. |
| `PooledStringThreshold` | `int { get; init; }` | `256` | The same line for text items. |
| `MaxPayloadLength` | `int { get; init; }` | `0xFFFFFF` | E5's three length bytes. A longer item is a decode error, not an allocation. |
| `EncodeBufferInitialSize` | `int { get; init; }` | `4096` | |
| `GetEncoding` | `Encoding GetEncoding(SecsFormat format)` | | `ASCII` or `JIS8`; throws for anything else. |

---

## Exceptions

### `SecsException`

`public class SecsException : Exception`

The base of everything this stack throws, so one `catch` covers the protocol.

| Member | Signature |
| --- | --- |
| ctor | `SecsException(string message)` |
| ctor | `SecsException(string message, Exception? innerException)` |
| ctor | `SecsException(string message, SecsMessage? secsMessage)` |
| `SecsMessage` | `SecsMessage? { get; }` |

### `SecsDecodeException`

`public sealed class SecsDecodeException : SecsException`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsDecodeException(string message)` | |
| ctor | `SecsDecodeException(string message, long offset)` | |
| ctor | `SecsDecodeException(string message, MessageHeader header)` | |
| `Offset` | `long { get; }` | `-1` when unknown. |
| `Header` | `MessageHeader? { get; }` | Present when the header parsed and only the body did not. **This is the recoverable case**: the frame was framed correctly, so the byte stream is still synchronised and the link can carry on. A `null` header means the stream is lost. |

### `SecsTimeoutException`

`public sealed class SecsTimeoutException : SecsException`

| Member | Signature |
| --- | --- |
| ctor | `SecsTimeoutException(SecsTimer timer, TimeSpan interval, SecsMessage? secsMessage = null)` |
| `Timer` | `SecsTimer { get; }` |
| `Interval` | `TimeSpan { get; }` |

### `SecsTimer`

`public enum SecsTimer` - which timer expired: `T3`, `T5`, `T6`, `T7`, `T8`.

---

## Serialization contracts

Namespace `Barbatos.SecsGem.Serialization`.

These two interfaces are the whole extension point. A new format is a new assembly that implements
them and ships an `AddSecsXxx()` method; nothing in this package changes.

### `ISecsFormatReader`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | Identity and capabilities. |
| `ReadAllAsync` | `IAsyncEnumerable<SecsMessage> ReadAllAsync(PipeReader input, SecsReadOptions? options = null, CancellationToken cancellationToken = default)` | Lazy, in document order. The reader completes `input` when it finishes. **The caller owns every message.** Throws `SecsFormatException` on invalid input. |

### `ISecsFormatWriter`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `SecsFormatDescriptor { get; }` | |
| `WriteAsync` | `ValueTask WriteAsync(PipeWriter output, SecsMessage message, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | One message as a whole document. Flushes `output`, does not complete it. |
| `WriteAllAsync` | `ValueTask<int> WriteAllAsync(PipeWriter output, IAsyncEnumerable<SecsMessage> messages, SecsWriteOptions? options = null, CancellationToken cancellationToken = default)` | Returns the number written. A format without `MultipleMessages` writes the first and warns. |

### `SecsFormatDescriptor`

`public sealed record SecsFormatDescriptor(string Id, string DisplayName, IReadOnlyList<string> FileExtensions, string MediaType, SecsFormatCapabilities Capabilities)`

| Member | Signature | Notes |
| --- | --- | --- |
| `Supports` | `bool Supports(SecsFormatCapabilities capability)` | |
| `ToString()` | `override string` | `Id`. |

A descriptor is how the transcoder knows what a conversion will cost before it runs one, which is
what turns silent loss into a warning.

### `SecsFormatCapabilities`

`public enum SecsFormatCapabilities` - `[Flags]`-shaped.

| Member | Value | What its absence costs |
| --- | --- | --- |
| `None` | 0 | |
| `MultipleMessages` | `1 << 0` | Only the first message of a document survives. |
| `MessageNames` | `1 << 1` | `SecsMessage.Name` is dropped. |
| `ItemLabels` | `1 << 2` | `SecsItem.Label` is dropped. |
| `Comments` | `1 << 3` | Comments in the source are not reproduced. |
| `ExactIntegers` | `1 << 4` | 64-bit values above 2^53 cannot be trusted in the target document. |
| `BinaryPayload` | `1 << 5` | Arbitrary bytes do not survive the target's escaping. |
| `Indentation` | `1 << 6` | Nothing. A format that cannot indent has not lost anything, which is why this is the one capability with no warning code. |
| `Lossless` | all of the above | What a format claims when it drops nothing. |

### `SecsFormatIds`

`public static class SecsFormatIds`

| Member | Value |
| --- | --- |
| `Secs2Binary` | `"secs2"` |
| `Sml` | `"sml"` |
| `Json` | `"json"` |
| `Xml` | `"xml"` |
| `Ini` | `"ini"` |

### `SecsFormatException`

`public sealed class SecsFormatException : SecsException`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsFormatException(string formatId, string message)` | |
| ctor | `SecsFormatException(string formatId, string message, int lineNumber)` | |
| ctor | `SecsFormatException(string formatId, string message, Exception? innerException)` | |
| `FormatId` | `string { get; }` | |
| `LineNumber` | `int { get; }` | `0` when the format has no lines to count. |

The message is prefixed with the format and the line, so `[sml] line 1: ...` arrives without the
caller having to assemble it.

---

## The registry and the transcoder

### `ISecsFormatRegistry`

| Member | Signature |
| --- | --- |
| `Formats` | `IReadOnlyCollection<SecsFormatDescriptor> { get; }` |
| `TryGetReader` | `bool TryGetReader(string formatId, out ISecsFormatReader? reader)` |
| `TryGetWriter` | `bool TryGetWriter(string formatId, out ISecsFormatWriter? writer)` |
| `TryResolveFormatId` | `bool TryResolveFormatId(string pathOrExtension, out string? formatId)` |

### `SecsFormatRegistry`

`public sealed class SecsFormatRegistry : ISecsFormatRegistry`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsFormatRegistry()` | Empty; add with `Register`. |
| ctor | `SecsFormatRegistry(IEnumerable<ISecsFormatReader> readers, IEnumerable<ISecsFormatWriter> writers)` | What the DI registration uses. |
| `Register` | `SecsFormatRegistry Register(ISecsFormatReader reader)` / `Register(ISecsFormatWriter writer)` | Returns `this`. |
| `Formats` | `IReadOnlyCollection<SecsFormatDescriptor> { get; }` | |
| `TryGetReader` / `TryGetWriter` | `bool` | |
| `TryResolveFormatId` | `bool TryResolveFormatId(string pathOrExtension, out string? formatId)` | Accepts `"recipe.sml"`, `".sml"` or `"sml"`. |

A reader and a writer are registered separately on purpose: a format may legitimately be readable
and not writable.

### `SecsTranscoder`

`public sealed class SecsTranscoder`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsTranscoder(ISecsFormatRegistry registry)` | |
| `Registry` | `ISecsFormatRegistry { get; }` | |
| `TranscodeAsync` | `ValueTask<SecsTranscodeReport> TranscodeAsync(string sourceFormatId, PipeReader input, string targetFormatId, PipeWriter output, SecsReadOptions? readOptions = null, SecsWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)` | Streams; nothing is buffered whole. |
| `TranscodeTextAsync` | `ValueTask<(string Text, SecsTranscodeReport Report)> TranscodeTextAsync(string sourceFormatId, string source, string targetFormatId, ...)` | Convenience for text formats. |
| `TranscodeFileAsync` | `ValueTask<SecsTranscodeReport> TranscodeFileAsync(string sourcePath, string targetPath, ...)` | Both formats come from the extensions. |
| `ReadTextAsync` | `ValueTask<List<SecsMessage>> ReadTextAsync(string formatId, string source, ...)` | **The caller owns every message.** |
| `WriteTextAsync` | `ValueTask<string> WriteTextAsync(string formatId, IEnumerable<SecsMessage> messages, ...)` | |

### `SecsTranscodeReport`

`public sealed record SecsTranscodeReport(SecsFormatDescriptor SourceFormat, SecsFormatDescriptor TargetFormat, int MessageCount, IReadOnlyList<SecsTranscodeWarning> Warnings)`

| Member | Signature | Notes |
| --- | --- | --- |
| `IsLossless` | `bool { get; }` | No warnings. |
| `ToString()` | `override string` | For example `sml -> json: 12 message(s), lossless`. |

### `SecsTranscodeWarning`

`public sealed record SecsTranscodeWarning(string Code, string Message)`

A stable `Code` and prose. The code is what a build script greps for; the message is what a person
reads.

| Code | Raised when |
| --- | --- |
| `MULTIPLE_MESSAGES_UNSUPPORTED` | The target carries one message and the source had more. |
| `MESSAGE_NAMES_DROPPED` | The source had names and the target cannot carry them. |
| `ITEM_LABELS_DROPPED` | The source had labels and the target cannot carry them. |
| `COMMENTS_DROPPED` | The target cannot reproduce comments. |
| `LARGE_INTEGERS_INEXACT` | The target cannot be trusted with 64-bit values above 2^53. |
| `BINARY_PAYLOAD_LOSSY` | The target's escaping does not survive arbitrary bytes. |

There is no warning for indentation. `SecsFormatCapabilities.Indentation` exists so a writer knows
whether to bother, not so a conversion can apologise for it - a format that cannot indent has lost
nothing.

### `SecsReadOptions`

`public sealed class SecsReadOptions`

| Member | Signature | Default |
| --- | --- | --- |
| `Default` | `static SecsReadOptions { get; }` | |
| `Codec` | `SecsCodecOptions { get; init; }` | `SecsCodecOptions.Default` |
| `Strict` | `bool { get; init; }` | `true` |
| `ReadItemLabels` | `bool { get; init; }` | `true` |
| `MaxDocumentLength` | `long { get; init; }` | `0`, meaning no limit |

`Strict` is the difference between refusing a document whose declared count disagrees with its
contents and quietly believing one of them.

### `SecsWriteOptions`

`public sealed class SecsWriteOptions`

| Member | Signature | Default |
| --- | --- | --- |
| `Default` | `static SecsWriteOptions { get; }` | |
| `Compact` | `static SecsWriteOptions { get; }` | `Default` with `Indent = false` |
| `Codec` | `SecsCodecOptions { get; init; }` | `SecsCodecOptions.Default` |
| `Indent` | `bool { get; init; }` | `true` |
| `IndentSize` | `int { get; init; }` | `2` |
| `NewLine` | `string { get; init; }` | `"\r\n"` |
| `WriteItemLabels` | `bool { get; init; }` | `true` |
| `WriteLargeIntegersAsStrings` | `bool { get; init; }` | `true` |
| `BinaryBytesPerLine` | `int { get; init; }` | `16` |

---

## The binary spoke

SECS-II binary is the hub written down as a spoke, registered like any other format. That is what
lets `TranscodeFileAsync("capture.secs2", "capture.sml")` work with no special case anywhere.

### `Secs2BinaryFormat`

`public static class Secs2BinaryFormat`

| Member | Signature | Notes |
| --- | --- | --- |
| `Descriptor` | `static SecsFormatDescriptor { get; }` | Id `secs2`; extensions `.secs2`, `.secs`, `.bin`; media type `application/octet-stream`. |

Capabilities: `MultipleMessages | ExactIntegers | BinaryPayload`. It is the only shipped format with
the last two, and the only one without `MessageNames`, `ItemLabels`, `Comments` or `Indentation` -
the wire format has nowhere to put a name, a label or a comment, and nothing to indent.

### `Secs2BinaryFormatReader`

`public sealed class Secs2BinaryFormatReader : ISecsFormatReader`

Length-prefixed frames off a `PipeReader`, exactly as HSMS frames them.

### `Secs2BinaryFormatWriter`

`public sealed class Secs2BinaryFormatWriter : ISecsFormatWriter`

The same frames in the other direction. System bytes are numbered from one per document, because a
file is not a session and has no transaction to belong to.

---

## Writing a spoke

These are the pieces the four shipped spokes are built from. They are public so that a format
living outside this repository is built the same way rather than worse.

### `SecsItemText`

`public static class SecsItemText`

| Member | Signature | Notes |
| --- | --- | --- |
| `Parse` | `static SecsItem Parse(SecsFormat format, ReadOnlySpan<char> text)` | One item's payload from its text form. |
| `WriteValues` | `static void WriteValues(SecsUtf8Writer writer, SecsItem item, SecsWriteOptions? options = null)` | The payload only - no header, no brackets. |
| `WriteQuoted` | `static void WriteQuoted(SecsUtf8Writer writer, string? value)` | Quotes and escapes the way every text spoke here agrees to. |
| `ParseString` | `static string ParseString(ReadOnlySpan<char> text)` | The inverse. |

### `SecsUtf8Writer`

`public sealed class SecsUtf8Writer`

A thin UTF-8 writer over an `IBufferWriter<byte>`: no `StringBuilder`, no intermediate `string`.

| Member | Signature |
| --- | --- |
| ctor | `SecsUtf8Writer(IBufferWriter<byte> writer)` |
| `Write` | `void Write(char value)` / `Write(string? value)` / `Write(ReadOnlySpan<char> value)` |
| `WriteValue<T>` | `void WriteValue<T>(T value, string? format = null)` |
| `WriteBoolean` | `void WriteBoolean(bool value)` |
| `WriteHex` | `void WriteHex(byte value)` |
| `WriteLine` | `void WriteLine(string newLine = "\r\n")` |
| `WriteIndent` | `void WriteIndent(int level, int size)` |

### `SecsUtf8`

`public static class SecsUtf8`

| Member | Signature | Notes |
| --- | --- | --- |
| `GetString` | `static string GetString(in ReadOnlySequence<byte> source)` | Handles a sequence split across segments. |
| `TrimPreamble` | `static ReadOnlySequence<byte> TrimPreamble(in ReadOnlySequence<byte> source)` | Drops a UTF-8 BOM. Equipment and editors both produce them. |

### `SecsLineReader`

`public sealed class SecsLineReader`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsLineReader(PipeReader input)` | |
| `LineNumber` | `int { get; }` | 1-based, for error messages. |
| `ReadLineAsync` | `ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken = default)` | `null` at the end. Handles CR, LF and CRLF. |

---

## No hosting in this package

There is no `AddSecsSerialization`, and no reference to
`Microsoft.Extensions.DependencyInjection.Abstractions`. This assembly has to stay usable from a
file converter, a Roslyn analyzer and a WebAssembly tool, and not one of those owns a container.

What that method used to register is registered by every `AddSecsXxx()` instead - see
[`AddSecsSml`](../Barbatos.SecsGem.Serialization.Sml/API-REFERENCE.md#addsecssml) for the table -
and by `AddSecsGem()` in the HSMS package. One call per format is the whole wiring, and two calls
still build one registry, because every row goes in through `TryAdd`.

Without a container, the hub is two constructors:

```csharp
var registry = new SecsFormatRegistry()
    .Register(new Secs2BinaryFormatReader())
    .Register(new Secs2BinaryFormatWriter());

var transcoder = new SecsTranscoder(registry);
```

`SecsCodecOptions` is a value rather than a service: hand it to `Decode`, `EncodeTo` or
`SecsSerializationOptions.Codec` where it matters, or leave it out and get
`SecsCodecOptions.Default`.

### Registering the hub from a spoke of your own

A spoke outside this repository cannot compile-link the shared file the four shipped spokes use,
so its `AddSecsXxx()` registers the same five services itself. Every type involved is public:

```csharp
public static IServiceCollection AddSecsMyFormat(this IServiceCollection services)
{
    // The hub. TryAdd throughout, so this is a no-op beside any other AddSecsXxx().
    services.TryAddSingleton(SecsCodecOptions.Default);
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, Secs2BinaryFormatReader>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, Secs2BinaryFormatWriter>());
    services.TryAddSingleton<ISecsFormatRegistry>(provider => new SecsFormatRegistry(
        provider.GetServices<ISecsFormatReader>(),
        provider.GetServices<ISecsFormatWriter>()));
    services.TryAddSingleton<SecsTranscoder>();

    // The spoke.
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, MyFormatReader>());
    services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, MyFormatWriter>());
    return services;
}
```

Those five lines are what this package not owning a container costs, and they are the honest price
of it: the alternative was a dependency on
`Microsoft.Extensions.DependencyInjection.Abstractions` in the one assembly a file converter, an
analyzer and a WebAssembly tool all have to be able to use.
