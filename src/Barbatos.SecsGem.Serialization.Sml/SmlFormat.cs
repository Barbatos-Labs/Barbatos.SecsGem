// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Sml;

/// <summary>
/// SML - SECS Message Language - the text notation the industry reads SECS messages in.
/// </summary>
/// <remarks>
/// <para>
/// The shape written here is wire-compatible with the SML other tools emit, so an existing
/// corpus of <c>.sml</c> fixtures stays readable:
/// </para>
/// <code>
/// GetEquipmentStatus: 'S1F3' W
/// &lt;L [2]
///   &lt;A [6] 'MDLN01'&gt;   // MDLN
///   &lt;U4 [1] 1234&gt;
/// &gt;
/// .
/// </code>
/// <para>
/// Two additions, both of which other parsers ignore because they are comments:
/// <c>//</c> starts a comment, and a comment immediately after an item becomes that item's
/// <see cref="SecsItem.Label"/>. That is what lets a name survive the trip to INI and XML and
/// back again.
/// </para>
/// </remarks>
public static class SmlFormat
{
    /// <summary>Describes the SML format.</summary>
    public static SecsFormatDescriptor Descriptor { get; } = new(
        SecsFormatIds.Sml,
        "SML (SECS Message Language)",
        [".sml"],
        "text/plain",
        SecsFormatCapabilities.MultipleMessages
        | SecsFormatCapabilities.MessageNames
        | SecsFormatCapabilities.ItemLabels
        | SecsFormatCapabilities.Comments
        | SecsFormatCapabilities.ExactIntegers
        | SecsFormatCapabilities.BinaryPayload
        | SecsFormatCapabilities.Indentation);

    /// <summary>The character sequence that ends a message.</summary>
    public const string MessageTerminator = ".";
}
