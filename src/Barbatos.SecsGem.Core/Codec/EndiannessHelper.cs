// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

/// <summary>
/// Swaps multi-byte values between SECS-II wire order (big-endian) and host order.
/// </summary>
/// <remarks>
/// <para>
/// SECS-II is big-endian. On a little-endian host every multi-byte element needs reversing;
/// on a big-endian host the wire order <em>is</em> the host order and this becomes a no-op.
/// secs4net instead throws <see cref="PlatformNotSupportedException"/> from a static
/// constructor on big-endian hardware, which rules out s390x and any future big-endian target.
/// </para>
/// <para>
/// The operation is its own inverse, so one method serves both directions.
/// </para>
/// </remarks>
internal static class EndiannessHelper
{
    /// <summary>
    /// Reverses the byte order of every element of <paramref name="values"/> in place, when the
    /// host needs it.
    /// </summary>
    /// <typeparam name="T">An unmanaged element type of size 1, 2, 4 or 8 bytes.</typeparam>
    /// <param name="values">The elements to swap.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void SwapIfNeeded<T>(Span<T> values)
        where T : unmanaged
    {
        // Size 1 covers byte, sbyte and bool: nothing to swap whatever the host.
        if (!BitConverter.IsLittleEndian || sizeof(T) == 1 || values.IsEmpty)
        {
            return;
        }

        switch (sizeof(T))
        {
            case 2:
                Swap16(MemoryMarshal.Cast<T, ushort>(values));
                break;
            case 4:
                Swap32(MemoryMarshal.Cast<T, uint>(values));
                break;
            case 8:
                Swap64(MemoryMarshal.Cast<T, ulong>(values));
                break;
            default:
                ThrowUnsupportedSize(sizeof(T));
                break;
        }
    }

    private static void Swap16(Span<ushort> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReverseEndianness(values[i]);
        }
    }

    private static void Swap32(Span<uint> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReverseEndianness(values[i]);
        }
    }

    private static void Swap64(Span<ulong> values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReverseEndianness(values[i]);
        }
    }

    [DoesNotReturn]
    private static void ThrowUnsupportedSize(int size)
        => throw new NotSupportedException($"SECS-II has no {size}-byte element format.");
}
