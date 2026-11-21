# Barbatos.SecsGem.Gem - API reference

Namespace `Barbatos.SecsGem.Gem`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The equipment](#the-equipment)
  - [`GemEquipment`](#gemequipment)
- [State models](#state-models)
  - [`GemCommunicationState`](#gemcommunicationstate)
  - [`GemControlState`](#gemcontrolstate)
  - [`GemCommunicationStateChangedEventArgs`](#gemcommunicationstatechangedeventargs)
  - [`GemControlStateChangedEventArgs`](#gemcontrolstatechangedeventargs)
- [The object model](#the-object-model)
  - [`GemEquipmentModel`](#gemequipmentmodel)
  - [`GemEquipmentModelBuilder`](#gemequipmentmodelbuilder)
  - [`GemVariable`](#gemvariable)
  - [`GemVariableKind`](#gemvariablekind)
  - [`GemEquipmentConstant`](#gemequipmentconstant)
  - [`GemCollectionEvent`](#gemcollectionevent)
  - [`GemReport`](#gemreport)
  - [`GemAlarm`](#gemalarm)
- [Remote commands](#remote-commands)
  - [`GemRemoteCommandHandler`](#gemremotecommandhandler)
  - [`GemRemoteCommandContext`](#gemremotecommandcontext)
  - [`GemRemoteCommand`](#gemremotecommand)
- [Spooling](#spooling)
  - [On `GemEquipment`](#on-gemequipment)
  - [`GemSpoolState`](#gemspoolstate)
  - [`GemSpoolOverflowPolicy`](#gemspooloverflowpolicy)
  - [`GemSpooledDataRequest` / `GemSpooledDataAcknowledgement`](#gemspooleddatarequest--gemspooleddataacknowledgement)
  - [`GemSpooledMessage`](#gemspooledmessage)
  - [`IGemSpool`](#igemspool)
  - [`GemInMemorySpool`](#geminmemoryspool)
  - [`GemFileSpool`](#gemfilespool)
  - [`AddGemFileSpooling`](#addgemfilespooling)
  - [What the equipment does with a durable spool](#what-the-equipment-does-with-a-durable-spool)
  - [`AddGemSpooling`](#addgemspooling)
- [Limits monitoring](#limits-monitoring)
  - [`GemVariableLimit`](#gemvariablelimit)
  - [`GemVariableLimitDefinition`](#gemvariablelimitdefinition)
  - [`GemVariableLimitAcknowledgement`](#gemvariablelimitacknowledgement)
  - [`GemVariableLimitAttributes`](#gemvariablelimitattributes)
  - [`AddGemLimits`](#addgemlimits)
- [Trace data collection](#trace-data-collection)
  - [`GemTraceRequest`](#gemtracerequest)
  - [`GemTraceAcknowledgement`](#gemtraceacknowledgement)
  - [`AddGemTraces`](#addgemtraces)
- [Terminal services](#terminal-services)
  - [`IGemTerminal`](#igemterminal)
  - [`GemTerminalMessage`](#gemterminalmessage)
  - [`GemTerminalAcknowledgement`](#gemterminalacknowledgement)
  - [`GemInMemoryTerminal`](#geminmemoryterminal)
  - [`AddGemTerminal`](#addgemterminal)
- [Process programs](#process-programs)
  - [`IGemProcessProgramStore`](#igemprocessprogramstore)
  - [`GemInMemoryProcessProgramStore`](#geminmemoryprocessprogramstore)
  - [`IGemFormattedProcessProgramStore`](#igemformattedprocessprogramstore)
  - [`GemInMemoryFormattedProcessProgramStore`](#geminmemoryformattedprocessprogramstore)
  - [`GemFormattedProcessProgram`](#gemformattedprocessprogram)
  - [`GemProcessProgramCommand`](#gemprocessprogramcommand)
  - [`GemProcessProgramGrant`](#gemprocessprogramgrant)
  - [`GemProcessProgramAcknowledgement`](#gemprocessprogramacknowledgement)
- [The host side](#the-host-side)
  - [`GemHost`](#gemhost)
  - [`GemHostOptions`](#gemhostoptions)
  - [`GemEquipmentIdentity`](#gemequipmentidentity)
  - [`GemVariableDescription`](#gemvariabledescription)
  - [`GemConstantDescription`](#gemconstantdescription)
  - [`GemAlarmReport`](#gemalarmreport)
  - [`GemReceivedReport`](#gemreceivedreport)
  - [Event arguments](#event-arguments)
- [Acknowledgement codes](#acknowledgement-codes)
- [Options](#options)
  - [`GemOptions`](#gemoptions)
  - [`GemOptionsValidator`](#gemoptionsvalidator)
- [Hosting](#hosting)
  - [`AddGemEquipment`](#addgemequipment)
  - [`AddGemProcessPrograms`](#addgemprocessprograms)
  - [`AddGemFormattedProcessPrograms`](#addgemformattedprocessprograms)
  - [`AddGemHost`](#addgemhost)

---

## The equipment

### `GemEquipment`

`public sealed class GemEquipment : IAsyncDisposable`

Both state models, the standard message set, and the reporting that hangs off them. One instance
sits on one `HsmsConnection`; it does not run the connection, it uses it.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemEquipment(GemOptions options, GemEquipmentModel model, HsmsConnection connection, TimeProvider? timeProvider = null, ILogger<GemEquipment>? logger = null, IGemProcessProgramStore? processPrograms = null, IGemSpool? spool = null, IGemFormattedProcessProgramStore? formattedProcessPrograms = null)` | `null` provider means `TimeProvider.System`; `null` logger discards diagnostics. The last three are the opt-in capabilities, and `null` in each leaves its messages undeclared rather than declared and refused. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | The twenty-four core pairs. Registering them is how the equipment declares its capability - see below. |
| `ProcessProgramMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | The five unformatted stream 7 pairs - S7F1, S7F3, S7F5, and the S7F17/S7F19 both halves share - registered only when a store is configured. Kept separate so equipment with no recipes answers S9F3 for stream 7 rather than refusing each message in turn. |
| `FormattedProcessProgramMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | S7F23 and S7F25, registered only when a formatted store is configured. Independent of `ProcessProgramMessages`, and disjoint from it: a stream and function may be declared only once. |
| `SpoolMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | S6F23, registered only when a spool is configured. Equipment that does not spool answers S9F5 rather than RSDA 1 - "I do not do that" and "no, not now" are different answers to a host deciding whether to wait. |
| `TerminalMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | S10F3, S10F5 and S10F9, registered only when a terminal is configured. Not S10F1, which the equipment sends; not S10F7, which the catalogue flags as representative and which ACKC10 2 already says. |
| `CheckLimitsAsync` | `ValueTask<bool> CheckLimitsAsync(uint variableId, CancellationToken cancellationToken = default)` | E30 limits monitoring: checks a variable against the bands a host set, and reports a crossing. Called by the application when a value changes - see [Limits monitoring](#limits-monitoring). |
| `SendTerminalRequestAsync` | `ValueTask<GemTerminalAcknowledgement?> SendTerminalRequestAsync(string text, byte terminalId = 0, CancellationToken cancellationToken = default)` | S10F1: something an operator typed. `null` when there was nobody to tell - **not spooled**, because a prompt for a person at the host is worthless an hour later. Needs no `IGemTerminal`. |
| `RunAsync` | `Task RunAsync(CancellationToken cancellationToken = default)` | Drives the state models. Run it **alongside** `HsmsConnection.RunAsync`, not instead of it. Throws `InvalidOperationException` on a second call. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)` | Handles one inbound primary. Wired to every pair in `HandledMessages`. |
| `Model` | `GemEquipmentModel { get; }` | |
| `CommunicationState` | `GemCommunicationState { get; }` | |
| `ControlState` | `GemControlState { get; }` | |
| `IsCommunicating` | `bool { get; }` | Whether application messages may flow. **Not** the same as `HsmsConnection.IsSelected`. |
| `IsOnlineRemote` | `bool { get; }` | Whether the host may send remote commands. |
| `IsAlarmSet` | `bool IsAlarmSet(ulong alarmId)` | Whether an alarm is in alarm **right now**. Not the same question as `GemEquipmentModel.IsAlarmEnabled`, which is whether the host wants to hear about it. S5F6 reports this one in ALCD's top bit, and it is tracked even while nothing can be reported - the machine is in alarm whether or not there is a host to tell. |
| `Now` | `DateTime { get; }` | The time this equipment reports, including any offset a host set with S2F31. Never the operating system clock. |
| `TriggerEventAsync` | `ValueTask<bool> TriggerEventAsync(uint eventId, CancellationToken cancellationToken = default)` | Reports a collection event. `false` when the event is unknown, disabled, or there is nobody to tell. |
| `SetAlarmAsync` | `ValueTask<bool> SetAlarmAsync(ulong alarmId, CancellationToken cancellationToken = default)` | S5F1 with ALCD bit 7 set, then the linked set event. |
| `ClearAlarmAsync` | `ValueTask<bool> ClearAlarmAsync(ulong alarmId, CancellationToken cancellationToken = default)` | The same with the bit clear, then the linked clear event. |
| `PostExceptionAsync` | `ValueTask<bool> PostExceptionAsync(string exceptionId, string type, string message, CancellationToken cancellationToken = default)` | S5F9: EXID, EXTYPE and EXMESSAGE, timestamped from `Now`. Three strings rather than an identifier into a table, because the documented half of stream 5 has no exception namelist, no enable message and no way to ask what is outstanding. Spooled like a report - nothing will repeat it. `false` means nothing reached the host, kept or not; `SpoolCountActual` separates the two. |
| `SendDiscreteVariableDataAsync` | `ValueTask<bool> SendDiscreteVariableDataAsync(uint eventId, CancellationToken cancellationToken = default)` | S6F3: DATAID and CEID, which is all this reference's row names - **no values**, since the items that would carry them, `DVNAME` and `DVVAL`, are named by none of its 524 rows. Gated on the same two conditions as `TriggerEventAsync`, because S2F37 disables a CEID and this message names one. |
| `InquireMultiBlockAsync` | `ValueTask<GemMultiBlockGrant> InquireMultiBlockAsync(uint length, CancellationToken cancellationToken = default)` | S6F5, answered by S6F6's `GRANT6`. **Never spooled**: a question, not news. With no link it answers `NoSpace`, the table having no code for "nobody was listening" and `Busy` inviting a pointless retry. Sends nothing afterwards and measures nothing - how much fits in a block is the link's business and whether a report is worth the round trip is the application's. `DATALENGTH` is passed through as given; the reference records no units for it. |
| `GoOnline` | `bool GoOnline(bool remote = true)` | The equipment's own decision. Refused - returns `false` - unless communication is established. |
| `GoOffline` | `bool GoOffline()` | The equipment's own decision. |
| `CommunicationStateChanged` | `event EventHandler<GemCommunicationStateChangedEventArgs>?` | Raised on the thread that made the transition. Keep handlers short. |
| `ControlStateChanged` | `event EventHandler<GemControlStateChangedEventArgs>?` | As above. |
| `DisposeAsync` | `ValueTask DisposeAsync()` | Stops the retry timer. **Does not** dispose the connection, which it does not own. |

**What `HandledMessages` contains**

| S/F | Message | Answered with |
| --- | --- | --- |
| S1F1 | Are You There | S1F2 - identity, or a zero-length list while off-line |
| S1F3 | Selected Equipment Status Request | S1F4 |
| S1F11 | Status Variable Namelist Request | S1F12 - SVID, SVNAME, UNITS |
| S1F13 | Establish Communications Request | S1F14 (COMMACK) |
| S1F15 | Request Off-Line | S1F16 (OFLACK) |
| S1F17 | Request On-Line | S1F18 (ONLACK) |
| S1F21 | Data Variable Namelist Request | S1F22 - VID, DVVALNAME, UNITS. Data variables only: a status variable asked for here comes back named nothing, like any identifier the machine has not got |
| S1F23 | Collection Event Namelist Request | S1F24 - CEID, CENAME and the variables the event is about. The machine's own description, not the host's current report links |
| S2F13 | Equipment Constant Request | S2F14 |
| S2F15 | New Equipment Constant Send | S2F16 (EAC) |
| S2F17 | Date and Time Request | S2F18 |
| S2F29 | Equipment Constant Namelist Request | S2F30 - ECID, ECNAME, ECMIN, ECMAX, ECDEF, UNITS |
| S2F31 | Date and Time Set Request | S2F32 (TIACK) |
| S2F33 | Define Report | S2F34 (DRACK) |
| S2F35 | Link Event Report | S2F36 (LRACK) |
| S2F37 | Enable/Disable Event Report | S2F38 (ERACK) |
| S2F21 | Remote Command - the one with no parameters | S2F22 (CMDA). Same command table and same control-state rule as S2F41; seven codes narrowed onto three |
| S2F25 | Loopback Diagnostic Request | S2F26 - the body echoed. Answered **off-line as well as on**, since it changes nothing and refusing it would make the link untestable where a host most wants to test it |
| S2F41 | Host Command Send | S2F42 (HCACK) |
| S5F3 | Enable/Disable Alarm Send | S5F4 (ACKC5) |
| S5F5 | List Alarms Request | S5F6 - ALCD, ALID, ALTX |
| S5F7 | List Enabled Alarm Request | S5F8 - the enabled subset, same shape as S5F6 |
| S6F15 | Event Report Request | S6F16 - the event's reports, **even when the event is disabled** |
| S6F19 | Individual Report Request | S6F20 - one report's values |

Three details in the discovery messages are decisions rather than transcription:

- **An unknown identifier still gets an entry.** S1F12 and S2F30 answer with the identifier the
  host asked about and empty names, rather than dropping it, so the reply stays aligned with the
  request.
- **An undocumented bound is a zero-length item, not a zero.** `ECMIN`, `ECMAX` and `ECDEF` come
  from optional delegates on `GemEquipmentConstant`; where there is none, S2F30 says so. "No
  documented bound" and "the bound is zero" are different answers, and a host acting on the second
  would refuse values the machine accepts.
- **S6F15 answers a disabled event.** S2F37 governs what the equipment sends on its own; a host
  asking directly has already said it wants this one now.

S5F8 uses the same three-element shape as S5F6 - the enabled subset of the same listing - so a host
that can parse one needs no second parser. The reference table lists only ALID there, which would
have made the two replies gratuitously different.

Registering the list is not a convenience. The routing table is also the declaration of what an
entity understands, which is what lets the connection answer S9F5 for an unknown function in a
known stream and S9F3 for a stream that is not there at all.

**What it sends**

| S/F | When | Waits for |
| --- | --- | --- |
| S1F13 | The link reaches Selected, and on each retry | S1F14, under T3 |
| S6F11 | `TriggerEventAsync`, when the event is enabled | S6F12, when `EventReportsExpectReply` |
| S6F3 | `SendDiscreteVariableDataAsync`, under the same two gates | S6F4, when `EventReportsExpectReply` |
| S6F5 | `InquireMultiBlockAsync` | S6F6, always - the answer is the point |
| S5F1 | `SetAlarmAsync` / `ClearAlarmAsync`, when the alarm is enabled | S5F2, when `AlarmReportsExpectReply` |
| S5F9 | `PostExceptionAsync` | S5F10, when `ExceptionPostsExpectReply` |

---

## State models

### `GemCommunicationState`

`public enum GemCommunicationState`

| Member | Meaning |
| --- | --- |
| `Disabled` | Switched off. No S1F13 goes out; one that arrives is denied. |
| `WaitingForAcknowledge` | S1F13 is out and unanswered. E30's WAIT CRA. |
| `WaitingToRetry` | Enabled and not communicating: waiting out the retry interval, or waiting for a link. |
| `Communicating` | Both sides have agreed. Application messages may flow. |

E30 nests these - DISABLED, or ENABLED with NOT COMMUNICATING and COMMUNICATING under it. This
enum flattens the leaves, because a flat state is one a caller can switch on and a nested one is
three questions.

### `GemControlState`

`public enum GemControlState`

| Member | Meaning |
| --- | --- |
| `EquipmentOffline` | Off-line by the equipment's own decision. |
| `AttemptOnline` | Transient: trying to go on-line. |
| `HostOffline` | Off-line because the host asked with S1F15. |
| `OnlineLocal` | On-line, control at the machine. The host may read, not command. |
| `OnlineRemote` | On-line, control at the host. Remote commands are accepted. |

`EquipmentOffline` and `HostOffline` both refuse commands and are **not** the same event. An
implementation that collapses them cannot tell an operator why the machine will not take orders.

### `GemCommunicationStateChangedEventArgs`

`public sealed class GemCommunicationStateChangedEventArgs : EventArgs`

| Member | Signature |
| --- | --- |
| ctor | `GemCommunicationStateChangedEventArgs(GemCommunicationState previous, GemCommunicationState current, string? reason = null)` |
| `Previous` / `Current` | `GemCommunicationState { get; }` |
| `Reason` | `string? { get; }` |
| `ToString()` | `override string` - for example `WaitingForAcknowledge -> Communicating (S1F14 COMMACK=0)` |

### `GemControlStateChangedEventArgs`

`public sealed class GemControlStateChangedEventArgs : EventArgs`

The same shape over `GemControlState`.

---

## The object model

### `GemEquipmentModel`

`public sealed class GemEquipmentModel`

What the equipment publishes, plus the link state the host configures at run time. Built through
`GemEquipmentModelBuilder`; the constructor is internal.

| Member | Signature | Notes |
| --- | --- | --- |
| `Variables` | `IReadOnlyDictionary<uint, GemVariable> { get; }` | |
| `Constants` | `IReadOnlyDictionary<uint, GemEquipmentConstant> { get; }` | |
| `Events` | `IReadOnlyDictionary<uint, GemCollectionEvent> { get; }` | |
| `Alarms` | `IReadOnlyDictionary<ulong, GemAlarm> { get; }` | |
| `RemoteCommands` | `IReadOnlyDictionary<string, GemRemoteCommand> { get; }` | Ordinal comparison. |
| `DefinedReports` | `IReadOnlyCollection<uint> { get; }` | |
| `VariablesForReport` | `IReadOnlyList<uint> VariablesForReport(uint reportId)` | Empty when not defined. |
| `ReportsForEvent` | `IReadOnlyList<uint> ReportsForEvent(uint eventId)` | Empty when nothing is linked. |
| `IsEventEnabled` | `bool IsEventEnabled(uint eventId)` | |
| `IsAlarmEnabled` | `bool IsAlarmEnabled(ulong alarmId)` | |
| `Reset` | `void Reset()` | Back to what the model was built with. Called when communication is re-established. |
| `DefineReports` | `GemDefineReportAcknowledgement DefineReports(IReadOnlyList<(uint ReportId, IReadOnlyList<uint> VariableIds)> definitions)` | S2F33. Empty list deletes every report **and** every link; an entry with no variables deletes that one. |
| `LinkEventReports` | `GemLinkReportAcknowledgement LinkEventReports(IReadOnlyList<(uint EventId, IReadOnlyList<uint> ReportIds)> links)` | S2F35. Empty list deletes every link; an entry with no reports deletes that event's. |
| `SetEventsEnabled` | `GemEnableEventAcknowledgement SetEventsEnabled(IReadOnlyList<uint> eventIds, bool enabled)` | S2F37. Empty list means every event. |
| `SetAlarmEnabled` | `GemAlarmAcknowledgement SetAlarmEnabled(ulong alarmId, bool enabled)` | S5F3. Zero means every alarm. |

Reads are lock-free: the mutable state is one immutable value, replaced rather than edited, so an
event firing while the host redefines reports sees the old set or the new one and never half of
each.

Writes validate the whole batch before publishing any of it. **A rejected batch changes nothing**,
because a host told DRACK 4 is entitled to assume its definition was rejected in full.

### `GemEquipmentModelBuilder`

`public sealed class GemEquipmentModelBuilder`

| Member | Signature | Notes |
| --- | --- | --- |
| `AddStatusVariable` | `GemEquipmentModelBuilder AddStatusVariable(uint id, string name, Func<SecsItem> read, string units = "")` | Readable with S1F3. |
| `AddDataVariable` | `GemEquipmentModelBuilder AddDataVariable(uint id, string name, Func<SecsItem> read, string units = "")` | Meaningful only inside its event's report. |
| `AddVariable` | `GemEquipmentModelBuilder AddVariable(GemVariable variable)` | |
| `AddConstant` | `GemEquipmentModelBuilder AddConstant(GemEquipmentConstant constant)` | |
| `AddEvent` | `GemEquipmentModelBuilder AddEvent(uint id, string name, bool enabledByDefault = true)` | |
| `AddEvent` | `GemEquipmentModelBuilder AddEvent(uint id, string name, IReadOnlyList<uint> variableIds, bool enabledByDefault = true)` | The same, declaring what the event is *about* - answered by S1F24. Not the same thing as the reports a host links to it with S2F35: this is the machine's description and does not change when the host changes its subscription. The two-argument form is this one with an empty list, which S1F24 reports as an event about nothing in particular rather than as a refusal. |
| `AddReport` | `GemEquipmentModelBuilder AddReport(uint id, params uint[] variableIds)` | A default a host may replace with S2F33. |
| `AddAlarm` | `GemEquipmentModelBuilder AddAlarm(GemAlarm alarm)` | Refuses identifier zero, which S5F3 reserves for "every alarm". |
| `AddRemoteCommand` | `GemEquipmentModelBuilder AddRemoteCommand(string name, GemRemoteCommandHandler handler, bool requiresRemoteControl = true)` | |
| `Build` | `GemEquipmentModel Build()` | Throws `InvalidOperationException` when a default report names a variable that does not exist. |

Every `Add` throws `ArgumentException` on a duplicate identifier rather than overwriting. Two
variables sharing an SVID would surface as a host reading the wrong sensor, which is not a thing to
discover in a fab.

### `GemVariable`

`public sealed class GemVariable`

| Member | Signature | Notes |
| --- | --- | --- |
| `Id` | `required uint { get; init; }` | SVID or DVID. |
| `Name` | `required string { get; init; }` | |
| `Units` | `string { get; init; }` | E5 unit notation; empty when dimensionless. |
| `Kind` | `GemVariableKind { get; init; }` | Defaults to `Status`. |
| `Read` | `required Func<SecsItem> { get; init; }` | A new item each call. **The caller disposes it.** |

`Read` is a delegate rather than a field because a status variable is a question about the machine
right now. A cached one reports the chamber pressure from whenever the cache was filled, which
reads as a sensor fault rather than as a bug.

### `GemVariableKind`

`public enum GemVariableKind` - `Status` or `Data`. S1F3 answers status variables and returns a
zero-length item for a data variable; a report may name either.

### `GemEquipmentConstant`

`public sealed class GemEquipmentConstant`

| Member | Signature | Notes |
| --- | --- | --- |
| `Id` | `required uint { get; init; }` | ECID. |
| `Name` | `required string { get; init; }` | |
| `Units` | `string { get; init; }` | |
| `Read` | `required Func<SecsItem> { get; init; }` | The caller disposes what it returns. |
| `Write` | `Func<SecsItem, bool>? { get; init; }` | `null` is read-only. Return `false` to reject as out of range. The item passed in belongs to the incoming message: read it, do not keep it, do not dispose it. |
| `Minimum` | `Func<SecsItem>? { get; init; }` | For S2F30 only. `null` reports a zero-length item. |
| `Maximum` | `Func<SecsItem>? { get; init; }` | As above. |
| `Default` | `Func<SecsItem>? { get; init; }` | The factory default, for S2F30. |
| `IsWritable` | `bool { get; }` | |

Range *checking* belongs inside `Write`, and the three bounds exist for the namelist reply and
nothing else. They are not an alternative authority: a setpoint may be limited by what the chamber
is doing right now, which no static minimum can express, so `Write` always has the last word on
whether a value is accepted.

### `GemCollectionEvent`

`public sealed class GemCollectionEvent`

| Member | Signature |
| --- | --- |
| `Id` | `required uint { get; init; }` |
| `Name` | `required string { get; init; }` |
| `VariableIds` | `IReadOnlyList<uint> { get; init; }` - defaults to empty |
| `EnabledByDefault` | `bool { get; init; }` - defaults to `true` |

`VariableIds` is what the event is *about*, reported by S1F24, and is not the set of reports a host
has linked to it with S2F35. One is the machine's description of its own event and the other is the
host's current subscription; they move independently, and an empty list is an event about nothing
in particular rather than a refusal to say.

### `GemReport`

`public sealed class GemReport`

| Member | Signature |
| --- | --- |
| `Id` | `required uint { get; init; }` |
| `VariableIds` | `required IReadOnlyList<uint> { get; init; }` - in report order |

### `GemAlarm`

`public sealed class GemAlarm`

| Member | Signature | Notes |
| --- | --- | --- |
| `Id` | `required ulong { get; init; }` | ALID. Zero is refused. |
| `Text` | `required string { get; init; }` | ALTX. Truncated to 120 characters on the wire. |
| `SetEventId` | `uint { get; init; }` | The event raised when set, or zero for none. |
| `ClearEventId` | `uint { get; init; }` | The event raised when cleared, or zero for none. |
| `EnabledByDefault` | `bool { get; init; }` | Defaults to `true`. |

The alarm report and its collection event are two separate subscriptions. A host that disabled S5F1
with S5F3 may still be watching the event, so the event is raised either way.

---

## Remote commands

### `GemRemoteCommandHandler`

`public delegate ValueTask<GemHostCommandAcknowledgement> GemRemoteCommandHandler(GemRemoteCommandContext context, CancellationToken cancellationToken)`

Return `CompletionLater` for anything not finished when the handler returns, and raise a collection
event when it is. A handler that blocks until a wafer is processed is holding up the dispatch loop,
and therefore every other message on the link. A handler that throws is logged and answered
`CannotPerformNow`; the link carries on.

### `GemRemoteCommandContext`

`public sealed class GemRemoteCommandContext`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemRemoteCommandContext(string command, IReadOnlyDictionary<string, SecsItem> parameters)` | |
| `Command` | `string { get; }` | RCMD. |
| `Parameters` | `IReadOnlyDictionary<string, SecsItem> { get; }` | Keyed by CPNAME, ordinal. |
| `GetText` | `string? GetText(string name)` | The value when present and a text item. |
| `GetValue<T>` | `T? GetValue<T>(string name) where T : unmanaged, IEquatable<T>` | The first element when present and the width matches. |

The parameter items belong to the incoming S2F41 and are disposed with it. Read them inside the
handler; do not keep one past the call, and do not dispose one.

### `GemRemoteCommand`

`public sealed class GemRemoteCommand`

| Member | Signature | Notes |
| --- | --- | --- |
| `Name` | `required string { get; init; }` | |
| `Handler` | `required GemRemoteCommandHandler { get; init; }` | |
| `RequiresRemoteControl` | `bool { get; init; }` | Defaults to `true`. `false` for a command that only reads, which E30 does not forbid in on-line local. |

---

## Spooling

Opt-in. Without `AddGemSpooling` or `AddGemFileSpooling`, S6F23 is not in the routing table and the connection answers S9F5
for it.

### On `GemEquipment`

| Member | Signature | Notes |
| --- | --- | --- |
| `SpoolMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | Just S6F23. Separate from `HandledMessages` so equipment that does not spool answers S9F5 rather than RSDA 1. |
| `IsSpooling` | `bool { get; }` | A spool is configured **and** `GemOptions.SpoolingEnabled` is set. |
| `SpoolState` | `GemSpoolState { get; }` | |
| `SpoolCountActual` | `int { get; }` | Held and not yet transmitted. E30's SpoolCountActual. |
| `SpoolCountTotal` | `int { get; }` | Held since spooling last became active. Successful holds only. |
| `SpoolCountDiscarded` | `int { get; }` | Lost because the spool was full. E30 leaves this to be inferred; counting it saves a host doing arithmetic to find out whether it missed anything. |
| `SpoolStartTime` | `DateTimeOffset? { get; }` | When spooling became active, or `null`. |
| `SpoolFullTime` | `DateTimeOffset? { get; }` | When the spool last filled up, or `null`. |

`SpoolState` and `SpoolCountActual` change together, in the same step - so a host reading both never
sees a spool that is empty and active at once.

### `GemSpoolState`

`public enum GemSpoolState` - `Inactive`, `Active`, `Full`.

`Full` means the last attempt to hold a message was refused, or something had to be dropped to make
room. Defined that way rather than as "the count reached the capacity", because capacity belongs to
the `IGemSpool` and a custom one need not have a number to compare against.

### `GemSpoolOverflowPolicy`

`public enum GemSpoolOverflowPolicy`

| Member | Behaviour |
| --- | --- |
| `DiscardNewest` | Refuse the new message, keep what is held. The default. |
| `DiscardOldest` | Drop the oldest to make room. |

### `GemSpooledDataRequest` / `GemSpooledDataAcknowledgement`

S6F23 RSDC: `Transmit` 0, `Purge` 1. S6F24 RSDA: `Accepted` 0, `Denied` 1.

**Order on the wire: the S6F24 first, then the spooled messages.** A `Transmit` is acknowledged and
then transmitted, and the transmit is queued to the equipment's own loop only after the S6F24 has
been flushed - never before it, because the loop runs on another thread and the two would race for
the connection's write lock. A request with W clear gets no S6F24 and is transmitted all the same.
When the S6F24 cannot be sent (the link went away under it) nothing is queued and everything stays
held for the host's next S6F23.

RSDC is read in any numeric format. `Barbatos.SecsGem.Catalog` flags S6F23 as a representative
subset and gives its body as `DATAID` with RSDC as `U2`, while E30 says `U1` - so being strict about
the width would mean failing against a host built from the other reading. RSDA is written as `B`,
like every other acknowledgement code in this layer.

### `GemSpooledMessage`

`public sealed class GemSpooledMessage`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemSpooledMessage(byte stream, byte function, bool replyExpected, ReadOnlyMemory<byte> body, DateTimeOffset spooledAt, string? name = null)` | |
| `Stream` / `Function` / `ReplyExpected` / `Name` | | |
| `Body` | `ReadOnlyMemory<byte> { get; }` | The **encoded** item tree. Empty for a message with no body. |
| `SpooledAt` | `DateTimeOffset { get; }` | |
| `From` | `static GemSpooledMessage From(SecsMessage message, DateTimeOffset spooledAt)` | Encodes. Does not consume the message. |
| `ToMessage` | `SecsMessage ToMessage()` | Decodes. **The caller owns the result.** |

The body is bytes rather than a live item tree for two reasons: a message above the pooled
threshold holds rented memory, and a thousand of them waiting for a host that returns in an hour
would pin a thousand buffers out of `ArrayPool` for that hour - and bytes are what a durable spool
needs anyway.

### `IGemSpool`

| Member | Signature |
| --- | --- |
| `CountAsync` | `ValueTask<int> CountAsync(CancellationToken cancellationToken)` |
| `TryAppendAsync` | `ValueTask<bool> TryAppendAsync(GemSpooledMessage message, CancellationToken cancellationToken)` |
| `TryPeekAsync` | `ValueTask<GemSpooledMessage?> TryPeekAsync(CancellationToken cancellationToken)` |
| `RemoveHeadAsync` | `ValueTask<bool> RemoveHeadAsync(CancellationToken cancellationToken)` |
| `PurgeAsync` | `ValueTask PurgeAsync(CancellationToken cancellationToken)` |

Taking is two steps - peek, then remove - rather than one dequeue. A transmit that fails half way
must leave the rest spooled **in order**, and a dequeue that has to put something back puts it at
the wrong end.

### `GemInMemorySpool`

`public sealed class GemInMemorySpool : IGemSpool`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemInMemorySpool(int capacity = 1000, GemSpoolOverflowPolicy overflow = DiscardNewest)` | Throws `ArgumentOutOfRangeException` for a capacity below 1: a spool that holds nothing is a slower way of dropping the message. |
| `Capacity` / `Overflow` | | |
| `Discarded` | `int { get; }` | |

Bounded, and never grows past its capacity - the only honest way to hold data for a host that may
never come back. Not durable.

### `GemFileSpool`

`public sealed class GemFileSpool : IGemSpool`

A spool that survives the equipment's own restart, by keeping each held message in its own file.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemFileSpool(string directory, int capacity = 1000, GemSpoolOverflowPolicy overflow = DiscardNewest, ILogger<GemFileSpool>? logger = null)` | Creates the directory and adopts the records already in it. Throws `ArgumentOutOfRangeException` below capacity 1, and `IOException` for a directory it cannot create or read. |
| `Directory` | `string { get; }` | A full path, resolved in the constructor - a relative path that worked at start-up and not after a working-directory change is a fab-only failure. |
| `Capacity` / `Overflow` | | As `GemInMemorySpool`. |
| `Discarded` | `int { get; }` | Dropped because the spool was full. |
| `Unreadable` | `int { get; }` | Dropped because the record could not be read back. |
| `RecordExtension` | `const string` | `.spool` |
| `PartialExtension` | `const string` | `.spooling` - a record being written. |

**One file per message, not one append-only log.** Removing the head of a log needs a read cursor
as durable as the log itself, the log needs compacting, and a record half written when the power
went needs a checksum to find. A file per message replaces all three with a rename: the record is
written as `.spooling` and `File.Move`d into place, and a move within one directory is atomic, so a
message is either entirely a record or not a record at all.

The order is held in memory and the files are the durable copy, so counting, appending and removing
cost no directory scan. Only `TryPeekAsync` reads a file - once per message transmitted - and the
scan happens once, in the constructor.

Records are named with a zero-padded sequence rather than a timestamp, because reports raised in
the same millisecond still have an order. A reopened spool continues from the highest number it
finds. A leftover `.spooling` file is deleted on open: its write never finished, so `TryAppendAsync`
never returned and nothing was told the message was held. A file whose name is not a sequence number
is left alone.

Writes go through `FileOptions.WriteThrough` - a disk round trip per held message, which is the
point: a buffered write the operating system has not flushed is what a power cut takes.

`TryPeekAsync` drops a record it cannot read and counts it in `Unreadable`, which makes it the one
method here that both reads a file and changes what is held. Retrying it instead would mean every
S6F23 stopping on the same file and never delivering the messages behind it. A disk or permission
error, by contrast, propagates: deleting data on the strength of a transient failure is not a
repair, and the equipment handles those - see below.

One spool per directory.

### `AddGemFileSpooling`

```csharp
public static ISecsGemBuilder AddGemFileSpooling(this ISecsGemBuilder builder, string? directory = null)
```

Registers a `GemFileSpool` over `directory`, or over `GemOptions.SpoolDirectory` when that is
`null`, and declares `GemEquipment.SpoolMessages` with it. Uses `GemOptions.SpoolCapacity` and
`GemOptions.SpoolOverflow`, the same as the in-memory spool.

Throws `InvalidOperationException` at the first resolve when neither source gives a directory, not
at the first held message: a spool that cannot be built is a configuration fault.

### What the equipment does with a durable spool

Two behaviours exist for this spool and are harmless with the in-memory one.

`RunAsync` **adopts whatever the spool is already holding**, before anything else. S6F23 Transmit is
gated on `SpoolCountActual`, so equipment that started at zero over a spool holding a hundred
messages would answer RSDA 1 - "there is nothing spooled" - while a hundred reports sat on the disk.
`SpoolCountActual` and `SpoolCountTotal` are seeded from the count, the state becomes `Active`, and
`SpoolStartTime` comes from **the oldest held message** rather than from the restart. Spooling
switched off adopts nothing, and leaves the records where they are.

`IOException` and `UnauthorizedAccessException` from a spool are handled rather than thrown:

| Where | What happens |
| --- | --- |
| Holding a message | Logged, counted in `SpoolCountDiscarded`, state `Full`, and `TriggerEventAsync` returns `false`. From the host's side "out of room" and "out of disk" are the same thing. |
| Reading the spool to transmit | Logged, transmitting stops, everything stays held for the next S6F23. |
| Removing a message already sent | Logged, transmitting stops. |

A held message whose body is not a decodable item tree is dropped by the transmit loop, separately
from the `SecsException` that means the link failed - that one leaves the message at the head on
purpose, and this one can never be sent at all.

### `AddGemSpooling`

```csharp
public static ISecsGemBuilder AddGemSpooling(this ISecsGemBuilder builder)
public static ISecsGemBuilder AddGemSpooling<TSpool>(this ISecsGemBuilder builder)
    where TSpool : class, IGemSpool
public static ISecsGemBuilder AddGemSpooling(this ISecsGemBuilder builder, IGemSpool spool)
```

The parameterless overload builds a `GemInMemorySpool` from `GemOptions.SpoolCapacity` and
`GemOptions.SpoolOverflow`. Registering a spool is the switch; `SpoolingEnabled` is the
configuration override, and defaults to `true` because having registered one the sensible thing is
to use it.

---

## Limits monitoring

E30's other way of not polling: the host sets the bands, the equipment reports a collection event
when a variable leaves one or comes back. Opt-in through `AddGemLimits`.

**Three of this capability's four messages - S2F45, S2F46 and S2F48 - are representative entries in
this repository's catalogue rather than definitions; only the S2F47 request is firm.** The shapes
below follow SEMI E5 and the catalogue's item lists corroborate them item for item and in order,
including the per-variable LVACK beside the per-limit LIMITACK. The tests prove both ends of this
library agree; only a copy of E5 can say whether they agree with another host. They live in one
place, `GemLimitCodec`.

### `GemVariableLimit`

`public sealed class GemVariableLimit`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemVariableLimit(uint id, double lower, double upper)` | Throws `ArgumentOutOfRangeException` for an upper below its lower, or a bound that is NaN. |
| `Id` | `uint { get; }` | LIMITID. |
| `Lower` / `Upper` | `double { get; }` | LOWERDB and UPPERDB. |
| `Contains` | `bool Contains(double value)` | **Inclusive at both ends**, which makes a band of `[x, x]` mean "exactly x". |

The band is the deadband. A value inside it is in the zone and stays there until it is outside, so a
wobble inside the band is silent - which is the whole reason E5 names the bounds after a deadband
rather than a threshold.

### `GemVariableLimitDefinition`

`public sealed class GemVariableLimitDefinition` - `VariableId` and `Limits`, copied. A definition
with no limits removes every limit on that variable.

### `GemVariableLimitAcknowledgement`

`public enum GemVariableLimitAcknowledgement : byte` - LIMITACK.

| Member | Value |
| --- | --- |
| `Accepted` | 0 |
| `DefinitionError` | 1 |
| `CannotPerformNow` | 2 |
| `NoSuchVariable` | 3 |
| `ValueError` | 4 |

### `GemVariableLimitReply` / `GemVariableLimitResult`

`Accepted` is VLAACK - whether the whole definition was taken. `Variables` carries one
`GemVariableLimitResult` each: its own `Accepted` (LVACK) and a `Limits` list of
`(uint Id, GemVariableLimitAcknowledgement Acknowledgement)` in the order they were sent.

Per variable it is all or nothing: one unusable band and none of that variable's bands are in
force, because a half-configured variable would have the host believe it is watching a band that is
not there. The per-band answers still say which one - `4` for the band that was not a band, `1` for
the others, `3` against every band on a variable the machine does not have.

### `GemVariableLimitAttributes`

`public sealed class GemVariableLimitAttributes` - one S2F48 entry: `VariableId`, `Units`,
`Minimum` and `Maximum` as `double?`, and `Limits`.

`Minimum` and `Maximum` are the **variable's** documented range rather than a band, and are `null`
when the equipment documents none. This library's own equipment carries no range for a status
variable and reports a zero-length item for them, which is what E5 uses for "no documented bound".

### On `GemEquipment`

| Member | Signature | Notes |
| --- | --- | --- |
| `LimitMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | S2F45 and S2F47. |
| `CheckLimitsAsync` | `ValueTask<bool> CheckLimitsAsync(uint variableId, CancellationToken cancellationToken = default)` | Called **by the application** when a value changes; nothing here knows when one does. `true` only when a crossing was found and a report went out - the crossing is recorded either way, so the next one is measured from here. |

Redefining a band forgets which side the value was on: that is the host's own edit, and reporting a
crossing for the difference would be reporting it back at them.

### `AddGemLimits`

```csharp
public static ISecsGemBuilder AddGemLimits(this ISecsGemBuilder builder)
```

Declares `GemEquipment.LimitMessages`. Needs nothing registered with it; what it does need is a
`GemVariable.LimitEventId` on the variables worth watching, since S2F45 carries no CEID and E30
leaves the event to the equipment.

---

## Trace data collection

E30's trace capability: the host names variables, a period and a count, and the equipment samples
on its own clock and reports the samples in groups. Opt-in through `AddGemTraces`, and the one
capability here that needs nothing registered with it - a trace samples status variables the model
already has.

### `GemTraceRequest`

`public sealed class GemTraceRequest`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemTraceRequest(uint id, TimeSpan period, int totalSamples, int groupSize, IReadOnlyList<uint> variableIds)` | Copies the identifiers. |
| `Id` | `uint { get; }` | TRID. |
| `Period` | `TimeSpan { get; }` | DSPER. Travels as `hhmmss` or `hhmmsscc`, so finer than a hundredth of a second rounds away and a hundred hours does not fit. |
| `TotalSamples` | `int { get; }` | TOTSMP. **Zero cancels the trace with this identifier.** |
| `GroupSize` | `int { get; }` | REPGSZ: how many samples travel in one S6F1. |
| `VariableIds` | `IReadOnlyList<uint> { get; }` | The SVIDs, in the order they are reported. |

A request for zero samples is a request to stop, because E5 defines S2F23 as *initialising* a
trace. The standard leaves room for reading zero as "run for ever"; this is the safer reading - a
host that meant "stop" and was understood as "for ever" floods, and the other way round it simply
asks again.

### `GemTraceAcknowledgement`

`public enum GemTraceAcknowledgement : byte` - TIAACK.

| Member | Value | Means |
| --- | --- | --- |
| `Accepted` | 0 | The trace is running. |
| `TooManyVariables` | 1 | More than `GemOptions.MaxTraceVariables`. |
| `NoMoreTracesAllowed` | 2 | As many running as `GemOptions.MaxTraces`. |
| `InvalidPeriod` | 3 | DSPER was not a period this equipment can sample on. |
| `UnknownVariable` | 4 | Not a variable it has - or a *data* variable, which only has a value inside the event it belongs to. |
| `InvalidGroupSize` | 5 | REPGSZ does not fit the samples asked for. |

E5's own table is these six codes; `Barbatos.SecsGem.Catalog` flags S2F24 as a representative entry
and lists only 0 and 1. Both agree that zero is accepted, so a host that tests for zero is safe
against either reading.

A body that is not the five items E5 defines has no field to blame, and TIAACK has no code for
"that made no sense", so the code for one is a best effort - what a host must read from it is that
it is not zero.

### On `GemEquipment`

| Member | Notes |
| --- | --- |
| `TraceMessages` | S2F23. S6F1 is the equipment's to send, through the same path as an event report - so a group that cannot be delivered is spooled. |

Sampling happens on the equipment's own loop; the timer only says which trace is due. Each sample
is the value at its own moment, the last group goes even when it is short of REPGSZ, and SMPLN and
STIME describe the last sample in the group. A re-initialised TRID needs no new slot, and every
trace is cancelled where the host's report definitions are reset.

### `AddGemTraces`

```csharp
public static ISecsGemBuilder AddGemTraces(this ISecsGemBuilder builder)
```

Declares `GemEquipment.TraceMessages` and nothing else. Equipment that leaves it out answers S9F5
for S2F23, which is better than accepting a trace it will not run.

---

## Terminal services

E30's stream 10 capability: text in front of whoever is standing at the machine, and text back
from them. Opt-in - without `AddGemTerminal` the stream is not in the routing table and the
connection answers S9F3 for it, which is the honest answer for equipment with no screen rather
than ACKC10 2 for every message.

### `IGemTerminal`

`public interface IGemTerminal`

| Member | Signature | Answers |
| --- | --- | --- |
| `DisplayAsync` | `ValueTask<GemTerminalAcknowledgement> DisplayAsync(GemTerminalMessage message, CancellationToken cancellationToken)` | S10F4, S10F6 or S10F10 |

One method, because the whole content of this capability is outside the library: a terminal is a
screen, a line on an HMI, a row in an operator's work queue, or a chime.

It may be called from the connection's dispatch loop, so a slow one holds up every other message
on the link. **An implementation that waits for a human has already got the wrong answer** - accept
the message, return, and show it in your own time.

The other direction is `GemEquipment.SendTerminalRequestAsync`, not a method here: the application
initiates it, and nothing calls into an operator.

### `GemTerminalMessage`

`public sealed class GemTerminalMessage`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemTerminalMessage(byte terminalId, IReadOnlyList<string> lines, bool isBroadcast = false)` | Copies `lines`. Throws `ArgumentNullException` for a null list or a null block. |
| `TerminalId` | `byte { get; }` | TID. Meaningless for a broadcast. |
| `Lines` | `IReadOnlyList<string> { get; }` | The blocks, in order. One entry for a single-block message - a deliberate shape, since a terminal that lays out one line and one that lays out two are the same code. |
| `IsBroadcast` | `bool { get; }` | S10F9: for every terminal. Equipment with one cannot tell the difference any other way. |

Blocks are neither trimmed nor dropped when empty. TEXT is a variable-length field, so its spaces
are somebody's rather than padding, and a blank line between two paragraphs is the layout.

### `GemTerminalAcknowledgement`

`public enum GemTerminalAcknowledgement : byte` - ACKC10.

| Member | Value | Means |
| --- | --- | --- |
| `Accepted` | 0 | Taken for display. Not "somebody read it". |
| `WillNotDisplay` | 1 | It arrived and will not be shown. A decision. |
| `TerminalNotAvailable` | 2 | There is no such terminal, or it cannot be reached. A fault, and the only one of the three worth retrying. |
| `Other` | 3 | Anything in 3-63, which `ACKC10` reserves and does not name. The arriving code goes to the log. |

### `GemInMemoryTerminal`

`public sealed class GemInMemoryTerminal : IGemTerminal`

| Member | Signature | Default |
| --- | --- | --- |
| `Capacity` | `int { get; set; }` | `64` messages; older ones are dropped |
| `Acknowledgement` | `GemTerminalAcknowledgement { get; set; }` | `Accepted` - settable so a test can be a terminal that is switched off |
| `Displayed` | `IReadOnlyCollection<GemTerminalMessage> { get; }` | Oldest first |

Keeps what it was asked to display and shows it to nobody. Enough for a simulator, a test, or
equipment whose operator interface is somewhere else and only needs the messages to arrive.

### `AddGemTerminal`

```csharp
public static ISecsGemBuilder AddGemTerminal(this ISecsGemBuilder builder)
public static ISecsGemBuilder AddGemTerminal<TTerminal>(this ISecsGemBuilder builder)
    where TTerminal : class, IGemTerminal
public static ISecsGemBuilder AddGemTerminal(this ISecsGemBuilder builder, IGemTerminal terminal)
```

Registers the terminal and, with it, `GemEquipment.TerminalMessages`. The parameterless overload
uses `GemInMemoryTerminal`. Calling it twice is harmless: the pairs go in through
the builder's `DeclareOnce`.

---

## Process programs

Everything here is opt-in, and in two halves. Without `AddGemProcessPrograms` the unformatted
messages are not in the routing table; without `AddGemFormattedProcessPrograms` the formatted ones
are not; without either, stream 7 is unknown and the connection answers S9F3 for all of it.

### `IGemProcessProgramStore`

`public interface IGemProcessProgramStore`

| Member | Signature | Answers |
| --- | --- | --- |
| `InquireAsync` | `ValueTask<GemProcessProgramGrant> InquireAsync(string id, int length, CancellationToken cancellationToken)` | S7F2 (PPGNT) |
| `SaveAsync` | `ValueTask<GemProcessProgramAcknowledgement> SaveAsync(string id, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)` | S7F4 (ACKC7) |
| `LoadAsync` | `ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string id, CancellationToken cancellationToken)` | S7F6; `null` means no such program |
| `DeleteAsync` | `ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(string id, CancellationToken cancellationToken)` | S7F18 |
| `DeleteAllAsync` | `ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken)` | S7F18, for an S7F17 carrying an empty list |
| `ListAsync` | `ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)` | S7F20 |

**The body passed to `SaveAsync` is only valid for the length of the call.** It is a window onto the
message that arrived, and that message is disposed as soon as the handler returns. Copy what you
keep.

Every method may be called from the connection's dispatch loop, so a slow one holds up every other
message on the link. Write a large program in the background and answer
`GemProcessProgramAcknowledgement.PerformedLater`.

### `GemInMemoryProcessProgramStore`

`public sealed class GemInMemoryProcessProgramStore : IGemProcessProgramStore`

| Member | Signature | Default |
| --- | --- | --- |
| `MaxIdLength` | `const int` | `120` - E5's cap on a PPID |
| `Capacity` | `int { get; set; }` | `64` programs |
| `MaxBodyLength` | `int { get; set; }` | 1 MiB |
| `Count` | `int { get; }` | |

Copies what it is given. Replacing an existing program is not a new slot, so it still fits at
capacity. An empty or over-long identifier is `InvalidId`.

### `IGemFormattedProcessProgramStore`

`public interface IGemFormattedProcessProgramStore`

The structured half of stream 7, opt-in separately from the unformatted one.

| Member | Signature | Answers |
| --- | --- | --- |
| `SaveAsync` | `ValueTask<GemProcessProgramAcknowledgement> SaveAsync(GemFormattedProcessProgram program, CancellationToken cancellationToken)` | S7F24 (ACKC7) |
| `LoadAsync` | `ValueTask<GemFormattedProcessProgram?> LoadAsync(string id, CancellationToken cancellationToken)` | S7F26; `null` means no such program, and is answered with a zero-length list |
| `DeleteAsync` | `ValueTask<GemProcessProgramAcknowledgement> DeleteAsync(string id, CancellationToken cancellationToken)` | S7F18 |
| `DeleteAllAsync` | `ValueTask<GemProcessProgramAcknowledgement> DeleteAllAsync(CancellationToken cancellationToken)` | S7F18, for an S7F17 carrying an empty list |
| `ListAsync` | `ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)` | S7F20 |

There is no `InquireAsync`. The formatted load inquire is S7F33, which the catalogue flags as a
representative entry rather than a definition - so it is not implemented rather than guessed at.

`DeleteAsync`, `DeleteAllAsync` and `ListAsync` are here because S7F17 and S7F19 are not
per-format. With both stores registered the equipment merges them: one directory with no identifier
listed twice, and a delete that any store accepted is ACKC7 0. A store that answers anything other
than `Accepted` or `NotFound` - `Busy`, `PerformedLater` - wins the merge, because telling a host
`Accepted` while one store is still working would have it believe the identifier is free.

Unlike the unformatted `SaveAsync`, nothing borrows the inbound message: the program was read out
of it into strings before the call, so there is no copy to remember to make.

### `GemInMemoryFormattedProcessProgramStore`

`public sealed class GemInMemoryFormattedProcessProgramStore : IGemFormattedProcessProgramStore`

| Member | Signature | Default |
| --- | --- | --- |
| `Capacity` | `int { get; set; }` | `64` programs |
| `Count` | `int { get; }` | |

Replacing an existing program is not a new slot, so it still fits at capacity. An empty or
over-long identifier - over `GemInMemoryProcessProgramStore.MaxIdLength` - is `NotFound`.

### `GemFormattedProcessProgram`

`public sealed class GemFormattedProcessProgram`

S7F23's body as data: the four items E5 defines, in order.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemFormattedProcessProgram(string id, string modelName, string softwareRevision, IReadOnlyList<GemProcessProgramCommand> commands)` | Copies `commands`. Throws `ArgumentNullException` for a null argument or a null command. |
| `Id` | `string { get; }` | PPID, trimmed. |
| `ModelName` | `string { get; }` | MDLN **as the host sent it**, trimmed. Not compared against `GemOptions.ModelName` - whether a machine accepts a recipe written for another model is the store's decision. |
| `SoftwareRevision` | `string { get; }` | SOFTREV as the host sent it, trimmed. |
| `Commands` | `IReadOnlyList<GemProcessProgramCommand> { get; }` | May be empty: a program with no commands is a program, which is a different answer from no such program. |
| `ToString` | `override string ToString()` | For example `RECIPE-7 (BRBTOS 1.0.0, 3 commands)`. |

### `GemProcessProgramCommand`

`public sealed class GemProcessProgramCommand`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemProcessProgramCommand(byte code, IReadOnlyList<string> parameters)` | Copies `parameters`. Throws `ArgumentNullException` for a null list or a null parameter. |
| `Code` | `byte { get; }` | CCODE, which E5 types `U1[1]`. A wider unsigned item is read when the value fits; one that does not is ACKC7 5. |
| `Parameters` | `IReadOnlyList<string> { get; }` | PPARM, **not trimmed**: S7F26 has to give back what S7F23 sent, and a trim is not reversible. |
| `ToString` | `override string ToString()` | For example `CCODE 12 (3 parameters)`. |

A PPARM is text here. E5 lets one be any item at all, and a body that is the right shape but
carries a numeric or binary parameter is answered `ModeUnsupported` rather than reshaped into
something storable - the equipment does formatted process programs, just not that flavour of one.

### `GemProcessProgramGrant`

`public enum GemProcessProgramGrant : byte` - S7F2 PPGNT.

| Member | Value |
| --- | --- |
| `Ok` | 0 |
| `AlreadyHave` | 1 |
| `NoSpace` | 2 |
| `InvalidId` | 3 |
| `Busy` | 4 |
| `WillNotAccept` | 5 |
| `Other` | 6 |

### `GemProcessProgramAcknowledgement`

`public enum GemProcessProgramAcknowledgement : byte` - ACKC7.

| Member | Value |
| --- | --- |
| `Accepted` | 0 |
| `PermissionNotGranted` | 1 |
| `LengthError` | 2 |
| `MatrixOverflow` | 3 |
| `NotFound` | 4 |
| `ModeUnsupported` | 5 |
| `PerformedLater` | 6 |
| `Other` | 7 |

ACKC7 has no code for "that body made no sense", so a PPBODY that did not arrive as a binary item
is answered `LengthError` rather than stored as an empty program.

---

## Every acknowledgement's reserved range

**Sixteen of the acknowledgement items this package reads give E5 a reserved range**, and each of
those enums now has a member at the first reserved code - named `Other` except where a single
non-zero member already covered the whole range:

| item | named codes | reserved | member at the boundary |
| --- | --- | --- | --- |
| `OFLACK` | 0 | 1-63 | `GemOfflineAcknowledgement.Other` |
| `ONLACK` | 0-2 | 3-63 | `GemOnlineAcknowledgement.Other` |
| `EAC` | 0-3 | 4-63 | `GemEquipmentConstantAcknowledgement.Other` |
| `TIACK` | 0-1 | 2-63 | `GemTimeAcknowledgement.Other` |
| `DRACK` | 0-4 | 5-63 | `GemDefineReportAcknowledgement.Other` |
| `LRACK` | 0-5 | 6-63 | `GemLinkReportAcknowledgement.Other` |
| `ERACK` | 0-1 | 2-63 | `GemEnableEventAcknowledgement.Other` |
| `ACKC5` | 0 | 1-63 | `GemAlarmAcknowledgement.Error` |
| `ACKC6` | 0 | 1-63 | `GemReportAcknowledgement.Error` |
| `RSPACK` | 0 | 1-63 | `GemSpooledDataAcknowledgement.Denied` |
| `ACKC10` | 0-2 | 3-63 | `GemTerminalAcknowledgement.Other` |
| `PPGNT` | 0-5 | 6-63 | `GemProcessProgramGrant.Other` |
| `ACKC7` | 0-6 | 7-63 | `GemProcessProgramAcknowledgement.Other` |
| `HCACK` | 0-6 | 7-63 | `GemHostCommandAcknowledgement.Other` |
| `CMDA` | 0-2 | 3-63 | `GemRemoteCommandAcknowledgement.Other` |
| `GRANT6` | 0-2 | 3-63 | `GemMultiBlockGrant.Other` |

`GemHost` folds the whole reserved range onto that member and logs the arriving code, which is the
only place it survives. **Twelve of the first thirteen used to cast the code straight to its
enum**, so an equipment answering S1F18 with 40 produced an `ONLACK` value with no name and a
caller switching on it fell through every case. `HCACK` was fixed on its own two chapters earlier;
the rest were found at once by the catalogue's fourth provenance measure and are guarded now.

**The fourteenth was missed anyway**, which is the part worth recording. `CMDA` arrived with S2F21
in the coverage sweep, written with the item's three named codes and no member at 3 - the same hole,
in the chapter after the one that closed the twelve. Comparing the enum with the table did not
catch it, because the table was never consulted for a brand-new enum; what caught it was the
repository's rule that an item served from two packages must mean one thing, since
`Barbatos.SecsGem.ProcessJobs` reads `CMDA` too. That package had the hole as well, and both are
closed. The lesson is that an enum and its reader written in the same sitting get no second pair of
eyes, and the item's table is the only second pair there is.

`GRANT6` is the other shared one - `Barbatos.SecsGem.DataSets` reads it for S13F2 - and it is not
`GRANT`, which names four codes and reserves from 4. One code's difference, so a catch-all copied
across would read a machine's reserved 3 as a refusal it never made.

**Two acknowledgements are read without that fold, and deliberately.** `COMMACK` names both its
codes and reserves nothing. `TRACK` has no table of values at all - so
`GemTraceAcknowledgement`'s six members come from E30's prose rather than from the dictionary, and
there is no reserved range to fold.

Both halves are tested: one file compares each enum's boundary with its item's table, and another
makes a machine answer 40 and drives every reader that can meet one. The comparison alone would
pass a boundary that is right in the enum and unused in the reader.

---

## The host side

### `GemHost`

`public sealed class GemHost : IAsyncDisposable`

The other end of `GemEquipment`. One instance sits on one `HsmsConnection`; it does not run the
connection, and unlike the equipment it has no loop of its own - a host is an application with its
own control flow, and E30 gives the *equipment* the duty of retrying until communications are
established.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemHost(GemHostOptions options, HsmsConnection connection, TimeProvider? timeProvider = null, ILogger<GemHost>? logger = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | S1F1, S1F13, S2F25, S5F1, S5F9, S6F1, S6F3, S6F5, S6F11, S6F13, S10F1 and stream 9 functions 1 through 13 - everything a machine may send a host unprompted, plus the loopback, which travels either way. |
| `IsCommunicating` | `bool { get; }` | Set by an accepted S1F13 in either direction, cleared when the link leaves Selected. **Not** the same question as `HsmsConnection.IsSelected`: that is a socket, this is an agreement. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)` | Handles one inbound primary. Wired to every pair in `HandledMessages`. |
| `EventReportReceived` | `event EventHandler<GemEventReportEventArgs>?` | S6F11, S6F13 **and S6F3**, before the acknowledgement. `GemEventReportEventArgs.Function` says which, since an S6F3 and an S6F11 for an event with no linked reports both arrive carrying nothing. |
| `AlarmReceived` | `event EventHandler<GemHostAlarmEventArgs>?` | S5F1, before the acknowledgement. |
| `ExceptionReceived` | `event EventHandler<GemHostExceptionEventArgs>?` | S5F9, before the acknowledgement. Separate from `AlarmReceived` although both are stream 5 and both answer with `ACKC5`: an alarm is a condition with an identifier and a state a host can read back, and an exception post is an occurrence named by free text that nothing will mention again. Arrives already set to `Error` when the body did not read. |
| `MultiBlockInquired` | `event EventHandler<GemMultiBlockInquiryEventArgs>?` | S6F5, before the grant. **Grants by default**, so a host with no handler says yes - a handshake whose only purpose is to let a host refuse should not refuse by silence. |
| `TraceDataReceived` | `event EventHandler<GemTraceDataEventArgs>?` | S6F1. The samples are a view into the arriving message and die with the handler. |
| `TerminalRequestReceived` | `event EventHandler<GemTerminalRequestEventArgs>?` | S10F1 - something an operator typed. There is a person waiting, so a handler that cannot deal with it now should say so in the acknowledgement rather than accept and drop it. |
| `ErrorReported` | `event EventHandler<GemHostErrorEventArgs>?` | Stream 9. No reply exists to send. |
| `CommunicationStateChanged` | `event EventHandler<GemHostCommunicationStateChangedEventArgs>?` | |

**Requests.** Every primary `GemEquipment` answers has one, which a test asserts rather than this
table claiming it.

| Method | Sends | Returns |
| --- | --- | --- |
| `AreYouThereAsync` | S1F1 | `GemEquipmentIdentity?` - `null` for the zero-length list off-line equipment answers with |
| `EstablishCommunicationsAsync` | S1F13 | `GemCommunicationAcknowledgement`; sets `IsCommunicating` when accepted |
| `ReadStatusAsync(svids)` | S1F3 | `SecsMessage` - **the caller disposes it**. Empty list asks for every status variable |
| `ReadStatusNamelistAsync(svids)` | S1F11 | `IReadOnlyList<GemVariableDescription>` |
| `ReadDataVariableNamelistAsync(vids)` | S1F21 | `IReadOnlyList<GemVariableDescription>` - data variables only. A status variable asked for here comes back with `Exists` false, like an identifier the machine has not got, which is what lets a host read the two lists side by side |
| `ReadCollectionEventNamelistAsync(ceids)` | S1F23 | `IReadOnlyList<GemEventDescription>` - the name and the variables each event is about. The machine's own account, not the reports the host has linked |
| `RequestOfflineAsync` | S1F15 | `GemOfflineAcknowledgement` |
| `RequestOnlineAsync` | S1F17 | `GemOnlineAcknowledgement` |
| `ReadConstantsAsync(ecids)` | S2F13 | `SecsMessage` - **the caller disposes it** |
| `WriteConstantsAsync(values)` | S2F15 | `GemEquipmentConstantAcknowledgement`. The value items are placed in the request and disposed with it |
| `ReadClockAsync` | S2F17 | `DateTimeOffset?` in UTC; `null` when TIME was none of E5's three formats |
| `SetClockAsync(when)` | S2F31 | `GemTimeAcknowledgement`. `null` sends this host's current time |
| `ReadConstantNamelistAsync(ecids)` | S2F29 | `IReadOnlyList<GemConstantDescription>` |
| `DefineReportsAsync(reports)` | S2F33 | `GemDefineReportAcknowledgement` |
| `DeleteAllReportsAsync` | S2F33, empty | `GemDefineReportAcknowledgement`. Worth doing before defining a set, so a host inherits nothing from whoever had the link last |
| `LinkEventsAsync(links)` | S2F35 | `GemLinkReportAcknowledgement` |
| `EnableEventsAsync(enable, ceids)` | S2F37 | `GemEnableEventAcknowledgement`. Empty list means every event |
| `SendCommandAsync(command, parameters)` | S2F41 | `GemHostCommandAcknowledgement`. **`CompletionLater` (4) is a success** |
| `SendRemoteCommandAsync(command)` | S2F21 | `GemRemoteCommandAcknowledgement`. The same command table, the same control-state rule, and three codes instead of seven - so "will complete later" arrives as `CannotPerformNow`. A host that needs the distinction sends S2F41 with an empty parameter list |
| `LoopbackAsync(body)` | S2F25 | `bool` - whether the machine echoed exactly what it was sent. A loopback whose answer you have to compare yourself is a loopback half done |
| `LoopbackDataAsync(body)` | S2F25 | `ReadOnlyMemory<byte>` - the echoed bytes, for a caller that wants to look at them |
| `EnableAlarmAsync(enable, alarmId)` | S5F3 | `GemAlarmAcknowledgement`. Zero as the identifier means all of them. ALED is sent as E5 defines it: a `B[1]` whose top bit is the choice |
| `ListAlarmsAsync(alarmIds)` | S5F5 | `IReadOnlyList<GemAlarmReport>` - which alarms are *in alarm* |
| `ListEnabledAlarmsAsync` | S5F7 | `IReadOnlyList<GemAlarmReport>` - which the host has asked to hear about. A different question |
| `RequestEventReportAsync(ceid)` | S6F15 | `SecsMessage` - **the caller disposes it**. A zero-length body means no such event |
| `RequestReportAsync(rptid)` | S6F19 | `SecsMessage` - **the caller disposes it** |
| `RequestSpooledDataAsync(request)` | S6F23 | `GemSpooledDataAcknowledgement`. Accepted means the equipment agreed, not that the messages have arrived |
| `InquireProcessProgramAsync(id, length)` | S7F1 | `GemProcessProgramGrant` |
| `SendProcessProgramAsync(id, body)` | S7F3 | `GemProcessProgramAcknowledgement` |
| `RequestProcessProgramAsync(id)` | S7F5 | `ReadOnlyMemory<byte>?` - `null` is the zero-length refusal S7F6 uses, because it has no code for one |
| `DeleteProcessProgramsAsync(ids)` | S7F17 | `GemProcessProgramAcknowledgement`. Empty list means every program |
| `ListProcessProgramsAsync` | S7F19 | `IReadOnlyList<string>` |
| `SendFormattedProcessProgramAsync(program)` | S7F23 | `GemProcessProgramAcknowledgement` |
| `RequestFormattedProcessProgramAsync(id)` | S7F25 | `GemFormattedProcessProgram?` |
| `DefineVariableLimitsAsync(definitions)` | S2F45 | `GemVariableLimitReply`: VLAACK, and the answer per variable and per band. Throws `SecsException` when S2F46 was not the shape E5 defines |
| `DeleteAllVariableLimitsAsync` | S2F45, empty | `GemVariableLimitReply` |
| `ReadVariableLimitsAsync(variableIds)` | S2F47 | `IReadOnlyList<GemVariableLimitAttributes>`. Empty list asks about every variable that has limits |
| `StartTraceAsync(trace)` | S2F23 | `GemTraceAcknowledgement`. Throws `ArgumentOutOfRangeException` for a period the `hhmmsscc` field cannot carry, rather than spending a round trip on arithmetic the host could have done |
| `StopTraceAsync(traceId)` | S2F23 asking for no samples | `GemTraceAcknowledgement`. Accepted for a trace that was not running |
| `DisplayTerminalMessageAsync(text, terminalId)` | S10F3 | `GemTerminalAcknowledgement` |
| `DisplayTerminalMessageAsync(lines, terminalId)` | S10F5 | `GemTerminalAcknowledgement`. A one-entry list still sends the multi-block form |
| `BroadcastAsync(text)` | S10F9 | `GemTerminalAcknowledgement`. No TID: a broadcast is for every terminal |

A reply that does not carry the acknowledgement code E5 defines for it throws `SecsException`
rather than coming back as a refusal. Every one of these enums has zero meaning accepted, so a
missing body read as zero would turn a broken machine into a silent success.

`GemHost` covers the equipment side of this library and no more. A host that needs stream 3, 10 or
14 uses the connection directly - the helper is a layer over the link, not a wall in front of it.

### `GemHostOptions`

`public sealed class GemHostOptions` - bound from `Barbatos:GemHost`, validated on start by
`GemHostOptionsValidator`.

| Member | Type | Default | Notes |
| --- | --- | --- | --- |
| `SectionName` | `const string` | `Barbatos:GemHost` | |
| `ModelName` | `string` | `BRBTOS-HOST` | The **host's** MDLN, for S1F2 and S1F14. Capped at `GemOptions.MaxIdentityLength`. |
| `SoftwareRevision` | `string` | `0.1.0` | The host's SOFTREV. |
| `AcceptCommunications` | `bool` | `true` | Off answers an equipment-initiated S1F13 with COMMACK 1, which is how a host under maintenance tells a machine to keep retrying rather than time out against silence. It does not disable `EstablishCommunicationsAsync` - contradictory, and the host's contradiction to make. |

### `GemEquipmentIdentity`

`public sealed class GemEquipmentIdentity` - `ModelName`, `SoftwareRevision`, and a `ToString` of
both.

### `GemEventDescription`

`public sealed class GemEventDescription`

One collection event, as S1F24 describes it: `Id` (CEID), `Name` (CENAME), `VariableIds` (the VIDs
the event is about) and `Exists`, which is false for an identifier the machine does not have.

An event that exists with an empty `VariableIds` is **not** a refusal - S1F24 has no way to say
"I decline to tell you", and a machine may have an event that is simply an occurrence.

---

### `GemVariableDescription`

`public sealed class GemVariableDescription` - one S1F12 entry: `Id`, `Name`, `Units`, and
`Exists`, which is `Name.Length > 0`. E5 answers an unknown SVID with its name and units both
zero-length rather than by leaving it out, so an empty name is the equipment saying "not mine".

### `GemConstantDescription`

`public sealed class GemConstantDescription` - one S2F30 entry: `Id`, `Name`, `Units`, `Exists`,
and `Minimum` / `Maximum` / `Default` as `ReadOnlyMemory<byte>`.

The bounds are **encoded items** rather than values, for the same reason `GemSpooledMessage.Body`
is: an ECMIN may be any format E5 allows, and the reply it arrived in is disposed as soon as the
call returns. Decode one with `SecsItem.Decode` when you need it. Empty means the equipment
documented no bound - E5 says that with a zero-length item, and this says it without making a
caller decode two bytes to find out.

### `GemAlarmReport`

`public sealed class GemAlarmReport` - `Code` (ALCD), `Id`, `Text`, and `IsSet`, which is ALCD bit
7. The same message carries both the set and the clear, which is why that is a bit and not a
function.

### `GemReceivedReport`

`public sealed class GemReceivedReport` - `Id` (RPTID) and `Values`.

**`Values` is alive only while the handler runs.** It is a view into the message that arrived, and
that message is disposed as soon as the event returns. Read what you need, or copy it.

### Event arguments

| Type | Carries | Settable |
| --- | --- | --- |
| `GemEventReportEventArgs` | `DataId`, `EventId`, `Reports`, `Function` - 11, 13 or 3, since an S6F3 and an S6F11 with nothing linked both arrive carrying no reports | `Acknowledgement` (ACKC6) |
| `GemHostAlarmEventArgs` | `Alarm` | `Acknowledgement` (ACKC5) |
| `GemHostExceptionEventArgs` | `Exception` - a `GemExceptionPost` of `PostedAt` (null when TIMESTAMP would not parse), `Id`, `Type` and `Message` | `Acknowledgement` (ACKC5), arriving as `Error` when the body did not read |
| `GemMultiBlockInquiryEventArgs` | `DataId`, `Length` - DATALENGTH as the machine sent it, in whatever it and the host have agreed it counts; the reference records no units | `Grant` (GRANT6), **defaulting to `Ok`** |
| `GemTraceDataEventArgs` | `TraceId`, `SampleNumber` and `SampledAt` (both describing the **last** sample in the group), `Samples` - each a live view, valid only inside the handler | `Acknowledgement` (ACKC6) |
| `GemTerminalRequestEventArgs` | `TerminalId`, `Text` (not trimmed - TEXT is variable-length, so its spaces are somebody's) | `Acknowledgement` (ACKC10) |
| `GemHostErrorEventArgs` | `Function`, `OffendingHeader` (ten bytes, copied; empty for S9F13), `Description` (S9F13's MEXP and EDID, else empty) | - |
| `GemHostCommunicationStateChangedEventArgs` | `IsCommunicating`, `Reason` | - |

The acknowledgement is settable because whether a host could store what it was sent is the one
decision about an inbound report that cannot be made for it. The default is accepted - and for
`GemMultiBlockInquiryEventArgs.Grant` that default lives on the property rather than being written
by the handler, so the documented default is the one that runs.

---

## Acknowledgement codes

Every one of these is `: byte`, and in every one **zero means accepted**. A handler that tests for
truthiness rather than for zero has it exactly backwards.

| Enum | Message | Members |
| --- | --- | --- |
| `GemCommunicationAcknowledgement` | S1F14 COMMACK | `Accepted` 0, `Denied` 1 |
| `GemOfflineAcknowledgement` | S1F16 OFLACK | `Acknowledged` 0 |
| `GemOnlineAcknowledgement` | S1F18 ONLACK | `Accepted` 0, `Refused` 1, `AlreadyOnline` 2 |
| `GemEquipmentConstantAcknowledgement` | S2F16 EAC | `Accepted` 0, `UnknownConstant` 1, `Busy` 2, `OutOfRange` 3 |
| `GemTimeAcknowledgement` | S2F32 TIACK | `Accepted` 0, `Error` 1 |
| `GemDefineReportAcknowledgement` | S2F34 DRACK | `Accepted` 0, `InsufficientSpace` 1, `InvalidFormat` 2, `ReportAlreadyDefined` 3, `UnknownVariable` 4 |
| `GemLinkReportAcknowledgement` | S2F36 LRACK | `Accepted` 0, `InsufficientSpace` 1, `InvalidFormat` 2, `LinkAlreadyDefined` 3, `UnknownEvent` 4, `UnknownReport` 5 |
| `GemEnableEventAcknowledgement` | S2F38 ERACK | `Accepted` 0, `UnknownEvent` 1 |
| `GemHostCommandAcknowledgement` | S2F42 HCACK | `Accepted` 0, `InvalidCommand` 1, `CannotPerformNow` 2, `InvalidParameter` 3, `CompletionLater` 4, `AlreadyInDesiredCondition` 5, `NoSuchObject` 6, `Other` 7 - E5's reserved range 7 to 63, added once `Barbatos.SecsGem.Subsystems` served the same item for S18F2 |
| `GemRemoteCommandAcknowledgement` | S2F22 CMDA | `Performed` 0, `DoesNotExist` 1, `CannotPerformNow` 2, `Other` 3 - and the three are what a handler's seven `HCACK` codes narrow onto. Also `Barbatos.SecsGem.ProcessJobs`' item for S16F4 and S16F24, member for member |
| `GemMultiBlockGrant` | S6F6 GRANT6 | `Ok` 0, `Busy` 1, `NoSpace` 2, `Other` 3 - the same four names `Barbatos.SecsGem.DataSets` gives the same item. Not `GRANT`, which has five |
| `GemAlarmAcknowledgement` | ACKC5 | `Accepted` 0, `Error` 1 |
| `GemReportAcknowledgement` | ACKC6 | `Accepted` 0, `Error` 1 |

---

## Options

### `GemOptions`

`public sealed class GemOptions`

| Member | Signature | Default | Notes |
| --- | --- | --- | --- |
| `SectionName` | `const string` | `"Barbatos:Gem"` | |
| `MaxIdentityLength` | `const int` | `20` | E30's cap on MDLN and SOFTREV. |
| `MaxAlarmTextLength` | `const int` | `120` | E30's cap on ALTX. |
| `ModelName` | `string { get; set; }` | `"BRBTOS"` | MDLN. |
| `SoftwareRevision` | `string { get; set; }` | `"0.1.0"` | SOFTREV. |
| `CommunicationEnabled` | `bool { get; set; }` | `true` | `false` is E30's DISABLED state. |
| `EstablishCommunicationsRetryInterval` | `TimeSpan { get; set; }` | 30 s | E30's WAIT DELAY. Runs on `TimeProvider`. |
| `DefaultControlState` | `GemControlState { get; set; }` | `OnlineRemote` | Must be a state the equipment can choose. |
| `EventReportsExpectReply` | `bool { get; set; }` | `true` | Clearing it makes reporting fire-and-forget. |
| `AlarmReportsExpectReply` | `bool { get; set; }` | `true` | |
| `ExceptionPostsExpectReply` | `bool { get; set; }` | `true` | S5F9's W bit, separate from the alarm one although both messages answer with `ACKC5`. An alarm is a condition S5F6 will serve again, so a lost S5F1 can be recovered from; nothing in the documented half of stream 5 will repeat an exception post, so a machine may reasonably insist on an acknowledgement for the one and not the other. |
| `AcceptHostClockSet` | `bool { get; set; }` | `true` | Moves an internal offset, never the operating system clock. |
| `ResetLinkStateOnDisconnect` | `bool { get; set; }` | `true` | Report definitions do not survive a reconnection. |
| `SpoolingEnabled` | `bool { get; set; }` | `true` | The configuration override, not the switch - registering an `IGemSpool` is what turns spooling on. |
| `SpoolCapacity` | `int { get; set; }` | `1000` | Sizes either spool that ships here. Must be at least 1 when spooling is enabled. |
| `SpoolOverflow` | `GemSpoolOverflowPolicy { get; set; }` | `DiscardNewest` | |
| `MaxTraces` | `int { get; set; }` | `4` | How many traces may run at once; what TIAACK 2 reports. Must be at least 1. |
| `MaxTraceVariables` | `int { get; set; }` | `64` | How many variables one trace may sample; what TIAACK 1 reports. Must be at least 1. |
| `SpoolDirectory` | `string? { get; set; }` | `null` | Where `AddGemFileSpooling` keeps its records when the call gives no directory of its own, so the path can come from configuration. Set but blank fails validation: that is a typo, where unset is a deployment with no durable spool. Ignored by the in-memory spool. |
| `MaxSpoolTransmit` | `int { get; set; }` | `0`, meaning all of them | E30's MaxSpoolTransmit: how many messages one S6F23 transmit sends. |

### `GemOptionsValidator`

`public sealed class GemOptionsValidator : IValidateOptions<GemOptions>`

Registered by `AddGemEquipment` with `ValidateOnStart`. Refuses an empty or over-long `ModelName`,
an over-long `SoftwareRevision`, a non-positive retry interval, and a `DefaultControlState` of
`AttemptOnline` or `HostOffline` - the first is transient and the second is the host's decision to
make.

---

## Hosting

Namespace `Microsoft.Extensions.DependencyInjection`.

### `AddGemEquipment`

```csharp
public static ISecsGemBuilder AddGemEquipment(
    this ISecsGemBuilder builder,
    Action<GemEquipmentModelBuilder> configureModel,
    IConfiguration? configuration = null,
    Action<GemOptions>? configure = null,
    bool runAsHostedService = true)

public static ISecsGemBuilder AddGemEquipment(
    this ISecsGemBuilder builder,
    Action<GemEquipmentModelBuilder, IServiceProvider> configureModel,
    IConfiguration? configuration = null,
    Action<GemOptions>? configure = null,
    bool runAsHostedService = true)
```

The second overload is the one real equipment wants: a status variable reads the machine, and the
machine is a service.

| Service | Lifetime | Notes |
| --- | --- | --- |
| `GemOptions` | Options | Bound from `Barbatos:Gem`, validated on start. |
| `GemEquipmentModel` | Singleton | Built from `configureModel`. |
| `GemEquipment` | Singleton | |
| Hosted service | Singleton | Runs `GemEquipment.RunAsync`, unless `runAsHostedService` is `false`. |

It also registers a handler for every pair in `GemEquipment.HandledMessages`, through the builder's
`DeclareOnce` - so calling it twice registers them once, where it used to fail at start-up.

### `AddGemProcessPrograms`

```csharp
public static ISecsGemBuilder AddGemProcessPrograms(this ISecsGemBuilder builder)
public static ISecsGemBuilder AddGemProcessPrograms<TStore>(this ISecsGemBuilder builder)
    where TStore : class, IGemProcessProgramStore
public static ISecsGemBuilder AddGemProcessPrograms(this ISecsGemBuilder builder, IGemProcessProgramStore store)
```

Registers the store and, with it, the five pairs in `GemEquipment.ProcessProgramMessages` - S7F1,
S7F3 and S7F5, plus the S7F17 and S7F19 both halves of stream 7 share. The parameterless overload
uses `GemInMemoryProcessProgramStore`.

Omit it and the connection reports S9F3 for anything in stream 7, unless
`AddGemFormattedProcessPrograms` declared the other half. Calling it twice is harmless: the pairs go in
through the builder's `DeclareOnce`, which declares a set once however many times it is asked for.

Order does not matter relative to `AddGemEquipment`. The equipment resolves its store with
`GetService`, so it is `null` when nothing registered one.

### `AddGemFormattedProcessPrograms`

```csharp
public static ISecsGemBuilder AddGemFormattedProcessPrograms(this ISecsGemBuilder builder)
public static ISecsGemBuilder AddGemFormattedProcessPrograms<TStore>(this ISecsGemBuilder builder)
    where TStore : class, IGemFormattedProcessProgramStore
public static ISecsGemBuilder AddGemFormattedProcessPrograms(this ISecsGemBuilder builder, IGemFormattedProcessProgramStore store)
```

Registers the formatted store and, with it, S7F23 and S7F25. The parameterless overload uses
`GemInMemoryFormattedProcessProgramStore`.

Independent of `AddGemProcessPrograms` - equipment may call either, both, or neither, and calling
both is safe because the two message lists are disjoint. **S7F17 and S7F19 come with the
unformatted half**: a stream and function may be declared only once, so equipment that wants the
delete and directory messages registers that half as well, and the handlers then cover both stores.

**Two loops, two hosted services.** The connection still needs its own `RunAsHostedService()`. The
GEM loop is registered here by default because it is what establishes communications: equipment
whose loop never ran would sit in `WaitingToRetry` for ever while looking perfectly configured.

**On the cycle.** The handlers depend on the equipment, the equipment depends on the connection,
and the connection depends on the router the handlers are registered into. The registration breaks
it by resolving the equipment **inside the handler** rather than inside the factory - see
[`SecsMessageRouterBuilder.OnPrimary`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secsmessagerouterbuilder),
which is the overload that makes it possible and the place the trap is documented.

### `AddGemHost`

```csharp
public static ISecsGemBuilder AddGemHost(
    this ISecsGemBuilder builder,
    IConfiguration? configuration = null,
    Action<GemHostOptions>? configure = null)
```

Registers `GemHost`, binds `Barbatos:GemHost` when given a configuration, validates it on start,
and declares every pair in `GemHost.HandledMessages`. Everything else a host does, it initiates -
so there is nothing more to declare and no hosted service to run.

A host connection is normally the active end, `link.Mode = HsmsConnectionMode.Active`, which is the
opposite of the equipment's. Nothing here enforces it: E37 allows either end to be either, and a
fab occasionally needs the unusual one.

**Do not call this and `AddGemEquipment` on the same builder.** Both declare S1F1 and S1F13, which
the routing table refuses - and one process answering as both ends of the same link is not a thing
E30 describes. Two connections in one process, as the host sample does, means two builders.
