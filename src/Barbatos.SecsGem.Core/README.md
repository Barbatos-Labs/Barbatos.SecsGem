# Barbatos.SecsGem.Core

The SECS-II (SEMI E5) item tree, its binary codec, and the contracts every format spoke plugs
into - for **.NET 8, 9 and 10**. No sockets, no hosting, no `Microsoft.Extensions.*`.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Core
```

Two dependencies, and the list is short on purpose: this package is what everything else in the
stack is built on, so anything it drags in, a fab drags in.

| Dependency | Why |
| --- | --- |
| `CommunityToolkit.HighPerformance` | `MemoryOwner<T>` and `ArrayPoolBufferWriter<T>`, which is how the codec avoids the Large Object Heap |
| `System.IO.Pipelines` | `ISecsFormatReader` and `ISecsFormatWriter` are defined over `PipeReader` and `PipeWriter` |

Nothing from `Microsoft.Extensions.*`, dependency-injection abstractions included. A file
converter, a Roslyn analyzer and a WebAssembly tool all want the item model and the binary codec,
and not one of them owns a container - so the `AddSecsXxx()` methods live in the packages that
already need one, and a test in this repository fails the build if a reference creeps back in here.

## Two types carry the whole model

```csharp
using var message = new SecsMessage(6, 11, replyExpected: true)
{
    Name = "EventReportSend",
    Item = SecsItem.L(
        SecsItem.U4(1u),                       // DATAID
        SecsItem.U4(7_001u),                   // CEID
        SecsItem.L(
            SecsItem.L(
                SecsItem.U4(5_001u),           // RPTID
                SecsItem.L(
                    SecsItem.A("PRESSURE").Named("VID"),
                    SecsItem.A("1013.25").Named("VALUE"))))),
};
```

`SecsItem` is a format, a count and either a payload or children. `SecsMessage` is a stream, a
function, the W bit and a body.

Notice what is **not** in either of them: no session id, no system bytes, no SType. Those are
E37's, and E37 is a different package. A `SecsMessage` read out of a `.sml` file on a laptop with
no network card is the same object as one that arrived over a socket, which is the property the
rest of the stack is built on.

## Reading a value back out

The payload is reinterpreted, not copied:

```csharp
ReadOnlySpan<uint> ids = item[1].GetSpan<uint>();
uint ceid = item[1].FirstValueOrDefault<uint>();
string vid = item[2][0][1][0].GetString();

// When the format is not known up front:
if (item.TryGetSpan<ulong>(out var wide)) { /* ... */ }
```

`FirstValue<T>()` returns a `ref`, so a value can be read or written in place without rebuilding
the item.

One sharp edge worth knowing before it bites: **`Count` on a text item is bytes, not characters.**
That is E5's definition, not a convenience, and it is why a text item cannot hold a sequence the
way a numeric item can.

## Ownership is explicit, because pooled memory has to be

A payload at or above `SecsCodecOptions.PooledPayloadThreshold` - 1024 bytes by default - is rented
from `ArrayPool`, and the item owns it. So:

- A list **owns its children**. Dispose the root and the tree goes with it.
- A message **owns its body**. Assigning over `Item` disposes the old one.
- Whatever `Decode` or a format reader hands you, **you own**.
- The `IMemoryOwner<T>` factory overloads **take ownership**; the others copy.

```csharp
using var item = SecsItem.Decode(bytes);   // the using is not decoration
```

An empty item is the exception, and on purpose. `SecsItem.Empty(format)`, and so `SecsItem.A(null)`
or `SecsItem.L()`, hand back a process-wide singleton - so `Dispose()` on one does nothing, and a
caller who disposes an empty cannot spoil it for everybody else. `Named()` on a shared empty clones
rather than mutating, for the same reason.

## The binary codec

```csharp
var writer = new ArrayPoolBufferWriter<byte>();
message.Item!.EncodeTo(writer);                       // no byte[] anywhere

var buffer = new ReadOnlySequence<byte>(received);
using var decoded = SecsItem.Decode(ref buffer);      // advances past what it consumed
```

`EncodeTo` writes into any `IBufferWriter<byte>`, and the `ref ReadOnlySequence<byte>` overload of
`Decode` composes inside a `System.IO.Pipelines` loop - which is exactly how the HSMS package
decodes a frame that straddles a chunk boundary.

Nesting is capped at `SecsItem.MaxDepth` (256) and an item payload at
`SecsCodecOptions.MaxPayloadLength` (E5's three length bytes). Both are refusals rather than
allocations: a malformed length on the wire should cost a `SecsDecodeException`, not 16 MB.

## Adding a format never changes this package

A spoke is an assembly that implements two interfaces:

```csharp
public interface ISecsFormatReader
{
    SecsFormatDescriptor Descriptor { get; }
    IAsyncEnumerable<SecsMessage> ReadAllAsync(PipeReader input, SecsReadOptions? options = null, CancellationToken ct = default);
}
```

and an extension method that adds itself to the service collection. The registry is built from
`IEnumerable<ISecsFormatReader>` whenever it is resolved, so the registry knows nothing about SML
and a spoke never has to be told about the formats registered beside it.

**If adding a format means editing `Barbatos.SecsGem.Core`, the design is wrong.** That is the test
the four shipped spokes were written against, and the reason the binary format is itself a
registered spoke rather than a special case.

A spoke in another repository registers the hub from its own `AddSecsXxx()` in five lines of
public API - see
[Registering the hub from a spoke of your own](API-REFERENCE.md#registering-the-hub-from-a-spoke-of-your-own).

## Converting between formats, with no protocol stack in sight

```csharp
services.AddSecsSml().AddSecsJson();

var transcoder = provider.GetRequiredService<SecsTranscoder>();
var report = await transcoder.TranscodeFileAsync("recipe.sml", "recipe.json");

Console.WriteLine(report);              // sml -> json: 1 message(s), 2 warning(s)
Console.WriteLine(report.IsLossless);   // False
foreach (var warning in report.Warnings)
{
    Console.WriteLine($"{warning.Code}: {warning.Message}");
}
```

There is no `AddSecsSerialization()` in this package, and that is the point: each `AddSecsXxx()`
registers the hub it plugs into, so one call per format is the whole wiring. A tool with no
container at all builds the same thing by hand:

```csharp
var registry = new SecsFormatRegistry()
    .Register(new Secs2BinaryFormatReader())
    .Register(new Secs2BinaryFormatWriter())
    .Register(new SmlFormatReader())
    .Register(new SmlFormatWriter());

var transcoder = new SecsTranscoder(registry);
```

A `SecsFormatDescriptor` states what a format can carry, so the transcoder knows what a conversion
will cost **before** it runs one. That is what turns silent loss into
`MESSAGE_NAMES_DROPPED`, and it is why every conversion returns a report rather than just bytes.

Each warning has a stable `Code` for a build script to grep and a `Message` for a person to read.
There is deliberately no warning for indentation: a format that cannot indent has not lost
anything.

## Timers and exceptions live here, the clocks do not

`SecsTimer`, `SecsTimeoutException` and `SecsDecodeException` are in this package because the
format spokes and the protocol engine both throw them. The timers themselves are not - a
`TimeProvider` belongs where the sockets are.

`SecsDecodeException.Header` is worth one line of explanation, because the HSMS engine depends on
it: a header means the frame was framed correctly and only its body would not decode, so the byte
stream is still synchronised and the link can carry on. No header means the stream is lost.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

A runnable demonstration of the transcoder and every format:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Transcode
```

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
