// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The four pairs the coverage inventory found: two namelists, the simple remote command and the
/// loopback.
/// </summary>
/// <remarks>
/// <para>
/// All four are rows the source behind the catalogue <strong>documented</strong> rather than
/// flagged as representative, and all four are made entirely of defined items - which is why they
/// were the first of the gaps to close. Of the 122 primaries this repository did not implement,
/// seven were definitions; these are four of the seven.
/// </para>
/// <para>
/// Their shapes are not guesses either. S1F22 and S1F24 are built the way S1F12 and S2F30 are -
/// identifier, name, then whatever else the row names - and S2F22 and S2F26 carry one scalar each,
/// which leaves nothing to decide.
/// </para>
/// </remarks>
public class GemNamelistAndDiagnosticTests
{
    private string? _started;

    private void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-001"))
            .AddDataVariable(11, "WaferCount", () => SecsItem.U4(25), "wafers")
            .AddEvent(1_000, "ProcessEnd", [10u, 11u])
            .AddEvent(2_000, "Loaded")
            // Neither of these needs remote control, so the tests below reach the handler
            // without first driving the control model - which this file is not about. LOCKED is the
            // one that does need it, and it is how the control rule itself is tested.
            .AddRemoteCommand(
                "START",
                (_, _) =>
                {
                    _started = "yes";
                    return ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted);
                },
                requiresRemoteControl: false)
            .AddRemoteCommand(
                "PUMP",
                (_, _) => ValueTask.FromResult(GemHostCommandAcknowledgement.CompletionLater),
                requiresRemoteControl: false)
            .AddRemoteCommand(
                "LOCKED",
                (_, _) => ValueTask.FromResult(GemHostCommandAcknowledgement.Accepted));

    // --------------------------------------------------------------- the data variable namelist

    [Fact]
    public async Task The_data_variable_namelist_answers_with_names_and_units()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var all = await pair.Host.ReadDataVariableNamelistAsync([], GemPair.Token);

        // Every data variable and no status variable: S1F21 is S1F11's sibling and the two must
        // not answer each other's question. ControlState is a status variable and must not appear.
        Assert.Equal(2, all.Count);
        Assert.Equal<ulong[]>([10, 11], [.. all.Select(description => description.Id)]);
        Assert.Equal("LotId", all[0].Name);
        Assert.Equal("WaferCount", all[1].Name);
        Assert.Equal("wafers", all[1].Units);
        Assert.Equal(string.Empty, all[0].Units);
    }

    [Fact]
    public async Task A_data_variable_the_machine_has_not_got_comes_back_named_nothing()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Aligned with the request rather than filtered, which is what lets a host read the two
        // side by side - and the status variable is refused here for the same reason it is absent
        // from the full list above.
        var answered = await pair.Host.ReadDataVariableNamelistAsync([11, 999, 1], GemPair.Token);

        Assert.Equal(3, answered.Count);
        Assert.Equal<ulong[]>([11, 999, 1], [.. answered.Select(description => description.Id)]);
        Assert.True(answered[0].Exists);
        Assert.False(answered[1].Exists);
        Assert.False(answered[2].Exists);
    }

    // ------------------------------------------------------------ the collection event namelist

    [Fact]
    public async Task The_event_namelist_answers_with_names_and_the_variables_each_is_about()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var all = await pair.Host.ReadCollectionEventNamelistAsync([], GemPair.Token);

        Assert.Equal(2, all.Count);
        Assert.Equal("ProcessEnd", all[0].Name);
        Assert.Equal<ulong[]>([10, 11], [.. all[0].VariableIds]);

        // An event the machine has not associated with anything comes back named, with no
        // variables - which is not a refusal. A machine may have an event that is about nothing in
        // particular, and S1F24 has no way to say "I decline to tell you".
        Assert.Equal("Loaded", all[1].Name);
        Assert.Empty(all[1].VariableIds);
        Assert.True(all[1].Exists);
    }

    [Fact]
    public async Task An_event_the_machine_has_not_got_comes_back_named_nothing()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var answered = await pair.Host.ReadCollectionEventNamelistAsync([1_000, 777], GemPair.Token);

        Assert.Equal(2, answered.Count);
        Assert.True(answered[0].Exists);
        Assert.False(answered[1].Exists);
        Assert.Empty(answered[1].VariableIds);
        Assert.Equal("CEID 777 is not one this machine has", answered[1].ToString());
    }

    [Fact]
    public async Task The_namelist_is_the_machines_account_and_not_the_hosts_reports()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Relink the event to a report carrying only one variable, which is the thing most easily
        // confused with the namelist - and then ask again. The answer must not move.
        await pair.Host.DefineReportsAsync([(5_000UL, [10UL])], GemPair.Token);
        await pair.Host.LinkEventsAsync([(1_000UL, [5_000UL])], GemPair.Token);

        var described = await pair.Host.ReadCollectionEventNamelistAsync([1_000], GemPair.Token);

        Assert.Equal<ulong[]>([10, 11], [.. Assert.Single(described).VariableIds]);
    }

    // --------------------------------------------------------------- the simple remote command

    [Fact]
    public async Task The_simple_remote_command_runs_the_same_handler_as_the_one_with_parameters()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        Assert.Equal(
            GemRemoteCommandAcknowledgement.Performed,
            await pair.Host.SendRemoteCommandAsync("START", GemPair.Token));

        Assert.Equal("yes", _started);
    }

    [Fact]
    public async Task A_command_the_machine_does_not_have_is_answered_does_not_exist()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // CMDA 1 is "command does not exist", which is the one code of the three that is not a
        // refusal about timing - and the only part of HCACK's seven that survives the narrowing
        // with its own meaning.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.DoesNotExist,
            await pair.Host.SendRemoteCommandAsync("NOPE", GemPair.Token));
    }

    [Fact]
    public async Task Seven_codes_narrow_onto_three_and_the_detail_is_lost_on_purpose()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // PUMP answers HCACK 4, "will be performed with completion signalled later". S2F22 carries
        // CMDA, which has no such code, so it comes back as CannotPerformNow - which is not what
        // the machine meant. That loss is the message's and not this library's, and a host that
        // needs the distinction sends S2F41 with no parameters instead.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.CannotPerformNow,
            await pair.Host.SendRemoteCommandAsync("PUMP", GemPair.Token));

        Assert.Equal(
            GemHostCommandAcknowledgement.CompletionLater,
            await pair.Host.SendCommandAsync("PUMP", [], GemPair.Token));
    }

    [Fact]
    public async Task A_command_that_needs_remote_control_is_refused_when_the_host_is_not_in_charge()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // The pair comes up in remote control, so the machine is sent off-line first - which is
        // also the honest way round: a command refused for the control state is a thing that
        // happens to a host that was in charge a moment ago.
        await pair.Host.RequestOfflineAsync(GemPair.Token);

        // The shorter message is not the safer one: the control-state rule is E30's and applies to
        // both commands. So LOCKED is refused - and refused as CannotPerformNow, because CMDA has
        // no code for "you are not in charge", which is detail S2F41 would have kept.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.CannotPerformNow,
            await pair.Host.SendRemoteCommandAsync("LOCKED", GemPair.Token));

        // And the command's own flag is what decided it, not the control state on its own: START
        // is off the same table, arrives over the same message in the same state, and runs. A test
        // that only asserted the refusal would pass against a handler that refused everything.
        Assert.Equal(
            GemRemoteCommandAcknowledgement.Performed,
            await pair.Host.SendRemoteCommandAsync("START", GemPair.Token));

        Assert.Equal("yes", _started);
    }

    // ------------------------------------------------------------------------ the loopback

    [Fact]
    public async Task The_loopback_echoes_what_it_was_sent()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        byte[] sent = [0x00, 0x7F, 0xFF, 0x01];

        Assert.True(await pair.Host.LoopbackAsync(sent, GemPair.Token));
        Assert.Equal<byte[]>(sent, [.. (await pair.Host.LoopbackDataAsync(sent, GemPair.Token)).Span]);
    }

    [Fact]
    public async Task An_empty_loopback_is_still_an_answer()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Zero bytes is a legitimate thing to send: ABS has no minimum, and a host checking that a
        // body crosses at all may well send none.
        Assert.True(await pair.Host.LoopbackAsync(default, GemPair.Token));
        Assert.True((await pair.Host.LoopbackDataAsync(default, GemPair.Token)).IsEmpty);
    }

    [Fact]
    public async Task The_loopback_is_answered_off_line_because_a_diagnostic_you_cannot_run_is_no_use()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();
        await pair.Host.RequestOfflineAsync(GemPair.Token);

        // Every other message that changes anything consults the control state. This one changes
        // nothing, and refusing it would make the link untestable in exactly the state where a
        // host most wants to test it.
        Assert.True(await pair.Host.LoopbackAsync(new byte[] { 9 }, GemPair.Token));
    }

    [Fact]
    public async Task The_loopback_goes_the_other_way_too()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // S2F25's direction is Either, so the machine can test the host. Sent by hand because no
        // GemEquipment method sends it - a machine that wants to check a host writes the message
        // itself, and what matters is that the host answers.
        using var primary = new SecsMessage(2, 25, replyExpected: true)
        {
            Name = "LoopbackDiagnosticRequest",
            Item = SecsItem.B(1, 2, 3).Named("ABS"),
        };

        using var reply = await pair.AskEquipmentAsync(primary);

        Assert.Equal(26, reply.F);
        Assert.Equal<byte[]>([1, 2, 3], [.. reply.Item!.GetMemory<byte>().Span]);
    }

    [Fact]
    public async Task A_loopback_body_sent_as_text_is_echoed_as_text()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 25, replyExpected: true)
        {
            Name = "LoopbackDiagnosticRequest",
            Item = SecsItem.A("ping").Named("ABS"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, GemPair.Token);

        // ABS is defined as a binary, so a machine sending text is being loose - but an echo that
        // corrected the format would be answering a different question from the one asked.
        Assert.True(reply.Item!.Format.IsText());
        Assert.Equal("ping", reply.Item.GetString());
        Assert.Equal<byte[]>(Encoding.ASCII.GetBytes("ping"), [.. Encoding.ASCII.GetBytes(reply.Item.GetString())]);
    }
}
