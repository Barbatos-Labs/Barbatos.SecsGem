# Barbatos.SecsGem.WaferMap

Wafer mapping - SECS-II stream 12 - for **.NET 8, 9 and 10**: map setup data, the three map data
formats, the transmit handshake, maps in either direction, and every code table as an enum rather
than a byte.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.WaferMap
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 12 is E5, not
E30: it has no state model, defends no control state and needs nothing that GEM owns. A prober that
maps wafers without implementing E30 can use this on its own, and a machine that does both adds it
beside `AddGemEquipment` on the same connection.

## The directions are inverted

This is the first thing to know and it catches everybody once. In GEM the host asks and the
equipment answers. In stream 12 the **equipment** sends every odd function and the host answers
every even one:

| | |
| --- | --- |
| S12F1 / F2 | equipment sends map setup data, host acknowledges |
| S12F3 / F4 | equipment asks for map setup data, host sends it |
| S12F5 / F6 | equipment asks whether it may send a map, host grants or refuses |
| S12F7 / F8 | equipment sends map data type 1 (rows), host acknowledges |
| S12F9 / F10 | equipment sends map data type 2 (one run of bins), host acknowledges |
| S12F11 / F12 | equipment sends map data type 3 (coordinates), host acknowledges |
| S12F13 / F14 | equipment asks for map data type 1, host sends it |
| S12F15 / F16 | equipment asks for map data type 2, host sends it |
| S12F17 / F18 | equipment asks for map data type 3, host sends it |
| S12F19 | either end reports on a map; no reply |

A prober knows which die it probed, and a host knows which wafer it wants mapped. So the equipment
is the active party throughout, and `WaferMapEquipment` is the end with the methods on it.

## The equipment: sending a map

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddWaferMapEquipment()
    .RunAsHostedService();
```

```csharp
var mapping = provider.GetRequiredService<WaferMapEquipment>();

// Tell the host how to read the map that follows. Fifteen fields, and the acknowledgement comes
// back as something that cannot be read backwards.
var setup = await mapping.SendSetupAsync(new WaferMapSetup
{
    MaterialId = "W01",
    IdType = WaferMapIdType.WaferIdAndOrientation,
    FirstLocation = 2,
    FlatRotation = 270,
    OriginLocation = 3,
    ReferencePoints = [new WaferMapPosition(-15_000, 0), new WaferMapPosition(15_000, 60_000)],
    DieUnits = "um",
    DieSizeX = 5_400,
    DieSizeY = 7_200,
    RowCount = 41,
    ColumnCount = 37,
    NullBinCode = '.',
    ProductId = "BRBTOS-A7",
});

if (setup is WaferMapSetupAcknowledgement.Rejected)
{
    return;   // and the host logged the vendor-specific code it sent, because E5 names none
}

// Ask before a large one. The inquire carries the location count, so a host that is not ready
// says so for the price of two small messages instead of a quarter of a million bins.
var grant = await mapping.InquireTransmitAsync(new WaferMapTransmitInquiry
{
    MaterialId = "W01",
    MapFormat = "1",
    MapLocationCount = 1_517,
});

if (grant is not WaferMapTransmitGrant.Granted)
{
    return;   // Busy, NotInterested, InvalidMaterial, or a code E5 reserved and did not name
}

// Then the map. Row data is the compact one: a row probed right to left is an increment of -1,
// and the bins need no coordinates of their own.
var sent = await mapping.SendRowDataAsync(new WaferMapRowData
{
    MaterialId = "W01",
    IdType = WaferMapIdType.WaferIdAndOrientation,
    Rows =
    [
        new WaferMapRow { Row = 0, StartColumn = 3, Increment = 1, Bins = new byte[] { 1, 1, 2 } },
        new WaferMapRow { Row = 1, StartColumn = 9, Increment = -1, Bins = new byte[] { 2, 0, 1 } },
    ],
});
```

`MaterialId` and the bins are the only fields with no sensible default, and `Row` is the only one
of a row. Everything else has one, so a machine that does not report a flat rotation leaves it out
rather than sending a zero that means something.

## Three formats, and when each is the right one

| Format | Message | Shape | Use it when |
| --- | --- | --- | --- |
| Type 1 - rows | S12F7 | one entry per row: row number, start column, increment, bins | the normal case; probing is row by row |
| Type 2 - array | S12F9 | one starting coordinate, then every bin in order | the whole map, smallest on the wire, both ends already agree on the row and column counts |
| Type 3 - coordinates | S12F11 | a coordinate and a run of bins, repeated | the map has holes, or only the good die are worth sending |

Type 2 is the least forgiving: there is nothing in the body to anchor a bin to a location beyond
the starting point, so a bin count that does not match `ROWCT * COLCT` is the usual cause of a
`WaferMapError.SlotCountInvalid` coming back on an S12F19.

Type 3 is the only format that can leave a location out, which is what a request for
`WaferMapBinSelection.NonNullOnly` asks for: a wafer with forty good die reported that way is forty
entries rather than a full array of bins.

## The equipment: asking for a map

A prober that is told which wafer it has, and asks the host what to do with it:

```csharp
var map = await mapping.RequestRowDataAsync(new WaferMapDataRequest { MaterialId = "W01" });

if (map is null)
{
    // Not a failure. E5 answers "I have no map for that material" with a zero-length list, and
    // S12F14 has no acknowledgement code in it to say anything else.
    return;
}

foreach (var row in map.Rows)
{
    var column = row.StartColumn;

    foreach (var bin in row.Bins.Span)
    {
        Probe(column, row.Row, bin);
        column += row.Increment;
    }
}
```

## The host: answering

The host side is nine methods behind one interface, and between them they are the whole of what a
host does in stream 12:

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddWaferMapHost<MyMapStore>()
    .RunAsHostedService();
```

```csharp
public sealed class MyMapStore : IWaferMapStore
{
    public async ValueTask<WaferMapDataAcknowledgement> SaveRowDataAsync(
        WaferMapRowData data,
        CancellationToken cancellationToken)
    {
        // The map is here, in full, and the equipment is holding its T3 open waiting for this
        // method to come back. Queue the work; do not write a quarter of a million bins to a
        // database before acknowledging.
        _incoming.Writer.TryWrite(data);

        return WaferMapDataAcknowledgement.Accepted;
    }

    public ValueTask<WaferMapRowData?> LoadRowDataAsync(
        WaferMapDataRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(_maps.GetValueOrDefault(request.MaterialId));

    // ... seven more: the other two formats in each direction, setup data both ways, and the grant
}
```

There is no "a map arrived" event beside these methods, deliberately. An application that cares
about a map needs the map, and a store method already has it, can refuse it, and is awaited before
the acknowledgement goes back - none of which an event raised beside the transaction could do.

`AddWaferMapHost()` with no type argument uses `WaferMapInMemoryStore`, which is enough for a
simulator or a host that passes maps straight on. It does not convert between the three formats: a
map saved as rows is handed back as rows and `LoadArrayDataAsync` answers `null`, because
converting would mean inventing the row and column counts for locations nobody sent.

## A body that cannot be read is refused, not dropped

S12F1 and the three map data sends are answered with a rejection, so the equipment learns inside
the transaction it is already waiting on rather than waiting out its T3. The four requests have no
such code in their replies - E5 gives S12F4, S12F14, S12F16 and S12F18 nothing but data - so an
unreadable request is answered with the zero-length list that means "no map", and
`ReportErrorAsync(WaferMapError.FormatError)` is there for an application that wants to say why. It
is not sent automatically: an unasked-for primary is a decision, not a detail.

In the other direction, a reply that carries no acknowledgement code is a `SecsException` and never
an acceptance. A reader that takes the first byte of an empty body gets nought, and nought means
accepted - which is the one failure a caller cannot see.

## S12F19 is not only an error

```csharp
mapping.ErrorReported += (_, args) =>
{
    if (args.Error is WaferMapError.ReceivedCorrectly)
    {
        // A confirmation. For a map sent with none of the acknowledged functions it is the only
        // one either end gets, and treating every S12F19 as a fault has it backwards.
        return;
    }

    Log(args.Error, args.Location);
};
```

Either end may send it, so both `WaferMapEquipment` and `WaferMapHost` have the event and the
`ReportErrorAsync` method.

## Coordinates, and the one place this throws

`REFP` is `I4` and takes the full range of `int`. A coordinate inside map data - `STRP` and
`XYPOS` - is `I2`, so sending one outside [-32768, 32767] throws `ArgumentOutOfRangeException`
rather than wrapping a die round to the other side of the wafer. That is the kind of bug that turns
up in a bin summary a week later, so it is a throw and not a clamp.

Counts read the other way: `ROWCT` arriving as a `U4` with four billion in it is a bug a refused
map will not help anybody find, so a count is clamped and carries on. The clamp is visible in the
data; a thrown exception in the middle of a map transfer is not.

## What came from the standard and what did not

The item lists come from `Barbatos.SecsGem.Catalog`, which flags S12F1 and S12F19 as a
representative subset and the other seventeen functions as definitions. What a flat item list
cannot express is nesting, and four bodies need it - S12F7, S12F11, S12F14 and S12F18 each carry a
repeating pair, and a body with one `RSINF` beside one `BINLT` could describe exactly one row,
which is not a map. So this library writes them as a list of two-element entries, and reads three
shapes: that one, a flattened list of alternating descriptors and bins, and the two items left
bare in the body. If a vendor nests differently again,
[`Internal/WaferMapCodec.cs`](Internal/WaferMapCodec.cs) is the only file to change.

The same file is where formats are read tolerantly and written exactly. Writing follows the
catalogue, because a receiver is entitled to the format the standard names. Reading accepts any
numeric format for a numeric field, either `B` or `U1` for bins, and `NULBC` as a number as well as
the character E5 writes it as - because a map refused over a `U2` where a `U4` was expected is a
wafer nobody can probe.

## What is not here yet

- **Nothing of stream 12 is missing.** All nineteen functions are implemented at both ends.
- Carrier and substrate mapping as E87 models it, which is a different standard with a different
  object model.
- A store that outlives the process. `WaferMapInMemoryStore` is in memory, as it says; the
  interface is nine methods, and a database behind it is an afternoon.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
