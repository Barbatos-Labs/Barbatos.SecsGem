// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Spooling: holding reports for a host that is not listening, and handing them over when it asks.
/// </summary>
/// <remarks>
/// Every test here reaches "not communicating" the honest way - the link is up and selected, and
/// the host has denied S1F13 - rather than by tearing the transport down. That is a real GEM state
/// and it is the one in which a machine does most of its spooling: powered on, cabled, and not yet
/// talking to anybody.
/// </remarks>
public class GemSpoolingTests
{
    /// <summary>The transaction id for an S6F23 the test sends itself, without <c>RequestAsync</c>.</summary>
    private const int HostSystemBytes = 0x7001;

    private static double _pressure = 1_013.25d;

    private static void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "Pressure", () => SecsItem.F8(_pressure), "mbar")
            .AddEvent(1_000, "ProcessEnd")
            .AddEvent(1_001, "Quiet", enabledByDefault: false)
            .AddReport(5_000, 1)
            .AddAlarm(new GemAlarm { Id = 900, Text = "Pressure" });

    /// <summary>Selects, has the host deny S1F13, and links report 5000 to event 1000.</summary>
    /// <remarks>
    /// <paramref name="gate"/>, when given, is on the equipment's side of the link from the start, but
    /// holds nothing until the test arms it.
    /// </remarks>
    private static async Task<GemLink> NotCommunicatingAsync(
        IGemSpool? spool,
        Action<GemOptions>? configure = null,
        OutputGate? gate = null)
    {
        _pressure = 1_013.25d;

        var link = GemLink.Start(
            Model,
            options =>
            {
                // Links have to survive establishing, because these tests spool first and let the
                // host in afterwards.
                options.ResetLinkStateOnDisconnect = false;
                configure?.Invoke(options);
            },
            spool: spool,
            outputGate: gate);

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        await GemLink.WaitUntilAsync(() => !link.Equipment.IsCommunicating, "the equipment to be not communicating");

        using var linked = await link.RequestAsync(new SecsMessage(2, 35, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(SecsItem.U4(1_000u), SecsItem.L(SecsItem.U4(5_000u))))),
        });

        Assert.Equal((byte)GemLinkReportAcknowledgement.Accepted, linked.Item!.FirstValueOrDefault<byte>());

        return link;
    }

    /// <summary>Lets the host establish communications from its own side.</summary>
    private static async Task CommunicateAsync(GemLink link)
    {
        using var request = new SecsMessage(1, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("HOST"), SecsItem.A("1.0")),
        };

        using var reply = await link.RequestAsync(request);
        Assert.Equal((byte)GemCommunicationAcknowledgement.Accepted, reply.Item![0].FirstValueOrDefault<byte>());

        await GemLink.WaitUntilAsync(() => link.Equipment.IsCommunicating, "the equipment to be communicating");
    }

    // ----------------------------------------------------------------- opt-in

    [Fact]
    public void S6F23_is_not_in_the_core_declaration()
    {
        Assert.DoesNotContain(GemEquipment.SpoolMessages[0], GemEquipment.HandledMessages);
        Assert.Equal((6, 23), ((int)GemEquipment.SpoolMessages[0].Stream, (int)GemEquipment.SpoolMessages[0].Function));
    }

    [Fact]
    public async Task Without_a_spool_a_report_that_cannot_be_delivered_is_dropped()
    {
        await using var link = await NotCommunicatingAsync(spool: null);

        Assert.False(link.Equipment.IsSpooling);
        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));

        Assert.Equal(0, link.Equipment.SpoolCountActual);
        Assert.Equal(GemSpoolState.Inactive, link.Equipment.SpoolState);

        // And nothing is waiting for the host when it does turn up.
        await CommunicateAsync(link);
        Assert.Equal(0, link.Equipment.SpoolCountActual);
    }

    [Fact]
    public async Task Spooling_can_be_turned_off_by_configuration_without_removing_the_spool()
    {
        await using var link = await NotCommunicatingAsync(
            new GemInMemorySpool(),
            options => options.SpoolingEnabled = false);

        Assert.False(link.Equipment.IsSpooling);
        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        Assert.Equal(0, link.Equipment.SpoolCountActual);
    }

    // ----------------------------------------------------------------- holding

    [Fact]
    public async Task A_report_that_cannot_be_delivered_is_held()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        Assert.True(link.Equipment.IsSpooling);

        // False, because nothing reached the host - and it was kept, which is what the count says.
        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));

        Assert.Equal(1, link.Equipment.SpoolCountActual);
        Assert.Equal(1, link.Equipment.SpoolCountTotal);
        Assert.Equal(0, link.Equipment.SpoolCountDiscarded);
        Assert.Equal(GemSpoolState.Active, link.Equipment.SpoolState);
        Assert.NotNull(link.Equipment.SpoolStartTime);
    }

    [Fact]
    public async Task An_event_the_host_disabled_is_not_held()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        Assert.False(await link.Equipment.TriggerEventAsync(1_001, GemLink.Token));

        // Holding a report the host switched off would only deliver it later, which is not what
        // switching it off meant.
        Assert.Equal(0, link.Equipment.SpoolCountActual);
        Assert.Equal(GemSpoolState.Inactive, link.Equipment.SpoolState);
    }

    [Fact]
    public async Task An_alarm_is_held_too()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        Assert.False(await link.Equipment.SetAlarmAsync(900, GemLink.Token));

        // The S5F1 and the collection event linked to it - two messages, two subscriptions.
        Assert.True(link.Equipment.SpoolCountActual >= 1);
        Assert.True(link.Equipment.IsAlarmSet(900));
    }

    // ----------------------------------------------------------------- the point of it

    [Fact]
    public async Task A_held_report_carries_the_values_from_when_the_event_happened()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        _pressure = 500d;
        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));

        // The machine moves on while the host is away.
        _pressure = 1_200d;

        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal(24, rsda.F);
        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        using var report = await link.ReceiveAsync();

        // 500, not 1200. This is the whole reason a spool holds an encoded report rather than a
        // note to go and read the variables later: the values belong to the moment the event
        // happened, and a report timestamped an hour ago carrying today's pressure is worse than
        // no report at all.
        Assert.Equal(6, report.S);
        Assert.Equal(11, report.F);
        Assert.Equal(500d, report.Item![2][0][1][0].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task Held_reports_are_handed_over_in_the_order_they_happened()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        foreach (var pressure in new[] { 100d, 200d, 300d })
        {
            _pressure = pressure;
            Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        }

        Assert.Equal(3, link.Equipment.SpoolCountActual);

        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        foreach (var expected in new[] { 100d, 200d, 300d })
        {
            using var report = await link.ReceiveAsync();
            Assert.Equal(expected, report.Item![2][0][1][0].FirstValueOrDefault<double>());
        }

        // Waiting on the state rather than the count, because the state is the summary: it goes
        // Inactive in the same step that takes the count to zero.
        await GemLink.WaitUntilAsync(
            () => link.Equipment.SpoolState == GemSpoolState.Inactive,
            "the spool to go inactive");

        Assert.Equal(0, link.Equipment.SpoolCountActual);
        Assert.Null(link.Equipment.SpoolStartTime);
    }

    [Fact]
    public async Task The_host_may_throw_the_backlog_away_instead()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        Assert.Equal(1, link.Equipment.SpoolCountActual);

        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Purge),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(0, link.Equipment.SpoolCountActual);
        Assert.Equal(GemSpoolState.Inactive, link.Equipment.SpoolState);

        // A clean start is a decision the host is entitled to make, so nothing follows.
        using var alive = await link.RequestAsync(new SecsMessage(1, 1, replyExpected: true));
        Assert.Equal(2, alive.F);
    }

    [Fact]
    public async Task Asking_to_transmit_an_empty_spool_is_denied()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());
        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Denied, rsda.Item!.FirstValueOrDefault<byte>());
    }

    [Theory]
    [InlineData("U1")]
    [InlineData("U2")]
    [InlineData("U4")]
    public async Task RSDC_is_read_in_whatever_numeric_format_the_host_chose(string format)
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        await CommunicateAsync(link);

        SecsItem command = format switch
        {
            "U1" => SecsItem.U1(1),
            "U2" => SecsItem.U2(1),
            _ => SecsItem.U4(1u),
        };

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true) { Item = command });

        // Purge, in three formats. The catalogue flags S6F23 as a representative subset and gives
        // RSDC as U2, while E30 says U1 - so being strict about the width here would mean failing
        // against a host built from the other reading.
        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(0, link.Equipment.SpoolCountActual);
    }

    [Fact]
    public async Task A_transmit_burst_can_be_capped()
    {
        await using var link = await NotCommunicatingAsync(
            new GemInMemorySpool(),
            options => options.MaxSpoolTransmit = 2);

        foreach (var pressure in new[] { 100d, 200d, 300d })
        {
            _pressure = pressure;
            Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        }

        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        using var first = await link.ReceiveAsync();
        using var second = await link.ReceiveAsync();

        Assert.Equal(100d, first.Item![2][0][1][0].FirstValueOrDefault<double>());
        Assert.Equal(200d, second.Item![2][0][1][0].FirstValueOrDefault<double>());

        // The third is still held, and the host has to ask again - which is the point. A thousand
        // transactions the moment a link recovers is how equipment knocks over the host that was
        // waiting for it.
        await GemLink.WaitUntilAsync(() => link.Equipment.SpoolCountActual == 1, "one report to remain held");
        Assert.Equal(GemSpoolState.Active, link.Equipment.SpoolState);
    }

    // ----------------------------------------------------------------- the answer comes first

    [Fact]
    public async Task The_transmit_does_not_begin_until_the_acknowledgement_is_on_the_wire()
    {
        using var gate = new OutputGate();
        var spool = new PeekCountingSpool(new GemInMemorySpool());

        await using var link = await NotCommunicatingAsync(spool, gate: gate);

        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        await CommunicateAsync(link);

        // The equipment looks at the head of the spool once at start-up, to adopt whatever a
        // durable spool was holding, and once more every time a transmit begins. So a rise in this
        // count is the transmit starting.
        var before = spool.Peeks;

        // The next frame the equipment flushes is the S6F24, and it is held on its way out.
        gate.HoldNextFlush();

        using (var request = new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        })
        {
            await link.SendDataAsync(request, HostSystemBytes);
        }

        await gate.WhenHeld.WaitAsync(TimeSpan.FromSeconds(10), GemLink.Token);

        // Long enough for a transmit that was going to start to have started. It is one channel
        // read away - microseconds on an idle machine - and this is thousands of times that. A wall
        // clock, because "nothing happened" has no event to wait on, and it cannot make this flaky:
        // it never fails correct equipment however loaded the machine is, and can only let a
        // violation slip past if the thread pool stalls for a tenth of a second.
        await Task.Delay(TimeSpan.FromMilliseconds(100), GemLink.Token);

        // A transmit is one channel read from starting the moment it is queued, so one queued ahead
        // of the S6F24 would have read the spool by now - with the answer still in the writer, and
        // on a bad turn of the scheduler the first report on the wire in front of it. The other
        // tests in this class read the wire and see whichever order the scheduler chose, which is
        // the right one nearly every time; this one holds the answer back and watches.
        Assert.True(
            spool.Peeks == before,
            "The equipment began transmitting while its S6F24 had not yet been flushed.");

        gate.Release();

        // And once the answer is out, the data follows it - in that order.
        using (var answer = await link.ReadAsync())
        {
            Assert.Equal(HostSystemBytes, answer.Header.SystemBytes);
            Assert.Equal(24, answer.Data!.F);
            Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, answer.Data.Item!.FirstValueOrDefault<byte>());
        }

        using var report = await link.ReceiveAsync();
        Assert.Equal(11, report.F);
    }

    [Fact]
    public async Task A_transmit_asked_for_without_a_reply_is_still_made()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        await CommunicateAsync(link);

        // W clear: the host is not waiting for an S6F24, and its request is no less a request. The
        // answer and the transmit are two decisions that happen to be taken together, and this is
        // the case that tells them apart - a transmit nested inside "send the answer" would pass
        // every other test in this class and never start here.
        using (var request = new SecsMessage(6, 23, replyExpected: false)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        })
        {
            await link.SendDataAsync(request, HostSystemBytes);
        }

        using var report = await link.ReceiveAsync();

        Assert.Equal(6, report.S);
        Assert.Equal(11, report.F);
    }

    // ----------------------------------------------------------------- full

    [Fact]
    public async Task A_full_spool_keeps_the_oldest_by_default()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool(capacity: 2));

        foreach (var pressure in new[] { 100d, 200d, 300d })
        {
            _pressure = pressure;
            Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        }

        Assert.Equal(2, link.Equipment.SpoolCountActual);
        Assert.Equal(1, link.Equipment.SpoolCountDiscarded);
        Assert.Equal(GemSpoolState.Full, link.Equipment.SpoolState);
        Assert.NotNull(link.Equipment.SpoolFullTime);

        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        // 100 and 200 - the beginning of what the host missed, which is the part that explains the
        // rest.
        using var first = await link.ReceiveAsync();
        using var second = await link.ReceiveAsync();

        Assert.Equal(100d, first.Item![2][0][1][0].FirstValueOrDefault<double>());
        Assert.Equal(200d, second.Item![2][0][1][0].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task A_full_spool_can_be_told_to_keep_the_newest_instead()
    {
        await using var link = await NotCommunicatingAsync(
            new GemInMemorySpool(capacity: 2, overflow: GemSpoolOverflowPolicy.DiscardOldest));

        foreach (var pressure in new[] { 100d, 200d, 300d })
        {
            _pressure = pressure;
            Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
        }

        // Still two held and one lost, and the count is the spool's rather than a tally of our own
        // calls - which is what makes it right under this policy.
        Assert.Equal(2, link.Equipment.SpoolCountActual);
        Assert.Equal(1, link.Equipment.SpoolCountDiscarded);
        Assert.Equal(GemSpoolState.Full, link.Equipment.SpoolState);

        await CommunicateAsync(link);

        using var rsda = await link.RequestAsync(new SecsMessage(6, 23, replyExpected: true)
        {
            Item = SecsItem.U1((byte)GemSpooledDataRequest.Transmit),
        });

        Assert.Equal((byte)GemSpooledDataAcknowledgement.Accepted, rsda.Item!.FirstValueOrDefault<byte>());

        using var first = await link.ReceiveAsync();
        using var second = await link.ReceiveAsync();

        Assert.Equal(200d, first.Item![2][0][1][0].FirstValueOrDefault<double>());
        Assert.Equal(300d, second.Item![2][0][1][0].FirstValueOrDefault<double>());
    }

    // ----------------------------------------------------------------- the pieces on their own

    [Fact]
    public void A_spooled_message_survives_encoding_and_decoding()
    {
        using var original = new SecsMessage(6, 11, replyExpected: true)
        {
            Name = "EventReportSend",
            Item = SecsItem.L(SecsItem.U4(1u), SecsItem.A("LOT-1"), SecsItem.F8(1013.25d)),
        };

        var held = GemSpooledMessage.From(original, DateTimeOffset.UnixEpoch);

        Assert.Equal(6, held.Stream);
        Assert.Equal(11, held.Function);
        Assert.True(held.ReplyExpected);
        Assert.Equal("EventReportSend", held.Name);
        Assert.False(held.Body.IsEmpty);

        using var restored = held.ToMessage();

        Assert.Equal(original, restored);
    }

    [Fact]
    public void A_message_with_no_body_spools_and_comes_back_with_no_body()
    {
        using var original = new SecsMessage(1, 1, replyExpected: true);

        var held = GemSpooledMessage.From(original, DateTimeOffset.UnixEpoch);
        Assert.True(held.Body.IsEmpty);

        using var restored = held.ToMessage();
        Assert.Null(restored.Item);
    }

    [Fact]
    public async Task The_in_memory_spool_hands_things_back_in_order_and_only_once_committed()
    {
        var spool = new GemInMemorySpool(capacity: 4);
        var token = TestContext.Current.CancellationToken;

        for (var i = 1; i <= 3; i++)
        {
            Assert.True(await spool.TryAppendAsync(
                new GemSpooledMessage(6, 11, false, new byte[] { (byte)i }, DateTimeOffset.UnixEpoch),
                token));
        }

        Assert.Equal(3, await spool.CountAsync(token));

        // Peeking twice gives the same message: nothing leaves until it has been dealt with.
        var first = await spool.TryPeekAsync(token);
        var again = await spool.TryPeekAsync(token);

        Assert.Equal(1, first!.Body.Span[0]);
        Assert.Equal(1, again!.Body.Span[0]);
        Assert.Equal(3, await spool.CountAsync(token));

        Assert.True(await spool.RemoveHeadAsync(token));
        Assert.Equal(2, (await spool.TryPeekAsync(token))!.Body.Span[0]);

        await spool.PurgeAsync(token);
        Assert.Equal(0, await spool.CountAsync(token));
        Assert.Null(await spool.TryPeekAsync(token));
        Assert.False(await spool.RemoveHeadAsync(token));
    }

    [Fact]
    public void A_spool_that_holds_nothing_is_refused_outright()
    {
        // A capacity of zero is a slower way of dropping the message, so it is a configuration
        // error rather than a valid choice.
        Assert.Throws<ArgumentOutOfRangeException>(() => new GemInMemorySpool(capacity: 0));
    }

    /// <summary>A spool that counts how often it is asked for its head, which is how a transmit begins.</summary>
    private sealed class PeekCountingSpool(IGemSpool inner) : IGemSpool
    {
        private int _peeks;

        public int Peeks => Volatile.Read(ref _peeks);

        public ValueTask<int> CountAsync(CancellationToken cancellationToken)
            => inner.CountAsync(cancellationToken);

        public ValueTask<bool> TryAppendAsync(GemSpooledMessage message, CancellationToken cancellationToken)
            => inner.TryAppendAsync(message, cancellationToken);

        public ValueTask<GemSpooledMessage?> TryPeekAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _peeks);
            return inner.TryPeekAsync(cancellationToken);
        }

        public ValueTask<bool> RemoveHeadAsync(CancellationToken cancellationToken)
            => inner.RemoveHeadAsync(cancellationToken);

        public ValueTask PurgeAsync(CancellationToken cancellationToken)
            => inner.PurgeAsync(cancellationToken);
    }
}
