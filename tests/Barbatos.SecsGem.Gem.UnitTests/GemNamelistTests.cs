// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The messages a host uses to find out what the equipment has, rather than to read it.
/// </summary>
/// <remarks>
/// S1F11, S2F29, S5F5 and S5F7 are how a host discovers an unfamiliar machine - and S6F15 and
/// S6F19 are how it asks for a report now instead of waiting for the event.
/// </remarks>
public class GemNamelistTests
{
    private static void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusVariable(2, "Pressure", () => SecsItem.F8(1013.25d), "mbar")
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-001"))
            .AddConstant(new GemEquipmentConstant
            {
                Id = 100,
                Name = "Setpoint",
                Units = "mbar",
                Read = () => SecsItem.F8(1_013.25d),
                Write = _ => true,
                Minimum = () => SecsItem.F8(100d),
                Maximum = () => SecsItem.F8(2_000d),
                Default = () => SecsItem.F8(1_013.25d),
            })
            .AddConstant(new GemEquipmentConstant
            {
                Id = 101,
                Name = "Undocumented",
                Read = () => SecsItem.U4(7u),
            })
            .AddEvent(1_000, "ProcessEnd")
            .AddEvent(1_001, "Quiet", enabledByDefault: false)
            .AddReport(5_000, 1, 2)
            .AddAlarm(new GemAlarm { Id = 900, Text = "Pressure" })
            .AddAlarm(new GemAlarm { Id = 901, Text = "Temperature", EnabledByDefault = false });

    // ----------------------------------------------------------------- S1F11

    [Fact(Timeout = 15_000)]
    public async Task A_status_namelist_reports_every_status_variable_with_its_name_and_units()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 11, replyExpected: true) { Item = SecsItem.L() });

        Assert.Equal(12, reply.F);
        Assert.Equal(2, reply.Item!.Count);

        Assert.Equal(1u, reply.Item[0][0].FirstValueOrDefault<uint>());
        Assert.Equal("ControlState", reply.Item[0][1].GetString());
        Assert.Equal(string.Empty, reply.Item[0][2].GetString());

        Assert.Equal("Pressure", reply.Item[1][1].GetString());
        Assert.Equal("mbar", reply.Item[1][2].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_unknown_identifier_in_a_namelist_gets_an_entry_with_no_name()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 11, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(2u), SecsItem.U4(999u)),
        });

        // The entry is still there, carrying the identifier the host asked about and nothing else.
        // E5 keeps the reply aligned with the request rather than dropping what it cannot answer.
        Assert.Equal(2, reply.Item!.Count);
        Assert.Equal(999u, reply.Item[1][0].FirstValueOrDefault<uint>());
        Assert.Equal(string.Empty, reply.Item[1][1].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_data_variable_is_not_in_the_status_namelist()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 11, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(10u)),
        });

        Assert.Equal(string.Empty, reply.Item![0][1].GetString());
    }

    // ----------------------------------------------------------------- S2F29

    [Fact(Timeout = 15_000)]
    public async Task A_constant_namelist_reports_the_bounds_when_they_are_documented()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 29, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(100u)),
        });

        Assert.Equal(30, reply.F);

        var entry = reply.Item![0];
        Assert.Equal(6, entry.Count);
        Assert.Equal(100u, entry[0].FirstValueOrDefault<uint>());
        Assert.Equal("Setpoint", entry[1].GetString());
        Assert.Equal(100d, entry[2].FirstValueOrDefault<double>());
        Assert.Equal(2_000d, entry[3].FirstValueOrDefault<double>());
        Assert.Equal(1_013.25d, entry[4].FirstValueOrDefault<double>());
        Assert.Equal("mbar", entry[5].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_constant_with_no_documented_bounds_reports_zero_length_items_rather_than_inventing_them()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 29, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(101u)),
        });

        var entry = reply.Item![0];

        // "No documented bound" and "the bound is zero" are different answers, and a host acting on
        // the second would refuse values the machine would have accepted.
        Assert.Equal(0, entry[2].Count);
        Assert.Equal(0, entry[3].Count);
        Assert.Equal(0, entry[4].Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_constant_namelist_request_reports_every_constant()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(2, 29, replyExpected: true) { Item = SecsItem.L() });

        Assert.Equal(2, reply.Item!.Count);
        Assert.Equal(100u, reply.Item[0][0].FirstValueOrDefault<uint>());
        Assert.Equal(101u, reply.Item[1][0].FirstValueOrDefault<uint>());
    }

    // ----------------------------------------------------------------- S5F5 and S5F7

    [Fact(Timeout = 15_000)]
    public async Task Listing_alarms_reports_every_one_with_its_text()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(5, 5, replyExpected: true) { Item = SecsItem.L() });

        Assert.Equal(6, reply.F);
        Assert.Equal(2, reply.Item!.Count);
        Assert.Equal(900u, reply.Item[0][1].FirstValueOrDefault<uint>());
        Assert.Equal("Pressure", reply.Item[0][2].GetString());

        // Nothing is in alarm, so the top bit of ALCD is clear on both.
        Assert.Equal(0x00, reply.Item[0][0].FirstValueOrDefault<byte>());
        Assert.Equal(0x00, reply.Item[1][0].FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task Listing_alarms_says_which_one_is_in_alarm_right_now()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        var set = link.Equipment.SetAlarmAsync(900, GemLink.Token);
        using var report = await link.ReceiveAsync();
        Assert.True(await set);

        using var reply = await link.RequestAsync(new SecsMessage(5, 5, replyExpected: true) { Item = SecsItem.L() });

        // Enabled is whether the host wants to hear about it; this is whether it is happening. An
        // implementation that conflated them would list every alarm as quiet while the chamber was
        // out of range.
        Assert.Equal(0x80, reply.Item![0][0].FirstValueOrDefault<byte>());
        Assert.Equal(0x00, reply.Item[1][0].FirstValueOrDefault<byte>());
        Assert.True(link.Equipment.IsAlarmSet(900));
        Assert.False(link.Equipment.IsAlarmSet(901));
    }

    [Fact(Timeout = 15_000)]
    public async Task The_enabled_alarm_listing_leaves_out_what_the_host_disabled()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var listed = await link.RequestAsync(new SecsMessage(5, 7, replyExpected: true));

        Assert.Equal(8, listed.F);
        Assert.Equal(1, listed.Item!.Count);
        Assert.Equal(900u, listed.Item[0][1].FirstValueOrDefault<uint>());

        using var disabled = await link.RequestAsync(new SecsMessage(5, 3, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.Boolean(false), SecsItem.U4(900u)),
        });

        Assert.Equal((byte)GemAlarmAcknowledgement.Accepted, disabled.Item!.FirstValueOrDefault<byte>());

        using var empty = await link.RequestAsync(new SecsMessage(5, 7, replyExpected: true));
        Assert.Equal(0, empty.Item!.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_alarm_is_recorded_as_set_even_with_nobody_to_tell()
    {
        await using var link = GemLink.Start(Model);

        // Not communicating, so nothing is reported - and the machine is still in alarm. S5F6 asks
        // what is wrong with the machine, not what the machine managed to send.
        Assert.False(await link.Equipment.SetAlarmAsync(900, GemLink.Token));
        Assert.True(link.Equipment.IsAlarmSet(900));

        Assert.False(await link.Equipment.ClearAlarmAsync(900, GemLink.Token));
        Assert.False(link.Equipment.IsAlarmSet(900));
    }

    // ----------------------------------------------------------------- S6F15 and S6F19

    [Fact(Timeout = 15_000)]
    public async Task A_host_can_ask_for_an_event_report_now()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var linked = await link.RequestAsync(new SecsMessage(2, 35, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1u),
                SecsItem.L(SecsItem.L(SecsItem.U4(1_000u), SecsItem.L(SecsItem.U4(5_000u))))),
        });

        Assert.Equal((byte)GemLinkReportAcknowledgement.Accepted, linked.Item!.FirstValueOrDefault<byte>());

        using var reply = await link.RequestAsync(new SecsMessage(6, 15, replyExpected: true)
        {
            Item = SecsItem.U4(1_000u),
        });

        Assert.Equal(16, reply.F);
        Assert.Equal(1_000u, reply.Item![1].FirstValueOrDefault<uint>());
        Assert.Equal(5_000u, reply.Item[2][0][0].FirstValueOrDefault<uint>());
        Assert.Equal(1013.25d, reply.Item[2][0][1][1].FirstValueOrDefault<double>());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_direct_request_is_answered_even_for_a_disabled_event()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        Assert.False(link.Equipment.Model.IsEventEnabled(1_001));

        using var reply = await link.RequestAsync(new SecsMessage(6, 15, replyExpected: true)
        {
            Item = SecsItem.U4(1_001u),
        });

        // S2F37 governs what the equipment sends on its own. A host asking directly has already
        // said it wants this one now, so refusing would be answering a question nobody asked.
        Assert.Equal(1_001u, reply.Item![1].FirstValueOrDefault<uint>());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_unknown_event_gets_a_zero_length_reply()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(6, 15, replyExpected: true)
        {
            Item = SecsItem.U4(424_242u),
        });

        Assert.Equal(0, reply.Item!.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_host_can_ask_for_one_report_by_itself()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(6, 19, replyExpected: true)
        {
            Item = SecsItem.U4(5_000u),
        });

        Assert.Equal(20, reply.F);
        Assert.Equal(2, reply.Item!.Count);
        Assert.Equal(5, reply.Item[0].FirstValueOrDefault<byte>());
        Assert.Equal(1013.25d, reply.Item[1].FirstValueOrDefault<double>());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_undefined_report_gets_a_zero_length_reply()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(6, 19, replyExpected: true)
        {
            Item = SecsItem.U4(424_242u),
        });

        Assert.Equal(0, reply.Item!.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task Every_new_message_is_in_the_declared_set()
    {
        // The routing table is the declaration of what this entity understands, so a handler that
        // exists but is not declared answers S9F5 for a message it could have served.
        foreach (var pair in new[] { (1, 11), (2, 29), (5, 5), (5, 7), (6, 15), (6, 19) })
        {
            Assert.Contains(
                ((byte)pair.Item1, (byte)pair.Item2),
                GemEquipment.HandledMessages);
        }

        await Task.CompletedTask;
    }
}
