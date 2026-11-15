// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>The GEM message set, over a real link.</summary>
public class GemMessageTests
{
    private static SecsItem DataIdList(params SecsItem[] entries)
        => SecsItem.L(SecsItem.U4(1u), SecsItem.L(entries));

    private static SecsItem Entry(uint id, params uint[] members)
        => SecsItem.L(SecsItem.U4(id), SecsItem.L([.. members.Select(m => SecsItem.U4(m))]));

    // ----------------------------------------------------------------- S1F3

    [Fact]
    public async Task An_empty_status_request_answers_every_status_variable_and_no_data_variable()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 3, replyExpected: true) { Item = SecsItem.L() });

        // Two status variables are configured; the data variable is not a status variable and S1F3
        // is not the message that reports it.
        Assert.Equal(4, reply.F);
        Assert.Equal(2, reply.Item!.Count);
        Assert.Equal(5, reply.Item[0].FirstValueOrDefault<byte>());
        Assert.Equal(1013.25d, reply.Item[1].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task A_status_request_answers_in_the_order_it_was_asked()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(2u), SecsItem.U4(1u)),
        });

        Assert.Equal(1013.25d, reply.Item![0].FirstValueOrDefault<double>());
        Assert.Equal(5, reply.Item[1].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task An_unknown_status_variable_is_a_zero_length_item_in_its_place()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(1u), SecsItem.U4(999u), SecsItem.U4(2u)),
        });

        // In its place, not dropped. Dropping it would shift every later value by one and the host
        // would read the pressure as the thing it asked for third.
        Assert.Equal(3, reply.Item!.Count);
        Assert.Equal(0, reply.Item[1].Count);
        Assert.Equal(1013.25d, reply.Item[2].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task Asking_S1F3_for_a_data_variable_gets_a_zero_length_item()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(10u)),
        });

        Assert.Equal(0, reply.Item![0].Count);
    }

    [Theory]
    [InlineData("U1")]
    [InlineData("U2")]
    [InlineData("U4")]
    [InlineData("U8")]
    [InlineData("I4")]
    [InlineData("ASCII")]
    public async Task An_identifier_is_read_in_whatever_numeric_format_the_host_chose(string format)
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        SecsItem id = format switch
        {
            "U1" => SecsItem.U1(2),
            "U2" => SecsItem.U2(2),
            "U4" => SecsItem.U4(2u),
            "U8" => SecsItem.U8(2ul),
            "I4" => SecsItem.I4(2),
            _ => SecsItem.A("2"),
        };

        using var reply = await link.RequestAsync(new SecsMessage(1, 3, replyExpected: true)
        {
            Item = SecsItem.L(id),
        });

        // GEM says an identifier is "a number", not "a U4". Equipment that only accepts U4 works
        // with the host it was tested against and fails with the next one, which is the single most
        // common shape of SECS interop bug.
        Assert.Equal(1013.25d, reply.Item![0].FirstValueOrDefault<double>());
    }

    // ----------------------------------------------------------------- S2F13 and S2F15

    [Fact]
    public async Task An_equipment_constant_can_be_read()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(100u)),
        });

        Assert.Equal(14, reply.F);
        Assert.Equal(42u, reply.Item![0].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task Setting_a_read_only_constant_is_refused_without_changing_anything()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 15, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.L(SecsItem.U4(100u), SecsItem.U4(7u))),
        });

        Assert.Equal(16, reply.F);
        Assert.Equal((byte)GemEquipmentConstantAcknowledgement.UnknownConstant, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_writable_constant_takes_the_new_value()
    {
        var stored = 10u;

        await using var link = GemLink.Start(model: builder =>
        {
            GemLink.DefaultModel(builder);
            builder.AddConstant(new GemEquipmentConstant
            {
                Id = 101,
                Name = "Setpoint",
                Read = () => SecsItem.U4(stored),
                Write = item =>
                {
                    var candidate = item.FirstValueOrDefault<uint>();

                    if (candidate > 100)
                    {
                        return false;
                    }

                    stored = candidate;
                    return true;
                },
            });
        });

        await link.OpenAsync();

        using var accepted = await link.RequestAsync(new SecsMessage(2, 15, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.L(SecsItem.U4(101u), SecsItem.U4(55u))),
        });

        Assert.Equal((byte)GemEquipmentConstantAcknowledgement.Accepted, accepted.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(55u, stored);

        using var refused = await link.RequestAsync(new SecsMessage(2, 15, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.L(SecsItem.U4(101u), SecsItem.U4(500u))),
        });

        Assert.Equal((byte)GemEquipmentConstantAcknowledgement.OutOfRange, refused.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(55u, stored);
    }

    // ----------------------------------------------------------------- the clock

    [Fact]
    public async Task The_equipment_reports_a_sixteen_character_timestamp()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 17, replyExpected: true));

        Assert.Equal(18, reply.F);
        var text = reply.Item!.GetString();

        Assert.Equal(16, text.Length);
        Assert.True(DateTime.TryParseExact(text, "yyyyMMddHHmmssff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
    }

    [Fact]
    public async Task A_host_clock_set_moves_what_the_equipment_reports_and_nothing_else()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        var before = link.Equipment.Now;

        using var reply = await link.RequestAsync(new SecsMessage(2, 31, replyExpected: true)
        {
            Item = SecsItem.A("20301225093000" + "00"),
        });

        Assert.Equal(32, reply.F);
        Assert.Equal((byte)GemTimeAcknowledgement.Accepted, reply.Item!.FirstValueOrDefault<byte>());

        // An offset, not a clock change: the fake clock the connection runs on has not moved.
        Assert.Equal(2030, link.Equipment.Now.Year);
        Assert.NotEqual(before.Year, link.Equipment.Now.Year);

        using var now = await link.RequestAsync(new SecsMessage(2, 17, replyExpected: true));
        Assert.StartsWith("2030", now.Item!.GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_clock_set_can_be_refused()
    {
        await using var link = GemLink.Start(configure: o => o.AcceptHostClockSet = false);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 31, replyExpected: true)
        {
            Item = SecsItem.A("2030122509300000"),
        });

        Assert.Equal((byte)GemTimeAcknowledgement.Error, reply.Item!.FirstValueOrDefault<byte>());
        Assert.NotEqual(2030, link.Equipment.Now.Year);
    }

    // ----------------------------------------------------------------- S2F33, S2F35, S2F37

    [Fact]
    public async Task A_host_defines_reports_links_them_and_enables_the_event()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var defined = await link.RequestAsync(new SecsMessage(2, 33, replyExpected: true)
        {
            Item = DataIdList(Entry(6_000, 1, 2)),
        });

        Assert.Equal(34, defined.F);
        Assert.Equal((byte)GemDefineReportAcknowledgement.Accepted, defined.Item!.FirstValueOrDefault<byte>());

        using var linked = await link.RequestAsync(new SecsMessage(2, 35, replyExpected: true)
        {
            Item = DataIdList(Entry(1_000, 6_000)),
        });

        Assert.Equal(36, linked.F);
        Assert.Equal((byte)GemLinkReportAcknowledgement.Accepted, linked.Item!.FirstValueOrDefault<byte>());

        using var enabled = await link.RequestAsync(new SecsMessage(2, 37, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.Boolean(true), SecsItem.L(SecsItem.U4(1_000u))),
        });

        Assert.Equal(38, enabled.F);
        Assert.Equal((byte)GemEnableEventAcknowledgement.Accepted, enabled.Item!.FirstValueOrDefault<byte>());
        Assert.Equal<uint[]>([6_000], [.. link.Equipment.Model.ReportsForEvent(1_000)]);
    }

    [Fact]
    public async Task A_report_naming_an_unknown_variable_is_refused_on_the_wire_too()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 33, replyExpected: true)
        {
            Item = DataIdList(Entry(6_000, 1, 404)),
        });

        Assert.Equal((byte)GemDefineReportAcknowledgement.UnknownVariable, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_malformed_report_definition_is_an_invalid_format_rather_than_a_dropped_link()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 33, replyExpected: true)
        {
            Item = SecsItem.A("not a report definition"),
        });

        // The host gets DRACK 2 and the link carries on. A body this stack cannot parse is not a
        // reason to drop a connection that is otherwise working.
        Assert.Equal((byte)GemDefineReportAcknowledgement.InvalidFormat, reply.Item!.FirstValueOrDefault<byte>());

        using var alive = await link.RequestAsync(new SecsMessage(1, 1, replyExpected: true));
        Assert.Equal(2, alive.F);
    }

    // ----------------------------------------------------------------- S6F11

    [Fact]
    public async Task Triggering_an_event_sends_the_reports_linked_to_it()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var linked = await link.RequestAsync(new SecsMessage(2, 35, replyExpected: true)
        {
            Item = DataIdList(Entry(1_000, 5_000)),
        });

        Assert.Equal((byte)GemLinkReportAcknowledgement.Accepted, linked.Item!.FirstValueOrDefault<byte>());

        var trigger = link.Equipment.TriggerEventAsync(1_000, GemLink.Token);
        using var report = await link.ReceiveAsync();

        Assert.True(await trigger);
        Assert.Equal(6, report.S);
        Assert.Equal(11, report.F);
        Assert.Equal(1_000u, report.Item![1].FirstValueOrDefault<uint>());

        var reports = report.Item[2];
        Assert.Equal(1, reports.Count);
        Assert.Equal(5_000u, reports[0][0].FirstValueOrDefault<uint>());

        var values = reports[0][1];
        Assert.Equal(2, values.Count);
        Assert.Equal(5, values[0].FirstValueOrDefault<byte>());
        Assert.Equal(1013.25d, values[1].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task A_disabled_event_sends_nothing_and_says_so()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var disabled = await link.RequestAsync(new SecsMessage(2, 37, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.Boolean(false), SecsItem.L(SecsItem.U4(1_000u))),
        });

        Assert.Equal((byte)GemEnableEventAcknowledgement.Accepted, disabled.Item!.FirstValueOrDefault<byte>());

        // False rather than an exception: a host that disabled an event has said it does not want
        // it, and ordinary operation should not look like a fault.
        Assert.False(await link.Equipment.TriggerEventAsync(1_000, GemLink.Token));
    }

    [Fact]
    public async Task An_unknown_event_is_not_reported()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        Assert.False(await link.Equipment.TriggerEventAsync(123_456, GemLink.Token));
    }

    [Fact]
    public async Task An_event_with_nothing_linked_is_still_reported()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        var trigger = link.Equipment.TriggerEventAsync(1_001, GemLink.Token);
        using var report = await link.ReceiveAsync();

        Assert.True(await trigger);

        // E30 reports the event whether or not the host linked a report to it. The host asked to
        // know it happened; the reports are a separate subscription.
        Assert.Equal(1_001u, report.Item![1].FirstValueOrDefault<uint>());
        Assert.Equal(0, report.Item[2].Count);
    }

    [Fact]
    public async Task An_event_report_can_be_sent_without_asking_for_an_acknowledgement()
    {
        await using var link = GemLink.Start(configure: o => o.EventReportsExpectReply = false);
        await link.OpenAsync();

        var trigger = link.Equipment.TriggerEventAsync(1_001, GemLink.Token);
        using var report = await link.ReceiveAsync();

        Assert.True(await trigger);
        Assert.False(report.ReplyExpected);
    }

    // ----------------------------------------------------------------- S2F41

    [Fact]
    public async Task A_remote_command_runs_and_sees_its_parameters()
    {
        string? seenLot = null;
        uint? seenSlot = null;

        await using var link = GemLink.Start(model: builder =>
        {
            GemLink.DefaultModel(builder);
            builder.AddRemoteCommand("START", (context, _) =>
            {
                seenLot = context.GetText("LOTID");
                seenSlot = context.GetValue<uint>("SLOT");
                return ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted);
            });
        });

        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 41, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("START"),
                SecsItem.L(
                    SecsItem.L(SecsItem.A("LOTID"), SecsItem.A("LOT-42")),
                    SecsItem.L(SecsItem.A("SLOT"), SecsItem.U4(13u)))),
        });

        Assert.Equal(42, reply.F);
        Assert.Equal((byte)GemHostCommandAcknowledgement.Accepted, reply.Item![0].FirstValueOrDefault<byte>());
        Assert.Equal("LOT-42", seenLot);
        Assert.Equal(13u, seenSlot);
    }

    [Fact]
    public async Task An_unknown_remote_command_is_refused()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 41, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("NOPE"), SecsItem.L()),
        });

        Assert.Equal((byte)GemHostCommandAcknowledgement.InvalidCommand, reply.Item![0].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_remote_command_is_refused_unless_the_host_is_actually_in_charge()
    {
        var ran = false;

        await using var link = GemLink.Start(
            model: builder =>
            {
                GemLink.DefaultModel(builder);
                builder.AddRemoteCommand("START", (_, _) =>
                {
                    ran = true;
                    return ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted);
                });
            },
            configure: o => o.DefaultControlState = GemControlState.OnlineLocal);

        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 41, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("START"), SecsItem.L()),
        });

        // This is what the control state model is for. On-line local means a person at the machine
        // is in charge, and a command that changes what it does is not the host's to give.
        Assert.Equal((byte)GemHostCommandAcknowledgement.CannotPerformNow, reply.Item![0].FirstValueOrDefault<byte>());
        Assert.False(ran);
    }

    [Fact]
    public async Task A_command_that_only_reads_can_be_allowed_in_local_control()
    {
        await using var link = GemLink.Start(
            model: builder =>
            {
                GemLink.DefaultModel(builder);
                builder.AddRemoteCommand(
                    "PP-SELECT",
                    (_, _) => ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted),
                    requiresRemoteControl: false);
            },
            configure: o => o.DefaultControlState = GemControlState.OnlineLocal);

        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 41, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("PP-SELECT"), SecsItem.L()),
        });

        Assert.Equal((byte)GemHostCommandAcknowledgement.Accepted, reply.Item![0].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_remote_command_handler_that_throws_does_not_cost_the_link()
    {
        await using var link = GemLink.Start(model: builder =>
        {
            GemLink.DefaultModel(builder);
            builder.AddRemoteCommand("BOOM", (_, _) => throw new InvalidOperationException("the application is wrong"));
        });

        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 41, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("BOOM"), SecsItem.L()),
        });

        // A handler that throws is a bug in the application, not a reason to drop a link that is
        // serving everything else perfectly well.
        Assert.Equal((byte)GemHostCommandAcknowledgement.CannotPerformNow, reply.Item![0].FirstValueOrDefault<byte>());

        using var alive = await link.RequestAsync(new SecsMessage(1, 1, replyExpected: true));
        Assert.Equal(2, alive.F);
    }

    // ----------------------------------------------------------------- stream 5

    [Fact]
    public async Task Setting_an_alarm_reports_it_and_raises_the_event_linked_to_it()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        var set = link.Equipment.SetAlarmAsync(9_000, GemLink.Token);

        using var alarm = await link.ReceiveAsync();
        using var raised = await link.ReceiveAsync();

        Assert.True(await set);

        Assert.Equal(5, alarm.S);
        Assert.Equal(1, alarm.F);

        // ALCD carries the category in the low bits and "is set" in bit 7, which is why a set and a
        // clear are the same message with one bit between them.
        Assert.Equal(0x80, alarm.Item![0].FirstValueOrDefault<byte>());
        Assert.Equal(9_000u, alarm.Item[1].FirstValueOrDefault<uint>());
        Assert.Equal("Chamber pressure out of range", alarm.Item[2].GetString());

        Assert.Equal(6, raised.S);
        Assert.Equal(2_000u, raised.Item![1].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task Clearing_an_alarm_clears_the_bit_and_raises_the_other_event()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        var clear = link.Equipment.ClearAlarmAsync(9_000, GemLink.Token);

        using var alarm = await link.ReceiveAsync();
        using var raised = await link.ReceiveAsync();

        Assert.True(await clear);
        Assert.Equal(0x00, alarm.Item![0].FirstValueOrDefault<byte>());
        Assert.Equal(2_001u, raised.Item![1].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task A_disabled_alarm_sends_no_S5F1_but_its_event_still_fires()
    {
        await using var link = GemLink.Start();
        await link.OpenAsync();

        using var disabled = await link.RequestAsync(new SecsMessage(5, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.Boolean(false), SecsItem.U4(9_000u)),
        });

        Assert.Equal(4, disabled.F);
        Assert.Equal((byte)GemAlarmAcknowledgement.Accepted, disabled.Item!.FirstValueOrDefault<byte>());

        var set = link.Equipment.SetAlarmAsync(9_000, GemLink.Token);
        using var raised = await link.ReceiveAsync();

        Assert.True(await set);

        // The alarm report and the collection event are two separate subscriptions. A host that
        // turned off S5F1 may still be watching the event.
        Assert.Equal(6, raised.S);
        Assert.Equal(11, raised.F);
    }

    [Fact]
    public async Task Alarm_text_longer_than_E30_allows_is_truncated_rather_than_refused()
    {
        await using var link = GemLink.Start(model: builder =>
        {
            builder
                .AddEvent(1, "Set")
                .AddAlarm(new GemAlarm { Id = 1, Text = new string('x', 200) });
        });

        await link.OpenAsync();

        var set = link.Equipment.SetAlarmAsync(1, GemLink.Token);
        using var alarm = await link.ReceiveAsync();

        Assert.True(await set);
        Assert.Equal(GemOptions.MaxAlarmTextLength, alarm.Item![2].GetString().Length);
    }

    [Fact]
    public async Task Nothing_is_reported_before_communication_is_established()
    {
        await using var link = GemLink.Start();
        await link.SelectAsync();

        using var request = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        // The link is selected and the event is enabled, and still nothing goes out. That is the
        // difference E30 draws between a link and a conversation.
        Assert.False(await link.Equipment.TriggerEventAsync(1_001, GemLink.Token));
        Assert.False(await link.Equipment.SetAlarmAsync(9_000, GemLink.Token));
    }
}
