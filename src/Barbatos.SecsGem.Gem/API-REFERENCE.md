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
- [Acknowledgement codes](#acknowledgement-codes)
- [Options](#options)
  - [`GemOptions`](#gemoptions)
  - [`GemOptionsValidator`](#gemoptionsvalidator)
- [Hosting](#hosting)
  - [`AddGemEquipment`](#addgemequipment)

---

## The equipment

### `GemEquipment`

`public sealed class GemEquipment : IAsyncDisposable`

Both state models, the standard message set, and the reporting that hangs off them. One instance
sits on one `HsmsConnection`; it does not run the connection, it uses it.

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `GemEquipment(GemOptions options, GemEquipmentModel model, HsmsConnection connection, TimeProvider? timeProvider = null, ILogger<GemEquipment>? logger = null)` | `null` provider means `TimeProvider.System`; `null` logger discards diagnostics. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | The twenty core pairs. Registering them is how the equipment declares its capability - see below. |
| `ProcessProgramMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | The five stream 7 pairs, registered only when a store is configured. Kept separate so equipment with no recipes answers S9F3 for stream 7 rather than refusing each message in turn. |
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
| S2F13 | Equipment Constant Request | S2F14 |
| S2F15 | New Equipment Constant Send | S2F16 (EAC) |
| S2F17 | Date and Time Request | S2F18 |
| S2F29 | Equipment Constant Namelist Request | S2F30 - ECID, ECNAME, ECMIN, ECMAX, ECDEF, UNITS |
| S2F31 | Date and Time Set Request | S2F32 (TIACK) |
| S2F33 | Define Report | S2F34 (DRACK) |
| S2F35 | Link Event Report | S2F36 (LRACK) |
| S2F37 | Enable/Disable Event Report | S2F38 (ERACK) |
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
| S5F1 | `SetAlarmAsync` / `ClearAlarmAsync`, when the alarm is enabled | S5F2, when `AlarmReportsExpectReply` |

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
| `EnabledByDefault` | `bool { get; init; }` - defaults to `true` |

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

Opt-in. Without `AddGemSpooling`, S6F23 is not in the routing table and the connection answers S9F5
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

## Process programs

Everything here is opt-in. Without `AddGemProcessPrograms`, stream 7 is not in the routing table
and the connection answers S9F3 for it.

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

ACKC7 has no code for "that body made no sense", so a PPBODY that did not arrive as a binary item
is answered `LengthError` rather than stored as an empty program.

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
| `GemHostCommandAcknowledgement` | S2F42 HCACK | `Accepted` 0, `InvalidCommand` 1, `CannotPerformNow` 2, `InvalidParameter` 3, `CompletionLater` 4, `AlreadyInDesiredCondition` 5, `NoSuchObject` 6 |
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
| `AcceptHostClockSet` | `bool { get; set; }` | `true` | Moves an internal offset, never the operating system clock. |
| `ResetLinkStateOnDisconnect` | `bool { get; set; }` | `true` | Report definitions do not survive a reconnection. |
| `SpoolingEnabled` | `bool { get; set; }` | `true` | The configuration override, not the switch - registering an `IGemSpool` is what turns spooling on. |
| `SpoolCapacity` | `int { get; set; }` | `1000` | Sizes the default in-memory spool. Must be at least 1 when spooling is enabled. |
| `SpoolOverflow` | `GemSpoolOverflowPolicy { get; set; }` | `DiscardNewest` | |
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

It also registers a handler for every pair in `GemEquipment.HandledMessages`.

### `AddGemProcessPrograms`

```csharp
public static ISecsGemBuilder AddGemProcessPrograms(this ISecsGemBuilder builder)
public static ISecsGemBuilder AddGemProcessPrograms<TStore>(this ISecsGemBuilder builder)
    where TStore : class, IGemProcessProgramStore
public static ISecsGemBuilder AddGemProcessPrograms(this ISecsGemBuilder builder, IGemProcessProgramStore store)
```

Registers the store and, with it, the five stream 7 pairs. The parameterless overload uses
`GemInMemoryProcessProgramStore`.

This is what declares stream 7 - omit it and the connection reports S9F3 for anything in that
stream. Call it once: a second call registers the same pairs twice, which the routing table refuses.

Order does not matter relative to `AddGemEquipment`. The equipment resolves its store with
`GetService`, so it is `null` when nothing registered one.

**Two loops, two hosted services.** The connection still needs its own `RunAsHostedService()`. The
GEM loop is registered here by default because it is what establishes communications: equipment
whose loop never ran would sit in `WaitingToRetry` for ever while looking perfectly configured.

**On the cycle.** The handlers depend on the equipment, the equipment depends on the connection,
and the connection depends on the router the handlers are registered into. The registration breaks
it by resolving the equipment **inside the handler** rather than inside the factory - see
[`SecsMessageRouterBuilder.OnPrimary`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secsmessagerouterbuilder),
which is the overload that makes it possible and the place the trap is documented.
