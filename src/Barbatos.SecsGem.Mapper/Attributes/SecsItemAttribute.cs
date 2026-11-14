// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// Overrides the convention for one member.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is required. Left alone, a member maps by convention: its position comes from
/// declaration order and its <see cref="SecsFormat"/> from its C# type. Reach for this attribute
/// only where the equipment disagrees with the convention - and equipment often does:
/// </para>
/// <code>
/// public sealed class EquipmentConstant
/// {
///     // The equipment wants the id as U4 even though the natural C# type is int.
///     [SecsItem(SecsFormat.U4, Name = "ECID")]
///     public int Id { get; set; }
///
///     // GEM wants a fixed-width A[16] timestamp, space padded.
///     [SecsItem(Length = 16)]
///     public string Timestamp { get; set; } = string.Empty;
///
///     // A list of single-element items, not one array item.
///     [SecsItem(AsList = true)]
///     public int[] Limits { get; set; } = [];
/// }
/// </code>
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false, Inherited = true)]
public sealed class SecsItemAttribute : Attribute
{
    /// <summary>Initialises a new instance that changes nothing but what its properties set.</summary>
    public SecsItemAttribute()
    {
    }

    /// <summary>Initialises a new instance that pins the member's SECS-II format.</summary>
    /// <param name="format">The format to use instead of the inferred one.</param>
    public SecsItemAttribute(SecsFormat format) => Format = format;

    /// <summary>Gets or sets the format to use instead of the one inferred from the C# type.</summary>
    public SecsFormat? Format { get; set; }

    /// <summary>
    /// Gets or sets this member's position in the item list, lowest first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unset, position comes from declaration order - from the syntax tree when the source
    /// generator can see the type, from the constructor's parameter order for a positional
    /// record, and from reflection metadata otherwise.
    /// </para>
    /// <para>
    /// Set it on <em>every</em> mapped member or on none. A type where some members are ordered
    /// and others are not is the one shape that reads differently depending on which resolver
    /// ran, and analyzer BSG001 reports it.
    /// </para>
    /// </remarks>
    public int Order { get; set; } = -1;

    /// <summary>Gets or sets the item label, carried by SML, JSON, XML and INI.</summary>
    /// <remarks>Defaults to the member's own name when <see cref="SecsSerializerOptions.WritePropertyLabels"/> is on.</remarks>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a fixed element count. Text is padded or truncated to it; other formats are
    /// checked against it.
    /// </summary>
    /// <remarks>
    /// GEM is full of fixed-width fields, and equipment that rejects anything else. <c>0</c>
    /// means no fixed length.
    /// </remarks>
    public int Length { get; set; }

    /// <summary>Gets or sets the character text is padded with when <see cref="Length"/> is set. Defaults to a space.</summary>
    public char Padding { get; set; } = ' ';

    /// <summary>
    /// Gets or sets a value indicating whether a collection of scalars becomes a list of
    /// single-element items rather than one array item.
    /// </summary>
    /// <remarks>
    /// By convention <c>int[] { 1, 2, 3 }</c> maps to <c>&lt;I4 [3] 1 2 3&gt;</c>, which is what
    /// SECS-II is for. Some equipment nonetheless expects
    /// <c>&lt;L [3] &lt;I4 [1] 1&gt; &lt;I4 [1] 2&gt; &lt;I4 [1] 3&gt;&gt;</c>, and this is how
    /// you say so.
    /// </remarks>
    public bool AsList { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the member may be missing from an incoming item
    /// list without that being an error.
    /// </summary>
    /// <remarks>
    /// For trailing members that newer equipment sends and older equipment does not.
    /// </remarks>
    public bool Optional { get; set; }

    /// <summary>Gets or sets a converter type for this member, overriding any registered for the type.</summary>
    /// <remarks>Must implement <see cref="ISecsConverter"/> and have a public parameterless constructor.</remarks>
    public Type? Converter { get; set; }
}
