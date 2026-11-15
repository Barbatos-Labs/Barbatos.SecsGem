// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// T3, T5, T6 and T7 - what each one measures, and what it must leave alone.
/// </summary>
/// <remarks>
/// <para>
/// Every interval here runs on a <c>FakeTimeProvider</c>, so a 45-second T3 elapses in
/// microseconds and the suite has no wall-clock waits to be flaky about. T8 has its own file; it
/// belongs to the decoder rather than the connection.
/// </para>
/// <para>
/// Each timer gets a pair of tests: one that it fires when it should, and one that it stays quiet
/// when it should. The second half is the half that catches a timer wired to the wrong event - a
/// T7 that keeps running after Select, say, which drops a link that was working perfectly.
/// </para>
/// </remarks>
public sealed class HsmsTimerTests
{
    [Fact]
    public async Task T3_expires_when_a_primary_gets_no_reply()
    {
        await using var link = TestLink.Start(active: true, configure: NoLinkTest);
        await link.SelectAsync();

        using var primary = new SecsMessage(1, 3) { Item = SecsItem.U4(7001u) };
        var sending = link.Connection.SendAsync(primary, Frames.Token).AsTask();

        using var sent = await link.ReadAsync();
        await link.AdvanceUntilAsync(link.Options.T3 / 10, () => sending.IsCompleted, "T3 to expire");

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => sending);
        Assert.Equal(SecsTimer.T3, error.Timer);
        Assert.Equal(link.Options.T3, error.Interval);
        Assert.Same(primary, error.SecsMessage);
    }

    [Fact]
    public async Task T3_is_reported_to_the_peer_as_S9F9()
    {
        await using var link = TestLink.Start(active: true, configure: NoLinkTest);
        await link.SelectAsync();

        using var primary = new SecsMessage(1, 3);
        var sending = link.Connection.SendAsync(primary, Frames.Token).AsTask();

        using var sent = await link.ReadAsync();
        await link.AdvanceUntilAsync(link.Options.T3 / 10, () => sending.IsCompleted, "T3 to expire");
        await Assert.ThrowsAsync<SecsTimeoutException>(() => sending);

        // E5 says the entity whose transaction timer expired reports it. secs4net never sends
        // this, so a peer that was merely slow has no idea its reply was abandoned.
        using var report = await link.ReadAsync();
        Assert.Equal(9, report.Header.Stream);
        Assert.Equal(9, report.Header.Function);
        Assert.False(report.Header.ReplyExpected);
        Assert.Equal(Frames.HeaderBytes(sent.Header), Frames.ReportedHeader(report));
    }

    [Fact]
    public async Task T3_reporting_can_be_turned_off()
    {
        using var acknowledgement = new SecsMessage(1, 2, replyExpected: false) { Item = SecsItem.A("ack") };

        await using var link = TestLink.Start(
            active: true,
            routes: routes => routes.OnPrimary(1, 1, async (transaction, token) =>
                await transaction.ReplyAsync(acknowledgement, token)),
            configure: options =>
            {
                NoLinkTest(options);
                options.SendS9F9OnReplyTimeout = false;
            });

        await link.SelectAsync();

        using var primary = new SecsMessage(1, 3);
        var sending = link.Connection.SendAsync(primary, Frames.Token).AsTask();

        using var sent = await link.ReadAsync();
        await link.AdvanceUntilAsync(link.Options.T3 / 10, () => sending.IsCompleted, "T3 to expire");
        await Assert.ThrowsAsync<SecsTimeoutException>(() => sending);

        // Asserted by what comes next rather than by waiting for nothing. An S1F1 is sent and
        // answered, and the very next frame out is that answer - so no S9F9 was ever queued in
        // front of it.
        using var request = new SecsMessage(1, 1);
        await link.SendDataAsync(request, systemBytes: 0x8001);

        using var next = await link.ReadAsync();
        Assert.Equal(1, next.Header.Stream);
        Assert.Equal(2, next.Header.Function);
    }

    [Fact]
    public async Task T3_does_not_run_for_a_message_that_asked_for_no_reply()
    {
        await using var link = TestLink.Start(active: true, configure: NoLinkTest);
        await link.SelectAsync();

        using var report = new SecsMessage(6, 11, replyExpected: false) { Item = SecsItem.U4(7001u) };
        await link.Connection.PostAsync(report, Frames.Token);

        using var sent = await link.ReadAsync();
        Assert.Equal(0, link.Connection.OpenTransactions);

        // No transaction was opened, so there is nothing for T3 to expire, however far the clock
        // moves. A timer armed per send rather than per transaction would have fired here and
        // reported a timeout for a message that was never going to be answered.
        link.Clock.Advance(link.Options.T3 * 100);
        await Task.Delay(20, Frames.Token);

        Assert.Equal(HsmsConnectionState.Selected, link.Connection.State);
        Assert.Equal(0, link.Connection.OpenTransactions);
    }

    [Fact]
    public async Task T6_expires_when_a_Linktest_gets_no_response()
    {
        await using var link = TestLink.Start(active: true, configure: NoLinkTest);
        await link.SelectAsync();

        var testing = link.Connection.LinkTestAsync(Frames.Token).AsTask();

        using var request = await link.ReadAsync();
        Assert.Equal(MessageType.LinkTestRequest, request.Header.MessageType);

        await link.AdvanceUntilAsync(link.Options.T6 / 10, () => testing.IsCompleted, "T6 to expire");

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => testing);
        Assert.Equal(SecsTimer.T6, error.Timer);
        Assert.Equal(link.Options.T6, error.Interval);
    }

    [Fact]
    public async Task T6_expiring_on_Select_drops_the_link()
    {
        await using var link = TestLink.Start(active: true, configure: NoLinkTest);

        using var request = await link.ReadAsync();
        Assert.Equal(MessageType.SelectRequest, request.Header.MessageType);

        // Never answered. A link that cannot be selected is no use, so it goes rather than sitting
        // in CONNECTED forever.
        await link.AdvanceUntilAsync(
            link.Options.T6 / 10,
            () => link.Connection.State == HsmsConnectionState.NotConnected,
            "the link to drop after T6");
    }

    [Fact]
    public async Task T7_expires_when_a_connected_link_is_never_selected()
    {
        await using var link = TestLink.Start(active: false, configure: NoLinkTest);
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        // Nobody selects. This is what a peer that crashed right after connecting leaves behind,
        // and without T7 it stays there forever.
        await link.AdvanceUntilAsync(
            link.Options.T7 / 10,
            () => link.Connection.State == HsmsConnectionState.NotConnected,
            "the link to drop after T7");
    }

    [Fact]
    public async Task T7_stops_once_the_link_is_selected()
    {
        await using var link = TestLink.Start(active: false, configure: NoLinkTest);
        await link.SelectAsync();

        // The pair to the test above, and the one that matters more. A T7 left running after
        // Select drops a link that is working, on a timer nobody is watching, minutes later.
        link.Clock.Advance(link.Options.T7 * 100);

        using var request = new SecsMessage(1, 1, replyExpected: false);
        await link.SendDataAsync(request, systemBytes: 0x8003);

        await Frames.WaitUntilAsync(
            () => link.Connection.State == HsmsConnectionState.Selected,
            "the link to still be selected");
        Assert.Equal(HsmsConnectionState.Selected, link.Connection.State);
    }

    [Fact]
    public async Task T7_runs_again_after_a_Deselect()
    {
        await using var link = TestLink.Start(active: false, configure: NoLinkTest);
        await link.SelectAsync();

        await link.SendAsync(HsmsMessage.Control(MessageType.DeselectRequest, 0x8004));
        using var response = await link.ReadAsync();
        await link.WaitForStateAsync(HsmsConnectionState.Connected);

        // Back in NOT SELECTED, so the half-open guard applies again. A peer that deselects and
        // then goes quiet must not be able to park the link indefinitely.
        await link.AdvanceUntilAsync(
            link.Options.T7 / 10,
            () => link.Connection.State == HsmsConnectionState.NotConnected,
            "the link to drop after T7 following a Deselect");
    }

    [Fact]
    public async Task T5_separates_connection_attempts()
    {
        await using var link = TestLink.Start(active: true, configure: NoLinkTest);
        await link.SelectAsync();

        await link.SendAsync(HsmsMessage.Control(MessageType.SeparateRequest, 0x8005));
        await link.WaitForStateAsync(HsmsConnectionState.NotConnected);

        // T5 is a floor on how often we knock. The transport here connects instantly, so without
        // T5 this would be a reconnect loop running as fast as the CPU allows against equipment
        // that is power cycling.
        var attemptsAfterDrop = link.Transitions.Count;
        link.Clock.Advance(link.Options.T5 / 4);
        await Task.Delay(20, Frames.Token);
        Assert.Equal(attemptsAfterDrop, link.Transitions.Count);

        await link.AdvanceUntilAsync(
            link.Options.T5 / 4,
            () => link.Connection.State == HsmsConnectionState.Connected,
            "the connection to be retried after T5");
    }

    [Fact]
    public async Task The_Linktest_interval_keeps_a_quiet_link_honest()
    {
        await using var link = TestLink.Start(
            active: true,
            configure: options => options.LinkTestInterval = TimeSpan.FromSeconds(30));

        await link.SelectAsync();

        // Reaching Selected means the Linktest timer is already armed - the connection arms it
        // before it publishes the state - so one jump past the interval is enough, and ReadAsync
        // then blocks until the request actually turns up.
        link.Clock.Advance(TimeSpan.FromSeconds(31));

        using var request = await link.ReadAsync();
        Assert.Equal(MessageType.LinkTestRequest, request.Header.MessageType);

        await link.SendAsync(HsmsMessage.Control(MessageType.LinkTestResponse, request.SystemBytes));

        // Answered, so the link stays up. A quiet link is not a dead one, and this is how the two
        // are told apart - not with T8, which measures a gap inside one message.
        await Frames.WaitUntilAsync(
            () => link.Connection.State == HsmsConnectionState.Selected,
            "the link to stay selected");
    }

    [Fact]
    public async Task An_unanswered_periodic_Linktest_drops_the_link()
    {
        await using var link = TestLink.Start(
            active: true,
            configure: options => options.LinkTestInterval = TimeSpan.FromSeconds(30));

        await link.SelectAsync();

        // The peer stops answering. That is a dead link, and T6 on the Linktest is what notices -
        // TCP would not report it for minutes.
        await link.AdvanceUntilAsync(
            TimeSpan.FromSeconds(3),
            () => link.Connection.State == HsmsConnectionState.NotConnected,
            "the link to drop after an unanswered Linktest");
    }

    private static void NoLinkTest(SecsGemOptions options) => options.LinkTestInterval = TimeSpan.Zero;
}
