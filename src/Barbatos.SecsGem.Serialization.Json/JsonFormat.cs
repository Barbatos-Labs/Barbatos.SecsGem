// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Json;

/// <summary>
/// The JSON shape Barbatos.SecsGem reads and writes.
/// </summary>
/// <remarks>
/// <para>
/// An item is an object with one property, named after its format - the shape other SECS
/// tooling already uses, so an existing corpus stays readable:
/// </para>
/// <code>
/// [
///   {
///     "name": "GetEquipmentStatus",
///     "s": 1, "f": 3, "w": true,
///     "item": { "L": [ { "A": "MDLN01", "$label": "MDLN" }, { "U4": [1234] } ] }
///   }
/// ]
/// </code>
/// <para>
/// <c>$label</c> carries <see cref="SecsItem.Label"/>. The dollar sign keeps it clear of the
/// format names, so no future SECS-II format can collide with it.
/// </para>
/// <para>
/// Note what the descriptor does <em>not</em> claim: <see cref="SecsFormatCapabilities.ExactIntegers"/>.
/// A JSON number is an IEEE 754 double to most readers, which silently rounds a
/// <see cref="SecsFormat.U8"/> above 2^53. <see cref="SecsWriteOptions.WriteLargeIntegersAsStrings"/>
/// is on by default to keep those exact, and the transcoder warns when it is turned off.
/// </para>
/// </remarks>
public static class JsonFormat
{
    /// <summary>Describes the JSON format.</summary>
    public static SecsFormatDescriptor Descriptor { get; } = new(
        SecsFormatIds.Json,
        "JSON",
        [".json"],
        "application/json",
        SecsFormatCapabilities.MultipleMessages
        | SecsFormatCapabilities.MessageNames
        | SecsFormatCapabilities.ItemLabels
        | SecsFormatCapabilities.BinaryPayload
        | SecsFormatCapabilities.Indentation);

    /// <summary>The property that carries <see cref="SecsMessage.Name"/>.</summary>
    public const string NameProperty = "name";

    /// <summary>The property that carries the stream number.</summary>
    public const string StreamProperty = "s";

    /// <summary>The property that carries the function number.</summary>
    public const string FunctionProperty = "f";

    /// <summary>The property that carries the W-bit.</summary>
    public const string ReplyExpectedProperty = "w";

    /// <summary>The property that carries the root item.</summary>
    public const string ItemProperty = "item";

    /// <summary>The property that carries <see cref="SecsItem.Label"/>.</summary>
    public const string LabelProperty = "$label";
}
