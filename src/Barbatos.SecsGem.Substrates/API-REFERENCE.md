# Barbatos.SecsGem.Substrates - API reference

Namespace `Barbatos.SecsGem.Substrates`. Concepts, the state diagrams and the provenance warning
live in [`README.md`](README.md); this page is member-by-member.

- [`SubstrateTracker`](#substratetracker)
- [The objects](#the-objects)
  - [`Substrate`](#substrate)
  - [`SubstrateLocation`](#substratelocation)
- [The states](#the-states)
- [`SubstrateResult`](#substrateresult)
- [`SubstrateObjectProvider`](#substrateobjectprovider)
- [Events](#events)
- [Registration](#registration)

## `SubstrateTracker`

`sealed class`. The substrates, the locations, and E90's rules. No connection and no messages,
because E90 has none: this is the package, and nearly all of its tests never open a link.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `SubstrateTracker()` | |
| `Substrates` | `IReadOnlyCollection<Substrate> { get; }` | A snapshot, taken under the lock. |
| `Locations` | `IReadOnlyCollection<SubstrateLocation> { get; }` | |
| `AddLocation` | `SubstrateLocation AddLocation(string locationId)` | Declares geography. Declaring twice returns the same location and resets nothing. |
| `FindSubstrate` | `Substrate? FindSubstrate(string substrateId)` | |
| `FindLocation` | `SubstrateLocation? FindLocation(string locationId)` | |
| `Add` | `SubstrateResult Add(string substrateId, string sourceLocationId, byte slotId = 0)` | Starts a substrate `AtSource` and `NeedsProcessing`. Refused for an unknown or occupied location, or a substrate already tracked. |
| `Describe` | `SubstrateResult Describe(string substrateId, string? lotId = null, string? usage = null, string? substrateType = null, string? destinationLocationId = null, string? acquiredId = null)` | The labels a host owns, settable at any time. A destination naming no declared location is refused. |
| `MoveTo` | `SubstrateResult MoveTo(string substrateId, string locationId)` | Derives the transport state from where it went. Refused while `InProcess`, once `AtDestination`, once `Lost`, or into an occupied location. |
| `BeginProcessing` | `SubstrateResult BeginProcessing(string substrateId)` | Only from `NeedsProcessing` and only while `AtWork`. |
| `EndProcessing` | `SubstrateResult EndProcessing(string substrateId, SubstrateProcessingState state)` | Only from `InProcess`. `state` must be `Processed`, `Aborted`, `Stopped` or `Rejected`; anything else throws `ArgumentOutOfRangeException`. |
| `Skip` | `SubstrateResult Skip(string substrateId)` | Only from `NeedsProcessing`. |
| `Lose` | `SubstrateResult Lose(string substrateId)` | From anywhere, including the endings. Empties the location it was at. |
| `Remove` | `SubstrateResult Remove(string substrateId)` | Only once `AtDestination` or `Lost`. |
| `SubstrateStateChanged` | `event EventHandler<SubstrateStateChangedEventArgs>?` | After the move, inside the lock. |
| `LocationChanged` | `event EventHandler<SubstrateLocationChangedEventArgs>?` | |

Every refusal carries a `Reason` naming the state that refused it. Handlers must not call back into
the tracker - the events are raised under its lock.

## The objects

### `Substrate`

`sealed class`. Public getters, **internal setters**: the only thing that can move a substrate is
the tracker.

`SubstrateId`, `TransportState`, `ProcessingState`, `LocationId`, `SourceLocationId`,
`DestinationLocationId`, `SlotId` (`byte`, `SLOTID` - the one field in this package the catalogue
defines), `LotId`, `Usage`, `SubstrateType`, `AcquiredId`, and `IsFinished` - true for every ending
E90 names, `Skipped` and `Lost` included.

### `SubstrateLocation`

`sealed class`. `LocationId`, `State`, `SubstrateId` (`string?`). One substrate at a time; the
state follows the substrates and cannot be assigned.

## The states

| Enum | Members |
| --- | --- |
| `SubstrateTransportState` | `AtSource`, `AtWork`, `AtDestination` |
| `SubstrateProcessingState` | `NeedsProcessing`, `InProcess`, `Processed`, `Aborted`, `Stopped`, `Rejected`, `Lost`, `Skipped` |
| `SubstrateLocationState` | `Unoccupied`, `Occupied` |
| `SubstrateStateMachine` | `TransportState`, `ProcessingState` - which enum an event's states belong to |

## `SubstrateResult`

`sealed class`. `IsAccepted` (`required bool`) and `Reason` (`string`).

No acknowledgement code, because E90 has no messages to carry one. An application that must answer
a host maps the reason to whatever code that message has.

## `SubstrateObjectProvider`

`sealed class : IObjectProvider` (from `Barbatos.SecsGem.Objects`). E90's entire wire presence.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `SubstrateObjectProvider(SubstrateTracker tracker)` | |
| `SubstrateType` | `const string` = `"Substrate"` | |
| `SubstrateLocationType` | `const string` = `"SubstrateLocation"` | |

Readable attributes are listed in the README. Writable: `LotID`, `SubstrateUsage`,
`SubstrateType`, `SubstrateDestinationLocID` and `AcquiredID`, all through the tracker. Everything
else is `InvalidAttribute`, checked before anything is written, so a set naming one read-only
attribute changes nothing. A location has nothing a host may set.

## Events

`SubstrateStateChangedEventArgs`: `Substrate`, `StateMachine`, `Previous`, `Current` - the states
as `int`, because two enums cannot share one property. `SubstrateLocationChangedEventArgs`:
`Location`, `Previous`, `Current`, typed, because a location has one state machine.

Raised after the move and inside the tracker's lock.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddSubstrateTracking(Action<SubstrateTracker>? configure = null)` | `SubstrateTracker` (locations declared in `configure`), `SubstrateObjectProvider` as an `IObjectProvider`, and the seven primaries of object services. |

One method, because E90 has one side. It composes with `AddCarrierEquipment`: both providers
answer, through `CompositeObjectProvider`.
