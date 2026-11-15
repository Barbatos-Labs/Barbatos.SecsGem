# Barbatos.SecsGem.Serialization.Ini

INI reader and writer for **.NET 8, 9 and 10**, with every item addressed by its path - the format
for the tools that still speak `.ini`, and for diffing two recipes line by line.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Serialization.Ini
```

One call registers the reader, the writer and the transcoder they plug into:

```csharp
services.AddSecsIni();
```

## What it writes

```ini
[1]
name = EventReportSend
stream = 6
function = 11
replyExpected = true
item.1 = L[3]
item.1.$label = Body
item.1.1 = U4[1] 1
item.1.1.$label = DATAID
item.1.2 = U4[1] 7001
item.1.2.$label = CEID
item.1.3 = L[1]
```

INI has no nesting, so the nesting goes in the **key**. `item.1.3.2.1` is the first child of the
second child of the third child of the root. The section name is the message number, so one file
holds as many messages as you like.

That choice has a property no other spoke here has: **every item has a stable address**. Two
recipes that differ in one parameter differ in one line, and a line is something `git diff`, a
change-control system and a person all understand. A nested format, including the lossless ones,
turns the same edit into a reformatted block.

## Where INI cannot keep up

| Capability | |
| --- | --- |
| `MultipleMessages` | yes |
| `MessageNames` | yes |
| `ItemLabels` | yes |
| `Comments` | yes |
| `ExactIntegers` | yes |
| `BinaryPayload` | yes |
| `Indentation` | **no** |

Only indentation, and that costs nothing - a path-addressed format has no tree to indent, and
there is deliberately no warning code for it. A conversion into INI is otherwise lossless.

## What the reader tolerates

Keys arrive in whatever order the last tool wrote them, so order is not meaningful:

```ini
[1]
item.1.2 = U4[1] 7001
function = 11
item.1 = L[2]
stream = 6
item.1.1 = U4[1] 1
```

That parses to the same message as the tidy version. The parser builds the tree from the paths
rather than from the sequence, because a format whose semantics depend on line order would be a
format you cannot safely merge.

Also accepted: `;` and `#` comments, blank lines, whitespace around `=`, and a value containing
`=` or a delimiter as long as it is quoted.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
