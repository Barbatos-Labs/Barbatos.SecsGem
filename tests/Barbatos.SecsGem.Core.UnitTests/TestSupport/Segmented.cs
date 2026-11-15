// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;

namespace Barbatos.SecsGem.Core.UnitTests;

/// <summary>
/// Builds a <see cref="ReadOnlySequence{T}"/> out of several segments, so a decoder can be
/// handed the same bytes cut up in every possible way.
/// </summary>
/// <remarks>
/// A single-segment sequence is the easy case and the one a naive decoder passes. Real traffic
/// off a socket arrives as a chain of pooled blocks, and a value that straddles two of them is
/// where a decoder that reaches for <c>FirstSpan</c> quietly returns the wrong number. This
/// helper is how the item codec gets tested against that on purpose rather than by luck.
/// </remarks>
internal static class Segmented
{
    /// <summary>Builds a sequence whose segments are all at most <paramref name="segmentSize"/> bytes.</summary>
    /// <param name="data">The bytes.</param>
    /// <param name="segmentSize">The largest segment. <c>1</c> puts every byte in its own segment.</param>
    /// <returns>The sequence.</returns>
    public static ReadOnlySequence<byte> Create(ReadOnlySpan<byte> data, int segmentSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(segmentSize, 1);

        if (data.IsEmpty)
        {
            return ReadOnlySequence<byte>.Empty;
        }

        if (segmentSize >= data.Length)
        {
            return new ReadOnlySequence<byte>(data.ToArray());
        }

        Segment? first = null;
        Segment? last = null;

        for (var offset = 0; offset < data.Length; offset += segmentSize)
        {
            var take = Math.Min(segmentSize, data.Length - offset);
            var block = data.Slice(offset, take).ToArray();

            if (first is null)
            {
                first = new Segment(block, runningIndex: 0);
                last = first;
            }
            else
            {
                last = last!.Append(block);
            }
        }

        return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
    }

    /// <summary>The segment sizes worth trying for a buffer of a given length.</summary>
    /// <param name="length">The buffer length.</param>
    /// <returns>Every size from one byte per segment up to the whole buffer in one.</returns>
    /// <remarks>
    /// Exhaustive up to 8 bytes per segment - which is where the multi-byte element formats live,
    /// F8 and I8 being 8 bytes each - then a handful of larger sizes and finally the contiguous
    /// case.
    /// </remarks>
    public static IEnumerable<int> SegmentSizes(int length)
    {
        var sizes = new SortedSet<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 13, 16, 64, length };
        return sizes.Where(size => size >= 1 && size <= Math.Max(length, 1));
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory, RunningIndex + Memory.Length);
            Next = next;
            return next;
        }
    }
}
