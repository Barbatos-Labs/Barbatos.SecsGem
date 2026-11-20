// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// A queue that outlives the process, on the same rule E94's follows - plus one of its own, about
/// a machine that has been reconfigured since.
/// </summary>
/// <remarks>
/// A job's definition is a host instruction and comes back whole: recipe, method, material,
/// parameters, order, the event that pauses it, and the order of the queue, which E40 lets a host
/// set. A job that was in flight ends at E40's one terminal state, with the reason saying whether
/// any material went through. And a snapshot does not raise the job space, because the limit is
/// the machine's statement about itself.
/// </remarks>
public class ProcessJobStateTests
{
    private const string First = "PJ-1";
    private const string Second = "PJ-2";
    private const string Third = "PJ-3";

    private static ProcessJobManager WithThreeQueuedJobs(ushort? space = null)
    {
        var manager = new ProcessJobManager { JobSpace = space };

        Assert.True(manager.Create(First, "RECIPE-7", ["W01", "W02"]).IsAccepted);
        Assert.True(manager.Create(Second, "RECIPE-8", ["W03"]).IsAccepted);
        Assert.True(manager.Create(Third, "RECIPE-9", ["W04"]).IsAccepted);

        return manager;
    }

    [Fact]
    public void A_jobs_definition_and_the_queue_order_come_back()
    {
        var manager = WithThreeQueuedJobs();

        var restarted = new ProcessJobManager();
        var report = restarted.Restore(manager.Capture());

        Assert.True(report.IsAccepted);
        Assert.Equal(3, report.Count);
        Assert.Equal((string[])[First, Second, Third], restarted.Queue);

        var job = restarted.Find(First)!;

        Assert.Equal(ProcessJobState.Queued, job.State);
        Assert.Equal("RECIPE-7", job.RecipeId);
        Assert.Equal((string[])["W01", "W02"], job.Material);
    }

    [Fact]
    public void The_parameters_a_host_set_come_back_too()
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create(
            First,
            "RECIPE-7",
            ["W01"],
            recipeMethod: ProcessJobRecipeMethod.WithVariableTuning,
            pauseEventId: 7_001u,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["PRESSURE"] = "1000" }).IsAccepted);

        Assert.True(manager.ChangeParameter(First, "PRESSURE", "1013").IsAccepted);

        var restarted = new ProcessJobManager();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        var job = restarted.Find(First)!;

        // The recipe tuning a host asked for, the value it last set, and the event it chose to
        // pause on: all three are instructions, and all three survive.
        Assert.Equal("1013", job.Parameters["PRESSURE"]);
        Assert.Equal(ProcessJobRecipeMethod.WithVariableTuning, job.RecipeMethod);
        Assert.Equal(7_001u, job.PauseEventId);
    }

    [Fact]
    public void A_job_that_was_only_being_set_up_processed_nothing()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.BeginSetup(First).IsAccepted);
        Assert.Equal(ProcessJobState.SettingUp, manager.Find(First)!.State);

        var restarted = new ProcessJobManager();
        var report = restarted.Restore(manager.Capture());
        var job = restarted.Find(First)!;

        Assert.Equal(ProcessJobState.ProcessComplete, job.State);
        Assert.Equal(ProcessJobCompletion.Cancelled, job.Completion);
        Assert.Contains("not processing anything", report.Adjusted[First], StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_that_was_processing_was_aborted()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.BeginSetup(First).IsAccepted);
        Assert.True(manager.SetupComplete(First).IsAccepted);
        Assert.Equal(ProcessJobState.Processing, manager.Find(First)!.State);

        var restarted = new ProcessJobManager();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        var job = restarted.Find(First)!;

        // Aborted rather than cancelled, because material went through and the record has to say
        // so: a host that read "cancelled" would expect untouched wafers.
        Assert.Equal(ProcessJobState.ProcessComplete, job.State);
        Assert.Equal(ProcessJobCompletion.Aborted, job.Completion);
    }

    [Fact]
    public void A_job_that_had_already_finished_keeps_the_ending_it_recorded()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.BeginSetup(First).IsAccepted);
        Assert.True(manager.SetupComplete(First).IsAccepted);
        Assert.True(manager.MaterialProcessed(First).IsAccepted);

        var expected = manager.Find(First)!.Completion;

        var restarted = new ProcessJobManager();
        var report = restarted.Restore(manager.Capture());

        Assert.Equal(ProcessJobState.ProcessComplete, restarted.Find(First)!.State);
        Assert.Equal(expected, restarted.Find(First)!.Completion);
        Assert.DoesNotContain(First, report.Adjusted.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// A machine reconfigured downwards between runs, which is the one rule this package's restore
    /// has that E94's does not.
    /// </summary>
    [Fact]
    public void A_snapshot_does_not_raise_the_job_space()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Reorder([Third, Second, First]).IsAccepted);

        var restarted = new ProcessJobManager { JobSpace = 2 };
        var report = restarted.Restore(manager.Capture());

        Assert.True(report.IsAccepted);
        Assert.Equal(2, report.Count);

        // The queue order decides which two survive, so the jobs a host put first are the jobs
        // that are kept rather than whichever the dictionary happened to yield.
        Assert.Equal((string[])[Third, Second], restarted.Queue);
        Assert.Null(restarted.Find(First));
        Assert.Contains("job space", report.Adjusted[First], StringComparison.Ordinal);
    }

    [Fact]
    public void A_manager_that_already_has_jobs_refuses()
    {
        var manager = WithThreeQueuedJobs();
        var busy = new ProcessJobManager();

        Assert.True(busy.Create("PJ-9", "RECIPE-1", ["W09"]).IsAccepted);

        var report = busy.Restore(manager.Capture());

        Assert.False(report.IsAccepted);
        Assert.Contains("already has", report.Reason, StringComparison.Ordinal);
        Assert.Null(busy.Find(First));
    }

    [Fact]
    public void Restoring_raises_nothing()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.BeginSetup(First).IsAccepted);
        Assert.True(manager.SetupComplete(First).IsAccepted);

        var restarted = new ProcessJobManager();
        var moves = 0;

        restarted.JobStateChanged += (_, _) => moves++;

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        // Which matters more here than anywhere else in this chapter: the E40 sample wires
        // JobStateChanged to an S16F11, so a raised transition would put a report on the wire
        // telling a host the machine had just aborted a job it never started.
        Assert.Equal(0, moves);
    }

    [Fact]
    public void The_job_space_is_not_in_the_snapshot_at_all()
    {
        var manager = WithThreeQueuedJobs(space: 8);
        var restarted = new ProcessJobManager { JobSpace = 4 };

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        // How many jobs a machine can hold is its own configuration, not something a host set, so
        // a snapshot that carried it could put a restarted machine's limit back to whatever it was
        // two upgrades ago.
        Assert.Equal((ushort?)4, restarted.JobSpace);
    }

    [Fact]
    public void An_empty_manager_takes_a_snapshot_of_nothing()
    {
        var report = new ProcessJobManager().Restore(new ProcessJobManagerSnapshot());

        Assert.True(report.IsAccepted);
        Assert.Empty(report.Restored);
    }

    [Fact]
    public void A_snapshot_nobody_passed_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => new ProcessJobManager().Restore(null!));
}
