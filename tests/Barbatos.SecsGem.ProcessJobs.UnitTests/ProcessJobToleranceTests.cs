// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// The shapes this library reads but would never write.
/// </summary>
/// <remarks>
/// <para>
/// Not one of stream 16's twenty-eight catalogue rows is a definition rather than part of a
/// representative subset, and five of its thirteen item names have no dictionary entry at all. So
/// the bodies this package writes are a careful reading, and what it <em>accepts</em> has to be
/// wider than what it sends - otherwise the first machine that disagrees about a format is a
/// machine this library cannot talk to.
/// </para>
/// <para>
/// Each test here plays the equipment by hand, which is the only way to put a shape on the wire
/// that the equipment side would not have produced.
/// </para>
/// </remarks>
public class ProcessJobToleranceTests
{
    private static SecsMessage Reply(byte function, SecsItem body)
        => new(16, function, replyExpected: false) { Name = $"HandWritten{function}", Item = body };

    [Fact]
    public async Task A_state_the_catalogue_reserves_is_read_as_a_code_with_no_name()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            5 => Reply(6, SecsItem.L(
                SecsItem.A("PJ-1"),
                SecsItem.A("RECIPE-7"),
                SecsItem.U1(5))),
            _ => null,
        });

        await pair.OpenAsync();

        var status = await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);

        // PRSTATE reserves 5, so this library has no name for it - and says so, rather than
        // reading it as Queued, the zero value, which would tell a host the job has not started.
        Assert.NotNull(status);
        Assert.Null(status.State);
        Assert.Equal((byte)5, status.StateCode);
        Assert.Equal("RECIPE-7", status.RecipeId);
    }

    [Fact]
    public async Task A_state_nobody_defined_at_all_is_read_the_same_way()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            5 => Reply(6, SecsItem.L(SecsItem.A("PJ-1"), SecsItem.A(string.Empty), SecsItem.U4(200u))),
            _ => null,
        });

        await pair.OpenAsync();

        var status = await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);

        Assert.Null(status?.State);
        Assert.Equal((byte)200, status?.StateCode);
    }

    [Theory]
    [InlineData("Processing", ProcessJobState.Processing)]
    [InlineData("WAITINGFORSTART", ProcessJobState.WaitingForStart)]
    [InlineData("3", ProcessJobState.Processing)]
    public async Task A_state_sent_as_text_is_understood(string sent, ProcessJobState expected)
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            5 => Reply(6, SecsItem.L(SecsItem.A("PJ-1"), SecsItem.A("RECIPE-7"), SecsItem.A(sent))),
            _ => null,
        });

        await pair.OpenAsync();

        var status = await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);

        // The names are PRSTATE's own, so a machine that sends one rather than its number is not
        // guessing - and a machine that writes the number into a text item is understood too.
        Assert.Equal(expected, status?.State);
        Assert.Equal((byte)expected, status?.StateCode);
    }

    [Fact]
    public async Task A_state_in_a_form_nothing_can_read_leaves_both_properties_empty()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            5 => Reply(6, SecsItem.L(SecsItem.A("PJ-1"), SecsItem.A("RECIPE-7"), SecsItem.A("sometime"))),
            _ => null,
        });

        await pair.OpenAsync();

        var status = await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);

        // The report still arrives: which job it is about matters more than what state it claims,
        // and a host that threw here would lose the identifier as well.
        Assert.NotNull(status);
        Assert.Null(status.State);
        Assert.Equal((byte)0, status.StateCode);
    }

    [Fact]
    public async Task An_acknowledgement_in_any_numeric_format_is_read()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            1 => Reply(2, SecsItem.L(SecsItem.A("PJ-1"), SecsItem.U4(0u))),
            9 => Reply(10, SecsItem.L(SecsItem.A("PJ-1"), SecsItem.I2(1))),
            _ => null,
        });

        await pair.OpenAsync();

        // PJACK is written B[1] here, from PREACK's two values - and read out of whatever numeric
        // format arrived, because the inference decides what this library writes and nothing else.
        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Host.CreateAsync("PJ-1", "RECIPE-7", cancellationToken: ProcessJobPair.Token));

        Assert.Equal(
            ProcessJobAcknowledgement.Rejected,
            await pair.Host.DeselectAsync("PJ-1", ProcessJobPair.Token));
    }

    [Fact]
    public async Task A_command_code_E5_does_not_define_never_reads_as_performed()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            3 => Reply(4, SecsItem.L(SecsItem.A("PJ-1"), SecsItem.B(9))),
            _ => null,
        });

        await pair.OpenAsync();

        var acknowledgement = await pair.Host.CommandAsync(
            "PJ-1",
            ProcessJobCommands.Stop,
            ProcessJobPair.Token);

        // The rule the whole stack follows: an undefined acknowledgement reads as the
        // unfavourable answer. Here it is "cannot perform now" rather than "does not exist" - a
        // machine answering with something unreadable has the command, or it would have said 1.
        Assert.Equal(ProcessJobCommandAcknowledgement.CannotPerformNow, acknowledgement);
    }

    [Fact]
    public async Task A_reply_carrying_only_the_code_is_read()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            1 => Reply(2, SecsItem.B(0)),
            3 => Reply(4, SecsItem.B(0)),
            _ => null,
        });

        await pair.OpenAsync();

        // Some machines answer with the code and not the pair. The identifier was in the request,
        // so nothing is lost by accepting it.
        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Host.CreateAsync("PJ-1", "RECIPE-7", cancellationToken: ProcessJobPair.Token));

        Assert.Equal(
            ProcessJobCommandAcknowledgement.Performed,
            await pair.Host.CommandAsync("PJ-1", ProcessJobCommands.Stop, ProcessJobPair.Token));
    }

    [Fact]
    public async Task A_list_of_jobs_laid_out_flat_is_read_as_entries()
    {
        await using var pair = ProcessJobPair.Start(answer: transaction => transaction.F switch
        {
            7 => Reply(8, SecsItem.L(
                SecsItem.A("PJ-1"),
                SecsItem.A("RECIPE-7"),
                SecsItem.U1(0),
                SecsItem.A("PJ-2"),
                SecsItem.A("RECIPE-9"),
                SecsItem.U1(3))),
            _ => null,
        });

        await pair.OpenAsync();

        var jobs = await pair.Host.ListAsync(ProcessJobPair.Token);

        // An item list cannot say whether a repeating body is a list of entries or a flat run of
        // fields, so this package writes one and reads both - the same tolerance streams 12 and 3
        // needed, for the same reason.
        Assert.Equal(2, jobs.Count);
        Assert.Equal("PJ-1", jobs[0].ProcessJobId);
        Assert.Equal(ProcessJobState.Queued, jobs[0].State);
        Assert.Equal("PJ-2", jobs[1].ProcessJobId);
        Assert.Equal(ProcessJobState.Processing, jobs[1].State);
    }

    [Fact]
    public async Task A_timestamp_in_a_shorter_form_is_read_and_one_in_no_form_is_not_fatal()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        List<ProcessJobStateReport> seen = [];

        pair.Host.JobStateReported += (_, args) => seen.Add(args.Report);

        // The equipment's own S16F11 writes sixteen characters; a machine that writes fourteen or
        // twelve is still understood, and one that writes nonsense still gets its report through.
        foreach (var time in new[] { "20261116190206", "261116190206", "whenever", string.Empty })
        {
            using var primary = new SecsMessage(16, 11, replyExpected: true)
            {
                Name = "HandWritten11",
                Item = SecsItem.L(SecsItem.A("PJ-1"), SecsItem.U1(3), SecsItem.A(time)),
            };

            using var reply = await pair.EquipmentLink.SendAsync(primary, ProcessJobPair.Token);

            Assert.Equal(16, reply.S);
            Assert.Equal(12, reply.F);
        }

        Assert.Equal(4, seen.Count);
        Assert.Equal(new DateTime(2026, 11, 16, 19, 2, 6, DateTimeKind.Utc), seen[0].At);
        Assert.Equal(new DateTime(2026, 11, 16, 19, 2, 6, DateTimeKind.Utc), seen[1].At);
        Assert.Null(seen[2].At);
        Assert.Null(seen[3].At);
        Assert.All(seen, report => Assert.Equal(ProcessJobState.Processing, report.State));
    }

    [Fact]
    public async Task A_material_list_sent_as_one_bare_item_is_read_as_one_piece_of_material()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(16, 1, replyExpected: true)
        {
            Name = "HandWritten1",
            Item = SecsItem.L(SecsItem.A("PJ-1"), SecsItem.A("RECIPE-7"), SecsItem.A("W01")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ProcessJobPair.Token);

        Assert.Equal(2, reply.F);

        // A list where a list of one was expected, which is the commonest disagreement there is.
        Assert.Equal("W01", Assert.Single(pair.Job().Material));
    }

    [Fact]
    public async Task A_create_with_no_material_at_all_is_read_as_a_job_awaiting_material()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(16, 1, replyExpected: true)
        {
            Name = "HandWritten1",
            Item = SecsItem.L(SecsItem.A("PJ-1"), SecsItem.A("RECIPE-7")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ProcessJobPair.Token);

        Assert.Equal(2, reply.F);

        // A real thing rather than a malformed body: E40 lets a host bind material later, and
        // S16F25 exists for exactly that.
        Assert.Empty(pair.Job().Material);
    }

    [Fact]
    public async Task A_create_with_no_identifier_is_refused_rather_than_guessed_at()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(16, 1, replyExpected: true)
        {
            Name = "HandWritten1",
            Item = SecsItem.L(SecsItem.A(string.Empty), SecsItem.A("RECIPE-7")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ProcessJobPair.Token);

        Assert.Equal(2, reply.F);
        Assert.Empty(pair.Manager.Jobs);
    }

    [Fact]
    public async Task A_body_that_is_not_a_list_at_all_is_refused_rather_than_throwing()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        // A machine that threw inside a handler would leave the host waiting out T3 for a reply
        // that never comes, which is the worst answer available.
        using var primary = new SecsMessage(16, 1, replyExpected: true)
        {
            Name = "HandWritten1",
            Item = SecsItem.A("PJ-1"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, ProcessJobPair.Token);

        Assert.Equal(2, reply.F);
        Assert.Empty(pair.Manager.Jobs);
    }
}
