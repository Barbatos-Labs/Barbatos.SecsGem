# Barbatos.SecsGem.Material - API reference

Namespace `Barbatos.SecsGem.Material`, except where noted. Concepts, worked examples and the
provenance warning live in [`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`MaterialEquipment`](#materialequipment)
  - [`MaterialHost`](#materialhost)
- [The inventory](#the-inventory)
  - [`IMaterialSource`](#imaterialsource)
  - [`MaterialInMemorySource`](#materialinmemorysource)
- [The model](#the-model)
  - [`MaterialLocation`](#materiallocation)
  - [`MaterialStatus`](#materialstatus)
  - [`MaterialCompletion`](#materialcompletion)
  - [`MaterialTransfer`](#materialtransfer)
  - [`MaterialStatusChange`](#materialstatuschange)
  - [`MaterialMovement`](#materialmovement)
- [Codes and events](#codes-and-events)
  - [`MaterialAcknowledgement`](#materialacknowledgement)
  - [`MaterialGrant`](#materialgrant)
  - [`MaterialLocationEventArgs`](#materiallocationeventargs)
  - [`MaterialStatusChangeEventArgs`](#materialstatuschangeeventargs)
  - [`MaterialMovementEventArgs`](#materialmovementeventargs)
- [Registration](#registration)

## The two ends

### `MaterialEquipment`

`sealed class`. Answers the host's five questions out of an [`IMaterialSource`](#imaterialsource),
and sends the four reports the host does not ask for. Not disposable - no state, no timer, nothing
to release.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `MaterialEquipment(IMaterialSource source, HsmsConnection connection, ILogger<MaterialEquipment>? logger = null)` | Does not run the connection; uses it. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S3F1, S3F3, S3F9, S3F11, S3F15. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Asks the source, then replies. |
| `ReportFoundAsync` | `ValueTask<MaterialAcknowledgement> ReportFoundAsync(MaterialLocation, CancellationToken = default)` | S3F5. |
| `ReportLostAsync` | `ValueTask<MaterialAcknowledgement> ReportLostAsync(MaterialLocation, CancellationToken = default)` | S3F7. The location is where it was last seen. |
| `ReportStatusChangeAsync` | `ValueTask<MaterialAcknowledgement> ReportStatusChangeAsync(MaterialStatusChange, CancellationToken = default)` | S3F13. |
| `ReportMovementAsync` | `ValueTask<MaterialAcknowledgement> ReportMovementAsync(MaterialMovement, CancellationToken = default)` | S3F35. The arrival an accepted S3F11 promised. |

A reply with no acknowledgement code throws `SecsException`, never `Accepted`. A body this end
cannot read is answered `Denied` on S3F12, `DuplicateOrInvalid` on S3F16, and a zero-length list on
S3F10 - which carries material and a location and has no code in it to refuse with.

### `MaterialHost`

`sealed class`. Asks the five questions, and is told the four things. Not disposable.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `MaterialHost(HsmsConnection connection, ILogger<MaterialHost>? logger = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S3F5, S3F7, S3F13, S3F35. |
| `MaterialFound` | `event EventHandler<MaterialLocationEventArgs>?` | S3F5. |
| `MaterialLost` | `event EventHandler<MaterialLocationEventArgs>?` | S3F7. |
| `MaterialStatusChanged` | `event EventHandler<MaterialStatusChangeEventArgs>?` | S3F13. |
| `MaterialMoved` | `event EventHandler<MaterialMovementEventArgs>?` | S3F35. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Raises the event, then sends whatever the handler left on it. |
| `RequestStatusAsync` | `ValueTask<IReadOnlyList<MaterialStatus>> RequestStatusAsync(CancellationToken = default)` | S3F1. Empty means the machine is empty. |
| `RequestTimeToCompletionAsync` | `ValueTask<IReadOnlyList<MaterialCompletion>> RequestTimeToCompletionAsync(CancellationToken = default)` | S3F3. |
| `FindMaterialAsync` | `ValueTask<MaterialLocation?> FindMaterialAsync(string materialId, CancellationToken = default)` | S3F9. `null` means the machine does not have it. |
| `RequestTransferAsync` | `ValueTask<MaterialAcknowledgement> RequestTransferAsync(MaterialTransfer, CancellationToken = default)` | S3F11. Accepted means taken, not arrived. |
| `InquireMultiBlockAsync` | `ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken = default)` | S3F15. |

A report this end cannot read is answered `Denied` inside the transaction the equipment is already
waiting on. A host that subscribes to nothing still answers every report with `Accepted`.

## The inventory

### `IMaterialSource`

`interface`. Five methods, one per question the host can ask. Each is awaited inside a transaction,
so none should take long.

| Member | Signature | Message |
| --- | --- | --- |
| `GetStatusAsync` | `ValueTask<IReadOnlyList<MaterialStatus>> GetStatusAsync(CancellationToken)` | S3F1 |
| `GetTimeToCompletionAsync` | `ValueTask<IReadOnlyList<MaterialCompletion>> GetTimeToCompletionAsync(CancellationToken)` | S3F3 |
| `FindAsync` | `ValueTask<MaterialLocation?> FindAsync(string materialId, CancellationToken)` | S3F9; `null` answers a zero-length list |
| `TransferAsync` | `ValueTask<MaterialAcknowledgement> TransferAsync(MaterialTransfer, CancellationToken)` | S3F11 |
| `InquireMultiBlockAsync` | `ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken)` | S3F15 |

### `MaterialInMemorySource`

`sealed class : IMaterialSource`. Keyed by material identifier, compared with
`StringComparer.Ordinal`.

| Member | Signature | Notes |
| --- | --- | --- |
| `Load` | `bool Load(string materialId, uint location, byte quantity = 1, uint timeToCompletion = 0)` | `false` when full. |
| `Unload` | `bool Unload(string materialId)` | `false` when it was not there. |
| `Clear` | `void Clear()` | |
| `Capacity` | `int { get; set; }` | 64 by default. |
| `Count` | `int { get; }` | |
| `Grant` | `MaterialGrant { get; set; }` | `Accepted` by default. |
| `TransferAnswer` | `MaterialAcknowledgement { get; set; }` | `Accepted` by default; a denial leaves the material where it is. |

A transfer it accepts **moves** the material, so a later S3F9 reports the new location. Material it
does not hold is denied whatever `TransferAnswer` says - accepting would leave the host waiting for
an S3F35 that cannot come.

## The model

### `MaterialLocation`

`sealed class`. `MaterialId` (`required string`, `MID`) and `Location` (`uint`, `LOC`).

### `MaterialStatus`

`sealed class`. `MaterialId`, `Location`, and `Quantity` (`byte`, `QUA` - one byte, no code table
in the catalogue, so nothing here interprets it).

### `MaterialCompletion`

`sealed class`. `MaterialId`, `Location`, and `TimeToCompletion` (`uint`, `TTC`) - a number in
whatever unit the machine counts in, deliberately not a `TimeSpan`.

### `MaterialTransfer`

`sealed class`. `MaterialId` and `Destination` (`uint`, `DESTLOC` - **inferred as `U4`**; see the
README).

### `MaterialStatusChange`

`sealed class`. `MaterialId`, `Location`, and `Status` (`byte`, `MATLSTATUS` - **inferred as
`B[1]`**, codes undefined by the catalogue and so unnamed here).

### `MaterialMovement`

`sealed class`. `MaterialId`, `Location` (where it came from) and `Destination` (where it went).

## Codes and events

### `MaterialAcknowledgement`

`enum : byte` - `ACKC3`. `Accepted = 0`, `Denied = 1`. E5 defines both and no others, and no error
text anywhere in this half of the stream, so a denial cannot say why.

### `MaterialGrant`

`enum : byte` - `GRANT`. `Accepted = 0`, `Busy = 1`, `NoSpace = 2`, `DuplicateOrInvalid = 3`,
`Other = 4`. Codes 4 to 63 are the equipment's to define; all of them read as `Other` and are
logged.

### `MaterialLocationEventArgs`

`sealed class : EventArgs`. `Material` (`required MaterialLocation`) and `Acknowledgement`
(`MaterialAcknowledgement`, settable, `Accepted` by default). Used by both `MaterialFound` and
`MaterialLost`.

### `MaterialStatusChangeEventArgs`

`sealed class : EventArgs`. `Change` (`required MaterialStatusChange`) and `Acknowledgement`.

### `MaterialMovementEventArgs`

`sealed class : EventArgs`. `Movement` (`required MaterialMovement`) and `Acknowledgement`.

The payload on all three is valid for the life of the handler.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddMaterialEquipment()` | `MaterialEquipment`, `MaterialInMemorySource` as `IMaterialSource`, and handlers for S3F1, F3, F9, F11, F15. |
| `AddMaterialEquipment<TSource>()` | The same with an inventory of your own. `TryAdd`, so an `IMaterialSource` already registered wins. |
| `AddMaterialHost()` | `MaterialHost` and handlers for S3F5, F7, F13, F35. |

Either called twice is harmless: the messages go in through the builder's `DeclareOnce`, which
declares a capability's set once however many times it is asked for.

Do not call the equipment and the host on one builder, though. They declare *different* messages,
so neither the routing table nor `DeclareOnce` can catch the mistake - and one process answering as
both ends of one link is not a thing E5 describes.
