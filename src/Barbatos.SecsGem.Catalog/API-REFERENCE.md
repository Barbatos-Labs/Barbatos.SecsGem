# Barbatos.SecsGem.Catalog - API reference

Namespace `Barbatos.SecsGem.Catalog`. Concepts, provenance and the caveats that go with it live in
[`README.md`](README.md); this page is member-by-member.

- [`SecsCatalog`](#secscatalog)
- [`SecsCatalogExtensions`](#secscatalogextensions)
- [`SecsStreamInfo`](#secsstreaminfo)
- [`SecsStreamAnchoring`](#secsstreamanchoring)
- [`SecsDataItemRival`](#secsdataitemrival)
- [`SecsNameRelation`](#secsnamerelation)
- [`SecsStreamNameClash`](#secsstreamnameclash)
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
| `Anchoring` | `SecsStreamAnchoring { get; }` | **Whose** the documented part is: the stream's own vocabulary or somebody else's. The question to ask second. |
| `RivalSpellings` | `IReadOnlyList<SecsDataItemRival> { get; }` | For each of this stream's gaps, any definition that plainly means the same thing. The question to ask third, and empty for seventeen of the twenty streams. |
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

## `SecsStreamAnchoring`

`sealed class`. The third provenance measure, and the one every chapter of this repository computed
by hand before it was a property. `SecsStreamProvenance` says *how much* of a stream the source
documented; `SecsDataItemInfo.IsNamedByNoMessage` says whether anything uses a definition. This
says, of the names a stream uses, **which are its own and which are borrowed, and whether its gaps
are its own or everybody's** - two independent facts per name, so four buckets that partition
exactly.

| Member | Signature | Notes |
| --- | --- | --- |
| `Stream` | `byte { get; }` | |
| `OwnAnchors` | `IReadOnlyList<string> { get; }` | Defined, and named by this stream alone - its own documented vocabulary. Stream 12 has 27; stream 19 has none. |
| `BorrowedAnchors` | `IReadOnlyList<string> { get; }` | Defined, and shared - firm ground that is somebody else's. Stream 17 has 14 of these and no own ones, which is what an extrapolation looks like counted. |
| `SharedGaps` | `IReadOnlyList<string> { get; }` | Undefined, and named by other streams too - the source's habit rather than this stream's hole. All seven block-transfer items are these. |
| `OwnGaps` | `IReadOnlyList<string> { get; }` | Undefined, and named by this stream alone - nothing anywhere to read them against. `SSID` and `DSDATA` are these. |
| `DataItemCount` | `int { get; }` | The four added. Empty names are not counted, and that filter is the one line here no test reaches. |
| `HasNoOwnVocabulary` | `bool { get; }` | True for six streams: 8, 15, 16, 17, 19, 20. **Evidence, not a verdict** - 8 and 16 are implemented here. |
| `ToString()` | `override string` | `S19 5 borrowed anchor(s), 0 own, 1 shared gap(s), 5 own`. |

The gap split is the half that has already decided two questions. `BLKNUM` is a **shared** gap, so
the blocked transfers of streams 8 and 13 stay out - that vocabulary was never written down
anywhere. `DSDATA` is an **own** gap, a hole in one row of a stream whose siblings' payloads are
defined, so a data set is carried untouched instead. Same word in the older measure; different
answer in this one.

## `SecsDataItemRival`

`public sealed class SecsDataItemRival`

A name a message uses that the dictionary does not define, beside a definition that plainly means
the same thing. **The fourth provenance measure**, and the only one that has gone on to find a
fault in shipping code.

| Member | Signature | Notes |
| --- | --- | --- |
| `UsedName` | `string { get; }` | The name a message uses and no definition covers. |
| `DefinedName` | `string { get; }` | The definition it resembles. |
| `Relation` | `SecsNameRelation { get; }` | How the two resemble each other. |
| `Streams` | `IReadOnlyList<byte> { get; }` | The streams whose rows use `UsedName`, ordered. |
| `IsDefinitionUsedElsewhere` | `bool { get; }` | Whether some message names `DefinedName` too. `false` is the milder case - a spelling the reference abandoned. `true` means both are live, so the resemblance is probably a collision; the current data has one, `VALACK` against `VLAACK`. |
| `ToString()` | `override string` | `RCPIDNEW (S15) is an anagram of RCPNEWID, which no message names`. |

**A rival is not permission to read one name as the other** - it is the opposite. Where a gap has a
rival, reading a format off a sibling means choosing between two spellings of one item while the
source's own spelling sits unused, which is correcting the reference rather than reading it.

All eight in the current data are recipe names. Seven are stream 15's, and they are what kept the
rename and the copy out of `Barbatos.SecsGem.Recipes`.

**Four bounds keep the measure from being noise and only one of them bites.** A prefix relation
needs a stem of at least four characters, which is what drops `LOC` against `LOCKID` - a location
against a lock - and a one-character item name the reference carries. The other three bounds are
unreachable with this data, which a test asserts rather than claims, so a refresh that makes one
matter fails it and says which.

## `SecsNameRelation`

`public enum SecsNameRelation : byte`

| Member | Value | Meaning |
| --- | --- | --- |
| `Anagram` | 0 | The same letters, reordered - `RCPIDNEW` against `RCPNEWID`. Three in the whole reference, and hard to reach by coincidence. |
| `DefinitionExtendsUsed` | 1 | The definition is the used name plus a little - `RCPVER` against `RCPVERS`. |
| `UsedExtendsDefinition` | 2 | The used name is the definition plus a little - `RCPSTATE` against `RCPSTAT`. |

A prefix relation is a candidate, not a conclusion; an anagram is close to one. The relation is
reported rather than flattened so a reader can tell them apart.

## `SecsStreamNameClash`

`sealed class`. Stream names that more than one stream carries. There is **one** in the current
data, and it is the headline of a refusal: streams 15 and 19 are both called `Recipe Management`.

| Member | Signature | Notes |
| --- | --- | --- |
| `Name` | `string { get; }` | Matched without regard to case - the one non-ordinal comparison in this repository, because a stream name is prose and not an identifier. A line no test reaches: the one clash is an exact match. |
| `Streams` | `IReadOnlyList<byte> { get; }` | The streams carrying it, ordered. |
| `ToString()` | `override string` | `"Recipe Management" is S15 and S19`. |

A shared name is not a fault by itself. What makes this one worth a type is what sits underneath:
the two streams use **all twenty of the same function numbers**, four of them for the identically
named transaction and the rest shifted - so S19F11 is a recipe *delete* where S15F11 is a recipe
*verify*. Neither stream has one documented row. See the repository roadmap for why that closed the
question rather than opening it.

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
| `StreamNameClashes` | `IReadOnlyList<SecsStreamNameClash> { get; }` | Names more than one stream carries. One entry: `Recipe Management`, streams 15 and 19. |
| `RivalSpellings` | `IReadOnlyList<SecsDataItemRival> { get; }` | The eight gaps the source also wrote down under another name, ordered. All eight are recipe names. |
| `DetailedUnusedDefinitions` | `IReadOnlyList<string> { get; }` | The 44 unused definitions that carry a table of code values, ordered - specified in full and wired to nothing. `ACKC15` is one. |
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
| `Streams` | `IReadOnlyList<byte> { get; }` | The streams whose messages name this item, ordered; empty when none do. The most-reused measurement in this repository: `GRANT` is named across five streams, `DATAID` across seven, and `SSID` across one. Empty exactly when `IsNamedByNoMessage` is true, which a test asserts item for item. |
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
