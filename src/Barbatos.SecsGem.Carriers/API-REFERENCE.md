# Barbatos.SecsGem.Carriers - API reference

Namespace `Barbatos.SecsGem.Carriers`, except where noted. Concepts, the state diagrams and the
provenance warning live in [`README.md`](README.md); this page is member-by-member.

- [`CarrierManager`](#carriermanager)
- [The objects](#the-objects)
  - [`Carrier`](#carrier)
  - [`LoadPort`](#loadport)
  - [`CarrierStorageLocation`](#carrierstoragelocation)
  - [Object types](#carrierobjectprovider)
- [The states](#the-states)
- [Actions](#actions)
  - [`CarrierActions`](#carrieractions)
  - [`CarrierActionRequest`](#carrieractionrequest)
  - [`CarrierActionResult`](#carrieractionresult)
  - [`CarrierAcknowledgement`](#carrieracknowledgement)
  - [`CarrierRequestAcknowledgement`](#carrierrequestacknowledgement)
- [The two ends](#the-two-ends)
  - [`CarrierEquipment`](#carrierequipment)
  - [`CarrierHost`](#carrierhost)
- [`CarrierStatus`](#carrierstatus)
- [`CarrierPortStatus`](#carrierportstatus)
- [`CarrierStorageStatus`](#carrierstoragestatus)
- [`CarrierObjectProvider`](#carrierobjectprovider)
- [Events](#events)
- [Registration](#registration)
- [Outliving the process](#outliving-the-process)

## `CarrierManager`

`sealed class`. The carriers, the load ports, and E87's rules about what may happen to them. No
connection, no transport - which is what makes it testable as a state machine.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `CarrierManager()` | |
| `Carriers` | `IReadOnlyCollection<Carrier> { get; }` | A snapshot, taken under the lock. |
| `Ports` | `IReadOnlyCollection<LoadPort> { get; }` | |
| `StorageLocations` | `IReadOnlyCollection<CarrierStorageLocation> { get; }` | Ordered by `LOC`, because S3F32 reports them. |
| `AddPort` | `LoadPort AddPort(byte portId)` | Declares hardware. Out of service until told otherwise; declaring twice returns the same port and resets nothing. |
| `AddStorageLocation` | `CarrierStorageLocation AddStorageLocation(uint location)` | The same, for a place inside the machine that is not a port. No host action reaches one. |
| `FindCarrier` | `Carrier? FindCarrier(string carrierId)` | |
| `FindPort` | `LoadPort? FindPort(byte portId)` | |
| `FindStorageLocation` | `CarrierStorageLocation? FindStorageLocation(uint location)` | |
| `PlaceCarrier` | `CarrierActionResult PlaceCarrier(byte portId, string carrierId)` | Only from `ReadyToLoad`, and a reserved port takes only its own carrier. |
| `RemoveCarrier` | `CarrierActionResult RemoveCarrier(byte portId)` | Only from `ReadyToUnload`. |
| `SetTransferState` | `CarrierActionResult SetTransferState(byte portId, PortTransferState state)` | The three in-service states. `OutOfService` is refused here - it is a service decision. |
| `SetServiceStatus` | `CarrierActionResult SetServiceStatus(byte portId, bool inService)` | A port holding a carrier cannot go out of service. |
| `StoreCarrier` | `CarrierActionResult StoreCarrier(uint location, string carrierId)` | Only from `ReadyToLoad`. Refused for a carrier that is on a port or in another location - a carrier is in one place. |
| `FetchCarrier` | `CarrierActionResult FetchCarrier(uint location)` | Only from `ReadyToUnload`. |
| `SetStorageTransferState` | `CarrierActionResult SetStorageTransferState(uint location, PortTransferState state)` | A port's three rules, for a shelf. |
| `SetStorageServiceStatus` | `CarrierActionResult SetStorageServiceStatus(uint location, bool inService)` | A location holding a carrier cannot go out of service. |
| `ReadCarrierId` | `CarrierActionResult ReadCarrierId(string carrierId, bool? verified)` | `true` agree, `false` refuse, `null` leave it to the host. Once only. |
| `ReadSlotMap` | `CarrierActionResult ReadSlotMap(string carrierId, ReadOnlyMemory<byte> slotMap, bool? verified)` | The bytes are copied. Sets `SubstrateCount` and, when unset, `Capacity`. |
| `BeginAccess` | `CarrierActionResult BeginAccess(string carrierId)` | Refused unless both verifications are agreed. |
| `EndAccess` | `CarrierActionResult EndAccess(string carrierId, bool stopped = false)` | `CarrierComplete`, or `CarrierStopped`. |
| `Perform` | `CarrierActionResult Perform(CarrierActionRequest request)` | Every carrier and port action. An action it does not know is `InvalidCommand`. |
| `CarrierStateChanged` | `event EventHandler<CarrierStateChangedEventArgs>?` | After the move, inside the lock. |
| `PortStateChanged` | `event EventHandler<PortStateChangedEventArgs>?` | After the move, inside the lock. |
| `StorageStateChanged` | `event EventHandler<StorageStateChangedEventArgs>?` | After the move, inside the lock. One event, because a location has one state machine - so `Previous` and `Current` are `PortTransferState` rather than `int`. |

Every refusal carries an `ErrorText` naming the state that refused it. Handlers must not call back
into the manager - the events are raised under its lock.

## The objects

### `Carrier`

`sealed class`. Public getters, **internal setters**: the only way a carrier changes state is
through the manager.

`CarrierId`, `IdStatus`, `SlotMapStatus`, `AccessStatus`, `PortId` (`byte?`), `StorageLocation`
(`uint?`), `SlotMap` (`ReadOnlyMemory<byte>`), `Capacity`, `SubstrateCount`, `LotId`, `Usage`, and
`IsVerified` - true when the identifier and the slot map are both agreed, which is E87's
precondition for access.

`PortId` and `StorageLocation` are never both set: a carrier is in one place, and the manager
refuses the move that would make it two.

### `LoadPort`

`sealed class`. Same rule. `PortId`, `TransferState`, `AccessMode`, `AssociationState`,
`ReservationState`, `AssociatedCarrierId` (`string?`), `ReservedForCarrierId` (`string?`), and
`IsInService`.

### `CarrierStorageLocation`

`sealed class`. Same rule. A place inside the machine addressed by `LOC` rather than by `PTN`:
`Location` (`uint`), `TransferState` (a `PortTransferState`, because S3F32 carries the same
`PORTSTATUS` item S3F22 does), `CarrierId` (`string?`), `IsInService`, `IsOccupied` and
`ToString()` - `location 101 ReadyToUnload (CAR-001)`.

One state machine and not an eighth: no access mode, no reservation, no association state. The
[README](README.md#storage-locations) says why for each.

## The states

| Enum | Members |
| --- | --- |
| `CarrierIdStatus` | `IdNotRead`, `WaitingForHost`, `IdVerificationOk`, `IdVerificationFailed` |
| `CarrierSlotMapStatus` | `SlotMapNotRead`, `WaitingForHost`, `SlotMapVerificationOk`, `SlotMapVerificationFailed` |
| `CarrierAccessStatus` | `NotAccessed`, `InAccess`, `CarrierComplete`, `CarrierStopped` |
| `PortTransferState` | `OutOfService`, `TransferBlocked`, `ReadyToLoad`, `ReadyToUnload` |
| `PortAccessMode` | `Manual`, `Auto` |
| `PortAssociationState` | `NotAssociated`, `Associated` |
| `PortReservationState` | `NotReserved`, `Reserved` |

## Actions

### `CarrierActions`

`static class` of `const string`. `ProceedWithCarrier`, `CancelCarrier`, `CarrierNotification`,
`CancelCarrierNotification`, `BindCarrier`, `CancelBind`, `CarrierOut`, `CarrierIn`,
`CancelCarrierOut`, `CancelCarrierIn`, `CarrierReCreate`, `ChangeAccess`, `ReserveAtPort`,
`CancelReservationAtPort`, `ChangeServiceStatus`.

Strings because `CARRIERACTION` is an `A` item. A convenience, not a limit: an action not in this
list is answered `InvalidCommand` and can still be sent.

### `CarrierActionRequest`

`sealed class`. `Action` (`required string`), `CarrierId` (`string`), `PortId` (`byte?`),
`Argument` (`string`).

`Argument` **does not travel**: S3F17 carries an action, a carrier and a port and nothing else. It
serves `ChangeAccess` and `ChangeServiceStatus` when the manager is called in process; over a wire
those two are attribute writes on stream 14.

### `CarrierActionResult`

`sealed class`. `Acknowledgement`, `ErrorCode` (`uint`), `ErrorText` (`string`), `IsAccepted`.

### `CarrierAcknowledgement`

`enum : byte` - `CAACK`, all four codes from the catalogue. `Accepted = 0`, `InvalidCommand = 1`,
`CannotPerformNow = 2`, `InvalidData = 3`. A code E5 does not define reads as `CannotPerformNow`,
never as `Accepted`.

### `CarrierRequestAcknowledgement`

`enum : byte` - `ACKC3`. `Accepted = 0`, `Denied = 1`. What S3F26, S3F30 and S3F34 carry; it cannot
say why.

## The two ends

### `CarrierEquipment`

`sealed class`. Answers the five requests out of a `CarrierManager`. Not disposable.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `CarrierEquipment(CarrierManager manager, ILogger<CarrierEquipment>? logger = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S3F17, F19, F21, F23, F25, F27, F29, F31, F33 - all nine of E87's half of stream 3. |
| `LocationOf` | `Func<Carrier, uint> { get; set; }` | What S3F24 reports. Defaults to the storage location a carrier is in, then the port it is at, then zero. A machine whose shelves and ports share numbers replaces it - S3F24 has room for one number and nothing in E87 keeps the two apart. |
| `StatusOf` | `Func<Carrier, string> { get; set; }` | What `CARRIERSTATUS` says in S3F20. Defaults to `AccessStatus`, which is the only one of a carrier's three states a single word can carry - the [README](README.md#s3f20-and-the-one-word-that-names-two-state-machines) gives both grounds. A fab with its own vocabulary replaces it. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | |

A body it cannot read is `InvalidData` on S3F18, `Denied` on S3F26/F30/F34, and a zero-length list
on S3F24 - which carries a carrier and a location and has no code to refuse with.

### `CarrierHost`

`sealed class`. Sends the five; answers nothing.

| Member | Signature | Message |
| --- | --- | --- |
| constructor | `CarrierHost(HsmsConnection connection, ILogger<CarrierHost>? logger = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | **Empty.** E87 gives a host no primary to answer. |
| `PerformAsync` | `ValueTask<CarrierActionResult> PerformAsync(CarrierActionRequest, CancellationToken = default)` | S3F17 |
| `ProceedWithCarrierAsync` | `ValueTask<CarrierActionResult> ProceedWithCarrierAsync(string carrierId, CancellationToken = default)` | S3F17 |
| `CancelCarrierAsync` | `ValueTask<CarrierActionResult> CancelCarrierAsync(string carrierId, CancellationToken = default)` | S3F17 |
| `NotifyCarrierAsync` | `ValueTask<CarrierActionResult> NotifyCarrierAsync(string carrierId, CancellationToken = default)` | S3F17 |
| `BindCarrierAsync` | `ValueTask<CarrierActionResult> BindCarrierAsync(string carrierId, byte portId, CancellationToken = default)` | S3F17 |
| `ReserveAtPortAsync` | `ValueTask<CarrierActionResult> ReserveAtPortAsync(string carrierId, byte portId, CancellationToken = default)` | S3F17; says why when refused |
| `ReadCarrierStatusAsync` | `ValueTask<CarrierStatus?> ReadCarrierStatusAsync(string carrierId, CancellationToken = default)` | S3F19; `null` means no such carrier |
| `ReadPortStatusAsync` | `ValueTask<CarrierPortStatus?> ReadPortStatusAsync(byte portId, CancellationToken = default)` | S3F21; `null` means no such port |
| `FindCarrierAsync` | `ValueTask<uint?> FindCarrierAsync(string carrierId, CancellationToken = default)` | S3F23; `null` means no such carrier |
| `ReadCarrierIdAsync` | `ValueTask<string?> ReadCarrierIdAsync(byte portId, CancellationToken = default)` | S3F25; `null` means nothing there, or the label is not read |
| `ReadSlotMapAsync` | `ValueTask<ReadOnlyMemory<byte>?> ReadSlotMapAsync(string carrierId, CancellationToken = default)` | S3F27; `null` means no such carrier, or the map is not agreed. Empty means agreed and empty. |
| `ReadStorageStatusAsync` | `ValueTask<IReadOnlyList<CarrierStorageStatus>> ReadStorageStatusAsync(CancellationToken = default)` | S3F31; empty means **no storage**, not "will not say" - the request names nothing, so there is nothing to be missing. |
| `ReserveAsync` | `ValueTask<CarrierRequestAcknowledgement> ReserveAsync(string carrierId, byte portId, CancellationToken = default)` | S3F29; cannot say why |
| `CarrierOutAsync` | `ValueTask<CarrierRequestAcknowledgement> CarrierOutAsync(string carrierId, CancellationToken = default)` | S3F33 |

A reply with no acknowledgement code throws `SecsException`, never `Accepted`.

## `CarrierStatus`

`sealed class`. What a carrier is doing and where - S3F20. The undefined item with the least to go
on, which is why what fills it is `CarrierEquipment.StatusOf` rather than a decision this type
makes.

| Member | Signature | Notes |
| --- | --- | --- |
| `CarrierId` | `required string { get; init; }` | `CARRIERID`. |
| `Status` | `required string { get; init; }` | What `CARRIERSTATUS` carried, as it arrived. |
| `PortId` | `byte? { get; init; }` | `PTN`, and `null` for a carrier on no port - a real answer, not a missing one. Port 0 is a port, so an absent one travels as a zero-length item. |
| `AccessStatus` | `CarrierAccessStatus? { get; init; }` | The reading, where one is possible. Only this enum: `WaitingForHost` belongs to both of the other two state machines, so a word from either comes back unread. |
| `ToString()` | `override string` | `CAR-001 InAccess (port 1)`, or `CAR-001 NotAccessed (no port)`. |

## `CarrierPortStatus`

`public sealed class CarrierPortStatus`. What S3F22 carries, with the undefined item kept twice.

| Member | Signature | Notes |
| --- | --- | --- |
| `PortId` | `required byte { get; init; }` | `PTN`. |
| `Status` | `required string { get; init; }` | What `PORTSTATUS` carried, as it arrived. |
| `TransferState` | `PortTransferState? { get; init; }` | This library's reading of it, `null` when the word is not one of E87's four - never silently `OutOfService`, which is the zero of the enum. |
| `CarrierId` | `string { get; init; }` | The carrier on the port, or empty. |
| `ToString()` | `override string` | `port 1 ReadyToUnload (CAR-001)`. |

## `CarrierStorageStatus`

`sealed class`. `CarrierPortStatus`'s twin, for a place addressed by `LOC`. Two types rather than
one with a nullable address, because the two are not interchangeable - a host that confused a shelf
for a load port would route material into the machine instead of out of it.

| Member | Signature | Notes |
| --- | --- | --- |
| `Location` | `required uint { get; init; }` | `LOC`. |
| `Status` | `required string { get; init; }` | What `PORTSTATUS` carried, as it arrived. |
| `TransferState` | `PortTransferState? { get; init; }` | This library's reading of it, `null` for a word not among E87's four. Never silently `OutOfService`. |
| `CarrierId` | `string { get; init; }` | The carrier in it, or empty. |
| `ToString()` | `override string` | `location 101 ReadyToUnload (CAR-001)`. |

`PORTSTATUS` has no dictionary entry, so the text is kept and the reading is offered rather than
forced. The [README](README.md#the-transactions-whose-item-nobody-defined) explains why this
library reads the transfer state out of a port's four state machines, and on what grounds it reads
anything at all.

## `CarrierObjectProvider`

`sealed class : IObjectProvider` (from `Barbatos.SecsGem.Objects`). E87's attributes over
stream 14.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `CarrierObjectProvider(CarrierManager manager)` | |
| `CarrierType` | `const string` = `"Carrier"` | |
| `LoadPortType` | `const string` = `"LoadPort"` | |
| `StorageLocationType` | `const string` = `"StorageLocation"` | Advertised only by a machine that has at least one, unlike the other two - the same asymmetry S3F31 keeps. Its attributes are S3F32's three items, and **nothing on it is writable**. |
| `PortSpecification` | `static string PortSpecification(byte portId)` | How a port is named as an object; `LoadPort1`. |
| `TryReadPortSpecification` | `static byte? TryReadPortSpecification(string)` | The only reader of that convention. |
| `StorageSpecification` | `static string StorageSpecification(uint location)` | The same convention; `StorageLocation101`. It also keeps a shelf numbered 1 apart from load port 1, which `LOC` has no room to do. |
| `TryReadStorageSpecification` | `static uint? TryReadStorageSpecification(string)` | The only reader of that one. |

Readable attributes are listed in the README. Writable: `LotID` and `Usage` straight through,
`AccessMode` through the `ChangeAccess` action. Everything else is `InvalidAttribute`, checked
before anything is written, so a set naming one read-only attribute changes nothing.

## Events

`CarrierStateChangedEventArgs`: `Carrier`, `StateMachine` (`CarrierStateMachine`), `Previous`,
`Current`. `PortStateChangedEventArgs`: `Port`, `StateMachine` (`PortStateMachine`), `Previous`,
`Current`.

The states are `int` because three enums cannot share one property; cast to the enum the
`StateMachine` member names. Raised after the move and inside the manager's lock.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddCarrierEquipment(Action<CarrierManager>? configure = null)` | `CarrierManager` (ports and storage locations declared in `configure`), `CarrierEquipment`, `CarrierObjectProvider` as an `IObjectProvider`, and handlers for the nine carrier primaries **and** the eleven of object services. |
| `AddCarrierHost()` | `CarrierHost`, plus `AddObjectServices` so a host can read carrier attributes. Declares nothing. |

The provider **composes**: an application with objects beyond carriers and ports registers its own
as well, and both answer through `CompositeObjectProvider`. The same goes for `AddSubstrateTracking`
and `AddControlJobs` on the same machine - three packages, one stream, and the primaries declared
once.

Calling `AddCarrierEquipment` twice is harmless too: both sets go in through the builder's
`DeclareOnce`. The manager is a `TryAdd` singleton, so the first `configure` is the one that runs.

## Outliving the process

| Member | Signature | Notes |
| --- | --- | --- |
| `CarrierManager.Capture` | `CarrierManagerSnapshot Capture()` | A faithful record, taken under the lock, including the states a restart will not claim again. |
| `CarrierManager.Restore` | `CarrierRestoreReport Restore(CarrierManagerSnapshot snapshot)` | Into a manager with no carriers. Raises nothing. |

`CarrierManagerSnapshot` holds `Ports` (`IReadOnlyList<LoadPortSnapshot>`), `StorageLocations`
(`IReadOnlyList<CarrierStorageSnapshot>`) and `Carriers` (`IReadOnlyList<CarrierSnapshot>`).
`CarrierSnapshot` carries every `Carrier` property plus the `SlotMap` as an `IReadOnlyList<byte>`;
`LoadPortSnapshot` carries every `LoadPort` property; `CarrierStorageSnapshot` carries `Location`,
`TransferState` and `CarrierId`. All `init`-only with no constructor, so `System.Text.Json`
round-trips them and this package depends on no serializer.

`CarrierRestoreReport`: `IsAccepted` (`required bool` - false only when nothing was attempted),
`Reason`, `Restored` (`IReadOnlyList<string>` - carrier identifiers, `port 1` and `location 101`),
`Adjusted` (`IReadOnlyDictionary<string, string>` - what was not restored as recorded, and why) and
`Count`.

What comes back and what does not is in the [README](README.md#outliving-the-process); the short
version is that a host's instructions survive and the machine's observations do not, and a carrier
that was `InAccess` comes back `CarrierStopped`. A storage location is a port's twin here: in
service comes back, and what was in it does not.
