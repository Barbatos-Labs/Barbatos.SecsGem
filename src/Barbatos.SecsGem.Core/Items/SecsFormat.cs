// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

/// <summary>
/// The SECS-II item format, as defined by SEMI E5.
/// </summary>
/// <remarks>
/// The values are the six format bits exactly as they sit in the wire format's item header byte,
/// which is <c>(format &lt;&lt; 2) | lengthByteCount</c>. Keeping the enum in wire units means
/// encoding and decoding are a shift, never a lookup table.
/// </remarks>
public enum SecsFormat
{
    /// <summary>A list of sub-items. The only format whose <see cref="SecsItem.Count"/> counts items rather than bytes.</summary>
    List = 0b_0000_00,

    /// <summary>Binary data - an opaque byte string.</summary>
    Binary = 0b_0010_00,

    /// <summary>One byte per boolean; any non-zero byte reads as <see langword="true"/>.</summary>
    Boolean = 0b_0010_01,

    /// <summary>ASCII text.</summary>
    ASCII = 0b_0100_00,

    /// <summary>JIS-8 text.</summary>
    JIS8 = 0b_0100_01,

    /// <summary>Signed 8-byte integer.</summary>
    I8 = 0b_0110_00,

    /// <summary>Signed 1-byte integer.</summary>
    I1 = 0b_0110_01,

    /// <summary>Signed 2-byte integer.</summary>
    I2 = 0b_0110_10,

    /// <summary>Signed 4-byte integer.</summary>
    I4 = 0b_0111_00,

    /// <summary>8-byte IEEE 754 floating point.</summary>
    F8 = 0b_1000_00,

    /// <summary>4-byte IEEE 754 floating point.</summary>
    F4 = 0b_1001_00,

    /// <summary>Unsigned 8-byte integer.</summary>
    U8 = 0b_1010_00,

    /// <summary>Unsigned 1-byte integer.</summary>
    U1 = 0b_1010_01,

    /// <summary>Unsigned 2-byte integer.</summary>
    U2 = 0b_1010_10,

    /// <summary>Unsigned 4-byte integer.</summary>
    U4 = 0b_1011_00,
}

/// <summary>
/// Helpers for <see cref="SecsFormat"/>.
/// </summary>
public static class SecsFormatExtensions
{
    /// <summary>Gets the canonical name of a format, without the allocation <see cref="Enum.ToString()"/> would cost.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The format's name, for example <c>"U4"</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined format.</exception>
    public static string GetName(this SecsFormat format) => format switch
    {
        SecsFormat.List => nameof(SecsFormat.List),
        SecsFormat.Binary => nameof(SecsFormat.Binary),
        SecsFormat.Boolean => nameof(SecsFormat.Boolean),
        SecsFormat.ASCII => nameof(SecsFormat.ASCII),
        SecsFormat.JIS8 => nameof(SecsFormat.JIS8),
        SecsFormat.I8 => nameof(SecsFormat.I8),
        SecsFormat.I1 => nameof(SecsFormat.I1),
        SecsFormat.I2 => nameof(SecsFormat.I2),
        SecsFormat.I4 => nameof(SecsFormat.I4),
        SecsFormat.F8 => nameof(SecsFormat.F8),
        SecsFormat.F4 => nameof(SecsFormat.F4),
        SecsFormat.U8 => nameof(SecsFormat.U8),
        SecsFormat.U1 => nameof(SecsFormat.U1),
        SecsFormat.U2 => nameof(SecsFormat.U2),
        SecsFormat.U4 => nameof(SecsFormat.U4),
        _ => throw new ArgumentOutOfRangeException(nameof(format), (int)format, "Not a SECS-II item format."),
    };

    /// <summary>Gets the size in bytes of one element of <paramref name="format"/>, or <c>1</c> for text and binary.</summary>
    /// <param name="format">The format.</param>
    /// <returns>The element size in bytes; <c>0</c> for <see cref="SecsFormat.List"/>.</returns>
    public static int GetElementSize(this SecsFormat format) => format switch
    {
        SecsFormat.List => 0,
        SecsFormat.I2 or SecsFormat.U2 => 2,
        SecsFormat.I4 or SecsFormat.U4 or SecsFormat.F4 => 4,
        SecsFormat.I8 or SecsFormat.U8 or SecsFormat.F8 => 8,
        _ => 1,
    };

    /// <summary>Gets a value indicating whether <paramref name="format"/> carries text.</summary>
    /// <param name="format">The format.</param>
    /// <returns><see langword="true"/> for <see cref="SecsFormat.ASCII"/> and <see cref="SecsFormat.JIS8"/>.</returns>
    public static bool IsText(this SecsFormat format)
        => format is SecsFormat.ASCII or SecsFormat.JIS8;

    /// <summary>Tries to parse a format name, accepting both the enum names and the SML spellings (<c>A</c>, <c>L</c>, <c>B</c>, <c>BOOLEAN</c>, ...).</summary>
    /// <param name="name">The name to parse.</param>
    /// <param name="format">The parsed format.</param>
    /// <returns><see langword="true"/> if <paramref name="name"/> was recognised.</returns>
    public static bool TryParse(ReadOnlySpan<char> name, out SecsFormat format)
    {
        format = default;
        if (name.IsEmpty)
        {
            return false;
        }

        // Ordinal-ignore-case: SML in the wild is written in every casing imaginable.
        if (Eq(name, "L") || Eq(name, "List")) { format = SecsFormat.List; return true; }
        if (Eq(name, "A") || Eq(name, "ASCII")) { format = SecsFormat.ASCII; return true; }
        if (Eq(name, "J") || Eq(name, "JIS8")) { format = SecsFormat.JIS8; return true; }
        if (Eq(name, "B") || Eq(name, "Binary")) { format = SecsFormat.Binary; return true; }
        if (Eq(name, "BOOLEAN") || Eq(name, "Bool")) { format = SecsFormat.Boolean; return true; }
        if (Eq(name, "I1")) { format = SecsFormat.I1; return true; }
        if (Eq(name, "I2")) { format = SecsFormat.I2; return true; }
        if (Eq(name, "I4")) { format = SecsFormat.I4; return true; }
        if (Eq(name, "I8")) { format = SecsFormat.I8; return true; }
        if (Eq(name, "U1")) { format = SecsFormat.U1; return true; }
        if (Eq(name, "U2")) { format = SecsFormat.U2; return true; }
        if (Eq(name, "U4")) { format = SecsFormat.U4; return true; }
        if (Eq(name, "U8")) { format = SecsFormat.U8; return true; }
        if (Eq(name, "F4")) { format = SecsFormat.F4; return true; }
        if (Eq(name, "F8")) { format = SecsFormat.F8; return true; }
        return false;

        static bool Eq(ReadOnlySpan<char> value, string other)
            => value.Equals(other, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gets the short SML spelling of a format - <c>L</c>, <c>A</c>, <c>B</c>, <c>BOOLEAN</c>, <c>U4</c>, ...</summary>
    /// <param name="format">The format.</param>
    /// <returns>The SML token for <paramref name="format"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is not a defined format.</exception>
    public static string ToSmlToken(this SecsFormat format) => format switch
    {
        SecsFormat.List => "L",
        SecsFormat.Binary => "B",
        SecsFormat.Boolean => "BOOLEAN",
        SecsFormat.ASCII => "A",
        SecsFormat.JIS8 => "J",
        _ => format.GetName(),
    };
}
