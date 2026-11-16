# Barbatos.SecsGem.MaterialFlow - API reference

Namespace `Barbatos.SecsGem.MaterialFlow`, except where noted. Why this is a package of its own,
and what each default means, is in [`README.md`](README.md); this page is member-by-member.

- [`CarrierSubstrateBridge`](#carriersubstratebridge)
- [`CarrierSubstrateOptions`](#carriersubstrateoptions)
- [`CarrierSubstrateResult`](#carriersubstrateresult)
- [`CarrierSubstrateEntry`](#carriersubstrateentry)
- [`ProcessJobSubstrateBridge`](#processjobsubstratebridge)
- [`ProcessJobSubstrateOptions`](#processjobsubstrateoptions)
- [`ProcessJobSubstrateResult`](#processjobsubstrateresult)
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

## `ProcessJobSubstrateBridge`

`sealed class`. Carries a process job's ending through to the substrates it was running. No
connection and no message, as with the other one.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ProcessJobSubstrateBridge(ProcessJobManager jobs, SubstrateTracker substrates, ProcessJobSubstrateOptions? options = null)` | |
| `Options` | `ProcessJobSubstrateOptions { get; }` | |
| `IsAttached` | `bool { get; }` | |
| `Attach` | `void Attach()` | Listens for jobs that end, and for jobs being set up. Attaching twice listens once. |
| `Detach` | `void Detach()` | What it already settled stays settled: a wafer whose work ended does not un-end. |
| `Check` | `ProcessJobSubstrateResult Check(string processJobId)` | Every piece of the job's material the tracker does not have, in `Unexpected`. Changes nothing. |
| `Settle` | `ProcessJobSubstrateResult Settle(string processJobId)` | Carries the job's ending to its material. Only a job that has one. |
| `Settled` | `event EventHandler<ProcessJobSubstrateEventArgs>?` | After a job's substrates are settled. |
| `MaterialMissing` | `event EventHandler<ProcessJobSubstrateEventArgs>?` | When a job being set up names material the tracker lacks - only when there is something to say. |

**What `Settle` does**, per piece of material, and nothing else:

| The wafer is | The job ended | What happens |
| --- | --- | --- |
| `InProcess` | `Stopped` or `Aborted` | work ends `Stopped` / `Aborted` - the same word both standards use |
| `NeedsProcessing` | `Stopped`, `Aborted` or `Cancelled` | skipped, which is E90's word for a wafer nobody got to |
| `InProcess` | `Completed` | reported: there is no honest state to end it in |
| `NeedsProcessing` | `Completed` | reported: a completed job says every piece went through |
| `Processed` | `Cancelled` | reported: the job never ran |
| anything else | anything | left alone - already accounted for |

`Settle` is refused outright for a job the manager does not have and for one with no ending yet -
having a completion and being `ProcessJobState.ProcessComplete` are the same fact in E40's manager,
so that is one check rather than two.

**Locks**: attached, it runs inside the process job manager's lock and calls the substrate tracker
from there. Jobs first, then substrates, as the carrier bridge goes carriers first - substrates are
always last.

## `ProcessJobSubstrateOptions`

`sealed class`.

| Member | Type | Default | What it decides |
| --- | --- | --- | --- |
| `EndsWorkInProgress` | `bool` | `true` | Whether a job's ending ends work on the wafers being processed |
| `SkipsUntouched` | `bool` | `true` | Whether wafers nobody got to are skipped when a job ends early |
| `SettlesWhenTheJobEnds` | `bool` | `true` | Whether an attached bridge settles by itself |
| `ChecksMaterialWhenSettingUp` | `bool` | `true` | Whether a job being set up has its material checked |

With either of the first two off, the wafers it would have moved are reported in `Unexpected`
instead: the option changes who accounts for them, not whether anybody has to.

## `ProcessJobSubstrateResult`

`sealed class`. `IsAccepted` (`required bool`), `Reason` (`string`), `ProcessJobId`
(`required string`), `Ended` and `Skipped` (`IReadOnlyList<string>`), `Unexpected`
(`IReadOnlyDictionary<string, string>` - substrate to what does not fit) and `Count`
(`Ended` plus `Skipped`).

`Check` fills only `Unexpected`.

## Events

`CarrierSubstrateEventArgs` and `ProcessJobSubstrateEventArgs` each carry the `Result`. Nothing is
raised for an outright refusal: an event that announced work nobody did would be worse than
silence.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddCarrierSubstrateBridge(Action<CarrierSubstrateOptions>? configure = null)` | `CarrierSubstrateOptions`, `CarrierSubstrateBridge`, and the bridge is attached as soon as it is resolved. |
| `AddProcessJobSubstrateBridge(Action<ProcessJobSubstrateOptions>? configure = null)` | `ProcessJobSubstrateOptions`, `ProcessJobSubstrateBridge`, attached the same way. |

The first needs `CarrierManager` and `SubstrateTracker` in the container, the second
`ProcessJobManager` and `SubstrateTracker` - so call each after the registrations that put those
there. A graph missing one fails to resolve that bridge, which is the right moment to find out: the
alternative is a machine that reads slot maps and silently tracks nothing, or one whose stopped
jobs quietly leave a lot half tracked.

Either called twice adds one bridge. Neither declares a message of its own.
