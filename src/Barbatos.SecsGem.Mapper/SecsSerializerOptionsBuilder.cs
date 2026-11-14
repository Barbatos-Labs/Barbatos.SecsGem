// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Mapping;

/// <summary>
/// The mutable form of <see cref="SecsSerializerOptions"/>, for configuration and for binding.
/// </summary>
/// <remarks>
/// <see cref="SecsSerializerOptions"/> is immutable so it can be shared and so its contract
/// cache is safe to read from any thread. That makes it a poor fit for
/// <c>IOptions&lt;T&gt;</c>, which wants to set properties one at a time. This type is the
/// bridge: bind or configure it, then <see cref="Build"/> the immutable options once.
/// </remarks>
public sealed class SecsSerializerOptionsBuilder
{
    /// <summary>Gets or sets the format used for <see cref="string"/> members.</summary>
    public SecsFormat DefaultTextFormat { get; set; } = SecsFormat.ASCII;

    /// <summary>Gets or sets how a <see cref="DateTime"/> is written.</summary>
    public SecsDateTimeFormat DateTimeFormat { get; set; } = SecsDateTimeFormat.Timestamp16;

    /// <summary>Gets or sets what a <see langword="null"/> member becomes.</summary>
    public SecsNullHandling NullHandling { get; set; } = SecsNullHandling.EmptyItem;

    /// <summary>Gets or sets where member order comes from.</summary>
    public SecsPropertyOrdering PropertyOrdering { get; set; } = SecsPropertyOrdering.Convention;

    /// <summary>Gets or sets a value indicating whether a mismatch between item tree and type is an error.</summary>
    public bool Strict { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether numeric members accept an ASCII item.</summary>
    public bool AllowStringToNumber { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether member names are written to <see cref="SecsItem.Label"/>.</summary>
    public bool WritePropertyLabels { get; set; } = true;

    /// <summary>Gets the converters to place ahead of the built-in ones.</summary>
    public IList<ISecsConverter> Converters { get; } = [];

    /// <summary>Gets or sets the resolver chain. <see langword="null"/> uses <see cref="SecsTypeInfoResolver.Default"/>.</summary>
    public ISecsTypeInfoResolver? TypeInfoResolver { get; set; }

    /// <summary>Builds the immutable options.</summary>
    /// <param name="additionalConverters">Converters resolved from the container, placed first.</param>
    /// <returns>The options.</returns>
    public SecsSerializerOptions Build(IEnumerable<ISecsConverter>? additionalConverters = null)
    {
        List<ISecsConverter> converters = [];
        if (additionalConverters is not null)
        {
            converters.AddRange(additionalConverters);
        }

        converters.AddRange(Converters);
        converters.AddRange(SecsBuiltInConverters.All);

        return new SecsSerializerOptions
        {
            DefaultTextFormat = DefaultTextFormat,
            DateTimeFormat = DateTimeFormat,
            NullHandling = NullHandling,
            PropertyOrdering = PropertyOrdering,
            Strict = Strict,
            AllowStringToNumber = AllowStringToNumber,
            WritePropertyLabels = WritePropertyLabels,
            Converters = converters,
            TypeInfoResolver = TypeInfoResolver ?? SecsTypeInfoResolver.Default,
        };
    }
}
