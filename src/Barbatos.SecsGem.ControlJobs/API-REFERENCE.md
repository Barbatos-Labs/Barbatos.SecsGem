# Barbatos.SecsGem.ControlJobs - API reference

Namespace `Barbatos.SecsGem.ControlJobs`. Concepts, the state diagram and the provenance warning
live in [`README.md`](README.md); this page is member-by-member.

- [`ControlJobManager`](#controljobmanager)
- [`ControlJob`](#controljob)
- [The states](#the-states)
- [`ControlJobResult`](#controljobresult)
- [`ControlJobCommands`](#controljobcommands)
- [`ControlJobObjectProvider`](#controljobobjectprovider)
- [Events](#events)
- [Registration](#registration)

## `ControlJobManager`

`sealed class`. The control jobs a machine has, and E94's rules about what may happen to them. No
connection and no messages, because E94 has none: this is the package, and nearly all of its tests
never open a link.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ControlJobManager()` | |
| `Jobs` | `IReadOnlyCollection<ControlJob> { get; }` | A snapshot, taken under the lock. |
| `Queue` | `IReadOnlyList<string> { get; }` | The jobs waiting their turn, in the order they will be offered. A snapshot. |
| `Current` | `ControlJob? { get; }` | The one that is selected, waiting or running. At most one: a machine runs one control job at a time. |
| `Create` | `ControlJobResult Create(string controlJobId, IReadOnlyList<string>? carriers = null, ControlJobStartMethod startMethod = Automatic, string processOrder = "", string processingSpecification = "")` | Starts a job `Queued`, at the back of the queue. Refused for a name already taken or an empty one. The carrier list is copied. |
| `Delete` | `ControlJobResult Delete(string controlJobId)` | Only a finished job. |
| `Find` | `ControlJob? Find(string controlJobId)` | |
| `Reorder` | `ControlJobResult Reorder(IReadOnlyList<string> controlJobIds)` | Every queued job exactly once; naming one twice, leaving one out, or naming one that is not in the queue is refused. |
| `Describe` | `ControlJobResult Describe(string controlJobId, IReadOnlyList<string>? carriers = null, ControlJobStartMethod? startMethod = null, string? processOrder = null, string? processingSpecification = null)` | Only while `Queued`. A `null` argument means "leave it", not "clear it". |
| `Command` | `ControlJobResult Command(string controlJobId, string command)` | Dispatches one of [`ControlJobCommands`](#controljobcommands). Any casing of a defined spelling; space around it is not trimmed. A command this library does not know is refused, not thrown. |
| `Select` | `ControlJobResult Select(string controlJobId)` | Only from `Queued`, and only when nothing else is selected, waiting or running. |
| `Deselect` | `ControlJobResult Deselect(string controlJobId)` | Only from `Selected` or `WaitingForStart`. Goes back to the **front** of the queue. |
| `MaterialReady` | `ControlJobResult MaterialReady(string controlJobId)` | The machine's own transition, only from `Selected`. Goes to `Executing` or `WaitingForStart` by `StartMethod`. |
| `Start` | `ControlJobResult Start(string controlJobId)` | Only from `WaitingForStart`. |
| `Pause` | `ControlJobResult Pause(string controlJobId)` | Only from `Executing`. |
| `Resume` | `ControlJobResult Resume(string controlJobId)` | Only from `Paused`. |
| `MaterialFinished` | `ControlJobResult MaterialFinished(string controlJobId)` | The machine's own ending, only from `Executing`. Completion `Completed`. |
| `JobStateChanged` | `event EventHandler<ControlJobStateChangedEventArgs>?` | After the move, inside the lock. |

`Abort`, `Stop` and `Cancel` have no method of their own: they arrive through `Command`, because
all three are one transition with three reasons. Abort and stop need a job that has run; cancel
needs one that has not.

Every refusal carries a `Reason` naming the state that refused it. Handlers must not call back into
the manager - the events are raised under its lock.

## `ControlJob`

`sealed class`. Public getters, **internal setters**: the only thing that can move a control job is
the manager.

| Member | Type | Notes |
| --- | --- | --- |
| `ControlJobId` | `string` | Its `OBJID`. |
| `State` | `ControlJobState` | |
| `Completion` | `ControlJobCompletion?` | `null` while it has not ended. |
| `Carriers` | `IReadOnlyList<string>` | E94's carrier input specification. Identifiers, deliberately not E87 objects. |
| `StartMethod` | `ControlJobStartMethod` | |
| `ProcessOrder` | `string` | E94's process order management, in the fab's own words. |
| `ProcessingSpecification` | `string` | The recipe or process job specification. |
| `IsFinished` | `bool` | `State is Completed`. |
| `IsActive` | `bool` | `Executing` or `Paused` - a paused job still owns its material. |

## The states

| Enum | Members |
| --- | --- |
| `ControlJobState` | `Queued`, `Selected`, `WaitingForStart`, `Executing`, `Paused`, `Completed` |
| `ControlJobCompletion` | `Completed`, `Aborted`, `Stopped`, `Cancelled` |
| `ControlJobStartMethod` | `Automatic`, `HostInitiated` |

One terminal state, with the reason beside it in `ControlJob.Completion`.

## `ControlJobResult`

`sealed class`. `IsAccepted` (`required bool`) and `Reason` (`string`).

No acknowledgement code, because E94 has no messages to carry one. The provider maps a refusal to
`OBJACK = Error`; an application answering some other message maps it to whatever code that message
has.

## `ControlJobCommands`

`static class`, eight `const string`. The values are `SELECT`, `DESELECT`, `START`, `PAUSE`,
`RESUME`, `ABORT`, `STOP`, `CANCEL` - six of them the code values the catalogue defines for
`CTLJOBCMD`, and `SELECT` and `DESELECT` the two it does not name. Strings because `RCMD` is an
`A` item: a convenience and not a limit, and `ControlJobManager.Command` accepts any casing of a
defined spelling while refusing one with space around it.

## `ControlJobObjectProvider`

`sealed class : IObjectProvider` (from `Barbatos.SecsGem.Objects`). E94's entire wire presence, and
the first provider in this stack to use all of stream 14 rather than its reading half.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ControlJobObjectProvider(ControlJobManager manager)` | |
| `ControlJobType` | `const string` = `"ControlJob"` | |

| Message | What it does |
| --- | --- |
| S14F5 | Creates a job, `Queued`. Named from an `ObjID`, `ObjectID` or `ControlJobID` attribute, else by the machine - S14F6 carries the name back. An unknown attribute refuses the whole create; so does any object type but `ControlJob`, an empty one included. |
| S14F7 | Deletes a finished job. `InvalidObjectInstance` for one nobody created, `Error` for one that is not over. |
| S14F9, S14F27 | `RCMD` goes to `ControlJobManager.Command` unchanged. `Successful`, or `Error` when the state refuses it. |
| S14F1, S14F19 | Reads `ObjID`, `State`, `Completion`, `CarrierInputSpec`, `StartMethod`, `ProcessOrderMgmt`, `ProcessingCtrlSpec`. A job that has not ended reads a zero-length `Completion`. |
| S14F3, S14F21 | Writes `CarrierInputSpec`, `StartMethod`, `ProcessOrderMgmt`, `ProcessingCtrlSpec`, through the manager, so only while the job is `Queued`. Everything else is `InvalidAttribute`, checked before anything is written. |
| S14F13, S14F15, S14F17 | The type, its instances - finished jobs included, until a host deletes them - and the attribute names. |

An object this provider does not have is **left out** of its answers rather than refused, so a
machine that also does E87 and E90 composes: each provider answers for its own objects, and
`ObjectServices` reports the rest as `InvalidObjectInstance`.

Tolerant read, exact write. `CarrierInputSpec` is written as a list and read from a list or from a
bare item; `StartMethod` is written as a name and read from a name or a number, with anything
unrecognised reading as `Automatic`.

## Events

`ControlJobStateChangedEventArgs`: `Job`, `Previous`, `Current`, and `Completion` - set on the one
transition that is an ending, `null` on every other. Raised after the move and inside the manager's
lock, so a handler reading the job sees the state the event announces.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extension on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddControlJobs(Action<ControlJobManager>? configure = null)` | `ControlJobManager` (jobs queued in `configure`), `ControlJobObjectProvider` as an `IObjectProvider`, and the eleven primaries of object services. |

One method, because E94 has one side. It composes with `AddCarrierEquipment` and
`AddSubstrateTracking`: all three providers answer, through `CompositeObjectProvider`.
