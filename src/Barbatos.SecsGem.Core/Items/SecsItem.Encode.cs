// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

public partial class SecsItem
{
    /// <summary>The deepest item nesting this codec will encode or decode.</summary>
    /// <remarks>
    /// A bound on recursion, because the nesting depth of an incoming message is a number the
    /// peer chooses. Without it a few kilobytes of nothing but list headers overflows the stack -
    /// a denial of service that costs the sender nothing. No real GEM message nests past a dozen
    /// levels.
    /// </remarks>
    public const int MaxDepth = 256;

    /// <summary>
    /// Writes an item header: the format and length-byte count, then the length itself.
    /// </summary>
    /// <param name="format">The item format.</param>
    /// <param name="length">The byte count of the payload, or the item count for a list.</param>
    /// <param name="writer">The destination.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> does not fit in three bytes.</exception>
    /// <remarks>
    /// The wire format spends as few bytes on the length as it can - one, two or three - and
    /// records how many it used in the low two bits of the first byte.
    /// </remarks>
    private static void WriteItemHeader(SecsFormat format, int length, IBufferWriter<byte> writer)
    {
        var formatBits = (int)format << 2;
        var span = writer.GetSpan(4);

        if (length <= 0xFF)
        {
            span[0] = (byte)(formatBits | 1);
            span[1] = (byte)length;
            writer.Advance(2);
            return;
        }

        if (length <= 0xFF_FF)
        {
            span[0] = (byte)(formatBits | 2);
            span[1] = (byte)(length >> 8);
            span[2] = (byte)length;
            writer.Advance(3);
            return;
        }

        if (length <= 0xFF_FF_FF)
        {
            span[0] = (byte)(formatBits | 3);
            span[1] = (byte)(length >> 16);
            span[2] = (byte)(length >> 8);
            span[3] = (byte)length;
            writer.Advance(4);
            return;
        }

        throw new ArgumentOutOfRangeException(
            nameof(length),
            length,
            "A SECS-II item length is encoded in at most three bytes, so it cannot exceed 16,777,215.");
    }

    /// <summary>Writes the two-byte header of an empty item.</summary>
    /// <param name="format">The item format.</param>
    /// <param name="writer">The destination.</param>
    private static void WriteEmptyItemHeader(SecsFormat format, IBufferWriter<byte> writer)
    {
        var span = writer.GetSpan(2);
        span[0] = (byte)(((int)format << 2) | 1);
        span[1] = 0;
        writer.Advance(2);
    }
}
