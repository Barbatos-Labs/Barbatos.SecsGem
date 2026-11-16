// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Substrates;

namespace Barbatos.SecsGem.MaterialFlow.UnitTests;

/// <summary>
/// The join between E40's endings and E90's, which are the same words about different things.
/// </summary>
/// <remarks>
/// <para>
/// A stopped job leaves the wafer on the chuck <c>InProcess</c> and the rest of the lot
/// <c>NeedsProcessing</c>, and E90's own rules mean nothing can move them afterwards: work ends
/// only from <c>InProcess</c> and skipping is only from <c>NeedsProcessing</c>. That is the hole
/// this bridge fills, and the half-run lot is the case these tests are mostly about.
/// </para>
/// <para>
/// The other half is what it refuses to do. A completed job with an unprocessed wafer, or a
/// cancelled one with a processed wafer, is a machine telling two stories - and the bridge reports
/// both rather than picking one.
/// </para>
/// </remarks>
public class ProcessJobSubstrateBridgeTests
{
    private const string Recipe = "RECIPE-7";
    private const string Chuck = "CHUCK";
    private const string Slot = "SLOT-1";

    private sealed record Machine(
        ProcessJobManager Jobs,
        SubstrateTracker Substrates,
        ProcessJobSubstrateBridge Bridge);

    /// <summary>A three-wafer job, with the wafers tracked and the first one on the chuck.</summary>
    private static Machine Build(
        Action<ProcessJobSubstrateOptions>? configure = null,
        bool attach = false,
        int wafers = 3,
        bool track = true)
    {
        var jobs = new ProcessJobManager();
        var substrates = new SubstrateTracker();
        var options = new ProcessJobSubstrateOptions();

        configure?.Invoke(options);

        var bridge = new ProcessJobSubstrateBridge(jobs, substrates, options);

        if (attach)
        {
            bridge.Attach();
        }

        substrates.AddLocation(Chuck);
        substrates.AddLocation(Slot);

        List<string> material = [];

        for (var index = 1; index <= wafers; index++)
        {
            var wafer = $"W{index:00}";

            material.Add(wafer);

            if (!track)
            {
                continue;
            }

            substrates.AddLocation($"{Slot}-{index:00}");
            Assert.True(substrates.Add(wafer, $"{Slot}-{index:00}", (byte)index).IsAccepted);
        }

        Assert.True(jobs.Create("PJ-1", Recipe, material).IsAccepted);

        return new Machine(jobs, substrates, bridge);
    }

    /// <summary>Runs the job up to the point where one wafer is on the chuck, being processed.</summary>
    private static void StartWork(Machine machine, string wafer = "W01")
    {
        Assert.True(machine.Jobs.BeginSetup("PJ-1").IsAccepted);
        Assert.True(machine.Jobs.SetupComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.Processing, machine.Jobs.Find("PJ-1")?.State);

        Assert.True(machine.Substrates.MoveTo(wafer, Chuck).IsAccepted);
        Assert.True(machine.Substrates.BeginProcessing(wafer).IsAccepted);
    }

    private static SubstrateProcessingState? StateOf(Machine machine, string wafer)
        => machine.Substrates.FindSubstrate(wafer)?.ProcessingState;

    // ------------------------------------------------------------- the half-run lot

    [Theory]
    [InlineData(ProcessJobCommands.Stop, SubstrateProcessingState.Stopped)]
    [InlineData(ProcessJobCommands.Abort, SubstrateProcessingState.Aborted)]
    public void A_job_that_ends_early_stops_the_work_and_skips_the_rest(string command, SubstrateProcessingState expected)
    {
        var machine = Build();

        StartWork(machine);

        Assert.True(machine.Jobs.Command("PJ-1", command).IsAccepted);
        Assert.True(command == ProcessJobCommands.Stop
            ? machine.Jobs.StopComplete("PJ-1").IsAccepted
            : machine.Jobs.AbortComplete("PJ-1").IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        Assert.True(result.IsAccepted);
        Assert.Equal(3, result.Count);
        Assert.Empty(result.Unexpected);

        // The wafer that was being worked on ends the way the job ended - the one mapping here
        // that needed no choosing, because both standards use the same word.
        Assert.Equal("W01", Assert.Single(result.Ended));
        Assert.Equal(expected, StateOf(machine, "W01"));

        // And the two nobody got to are skipped, which is E90's word for exactly that.
        Assert.Equal(["W02", "W03"], result.Skipped);
        Assert.Equal(SubstrateProcessingState.Skipped, StateOf(machine, "W02"));
        Assert.Equal(SubstrateProcessingState.Skipped, StateOf(machine, "W03"));
    }

    [Fact]
    public void A_cancelled_job_skips_its_whole_lot_and_ends_nothing()
    {
        var machine = Build();

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Cancel).IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        // A cancelled job never processed anything, so there is no work to end - and the wafers
        // are all still waiting, which is precisely what skipping is for.
        Assert.Empty(result.Ended);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Empty(result.Unexpected);
        Assert.All(
            result.Skipped,
            wafer => Assert.Equal(SubstrateProcessingState.Skipped, StateOf(machine, wafer)));
    }

    [Fact]
    public void A_completed_job_leaves_a_properly_processed_lot_alone()
    {
        var machine = Build(wafers: 2);

        Assert.True(machine.Jobs.BeginSetup("PJ-1").IsAccepted);
        Assert.True(machine.Jobs.SetupComplete("PJ-1").IsAccepted);

        // Each wafer to the chuck and back to its own slot, because a location holds one at a
        // time - the first draft of this test sent both back to the same one and found out.
        foreach (var (wafer, slot) in new[] { ("W01", $"{Slot}-01"), ("W02", $"{Slot}-02") })
        {
            Assert.True(machine.Substrates.MoveTo(wafer, Chuck).IsAccepted);
            Assert.True(machine.Substrates.BeginProcessing(wafer).IsAccepted);
            Assert.True(machine.Substrates.EndProcessing(wafer, SubstrateProcessingState.Processed).IsAccepted);
            Assert.True(machine.Substrates.MoveTo(wafer, slot).IsAccepted);
        }

        Assert.True(machine.Jobs.MaterialProcessed("PJ-1").IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        // Nothing to do, and nothing odd about it: every wafer is accounted for.
        Assert.True(result.IsAccepted);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.Unexpected);
        Assert.Equal(SubstrateProcessingState.Processed, StateOf(machine, "W01"));
    }

    // ----------------------------------------------------- the two stories it reports

    [Fact]
    public void A_completed_job_with_a_wafer_still_in_process_is_reported_and_not_tidied()
    {
        var machine = Build(wafers: 1);

        StartWork(machine);

        Assert.True(machine.Jobs.MaterialProcessed("PJ-1").IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        // There is no honest state to end it in: "processed" would be a claim about a recipe
        // nobody made, so the bridge says what it found and changes nothing.
        Assert.True(result.IsAccepted);
        Assert.Equal(0, result.Count);

        var odd = Assert.Single(result.Unexpected);

        Assert.Equal("W01", odd.Key);
        Assert.Contains("InProcess after a Completed job", odd.Value, StringComparison.Ordinal);
        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));
    }

    [Fact]
    public void A_completed_job_with_a_wafer_nobody_touched_is_reported_too()
    {
        var machine = Build(wafers: 2);

        Assert.True(machine.Jobs.BeginSetup("PJ-1").IsAccepted);
        Assert.True(machine.Jobs.SetupComplete("PJ-1").IsAccepted);
        Assert.True(machine.Substrates.MoveTo("W01", Chuck).IsAccepted);
        Assert.True(machine.Substrates.BeginProcessing("W01").IsAccepted);
        Assert.True(machine.Substrates.EndProcessing("W01", SubstrateProcessingState.Processed).IsAccepted);
        Assert.True(machine.Jobs.MaterialProcessed("PJ-1").IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        // Not skipped: a completed job says every piece of its material went through, so a wafer
        // that did not is a contradiction rather than a wafer to tidy away.
        Assert.Empty(result.Skipped);

        var odd = Assert.Single(result.Unexpected);

        Assert.Equal("W02", odd.Key);
        Assert.Contains("never processed under a Completed job", odd.Value, StringComparison.Ordinal);
        Assert.Equal(SubstrateProcessingState.NeedsProcessing, StateOf(machine, "W02"));
    }

    [Fact]
    public void A_cancelled_job_with_a_processed_wafer_is_reported()
    {
        var machine = Build(wafers: 1);

        StartWork(machine);
        Assert.True(machine.Substrates.EndProcessing("W01", SubstrateProcessingState.Processed).IsAccepted);

        // The job is Processing, so it cannot be cancelled - which is E40's own rule. Reaching
        // this state at all takes a machine that processed a wafer under a job it then cancelled
        // from the queue, and the bridge is what notices.
        Assert.True(machine.Jobs.Create("PJ-2", Recipe, ["W01"]).IsAccepted);
        Assert.True(machine.Jobs.Command("PJ-2", ProcessJobCommands.Cancel).IsAccepted);

        var result = machine.Bridge.Settle("PJ-2");

        var odd = Assert.Single(result.Unexpected);

        Assert.Equal("W01", odd.Key);
        Assert.Contains("already Processed under a Cancelled job", odd.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Material_nothing_is_tracking_is_reported_rather_than_invented()
    {
        var machine = Build(track: false);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Cancel).IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        Assert.True(result.IsAccepted);
        Assert.Equal(0, result.Count);
        Assert.Equal(3, result.Unexpected.Count);
        Assert.All(
            result.Unexpected.Values,
            reason => Assert.Contains("nothing is tracking", reason, StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ what it refuses

    [Fact]
    public void A_job_that_has_not_ended_has_nothing_to_settle()
    {
        var machine = Build();

        StartWork(machine);

        var running = machine.Bridge.Settle("PJ-1");

        Assert.False(running.IsAccepted);
        Assert.Contains(nameof(ProcessJobState.Processing), running.Reason, StringComparison.Ordinal);
        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));

        // Not even one that is on its way out: settling a job that is still stopping would end
        // work the machine has not finished stopping.
        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);

        var stopping = machine.Bridge.Settle("PJ-1");

        Assert.False(stopping.IsAccepted);
        Assert.Contains(nameof(ProcessJobState.Stopping), stopping.Reason, StringComparison.Ordinal);
        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));
    }

    [Fact]
    public void A_job_nobody_created_is_refused()
    {
        var machine = Build();

        var result = machine.Bridge.Settle("PJ-404");

        Assert.False(result.IsAccepted);
        Assert.Contains("no process job", result.Reason, StringComparison.Ordinal);

        Assert.False(machine.Bridge.Check("PJ-404").IsAccepted);
    }

    // ---------------------------------------------------------------------- the check

    [Fact]
    public void A_check_says_which_material_nothing_is_tracking()
    {
        var machine = Build(wafers: 2);

        Assert.True(machine.Jobs.Create("PJ-2", Recipe, ["W01", "W09"]).IsAccepted);

        var known = machine.Bridge.Check("PJ-1");

        Assert.True(known.IsAccepted);
        Assert.Empty(known.Unexpected);

        var unknown = machine.Bridge.Check("PJ-2");

        // What a host should call before it tells a machine to set a job up: E40 carries material
        // as identifiers and knows nothing about substrates, so a job can name a wafer nothing is
        // tracking and the machine would process material it cannot account for.
        Assert.True(unknown.IsAccepted);
        Assert.Equal("W09", Assert.Single(unknown.Unexpected).Key);
    }

    [Fact]
    public void A_check_changes_nothing()
    {
        var machine = Build();

        StartWork(machine);

        Assert.True(machine.Bridge.Check("PJ-1").IsAccepted);

        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));
        Assert.Equal(SubstrateProcessingState.NeedsProcessing, StateOf(machine, "W02"));
    }

    // --------------------------------------------------------------------- the options

    [Fact]
    public void Work_in_progress_can_be_left_alone()
    {
        var machine = Build(options => options.EndsWorkInProgress = false);

        StartWork(machine);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);
        Assert.True(machine.Jobs.StopComplete("PJ-1").IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        Assert.Empty(result.Ended);
        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));

        // Reported rather than silently left: the wafer is still a wafer somebody has to account
        // for, and the option changes who does it rather than whether it matters.
        Assert.Contains("told not to end it", result.Unexpected["W01"], StringComparison.Ordinal);

        // And the skipping half is untouched by the choice.
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public void Untouched_wafers_can_be_left_waiting()
    {
        var machine = Build(options => options.SkipsUntouched = false);

        StartWork(machine);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Abort).IsAccepted);
        Assert.True(machine.Jobs.AbortComplete("PJ-1").IsAccepted);

        var result = machine.Bridge.Settle("PJ-1");

        Assert.Equal("W01", Assert.Single(result.Ended));
        Assert.Empty(result.Skipped);
        Assert.Equal(2, result.Unexpected.Count);
        Assert.Contains("told not to skip it", result.Unexpected["W02"], StringComparison.Ordinal);
        Assert.Equal(SubstrateProcessingState.NeedsProcessing, StateOf(machine, "W02"));
    }

    [Fact]
    public void A_bridge_with_no_options_of_its_own_takes_the_defaults()
    {
        var bridge = new ProcessJobSubstrateBridge(new ProcessJobManager(), new SubstrateTracker());

        Assert.True(bridge.Options.EndsWorkInProgress);
        Assert.True(bridge.Options.SkipsUntouched);
        Assert.True(bridge.Options.SettlesWhenTheJobEnds);
        Assert.True(bridge.Options.ChecksMaterialWhenSettingUp);
        Assert.False(bridge.IsAttached);
    }

    // ------------------------------------------------------------------ the attachment

    [Fact]
    public void An_attached_bridge_settles_the_lot_the_moment_the_job_ends()
    {
        var machine = Build(attach: true);

        Assert.True(machine.Bridge.IsAttached);

        StartWork(machine);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);

        // Stopping, not stopped: nothing has happened to the wafers yet.
        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));

        Assert.True(machine.Jobs.StopComplete("PJ-1").IsAccepted);

        // And now the whole lot is accounted for, with nobody having called anything.
        Assert.Equal(SubstrateProcessingState.Stopped, StateOf(machine, "W01"));
        Assert.Equal(SubstrateProcessingState.Skipped, StateOf(machine, "W02"));
        Assert.Equal(SubstrateProcessingState.Skipped, StateOf(machine, "W03"));
    }

    [Fact]
    public void An_attached_bridge_with_the_option_off_waits_to_be_asked()
    {
        var machine = Build(options => options.SettlesWhenTheJobEnds = false, attach: true);

        StartWork(machine);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Abort).IsAccepted);
        Assert.True(machine.Jobs.AbortComplete("PJ-1").IsAccepted);

        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));
        Assert.Single(machine.Bridge.Settle("PJ-1").Ended);
    }

    [Fact]
    public void Attaching_twice_listens_once_and_detaching_stops_it()
    {
        var machine = Build(attach: true);

        machine.Bridge.Attach();

        StartWork(machine);
        machine.Bridge.Detach();

        Assert.False(machine.Bridge.IsAttached);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);
        Assert.True(machine.Jobs.StopComplete("PJ-1").IsAccepted);

        // Detached, so nothing happened by itself - and what it had already settled would have
        // stayed settled, because a wafer whose work ended does not un-end.
        Assert.Equal(SubstrateProcessingState.InProcess, StateOf(machine, "W01"));
    }

    [Fact]
    public void An_attached_bridge_reports_missing_material_when_a_job_is_set_up()
    {
        var machine = Build(attach: true, track: false);
        List<ProcessJobSubstrateResult> reported = [];

        machine.Bridge.MaterialMissing += (_, args) => reported.Add(args.Result);

        Assert.True(machine.Jobs.BeginSetup("PJ-1").IsAccepted);

        // The moment the machine starts bringing material in is the moment it has to exist.
        Assert.Equal(3, Assert.Single(reported).Unexpected.Count);
    }

    [Fact]
    public void A_job_whose_material_is_all_there_reports_nothing_when_it_is_set_up()
    {
        var machine = Build(attach: true);
        var reports = 0;

        machine.Bridge.MaterialMissing += (_, _) => reports++;

        Assert.True(machine.Jobs.BeginSetup("PJ-1").IsAccepted);

        // The normal case, and an event for it would be noise in every lot.
        Assert.Equal(0, reports);
    }

    [Fact]
    public void The_material_check_can_be_turned_off()
    {
        var machine = Build(
            options => options.ChecksMaterialWhenSettingUp = false,
            attach: true,
            track: false);

        var reports = 0;

        machine.Bridge.MaterialMissing += (_, _) => reports++;

        Assert.True(machine.Jobs.BeginSetup("PJ-1").IsAccepted);

        Assert.Equal(0, reports);
    }

    // ----------------------------------------------------------------------- the events

    [Fact]
    public void Settling_is_announced_with_what_happened()
    {
        var machine = Build();
        List<ProcessJobSubstrateResult> settled = [];

        machine.Bridge.Settled += (_, args) => settled.Add(args.Result);

        StartWork(machine);

        Assert.True(machine.Jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);
        Assert.True(machine.Jobs.StopComplete("PJ-1").IsAccepted);
        Assert.True(machine.Bridge.Settle("PJ-1").IsAccepted);

        var result = Assert.Single(settled);

        Assert.Equal("PJ-1", result.ProcessJobId);
        Assert.Single(result.Ended);
        Assert.Equal(2, result.Skipped.Count);
    }

    [Fact]
    public void A_refused_settle_announces_nothing()
    {
        var machine = Build();
        var announcements = 0;

        machine.Bridge.Settled += (_, _) => announcements++;

        Assert.False(machine.Bridge.Settle("PJ-1").IsAccepted);
        Assert.False(machine.Bridge.Settle("PJ-404").IsAccepted);

        Assert.Equal(0, announcements);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var machine = Build();

        Assert.Throws<ArgumentNullException>(
            () => new ProcessJobSubstrateBridge(null!, machine.Substrates));

        Assert.Throws<ArgumentNullException>(
            () => new ProcessJobSubstrateBridge(machine.Jobs, null!));

        Assert.Throws<ArgumentNullException>(() => machine.Bridge.Settle(null!));
        Assert.Throws<ArgumentNullException>(() => machine.Bridge.Check(null!));
    }
}
