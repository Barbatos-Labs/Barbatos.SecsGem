// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers.Binary;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// Automatic Stream 9 reporting - SEMI E5.
/// </summary>
/// <remarks>
/// <para>
/// Every test here checks two things: that the right function goes out, and that it carries the
/// right MHEAD. The second is what makes an S9 message useful. A peer with a dozen transactions in
/// flight cannot act on "something you sent was wrong" - it needs the ten bytes that say which
/// one, and a report with the wrong header is worse than no report at all.
/// </para>
/// <para>
/// The negative cases are asserted by what comes next rather than by waiting for nothing: a good
/// message is sent afterwards, and its reply being the very next frame on the wire proves no
/// report was queued in front of it.
/// </para>
/// </remarks>
public sealed class S9ErrorTests
{
    [Fact]
    public async Task A_message_for_another_device_is_reported_as_S9F1()
    {
        await using var link = Link();
        await link.SelectAsync();

        using var message = new SecsMessage(1, 1, replyExpected: false);
        await link.SendDataAsync(message, systemBytes: 0x9001, deviceId: 99);

        using var report = await link.ReadAsync();

        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(1, report.Header.Function);
        Assert.False(report.Header.ReplyExpected);

        // Note that it is reported at all. Dropping the message silently - which is all secs4net
        // can do, having no S9F1 - leaves the sender waiting out T3 with no idea it had the wrong
        // address.
        var reported = MessageHeader.Parse(Frames.ReportedHeader(report));
        Assert.Equal(99, reported.DeviceId);
        Assert.Equal(0x9001, reported.SystemBytes);
    }

    [Fact]
    public async Task S9F1_can_be_turned_off()
    {
        await using var link = Link(options => options.SendS9F1OnUnknownDeviceId = false);
        await link.SelectAsync();

        using var wrongDevice = new SecsMessage(1, 1, replyExpected: false);
        await link.SendDataAsync(wrongDevice, systemBytes: 0x9002, deviceId: 99);

        await AssertNextIsTheReplyAsync(link, 0x9003);
    }

    [Fact]
    public async Task An_unimplemented_stream_is_reported_as_S9F3()
    {
        await using var link = Link();
        await link.SelectAsync();

        // The router knows stream 1 and nothing else, so stream 2 is unrecognised as a whole.
        using var message = new SecsMessage(2, 41, replyExpected: false);
        await link.SendDataAsync(message, systemBytes: 0x9004);

        using var report = await link.ReadAsync();

        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(3, report.Header.Function);

        var reported = MessageHeader.Parse(Frames.ReportedHeader(report));
        Assert.Equal(2, reported.Stream);
        Assert.Equal(41, reported.Function);
    }

    [Fact]
    public async Task An_unimplemented_function_of_a_known_stream_is_reported_as_S9F5()
    {
        await using var link = Link();
        await link.SelectAsync();

        // Stream 1 is implemented, F13 is not. Reporting S9F3 here would send a host looking for
        // a missing stream when the stream is there and one message of it is not - which is the
        // whole reason the routing table has to declare both.
        using var message = new SecsMessage(1, 13, replyExpected: false);
        await link.SendDataAsync(message, systemBytes: 0x9005);

        using var report = await link.ReadAsync();

        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(5, report.Header.Function);

        var reported = MessageHeader.Parse(Frames.ReportedHeader(report));
        Assert.Equal(1, reported.Stream);
        Assert.Equal(13, reported.Function);
    }

    [Fact]
    public async Task S9F3_and_S9F5_can_be_turned_off()
    {
        await using var link = Link(options => options.SendS9F3F5OnUnhandledMessage = false);
        await link.SelectAsync();

        using var unhandled = new SecsMessage(2, 41, replyExpected: false);
        await link.SendDataAsync(unhandled, systemBytes: 0x9006);

        await AssertNextIsTheReplyAsync(link, 0x9007);
    }

    [Fact]
    public async Task A_catch_all_handler_turns_off_S9F3_and_S9F5()
    {
        using var acknowledgement = new SecsMessage(1, 2, replyExpected: false);
        var caught = 0;

        await using var link = TestLink.Start(
            active: false,
            routes: routes => routes
                .OnPrimary(1, 1, async (transaction, token) => await transaction.ReplyAsync(acknowledgement, token))
                .OnUnhandledPrimary((_, _) =>
                {
                    Interlocked.Increment(ref caught);
                    return ValueTask.CompletedTask;
                }));

        await link.SelectAsync();

        // An entity that claims every message cannot also claim not to recognise one.
        using var unhandled = new SecsMessage(2, 41, replyExpected: false);
        await link.SendDataAsync(unhandled, systemBytes: 0x9008);

        await Frames.WaitUntilAsync(() => Volatile.Read(ref caught) == 1, "the catch-all to run");
        await AssertNextIsTheReplyAsync(link, 0x9009);
    }

    [Fact]
    public async Task An_undecodable_body_is_reported_as_S9F7()
    {
        await using var link = Link();
        await link.SelectAsync();

        var header = DataHeader(link.Options.DeviceId, stream: 1, function: 1, systemBytes: 0x900A);
        await link.SendRawAsync(FrameWithBrokenBody(header));

        using var report = await link.ReadAsync();

        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(7, report.Header.Function);
        Assert.Equal(Frames.HeaderBytes(header), Frames.ReportedHeader(report));

        // And the link survives it: one malformed message costs an S9F7, not the connection.
        Assert.Equal(HsmsConnectionState.Selected, link.Connection.State);
        await AssertNextIsTheReplyAsync(link, 0x900B);
    }

    [Fact]
    public async Task An_oversized_message_is_reported_as_S9F11_and_drops_the_link()
    {
        await using var link = Link(options => options.MaxMessageLength = 1024);
        await link.SelectAsync();

        var header = DataHeader(link.Options.DeviceId, stream: 7, function: 3, systemBytes: 0x900C);
        await link.SendRawAsync(OversizedFrame(header, announced: 2_000_000));

        using var report = await link.ReadAsync();

        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(11, report.Header.Function);

        // MHEAD is filled in from bytes that had already arrived. The decoder does not wait for
        // them - the frame is being rejected precisely because its length cannot be trusted.
        Assert.Equal(Frames.HeaderBytes(header), Frames.ReportedHeader(report));

        // Unlike every other S9 case, this one cannot be recovered from: skipping the body would
        // mean trusting the length just rejected, so the byte stream is no longer synchronised.
        await link.WaitForStateAsync(HsmsConnectionState.NotConnected);
    }

    [Fact]
    public async Task An_error_report_is_never_answered_with_an_error_report()
    {
        await using var link = Link();
        await link.SelectAsync();

        // No handler claims stream 9. Answering it with S9F5 would build a loop that saturates
        // the link, with each entity reporting the other's report.
        using var peerReport = S9Messages.IllegalData(DataHeader(link.Options.DeviceId, 1, 1, 0x900D));
        await link.SendDataAsync(peerReport, systemBytes: 0x900E);

        await AssertNextIsTheReplyAsync(link, 0x900F);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(11)]
    public void Every_header_carrying_report_names_its_body_MHEAD(byte function)
    {
        var header = DataHeader(deviceId: 7, stream: 6, function: 11, systemBytes: 0x1234);

        using var report = function switch
        {
            1 => S9Messages.UnrecognizedDeviceId(header),
            3 => S9Messages.UnrecognizedStream(header),
            5 => S9Messages.UnrecognizedFunction(header),
            7 => S9Messages.IllegalData(header),
            _ => S9Messages.DataTooLong(header),
        };

        Assert.Equal(9, report.S);
        Assert.Equal(function, report.F);
        Assert.False(report.ReplyExpected);
        Assert.Equal(SecsFormat.Binary, report.Item?.Format);
        Assert.Equal("MHEAD", report.Item?.Label);
        Assert.Equal(Frames.HeaderBytes(header), report.Item?.GetSpan<byte>().ToArray());
    }

    [Fact]
    public void S9F9_names_its_body_SHEAD_because_it_reports_what_this_entity_sent()
    {
        var header = DataHeader(deviceId: 7, stream: 1, function: 3, systemBytes: 0x4321);

        using var report = S9Messages.TransactionTimerTimeout(header);

        Assert.Equal(9, report.S);
        Assert.Equal(9, report.F);

        // Not MHEAD: every other S9 reports a message that arrived, and this one reports a message
        // that left.
        Assert.Equal("SHEAD", report.Item?.Label);
    }

    [Fact]
    public void S9F13_reports_a_conversation_rather_than_a_header()
    {
        using var report = S9Messages.ConversationTimeout("S6F12", "event report acknowledgement");

        Assert.Equal(9, report.S);
        Assert.Equal(13, report.F);
        Assert.Equal(2, report.Item?.Count);
        Assert.Equal("S6F12", report.Item?[0].GetString());
        Assert.Equal("MEXP", report.Item?[0].Label);
        Assert.Equal("event report acknowledgement", report.Item?[1].GetString());
        Assert.Equal("EDID", report.Item?[1].Label);
    }

    [Fact]
    public void A_stream_nine_message_is_recognised_as_an_error_report()
    {
        using var report = S9Messages.UnrecognizedFunction(default);
        using var ordinary = new SecsMessage(6, 11, replyExpected: false);

        Assert.True(S9Messages.IsErrorReport(report));
        Assert.False(S9Messages.IsErrorReport(ordinary));
    }

    /// <summary>A passive link whose router knows S1F1 and nothing else.</summary>
    private static TestLink Link(Action<SecsGemOptions>? configure = null)
    {
        var acknowledgement = new SecsMessage(1, 2, replyExpected: false) { Item = SecsItem.A("ack") };

        return TestLink.Start(
            active: false,
            routes: routes => routes.OnPrimary(1, 1, async (transaction, token) =>
                await transaction.ReplyAsync(acknowledgement, token)),
            configure: options =>
            {
                options.LinkTestInterval = TimeSpan.Zero;
                configure?.Invoke(options);
            });
    }

    /// <summary>Sends an S1F1 and asserts its reply is the very next frame out.</summary>
    private static async Task AssertNextIsTheReplyAsync(TestLink link, int systemBytes)
    {
        using var request = new SecsMessage(1, 1);
        await link.SendDataAsync(request, systemBytes);

        using var next = await link.ReadAsync();

        Assert.Equal(1, next.Header.Stream);
        Assert.Equal(2, next.Header.Function);
        Assert.Equal(systemBytes, next.SystemBytes);
    }

    private static MessageHeader DataHeader(ushort deviceId, byte stream, byte function, int systemBytes) => new()
    {
        SessionId = deviceId,
        Stream = stream,
        Function = function,
        ReplyExpected = false,
        PType = 0,
        MessageType = MessageType.DataMessage,
        SystemBytes = systemBytes,
    };

    /// <summary>A frame whose body announces a ten-byte payload and carries none.</summary>
    private static byte[] FrameWithBrokenBody(MessageHeader header)
    {
        var frame = new byte[HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length + 2];
        BinaryPrimitives.WriteUInt32BigEndian(frame, MessageHeader.Length + 2);
        header.WriteTo(frame.AsSpan(HsmsPipeDecoder.LengthPrefixSize));
        frame[^2] = ((int)SecsFormat.Binary << 2) | 1;
        frame[^1] = 10;

        return frame;
    }

    /// <summary>A length prefix announcing more than the limit, followed by a real header.</summary>
    private static byte[] OversizedFrame(MessageHeader header, uint announced)
    {
        var frame = new byte[HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, announced);
        header.WriteTo(frame.AsSpan(HsmsPipeDecoder.LengthPrefixSize));

        return frame;
    }
}
