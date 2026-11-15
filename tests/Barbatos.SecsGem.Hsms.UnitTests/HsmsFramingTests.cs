// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// What the decoder does with a frame that is wrong, and what it does to the link afterwards.
/// </summary>
/// <remarks>
/// Every length in an HSMS frame is a number the peer chose, so each one is a place a peer can
/// lie. The tests here walk the lies: a length below the header, a length past the configured
/// limit, a body with bytes left over, and a link that stops in the middle. The last two tests
/// are about recovery rather than detection - a malformed body must cost one S9F7, not the
/// connection.
/// </remarks>
public sealed class HsmsFramingTests
{
    [Fact]
    public async Task A_frame_shorter_than_a_header_is_rejected()
    {
        var decoder = new HsmsPipeDecoder(Frames.Options());

        var error = await Assert.ThrowsAsync<SecsDecodeException>(
            () => Frames.DecodeAllAsync(decoder, PrefixFor(MessageHeader.Length - 1)));

        Assert.Contains("9 byte(s)", error.Message, StringComparison.Ordinal);
        Assert.Contains("header alone is 10", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_frame_past_the_configured_limit_is_rejected_before_anything_is_allocated()
    {
        var decoder = new HsmsPipeDecoder(Frames.Options(maxMessageLength: 64));

        var error = await Assert.ThrowsAsync<HsmsMessageTooLongException>(
            () => Frames.DecodeAllAsync(decoder, PrefixFor(1_000_000)));

        Assert.Equal(1_000_000, error.Length);
        Assert.Equal(64, error.Limit);
    }

    [Fact]
    public async Task A_body_with_bytes_left_over_after_its_root_item_is_rejected()
    {
        var options = Frames.Options();
        var bytes = WithTrailingJunk(options);
        var decoder = new HsmsPipeDecoder(options);

        var error = await Assert.ThrowsAsync<SecsDecodeException>(() => Frames.DecodeAllAsync(decoder, bytes));

        Assert.Contains("after its root item", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_that_closes_at_a_frame_boundary_simply_ends()
    {
        var options = Frames.Options();
        using var sent = Frames.Create("s1f2");
        var decoder = new HsmsPipeDecoder(options);

        var frames = await Frames.DecodeAllAsync(decoder, Frames.Encode(sent, options));

        using var received = Assert.Single(frames);
        Frames.AssertSameFrame(sent, received);
    }

    [Fact]
    public async Task A_link_that_closes_with_nothing_at_all_simply_ends()
    {
        var decoder = new HsmsPipeDecoder(Frames.Options());

        var frames = await Frames.DecodeAllAsync(decoder, ReadOnlyMemory<byte>.Empty);

        Assert.Empty(frames);
    }

    [Fact]
    public async Task A_link_that_closes_mid_frame_is_a_truncation_not_an_ending()
    {
        var options = Frames.Options();
        using var sent = Frames.Create("s6f11");
        var bytes = Frames.Encode(sent, options);
        var decoder = new HsmsPipeDecoder(options);

        // Silently treating this as a clean end is how a host ends up waiting out T3 for a reply
        // that was already lost.
        var error = await Assert.ThrowsAsync<SecsDecodeException>(
            () => Frames.DecodeAllAsync(decoder, bytes.AsMemory(..^3)));

        Assert.Contains("3 byte(s) short", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_link_that_closes_inside_the_length_prefix_is_a_truncation()
    {
        var decoder = new HsmsPipeDecoder(Frames.Options());

        var error = await Assert.ThrowsAsync<SecsDecodeException>(
            () => Frames.DecodeAllAsync(decoder, new byte[] { 0x00, 0x00 }));

        Assert.Contains("2 stray byte(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_data_message_in_an_unsupported_presentation_surfaces_its_header_only()
    {
        var options = Frames.Options();
        var encoder = new HsmsPipeEncoder(options);
        var buffer = new ArrayBufferWriter<byte>();

        // PType 1 is some presentation other than SECS-II. The body is not ours to parse, so the
        // header comes through on its own and the connection answers Reject.req.
        var header = new MessageHeader
        {
            SessionId = 7,
            Stream = 1,
            Function = 3,
            ReplyExpected = true,
            PType = 1,
            MessageType = MessageType.DataMessage,
            SystemBytes = 42,
        };

        using var body = new SecsMessage(1, 3) { Item = SecsItem.A("not secs-ii at all") };
        encoder.Encode(buffer, header, body);

        var decoder = new HsmsPipeDecoder(options);
        var frames = await Frames.DecodeAllAsync(decoder, buffer.WrittenMemory);

        using var received = Assert.Single(frames);
        Assert.Equal(1, received.Header.PType);
        Assert.False(received.Header.IsDataMessage);
        Assert.Null(received.Data);
        Assert.Equal(42, received.SystemBytes);
    }

    [Fact]
    public async Task A_malformed_body_consumes_its_frame_and_leaves_the_link_synchronised()
    {
        var options = Frames.Options();
        var decoder = new HsmsPipeDecoder(options);

        using var good = Frames.Create("s1f2");
        var goodBytes = Frames.Encode(good, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(WithTrailingJunk(options), Frames.Token);
        await pipe.Writer.WriteAsync(goodBytes, Frames.Token);
        await pipe.Writer.CompleteAsync();

        // The first frame is rejected...
        await Assert.ThrowsAsync<SecsDecodeException>(() => Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token));

        // ...and because it was still consumed, the next frame is right where it should be. This
        // is what lets a connection answer S9F7 and carry on rather than dropping the link. An
        // async iterator cannot resume past an exception, so the engine re-enters DecodeAsync -
        // on the same reader, which has not lost its place.
        using var received = await Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);
        Frames.AssertSameFrame(good, received);
    }

    [Fact]
    public void The_encoder_refuses_a_message_past_the_limit()
    {
        var options = Frames.Options(maxMessageLength: 64);
        var encoder = new HsmsPipeEncoder(options);

        using var message = new SecsMessage(7, 3) { Item = SecsItem.B(new byte[256]) };
        var header = new MessageHeader { MessageType = MessageType.DataMessage, Stream = 7, Function = 3 };

        // Better to fail locally than to make the peer send S9F11 about it.
        var error = Assert.Throws<HsmsMessageTooLongException>(
            () => encoder.Encode(new ArrayBufferWriter<byte>(), header, message));

        Assert.Equal(64, error.Limit);
        Assert.True(error.Length > 64);
    }

    [Fact]
    public async Task A_control_message_that_arrives_with_a_body_surfaces_its_header_only()
    {
        var options = Frames.Options();

        // Built by hand, because the encoder will not produce this: E37 gives control messages no
        // body, and a peer that sends one anyway must not be able to confuse the decoder.
        using var junk = new SecsMessage(0, 0) { Item = SecsItem.A("surprise") };
        var itemBytes = junk.Item!.ToByteArray();

        var buffer = new ArrayBufferWriter<byte>();
        var prefix = buffer.GetSpan(HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length);
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)(MessageHeader.Length + itemBytes.Length));
        new MessageHeader
        {
            SessionId = 0xFFFF,
            MessageType = MessageType.LinkTestRequest,
            SystemBytes = 7,
        }.WriteTo(prefix[HsmsPipeDecoder.LengthPrefixSize..]);
        buffer.Advance(HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length);
        buffer.Write(itemBytes);

        var decoder = new HsmsPipeDecoder(options);
        var frames = await Frames.DecodeAllAsync(decoder, buffer.WrittenMemory);

        using var received = Assert.Single(frames);
        Assert.Equal(MessageType.LinkTestRequest, received.Header.MessageType);
        Assert.Null(received.Data);
        Assert.Equal(7, received.SystemBytes);
    }

    private static byte[] PrefixFor(int payloadLength)
    {
        var prefix = new byte[HsmsPipeDecoder.LengthPrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payloadLength);
        return prefix;
    }

    /// <summary>Builds a frame whose announced length is one byte longer than its item tree.</summary>
    private static byte[] WithTrailingJunk(SecsGemOptions options)
    {
        using var message = Frames.Create("s1f2");
        var good = Frames.Encode(message, options);

        var bad = new byte[good.Length + 1];
        good.CopyTo(bad, 0);
        bad[^1] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(bad, (uint)(bad.Length - HsmsPipeDecoder.LengthPrefixSize));

        return bad;
    }
}
