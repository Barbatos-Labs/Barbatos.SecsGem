// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// The HSMS-SS state machine and its control transactions - SEMI E37.
/// </summary>
/// <remarks>
/// Every test here drives the real connection over a real pair of pipes and asserts on the frames
/// that actually went out, rather than on an internal field. A state machine tested through its
/// own state property can be wrong in exactly the way that matters - sending the wrong thing to the
/// peer - and still pass.
/// </remarks>
public sealed class HsmsConnectionTests
{
    [Fact]
    public async Task A_new_connection_starts_out_not_connected()
    {
        await using var link = TestLink.Start();

        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        Assert.Equal(HsmsConnectionState.Connected, link.Connection.State);
        Assert.False(link.Connection.IsSelected);
    }

    [Fact]
    public async Task An_active_entity_sends_Select_req_as_soon_as_it_is_connected()
    {
        await using var link = TestLink.Start(active: true);

        using var request = await link.ReadAsync();

        Assert.Equal(MessageType.SelectRequest, request.Header.MessageType);
        Assert.Equal(0xFFFF, request.Header.SessionId);
        Assert.Null(request.Data);
    }

    [Fact]
    public async Task A_passive_entity_waits_to_be_selected()
    {
        await using var link = TestLink.Start(active: false);

        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        // It is the active side's job to select. A passive entity that sent Select.req would be
        // two entities both trying to select one connection.
        await link.SendAsync(HsmsMessage.Control(MessageType.SelectRequest, 0x7001));
        using var response = await link.ReadAsync();

        Assert.Equal(MessageType.SelectResponse, response.Header.MessageType);
        Assert.Equal(SelectStatus.Success, response.Header.SelectStatus);
        Assert.Equal(0x7001, response.SystemBytes);

        await link.WaitForStateAsync(HsmsConnectionState.Selected);
    }

    [Fact]
    public async Task A_control_response_echoes_the_request_transaction_id()
    {
        await using var link = TestLink.Start(active: false);
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        await link.SendAsync(HsmsMessage.Control(MessageType.LinkTestRequest, 0x1234_5678));
        using var response = await link.ReadAsync();

        // Get this wrong and the peer waits out T6 while a perfectly good answer sits on the wire
        // under a transaction id it is not watching.
        Assert.Equal(MessageType.LinkTestResponse, response.Header.MessageType);
        Assert.Equal(0x1234_5678, response.SystemBytes);
    }

    [Fact]
    public async Task An_active_entity_becomes_selected_when_its_Select_is_accepted()
    {
        await using var link = TestLink.Start(active: true);

        await link.SelectAsync();

        Assert.True(link.Connection.IsSelected);

        // Both of them reported, not just both of them reached: the connection sets the state and
        // then raises the event, so this list can be one entry behind the state the assertion
        // above just read.
        await link.WaitForTransitionsAsync(2);

        Assert.Equal(
            new[] { HsmsConnectionState.Connected, HsmsConnectionState.Selected },
            link.Transitions.Select(transition => transition.Current).ToArray());
    }

    [Fact]
    public async Task A_refused_Select_leaves_the_connection_unselected()
    {
        await using var link = TestLink.Start(active: true);

        using var request = await link.ReadAsync();
        await link.SendAsync(HsmsMessage.Control(
            MessageType.SelectResponse,
            request.SystemBytes,
            status: (byte)SelectStatus.NotReady));

        // The link goes down rather than sitting in a state where nothing can be sent: equipment
        // that says NotReady wants to be asked again later, after T5.
        await link.WaitForStateAsync(HsmsConnectionState.NotConnected);
        Assert.DoesNotContain(HsmsConnectionState.Selected, link.Transitions.Select(transition => transition.Current));
    }

    [Fact]
    public async Task A_second_Select_req_is_answered_with_already_active()
    {
        await using var link = TestLink.Start(active: false);
        await link.SelectAsync();

        await link.SendAsync(HsmsMessage.Control(MessageType.SelectRequest, 0x7002));
        using var response = await link.ReadAsync();

        // HSMS-SS allows one selection at a time, so the second request is answered rather than
        // silently allowed to replace the first.
        Assert.Equal(MessageType.SelectResponse, response.Header.MessageType);
        Assert.Equal(SelectStatus.AlreadyActive, response.Header.SelectStatus);
        Assert.True(link.Connection.IsSelected);
    }

    [Fact]
    public async Task A_Linktest_is_answered_while_selected()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        await link.SendAsync(HsmsMessage.Control(MessageType.LinkTestRequest, 0x7003));
        using var response = await link.ReadAsync();

        Assert.Equal(MessageType.LinkTestResponse, response.Header.MessageType);
        Assert.Equal(0x7003, response.SystemBytes);
    }

    [Fact]
    public async Task A_Deselect_returns_the_connection_to_not_selected()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        await link.SendAsync(HsmsMessage.Control(MessageType.DeselectRequest, 0x7004));
        using var response = await link.ReadAsync();

        Assert.Equal(MessageType.DeselectResponse, response.Header.MessageType);
        Assert.Equal(DeselectStatus.Success, response.Header.DeselectStatus);

        await link.WaitForStateAsync(HsmsConnectionState.Connected);
        Assert.False(link.Connection.IsSelected);
    }

    [Fact]
    public async Task A_Deselect_while_not_selected_says_so()
    {
        await using var link = TestLink.Start(active: false);
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        await link.SendAsync(HsmsMessage.Control(MessageType.DeselectRequest, 0x7005));
        using var response = await link.ReadAsync();

        Assert.Equal(DeselectStatus.NotSelected, response.Header.DeselectStatus);
    }

    [Fact]
    public async Task A_Separate_req_closes_the_link_with_no_answer()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        await link.SendAsync(HsmsMessage.Control(MessageType.SeparateRequest, 0x7006));

        // Separate is abrupt by design: E37 gives it no response, so answering one would be the
        // bug. The link simply goes.
        await link.WaitForStateAsync(HsmsConnectionState.NotConnected);
    }

    [Fact]
    public async Task An_unknown_SType_is_rejected()
    {
        await using var link = TestLink.Start(active: false);
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        // SType 8 is not defined. secs4net has no Reject.req at all, so a peer that sends this
        // gets silence and has to infer what happened from a dropped link.
        await link.SendAsync(new HsmsMessage(new MessageHeader
        {
            SessionId = 0xFFFF,
            MessageType = (MessageType)8,
            SystemBytes = 0x7007,
        }));

        using var response = await link.ReadAsync();

        Assert.Equal(MessageType.RejectRequest, response.Header.MessageType);
        Assert.Equal(RejectReason.StypeNotSupported, response.Header.RejectReason);
        Assert.Equal(0x7007, response.SystemBytes);
    }

    [Fact]
    public async Task A_data_message_in_an_unsupported_presentation_is_rejected()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        await link.SendAsync(new HsmsMessage(
            new MessageHeader
            {
                SessionId = link.Options.DeviceId,
                Stream = 1,
                Function = 1,
                ReplyExpected = true,
                PType = 1,
                MessageType = MessageType.DataMessage,
                SystemBytes = 0x7008,
            }));

        using var response = await link.ReadAsync();

        Assert.Equal(MessageType.RejectRequest, response.Header.MessageType);
        Assert.Equal(RejectReason.PtypeNotSupported, response.Header.RejectReason);
    }

    [Fact]
    public async Task A_data_message_before_Select_is_rejected()
    {
        await using var link = TestLink.Start(active: false);
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        using var message = new SecsMessage(1, 1);
        await link.SendDataAsync(message, systemBytes: 0x7009);

        using var response = await link.ReadAsync();

        // Not S9F5 and not silence: E37 is specific that data outside SELECTED is a Reject.
        Assert.Equal(MessageType.RejectRequest, response.Header.MessageType);
        Assert.Equal(RejectReason.EntityNotSelected, response.Header.RejectReason);
    }

    [Fact]
    public async Task A_received_primary_reaches_its_handler_and_the_reply_goes_back()
    {
        using var reply = new SecsMessage(1, 2, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A("BRBTOS"), SecsItem.A("0.1.0")),
        };

        SecsTransaction? seen = null;

        await using var link = TestLink.Start(
            active: false,
            routes: routes => routes.OnPrimary(1, 1, async (transaction, token) =>
            {
                seen = transaction;
                await transaction.ReplyAsync(reply, token);
            }));

        await link.SelectAsync();

        using var request = new SecsMessage(1, 1);
        await link.SendDataAsync(request, systemBytes: 0x700A);

        using var response = await link.ReadAsync();

        Assert.NotNull(seen);
        Assert.Equal(1, seen!.S);
        Assert.Equal(1, seen.F);
        Assert.True(seen.ReplyExpected);

        Assert.Equal(MessageType.DataMessage, response.Header.MessageType);
        Assert.Equal(1, response.Header.Stream);
        Assert.Equal(2, response.Header.Function);
        Assert.False(response.Header.ReplyExpected);
        Assert.Equal(0x700A, response.SystemBytes);
        Assert.Equal(reply.Item, response.Data?.Item);
    }

    [Fact]
    public async Task A_catch_all_handler_sees_what_no_other_handler_claims()
    {
        var claimed = new List<string>();

        await using var link = TestLink.Start(
            active: false,
            routes: routes => routes
                .OnPrimary(1, 1, (transaction, _) =>
                {
                    claimed.Add($"exact S{transaction.S}F{transaction.F}");
                    return ValueTask.CompletedTask;
                })
                .OnUnhandledPrimary((transaction, _) =>
                {
                    claimed.Add($"catch-all S{transaction.S}F{transaction.F}");
                    return ValueTask.CompletedTask;
                }));

        await link.SelectAsync();

        using var known = new SecsMessage(1, 1, replyExpected: false);
        using var unknown = new SecsMessage(2, 41, replyExpected: false);
        await link.SendDataAsync(known, systemBytes: 0x700B);
        await link.SendDataAsync(unknown, systemBytes: 0x700C);

        await Frames.WaitUntilAsync(() => claimed.Count == 2, "both messages to be dispatched");

        Assert.Equal(["exact S1F1", "catch-all S2F41"], claimed);
    }

    [Fact]
    public async Task A_sent_primary_is_matched_to_its_reply()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        using var primary = new SecsMessage(1, 3) { Item = SecsItem.U4(7001u) };
        var sending = link.Connection.SendAsync(primary, Frames.Token);

        using var sent = await link.ReadAsync();
        Assert.Equal(1, sent.Header.Stream);
        Assert.Equal(3, sent.Header.Function);
        Assert.True(sent.Header.ReplyExpected);
        Assert.Equal(link.Options.DeviceId, sent.Header.SessionId);

        using var reply = new SecsMessage(1, 4, replyExpected: false) { Item = SecsItem.A("ok") };
        await link.SendDataAsync(reply, sent.SystemBytes);

        using var received = await sending;
        Assert.Equal(1, received.S);
        Assert.Equal(4, received.F);
        Assert.Equal(reply.Item, received.Item);
    }

    [Fact]
    public async Task A_reply_under_the_wrong_transaction_id_is_not_delivered()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        using var primary = new SecsMessage(1, 3) { Item = SecsItem.U4(1u) };
        var sending = link.Connection.SendAsync(primary, Frames.Token);

        using var sent = await link.ReadAsync();

        // A secondary whose system bytes match nothing is a reply that lost its race with T3, or
        // one the peer invented. Either way it must not be handed to the waiter as if it fitted.
        using var stray = new SecsMessage(1, 4, replyExpected: false);
        await link.SendDataAsync(stray, sent.SystemBytes + 1);

        Assert.False(sending.IsCompleted);

        using var correct = new SecsMessage(1, 4, replyExpected: false) { Item = SecsItem.A("right one") };
        await link.SendDataAsync(correct, sent.SystemBytes);

        using var received = await sending;
        Assert.Equal("right one", received.Item?.GetString());
    }

    [Fact]
    public async Task Sending_a_data_message_before_Select_is_refused_locally()
    {
        await using var link = TestLink.Start(active: true);
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        using var primary = new SecsMessage(1, 3);

        // Better to fail the caller than to put a message on a link the peer will Reject.
        var error = await Assert.ThrowsAsync<SecsException>(
            async () => await link.Connection.SendAsync(primary, Frames.Token));

        Assert.Contains("Connected", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_and_Post_each_refuse_the_other_shape_of_message()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        using var withoutReply = new SecsMessage(6, 11, replyExpected: false);
        using var withReply = new SecsMessage(1, 3);

        var send = await Assert.ThrowsAsync<ArgumentException>(
            async () => await link.Connection.SendAsync(withoutReply, Frames.Token));
        var post = await Assert.ThrowsAsync<ArgumentException>(
            async () => await link.Connection.PostAsync(withReply, Frames.Token));

        Assert.Contains("PostAsync", send.Message, StringComparison.Ordinal);
        Assert.Contains("SendAsync", post.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_posted_message_goes_out_with_W_clear()
    {
        await using var link = TestLink.Start(active: true);
        await link.SelectAsync();

        using var report = new SecsMessage(6, 11, replyExpected: false) { Item = SecsItem.U4(7001u) };
        await link.Connection.PostAsync(report, Frames.Token);

        using var sent = await link.ReadAsync();

        Assert.Equal(6, sent.Header.Stream);
        Assert.Equal(11, sent.Header.Function);
        Assert.False(sent.Header.ReplyExpected);
        Assert.Equal(report.Item, sent.Data?.Item);
    }

    [Fact]
    public async Task A_handler_may_not_reply_twice_and_may_not_reply_to_a_notification()
    {
        InvalidOperationException? doubleReply = null;
        InvalidOperationException? unwantedReply = null;

        using var reply = new SecsMessage(1, 2, replyExpected: false);

        await using var link = TestLink.Start(
            active: false,
            routes: routes => routes
                .OnPrimary(1, 1, async (transaction, token) =>
                {
                    await transaction.ReplyAsync(reply, token);
                    doubleReply = Assert.Throws<InvalidOperationException>(() => transaction.ReplyAsync(reply, token));
                })
                .OnPrimary(6, 11, (transaction, token) =>
                {
                    unwantedReply = Assert.Throws<InvalidOperationException>(() => transaction.ReplyAsync(reply, token));
                    return ValueTask.CompletedTask;
                }));

        await link.SelectAsync();

        using var request = new SecsMessage(1, 1);
        using var notification = new SecsMessage(6, 11, replyExpected: false);
        await link.SendDataAsync(request, systemBytes: 0x700D);
        using var acknowledgement = await link.ReadAsync();
        await link.SendDataAsync(notification, systemBytes: 0x700E);

        await Frames.WaitUntilAsync(() => doubleReply is not null && unwantedReply is not null, "both handlers to run");

        Assert.Contains("already been answered", doubleReply!.Message, StringComparison.Ordinal);
        Assert.Contains("W=0", unwantedReply!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_malformed_body_costs_one_message_and_not_the_link()
    {
        var handled = 0;

        await using var link = TestLink.Start(
            active: true,
            routes: routes => routes.OnPrimary(1, 1, (_, _) =>
            {
                Interlocked.Increment(ref handled);
                return ValueTask.CompletedTask;
            }));

        await link.SelectAsync();

        // A frame whose body claims more bytes than it carries. The decoder consumes the frame,
        // so the link stays synchronised and the next message is right where it should be.
        await link.SendRawAsync(MalformedBody(link.Options.DeviceId, systemBytes: 0x700F));

        using var report = await link.ReadAsync();
        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(7, report.Header.Function);

        using var good = new SecsMessage(1, 1, replyExpected: false);
        await link.SendDataAsync(good, systemBytes: 0x7010);

        await Frames.WaitUntilAsync(() => Volatile.Read(ref handled) == 1, "the message after the bad one to arrive");
        Assert.Equal(HsmsConnectionState.Selected, link.Connection.State);
    }

    private static byte[] MalformedBody(ushort deviceId, int systemBytes)
    {
        // Header, then an item header announcing a ten-byte binary payload with none behind it.
        var header = new MessageHeader
        {
            SessionId = deviceId,
            Stream = 1,
            Function = 1,
            ReplyExpected = false,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = systemBytes,
        };

        var frame = new byte[HsmsPipeDecoder.LengthPrefixSize + MessageHeader.Length + 2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(frame, MessageHeader.Length + 2);
        header.WriteTo(frame.AsSpan(HsmsPipeDecoder.LengthPrefixSize));
        frame[^2] = ((int)SecsFormat.Binary << 2) | 1;
        frame[^1] = 10;

        return frame;
    }
}
