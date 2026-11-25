// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Subsystems.UnitTests;

/// <summary>
/// The shapes this package tolerates but does not write, and the ones it refuses.
/// </summary>
/// <remarks>
/// Both ends are played by hand here, because two real ends can only exercise what this library
/// itself emits. Stream 18 needs it at each end, since each end reads replies the other writes.
/// </remarks>
public sealed class SubsystemWireTests
{
    private static readonly SubsystemId Chamber = SubsystemId.Named("CHAMBER-A");

    [Fact]
    public async Task An_identifier_sent_as_text_reads_as_a_name_even_when_it_is_digits()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: null);
        await pair.OpenAsync();

        pair.Memory.Add(SubsystemId.Named("7"), "named seven", "Idle");

        // A machine whose SSID is the text "7". Read by format and never by content, which is the
        // one rule this package will not bend: the carrier chapter's Enum.TryParse read a number
        // where a name was promised and put a port into the wrong access mode.
        using var primary = new SecsMessage(18, 3, replyExpected: true)
        {
            Name = "SubsystemStatusRequest",
            Item = SecsItem.A("7").Named("SSID"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, SubsystemPair.Token);

        Assert.Equal(4, reply.F);
        Assert.Equal("Idle", reply.Item?.Items[1].GetString());
    }

    [Fact]
    public async Task An_identifier_sent_as_a_number_does_not_find_a_name()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        pair.Memory.Add(SubsystemId.Named("7"), "named seven", "Idle");

        using var primary = new SecsMessage(18, 3, replyExpected: true)
        {
            Name = "SubsystemStatusRequest",
            Item = SecsItem.U4(7).Named("SSID"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, SubsystemPair.Token);

        // The mirror of the test above, and the reason both are here: the two readings cross the
        // wire differently and must not meet in the middle.
        Assert.Equal(string.Empty, reply.Item?.Items[1].GetString());
    }

    [Fact]
    public async Task An_identifier_in_any_numeric_format_is_still_that_number()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        pair.Memory.Add(SubsystemId.Numbered(7), "numbered seven", "Running");

        // Tolerant about width and strict about kind: a U1 and a U4 are the same identifier, a
        // string is not.
        foreach (var item in new[] { SecsItem.U1(7), SecsItem.U2(7), SecsItem.U4(7), SecsItem.I4(7) })
        {
            using var primary = new SecsMessage(18, 3, replyExpected: true)
            {
                Name = "SubsystemStatusRequest",
                Item = item.Named("SSID"),
            };

            using var reply = await pair.HostLink.SendAsync(primary, SubsystemPair.Token);

            Assert.Equal("Running", reply.Item?.Items[1].GetString());
        }
    }

    [Fact]
    public async Task A_state_sent_as_a_number_is_read_as_its_digits()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 4, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("CHAMBER-A").Named("SSID"),
                SecsItem.U1(3).Named("SSSTATE")),
        });

        await pair.OpenAsync();

        // SSSTATE has no dictionary entry, so a machine may well send a code where this package
        // writes a word. Reading is tolerant; writing follows the one inference this package owns.
        Assert.Equal("3", await pair.Host.StatusAsync(Chamber, SubsystemPair.Token));
    }

    [Fact]
    public async Task A_reserved_command_code_reads_as_other()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("CHAMBER-A").Named("SSID"),
                SecsItem.B(40).Named("HCACK"),
                SecsItem.L()),
        });

        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            new SubsystemCommand { Subsystem = Chamber, Command = "PUMP" },
            SubsystemPair.Token);

        // The table reserves 7 to 63, and none of them is the zero of this enum - which would turn
        // somebody else's refusal into "done".
        Assert.Equal(SubsystemCommandAcknowledgement.Other, result.Acknowledgement);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task A_parameter_code_of_zero_is_reported_rather_than_believed()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 2, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("CHAMBER-A").Named("SSID"),
                SecsItem.B(3).Named("HCACK"),
                SecsItem.L(SecsItem.L(
                    SecsItem.A("TARGET").Named("CPNAME"),
                    SecsItem.B(0).Named("CPACK")))),
        });

        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            new SubsystemCommand { Subsystem = Chamber, Command = "PUMP" },
            SubsystemPair.Token);

        // CPACK's table starts at 1, so a zero says nothing the item has a meaning for. It reads
        // as Other and is logged, rather than being invented into a success.
        Assert.Equal(SubsystemParameterAcknowledgement.Other, Assert.Single(result.Faults).Acknowledgement);
        Assert.Contains(
            pair.HostLog.From(LogLevel.Information),
            message => message.Contains("CPACK 0", StringComparison.Ordinal)
                && message.Contains("starts at 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_reply_about_another_subsystem_is_reported()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 4, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("SOMETHING-ELSE").Named("SSID"),
                SecsItem.A("Idle").Named("SSSTATE")),
        });

        await pair.OpenAsync();

        var state = await pair.Host.StatusAsync(Chamber, SubsystemPair.Token);

        // Logged and not thrown: a host that wants to act on the mismatch has the log, and one
        // that wanted the state has the state.
        Assert.Equal("Idle", state);
        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("SOMETHING-ELSE", StringComparison.Ordinal)
                && message.Contains("CHAMBER-A", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_listing_whose_entries_are_laid_out_flat_is_read()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 10, replyExpected: false)
        {
            // Nesting is the one thing a flat item list cannot pin down, so a machine that laid
            // three fields per subsystem end to end is reading the same row differently.
            Item = SecsItem.L(
                SecsItem.A("A").Named("SSID"),
                SecsItem.A("first").Named("SSNAME"),
                SecsItem.A("Idle").Named("SSSTATE"),
                SecsItem.A("B").Named("SSID"),
                SecsItem.A("second").Named("SSNAME"),
                SecsItem.A("Running").Named("SSSTATE")),
        });

        await pair.OpenAsync();

        var subsystems = await pair.Host.ListAsync(SubsystemPair.Token);

        Assert.Equal(2, subsystems.Count);
        Assert.Equal(SubsystemId.Named("A"), subsystems[0].Id);
        Assert.Equal("Running", subsystems[1].State);
    }

    [Fact]
    public async Task A_listing_entry_short_of_its_three_fields_is_dropped()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 10, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(
                    SecsItem.A("A").Named("SSID"),
                    SecsItem.A("first").Named("SSNAME"),
                    SecsItem.A("Idle").Named("SSSTATE")),
                SecsItem.L(SecsItem.A("B").Named("SSID"))),
        });

        await pair.OpenAsync();

        // Dropped rather than padded: a subsystem with no state is not a subsystem, and inventing
        // one would put a state on something nobody described.
        Assert.Equal(SubsystemId.Named("A"), Assert.Single(await pair.Host.ListAsync(SubsystemPair.Token)).Id);
    }

    [Fact]
    public async Task A_listing_with_a_body_nothing_can_be_read_from_is_reported()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 10, replyExpected: false)
        {
            Item = SecsItem.A("not a listing at all"),
        });

        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ListAsync(SubsystemPair.Token));
        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("S18F10", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_write_acknowledged_with_a_bare_code_is_read()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 8, replyExpected: false)
        {
            // A machine that left the echoed identifier off. This end already knows which
            // subsystem it asked, so the code is the part that matters.
            Item = SecsItem.B((byte)SubsystemAcknowledgement.ValueOutOfRange).Named("SSACK"),
        });

        await pair.OpenAsync();

        Assert.Equal(
            SubsystemAcknowledgement.ValueOutOfRange,
            await pair.Host.WriteAsync(
                new SubsystemVariable { Subsystem = Chamber, VariableId = 1, Value = "x" },
                SubsystemPair.Token));
    }

    [Fact]
    public async Task A_command_reply_with_no_code_throws()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 2, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A("CHAMBER-A").Named("SSID")),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.CommandAsync(
                new SubsystemCommand { Subsystem = Chamber, Command = "PUMP" },
                SubsystemPair.Token).AsTask());

        Assert.Contains("HCACK", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_reply_that_is_not_a_status_throws()
    {
        await using var pair = SubsystemPair.Start(answerEquipment: _ => new SecsMessage(18, 4, replyExpected: false)
        {
            Item = SecsItem.A("no status here"),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            () => pair.Host.StatusAsync(Chamber, SubsystemPair.Token).AsTask());

        Assert.Contains("S18F4", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unreadable_command_is_refused_as_a_bad_parameter()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(18, 1, replyExpected: true)
        {
            Name = "SubsystemCommandSend",
            Item = SecsItem.A("not a command body"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, SubsystemPair.Token);

        // HCACK 3 and not 1: the command may well exist, and what went wrong was the body. HCACK
        // has no code for "that made no sense", which is the nearest it has.
        Assert.Equal(2, reply.F);
        Assert.Equal<byte?>(
            (byte)SubsystemCommandAcknowledgement.InvalidParameter,
            reply.Item?.Items[1].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task An_unreadable_status_request_is_answered_with_nothing()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(18, 3, replyExpected: true)
        {
            Name = "SubsystemStatusRequest",
            Item = SecsItem.L(SecsItem.A("x"), SecsItem.A("y")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, SubsystemPair.Token);

        // S18F4 has nowhere to put a refusal, and naming a subsystem this end could not read would
        // answer a question nobody asked.
        Assert.Equal(4, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact]
    public async Task An_unreadable_write_is_refused_as_an_unknown_variable()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(18, 7, replyExpected: true)
        {
            Name = "SubsystemDataWriteRequest",
            Item = SecsItem.L(SecsItem.A("CHAMBER-A").Named("SSID")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, SubsystemPair.Token);

        Assert.Equal(8, reply.F);
        Assert.Equal<byte?>(
            (byte)SubsystemAcknowledgement.UnknownVariable,
            reply.Item?.Items[^1].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task An_alarm_category_the_table_leaves_to_the_equipment_reads_as_other()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        SubsystemAlarm? heard = null;
        pair.Host.AlarmReported += (_, args) => heard = args.Alarm;

        // ALCD 0x80 | 42: set, and a category the table does not name.
        using var primary = new SecsMessage(18, 13, replyExpected: true)
        {
            Name = "SubsystemAlarmReportSend",
            Item = SecsItem.L(
                SecsItem.A("CHAMBER-A").Named("SSID"),
                SecsItem.B(0x80 | 42).Named("ALCD"),
                SecsItem.U4(3001).Named("ALID"),
                SecsItem.A("something vendor specific").Named("ALTX")),
        };

        using var reply = await pair.EquipmentLink.SendAsync(primary, SubsystemPair.Token);

        Assert.Equal(14, reply.F);
        Assert.True(heard?.IsSet);
        Assert.Equal(SubsystemAlarmCategory.Other, heard?.Category);
        Assert.Contains(
            pair.HostLog.From(LogLevel.Information),
            message => message.Contains("category 42", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_alarm_text_longer_than_the_dictionary_allows_is_truncated_not_refused()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        SubsystemAlarm? heard = null;
        pair.Host.AlarmReported += (_, args) => heard = args.Alarm;

        await pair.Equipment.ReportAlarmAsync(
            new SubsystemAlarm
            {
                Subsystem = Chamber,
                AlarmId = 1,
                IsSet = true,
                Text = new string('x', 200),
            },
            SubsystemPair.Token);

        // ALTX is an A[120]. An alarm with a long description is still an alarm, so it is cut
        // rather than dropped.
        Assert.Equal(120, heard?.Text.Length);
    }

    [Fact]
    public async Task An_event_this_end_cannot_read_is_still_acknowledged()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        var raised = false;
        pair.Host.EventReported += (_, _) => raised = true;

        using var primary = new SecsMessage(18, 11, replyExpected: true)
        {
            Name = "SubsystemEventReportSend",
            Item = SecsItem.A("not an event report"),
        };

        using var reply = await pair.EquipmentLink.SendAsync(primary, SubsystemPair.Token);

        // SSACK has no code for "that made no sense", so the machine is acknowledged and the log
        // carries the complaint. Leaving it unanswered would hang the machine on its T3.
        Assert.Equal(12, reply.F);
        Assert.Equal<byte?>(
            (byte)SubsystemAcknowledgement.Accepted,
            reply.Item?.FirstValueOrDefault<byte>());

        Assert.False(raised);
        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("S18F11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_that_refuses_reports_says_so_with_the_one_code_it_has()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        pair.Host.ReportAcknowledgement = SubsystemAcknowledgement.Other;

        Assert.Equal(
            SubsystemAcknowledgement.Other,
            await pair.Equipment.ReportEventAsync(
                new SubsystemEvent { Subsystem = Chamber, EventId = 1 },
                SubsystemPair.Token));
    }

    [Fact]
    public async Task A_function_outside_the_six_is_logged_and_left_alone()
    {
        // Only reachable if an application routes a function the package does not declare, since
        // the router answers S9F5 for anything undeclared - so the harness routes one.
        await using var pair = SubsystemPair.Start(alsoRouteEquipment: [(18, 17)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(18, 17, replyExpected: false)
        {
            Name = "NotInStream18AtAll",
            Item = SecsItem.L(),
        };

        // Posted from the host, because the equipment is the end being tested: a message sent on
        // the equipment's own link goes the other way, which is how the first version of this test
        // waited seventy-five seconds for something that was never coming.
        await pair.HostLink.PostAsync(primary, SubsystemPair.Token);
        await SubsystemPair.WaitUntilAsync(() => pair.AskedEquipment.Count > 0, "the equipment to be asked");

        // And then for the LOG, which is what the assertion reads. The harness records the message
        // in the route BEFORE calling the end under test, so a wait on that queue is a wait on the
        // wrong thing - the handler may not have written its line yet. This package was missed by
        // the sweep that added these second waits elsewhere, and the race showed up once in
        // twenty-four parallel runs of this project.
        await SubsystemPair.WaitUntilAsync(
            () => pair.EquipmentLog.From(LogLevel.Warning).Count > 0,
            "the equipment to say why");

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S18F17", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_primary_the_host_does_not_answer_is_logged_and_left_alone()
    {
        // The same guard at the other end, and worth having separately: the host's six primaries
        // are the equipment's to answer, so one arriving here is a machine with a mixed-up
        // registration.
        await using var pair = SubsystemPair.Start(alsoRouteHost: [(18, 1)]);
        await pair.OpenAsync();

        using var primary = new SecsMessage(18, 1, replyExpected: false)
        {
            Name = "SubsystemCommandSend",
            Item = SecsItem.L(),
        };

        await pair.EquipmentLink.PostAsync(primary, SubsystemPair.Token);
        await SubsystemPair.WaitUntilAsync(() => pair.AskedHost.Count > 0, "the host to be asked");

        await SubsystemPair.WaitUntilAsync(
            () => pair.HostLog.From(LogLevel.Warning).Count > 0,
            "the host to say why");

        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("S18F1", StringComparison.Ordinal)
                && message.Contains("nothing to answer it", StringComparison.Ordinal));
    }
}
