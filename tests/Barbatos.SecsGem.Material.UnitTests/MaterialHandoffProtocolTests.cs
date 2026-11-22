// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// The handoff protocol - stream 4's S4F27 to S4F41, and the two refusals it overturned.
/// </summary>
/// <remarks>
/// <para>
/// These eight rows were refused in this package's README for two stated reasons, and the stream 4
/// sweep found both wrong by asking the item dictionary questions the earlier chapter had asked of
/// the message rows.
/// </para>
/// <para>
/// <strong>"Every one of them is addressed by a <c>TRLINK</c>, which is created by S4F19"</strong> -
/// so the handoff half could not go in before the transfer jobs. But S4F27 carries <c>TRLINK</c>
/// itself, along with the same nine descriptor items S4F19 carries, and it travels either way.
/// Nothing in the reference reserves link naming to a job: two ends can agree a handoff between
/// themselves, and <c>A_link_comes_from_the_announcement_and_not_from_a_transfer_job</c> is the
/// test that says so.
/// </para>
/// <para>
/// <strong>"A transfer job has no state machine anywhere in this reference"</strong> - which is
/// true of the message rows and false of the dictionary: <c>TRJOBMS</c> carries six states with
/// their names. That was the reason for refusing the *job* half, and it is gone; the job half
/// stays out on <c>TRACK</c> instead, which <c>SecsCatalogStream4Tests</c> measures.
/// </para>
/// <para>
/// <strong>One control here found nothing and stays unasserted on purpose.</strong> Giving S4F37's
/// acknowledgement the name <c>HOHALTACK</c> instead of <c>HOCANCELACK</c> changes nothing any
/// test can reach, because SECS-II does not carry item names on the wire at all: a
/// <c>.Named(...)</c> in this repository is for logs, for SML output and for a reader, and it is
/// gone by the time the bytes cross. That is true of every item name in every package here, so it
/// is recorded once rather than chased - the label is documentation, and the way to keep it right
/// is to read it, not to assert it.
/// </para>
/// </remarks>
public class MaterialHandoffProtocolTests
{
    private const string Link = "LINK-1";

    private static List<MaterialHandoffLinkEventArgs> WatchLink(MaterialHandoff end)
    {
        List<MaterialHandoffLinkEventArgs> seen = [];

        end.HandoffLinkReported += (_, args) => seen.Add(args);

        return seen;
    }

    private static MaterialHandoffLink Descriptor() => new()
    {
        Link = Link,
        EquipmentName = "EQ1",
        ObjectName = "CAR-001",
        ObjectType = "Carrier",
        Port = "LP1",
        Role = MaterialHandoffRole.Source,
        PartnerName = "AGV3",
        PartnerPort = "ARM",
        Location = "BAY-4",
    };

    // --------------------------------------------------------- S4F27, where a link comes from

    [Fact]
    public async Task A_link_comes_from_the_announcement_and_not_from_a_transfer_job()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialHandoffLink? seen = null;
        pair.Receiver.HandoffLinkAnnounced += (_, e) => seen = e.Link;

        await pair.Sender.AnnounceLinkAsync(Descriptor(), MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen is not null, "the announcement to reach the other end");

        // All nine items, which is the point: this row carries the same descriptor S4F19 does, so
        // the claim that a TRLINK can only be minted by a transfer job does not survive reading it.
        Assert.Equal(Link, seen!.Link);
        Assert.Equal("EQ1", seen.EquipmentName);
        Assert.Equal("CAR-001", seen.ObjectName);
        Assert.Equal("Carrier", seen.ObjectType);
        Assert.Equal("LP1", seen.Port);
        Assert.Equal(MaterialHandoffRole.Source, seen.Role);
        Assert.Equal("AGV3", seen.PartnerName);
        Assert.Equal("ARM", seen.PartnerPort);
        Assert.Equal("BAY-4", seen.Location);
        Assert.Equal("LINK-1 Source EQ1:LP1 -> AGV3:ARM", seen.ToString());

        // And no transfer job was created, because this library does not serve S4F19 at all.
        Assert.DoesNotContain(((byte)4, (byte)19), MaterialHandoff.HandledMessages);
    }

    [Fact]
    public async Task A_role_word_the_table_does_not_name_reads_as_other()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        List<MaterialHandoffLink> seen = [];
        pair.Receiver.HandoffLinkAnnounced += (_, e) => seen.Add(e.Link);

        // Lower case, which TRROLE's table does not name. Ordinal comparison, as everything in
        // this repository is over an identifier on a wire - and reading it anyway would be deciding
        // which end is holding the material on the strength of a guess.
        using var primary = new SecsMessage(4, 27, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("EQ1"),
                SecsItem.A(Link),
                SecsItem.A("CAR-001"),
                SecsItem.A("Carrier"),
                SecsItem.A("LP1"),
                SecsItem.A("source"),
                SecsItem.A("AGV3"),
                SecsItem.A("ARM"),
                SecsItem.A("BAY-4")),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the announcement to arrive");

        Assert.Equal(MaterialHandoffRole.Other, seen[0].Role);

        // And Other is zero, so a descriptor nobody set a role on reads the same way. Which side
        // is sending the material is not something to default.
        Assert.Equal(MaterialHandoffRole.Other, new MaterialHandoffLink { Link = Link }.Role);
    }

    [Fact]
    public async Task An_announcement_naming_no_link_is_dropped_rather_than_raised()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var announcements = 0;
        pair.Receiver.HandoffLinkAnnounced += (_, _) => announcements++;

        using var primary = new SecsMessage(4, 27, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("EQ1"),
                SecsItem.A(string.Empty),
                SecsItem.A("CAR-001"),
                SecsItem.A("Carrier"),
                SecsItem.A("LP1"),
                SecsItem.A("SOURCE"),
                SecsItem.A("AGV3"),
                SecsItem.A("ARM"),
                SecsItem.A("BAY-4")),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);

        // Then a good one, so the wait has something to finish on rather than timing out.
        await pair.Sender.AnnounceLinkAsync(Descriptor(), MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => announcements == 1, "only the second announcement");

        // One, not two. Every later message addresses a link by that string, so an announcement
        // without it could never be acted on - raising it would put an unusable entry in whatever
        // table an application keeps.
        Assert.Equal(1, announcements);
    }

    [Fact]
    public async Task A_body_that_is_not_nine_items_is_rejected_with_a_reason()
    {
        var log = new RecordingLogger<MaterialHandoff>();
        await using var pair = MaterialPair.Start(receiverLogger: log);
        await pair.OpenAsync();

        var announcements = 0;
        pair.Receiver.HandoffLinkAnnounced += (_, _) => announcements++;

        using var primary = new SecsMessage(4, 27, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A("EQ1"), SecsItem.A(Link)),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);
        await pair.Sender.AnnounceLinkAsync(Descriptor(), MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => announcements == 1, "only the well-formed announcement");

        Assert.Equal(1, announcements);

        // The log is the assertion that matters here, and a control earned it. Relax the arity
        // guard and the reader walks off the end of a two-item list, which throws inside the
        // dispatch loop - and the event is not raised either way, so a test watching only the
        // event passes against both. "Dropped, and here is why" and "threw somewhere" are very
        // different things to find in a fab, and this line is the difference.
        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("nine items", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_role_this_end_cannot_name_is_left_unstated_on_the_wire()
    {
        SecsItem? role = null;

        await using var pair = MaterialPair.Start(equipmentAnswer: transaction =>
        {
            if (transaction.F == 27 && transaction.Message.Item is { IsList: true, Count: 9 } body)
            {
                role = SecsItem.A(body[5].Format.IsText() ? body[5].GetString() : "?");
            }

            return null;
        });

        await pair.OpenAsync();

        await pair.Sender.AnnounceLinkAsync(
            new MaterialHandoffLink { Link = Link, Role = MaterialHandoffRole.Other },
            MaterialPair.Token);

        await MaterialPair.WaitUntilAsync(() => role is not null, "the announcement to cross the wire");

        // Empty, not the word "Other" - which no table names. A role this end cannot name is a
        // role it does not state, and writing a word the reference never defined would put a third
        // value into a two-value vocabulary. Asserted off the wire rather than through a
        // round-trip, because both spellings read back as Other and only the bytes differ.
        Assert.Equal(string.Empty, role!.GetString());
        role.Dispose();
    }

    [Fact]
    public async Task A_link_cannot_be_announced_under_no_name()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        // Refused here rather than sent and dropped at the far end, which is the one place in this
        // protocol where an argument is checked: every later message addresses a link by this
        // string, so an announcement without it is a message that can never be followed up. The
        // other seven methods check it too, for the same reason.
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await pair.Sender.AnnounceLinkAsync(
                new MaterialHandoffLink { Link = string.Empty },
                MaterialPair.Token));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await pair.Sender.RequestCancelAsync(string.Empty, MaterialPair.Token));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await pair.Sender.SendCommandAsync(string.Empty, 1, "LOAD", [], MaterialPair.Token));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await pair.Sender.AnnounceLinkAsync(null!, MaterialPair.Token));
    }

    // ------------------------------------------------- S4F29 to S4F41, the seven per-link rows

    [Fact]
    public async Task A_command_carries_its_index_its_name_and_its_parameters()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        await pair.Sender.SendCommandAsync(
            Link,
            2,
            "LOAD",
            [("SLOT", "7"), ("SPEED", "SLOW")],
            MaterialPair.Token);

        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the command to arrive");

        Assert.Equal(MaterialHandoffNotice.Command, seen[0].Notice);
        Assert.Equal(Link, seen[0].Link);
        Assert.Equal(2u, seen[0].Index);
        Assert.Equal("LOAD", seen[0].CommandName);
        Assert.Equal<(string, string)[]>([("SLOT", "7"), ("SPEED", "SLOW")], [.. seen[0].Parameters]);
        Assert.Equal("Command LINK-1 #2 LOAD", seen[0].ToString());
    }

    [Fact]
    public async Task The_parameters_outlive_the_handler_because_they_are_strings()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        await pair.Sender.SendCommandAsync(Link, 1, "LOAD", [("SLOT", "7")], MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the command to arrive");

        // Read after the handler returned and the message was disposed. CPVAL is an A here, so
        // there is no format to preserve and nothing borrowed from the arriving message - which is
        // the opposite of GEM's remote command parameters, and the difference is worth asserting
        // because it is the one place a caller may hold on to what arrived.
        Assert.Equal("7", Assert.Single(seen[0].Parameters).Value);
    }

    [Fact]
    public async Task A_command_completion_carries_the_index_back()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        await pair.Sender.ReportCommandCompleteAsync(
            Link,
            2,
            MaterialHandoffAcknowledgement.Accepted,
            MaterialPair.Token);

        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the completion to arrive");

        Assert.Equal(MaterialHandoffNotice.CommandComplete, seen[0].Notice);
        Assert.Equal(2u, seen[0].Index);
        Assert.Equal(MaterialHandoffAcknowledgement.Accepted, seen[0].Acknowledgement);
        Assert.Equal("CommandComplete LINK-1 #2 Accepted", seen[0].ToString());
    }

    [Fact]
    public async Task The_verification_is_the_one_row_that_can_say_why()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        await pair.Sender.ReportVerifiedAsync(
            Link,
            MaterialHandoffAcknowledgement.Refused,
            errorCode: 42,
            errorText: "arm did not reach the port",
            MaterialPair.Token);

        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the verification to arrive");

        // HOACK has no table, so a refusal carries no reason of its own - and this row carries
        // ERRCODE and ERRTEXT beside it, which is where one goes. Neither is tabled either, so the
        // code crosses as a number and nothing here interprets it.
        Assert.Equal(MaterialHandoffNotice.Verified, seen[0].Notice);
        Assert.Equal(MaterialHandoffAcknowledgement.Refused, seen[0].Acknowledgement);
        Assert.Equal(42u, seen[0].ErrorCode);
        Assert.Equal("arm did not reach the port", seen[0].ErrorText);
    }

    [Fact]
    public async Task A_cancel_and_a_halt_are_two_things_and_stay_two_things()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        await pair.Sender.RequestCancelAsync(Link, MaterialPair.Token);
        await pair.Sender.RequestHaltAsync(Link, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 2, "both stop requests to arrive");

        // The reference gives this stream two ways to stop and names them differently, and says
        // nothing about the difference - no table, no prose. So both are carried and neither is
        // defined: collapsing them would throw away a distinction the wire makes.
        Assert.Equal(MaterialHandoffNotice.CancelReady, seen[0].Notice);
        Assert.Equal(MaterialHandoffNotice.Halt, seen[1].Notice);
        Assert.Equal("CancelReady LINK-1", seen[0].ToString());
        Assert.Equal("Halt LINK-1", seen[1].ToString());
    }

    [Fact]
    public async Task The_two_stop_requests_are_answered_by_primaries_of_their_own()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var atSender = WatchLink(pair.Sender);

        await pair.Receiver.AcknowledgeCancelAsync(Link, MaterialHandoffAcknowledgement.Accepted, MaterialPair.Token);
        await pair.Receiver.AcknowledgeHaltAsync(Link, MaterialHandoffAcknowledgement.Refused, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => atSender.Count == 2, "both answers to arrive");

        // Primaries, not replies: the reference gives S4F35 and S4F39 no secondary, so the answer
        // is a message of its own travelling the other way. The same shape seven of the interlocked
        // handshake's nine have.
        Assert.Equal(MaterialHandoffNotice.CancelReadyAcknowledge, atSender[0].Notice);
        Assert.Equal(MaterialHandoffAcknowledgement.Accepted, atSender[0].Acknowledgement);
        Assert.Equal(MaterialHandoffNotice.HaltAcknowledge, atSender[1].Notice);
        Assert.Equal(MaterialHandoffAcknowledgement.Refused, atSender[1].Acknowledgement);
    }

    [Fact]
    public async Task Every_non_zero_acknowledgement_is_a_refusal_because_the_items_name_none()
    {
        var log = new RecordingLogger<MaterialHandoff>();
        await using var pair = MaterialPair.Start(receiverLogger: log);
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        // 9, which is neither zero nor one. HOACK, HOCANCELACK and HOHALTACK are B[1] items with
        // no table of values at all, so there is no boundary to fold at and no named reason to
        // report: zero is the favourable answer, read off this stream's own RSACK and RRACK, and
        // everything else is a refusal.
        using var primary = new SecsMessage(4, 31, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A(Link), SecsItem.U4(1u), SecsItem.B(9)),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the completion to arrive");

        Assert.Equal(MaterialHandoffAcknowledgement.Refused, seen[0].Acknowledgement);
        Assert.True(Enum.IsDefined(seen[0].Acknowledgement));

        // And 9 survives in the log, because collapsing the range to one member throws it away
        // everywhere else. A control earned this line too: without it, deleting the diagnostic
        // changed nothing a test could see - the same finding three chapters in a row.
        Assert.Contains(
            log.From(LogLevel.Information),
            message => message.Contains("acknowledgement 9", StringComparison.Ordinal)
                && message.Contains("no table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_notice_naming_no_link_is_dropped()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        using var primary = new SecsMessage(4, 35, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A(string.Empty)),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);
        await pair.Sender.RequestCancelAsync(Link, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "only the one that names a link");

        // The link is the only field whose absence is fatal: a message addressed to no link cannot
        // be acted on, where a missing index or reason leaves a step that still says which link and
        // which step it is.
        Assert.Equal(1, seen.Count);
        Assert.Equal(Link, seen[0].Link);
    }

    [Fact]
    public async Task A_bare_link_item_is_read_rather_than_refused()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        // S4F35 and S4F39 carry one item, so a machine that sent the bare TRLINK rather than a
        // list of one is doing the sensible thing with an ambiguous row. This end writes the list
        // and reads either.
        using var primary = new SecsMessage(4, 39, replyExpected: false)
        {
            Item = SecsItem.A(Link).Named("TRLINK"),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the bare-item halt to arrive");

        Assert.Equal(MaterialHandoffNotice.Halt, seen[0].Notice);
        Assert.Equal(Link, seen[0].Link);
    }

    [Fact]
    public async Task A_short_body_keeps_the_link_and_the_step_and_loses_the_rest()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var seen = WatchLink(pair.Receiver);

        // A command with no name and no parameters. Everything after the link is optional in this
        // reader on purpose: a step that says which link and which step it is still tells an
        // application something, and the alternative is dropping a message over a missing field
        // the reference never says is required.
        using var primary = new SecsMessage(4, 29, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.A(Link)),
        };

        await pair.HostLink.PostAsync(primary, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(() => seen.Count == 1, "the short command to arrive");

        Assert.Equal(MaterialHandoffNotice.Command, seen[0].Notice);
        Assert.Equal(Link, seen[0].Link);
        Assert.Equal(0u, seen[0].Index);
        Assert.Equal(string.Empty, seen[0].CommandName);
        Assert.Empty(seen[0].Parameters);
    }

    // ------------------------------------------------------------------- the two halves together

    [Fact]
    public async Task The_handoff_protocol_and_the_interlocked_handshake_share_one_type_and_no_functions()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var steps = new List<MaterialHandoffStep>();
        var notices = new List<MaterialHandoffNotice>();
        pair.Receiver.HandoffReported += (_, e) => steps.Add(e.Step);
        pair.Receiver.HandoffLinkReported += (_, e) => notices.Add(e.Notice);

        await pair.Sender.ReportAsync(MaterialHandoffStep.SendMaterial, 1, "LOT-7", MaterialPair.Token);
        await pair.Sender.RequestCancelAsync(Link, MaterialPair.Token);
        await MaterialPair.WaitUntilAsync(
            () => steps.Count == 1 && notices.Count == 1,
            "one step and one notice to arrive");

        // Two protocols in one stream and one type, and they do not leak into each other: the
        // handshake's nine functions and the protocol's eight are disjoint, and so are the events.
        Assert.Equal(MaterialHandoffStep.SendMaterial, Assert.Single(steps));
        Assert.Equal(MaterialHandoffNotice.CancelReady, Assert.Single(notices));

        var handshake = Enum.GetValues<MaterialHandoffStep>().Select(step => (byte)step).ToList();
        var protocol = Enum.GetValues<MaterialHandoffNotice>().Select(notice => (byte)notice).ToList();

        Assert.Empty(handshake.Intersect(protocol));
        Assert.Equal(9, handshake.Count);
        Assert.Equal(7, protocol.Count);

        // Seventeen declared: nine plus seven plus S4F27, which has an event of its own because it
        // announces a link rather than acting on one.
        Assert.Equal(17, MaterialHandoff.HandledMessages.Count);
        Assert.Contains(((byte)4, (byte)27), MaterialHandoff.HandledMessages);
    }
}
