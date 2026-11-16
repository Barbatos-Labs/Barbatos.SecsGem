# Barbatos.SecsGem.MaterialFlow - API reference

Namespace `Barbatos.SecsGem.MaterialFlow`, except where noted. Why this is a package of its own,
and what each default means, is in [`README.md`](README.md); this page is member-by-member.

- [`CarrierSubstrateBridge`](#carriersubstratebridge)
- [`CarrierSubstrateOptions`](#carriersubstrateoptions)
- [`CarrierSubstrateResult`](#carriersubstrateresult)
- [`CarrierSubstrateEntry`](#carriersubstrateentry)
- [Events](#events)
- [Registration](#registration)

## `CarrierSubstrateBridge`

`sealed class`. Turns a carrier's verified slot map into tracked substrates, and forgets them
again. No connection and no message: the join is behaviour between two state machines.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `CarrierSubstrateBridge(CarrierManager carriers, SubstrateTracker substrates, CarrierSubstrateOptions? options = null)` | A `null` options takes every default. |
| `Options` | `CarrierSubstrateOptions { get; }` | The convention this bridge follows. Read it back rather than keeping a copy. |
| `OpenCarriers` | `IReadOnlyList<string> { get; }` | The carriers whose substrates it created and has not forgotten. A snapshot. |
| `IsAttached` | `bool { get; }` | Whether it is listening to the carrier manager. |
| `Attach` | `void Attach()` | Listens, so a verified slot map creates substrates by itself. Attaching twice listens once. |
| `Detach` | `void Detach()` | Stops listening. What it already created stays created. |
| `SubstratesOf` | `IReadOnlyList<CarrierSubstrateEntry> SubstratesOf(string carrierId)` | What it created for a carrier, as a snapshot. Empty for one it has not opened. |
| `Open` | `CarrierSubstrateResult Open(string carrierId)` | One substrate per occupied slot. Declares the slot locations as it goes. |
| `Close` | `CarrierSubstrateResult Close(string carrierId)` | Forgets the substrates it created for that carrier, as far as E90 allows. |
| `Opened` | `event EventHandler<CarrierSubstrateEventArgs>?` | After substrates are created. |
| `Closed` | `event EventHandler<CarrierSubstrateEventArgs>?` | After they are forgotten. |

**What `Open` refuses outright** - `IsAccepted` false, nothing attempted: a carrier the manager does
not have, a slot map that is not `CarrierSlotMapStatus.SlotMapVerificationOk`, and a carrier this
bridge has already opened. A slot the *tracker* refuses is not an outright refusal: it goes in
`Refused` and the other slots are still created.

**What `Close` refuses outright**: a carrier this bridge never opened. A substrate E90 will not let
go of - one that is not at its destination or lost - goes in `Refused` and stays this bridge's, so
the call can be made again when the lot gets home. That is why a close that forgets nothing is
still accepted.

**Locks.** The bridge's own lock covers its bookkeeping only. Attached, its handler runs inside the
carrier manager's lock and calls into the substrate tracker from there, so the order is always
carriers, then substrates - never the other way. Its own events are raised on whichever thread did
the work, which attached means inside that lock: queue the work.

## `CarrierSubstrateOptions`

`sealed class`. Every decision the join makes, each one a convention rather than a rule.

| Member | Type | Default |
| --- | --- | --- |
| `SlotLocationName` | `Func<string, byte, string>` | `(carrier, slot) => $"{carrier}-SLOT-{slot:00}"` |
| `SubstrateName` | `Func<string, byte, string>` | `(carrier, slot) => $"{carrier}-{slot:00}"` |
| `IsOccupied` | `Func<byte, bool>` | `code => code != 0` |
| `ReturnsToItsOwnSlot` | `bool` | `true` |
| `CopiesLotId` | `bool` | `true` |
| `CopiesUsage` | `bool` | `true` |
| `SubstrateType` | `string` | empty - not `"Wafer"` |
| `OpensWhenSlotMapVerified` | `bool` | `true` |
| `ClosesWhenCarrierFinished` | `bool` | `false` |

A naming delegate that returns an empty string refuses that slot rather than inventing a name: a
wafer the machine cannot name is a wafer nobody can ask about later.

`ClosesWhenCarrierFinished` acts on both of E87's endings, `CarrierComplete` and `CarrierStopped` -
both mean the machine has let go, and whether the wafers can be forgotten is E90's answer rather
than this bridge's.

## `CarrierSubstrateResult`

`sealed class`. What happened to a carrier's worth of substrates.

| Member | Type | Notes |
| --- | --- | --- |
| `IsAccepted` | `required bool` | False only when nothing was attempted. |
| `Reason` | `string` | Why nothing was attempted. Empty when something was. |
| `CarrierId` | `required string` | |
| `Substrates` | `IReadOnlyList<CarrierSubstrateEntry>` | The ones created, or forgotten. |
| `Refused` | `IReadOnlyDictionary<byte, string>` | Slot to the tracker's own reason. |
| `Count` | `int` | How many are in `Substrates`. |

A list of what was done and a list of what was not, rather than one boolean: a carrier where
twenty-four slots worked and one did not is the case this type exists for.

## `CarrierSubstrateEntry`

`sealed class`. `Slot` (`required byte`, counting from one), `SubstrateId` (`required string`) and
`LocationId` (`required string`) - the slot location the wafer came out of, which is also where it
goes back to when `ReturnsToItsOwnSlot` is on.

## Events

`CarrierSubstrateEventArgs` carries the `Result`. Nothing is raised for an outright refusal: an
event that announced work nobody did would be worse than silence.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddCarrierSubstrateBridge(Action<CarrierSubstrateOptions>? configure = null)` | `CarrierSubstrateOptions`, `CarrierSubstrateBridge`, and the bridge is attached as soon as it is resolved. |

It needs `CarrierManager` and `SubstrateTracker` in the container, so call it after
`AddCarrierEquipment` and `AddSubstrateTracking`. A graph missing either fails to resolve the
bridge, which is the right moment to find out: the alternative is a machine that reads slot maps
and silently tracks nothing.

Calling it twice adds one bridge. It declares no message of its own.
