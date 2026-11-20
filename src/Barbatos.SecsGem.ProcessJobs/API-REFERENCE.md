# Barbatos.SecsGem.ProcessJobs - API reference

Namespace `Barbatos.SecsGem.ProcessJobs`, except where noted. Concepts, the state diagram and the
provenance note live in [`README.md`](README.md); this page is member-by-member.

- [`ProcessJobManager`](#processjobmanager)
- [`ProcessJob`](#processjob)
- [The states and the codes](#the-states-and-the-codes)
- [`ProcessJobResult`](#processjobresult)
- [`ProcessJobCommands`](#processjobcommands)
- [`ProcessJobEquipment`](#processjobequipment)
- [`ProcessJobHost`](#processjobhost)
- [The reports](#the-reports)
- [`ProcessJobObjectProvider`](#processjobobjectprovider)
- [Registration](#registration)

## `ProcessJobManager`

`sealed class`. The process jobs a machine has, and E40's rules. No connection and no messages,
even though E40 has a stream of its own: `ProcessJobEquipment` puts stream 16 in front of this, and
keeping them apart is what lets the whole of E40's sequencing be tested without a wire.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ProcessJobManager()` | |
| `JobSpace` | `ushort? { get; set; }` | `PRJOBSPACE`. `null` is "no stated limit", not zero. Counts jobs, not queued jobs. |
| `Jobs` | `IReadOnlyCollection<ProcessJob> { get; }` | A snapshot, taken under the lock. |
| `Queue` | `IReadOnlyList<string> { get; }` | The jobs still `Queued`, in the order they will be taken up. A snapshot. |
| `Create` | `ProcessJobResult Create(string processJobId, string recipeId, IReadOnlyList<string>? material = null, bool startsAutomatically = true, ProcessJobRecipeMethod recipeMethod = WithoutVariableTuning, ProcessJobMaterialOrder materialOrder = ArrivalSequence, uint? pauseEventId = null, IReadOnlyDictionary<string, string>? parameters = null)` | Starts a job `Queued`. Refused for no identifier, no recipe, a name already taken, or a full machine. The collections are copied. |
| `Deselect` | `ProcessJobResult Deselect(string processJobId)` | Only a `Queued` or `ProcessComplete` job. Takes it off the machine, leaving no record. |
| `Find` | `ProcessJob? Find(string processJobId)` | |
| `QueuePosition` | `ushort QueuePosition(string processJobId)` | `QPOSN`, counting from one. Zero for a job that is not waiting. |
| `Reorder` | `ProcessJobResult Reorder(IReadOnlyList<string> processJobIds)` | Every queued job exactly once. |
| `Describe` | `ProcessJobResult Describe(string processJobId, string? recipeId = null, ProcessJobRecipeMethod? recipeMethod = null, bool? startsAutomatically = null, ProcessJobMaterialOrder? materialOrder = null)` | Only while `Queued`. A `null` argument is "leave it"; an empty recipe is refused. |
| `SetPauseEvent` | `ProcessJobResult SetPauseEvent(string processJobId, uint? pauseEventId)` | Any time before the job is over. |
| `Command` | `ProcessJobResult Command(string processJobId, string command)` | Dispatches one of [`ProcessJobCommands`](#processjobcommands). Any casing; a command this equipment does not know answers `CMDA` 1 rather than 2. |
| `Start` | `ProcessJobResult Start(string processJobId)` | Only from `WaitingForStart`. |
| `Pause` | `ProcessJobResult Pause(string processJobId)` | Only from `Processing`. Lands in `Pausing`. |
| `Resume` | `ProcessJobResult Resume(string processJobId)` | Only from `Paused`. |
| `Stop` | `ProcessJobResult Stop(string processJobId)` | From `Processing`, `Pausing` or `Paused`. Lands in `Stopping`. |
| `Abort` | `ProcessJobResult Abort(string processJobId)` | From those three and `Stopping`. Lands in `Aborting`. |
| `Cancel` | `ProcessJobResult Cancel(string processJobId)` | Only from `Queued`, `SettingUp` or `WaitingForStart`. |
| `BeginSetup` | `ProcessJobResult BeginSetup(string processJobId)` | The machine's own: takes a queued job up and out of the queue. |
| `SetupComplete` | `ProcessJobResult SetupComplete(string processJobId)` | Goes to `Processing` or `WaitingForStart` by `StartsAutomatically`. |
| `PauseComplete` | `ProcessJobResult PauseComplete(string processJobId)` | `Pausing` → `Paused`. |
| `StopComplete` | `ProcessJobResult StopComplete(string processJobId)` | `Stopping` → `ProcessComplete`, completion `Stopped`. |
| `AbortComplete` | `ProcessJobResult AbortComplete(string processJobId)` | `Aborting` → `ProcessComplete`, completion `Aborted`. |
| `MaterialProcessed` | `ProcessJobResult MaterialProcessed(string processJobId)` | Only from `Processing`. Completion `Completed`. |
| `BindMaterial` | `ProcessJobResult BindMaterial(string processJobId, IReadOnlyList<string> material)` | Only `Queued` or `SettingUp`. Replaces. |
| `AddMaterial` | `ProcessJobResult AddMaterial(string processJobId, IReadOnlyList<string> material)` | Up to and including `Processing`. Appends; a duplicate refuses the whole call. |
| `ChangeParameter` | `ProcessJobResult ChangeParameter(string processJobId, string name, string value)` | A parameter the job has, and while it is not running - or while it is, if `RecipeMethod` is `WithVariableTuning`. |
| `JobStateChanged` | `event EventHandler<ProcessJobStateChangedEventArgs>?` | After the move, inside the lock. |

Every refusal carries a `Reason` naming the state that refused it. Handlers must not call back into
the manager - the events are raised under its lock. Nothing here refuses a second job in
`Processing`: a four-chamber tool runs four.

## `ProcessJob`

`sealed class`. Public getters, **internal setters**: the only thing that can move a process job is
the manager.

| Member | Type | Item | Notes |
| --- | --- | --- | --- |
| `ProcessJobId` | `string` | `PRJOBID` | |
| `State` | `ProcessJobState` | `PRSTATE` | |
| `Completion` | `ProcessJobCompletion?` | - | `null` while it has not ended. |
| `RecipeId` | `string` | `PPID` | |
| `RecipeMethod` | `ProcessJobRecipeMethod` | `PRRECIPEMETHOD` | Decides whether a parameter may change while processing. |
| `StartsAutomatically` | `bool` | `PRPROCESSSTART` | True means the machine starts it; false means it waits for `START`. |
| `MaterialOrder` | `ProcessJobMaterialOrder` | `PRMTRLORDER` | Carried, not acted on. |
| `PauseEventId` | `uint?` | `PRPAUSEEVENTID` | A `CEID`. Carried, not watched for. |
| `Material` | `IReadOnlyList<string>` | `MTRLID` | Identifiers, deliberately not E90 substrates. |
| `Parameters` | `IReadOnlyDictionary<string, string>` | `CPNAME`, `CPVAL` | |
| `IsFinished` | `bool` | - | `State is ProcessComplete`. |
| `IsActive` | `bool` | - | Anything but `Queued` and `ProcessComplete` - the job holds material. |

## The states and the codes

| Enum | Members and values |
| --- | --- |
| `ProcessJobState : byte` | `Queued = 0`, `SettingUp = 1`, `WaitingForStart = 2`, `Processing = 3`, `ProcessComplete = 4`, `Pausing = 6`, `Paused = 7`, `Stopping = 8`, `Aborting = 9` - `PRSTATE`'s own numbers, with 5 left out because it reserves it |
| `ProcessJobCompletion` | `Completed`, `Stopped`, `Aborted`, `Cancelled` - this library's, not E40's |
| `ProcessJobRecipeMethod : byte` | `WithoutVariableTuning = 1`, `WithVariableTuning = 2` - `PRRECIPEMETHOD`, which has no zero |
| `ProcessJobMaterialOrder : byte` | `ArrivalSequence = 1`, `MaximizeThroughput = 2`, `ListedSequence = 3` - `PRMTRLORDER` |
| `ProcessJobAcknowledgement : byte` | `Accepted = 0`, `Rejected = 1` - `PJACK`, which is undefined; the values are `PREACK`'s |
| `ProcessJobCommandAcknowledgement : byte` | `Performed = 0`, `DoesNotExist = 1`, `CannotPerformNow = 2` - `CMDA`, defined |
| `ProcessJobParameterAcknowledgement : byte` | `Accepted = 0`, `NameDoesNotExist = 1`, `IllegalValue = 2`, `IllegalFormat = 3`, `CannotChangeNow = 4` - `CPACK`, defined for 1 to 3 |
| `ProcessJobQueueAcknowledgement : byte` | `Acknowledged = 0` … `NoSuchObject = 6` - `HCACK`, defined; a reorder uses four of the seven |

An acknowledgement code E5 does not define never reads as success: an unknown `CMDA` reads as
`CannotPerformNow`, and an unknown `PJACK` as `Rejected`.

## `ProcessJobResult`

`sealed class`. `IsAccepted` (`required bool`), `Reason` (`string`), and the code each message can
carry: `Acknowledgement` (`PJACK`, derived from `IsAccepted`), `CommandAcknowledgement` (`CMDA`),
`ParameterAcknowledgement` (`CPACK`) and `QueueAcknowledgement` (`HCACK`).

Four codes rather than one, because the messages differ in what they can say. A command refused
from a state is `CannotPerformNow` and a command that does not exist is `DoesNotExist` - the
distinction E40 can make and E94 cannot.

The manager picks each code, rather than leaving whoever sends the reply to work it out from
`Reason`: a code chosen by looking for words in a sentence is a code that changes when somebody
rewords the sentence.

## `ProcessJobCommands`

`static class`, six `const string`: `START`, `PAUSE`, `RESUME`, `STOP`, `ABORT`, `CANCEL`. E40's own
command item has no code values in the catalogue, so these are taken from `CTLJOBCMD`, the one
place in it that spells job commands - which at least means a fab doing E40 and E94 sends one word
per verb.

## `ProcessJobEquipment`

`sealed class`. Answers the host's twelve primaries out of a manager, and sends two of its own.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ProcessJobEquipment(ProcessJobManager manager, HsmsConnection connection, TimeProvider? clock = null, ILogger<ProcessJobEquipment>? logger = null)` | The clock stamps S16F11. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S16F1, F3, F5, F7, F9, F15, F17, F19, F21, F23, F25, F27. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | |
| `ReportStateAsync` | `ValueTask<ProcessJobAcknowledgement> ReportStateAsync(string processJobId, ProcessJobState state, CancellationToken = default)` | S16F11. Takes the state rather than the job, because a report is what was true at a moment. |
| `ReportQueueAsync` | `ValueTask<ProcessJobAcknowledgement> ReportQueueAsync(CancellationToken = default)` | S16F29. Every job, not only the queued ones. |

A state change does **not** send an S16F11 by itself: the manager raises its event inside its own
lock, so the wiring belongs to the application. The README shows the three lines.

S16F6 and S16F20 answer with a zero-length list for a job the machine does not have, because
neither carries an acknowledgement code. A body that cannot be read answers `PJACK` 1 rather than
throwing - a handler that threw would leave the host waiting out T3.

## `ProcessJobHost`

`sealed class`. Twelve out, two in. No state of its own.

| Member | Signature | Message |
| --- | --- | --- |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S16F11, S16F29 |
| `CreateAsync` | `ValueTask<ProcessJobAcknowledgement> CreateAsync(string processJobId, string recipeId, IReadOnlyList<string>? material = null, CancellationToken = default)` | S16F1 |
| `CommandAsync` | `ValueTask<ProcessJobCommandAcknowledgement> CommandAsync(string processJobId, string command, CancellationToken = default)` | S16F3 |
| `CommandAsync` | `ValueTask<IReadOnlyList<ProcessJobCommandResult>> CommandAsync(IReadOnlyList<string> processJobIds, string command, CancellationToken = default)` | S16F23. Partial success is normal. |
| `GetStatusAsync` | `ValueTask<ProcessJobStatus?> GetStatusAsync(string processJobId, CancellationToken = default)` | S16F5. `null` means no such job. |
| `ListAsync` | `ValueTask<IReadOnlyList<ProcessJobStatus>> ListAsync(CancellationToken = default)` | S16F7 |
| `DeselectAsync` | `ValueTask<ProcessJobAcknowledgement> DeselectAsync(string processJobId, CancellationToken = default)` | S16F9 |
| `AddMaterialAsync` | `ValueTask<ProcessJobAcknowledgement> AddMaterialAsync(string processJobId, IReadOnlyList<string> material, CancellationToken = default)` | S16F15 |
| `BindMaterialAsync` | `ValueTask<ProcessJobAcknowledgement> BindMaterialAsync(string processJobId, IReadOnlyList<string> material, CancellationToken = default)` | S16F25 |
| `ChangeParametersAsync` | `ValueTask<IReadOnlyList<ProcessJobParameterResult>> ChangeParametersAsync(string processJobId, IReadOnlyList<ProcessJobParameter> parameters, CancellationToken = default)` | S16F17. One code per name. |
| `GetParametersAsync` | `ValueTask<IReadOnlyList<ProcessJobParameter>> GetParametersAsync(string processJobId, IReadOnlyList<string>? names = null, CancellationToken = default)` | S16F27. Empty names asks for all; a name the job lacks is absent from the answer. |
| `GetQueuePositionAsync` | `ValueTask<ProcessJobQueuePosition?> GetQueuePositionAsync(string processJobId, CancellationToken = default)` | S16F19 |
| `ReorderAsync` | `ValueTask<ProcessJobQueueAcknowledgement> ReorderAsync(IReadOnlyList<string> processJobIds, CancellationToken = default)` | S16F21 |
| `JobStateReported` | `event EventHandler<ProcessJobStateReportEventArgs>?` | S16F11 arriving |
| `QueueReported` | `event EventHandler<ProcessJobQueueReportEventArgs>?` | S16F29 arriving |

A reply with no acknowledgement code in it is a `SecsException`, never read as success. A host that
subscribes to neither event still answers both reports correctly.

## The reports

`ProcessJobStatus`, `ProcessJobQueuePosition`, `ProcessJobStateReport` and `ProcessJobQueueEntry`
each carry **two properties for the state**: `State` (`ProcessJobState?`) and `StateCode`
(`byte`). A machine that sends a code outside `PRSTATE`'s ten is not lying, it is using a state
this library has no name for - so the name is `null` and the code is what arrived. Reading an
unnamed code as `Queued`, the zero value, would tell a host the job has not started.

`ProcessJobStateReport.At` is `null` when the timestamp was absent or unreadable; the report is
still delivered, because which job it is about matters more than when.

`ProcessJobParameter` is a pair of strings. `ProcessJobParameterResult` and
`ProcessJobCommandResult` are a name or an identifier with the code that came back for it. The two
event argument types carry a settable `Acknowledgement`, which defaults to `Accepted`.

## `ProcessJobObjectProvider`

`sealed class : IObjectProvider` (from `Barbatos.SecsGem.Objects`). The half of E40 that is
stream 14: a job described, as against driven.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ProcessJobObjectProvider(ProcessJobManager manager)` | |
| `ProcessJobType` | `const string` = `"ProcessJob"` | |

Attributes, every name a data item the catalogue defines: `PRJOBID` (`A`), `PRSTATE` (`U1`),
`PPID` (`A`), `PRRECIPEMETHOD` (`U1`), `PRPROCESSSTART` (`BOOLEAN`), `PRMTRLORDER` (`U1`),
`PRPAUSEEVENTID` (`U4`, zero-length when there is none).

Settable: `PPID`, `PRRECIPEMETHOD`, `PRPROCESSSTART` and `PRMTRLORDER` while the job is `Queued`,
and `PRPAUSEEVENTID` any time before it is over - all through the manager, so the state machine
refuses what it would refuse anywhere. Everything else is `InvalidAttribute`, checked before
anything is written.

`ActAsync` maps E40's commands for a host that has stream 14 and not stream 16; a refusal arrives
as `Error`, which is why S16F3 is the better way round. `CreateAsync` and `DeleteAsync` are **not**
overridden, so S14F5 and S14F7 answer `InvalidObjectType`: E40 has S16F1 and S16F9, and a create
over stream 14 would have to invent a recipe.

Tolerant read, exact write: `PRRECIPEMETHOD` and `PRMTRLORDER` out of any numeric format or text,
`PRPROCESSSTART` out of a `BOOLEAN`, a word or a digit, and an undefined code reads as the value
that needs nothing of the machine.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddProcessJobEquipment(Action<ProcessJobManager>? configure = null)` | `ProcessJobManager` (`JobSpace` and any pre-created job in `configure`), `ProcessJobEquipment`, `ProcessJobObjectProvider` as an `IObjectProvider`, stream 16's twelve host primaries and the eleven of object services. |
| `AddProcessJobHost()` | `ProcessJobHost`, handlers for S16F11 and S16F29, and `AddObjectServices` so a host can read a job's attributes. No provider. |

Calling either twice is harmless: the primaries go in through the builder's `DeclareOnce`, which is
where that rule lives for every capability in this stack.
`AddProcessJobEquipment` composes with `AddCarrierEquipment`, `AddSubstrateTracking` and
`AddControlJobs` - four providers, one stream 14, through `CompositeObjectProvider`.

## Outliving the process

| Member | Signature | Notes |
| --- | --- | --- |
| `ProcessJobManager.Capture` | `ProcessJobManagerSnapshot Capture()` | Jobs and queue together, under the lock. `JobSpace` is deliberately not in it. |
| `ProcessJobManager.Restore` | `ProcessJobRestoreReport Restore(ProcessJobManagerSnapshot snapshot)` | Into a manager with no jobs. Raises nothing - which matters here, because `JobStateChanged` is wired to an S16F11. |

`ProcessJobManagerSnapshot` holds `Jobs` (`IReadOnlyList<ProcessJobSnapshot>`) and `Queue`.
`ProcessJobSnapshot` carries every `ProcessJob` property, parameters included.
`ProcessJobRestoreReport`: `IsAccepted`, `Reason`, `Restored`, `Adjusted`, `Count`.

A snapshot does not raise `JobSpace`: jobs past the limit are reported and skipped, and the queue
order decides which survive. The table of endings is in the
[README](README.md#outliving-the-process).
