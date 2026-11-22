// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The nine stream 7 pairs the reference illustrates rather than defines, and the measurement that
/// let them in.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Stream 7 has no gaps.</strong> Every one of the nine names it uses - <c>PPID</c>,
/// <c>PPBODY</c>, <c>LENGTH</c>, <c>PPGNT</c>, <c>ACKC7</c>, <c>RCMD</c>, <c>CMDA</c>,
/// <c>CPNAME</c>, <c>CPVAL</c> - is defined in the item dictionary, and two of them, <c>PPGNT</c>
/// and <c>ACKC7</c>, are named by no other stream at all. So its firm ground is its own
/// vocabulary, which is the opposite of the streams this repository has refused: stream 15 has
/// eighteen gaps and no own anchors, stream 20 thirteen and none, stream 17 five and none.
/// </para>
/// <para>
/// That is why provenance alone did not settle these rows, in the way it did not settle stream 15.
/// Each of the nine implemented here names only items the stream's own documented rows already
/// carry, and stream 7 has no rival spellings, so reading a format off a sibling is reading the
/// reference rather than correcting it. <c>SecsCatalogStream7Tests</c> pins the figures, including
/// the six pairs that stayed out and why.
/// </para>
/// </remarks>
public class GemProcessProgramSweepTests
{
    private static readonly byte[] Body = [1, 2, 3, 4];

    private string? _commandProgram;
    private string? _commandLot;

    private void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddEvent(1_000, "ProcessEnd")
            .AddRemoteCommand(
                "START",
                (context, _) =>
                {
                    _commandProgram = context.ProcessProgramId;
                    _commandLot = context.GetText("LOTID");

                    return ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted);
                },
                requiresRemoteControl: false)
            .AddRemoteCommand(
                "LOCKED",
                (_, _) => ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted));

    private static GemInMemoryProcessProgramStore Stored()
    {
        var store = new GemInMemoryProcessProgramStore();

        store.SaveAsync("RECIPE-7", Body, GemLink.Token).AsTask().GetAwaiter().GetResult();

        return store;
    }

    private static GemInMemoryFormattedProcessProgramStore Shaped()
    {
        var store = new GemInMemoryFormattedProcessProgramStore();

        store.SaveAsync(Formatted(), GemLink.Token).AsTask().GetAwaiter().GetResult();

        return store;
    }

    private static GemFormattedProcessProgram Formatted(
        string revision = "1.0.0",
        string command = "SETPOINT")
        => new("SHAPED", "BRBTOS-1", revision, [new GemProcessProgramCommand(12, [command, "350.0"])]);

    // ------------------------------------------------------------- S7F7, the directory

    [Fact]
    public async Task The_directory_and_the_current_eppd_answer_the_same_question()
    {
        await using var pair = GemPair.Start(Model, programs: Stored(), formatted: Shaped());
        await pair.OpenAsync();

        var directory = await pair.Host.ReadProcessProgramDirectoryAsync(GemPair.Token);
        var eppd = await pair.Host.ListProcessProgramsAsync(GemPair.Token);

        // S7F8 is "Process Program Directory Data" and S7F20 is "Current EPPD Data", and the
        // reference gives both bodies one item: a list of PPID. Nothing in it tells them apart, so
        // answering them differently would be inventing a distinction.
        Assert.Equal<string[]>(["RECIPE-7", "SHAPED"], [.. directory]);
        Assert.Equal<string[]>([.. eppd], [.. directory]);
    }

    [Fact]
    public async Task The_directory_is_answered_on_the_function_it_was_asked_on()
    {
        await using var link = GemLink.Start(Model, programs: Stored());
        await link.OpenAsync();

        using var directory = await link.RequestAsync(new SecsMessage(7, 7, replyExpected: true));
        using var eppd = await link.RequestAsync(new SecsMessage(7, 19, replyExpected: true));

        // One handler, two reply functions - which is worth asserting because the handler takes the
        // number as a parameter and a mistake there would answer S7F7 with an S7F20.
        Assert.Equal(8, directory.F);
        Assert.Equal(20, eppd.F);
        Assert.Equal(1, directory.Item!.Count);
    }

    // ------------------------------------------------------------- S7F9, the verify

    [Fact]
    public async Task A_body_that_matches_verifies()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.VerifyProcessProgramAsync("RECIPE-7", Body, GemPair.Token));
    }

    [Fact]
    public async Task A_program_the_machine_has_not_got_is_not_found()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramAcknowledgement.NotFound,
            await pair.Host.VerifyProcessProgramAsync("NOPE", Body, GemPair.Token));
    }

    [Fact]
    public async Task A_body_of_the_wrong_length_is_a_length_error()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        // The one failure ACKC7 has a word for, and it is exactly the right word.
        Assert.Equal(
            GemProcessProgramAcknowledgement.LengthError,
            await pair.Host.VerifyProcessProgramAsync("RECIPE-7", new byte[] { 1, 2 }, GemPair.Token));
    }

    [Fact]
    public async Task A_body_of_the_right_length_and_the_wrong_contents_has_no_word_of_its_own()
    {
        var log = new RecordingLogger<GemEquipment>();
        await using var link = GemLink.Start(Model, programs: Stored(), logger: log);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(7, 9, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("RECIPE-7"), SecsItem.B(9, 9, 9, 9)),
        });

        // ACKC7 has no code for "those bytes are not these bytes", which is the one thing a verify
        // exists to discover. PermissionNotGranted is the table's nearest word for "I will not
        // confirm that" - stated here because it is a choice and not a reading.
        Assert.Equal(10, reply.F);
        Assert.Equal(
            (byte)GemProcessProgramAcknowledgement.PermissionNotGranted,
            reply.Item!.FirstValueOrDefault<byte>());

        // And the difference survives in the log, because the code cannot carry it: a host seeing
        // PermissionNotGranted cannot tell a mismatch from a refusal.
        Assert.Contains(
            log.From(LogLevel.Information),
            message => message.Contains("not the bytes held", StringComparison.Ordinal)
                && message.Contains("no code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verify_whose_body_this_end_cannot_read_is_a_length_error()
    {
        await using var link = GemLink.Start(Model, programs: Stored());
        await link.OpenAsync();

        // PPBODY as text rather than binary. The same guard S7F3 has, and for the same reason:
        // handing the comparison an empty body would answer Accepted for a program of zero bytes.
        using var reply = await link.RequestAsync(new SecsMessage(7, 9, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("RECIPE-7"), SecsItem.A("not binary")),
        });

        Assert.Equal(
            (byte)GemProcessProgramAcknowledgement.LengthError,
            reply.Item!.FirstValueOrDefault<byte>());
    }

    // ------------------------------------------------------------- S7F31, availability

    [Fact]
    public async Task A_program_the_machine_holds_answers_already_have()
    {
        await using var pair = GemPair.Start(Model, programs: Stored(), formatted: Shaped());
        await pair.OpenAsync();

        // AlreadyHave is yes. PPGNT exists for a load grant, so Ok means "go ahead and send" -
        // which answers a question nobody asked here, and a host comparing against it would read
        // every held program as absent.
        Assert.Equal(
            GemProcessProgramGrant.AlreadyHave,
            await pair.Host.IsProcessProgramAvailableAsync("RECIPE-7", GemPair.Token));

        // Both stores, like S7F17 and S7F19: a host asking whether SHAPED is here is asking about
        // the program, not the flavour it was transferred in.
        Assert.Equal(
            GemProcessProgramGrant.AlreadyHave,
            await pair.Host.IsProcessProgramAvailableAsync("SHAPED", GemPair.Token));
    }

    [Fact]
    public async Task A_program_it_has_not_got_and_a_name_it_cannot_read_are_the_same_answer()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramGrant.InvalidId,
            await pair.Host.IsProcessProgramAvailableAsync("NOPE", GemPair.Token));

        // PPGNT has one code for both, which is a shortage worth knowing rather than hiding: a
        // host cannot tell "no such program" from "that identifier made no sense".
        Assert.Equal(
            GemProcessProgramGrant.InvalidId,
            await pair.Host.IsProcessProgramAvailableAsync(string.Empty, GemPair.Token));
    }

    // ------------------------------------------------------- S7F33 and S7F27, the formatted half

    [Fact]
    public async Task The_formatted_inquire_says_whether_it_already_has_one()
    {
        await using var pair = GemPair.Start(Model, formatted: Shaped());
        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramGrant.AlreadyHave,
            await pair.Host.InquireFormattedProcessProgramAsync("SHAPED", 64, GemPair.Token));

        Assert.Equal(
            GemProcessProgramGrant.Ok,
            await pair.Host.InquireFormattedProcessProgramAsync("NEW-ONE", 64, GemPair.Token));
    }

    [Fact]
    public async Task The_formatted_inquire_cannot_refuse_on_space_and_does_not_pretend_to()
    {
        await using var link = GemLink.Start(Model, formatted: Shaped());
        await link.OpenAsync();

        // Every length, including one no store could hold. IGemFormattedProcessProgramStore exposes
        // no capacity - adding a method would break every store anybody has written - so NoSpace is
        // unreachable here and an Ok is not a promise that S7F23 will be accepted.
        using var reply = await link.RequestAsync(new SecsMessage(7, 33, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("ENORMOUS"), SecsItem.U4(uint.MaxValue)),
        });

        Assert.Equal(34, reply.F);
        Assert.Equal((byte)GemProcessProgramGrant.Ok, reply.Item!.FirstValueOrDefault<byte>());

        // A body that is not this message is refused, which is what reading LENGTH is for even
        // though nothing acts on the value.
        using var malformed = await link.RequestAsync(new SecsMessage(7, 33, replyExpected: true)
        {
            Item = SecsItem.A("SHAPED"),
        });

        Assert.Equal((byte)GemProcessProgramGrant.InvalidId, malformed.Item!.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task The_formatted_verify_compares_the_whole_structure()
    {
        await using var pair = GemPair.Start(Model, formatted: Shaped());
        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Host.VerifyFormattedProcessProgramAsync(Formatted(), GemPair.Token));

        // A revision nobody stored. The commands match, and it is still not the same program: one
        // written for another software revision is a different program, which is the whole reason
        // MDLN and SOFTREV are in the body.
        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await pair.Host.VerifyFormattedProcessProgramAsync(Formatted(revision: "2.0.0"), GemPair.Token));

        // And a command that differs, to show the comparison reaches past the header.
        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await pair.Host.VerifyFormattedProcessProgramAsync(Formatted(command: "RAMP"), GemPair.Token));
    }

    [Fact]
    public async Task A_formatted_program_the_machine_has_not_got_is_not_found()
    {
        await using var pair = GemPair.Start(Model, formatted: new GemInMemoryFormattedProcessProgramStore());
        await pair.OpenAsync();

        Assert.Equal(
            GemProcessProgramAcknowledgement.NotFound,
            await pair.Host.VerifyFormattedProcessProgramAsync(Formatted(), GemPair.Token));
    }

    // ------------------------------------------- S7F37 and S7F41, the program-related commands

    [Fact]
    public async Task A_program_related_command_runs_the_same_handler_and_says_which_program()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        Assert.Equal(
            GemRemoteCommandAcknowledgement.Performed,
            await pair.Host.SendProgramCommandAsync("RECIPE-7", "START", [], GemPair.Token));

        // One command table for all four messages, because a machine that can be told to START has
        // one START whether or not the host mentioned a recipe. What the stream 7 form adds is the
        // program, and it reaches the handler through the context.
        Assert.Equal("RECIPE-7", _commandProgram);
        Assert.Null(_commandLot);
    }

    [Fact]
    public async Task The_form_with_parameters_carries_them_and_the_other_form_carries_none()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        Assert.Equal(
            GemRemoteCommandAcknowledgement.Performed,
            await pair.Host.SendProgramCommandAsync("RECIPE-7", "START", [("LOTID", "LOT-1")], GemPair.Token));

        Assert.Equal("RECIPE-7", _commandProgram);
        Assert.Equal("LOT-1", _commandLot);

        // Which function went out is decided by the parameter list, because the reference gives
        // S7F37 two items and S7F41 three: an empty list is the message that carries no list, not
        // the message carrying an empty one.
        Assert.Contains(((byte)7, (byte)41), pair.Asked);
    }

    [Fact]
    public async Task A_command_about_a_program_the_machine_has_not_got_is_refused_before_the_handler()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        // CannotPerformNow and not DoesNotExist: CMDA 1 says the *command* does not exist, which
        // would be a lie about a command this machine has. There is no code for a missing program,
        // so the honest answer is the one that is true.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.CannotPerformNow,
            await pair.Host.SendProgramCommandAsync("NOPE", "START", [], GemPair.Token));

        Assert.Null(_commandProgram);
    }

    [Fact]
    public async Task A_command_the_machine_does_not_have_is_answered_does_not_exist()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        Assert.Equal(
            GemRemoteCommandAcknowledgement.DoesNotExist,
            await pair.Host.SendProgramCommandAsync("RECIPE-7", "NOPE", [], GemPair.Token));
    }

    [Fact]
    public async Task The_control_state_rule_applies_to_the_stream_seven_form_too()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();
        await pair.Host.RequestOfflineAsync(GemPair.Token);

        // E30's rule is about the command, not the stream that asked for it - so a command flagged
        // as needing remote control is refused here exactly as it is for S2F21.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.CannotPerformNow,
            await pair.Host.SendProgramCommandAsync("RECIPE-7", "LOCKED", [], GemPair.Token));

        // And one that does not need it still runs, which is what makes the refusal the flag's
        // doing rather than the control state refusing everything.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.Performed,
            await pair.Host.SendProgramCommandAsync("RECIPE-7", "START", [], GemPair.Token));
    }

    // ------------------------------------------------- S7F29 and S7F39, the two notices

    [Fact]
    public async Task The_machine_can_say_a_program_changed_or_was_selected()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        List<(byte Function, string Program, bool IsChange)> seen = [];
        pair.Host.ProcessProgramNoticeReceived += (_, e) => seen.Add((e.Function, e.ProgramId, e.IsChange));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Equipment.NotifyProgramChangedAsync("RECIPE-7", GemPair.Token));

        Assert.Equal(
            GemProcessProgramAcknowledgement.Accepted,
            await pair.Equipment.NotifyProgramSelectedAsync("RECIPE-7", GemPair.Token));

        await GemPair.WaitUntilAsync(() => seen.Count == 2, "both notices to reach the host");

        // The bodies are identical, so the function is the only thing that separates them - and a
        // host may well care about one and not the other.
        Assert.Equal((29, "RECIPE-7", true), seen[0]);
        Assert.Equal((39, "RECIPE-7", false), seen[1]);
    }

    [Fact]
    public async Task A_notice_naming_no_program_is_not_accepted()
    {
        await using var pair = GemPair.Start(Model, programs: Stored());
        await pair.OpenAsync();

        using var primary = new SecsMessage(7, 29, replyExpected: true)
        {
            Name = "ProcessProgramChangeNotify",
            Item = SecsItem.A(string.Empty).Named("PPID"),
        };

        using var reply = await pair.AskEquipmentAsync(primary);

        // NotFound rather than Accepted: accepting it would tell the machine its notice landed
        // when there is nothing a host could act on.
        Assert.Equal(30, reply.F);
        Assert.Equal(
            (byte)GemProcessProgramAcknowledgement.NotFound,
            reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_notice_nobody_is_listening_to_is_not_sent_and_not_kept()
    {
        var spool = new GemInMemorySpool();
        await using var link = GemLink.Start(Model, programs: Stored(), spool: spool);

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);
        await GemLink.WaitUntilAsync(
            () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
            "the equipment to be spooling with nobody to talk to");

        // Null for "there was nobody to tell", and nothing held. A spooled "RECIPE-7 changed" would
        // reach a host that can get something strictly better by asking for the directory, which
        // answers with the store as it is now rather than with news from an hour ago.
        Assert.Null(await link.Equipment.NotifyProgramChangedAsync("RECIPE-7", GemLink.Token));
        Assert.Equal(0, link.Equipment.SpoolCountActual);
    }

    [Fact]
    public async Task A_notice_the_host_refuses_comes_back_refused()
    {
        await using var link = GemLink.Start(Model, programs: Stored());
        await link.OpenAsync();

        var sent = link.Equipment.NotifyProgramChangedAsync("RECIPE-7", GemLink.Token).AsTask();

        using var notice = await link.ReceiveAsync((byte)GemProcessProgramAcknowledgement.PermissionNotGranted);

        Assert.Equal(29, notice.F);

        // Handed back rather than swallowed, because a host answering anything but Accepted is
        // saying it did not take the notice - and the machine is the only place that can be logged.
        Assert.Equal(
            GemProcessProgramAcknowledgement.PermissionNotGranted,
            await sent.WaitAsync(TimeSpan.FromSeconds(5), GemLink.Token));
    }

    [Fact]
    public async Task A_reserved_ACKC7_on_a_notice_folds_onto_Other()
    {
        var log = new RecordingLogger<GemEquipment>();
        await using var link = GemLink.Start(Model, programs: Stored(), logger: log);
        await link.OpenAsync();

        var sent = link.Equipment.NotifyProgramSelectedAsync("RECIPE-7", GemLink.Token).AsTask();

        using var notice = await link.ReceiveAsync(40);

        Assert.Equal(39, notice.F);
        Assert.Equal(
            GemProcessProgramAcknowledgement.Other,
            await sent.WaitAsync(TimeSpan.FromSeconds(5), GemLink.Token));

        Assert.Contains(
            log.From(LogLevel.Information),
            message => message.Contains("ACKC7 40", StringComparison.Ordinal)
                && message.Contains("reserves", StringComparison.Ordinal));
    }
}
