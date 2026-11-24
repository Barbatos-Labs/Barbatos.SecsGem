// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The last three pairs of the coverage sweep: the exception post, the discrete variable data send
/// and the multi-block inquire.
/// </summary>
/// <remarks>
/// <para>
/// S5F9/F10, S6F3/F4 and S6F5/F6 - the remaining three of the seven primaries this repository had
/// not implemented that the reference documents rather than illustrates. All three are equipment
/// primaries, so each is driven from the machine's end and checked at the host's.
/// </para>
/// <para>
/// <strong>Two of the three are not what E5 is usually quoted as saying</strong>, and that is the
/// point of the measurements in <c>SecsCatalogStream6Tests</c>: S6F3 carries no values here,
/// because the items it would carry them in are named by no row in the whole reference, and S6F5
/// is the only multi-block inquire of the reference's three that is a definition.
/// </para>
/// </remarks>
public class GemExceptionAndBlockTests
{
    private static void Model(GemEquipmentModelBuilder builder)
        => builder
            .AddStatusVariable(1, "ControlState", () => SecsItem.U1(5))
            .AddDataVariable(10, "LotId", () => SecsItem.A("LOT-001"))
            .AddEvent(1_000, "ProcessEnd", [10u])
            .AddEvent(2_000, "Loaded")

            // One alarm, so the tests below can show that an exception post and an alarm report
            // arrive separately rather than that only one handler was ever wired up.
            .AddAlarm(new GemAlarm
            {
                Id = 9_000,
                Text = "Chamber pressure out of range",
            });

    // ------------------------------------------------------------------ S5F9, the exception post

    [Fact(Timeout = 15_000)]
    public async Task An_exception_post_reaches_the_host_with_all_four_fields()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        GemExceptionPost? seen = null;
        pair.Host.ExceptionReceived += (_, e) => seen = e.Exception;

        Assert.True(await GemPair.SentAsync(pair.Equipment.PostExceptionAsync(
            "PRESSURE-HIGH",
            "ALARM",
            "Chamber pressure above limit",
            GemPair.Token)));

        await GemPair.WaitUntilAsync(() => seen is not null, "the exception to reach the host");

        Assert.Equal("PRESSURE-HIGH", seen!.Id);
        Assert.Equal("ALARM", seen.Type);
        Assert.Equal("Chamber pressure above limit", seen.Message);
        Assert.Equal("ALARM PRESSURE-HIGH Chamber pressure above limit", seen.ToString());

        // TIMESTAMP is the equipment's own clock, which under test is the FakeTimeProvider both
        // ends share - so this is an exact equality rather than a window.
        Assert.Equal(pair.Clock.GetUtcNow().UtcDateTime, seen.PostedAt);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_exception_is_not_an_alarm_and_does_not_arrive_as_one()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var alarms = 0;
        GemExceptionPost? seen = null;
        pair.Host.AlarmReceived += (_, _) => alarms++;
        pair.Host.ExceptionReceived += (_, e) => seen = e.Exception;

        // An empty EXMESSAGE along the way: a legitimate post, since a machine whose exception
        // type says everything has nothing to add.
        Assert.True(await GemPair.SentAsync(pair.Equipment.PostExceptionAsync("E-1", "WARNING", "", GemPair.Token)));
        await GemPair.WaitUntilAsync(() => seen is not null, "the exception to reach the host");

        // Both are stream 5 and both answer with ACKC5, which is exactly why this is worth
        // asserting: one event for the two would hand a host a stream-5 notification whose two
        // halves it has to tell apart itself.
        Assert.Equal(0, alarms);
        Assert.Equal(string.Empty, seen!.Message);
        Assert.Equal("WARNING E-1 ", seen.ToString());

        // And an alarm still arrives as an alarm, which is what makes the zero above a separation
        // rather than a handler nobody wired up.
        Assert.Equal(
            GemAlarmAcknowledgement.Accepted,
            await pair.Host.EnableAlarmAsync(true, 9_000, GemPair.Token));

        Assert.True(await pair.Equipment.SetAlarmAsync(9_000, GemPair.Token));
        await GemPair.WaitUntilAsync(() => alarms == 1, "the alarm to reach the host");
    }

    [Fact(Timeout = 15_000)]
    public async Task A_post_that_crosses_as_the_wrong_shape_is_refused_rather_than_accepted()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        GemExceptionPost? seen = null;
        pair.Host.ExceptionReceived += (_, e) => seen = e.Exception;

        // Three items where the row names four. Sent by hand, because a real GemEquipment cannot
        // build this - which is the only way to reach the stricter half of the handler.
        using var primary = new SecsMessage(5, 9, replyExpected: true)
        {
            Name = "ExceptionPostNotify",
            Item = SecsItem.L(
                SecsItem.A("20260101000000").Named("TIMESTAMP"),
                SecsItem.A("E-2").Named("EXID"),
                SecsItem.A("ALARM").Named("EXTYPE")),
        };

        using var reply = await pair.AskHostAsync(primary);

        // ACKC5 1. An alarm handler can afford to accept a malformed body because S5F6 will serve
        // the condition again; nothing in stream 5 will ever repeat an exception post.
        Assert.Equal(10, reply.F);
        Assert.Equal((byte)GemAlarmAcknowledgement.Error, reply.Item!.FirstValueOrDefault<byte>());

        // The handler still raises, with everything it could not read left empty: a host that
        // wants to log that something unreadable arrived can, and a host that only acts on
        // well-formed posts has the acknowledgement to go by.
        Assert.NotNull(seen);
        Assert.Equal(string.Empty, seen!.Id);
        Assert.Null(seen.PostedAt);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_timestamp_that_will_not_parse_leaves_the_instant_unknown_and_the_post_standing()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        GemExceptionPost? seen = null;
        pair.Host.ExceptionReceived += (_, e) => seen = e.Exception;

        using var primary = new SecsMessage(5, 9, replyExpected: true)
        {
            Name = "ExceptionPostNotify",
            Item = SecsItem.L(
                SecsItem.A("not a time").Named("TIMESTAMP"),
                SecsItem.A("E-3").Named("EXID"),
                SecsItem.A("ALARM").Named("EXTYPE"),
                SecsItem.A("still worth hearing").Named("EXMESSAGE")),
        };

        using var reply = await pair.AskHostAsync(primary);

        // Accepted: the four items are there, so the body read. What the machine could not say is
        // when, and that is a null rather than a refusal - the exception is the news.
        Assert.Equal((byte)GemAlarmAcknowledgement.Accepted, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Null(seen!.PostedAt);
        Assert.Equal("still worth hearing", seen.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_exception_the_link_cannot_carry_is_spooled_like_a_report()
    {
        var spool = new GemInMemorySpool();
        await using var link = await NotCommunicatingAsync(spool);

        // False means nothing reached the host, which is true whether or not it was kept - the
        // same contract TriggerEventAsync has. What separates kept from dropped is the count.
        Assert.False(await link.Equipment.PostExceptionAsync("E-4", "ALARM", "held", GemLink.Token));

        Assert.Equal(1, link.Equipment.SpoolCountActual);

        var held = await spool.TryPeekAsync(GemLink.Token);

        Assert.NotNull(held);
        Assert.Equal(5, held!.Stream);
        Assert.Equal(9, held.Function);
    }

    // ----------------------------------------------------- S6F3, the discrete variable data send

    [Fact(Timeout = 15_000)]
    public async Task The_discrete_variable_data_send_names_an_event_and_carries_no_values()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        var sent = link.Equipment.SendDiscreteVariableDataAsync(1_000, GemLink.Token);
        using var message = await link.ReceiveAsync();

        Assert.True(await sent);
        Assert.Equal(6, message.S);
        Assert.Equal(3, message.F);

        // Two items, DATAID and CEID, which is what the reference's row names. Not three - an
        // S6F11 for the same event carries a report list as its third, and this is the message
        // that does not.
        Assert.True(message.Item!.IsList);
        Assert.Equal(2, message.Item.Count);
        Assert.Equal(1_000u, message.Item[1].FirstValueOrDefault<uint>());
    }

    [Fact(Timeout = 15_000)]
    public async Task It_arrives_on_the_event_report_event_and_says_which_message_brought_it()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        List<(byte Function, ulong Event, int Reports)> seen = [];
        pair.Host.EventReportReceived += (_, e) => seen.Add((e.Function, e.EventId, e.Reports.Count));

        // An S6F11 for an event with nothing linked to it, then an S6F3 for the same event. Both
        // arrive with no reports, so the report list cannot tell them apart - which is the whole
        // reason GemEventReportEventArgs.Function exists.
        Assert.True(await pair.Equipment.TriggerEventAsync(1_000, GemPair.Token));
        await GemPair.WaitUntilAsync(() => seen.Count == 1, "the event report to reach the host");

        Assert.True(await pair.Equipment.SendDiscreteVariableDataAsync(1_000, GemPair.Token));
        await GemPair.WaitUntilAsync(() => seen.Count == 2, "the discrete send to reach the host");

        Assert.Equal((11, 1_000UL, 0), seen[0]);
        Assert.Equal((3, 1_000UL, 0), seen[1]);
    }

    [Fact(Timeout = 15_000)]
    public async Task It_obeys_the_same_two_gates_the_event_report_obeys()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        // An event this machine does not declare.
        Assert.False(await pair.Equipment.SendDiscreteVariableDataAsync(4_242, GemPair.Token));

        // And one the host has switched off. S2F37 disables a CEID, and this message names one -
        // so the shorter primary must not be a way around the host's own instruction.
        Assert.Equal(
            GemEnableEventAcknowledgement.Accepted,
            await pair.Host.EnableEventsAsync(false, [1_000UL], GemPair.Token));

        Assert.False(await pair.Equipment.SendDiscreteVariableDataAsync(1_000, GemPair.Token));

        // The other event is untouched, which is what makes the refusal above the host's doing
        // rather than a broken send.
        Assert.True(await pair.Equipment.SendDiscreteVariableDataAsync(2_000, GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_discrete_send_of_the_wrong_shape_is_refused_with_ACKC6_one()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        using var primary = new SecsMessage(6, 3, replyExpected: true)
        {
            Name = "DiscreteVariableDataSend",
            Item = SecsItem.U4(1_000).Named("CEID"),
        };

        using var reply = await pair.AskHostAsync(primary);

        Assert.Equal(4, reply.F);
        Assert.Equal((byte)GemReportAcknowledgement.Error, reply.Item!.FirstValueOrDefault<byte>());
    }

    // ---------------------------------------------------------- S6F5, the multi-block inquire

    [Fact(Timeout = 15_000)]
    public async Task The_multi_block_inquire_is_granted_by_default_and_carries_the_length_it_was_given()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        GemMultiBlockInquiryEventArgs? asked = null;
        pair.Host.MultiBlockInquired += (_, e) => asked = e;

        Assert.Equal(
            GemMultiBlockGrant.Ok,
            await pair.Equipment.InquireMultiBlockAsync(70_000, GemPair.Token));

        // Granted with no handler at all, which is the right default for a handshake whose only
        // purpose is to let a host say no: silence that refused would stop a machine reporting.
        Assert.NotNull(asked);
        Assert.Equal(70_000u, asked!.Length);
        Assert.NotEqual(0u, asked.DataId);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_host_that_says_no_is_obeyed_and_each_refusal_comes_back_as_itself()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var answer = GemMultiBlockGrant.Busy;
        pair.Host.MultiBlockInquired += (_, e) => e.Grant = answer;

        Assert.Equal(
            GemMultiBlockGrant.Busy,
            await pair.Equipment.InquireMultiBlockAsync(1, GemPair.Token));

        answer = GemMultiBlockGrant.NoSpace;

        Assert.Equal(
            GemMultiBlockGrant.NoSpace,
            await pair.Equipment.InquireMultiBlockAsync(1, GemPair.Token));

        // Busy and NoSpace are different findings - one invites another attempt and the other does
        // not - so a reader that folded both onto one refusal would be throwing away the only
        // thing the machine can act on.
        answer = GemMultiBlockGrant.Ok;

        Assert.Equal(
            GemMultiBlockGrant.Ok,
            await pair.Equipment.InquireMultiBlockAsync(1, GemPair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_reserved_GRANT6_folds_onto_Other_rather_than_onto_a_refusal()
    {
        var log = new RecordingLogger<GemEquipment>();
        await using var link = GemLink.Start(Model, logger: log);
        await link.OpenAsync();

        var asked = link.Equipment.InquireMultiBlockAsync(99, GemLink.Token);

        // 40, which the table reserves. Written by hand because a real GemHost answers from the
        // enum, so the cast would give the right answer for the wrong reason - the lesson the
        // twelve-reader sweep paid for.
        using var inquire = await link.ReceiveAsync(40);

        Assert.Equal(5, inquire.F);
        Assert.Equal(GemMultiBlockGrant.Other, await asked);

        // And the arriving code survives in the log, because folding throws it away everywhere
        // else: without this assertion, deleting the log line changes nothing a test can see.
        Assert.Contains(
            log.From(LogLevel.Information),
            message => message.Contains("GRANT6 40", StringComparison.Ordinal)
                && message.Contains("reserves", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_inquire_with_nobody_to_ask_is_refused_and_never_spooled()
    {
        await using var link = await NotCommunicatingAsync(new GemInMemorySpool());

        // The one outbound message here that is a question rather than news. Permission nobody is
        // there to give is not permission, and a grant delivered when the host comes back would
        // answer a send that has long been abandoned.
        //
        // The real-clock timeout is deliberate. Delete the guard this asserts and the inquire goes
        // out on a link nobody answers, where it waits on a T3 the FakeTimeProvider never
        // advances - so the control for this rule hung the suite instead of failing it, which in a
        // CI run is the worse of the two. This turns the hang back into a failure.
        var asked = link.Equipment.InquireMultiBlockAsync(99, GemLink.Token).AsTask();

        Assert.Equal(
            GemMultiBlockGrant.NoSpace,
            await asked.WaitAsync(TimeSpan.FromSeconds(5), GemLink.Token));

        Assert.Equal(0, link.Equipment.SpoolCountActual);

        // A report in the same state is kept, which is the contrast worth asserting: the spool is
        // working, and this message is the one that stays out of it.
        Assert.False(await link.Equipment.PostExceptionAsync("E-5", "ALARM", "kept", GemLink.Token));
        Assert.Equal(1, link.Equipment.SpoolCountActual);
    }

    /// <summary>A spooling equipment with no host, built the way the spooling tests build one.</summary>
    /// <param name="spool">The spool.</param>
    /// <returns>The link.</returns>
    private static async Task<GemLink> NotCommunicatingAsync(IGemSpool spool)
    {
        var link = GemLink.Start(Model, spool: spool);

        await link.SelectAsync();
        using var denied = await link.EstablishAsync(GemCommunicationAcknowledgement.Denied);

        await GemLink.WaitUntilAsync(
            () => !link.Equipment.IsCommunicating && link.Equipment.IsSpooling,
            "the equipment to be spooling with nobody to talk to");

        return link;
    }

    [Fact(Timeout = 15_000)]
    public async Task An_inquire_the_host_cannot_read_is_refused_finally_rather_than_with_busy()
    {
        await using var pair = GemPair.Start(Model);
        await pair.OpenAsync();

        var asked = 0;
        pair.Host.MultiBlockInquired += (_, _) => asked++;

        // One item where the row names two. Written by hand, since a real GemEquipment cannot
        // build it.
        using var primary = new SecsMessage(6, 5, replyExpected: true)
        {
            Name = "MultiBlockDataSendInquire",
            Item = SecsItem.U4(1).Named("DATAID"),
        };

        using var reply = await pair.AskHostAsync(primary);

        // NoSpace, not Busy: Busy invites the machine to ask again, and the second attempt would
        // be exactly as unreadable as the first. The host still raises, so an application can see
        // that something it could not parse arrived.
        Assert.Equal(6, reply.F);
        Assert.Equal((byte)GemMultiBlockGrant.NoSpace, reply.Item!.FirstValueOrDefault<byte>());
        Assert.Equal(1, asked);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_exception_post_can_be_sent_without_asking_for_an_answer()
    {
        await using var link = GemLink.Start(Model, options => options.ExceptionPostsExpectReply = false);
        await link.OpenAsync();

        var sent = link.Equipment
            .PostExceptionAsync("E-6", "ALARM", "fire and forget", GemLink.Token)
            .AsTask();

        using var frame = await link.ReadAsync();

        // W clear, so the machine is not holding a transaction open waiting for an S5F10. Its own
        // setting rather than a share of the alarm one: an alarm can be asked about again with
        // S5F6, and an exception post cannot, so a machine may well insist on one and not the
        // other.
        Assert.False(frame.Header.ReplyExpected);
        Assert.Equal(5, frame.Data!.S);
        Assert.Equal(9, frame.Data.F);

        // The real-clock timeout is here for the reason the multi-block inquire has one: force W
        // on and the machine waits for an S5F10 that this test deliberately never sends, on a T3
        // the FakeTimeProvider never advances. Without it the control for this rule hung the suite
        // rather than failing it.
        Assert.True(await sent.WaitAsync(TimeSpan.FromSeconds(5), GemLink.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_grant_of_the_wrong_shape_is_a_refusal_and_not_a_crash()
    {
        await using var link = GemLink.Start(Model);
        await link.OpenAsync();

        var asked = link.Equipment.InquireMultiBlockAsync(99, GemLink.Token);

        using var frame = await link.ReadAsync();
        using var primary = frame.Data!;

        using var reply = new SecsMessage(6, 6, replyExpected: false)
        {
            Name = "MultiBlockGrant",
            Item = SecsItem.L(),
        };

        await link.SendDataAsync(reply, frame.Header.SystemBytes);

        // No GRANT6 at all. The send must not go ahead, and the refusal has to be the final one:
        // Busy would have the machine ask again and get the same empty answer.
        Assert.Equal(GemMultiBlockGrant.NoSpace, await asked);
    }
}
