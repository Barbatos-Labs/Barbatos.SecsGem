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
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)> { get; }` | Every pair this object answers. Registering it is how the equipment declares its capability - see below. |
| `RunAsync` | `Task RunAsync(CancellationToken cancellationToken = default)` | Drives the state models. Run it **alongside** `HsmsConnection.RunAsync`, not instead of it. Throws `InvalidOperationException` on a second call. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)` | Handles one inbound primary. Wired to every pair in `HandledMessages`. |
| `Model` | `GemEquipmentModel { get; }` | |
| `CommunicationState` | `GemCommunicationState { get; }` | |
| `ControlState` | `GemControlState { get; }` | |
| `IsCommunicating` | `bool { get; }` | Whether application messages may flow. **Not** the same as `HsmsConnection.IsSelected`. |
| `IsOnlineRemote` | `bool { get; }` | Whether the host may send remote commands. |
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
| S1F13 | Establish Communications Request | S1F14 (COMMACK) |
| S1F15 | Request Off-Line | S1F16 (OFLACK) |
| S1F17 | Request On-Line | S1F18 (ONLACK) |
| S2F13 | Equipment Constant Request | S2F14 |
| S2F15 | New Equipment Constant Send | S2F16 (EAC) |
| S2F17 | Date and Time Request | S2F18 |
| S2F31 | Date and Time Set Request | S2F32 (TIACK) |
| S2F33 | Define Report | S2F34 (DRACK) |
| S2F35 | Link Event Report | S2F36 (LRACK) |
| S2F37 | Enable/Disable Event Report | S2F38 (ERACK) |
| S2F41 | Host Command Send | S2F42 (HCACK) |
| S5F3 | Enable/Disable Alarm Send | S5F4 (ACKC5) |

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
| `IsWritable` | `bool { get; }` | |

Range checking belongs inside `Write`: only the equipment knows whether a value is reachable, and a
model holding a minimum and a maximum would be describing a machine it cannot see.

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

**Two loops, two hosted services.** The connection still needs its own `RunAsHostedService()`. The
GEM loop is registered here by default because it is what establishes communications: equipment
whose loop never ran would sit in `WaitingToRetry` for ever while looking perfectly configured.

**On the cycle.** The handlers depend on the equipment, the equipment depends on the connection,
and the connection depends on the router the handlers are registered into. The registration breaks
it by resolving the equipment **inside the handler** rather than inside the factory - see
[`SecsMessageRouterBuilder.OnPrimary`](../Barbatos.SecsGem.Hsms/API-REFERENCE.md#secsmessagerouterbuilder),
which is the overload that makes it possible and the place the trap is documented.
