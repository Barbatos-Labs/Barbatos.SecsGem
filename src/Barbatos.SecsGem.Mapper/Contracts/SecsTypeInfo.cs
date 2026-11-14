// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// One mapped member of a type: where it sits in the item list, and how to read and write it.
/// </summary>
/// <remarks>
/// Produced by an <see cref="ISecsTypeInfoResolver"/>, whether from the syntax tree at compile
/// time or from reflection at run time. Everything downstream works from this, so the two paths
/// cannot drift in behaviour - only in cost.
/// </remarks>
public sealed class SecsPropertyInfo
{
    /// <summary>Gets the member's name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the member's declared type.</summary>
    public required Type PropertyType { get; init; }

    /// <summary>Gets the member's position in the item list, lowest first.</summary>
    public int Order { get; init; }

    /// <summary>Gets the format the member was pinned to, or <see langword="null"/> to infer it.</summary>
    public SecsFormat? Format { get; init; }

    /// <summary>Gets the item label to write, or <see langword="null"/> for none.</summary>
    public string? Label { get; init; }

    /// <summary>Gets the fixed element count, or <c>0</c> for none.</summary>
    public int FixedLength { get; init; }

    /// <summary>Gets the character text is padded with when <see cref="FixedLength"/> is set.</summary>
    public char Padding { get; init; } = ' ';

    /// <summary>Gets a value indicating whether a scalar collection becomes a list of single-element items.</summary>
    public bool AsList { get; init; }

    /// <summary>Gets a value indicating whether the member may be absent from an incoming item list.</summary>
    public bool Optional { get; init; }

    /// <summary>Gets the converter pinned to this member, or <see langword="null"/> to use the type's own.</summary>
    public ISecsConverter? Converter { get; init; }

    /// <summary>Gets the accessor that reads the member from an instance.</summary>
    public required Func<object, object?> Get { get; init; }

    /// <summary>Gets the accessor that writes the member, or <see langword="null"/> when it is constructor-only.</summary>
    public Action<object, object?>? Set { get; init; }

    /// <summary>Gets this member's index in the constructor's parameter list, or <c>-1</c> when it is not a parameter.</summary>
    /// <remarks>
    /// How a positional <see langword="record"/> is deserialised: its properties have no usable
    /// setter, so the values are collected and passed to the constructor instead.
    /// </remarks>
    public int ConstructorParameterIndex { get; init; } = -1;
}

/// <summary>
/// Everything the mapper needs to know about a type.
/// </summary>
public sealed class SecsTypeInfo
{
    /// <summary>
    /// The members a resolver reflects over. Used in <see cref="DynamicallyAccessedMembersAttribute"/>
    /// annotations so the trimmer keeps what the reflection path will look for.
    /// </summary>
    internal const DynamicallyAccessedMemberTypes MappedMembers =
        DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicFields
        | DynamicallyAccessedMemberTypes.PublicConstructors;

    /// <summary>Gets the type this contract describes.</summary>
    public required Type Type { get; init; }

    /// <summary>Gets the type's <see cref="SecsMessageAttribute"/>, when it has one.</summary>
    public SecsMessageAttribute? Message { get; init; }

    /// <summary>Gets the mapped members, already in item order.</summary>
    public required IReadOnlyList<SecsPropertyInfo> Properties { get; init; }

    /// <summary>Gets a factory that creates an empty instance, when the type has a parameterless constructor.</summary>
    public Func<object>? CreateInstance { get; init; }

    /// <summary>Gets a factory that creates an instance from constructor arguments, when the type needs them.</summary>
    public Func<object?[], object>? CreateWithArguments { get; init; }

    /// <summary>Gets the number of arguments <see cref="CreateWithArguments"/> expects.</summary>
    public int ConstructorParameterCount { get; init; }

    /// <summary>Gets a value indicating whether this type can be deserialised at all.</summary>
    public bool CanDeserialize => CreateInstance is not null || CreateWithArguments is not null;
}

/// <summary>
/// Produces the mapping contract for a type.
/// </summary>
/// <remarks>
/// <para>
/// The seam between the two ways a type's shape can be discovered. The source generator reads
/// declaration order from the syntax tree - exact, allocation-free and AOT-safe. Reflection
/// reads it from metadata, which is right in practice but cannot be guaranteed by the runtime
/// for a type split across partial files or spread over a base class.
/// </para>
/// <para>
/// Both produce the same <see cref="SecsTypeInfo"/>, so adding the generator later changes
/// performance and trimmability, never behaviour.
/// </para>
/// </remarks>
public interface ISecsTypeInfoResolver
{
    /// <summary>Gets the contract for a type, or <see langword="null"/> when this resolver has none.</summary>
    /// <param name="type">The type to describe.</param>
    /// <param name="options">The serializer options, which carry the ordering policy and converters.</param>
    /// <returns>The contract, or <see langword="null"/> to let the next resolver try.</returns>
    SecsTypeInfo? GetTypeInfo(Type type, SecsSerializerOptions options);
}
