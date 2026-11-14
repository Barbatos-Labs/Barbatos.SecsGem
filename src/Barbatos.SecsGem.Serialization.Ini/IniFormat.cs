// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;

namespace Barbatos.SecsGem.Serialization.Ini;

/// <summary>
/// The INI shape Barbatos.SecsGem reads and writes: one section per message, and an item tree
/// flattened into path-addressed keys.
/// </summary>
/// <remarks>
/// <para>
/// INI has no nesting, so the tree is carried in the key instead. <c>item.1</c> is the root,
/// <c>item.1.2</c> is its second child, and so on - the same addressing an engineer would use
/// out loud:
/// </para>
/// <code>
/// ; Barbatos.SecsGem
/// [1]
/// name = GetEquipmentStatus
/// stream = 1
/// function = 3
/// replyExpected = true
/// item.1 = L[2]
/// item.1.1 = A[6] 'MDLN01'
/// item.1.1.$label = MDLN
/// item.1.2 = U4[1] 1234
/// </code>
/// <para>
/// This is the format to reach for when a value has to be addressable by a path - a recipe
/// parameter a technician edits by hand, a diff in a code review, a <c>sed</c> in a shell
/// script. It is the only one of the four where "the second element of the third item" is
/// something you can grep for.
/// </para>
/// </remarks>
public static class IniFormat
{
    /// <summary>Describes the INI format.</summary>
    public static SecsFormatDescriptor Descriptor { get; } = new(
        SecsFormatIds.Ini,
        "INI (path-addressed)",
        [".ini"],
        "text/plain",
        SecsFormatCapabilities.MultipleMessages
        | SecsFormatCapabilities.MessageNames
        | SecsFormatCapabilities.ItemLabels
        | SecsFormatCapabilities.Comments
        | SecsFormatCapabilities.ExactIntegers
        | SecsFormatCapabilities.BinaryPayload);

    /// <summary>The key carrying <see cref="SecsMessage.Name"/>.</summary>
    public const string NameKey = "name";

    /// <summary>The key carrying the stream number.</summary>
    public const string StreamKey = "stream";

    /// <summary>The key carrying the function number.</summary>
    public const string FunctionKey = "function";

    /// <summary>The key carrying the W-bit.</summary>
    public const string ReplyExpectedKey = "replyExpected";

    /// <summary>The prefix of every item key.</summary>
    public const string ItemPrefix = "item.";

    /// <summary>The key suffix carrying <see cref="SecsItem.Label"/>.</summary>
    /// <remarks>
    /// The dollar sign is what keeps it unambiguous: a path segment is always a number, so
    /// <c>$label</c> can never be mistaken for a child index.
    /// </remarks>
    public const string LabelSuffix = ".$label";

    /// <summary>The path of the root item.</summary>
    public const string RootPath = "1";
}
