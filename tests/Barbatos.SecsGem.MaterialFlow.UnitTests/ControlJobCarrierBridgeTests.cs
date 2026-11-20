// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.ControlJobs;

namespace Barbatos.SecsGem.MaterialFlow.UnitTests;

/// <summary>
/// The join between E87 and E94: a control job finding out that its material has arrived.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here opens a link. E94 has no message of its own and the join has none either, so
/// every test is two state machines meeting - which is also why the one thing these tests watch
/// most closely is <em>what the bridge does not do</em>. It listens to the carrier manager and to
/// nothing else, on purpose, and one test moves a job through its whole life to prove the
/// carriers never felt it.
/// </para>
/// <para>
/// The other claim worth a test of its own is the distinction
/// <see cref="ControlJobCarrierOptions.RequiresEveryCarrier"/> draws. Turning it off says a job
/// may name material E87 does not track; it must not also say an unverified carrier is good
/// enough. Those are two different facts about two different carriers and only the first is
/// tolerable, so there is a test for each.
/// </para>
/// </remarks>
public class ControlJobCarrierBridgeTests
{
    private const string JobName = "CJ-1";
    private const string First = "CAR-001";
    private const string Second = "CAR-002";
    private const string Stranger = "CAR-003";

    /// <summary>Material a job may name that is not a carrier at all - the fab's own spelling.</summary>
    private const string Reticle = "RET-9";

    private static readonly byte[] SlotMap = [1, 1, 0, 1];

    /// <summary>One fab's answer to "which of a job's identifiers are carriers": the ones spelled so.</summary>
    private static bool OnlyCarriers(string identifier)
        => identifier.StartsWith("CAR-", StringComparison.Ordinal);

    private sealed record Machine(
        CarrierManager Carriers,
        ControlJobManager Jobs,
        ControlJobCarrierBridge Bridge);

    /// <summary>Two ports in service, a job naming two carriers, and nothing arrived yet.</summary>
    private static Machine Build(
        Action<ControlJobCarrierOptions>? configure = null,
        bool attach = false,
        bool select = true,
        ControlJobStartMethod startMethod = ControlJobStartMethod.Automatic,
        IReadOnlyList<string>? carriers = null)
    {
        var manager = new CarrierManager();
        var jobs = new ControlJobManager();
        var options = new ControlJobCarrierOptions();

        configure?.Invoke(options);

        var bridge = new ControlJobCarrierBridge(manager, jobs, options);

        if (attach)
        {
            bridge.Attach();
        }

        // Three ports, because one of the tests needs a carrier for the next lot to come and go
        // while this job is still waiting.
        foreach (var portId in (byte[])[1, 2, 3])
        {
            manager.AddPort(portId);
            Assert.True(manager.SetServiceStatus(portId, inService: true).IsAccepted);
            Assert.True(manager.SetTransferState(portId, PortTransferState.ReadyToLoad).IsAccepted);
        }

        IReadOnlyList<string> material = carriers ?? [First, Second];

        Assert.True(jobs.Create(JobName, material, startMethod).IsAccepted);

        if (select)
        {
            Assert.True(jobs.Select(JobName).IsAccepted);
        }

        return new Machine(manager, jobs, bridge);
    }

    /// <summary>A carrier placed on a port, read and agreed - E87's whole arrival sequence.</summary>
    private static void Arrive(Machine machine, byte portId, string carrierId, bool verified = true)
    {
        Assert.True(machine.Carriers.PlaceCarrier(portId, carrierId).IsAccepted);
        Assert.True(machine.Carriers.ReadCarrierId(carrierId, verified).IsAccepted);
        Assert.True(machine.Carriers.ReadSlotMap(carrierId, SlotMap, verified).IsAccepted);
    }

    /// <summary>A carrier the host says is coming, verified but never placed on anything.</summary>
    private static void Announce(Machine machine, string carrierId)
    {
        Assert.True(machine.Carriers.ReadCarrierId(carrierId, verified: true).IsAccepted);
        Assert.True(machine.Carriers.ReadSlotMap(carrierId, SlotMap, verified: true).IsAccepted);
    }

    // ------------------------------------------------------------ what counts as ready

    [Fact]
    public void Check_names_the_carrier_that_is_holding_the_job_up()
    {
        var machine = Build();

        Arrive(machine, 1, First);

        var answer = machine.Bridge.Check(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Equal(First, Assert.Single(answer.Carriers));
        Assert.Equal(1, answer.Count);

        // The only part anybody can act on: which box is missing, by name.
        Assert.Contains(Second, answer.Refused[Second], StringComparison.Ordinal);
        Assert.Contains("1 of 2", answer.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_accepts_once_every_carrier_is_at_a_port_and_agreed()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        var answer = machine.Bridge.Check(JobName);

        Assert.True(answer.IsAccepted);
        Assert.Equal(string.Empty, answer.Reason);
        Assert.Equal((string[])[First, Second], answer.Carriers);
        Assert.Empty(answer.Refused);

        // And it changed nothing: Check is a question.
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_job_that_names_no_carrier_is_never_ready()
    {
        var machine = Build(carriers: []);

        var answer = machine.Bridge.Check(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains("names no carrier", answer.Reason, StringComparison.Ordinal);

        // Vacuous truth must not start a lot, so the same rule holds for the one that acts.
        Assert.False(machine.Bridge.Ready(JobName).IsAccepted);
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_carrier_that_has_not_arrived_holds_the_job_up()
    {
        var machine = Build(carriers: [First, Stranger]);

        Arrive(machine, 1, First);

        var answer = machine.Bridge.Check(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains("has not arrived", answer.Refused[Stranger], StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_may_name_material_that_is_not_a_carrier_at_all()
    {
        var machine = Build(options => options.IsCarrier = OnlyCarriers, carriers: [First, Reticle]);

        Arrive(machine, 1, First);

        var answer = machine.Bridge.Check(JobName);

        Assert.True(answer.IsAccepted);
        Assert.Equal(First, Assert.Single(answer.Carriers));

        // Tolerated is not the same as unmentioned: it is still in the report, because a job that
        // went ahead over material nothing here can see is worth being able to find out about.
        Assert.Contains(Reticle, answer.Refused.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// The one this bridge got wrong first and a negative control caught.
    /// </summary>
    /// <remarks>
    /// Naming the foreign material used to be a flag - "tolerate the identifiers you do not
    /// know" - and E87 cannot tell an identifier it is not responsible for from a carrier that
    /// has not turned up: both are simply absent from it. So the flag also tolerated a lot whose
    /// second carrier was still on a conveyor somewhere, and started it. A predicate puts the
    /// distinction where the only thing that knows it lives, which is the fab's naming.
    /// </remarks>
    [Fact]
    public void A_carrier_that_has_not_arrived_is_not_foreign_material()
    {
        var machine = Build(options => options.IsCarrier = OnlyCarriers);

        Arrive(machine, 1, First);

        var answer = machine.Bridge.Check(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains("has not arrived", answer.Refused[Second], StringComparison.Ordinal);
    }

    [Fact]
    public void Naming_foreign_material_does_not_make_an_unverified_carrier_good_enough()
    {
        var machine = Build(options => options.IsCarrier = OnlyCarriers);

        Arrive(machine, 1, First);
        Assert.True(machine.Carriers.PlaceCarrier(2, Second).IsAccepted);

        // The carrier is right there, spelled like a carrier, and nobody has agreed it.
        var answer = machine.Bridge.Check(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains("not verified", answer.Refused[Second], StringComparison.Ordinal);
    }

    [Fact]
    public void A_job_that_names_no_carrier_of_ours_is_still_not_ready()
    {
        var machine = Build(options => options.IsCarrier = OnlyCarriers, carriers: [Reticle]);

        // Nothing in the way, and nothing to go on either. An empty quorum is not a met one.
        Assert.False(machine.Bridge.Check(JobName).IsAccepted);
    }

    [Fact]
    public void By_default_every_identifier_a_job_names_is_a_carrier()
    {
        var machine = Build(carriers: [First, Reticle]);

        Arrive(machine, 1, First);

        // Nothing here knows what a RET- is, so the cautious reading is that it is a carrier
        // which has not arrived - and the lot waits.
        Assert.False(machine.Bridge.Check(JobName).IsAccepted);
    }

    [Fact]
    public void A_verified_carrier_that_is_not_at_a_port_has_not_arrived()
    {
        var machine = Build(carriers: [First]);

        Announce(machine, First);

        var answer = machine.Bridge.Check(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains("not at a load port", answer.Refused[First], StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_whose_carriers_live_elsewhere_turns_the_port_off()
    {
        var machine = Build(options => options.RequiresCarrierAtPort = false, carriers: [First]);

        Announce(machine, First);

        Assert.True(machine.Bridge.Check(JobName).IsAccepted);
    }

    [Fact]
    public void There_is_no_such_job()
    {
        var machine = Build();

        foreach (var answer in (ControlJobCarrierResult[])
        [
            machine.Bridge.Check("CJ-9"),
            machine.Bridge.Ready("CJ-9"),
            machine.Bridge.Finish("CJ-9"),
            machine.Bridge.ClaimCarriers("CJ-9"),
            machine.Bridge.ReleaseCarriers("CJ-9"),
        ])
        {
            Assert.False(answer.IsAccepted);
            Assert.Equal("CJ-9", answer.ControlJobId);
            Assert.Contains("there is no control job", answer.Reason, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------ telling the job

    [Fact]
    public void Ready_is_quiet_while_the_material_is_still_arriving()
    {
        var machine = Build();
        var readied = 0;

        machine.Bridge.Readied += (_, _) => readied++;

        Arrive(machine, 1, First);

        Assert.False(machine.Bridge.Ready(JobName).IsAccepted);

        // The ordinary case on a machine where carriers arrive one at a time is not an event.
        Assert.Equal(0, readied);
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void The_last_carrier_to_be_agreed_starts_an_automatic_job()
    {
        var machine = Build();
        var readied = 0;

        machine.Bridge.Readied += (_, arguments) =>
        {
            readied++;
            Assert.True(arguments.Result.IsAccepted);
            Assert.Equal(2, arguments.Result.Count);
        };

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        Assert.Equal(1, readied);
        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_host_initiated_job_waits_for_the_host_even_with_its_material_there()
    {
        var machine = Build(startMethod: ControlJobStartMethod.HostInitiated);

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);

        // The one thing the start method decides, and the bridge does not get a vote.
        Assert.Equal(ControlJobState.WaitingForStart, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_job_that_is_not_selected_refuses_in_E94s_own_words()
    {
        var machine = Build(select: false);
        ControlJobCarrierResult? seen = null;

        machine.Bridge.Readied += (_, arguments) => seen = arguments.Result;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        var answer = machine.Bridge.Ready(JobName);

        Assert.False(answer.IsAccepted);

        // E94's refusal, not a copy of E94's rule kept here - and the carriers it found are still
        // in the answer, because "the material arrived and the job would not take it" is the
        // actionable shape of this.
        Assert.Contains(nameof(ControlJobState.Queued), answer.Reason, StringComparison.Ordinal);
        Assert.Equal(2, answer.Count);
        Assert.NotNull(seen);
        Assert.False(seen!.IsAccepted);
    }

    // ------------------------------------------------------------ the one direction that listens

    [Fact]
    public void Attached_the_lot_starts_itself()
    {
        var machine = Build(attach: true);
        var readied = 0;

        machine.Bridge.Readied += (_, _) => readied++;

        Arrive(machine, 1, First);

        // One carrier in, and nothing has happened: the quorum is the point.
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);
        Assert.Equal(0, readied);

        Arrive(machine, 2, Second);

        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
        Assert.Equal(1, readied);
    }

    /// <summary>
    /// The one case an attached bridge cannot see, named here so that nobody has to find it in a
    /// fab.
    /// </summary>
    /// <remarks>
    /// A job selected when its carriers are already there has no carrier transition left to
    /// happen, and the only thing that would notice is a handler on the control job manager -
    /// which is the direction that may not listen, because both managers raise inside their own
    /// lock. So the cost of that rule is exactly one call after selecting, and this test is the
    /// bill.
    /// </remarks>
    [Fact]
    public void A_job_selected_after_its_material_arrived_has_no_transition_left_to_notice()
    {
        var machine = Build(attach: true, select: false);

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        Assert.True(machine.Jobs.Select(JobName).IsAccepted);
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);

        // One unconditional call after selecting, which is what the documentation asks for.
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void Attaching_twice_listens_once()
    {
        var machine = Build(attach: true);
        var readied = 0;

        machine.Bridge.Attach();
        machine.Bridge.Readied += (_, _) => readied++;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        Assert.True(machine.Bridge.IsAttached);
        Assert.Equal(1, readied);
    }

    [Fact]
    public void Detaching_stops_it_and_keeps_what_it_did()
    {
        var machine = Build(attach: true);

        Arrive(machine, 1, First);
        machine.Bridge.Detach();
        Arrive(machine, 2, Second);

        Assert.False(machine.Bridge.IsAttached);
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);

        // Detached is not broken: by hand it still works, and the material really is ready.
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_carrier_no_running_job_named_is_none_of_its_business()
    {
        var machine = Build(attach: true, carriers: [First]);
        var readied = 0;

        machine.Bridge.Readied += (_, _) => readied++;

        Arrive(machine, 2, Stranger);

        Assert.Equal(0, readied);
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_queued_job_is_not_the_current_one_and_hears_nothing()
    {
        var machine = Build(attach: true, select: false);
        var readied = 0;

        machine.Bridge.Readied += (_, _) => readied++;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        Assert.Equal(0, readied);
        Assert.Equal(ControlJobState.Queued, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void The_bridge_never_listens_to_the_job_manager()
    {
        var machine = Build(attach: true);

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);

        // Both carriers in access, so there is something for a job-side handler to let go of -
        // without this the test would pass against a bridge that did listen, because releasing a
        // carrier nobody claimed is refused anyway and leaves it exactly where it was.
        Assert.Equal(2, machine.Bridge.ClaimCarriers(JobName).Count);

        // A whole life of the job, by hand.
        Assert.True(machine.Jobs.Pause(JobName).IsAccepted);
        Assert.True(machine.Jobs.Resume(JobName).IsAccepted);
        Assert.True(machine.Jobs.Command(JobName, ControlJobCommands.Abort).IsAccepted);
        Assert.Equal(ControlJobState.Completed, machine.Jobs.Find(JobName)!.State);

        // And the carriers never felt any of it: the job is over and the machine still has its
        // material. That is the design claim of this bridge - both managers raise inside their
        // own lock, so only one direction may listen, and this is it not being the job manager.
        // Letting go is ReleaseCarriers, called from outside both locks.
        Assert.Equal(CarrierAccessStatus.InAccess, machine.Carriers.FindCarrier(First)!.AccessStatus);
        Assert.Equal(CarrierAccessStatus.InAccess, machine.Carriers.FindCarrier(Second)!.AccessStatus);

        Assert.Equal(2, machine.Bridge.ReleaseCarriers(JobName).Count);
        Assert.Equal(CarrierAccessStatus.CarrierStopped, machine.Carriers.FindCarrier(First)!.AccessStatus);
    }

    // ------------------------------------------------------------ the other end

    [Fact]
    public void A_job_finishes_when_the_last_carrier_is_complete()
    {
        var machine = Build(attach: true);
        var finished = 0;

        machine.Bridge.Finished += (_, _) => finished++;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.Equal(2, machine.Bridge.ClaimCarriers(JobName).Count);

        Assert.True(machine.Carriers.EndAccess(First).IsAccepted);
        Assert.Equal(0, finished);
        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);

        Assert.True(machine.Carriers.EndAccess(Second).IsAccepted);

        Assert.Equal(1, finished);

        var job = machine.Jobs.Find(JobName)!;

        Assert.Equal(ControlJobState.Completed, job.State);
        Assert.Equal(ControlJobCompletion.Completed, job.Completion);
    }

    [Fact]
    public void A_stopped_carrier_does_not_finish_the_lot_and_does_not_pass_in_silence()
    {
        var machine = Build(attach: true);
        var finished = 0;
        ControlJobCarrierResult? stalled = null;

        machine.Bridge.Finished += (_, _) => finished++;
        machine.Bridge.Stalled += (_, arguments) => stalled = arguments.Result;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        machine.Bridge.ClaimCarriers(JobName);

        Assert.True(machine.Carriers.EndAccess(First).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(Second, stopped: true).IsAccepted);

        // Nothing left to wait for, and this is not a lot anybody finished. So the job stays put
        // and waits for the host to abort it - and the bridge says why, because a job that
        // quietly never ends is the failure this event exists to prevent.
        Assert.Equal(0, finished);
        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
        Assert.NotNull(stalled);
        Assert.False(stalled!.IsAccepted);
        Assert.Contains(nameof(CarrierAccessStatus.CarrierStopped), stalled.Refused[Second], StringComparison.Ordinal);
        Assert.Equal(First, Assert.Single(stalled.Carriers));
    }

    /// <summary>
    /// What the handler's "did this job name this carrier" guard is actually for.
    /// </summary>
    /// <remarks>
    /// A negative control deleted that guard and the whole suite stayed green, which is how this
    /// test came to exist. The guard is not an optimisation: a stalled job sits there waiting for
    /// the host, and every unrelated carrier that comes and goes in the meantime would run the
    /// finishing quorum again and say the same thing a second, fifth, fiftieth time.
    /// </remarks>
    [Fact]
    public void A_stall_is_said_once_and_not_again_for_somebody_elses_carrier()
    {
        var machine = Build(attach: true);
        var stalls = 0;
        var readied = 0;

        machine.Bridge.Stalled += (_, _) => stalls++;
        machine.Bridge.Readied += (_, _) => readied++;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        machine.Bridge.ClaimCarriers(JobName);

        Assert.True(machine.Carriers.EndAccess(First).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(Second, stopped: true).IsAccepted);
        Assert.Equal(1, stalls);
        Assert.Equal(1, readied);

        // A carrier for the next lot arrives and is worked through while this job still waits for
        // the host to abort it. None of that is this job's material.
        Arrive(machine, 3, Stranger);
        Assert.True(machine.Carriers.BeginAccess(Stranger).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(Stranger).IsAccepted);

        Assert.Equal(1, stalls);
        Assert.Equal(1, readied);
    }

    [Fact]
    public void A_fab_that_counts_a_stopped_carrier_gets_a_completed_lot()
    {
        var machine = Build(options => options.CountsStoppedCarriers = true, attach: true);
        var stalls = 0;

        machine.Bridge.Stalled += (_, _) => stalls++;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        machine.Bridge.ClaimCarriers(JobName);

        Assert.True(machine.Carriers.EndAccess(First).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(Second, stopped: true).IsAccepted);

        Assert.Equal(0, stalls);
        Assert.Equal(ControlJobState.Completed, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_carrier_still_in_access_is_not_a_stall()
    {
        var machine = Build(attach: true);
        var noise = 0;

        machine.Bridge.Finished += (_, _) => noise++;
        machine.Bridge.Stalled += (_, _) => noise++;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        machine.Bridge.ClaimCarriers(JobName);

        Assert.True(machine.Carriers.EndAccess(First, stopped: true).IsAccepted);

        // One stopped and one still being worked on. There is something to wait for, so there is
        // nothing to say yet.
        Assert.Equal(0, noise);
        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
    }

    [Fact]
    public void A_paused_job_has_not_finished_and_E94_says_so()
    {
        var machine = Build(attach: true);
        ControlJobCarrierResult? seen = null;

        machine.Bridge.Finished += (_, arguments) => seen = arguments.Result;

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        machine.Bridge.ClaimCarriers(JobName);
        Assert.True(machine.Jobs.Pause(JobName).IsAccepted);

        Assert.True(machine.Carriers.EndAccess(First).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(Second).IsAccepted);

        Assert.NotNull(seen);
        Assert.False(seen!.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Paused), seen.Reason, StringComparison.Ordinal);
        Assert.Equal(ControlJobState.Paused, machine.Jobs.Find(JobName)!.State);
    }

    // ------------------------------------------------------------ the direction that is called

    [Fact]
    public void Claiming_puts_every_carrier_of_a_running_job_into_access()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);

        var answer = machine.Bridge.ClaimCarriers(JobName);

        Assert.True(answer.IsAccepted);
        Assert.Equal((string[])[First, Second], answer.Carriers);
        Assert.Empty(answer.Refused);
        Assert.Equal(CarrierAccessStatus.InAccess, machine.Carriers.FindCarrier(First)!.AccessStatus);
        Assert.Equal(CarrierAccessStatus.InAccess, machine.Carriers.FindCarrier(Second)!.AccessStatus);
    }

    [Fact]
    public void A_job_that_has_not_started_does_not_own_its_material()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        var answer = machine.Bridge.ClaimCarriers(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Selected), answer.Reason, StringComparison.Ordinal);
        Assert.Equal(CarrierAccessStatus.NotAccessed, machine.Carriers.FindCarrier(First)!.AccessStatus);
    }

    [Fact]
    public void A_carrier_already_in_access_is_reported_and_the_rest_are_taken()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        Assert.True(machine.Carriers.BeginAccess(First).IsAccepted);

        var answer = machine.Bridge.ClaimCarriers(JobName);

        // Accepted, because something was attempted; the per-carrier truth is in the two lists.
        Assert.True(answer.IsAccepted);
        Assert.Equal(Second, Assert.Single(answer.Carriers));
        Assert.Contains(nameof(CarrierAccessStatus.InAccess), answer.Refused[First], StringComparison.Ordinal);
    }

    [Fact]
    public void A_completed_job_leaves_its_carriers_complete()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        machine.Bridge.ClaimCarriers(JobName);
        Assert.True(machine.Jobs.MaterialFinished(JobName).IsAccepted);

        var answer = machine.Bridge.ReleaseCarriers(JobName);

        Assert.Equal((string[])[First, Second], answer.Carriers);
        Assert.Equal(CarrierAccessStatus.CarrierComplete, machine.Carriers.FindCarrier(First)!.AccessStatus);
        Assert.Equal(CarrierAccessStatus.CarrierComplete, machine.Carriers.FindCarrier(Second)!.AccessStatus);
    }

    [Fact]
    public void An_aborted_job_leaves_them_stopped()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        machine.Bridge.ClaimCarriers(JobName);
        Assert.True(machine.Jobs.Command(JobName, ControlJobCommands.Abort).IsAccepted);

        machine.Bridge.ReleaseCarriers(JobName);

        // The one mapping that needed no choosing: a job that did not complete did not complete
        // its carriers either.
        Assert.Equal(CarrierAccessStatus.CarrierStopped, machine.Carriers.FindCarrier(First)!.AccessStatus);
        Assert.Equal(CarrierAccessStatus.CarrierStopped, machine.Carriers.FindCarrier(Second)!.AccessStatus);
    }

    [Fact]
    public void That_mapping_is_replaceable()
    {
        var machine = Build(options => options.EndsCarriersEarly = _ => false);

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        machine.Bridge.ClaimCarriers(JobName);
        Assert.True(machine.Jobs.Command(JobName, ControlJobCommands.Abort).IsAccepted);

        machine.Bridge.ReleaseCarriers(JobName);

        Assert.Equal(CarrierAccessStatus.CarrierComplete, machine.Carriers.FindCarrier(First)!.AccessStatus);
    }

    [Fact]
    public void A_cancelled_job_never_took_its_carriers_and_E87_says_so()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Jobs.Command(JobName, ControlJobCommands.Cancel).IsAccepted);
        Assert.Equal(ControlJobCompletion.Cancelled, machine.Jobs.Find(JobName)!.Completion);

        var answer = machine.Bridge.ReleaseCarriers(JobName);

        // Nothing to let go of, and that refusal is reported rather than smoothed over: a
        // cancelled job with a carrier in access would be a machine telling two stories, and this
        // is where it would show up.
        Assert.True(answer.IsAccepted);
        Assert.Empty(answer.Carriers);
        Assert.Equal(2, answer.Refused.Count);
        Assert.Contains(nameof(CarrierAccessStatus.NotAccessed), answer.Refused[First], StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_job_does_not_let_its_material_go()
    {
        var machine = Build();

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);
        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        machine.Bridge.ClaimCarriers(JobName);

        var answer = machine.Bridge.ReleaseCarriers(JobName);

        Assert.False(answer.IsAccepted);
        Assert.Contains(nameof(ControlJobState.Executing), answer.Reason, StringComparison.Ordinal);
        Assert.Equal(CarrierAccessStatus.InAccess, machine.Carriers.FindCarrier(First)!.AccessStatus);
    }

    [Fact]
    public void A_job_naming_no_carrier_has_nothing_to_claim_or_release()
    {
        var machine = Build(carriers: []);

        Assert.True(machine.Jobs.MaterialReady(JobName).IsAccepted);

        var claimed = machine.Bridge.ClaimCarriers(JobName);

        Assert.False(claimed.IsAccepted);
        Assert.Contains("names no carrier", claimed.Reason, StringComparison.Ordinal);

        Assert.True(machine.Jobs.MaterialFinished(JobName).IsAccepted);

        var released = machine.Bridge.ReleaseCarriers(JobName);

        Assert.False(released.IsAccepted);
        Assert.Contains("names no carrier", released.Reason, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the arguments

    [Fact]
    public void A_bridge_needs_both_ends()
    {
        Assert.Throws<ArgumentNullException>(() => new ControlJobCarrierBridge(null!, new ControlJobManager()));
        Assert.Throws<ArgumentNullException>(() => new ControlJobCarrierBridge(new CarrierManager(), null!));

        // And the conventions are optional, which is the whole claim of a default.
        var bridge = new ControlJobCarrierBridge(new CarrierManager(), new ControlJobManager());

        Assert.True(bridge.Options.IsCarrier("anything at all"));
        Assert.True(bridge.Options.RequiresCarrierAtPort);
        Assert.True(bridge.Options.ReadiesWhenCarriersVerified);
        Assert.True(bridge.Options.FinishesWhenCarriersComplete);
        Assert.False(bridge.Options.CountsStoppedCarriers);
        Assert.False(bridge.IsAttached);
    }

    [Fact]
    public void Every_entry_point_rejects_a_null_job()
    {
        var bridge = Build().Bridge;

        Assert.Throws<ArgumentNullException>(() => bridge.Check(null!));
        Assert.Throws<ArgumentNullException>(() => bridge.Ready(null!));
        Assert.Throws<ArgumentNullException>(() => bridge.Finish(null!));
        Assert.Throws<ArgumentNullException>(() => bridge.ClaimCarriers(null!));
        Assert.Throws<ArgumentNullException>(() => bridge.ReleaseCarriers(null!));
    }

    [Fact]
    public void The_default_ending_map_is_the_one_E87_has_two_endings_for()
    {
        var options = new ControlJobCarrierOptions();

        Assert.False(options.EndsCarriersEarly(ControlJobCompletion.Completed));
        Assert.True(options.EndsCarriersEarly(ControlJobCompletion.Aborted));
        Assert.True(options.EndsCarriersEarly(ControlJobCompletion.Stopped));
        Assert.True(options.EndsCarriersEarly(ControlJobCompletion.Cancelled));
    }

    [Fact]
    public void Either_half_of_the_listening_can_be_turned_off()
    {
        var machine = Build(
            options =>
            {
                options.ReadiesWhenCarriersVerified = false;
                options.FinishesWhenCarriersComplete = false;
            },
            attach: true);

        Arrive(machine, 1, First);
        Arrive(machine, 2, Second);

        // Listening and doing nothing, which is a legitimate way to drive it by hand.
        Assert.True(machine.Bridge.IsAttached);
        Assert.Equal(ControlJobState.Selected, machine.Jobs.Find(JobName)!.State);

        Assert.True(machine.Bridge.Ready(JobName).IsAccepted);
        machine.Bridge.ClaimCarriers(JobName);
        Assert.True(machine.Carriers.EndAccess(First).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(Second).IsAccepted);

        Assert.Equal(ControlJobState.Executing, machine.Jobs.Find(JobName)!.State);
        Assert.True(machine.Bridge.Finish(JobName).IsAccepted);
        Assert.Equal(ControlJobState.Completed, machine.Jobs.Find(JobName)!.State);
    }
}
