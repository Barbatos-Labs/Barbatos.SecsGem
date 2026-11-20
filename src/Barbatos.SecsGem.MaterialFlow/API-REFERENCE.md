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
- [`ControlJobCarrierBridge`](#controljobcarrierbridge)
- [`ControlJobCarrierOptions`](#controljobcarrieroptions)
- [`ControlJobCarrierResult`](#controljobcarrierresult)
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

## `ControlJobCarrierBridge`

`sealed class`. Tells a control job when the carriers it named have arrived and when they are
finished with, and tells those carriers when the job has taken them and let them go.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ControlJobCarrierBridge(CarrierManager carriers, ControlJobManager jobs, ControlJobCarrierOptions? options = null)` | A `null` options takes every default. |
| `Options` | `ControlJobCarrierOptions { get; }` | The conventions this bridge follows. |
| `IsAttached` | `bool { get; }` | Whether it is listening to the **carrier** manager. There is nothing to ask about the job manager. |
| `Attach` | `void Attach()` | Listens to the carrier manager, so a job's material announces itself. Attaching twice listens once. |
| `Detach` | `void Detach()` | Stops listening. Whatever it has already told either side stays told. |
| `Check` | `ControlJobCarrierResult Check(string controlJobId)` | Whether the material is ready, and which carrier is holding it up. Changes nothing. |
| `Ready` | `ControlJobCarrierResult Ready(string controlJobId)` | Tells E94 the material is ready, if it is. Quiet when it is not. |
| `Finish` | `ControlJobCarrierResult Finish(string controlJobId)` | Tells E94 the material is all dealt with, if it is. Raises `Stalled` for the one case that never will be. |
| `ClaimCarriers` | `ControlJobCarrierResult ClaimCarriers(string controlJobId)` | `BeginAccess` for each carrier of a running job. Only `IsActive`. |
| `ReleaseCarriers` | `ControlJobCarrierResult ReleaseCarriers(string controlJobId)` | `EndAccess` for each carrier of a job that has ended, the way it ended. |
| `Readied` | `event EventHandler<ControlJobCarrierEventArgs>?` | When the quorum was met and E94 was told - whatever E94 then said. |
| `Finished` | `event EventHandler<ControlJobCarrierEventArgs>?` | The same at the other end. |
| `Stalled` | `event EventHandler<ControlJobCarrierEventArgs>?` | Every carrier accounted for and one of them stopped, so this bridge will not call it a completed lot. |

**It attaches to one manager and that is the design**, not an omission. Both managers raise inside
their own lock, so a bridge that listened to both would take their two locks in two orders - the
one deadlock a stack like this can build for itself. `ClaimCarriers` and `ReleaseCarriers` hold no
lock when they begin: they read the job, E94's lock is released, and only then do they call E87.
See the README's lock section.

| What happened | What the bridge does |
| --- | --- |
| the last carrier of a `Selected` job is agreed | `MaterialReady`, so an automatic job runs and a host-initiated one waits |
| the last carrier of an `Executing` job is `CarrierComplete` | `MaterialFinished`, completion `Completed` |
| every carrier accounted for and one `CarrierStopped` | nothing, and `Stalled` says why - the host's `STOP` or `ABORT` is the honest ending |
| a carrier arrives that the current job never named | nothing at all |
| any of this while no job is selected or running | nothing at all |

## `ControlJobCarrierOptions`

`sealed class`.

| Member | Type | Default | What it decides |
| --- | --- | --- | --- |
| `IsCarrier` | `Func<string, bool>` | `_ => true` | Which of a job's identifiers name E87 carriers at all |
| `RequiresCarrierAtPort` | `bool` | `true` | Whether a verified carrier must be docked to count as arrived |
| `ReadiesWhenCarriersVerified` | `bool` | `true` | Whether an attached bridge starts the lot itself |
| `FinishesWhenCarriersComplete` | `bool` | `true` | Whether an attached bridge ends it itself |
| `CountsStoppedCarriers` | `bool` | `false` | Whether a `CarrierStopped` carrier counts as dealt with |
| `EndsCarriersEarly` | `Func<ControlJobCompletion, bool>` | `c => c is not Completed` | Which endings leave a carrier `CarrierStopped` |

`IsCarrier` is a predicate rather than a flag because E87 cannot tell an identifier it is not
responsible for from a carrier that has not arrived yet. Anything the predicate calls a carrier and
the manager does not have is treated as **on its way**, and the lot waits.

## `ControlJobCarrierResult`

`sealed class`. `IsAccepted` (`required bool`), `Reason` (`string`), `ControlJobId`
(`required string`), `Carriers` (`IReadOnlyList<string>` - the ones that satisfied what was asked),
`Refused` (`IReadOnlyDictionary<string, string>` - carrier to why not, the tolerated foreign ones
included) and `Count` (`Carriers.Count`).

For `Ready` and `Finish`, `IsAccepted` and `Reason` are **E94's own answer** once the carriers have
agreed - so a job that refuses the transition on its own grounds comes back here saying so, with
the carriers it found still listed. For `ClaimCarriers` and `ReleaseCarriers`, `IsAccepted` is
false only when nothing was attempted; the per-carrier outcomes are the two lists.

## Events

`CarrierSubstrateEventArgs`, `ProcessJobSubstrateEventArgs` and `ControlJobCarrierEventArgs` each
carry the `Result`. Nothing is raised for an outright refusal: an event that announced work nobody
did would be worse than silence.

`ControlJobCarrierBridge.Readied` and `Finished` are the exception worth naming - they are raised
when the quorum was met even if E94 then refused, because "the material arrived and the job would
not take it" is the one refusal somebody has to act on.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddCarrierSubstrateBridge(Action<CarrierSubstrateOptions>? configure = null)` | `CarrierSubstrateOptions`, `CarrierSubstrateBridge`, and the bridge is attached as soon as it is resolved. |
| `AddProcessJobSubstrateBridge(Action<ProcessJobSubstrateOptions>? configure = null)` | `ProcessJobSubstrateOptions`, `ProcessJobSubstrateBridge`, attached the same way. |
| `AddControlJobCarrierBridge(Action<ControlJobCarrierOptions>? configure = null)` | `ControlJobCarrierOptions`, `ControlJobCarrierBridge`, attached the same way - to the carrier manager only. |

The first needs `CarrierManager` and `SubstrateTracker` in the container, the second
`ProcessJobManager` and `SubstrateTracker`, the third `CarrierManager` and `ControlJobManager` - so
call each after the registrations that put those there. A graph missing one fails to resolve that
bridge, which is the right moment to find out: the alternative is a machine that reads slot maps
and silently tracks nothing, one whose stopped jobs quietly leave a lot half tracked, or one whose
carriers all arrive and whose lot never starts.

Any of them called twice adds one bridge. None declares a message of its own.
