# Barbatos.SecsGem.Objects - API reference

Namespace `Barbatos.SecsGem.Objects`, except where noted. Concepts, worked examples and the
provenance note live in [`README.md`](README.md); this page is member-by-member.

- [`ObjectServices`](#objectservices)
- [The objects](#the-objects)
  - [`IObjectProvider`](#iobjectprovider)
  - [`ObjectInMemoryProvider`](#objectinmemoryprovider)
  - [`CompositeObjectProvider`](#compositeobjectprovider)
- [The model](#the-model)
  - [`ObjectAttribute`](#objectattribute)
  - [`ObjectAttributeRequest`](#objectattributerequest)
  - [`ObjectAttributeWrite`](#objectattributewrite)
  - [`ObjectAttributeValues`](#objectattributevalues)
  - [`ObjectSetResult`](#objectsetresult)
  - [`ObjectParameter`](#objectparameter)
  - [`ObjectActionRequest`](#objectactionrequest)
  - [`ObjectActionResult`](#objectactionresult)
  - [`ObjectCreateRequest`](#objectcreaterequest)
  - [`ObjectCreateResult`](#objectcreateresult)
  - [`ObjectDeleteRequest`](#objectdeleterequest)
  - [`ObjectAcknowledgement`](#objectacknowledgement)
- [The enhanced half of stream 2](#the-enhanced-half-of-stream-2)
  - [`EnhancedServicesHost`](#enhancedserviceshost)
  - [`EnhancedServicesEquipment`](#enhancedservicesequipment)
  - [`IEnhancedServiceProvider`](#ienhancedserviceprovider)
  - [`EnhancedInMemoryProvider`](#enhancedinmemoryprovider)
  - [The enhanced model](#the-enhanced-model)
- [Registration](#registration)

## `ObjectServices`

`sealed class`. Asks and answers; one class because E5 gives stream 14 no sides. Not disposable -
no state, no timer, nothing to release.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ObjectServices(HsmsConnection connection, IObjectProvider? provider = null, ILogger<ObjectServices>? logger = null)` | A `null` provider makes an end that only asks. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | Eleven primaries: S14F1, F3, F5, F7, F9, F13, F15, F17, F19, F21, F27. Declare them only on an end that has a provider. |
| `CanAnswer` | `bool { get; }` | Whether a provider was given. |
| `Provider` | `IObjectProvider? { get; }` | The provider this end answers out of, so a composition is observable. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Answers out of the provider. With no provider it logs and replies nothing. |
| `GetObjectTypesAsync` | `ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken = default)` | S14F13. |
| `GetInstancesAsync` | `ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken = default)` | S14F15. |
| `GetAttributeNamesAsync` | `ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(string objectSpecification, string objectType, CancellationToken = default)` | S14F17. An empty specification asks about the type. |
| `GetAttributesAsync` | `ValueTask<bool> GetAttributesAsync(string objectSpecification, string objectType, IReadOnlyList<string> attributes, Action<ObjectAttributeValues> read, CancellationToken = default)` | S14F1. `false` means the other end does not have that object - and that is all it can mean, because S14F2 carries no acknowledgement. |
| `GetAttributeAsync` | `ValueTask<ObjectAttributeError?> GetAttributeAsync(string objectType, string objectId, IReadOnlyList<string> attributes, Action<ObjectAttributeValues> read, CancellationToken = default)` | S1F19, **stream 1's one E39 row**. `null` means no error. Not a duplicate of S14F1: its reply carries an `ERRCODE` and an `ERRTEXT`, which makes it the only attribute get in the reference that can say *why* - where S14F2 can only send an empty list. Names its object with an `OBJID` where S14F1 uses an `OBJSPEC`, and this package does not reconcile them. |
| `FindObjectsAsync` | `ValueTask<IReadOnlyList<string>> FindObjectsAsync(string objectType, string attribute, ObjectAttributeRelation relation, SecsItem? value = null, CancellationToken = default)` | S14F11 - a filtered instance namelist. `ATTRRELN` arrives with its eight relations tabled, which is what let the row be served; how two `ATTRDATA` items stand in one is this library's decision and the package README tabulates it. The value is ignored for `Present` and `Absent`, and belongs to the caller. **S14F12 has no acknowledgement**, so a query this end could not read is the same empty list as one that matched nothing - the machine logs which. |
| `GetObjectErrorAsync` | `ValueTask<ObjectAttributeError?> GetObjectErrorAsync(string objectSpecification, CancellationToken = default)` | S14F25. `null` means nothing wrong, which is also what a provider keeping no errors reports. An object the other end does not have at all comes back as an error, because a host needs to tell that from silence. The same `ERRCODE`/`ERRTEXT` pair S1F20 carries, so the same type - one item means one thing. |
| `GetAttributesAsync` | `ValueTask<int> GetAttributesAsync(IReadOnlyList<string> objectSpecifications, IReadOnlyList<string> attributes, Action<ObjectAttributeValues> read, CancellationToken = default)` | S14F19. Returns how many objects came back. |
| `SetAttributesAsync` | `ValueTask<ObjectAcknowledgement> SetAttributesAsync(string objectSpecification, string objectType, IReadOnlyList<ObjectAttribute> attributes, CancellationToken = default)` | S14F3. |
| `SetAttributesAsync` | `ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(IReadOnlyList<string> objectSpecifications, IReadOnlyList<ObjectAttribute> attributes, CancellationToken = default)` | S14F21. One result per object. |
| `ActAsync` | `ValueTask<ObjectAcknowledgement> ActAsync(string objectSpecification, string objectType, string command, IReadOnlyList<ObjectParameter>? parameters = null, CancellationToken = default)` | S14F9. |
| `ActAsync` | `ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(IReadOnlyList<string> objectSpecifications, string command, IReadOnlyList<ObjectParameter>? parameters = null, CancellationToken = default)` | S14F27, which carries no `OBJTYPE`. One result per object; an object the other end did not answer for reads as `InvalidObjectInstance`. |
| `CreateAsync` | `ValueTask<ObjectCreateResult> CreateAsync(string objectType, IReadOnlyList<ObjectAttribute>? attributes = null, CancellationToken = default)` | S14F5. The equipment names the object; S14F6 carries the name back. |
| `DeleteAsync` | `ValueTask<ObjectAcknowledgement> DeleteAsync(string objectSpecification, string objectType = "", CancellationToken = default)` | S14F7. |

**The callback on a get** is not a style choice. An attribute value is a view into the reply, and
the reply is disposed when the method returns, so values cannot be handed back - everything a
caller keeps, it copies inside the callback.

**A get cannot fail.** S14F2 and S14F20 carry attributes and no acknowledgement code, so an object
that could not be read is absent from the answer. A reply that is not attribute data at all is a
`SecsException`; so is a set reply with no `OBJACK` in it, which is never read as success.

## The objects

### `IObjectProvider`

`interface`. Five methods to implement and three with defaults, and they are where the objects
live. Each is awaited inside a transaction, so none should take long.

| Member | Signature | Message |
| --- | --- | --- |
| `GetObjectTypesAsync` | `ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken)` | S14F13 |
| `GetInstancesAsync` | `ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken)` | S14F15 |
| `GetAttributeNamesAsync` | `ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(string objectSpecification, string objectType, CancellationToken)` | S14F17 |
| `GetAttributesAsync` | `ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(ObjectAttributeRequest, CancellationToken)` | S14F1, S14F19 **and S1F19** - one provider method for all three, since what they ask for is the same thing |
| `FindAsync` | `ValueTask<IReadOnlyList<string>> FindAsync(ObjectRelationRequest, CancellationToken)` | S14F11. **Has a default**, which answers the query out of `GetInstancesAsync` and `GetAttributesAsync` - so every provider already written gets it for nothing, and one with an index over its attributes overrides it and does the work where the work is cheap |
| `GetErrorAsync` | `ValueTask<ObjectAttributeError?> GetErrorAsync(string objectSpecification, CancellationToken)` | S14F25. **Has a default** reporting none, which is the honest answer from a provider that keeps no errors. An object the provider does not have is answered by `ObjectServices` and never reaches here |
| `SetAttributesAsync` | `ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(ObjectAttributeWrite, CancellationToken)` | S14F3 and S14F21 |
| `ActAsync` | `ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(ObjectActionRequest, CancellationToken)` | S14F9 and S14F27. Default: an empty list, read as "no object of mine". |
| `CreateAsync` | `ValueTask<ObjectCreateResult> CreateAsync(ObjectCreateRequest, CancellationToken)` | S14F5. Default: `InvalidObjectType`. |
| `DeleteAsync` | `ValueTask<ObjectAcknowledgement> DeleteAsync(ObjectDeleteRequest, CancellationToken)` | S14F7. Default: `InvalidObjectType`. |

One interface serves the single-object and multi-object messages both: the single one arrives as a
request naming one object. An object left out of a get's answer is one this end does not have; an
object with no result in a set reads as `Error`, and one with no result in an action reads as
`InvalidObjectInstance`.

**The last three have defaults**, and the defaults are the honest answer for a provider whose
objects are the machine's own hardware: most object types have no actions, and a host cannot create
a load port by asking. A provider that has actions overrides `ActAsync`; one that does not is not
obliged to write a method saying so. They are default interface methods rather than new members, so
a provider written before they existed still compiles.

### `ObjectInMemoryProvider`

`sealed class : IObjectProvider`. Keyed by object specification, compared with
`StringComparer.Ordinal`. Attribute values are held as encoded bytes, because an item belongs to
its message and a store outlives every message it sees.

| Member | Signature | Notes |
| --- | --- | --- |
| `Add` | `void Add(string objectSpecification, string objectType)` | Adds or replaces an object. |
| `Set` | `bool Set(string objectSpecification, string attribute, SecsItem value)` | `false` when there is no such object. The item stays the caller's. |
| `Clear` | `void Clear()` | |
| `Count` | `int { get; }` | |
| `AllowNewAttributes` | `bool { get; set; }` | `false` by default: a set cannot invent an attribute. |

Refusals name the part that was wrong: an unknown type is `InvalidObjectType`, an unknown instance
`InvalidObjectInstance`, an unknown attribute `InvalidAttribute`. Every attribute of a set is
checked before any of them is written, so a set that names one bad attribute changes nothing.

It overrides `CreateAsync` and `DeleteAsync`, because a generic store can honestly make and remove
an object: the name comes from an `ObjectID` or `ObjID` attribute when the asker supplied one, and
otherwise from the type and a number. It does not override `ActAsync` - a bag of attributes has no
behaviour to drive.

### `CompositeObjectProvider`

`sealed class : IObjectProvider`. Several providers answering one stream.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `CompositeObjectProvider(params IObjectProvider[] providers)` | Also takes an `IEnumerable<IObjectProvider>`. |
| `Providers` | `IReadOnlyList<IObjectProvider> { get; }` | In registration order. |

Reads and sets concatenate, so each provider answers for its own objects and leaves the rest alone.
A create or a delete goes to the first provider that does not say "not mine" - a refusal of
`InvalidObjectType` - so one provider refusing does not end the search.

This is what makes a machine doing E87, E90 and E94 possible on one link: three packages each
register a provider, and the registration composes them instead of keeping the first and dropping
the rest.

## The model

### `ObjectAttribute`

`sealed class`. `Name` (`required string`, `ATTRID`) and `Value` (`required SecsItem`, `ATTRDATA`).

Ownership, in two rules: a value you hand this library becomes the library's and is disposed with
the message it travels in; a value this library hands you is a live view that ends when the call
returns. The one place the library copies a value for you is the echo in a set reply, which has to.

### `ObjectAttributeRelation`

`enum : byte` - `ATTRRELN`, and the eight names are the table's own: `EqualTo = 0`,
`NotEqualTo = 1`, `LessThan = 2`, `LessThanOrEqualTo = 3`, `GreaterThan = 4`,
`GreaterThanOrEqualTo = 5`, `Present = 6`, `Absent = 7`.

The last two compare against nothing, so a query using them carries no value. The table names no
reserved range, so there is **no catch-all member and nothing to fold** - the same position
`COMMACK` is in, and the reason a relation outside the table is a refusal rather than a value: a
query whose relation cannot be read is a query that cannot be performed.

---

### `ObjectRelationRequest`

`public sealed class` - what an S14F11 query is asking. `ObjectType` (`required string`),
`Attribute` (`required string`), `Relation` (`required ObjectAttributeRelation`) and `Value`
(`SecsItem?`, null for `Present` and `Absent`), with a `ToString()` of `Carrier.SlotCount
GreaterThan`.

---

### `ObjectAttributeRequest`

`sealed class`. `Objects` (`required IReadOnlyList<string>`, `OBJSPEC`), `ObjectType` (`string`,
`OBJTYPE` - empty for a multi-object request, which carries none) and `Attributes`
(`IReadOnlyList<string>`, `ATTRID` - empty asks for all of them).

### `ObjectAttributeWrite`

`sealed class`. `Objects`, `ObjectType`, and `Attributes`
(`required IReadOnlyList<ObjectAttribute>`).

### `ObjectAttributeValues`

`sealed class`. `ObjectSpecification` (`required string`) and `Attributes`
(`required IReadOnlyList<ObjectAttribute>`).

### `ObjectSetResult`

`sealed class`. `ObjectSpecification` (`required string`) and `Acknowledgement`
(`required ObjectAcknowledgement`).

### `ObjectParameter`

`sealed class`. `Name` (`required string`, `CPNAME`) and `Value` (`string`, `CPVAL`).

Two strings rather than a name and an item of any format, because the catalogue says `A` for both.
The reader takes any format and reads its text form, so a `U4` arriving where text was expected is
still understood.

### `ObjectActionRequest`

`sealed class`. `Objects` (`required IReadOnlyList<string>`, `OBJSPEC`), `ObjectType` (`string`,
`OBJTYPE` - empty for a multi-object action, which carries none), `Command`
(`required string`, `RCMD`) and `Parameters` (`IReadOnlyList<ObjectParameter>`).

`Command` is a string because `RCMD` is an `A` item: the vocabulary belongs to whatever standard
defines the object, and an enum here would cap what a caller can say.

### `ObjectActionResult`

`sealed class`. `ObjectSpecification` (`required string`) and `Acknowledgement`
(`required ObjectAcknowledgement`).

### `ObjectCreateRequest`

`sealed class`. `ObjectType` (`required string`) and `Attributes`
(`IReadOnlyList<ObjectAttribute>` - what the object should start out as).

### `ObjectCreateResult`

`sealed class`. `Acknowledgement` (`required ObjectAcknowledgement`) and `ObjectId` (`string`,
`OBJID` - empty when nothing was made).

The equipment names the object, not the asker: a host that wants a particular name sets it as an
attribute and leaves the machine to agree or refuse.

### `ObjectDeleteRequest`

`sealed class`. `ObjectType` (`string` - empty leaves it unsaid) and `ObjectSpecification`
(`required string`).

### `ObjectAcknowledgement`

`enum : byte` - `OBJACK`. `Successful = 0`, `Error = 1`, `InvalidObjectType = 2`,
`InvalidObjectInstance = 3`, `InvalidAttribute = 4`. A code E5 does not define reads as `Error`,
never as `Successful`.

## The enhanced half of stream 2

Eight transactions, all host to equipment: GEM's own questions with an `OBJSPEC` in front. Unlike
stream 14 this half has a direction, so it has two classes rather than one.

### `EnhancedServicesHost`

`sealed class`. The asking end. Declares no handlers, because a host answers nothing in this range.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `EnhancedServicesHost(HsmsConnection connection, ILogger<EnhancedServicesHost>? logger = null)` | |
| `CommandAsync` | `ValueTask<EnhancedCommandResult> CommandAsync(string objectSpecification, string command, IReadOnlyList<ObjectParameter>? parameters = null, uint dataId = 0, CancellationToken = default)` | S2F49. Read `IsAccepted`, not the code: `CompletionLater` is a success. |
| `BindReportAsync` | `ValueTask<EnhancedReportBindResult> BindReportAsync(uint reportId, string objectSpecification, CancellationToken = default)` | S2F51. The report is defined with GEM's S2F33. |
| `UnbindReportAsync` | `ValueTask<EnhancedReportBindResult> UnbindReportAsync(uint reportId, string objectSpecification, CancellationToken = default)` | S2F53. |
| `GetEquipmentConstantsAsync` | `ValueTask<int> (string objectSpecification, IReadOnlyList<uint> ids, Action<IReadOnlyList<SecsItem>> read, CancellationToken = default)` | S2F55. Positional; an empty list asks for all. |
| `GetStatusVariablesAsync` | as above | S2F57. |
| `GetDataVariablesAsync` | as above | S2F59. |
| `GetAlarmsAsync` | `ValueTask<IReadOnlyList<EnhancedAlarm>> GetAlarmsAsync(string objectSpecification, IReadOnlyList<uint> alarmIds, CancellationToken = default)` | S2F61. Not positional: entries carry their own `ALID`. |
| `GetDiagnosticsAsync` | `ValueTask<bool> GetDiagnosticsAsync(string objectSpecification, uint dataId, Action<SecsItem> read, CancellationToken = default)` | S2F63. `false` when the machine has no such diagnostic. |

The values come back through a callback for the same reason stream 14's attributes do: each item is
a view into the reply, and the reply is disposed when the method returns. A reply that cannot be
read at all throws `SecsException`; an acknowledgement this library does not recognise is reported
and never reads as success.

### `EnhancedServicesEquipment`

`sealed class`. The answering end. No connection of its own - it answers transactions handed to it.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `EnhancedServicesEquipment(IEnhancedServiceProvider provider, ILogger<EnhancedServicesEquipment>? logger = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S2F49, F51, F53, F55, F57, F59, F61, F63. None collides with GEM's nine. |
| `Provider` | `IEnhancedServiceProvider { get; }` | |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | |

A short answer from a provider is padded with zero-length items and a long one trimmed, so a
positional reply stays positional. `CommandAsync` is the one call guarded against a throwing
handler - it answers `CannotPerformNow`, as GEM does for S2F41 - because the read replies have no
code that could carry a failure.

### `IEnhancedServiceProvider`

`interface`. Eight methods, **all with defaults**, so an equipment implements the requests its
machine serves.

| Member | Signature | Default |
| --- | --- | --- |
| `CommandAsync` | `ValueTask<EnhancedCommandResult> (EnhancedCommandRequest, CancellationToken)` | `InvalidCommand` |
| `GetEquipmentConstantsAsync` | `ValueTask<IReadOnlyList<SecsItem>> (EnhancedValueRequest, CancellationToken)` | empty |
| `GetStatusVariablesAsync` | as above | empty |
| `GetDataVariablesAsync` | as above | empty |
| `GetAlarmsAsync` | `ValueTask<IReadOnlyList<EnhancedAlarm>> (EnhancedAlarmRequest, CancellationToken)` | empty |
| `GetDiagnosticsAsync` | `ValueTask<SecsItem?> (EnhancedDiagnosticRequest, CancellationToken)` | `null` |
| `BindReportAsync` | `ValueTask<EnhancedReportAcknowledgement> (EnhancedReportBinding, CancellationToken)` | `Denied` |
| `UnbindReportAsync` | as above | `Denied` |

Return one value per requested identifier, with a zero-length item for one this end does not have.
A value returned becomes the reply's and is disposed with it, so build a fresh item for each - the
first of the two ownership rules on [`ObjectAttribute`](#objectattribute), unchanged.

**The control state is yours to check** in `CommandAsync`: this package cannot see GEM's.

### `EnhancedInMemoryProvider`

`sealed class : IEnhancedServiceProvider`. Enough for a simulator.

| Member | Signature | Notes |
| --- | --- | --- |
| `Objects` | `IReadOnlyCollection<string> { get; }` | |
| `Bindings` | `IReadOnlyCollection<EnhancedReportBinding> { get; }` | |
| `AddObject` | `void AddObject(string objectSpecification)` | Also done implicitly by the four setters. |
| `SetVariable` | `void SetVariable(string objectSpecification, EnhancedVariableKind kind, uint id, SecsItem value)` | Encoded on the way in. |
| `SetAlarm` | `void SetAlarm(string objectSpecification, EnhancedAlarm alarm)` | Replaces one of the same `ALID`. |
| `SetDiagnostic` | `void SetDiagnostic(string objectSpecification, uint dataId, SecsItem data)` | |
| `SetCommand` | `void SetCommand(string objectSpecification, string command, Func<EnhancedCommandRequest, EnhancedCommandResult> handler)` | |
| `Clear` | `void Clear()` | |

An undeclared object gets `NoSuchObject`, an unregistered command `InvalidCommand`, and binding a
report twice is denied.

### The enhanced model

| Type | Members |
| --- | --- |
| `EnhancedCommandRequest` | `ObjectSpecification`, `Command`, `DataId`, `Parameters` (`ObjectParameter`, shared with S14F9) |
| `EnhancedCommandResult` | `Acknowledgement`, `Parameters`, `IsAccepted`, `static From(code)` |
| `EnhancedParameterResult` | `Name`, `Acknowledgement` |
| `EnhancedValueRequest` | `ObjectSpecification`, `Ids` - one type for `ECID`, `SVID` and `VID`, all `U4` |
| `EnhancedAlarmRequest` | `ObjectSpecification`, `AlarmIds` |
| `EnhancedAlarm` | `Id`, `Code` (`ALCD`), `Text`, `IsSet` (bit 7 of the code) |
| `EnhancedDiagnosticRequest` | `ObjectSpecification`, `DataId` |
| `EnhancedReportBinding` | `ReportId`, `ObjectSpecification` |
| `EnhancedReportBindResult` | `Code` (the raw byte), `Acknowledgement`, `IsAccepted` - nought only |
| `EnhancedVariableKind` | `EquipmentConstant`, `StatusVariable`, `DataVariable` |

| Enum | Members |
| --- | --- |
| `EnhancedCommandAcknowledgement` | `Accepted` 0, `InvalidCommand` 1, `CannotPerformNow` 2, `InvalidParameter` 3, `CompletionLater` 4, `AlreadyInDesiredCondition` 5, `NoSuchObject` 6 |
| `EnhancedParameterAcknowledgement` | `Accepted` 0, `NameDoesNotExist` 1, `IllegalValue` 2, `IllegalFormat` 3, `Error` 4 |
| `EnhancedReportAcknowledgement` | `Accepted` 0, `Denied` 1 - for both `RPACK` and `URACK`, neither of which the catalogue defines |

`EnhancedCommandAcknowledgement` is spelled like GEM's `GemHostCommandAcknowledgement` on purpose,
and is a second enum rather than a reference because this package does not depend on GEM - the
same reason the E40 package declares its own `HCACK`. Its sixth code is the one GEM can never
produce.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddObjectServices()` | `ObjectServices` for an end that only asks. Declares nothing. |
| `AddObjectProvider()` | The same plus `ObjectInMemoryProvider`, and handlers for all eleven primaries. |
| `AddObjectProvider<TProvider>()` | The same with a provider of your own. |
| `AddEnhancedServices()` | `EnhancedServicesHost`, for a host. Declares nothing: every primary in that range is host to equipment. |
| `AddEnhancedServiceProvider<TProvider>()` | `EnhancedServicesEquipment` over your provider, and handlers for the eight primaries. |

**The enhanced registration takes one provider, not several**, and that asymmetry is deliberate:
its replies are positional, so two providers each answering half a request cannot be merged. A
second call with a different provider keeps the first. Calling it twice is not a start-up failure.

Both ends of one link may use either registration: stream 14 is symmetric, and two ends that both
have providers can each ask the other.

**Providers compose.** Calling `AddObjectProvider` more than once - which is what a machine doing
E87, E90 and E94 does, one call per package - wraps every registered provider in a
`CompositeObjectProvider` rather than keeping one and dropping the rest. The same provider type
added twice is added once, and the eleven primaries are declared once however many providers there
are - through the builder's `DeclareOnce`, which this package had its own marker type for before
that rule moved where every capability could use it. An application that wants only its own simply
does not call the other registrations.
