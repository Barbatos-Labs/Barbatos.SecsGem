// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// E40's state machine, with no connection anywhere near it.
/// </summary>
/// <remarks>
/// <para>
/// E40 has a stream of its own, unlike E90 and E94, and the sequencing still lives in a class that
/// has never heard of one - so this is where most of the standard is checked. The nine states and
/// their numbers come from <c>PRSTATE</c>; the order they may come in does not, and these tests
/// are the only thing that holds it.
/// </para>
/// <para>
/// Every rule is tested from both sides: the transition that is allowed, and the one that is
/// refused with the refusing state named. A state machine tested only where it says yes is a state
/// machine that says yes to everything.
/// </para>
/// </remarks>
public class ProcessJobManagerTests
{
    private const string Recipe = "RECIPE-7";

    private static ProcessJobManager Queued(
        string processJobId = "PJ-1",
        bool startsAutomatically = true,
        ProcessJobRecipeMethod recipeMethod = ProcessJobRecipeMethod.WithoutVariableTuning)
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create(
            processJobId,
            Recipe,
            ["W01", "W02"],
            startsAutomatically,
            recipeMethod,
            parameters: new Dictionary<string, string>(StringComparer.Ordinal) { ["TEMP"] = "250" }).IsAccepted);

        return manager;
    }

    private static ProcessJobManager Processing(string processJobId = "PJ-1")
    {
        var manager = Queued(processJobId);

        Assert.True(manager.BeginSetup(processJobId).IsAccepted);
        Assert.True(manager.SetupComplete(processJobId).IsAccepted);
        Assert.Equal(ProcessJobState.Processing, Job(manager, processJobId).State);

        return manager;
    }

    private static ProcessJob Job(ProcessJobManager manager, string processJobId = "PJ-1")
    {
        var job = manager.Find(processJobId);

        Assert.NotNull(job);

        return job;
    }

    /// <summary>Puts a job in whichever state a theory asks for.</summary>
    private static ProcessJobManager In(ProcessJobState state, string processJobId = "PJ-1")
    {
        var manager = Queued(processJobId, startsAutomatically: state is not ProcessJobState.WaitingForStart);

        switch (state)
        {
            case ProcessJobState.Queued:
                break;

            case ProcessJobState.SettingUp:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                break;

            case ProcessJobState.WaitingForStart:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                Assert.True(manager.SetupComplete(processJobId).IsAccepted);
                break;

            case ProcessJobState.Processing:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                Assert.True(manager.SetupComplete(processJobId).IsAccepted);
                break;

            case ProcessJobState.Pausing:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                Assert.True(manager.SetupComplete(processJobId).IsAccepted);
                Assert.True(manager.Pause(processJobId).IsAccepted);
                break;

            case ProcessJobState.Paused:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                Assert.True(manager.SetupComplete(processJobId).IsAccepted);
                Assert.True(manager.Pause(processJobId).IsAccepted);
                Assert.True(manager.PauseComplete(processJobId).IsAccepted);
                break;

            case ProcessJobState.Stopping:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                Assert.True(manager.SetupComplete(processJobId).IsAccepted);
                Assert.True(manager.Stop(processJobId).IsAccepted);
                break;

            case ProcessJobState.Aborting:
                Assert.True(manager.BeginSetup(processJobId).IsAccepted);
                Assert.True(manager.SetupComplete(processJobId).IsAccepted);
                Assert.True(manager.Abort(processJobId).IsAccepted);
                break;

            case ProcessJobState.ProcessComplete:
                Assert.True(manager.Cancel(processJobId).IsAccepted);
                break;

            default:
                Assert.Fail($"{state} is not a state this helper knows how to reach.");
                break;
        }

        Assert.Equal(state, Job(manager, processJobId).State);

        return manager;
    }

    // ------------------------------------------------------------- the vocabulary

    /// <summary>The state numbers are <c>PRSTATE</c>'s, and this test is why they stay that way.</summary>
    /// <remarks>
    /// Asserted against literals rather than against the enum, which would assert nothing.
    /// <c>PRSTATE</c> is defined as <c>U1</c> with ten code values; nine of them are states and
    /// the tenth, 5, is reserved. These numbers go on the wire in every S16F6, S16F8, S16F11,
    /// S16F20 and S16F29, so a renumbering would put this library's <c>Pausing</c> on the wire as
    /// somebody else's state.
    /// </remarks>
    [Fact]
    public void The_state_numbers_are_the_ones_the_catalogue_defines()
    {
        Assert.Equal(0, (byte)ProcessJobState.Queued);
        Assert.Equal(1, (byte)ProcessJobState.SettingUp);
        Assert.Equal(2, (byte)ProcessJobState.WaitingForStart);
        Assert.Equal(3, (byte)ProcessJobState.Processing);
        Assert.Equal(4, (byte)ProcessJobState.ProcessComplete);
        Assert.Equal(6, (byte)ProcessJobState.Pausing);
        Assert.Equal(7, (byte)ProcessJobState.Paused);
        Assert.Equal(8, (byte)ProcessJobState.Stopping);
        Assert.Equal(9, (byte)ProcessJobState.Aborting);

        // And nothing is 5, because PRSTATE reserves it.
        Assert.DoesNotContain((byte)5, Enum.GetValues<ProcessJobState>().Select(state => (byte)state));
    }

    [Fact]
    public void The_command_names_and_the_code_values_are_the_defined_ones()
    {
        Assert.Equal("START", ProcessJobCommands.Start);
        Assert.Equal("PAUSE", ProcessJobCommands.Pause);
        Assert.Equal("RESUME", ProcessJobCommands.Resume);
        Assert.Equal("STOP", ProcessJobCommands.Stop);
        Assert.Equal("ABORT", ProcessJobCommands.Abort);
        Assert.Equal("CANCEL", ProcessJobCommands.Cancel);

        // CMDA, CPACK and PRRECIPEMETHOD are all defined with their meanings, so these numbers are
        // quotations rather than choices - and the two that are this library's own say so: CPACK 0
        // for success, which the catalogue leaves out, and CPACK 4, the first of its reserved range.
        Assert.Equal(0, (byte)ProcessJobCommandAcknowledgement.Performed);
        Assert.Equal(1, (byte)ProcessJobCommandAcknowledgement.DoesNotExist);
        Assert.Equal(2, (byte)ProcessJobCommandAcknowledgement.CannotPerformNow);
        Assert.Equal(1, (byte)ProcessJobParameterAcknowledgement.NameDoesNotExist);
        Assert.Equal(2, (byte)ProcessJobParameterAcknowledgement.IllegalValue);
        Assert.Equal(3, (byte)ProcessJobParameterAcknowledgement.IllegalFormat);
        Assert.Equal(1, (byte)ProcessJobRecipeMethod.WithoutVariableTuning);
        Assert.Equal(2, (byte)ProcessJobRecipeMethod.WithVariableTuning);
        Assert.Equal(1, (byte)ProcessJobMaterialOrder.ArrivalSequence);
        Assert.Equal(3, (byte)ProcessJobMaterialOrder.ListedSequence);
    }

    // ------------------------------------------------------------------ creating

    [Fact]
    public void A_new_job_is_queued_with_everything_it_was_created_with()
    {
        var manager = Queued();

        var job = Job(manager);

        Assert.Equal(ProcessJobState.Queued, job.State);
        Assert.Null(job.Completion);
        Assert.Equal(Recipe, job.RecipeId);
        Assert.Equal(["W01", "W02"], job.Material);
        Assert.Equal("250", job.Parameters["TEMP"]);
        Assert.Equal("PJ-1", Assert.Single(manager.Queue));
        Assert.Equal(1, manager.QueuePosition("PJ-1"));

        // Queued is the one state that is not active: nothing of the machine is committed to it.
        Assert.False(job.IsActive);
        Assert.False(job.IsFinished);
    }

    [Fact]
    public void A_job_with_no_recipe_is_refused_rather_than_left_unrunnable()
    {
        var manager = new ProcessJobManager();

        var refusal = manager.Create("PJ-1", string.Empty);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("needs a recipe", refusal.Reason, StringComparison.Ordinal);
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public void A_job_with_no_name_is_refused()
    {
        var manager = new ProcessJobManager();

        Assert.False(manager.Create(string.Empty, Recipe).IsAccepted);
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public void The_same_name_twice_is_refused_and_the_first_job_is_untouched()
    {
        var manager = Processing();

        var refusal = manager.Create("PJ-1", "RECIPE-OTHER");

        Assert.False(refusal.IsAccepted);
        Assert.Contains("already exists", refusal.Reason, StringComparison.Ordinal);

        // The fault worth guarding: a create that overwrote would reset a processing job.
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);
        Assert.Equal(Recipe, Job(manager).RecipeId);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public void A_machine_that_has_said_how_many_jobs_it_holds_refuses_one_too_many()
    {
        var manager = new ProcessJobManager { JobSpace = 2 };

        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);

        var refusal = manager.Create("PJ-3", Recipe);

        // PRJOBSPACE earning its keep: a machine that took a third job it cannot hold would be
        // accepting work it has nowhere to put.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("holds 2 of 2", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(2, manager.Jobs.Count);
    }

    [Fact]
    public void A_machine_that_has_said_nothing_about_its_capacity_takes_what_it_is_given()
    {
        var manager = new ProcessJobManager();

        Assert.Null(manager.JobSpace);

        for (var index = 0; index < 20; index++)
        {
            Assert.True(manager.Create($"PJ-{index}", Recipe).IsAccepted);
        }

        // Null is "no stated limit", not zero: a machine that has not said how many jobs it can
        // hold has not said it can hold none.
        Assert.Equal(20, manager.Jobs.Count);
    }

    [Fact]
    public void Space_is_counted_in_jobs_and_not_in_queued_jobs()
    {
        var manager = new ProcessJobManager { JobSpace = 1 };

        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.Empty(manager.Queue);

        // The queue is empty and the machine is still full: a job being set up takes the room it
        // took while it waited, and counting only the queue would let a host pile up work.
        Assert.False(manager.Create("PJ-2", Recipe).IsAccepted);

        Assert.True(manager.Cancel("PJ-1").IsAccepted);
        Assert.True(manager.Deselect("PJ-1").IsAccepted);
        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);
    }

    [Fact]
    public void The_material_and_the_parameters_a_job_was_given_are_copied()
    {
        var manager = new ProcessJobManager();
        List<string> material = ["W01"];
        Dictionary<string, string> parameters = new(StringComparer.Ordinal) { ["TEMP"] = "250" };

        Assert.True(manager.Create("PJ-1", Recipe, material, parameters: parameters).IsAccepted);

        material.Add("W02");
        parameters["TEMP"] = "900";
        parameters["PRESSURE"] = "1";

        // A job whose collections were the caller's would change under the machine whenever the
        // application touched its own.
        Assert.Equal("W01", Assert.Single(Job(manager).Material));
        Assert.Equal("250", Assert.Single(Job(manager).Parameters).Value);
    }

    [Fact]
    public void Null_arguments_are_rejected_rather_than_stored()
    {
        var manager = new ProcessJobManager();

        Assert.Throws<ArgumentNullException>(() => manager.Create(null!, Recipe));
        Assert.Throws<ArgumentNullException>(() => manager.Create("PJ-1", null!));
        Assert.Throws<ArgumentNullException>(() => manager.Find(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Deselect(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Describe(null!));
        Assert.Throws<ArgumentNullException>(() => manager.SetPauseEvent(null!, 1));
        Assert.Throws<ArgumentNullException>(() => manager.Reorder(null!));
        Assert.Throws<ArgumentNullException>(() => manager.QueuePosition(null!));
        Assert.Throws<ArgumentNullException>(() => manager.Command(null!, ProcessJobCommands.Start));
        Assert.Throws<ArgumentNullException>(() => manager.Command("PJ-1", null!));
        Assert.Throws<ArgumentNullException>(() => manager.BindMaterial("PJ-1", null!));
        Assert.Throws<ArgumentNullException>(() => manager.AddMaterial("PJ-1", null!));
        Assert.Throws<ArgumentNullException>(() => manager.ChangeParameter("PJ-1", null!, "1"));
        Assert.Throws<ArgumentNullException>(() => manager.ChangeParameter("PJ-1", "TEMP", null!));
    }

    [Fact]
    public void A_job_nobody_created_cannot_be_anything()
    {
        var manager = new ProcessJobManager();

        Assert.Null(manager.Find("PJ-404"));
        Assert.Equal(0, manager.QueuePosition("PJ-404"));

        foreach (var result in new[]
        {
            manager.BeginSetup("PJ-404"),
            manager.SetupComplete("PJ-404"),
            manager.Start("PJ-404"),
            manager.Pause("PJ-404"),
            manager.PauseComplete("PJ-404"),
            manager.Resume("PJ-404"),
            manager.Stop("PJ-404"),
            manager.StopComplete("PJ-404"),
            manager.Abort("PJ-404"),
            manager.AbortComplete("PJ-404"),
            manager.Cancel("PJ-404"),
            manager.MaterialProcessed("PJ-404"),
            manager.Deselect("PJ-404"),
            manager.Describe("PJ-404", recipeId: Recipe),
            manager.SetPauseEvent("PJ-404", 7),
            manager.BindMaterial("PJ-404", ["W01"]),
            manager.AddMaterial("PJ-404", ["W01"]),
            manager.ChangeParameter("PJ-404", "TEMP", "1"),
            manager.Command("PJ-404", ProcessJobCommands.Start),
        })
        {
            Assert.False(result.IsAccepted);
            Assert.Contains("no process job", result.Reason, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------- the lifecycle

    [Fact]
    public void A_job_that_starts_itself_goes_from_setup_straight_to_processing()
    {
        var manager = Queued();

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.SettingUp, Job(manager).State);
        Assert.Empty(manager.Queue);
        Assert.Equal(0, manager.QueuePosition("PJ-1"));
        Assert.True(Job(manager).IsActive);

        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);
    }

    [Fact]
    public void A_job_that_waits_for_the_host_stops_at_waiting_for_start()
    {
        var manager = Queued(startsAutomatically: false);

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);

        // The one place StartsAutomatically decides anything, and the reason the field exists.
        Assert.Equal(ProcessJobState.WaitingForStart, Job(manager).State);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Start).IsAccepted);
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);
    }

    [Theory]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.WaitingForStart)]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.Aborting)]
    [InlineData(ProcessJobState.ProcessComplete)]
    public void Only_a_queued_job_can_be_taken_up(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.BeginSetup("PJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(from.ToString(), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(from, Job(manager).State);
    }

    [Fact]
    public void Setup_can_only_finish_once_and_only_from_setting_up()
    {
        var manager = Queued();

        Assert.False(manager.SetupComplete("PJ-1").IsAccepted);

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);

        var refusal = manager.SetupComplete("PJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ProcessJobState.Processing), refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.ProcessComplete)]
    public void Only_a_job_waiting_for_a_start_can_be_started(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.Start("PJ-1");

        // Setting up is the interesting one: the material is still coming in, and a start there
        // would run a recipe over wafers that have not arrived.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(from.ToString(), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(from, Job(manager).State);
    }

    // ------------------------------------------------------- pausing and resuming

    [Fact]
    public void A_pause_lands_in_pausing_and_only_the_machine_finishes_it()
    {
        var manager = Processing();

        Assert.True(manager.Pause("PJ-1").IsAccepted);

        // Not Paused: the machine has to get to a point where pausing is safe, and saying it is
        // already there would be the state machine lying on the equipment's behalf.
        Assert.Equal(ProcessJobState.Pausing, Job(manager).State);
        Assert.True(Job(manager).IsActive);

        // And a host cannot resume out of it, because there is nothing to resume yet.
        Assert.False(manager.Resume("PJ-1").IsAccepted);

        Assert.True(manager.PauseComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.Paused, Job(manager).State);

        Assert.True(manager.Resume("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);
    }

    [Fact]
    public void A_second_pause_while_pausing_changes_nothing()
    {
        var manager = In(ProcessJobState.Pausing);

        var refusal = manager.Pause("PJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ProcessJobState.Pausing), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ProcessJobState.Pausing, Job(manager).State);
    }

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.WaitingForStart)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.ProcessComplete)]
    public void Only_a_processing_job_can_be_paused(ProcessJobState from)
    {
        var manager = In(from);

        Assert.False(manager.Pause("PJ-1").IsAccepted);
        Assert.Equal(from, Job(manager).State);
    }

    [Fact]
    public void Only_a_pausing_job_can_finish_pausing()
    {
        var manager = Processing();

        var refusal = manager.PauseComplete("PJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ProcessJobState.Processing), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);
    }

    // ------------------------------------------------------------------- endings

    [Fact]
    public void A_job_that_works_through_its_material_completes()
    {
        var manager = Processing();

        Assert.True(manager.MaterialProcessed("PJ-1").IsAccepted);

        var job = Job(manager);

        Assert.Equal(ProcessJobState.ProcessComplete, job.State);
        Assert.Equal(ProcessJobCompletion.Completed, job.Completion);
        Assert.True(job.IsFinished);
        Assert.False(job.IsActive);
    }

    [Theory]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.Aborting)]
    public void Only_a_processing_job_can_have_finished_processing(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.MaterialProcessed("PJ-1");

        // A paused job has not finished, and a job being stopped ends as Stopped: calling either
        // of those a completion would tell a host the material is done.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(from.ToString(), refusal.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    public void A_stop_takes_a_live_job_through_stopping(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);
        Assert.Equal(ProcessJobState.Stopping, Job(manager).State);

        // A pause that had not landed is overtaken rather than waited for.
        Assert.True(manager.StopComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.ProcessComplete, Job(manager).State);
        Assert.Equal(ProcessJobCompletion.Stopped, Job(manager).Completion);
    }

    [Theory]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.Stopping)]
    public void An_abort_overtakes_everything_including_a_tidy_stop(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Abort).IsAccepted);
        Assert.Equal(ProcessJobState.Aborting, Job(manager).State);

        Assert.True(manager.AbortComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobCompletion.Aborted, Job(manager).Completion);
    }

    [Fact]
    public void An_abort_cannot_be_overtaken_by_a_stop_or_by_another_abort()
    {
        var manager = In(ProcessJobState.Aborting);

        Assert.False(manager.Stop("PJ-1").IsAccepted);
        Assert.False(manager.Abort("PJ-1").IsAccepted);

        // Aborting is already the fastest ending there is; there is nothing to escalate to.
        Assert.Equal(ProcessJobState.Aborting, Job(manager).State);
    }

    [Fact]
    public void A_stopping_job_cannot_be_stopped_again_or_paused()
    {
        var manager = In(ProcessJobState.Stopping);

        Assert.False(manager.Stop("PJ-1").IsAccepted);
        Assert.False(manager.Pause("PJ-1").IsAccepted);
        Assert.False(manager.Resume("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.Stopping, Job(manager).State);
    }

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.WaitingForStart)]
    public void Cancel_works_from_every_state_before_a_job_processes(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Cancel).IsAccepted);

        Assert.Equal(ProcessJobState.ProcessComplete, Job(manager).State);
        Assert.Equal(ProcessJobCompletion.Cancelled, Job(manager).Completion);
        Assert.Empty(manager.Queue);
    }

    [Theory]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.Aborting)]
    public void A_job_that_has_processed_cannot_be_cancelled(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.Cancel("PJ-1");

        // E40 keeping the record honest: a cancelled job touched no material.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("stop or abort it", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(from, Job(manager).State);
    }

    [Fact]
    public void Nothing_at_all_comes_out_of_process_complete()
    {
        var manager = Processing();

        Assert.True(manager.MaterialProcessed("PJ-1").IsAccepted);

        foreach (var result in new[]
        {
            manager.BeginSetup("PJ-1"),
            manager.SetupComplete("PJ-1"),
            manager.Start("PJ-1"),
            manager.Pause("PJ-1"),
            manager.PauseComplete("PJ-1"),
            manager.Resume("PJ-1"),
            manager.Stop("PJ-1"),
            manager.StopComplete("PJ-1"),
            manager.Abort("PJ-1"),
            manager.AbortComplete("PJ-1"),
            manager.Cancel("PJ-1"),
            manager.MaterialProcessed("PJ-1"),
            manager.Describe("PJ-1", recipeId: "RECIPE-OTHER"),
            manager.SetPauseEvent("PJ-1", 7),
            manager.BindMaterial("PJ-1", ["W09"]),
            manager.AddMaterial("PJ-1", ["W09"]),
            manager.ChangeParameter("PJ-1", "TEMP", "900"),
        })
        {
            Assert.False(result.IsAccepted);
        }

        // And the ending it had is the ending it keeps.
        Assert.Equal(ProcessJobCompletion.Completed, Job(manager).Completion);
    }

    // ------------------------------------------------------------------ deselect

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.ProcessComplete)]
    public void A_job_that_is_waiting_or_over_can_be_taken_off_the_machine(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.Deselect("PJ-1").IsAccepted);

        Assert.Empty(manager.Jobs);
        Assert.Empty(manager.Queue);
        Assert.Null(manager.Find("PJ-1"));

        // And the name comes back, which is what makes a long-running machine possible.
        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
    }

    [Theory]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.WaitingForStart)]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.Aborting)]
    public void A_job_that_holds_material_cannot_be_taken_off_the_machine(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.Deselect("PJ-1");

        Assert.False(refusal.IsAccepted);
        Assert.Contains(from.ToString(), refusal.Reason, StringComparison.Ordinal);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public void A_cancelled_job_leaves_a_record_and_a_deselected_one_does_not()
    {
        var manager = Queued();

        Assert.True(manager.Cancel("PJ-1").IsAccepted);

        // The difference between the two, which is the whole reason both exist.
        Assert.Equal(ProcessJobCompletion.Cancelled, Job(manager).Completion);

        Assert.True(manager.Deselect("PJ-1").IsAccepted);
        Assert.Null(manager.Find("PJ-1"));
    }

    // ------------------------------------------------------------------ the queue

    [Fact]
    public void The_queue_is_the_jobs_that_are_still_waiting()
    {
        var manager = new ProcessJobManager();

        foreach (var name in new[] { "PJ-1", "PJ-2", "PJ-3" })
        {
            Assert.True(manager.Create(name, Recipe).IsAccepted);
        }

        Assert.Equal(["PJ-1", "PJ-2", "PJ-3"], manager.Queue);
        Assert.Equal(2, manager.QueuePosition("PJ-2"));

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);

        // Taken up, so no longer waiting - and the rest move up, which is what a position means.
        Assert.Equal(["PJ-2", "PJ-3"], manager.Queue);
        Assert.Equal(0, manager.QueuePosition("PJ-1"));
        Assert.Equal(1, manager.QueuePosition("PJ-2"));
    }

    [Fact]
    public void A_host_can_reorder_the_queue()
    {
        var manager = new ProcessJobManager();

        foreach (var name in new[] { "PJ-1", "PJ-2", "PJ-3" })
        {
            Assert.True(manager.Create(name, Recipe).IsAccepted);
        }

        Assert.True(manager.Reorder(["PJ-3", "PJ-1", "PJ-2"]).IsAccepted);

        Assert.Equal(["PJ-3", "PJ-1", "PJ-2"], manager.Queue);
        Assert.Equal(1, manager.QueuePosition("PJ-3"));
    }

    [Fact]
    public void A_reorder_of_the_wrong_length_or_with_a_name_twice_is_refused()
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);

        var short_ = manager.Reorder(["PJ-1"]);
        var twice = manager.Reorder(["PJ-1", "PJ-1"]);

        Assert.False(short_.IsAccepted);
        Assert.Contains("names 1", short_.Reason, StringComparison.Ordinal);
        Assert.False(twice.IsAccepted);
        Assert.Contains("twice", twice.Reason, StringComparison.Ordinal);
        Assert.Equal(["PJ-1", "PJ-2"], manager.Queue);
    }

    /// <summary>Each reorder refusal carries the <c>HCACK</c> code that says which part was wrong.</summary>
    /// <remarks>
    /// Asserted on the result rather than through a reply, because that is where the choice is
    /// made: the first version of the equipment side picked the code by looking for words in
    /// <see cref="ProcessJobResult.Reason"/>, which would have changed the wire the next time
    /// somebody reworded a sentence.
    /// </remarks>
    [Fact]
    public void A_reorder_refusal_names_which_part_was_wrong()
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);
        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);

        Assert.Equal(
            ProcessJobQueueAcknowledgement.ParameterInvalid,
            manager.Reorder(["PJ-2", "PJ-2"]).QueueAcknowledgement);

        Assert.Equal(
            ProcessJobQueueAcknowledgement.ParameterInvalid,
            manager.Reorder([]).QueueAcknowledgement);

        Assert.Equal(
            ProcessJobQueueAcknowledgement.NoSuchObject,
            manager.Reorder(["PJ-1"]).QueueAcknowledgement);

        // And an accepted reorder leaves it at the code that means yes.
        Assert.Equal(
            ProcessJobQueueAcknowledgement.Acknowledged,
            manager.Reorder(["PJ-2"]).QueueAcknowledgement);
    }

    [Fact]
    public void A_reorder_naming_a_job_the_machine_has_taken_up_is_refused()
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);
        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);

        var refusal = manager.Reorder(["PJ-1"]);

        // A job being set up is not in the queue, and a reorder is not a way back into it.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("not in the queue", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal("PJ-2", Assert.Single(manager.Queue));
    }

    // ------------------------------------------------------------ the description

    [Fact]
    public void A_queued_job_can_be_described_and_a_job_in_setup_cannot()
    {
        var manager = Queued();

        Assert.True(manager.Describe(
            "PJ-1",
            recipeId: "RECIPE-9",
            recipeMethod: ProcessJobRecipeMethod.WithVariableTuning,
            startsAutomatically: false,
            materialOrder: ProcessJobMaterialOrder.ListedSequence).IsAccepted);

        var job = Job(manager);

        Assert.Equal("RECIPE-9", job.RecipeId);
        Assert.Equal(ProcessJobRecipeMethod.WithVariableTuning, job.RecipeMethod);
        Assert.False(job.StartsAutomatically);
        Assert.Equal(ProcessJobMaterialOrder.ListedSequence, job.MaterialOrder);

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);

        var refusal = manager.Describe("PJ-1", recipeId: "RECIPE-OTHER");

        // The recipe is going down and the material is coming in: changing either underneath that
        // is how a lot gets run twice.
        Assert.False(refusal.IsAccepted);
        Assert.Contains(nameof(ProcessJobState.SettingUp), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal("RECIPE-9", Job(manager).RecipeId);
    }

    [Fact]
    public void Describing_changes_only_what_it_names_and_cannot_empty_the_recipe()
    {
        var manager = Queued();

        Assert.True(manager.Describe("PJ-1", startsAutomatically: false).IsAccepted);

        Assert.Equal(Recipe, Job(manager).RecipeId);
        Assert.Equal(ProcessJobRecipeMethod.WithoutVariableTuning, Job(manager).RecipeMethod);

        var refusal = manager.Describe("PJ-1", recipeId: string.Empty);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("needs a recipe", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(Recipe, Job(manager).RecipeId);
    }

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Paused)]
    public void A_pause_event_can_be_nominated_at_any_time_before_the_job_is_over(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.SetPauseEvent("PJ-1", 2_500).IsAccepted);
        Assert.Equal(2_500u, Job(manager).PauseEventId);

        // And cleared, which a host does by sending a zero-length PRPAUSEEVENTID.
        Assert.True(manager.SetPauseEvent("PJ-1", null).IsAccepted);
        Assert.Null(Job(manager).PauseEventId);
    }

    [Fact]
    public void A_finished_job_will_not_pause_again()
    {
        var manager = In(ProcessJobState.ProcessComplete);

        var refusal = manager.SetPauseEvent("PJ-1", 7);

        Assert.False(refusal.IsAccepted);
        Assert.Contains("will not pause again", refusal.Reason, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- material

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.SettingUp)]
    public void Material_can_be_bound_before_a_job_processes(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.BindMaterial("PJ-1", ["W09", "W10"]).IsAccepted);

        // Replaces rather than appends, which is what binding means.
        Assert.Equal(["W09", "W10"], Job(manager).Material);
    }

    [Theory]
    [InlineData(ProcessJobState.WaitingForStart)]
    [InlineData(ProcessJobState.Processing)]
    [InlineData(ProcessJobState.Paused)]
    [InlineData(ProcessJobState.ProcessComplete)]
    public void Material_cannot_be_bound_once_a_job_is_ready_to_run(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.BindMaterial("PJ-1", ["W09"]);

        Assert.False(refusal.IsAccepted);
        Assert.Contains(from.ToString(), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(["W01", "W02"], Job(manager).Material);
    }

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.WaitingForStart)]
    [InlineData(ProcessJobState.Processing)]
    public void Material_can_be_added_up_to_and_including_processing(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.AddMaterial("PJ-1", ["W03"]).IsAccepted);

        // Appends, because extending a lot that is already running is what S16F15 is for.
        Assert.Equal(["W01", "W02", "W03"], Job(manager).Material);
    }

    [Theory]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.Aborting)]
    [InlineData(ProcessJobState.ProcessComplete)]
    public void Material_cannot_be_added_to_a_job_that_is_ending(ProcessJobState from)
    {
        var manager = In(from);

        Assert.False(manager.AddMaterial("PJ-1", ["W03"]).IsAccepted);
        Assert.Equal(2, Job(manager).Material.Count);
    }

    [Fact]
    public void Material_the_job_already_has_is_refused_and_nothing_is_added()
    {
        var manager = Queued();

        var refusal = manager.AddMaterial("PJ-1", ["W03", "W01", "W04"]);

        // The same wafer twice is a mistake somewhere upstream, and a half-applied add would leave
        // a host with no way of knowing which half took.
        Assert.False(refusal.IsAccepted);
        Assert.Contains("already has material 'W01'", refusal.Reason, StringComparison.Ordinal);
        Assert.Equal(["W01", "W02"], Job(manager).Material);
    }

    [Fact]
    public void Binding_no_material_at_all_is_allowed()
    {
        var manager = Queued();

        Assert.True(manager.BindMaterial("PJ-1", []).IsAccepted);

        // A job with no material is a real thing: E40 lets a host bind some later, and S16F15
        // exists for exactly that.
        Assert.Empty(Job(manager).Material);
    }

    // --------------------------------------------------------------- parameters

    [Theory]
    [InlineData(ProcessJobState.Queued)]
    [InlineData(ProcessJobState.SettingUp)]
    [InlineData(ProcessJobState.WaitingForStart)]
    [InlineData(ProcessJobState.Paused)]
    public void A_parameter_can_be_changed_while_the_job_is_not_running(ProcessJobState from)
    {
        var manager = In(from);

        Assert.True(manager.ChangeParameter("PJ-1", "TEMP", "300").IsAccepted);
        Assert.Equal("300", Job(manager).Parameters["TEMP"]);
    }

    [Fact]
    public void A_running_job_takes_a_parameter_change_only_with_variable_tuning()
    {
        var tuned = Queued(recipeMethod: ProcessJobRecipeMethod.WithVariableTuning);

        Assert.True(tuned.BeginSetup("PJ-1").IsAccepted);
        Assert.True(tuned.SetupComplete("PJ-1").IsAccepted);
        Assert.True(tuned.ChangeParameter("PJ-1", "TEMP", "300").IsAccepted);
        Assert.Equal("300", Job(tuned).Parameters["TEMP"]);

        var fixed_ = Processing();

        var refusal = fixed_.ChangeParameter("PJ-1", "TEMP", "300");

        // The one place in these four standards where a defined code value decides a behaviour:
        // PRRECIPEMETHOD 1 is a recipe without variable tuning, and tuning it anyway is what this
        // refusal is for.
        Assert.False(refusal.IsAccepted);
        Assert.Equal(ProcessJobParameterAcknowledgement.CannotChangeNow, refusal.ParameterAcknowledgement);
        Assert.Contains(nameof(ProcessJobRecipeMethod.WithoutVariableTuning), refusal.Reason, StringComparison.Ordinal);
        Assert.Equal("250", Job(fixed_).Parameters["TEMP"]);
    }

    [Fact]
    public void A_parameter_the_job_does_not_have_cannot_be_invented()
    {
        var manager = Queued();

        var refusal = manager.ChangeParameter("PJ-1", "PRESSURE", "1");

        Assert.False(refusal.IsAccepted);
        Assert.Equal(ProcessJobParameterAcknowledgement.NameDoesNotExist, refusal.ParameterAcknowledgement);
        Assert.Equal("TEMP", Assert.Single(Job(manager).Parameters).Key);
    }

    [Theory]
    [InlineData(ProcessJobState.Pausing)]
    [InlineData(ProcessJobState.Stopping)]
    [InlineData(ProcessJobState.Aborting)]
    public void A_job_that_is_ending_takes_no_parameter_change(ProcessJobState from)
    {
        var manager = In(from);

        var refusal = manager.ChangeParameter("PJ-1", "TEMP", "300");

        Assert.False(refusal.IsAccepted);
        Assert.Equal(ProcessJobParameterAcknowledgement.CannotChangeNow, refusal.ParameterAcknowledgement);
        Assert.Equal("250", Job(manager).Parameters["TEMP"]);
    }

    // ------------------------------------------------------------------ commands

    [Fact]
    public void Every_command_name_reaches_the_transition_it_names()
    {
        var manager = Queued(startsAutomatically: false);

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Start).IsAccepted);
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Pause).IsAccepted);
        Assert.True(manager.PauseComplete("PJ-1").IsAccepted);
        Assert.Equal(ProcessJobState.Paused, Job(manager).State);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Resume).IsAccepted);
        Assert.Equal(ProcessJobState.Processing, Job(manager).State);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);
        Assert.Equal(ProcessJobState.Stopping, Job(manager).State);

        Assert.True(manager.Command("PJ-1", ProcessJobCommands.Abort).IsAccepted);
        Assert.Equal(ProcessJobState.Aborting, Job(manager).State);
    }

    [Fact]
    public void A_command_nobody_defined_says_so_rather_than_saying_not_now()
    {
        var manager = Processing();

        var refusal = manager.Command("PJ-1", "TELEPORT");

        // CMDA is defined with both meanings, which is the one thing E40's command path has that
        // E94's does not: a command this machine has never heard of is not a command it cannot do
        // yet, and a host told the difference has something to fix.
        Assert.False(refusal.IsAccepted);
        Assert.Equal(ProcessJobCommandAcknowledgement.DoesNotExist, refusal.CommandAcknowledgement);
        Assert.Contains("TELEPORT", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_this_machine_has_but_cannot_do_now_says_that_instead()
    {
        var manager = Queued();

        var refusal = manager.Command("PJ-1", ProcessJobCommands.Pause);

        Assert.False(refusal.IsAccepted);
        Assert.Equal(ProcessJobCommandAcknowledgement.CannotPerformNow, refusal.CommandAcknowledgement);
    }

    [Fact]
    public void A_command_is_matched_by_its_spelling_and_not_by_its_casing()
    {
        var manager = Processing();

        Assert.True(manager.Command("PJ-1", "pause").IsAccepted);
        Assert.Equal(ProcessJobState.Pausing, Job(manager).State);

        Assert.False(manager.Command("PJ-1", " PAUSE").IsAccepted);
    }

    // ---------------------------------------------------- several jobs at once

    [Fact]
    public void Two_jobs_can_process_at_the_same_time()
    {
        var manager = new ProcessJobManager();

        foreach (var name in new[] { "PJ-1", "PJ-2" })
        {
            Assert.True(manager.Create(name, Recipe).IsAccepted);
            Assert.True(manager.BeginSetup(name).IsAccepted);
            Assert.True(manager.SetupComplete(name).IsAccepted);
        }

        // The rule E94 has and E40 does not: a control job is one lot, and a tool with four
        // chambers runs four process jobs. Nothing here refuses the second.
        Assert.Equal(ProcessJobState.Processing, Job(manager, "PJ-1").State);
        Assert.Equal(ProcessJobState.Processing, Job(manager, "PJ-2").State);
        Assert.Empty(manager.Queue);
    }

    [Fact]
    public void Two_threads_racing_to_take_up_one_job_produce_one_setup()
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);

        var accepted = 0;

        Parallel.For(0, 32, _ =>
        {
            if (manager.BeginSetup("PJ-1").IsAccepted)
            {
                Interlocked.Increment(ref accepted);
            }
        });

        // One lock around every transition, and this is what it buys: two schedulers asking at the
        // same moment do not both start setting the same job up.
        Assert.Equal(1, accepted);
        Assert.Equal(ProcessJobState.SettingUp, Job(manager).State);
        Assert.Empty(manager.Queue);
    }

    [Fact]
    public void The_collections_a_caller_gets_are_snapshots()
    {
        var manager = Queued();

        var jobs = manager.Jobs;
        var queue = manager.Queue;

        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);

        Assert.Single(jobs);
        Assert.Single(queue);
        Assert.Equal(2, manager.Jobs.Count);
    }

    // -------------------------------------------------------------------- events

    [Fact]
    public void Every_move_is_announced_with_both_states()
    {
        var manager = Queued(startsAutomatically: false);
        List<ProcessJobStateChangedEventArgs> seen = [];

        manager.JobStateChanged += (_, args) => seen.Add(args);

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);
        Assert.True(manager.Start("PJ-1").IsAccepted);
        Assert.True(manager.Pause("PJ-1").IsAccepted);
        Assert.True(manager.PauseComplete("PJ-1").IsAccepted);
        Assert.True(manager.Abort("PJ-1").IsAccepted);
        Assert.True(manager.AbortComplete("PJ-1").IsAccepted);

        Assert.Equal(
            [
                (ProcessJobState.Queued, ProcessJobState.SettingUp),
                (ProcessJobState.SettingUp, ProcessJobState.WaitingForStart),
                (ProcessJobState.WaitingForStart, ProcessJobState.Processing),
                (ProcessJobState.Processing, ProcessJobState.Pausing),
                (ProcessJobState.Pausing, ProcessJobState.Paused),
                (ProcessJobState.Paused, ProcessJobState.Aborting),
                (ProcessJobState.Aborting, ProcessJobState.ProcessComplete),
            ],
            seen.Select(args => (args.Previous, args.Current)));

        // The completion rides along on the one transition that is an ending, and on no other -
        // which is what lets the equipment side put an S16F11 on the wire without a second lookup.
        Assert.All(seen.Take(6), args => Assert.Null(args.Completion));
        Assert.Equal(ProcessJobCompletion.Aborted, seen[^1].Completion);
    }

    [Fact]
    public void A_handler_sees_the_state_the_event_announces()
    {
        var manager = Processing();
        ProcessJobState? asRead = null;

        manager.JobStateChanged += (_, args) =>
        {
            if (args.Current is ProcessJobState.ProcessComplete)
            {
                asRead = args.Job.State;
            }
        };

        Assert.True(manager.MaterialProcessed("PJ-1").IsAccepted);

        // Raised after the move: a handler that read the job and saw Processing would put a report
        // on the wire saying the job is running at the moment it finished.
        Assert.Equal(ProcessJobState.ProcessComplete, asRead);
    }

    [Fact]
    public void A_refused_transition_announces_nothing()
    {
        var manager = Queued();
        var moves = 0;

        manager.JobStateChanged += (_, _) => moves++;

        Assert.False(manager.Start("PJ-1").IsAccepted);
        Assert.False(manager.Pause("PJ-1").IsAccepted);
        Assert.False(manager.MaterialProcessed("PJ-1").IsAccepted);
        Assert.False(manager.Command("PJ-1", "TELEPORT").IsAccepted);
        Assert.False(manager.ChangeParameter("PJ-1", "PRESSURE", "1").IsAccepted);

        Assert.Equal(0, moves);

        // And a deselect that succeeds announces nothing either: the job is gone rather than
        // moved, and a state change event for an object that no longer exists would be a handler's
        // worst morning.
        Assert.True(manager.Deselect("PJ-1").IsAccepted);
        Assert.Equal(0, moves);
    }

    [Fact]
    public void Describing_a_job_announces_nothing_because_it_is_not_a_move()
    {
        var manager = Queued();
        var moves = 0;

        manager.JobStateChanged += (_, _) => moves++;

        Assert.True(manager.Describe("PJ-1", recipeId: "RECIPE-9").IsAccepted);
        Assert.True(manager.SetPauseEvent("PJ-1", 7).IsAccepted);
        Assert.True(manager.AddMaterial("PJ-1", ["W03"]).IsAccepted);
        Assert.True(manager.ChangeParameter("PJ-1", "TEMP", "300").IsAccepted);

        Assert.Equal(0, moves);
    }
}
