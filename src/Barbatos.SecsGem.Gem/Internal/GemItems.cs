// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// Reading and writing the few item shapes both ends of a GEM conversation need.
/// </summary>
/// <remarks>
/// These were private to <see cref="GemEquipment"/> until there was a host in this assembly to
/// read the same fields from the other direction. Tolerating whatever numeric format the other
/// side chose is a decision worth making once: a host that only understood <c>U4</c> SVIDs would
/// fail against a machine that answers in <c>U1</c>, and the failure would look like a missing
/// variable rather than like a parsing bug.
/// </remarks>
internal static class GemItems
{
    /// <summary>Reads an identifier in whatever numeric format the other side chose.</summary>
    /// <param name="item">The item.</param>
    /// <param name="value">The identifier.</param>
    /// <returns><see langword="false"/> when the item is empty, negative, or not a number at all.</returns>
    internal static bool TryReadId(SecsItem item, out ulong value)
    {
        value = 0;

        if (item.Count == 0)
        {
            return false;
        }

        switch (item.Format)
        {
            case SecsFormat.U1:
                value = item.FirstValueOrDefault<byte>();
                return true;
            case SecsFormat.U2:
                value = item.FirstValueOrDefault<ushort>();
                return true;
            case SecsFormat.U4:
                value = item.FirstValueOrDefault<uint>();
                return true;
            case SecsFormat.U8:
                value = item.FirstValueOrDefault<ulong>();
                return true;
            case SecsFormat.I1:
                return NonNegative(item.FirstValueOrDefault<sbyte>(), out value);
            case SecsFormat.I2:
                return NonNegative(item.FirstValueOrDefault<short>(), out value);
            case SecsFormat.I4:
                return NonNegative(item.FirstValueOrDefault<int>(), out value);
            case SecsFormat.I8:
                return NonNegative(item.FirstValueOrDefault<long>(), out value);
            case SecsFormat.ASCII:
            case SecsFormat.JIS8:
                return ulong.TryParse(item.GetString().Trim(), CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }

        static bool NonNegative(long candidate, out ulong value)
        {
            value = candidate < 0 ? 0 : (ulong)candidate;
            return candidate >= 0;
        }
    }

    /// <summary>Reads an acknowledgement code.</summary>
    /// <param name="item">The item, which may be <see langword="null"/>.</param>
    /// <param name="code">The code.</param>
    /// <returns><see langword="false"/> when there is no code to read.</returns>
    /// <remarks>
    /// Separate from <see cref="TryReadId"/> because an acknowledgement code is a <c>B[1]</c> and a
    /// binary item is not a number as far as that method is concerned - deliberately, since
    /// reinterpreting a four-byte binary payload as an identifier would be a guess. Here the
    /// binary case is the normal one, and a numeric format is tolerated for a sender that sent
    /// <c>U1</c> instead.
    /// </remarks>
    internal static bool TryReadCode(SecsItem? item, out byte code)
    {
        code = 0;

        if (item is null || item.Count == 0)
        {
            return false;
        }

        if (item.Format == SecsFormat.Binary)
        {
            code = item.FirstValueOrDefault<byte>();
            return true;
        }

        if (TryReadId(item, out var value) && value <= byte.MaxValue)
        {
            code = (byte)value;
            return true;
        }

        return false;
    }

    /// <summary>Reads one of the timestamp formats E5 allows.</summary>
    /// <param name="text">TIME.</param>
    /// <param name="value">The instant, in UTC.</param>
    /// <returns><see langword="false"/> when the text is none of the three formats.</returns>
    internal static bool TryParseTimestamp(string text, out DateTime value)
    {
        var trimmed = text.Trim();
        string[] patterns = ["yyyyMMddHHmmssff", "yyyyMMddHHmmss", "yyMMddHHmmss"];

        foreach (var pattern in patterns)
        {
            if (DateTime.TryParseExact(
                trimmed,
                pattern,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>Writes an identifier in the narrowest unsigned format that holds it.</summary>
    /// <param name="id">The identifier.</param>
    /// <returns>A <c>U4</c>, or a <c>U8</c> when the value needs one.</returns>
    /// <remarks>
    /// <c>U4</c> rather than <c>U1</c> for small values on purpose. Every machine understands a
    /// <c>U4</c> identifier and it is what E30's examples use, so the narrowest format that *fits*
    /// would be trading interoperability for three bytes.
    /// </remarks>
    internal static SecsItem IdItem(ulong id)
        => id <= uint.MaxValue ? SecsItem.U4((uint)id) : SecsItem.U8(id);

    /// <summary>Writes a list of identifiers.</summary>
    /// <param name="ids">The identifiers. An empty list gives an empty list, which several GEM messages read as "all of them".</param>
    /// <returns>The list item.</returns>
    internal static SecsItem IdList(IReadOnlyList<ulong> ids)
    {
        var items = new SecsItem[ids.Count];

        for (var i = 0; i < ids.Count; i++)
        {
            items[i] = IdItem(ids[i]);
        }

        return SecsItem.L(items);
    }

    /// <summary>Reads blocks of free text, keeping every one of them.</summary>
    /// <param name="item">A text item, or a list of them.</param>
    /// <returns>The blocks, in order.</returns>
    /// <remarks>
    /// Neither trimmed nor filtered, which is what separates this from the identifier readers. A
    /// PPID is a fixed-width field whose padding is framing, and an empty one is not an
    /// identifier - so that reader trims and drops. A line of terminal text is what somebody typed
    /// for an operator to read: its trailing spaces are theirs, and a blank line between two
    /// paragraphs is the layout.
    /// </remarks>
    internal static List<string> ReadTextBlocks(SecsItem? item)
    {
        List<string> blocks = [];

        if (item is null)
        {
            return blocks;
        }

        if (!item.IsList)
        {
            if (item.Format.IsText())
            {
                blocks.Add(item.Count == 0 ? string.Empty : item.GetString());
            }

            return blocks;
        }

        foreach (var element in item.Items)
        {
            if (element.Format.IsText())
            {
                blocks.Add(element.Count == 0 ? string.Empty : element.GetString());
            }
        }

        return blocks;
    }

    /// <summary>Reads a text item, or an empty string when the item is not text.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The text, trimmed of the padding a fixed-width field carries.</returns>
    internal static string ReadText(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;
}
