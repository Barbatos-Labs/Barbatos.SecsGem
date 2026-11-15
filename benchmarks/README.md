# Benchmarks

BenchmarkDotNet over the four things in this repository whose cost is a design claim rather than a
detail: the item codec, the HSMS frame codec, the object mapper's two resolvers, and the format
spokes.

## Running them

```
dotnet run -c Release --project benchmarks/Barbatos.SecsGem.Benchmarks -- --filter "*"
```

`-c Release` is not optional - BenchmarkDotNet refuses a debug build, and it is right to.

A full run takes a while. While iterating:

```
dotnet run -c Release --project benchmarks/Barbatos.SecsGem.Benchmarks -- --filter "*Mapper*" --job short
```

```
dotnet run -c Release --project benchmarks/Barbatos.SecsGem.Benchmarks -- --filter "*" --job Dry
```

`--job short` trades confidence for speed: useful for a direction, not for a number you intend to
quote. `--job Dry` runs every case exactly once and measures nothing - it is the fastest way to
prove the whole suite still executes after a change.

## What each one measures

### `ItemCodecBenchmarks`

`SecsItem.EncodeTo` and `SecsItem.Decode`, over three shapes.

The shapes are not a size spread for its own sake. `Status` is an S1F2 that a host polls constantly,
`EventReport` is the nested S6F11 that dominates real GEM traffic, and `Trace` carries a 64 KiB
binary payload - deliberately past `SecsCodecOptions.PooledPayloadThreshold`, so the pooled path is
exercised at all. A suite that stayed under the threshold would report half the design.

`Decode` includes disposing the tree it produced. A decoded payload above the threshold holds
rented memory, so leaving the return out would measure half a transaction and call it whole.

### `HsmsFrameBenchmarks`

`HsmsPipeEncoder.Encode`, and the question the dual-path decoder exists to answer.

`DecodeWhole` hands each frame to the pipe in one piece, so the decoder reads it in place out of the
`ReadOnlySequence`. `DecodeChunked` hands over the same bytes **seven at a time**, so every frame
straddles a boundary and takes the gathering path. The gap between the two is the price of a
fragmented network.

T8 is left at its default rather than switched off, because the gathering path arms the timer and
the fast path never does. Disabling it would flatter the chunked arm by hiding work a real link
pays for.

### `MapperBenchmarks`

`SecsTypeInfoResolver.Generated` against `SecsTypeInfoResolver.Reflection`, mapping the same objects
with the same converters and the same options.

Its `[GlobalSetup]` **throws** unless the generator produced a contract for every type involved.
Without that check a generator that silently did nothing would leave both arms running reflection,
and the table would report a dead heat as a result rather than as the bug it is.

### `FormatBenchmarks`

Write, read and transcode-from-SML, for each of the four text spokes.

The registry is built through `AddSecsSerialization().AddSecsSml()` and the rest, the way an
application builds it, so the figures come from the configured path rather than from a reader
instantiated by hand. The binary hub is absent on purpose: it is not a text format, and its cost is
the two suites above.

Writing goes through a reused `MemoryStream`, so each row carries one `StreamPipeWriter` allocation
that has nothing to do with the format. It is the same constant in all four rows, which makes the
rows comparable to each other rather than absolute.

## Read the allocation column first

Timings move with the machine, the power plan and whatever else is running. Allocations do not.
These invariants should hold anywhere, and a change that breaks one is a regression even if the
nanoseconds look fine:

| Invariant | Why it matters |
| --- | --- |
| `ItemCodecBenchmarks.Encode` allocates **nothing**, for every shape including the 64 KiB one | `EncodeTo` writes into the caller's `IBufferWriter<byte>`. Any allocation here means something built a `byte[]` on the way. |
| `HsmsFrameBenchmarks.Encode` allocates a small **constant**, whatever the body size | The frame encoder stages the body in a pooled writer. An allocation that grows with the message means the pooling stopped working. |
| `DecodeChunked` allocates within a few per cent of `DecodeWhole` | The gathering path stages into pooled memory. If the chunked arm allocates in proportion to the traffic, the pool is being bypassed. |
| Generated and Reflection allocate **identically** | Both resolvers cache their contracts, so the only allocation left is the item tree, which is the same either way. |

That last row is the reason this directory exists. It did not hold: the generated resolver rebuilt
its contract on every single mapped value - property array, delegates and labels - while the
reflection resolver cached. The first run put the generator about 13% slower on three of the four
operations and allocating 45% more than the reflection it was brought in to replace. Nothing else
in the repository would have noticed, because both resolvers produced *correct* output and the
parity tests passed throughout. A cache in `GeneratedSecsTypeInfoResolver.GetTypeInfo` fixed it,
and the allocation columns have been equal since.

## Not here, and why

- **No comparison against secs4net.** The protocol claims in this repository are about behaviour -
  a T8 that is wired up, a state machine that cannot re-enter a transition - and those are the
  claims the tests make. A throughput table against a library nobody here has tuned would be an
  argument about configuration dressed up as an argument about design.
- **No numbers checked into the documentation.** They would be a measurement of one laptop on one
  afternoon, and stale by the next release. Run the suite; the invariants above are the part that
  travels.
