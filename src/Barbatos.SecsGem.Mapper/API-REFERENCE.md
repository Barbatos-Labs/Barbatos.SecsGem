# Barbatos.SecsGem.Mapper - API reference

Namespace `Barbatos.SecsGem.Mapping`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [Mapping](#mapping)
  - [`SecsSerializer`](#secsserializer)
  - [`SecsSerializerOptions`](#secsserializeroptions)
  - [`SecsSerializerOptionsBuilder`](#secsserializeroptionsbuilder)
  - [`SecsDateTimeFormat`](#secsdatetimeformat)
  - [`SecsNullHandling`](#secsnullhandling)
  - [`SecsPropertyOrdering`](#secspropertyordering)
- [Attributes](#attributes)
  - [`SecsMessageAttribute`](#secsmessageattribute)
  - [`SecsItemAttribute`](#secsitemattribute)
  - [`SecsIgnoreAttribute`](#secsignoreattribute)
  - [`SecsConverterAttribute`](#secsconverterattribute)
- [Converters](#converters)
  - [`ISecsConverter`](#isecsconverter)
  - [`SecsConverter<T>`](#secsconvertert)
  - [`SecsBuiltInConverters`](#secsbuiltinconverters)
- [Contracts](#contracts)
  - [`ISecsTypeInfoResolver`](#isecstypeinforesolver)
  - [`SecsTypeInfoResolver`](#secstypeinforesolver)
  - [`GeneratedSecsTypeInfoResolver`](#generatedsecstypeinforesolver)
  - [`ReflectionSecsTypeInfoResolver`](#reflectionsecstypeinforesolver)
  - [`SecsTypeInfo`](#secstypeinfo)
  - [`SecsPropertyInfo`](#secspropertyinfo)
- [Inference](#inference)
  - [`SecsFormatInference`](#secsformatinference)
- [Exceptions](#exceptions)
  - [`SecsMappingException`](#secsmappingexception)
- [Hosting](#hosting)
  - [`AddSecsMapper`](#addsecsmapper)
  - [`AddSecsConverter`](#addsecsconverter)

---

## Mapping

### `SecsSerializer`

`public static class SecsSerializer`

| Member | Signature | Notes |
| --- | --- | --- |
| `Serialize<T>` | `static SecsItem Serialize<T>(T? value, SecsSerializerOptions? options = null)` | **The caller owns the item.** |
| `Serialize` | `static SecsItem Serialize(object? value, Type type, SecsSerializerOptions? options = null)` | The non-generic form, for a type known only at run time. |
| `ToMessage<T>` | `static SecsMessage ToMessage<T>(T value, SecsSerializerOptions? options = null)` | Needs `[SecsMessage]` on `T` for the stream and function. **The caller owns the message.** |
| `Deserialize<T>` | `static T? Deserialize<T>(SecsItem item, SecsSerializerOptions? options = null)` | Does not consume `item`; the caller still owns it. |
| `Deserialize` | `static object? Deserialize(SecsItem item, Type type, SecsSerializerOptions? options = null)` | |
| `FromMessage<T>` | `static T? FromMessage<T>(SecsMessage message, SecsSerializerOptions? options = null)` | Reads the body. |

`null` options means `SecsSerializerOptions.Default`. These six methods are also the call sites the
source generator watches: a `Serialize<T>` written here is how a plain record with no attributes
gets a generated contract.

### `SecsSerializerOptions`

`public sealed class SecsSerializerOptions`

Immutable; build one with an object initialiser, or through the builder below.

| Member | Signature | Default |
| --- | --- | --- |
| `Default` | `static SecsSerializerOptions { get; }` | |
| `TypeInfoResolver` | `ISecsTypeInfoResolver { get; init; }` | `SecsTypeInfoResolver.Default` |
| `Converters` | `IReadOnlyList<ISecsConverter> { get; init; }` | `SecsBuiltInConverters.All` |
| `DefaultTextFormat` | `SecsFormat { get; init; }` | `SecsFormat.ASCII` |
| `DateTimeFormat` | `SecsDateTimeFormat { get; init; }` | `Timestamp16` |
| `NullHandling` | `SecsNullHandling { get; init; }` | `EmptyItem` |
| `PropertyOrdering` | `SecsPropertyOrdering { get; init; }` | `Convention` |
| `Strict` | `bool { get; init; }` | `true` |
| `AllowStringToNumber` | `bool { get; init; }` | `true` |
| `WritePropertyLabels` | `bool { get; init; }` | `true` |
| `GetConverter` | `ISecsConverter? GetConverter(Type type)` | First converter whose `CanConvert` says yes. |

An options instance is part of the contract cache key in both resolvers, so a **new** options
object per call defeats that cache. Build one and keep it - which is what the DI registration does.

### `SecsSerializerOptionsBuilder`

`public sealed class SecsSerializerOptionsBuilder`

The mutable form, for `AddSecsMapper(mapper => ...)`.

| Member | Signature |
| --- | --- |
| `DefaultTextFormat` | `SecsFormat { get; set; }` |
| `DateTimeFormat` | `SecsDateTimeFormat { get; set; }` |
| `NullHandling` | `SecsNullHandling { get; set; }` |
| `PropertyOrdering` | `SecsPropertyOrdering { get; set; }` |
| `Strict` | `bool { get; set; }` |
| `AllowStringToNumber` | `bool { get; set; }` |
| `WritePropertyLabels` | `bool { get; set; }` |
| `Converters` | `IList<ISecsConverter> { get; }` |
| `TypeInfoResolver` | `ISecsTypeInfoResolver? { get; set; }` |
| `Build` | `SecsSerializerOptions Build(IEnumerable<ISecsConverter>? additionalConverters = null)` |

`additionalConverters` is how `AddSecsConverter<T>` contributes: converters registered in the
container are appended at `Build` time, so the order is "mine, then the built-ins".

### `SecsDateTimeFormat`

`public enum SecsDateTimeFormat`

| Member | Written as |
| --- | --- |
| `Timestamp16` | `yyyyMMddHHmmssff` - GEM's 16-character form, the default |
| `Timestamp12` | `yyMMddHHmmss` |
| `Iso8601` | round-trip ISO 8601 |
| `Ticks` | .NET ticks |

Reading accepts all of those **and** the 14-character `yyyyMMddHHmmss`, which equipment produces
often enough to read and which is deliberately not selectable for writing.

### `SecsNullHandling`

`public enum SecsNullHandling`

| Member | A `null` member becomes |
| --- | --- |
| `EmptyItem` | An empty item of the inferred format - the default, and what keeps list positions stable |
| `EmptyList` | An empty `L` |
| `Throw` | `SecsMappingException` |

Position matters in a SECS-II list, so a `null` must still occupy its slot. That is why the default
writes an empty item rather than skipping the member.

### `SecsPropertyOrdering`

`public enum SecsPropertyOrdering`

| Member | Meaning |
| --- | --- |
| `Convention` | `Order`, else constructor parameter order, else declaration order |
| `Explicit` | Refuse to map a member with no `Order` |

`Explicit` is the setting for a message whose field order comes from a document rather than from
your class.

---

## Attributes

### `SecsMessageAttribute`

`public sealed class SecsMessageAttribute : Attribute`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsMessageAttribute()` | |
| ctor | `SecsMessageAttribute(byte s, byte f)` | |
| `S` / `F` | `byte { get; set; }` | |
| `ReplyExpected` | `bool? { get; set; }` | `null` means "infer". |
| `Name` | `string? { get; set; }` | Becomes `SecsMessage.Name`. |
| `GetReplyExpected` | `bool GetReplyExpected()` | `ReplyExpected`, or odd-function inference when it is `null`. |

### `SecsItemAttribute`

`public sealed class SecsItemAttribute : Attribute`

| Member | Signature | Default | Notes |
| --- | --- | --- | --- |
| ctor | `SecsItemAttribute()` | | |
| ctor | `SecsItemAttribute(SecsFormat format)` | | |
| `Format` | `SecsFormat? { get; set; }` | `null` | Overrides inference - `U4` where C# would say `int`. |
| `Order` | `int { get; set; }` | `-1` | `-1` means unset. Set it on **every** mapped member or none; some is [BSG001](README.md#bsg001). |
| `Name` | `string? { get; set; }` | `null` | The item's label. |
| `Length` | `int { get; set; }` | `0` | Fixed width, for GEM's fixed-width fields. |
| `Padding` | `char { get; set; }` | `' '` | |
| `AsList` | `bool { get; set; }` | `false` | A list of single-element items instead of one array item. |
| `Optional` | `bool { get; set; }` | `false` | |
| `Converter` | `Type? { get; set; }` | `null` | A converter for this member only. |

### `SecsIgnoreAttribute`

`public sealed class SecsIgnoreAttribute : Attribute`

Leaves a member off the wire. An ignored member has no position, so it is also not counted by
BSG001.

### `SecsConverterAttribute`

`public sealed class SecsConverterAttribute : Attribute`

| Member | Signature |
| --- | --- |
| ctor | `SecsConverterAttribute(Type converterType)` |
| `ConverterType` | `Type { get; }` |

---

## Converters

### `ISecsConverter`

| Member | Signature |
| --- | --- |
| `CanConvert` | `bool CanConvert(Type type)` |
| `Write` | `SecsItem Write(object? value, Type type, SecsSerializerOptions options)` |
| `Read` | `object? Read(SecsItem item, Type type, SecsSerializerOptions options)` |

### `SecsConverter<T>`

`public abstract class SecsConverter<T> : ISecsConverter`

The typed base. Implement two methods; the non-generic interface is handled for you.

| Member | Signature |
| --- | --- |
| `CanConvert` | `virtual bool CanConvert(Type type)` - `type == typeof(T)` |
| `Write` | `abstract SecsItem Write(T? value, SecsSerializerOptions options)` |
| `Read` | `abstract T? Read(SecsItem item, SecsSerializerOptions options)` |

### `SecsBuiltInConverters`

`public static class SecsBuiltInConverters`

| Member | Signature |
| --- | --- |
| `All` | `static IReadOnlyList<ISecsConverter> { get; }` |

Seven ship, for the types SECS-II has no format for:

| Converter | Type | Wire form |
| --- | --- | --- |
| `SecsDateTimeConverter` | `DateTime` | per `DateTimeFormat`; also exposes `Timestamp16Pattern`, `Timestamp12Pattern` and a lenient `static DateTime Parse(string)` |
| `SecsDateTimeOffsetConverter` | `DateTimeOffset` | the same, with the offset |
| `SecsDecimalConverter` | `decimal` | ASCII, so nothing is rounded |
| `SecsTimeSpanConverter` | `TimeSpan` | ASCII |
| `SecsGuidConverter` | `Guid` | 16 binary bytes |
| `SecsDateOnlyConverter` | `DateOnly` | ASCII |
| `SecsTimeOnlyConverter` | `TimeOnly` | ASCII |

---

## Contracts

A contract is a type's shape: its members, their order, their formats and how to build an instance.
Two resolvers produce them, and they are required to agree.

### `ISecsTypeInfoResolver`

| Member | Signature |
| --- | --- |
| `GetTypeInfo` | `SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)` |

`null` means "not mine" - which is what makes the chain below work.

### `SecsTypeInfoResolver`

`public static class SecsTypeInfoResolver`

| Member | Signature | Notes |
| --- | --- | --- |
| `Reflection` | `static ISecsTypeInfoResolver { get; }` | Metadata at run time. |
| `Generated` | `static GeneratedSecsTypeInfoResolver { get; }` | Contracts the generator emitted. |
| `Default` | `static ISecsTypeInfoResolver { get; }` | `Combine(Generated, Reflection)`. |
| `Combine` | `static ISecsTypeInfoResolver Combine(params ISecsTypeInfoResolver[] resolvers)` | First non-`null` answer wins. |

Generated sits first, which is what makes the source generator additive: reference it and a covered
type stops using reflection, with no call site changed.

### `GeneratedSecsTypeInfoResolver`

`public sealed class GeneratedSecsTypeInfoResolver : ISecsTypeInfoResolver`

| Member | Signature | Notes |
| --- | --- | --- |
| `Register` | `void Register(Type type, Func<SecsSerializerOptions, SecsTypeInfo> factory)` | Called by generated code from a `[ModuleInitializer]`. Last registration wins, and anything built from an earlier one is discarded. |
| `Contains` | `bool Contains(Type type)` | Whether the generator saw a type. Useful in a test or a startup assertion. |
| `GetTypeInfo` | `SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)` | Cached per type and options instance. |

The cache is not an optimisation detail worth hiding: without it the factory rebuilt the contract -
property array, delegates, labels - for every mapped value, and the generator cost **more** than the
reflection it replaced. `MapperBenchmarks` is what caught it.

### `ReflectionSecsTypeInfoResolver`

`public sealed class ReflectionSecsTypeInfoResolver : ISecsTypeInfoResolver`

| Member | Signature |
| --- | --- |
| `GetTypeInfo` | `SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options)` |

Also cached per type and options instance. Returns `null` for a primitive, an `enum`, a `string`,
an array, an abstract type or an interface - none of which is a mapped object.

### `SecsTypeInfo`

`public sealed class SecsTypeInfo`

| Member | Signature | Notes |
| --- | --- | --- |
| `Type` | `required Type { get; init; }` | |
| `Message` | `SecsMessageAttribute? { get; init; }` | Present when the type is a whole message. |
| `Properties` | `required IReadOnlyList<SecsPropertyInfo> { get; init; }` | **In wire order.** |
| `CreateInstance` | `Func<object>? { get; init; }` | A parameterless constructor. |
| `CreateWithArguments` | `Func<object?[], object>? { get; init; }` | A positional constructor - what makes a `record` work. |
| `ConstructorParameterCount` | `int { get; init; }` | |
| `CanDeserialize` | `bool { get; }` | Either creator is present. |

### `SecsPropertyInfo`

`public sealed class SecsPropertyInfo`

| Member | Signature | Notes |
| --- | --- | --- |
| `Name` | `required string { get; init; }` | |
| `PropertyType` | `required Type { get; init; }` | |
| `Order` | `int { get; init; }` | |
| `Format` | `SecsFormat? { get; init; }` | `null` means infer from `PropertyType`. |
| `Label` | `string? { get; init; }` | |
| `FixedLength` | `int { get; init; }` | |
| `Padding` | `char { get; init; }` | |
| `AsList` | `bool { get; init; }` | |
| `Optional` | `bool { get; init; }` | |
| `Converter` | `ISecsConverter? { get; init; }` | Per-member override. |
| `Get` | `required Func<object, object?> { get; init; }` | A direct delegate from the generator; a `PropertyInfo` call from reflection. |
| `Set` | `Action<object, object?>? { get; init; }` | `null` for a member only a constructor can fill. |
| `ConstructorParameterIndex` | `int { get; init; }` | `-1` when the member is not a constructor parameter. |

A member with no `Set` and `ConstructorParameterIndex` of `-1` cannot be written at all, which is
the shape the generator declines and leaves to reflection.

---

## Inference

### `SecsFormatInference`

`public static class SecsFormatInference`

The convention itself, public so a custom resolver can reuse it rather than reimplement it.

| Member | Signature | Notes |
| --- | --- | --- |
| `Unwrap` | `static Type Unwrap(Type type)` | Strips `Nullable<>`. |
| `TryInferScalar` | `static bool TryInferScalar(Type type, SecsSerializerOptions options, out SecsFormat format)` | The C#-to-E5 table. |
| `IsBinaryPayload` | `static bool IsBinaryPayload(Type type)` | `byte[]` and friends map to `B`, not to a list of `U1`. |
| `GetCollectionElementType` | `static Type? GetCollectionElementType(Type type)` | `null` when the type is not a sequence. |
| `CreateValueItem` | `static SecsItem CreateValueItem(SecsFormat format, IReadOnlyList<object?> values)` | |
| `ReadValues` | `static object?[] ReadValues(SecsItem item, Type elementType, SecsSerializerOptions options)` | |
| `ConvertScalar` | `static object? ConvertScalar(object? value, Type target, SecsSerializerOptions options)` | Honours `AllowStringToNumber`. |

---

## Exceptions

### `SecsMappingException`

`public sealed class SecsMappingException : SecsException`

| Member | Signature | Notes |
| --- | --- | --- |
| ctor | `SecsMappingException(string message)` | |
| ctor | `SecsMappingException(string message, Exception? innerException)` | |
| ctor | `SecsMappingException(string message, string memberPath, Exception? innerException = null)` | |
| `MemberPath` | `string? { get; }` | For example `EventReportSend.Reports[1].Values[0].Value`. |

A nested message that fails ten items deep is useless without the path, so the path is part of the
exception rather than something to reconstruct from a stack trace.

---

## Hosting

Namespace `Microsoft.Extensions.DependencyInjection`.

### `AddSecsMapper`

`public static IServiceCollection AddSecsMapper(this IServiceCollection services, Action<SecsSerializerOptionsBuilder>? configure = null)`

| Service | Lifetime | Notes |
| --- | --- | --- |
| `SecsSerializerOptions` | Singleton | Built once from the builder and every registered converter. |

One instance, kept - which also keeps the contract caches in both resolvers warm.

### `AddSecsConverter`

`public static IServiceCollection AddSecsConverter<TConverter>(this IServiceCollection services)`

Adds a converter to the collection `AddSecsMapper` draws from.

`GetConverter` takes the first converter whose `CanConvert` says yes, and `Build` concatenates them
in this order:

1. Converters registered in the container with `AddSecsConverter<T>`.
2. Converters added to `SecsSerializerOptionsBuilder.Converters` inside `AddSecsMapper(...)`.
3. `SecsBuiltInConverters.All`.

So a `DateTime` converter of your own beats `SecsDateTimeConverter`, and one registered through DI
beats one configured inline.
