// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// Stream 16 over a link, both ends, every transaction.
/// </summary>
/// <remarks>
/// The codec is internal, which is deliberate - a body is not public API - so this is where it is
/// checked: everything written here is read back by the other end's reader, and a shape that only
/// one side agreed with would fail here rather than in a fab.
/// </remarks>
public class ProcessJobPairTests
{
    private const string Recipe = "RECIPE-7";

    private static void Seed(ProcessJobManager manager, string processJobId = "PJ-1")
        => Assert.True(manager.Create(processJobId, Recipe, ["W01", "W02"], parameters:
            new Dictionary<string, string>(StringComparer.Ordinal) { ["TEMP"] = "250" }).IsAccepted);

    [Fact]
    public async Task A_host_runs_a_whole_process_job_over_stream_16()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        // Create it - S16F1, which carries three items and nothing else.
        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Host.CreateAsync("PJ-1", Recipe, ["W01", "W02"], ProcessJobPair.Token));

        var job = pair.Job();

        Assert.Equal(ProcessJobState.Queued, job.State);
        Assert.Equal(Recipe, job.RecipeId);
        Assert.Equal(["W01", "W02"], job.Material);

        // Read it - S16F5. The state comes back as PRSTATE's code, which this end turns back into
        // a name it can only have got from the catalogue.
        var status = await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);

        Assert.NotNull(status);
        Assert.Equal("PJ-1", status.ProcessJobId);
        Assert.Equal(Recipe, status.RecipeId);
        Assert.Equal(ProcessJobState.Queued, status.State);
        Assert.Equal(0, status.StateCode);

        // The machine takes it up and starts it: neither is a host command.
        Assert.True(pair.Manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(pair.Manager.SetupComplete("PJ-1").IsAccepted);

        // Pause it - S16F3 - and the reply says it was done. The job is Pausing, not Paused.
        Assert.Equal(
            ProcessJobCommandAcknowledgement.Performed,
            await pair.Host.CommandAsync("PJ-1", ProcessJobCommands.Pause, ProcessJobPair.Token));

        Assert.Equal(ProcessJobState.Pausing, pair.Job().State);

        Assert.True(pair.Manager.PauseComplete("PJ-1").IsAccepted);

        Assert.Equal(
            ProcessJobCommandAcknowledgement.Performed,
            await pair.Host.CommandAsync("PJ-1", ProcessJobCommands.Resume, ProcessJobPair.Token));

        // All its material goes through, and the host reads the ending.
        Assert.True(pair.Manager.MaterialProcessed("PJ-1").IsAccepted);

        var done = await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);

        Assert.Equal(ProcessJobState.ProcessComplete, done?.State);
        Assert.Equal((byte)4, done?.StateCode);

        // Take it off the machine - S16F9.
        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Host.DeselectAsync("PJ-1", ProcessJobPair.Token));

        Assert.Empty(pair.Manager.Jobs);

        pair.AssertAsked(16, 1);
        pair.AssertAsked(16, 3);
        pair.AssertAsked(16, 5);
        pair.AssertAsked(16, 9);
    }

    [Fact]
    public async Task A_status_request_for_a_job_nobody_created_answers_nothing()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        // Null rather than an exception: S16F6 has no acknowledgement code, so an empty body is
        // how the reply says there is no such job - and that is an answer, not a failure.
        Assert.Null(await pair.Host.GetStatusAsync("PJ-404", ProcessJobPair.Token));
        Assert.Null(await pair.Host.GetQueuePositionAsync("PJ-404", ProcessJobPair.Token));
    }

    [Fact]
    public async Task A_create_the_machine_refuses_comes_back_as_a_refusal_and_not_a_timeout()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        Assert.Equal(
            ProcessJobAcknowledgement.Rejected,
            await pair.Host.CreateAsync("PJ-1", Recipe, cancellationToken: ProcessJobPair.Token));

        // And the job that was there is untouched.
        Assert.Equal(["W01", "W02"], pair.Job().Material);
        Assert.Single(pair.Manager.Jobs);
    }

    [Fact]
    public async Task A_command_says_which_kind_of_no_it_is()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        // Queued, so a pause is a command this machine has and cannot do now.
        Assert.Equal(
            ProcessJobCommandAcknowledgement.CannotPerformNow,
            await pair.Host.CommandAsync("PJ-1", ProcessJobCommands.Pause, ProcessJobPair.Token));

        // And a command nobody defined is a different answer, which is the whole value of CMDA
        // being defined where OBJACK is not: one of these is a bug in the host and the other is
        // not.
        Assert.Equal(
            ProcessJobCommandAcknowledgement.DoesNotExist,
            await pair.Host.CommandAsync("PJ-1", "TELEPORT", ProcessJobPair.Token));

        Assert.Equal(ProcessJobState.Queued, pair.Job().State);
    }

    [Fact]
    public async Task The_list_comes_back_with_every_job_the_machine_has()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Seed(manager, "PJ-2");
            Assert.True(manager.BeginSetup("PJ-2").IsAccepted);
        });

        await pair.OpenAsync();

        var jobs = await pair.Host.ListAsync(ProcessJobPair.Token);

        Assert.Equal(2, jobs.Count);
        Assert.Contains(jobs, status => status.ProcessJobId == "PJ-1" && status.State == ProcessJobState.Queued);
        Assert.Contains(jobs, status => status.ProcessJobId == "PJ-2" && status.State == ProcessJobState.SettingUp);
        pair.AssertAsked(16, 7);
    }

    [Fact]
    public async Task The_list_of_a_machine_with_no_jobs_is_empty_rather_than_broken()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ListAsync(ProcessJobPair.Token));
    }

    [Fact]
    public async Task A_queue_position_comes_back_with_the_state_beside_it()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Seed(manager, "PJ-2");
        });

        await pair.OpenAsync();

        var second = await pair.Host.GetQueuePositionAsync("PJ-2", ProcessJobPair.Token);

        Assert.Equal((ushort)2, second?.Position);
        Assert.Equal(ProcessJobState.Queued, second?.State);

        Assert.True(pair.Manager.BeginSetup("PJ-1").IsAccepted);

        var moved = await pair.Host.GetQueuePositionAsync("PJ-2", ProcessJobPair.Token);
        var takenUp = await pair.Host.GetQueuePositionAsync("PJ-1", ProcessJobPair.Token);

        Assert.Equal((ushort)1, moved?.Position);

        // Zero, because positions count from one: a job the machine has taken up is not at
        // position one, and S16F20 has to say something.
        Assert.Equal((ushort)0, takenUp?.Position);
        Assert.Equal(ProcessJobState.SettingUp, takenUp?.State);
        pair.AssertAsked(16, 19);
    }

    [Fact]
    public async Task A_host_can_reorder_the_queue_and_is_told_which_part_was_wrong()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Seed(manager, "PJ-2");
            Seed(manager, "PJ-3");
        });

        await pair.OpenAsync();

        Assert.Equal(
            ProcessJobQueueAcknowledgement.Acknowledged,
            await pair.Host.ReorderAsync(["PJ-3", "PJ-2", "PJ-1"], ProcessJobPair.Token));

        Assert.Equal(["PJ-3", "PJ-2", "PJ-1"], pair.Manager.Queue);

        // HCACK is defined with seven meanings, and a reorder can honestly use three of them: a
        // list of the wrong length is a bad parameter, and a job that is not in the queue is no
        // such object. PJACK could only have said no.
        Assert.Equal(
            ProcessJobQueueAcknowledgement.ParameterInvalid,
            await pair.Host.ReorderAsync(["PJ-3"], ProcessJobPair.Token));

        Assert.True(pair.Manager.BeginSetup("PJ-3").IsAccepted);

        Assert.Equal(
            ProcessJobQueueAcknowledgement.NoSuchObject,
            await pair.Host.ReorderAsync(["PJ-3", "PJ-1"], ProcessJobPair.Token));

        Assert.Equal(["PJ-2", "PJ-1"], pair.Manager.Queue);
        pair.AssertAsked(16, 21);
    }

    [Fact]
    public async Task A_host_can_bind_and_add_material()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Host.BindMaterialAsync("PJ-1", ["W09"], ProcessJobPair.Token));

        Assert.Equal("W09", Assert.Single(pair.Job().Material));

        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Host.AddMaterialAsync("PJ-1", ["W10", "W11"], ProcessJobPair.Token));

        Assert.Equal(["W09", "W10", "W11"], pair.Job().Material);

        // Adding one it already has is refused, and the reply says so rather than quietly adding
        // the two it does not.
        Assert.Equal(
            ProcessJobAcknowledgement.Rejected,
            await pair.Host.AddMaterialAsync("PJ-1", ["W12", "W09"], ProcessJobPair.Token));

        Assert.Equal(3, pair.Job().Material.Count);
        pair.AssertAsked(16, 15);
        pair.AssertAsked(16, 25);
    }

    [Fact]
    public async Task A_parameter_change_answers_one_code_per_name()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        var results = await pair.Host.ChangeParametersAsync(
            "PJ-1",
            [
                new ProcessJobParameter { Name = "TEMP", Value = "300" },
                new ProcessJobParameter { Name = "PRESSURE", Value = "1" },
            ],
            ProcessJobPair.Token);

        // Partial success, and the message's own doing: S16F18 carries one CPACK per name, so the
        // one that took is reported beside the one that did not - and the one that did not says
        // which kind of wrong it was.
        Assert.Equal(2, results.Count);
        Assert.Equal(ProcessJobParameterAcknowledgement.Accepted, results[0].Acknowledgement);
        Assert.Equal("TEMP", results[0].Name);
        Assert.Equal(ProcessJobParameterAcknowledgement.NameDoesNotExist, results[1].Acknowledgement);
        Assert.Equal("300", pair.Job().Parameters["TEMP"]);
        pair.AssertAsked(16, 17);
    }

    [Fact]
    public async Task A_host_can_read_a_jobs_parameters()
    {
        await using var pair = ProcessJobPair.Start(manager =>
            Assert.True(manager.Create(
                "PJ-1",
                Recipe,
                parameters: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TEMP"] = "250",
                    ["PRESSURE"] = "1013",
                }).IsAccepted));

        await pair.OpenAsync();

        var all = await pair.Host.GetParametersAsync("PJ-1", cancellationToken: ProcessJobPair.Token);

        // An empty name list asks for all of them, the same reading stream 14 takes of an empty
        // ATTRID list.
        Assert.Equal(2, all.Count);

        var named = await pair.Host.GetParametersAsync("PJ-1", ["PRESSURE", "NOPE"], ProcessJobPair.Token);

        // And a name the job does not have is left out: S16F28 has no code to say which it was,
        // so answering with an empty value would invent a parameter.
        Assert.Equal("PRESSURE", Assert.Single(named).Name);
        Assert.Equal("1013", named[0].Value);
        pair.AssertAsked(16, 27);
    }

    [Fact]
    public async Task A_multi_command_answers_one_code_per_job()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Seed(manager, "PJ-2");

            foreach (var name in new[] { "PJ-1", "PJ-2" })
            {
                Assert.True(manager.BeginSetup(name).IsAccepted);
                Assert.True(manager.SetupComplete(name).IsAccepted);
            }

            Assert.True(manager.MaterialProcessed("PJ-2").IsAccepted);
        });

        await pair.OpenAsync();

        var results = await pair.Host.CommandAsync(
            ["PJ-1", "PJ-2", "PJ-404"],
            ProcessJobCommands.Stop,
            ProcessJobPair.Token);

        Assert.Equal(3, results.Count);
        Assert.Equal(ProcessJobCommandAcknowledgement.Performed, results[0].Acknowledgement);

        // One has already finished and one was never created, and both say "cannot perform now" -
        // nothing is rolled back, because a job that is stopping is stopping.
        Assert.Equal(ProcessJobCommandAcknowledgement.CannotPerformNow, results[1].Acknowledgement);
        Assert.Equal(ProcessJobCommandAcknowledgement.CannotPerformNow, results[2].Acknowledgement);
        Assert.Equal(ProcessJobState.Stopping, pair.Job().State);
        Assert.Equal(ProcessJobState.ProcessComplete, pair.Job("PJ-2").State);
        pair.AssertAsked(16, 23);
    }

    [Fact]
    public async Task The_equipment_reports_a_state_change_with_the_time_it_happened()
    {
        var at = new DateTimeOffset(2026, 11, 16, 19, 2, 6, 120, TimeSpan.Zero);

        await using var pair = ProcessJobPair.Start(manager => Seed(manager), at);
        await pair.OpenAsync();

        ProcessJobStateReport? seen = null;

        pair.Host.JobStateReported += (_, args) => seen = args.Report;

        Assert.True(pair.Manager.BeginSetup("PJ-1").IsAccepted);

        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Equipment.ReportStateAsync("PJ-1", ProcessJobState.SettingUp, ProcessJobPair.Token));

        Assert.NotNull(seen);
        Assert.Equal("PJ-1", seen.ProcessJobId);
        Assert.Equal(ProcessJobState.SettingUp, seen.State);
        Assert.Equal(1, seen.StateCode);

        // The sixteen characters of TIME, round-tripped: E40 is the only one of these four
        // standards with a timestamp on the wire, and this is the test that would catch a format
        // that wrote fourteen.
        Assert.Equal(at.UtcDateTime, seen.At);
    }

    [Fact]
    public async Task A_host_can_refuse_a_state_report_and_the_equipment_hears_it()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        pair.Host.JobStateReported += (_, args) => args.Acknowledgement = ProcessJobAcknowledgement.Rejected;

        Assert.Equal(
            ProcessJobAcknowledgement.Rejected,
            await pair.Equipment.ReportStateAsync("PJ-1", ProcessJobState.Queued, ProcessJobPair.Token));
    }

    [Fact]
    public async Task The_equipment_reports_its_whole_queue()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Seed(manager, "PJ-2");
            Assert.True(manager.BeginSetup("PJ-2").IsAccepted);
            Assert.True(manager.SetupComplete("PJ-2").IsAccepted);
        });

        await pair.OpenAsync();

        IReadOnlyList<ProcessJobQueueEntry> seen = [];

        pair.Host.QueueReported += (_, args) => seen = args.Jobs;

        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Equipment.ReportQueueAsync(ProcessJobPair.Token));

        // Every job, not only the waiting ones: a host that has just connected wants the one that
        // is processing most of all.
        Assert.Equal(2, seen.Count);
        Assert.Contains(seen, entry => entry.ProcessJobId == "PJ-1" && entry.State == ProcessJobState.Queued);
        Assert.Contains(seen, entry => entry.ProcessJobId == "PJ-2" && entry.State == ProcessJobState.Processing);
    }

    [Fact]
    public async Task A_host_that_subscribes_to_nothing_still_answers_both_reports()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        // No handler anywhere, and Accepted is still the right answer - the same shape the GEM
        // package uses for an event report arriving.
        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Equipment.ReportStateAsync("PJ-1", ProcessJobState.Queued, ProcessJobPair.Token));

        Assert.Equal(
            ProcessJobAcknowledgement.Accepted,
            await pair.Equipment.ReportQueueAsync(ProcessJobPair.Token));
    }

    [Fact]
    public async Task A_job_is_described_over_stream_14_and_driven_over_stream_16()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        // The division E40 makes itself: S16F1 carries three items, so everything else a job has
        // is an attribute. Here a host turns on variable tuning and makes the job wait for a start
        // - neither of which stream 16 has a message for.
        Assert.Equal(
            ObjectAcknowledgement.Successful,
            await pair.HostObjects.SetAttributesAsync(
                "PJ-1",
                ProcessJobObjectProvider.ProcessJobType,
                [
                    new ObjectAttribute
                    {
                        Name = "PRRECIPEMETHOD",
                        Value = SecsItem.U1((byte)ProcessJobRecipeMethod.WithVariableTuning),
                    },
                    new ObjectAttribute { Name = "PRPROCESSSTART", Value = SecsItem.Boolean(false) },
                ],
                ProcessJobPair.Token));

        Assert.Equal(ProcessJobRecipeMethod.WithVariableTuning, pair.Job().RecipeMethod);
        Assert.False(pair.Job().StartsAutomatically);

        // Which changes what stream 16 then does: setup ends at WaitingForStart, and a parameter
        // can be tuned while the job runs.
        Assert.True(pair.Manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(pair.Manager.SetupComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.WaitingForStart, pair.Job().State);

        Assert.Equal(
            ProcessJobCommandAcknowledgement.Performed,
            await pair.Host.CommandAsync("PJ-1", ProcessJobCommands.Start, ProcessJobPair.Token));

        var results = await pair.Host.ChangeParametersAsync(
            "PJ-1",
            [new ProcessJobParameter { Name = "TEMP", Value = "300" }],
            ProcessJobPair.Token);

        Assert.Equal(ProcessJobParameterAcknowledgement.Accepted, Assert.Single(results).Acknowledgement);
        Assert.Equal("300", pair.Job().Parameters["TEMP"]);
        pair.AssertAsked(14, 3);
    }

    [Fact]
    public async Task A_state_a_host_tries_to_assign_over_stream_14_is_refused()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        Assert.Equal(
            ObjectAcknowledgement.InvalidAttribute,
            await pair.HostObjects.SetAttributesAsync(
                "PJ-1",
                ProcessJobObjectProvider.ProcessJobType,
                [new ObjectAttribute { Name = "PRSTATE", Value = SecsItem.U1((byte)ProcessJobState.Processing) }],
                ProcessJobPair.Token));

        Assert.Equal(ProcessJobState.Queued, pair.Job().State);
    }

    [Fact]
    public async Task A_host_reads_a_jobs_description_over_stream_14()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Assert.True(manager.SetPauseEvent("PJ-1", 2_500).IsAccepted);
            Assert.True(manager.Describe("PJ-1", materialOrder: ProcessJobMaterialOrder.ListedSequence).IsAccepted);
        });

        await pair.OpenAsync();

        Dictionary<string, string> read = [];

        // Built inside the callback, because that is the only place the values are alive: an
        // attribute value is a view into the reply and the reply is gone when the call returns.
        Assert.True(await pair.HostObjects.GetAttributesAsync(
            "PJ-1",
            ProcessJobObjectProvider.ProcessJobType,
            [],
            values =>
            {
                foreach (var attribute in values.Attributes)
                {
                    read[attribute.Name] = Describe(attribute.Value);
                }
            },
            ProcessJobPair.Token));

        // Every one of these names is a data item the catalogue defines, which is the first time
        // that has been true of a provider in this stack - and the formats are the ones it gives:
        // the state and the two methods U1, the start flag BOOLEAN, the pause event U4.
        Assert.Equal("PJ-1", read["PRJOBID"]);
        Assert.Equal("0", read["PRSTATE"]);
        Assert.Equal(Recipe, read["PPID"]);
        Assert.Equal("1", read["PRRECIPEMETHOD"]);
        Assert.Equal("True", read["PRPROCESSSTART"]);
        Assert.Equal("3", read["PRMTRLORDER"]);
        Assert.Equal("2500", read["PRPAUSEEVENTID"]);
    }

    [Fact]
    public async Task A_create_over_stream_14_is_refused_because_E40_has_its_own()
    {
        await using var pair = ProcessJobPair.Start();
        await pair.OpenAsync();

        var created = await pair.HostObjects.CreateAsync(
            ProcessJobObjectProvider.ProcessJobType,
            cancellationToken: ProcessJobPair.Token);

        // The one place in this stack a provider turns down the actionable half on purpose: S16F1
        // insists on a recipe, and a create over stream 14 would have to invent one.
        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, created.Acknowledgement);
        Assert.Empty(pair.Manager.Jobs);

        Assert.Equal(
            ObjectAcknowledgement.InvalidObjectType,
            await pair.HostObjects.DeleteAsync(
                "PJ-1",
                ProcessJobObjectProvider.ProcessJobType,
                ProcessJobPair.Token));
    }

    [Fact]
    public async Task A_command_can_also_arrive_as_an_object_action()
    {
        await using var pair = ProcessJobPair.Start(manager =>
        {
            Seed(manager);
            Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
            Assert.True(manager.SetupComplete("PJ-1").IsAccepted);
        });

        await pair.OpenAsync();

        // For a host that has stream 14 and not stream 16. It works, and it says less: OBJACK has
        // one code for every no, which is why S16F3 is the better way round.
        Assert.Equal(
            ObjectAcknowledgement.Successful,
            await pair.HostObjects.ActAsync(
                "PJ-1",
                ProcessJobObjectProvider.ProcessJobType,
                ProcessJobCommands.Pause,
                cancellationToken: ProcessJobPair.Token));

        Assert.Equal(ProcessJobState.Pausing, pair.Job().State);

        Assert.Equal(
            ObjectAcknowledgement.Error,
            await pair.HostObjects.ActAsync(
                "PJ-1",
                ProcessJobObjectProvider.ProcessJobType,
                "TELEPORT",
                cancellationToken: ProcessJobPair.Token));

        pair.AssertAsked(14, 9);
    }

    [Fact]
    public async Task Every_primary_the_equipment_declares_can_be_sent_and_the_host_declares_two()
    {
        await using var pair = ProcessJobPair.Start(manager => Seed(manager));
        await pair.OpenAsync();

        await pair.Host.CreateAsync("PJ-2", Recipe, cancellationToken: ProcessJobPair.Token);
        await pair.Host.CommandAsync("PJ-1", ProcessJobCommands.Cancel, ProcessJobPair.Token);
        await pair.Host.GetStatusAsync("PJ-1", ProcessJobPair.Token);
        await pair.Host.ListAsync(ProcessJobPair.Token);
        await pair.Host.DeselectAsync("PJ-1", ProcessJobPair.Token);
        await pair.Host.AddMaterialAsync("PJ-2", ["W01"], ProcessJobPair.Token);
        await pair.Host.ChangeParametersAsync("PJ-2", [], ProcessJobPair.Token);
        await pair.Host.GetQueuePositionAsync("PJ-2", ProcessJobPair.Token);
        await pair.Host.ReorderAsync(["PJ-2"], ProcessJobPair.Token);
        await pair.Host.CommandAsync(["PJ-2"], ProcessJobCommands.Cancel, ProcessJobPair.Token);
        await pair.Host.BindMaterialAsync("PJ-2", ["W01"], ProcessJobPair.Token);
        await pair.Host.GetParametersAsync("PJ-2", cancellationToken: ProcessJobPair.Token);

        foreach (var message in ProcessJobEquipment.HandledMessages)
        {
            Assert.Contains(message, pair.Asked);
        }

        Assert.Equal(12, ProcessJobEquipment.HandledMessages.Count);

        // Two, which is what E40 gives a host - the first host side in this stack to answer
        // anything since stream 3's four.
        Assert.Equal(2, ProcessJobHost.HandledMessages.Count);
        Assert.Contains(((byte)16, (byte)11), ProcessJobHost.HandledMessages);
        Assert.Contains(((byte)16, (byte)29), ProcessJobHost.HandledMessages);
    }

    /// <summary>Prints an attribute value, whatever format it came in.</summary>
    private static string Describe(SecsItem value) => value switch
    {
        { Count: 0 } => string.Empty,
        { Format: SecsFormat.ASCII or SecsFormat.JIS8 } => value.GetString().TrimEnd(),
        { Format: SecsFormat.Boolean } => value.FirstValueOrDefault<bool>().ToString(),
        { Format: SecsFormat.U4 } => value.FirstValueOrDefault<uint>().ToString(CultureInfo.InvariantCulture),
        { Format: SecsFormat.U2 } => value.FirstValueOrDefault<ushort>().ToString(CultureInfo.InvariantCulture),
        _ => value.FirstValueOrDefault<byte>().ToString(CultureInfo.InvariantCulture),
    };
}
