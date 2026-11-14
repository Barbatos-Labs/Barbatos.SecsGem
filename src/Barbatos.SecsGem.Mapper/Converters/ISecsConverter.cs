// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Maps one CLR type to and from a SECS-II item.
/// </summary>
/// <remarks>
/// The mapper's extension point, and the reason a new type never needs a change to this
/// assembly: register a converter and the type maps everywhere - on its own, as a property, as
/// an element of a collection, nested however deep.
/// </remarks>
public interface ISecsConverter
{
    /// <summary>Gets a value indicating whether this converter handles <paramref name="type"/>.</summary>
    /// <param name="type">The CLR type.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    bool CanConvert(Type type);

    /// <summary>Converts a value to an item.</summary>
    /// <param name="value">The value. May be <see langword="null"/>.</param>
    /// <param name="type">The declared type of the value.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The item.</returns>
    SecsItem Write(object? value, Type type, SecsSerializerOptions options);

    /// <summary>Converts an item back to a value.</summary>
    /// <param name="item">The item.</param>
    /// <param name="type">The type to produce.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The value.</returns>
    object? Read(SecsItem item, Type type, SecsSerializerOptions options);
}

/// <summary>
/// A convenient base for a converter that handles exactly one type.
/// </summary>
/// <typeparam name="T">The type this converter handles.</typeparam>
public abstract class SecsConverter<T> : ISecsConverter
{
    /// <inheritdoc/>
    public virtual bool CanConvert(Type type) => type == typeof(T);

    /// <summary>Converts a value to an item.</summary>
    /// <param name="value">The value.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The item.</returns>
    public abstract SecsItem Write(T? value, SecsSerializerOptions options);

    /// <summary>Converts an item back to a value.</summary>
    /// <param name="item">The item.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The value.</returns>
    public abstract T? Read(SecsItem item, SecsSerializerOptions options);

    /// <inheritdoc/>
    SecsItem ISecsConverter.Write(object? value, Type type, SecsSerializerOptions options)
        => Write(value is null ? default : (T)value, options);

    /// <inheritdoc/>
    object? ISecsConverter.Read(SecsItem item, Type type, SecsSerializerOptions options)
        => Read(item, options);
}

/// <summary>
/// Pins a converter to a type or a member.
/// </summary>
/// <remarks>
/// On a type it applies wherever that type appears; on a member it applies to that member only,
/// overriding anything registered for the type.
/// </remarks>
/// <param name="converterType">A type implementing <see cref="ISecsConverter"/> with a public parameterless constructor.</param>
[AttributeUsage(
    AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Property | AttributeTargets.Field,
    AllowMultiple = false,
    Inherited = true)]
public sealed class SecsConverterAttribute(Type converterType) : Attribute
{
    /// <summary>Gets the converter type.</summary>
    public Type ConverterType { get; } = converterType ?? throw new ArgumentNullException(nameof(converterType));
}
