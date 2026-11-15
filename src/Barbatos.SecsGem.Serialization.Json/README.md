# Barbatos.SecsGem.Serialization.Json

JSON reader and writer for **.NET 8, 9 and 10**, on `Utf8JsonReader` and `Utf8JsonWriter` - for
when a SECS message has to reach something that is not a SECS tool.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Serialization.Json
```

```csharp
services.AddSecsSerialization().AddSecsJson();
```

## What it writes

```json
[
  {
    "name": "EventReportSend",
    "s": 6,
    "f": 11,
    "w": true,
    "item": {
      "List": [
        { "U4": [1], "$label": "DATAID" },
        { "U4": [7001], "$label": "CEID" }
      ],
      "$label": "Body"
    }
  }
]
```

An item is an object with **one** property whose name is the format and whose value is the payload,
plus an optional `$label`. That shape is why a reader never has to guess: the format is the key, so
there is no type field to get out of step with the data.

The document root is an array, because a `.json` capture should be able to hold more than one
message. A reader also accepts a single bare object, since that is what anybody hand-writing one
file will produce.

## Where JSON cannot keep up

| Capability | |
| --- | --- |
| `MultipleMessages` | yes |
| `MessageNames` | yes |
| `ItemLabels` | yes |
| `BinaryPayload` | yes |
| `Indentation` | yes |
| `Comments` | **no** - JSON has no comment syntax |
| `ExactIntegers` | **no** - see below |

A conversion into JSON therefore warns `COMMENTS_DROPPED` when the source had comments, and
`LARGE_INTEGERS_INEXACT` when the message carries `U8` or `I8`.

### The large-integer warning is about the document, not about this library

JSON text has arbitrary-precision numbers, and `Utf8JsonReader` parses straight to `ulong` without
going through a `double`. So `ulong.MaxValue` **does** survive a round trip through this package,
exactly, whichever way `WriteLargeIntegersAsStrings` is set.

The warning is about everybody else. `JSON.parse` in a browser gives you an IEEE 754 double, which
silently rounds above 2^53 - so a `U8` wafer id written as a bare number is a document that lies to
a JavaScript consumer. That is why `SecsWriteOptions.WriteLargeIntegersAsStrings` defaults to
`true`:

```json
{ "U8": ["18446744073709551615"] }
```

Set it to `false` and you get `18446744073709551615` as a number. The value is still read back
exactly here; it is the next tool in the chain that has the problem.

## One asymmetry the reader insists on

A **numeric** item's payload must be an array, even for a single element. A **text** item's may be
a bare string:

```json
{ "U4": [1] }           // required
{ "U4": 1 }             // rejected
{ "A": "PRESSURE" }     // accepted
{ "A": ["PRESSURE"] }   // also accepted
```

The reason is E5, not taste: a numeric item is a sequence of elements, so its payload is a
sequence. A text item's count is **bytes**, so it is one value and not a sequence of them, and
writing it as an array would suggest it could hold several strings.

It is a decision rather than an accident, and there is a test pinning it. If you need the lenient
form, `JsonFormatReader.ReadValue` is the one place to relax.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
