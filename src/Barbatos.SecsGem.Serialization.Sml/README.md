# Barbatos.SecsGem.Serialization.Sml

SML (SECS Message Language) reader and writer for **.NET 8, 9 and 10** - the format every SECS
engineer can already read, streamed over `PipeReader`/`PipeWriter`.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Serialization.Sml
```

## The two-line version

```csharp
using var message = SmlConvert.ParseSml("S1F1 W .");
Console.WriteLine(message.ToSml());
```

`SmlConvert` is the convenience layer. Nothing else in the stack needs it - the real contract is
`ISecsFormatReader`/`ISecsFormatWriter`, and this package registers both:

```csharp
services.AddSecsSerialization().AddSecsSml();
```

## What it writes

```sml
EventReportSend: 'S6F11' W
<L [3]
  <U4 [1] 1>   // DATAID
  <U4 [1] 7001>   // CEID
  <L [1]
    <L [2]
      <U4 [1] 100>   // RPTID
      <L [2]
        <A [8] 'PRESSURE'>   // VID
        <F8 [1] 1013.25>   // VALUE
      >
    >   // Report
  >
>
.
```

Item labels become trailing `//` comments, which is the one place SML has to put them. A message
name goes in front of the header. The `.` on its own line ends the message - that is
`SmlFormat.MessageTerminator`, and it is what lets several messages share a file.

## Lossless, and it is the only one that has to be

| Capability | |
| --- | --- |
| `MultipleMessages` | yes |
| `MessageNames` | yes |
| `ItemLabels` | yes |
| `Comments` | yes |
| `ExactIntegers` | yes |
| `BinaryPayload` | yes |
| `Indentation` | yes |

`SecsFormatCapabilities.Lossless` - every capability the hub defines. SML is the reference the other
spokes are measured against, so a conversion **into** SML never warns about anything.

## What the reader accepts

A writer has to pick one shape; a reader meets whatever a person or a decade-old tool produced. So
the header is deliberately generous - every one of these parses to the same message:

```sml
S1F3
'S1F3'
"S1F3"
S1F3 W
'S1F3' W
s1f3 w
'S99F127' W
  'S1F3'   W
EventReportSend: 'S6F11' W
```

Lowercase works, either quote style works, no quotes works, and the surrounding whitespace is
ignored. A message name may contain anything that looks like a name - including `Recipe.Download`,
and including `S1F3`, which is a name that looks like a header and is read as a name anyway.

Text can be single- or double-quoted. A quote of the *other* kind needs no escaping; a quote of the
*same* kind is doubled:

```sml
<A [9] 'it''s here'>     // -> it's here
<A [9] "it's here">      // -> it's here
<A [11] 'say "hello"'>   // -> say "hello"
<A [5] 'a > b'>          // -> a > b
<A [16] '// not a comment'>
<A [10] '  padded  '>    // whitespace inside quotes is content
<A [0] ''>
```

The declared count is **cross-checked against what follows**, not trusted. `<A [4] 'abcde'>` is an
error rather than a silent truncation - and the count is in **bytes**, which is E5's definition and
the thing that surprises people about multi-byte text.

Errors carry the line number:

```
[sml] line 1: Expected 'S' in the message header, found "<A [2] 'ok'>"
```

A UTF-8 BOM is skipped rather than being parsed as the first character of a header, because every
Windows editor writes one.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Transcode
```

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
