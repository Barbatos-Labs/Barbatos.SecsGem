// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// How a <see cref="DateTime"/> is written to SECS-II.
/// </summary>
public enum SecsDateTimeFormat
{
    /// <summary>16-character ASCII <c>YYYYMMDDhhmmsscc</c> - the GEM extended form.</summary>
    Timestamp16,

    /// <summary>12-character ASCII <c>YYMMDDhhmmss</c> - the original SECS-II form.</summary>
    Timestamp12,

    /// <summary>Round-trippable ISO 8601 ASCII text.</summary>
    Iso8601,

    /// <summary>A single <see cref="SecsFormat.I8"/> element holding ticks.</summary>
    Ticks,
}

/// <summary>
/// What a <see langword="null"/> member becomes on the wire.
/// </summary>
public enum SecsNullHandling
{
    /// <summary>An empty item of the member's own format - <c>&lt;A [0]&gt;</c>, <c>&lt;U4 [0]&gt;</c>.</summary>
    EmptyItem,

    /// <summary>An empty list - <c>&lt;L [0]&gt;</c> - whatever the member's format.</summary>
    EmptyList,

    /// <summary>Refuse to serialise.</summary>
    Throw,
}

/// <summary>
/// Where a type's member order comes from.
/// </summary>
public enum SecsPropertyOrdering
{
    /// <summary>
    /// Declaration order: from the source generator when it has seen the type, from constructor
    /// parameter order for a positional record, and from reflection metadata otherwise.
    /// </summary>
    Convention,

    /// <summary>
    /// Only <see cref="SecsItemAttribute.Order"/> counts. A mapped member without one is an error.
    /// </summary>
    Explicit,
}

/// <summary>
/// Controls how C# objects map to SECS-II item trees.
/// </summary>
/// <remarks>
/// Immutable and shareable, like <c>JsonSerializerOptions</c>. Build one per application and
/// keep it: the type contracts it caches are what make the second message cheaper than the first.
/// </remarks>
public sealed class SecsSerializerOptions
{
    private readonly Dictionary<Type, ISecsConverter?> _converterCache = [];

    /// <summary>The default options: convention ordering, ASCII text, 16-character timestamps, reflection mapping.</summary>
    public static SecsSerializerOptions Default { get; } = new();

    /// <summary>Gets the resolver that produces a type's mapping contract.</summary>
    /// <remarks>
    /// Defaults to <see cref="SecsTypeInfoResolver.Default"/>, which tries the generated
    /// contracts first and falls back to reflection.
    /// </remarks>
    public ISecsTypeInfoResolver TypeInfoResolver { get; init; } = SecsTypeInfoResolver.Default;

    /// <summary>Gets the converters consulted before the built-in rules, in order.</summary>
    public IReadOnlyList<ISecsConverter> Converters { get; init; } = SecsBuiltInConverters.All;

    /// <summary>Gets the format used for <see cref="string"/> members. Defaults to <see cref="SecsFormat.ASCII"/>.</summary>
    public SecsFormat DefaultTextFormat { get; init; } = SecsFormat.ASCII;

    /// <summary>Gets how a <see cref="DateTime"/> is written. Defaults to <see cref="SecsDateTimeFormat.Timestamp16"/>.</summary>
    public SecsDateTimeFormat DateTimeFormat { get; init; } = SecsDateTimeFormat.Timestamp16;

    /// <summary>Gets what a <see langword="null"/> member becomes. Defaults to <see cref="SecsNullHandling.EmptyItem"/>.</summary>
    public SecsNullHandling NullHandling { get; init; } = SecsNullHandling.EmptyItem;

    /// <summary>Gets where member order comes from. Defaults to <see cref="SecsPropertyOrdering.Convention"/>.</summary>
    public SecsPropertyOrdering PropertyOrdering { get; init; } = SecsPropertyOrdering.Convention;

    /// <summary>
    /// Gets a value indicating whether a mismatch between the item tree and the target type is
    /// an error. Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Strict is right for a host talking to equipment it was written against. Turn it off when
    /// the other end is a machine from 2004 whose item list is a surprise every firmware
    /// revision: missing trailing items then leave their members at their defaults and extra
    /// items are ignored.
    /// </remarks>
    public bool Strict { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether numeric members accept an ASCII item on the way in.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Equipment sending <c>&lt;A '1234'&gt;</c> where the manual promises <c>&lt;U4 1234&gt;</c>
    /// is common enough that refusing it by default would cost more than it gains.
    /// </remarks>
    public bool AllowStringToNumber { get; init; } = true;

    /// <summary>
    /// Gets a value indicating whether a member's name is written to <see cref="SecsItem.Label"/>.
    /// Defaults to <see langword="true"/>.
    /// </summary>
    /// <remarks>
    /// Costs nothing on the wire - the binary codec ignores labels - and makes the SML, XML and
    /// INI renderings of a mapped object self-describing.
    /// </remarks>
    public bool WritePropertyLabels { get; init; } = true;

    /// <summary>Finds the converter for a type, or <see langword="null"/> when the built-in rules apply.</summary>
    /// <param name="type">The type to convert.</param>
    /// <returns>The converter, or <see langword="null"/>.</returns>
    public ISecsConverter? GetConverter(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        lock (_converterCache)
        {
            if (_converterCache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            ISecsConverter? found = null;
            foreach (var converter in Converters)
            {
                if (converter.CanConvert(type))
                {
                    found = converter;
                    break;
                }
            }

            _converterCache[type] = found;
            return found;
        }
    }
}
