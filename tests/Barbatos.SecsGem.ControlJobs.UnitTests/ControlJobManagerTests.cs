// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// E94's state machine, with no connection anywhere near it.
/// </summary>
/// <remarks>
/// <para>
/// A control job has no messages of its own, so almost all of E94 can be tested without a wire -
/// which is the point of keeping the manager free of one. These tests are the standard's rules
/// about what may follow what, and they are the only check those rules get: the catalogue that
/// verifies the message set in this stack has no E94 rows at all.
/// </para>
/// <para>
/// So the discipline here is that every rule is tested from both sides: the transition that is
/// allowed, and the one that is refused with the refusing state named. A state machine tested only
/// where it says yes is a state machine that says yes to everything.
/// </para>
/// </remarks>
public class ControlJobManagerTests
{
    private static ControlJobManager Queued(
        string controlJobId = "CJ-1",
        ControlJobStartMethod startMethod = ControlJobStartMethod.Automatic)
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create(controlJobId, ["CAR-001"], startMethod).IsAccepted);

        return manager;
    }

    private static ControlJobManager Executing(string controlJobId = "CJ-1")
    {
        var manager = Queued(controlJobId);

        Assert.True(manager.Select(controlJobId).IsAccepted);
        Assert.True(manager.MaterialReady(controlJobId).IsAccepted);
        Assert.Equal(ControlJobState.Executing, manager.Find(controlJobId)!.State);

        return manager;
    }

    private static ControlJob Job(ControlJobManager manager, string controlJobId = "CJ-1")
    {
        var job = manager.Find(controlJobId);

        Assert.NotNull(job);

        return job;
    }

    // ------------------------------------------------------------------ creating

    [Fact]
    public void A_new_job_is_queued_and_in_the_queue()
    {
        var manager = Queued();

        var job = Job(manager);

        Assert.Equal(ControlJobState.Queued, job.State);
        Assert.Null(job.Completion);
        Assert.Equal("CAR-001", Assert.Single(job.Carriers));
        Assert.Equal("CJ-1", Assert.Single(manager.Queue));

        // Nothing is current until something is selected, which is what makes the queue a queue.
        Assert.Null(manager.Current);
        Assert.False(job.IsActive);
        Assert.False(job.IsFinished);
    }

    [Fact]
    public void A_job_remembers_everything_it_was_created_with()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create(
            "CJ-7",
            ["CAR-001", "CAR-002"],
            ControlJobStartMethod.HostInitiated,
            "FIFO",
            "RECIPE-A").IsAccepted);

        var job = Job(manager, "CJ-7");

        Assert.Equal(2, job.Carriers.Count);
        Assert.Equal(ControlJobStartMethod.HostInitiated, job.StartMethod);
        Assert.Equal("FIFO", job.ProcessOrder);
        Assert.Equal("RECIPE-A", job.ProcessingSpecification);
    }

    [Fact]
    public void The_same_name_twice_is_refused_and_the_first_job_is_untouched()
    {
        var manager = Queued();

        Assert.True(manager.Select("CJ-1").IsAccepted);

        var refusal = manager.Create("CJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("already exists", refusal.Reason, StringComparison.Ordinal);

        // And the second create has not reset the first job to Queued, which is the fault this
        // test exists for: a create that overwrote would un-select a running machine's job.
        Assert.Equal(ControlJobState.Selected, Job(manager).State);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public void A_job_with_no_name_is_refused()
    {
        var manager = new ControlJobManager();

        var refusal = manager.Create(string.Empty);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("identifier", refusal.Reason, StringComparison.Ordinal);
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public void A_job_created_with_no_carriers_has_none_rather_than_null()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create("CJ-1").IsAccepted);
        Assert.Empty(Job(manager).Carriers);
    }

    [Fact]
    public void Null_arguments_are_rejected_rather_than_stored()
    {
        var manager = new ControlJobManager();

        Assert.Throws<ArgumentNullException>(() => manager.Create(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Find(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Delete(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Describe(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Reorder(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Select(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Command(null!, ControlJobCommands.Start));
        Assert.Throws<ArgumentNullException>(() => manager.Command("CJ-1", null!));
    }

    [Fact]
    public void A_job_nobody_created_cannot_be_anything()
    {
        var manager = new ControlJobManager();

        Assert.Null(manager.Find("CJ-404"));

        foreach (var result in new[]
        {
            manager.Select("CJ-404"),
            manager.Deselect("CJ-404"),
            manager.MaterialReady("CJ-404"),
            manager.Start("CJ-404"),
            manager.Pause("CJ-404"),
            manager.Resume("CJ-404"),
            manager.MaterialFinished("CJ-404"),
            manager.Delete("CJ-404"),
            manager.Describe("CJ-404", processOrder: "FIFO"),
            manager.Command("CJ-404", ControlJobCommands.Abort),
        })
        {
            Assert.False(result.IsAccepted);
            Assert.Contains("no control job", result.Reason, StringComparison.Ordinal);
        }
    }

    // ----------------------------------------------------------------- selecting

    [Fact]
    public void Selecting_takes_the_job_out_of_the_queue_and_makes_it_current()
    {
        var manager = Queued();

        Assert.True(manager.Select("CJ-1").IsAccepted);

        Assert.Equal(ControlJobState.Selected, Job(manager).State);
        Assert.Empty(manager.Queue);
        Assert.Same(Job(manager), manager.Current);
    }

    [Fact]
    public void Only_one_job_can_be_selected_at_a_time()
    {
        var manager = Queued();

        Assert.True(manager.Create("CJ-2").IsAccepted);
        Assert.True(manager.Select("CJ-1").IsAccepted);

        var refusal = manager.Select("CJ-2");

        // The rule worth having: two selected jobs are two things believing they own the material.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("CJ-1", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Queued, Job(manager, "CJ-2").State);
        Assert.Equal("CJ-2", Assert.Single(manager.Queue));
    }

    [Fact]
    public void A_job_that_has_been_selected_cannot_be_selected_again()
    {
        var manager = Queued();

        Assert.True(manager.Select("CJ-1").IsAccepted);

        var refusal = manager.Select("CJ-1");

        Assert.False(refusal.IsAccepted);

        // The state refuses it, and the whole message is asserted on purpose: an already-selected
        // job falls foul of the one-at-a-time rule as well, so a test happy with either refusal
        // could not tell the two guards apart - and would go on passing with this one deleted.
        Assert.Equal(
            $"control job 'CJ-1' is {ControlJobState.Selected}; "
            + $"only a {ControlJobState.Queued} one can be selected.",
            refusal.Reason);
    }

    [Fact]
    public void Once_a_job_is_over_the_next_one_can_be_selected()
    {
        var manager = Executing();

        Assert.True(manager.Create("CJ-2").IsAccepted);
        Assert.False(manager.Select("CJ-2").IsAccepted);

        Assert.True(manager.MaterialFinished("CJ-1").IsAccepted);

        // Which is the whole reason Completed has to be reachable: a machine whose finished job
        // still counted as current would run one lot and stop forever.
        Assert.True(manager.Select("CJ-2").IsAccepted);
        Assert.Same(Job(manager, "CJ-2"), manager.Current);
    }

    [Fact]
    public void Deselecting_puts_the_job_back_at_the_front_of_the_queue()
    {
        var manager = Queued();

        Assert.True(manager.Create("CJ-2").IsAccepted);
        Assert.True(manager.Select("CJ-1").IsAccepted);
        Assert.Equal("CJ-2", Assert.Single(manager.Queue));

        Assert.True(manager.Deselect("CJ-1").IsAccepted);

        // At the front, because it was next a moment ago - a deselect is not a demotion.
        Assert.Equal(["CJ-1", "CJ-2"], manager.Queue);
        Assert.Equal(ControlJobState.Queued, Job(manager).State);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void A_job_waiting_for_a_start_can_still_be_deselected()
    {
        var manager = Queued(startMethod: ControlJobStartMethod.HostInitiated);

        Assert.True(manager.Select("CJ-1").IsAccepted);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.WaitingForStart, Job(manager).State);

        Assert.True(manager.Deselect("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Queued, Job(manager).State);
    }

    [Fact]
    public void A_running_job_cannot_be_deselected()
    {
        var manager = Executing();

        var refusal = manager.Deselect("CJ-1");

        // It is stopped or aborted instead: deselecting would leave material half processed with
        // nothing recording why.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Executing), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
        Assert.Empty(manager.Queue);
    }

    [Fact]
    public void A_queued_job_cannot_be_deselected_because_it_is_not_selected()
    {
        var manager = Queued();

        Assert.False(manager.Deselect("CJ-1").IsAccepted);

        // And it has not been added to the queue a second time, which an unguarded insert would do.
        Assert.Equal("CJ-1", Assert.Single(manager.Queue));
    }

    // ------------------------------------------------------------------- running

    [Fact]
    public void An_automatic_job_starts_itself_when_its_material_is_ready()
    {
        var manager = Queued();

        Assert.True(manager.Select("CJ-1").IsAccepted);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);

        Assert.Equal(ControlJobState.Executing, Job(manager).State);
        Assert.True(Job(manager).IsActive);
    }

    [Fact]
    public void A_host_initiated_job_waits_for_the_host()
    {
        var manager = Queued(startMethod: ControlJobStartMethod.HostInitiated);

        Assert.True(manager.Select("CJ-1").IsAccepted);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);

        // The one place StartMethod decides anything, and the reason the enum exists.
        Assert.Equal(ControlJobState.WaitingForStart, Job(manager).State);

        Assert.True(manager.Start("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
    }

    [Fact]
    public void Material_can_only_become_ready_for_a_selected_job()
    {
        var manager = Queued();

        var refusal = manager.MaterialReady("CJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Queued), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Queued, Job(manager).State);
    }

    [Fact]
    public void Only_a_job_waiting_for_a_start_can_be_started()
    {
        var manager = Queued();

        Assert.False(manager.Start("CJ-1").IsAccepted);

        Assert.True(manager.Select("CJ-1").IsAccepted);

        var refusal = manager.Start("CJ-1");

        // Selected is not WaitingForStart: the material is still being brought in, and a start
        // here would run a job over carriers that have not arrived.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Selected), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Selected, Job(manager).State);
    }

    [Fact]
    public void An_executing_job_cannot_be_started_again()
    {
        var manager = Executing();

        Assert.False(manager.Start("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
    }

    [Fact]
    public void Pause_and_resume_go_back_and_forth()
    {
        var manager = Executing();

        Assert.True(manager.Pause("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Paused, Job(manager).State);

        // A paused job still owns its material, which is what IsActive is for.
        Assert.True(Job(manager).IsActive);

        Assert.True(manager.Resume("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
    }

    [Fact]
    public void Only_an_executing_job_can_be_paused_and_only_a_paused_one_resumed()
    {
        var manager = Queued();

        Assert.False(manager.Pause("CJ-1").IsAccepted);
        Assert.False(manager.Resume("CJ-1").IsAccepted);

        Assert.True(manager.Select("CJ-1").IsAccepted);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);

        var refusal = manager.Resume("CJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Executing), refusal.Reason, StringComparison.Ordinal);

        Assert.True(manager.Pause("CJ-1").IsAccepted);
        Assert.False(manager.Pause("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Paused, Job(manager).State);
    }

    // ------------------------------------------------------------------- endings

    [Fact]
    public void A_job_that_works_through_its_material_completes()
    {
        var manager = Executing();

        Assert.True(manager.MaterialFinished("CJ-1").IsAccepted);

        var job = Job(manager);

        Assert.Equal(ControlJobState.Completed, job.State);
        Assert.Equal(ControlJobCompletion.Completed, job.Completion);
        Assert.True(job.IsFinished);
        Assert.False(job.IsActive);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void A_paused_job_has_not_finished()
    {
        var manager = Executing();

        Assert.True(manager.Pause("CJ-1").IsAccepted);

        var refusal = manager.MaterialFinished("CJ-1");

        // Calling this a completion would tell a host the lot is done when it is sitting still.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Paused), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Paused, Job(manager).State);
    }

    [Theory]
    [InlineData(ControlJobCommands.Abort, ControlJobCompletion.Aborted)]
    [InlineData(ControlJobCommands.Stop, ControlJobCompletion.Stopped)]
    public void A_running_job_can_be_aborted_or_stopped(string command, ControlJobCompletion expected)
    {
        var manager = Executing();

        Assert.True(manager.Command("CJ-1", command).IsAccepted);

        var job = Job(manager);

        Assert.Equal(ControlJobState.Completed, job.State);
        Assert.Equal(expected, job.Completion);
    }

    [Theory]
    [InlineData(ControlJobCommands.Abort)]
    [InlineData(ControlJobCommands.Stop)]
    public void A_paused_job_can_be_aborted_or_stopped(string command)
    {
        var manager = Executing();

        Assert.True(manager.Pause("CJ-1").IsAccepted);
        Assert.True(manager.Command("CJ-1", command).IsAccepted);
        Assert.Equal(ControlJobState.Completed, Job(manager).State);
    }

    [Fact]
    public void A_job_that_has_not_run_is_cancelled_rather_than_aborted()
    {
        var manager = Queued();

        var refusal = manager.Command("CJ-1", ControlJobCommands.Abort);

        // E94 keeping the record honest: an aborted job touched material, a cancelled one did not,
        // and a machine that reported the wrong one would have a host chasing wafers that were
        // never loaded.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("cancel it rather than aborting", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Queued, Job(manager).State);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Cancel).IsAccepted);
        Assert.Equal(ControlJobCompletion.Cancelled, Job(manager).Completion);
    }

    [Fact]
    public void A_job_that_has_run_cannot_be_cancelled()
    {
        var manager = Executing();

        var refusal = manager.Command("CJ-1", ControlJobCommands.Cancel);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("abort or stop it", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
    }

    [Theory]
    [InlineData(ControlJobState.Queued)]
    [InlineData(ControlJobState.Selected)]
    [InlineData(ControlJobState.WaitingForStart)]
    public void Cancel_works_from_every_state_before_a_job_runs(ControlJobState from)
    {
        var manager = Queued(startMethod: ControlJobStartMethod.HostInitiated);

        if (from is not ControlJobState.Queued)
        {
            Assert.True(manager.Select("CJ-1").IsAccepted);
        }

        if (from is ControlJobState.WaitingForStart)
        {
            Assert.True(manager.MaterialReady("CJ-1").IsAccepted);
        }

        Assert.Equal(from, Job(manager).State);
        Assert.True(manager.Command("CJ-1", ControlJobCommands.Cancel).IsAccepted);

        Assert.Equal(ControlJobState.Completed, Job(manager).State);
        Assert.Equal(ControlJobCompletion.Cancelled, Job(manager).Completion);
        Assert.Empty(manager.Queue);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void Nothing_at_all_comes_out_of_completed()
    {
        var manager = Executing();

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Abort).IsAccepted);

        foreach (var result in new[]
        {
            manager.Select("CJ-1"),
            manager.Deselect("CJ-1"),
            manager.MaterialReady("CJ-1"),
            manager.Start("CJ-1"),
            manager.Pause("CJ-1"),
            manager.Resume("CJ-1"),
            manager.MaterialFinished("CJ-1"),
            manager.Describe("CJ-1", processOrder: "FIFO"),
            manager.Command("CJ-1", ControlJobCommands.Abort),
            manager.Command("CJ-1", ControlJobCommands.Stop),
            manager.Command("CJ-1", ControlJobCommands.Cancel),
        })
        {
            Assert.False(result.IsAccepted);
        }

        // And the ending it already had is the ending it keeps: a second abort must not rewrite
        // the reason a job ended.
        Assert.Equal(ControlJobCompletion.Aborted, Job(manager).Completion);
    }

    // ------------------------------------------------------------------ deleting

    [Fact]
    public void A_finished_job_can_be_deleted_and_its_name_reused()
    {
        var manager = Executing();

        Assert.True(manager.MaterialFinished("CJ-1").IsAccepted);
        Assert.True(manager.Delete("CJ-1").IsAccepted);

        Assert.Empty(manager.Jobs);
        Assert.Null(manager.Find("CJ-1"));

        // Which is what makes a long-running machine possible: the names come back.
        Assert.True(manager.Create("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.Queued, Job(manager).State);
    }

    [Theory]
    [InlineData(ControlJobState.Queued)]
    [InlineData(ControlJobState.Selected)]
    [InlineData(ControlJobState.Executing)]
    [InlineData(ControlJobState.Paused)]
    public void A_job_that_is_not_over_cannot_be_deleted(ControlJobState from)
    {
        var manager = Queued();

        if (from is not ControlJobState.Queued)
        {
            Assert.True(manager.Select("CJ-1").IsAccepted);
        }

        if (from is ControlJobState.Executing or ControlJobState.Paused)
        {
            Assert.True(manager.MaterialReady("CJ-1").IsAccepted);
        }

        if (from is ControlJobState.Paused)
        {
            Assert.True(manager.Pause("CJ-1").IsAccepted);
        }

        Assert.Equal(from, Job(manager).State);

        var refusal = manager.Delete("CJ-1");

        // Deleting a live job would leave its material with nothing to account for it.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("only a finished one", refusal.Reason, StringComparison.Ordinal);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public void Deleting_a_queued_job_leaves_it_in_the_queue()
    {
        var manager = Queued();

        Assert.False(manager.Delete("CJ-1").IsAccepted);

        // The half-done delete this guards against: gone from the queue, still in the dictionary.
        Assert.Equal("CJ-1", Assert.Single(manager.Queue));
    }

    // ------------------------------------------------------------------ the queue

    [Fact]
    public void The_queue_keeps_the_order_jobs_were_created_in()
    {
        var manager = new ControlJobManager();

        foreach (var name in new[] { "CJ-1", "CJ-2", "CJ-3" })
        {
            Assert.True(manager.Create(name).IsAccepted);
        }

        Assert.Equal(["CJ-1", "CJ-2", "CJ-3"], manager.Queue);
    }

    [Fact]
    public void A_host_can_reorder_the_queue()
    {
        var manager = new ControlJobManager();

        foreach (var name in new[] { "CJ-1", "CJ-2", "CJ-3" })
        {
            Assert.True(manager.Create(name).IsAccepted);
        }

        Assert.True(manager.Reorder(["CJ-3", "CJ-1", "CJ-2"]).IsAccepted);
        Assert.Equal(["CJ-3", "CJ-1", "CJ-2"], manager.Queue);

        // And the order is the order: the next select takes the front of it.
        Assert.True(manager.Select("CJ-3").IsAccepted);
        Assert.Equal(["CJ-1", "CJ-2"], manager.Queue);
    }

    [Fact]
    public void A_reorder_that_leaves_a_job_out_is_refused()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create("CJ-1").IsAccepted);
        Assert.True(manager.Create("CJ-2").IsAccepted);

        var refusal = manager.Reorder(["CJ-1"]);

        // A reorder that dropped a job would be a deletion wearing a reorder's clothes.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("names 1", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(["CJ-1", "CJ-2"], manager.Queue);
    }

    [Fact]
    public void A_reorder_that_names_a_job_twice_is_refused()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create("CJ-1").IsAccepted);
        Assert.True(manager.Create("CJ-2").IsAccepted);

        var refusal = manager.Reorder(["CJ-1", "CJ-1"]);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("twice", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(["CJ-1", "CJ-2"], manager.Queue);
    }

    [Fact]
    public void A_reorder_that_names_the_selected_job_is_refused()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create("CJ-1").IsAccepted);
        Assert.True(manager.Create("CJ-2").IsAccepted);
        Assert.True(manager.Select("CJ-1").IsAccepted);

        var refusal = manager.Reorder(["CJ-1"]);

        // A selected job is not in the queue, and a reorder is not a way back into it.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("not in the queue", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal("CJ-2", Assert.Single(manager.Queue));
        Assert.Equal(ControlJobState.Selected, Job(manager).State);
    }

    // --------------------------------------------------------------- describing

    [Fact]
    public void A_queued_job_can_be_changed()
    {
        var manager = Queued();

        Assert.True(manager.Describe(
            "CJ-1",
            carriers: ["CAR-009", "CAR-010"],
            startMethod: ControlJobStartMethod.HostInitiated,
            processOrder: "SEQUENTIAL",
            processingSpecification: "RECIPE-B").IsAccepted);

        var job = Job(manager);

        Assert.Equal(["CAR-009", "CAR-010"], job.Carriers);
        Assert.Equal(ControlJobStartMethod.HostInitiated, job.StartMethod);
        Assert.Equal("SEQUENTIAL", job.ProcessOrder);
        Assert.Equal("RECIPE-B", job.ProcessingSpecification);
    }

    [Fact]
    public void Describing_changes_only_what_it_names()
    {
        var manager = Queued();

        Assert.True(manager.Describe("CJ-1", processOrder: "FIFO").IsAccepted);

        var job = Job(manager);

        Assert.Equal("FIFO", job.ProcessOrder);

        // The null arguments are "leave it", not "clear it", which is the difference between a
        // host setting one attribute and a host wiping a job's material by naming one.
        Assert.Equal("CAR-001", Assert.Single(job.Carriers));
        Assert.Equal(ControlJobStartMethod.Automatic, job.StartMethod);
        Assert.Equal(string.Empty, job.ProcessingSpecification);
    }

    [Fact]
    public void A_job_that_has_been_selected_cannot_be_changed()
    {
        var manager = Queued();

        Assert.True(manager.Select("CJ-1").IsAccepted);

        var refusal = manager.Describe("CJ-1", carriers: ["CAR-009"]);

        // Changing the material under a job whose carriers are being brought in is the kind of
        // edit that loses wafers.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Selected), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal("CAR-001", Assert.Single(Job(manager).Carriers));
    }

    [Fact]
    public void The_carriers_a_job_was_given_are_copied_rather_than_held()
    {
        var manager = new ControlJobManager();
        List<string> carriers = ["CAR-001"];

        Assert.True(manager.Create("CJ-1", carriers).IsAccepted);

        carriers.Add("CAR-002");

        // A job whose carrier list was the caller's list would change under the machine whenever
        // the application touched its own collection.
        Assert.Equal("CAR-001", Assert.Single(Job(manager).Carriers));

        Assert.True(manager.Describe("CJ-1", carriers: carriers).IsAccepted);
        carriers.Clear();

        Assert.Equal(2, Job(manager).Carriers.Count);
    }

    // ------------------------------------------------------------------ commands

    [Fact]
    public void Every_command_name_reaches_the_transition_it_names()
    {
        var manager = Queued(startMethod: ControlJobStartMethod.HostInitiated);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Select).IsAccepted);
        Assert.Equal(ControlJobState.Selected, Job(manager).State);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Deselect).IsAccepted);
        Assert.Equal(ControlJobState.Queued, Job(manager).State);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Select).IsAccepted);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Start).IsAccepted);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Pause).IsAccepted);
        Assert.Equal(ControlJobState.Paused, Job(manager).State);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Resume).IsAccepted);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Stop).IsAccepted);
        Assert.Equal(ControlJobCompletion.Stopped, Job(manager).Completion);
    }

    [Fact]
    public void A_command_nobody_defined_is_refused_rather_than_thrown()
    {
        var manager = Executing();

        var refusal = manager.Command("CJ-1", "Teleport");

        // RCMD is free text, so a host is entitled to ask for something this library has never
        // heard of - and entitled to a refusal rather than an exception in a message handler.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("Teleport", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
    }

    [Fact]
    public void A_command_is_matched_exactly_rather_than_loosely()
    {
        var manager = Executing();

        // Not "pause", not "PAUSE": RCMD is a wire value, and a machine that accepted any casing
        // would be guessing at a protocol.
        Assert.False(manager.Command("CJ-1", "pause").IsAccepted);
        Assert.False(manager.Command("CJ-1", " Pause").IsAccepted);
        Assert.Equal(ControlJobState.Executing, Job(manager).State);
    }

    // -------------------------------------------------------------------- events

    [Fact]
    public void Every_move_is_announced_with_both_states()
    {
        var manager = Queued();
        List<ControlJobStateChangedEventArgs> seen = [];

        manager.JobStateChanged += (_, args) => seen.Add(args);

        Assert.True(manager.Select("CJ-1").IsAccepted);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);
        Assert.True(manager.Pause("CJ-1").IsAccepted);
        Assert.True(manager.Command("CJ-1", ControlJobCommands.Abort).IsAccepted);

        Assert.Equal(4, seen.Count);

        Assert.Equal(
            [
                (ControlJobState.Queued, ControlJobState.Selected),
                (ControlJobState.Selected, ControlJobState.Executing),
                (ControlJobState.Executing, ControlJobState.Paused),
                (ControlJobState.Paused, ControlJobState.Completed),
            ],
            seen.Select(args => (args.Previous, args.Current)));

        // The completion rides along on the one transition that is an ending, and on no other:
        // that is what lets a handler publish E94's job-completed event without a second lookup.
        Assert.All(seen.Take(3), args => Assert.Null(args.Completion));
        Assert.Equal(ControlJobCompletion.Aborted, seen[3].Completion);
        Assert.All(seen, args => Assert.Equal("CJ-1", args.Job.ControlJobId));
    }

    [Fact]
    public void A_handler_sees_the_state_the_event_announces()
    {
        var manager = Queued();
        ControlJobState? asRead = null;
        ControlJobCompletion? completionAsRead = null;

        manager.JobStateChanged += (_, args) =>
        {
            if (args.Current is ControlJobState.Completed)
            {
                asRead = args.Job.State;
                completionAsRead = args.Job.Completion;
            }
        };

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Cancel).IsAccepted);

        // Raised after the move, not before: a handler that read the job and saw Queued would
        // publish a report saying the job is queued at the moment it ended.
        Assert.Equal(ControlJobState.Completed, asRead);
        Assert.Equal(ControlJobCompletion.Cancelled, completionAsRead);
    }

    [Fact]
    public void A_refused_command_announces_nothing()
    {
        var manager = Queued();
        var moves = 0;

        manager.JobStateChanged += (_, _) => moves++;

        Assert.False(manager.Start("CJ-1").IsAccepted);
        Assert.False(manager.Pause("CJ-1").IsAccepted);
        Assert.False(manager.Command("CJ-1", ControlJobCommands.Abort).IsAccepted);
        Assert.False(manager.Delete("CJ-1").IsAccepted);
        Assert.False(manager.Command("CJ-1", "Teleport").IsAccepted);

        Assert.Equal(0, moves);
    }

    [Fact]
    public void Describing_a_job_announces_nothing_because_it_is_not_a_move()
    {
        var manager = Queued();
        var moves = 0;

        manager.JobStateChanged += (_, _) => moves++;

        Assert.True(manager.Describe("CJ-1", processOrder: "FIFO").IsAccepted);

        Assert.Equal(0, moves);
    }

    // ----------------------------------------------------------------- the lock

    [Fact]
    public void Two_threads_racing_to_select_produce_one_selected_job()
    {
        var manager = new ControlJobManager();
        const int Jobs = 32;

        for (var index = 0; index < Jobs; index++)
        {
            Assert.True(manager.Create($"CJ-{index}").IsAccepted);
        }

        var accepted = 0;

        Parallel.For(0, Jobs, index =>
        {
            if (manager.Select($"CJ-{index}").IsAccepted)
            {
                Interlocked.Increment(ref accepted);
            }
        });

        // One lock around every transition, and this is what it buys: "a machine runs one control
        // job at a time" is a rule, not a hope, even when a host and the machine's own scheduler
        // ask at the same moment.
        Assert.Equal(1, accepted);
        Assert.NotNull(manager.Current);
        Assert.Equal(Jobs - 1, manager.Queue.Count);
        Assert.Equal(Jobs, manager.Jobs.Count);
    }

    [Fact]
    public void The_collections_a_caller_gets_are_snapshots()
    {
        var manager = Queued();

        var jobs = manager.Jobs;
        var queue = manager.Queue;

        Assert.True(manager.Create("CJ-2").IsAccepted);

        // Copies, so an application iterating them while the machine moves on does not get an
        // exception out of a foreach it never wrote a lock for.
        Assert.Single(jobs);
        Assert.Single(queue);
        Assert.Equal(2, manager.Jobs.Count);
        Assert.Equal(2, manager.Queue.Count);
    }
}
