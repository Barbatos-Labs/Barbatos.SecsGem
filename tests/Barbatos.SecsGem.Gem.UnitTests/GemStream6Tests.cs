// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The five pairs of stream 6 that went in after the gapless sweep, driven end to end.
/// </summary>
/// <remarks>
/// <para>
/// Stream 6 has four names the reference never defines and all four belong to it alone, so every
/// one of its remaining rows had to be read twice - once for whether the field is defined, and once
/// for what the field is <em>to the message</em>. <c>V</c> is a payload and is carried untouched,
/// which is not an invention: S6F11 carries it too and is a row the reference illustrates in full.
/// <c>CELLNAME</c> and <c>NRED</c> are what their rows are <em>for</em>, so those two stay out.
/// <c>SecsCatalogStream6Tests</c> holds the measurements.
/// </para>
/// <para>
/// <strong>The fifth went in on a different measure, a chapter later.</strong> S6F25 has no
/// undefined item at all and was refused over its <em>shape</em>: the reference records it exactly
/// as it records S6F16, and S6F16 is served here carrying a whole event report. What settled it was
/// asking why S6F16's body exceeds its record - S6F15 is the Event Report <em>Request</em>, and
/// nothing asks an S6F25 for anything.
/// </para>
/// <para>
/// <strong>Two of these rows answer a question nothing else here answers.</strong> S6F17 is the
/// only way to read a report whose values arrive identified, and S6F27 is the only way to read
/// back what S2F35 linked - a host that restarts has no record of it, and sending S2F35 again is
/// what changes the table rather than reporting it.
/// </para>
/// </remarks>
public class GemStream6Tests
{
    private static void Model(GemEquipmentModelBuilder builder) => GemLink.DefaultModel(builder);

    // ----------------------------------------------------------------- S6F17 and S6F18

    [Fact(Timeout = 15_000)]
    public async Task The_annotated_report_answers_with_every_value_identified_and_its_units()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var reply = await GemPair.SentAsync(pair.Host.RequestAnnotatedReportAsync(5_000, GemPair.Token));
        var values = GemHost.ReadAnnotatedReport(reply);

        // Report 5000 is status variables 1 and 2, in that order, and the units are the model's
        // own - one empty and one not, which is the pair worth asserting: an annotation that
        // dropped empty units would still look right on the second entry.
        Assert.Equal(2, values.Count);

        Assert.Equal(1UL, values[0].VariableId);
        Assert.Equal(string.Empty, values[0].Units);
        Assert.Equal(SecsItem.U1(5), values[0].Value);

        Assert.Equal(2UL, values[1].VariableId);
        Assert.Equal("mbar", values[1].Units);
        Assert.Equal(SecsItem.F8(1013.25d), values[1].Value);

        // And the reply is S6F18 rather than S6F20, which has to be read off the frame: the
        // connection matches a reply by its system bytes and never by its function, so a handler
        // answering on the wrong one would come back here looking correct.
        Assert.Equal(6, reply.S);
        Assert.Equal(18, reply.F);
    }

    [Fact(Timeout = 15_000)]
    public async Task The_annotated_report_is_what_the_plain_one_cannot_say()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var annotated = await GemPair.SentAsync(pair.Host.RequestAnnotatedReportAsync(5_000, GemPair.Token));
        using var plain = await GemPair.SentAsync(pair.Host.RequestReportAsync(5_000, GemPair.Token));

        // The whole justification for serving a second row that reads the same report: S6F20 is
        // the values and nothing else, so a host has to have read a namelist to know what the
        // second one is. Both carry two entries and only one of them says what they are.
        Assert.Equal(2, GemHost.ReadAnnotatedReport(annotated).Count);
        Assert.Equal(2, plain.Item!.Count);

        foreach (var value in plain.Item.Items)
        {
            Assert.False(value.IsList);
        }

        foreach (var value in annotated.Item!.Items)
        {
            Assert.True(value.IsList);
        }
    }

    [Fact(Timeout = 15_000)]
    public void An_annotated_entry_that_is_not_three_items_is_skipped_rather_than_guessed_at()
    {
        // ReadAnnotatedReport is static and public, so this needs no machine at all - which is
        // the argument for it being static: a host with a stored S6F18 can read it later.
        using var reply = new SecsMessage(6, 18, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(
                    SecsItem.U4(1u).Named("VID"),
                    SecsItem.U1(5).Named("V"),
                    SecsItem.A(string.Empty).Named("UNITS")),

                // Two items where the row defines three. Guessing which one is missing would mean
                // deciding whether an absent UNITS or an absent V is the likelier mistake, and
                // this library does not guess at a body.
                SecsItem.L(
                    SecsItem.U4(2u).Named("VID"),
                    SecsItem.F8(1013.25d).Named("V")),

                // And a flat item where the row defines a list.
                SecsItem.U4(3u).Named("VID")),
        };

        var values = GemHost.ReadAnnotatedReport(reply);

        Assert.Equal(1UL, Assert.Single(values).VariableId);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_annotated_report_the_machine_does_not_define_comes_back_empty()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var reply = await GemPair.SentAsync(pair.Host.RequestAnnotatedReportAsync(9_999, GemPair.Token));

        Assert.Empty(GemHost.ReadAnnotatedReport(reply));
        Assert.Equal(0, reply.Item!.Count);
    }

    [Fact(Timeout = 15_000)]
    public void A_report_naming_a_variable_the_model_does_not_hold_is_refused_at_build_time()
    {
        // Written as a test of the annotated reply and it would not run: the builder refuses the
        // model outright. Which is the answer to a question the reply raised - what happens to a
        // value that cannot be read, since dropping it would shift every value after it against
        // the order the report declares and a host reading by position would misread all of them.
        //
        // Nothing can happen to it, because no such report can exist. So the fallback in
        // AnnotatedReportValues is unreachable through this API, exactly as the one in
        // ReportValues has always been, and both are kept as defence rather than as behaviour.
        // Recorded here so a later chapter does not read either as a live path.
        var builder = new GemEquipmentModelBuilder();

        Model(builder);
        builder.AddReport(5_001, 1, 77, 2);

        var refused = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("77", refused.Message, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- S6F7 and S6F8

    [Fact(Timeout = 15_000)]
    public async Task The_data_transfer_request_answers_with_what_the_machine_filed_under_the_identifier()
    {
        await using var pair = GemPair.Start(Model);

        pair.Equipment.AnswerDataTransfer = id => id == 41 ? SecsItem.B(1, 2, 3) : null;

        await pair.OpenAsync();

        using var reply = await GemPair.SentAsync(pair.Host.RequestDataTransferAsync(41, GemPair.Token));

        Assert.Equal(6, reply.S);
        Assert.Equal(8, reply.F);
        Assert.True(reply.Item!.IsList);
        Assert.Equal(2, reply.Item.Count);

        // The identifier comes back beside the data, which is what makes the row usable when more
        // than one request is outstanding - DATAID is defined and carried untouched, and what it
        // selects is between the two ends.
        Assert.Equal(SecsItem.U4(41u), reply.Item[0]);
        Assert.Equal(SecsItem.B(1, 2, 3), reply.Item[1]);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_the_machine_has_nothing_under_answers_an_empty_body()
    {
        await using var pair = GemPair.Start(Model);

        pair.Equipment.AnswerDataTransfer = id => id == 41 ? SecsItem.B(1, 2, 3) : null;

        await pair.OpenAsync();

        using var reply = await GemPair.SentAsync(pair.Host.RequestDataTransferAsync(42, GemPair.Token));

        // S6F8 has no acknowledgement item, so an empty body is the only refusal the row can
        // carry. Not a design choice here - the same shape S6F16 and S6F20 already use.
        Assert.Equal(0, reply.Item!.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_machine_with_no_data_transfer_handler_answers_nothing_rather_than_throwing()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // The delegate's own default, left alone. A machine that was never told how to answer this
        // row still has to answer it, because the row is declared - and "nothing filed under that"
        // is the honest answer rather than a dropped transaction.
        using var reply = await GemPair.SentAsync(pair.Host.RequestDataTransferAsync(41, GemPair.Token));

        Assert.Equal(6, reply.S);
        Assert.Equal(8, reply.F);
        Assert.Equal(0, reply.Item!.Count);
    }

    // ----------------------------------------------------------------- S6F27 and S6F28

    [Fact(Timeout = 15_000)]
    public async Task The_notification_report_request_reads_back_what_S2F35_linked()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        Assert.Equal(
            GemLinkReportAcknowledgement.Accepted,
            await pair.Host.LinkEventsAsync([(1_000UL, [5_000UL])], GemPair.Token));

        // The claim the row is served for: what a host wrote with S2F35 it can now read back,
        // which is the only way a host that did not do the writing can find out.
        Assert.Equal<uint[]>(
            [5_000],
            [.. await GemPair.SentAsync(pair.Host.RequestNotificationReportsAsync(1_000, GemPair.Token))]);

        // And an event nobody linked anything to reads back empty, not stale.
        Assert.Empty(await GemPair.SentAsync(pair.Host.RequestNotificationReportsAsync(1_001, GemPair.Token)));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_event_with_nothing_linked_is_a_different_body_from_an_event_that_is_not_there()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // Both come back as an empty list through the public API, so this distinction exists only
        // on the wire - which is why it is read as a frame. The chapter that served S14F11 learned
        // the same lesson about a reply with no acknowledgement in it.
        Assert.Empty(await GemPair.SentAsync(pair.Host.RequestNotificationReportsAsync(1_001, GemPair.Token)));
        Assert.Empty(await GemPair.SentAsync(pair.Host.RequestNotificationReportsAsync(7_777, GemPair.Token)));

        using var known = new SecsMessage(6, 27, replyExpected: true)
        {
            Name = "NotificationReportRequest",
            Item = SecsItem.U4(1_001u).Named("CEID"),
        };

        using var unknown = new SecsMessage(6, 27, replyExpected: true)
        {
            Name = "NotificationReportRequest",
            Item = SecsItem.U4(7_777u).Named("CEID"),
        };

        using var toKnown = await pair.AskEquipmentAsync(known);
        using var toUnknown = await pair.AskEquipmentAsync(unknown);

        // An event this machine has: the CEID comes back with an empty report list beside it.
        Assert.Equal(2, toKnown.Item!.Count);
        Assert.Equal(SecsItem.U4(1_001u), toKnown.Item[0]);
        Assert.Equal(0, toKnown.Item[1].Count);

        // An event it does not have: nothing at all.
        Assert.Equal(0, toUnknown.Item!.Count);
    }

    // ----------------------------------------------------------------- S6F9 and S6F10

    [Fact(Timeout = 15_000)]
    public async Task Unformatted_data_reaches_the_host_as_the_item_the_machine_sent()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        GemUnformattedDataEventArgs? seen = null;
        var matched = false;

        pair.Host.UnformattedDataReceived += (_, args) =>
        {
            seen = args;

            // Compared inside the handler, which is the whole of the view's life. The item itself
            // is not ours to keep, so what leaves the handler is the verdict and not the data.
            using var expected = SecsItem.B(9, 8, 7);

            matched = args.Data.Equals(expected);
        };

        Assert.True(await GemPair.SentAsync(
            pair.Equipment.SendUnformattedDataAsync(SecsItem.B(9, 8, 7), GemPair.Token)));

        Assert.NotNull(seen);
        Assert.NotEqual(0u, seen!.DataId);
        Assert.True(matched, "the host did not receive the item the machine sent");

        // The acceptance is the args own initialiser and this handler set nothing, so the default
        // is reachable - which a control once found it was not, in this very file family.
        Assert.Equal(GemReportAcknowledgement.Accepted, seen.Acknowledgement);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_handler_that_sets_nothing_accepts_and_the_acceptance_is_on_the_wire()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var raised = 0;

        pair.Host.UnformattedDataReceived += (_, _) => raised++;

        using var primary = new SecsMessage(6, 9, replyExpected: true)
        {
            Name = "UnformattedDataSend",
            Item = SecsItem.L(
                SecsItem.U4(41u).Named("DATAID"),
                SecsItem.B(1).Named("V")),
        };

        using var reply = await pair.AskHostAsync(primary);

        Assert.Equal(1, raised);
        Assert.Equal(6, reply.S);
        Assert.Equal(10, reply.F);
        Assert.Equal(SecsItem.B(0), reply.Item);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_host_that_refuses_unformatted_data_puts_the_refusal_on_the_wire()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        pair.Host.UnformattedDataReceived += (_, args)
            => args.Acknowledgement = GemReportAcknowledgement.Error;

        using var primary = new SecsMessage(6, 9, replyExpected: true)
        {
            Name = "UnformattedDataSend",
            Item = SecsItem.L(
                SecsItem.U4(41u).Named("DATAID"),
                SecsItem.B(1).Named("V")),
        };

        using var reply = await pair.AskHostAsync(primary);

        Assert.Equal(SecsItem.B(1), reply.Item);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_unformatted_send_the_host_cannot_read_is_refused_and_the_log_says_which()
    {
        var log = new RecordingLogger<GemHost>();

        await using var pair = GemPair.Start(Model, hostLogger: log);
        await pair.OpenAsync();

        var raised = 0;

        pair.Host.UnformattedDataReceived += (_, _) => raised++;

        using var primary = new SecsMessage(6, 9, replyExpected: true)
        {
            Name = "UnformattedDataSend",
            Item = SecsItem.A("not a DATAID and a V"),
        };

        using var reply = await pair.AskHostAsync(primary);

        // ACKC6 1 is all the row can say, and it is the same byte a handler uses to refuse data it
        // understood perfectly. So the reply cannot carry this distinction and the log is the only
        // place it lives - assert the log, or deleting that line changes nothing a test can see.
        Assert.Equal(SecsItem.B(1), reply.Item);
        Assert.Equal(0, raised);

        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("S6F9", StringComparison.Ordinal)
                && message.Contains("could not read", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task Unformatted_data_is_spooled_for_a_host_that_is_not_there()
    {
        var spool = new GemInMemorySpool();
        var link = GemLink.Start(Model, spool: spool);

        await using (link)
        {
            // A pair that is merely unopened is not a pair with nobody listening - the transport
            // selects on its own and the equipment establishes. Spooling needs a host that
            // answered S1F13 with a refusal, which is what this sequence produces.
            await link.SelectAsync();
            using (await link.EstablishAsync(GemCommunicationAcknowledgement.Denied))
            {
            }

            await GemLink.WaitUntilAsync(
                () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
                "the equipment to be spooling with nobody to talk to");

            // False means nothing reached the host, which is true whether or not it was kept; the
            // count is what separates kept from dropped. Spooled like a report, because that is
            // what this is to a host that was away: data it would have been given.
            Assert.False(
                await link.Equipment.SendUnformattedDataAsync(SecsItem.B(9), GemLink.Token));

            Assert.Equal(1, link.Equipment.SpoolCountActual);

            var held = await spool.TryPeekAsync(GemLink.Token);

            Assert.NotNull(held);
            Assert.Equal(6, held!.Stream);
            Assert.Equal(9, held.Function);

            // The W bit survives the spool, which matters because S6F10 is a reply the reference
            // defines: a held message that lost it would be delivered as news when the host comes
            // back, and the machine would never learn whether it was taken.
            Assert.True(held.ReplyExpected);
        }
    }

    // ----------------------------------------------------------------- S6F25 and S6F26

    [Fact(Timeout = 15_000)]
    public async Task The_notification_report_carries_the_two_items_its_row_names_and_no_report_list()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        var sent = link.Equipment.SendNotificationReportAsync(1_000, GemLink.Token);
        using var message = await link.ReceiveAsync();

        Assert.True(await sent);
        Assert.Equal(6, message.S);
        Assert.Equal(25, message.F);

        // The whole question this row was refused over, and the only place the answer is visible.
        // Two items, DATAID and CEID - not three, which is what it would be if the record's
        // silence about RPTID meant here what it means at S6F16. It does not: S6F15 asked for a
        // report and nothing asks an S6F25 for anything.
        Assert.True(message.Item!.IsList);
        Assert.Equal(2, message.Item.Count);
        Assert.False(message.Item[0].IsList);
        Assert.False(message.Item[1].IsList);
        Assert.Equal(1_000u, message.Item[1].FirstValueOrDefault<uint>());
    }

    [Fact(Timeout = 15_000)]
    public async Task It_is_an_S6F3_item_for_item_and_the_function_is_the_whole_difference()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        var discrete = link.Equipment.SendDiscreteVariableDataAsync(1_000, GemLink.Token);
        using var three = await link.ReceiveAsync();

        Assert.True(await discrete);

        var notification = link.Equipment.SendNotificationReportAsync(1_000, GemLink.Token);
        using var twentyFive = await link.ReceiveAsync();

        Assert.True(await notification);

        // Identical in shape, in format and in the CEID. Which is why the row is served rather
        // than refused as a duplicate: E5 gives the notification report its own request and its
        // own enable, so the family is real even though the announcement is these same two items.
        // S1F9 went in on that question asked the other way round - ask what the row you are
        // calling a duplicate is made of.
        Assert.Equal(3, three.F);
        Assert.Equal(25, twentyFive.F);
        Assert.Equal(three.Item!.Count, twentyFive.Item!.Count);
        Assert.Equal(three.Item[0].Format, twentyFive.Item[0].Format);
        Assert.Equal(three.Item[1], twentyFive.Item[1]);

        // DATAID is the one item that differs, because it counts - which is the reason the
        // comparison above is item by item rather than one assertion on the whole body.
        Assert.NotEqual(three.Item[0], twentyFive.Item[0]);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_notification_report_reaches_the_host_naming_its_own_function()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        List<(byte Function, ulong Event, int Reports)> seen = [];
        pair.Host.EventReportReceived += (_, e) => seen.Add((e.Function, e.EventId, e.Reports.Count));

        Assert.True(await pair.Equipment.SendNotificationReportAsync(1_000, GemPair.Token));
        await GemPair.WaitUntilAsync(() => seen.Count == 1, "the notification report to reach the host");

        // Four rows arrive on this one event now, and GemEventReportEventArgs.Function is the only
        // thing that separates them: an S6F11 for an event with nothing linked, an S6F3 and an
        // S6F25 all arrive carrying no reports at all.
        Assert.Equal((25, 1_000UL, 0), Assert.Single(seen));
    }

    [Fact(Timeout = 15_000)]
    public async Task It_obeys_the_two_gates_S6F3_obeys_and_the_refusal_comes_back_on_S6F26()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // An event this machine does not declare, and then one the host switched off. S2F37
        // disables a CEID and this message names one, so a second way to announce must not be a
        // way round the host's own instruction.
        Assert.False(await pair.Equipment.SendNotificationReportAsync(4_242, GemPair.Token));

        Assert.Equal(
            GemEnableEventAcknowledgement.Accepted,
            await pair.Host.EnableEventsAsync(false, [1_000UL], GemPair.Token));

        Assert.False(await pair.Equipment.SendNotificationReportAsync(1_000, GemPair.Token));
        Assert.True(await pair.Equipment.SendNotificationReportAsync(1_001, GemPair.Token));

        // And the host end refuses a body it cannot read through the same handler S6F3 uses. One
        // handler for two rows is worth asserting from the wire, because the acknowledgement has
        // to go back on the right function and nothing else in the suite would notice.
        using var wrong = new SecsMessage(6, 25, replyExpected: true)
        {
            Name = "NotificationReportSend",
            Item = SecsItem.U4(1_000).Named("CEID"),
        };

        using var reply = await pair.AskHostAsync(wrong);

        Assert.Equal(6, reply.S);
        Assert.Equal(26, reply.F);
        Assert.Equal((byte)GemReportAcknowledgement.Error, reply.Item!.FirstValueOrDefault<byte>());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_notification_report_is_held_for_a_host_that_is_not_there()
    {
        var spool = new GemInMemorySpool();
        var link = GemLink.Start(Model, spool: spool);

        await using (link)
        {
            await link.SelectAsync();

            using (await link.EstablishAsync(GemCommunicationAcknowledgement.Denied))
            {
            }

            await GemLink.WaitUntilAsync(
                () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
                "the equipment to be spooling with nobody to talk to");

            // Spooled like a report, because that is what it is to a host that was away: news it
            // would have been told. The documented claim is that this row is gated and held
            // exactly as S6F3 is, and a claim in a remark that no test drives is a claim about
            // nothing.
            Assert.False(await link.Equipment.SendNotificationReportAsync(1_000, GemLink.Token));
            Assert.Equal(1, link.Equipment.SpoolCountActual);

            var held = await spool.TryPeekAsync(GemLink.Token);

            Assert.NotNull(held);
            Assert.Equal(6, held!.Stream);
            Assert.Equal(25, held.Function);

            // And the name, which is the only place in this suite one can be read: a message name
            // never travels on the wire, so a reply's is unobservable - a control that renamed the
            // host's S6F26 found nothing and could not. A spool keeps it, because a held message
            // has to come back as itself.
            Assert.Equal("NotificationReportSend", held.Name);
        }
    }
}
