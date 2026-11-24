// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The last rows of the gapless streams: an exception's other three stages, the terminal nobody can
/// reach, and the two status forms.
/// </summary>
/// <remarks>
/// <para>
/// Streams 1, 5 and 10 are three of the seven streams in this reference with no gaps of either
/// kind, and each had a handful of rows left that the reference illustrates rather than defines.
/// Every item those rows carry is one an implemented sibling in the same stream already carries -
/// which is the test that let stream 7's rows in and lets these in.
/// </para>
/// <para>
/// One row of the nine stayed out, and <c>SecsCatalogGaplessTests</c> measures why: S1F9/F10, whose
/// two items are free text with no tables, named by stream 1 alone, reporting material transfer -
/// a subject stream 3 and E87 serve properly here.
/// </para>
/// </remarks>
public class GemGaplessSweepTests
{
    private string? _aborted;

    private void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusVariable(2, "Pressure", () => SecsItem.F8(1013.25), "mbar")
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-7"))
            .AddEvent(1_000, "ProcessEnd")

            // Two forms over the same two variables, which is what SFCD selects. Form 9 is
            // deliberately a code nothing else uses, to show the byte is carried and not
            // interpreted.
            .AddStatusForm(1, 1, 2)
            .AddStatusForm(9, 2);

    // ------------------------------------------------- stream 5, an exception's other three stages

    [Fact]
    public async Task An_exception_runs_through_four_stages_on_one_event()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        List<(GemExceptionStage Stage, string Id, string Type, string Message)> seen = [];
        pair.Host.ExceptionReceived += (_, e) =>
            seen.Add((e.Stage, e.Exception.Id, e.Exception.Type, e.Exception.Message));

        // Bounded on the real clock, which a control earned: answer one of these on the function it
        // arrived on rather than one above, and the connection never matches the reply - so the
        // machine waits on a T3 the FakeTimeProvider never advances and the run dies on its own
        // timeout with no summary. The bound turns that back into a failure.
        Assert.True(await Sent(pair.Equipment.PostExceptionAsync("E-1", "ALARM", "pressure spiked", GemPair.Token)));
        Assert.True(await Sent(pair.Equipment.NotifyExceptionRecoveringAsync("E-1", GemPair.Token)));
        Assert.True(await Sent(pair.Equipment.NotifyExceptionRecoveredAsync("E-1", "pumped down again", GemPair.Token)));
        Assert.True(await Sent(pair.Equipment.ClearExceptionAsync("E-1", GemPair.Token)));

        static Task<bool> Sent(ValueTask<bool> sending)
            => sending.AsTask().WaitAsync(TimeSpan.FromSeconds(5), GemPair.Token);

        await GemPair.WaitUntilAsync(() => seen.Count == 4, "all four stages to reach the host");

        // One event for the four, because an application watching an exception wants its life
        // rather than four unrelated notifications - and Stage is what tells them apart.
        Assert.Equal((GemExceptionStage.Posted, "E-1", "ALARM", "pressure spiked"), seen[0]);
        Assert.Equal((GemExceptionStage.Recovering, "E-1", string.Empty, string.Empty), seen[1]);
        Assert.Equal((GemExceptionStage.Recovered, "E-1", string.Empty, "pumped down again"), seen[2]);
        Assert.Equal((GemExceptionStage.Cleared, "E-1", string.Empty, string.Empty), seen[3]);
    }

    [Fact]
    public async Task Each_stage_goes_out_on_its_own_function_and_is_answered_one_above()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        await CheckAsync(() => link.Equipment.ClearExceptionAsync("E-1", GemLink.Token), 11, 2);
        await CheckAsync(() => link.Equipment.NotifyExceptionRecoveringAsync("E-1", GemLink.Token), 13, 2);
        await CheckAsync(() => link.Equipment.NotifyExceptionRecoveredAsync("E-1", "done", GemLink.Token), 15, 3);

        async Task CheckAsync(Func<ValueTask<bool>> send, byte function, int items)
        {
            var sent = send().AsTask();
            using var notice = await link.ReceiveAsync();

            Assert.Equal(5, notice.S);
            Assert.Equal(function, notice.F);
            Assert.True(await sent.WaitAsync(TimeSpan.FromSeconds(5), GemLink.Token));

            // Three items for the recovery and two for the other two, which is what the reference
            // gives them - rather than always sending three and leaving one empty.
            Assert.Equal(items, notice.Item!.Count);
        }
    }

    [Fact]
    public async Task A_stage_that_arrives_with_the_wrong_number_of_items_is_refused()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // A clear carrying four items. The guard is per stage rather than "two or more", because a
        // clear that arrived shaped like a post is a body this end did not understand - and nothing
        // in stream 5 will repeat it, which is why the strict reading is the right one.
        using var primary = new SecsMessage(5, 11, replyExpected: true)
        {
            Name = "ExceptionClearNotify",
            Item = SecsItem.L(
                SecsItem.A("20260101000000").Named("TIMESTAMP"),
                SecsItem.A("E-1").Named("EXID"),
                SecsItem.A("ALARM").Named("EXTYPE"),
                SecsItem.A("extra").Named("EXMESSAGE")),
        };

        using var reply = await pair.AskHostAsync(primary);

        Assert.Equal(12, reply.F);
        Assert.Equal((byte)GemAlarmAcknowledgement.Error, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task The_host_can_ask_a_recovery_to_stop_and_is_refused_by_default()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Refused, and the opposite way round from Material's answer delegates. Those accept
        // because an offer of material costs nothing to take; this one stops something the machine
        // is in the middle of, so a machine nobody told how to abort a recovery cannot abort one -
        // and answering TRUE would tell a host the recovery had stopped when nothing stopped it.
        var refused = await pair.Host.AbortExceptionRecoveryAsync("E-1", GemPair.Token);

        Assert.False(refused.Accepted);
        Assert.Contains("no handler", refused.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_that_knows_how_to_abort_says_so_and_a_refusal_carries_its_reason()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        pair.Equipment.AnswerExceptionAbort = id =>
        {
            _aborted = id;

            return id == "E-1"
                ? GemExceptionAbortResult.Accept()
                : GemExceptionAbortResult.Refuse(42, "that recovery already finished");
        };

        var accepted = await pair.Host.AbortExceptionRecoveryAsync("E-1", GemPair.Token);

        Assert.True(accepted.Accepted);
        Assert.Equal(0u, accepted.ErrorCode);
        Assert.Equal("E-1", _aborted);

        // ACKA is a BOOLEAN with a table, so the answer is quoted - and S5F18 carries ERRCODE and
        // ERRTEXT beside it, which is the only place a refusal can say why.
        var refused = await pair.Host.AbortExceptionRecoveryAsync("E-2", GemPair.Token);

        Assert.False(refused.Accepted);
        Assert.Equal(42u, refused.ErrorCode);
        Assert.Equal("that recovery already finished", refused.ErrorText);
        Assert.Equal("refused: 42 that recovery already finished", refused.ToString());
    }

    [Fact]
    public async Task An_abort_naming_no_exception_is_refused_without_asking_the_handler()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        pair.Equipment.AnswerExceptionAbort = _ => GemExceptionAbortResult.Accept();

        var answer = await pair.Host.AbortExceptionRecoveryAsync(string.Empty, GemPair.Token);

        // A handler asked to abort the recovery of "" would have to guess which recovery was
        // meant, and S5F18 has an ERRTEXT to say so in instead.
        Assert.False(answer.Accepted);
        Assert.Contains("named no exception", answer.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handler_that_throws_leaves_the_recovery_where_it_was()
    {
        var log = new RecordingLogger<GemEquipment>();
        await using var link = GemLink.Start(Model, logger: log);
        await link.OpenAsync();

        link.Equipment.AnswerExceptionAbort = _ => throw new InvalidOperationException("boom");

        using var reply = await link.RequestAsync(new SecsMessage(5, 17, replyExpected: true)
        {
            Item = SecsItem.A("E-1").Named("EXID"),
        });

        Assert.Equal(18, reply.F);
        Assert.False(reply.Item![1].FirstValueOrDefault<bool>());

        Assert.Contains(
            log.From(LogLevel.Error),
            message => message.Contains("threw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_abort_wrapped_in_a_list_of_one_is_read_too()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        link.Equipment.AnswerExceptionAbort = _ => GemExceptionAbortResult.Accept();

        // The body is a bare EXID, which is the only row in this family shaped that way - so a
        // machine that wrapped it is read, for the reason stream 4's link-only rows are: the two
        // encodings are indistinguishable in intent.
        using var reply = await link.RequestAsync(new SecsMessage(5, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("E-1").Named("EXID")),
        });

        Assert.True(reply.Item![1].FirstValueOrDefault<bool>());

        // And ACKA crosses as the BOOLEAN the table defines, which only the wire can show: the
        // host's reader tolerates a B[1] from a machine that encoded it narrowly, so a round-trip
        // reads either spelling the same way and a control that changed the format found nothing.
        Assert.Equal(SecsFormat.Boolean, reply.Item[1].Format);
    }

    [Fact]
    public async Task A_recovery_stage_the_link_cannot_carry_is_spooled()
    {
        var spool = new GemInMemorySpool();
        await using var link = GemLink.Start(Model, spool: spool);

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);
        await GemLink.WaitUntilAsync(
            () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
            "the equipment to be spooling with nobody to talk to");

        // Spooled like the post, for the reason the post is: a host that was away wants to know a
        // condition cleared as much as it wants to know it arrived, and nothing in stream 5 will
        // tell it a second time.
        Assert.False(await link.Equipment.ClearExceptionAsync("E-1", GemLink.Token));
        Assert.Equal(1, link.Equipment.SpoolCountActual);

        var held = await spool.TryPeekAsync(GemLink.Token);

        Assert.Equal(5, held!.Stream);
        Assert.Equal(11, held.Function);
    }

    // ----------------------------------------------- stream 10, the terminal nobody can reach

    [Fact]
    public async Task A_terminal_that_cannot_be_reached_is_volunteered_rather_than_answered()
    {
        await using var pair = GemPair.Start(Model, terminal: new GemInMemoryTerminal());
        await pair.OpenAsync();

        byte? seen = null;
        pair.Host.TerminalNotAccessible += (_, e) => seen = e.TerminalId;

        Assert.True(await pair.Equipment.ReportTerminalNotAccessibleAsync(2, GemPair.Token));
        await GemPair.WaitUntilAsync(() => seen is not null, "the notice to reach the host");

        // Not ACKC10 2 by another route, which is the reason this row used to be left out. That
        // code answers a display request; this is a primary with no reply, volunteered before
        // anybody tries - so a host stops sending to terminal 2 without making the failing request.
        Assert.Equal((byte)2, seen);
    }

    [Fact]
    public async Task The_notice_needs_no_terminal_capability_and_is_not_spooled()
    {
        var spool = new GemInMemorySpool();
        await using var link = GemLink.Start(Model, spool: spool);

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);
        await GemLink.WaitUntilAsync(
            () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
            "the equipment to be spooling");

        // False for "nobody to tell", and nothing held: stream 10 is about a person standing at a
        // machine, and which terminals were unreachable an hour ago is not news anybody can act on.
        Assert.False(await link.Equipment.ReportTerminalNotAccessibleAsync(1, GemLink.Token));
        Assert.Equal(0, link.Equipment.SpoolCountActual);
    }

    [Fact]
    public async Task The_machine_sends_it_and_does_not_answer_it()
    {
        // Either way on the wire, and only one direction is served: a host telling a machine that
        // one of the host's terminals has gone would be a message nothing here answers, which is
        // the one thing the Gem README says a host method must not be.
        Assert.Contains(((byte)10, (byte)7), GemHost.HandledMessages);
        Assert.DoesNotContain(((byte)10, (byte)7), GemEquipment.TerminalMessages);
        Assert.DoesNotContain(((byte)10, (byte)7), GemEquipment.HandledMessages);
    }

    // --------------------------------------------------------- stream 1, the two status forms

    [Fact]
    public async Task A_form_answers_with_values_and_its_twin_with_names_beside_them()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var values = await pair.Host.ReadFormattedStatusAsync(1, GemPair.Token);

        // S1F6 carries SV alone: two values, in form order.
        Assert.Equal(6, values.F);
        Assert.Equal(2, values.Item!.Count);
        Assert.Equal((byte)5, values.Item[0].FirstValueOrDefault<byte>());
        Assert.Equal(1013.25, values.Item[1].FirstValueOrDefault<double>());

        using var named = await pair.Host.ReadFixedFormAsync(1, GemPair.Token);

        // S1F8 carries SVNAME beside every SV, nested per entry the way S1F12 and S2F30 do.
        Assert.Equal(8, named.F);
        Assert.Equal(2, named.Item!.Count);
        Assert.Equal("ControlState", named.Item[0][0].GetString());
        Assert.Equal("Pressure", named.Item[1][0].GetString());
        Assert.Equal(1013.25, named.Item[1][1].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task The_form_code_is_carried_and_not_interpreted()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Form 9 is a code nothing else uses, and it has one variable where form 1 has two. SFCD
        // has no table in the reference, so which form a code selects is the machine's to document
        // and this library carries the byte.
        using var nine = await pair.Host.ReadFormattedStatusAsync(9, GemPair.Token);

        Assert.Equal(1, nine.Item!.Count);
        Assert.Equal(1013.25, nine.Item[0].FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task A_form_the_machine_has_not_got_is_a_zero_length_list()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // The refusal these rows have: neither carries an acknowledgement code, exactly as S7F6
        // and S7F26 do not.
        using var missing = await pair.Host.ReadFormattedStatusAsync(200, GemPair.Token);
        using var missingNamed = await pair.Host.ReadFixedFormAsync(200, GemPair.Token);

        Assert.Equal(0, missing.Item!.Count);
        Assert.Equal(0, missingNamed.Item!.Count);
    }

    [Fact]
    public async Task A_body_that_is_not_a_form_code_is_the_same_refusal()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        using var reply = await link.RequestAsync(new SecsMessage(1, 5, replyExpected: true)
        {
            Item = SecsItem.L(),
        });

        Assert.Equal(6, reply.F);
        Assert.Equal(0, reply.Item!.Count);
    }

    [Fact]
    public void A_form_is_not_a_report_and_a_host_cannot_define_one()
    {
        var builder = new GemEquipmentModelBuilder();

        Model(builder);

        var model = builder.Build();

        // The machine's own, so they live beside the variables rather than in the state a host can
        // edit - and S2F33 defines reports, never forms.
        Assert.Equal<byte[]>([1, 9], [.. model.StatusForms.Keys.Order()]);
        Assert.Equal<uint[]>([1, 2], [.. model.StatusForms[1]]);

        Assert.Equal(
            GemDefineReportAcknowledgement.Accepted,
            model.DefineReports([(5_000u, (IReadOnlyList<uint>)[1u])]));

        Assert.Equal<byte[]>([1, 9], [.. model.StatusForms.Keys.Order()]);

        model.Reset();

        // And a re-establish clears the reports and leaves the forms, which is the difference
        // stated as a behaviour rather than as a comment.
        Assert.Empty(model.DefinedReports);
        Assert.Equal<byte[]>([1, 9], [.. model.StatusForms.Keys.Order()]);
    }

    [Fact]
    public void A_form_naming_a_variable_the_machine_has_not_got_is_caught_at_build()
    {
        var missing = new GemEquipmentModelBuilder()
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusForm(1, 1, 999);

        // The same check the reports get, for the same reason: a typo in a form would otherwise
        // answer S1F6 silently short a value.
        var caught = Assert.Throws<InvalidOperationException>(() => missing.Build());

        Assert.Contains("999", caught.Message, StringComparison.Ordinal);

        // And a data variable is refused too, because S1F5 and S1F7 ask for *status* - a data
        // variable there would be the mistake S1F21 exists to keep apart from S1F11.
        var wrongKind = new GemEquipmentModelBuilder()
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-7"))
            .AddStatusForm(1, 10);

        Assert.Contains(
            "data variable",
            Assert.Throws<InvalidOperationException>(() => wrongKind.Build()).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_form_code_may_not_be_declared_twice_and_zero_is_a_legitimate_code()
    {
        var builder = new GemEquipmentModelBuilder()
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddStatusForm(0, 1);

        // Zero is allowed, unlike the identifiers elsewhere in this builder: SFCD has no table, so
        // nothing in the reference reserves zero - and refusing it would be this library inventing
        // a rule about a byte it does not interpret.
        Assert.Single(builder.Build().StatusForms);

        Assert.Throws<ArgumentException>(() => builder.AddStatusForm(0, 1));
    }
}
