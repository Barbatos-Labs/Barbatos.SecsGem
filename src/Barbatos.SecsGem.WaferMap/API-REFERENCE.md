# Barbatos.SecsGem.WaferMap - API reference

Namespace `Barbatos.SecsGem.WaferMap`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`WaferMapEquipment`](#wafermapequipment)
  - [`WaferMapHost`](#wafermaphost)
- [The store](#the-store)
  - [`IWaferMapStore`](#iwafermapstore)
  - [`WaferMapInMemoryStore`](#wafermapinmemorystore)
- [Setup data](#setup-data)
  - [`WaferMapSetup`](#wafermapsetup)
  - [`WaferMapSetupRequest`](#wafermapsetuprequest)
  - [`WaferMapTransmitInquiry`](#wafermaptransmitinquiry)
- [Map data](#map-data)
  - [`WaferMapPosition`](#wafermapposition)
  - [`WaferMapRow`](#wafermaprow)
  - [`WaferMapRowData`](#wafermaprowdata)
  - [`WaferMapArrayData`](#wafermaparraydata)
  - [`WaferMapPoint`](#wafermappoint)
  - [`WaferMapPointData`](#wafermappointdata)
  - [`WaferMapDataRequest`](#wafermapdatarequest)
- [Codes](#codes)
  - [`WaferMapIdType`](#wafermapidtype)
  - [`WaferMapSetupAcknowledgement`](#wafermapsetupacknowledgement)
  - [`WaferMapDataAcknowledgement`](#wafermapdataacknowledgement)
  - [`WaferMapTransmitGrant`](#wafermaptransmitgrant)
  - [`WaferMapBinSelection`](#wafermapbinselection)
  - [`WaferMapError`](#wafermaperror)
  - [`WaferMapErrorEventArgs`](#wafermaperroreventargs)
- [Registration](#registration)

## The two ends

### `WaferMapEquipment`

`sealed class`. The end that sends maps and asks for them: every odd function of stream 12. Not
disposable - it holds nothing that needs releasing.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `WaferMapEquipment(HsmsConnection connection, ILogger<WaferMapEquipment>? logger = null)` | Does not run the connection; uses it. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | One pair: S12F19. |
| `ErrorReported` | `event EventHandler<WaferMapErrorEventArgs>?` | Raised for an arriving S12F19, `ReceivedCorrectly` included. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Answers S12F19 by raising the event. Sends nothing back; E5 defines no reply. |
| `SendSetupAsync` | `ValueTask<WaferMapSetupAcknowledgement> SendSetupAsync(WaferMapSetup, CancellationToken = default)` | S12F1. Throws `SecsException` if S12F2 carried no `SDACK`. |
| `RequestSetupAsync` | `ValueTask<WaferMapSetup?> RequestSetupAsync(WaferMapSetupRequest, CancellationToken = default)` | S12F3. `null` means the host has no setup for that material. |
| `InquireTransmitAsync` | `ValueTask<WaferMapTransmitGrant> InquireTransmitAsync(WaferMapTransmitInquiry, CancellationToken = default)` | S12F5. Throws `SecsException` if S12F6 carried no `GRNT1`. |
| `SendRowDataAsync` | `ValueTask<WaferMapDataAcknowledgement> SendRowDataAsync(WaferMapRowData, CancellationToken = default)` | S12F7, map data type 1. |
| `SendArrayDataAsync` | `ValueTask<WaferMapDataAcknowledgement> SendArrayDataAsync(WaferMapArrayData, CancellationToken = default)` | S12F9, map data type 2. |
| `SendPointDataAsync` | `ValueTask<WaferMapDataAcknowledgement> SendPointDataAsync(WaferMapPointData, CancellationToken = default)` | S12F11, map data type 3. |
| `RequestRowDataAsync` | `ValueTask<WaferMapRowData?> RequestRowDataAsync(WaferMapDataRequest, CancellationToken = default)` | S12F13. `null` means no map. |
| `RequestArrayDataAsync` | `ValueTask<WaferMapArrayData?> RequestArrayDataAsync(WaferMapDataRequest, CancellationToken = default)` | S12F15. |
| `RequestPointDataAsync` | `ValueTask<WaferMapPointData?> RequestPointDataAsync(WaferMapDataRequest, CancellationToken = default)` | S12F17. The one request that carries `SDBIN`. |
| `ReportErrorAsync` | `ValueTask ReportErrorAsync(WaferMapError, byte location = 0, CancellationToken = default)` | S12F19. Posted, not sent: no reply is expected. |

The three `Send...DataAsync` methods throw `ArgumentOutOfRangeException` for a coordinate, row,
start column or increment outside `I2`. A reply that is not the shape E5 defines is a
`SecsException`, never a zero that reads as accepted.

### `WaferMapHost`

`sealed class`. The end that answers: all ten primaries, decoded and handed to an
[`IWaferMapStore`](#iwafermapstore). Not disposable.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `WaferMapHost(IWaferMapStore store, HsmsConnection connection, ILogger<WaferMapHost>? logger = null)` | The connection is for S12F19, the only primary this end sends. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | Ten pairs: S12F1, F3, F5, F7, F9, F11, F13, F15, F17, F19. |
| `ErrorReported` | `event EventHandler<WaferMapErrorEventArgs>?` | Raised for an arriving S12F19. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Asks the store, then replies. The store is asked before the reply bit is looked at, so a map sent without it is still stored. |
| `ReportErrorAsync` | `ValueTask ReportErrorAsync(WaferMapError, byte location = 0, CancellationToken = default)` | S12F19. The only way to report what an acknowledgement code cannot say. |

A body this end cannot read is answered `Rejected` on S12F2, S12F8, S12F10 and S12F12,
`InvalidMaterial` on S12F6, and a zero-length list on S12F4, S12F14, S12F16 and S12F18 - those four
replies carry data and nothing else, so E5 leaves no code in them for a refusal.

## The store

### `IWaferMapStore`

`interface`. Nine methods, and between them the whole of what a host does in stream 12. Each is
asked inside a transaction the equipment is waiting on, so none should take long.

| Member | Signature | Message |
| --- | --- | --- |
| `SaveSetupAsync` | `ValueTask<WaferMapSetupAcknowledgement> SaveSetupAsync(WaferMapSetup, CancellationToken)` | S12F1 |
| `LoadSetupAsync` | `ValueTask<WaferMapSetup?> LoadSetupAsync(WaferMapSetupRequest, CancellationToken)` | S12F3; `null` answers a zero-length list |
| `InquireTransmitAsync` | `ValueTask<WaferMapTransmitGrant> InquireTransmitAsync(WaferMapTransmitInquiry, CancellationToken)` | S12F5 |
| `SaveRowDataAsync` | `ValueTask<WaferMapDataAcknowledgement> SaveRowDataAsync(WaferMapRowData, CancellationToken)` | S12F7 |
| `SaveArrayDataAsync` | `ValueTask<WaferMapDataAcknowledgement> SaveArrayDataAsync(WaferMapArrayData, CancellationToken)` | S12F9 |
| `SavePointDataAsync` | `ValueTask<WaferMapDataAcknowledgement> SavePointDataAsync(WaferMapPointData, CancellationToken)` | S12F11 |
| `LoadRowDataAsync` | `ValueTask<WaferMapRowData?> LoadRowDataAsync(WaferMapDataRequest, CancellationToken)` | S12F13 |
| `LoadArrayDataAsync` | `ValueTask<WaferMapArrayData?> LoadArrayDataAsync(WaferMapDataRequest, CancellationToken)` | S12F15 |
| `LoadPointDataAsync` | `ValueTask<WaferMapPointData?> LoadPointDataAsync(WaferMapDataRequest, CancellationToken)` | S12F17; reads `BinSelection` |

### `WaferMapInMemoryStore`

`sealed class : IWaferMapStore`. Keyed by material identifier, compared with
`StringComparer.Ordinal`.

| Member | Signature | Notes |
| --- | --- | --- |
| `Capacity` | `int { get; set; }` | 64 by default. A save beyond it is `Rejected`, which the equipment can see. |
| `Count` | `int { get; }` | How many materials have a setup or a map held. |
| `Grant` | `WaferMapTransmitGrant { get; set; }` | `Granted` by default. Set it to `Busy` to exercise the other end of the handshake. |
| `Clear` | `void Clear()` | Forgets everything. |

It does not convert between the three formats: a map saved as rows is handed back by
`LoadRowDataAsync` and `LoadArrayDataAsync` answers `null`. `LoadPointDataAsync` honours
`NonNullOnly` when a setup is held for that material, splitting a partly-null run so the bins that
hold a die keep their own coordinates; with no setup there is no null bin code to filter by, so the
whole map goes back.

## Setup data

### `WaferMapSetup`

`sealed class`. Carried by S12F1 and S12F4, which are not the same fifteen fields:

```
S12F1  equipment -> host   MID IDTYP FNLOC FFROT ORLOC RPSEL REFP DUTMS XDIES YDIES ROWCT COLCT NULBC PRDCT PRAXI
S12F4  host -> equipment   MID IDTYP FNLOC       ORLOC RPSEL REFP DUTMS XDIES YDIES ROWCT COLCT NULBC PRDCT       BCEQU MLCL
```

| Member | Type | Item | Notes |
| --- | --- | --- | --- |
| `MaterialId` | `required string` | `MID` | |
| `IdType` | `WaferMapIdType` | `IDTYP` | |
| `FirstLocation` | `byte` | `FNLOC` | A code E5 defines and the catalogue does not enumerate. |
| `FlatRotation` | `ushort?` | `FFROT` | **S12F1 only.** `null` means the message does not carry it. |
| `OriginLocation` | `byte` | `ORLOC` | |
| `ReportPointSelection` | `byte` | `RPSEL` | |
| `ReferencePoints` | `IReadOnlyList<WaferMapPosition>` | `REFP` | `I4`, so the full range of `int`. |
| `DieUnits` | `string` | `DUTMS` | Free text; the standard leaves the vocabulary open. |
| `DieSizeX` | `ushort` | `XDIES` | In `DieUnits`. |
| `DieSizeY` | `ushort` | `YDIES` | |
| `RowCount` | `ushort` | `ROWCT` | |
| `ColumnCount` | `uint` | `COLCT` | |
| `NullBinCode` | `char` | `NULBC` | One character on the wire; compare a bin against `(byte)NullBinCode`. Space by default. |
| `ProductId` | `string` | `PRDCT` | |
| `ProcessAxis` | `byte?` | `PRAXI` | **S12F1 only.** |
| `BinCodeEquivalents` | `ReadOnlyMemory<byte>` | `BCEQU` | **S12F4 only.** |
| `MapLocationCount` | `uint?` | `MLCL` | **S12F4 only.** |

### `WaferMapSetupRequest`

`sealed class`. S12F3: the conventions the equipment wants the answer in.

`MaterialId` (`required string`), `IdType`, `MapFormat` (`string`, `MAPFT` - free text, commonly
`"1"`, `"2"` or `"3"`), `FirstLocation`, `FlatRotation` (`ushort`), `OriginLocation`, `ProcessAxis`,
`BinCodeEquivalents`, `NullBinCode`.

### `WaferMapTransmitInquiry`

`sealed class`. S12F5: `MaterialId` (`required string`), `IdType`, `MapFormat`, `MapLocationCount`
(`uint`). The size is in the inquire so a host can refuse before the map arrives rather than after.

## Map data

### `WaferMapPosition`

`readonly record struct WaferMapPosition(int X, int Y)`. A coordinate, with value semantics.
`REFP` is `I4` and takes the full range; `STRP` and `XYPOS` are `I2`, so a coordinate in map data
outside [-32768, 32767] throws `ArgumentOutOfRangeException` on the way out.

### `WaferMapRow`

`sealed class`. One row: `Row` (`required int`), `StartColumn` (`int`), `Increment` (`int`, **1 by
default**), `Bins` (`ReadOnlyMemory<byte>`, `BINLT`). A row probed right to left is an increment of
`-1`.

### `WaferMapRowData`

`sealed class`. Map data type 1, carried by S12F7 and S12F14: `MaterialId` (`required string`),
`IdType`, `Rows` (`IReadOnlyList<WaferMapRow>`).

### `WaferMapArrayData`

`sealed class`. Map data type 2, carried by S12F9 and S12F16: `MaterialId` (`required string`),
`IdType`, `Start` (`WaferMapPosition`, `STRP`), `Bins` (`ReadOnlyMemory<byte>`).

### `WaferMapPoint`

`sealed class`. `Position` (`required WaferMapPosition`, `XYPOS`) and `Bins`
(`ReadOnlyMemory<byte>`). `Bins` is a run, not a single value: four bins describe the four
locations from `Position` onward, along the row with X increasing.

### `WaferMapPointData`

`sealed class`. Map data type 3, carried by S12F11 and S12F18: `MaterialId` (`required string`),
`IdType`, `Points` (`IReadOnlyList<WaferMapPoint>`). The only format that can leave a location out.

### `WaferMapDataRequest`

`sealed class`. S12F13, S12F15 and S12F17: `MaterialId` (`required string`), `IdType`,
`BinSelection` (`WaferMapBinSelection`). Only S12F17 carries `BinSelection`, because only
coordinate data can leave a location out.

## Codes

### `WaferMapIdType`

`enum : byte` - `IDTYP`. `None = 0`, `WaferId = 1`, `WaferIdAndOrientation = 2`.

### `WaferMapSetupAcknowledgement`

`enum : byte` - `SDACK`. `Accepted = 0`, `Rejected = 1`. E5 makes every code from 1 to 63 a
rejection and leaves the reason to the vendor, so a code above 1 reads as `Rejected` and is logged.

### `WaferMapDataAcknowledgement`

`enum : byte` - `MDACK`. `Accepted = 0`, `Rejected = 1`. E5 defines both and no others.

### `WaferMapTransmitGrant`

`enum : byte` - `GRNT1`. `Granted = 0`, `Busy = 1`, `NotInterested = 2`, `InvalidMaterial = 3`,
`Other = 4`. E5 reserves 4 to 63 and names none of them; every code from 4 upwards reads as `Other`
and is logged.

### `WaferMapBinSelection`

`enum : byte` - `SDBIN`. `AllCoordinates = 0`, `NonNullOnly = 1`.

### `WaferMapError`

`enum : byte` - `MAPER`. `ReceivedCorrectly = 0`, `SlotCountInvalid = 1`, `FormatError = 2`,
`NoCarrierAtLocation = 3`. Code 0 is a confirmation, which is why the type is not called an error
code. A code E5 does not define is dropped rather than renamed.

### `WaferMapErrorEventArgs`

`sealed class : EventArgs`. `Error` (`required WaferMapError`) and `Location`
(`required byte`, `DATLC`).

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`, the type
`AddSecsGem` returns.

| Method | What it adds |
| --- | --- |
| `AddWaferMapEquipment()` | `WaferMapEquipment` as a singleton, and a handler for S12F19. |
| `AddWaferMapHost()` | `WaferMapHost`, `WaferMapInMemoryStore` as `IWaferMapStore`, and a handler for all ten primaries. |
| `AddWaferMapHost<TStore>()` | The same with a store of your own. `TryAdd`, so an `IWaferMapStore` already registered wins. |

Either called twice is harmless - the messages go in through the builder's `DeclareOnce` - but
**do not call both on one builder**: both declare S12F19, and two different sets claiming one
message is a conflict rather than a duplicate, so the routing table still refuses it. That is the
distinction `DeclareOnce` keeps on purpose, and here it is doing its job.

Either sits happily beside `AddGemEquipment` or `AddGemHost`, which share no stream and function
pair with stream 12.
