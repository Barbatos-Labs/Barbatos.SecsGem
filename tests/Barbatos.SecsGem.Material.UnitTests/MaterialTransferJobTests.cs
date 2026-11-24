// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// The job half of stream 4, which was refused three times on three wrong reasons.
/// </summary>
/// <remarks>
/// <para>
/// Each refusal was argued from the message rows while the answer sat in the item dictionary. The
/// third and subtlest said <c>TRACK</c>, and <c>TRACK</c> is defined - an <c>A[n]</c> with no
/// values, which is a different thing from a name nobody wrote down. Every one of the twenty item
/// names across these four pairs is in the dictionary, and the first test here is that measurement.
/// </para>
/// <para>
/// So stream 4 is served whole: seventeen rows in <c>MaterialHandoff</c> and eight here.
/// </para>
/// </remarks>
public class MaterialTransferJobTests
{
    private static MaterialHandoffLink Link => new()
    {
        Link = "LINK-1",
        ObjectName = "CAR-001",
        ObjectType = "Carrier",
        Port = "LP1",
        Role = MaterialHandoffRole.Source,
        PartnerName = "AGV-2",
        PartnerPort = "ARM",
        Location = "BAY-3",
    };

    private static MaterialTransferJobRequest Request => new()
    {
        JobName = "JOB-7",
        Link = Link,
        DataId = 41,
    };

    // ----------------------------------------------------------------- the measurement

    [Fact]
    public void Every_item_in_all_four_pairs_is_defined_which_is_why_the_refusal_fell()
    {
        // The whole case for this capability in one assertion. Twenty names, no gaps - which makes
        // these four the only pairs served here whose rows were clean from the start rather than
        // needing a reading for something undefined.
        string[] names =
        [
            "DATAID", "TRJOBNAME", "TRLINK", "TROBJNAME", "TROBJTYPE", "TRPORT", "TRROLE",
            "TRPTNR", "TRPTPORT", "TRLOCATION", "TRJOBID", "TRACK", "ERRCODE", "ERRTEXT",
            "TRCMDNAME", "CPNAME", "CPVAL", "TRJOBMS", "DATALENGTH", "GRANT",
        ];

        Assert.Equal(20, names.Length);
        Assert.All(names, name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), name));

        // And TRACK, the item the last refusal named, is among them: an A with no code table.
        // Untabled is not undefined, which is the distinction that refusal missed.
        Assert.True(SecsCatalog.TryGetDataItem("TRACK", out var track));
        Assert.Equal("A", track!.FormatToken);
        Assert.Empty(track.Codes);
    }

    [Fact]
    public void The_job_state_enum_is_quoted_from_the_table_rather_than_invented()
    {
        // The first refusal said a transfer job has no state model anywhere in the reference.
        // TRJOBMS is a U1 with six named codes, so this enum is a quotation.
        Assert.True(SecsCatalog.TryGetDataItem("TRJOBMS", out var states));
        Assert.Equal("U1", states!.FormatToken);

        Assert.Equal<string[]>(
            ["Queued", "Selected", "Executing", "Paused", "Completed", "Cancelled"],
            [.. states.Codes.Select(code => code.Meaning)]);

        Assert.Equal<string[]>(
            ["Queued", "Selected", "Executing", "Paused", "Completed", "Cancelled"],
            Enum.GetNames<MaterialTransferJobState>());

        // No reserved range, so no catch-all member - the COMMACK position. A machine answering
        // six is reporting a state the reference does not name.
        Assert.DoesNotContain(states.Codes, code => code.Value.Contains('-', StringComparison.Ordinal));
    }

    [Fact]
    public void The_two_ends_declare_different_messages_which_the_handoff_half_does_not()
    {
        // The reason this is a type of its own. MaterialHandoff's seventeen travel either way and
        // one list serves both ends; these four are directional, so a machine declaring the host's
        // list would be claiming to answer its own alerts.
        Assert.Equal<(byte, byte)[]>(
            [(4, 19), (4, 21), (4, 25)],
            [.. MaterialTransferJobs.EquipmentMessages]);

        Assert.Equal<(byte, byte)[]>([(4, 23)], [.. MaterialTransferJobs.HostMessages]);

        Assert.Empty(MaterialTransferJobs.EquipmentMessages.Intersect(MaterialTransferJobs.HostMessages));
        Assert.Empty(MaterialTransferJobs.EquipmentMessages.Intersect(MaterialHandoff.HandledMessages));
        Assert.Empty(MaterialTransferJobs.HostMessages.Intersect(MaterialHandoff.HandledMessages));

        // Stream 4 whole: seventeen plus four primaries is every primary the reference gives it.
        Assert.True(SecsCatalog.TryGetStream(4, out var four));
        Assert.Equal(
            four!.Messages.Count(message => message.Function % 2 == 1),
            MaterialHandoff.HandledMessages.Count
                + MaterialTransferJobs.EquipmentMessages.Count
                + MaterialTransferJobs.HostMessages.Count);
    }

    // ----------------------------------------------------------------- S4F19 and S4F20

    [Fact]
    public async Task Creating_a_job_hands_back_the_identifier_the_machine_minted()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialTransferJobRequest? seen = null;

        pair.MachineJobs.AnswerCreate = request =>
        {
            seen = request;
            return ("TJ-1", new MaterialTransferJobResult("OK", 0, string.Empty));
        };

        var (jobId, result) = await MaterialPair.SentAsync(pair.HostJobs.CreateAsync(Request, MaterialPair.Token));

        // S4F20 is where the TRJOBID is minted, so the identifier comes back from this call and
        // nowhere else - every later message addresses the job by it.
        Assert.Equal("TJ-1", jobId);
        Assert.Equal("OK", result.Acknowledgement);
        Assert.Equal(0u, result.ErrorCode);

        // The link block arrived whole, which is the body read off S4F27 rather than guessed at.
        Assert.NotNull(seen);
        Assert.Equal("JOB-7", seen!.JobName);
        Assert.Equal(41u, seen.DataId);
        Assert.Equal("LINK-1", seen.Link.Link);
        Assert.Equal("CAR-001", seen.Link.ObjectName);
        Assert.Equal(MaterialHandoffRole.Source, seen.Link.Role);
        Assert.Equal("BAY-3", seen.Link.Location);
    }

    [Fact]
    public async Task A_machine_that_schedules_nothing_refuses_and_says_so_in_the_error_text()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        // The delegate's own default, left alone. Scheduling a transfer is the machine doing
        // something, and answering otherwise would tell a host material was going to move.
        var (jobId, result) = await MaterialPair.SentAsync(pair.HostJobs.CreateAsync(Request, MaterialPair.Token));

        Assert.Empty(jobId);
        Assert.Equal(1u, result.ErrorCode);
        Assert.Contains("schedules no transfer jobs", result.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_acknowledgement_item_in_this_half_is_tabled_so_neither_is_interpreted()
    {
        // The decision this capability turns on, pinned - and the first draft of the library got
        // it the other way round. TRACK is an A with no values and ERRCODE is a U4 with none, so
        // the row has no tabled item at all.
        foreach (var name in new[] { "TRACK", "ERRCODE" })
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
            Assert.Empty(item!.Codes);
        }

        // Where a row does have a tabled item this stack reads that and uses ERRCODE as the reason
        // beside it - S5F18's ACKA is a BOOLEAN that arrives with one. This row has nothing to
        // play that part.
        Assert.True(SecsCatalog.TryGetDataItem("ACKA", out var acka));
        Assert.Equal(2, acka!.Codes.Count);

        // So there is no verdict on the type: three fields, as they arrived. Which is the same
        // thing this package already says about ERRCODE at the S4F33 it serves, and getting those
        // two to agree is why the draft was changed rather than the remark.
        Assert.DoesNotContain(
            "IsAccepted",
            typeof(MaterialTransferJobResult).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task All_three_fields_come_back_exactly_as_the_machine_set_them()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        // A machine may put anything in TRACK, including a word that reads like a refusal beside
        // an ERRCODE of nought. Nothing here reconciles the two - both arrive as sent, and what
        // they mean together is between the host and the machine.
        pair.MachineJobs.AnswerCreate = _ => ("TJ-2", new MaterialTransferJobResult("DENIED", 0, string.Empty));

        var (_, odd) = await MaterialPair.SentAsync(pair.HostJobs.CreateAsync(Request, MaterialPair.Token));

        Assert.Equal("DENIED", odd.Acknowledgement);
        Assert.Equal(0u, odd.ErrorCode);
        Assert.Equal("TRACK=DENIED ERRCODE=0", odd.ToString());

        pair.MachineJobs.AnswerCreate = _ => ("TJ-3", new MaterialTransferJobResult("OK", 9, "no such port"));

        var (_, faulted) = await MaterialPair.SentAsync(pair.HostJobs.CreateAsync(Request, MaterialPair.Token));

        Assert.Equal("OK", faulted.Acknowledgement);
        Assert.Equal(9u, faulted.ErrorCode);
        Assert.Equal("TRACK=OK ERRCODE=9: no such port", faulted.ToString());
    }

    [Fact]
    public async Task A_job_request_this_end_cannot_read_is_refused_with_a_reason_and_logged()
    {
        var log = new RecordingLogger<MaterialTransferJobs>();

        await using var pair = MaterialPair.Start(jobLogger: log);
        await pair.OpenAsync();

        var reached = 0;

        pair.MachineJobs.AnswerCreate = _ =>
        {
            reached++;
            return ("TJ-4", new MaterialTransferJobResult(string.Empty, 0, string.Empty));
        };

        using var malformed = new SecsMessage(4, 19, replyExpected: true)
        {
            Name = "TransferJobCreate",
            Item = SecsItem.A("not a transfer job"),
        };

        using var reply = await pair.HostLink.SendAsync(malformed, MaterialPair.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), MaterialPair.Token);

        // Refused by the reader, so the delegate was never consulted - and S4F20 has an ERRCODE
        // and an ERRTEXT, which is more than most rows here can say about a body they could not
        // read.
        Assert.Equal(0, reached);
        Assert.Equal(4, reply.Item!.Count);
        Assert.Equal(SecsItem.U4(1u), reply.Item[2]);

        Assert.Contains(
            log.From(LogLevel.Warning),
            message => message.Contains("S4F19", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_job_naming_no_link_is_refused_because_nothing_later_could_address_it()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var reached = 0;

        pair.MachineJobs.AnswerCreate = _ =>
        {
            reached++;
            return ("TJ-5", new MaterialTransferJobResult(string.Empty, 0, string.Empty));
        };

        // Ten items, so the arity is right, and an empty TRLINK. Refused for the reason
        // TryReadHandoffLink gives: every later message addresses the link by that string.
        using var nameless = new SecsMessage(4, 19, replyExpected: true)
        {
            Name = "TransferJobCreate",
            Item = SecsItem.L(
                SecsItem.U4(1u).Named("DATAID"),
                SecsItem.A("JOB-8").Named("TRJOBNAME"),
                SecsItem.A(string.Empty).Named("TRLINK"),
                SecsItem.A("CAR-002").Named("TROBJNAME"),
                SecsItem.A("Carrier").Named("TROBJTYPE"),
                SecsItem.A("LP2").Named("TRPORT"),
                SecsItem.A("SOURCE").Named("TRROLE"),
                SecsItem.A("AGV-1").Named("TRPTNR"),
                SecsItem.A("ARM").Named("TRPTPORT"),
                SecsItem.A("BAY-1").Named("TRLOCATION")),
        };

        using var reply = await pair.HostLink.SendAsync(nameless, MaterialPair.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), MaterialPair.Token);

        Assert.Equal(0, reached);
        Assert.Equal(SecsItem.U4(1u), reply.Item![2]);
    }

    // ----------------------------------------------------------------- S4F21 and S4F22

    [Fact]
    public async Task Commanding_a_job_carries_its_name_and_parameters()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        (string Job, string Command, IReadOnlyList<(string Name, string Value)> Parameters) seen = default;

        pair.MachineJobs.AnswerCommand = (job, command, parameters) =>
        {
            seen = (job, command, parameters);
            return new MaterialTransferJobResult("OK", 0, string.Empty);
        };

        var result = await MaterialPair.SentAsync(pair.HostJobs.SendCommandAsync(
            "TJ-1",
            "PAUSE",
            [("REASON", "operator")],
            MaterialPair.Token));

        Assert.Equal(0u, result.ErrorCode);
        Assert.Equal("TJ-1", seen.Job);
        Assert.Equal("PAUSE", seen.Command);
        Assert.Equal(("REASON", "operator"), Assert.Single(seen.Parameters!));

        // The command names are the application's: TRCMDNAME is an A with no table, the same
        // position HOCMDNAME is in at S4F29, so this package carries the string and takes no view.
        Assert.True(SecsCatalog.TryGetDataItem("TRCMDNAME", out var command));
        Assert.Empty(command!.Codes);
    }

    [Fact]
    public async Task A_command_with_no_parameters_is_an_empty_list_rather_than_a_missing_item()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var count = -1;

        pair.MachineJobs.AnswerCommand = (_, _, parameters) =>
        {
            count = parameters.Count;
            return new MaterialTransferJobResult(string.Empty, 0, string.Empty);
        };

        await pair.HostJobs.SendCommandAsync("TJ-1", "START", [], MaterialPair.Token);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task A_machine_that_takes_no_commands_refuses_by_default()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var result = await MaterialPair.SentAsync(pair.HostJobs.SendCommandAsync("TJ-1", "START", [], MaterialPair.Token));

        Assert.Equal(1u, result.ErrorCode);
        Assert.Contains("no transfer job commands", result.ErrorText, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- S4F23 and S4F24

    [Fact]
    public async Task An_alert_reaches_the_host_with_the_state_the_machine_reported()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialTransferJobAlertEventArgs? seen = null;

        pair.HostJobs.JobAlerted += (_, args) => seen = args;

        await pair.MachineJobs.AlertAsync(
            "TJ-1",
            MaterialTransferJobState.Executing,
            "JOB-7",
            cancellationToken: MaterialPair.Token);

        Assert.NotNull(seen);
        Assert.Equal("TJ-1", seen!.JobId);
        Assert.Equal("JOB-7", seen.JobName);
        Assert.Equal(MaterialTransferJobState.Executing, seen.State);
        Assert.Equal(0u, seen.ErrorCode);

        // S4F23 is the only primary of this half the machine sends, which is why the alert arrives
        // on the host's object and not the machine's.
        Assert.Contains(((byte)4, (byte)23), pair.AskedOfHost);
    }

    [Fact]
    public async Task An_alert_can_carry_a_fault_as_well_as_a_state()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialTransferJobAlertEventArgs? seen = null;

        pair.HostJobs.JobAlerted += (_, args) => seen = args;

        await pair.MachineJobs.AlertAsync(
            "TJ-1",
            MaterialTransferJobState.Cancelled,
            errorCode: 7,
            errorText: "the partner withdrew",
            cancellationToken: MaterialPair.Token);

        Assert.NotNull(seen);
        Assert.Equal(MaterialTransferJobState.Cancelled, seen!.State);
        Assert.Equal(7u, seen.ErrorCode);
        Assert.Equal("the partner withdrew", seen.ErrorText);
    }

    [Fact]
    public async Task A_state_the_reference_does_not_name_is_refused_rather_than_folded()
    {
        var log = new RecordingLogger<MaterialTransferJobs>();

        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var raised = 0;

        pair.HostJobs.JobAlerted += (_, _) => raised++;

        using var beyond = new SecsMessage(4, 23, replyExpected: true)
        {
            Name = "TransferCommandAlert",
            Item = SecsItem.L(
                SecsItem.A("TJ-1").Named("TRJOBID"),
                SecsItem.A("JOB-7").Named("TRJOBNAME"),
                SecsItem.U1(6).Named("TRJOBMS"),
                SecsItem.U4(0u).Named("ERRCODE"),
                SecsItem.A(string.Empty).Named("ERRTEXT")),
        };

        using var reply = await pair.EquipmentLink.SendAsync(beyond, MaterialPair.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), MaterialPair.Token);

        // Nothing raised, because TRJOBMS reserves nothing: six codes and nothing above, so there
        // is no catch-all member to fold a seventh onto and inventing one would report a state the
        // machine never claimed.
        Assert.Equal(0, raised);

        // Acknowledged anyway, because S4F24 carries no items at all - a host that could not read
        // the alert has nowhere to say so and no reason to hold the transaction open.
        Assert.Equal(24, reply.F);
        Assert.Null(reply.Item);

        _ = log;
    }

    // ----------------------------------------------------------------- S4F25 and S4F26

    [Fact]
    public async Task The_multi_block_inquire_grants_by_default_and_can_be_refused()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        // Granted unless the machine says otherwise, which is how every other inquire in this
        // repository answers: a handshake whose only purpose is to let this end refuse should not
        // refuse by silence.
        // Bounded, which a control earned: removing S4F25 from the declaration list left this call
        // waiting on a T3 the fake clock never advances, so the run died with no summary and the
        // control reported finding nothing. See MaterialPair.SentAsync.
        Assert.Equal(
            MaterialGrant.Accepted,
            await MaterialPair.SentAsync(pair.HostJobs.InquireMultiBlockAsync(41, 70_000, MaterialPair.Token)));

        (uint Data, uint Length) asked = default;

        pair.MachineJobs.AnswerMultiBlockInquiry = (dataId, length) =>
        {
            asked = (dataId, length);
            return MaterialGrant.NoSpace;
        };

        Assert.Equal(
            MaterialGrant.NoSpace,
            await MaterialPair.SentAsync(pair.HostJobs.InquireMultiBlockAsync(41, 70_000, MaterialPair.Token)));

        Assert.Equal((41u, 70_000u), asked);
    }

    [Fact]
    public async Task The_inquire_uses_the_same_grant_table_three_other_rows_here_use()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        using var malformed = new SecsMessage(4, 25, replyExpected: true)
        {
            Name = "MultiBlockInquire",
            Item = SecsItem.A("not a DATAID and a DATALENGTH"),
        };

        using var reply = await pair.HostLink.SendAsync(malformed, MaterialPair.Token).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5), MaterialPair.Token);

        // DuplicateOrInvalid, the one code GRANT has that GRANT6 has not, and the only one of the
        // four that fits a body this end could not read.
        Assert.Equal(SecsItem.B((byte)MaterialGrant.DuplicateOrInvalid), reply.Item);

        // One item, one meaning, inside this package: S3F16 and S4F26 both carry GRANT and both
        // read MaterialGrant. S2F39's row carries it too, in another package, and the agreement
        // between the four readers is asserted against the reference's own table rather than
        // against each other.
        Assert.True(SecsCatalog.TryGetDataItem("GRANT", out var grant));
        Assert.Contains((byte)4, grant!.Streams);
        Assert.Contains((byte)3, grant.Streams);
    }
}
