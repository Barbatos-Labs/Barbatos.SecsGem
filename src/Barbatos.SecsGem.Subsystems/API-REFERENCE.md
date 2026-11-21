# Barbatos.SecsGem.Subsystems - API reference

Namespace `Barbatos.SecsGem.Subsystems`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`SubsystemEquipment`](#subsystemequipment)
  - [`SubsystemHost`](#subsystemhost)
- [The store](#the-store)
  - [`ISubsystemStore`](#isubsystemstore)
  - [`SubsystemInMemoryStore`](#subsysteminmemorystore)
- [The identifier](#the-identifier)
  - [`SubsystemId`](#subsystemid)
- [Bodies](#bodies)
  - [`Subsystem`](#subsystem)
  - [`SubsystemCommand`](#subsystemcommand)
  - [`SubsystemCommandResult`](#subsystemcommandresult)
  - [`SubsystemParameter`](#subsystemparameter)
  - [`SubsystemParameterFault`](#subsystemparameterfault)
  - [`SubsystemVariable`](#subsystemvariable)
  - [`SubsystemEvent`](#subsystemevent)
  - [`SubsystemAlarm`](#subsystemalarm)
  - [`SubsystemCodeCondition`](#subsystemcodecondition)
- [Codes](#codes)
  - [`SubsystemAcknowledgement`](#subsystemacknowledgement)
  - [`SubsystemCommandAcknowledgement`](#subsystemcommandacknowledgement)
  - [`SubsystemParameterAcknowledgement`](#subsystemparameteracknowledgement)
  - [`SubsystemAlarmCategory`](#subsystemalarmcategory)
- [Events](#events)
- [Registration](#registration)
- [What is not here](#what-is-not-here)

## The two ends

Both send primaries, and the two sets are disjoint - six at the equipment, two at the host.

### `SubsystemEquipment`

`sealed class SubsystemEquipment`

| Member | Notes |
| --- | --- |
| `SubsystemEquipment(ISubsystemStore store, HsmsConnection connection, ILogger<SubsystemEquipment>? logger = null)` | Throws `ArgumentNullException` for a null store or connection. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S18F1, F3, F5, F7, F9 and F15. |
| `ValueTask<SubsystemAcknowledgement> ReportEventAsync(SubsystemEvent report, CancellationToken = default)` | S18F11. How a command answered `CompletionLater` says it has finished. |
| `ValueTask<SubsystemAcknowledgement> ReportAlarmAsync(SubsystemAlarm alarm, CancellationToken = default)` | S18F13, for an alarm set **or** cleared. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the six. The store is asked before the reply bit is looked at. |

### `SubsystemHost`

`sealed class SubsystemHost`

| Member | Notes |
| --- | --- |
| `SubsystemHost(HsmsConnection connection, ILogger<SubsystemHost>? logger = null)` | Throws `ArgumentNullException` for a null connection. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S18F11 and F13. |
| `SubsystemAcknowledgement ReportAcknowledgement { get; set; }` | What this end answers a report with. `Accepted` by default - the only code in `SSACK`'s table that means anything here. |
| `ValueTask<SubsystemCommandResult> CommandAsync(SubsystemCommand command, CancellationToken = default)` | S18F1. `SecsException` when the reply carries no `HCACK`. |
| `ValueTask<string?> StatusAsync(SubsystemId id, CancellationToken = default)` | S18F3. `null` when there is no such subsystem - S18F4 has no code to say it with. |
| `ValueTask<string?> ReadAsync(SubsystemId id, uint variableId, CancellationToken = default)` | S18F5. `null` for an unknown variable, which **cannot** be refused where a write can. |
| `ValueTask<SubsystemAcknowledgement> WriteAsync(SubsystemVariable variable, CancellationToken = default)` | S18F7. The one transaction `SSACK`'s table was written for. |
| `ValueTask<IReadOnlyList<Subsystem>> ListAsync(CancellationToken = default)` | S18F9. An unreadable reply comes back empty and logged. |
| `ValueTask<SubsystemCodeCondition> ReadCodeAsync(string targetId, CancellationToken = default)` | S18F15. `SecsException` when the reply is not a code condition. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Raises the report, then acknowledges. A report it could not read is **still** acknowledged. |

A reply that answers for another subsystem, variable or target is **logged and still returned** -
the same choice `Barbatos.SecsGem.Objects` makes about an S2F64 echoing the wrong `DATAID`.

## The store

### `ISubsystemStore`

`interface ISubsystemStore`

| Member | Message |
| --- | --- |
| `ValueTask<SubsystemCommandResult> CommandAsync(SubsystemCommand command, CancellationToken)` | S18F1 |
| `ValueTask<string?> StatusAsync(SubsystemId id, CancellationToken)` | S18F3 - `null` goes out as an empty `SSSTATE` |
| `ValueTask<string?> ReadAsync(SubsystemId id, uint variableId, CancellationToken)` | S18F5 - `null` goes out as an empty `SV` |
| `ValueTask<SubsystemAcknowledgement> WriteAsync(SubsystemVariable variable, CancellationToken)` | S18F7 |
| `ValueTask<IReadOnlyList<Subsystem>> ListAsync(CancellationToken)` | S18F9 |
| `ValueTask<SubsystemCodeCondition?> ReadCodeAsync(string targetId, CancellationToken)` | S18F15 - `null` goes out as `SSACK` `Other` |

Three of the six have nowhere to refuse, because S18F4, S18F6 and S18F10 carry no acknowledgement
code at all.

### `SubsystemInMemoryStore`

`sealed class SubsystemInMemoryStore : ISubsystemStore`

| Member | Notes |
| --- | --- |
| `SubsystemCommandAcknowledgement UnhandledCommand { get; set; }` | `InvalidCommand` by default - not `Accepted`, which would claim a machine did something nobody implemented. |
| `Func<SubsystemCommand, SubsystemCommandResult>? AnswerCommand { get; set; }` | What answers a command. Null answers every one with `UnhandledCommand`. |
| `int Count { get; }` | Subsystems declared. |
| `void Add(SubsystemId id, string name = "", string state = "")` | Declares one. Stream 18 has no message that creates a subsystem. |
| `Subsystem? Find(SubsystemId id)` | What is declared. |
| `bool SetState(SubsystemId id, string state)` | Changes a state. Raises nothing - an event report is a decision. |
| `bool Set(SubsystemId id, uint variableId, string value, bool isReadOnly = false)` | Sets a variable without a transaction. |
| `void SetCodeCondition(SubsystemCodeCondition condition)` | Declares what a 2D code read of a target finds. |

A command to a subsystem it does not have is answered `NoSuchSubsystem`; a write to an unknown
variable `UnknownVariable`; a write to a read-only one `ReadOnlyVariable`. The listing comes back
numbered identifiers first, then named ones, so a host can diff two listings.

## The identifier

### `SubsystemId`

`readonly struct SubsystemId : IEquatable<SubsystemId>`

`SSID`, which the dictionary does not define - so this type carries either reading rather than
choosing one. See the README for the measurement that failed to settle it.

| Member | Notes |
| --- | --- |
| `static SubsystemId Named(string? name)` | Travels as an `A`. Null becomes empty. |
| `static SubsystemId Numbered(uint number)` | Travels as a `U4`. |
| `bool IsNumber { get; }` | Which reading. **False for the default value**, which is the empty name and not the number zero. |
| `uint Number { get; }` | The number, or zero for a name. |
| `string Name { get; }` | The name, or the number in decimal. Always usable; **not** an identity. |
| `bool Equals(SubsystemId)`, `==`, `!=`, `GetHashCode()` | Strict on kind **and** value: `Numbered(7) != Named("7")`. |
| `override string ToString()` | `#7` for a number, the name itself for a name. |

Reading off the wire goes **by format and never by content**: an `A` carrying "7" is the name "7".

## Bodies

### `Subsystem`

One subsystem, as S18F10 lists it.

| Member | Item |
| --- | --- |
| `required SubsystemId Id` | `SSID` |
| `string Name` | `SSNAME` - undefined in the dictionary, written `A` |
| `string State` | `SSSTATE` - undefined, written `A`; see the README |

### `SubsystemCommand`

| Member | Item |
| --- | --- |
| `required SubsystemId Subsystem` | `SSID` |
| `required string Command` | `RCMD` |
| `IReadOnlyList<SubsystemParameter> Parameters` | the `CPNAME` / `CPVAL` pairs |

### `SubsystemCommandResult`

| Member | Item |
| --- | --- |
| `required SubsystemId Subsystem` | `SSID` |
| `required SubsystemCommandAcknowledgement Acknowledgement` | `HCACK` |
| `IReadOnlyList<SubsystemParameterFault> Faults` | the `CPNAME` / `CPACK` pairs |
| `bool IsAccepted` | **two codes are successes**: `Accepted` and `CompletionLater` |

### `SubsystemParameter`

`readonly record struct SubsystemParameter(string Name, string Value)` - `CPNAME` and `CPVAL`, both
`A` in the dictionary.

### `SubsystemParameterFault`

`readonly record struct SubsystemParameterFault(string Name, SubsystemParameterAcknowledgement Acknowledgement)`
- only the parameters that were **wrong** appear.

### `SubsystemVariable`

| Member | Item |
| --- | --- |
| `required SubsystemId Subsystem` | `SSID` |
| `required uint VariableId` | `VID`, a `U4` |
| `string Value` | `SV`, an `A` here - narrower than E30, so a number arrives as its digits |

### `SubsystemEvent`

| Member | Item |
| --- | --- |
| `required SubsystemId Subsystem` | `SSID` |
| `required uint EventId` | `CEID`, a `U4` |
| `string Time` | `TIME`, an `A[16]`, carried as text and not parsed |

Carries **no report data**: GEM's S6F11 has the linked reports and stream 18's event stops at the
timestamp.

### `SubsystemAlarm`

| Member | Item |
| --- | --- |
| `required SubsystemId Subsystem` | `SSID` |
| `required uint AlarmId` | `ALID`, a `U4` |
| `required bool IsSet` | the top bit of `ALCD` - **false is the same message reporting a clear** |
| `SubsystemAlarmCategory Category` | the low seven bits of `ALCD` |
| `string Text` | `ALTX`, an `A[120]`; written truncated rather than refused |

### `SubsystemCodeCondition`

The one pair in stream 18 that is not GEM's message set re-addressed, and the only pair whose every
item name the dictionary defines.

| Member | Item |
| --- | --- |
| `required string TargetId` | `TARGETID`, an `A` |
| `required SubsystemAcknowledgement Acknowledgement` | `SSACK` |
| `string MaterialId` | `MID`, an `A` |
| `byte Status` | `STATUS`, a `U1` with **no table of values**, so a bare number |
| `string Condition` | `CONDITION`, an `A` |
| `bool IsRead` | `Acknowledgement == Accepted` |

## Codes

### `SubsystemAcknowledgement`

`SSACK`, quoted. One item, four replies, and a table written for one of them.

| Member | Value | Notes |
| --- | --- | --- |
| `Accepted` | 0 | |
| `UnknownVariable` | 1 | a write only |
| `ValueOutOfRange` | 2 | a write only |
| `ReadOnlyVariable` | 3 | a write only |
| `Other` | 4 | the table's own reserved range, 4 to 63 |

### `SubsystemCommandAcknowledgement`

`HCACK` - the same dictionary entry `GemHostCommandAcknowledgement` quotes, and a test asserts the
two agree member for member.

| Member | Value |
| --- | --- |
| `Accepted` | 0 |
| `InvalidCommand` | 1 |
| `CannotPerformNow` | 2 |
| `InvalidParameter` | 3 |
| `CompletionLater` | 4 - **a success** |
| `AlreadyInDesiredCondition` | 5 |
| `NoSuchSubsystem` | 6 |
| `Other` | 7 - the table's reserved range, 7 to 63 |

### `SubsystemParameterAcknowledgement`

`CPACK`. **No success member**, because the table starts at 1 and the list carries what was wrong.

| Member | Value |
| --- | --- |
| `UnknownName` | 1 |
| `IllegalValue` | 2 |
| `IllegalFormat` | 3 |
| `Other` | 4 - the reserved range, and a zero reads as this one and is logged |

### `SubsystemAlarmCategory`

The low bits of `ALCD`, quoted: `None` 0, `PersonalSafety` 1, `EquipmentSafety` 2,
`ParameterControlWarning` 3, `ParameterControlError` 4, `IrrecoverableError` 5,
`EquipmentStatusWarning` 6, `AttentionFlags` 7, `DataIntegrity` 8, `Other` 9 for the 9-to-127 range
the table leaves to the equipment.

## Events

| Event | Raised on |
| --- | --- |
| `SubsystemHost.EventReported` → `SubsystemEventArgs.Report` | S18F11 |
| `SubsystemHost.AlarmReported` → `SubsystemAlarmEventArgs.Alarm` | S18F13, set **or** cleared |

Raised before the acknowledgement goes out, so a handler that throws leaves the machine on its T3.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`.

| Member | Notes |
| --- | --- |
| `ISecsGemBuilder AddSubsystemEquipment(this ISecsGemBuilder)` | The equipment side with `SubsystemInMemoryStore`. |
| `ISecsGemBuilder AddSubsystemEquipment<TStore>(this ISecsGemBuilder)` | With a store of your own. `TryAdd`, so one already in the container wins. Declares the six. |
| `ISecsGemBuilder AddSubsystemHost(this ISecsGemBuilder)` | The host side. Declares the two. |

Either end, never both on one builder - and the routing table will **not** catch it, because the two
sets are disjoint.

## What is not here

- Nothing of stream 18. All sixteen rows are implemented at both ends.
- A bridge to SEMI E39, whose enhanced stream 2 covers six of these eight pairs by another route.
  Serve one or the other; the README says which is on firmer ground and why.
- A durable store. `ISubsystemStore` is the mechanism, and there is no snapshot pair on purpose.
