// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// E39's enhanced stream 2, over a real link, both ends.
/// </summary>
/// <remarks>
/// The eight transactions that let a host say <em>which chamber</em>. GEM can ask a machine for
/// <c>SVID</c> 1234 and the machine has one answer; a cluster tool has four, and these are the
/// messages with somewhere to put the question.
/// </remarks>
public class EnhancedServicesTests
{
    private const string Chamber = "PM1";
    private const string OtherChamber = "PM2";

    private static EnhancedPair Loaded(Func<SecsTransaction, SecsMessage?>? answer = null)
        => EnhancedPair.Start(
            store =>
            {
                store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 1234, SecsItem.U4(250));
                store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 1235, SecsItem.A("ETCHING"));
                store.SetVariable(OtherChamber, EnhancedVariableKind.StatusVariable, 1234, SecsItem.U4(90));
                store.SetVariable(Chamber, EnhancedVariableKind.EquipmentConstant, 7, SecsItem.F8(1_000d));
                store.SetVariable(Chamber, EnhancedVariableKind.DataVariable, 42, SecsItem.U2(3));
                store.SetDiagnostic(Chamber, 99, SecsItem.L(SecsItem.A("PUMP"), SecsItem.U4(17)));
                store.SetAlarm(Chamber, new EnhancedAlarm { Id = 9000, Code = 0x82, Text = "Chamber pressure out of range" });
                store.SetAlarm(Chamber, new EnhancedAlarm { Id = 9001, Code = 0x06, Text = "Heater drifting" });
                store.SetCommand(Chamber, "PP-SELECT", _ => EnhancedCommandResult.From(EnhancedCommandAcknowledgement.Accepted));
                store.SetCommand(Chamber, "START", _ => EnhancedCommandResult.From(EnhancedCommandAcknowledgement.CompletionLater));
                store.SetCommand(
                    Chamber,
                    "SET-TEMP",
                    request => new EnhancedCommandResult
                    {
                        Acknowledgement = EnhancedCommandAcknowledgement.InvalidParameter,
                        Parameters =
                        [
                            new EnhancedParameterResult
                            {
                                Name = request.Parameters.Count > 0 ? request.Parameters[0].Name : "?",
                                Acknowledgement = EnhancedParameterAcknowledgement.IllegalValue,
                            },
                        ],
                    });
            },
            answer);

    [Fact]
    public async Task The_eight_primaries_do_not_collide_with_GEM_own_nine()
    {
        // Worth a test rather than a comment: both sets are stream 2, and a collision would be a
        // routing table where one registration silently won.
        byte[] gem = [13, 15, 17, 29, 31, 33, 35, 37, 41];

        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages)
        {
            Assert.Equal(2, stream);
            Assert.DoesNotContain(function, gem);
        }

        Assert.Equal(8, EnhancedServicesEquipment.HandledMessages.Count);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_status_variable_is_read_of_one_object_and_not_of_the_machine()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        uint one = 0;
        uint two = 0;

        await pair.Host.GetStatusVariablesAsync(Chamber, [1234], values => one = values[0].FirstValueOrDefault<uint>(), EnhancedPair.Token);
        await pair.Host.GetStatusVariablesAsync(OtherChamber, [1234], values => two = values[0].FirstValueOrDefault<uint>(), EnhancedPair.Token);

        // One identifier, two chambers, two answers - which S1F3 cannot do at all.
        Assert.Equal(250u, one);
        Assert.Equal(90u, two);
        pair.AssertAsked(2, 57);
    }

    [Fact]
    public async Task A_variable_the_machine_does_not_have_is_a_zero_length_item_in_its_place()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        List<int> counts = [];
        var temperature = 0u;
        var state = string.Empty;

        var answered = await pair.Host.GetStatusVariablesAsync(
            Chamber,
            [1234, 7777, 1235],
            values =>
            {
                foreach (var value in values)
                {
                    counts.Add(value.Count);
                }

                temperature = values[0].FirstValueOrDefault<uint>();

                // The placeholder is a zero-length list, so it is not a number and not text
                // either - which is how a caller tells "absent" from a reading of nought.
                Assert.True(values[1].IsList);

                state = values[2].GetString();
            },
            EnhancedPair.Token);

        // Three asked, three answered, and the gap is in the middle where it belongs: a reply that
        // dropped the unknown one would put ETCHING against 7777.
        Assert.Equal(3, answered);
        Assert.Equal([1, 0, 7], counts);
        Assert.Equal(250u, temperature);
        Assert.Equal("ETCHING", state);
    }

    [Fact]
    public async Task An_empty_request_asks_for_every_variable_of_that_kind()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var count = 0;

        await pair.Host.GetStatusVariablesAsync(Chamber, [], _ => { }, EnhancedPair.Token);
        await pair.Host.GetStatusVariablesAsync(Chamber, [], values => count = values.Count, EnhancedPair.Token);

        // Two status variables on PM1, and not the equipment constant or the data variable: E30
        // keeps the three apart and so does the store.
        Assert.Equal(2, count);
    }

    [Fact]
    public async Task The_three_kinds_of_variable_are_kept_apart()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var constant = 0d;
        var data = (ushort)0;
        var status = 0;

        await pair.Host.GetEquipmentConstantsAsync(Chamber, [7], values => constant = values[0].FirstValueOrDefault<double>(), EnhancedPair.Token);
        await pair.Host.GetDataVariablesAsync(Chamber, [42], values => data = values[0].FirstValueOrDefault<ushort>(), EnhancedPair.Token);

        // SVID 7 does not exist even though ECID 7 does, which is the distinction a store that
        // lumped them together would lose.
        await pair.Host.GetStatusVariablesAsync(Chamber, [7], values => status = values[0].Count, EnhancedPair.Token);

        Assert.Equal(1_000d, constant);
        Assert.Equal((ushort)3, data);
        Assert.Equal(0, status);
        pair.AssertAsked(2, 55);
        pair.AssertAsked(2, 59);
    }

    [Fact]
    public async Task A_command_is_addressed_to_an_object()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "PP-SELECT", cancellationToken: EnhancedPair.Token);

        Assert.Equal(EnhancedCommandAcknowledgement.Accepted, result.Acknowledgement);
        Assert.True(result.IsAccepted);
        Assert.Empty(result.Parameters);
        pair.AssertAsked(2, 49);
    }

    [Fact]
    public async Task A_command_that_will_finish_later_is_still_accepted()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "START", cancellationToken: EnhancedPair.Token);

        // HCACK 4 is a success. A caller comparing the code to nought treats every long-running
        // command as a failure, which is the commonest mistake made against this message.
        Assert.Equal(EnhancedCommandAcknowledgement.CompletionLater, result.Acknowledgement);
        Assert.True(result.IsAccepted);
    }

    [Fact]
    public async Task A_command_to_an_object_nobody_declared_says_so_in_the_code_the_catalogue_kept_for_it()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync("PM9", "PP-SELECT", cancellationToken: EnhancedPair.Token);

        // HCACK 6. Nothing in GEM can ever return it - S2F41 commands the equipment and there is
        // only one of those - so a code for "no such object" is the catalogue saying out loud that
        // a command is expected to be addressed to something.
        Assert.Equal(EnhancedCommandAcknowledgement.NoSuchObject, result.Acknowledgement);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task A_command_nobody_registered_is_told_apart_from_an_object_nobody_declared()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "LAUNCH", cancellationToken: EnhancedPair.Token);

        Assert.Equal(EnhancedCommandAcknowledgement.InvalidCommand, result.Acknowledgement);
    }

    [Fact]
    public async Task A_command_names_which_parameter_was_wrong()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(
            Chamber,
            "SET-TEMP",
            [new ObjectParameter { Name = "TEMP", Value = "9000" }],
            dataId: 11,
            EnhancedPair.Token);

        Assert.Equal(EnhancedCommandAcknowledgement.InvalidParameter, result.Acknowledgement);

        var parameter = Assert.Single(result.Parameters);

        Assert.Equal("TEMP", parameter.Name);
        Assert.Equal(EnhancedParameterAcknowledgement.IllegalValue, parameter.Acknowledgement);
    }

    [Fact]
    public async Task A_parameter_value_survives_the_round_trip()
    {
        string? name = null;
        string? value = null;

        await using var pair = EnhancedPair.Start(store => store.SetCommand(Chamber, "SET-TEMP", request =>
        {
            name = request.Parameters[0].Name;
            value = request.Parameters[0].Value;

            return EnhancedCommandResult.From(EnhancedCommandAcknowledgement.Accepted);
        }));

        await pair.OpenAsync();

        await pair.Host.CommandAsync(
            Chamber,
            "SET-TEMP",
            [new ObjectParameter { Name = "TEMP", Value = "450" }],
            cancellationToken: EnhancedPair.Token);

        Assert.Equal("TEMP", name);
        Assert.Equal("450", value);
    }

    [Fact]
    public async Task A_handler_that_throws_does_not_take_the_link_down()
    {
        await using var pair = EnhancedPair.Start(store => store.SetCommand(
            Chamber,
            "BREAK",
            _ => throw new InvalidOperationException("the application has a bug")));

        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "BREAK", cancellationToken: EnhancedPair.Token);

        // The same guard GEM puts on a remote command handler: a bug in the application is not a
        // reason to drop a link that is serving everything else.
        Assert.Equal(EnhancedCommandAcknowledgement.CannotPerformNow, result.Acknowledgement);

        // And the link still works afterwards, which is the half worth asserting.
        Assert.Equal(
            EnhancedCommandAcknowledgement.InvalidCommand,
            (await pair.Host.CommandAsync(Chamber, "OTHER", cancellationToken: EnhancedPair.Token)).Acknowledgement);
    }

    [Fact]
    public async Task An_object_alarm_list_comes_back_with_its_codes_and_its_text()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var alarms = await pair.Host.GetAlarmsAsync(Chamber, [], EnhancedPair.Token);

        Assert.Equal(2, alarms.Count);
        Assert.Equal(9000UL, alarms[0].Id);
        Assert.Equal("Chamber pressure out of range", alarms[0].Text);

        // ALCD's top bit is the state and its low bits are the category, which is why one message
        // carries both a set and a clear.
        Assert.True(alarms[0].IsSet);
        Assert.False(alarms[1].IsSet);
        pair.AssertAsked(2, 61);
    }

    [Fact]
    public async Task An_alarm_the_machine_does_not_have_is_absent_rather_than_padded()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var alarms = await pair.Host.GetAlarmsAsync(Chamber, [9000, 7777], EnhancedPair.Token);

        // Unlike the variable replies: S2F62's entries carry their own ALID, so nothing needs a
        // place held for it.
        Assert.Equal(9000UL, Assert.Single(alarms).Id);
    }

    [Fact]
    public async Task An_object_with_no_alarms_answers_an_empty_list()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.GetAlarmsAsync(OtherChamber, [], EnhancedPair.Token));
    }

    [Fact]
    public async Task Diagnostic_data_is_whatever_the_machine_produced()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var parts = 0;
        var pump = string.Empty;

        var found = await pair.Host.GetDiagnosticsAsync(
            Chamber,
            99,
            data =>
            {
                parts = data.Count;
                pump = data.Items[0].GetString();
            },
            EnhancedPair.Token);

        // DGDATA is not in the dictionary, so it is a whole item here for the same reason ATTRDATA
        // is: an undefined item has no format of its own.
        Assert.True(found);
        Assert.Equal(2, parts);
        Assert.Equal("PUMP", pump);
        pair.AssertAsked(2, 63);
    }

    [Fact]
    public async Task A_diagnostic_the_machine_does_not_have_reads_as_absent()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var called = false;

        var found = await pair.Host.GetDiagnosticsAsync(Chamber, 7777, _ => called = true, EnhancedPair.Token);

        Assert.False(found);
        Assert.False(called);
    }

    [Fact]
    public async Task A_report_is_tied_to_an_object_and_untied_again()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        var bound = await pair.Host.BindReportAsync(5, Chamber, EnhancedPair.Token);

        Assert.True(bound.IsAccepted);
        Assert.Equal(EnhancedReportAcknowledgement.Accepted, bound.Acknowledgement);
        Assert.Equal(5u, Assert.Single(pair.Provider.Bindings).ReportId);

        // Twice says nothing, so it is denied.
        Assert.False((await pair.Host.BindReportAsync(5, Chamber, EnhancedPair.Token)).IsAccepted);

        var unbound = await pair.Host.UnbindReportAsync(5, Chamber, EnhancedPair.Token);

        Assert.True(unbound.IsAccepted);
        Assert.Empty(pair.Provider.Bindings);

        // And untying one that was never tied is denied too.
        Assert.False((await pair.Host.UnbindReportAsync(5, Chamber, EnhancedPair.Token)).IsAccepted);

        pair.AssertAsked(2, 51);
        pair.AssertAsked(2, 53);
    }

    [Fact]
    public async Task A_report_bound_to_an_object_nobody_declared_is_denied()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        Assert.False((await pair.Host.BindReportAsync(5, "PM9", EnhancedPair.Token)).IsAccepted);
    }

    [Fact]
    public async Task A_provider_that_implements_nothing_answers_nothing_rather_than_failing()
    {
        await using var pair = EnhancedPair.Start(provider: new BareProvider());
        await pair.OpenAsync();

        var values = -1;

        await pair.Host.GetStatusVariablesAsync(Chamber, [1], v => values = v.Count, EnhancedPair.Token);

        // The interface defaults. An equipment that serves no commands and no diagnostics does not
        // write methods saying so - but a positional reply still lines up, because the library
        // pads it.
        Assert.Equal(1, values);
        Assert.Equal(
            EnhancedCommandAcknowledgement.InvalidCommand,
            (await pair.Host.CommandAsync(Chamber, "ANY", cancellationToken: EnhancedPair.Token)).Acknowledgement);
        Assert.False(await pair.Host.GetDiagnosticsAsync(Chamber, 1, _ => { }, EnhancedPair.Token));
        Assert.False((await pair.Host.BindReportAsync(1, Chamber, EnhancedPair.Token)).IsAccepted);
        Assert.False((await pair.Host.UnbindReportAsync(1, Chamber, EnhancedPair.Token)).IsAccepted);
        Assert.Empty(await pair.Host.GetAlarmsAsync(Chamber, [], EnhancedPair.Token));
    }

    [Fact]
    public async Task A_provider_that_answers_the_wrong_number_of_values_cannot_put_a_host_out_of_step()
    {
        await using var pair = EnhancedPair.Start(provider: new MiscountingProvider());
        await pair.OpenAsync();

        List<int> counts = [];

        var answered = await pair.Host.GetStatusVariablesAsync(
            Chamber,
            [1, 2, 3, 4],
            values =>
            {
                foreach (var value in values)
                {
                    counts.Add(value.Count);
                }
            },
            EnhancedPair.Token);

        // Two values for four identifiers. The reply carries no identifiers, so an unpadded answer
        // would hand the host two readings against the wrong variables and no way to notice.
        Assert.Equal(4, answered);
        Assert.Equal([1, 1, 0, 0], counts);
    }

    [Fact]
    public async Task Null_arguments_throw()
    {
        await using var pair = Loaded();
        await pair.OpenAsync();

        Assert.Throws<ArgumentNullException>(() => new EnhancedServicesHost(null!));
        Assert.Throws<ArgumentNullException>(() => new EnhancedServicesEquipment(null!));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.CommandAsync(null!, "X", cancellationToken: EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.CommandAsync(Chamber, null!, cancellationToken: EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.GetStatusVariablesAsync(null!, [], _ => { }, EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.GetStatusVariablesAsync(Chamber, null!, _ => { }, EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.GetStatusVariablesAsync(Chamber, [], null!, EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.GetAlarmsAsync(null!, [], EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.GetDiagnosticsAsync(null!, 1, _ => { }, EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.BindReportAsync(1, null!, EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Host.UnbindReportAsync(1, null!, EnhancedPair.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Equipment.HandleAsync(null!, EnhancedPair.Token));
    }

    /// <summary>A provider that overrides nothing, so every call lands on an interface default.</summary>
    private sealed class BareProvider : IEnhancedServiceProvider;

    /// <summary>A provider that answers fewer values than it was asked for.</summary>
    private sealed class MiscountingProvider : IEnhancedServiceProvider
    {
        public ValueTask<IReadOnlyList<SecsItem>> GetStatusVariablesAsync(
            EnhancedValueRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<SecsItem>>([SecsItem.U1(1), SecsItem.U1(2)]);
    }
}
