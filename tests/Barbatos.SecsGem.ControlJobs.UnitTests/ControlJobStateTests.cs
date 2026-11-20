// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// A queue that outlives the process, and the two ways an interrupted lot can end.
/// </summary>
/// <remarks>
/// A job's definition is a host instruction and comes back whole, queue order included - a machine
/// that came back with its lots in a different order would run the wrong one next. A job that was
/// <em>running</em> does not come back running, because a restarted machine is not running
/// anything, and it does not go back in the queue either: that would lose the fact that part of
/// the material has already been through. E94 has one ending with a reason beside it, so the
/// reason carries the difference - cancelled when nothing was processed, aborted when something
/// was.
/// </remarks>
public class ControlJobStateTests
{
    private const string First = "CJ-1";
    private const string Second = "CJ-2";
    private const string Third = "CJ-3";

    private static ControlJobManager WithThreeQueuedJobs()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create(First, ["CAR-001"], ControlJobStartMethod.HostInitiated, "ARRIVAL", "RECIPE-7").IsAccepted);
        Assert.True(manager.Create(Second, ["CAR-002"]).IsAccepted);
        Assert.True(manager.Create(Third, ["CAR-003"]).IsAccepted);

        return manager;
    }

    [Fact]
    public void A_jobs_definition_and_the_queue_order_come_back()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Reorder([Third, First, Second]).IsAccepted);

        var restarted = new ControlJobManager();
        var report = restarted.Restore(manager.Capture());

        Assert.True(report.IsAccepted);
        Assert.Equal(3, report.Count);
        Assert.Empty(report.Adjusted);
        Assert.Equal((string[])[Third, First, Second], restarted.Queue);

        var job = restarted.Find(First)!;

        Assert.Equal(ControlJobState.Queued, job.State);
        Assert.Equal("CAR-001", Assert.Single(job.Carriers));
        Assert.Equal(ControlJobStartMethod.HostInitiated, job.StartMethod);
        Assert.Equal("ARRIVAL", job.ProcessOrder);
        Assert.Equal("RECIPE-7", job.ProcessingSpecification);
    }

    [Theory]
    [InlineData(false, ControlJobCompletion.Cancelled)]
    [InlineData(true, ControlJobCompletion.Cancelled)]
    public void A_job_whose_material_was_still_arriving_was_never_run(bool waiting, ControlJobCompletion expected)
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Select(First).IsAccepted);

        if (waiting)
        {
            Assert.True(manager.MaterialReady(First).IsAccepted);
            Assert.Equal(ControlJobState.WaitingForStart, manager.Find(First)!.State);
        }

        var restarted = new ControlJobManager();
        var report = restarted.Restore(manager.Capture());
        var job = restarted.Find(First)!;

        Assert.Equal(ControlJobState.Completed, job.State);
        Assert.Equal(expected, job.Completion);
        Assert.Contains("not running a lot", report.Adjusted[First], StringComparison.Ordinal);

        // And it is not in the queue, which would offer a finished job as the next lot.
        Assert.DoesNotContain(First, restarted.Queue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_job_that_was_part_way_through_its_material_was_aborted(bool paused)
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Select(Second).IsAccepted);
        Assert.True(manager.MaterialReady(Second).IsAccepted);
        Assert.Equal(ControlJobState.Executing, manager.Find(Second)!.State);

        if (paused)
        {
            Assert.True(manager.Pause(Second).IsAccepted);
        }

        var restarted = new ControlJobManager();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        var job = restarted.Find(Second)!;

        // Aborted rather than cancelled, because some of the lot went through and the record has
        // to say so: a host that read "cancelled" would expect untouched material.
        Assert.Equal(ControlJobState.Completed, job.State);
        Assert.Equal(ControlJobCompletion.Aborted, job.Completion);
        Assert.True(job.IsFinished);
    }

    [Fact]
    public void A_job_that_had_already_ended_keeps_the_ending_it_recorded()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Command(First, ControlJobCommands.Cancel).IsAccepted);
        Assert.True(manager.Select(Second).IsAccepted);
        Assert.True(manager.MaterialReady(Second).IsAccepted);
        Assert.True(manager.Command(Second, ControlJobCommands.Stop).IsAccepted);

        var expected = manager.Find(Second)!.Completion;

        var restarted = new ControlJobManager();
        var report = restarted.Restore(manager.Capture());

        Assert.Equal(ControlJobCompletion.Cancelled, restarted.Find(First)!.Completion);
        Assert.Equal(expected, restarted.Find(Second)!.Completion);

        // History, not a claim about now, so nothing was adjusted for either of them.
        Assert.DoesNotContain(First, report.Adjusted.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain(Second, report.Adjusted.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void The_queue_keeps_only_the_jobs_that_really_are_queued()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Select(First).IsAccepted);
        Assert.True(manager.MaterialReady(First).IsAccepted);

        var restarted = new ControlJobManager();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        Assert.Equal((string[])[Second, Third], restarted.Queue);
        Assert.Null(restarted.Current);

        // And the machine can carry on: the next lot is selectable, which it would not be if the
        // aborted one had come back as the current job.
        Assert.True(restarted.Select(Second).IsAccepted);
    }

    /// <summary>
    /// A snapshot whose queue names a job that is not queued.
    /// </summary>
    /// <remarks>
    /// Which a capture from this package never writes, because ending a job takes it out of the
    /// queue - so this is a hand-written or hand-edited snapshot, or one from a version that did
    /// not prune. A negative control found it: dropping the check that a queued job really is
    /// queued left the whole suite green. Without it the machine offers a finished lot as the next
    /// one to run, and E94 will refuse to select it, so the queue stalls on an entry nothing can
    /// ever move.
    /// </remarks>
    [Fact]
    public void A_job_the_queue_names_but_that_is_not_queued_is_left_out_of_it()
    {
        var restarted = new ControlJobManager();

        var report = restarted.Restore(new ControlJobManagerSnapshot
        {
            Jobs =
            [
                new ControlJobSnapshot
                {
                    ControlJobId = First,
                    State = ControlJobState.Completed,
                    Completion = ControlJobCompletion.Aborted,
                },
                new ControlJobSnapshot { ControlJobId = Second, State = ControlJobState.Queued },
            ],
            Queue = [First, Second],
        });

        Assert.True(report.IsAccepted);
        Assert.Equal(2, report.Count);

        // Only the one that can actually be run next.
        Assert.Equal(Second, Assert.Single(restarted.Queue));
        Assert.True(restarted.Select(Second).IsAccepted);
    }

    [Fact]
    public void A_manager_that_already_has_jobs_refuses()
    {
        var manager = WithThreeQueuedJobs();
        var busy = new ControlJobManager();

        Assert.True(busy.Create("CJ-9", ["CAR-009"]).IsAccepted);

        var report = busy.Restore(manager.Capture());

        Assert.False(report.IsAccepted);
        Assert.Contains("already has", report.Reason, StringComparison.Ordinal);
        Assert.Null(busy.Find(First));
        Assert.Equal("CJ-9", Assert.Single(busy.Queue));
    }

    [Fact]
    public void Restoring_raises_nothing()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Select(First).IsAccepted);
        Assert.True(manager.MaterialReady(First).IsAccepted);

        var restarted = new ControlJobManager();
        var moves = 0;

        restarted.JobStateChanged += (_, _) => moves++;

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        // A host that heard "CJ-1 completed, aborted" as a transition would believe the machine
        // had just aborted a lot, rather than that it had been restarted.
        Assert.Equal(0, moves);
    }

    [Fact]
    public void A_capture_is_a_faithful_record_even_of_what_a_restore_will_not_claim()
    {
        var manager = WithThreeQueuedJobs();

        Assert.True(manager.Select(First).IsAccepted);
        Assert.True(manager.MaterialReady(First).IsAccepted);

        var snapshot = manager.Capture();
        var job = snapshot.Jobs.Single(saved => saved.ControlJobId == First);

        Assert.Equal(ControlJobState.WaitingForStart, job.State);
        Assert.Null(job.Completion);
    }

    [Fact]
    public void An_empty_manager_takes_a_snapshot_of_nothing()
    {
        var report = new ControlJobManager().Restore(new ControlJobManagerSnapshot());

        Assert.True(report.IsAccepted);
        Assert.Empty(report.Restored);
    }

    [Fact]
    public void A_snapshot_nobody_passed_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => new ControlJobManager().Restore(null!));
}
