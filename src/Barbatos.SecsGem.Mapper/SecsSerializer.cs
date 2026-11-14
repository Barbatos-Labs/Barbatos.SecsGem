// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections;

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Maps C# objects to SECS-II item trees and back, by convention.
/// </summary>
/// <remarks>
/// <para>
/// The rule is one sentence: <strong>an object is a list, and its members are that list's items,
/// in declaration order</strong>. Everything else follows from it.
/// </para>
/// <code>
/// public sealed record Person(string Name, int Age, string[] Emails);
///
/// SecsSerializer.Serialize(new Person("Hung", 36, ["a@x.com", "b@x.com"]));
/// // &lt;L [3]
/// //   &lt;A [4] 'Hung'&gt;        // Name
/// //   &lt;I4 [1] 36&gt;           // Age
/// //   &lt;L [2]                // Emails
/// //     &lt;A [7] 'a@x.com'&gt;
/// //     &lt;A [7] 'b@x.com'&gt;
/// //   &gt;
/// // &gt;
/// </code>
/// <para>
/// No attributes were needed for that, and none would change it. Attributes exist for the cases
/// where the equipment disagrees with the convention - see <see cref="SecsItemAttribute"/>.
/// </para>
/// <para>
/// <strong>Collections of scalars collapse.</strong> <c>int[] { 1, 2, 3 }</c> becomes
/// <c>&lt;I4 [3] 1 2 3&gt;</c>, one item with three elements, because that is what SECS-II
/// arrays are for. Strings cannot collapse - a single ASCII item holds one string, not three -
/// so <c>string[]</c> becomes a list, as above.
/// </para>
/// <para>
/// <strong>Ownership.</strong> Serializing produces a tree the caller owns and should dispose.
/// Deserializing does not take ownership of the item it reads.
/// </para>
/// </remarks>
public static class SecsSerializer
{
    // ---------------------------------------------------------------------------------------
    // Object  ->  item
    // ---------------------------------------------------------------------------------------

    /// <summary>Maps a value to a SECS-II item.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">Mapping options. <see langword="null"/> uses <see cref="SecsSerializerOptions.Default"/>.</param>
    /// <returns>The item tree. The caller disposes it.</returns>
    /// <exception cref="SecsMappingException">The type cannot be mapped.</exception>
    public static SecsItem Serialize<T>(T? value, SecsSerializerOptions? options = null)
        => SerializeValue(value, typeof(T), options ?? SecsSerializerOptions.Default, typeof(T).Name, depth: 0, member: null);

    /// <summary>Maps a value of a type known only at run time.</summary>
    /// <param name="value">The value.</param>
    /// <param name="type">The value's declared type.</param>
    /// <param name="options">Mapping options.</param>
    /// <returns>The item tree. The caller disposes it.</returns>
    public static SecsItem Serialize(object? value, Type type, SecsSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        return SerializeValue(value, type, options ?? SecsSerializerOptions.Default, type.Name, depth: 0, member: null);
    }

    /// <summary>Maps a value to a complete message, using its <see cref="SecsMessageAttribute"/>.</summary>
    /// <typeparam name="T">The value's type. Must carry <see cref="SecsMessageAttribute"/>.</typeparam>
    /// <param name="value">The value.</param>
    /// <param name="options">Mapping options.</param>
    /// <returns>The message. The caller disposes it.</returns>
    /// <exception cref="SecsMappingException"><typeparamref name="T"/> has no <see cref="SecsMessageAttribute"/>.</exception>
    public static SecsMessage ToMessage<T>(T value, SecsSerializerOptions? options = null)
    {
        options ??= SecsSerializerOptions.Default;

        var type = value?.GetType() ?? typeof(T);
        var attribute = type.GetCustomAttribute<SecsMessageAttribute>(inherit: true)
            ?? throw new SecsMappingException(
                $"{type.Name} has no [SecsMessage]. Add one, or use {nameof(Serialize)} and build the message yourself.");

        return new SecsMessage(attribute.S, attribute.F, attribute.GetReplyExpected())
        {
            Name = attribute.Name ?? type.Name,
            Item = SerializeValue(value, type, options, type.Name, depth: 0, member: null),
        };
    }

    // ---------------------------------------------------------------------------------------
    // Item  ->  object
    // ---------------------------------------------------------------------------------------

    /// <summary>Maps a SECS-II item back to a value.</summary>
    /// <typeparam name="T">The type to produce.</typeparam>
    /// <param name="item">The item. Ownership stays with the caller.</param>
    /// <param name="options">Mapping options.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SecsMappingException">The item does not fit <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(SecsItem item, SecsSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (T?)DeserializeValue(item, typeof(T), options ?? SecsSerializerOptions.Default, typeof(T).Name, depth: 0, member: null);
    }

    /// <summary>Maps a SECS-II item back to a value of a type known only at run time.</summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The type to produce.</param>
    /// <param name="options">Mapping options.</param>
    /// <returns>The value.</returns>
    public static object? Deserialize(SecsItem item, Type type, SecsSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(type);
        return DeserializeValue(item, type, options ?? SecsSerializerOptions.Default, type.Name, depth: 0, member: null);
    }

    /// <summary>Maps a message's body back to a value, checking the stream and function first.</summary>
    /// <typeparam name="T">The type to produce. Must carry <see cref="SecsMessageAttribute"/>.</typeparam>
    /// <param name="message">The message. Ownership stays with the caller.</param>
    /// <param name="options">Mapping options.</param>
    /// <returns>The value.</returns>
    /// <exception cref="SecsMappingException">
    /// <typeparamref name="T"/> has no <see cref="SecsMessageAttribute"/>, or the message's S and
    /// F do not match it, or the body does not fit.
    /// </exception>
    /// <remarks>
    /// The S/F check is what stops a handler registered for S1F3 from quietly deserialising an
    /// S1F5 that happened to have a compatible shape.
    /// </remarks>
    public static T? FromMessage<T>(SecsMessage message, SecsSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        options ??= SecsSerializerOptions.Default;

        var attribute = typeof(T).GetCustomAttribute<SecsMessageAttribute>(inherit: true)
            ?? throw new SecsMappingException(
                $"{typeof(T).Name} has no [SecsMessage]. Add one, or use {nameof(Deserialize)} on the message's item.");

        if (attribute.S != message.S || attribute.F != message.F)
        {
            throw new SecsMappingException(
                $"{typeof(T).Name} maps S{attribute.S}F{attribute.F}, but the message is S{message.S}F{message.F}.");
        }

        return message.Item is null
            ? default
            : (T?)DeserializeValue(message.Item, typeof(T), options, typeof(T).Name, depth: 0, member: null);
    }

    // ---------------------------------------------------------------------------------------
    // Serialization core
    // ---------------------------------------------------------------------------------------

    private static SecsItem SerializeValue(
        object? value,
        Type declaredType,
        SecsSerializerOptions options,
        string path,
        int depth,
        SecsPropertyInfo? member)
    {
        if (depth > SecsItem.MaxDepth)
        {
            throw new SecsMappingException($"Mapping nested deeper than the {SecsItem.MaxDepth}-level limit.", path);
        }

        // An item in the model is an escape hatch for messages no object model fits. It is
        // passed through untouched, and ownership stays with whoever built it.
        if (value is SecsItem passthrough)
        {
            return passthrough;
        }

        var type = SecsFormatInference.Unwrap(declaredType);
        var converter = member?.Converter ?? options.GetConverter(type);

        if (converter is not null)
        {
            return value is null
                ? CreateNullItem(type, options, member, path)
                : Label(converter.Write(value, type, options), member, options);
        }

        if (value is null)
        {
            return CreateNullItem(type, options, member, path);
        }

        if (SecsFormatInference.IsBinaryPayload(type))
        {
            return Label(SecsItem.B(AsByteArray(value)), member, options);
        }

        if (SecsFormatInference.TryInferScalar(type, options, out var inferred))
        {
            var format = member?.Format ?? inferred;
            return Label(CreateScalarItem(value, format, member, path), member, options);
        }

        var elementType = SecsFormatInference.GetCollectionElementType(type);
        if (elementType is not null)
        {
            return Label(SerializeCollection(value, elementType, options, path, depth, member), member, options);
        }

        var info = options.TypeInfoResolver.GetTypeInfo(type, options)
            ?? throw new SecsMappingException($"No mapping contract for {type.Name}.", path);

        var children = new SecsItem[info.Properties.Count];
        var written = 0;
        try
        {
            for (var i = 0; i < info.Properties.Count; i++)
            {
                var property = info.Properties[i];
                children[i] = SerializeValue(
                    property.Get(value),
                    property.PropertyType,
                    options,
                    $"{path}.{property.Name}",
                    depth + 1,
                    property);
                written++;
            }
        }
        catch
        {
            for (var i = 0; i < written; i++)
            {
                children[i].Dispose();
            }

            throw;
        }

        return Label(SecsItem.L(children), member, options);
    }

    private static SecsItem SerializeCollection(
        object value,
        Type elementType,
        SecsSerializerOptions options,
        string path,
        int depth,
        SecsPropertyInfo? member)
    {
        var elements = Enumerate(value, path);
        var element = SecsFormatInference.Unwrap(elementType);
        var elementConverter = options.GetConverter(element);

        // A collection of scalars collapses into ONE array item - the whole point of SECS-II's
        // array formats. Text cannot collapse: an ASCII item holds a single string.
        if (elementConverter is null
            && member?.AsList != true
            && SecsFormatInference.TryInferScalar(element, options, out var inferred)
            && !inferred.IsText())
        {
            var format = member?.Format ?? inferred;
            if (!format.IsText())
            {
                var item = SecsFormatInference.CreateValueItem(format, elements);
                CheckFixedLength(item, member, path);
                return item;
            }
        }

        var children = new SecsItem[elements.Count];
        var written = 0;
        try
        {
            for (var i = 0; i < elements.Count; i++)
            {
                children[i] = SerializeValue(
                    elements[i],
                    elementType,
                    options,
                    $"{path}[{i}]",
                    depth + 1,
                    member is null ? null : new SecsPropertyInfo
                    {
                        // The element inherits the member's format and converter, but not its
                        // label or fixed length: those describe the collection, not an element.
                        Name = member.Name,
                        PropertyType = elementType,
                        Format = member.Format,
                        Converter = member.Converter,
                        Get = member.Get,
                    });
                written++;
            }

            return SecsItem.L(children);
        }
        catch
        {
            for (var i = 0; i < written; i++)
            {
                children[i].Dispose();
            }

            throw;
        }
    }

    private static SecsItem CreateScalarItem(object value, SecsFormat format, SecsPropertyInfo? member, string path)
    {
        if (format.IsText())
        {
            var text = value as string ?? System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
            if (member is { FixedLength: > 0 })
            {
                return format == SecsFormat.JIS8
                    ? SecsItem.J(Pad(text, member.FixedLength, member.Padding))
                    : SecsItem.A(text, member.FixedLength, member.Padding);
            }

            return format == SecsFormat.JIS8 ? SecsItem.J(text) : SecsItem.A(text);
        }

        var item = SecsFormatInference.CreateValueItem(format, [value]);
        CheckFixedLength(item, member, path);
        return item;
    }

    private static void CheckFixedLength(SecsItem item, SecsPropertyInfo? member, string path)
    {
        if (member is { FixedLength: > 0 } && item.Count != member.FixedLength)
        {
            item.Dispose();
            throw new SecsMappingException(
                $"{member.FixedLength} element(s) expected, {item.Count} supplied.",
                path);
        }
    }

    private static string Pad(string text, int length, char padding)
        => text.Length == length ? text
            : text.Length > length ? text[..length]
            : text.PadRight(length, padding);

    private static SecsItem CreateNullItem(Type type, SecsSerializerOptions options, SecsPropertyInfo? member, string path)
        => options.NullHandling switch
        {
            SecsNullHandling.EmptyList => SecsItem.Empty(SecsFormat.List),
            SecsNullHandling.EmptyItem => SecsItem.Empty(
                member?.Format
                ?? (SecsFormatInference.TryInferScalar(type, options, out var format) ? format : SecsFormat.List)),
            _ => throw new SecsMappingException(
                $"A null value cannot be mapped while {nameof(SecsSerializerOptions.NullHandling)} is {options.NullHandling}.",
                path),
        };

    private static SecsItem Label(SecsItem item, SecsPropertyInfo? member, SecsSerializerOptions options)
    {
        if (member?.Label is { Length: > 0 } label && options.WritePropertyLabels)
        {
            return item.Named(label);
        }

        return item;
    }

    private static List<object?> Enumerate(object value, string path)
    {
        if (value is not IEnumerable sequence)
        {
            throw new SecsMappingException($"{value.GetType().Name} is not enumerable.", path);
        }

        var elements = new List<object?>();
        foreach (var element in sequence)
        {
            elements.Add(element);
        }

        return elements;
    }

    private static byte[] AsByteArray(object value) => value switch
    {
        byte[] bytes => bytes,
        Memory<byte> memory => memory.ToArray(),
        ReadOnlyMemory<byte> memory => memory.ToArray(),
        _ => throw new SecsMappingException($"{value.GetType().Name} is not a byte payload."),
    };

    // ---------------------------------------------------------------------------------------
    // Deserialization core
    // ---------------------------------------------------------------------------------------

    private static object? DeserializeValue(
        SecsItem item,
        Type declaredType,
        SecsSerializerOptions options,
        string path,
        int depth,
        SecsPropertyInfo? member)
    {
        if (depth > SecsItem.MaxDepth)
        {
            throw new SecsMappingException($"Mapping nested deeper than the {SecsItem.MaxDepth}-level limit.", path);
        }

        if (typeof(SecsItem).IsAssignableFrom(declaredType))
        {
            return item;
        }

        var nullable = Nullable.GetUnderlyingType(declaredType) is not null || !declaredType.IsValueType;
        var type = SecsFormatInference.Unwrap(declaredType);

        // An empty item is how a null travelled out, so it is how one travels back.
        if (item.Count == 0 && nullable && !item.IsList)
        {
            return null;
        }

        var converter = member?.Converter ?? options.GetConverter(type);
        if (converter is not null)
        {
            return converter.Read(item, type, options);
        }

        if (SecsFormatInference.IsBinaryPayload(type))
        {
            var bytes = item.GetSpan<byte>().ToArray();
            return type == typeof(byte[]) ? bytes
                : type == typeof(Memory<byte>) ? new Memory<byte>(bytes)
                : new ReadOnlyMemory<byte>(bytes);
        }

        if (SecsFormatInference.TryInferScalar(type, options, out _))
        {
            if (item.IsList)
            {
                throw new SecsMappingException($"A list cannot fill a {type.Name}.", path);
            }

            var values = SecsFormatInference.ReadValues(item, type, options);
            return values.Length > 0 ? values[0] : SecsFormatInference.ConvertScalar(null, type, options);
        }

        var elementType = SecsFormatInference.GetCollectionElementType(type);
        if (elementType is not null)
        {
            return DeserializeCollection(item, type, elementType, options, path, depth);
        }

        return DeserializeObject(item, type, options, path, depth);
    }

    private static object DeserializeCollection(
        SecsItem item,
        Type collectionType,
        Type elementType,
        SecsSerializerOptions options,
        string path,
        int depth)
    {
        object?[] elements;

        if (item.IsList)
        {
            elements = new object?[item.Count];
            for (var i = 0; i < item.Count; i++)
            {
                elements[i] = DeserializeValue(item[i], elementType, options, $"{path}[{i}]", depth + 1, member: null);
            }
        }
        else
        {
            // One array item holding many elements: the collapsed form.
            elements = SecsFormatInference.ReadValues(item, elementType, options);
        }

        var array = Array.CreateInstance(SecsFormatInference.Unwrap(elementType) == elementType ? elementType : elementType, elements.Length);
        for (var i = 0; i < elements.Length; i++)
        {
            array.SetValue(elements[i], i);
        }

        if (collectionType.IsArray || collectionType.IsAssignableFrom(array.GetType()))
        {
            return array;
        }

        var listType = typeof(List<>).MakeGenericType(elementType);
        if (collectionType.IsAssignableFrom(listType))
        {
            return Activator.CreateInstance(listType, array)!;
        }

        // A concrete collection with an IEnumerable<T> constructor - HashSet<T>, Collection<T>,
        // ImmutableArray<T>.Builder-style wrappers.
        var constructor = collectionType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(elementType)]);
        if (constructor is not null)
        {
            return constructor.Invoke([array]);
        }

        throw new SecsMappingException(
            $"{collectionType.Name} cannot be built from an item list. Use an array, a List<T>, or a type with an IEnumerable<T> constructor.",
            path);
    }

    private static object DeserializeObject(
        SecsItem item,
        Type type,
        SecsSerializerOptions options,
        string path,
        int depth)
    {
        var info = options.TypeInfoResolver.GetTypeInfo(type, options)
            ?? throw new SecsMappingException($"No mapping contract for {type.Name}.", path);

        if (!info.CanDeserialize)
        {
            throw new SecsMappingException(
                $"{type.Name} has no usable constructor. A mapped type needs a parameterless constructor or one whose parameters name its members.",
                path);
        }

        if (!item.IsList)
        {
            throw new SecsMappingException(
                $"{type.Name} maps to a list, but the item is {item.Format.ToSmlToken()}.",
                path);
        }

        if (options.Strict && item.Count > info.Properties.Count)
        {
            throw new SecsMappingException(
                $"{type.Name} has {info.Properties.Count} mapped member(s), but the list holds {item.Count} item(s).",
                path);
        }

        var values = new object?[info.Properties.Count];
        for (var i = 0; i < info.Properties.Count; i++)
        {
            var property = info.Properties[i];

            if (i >= item.Count)
            {
                if (options.Strict && !property.Optional)
                {
                    throw new SecsMappingException(
                        $"the list ends after {item.Count} item(s), before {property.Name}. Mark it [SecsItem(Optional = true)] if older equipment may omit it.",
                        $"{path}.{property.Name}");
                }

                values[i] = null;
                continue;
            }

            values[i] = DeserializeValue(
                item[i],
                property.PropertyType,
                options,
                $"{path}.{property.Name}",
                depth + 1,
                property);
        }

        object instance;
        if (info.CreateWithArguments is not null && info.ConstructorParameterCount > 0)
        {
            var arguments = new object?[info.ConstructorParameterCount];
            for (var i = 0; i < info.Properties.Count; i++)
            {
                var index = info.Properties[i].ConstructorParameterIndex;
                if (index >= 0 && index < arguments.Length)
                {
                    arguments[index] = values[i];
                }
            }

            FillMissingArguments(arguments, info);
            instance = info.CreateWithArguments(arguments);
        }
        else
        {
            instance = info.CreateInstance!();
        }

        // Members that are not constructor parameters are set afterwards.
        for (var i = 0; i < info.Properties.Count; i++)
        {
            var property = info.Properties[i];
            if (property.ConstructorParameterIndex >= 0 || property.Set is null)
            {
                continue;
            }

            if (values[i] is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
            {
                continue;
            }

            property.Set(instance, values[i]);
        }

        return instance;
    }

    /// <summary>Fills constructor slots no mapped member covered with that parameter's default.</summary>
    private static void FillMissingArguments(object?[] arguments, SecsTypeInfo info)
    {
        var covered = new bool[arguments.Length];
        foreach (var property in info.Properties)
        {
            if (property.ConstructorParameterIndex is var index && index >= 0 && index < covered.Length)
            {
                covered[index] = true;
            }
        }

        var parameters = info.Type.GetConstructors()
            .FirstOrDefault(c => c.GetParameters().Length == arguments.Length)
            ?.GetParameters();

        for (var i = 0; i < arguments.Length; i++)
        {
            if (covered[i])
            {
                continue;
            }

            var parameterType = parameters?[i].ParameterType;
            arguments[i] = parameterType is { IsValueType: true } && Nullable.GetUnderlyingType(parameterType) is null
                ? Activator.CreateInstance(parameterType)
                : null;
        }
    }
}
