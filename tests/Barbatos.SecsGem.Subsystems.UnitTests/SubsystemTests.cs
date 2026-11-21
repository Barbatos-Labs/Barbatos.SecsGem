// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Subsystems.UnitTests;

/// <summary>
/// Stream 18 end to end: a real host and a real equipment over one link.
/// </summary>
public sealed class SubsystemTests
{
    private static readonly SubsystemId Chamber = SubsystemId.Named("CHAMBER-A");

    private static SubsystemInMemoryStore Machine()
    {
        var store = new SubsystemInMemoryStore();

        store.Add(Chamber, "Process chamber A", "Idle");
        store.Set(Chamber, 1001, "23.9");
        store.Set(Chamber, 1002, "4.1", isReadOnly: true);
        store.Add(SubsystemId.Numbered(2), "Load lock", "Vented");

        return store;
    }

    [Fact]
    public async Task A_command_reaches_the_subsystem_and_two_codes_are_successes()
    {
        var store = Machine();
        SubsystemCommand? heard = null;

        store.AnswerCommand = command =>
        {
            heard = command;

            return new SubsystemCommandResult
            {
                Subsystem = command.Subsystem,
                Acknowledgement = SubsystemCommandAcknowledgement.CompletionLater,
            };
        };

        await using var pair = SubsystemPair.Start(store);
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            new SubsystemCommand
            {
                Subsystem = Chamber,
                Command = "PUMP",
                Parameters = [new SubsystemParameter("TARGET", "4.0")],
            },
            SubsystemPair.Token);

        Assert.Equal("PUMP", heard?.Command);
        Assert.Equal("TARGET", heard?.Parameters[0].Name);
        Assert.Equal("4.0", heard?.Parameters[0].Value);

        // HCACK 4 is a success: accepted and not yet finished. This is the acknowledgement in the
        // stream most likely to be read backwards.
        Assert.Equal(SubsystemCommandAcknowledgement.CompletionLater, result.Acknowledgement);
        Assert.True(result.IsAccepted);
        Assert.Equal(Chamber, result.Subsystem);
    }

    [Fact]
    public async Task A_command_to_a_subsystem_that_is_not_there_says_so_by_name()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            new SubsystemCommand { Subsystem = SubsystemId.Named("NOPE"), Command = "PUMP" },
            SubsystemPair.Token);

        // HCACK has a code for exactly this, which is more than most of this stack gets.
        Assert.Equal(SubsystemCommandAcknowledgement.NoSuchSubsystem, result.Acknowledgement);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task A_command_nobody_implemented_is_refused_rather_than_claimed()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            new SubsystemCommand { Subsystem = Chamber, Command = "PUMP" },
            SubsystemPair.Token);

        // The store's default, and not Accepted: claiming a machine did something nobody wrote is
        // the one answer here that cannot be right.
        Assert.Equal(SubsystemCommandAcknowledgement.InvalidCommand, result.Acknowledgement);
    }

    [Fact]
    public async Task A_bad_parameter_comes_back_named()
    {
        var store = Machine();

        store.AnswerCommand = command => new SubsystemCommandResult
        {
            Subsystem = command.Subsystem,
            Acknowledgement = SubsystemCommandAcknowledgement.InvalidParameter,
            Faults = [new SubsystemParameterFault("TARGET", SubsystemParameterAcknowledgement.IllegalValue)],
        };

        await using var pair = SubsystemPair.Start(store);
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            new SubsystemCommand
            {
                Subsystem = Chamber,
                Command = "PUMP",
                Parameters = [new SubsystemParameter("TARGET", "not a pressure")],
            },
            SubsystemPair.Token);

        Assert.Equal(SubsystemCommandAcknowledgement.InvalidParameter, result.Acknowledgement);

        var fault = Assert.Single(result.Faults);
        Assert.Equal("TARGET", fault.Name);
        Assert.Equal(SubsystemParameterAcknowledgement.IllegalValue, fault.Acknowledgement);
    }

    [Fact]
    public async Task A_status_comes_back_and_a_subsystem_that_is_not_there_comes_back_empty()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        Assert.Equal("Idle", await pair.Host.StatusAsync(Chamber, SubsystemPair.Token));

        // S18F4 has no acknowledgement code at all, so "no such subsystem" arrives as an empty
        // state in an otherwise ordinary reply.
        Assert.Null(await pair.Host.StatusAsync(SubsystemId.Named("NOPE"), SubsystemPair.Token));
    }

    [Fact]
    public async Task A_numbered_subsystem_and_a_named_one_both_answer()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        // The whole reason SubsystemId carries both readings: one machine, one link, two formats
        // of SSID on the wire, and neither of them chosen by this library.
        Assert.Equal("Idle", await pair.Host.StatusAsync(Chamber, SubsystemPair.Token));
        Assert.Equal("Vented", await pair.Host.StatusAsync(SubsystemId.Numbered(2), SubsystemPair.Token));
    }

    [Fact]
    public async Task A_number_does_not_find_the_subsystem_whose_name_is_that_number()
    {
        var store = new SubsystemInMemoryStore();
        store.Add(SubsystemId.Named("2"), "named two", "Running");

        await using var pair = SubsystemPair.Start(store);
        await pair.OpenAsync();

        Assert.Equal("Running", await pair.Host.StatusAsync(SubsystemId.Named("2"), SubsystemPair.Token));

        // Strictness end to end rather than only in the struct: the identifier crosses the wire as
        // a U4, arrives as a number, and does not match the name.
        Assert.Null(await pair.Host.StatusAsync(SubsystemId.Numbered(2), SubsystemPair.Token));
    }

    [Fact]
    public async Task A_variable_is_read_and_an_unknown_one_cannot_be_refused()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        Assert.Equal("23.9", await pair.Host.ReadAsync(Chamber, 1001, SubsystemPair.Token));

        // S18F6 carries no code, so this is the same shape as a successful read with nothing in
        // it - while the write below gets a reason. That asymmetry is E5's.
        Assert.Null(await pair.Host.ReadAsync(Chamber, 9999, SubsystemPair.Token));
    }

    [Fact]
    public async Task A_variable_is_written_and_an_unknown_one_is_refused_with_a_reason()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        Assert.Equal(
            SubsystemAcknowledgement.Accepted,
            await pair.Host.WriteAsync(
                new SubsystemVariable { Subsystem = Chamber, VariableId = 1001, Value = "25.0" },
                SubsystemPair.Token));

        Assert.Equal("25.0", await pair.Host.ReadAsync(Chamber, 1001, SubsystemPair.Token));

        // The three codes SSACK's table was actually written for.
        Assert.Equal(
            SubsystemAcknowledgement.UnknownVariable,
            await pair.Host.WriteAsync(
                new SubsystemVariable { Subsystem = Chamber, VariableId = 9999, Value = "0" },
                SubsystemPair.Token));

        Assert.Equal(
            SubsystemAcknowledgement.ReadOnlyVariable,
            await pair.Host.WriteAsync(
                new SubsystemVariable { Subsystem = Chamber, VariableId = 1002, Value = "0" },
                SubsystemPair.Token));
    }

    [Fact]
    public async Task A_write_to_a_subsystem_that_is_not_there_is_an_unknown_variable()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        // One code for both, because SSACK has one. A variable of a subsystem that does not exist
        // is a variable that does not exist.
        Assert.Equal(
            SubsystemAcknowledgement.UnknownVariable,
            await pair.Host.WriteAsync(
                new SubsystemVariable { Subsystem = SubsystemId.Named("NOPE"), VariableId = 1, Value = "x" },
                SubsystemPair.Token));
    }

    [Fact]
    public async Task The_listing_names_every_subsystem_with_its_state()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        var subsystems = await pair.Host.ListAsync(SubsystemPair.Token);

        Assert.Equal(2, subsystems.Count);

        // Numbered before named, which is the store's ordering and says out loud that the two are
        // not one vocabulary.
        Assert.Equal(SubsystemId.Numbered(2), subsystems[0].Id);
        Assert.Equal("Load lock", subsystems[0].Name);
        Assert.Equal("Vented", subsystems[0].State);

        Assert.Equal(Chamber, subsystems[1].Id);
        Assert.Equal("Process chamber A", subsystems[1].Name);
        Assert.Equal("Idle", subsystems[1].State);
    }

    [Fact]
    public async Task The_listing_of_a_machine_with_no_subsystems_is_empty()
    {
        await using var pair = SubsystemPair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ListAsync(SubsystemPair.Token));
    }

    [Fact]
    public async Task A_state_change_shows_up_in_the_status_and_in_the_listing()
    {
        var store = Machine();

        await using var pair = SubsystemPair.Start(store);
        await pair.OpenAsync();

        Assert.True(store.SetState(Chamber, "Processing"));

        Assert.Equal("Processing", await pair.Host.StatusAsync(Chamber, SubsystemPair.Token));

        var listed = await pair.Host.ListAsync(SubsystemPair.Token);
        Assert.Equal("Processing", listed.Single(s => s.Id == Chamber).State);

        // And the variables survived the state change, which is what the record's copy
        // constructor is doing and would be easy to lose.
        Assert.Equal("23.9", await pair.Host.ReadAsync(Chamber, 1001, SubsystemPair.Token));
    }

    [Fact]
    public async Task An_event_reaches_the_host_and_is_acknowledged()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        SubsystemEvent? heard = null;
        pair.Host.EventReported += (_, args) => heard = args.Report;

        var acknowledgement = await pair.Equipment.ReportEventAsync(
            new SubsystemEvent { Subsystem = Chamber, EventId = 2001, Time = "2026112100000000" },
            SubsystemPair.Token);

        Assert.Equal(SubsystemAcknowledgement.Accepted, acknowledgement);
        Assert.Equal(Chamber, heard?.Subsystem);
        Assert.Equal(2001u, heard?.EventId);
        Assert.Equal("2026112100000000", heard?.Time);
    }

    [Fact]
    public async Task An_alarm_reaches_the_host_set_and_then_cleared()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        List<SubsystemAlarm> heard = [];
        pair.Host.AlarmReported += (_, args) => heard.Add(args.Alarm);

        await pair.Equipment.ReportAlarmAsync(
            new SubsystemAlarm
            {
                Subsystem = Chamber,
                AlarmId = 3001,
                IsSet = true,
                Category = SubsystemAlarmCategory.EquipmentSafety,
                Text = "chamber pressure high",
            },
            SubsystemPair.Token);

        await pair.Equipment.ReportAlarmAsync(
            new SubsystemAlarm { Subsystem = Chamber, AlarmId = 3001, IsSet = false },
            SubsystemPair.Token);

        Assert.Equal(2, heard.Count);

        // The top bit of ALCD is set-or-cleared and the low bits are the category, so one byte
        // carries both and the same message reports an alarm going away.
        Assert.True(heard[0].IsSet);
        Assert.Equal(SubsystemAlarmCategory.EquipmentSafety, heard[0].Category);
        Assert.Equal("chamber pressure high", heard[0].Text);

        Assert.False(heard[1].IsSet);
        Assert.Equal(3001u, heard[1].AlarmId);
    }

    [Fact]
    public async Task A_2d_code_read_comes_back_whole()
    {
        var store = Machine();

        store.SetCodeCondition(new SubsystemCodeCondition
        {
            TargetId = "READER-1",
            Acknowledgement = SubsystemAcknowledgement.Accepted,
            MaterialId = "W01-2026",
            Status = 3,
            Condition = "contrast low",
        });

        await using var pair = SubsystemPair.Start(store);
        await pair.OpenAsync();

        var condition = await pair.Host.ReadCodeAsync("READER-1", SubsystemPair.Token);

        Assert.True(condition.IsRead);
        Assert.Equal("W01-2026", condition.MaterialId);

        // STATUS is a bare number because the dictionary gives it a format and no table of
        // values. There is nothing to make an enum against.
        Assert.Equal((byte)3, condition.Status);
        Assert.Equal("contrast low", condition.Condition);
    }

    [Fact]
    public async Task A_2d_code_read_of_a_target_that_is_not_there_has_only_one_code_left()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        var condition = await pair.Host.ReadCodeAsync("NO-SUCH-READER", SubsystemPair.Token);

        // SSACK's three refusals are all about writing a variable, so an unknown target gets the
        // only code the table has left.
        Assert.False(condition.IsRead);
        Assert.Equal(SubsystemAcknowledgement.Other, condition.Acknowledgement);
        Assert.Equal("NO-SUCH-READER", condition.TargetId);
    }

    [Fact]
    public async Task Each_end_sends_every_primary_the_other_declares()
    {
        await using var pair = SubsystemPair.Start(Machine());
        await pair.OpenAsync();

        await pair.Host.CommandAsync(
            new SubsystemCommand { Subsystem = Chamber, Command = "PUMP" },
            SubsystemPair.Token);

        await pair.Host.StatusAsync(Chamber, SubsystemPair.Token);
        await pair.Host.ReadAsync(Chamber, 1001, SubsystemPair.Token);
        await pair.Host.WriteAsync(
            new SubsystemVariable { Subsystem = Chamber, VariableId = 1001, Value = "24.0" },
            SubsystemPair.Token);
        await pair.Host.ListAsync(SubsystemPair.Token);
        await pair.Host.ReadCodeAsync("READER-1", SubsystemPair.Token);

        await pair.Equipment.ReportEventAsync(
            new SubsystemEvent { Subsystem = Chamber, EventId = 1 },
            SubsystemPair.Token);

        await pair.Equipment.ReportAlarmAsync(
            new SubsystemAlarm { Subsystem = Chamber, AlarmId = 1, IsSet = true },
            SubsystemPair.Token);

        // "Every" asserted rather than claimed, in both directions - which is the thing about
        // stream 18 that the routing table cannot check, because the two sets are disjoint.
        Assert.Equal(
            [.. SubsystemEquipment.HandledMessages.Order()],
            pair.AskedEquipment.Distinct().Order());

        Assert.Equal(
            [.. SubsystemHost.HandledMessages.Order()],
            pair.AskedHost.Distinct().Order());
    }

    [Fact]
    public void The_two_declarations_are_disjoint_and_cover_the_fourteen_rows_implemented()
    {
        Assert.Equal<(byte, byte)>(
            [(18, 1), (18, 3), (18, 5), (18, 7), (18, 9), (18, 15)],
            SubsystemEquipment.HandledMessages);

        Assert.Equal<(byte, byte)>([(18, 11), (18, 13)], SubsystemHost.HandledMessages);

        // Disjoint, so a process that declared both would not be refused by the routing table -
        // which is why this is a test and not a runtime check.
        Assert.Empty(SubsystemEquipment.HandledMessages.Intersect(SubsystemHost.HandledMessages));

        // Eight primaries, eight pairs, sixteen rows: the whole of stream 18.
        Assert.Equal(8, SubsystemEquipment.HandledMessages.Count + SubsystemHost.HandledMessages.Count);
    }

    [Fact]
    public void The_command_acknowledgement_agrees_with_the_one_GEM_quotes()
    {
        // One dictionary entry cannot mean two things in one repository. Barbatos.SecsGem.Gem
        // quotes HCACK for S2F42 and this package quotes it for S18F2, and the two enums are
        // checked member for member by name and by number rather than by eye.
        string[] expected =
        [
            "Accepted",
            "InvalidCommand",
            "CannotPerformNow",
            "InvalidParameter",
            "CompletionLater",
            "AlreadyInDesiredCondition",
            "NoSuchSubsystem",
            "Other",
        ];

        var actual = Enum.GetNames<SubsystemCommandAcknowledgement>();

        Assert.Equal(expected, actual);

        // And the numbers are the table's, which is what a host compares against the wire.
        Assert.Equal(0, (byte)SubsystemCommandAcknowledgement.Accepted);
        Assert.Equal(4, (byte)SubsystemCommandAcknowledgement.CompletionLater);
        Assert.Equal(6, (byte)SubsystemCommandAcknowledgement.NoSuchSubsystem);
        Assert.Equal(7, (byte)SubsystemCommandAcknowledgement.Other);
    }

    [Fact]
    public void The_parameter_acknowledgement_has_no_success_code()
    {
        // CPACK's table starts at 1 and reserves 4 to 63: the list in S18F2 carries the parameters
        // that were wrong, not the ones that were fine. A member for success would invite a reply
        // E5 does not describe.
        Assert.Equal(1, (byte)SubsystemParameterAcknowledgement.UnknownName);
        Assert.DoesNotContain(
            (byte)0,
            Enum.GetValues<SubsystemParameterAcknowledgement>().Select(code => (byte)code).ToArray());
    }
}
