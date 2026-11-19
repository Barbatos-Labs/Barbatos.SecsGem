// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// What arrives from an end that is not this library, on E39's enhanced stream 2.
/// </summary>
/// <remarks>
/// Fourteen of these sixteen catalogue rows are marked as a representative subset - only S2F49 and
/// S2F50 are definitions - so the nesting this library writes is a reading rather than a
/// quotation. These tests pin down the other half: that a body shaped differently is still
/// understood, that an undefined acknowledgement never reads as success, and that a body which
/// cannot be understood fails loudly rather than quietly.
/// </remarks>
public class EnhancedWireTests
{
    private const string Chamber = "PM1";

    private static SecsMessage Reply(byte function, SecsItem body)
        => new(2, function, replyExpected: false) { Item = body };

    // ----------------------------------------------- what the asking end accepts

    [Fact]
    public async Task A_command_answered_with_no_code_is_a_link_failure_and_never_a_success()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(50, SecsItem.L()));
        await pair.OpenAsync();

        // Nought is HCACK Accepted, so a reader that takes the first byte of an empty body reports
        // a command that was never performed.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.CommandAsync(Chamber, "START", cancellationToken: EnhancedPair.Token));
    }

    [Fact]
    public async Task A_command_answered_with_a_bare_code_is_still_read()
    {
        // The item list says L,2 and a machine that sends the code alone has still said what it
        // meant. The dangerous direction is the other one, and the test above covers it.
        await using var pair = EnhancedPair.Start(answer: _ => Reply(50, SecsItem.B(2)));
        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "START", cancellationToken: EnhancedPair.Token);

        Assert.Equal(EnhancedCommandAcknowledgement.CannotPerformNow, result.Acknowledgement);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task A_command_code_in_a_numeric_format_is_still_read()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(
            50,
            SecsItem.L(SecsItem.U1(6), SecsItem.L())));

        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "START", cancellationToken: EnhancedPair.Token);

        Assert.Equal(EnhancedCommandAcknowledgement.NoSuchObject, result.Acknowledgement);
    }

    [Fact]
    public async Task A_parameter_acknowledgement_list_is_read_even_when_the_entries_are_ragged()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(
            50,
            SecsItem.L(
                SecsItem.B(3),
                SecsItem.L(
                    SecsItem.L(SecsItem.A("TEMP"), SecsItem.B(2)),

                    // An entry with no code at all, which the reader skips rather than reading the
                    // next entry's name as this one's code.
                    SecsItem.L(SecsItem.A("RATE")),
                    SecsItem.L(SecsItem.A("TIME"), SecsItem.U4(1))))));

        await pair.OpenAsync();

        var result = await pair.Host.CommandAsync(Chamber, "SET", cancellationToken: EnhancedPair.Token);

        Assert.Equal(EnhancedCommandAcknowledgement.InvalidParameter, result.Acknowledgement);
        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal("TEMP", result.Parameters[0].Name);
        Assert.Equal(EnhancedParameterAcknowledgement.IllegalValue, result.Parameters[0].Acknowledgement);
        Assert.Equal("TIME", result.Parameters[1].Name);
        Assert.Equal(EnhancedParameterAcknowledgement.NameDoesNotExist, result.Parameters[1].Acknowledgement);
    }

    [Fact]
    public async Task A_bind_answered_with_nothing_is_a_link_failure()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(52, SecsItem.L()));
        await pair.OpenAsync();

        // RPACK is not in the dictionary at all, so nought is this library's reading of accepted -
        // which makes an empty body the most dangerous thing a peer can send, and it throws.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.BindReportAsync(5, Chamber, EnhancedPair.Token));
    }

    [Fact]
    public async Task A_bind_answered_inside_a_list_of_one_is_still_read()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(52, SecsItem.L(SecsItem.B(0))));
        await pair.OpenAsync();

        Assert.True((await pair.Host.BindReportAsync(5, Chamber, EnhancedPair.Token)).IsAccepted);
    }

    [Fact]
    public async Task A_bind_code_this_library_does_not_name_is_not_a_success_and_is_still_reported()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(52, SecsItem.B(9)));
        await pair.OpenAsync();

        var result = await pair.Host.BindReportAsync(5, Chamber, EnhancedPair.Token);

        // Both halves matter. An undefined code never reads as success, and the byte survives so
        // that a fab whose host uses code 9 for something can still see it.
        Assert.False(result.IsAccepted);
        Assert.Equal((byte)9, result.Code);
    }

    [Fact]
    public async Task A_value_reply_that_is_not_a_list_is_a_link_failure()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(58, SecsItem.A("250")));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.GetStatusVariablesAsync(Chamber, [1234], _ => { }, EnhancedPair.Token));
    }

    [Fact]
    public async Task A_short_value_reply_is_handed_over_rather_than_hidden()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(58, SecsItem.L(SecsItem.U4(250))));
        await pair.OpenAsync();

        var count = await pair.Host.GetStatusVariablesAsync(Chamber, [1234, 1235], _ => { }, EnhancedPair.Token);

        // One value for two identifiers. This end cannot repair it - it does not know which
        // identifier the value belongs to - so it reports the count and lets the caller decide,
        // having said so in the log.
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task An_alarm_reply_that_is_not_a_list_of_lists_is_a_link_failure()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(62, SecsItem.L(SecsItem.A("9000"))));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.GetAlarmsAsync(Chamber, [], EnhancedPair.Token));
    }

    [Fact]
    public async Task An_alarm_with_no_text_is_read_as_an_alarm_with_no_text()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(
            62,
            SecsItem.L(SecsItem.L(SecsItem.B(0x80), SecsItem.U2(9000)))));

        await pair.OpenAsync();

        var alarm = Assert.Single(await pair.Host.GetAlarmsAsync(Chamber, [], EnhancedPair.Token));

        // ALTX missing, and ALID in a narrower format than the catalogue's U4: both are a machine
        // being economical rather than wrong.
        Assert.Equal(9000UL, alarm.Id);
        Assert.True(alarm.IsSet);
        Assert.Equal(string.Empty, alarm.Text);
    }

    [Fact]
    public async Task A_diagnostic_reply_with_the_wrong_data_id_is_still_handed_over()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(
            64,
            SecsItem.L(SecsItem.U4(77), SecsItem.A("something"))));

        await pair.OpenAsync();

        var text = string.Empty;

        // Worth saying and not worth throwing: DATAID is the only thing S2F64 can be correlated
        // by, so a machine that echoes a different one has answered a question nobody asked - and
        // a caller that wanted the data still gets it.
        Assert.True(await pair.Host.GetDiagnosticsAsync(Chamber, 99, data => text = data.GetString(), EnhancedPair.Token));
        Assert.Equal("something", text);
    }

    [Fact]
    public async Task A_diagnostic_reply_with_no_data_item_is_a_link_failure()
    {
        await using var pair = EnhancedPair.Start(answer: _ => Reply(64, SecsItem.L(SecsItem.U4(99))));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.GetDiagnosticsAsync(Chamber, 99, _ => { }, EnhancedPair.Token));
    }

    // -------------------------------------------- what the answering end accepts

    [Fact]
    public async Task A_value_request_with_a_bare_identifier_is_still_answered()
    {
        await using var pair = EnhancedPair.Start(store =>
            store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 1234, SecsItem.U4(250)));

        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 57, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A(Chamber), SecsItem.U4(1234)),
        };

        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        Assert.Equal(58, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal(250u, reply.Item.Items[0].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task A_value_request_with_no_identifier_list_asks_for_everything()
    {
        await using var pair = EnhancedPair.Start(store =>
        {
            store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 1, SecsItem.U4(1));
            store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 2, SecsItem.U4(2));
        });

        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 57, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A(Chamber)),
        };

        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        // No list at all reads the same as an empty one, which is the rule GEM's S1F3 follows.
        Assert.NotNull(reply.Item);
        Assert.Equal(2, reply.Item.Count);
    }

    [Fact]
    public async Task An_identifier_in_a_narrower_format_still_names_the_same_variable()
    {
        await using var pair = EnhancedPair.Start(store =>
        {
            store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 1, SecsItem.A("one"));
            store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 2, SecsItem.A("two"));
        });

        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 57, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A(Chamber), SecsItem.L(SecsItem.U1(2))),
        };

        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        // Two variables on the machine and one asked for, which is the part that matters: an
        // identifier this end failed to read would fall through to "all of them" and a store with
        // only one variable in it would answer identically either way.
        Assert.NotNull(reply.Item);
        Assert.Equal(1, reply.Item.Count);
        Assert.Equal("two", reply.Item.Items[0].GetString());
    }

    [Fact]
    public async Task A_value_request_this_end_cannot_read_is_answered_with_an_empty_list()
    {
        await using var pair = EnhancedPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 57, replyExpected: true) { Item = SecsItem.A("not a list") };
        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        // S2F58 has no acknowledgement code, so a zero-length list is the only answer E5 leaves
        // for a request that could not be read - the same arrangement stream 14's gets have.
        Assert.Equal(58, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact]
    public async Task A_command_this_end_cannot_read_is_refused_with_a_code_rather_than_with_silence()
    {
        await using var pair = EnhancedPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 49, replyExpected: true) { Item = SecsItem.A("not a list") };
        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        // S2F50 does carry a code, unlike S2F58, so an unreadable body is said. Three is the only
        // defined code for "what you sent was wrong" that does not also claim the command does not
        // exist, which this end cannot know.
        Assert.Equal(50, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal(
            (byte)EnhancedCommandAcknowledgement.InvalidParameter,
            reply.Item.Items[0].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_command_with_its_parameters_flat_rather_than_nested_is_still_read()
    {
        string? value = null;

        await using var pair = EnhancedPair.Start(store => store.SetCommand(Chamber, "SET", request =>
        {
            value = request.Parameters.Count > 0 ? request.Parameters[0].Value : null;

            return EnhancedCommandResult.From(EnhancedCommandAcknowledgement.Accepted);
        }));

        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 49, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.U4(1),
                SecsItem.A(Chamber),
                SecsItem.A("SET"),

                // Flat pairs rather than two-element lists, and a numeric CPVAL where the
                // catalogue says A - both of which the shared stream 14 reader already tolerates.
                SecsItem.L(SecsItem.A("TEMP"), SecsItem.U4(450))),
        };

        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        Assert.NotNull(reply.Item);
        Assert.Equal(
            (byte)EnhancedCommandAcknowledgement.Accepted,
            reply.Item.Items[0].FirstValueOrDefault<byte>());
        Assert.Equal("450", value);
    }

    [Fact]
    public async Task A_diagnostic_reply_echoes_the_data_id_it_was_asked_for()
    {
        await using var pair = EnhancedPair.Start(store =>
        {
            store.SetDiagnostic(Chamber, 99, SecsItem.A("pump log"));
            store.SetDiagnostic(Chamber, 7, SecsItem.A("heater log"));
        });

        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 63, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A(Chamber), SecsItem.U4(7)),
        };

        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        // DATAID is the only thing S2F64 can be correlated by, so echoing the request's rather
        // than nought or the last one is the whole of what makes two outstanding diagnostics
        // tellable apart.
        Assert.NotNull(reply.Item);
        Assert.Equal(7u, reply.Item.Items[0].FirstValueOrDefault<uint>());
        Assert.Equal("heater log", reply.Item.Items[1].GetString());
    }

    [Fact]
    public async Task A_bind_request_this_end_cannot_read_is_denied()
    {
        await using var pair = EnhancedPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 51, replyExpected: true) { Item = SecsItem.U4(5) };
        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        Assert.Equal(52, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)EnhancedReportAcknowledgement.Denied, reply.Item.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_diagnostic_request_this_end_cannot_read_echoes_nothing_and_carries_nothing()
    {
        await using var pair = EnhancedPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 63, replyExpected: true) { Item = SecsItem.A(Chamber) };
        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        Assert.Equal(64, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal(2, reply.Item.Count);
        Assert.Equal(0u, reply.Item.Items[0].FirstValueOrDefault<uint>());
        Assert.True(reply.Item.Items[1].IsList);
        Assert.Equal(0, reply.Item.Items[1].Count);
    }

    [Fact]
    public async Task A_request_for_an_object_this_end_has_never_heard_of_is_answered_rather_than_dropped()
    {
        await using var pair = EnhancedPair.Start(store =>
            store.SetVariable(Chamber, EnhancedVariableKind.StatusVariable, 1234, SecsItem.U4(250)));

        await pair.OpenAsync();

        using var primary = new SecsMessage(2, 57, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("PM9"), SecsItem.L(SecsItem.U4(1234))),
        };

        using var reply = await pair.HostLink.SendAsync(primary, EnhancedPair.Token);

        // One identifier asked for, one placeholder back. An unknown object is not an error in
        // this stream - there is no code in S2F58 to carry one - so it reads exactly like an
        // object with none of the variables asked for, which is what it is.
        Assert.Equal(58, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal(1, reply.Item.Count);
        Assert.True(reply.Item.Items[0].IsList);
        Assert.Equal(0, reply.Item.Items[0].Count);
    }
}
