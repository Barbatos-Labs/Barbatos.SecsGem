# Barbatos.SecsGem.Catalog - API reference

Namespace `Barbatos.SecsGem.Catalog`. Concepts, provenance and the caveats that go with it live in
[`README.md`](README.md); this page is member-by-member.

- [`SecsCatalog`](#secscatalog)
- [`SecsCatalogExtensions`](#secscatalogextensions)
- [`SecsStreamInfo`](#secsstreaminfo)
- [`SecsStreamProvenance`](#secsstreamprovenance)
- [`SecsCatalogProvenance`](#secscatalogprovenance)
- [`SecsMessageInfo`](#secsmessageinfo)
- [`SecsMessageDirection`](#secsmessagedirection)
- [`SecsDataItemInfo`](#secsdataiteminfo)
- [`SecsDataItemCode`](#secsdataitemcode)

---

## `SecsCatalog`

`public static class SecsCatalog`

The lookup table. Everything is loaded from one embedded 40 KB resource the first time any member
is touched, into frozen dictionaries - so referencing the package costs nothing until you ask it
something.

| Member | Signature | Notes |
| --- | --- | --- |
| `Streams` | `static IReadOnlyList<SecsStreamInfo> { get; }` | 20 of them, ordered by number. There is no S11. |
| `Messages` | `static IReadOnlyList<SecsMessageInfo> { get; }` | 524, ordered by stream then function. |
| `DataItems` | `static IReadOnlyList<SecsDataItemInfo> { get; }` | 375, ordered by name. |
| `Provenance` | `static SecsCatalogProvenance { get; }` | How much of the table is anchored to the rest of it. Ask this before quoting the reference at anybody; ask `SecsStreamInfo.Provenance` before implementing against one stream. |
| `TryGetStream` | `static bool TryGetStream(byte stream, out SecsStreamInfo? info)` | |
| `TryGetMessage` | `static bool TryGetMessage(byte stream, byte function, out SecsMessageInfo? info)` | |
| `TryGetDataItem` | `static bool TryGetDataItem(string name, out SecsDataItemInfo? info)` | Ordinal and case-sensitive, as E5 writes the names. `false` for the 74 names that appear in bodies without a dictionary entry. |
| `NameOf` | `static string? NameOf(byte stream, byte function)` | For filling in `SecsMessage.Name`, which SML, JSON and XML all carry. |
| `Describe` | `static string Describe(byte stream, byte function)` | `S6F11 Event Report Send`, or just `S6F11` when unknown - so a log line needs no null check. |

## `SecsCatalogExtensions`

`public static class SecsCatalogExtensions`

| Member | Signature | Notes |
| --- | --- | --- |
| `Describe` | `static string Describe(this SecsMessage message)` | `S6F11 W Event Report Send`. What a log line wants; `SecsMessage.ToString()` gives `S6F11 W`, which is correct and says nothing about what happened. |
| `Catalogued` | `static SecsMessageInfo? Catalogued(this SecsMessage message)` | The entry, or `null` when the pair is not catalogued. |

## `SecsStreamInfo`

`public sealed class SecsStreamInfo`

| Member | Signature | Notes |
| --- | --- | --- |
| `Number` | `byte { get; }` | |
| `Name` | `string { get; }` | For example `Data Collection`. |
| `Messages` | `IReadOnlyList<SecsMessageInfo> { get; }` | This stream's messages, ordered by function. |
| `Provenance` | `SecsStreamProvenance { get; }` | How much of this stream the source actually documented. The question to ask before implementing against it. |
| `ToString()` | `override string` | `S6 Data Collection`. |

The twenty streams:

| S | Name | S | Name |
| --- | --- | --- | --- |
| 1 | Equipment Status | 13 | Data Set Transfer |
| 2 | Equipment Control and Diagnostics | 14 | Object Services |
| 3 | Material Status | 15 | Recipe Management |
| 4 | Material Control | 16 | Processing Management |
| 5 | Exception Handling | 17 | Equipment Control and Diagnostics (Extended) |
| 6 | Data Collection | 18 | Subsystem Control and Data |
| 7 | Process Program Management | 19 | Recipe Management |
| 8 | Control Program Transfer | 20 | Recipe Management System |
| 9 | System Errors | 21 | Item Transfer |
| 10 | Terminal Services | | |

## `SecsStreamProvenance`

`public sealed class SecsStreamProvenance`. How much of one stream the source behind this catalogue
actually documented - the question to ask before writing a codec against it.

| Member | Signature | Notes |
| --- | --- | --- |
| `Stream` | `byte { get; }` | |
| `MessageCount` | `int { get; }` | How many messages the catalogue has for the stream. |
| `RepresentativeCount` | `int { get; }` | How many the source marked as a subset. |
| `DefinedCount` | `int { get; }` | `MessageCount - RepresentativeCount`. |
| `UndefinedDataItems` | `IReadOnlyList<string> { get; }` | The item names this stream's bodies use that the dictionary lacks, ordered. Often the more telling half. |
| `IsEntirelyRepresentative` | `bool { get; }` | The source documented none of it. True for nine streams. |
| `ToString()` | `override string` | `S16 28 message(s), 0 defined, 5 undefined item name(s)`. |

## `SecsCatalogProvenance`

`sealed class`. The same question for the whole table, and it cannot be assembled out of the
per-stream measures: whether a definition is named by **any** message is a fact about every stream
at once. 375 definitions, 236 names the messages use, 162 both - so each unanchored set is larger
than the anchored one.

| Member | Signature | Notes |
| --- | --- | --- |
| `StreamCount` | `int { get; }` | 20. |
| `MessageCount` | `int { get; }` | 524, and the sum of the streams' own counts - a test asserts that. |
| `RepresentativeCount` | `int { get; }` | 405. |
| `DefinedCount` | `int { get; }` | `MessageCount - RepresentativeCount`. |
| `DataItemCount` | `int { get; }` | 375 definitions. |
| `NamedDataItemCount` | `int { get; }` | 236 distinct names the messages use. Empty names are dropped, which is one of **two lines here no test reaches**: no row in this data has a trailing comma in its item list. |
| `UndefinedDataItems` | `IReadOnlyList<string> { get; }` | The 74 names used with no definition, ordered. Exactly the union of every stream's list, asserted. |
| `UnusedDefinitions` | `IReadOnlyList<string> { get; }` | The 213 definitions no message names, ordered. The **second line no test reaches** is that ordering - the reference lists its dictionary alphabetically already - and removing the same sort from `UndefinedDataItems` does fail, so the assertion is sound and the input is what cannot tell the two apart. |
| `ToString()` | `override string` | `20 streams, 524 messages (405 representative), 375 items: 213 unused, 74 undefined`. |

```csharp
SecsCatalog.TryGetStream(16, out var stream);

stream.Provenance.IsEntirelyRepresentative;  // true
stream.Provenance.UndefinedDataItems;        // JOBSTATE, MTRLID, PJACK, PJID, QPOSN
```

**`IsEntirelyRepresentative` is not a veto.** Nine streams have no definitions - 3, 4, 15, 16, 17,
18, 19, 20 and 21 - and two of them, stream 3 and stream 16, are implemented in this repository
because they survived a check against the dictionary and against their standard's subject matter.
Stream 17 is the one that failed such a check and the [README](README.md) works through why.

The `MessageCount > 0` term in `IsEntirelyRepresentative` is the one line of this type no test
reaches: every stream the catalogue lists has messages, so a stream of none cannot be built through
the public surface. It is there because an empty stream answering `true` would be a claim about
nothing.

## `SecsMessageInfo`

`public sealed class SecsMessageInfo`

| Member | Signature | Notes |
| --- | --- | --- |
| `Stream` / `Function` | `byte { get; }` | |
| `Name` | `string { get; }` | For example `Event Report Send`. |
| `Direction` | `SecsMessageDirection { get; }` | |
| `IsPrimary` | `bool { get; }` | True for every odd function and no even one, across all 524. |
| `ReplyFunction` | `byte? { get; }` | The function that answers this one. `null` on a Stream 9 report, which nothing answers. |
| `DataItems` | `IReadOnlyList<string> { get; }` | The data item names the body uses, in order of appearance. **Not a structural template** - no nesting, no repetition, no optionality. |
| `IsRepresentative` | `bool { get; }` | True for 405 of 524: the reference marks the entry as a subset rather than a full definition. |
| `ToString()` | `override string` | `S6F11 Event Report Send`. |

## `SecsMessageDirection`

`public enum SecsMessageDirection`

| Member | Meaning |
| --- | --- |
| `HostToEquipment` | The host sends it. |
| `EquipmentToHost` | The equipment sends it. |
| `Either` | Both ends may. S1F1 and S1F13 are the common examples. |

## `SecsDataItemInfo`

`public sealed class SecsDataItemInfo`

| Member | Signature | Notes |
| --- | --- | --- |
| `Name` | `string { get; }` | For example `COMMACK`. |
| `FormatToken` | `string { get; }` | The format as the reference writes it. One of eleven tokens across all 375. |
| `Format` | `SecsFormat? { get; }` | The parsed format. Nullable as a contract; never `null` in the current data. |
| `Length` | `string { get; }` | A count, or **an empty string** for the 58 items the reference gives no length - `SVID` and `CEID` among them. Empty means "not stated", not "zero". |
| `Codes` | `IReadOnlyList<SecsDataItemCode> { get; }` | Standard values, or empty. 88 of the 375 are coded. |
| `IsCoded` | `bool { get; }` | |
| `IsNamedByNoMessage` | `bool { get; }` | True for **213 of the 375**: the dictionary defines it and no message in the catalogue uses it. Evidence, not a verdict - E87's `CARRIERSPEC`, `CATTRID`, `CATTRDATA`, `PORTACTION` and `PODID` are all in here and E87 ships on other grounds. What it rules out is reading such an item *in context*, since the reference gives none. |
| `MeaningOf` | `string? MeaningOf(string value)` | Exact match only: a value inside a documented range such as `7-63` is not found. |
| `ToString()` | `override string` | `COMMACK B[1]`. |

## `SecsDataItemCode`

`public sealed class SecsDataItemCode`

| Member | Signature | Notes |
| --- | --- | --- |
| `Value` | `string { get; }` | Text, because some entries are ranges such as `7-63`. |
| `Meaning` | `string { get; }` | |
| `ToString()` | `override string` | `0 = Accepted`. |

Values are text rather than numbers on purpose. E5 documents reserved and equipment-specific
regions as ranges, and a `byte` could not hold `7-63` without inventing an answer about which value
in it was meant.
