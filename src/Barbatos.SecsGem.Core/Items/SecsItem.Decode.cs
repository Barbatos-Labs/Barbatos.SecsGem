// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem;

public partial class SecsItem
{
    /// <summary>
    /// Decodes one item - and, for a list, its whole subtree - from the front of
    /// <paramref name="source"/>.
    /// </summary>
    /// <param name="source">
    /// The bytes to read. Advanced past the item that was decoded, so successive calls walk a
    /// buffer holding several items.
    /// </param>
    /// <param name="options">Text encodings and limits. <see langword="null"/> uses <see cref="SecsCodecOptions.Default"/>.</param>
    /// <returns>The decoded item. Dispose it, or the message that owns it, to return pooled buffers.</returns>
    /// <exception cref="SecsDecodeException">
    /// The bytes are not a valid item: an undefined format, a truncated payload, a payload that
    /// is not a whole number of elements, a length past
    /// <see cref="SecsCodecOptions.MaxPayloadLength"/>, or nesting past <see cref="MaxDepth"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Every byte of the item is already in <paramref name="source"/>, so the whole tree is built
    /// with no state machine and no suspension. <paramref name="source"/> may be segmented in any
    /// way - a value that straddles two segments reads correctly - because the decoder never
    /// assumes the first span holds what it needs.
    /// </para>
    /// <para>
    /// The HSMS layer calls this on both of its decode paths. What differs between them is where
    /// the bytes came from and whether T8 was running while they arrived, not how the tree is
    /// built: on the fast path this reads straight out of the reader's buffer, and on the slow
    /// path out of a staging buffer that the frame was gathered into first. An HSMS frame
    /// announces its own length, so there is never a need to suspend part way through an item.
    /// </para>
    /// </remarks>
    public static SecsItem Decode(ref ReadOnlySequence<byte> source, SecsCodecOptions? options = null)
        => DecodeCore(ref source, options ?? SecsCodecOptions.Default, depth: 0);

    /// <summary>Decodes a single item from a contiguous buffer.</summary>
    /// <param name="source">The bytes to read.</param>
    /// <param name="options">Text encodings and limits. <see langword="null"/> uses <see cref="SecsCodecOptions.Default"/>.</param>
    /// <returns>The decoded item.</returns>
    /// <exception cref="SecsDecodeException">The bytes are not a valid item.</exception>
    public static SecsItem Decode(ReadOnlyMemory<byte> source, SecsCodecOptions? options = null)
    {
        var sequence = new ReadOnlySequence<byte>(source);
        return Decode(ref sequence, options);
    }

    /// <summary>Reads the format and length-byte count from an item's first header byte.</summary>
    /// <param name="headerByte">The item's first byte.</param>
    /// <param name="format">The item format.</param>
    /// <param name="lengthByteCount">How many bytes the length field occupies: 1, 2 or 3.</param>
    /// <returns><see langword="true"/> when both fields are valid.</returns>
    /// <remarks>
    /// Public because inspecting a capture one byte at a time needs exactly this, and because a
    /// framing that carries no length up front - SECS-I over a serial line - has to resume an item
    /// header across reads. The HSMS decoder does not: an HSMS frame announces its own length, so
    /// it gathers the whole frame and then calls <see cref="Decode(ReadOnlyMemory{byte}, SecsCodecOptions?)"/>.
    /// </remarks>
    public static bool TryReadItemHeaderByte(byte headerByte, out SecsFormat format, out byte lengthByteCount)
    {
        format = (SecsFormat)(headerByte >> 2);
        lengthByteCount = (byte)(headerByte & 0b11);
        return lengthByteCount is >= 1 and <= 3 && IsDefinedFormat(format);
    }

    /// <summary>Reads a big-endian item length from its 1, 2 or 3 length bytes.</summary>
    /// <param name="lengthBytes">The length bytes, in wire order.</param>
    /// <returns>The length.</returns>
    /// <remarks>Public for the same reason as <see cref="TryReadItemHeaderByte"/>.</remarks>
    public static int ReadItemLength(in ReadOnlySequence<byte> lengthBytes)
    {
        var count = (int)lengthBytes.Length;
        if (count is < 1 or > 3)
        {
            throw new SecsDecodeException($"An item length occupies 1 to 3 bytes, not {count}.");
        }

        // Right-align the big-endian bytes in a 4-byte window, then read it as one integer:
        // no per-byte shifting and no endianness assumption about the host.
        Span<byte> window = stackalloc byte[4];
        window.Clear();
        lengthBytes.CopyTo(window[(4 - count)..]);
        return (int)BinaryPrimitives.ReadUInt32BigEndian(window);
    }

    /// <summary>Gets a value indicating whether <paramref name="format"/> is one of the 15 defined formats.</summary>
    /// <param name="format">The value to check.</param>
    /// <returns><see langword="true"/> when the value is a real SECS-II format.</returns>
    /// <remarks>
    /// Cheaper than <see cref="Enum.IsDefined{TEnum}(TEnum)"/> and allocation-free, which matters
    /// because this runs once per item on every message received.
    /// </remarks>
    public static bool IsDefinedFormat(SecsFormat format) => format switch
    {
        SecsFormat.List or SecsFormat.Binary or SecsFormat.Boolean or SecsFormat.ASCII or SecsFormat.JIS8
            or SecsFormat.I1 or SecsFormat.I2 or SecsFormat.I4 or SecsFormat.I8
            or SecsFormat.U1 or SecsFormat.U2 or SecsFormat.U4 or SecsFormat.U8
            or SecsFormat.F4 or SecsFormat.F8 => true,
        _ => false,
    };

    /// <summary>Builds a value item from a decoded format and its raw payload bytes.</summary>
    /// <param name="format">The item format. Must not be <see cref="SecsFormat.List"/>.</param>
    /// <param name="payload">The payload, in wire (big-endian) order.</param>
    /// <param name="options">Text encodings and limits.</param>
    /// <returns>The decoded item.</returns>
    /// <exception cref="SecsDecodeException">The payload is not a whole number of elements, or the format is a list.</exception>
    /// <remarks>
    /// Public so that a decoder which assembles a payload itself - see
    /// <see cref="TryReadItemHeaderByte"/> for which ones do - can turn it into an item without
    /// re-synthesising the item header in front of it.
    /// </remarks>
    public static SecsItem DecodeValue(SecsFormat format, in ReadOnlySequence<byte> payload, SecsCodecOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return format switch
        {
            SecsFormat.ASCII or SecsFormat.JIS8 => DecodeText(format, payload, options),
            SecsFormat.Binary => DecodeArray<byte>(format, payload, options),
            SecsFormat.U1 => DecodeArray<byte>(format, payload, options),
            SecsFormat.Boolean => DecodeArray<bool>(format, payload, options),
            SecsFormat.I1 => DecodeArray<sbyte>(format, payload, options),
            SecsFormat.I2 => DecodeArray<short>(format, payload, options),
            SecsFormat.I4 => DecodeArray<int>(format, payload, options),
            SecsFormat.I8 => DecodeArray<long>(format, payload, options),
            SecsFormat.U2 => DecodeArray<ushort>(format, payload, options),
            SecsFormat.U4 => DecodeArray<uint>(format, payload, options),
            SecsFormat.U8 => DecodeArray<ulong>(format, payload, options),
            SecsFormat.F4 => DecodeArray<float>(format, payload, options),
            SecsFormat.F8 => DecodeArray<double>(format, payload, options),
            _ => throw new SecsDecodeException($"{format} is not a value format."),
        };
    }

    private static SecsItem DecodeCore(ref ReadOnlySequence<byte> source, SecsCodecOptions options, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new SecsDecodeException($"Item nesting is deeper than the {MaxDepth}-level limit.");
        }

        if (source.Length < 2)
        {
            throw new SecsDecodeException("Truncated item: an item header is at least two bytes.");
        }

        var firstSpan = source.FirstSpan;
        var headerByte = firstSpan.Length > 0 ? firstSpan[0] : ReadFirstByte(source);

        if (!TryReadItemHeaderByte(headerByte, out var format, out var lengthByteCount))
        {
            throw new SecsDecodeException($"Item header byte 0x{headerByte:X2} is not a valid format and length-byte count.");
        }

        source = source.Slice(1);
        if (source.Length < lengthByteCount)
        {
            throw new SecsDecodeException($"Truncated item: {lengthByteCount} length byte(s) expected, {source.Length} available.");
        }

        var lengthBytes = source.Slice(0, lengthByteCount);
        var length = ReadItemLength(lengthBytes);
        source = source.Slice(lengthByteCount);

        if (format == SecsFormat.List)
        {
            if (length == 0)
            {
                return Empty(SecsFormat.List);
            }

            // A child costs at least two bytes, so a count larger than half the remaining buffer
            // is impossible. Checking it here means a malicious count never sizes an array.
            if (length > source.Length / 2)
            {
                throw new SecsDecodeException(
                    $"A list claims {length} children but only {source.Length} byte(s) remain.");
            }

            var children = new SecsItem[length];
            for (var i = 0; i < length; i++)
            {
                children[i] = DecodeCore(ref source, options, depth + 1);
            }

            return new ListItem(children);
        }

        if (length > options.MaxPayloadLength)
        {
            throw new SecsDecodeException(
                $"A {format} payload of {length} bytes exceeds the configured limit of {options.MaxPayloadLength}.");
        }

        if (source.Length < length)
        {
            throw new SecsDecodeException($"Truncated {format} payload: {length} byte(s) expected, {source.Length} available.");
        }

        var payload = source.Slice(0, length);
        var item = DecodeValue(format, payload, options);
        source = source.Slice(length);
        return item;
    }

    private static byte ReadFirstByte(in ReadOnlySequence<byte> source)
    {
        Span<byte> one = stackalloc byte[1];
        source.Slice(0, 1).CopyTo(one);
        return one[0];
    }

    private static SecsItem DecodeText(SecsFormat format, in ReadOnlySequence<byte> payload, SecsCodecOptions options)
    {
        var length = (int)payload.Length;
        if (length == 0)
        {
            return Empty(format);
        }

        var encoding = options.GetEncoding(format);

        // Short strings go through the shared pool: SECS traffic repeats the same model numbers,
        // recipe ids and alarm texts for the life of the link, so interning them turns a
        // per-message allocation into a lookup.
        if (length <= options.PooledStringThreshold)
        {
            using var pooled = SpanOwner<byte>.Allocate(length);
            payload.CopyTo(pooled.Span);
            return new StringItem(format, StringPool.Shared.GetOrAdd(pooled.Span, encoding));
        }

        if (payload.IsSingleSegment)
        {
            return new StringItem(format, encoding.GetString(payload.FirstSpan));
        }

        using var scratch = SpanOwner<byte>.Allocate(length);
        payload.CopyTo(scratch.Span);
        return new StringItem(format, encoding.GetString(scratch.Span));
    }

    private static SecsItem DecodeArray<T>(SecsFormat format, in ReadOnlySequence<byte> payload, SecsCodecOptions options)
        where T : unmanaged, IEquatable<T>
    {
        var byteLength = (int)payload.Length;
        if (byteLength == 0)
        {
            return Empty(format);
        }

        var elementSize = Unsafe.SizeOf<T>();
        if (byteLength % elementSize != 0)
        {
            throw new SecsDecodeException(
                $"A {format} payload of {byteLength} byte(s) is not a whole number of {elementSize}-byte elements.");
        }

        var count = byteLength / elementSize;

        if (byteLength >= options.PooledPayloadThreshold)
        {
            var owner = MemoryOwner<T>.Allocate(count);
            try
            {
                payload.CopyTo(MemoryMarshal.AsBytes(owner.Span));
                EndiannessHelper.SwapIfNeeded(owner.Span);
                return new PooledMemoryItem<T>(format, owner);
            }
            catch
            {
                owner.Dispose();
                throw;
            }
        }

        var values = new T[count];
        payload.CopyTo(MemoryMarshal.AsBytes(values.AsSpan()));
        EndiannessHelper.SwapIfNeeded(values.AsSpan());
        return new MemoryItem<T>(format, values);
    }
}
