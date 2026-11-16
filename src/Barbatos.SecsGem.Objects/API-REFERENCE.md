# Barbatos.SecsGem.Objects - API reference

Namespace `Barbatos.SecsGem.Objects`, except where noted. Concepts, worked examples and the
provenance note live in [`README.md`](README.md); this page is member-by-member.

- [`ObjectServices`](#objectservices)
- [The objects](#the-objects)
  - [`IObjectProvider`](#iobjectprovider)
  - [`ObjectInMemoryProvider`](#objectinmemoryprovider)
- [The model](#the-model)
  - [`ObjectAttribute`](#objectattribute)
  - [`ObjectAttributeRequest`](#objectattributerequest)
  - [`ObjectAttributeWrite`](#objectattributewrite)
  - [`ObjectAttributeValues`](#objectattributevalues)
  - [`ObjectSetResult`](#objectsetresult)
  - [`ObjectAcknowledgement`](#objectacknowledgement)
- [Registration](#registration)

## `ObjectServices`

`sealed class`. Asks and answers; one class because E5 gives stream 14 no sides. Not disposable -
no state, no timer, nothing to release.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `ObjectServices(HsmsConnection connection, IObjectProvider? provider = null, ILogger<ObjectServices>? logger = null)` | A `null` provider makes an end that only asks. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S14F1, F3, F13, F15, F17, F19, F21. Declare them only on an end that has a provider. |
| `CanAnswer` | `bool { get; }` | Whether a provider was given. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Answers out of the provider. With no provider it logs and replies nothing. |
| `GetObjectTypesAsync` | `ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken = default)` | S14F13. |
| `GetInstancesAsync` | `ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken = default)` | S14F15. |
| `GetAttributeNamesAsync` | `ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(string objectSpecification, string objectType, CancellationToken = default)` | S14F17. An empty specification asks about the type. |
| `GetAttributesAsync` | `ValueTask<bool> GetAttributesAsync(string objectSpecification, string objectType, IReadOnlyList<string> attributes, Action<ObjectAttributeValues> read, CancellationToken = default)` | S14F1. `false` means the other end does not have that object. |
| `GetAttributesAsync` | `ValueTask<int> GetAttributesAsync(IReadOnlyList<string> objectSpecifications, IReadOnlyList<string> attributes, Action<ObjectAttributeValues> read, CancellationToken = default)` | S14F19. Returns how many objects came back. |
| `SetAttributesAsync` | `ValueTask<ObjectAcknowledgement> SetAttributesAsync(string objectSpecification, string objectType, IReadOnlyList<ObjectAttribute> attributes, CancellationToken = default)` | S14F3. |
| `SetAttributesAsync` | `ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(IReadOnlyList<string> objectSpecifications, IReadOnlyList<ObjectAttribute> attributes, CancellationToken = default)` | S14F21. One result per object. |

**The callback on a get** is not a style choice. An attribute value is a view into the reply, and
the reply is disposed when the method returns, so values cannot be handed back - everything a
caller keeps, it copies inside the callback.

**A get cannot fail.** S14F2 and S14F20 carry attributes and no acknowledgement code, so an object
that could not be read is absent from the answer. A reply that is not attribute data at all is a
`SecsException`; so is a set reply with no `OBJACK` in it, which is never read as success.

## The objects

### `IObjectProvider`

`interface`. Five methods, and they are where the objects live. Each is awaited inside a
transaction, so none should take long.

| Member | Signature | Message |
| --- | --- | --- |
| `GetObjectTypesAsync` | `ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken)` | S14F13 |
| `GetInstancesAsync` | `ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken)` | S14F15 |
| `GetAttributeNamesAsync` | `ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(string objectSpecification, string objectType, CancellationToken)` | S14F17 |
| `GetAttributesAsync` | `ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(ObjectAttributeRequest, CancellationToken)` | S14F1 and S14F19 |
| `SetAttributesAsync` | `ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(ObjectAttributeWrite, CancellationToken)` | S14F3 and S14F21 |

One interface serves the single-object and multi-object messages both: the single one arrives as a
request naming one object. An object left out of a get's answer is one this end does not have; an
object with no result in a set reads as `Error`.

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

## The model

### `ObjectAttribute`

`sealed class`. `Name` (`required string`, `ATTRID`) and `Value` (`required SecsItem`, `ATTRDATA`).

Ownership, in two rules: a value you hand this library becomes the library's and is disposed with
the message it travels in; a value this library hands you is a live view that ends when the call
returns. The one place the library copies a value for you is the echo in a set reply, which has to.

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

### `ObjectAcknowledgement`

`enum : byte` - `OBJACK`. `Successful = 0`, `Error = 1`, `InvalidObjectType = 2`,
`InvalidObjectInstance = 3`, `InvalidAttribute = 4`. A code E5 does not define reads as `Error`,
never as `Successful`.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddObjectServices()` | `ObjectServices` for an end that only asks. Declares nothing. |
| `AddObjectProvider()` | The same plus `ObjectInMemoryProvider`, and handlers for all seven primaries. |
| `AddObjectProvider<TProvider>()` | The same with a provider of your own. `TryAdd`, so an `IObjectProvider` already registered wins. |

Both ends of one link may use either registration: stream 14 is symmetric, and two ends that both
have providers can each ask the other.
