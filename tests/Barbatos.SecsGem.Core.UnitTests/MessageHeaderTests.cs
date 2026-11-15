// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;

namespace Barbatos.SecsGem.Core.UnitTests;

/// <summary>
/// The 10-byte HSMS header: its byte layout, and the two fields that share a byte.
/// </summary>
public sealed class MessageHeaderTests
{
    [Fact]
    public void The_ten_bytes_land_where_E37_says_they_do()
    {
        var header = new MessageHeader
        {
            SessionId = 0x1234,
            Stream = 6,
            Function = 11,
            ReplyExpected = true,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = 0x0A0B0C0D,
        };

        var bytes = new byte[MessageHeader.Length];
        header.WriteTo(bytes);

        Assert.Equal(0x12, bytes[0]);
        Assert.Equal(0x34, bytes[1]);
        Assert.Equal(0b1000_0110, bytes[2]);
        Assert.Equal(11, bytes[3]);
        Assert.Equal(0, bytes[4]);
        Assert.Equal(0, bytes[5]);
        Assert.Equal(0x0A, bytes[6]);
        Assert.Equal(0x0B, bytes[7]);
        Assert.Equal(0x0C, bytes[8]);
        Assert.Equal(0x0D, bytes[9]);
    }

    [Fact]
    public void Setting_the_stream_after_the_W_bit_keeps_both()
    {
        // Both fields live in byte 2, so a naive implementation lets whichever is written second
        // clear the other. In secs4net that is a real hazard: an object initializer written in the
        // wrong order silently drops the W-bit, and a host then waits out T3 for a reply the
        // equipment was never asked for.
        var replyFirst = new MessageHeader { ReplyExpected = true, Stream = 6 };
        var streamFirst = new MessageHeader { Stream = 6, ReplyExpected = true };

        Assert.Equal(6, replyFirst.Stream);
        Assert.True(replyFirst.ReplyExpected);
        Assert.Equal(6, streamFirst.Stream);
        Assert.True(streamFirst.ReplyExpected);
    }

    [Fact]
    public void Clearing_the_W_bit_after_the_stream_keeps_the_stream()
    {
        var header = new MessageHeader { Stream = 127, ReplyExpected = false };

        Assert.Equal(127, header.Stream);
        Assert.False(header.ReplyExpected);
    }

    [Fact]
    public void The_highest_stream_and_a_set_W_bit_coexist()
    {
        var header = new MessageHeader { Stream = 127, ReplyExpected = true };

        Assert.Equal(127, header.Stream);
        Assert.True(header.ReplyExpected);
    }

    [Theory]
    [InlineData(0x0007, 0x0007)]
    [InlineData(0x8007, 0x0007)]
    [InlineData(0xFFFF, 0x7FFF)]
    public void The_device_id_is_the_session_id_without_its_high_bit(int sessionId, int expected)
    {
        // SECS-I reserves bit 15 as the R-bit. HSMS has no R-bit, but equipment bridging the two
        // still sets it, so a device id has to be compared with the bit masked off.
        var header = new MessageHeader { SessionId = (ushort)sessionId };

        Assert.Equal((ushort)sessionId, header.SessionId);
        Assert.Equal((ushort)expected, header.DeviceId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void The_system_bytes_round_trip(int systemBytes)
    {
        var header = new MessageHeader { SystemBytes = systemBytes };

        Assert.Equal(systemBytes, header.SystemBytes);
    }

    [Fact]
    public void The_presentation_type_is_mapped()
    {
        // secs4net never maps byte 4 at all, which is why it cannot tell a SECS-II body from
        // somebody else's and tries to decode both.
        var header = new MessageHeader { PType = 3 };

        Assert.Equal(3, header.PType);
    }

    [Theory]
    [InlineData(MessageType.DataMessage, 0, true)]
    [InlineData(MessageType.DataMessage, 1, false)]
    [InlineData(MessageType.SelectRequest, 0, false)]
    [InlineData(MessageType.LinkTestRequest, 0, false)]
    public void A_decodable_data_message_is_a_data_message_with_presentation_zero(
        MessageType messageType,
        int pType,
        bool expected)
    {
        var header = new MessageHeader { MessageType = messageType, PType = (byte)pType };

        Assert.Equal(expected, header.IsDataMessage);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(11, true)]
    [InlineData(12, false)]
    public void An_odd_function_is_a_primary_message(int function, bool expected)
    {
        var header = new MessageHeader { Function = (byte)function };

        Assert.Equal(expected, header.IsPrimary);
    }

    [Fact]
    public void A_control_response_reads_its_status_out_of_byte_three()
    {
        var select = new MessageHeader { MessageType = MessageType.SelectResponse, Function = 1 };
        var deselect = new MessageHeader { MessageType = MessageType.DeselectResponse, Function = 2 };
        var reject = new MessageHeader { MessageType = MessageType.RejectRequest, Function = 2 };

        Assert.Equal(SelectStatus.AlreadyActive, select.SelectStatus);
        Assert.Equal(DeselectStatus.Busy, deselect.DeselectStatus);
        Assert.Equal(RejectReason.PtypeNotSupported, reject.RejectReason);
    }

    [Fact]
    public void A_header_parses_back_out_of_its_own_bytes()
    {
        var original = new MessageHeader
        {
            SessionId = 0x00FF,
            Stream = 6,
            Function = 11,
            ReplyExpected = true,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = 0x12345678,
        };

        var bytes = new byte[MessageHeader.Length];
        original.WriteTo(bytes);

        var parsed = MessageHeader.Parse(bytes);

        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.Stream, parsed.Stream);
        Assert.Equal(original.Function, parsed.Function);
        Assert.Equal(original.ReplyExpected, parsed.ReplyExpected);
        Assert.Equal(original.PType, parsed.PType);
        Assert.Equal(original.MessageType, parsed.MessageType);
        Assert.Equal(original.SystemBytes, parsed.SystemBytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(10)]
    public void A_header_parses_out_of_a_sequence_however_it_is_segmented(int segmentSize)
    {
        // The case that actually happens: ten header bytes spread over several pooled blocks off
        // a socket.
        var original = new MessageHeader
        {
            SessionId = 0xABCD,
            Stream = 1,
            Function = 3,
            ReplyExpected = true,
            MessageType = MessageType.DataMessage,
            SystemBytes = unchecked((int)0xDEADBEEF),
        };

        var bytes = new byte[MessageHeader.Length];
        original.WriteTo(bytes);

        var parsed = MessageHeader.Parse(Segmented.Create(bytes, segmentSize));

        Assert.Equal(original.SessionId, parsed.SessionId);
        Assert.Equal(original.Stream, parsed.Stream);
        Assert.Equal(original.ReplyExpected, parsed.ReplyExpected);
        Assert.Equal(original.SystemBytes, parsed.SystemBytes);
    }

    [Fact]
    public void Parsing_fewer_than_ten_bytes_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MessageHeader.Parse(new byte[MessageHeader.Length - 1]));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MessageHeader.Parse(new ReadOnlySequence<byte>(new byte[MessageHeader.Length - 1])));
    }

    [Fact]
    public void Writing_into_fewer_than_ten_bytes_is_rejected()
    {
        var header = default(MessageHeader);

        Assert.Throws<ArgumentOutOfRangeException>(() => header.WriteTo(new byte[MessageHeader.Length - 1]));
    }

    [Fact]
    public void A_data_message_renders_as_its_stream_and_function()
    {
        var withReply = new MessageHeader
        {
            Stream = 1,
            Function = 3,
            ReplyExpected = true,
            MessageType = MessageType.DataMessage,
            SystemBytes = 42,
        };

        var withoutReply = new MessageHeader
        {
            Stream = 1,
            Function = 4,
            MessageType = MessageType.DataMessage,
            SystemBytes = 42,
        };

        Assert.Equal("S1F3 W [0x0000002A]", withReply.ToString());
        Assert.Equal("S1F4 [0x0000002A]", withoutReply.ToString());
    }

    [Fact]
    public void A_control_message_renders_as_its_type()
    {
        var header = new MessageHeader { MessageType = MessageType.LinkTestRequest, SystemBytes = 7 };

        Assert.Equal("LinkTestRequest [0x00000007]", header.ToString());
    }
}
