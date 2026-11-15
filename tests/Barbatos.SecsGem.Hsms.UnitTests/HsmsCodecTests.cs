// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// The encoder and the decoder against each other, over every segmentation of the byte stream.
/// </summary>
/// <remarks>
/// The interesting tests here are the boundary ones. A frame is withheld by its last
/// <c>k</c> bytes, the decoder is started and given time to park on the incomplete frame, and only
/// then do the remaining bytes arrive. Sweeping <c>k</c> over the length of the frame puts the
/// stall at every byte offset in turn - inside the length prefix, inside the header, and at every
/// position in the body - and every one of them has to produce the same message. That single
/// sweep is what a chunk-boundary bug cannot survive.
/// </remarks>
public sealed class HsmsCodecTests
{
    /// <summary>Every fixture, name paired with a representative set of stall offsets.</summary>
    public static TheoryData<string, int> BoundaryCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var name in Frames.All)
            {
                using var frame = Frames.Create(name);
                var length = Frames.Encode(frame).Length;

                foreach (var holdBack in HoldBacks(length))
                {
                    data.Add(name, holdBack);
                }
            }

            return data;
        }
    }

    /// <summary>Every single byte offset of one small frame, with nothing skipped.</summary>
    public static TheoryData<int> EveryOffsetOfOneFrame
    {
        get
        {
            using var frame = Frames.Create("s1f2");
            var length = Frames.Encode(frame).Length;

            var data = new TheoryData<int>();
            for (var holdBack = 1; holdBack <= length; holdBack++)
            {
                data.Add(holdBack);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Frames.Names), MemberType = typeof(Frames))]
    public async Task A_frame_that_arrives_whole_round_trips_byte_for_byte(string name)
    {
        var options = Frames.Options();
        using var sent = Frames.Create(name);
        var bytes = Frames.Encode(sent, options);

        var decoder = new HsmsPipeDecoder(options);
        var frames = await Frames.DecodeAllAsync(decoder, bytes);

        using var received = Assert.Single(frames);
        Frames.AssertSameFrame(sent, received);
        Assert.Equal(bytes, Frames.Encode(received, options));
    }

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public async Task A_frame_split_at_any_offset_decodes_to_the_same_message(string name, int holdBack)
    {
        var options = Frames.Options();
        using var sent = Frames.Create(name);
        var bytes = Frames.Encode(sent, options);

        var decoder = new HsmsPipeDecoder(options);
        using var received = await DecodeWithHoldbackAsync(decoder, bytes, holdBack);

        Frames.AssertSameFrame(sent, received);
        Assert.Equal(bytes, Frames.Encode(received, options));
    }

    [Theory]
    [MemberData(nameof(EveryOffsetOfOneFrame))]
    public async Task A_frame_split_at_every_offset_in_turn_decodes_to_the_same_message(int holdBack)
    {
        var options = Frames.Options();
        using var sent = Frames.Create("s1f2");
        var bytes = Frames.Encode(sent, options);

        var decoder = new HsmsPipeDecoder(options);
        using var received = await DecodeWithHoldbackAsync(decoder, bytes, holdBack);

        Frames.AssertSameFrame(sent, received);
        Assert.Equal(bytes, Frames.Encode(received, options));
    }

    [Fact]
    public async Task A_frame_already_buffered_whole_takes_the_fast_path()
    {
        var options = Frames.Options();
        using var sent = Frames.Create("s6f11");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes, Frames.Token);

        var decoder = new HsmsPipeDecoder(options);
        using var received = await Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);

        Frames.AssertSameFrame(sent, received);
        Assert.Equal(1L, decoder.FastPathFrames);
        Assert.Equal(0L, decoder.SlowPathFrames);
        Assert.False(decoder.IsReceivingFrame);
    }

    [Fact]
    public async Task A_frame_missing_its_last_byte_takes_the_slow_path()
    {
        var options = Frames.Options();
        using var sent = Frames.Create("s6f11");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes.AsMemory(..^1), Frames.Token);

        var decoder = new HsmsPipeDecoder(options);
        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);

        // It cannot finish: the frame is one byte short of what its own prefix announced.
        await Frames.WaitUntilAsync(() => decoder.IsReceivingFrame, "the decoder to hold a partial frame");
        Assert.Equal(bytes.Length - 1, decoder.PendingFrameBytes);
        Assert.False(decoding.IsCompleted);

        await pipe.Writer.WriteAsync(bytes.AsMemory(^1..), Frames.Token);
        using var received = await decoding;

        Frames.AssertSameFrame(sent, received);
        Assert.Equal(0L, decoder.FastPathFrames);
        Assert.Equal(1L, decoder.SlowPathFrames);
        Assert.False(decoder.IsReceivingFrame);
    }

    [Fact]
    public async Task Several_frames_in_one_buffer_all_decode_in_order()
    {
        var options = Frames.Options();
        var buffer = new ArrayBufferWriter<byte>();
        var encoder = new HsmsPipeEncoder(options);

        var sent = new List<HsmsMessage>();
        try
        {
            foreach (var name in Frames.All)
            {
                var frame = Frames.Create(name);
                sent.Add(frame);
                encoder.Encode(buffer, frame.Header, frame.Data);
            }

            var decoder = new HsmsPipeDecoder(options);
            var received = await Frames.DecodeAllAsync(decoder, buffer.WrittenMemory);

            try
            {
                Assert.Equal(sent.Count, received.Count);
                for (var i = 0; i < sent.Count; i++)
                {
                    Frames.AssertSameFrame(sent[i], received[i]);
                }

                Assert.Equal((long)sent.Count, decoder.FastPathFrames);
            }
            finally
            {
                foreach (var frame in received)
                {
                    frame.Dispose();
                }
            }
        }
        finally
        {
            foreach (var frame in sent)
            {
                frame.Dispose();
            }
        }
    }

    [Fact]
    public async Task A_frame_with_no_body_encodes_to_exactly_the_prefix_and_header()
    {
        var options = Frames.Options();
        using var sent = Frames.Create("select.req");
        var bytes = Frames.Encode(sent, options);

        Assert.Equal(HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length, bytes.Length);
        Assert.Equal(MessageHeader.Length, (int)BinaryPrimitives.ReadUInt32BigEndian(bytes));

        var decoder = new HsmsPipeDecoder(options);
        using var received = await Frames.DecodeOneAsync(decoder, await FilledPipeAsync(bytes), Frames.Token);

        Assert.True(received.IsControl);
        Assert.Null(received.Data);
        Assert.Equal(MessageType.SelectRequest, received.Header.MessageType);
    }

    [Fact]
    public async Task A_control_response_keeps_its_status_byte()
    {
        var options = Frames.Options();
        using var sent = Frames.Create("reject.req");
        var bytes = Frames.Encode(sent, options);

        var decoder = new HsmsPipeDecoder(options);
        using var received = await Frames.DecodeOneAsync(decoder, await FilledPipeAsync(bytes), Frames.Token);

        Assert.Equal(MessageType.RejectRequest, received.Header.MessageType);
        Assert.Equal(RejectReason.PtypeNotSupported, received.Header.RejectReason);
        Assert.Equal(0x2003, received.SystemBytes);
    }

    [Fact]
    public async Task The_message_name_does_not_survive_the_wire()
    {
        // Not a defect: SECS-II binary is positional and carries no names, which is exactly what
        // Secs2BinaryFormat.Descriptor declares by leaving MessageNames out of its capabilities.
        var options = Frames.Options();
        using var message = new SecsMessage(1, 2, replyExpected: false) { Name = "OnLineData", Item = SecsItem.A("x") };
        using var sent = HsmsMessage.FromData(message, sessionId: 7, systemBytes: 1);
        var bytes = Frames.Encode(sent, options);

        var decoder = new HsmsPipeDecoder(options);
        using var received = await Frames.DecodeOneAsync(decoder, await FilledPipeAsync(bytes), Frames.Token);

        Assert.NotNull(received.Data);
        Assert.Null(received.Data!.Name);
        Assert.Equal(message.Item, received.Data.Item);
    }

    [Fact]
    public void A_control_message_may_not_carry_a_body()
    {
        var encoder = new HsmsPipeEncoder(Frames.Options());
        using var body = new SecsMessage(1, 1) { Item = SecsItem.A("nope") };
        var header = new MessageHeader { MessageType = MessageType.LinkTestRequest, SystemBytes = 1 };

        var error = Assert.Throws<ArgumentException>(() => encoder.Encode(new ArrayBufferWriter<byte>(), header, body));
        Assert.Equal("data", error.ParamName);
    }

    private static IEnumerable<int> HoldBacks(int length)
    {
        // Every position that can matter: inside the length prefix, at and around the header
        // boundary, the midpoint, and the very first byte of the frame.
        var candidates = new SortedSet<int> { 1, 2, 3, 4, 5, 9, 10, 11, 13, 14, 15, length / 2, length - 4, length - 1, length };
        return candidates.Where(holdBack => holdBack >= 1 && holdBack <= length);
    }

    private static async Task<PipeReader> FilledPipeAsync(ReadOnlyMemory<byte> bytes)
    {
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes, Frames.Token);
        await pipe.Writer.CompleteAsync();
        return pipe.Reader;
    }

    /// <summary>
    /// Feeds a frame with its last <paramref name="holdBack"/> bytes withheld until the decoder
    /// has visibly parked on the incomplete frame, then releases them.
    /// </summary>
    private static async Task<HsmsMessage> DecodeWithHoldbackAsync(HsmsPipeDecoder decoder, byte[] bytes, int holdBack)
    {
        var pipe = new Pipe();
        var split = bytes.Length - holdBack;

        if (split > 0)
        {
            await pipe.Writer.WriteAsync(bytes.AsMemory(0, split), Frames.Token);
        }

        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);

        if (split > 0)
        {
            // The frame cannot complete without the withheld bytes, so this condition is reached
            // and the split is real rather than coalesced away by the pipe.
            await Frames.WaitUntilAsync(
                () => decoder.IsReceivingFrame,
                $"the decoder to hold the first {split} byte(s) of the frame");
        }

        await pipe.Writer.WriteAsync(bytes.AsMemory(split), Frames.Token);
        await pipe.Writer.CompleteAsync();

        return await decoding;
    }
}
