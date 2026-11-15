// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;

namespace Barbatos.SecsGem.Core.UnitTests;

/// <summary>
/// The SECS-II item codec: every format out and back, over every segmentation of the buffer, and
/// every way a peer can lie about a length.
/// </summary>
public sealed class SecsItemCodecTests
{
    /// <summary>Every item fixture, by name.</summary>
    public static TheoryData<string> Items
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in Fixtures)
            {
                data.Add(name);
            }

            return data;
        }
    }

    /// <summary>Every fixture paired with every segment size worth trying for it.</summary>
    public static TheoryData<string, int> ItemsBySegmentSize
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var name in Fixtures)
            {
                using var item = Create(name);
                var length = item.ToByteArray().Length;

                foreach (var segmentSize in Segmented.SegmentSizes(length))
                {
                    data.Add(name, segmentSize);
                }
            }

            return data;
        }
    }

    private static string[] Fixtures =>
    [
        "list-empty",
        "list-nested",
        "binary",
        "binary-empty",
        "binary-long",
        "boolean",
        "ascii",
        "ascii-empty",
        "jis8-multibyte",
        "i1",
        "i2",
        "i4",
        "i8",
        "u1",
        "u2",
        "u4",
        "u8",
        "f4",
        "f8",
    ];

    [Theory]
    [MemberData(nameof(ItemsBySegmentSize))]
    public void An_item_survives_the_buffer_being_cut_into_any_size_of_segment(string name, int segmentSize)
    {
        using var original = Create(name);
        var bytes = original.ToByteArray();

        var sequence = Segmented.Create(bytes, segmentSize);
        using var decoded = SecsItem.Decode(ref sequence, SecsCodecOptions.Default);

        Assert.Equal(original, decoded);
        Assert.Equal(original.Format, decoded.Format);
        Assert.Equal(original.Count, decoded.Count);
        Assert.True(sequence.IsEmpty, "decoding should consume exactly the item's bytes and no more");
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void An_item_round_trips_through_bytes(string name)
    {
        using var original = Create(name);
        var bytes = original.ToByteArray();

        using var decoded = SecsItem.Decode(bytes);

        Assert.Equal(original, decoded);
        Assert.Equal(bytes, decoded.ToByteArray());
    }

    [Theory]
    [MemberData(nameof(Items))]
    public void An_empty_item_of_the_same_format_round_trips(string name)
    {
        using var original = Create(name);
        using var empty = SecsItem.Empty(original.Format);

        using var decoded = SecsItem.Decode(empty.ToByteArray());

        Assert.Equal(original.Format, decoded.Format);
        Assert.Equal(0, decoded.Count);
    }

    [Fact]
    public void Successive_items_in_one_buffer_decode_one_after_another()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var first = SecsItem.A("first");
        using var second = SecsItem.U4(1u, 2u, 3u);
        using var third = SecsItem.L(SecsItem.Boolean(true));

        first.EncodeTo(buffer);
        second.EncodeTo(buffer);
        third.EncodeTo(buffer);

        // Decode advances the sequence past what it read, which is what lets a message body hold
        // several items and a capture file hold several messages.
        var sequence = Segmented.Create(buffer.WrittenSpan, segmentSize: 3);

        using var decodedFirst = SecsItem.Decode(ref sequence, SecsCodecOptions.Default);
        using var decodedSecond = SecsItem.Decode(ref sequence, SecsCodecOptions.Default);
        using var decodedThird = SecsItem.Decode(ref sequence, SecsCodecOptions.Default);

        Assert.Equal(first, decodedFirst);
        Assert.Equal(second, decodedSecond);
        Assert.Equal(third, decodedThird);
        Assert.True(sequence.IsEmpty);
    }

    [Fact]
    public void A_payload_above_the_pooling_threshold_still_decodes_to_the_same_values()
    {
        var values = new int[4096];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = i * -7;
        }

        using var original = SecsItem.I4(values);
        var options = new SecsCodecOptions { PooledPayloadThreshold = 64 };

        var sequence = Segmented.Create(original.ToByteArray(), segmentSize: 7);
        using var decoded = SecsItem.Decode(ref sequence, options);

        Assert.Equal(original, decoded);
        Assert.Equal(values, decoded.GetSpan<int>().ToArray());
    }

    [Fact]
    public void A_three_byte_length_decodes()
    {
        // Past 65,535 bytes an item needs all three length bytes, which is the branch a fixture
        // of a few hundred bytes never reaches.
        using var original = SecsItem.B(new byte[70_000]);

        var sequence = Segmented.Create(original.ToByteArray(), segmentSize: 4096);
        using var decoded = SecsItem.Decode(ref sequence, SecsCodecOptions.Default);

        Assert.Equal(SecsFormat.Binary, decoded.Format);
        Assert.Equal(70_000, decoded.Count);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void An_undefined_format_is_rejected()
    {
        // Format 1 is not one of the fifteen SEMI E5 defines.
        var bytes = new byte[] { (1 << 2) | 1, 0x00 };

        var sequence = new ReadOnlySequence<byte>(bytes);
        Assert.Throws<SecsDecodeException>(() => SecsItem.Decode(ref sequence, SecsCodecOptions.Default));
    }

    [Fact]
    public void A_truncated_payload_is_rejected()
    {
        // Binary announcing ten bytes, with three present.
        var bytes = new byte[] { ((int)SecsFormat.Binary << 2) | 1, 0x0A, 1, 2, 3 };

        var sequence = new ReadOnlySequence<byte>(bytes);
        Assert.Throws<SecsDecodeException>(() => SecsItem.Decode(ref sequence, SecsCodecOptions.Default));
    }

    [Fact]
    public void A_payload_that_is_not_a_whole_number_of_elements_is_rejected()
    {
        // I4 elements are four bytes each, so a three-byte payload cannot be I4.
        var bytes = new byte[] { ((int)SecsFormat.I4 << 2) | 1, 0x03, 1, 2, 3 };

        var sequence = new ReadOnlySequence<byte>(bytes);
        Assert.Throws<SecsDecodeException>(() => SecsItem.Decode(ref sequence, SecsCodecOptions.Default));
    }

    [Fact]
    public void A_payload_past_the_configured_limit_is_rejected_before_it_is_allocated()
    {
        // The length is the peer's number: 16 MiB announced, nothing behind it. A decoder that
        // sizes a buffer from this first is a denial of service waiting to happen.
        var bytes = new byte[] { ((int)SecsFormat.Binary << 2) | 3, 0xFF, 0xFF, 0xFF };
        var options = new SecsCodecOptions { MaxPayloadLength = 1024 };

        var sequence = new ReadOnlySequence<byte>(bytes);
        var error = Assert.Throws<SecsDecodeException>(() => SecsItem.Decode(ref sequence, options));

        Assert.Contains("1024", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_claiming_more_children_than_the_buffer_could_hold_is_rejected()
    {
        // A list of 1,000 children needs at least 2,000 bytes behind it, because the smallest
        // possible child is a two-byte empty item. Checking that before allocating the child
        // array is what stops a two-byte message asking for a million-element array.
        var bytes = new byte[] { ((int)SecsFormat.List << 2) | 2, 0x03, 0xE8 };

        var sequence = new ReadOnlySequence<byte>(bytes);
        Assert.Throws<SecsDecodeException>(() => SecsItem.Decode(ref sequence, SecsCodecOptions.Default));
    }

    [Fact]
    public void Nesting_past_the_depth_limit_is_rejected()
    {
        // Every level is a list of one: two bytes of header, no payload. A few hundred of them is
        // a tiny message that would blow the stack of a decoder that just recursed.
        var levels = SecsItem.MaxDepth + 8;
        var bytes = new byte[(levels * 2) + 2];
        for (var i = 0; i < levels; i++)
        {
            bytes[i * 2] = ((int)SecsFormat.List << 2) | 1;
            bytes[(i * 2) + 1] = 1;
        }

        bytes[^2] = ((int)SecsFormat.Binary << 2) | 1;
        bytes[^1] = 0;

        var sequence = new ReadOnlySequence<byte>(bytes);
        var error = Assert.Throws<SecsDecodeException>(() => SecsItem.Decode(ref sequence, SecsCodecOptions.Default));

        Assert.Contains("nesting", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"{SecsItem.MaxDepth}-level", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Nesting_up_to_the_depth_limit_is_accepted()
    {
        var levels = SecsItem.MaxDepth - 2;
        var bytes = new byte[(levels * 2) + 2];
        for (var i = 0; i < levels; i++)
        {
            bytes[i * 2] = ((int)SecsFormat.List << 2) | 1;
            bytes[(i * 2) + 1] = 1;
        }

        bytes[^2] = ((int)SecsFormat.Binary << 2) | 1;
        bytes[^1] = 0;

        var sequence = new ReadOnlySequence<byte>(bytes);
        using var decoded = SecsItem.Decode(ref sequence, SecsCodecOptions.Default);

        Assert.Equal(SecsFormat.List, decoded.Format);
        Assert.True(sequence.IsEmpty);
    }

    [Theory]
    [InlineData((int)SecsFormat.Binary, 1)]
    [InlineData((int)SecsFormat.ASCII, 2)]
    [InlineData((int)SecsFormat.F8, 3)]
    public void An_item_header_byte_reads_back_its_format_and_length_width(int format, int lengthBytes)
    {
        var headerByte = (byte)((format << 2) | lengthBytes);

        Assert.True(SecsItem.TryReadItemHeaderByte(headerByte, out var decodedFormat, out var decodedWidth));
        Assert.Equal((SecsFormat)format, decodedFormat);
        Assert.Equal(lengthBytes, decodedWidth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void An_item_header_byte_with_no_length_bytes_is_rejected(int lengthBytes)
    {
        // Zero length bytes is not legal; one is, so this also pins that the guard is on the
        // right side of the boundary.
        var headerByte = (byte)(((int)SecsFormat.Binary << 2) | lengthBytes);

        Assert.Equal(lengthBytes != 0, SecsItem.TryReadItemHeaderByte(headerByte, out _, out _));
    }

    [Theory]
    [InlineData(new byte[] { 0x2A }, 42)]
    [InlineData(new byte[] { 0x01, 0x00 }, 256)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF }, 0xFF_FF_FF)]
    public void An_item_length_reads_big_endian_whatever_its_width(byte[] lengthBytes, int expected)
    {
        Assert.Equal(expected, SecsItem.ReadItemLength(new ReadOnlySequence<byte>(lengthBytes)));
    }

    [Fact]
    public void An_item_length_split_across_segments_still_reads_big_endian()
    {
        var sequence = Segmented.Create([0x01, 0x00, 0x00], segmentSize: 1);

        Assert.Equal(65_536, SecsItem.ReadItemLength(sequence));
    }

    [Theory]
    [InlineData(SecsFormat.List)]
    [InlineData(SecsFormat.Binary)]
    [InlineData(SecsFormat.ASCII)]
    [InlineData(SecsFormat.F8)]
    [InlineData(SecsFormat.U8)]
    public void Every_defined_format_is_recognised(SecsFormat format)
    {
        Assert.True(SecsItem.IsDefinedFormat(format));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(63)]
    public void An_undefined_format_value_is_not_recognised(int value)
    {
        Assert.False(SecsItem.IsDefinedFormat((SecsFormat)value));
    }

    private static SecsItem Create(string name) => name switch
    {
        "list-empty" => SecsItem.L(),
        "list-nested" => SecsItem.L(
            SecsItem.A("outer"),
            SecsItem.L(SecsItem.U2(1, 2), SecsItem.L(SecsItem.Boolean(true), SecsItem.A("inner")))),
        "binary" => SecsItem.B(0x00, 0x7F, 0x80, 0xFF),
        "binary-empty" => SecsItem.B(),
        "binary-long" => SecsItem.B(new byte[300]),
        "boolean" => SecsItem.Boolean(true, false, true, true),
        "ascii" => SecsItem.A("BARBATOS 0.1.0"),
        "ascii-empty" => SecsItem.A(string.Empty),

        // Three characters, each more than one byte once encoded: the case where the item's
        // element count and its byte length are genuinely different numbers.
        "jis8-multibyte" => SecsItem.J("あいう"),

        "i1" => SecsItem.I1(sbyte.MinValue, -1, 0, 1, sbyte.MaxValue),
        "i2" => SecsItem.I2(short.MinValue, -1, 0, 1, short.MaxValue),
        "i4" => SecsItem.I4(int.MinValue, -1, 0, 1, int.MaxValue),
        "i8" => SecsItem.I8(long.MinValue, -1, 0, 1, long.MaxValue),
        "u1" => SecsItem.U1(0, 1, 127, 128, byte.MaxValue),
        "u2" => SecsItem.U2(0, 1, 32_768, ushort.MaxValue),
        "u4" => SecsItem.U4(0u, 1u, uint.MaxValue),
        "u8" => SecsItem.U8(0ul, 1ul, ulong.MaxValue),
        "f4" => SecsItem.F4(float.MinValue, -1.5f, 0f, float.Epsilon, float.MaxValue),
        "f8" => SecsItem.F8(double.MinValue, -1.5d, 0d, double.Epsilon, double.MaxValue),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a fixture."),
    };
}
