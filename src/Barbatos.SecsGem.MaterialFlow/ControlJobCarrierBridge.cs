// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.ControlJobs;

namespace Barbatos.SecsGem.MaterialFlow;

/// <summary>
/// Tells a control job when the carriers it named have arrived and when they are finished with,
/// and tells those carriers when the job has taken them and let them go.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is.</strong> E94 gives a control job two transitions the machine makes rather
/// than the host: its material is ready, and its material is all dealt with. The E94 package has
/// both of them - <see cref="ControlJobManager.MaterialReady"/> and
/// <see cref="ControlJobManager.MaterialFinished"/> - and nothing that calls either, because E94
/// does not say what the material is. E87 does. This bridge is the one sentence joining them: a
/// job's material is ready when every carrier it names is at a port with its identifier and its
/// slot map agreed.
/// </para>
/// <para>
/// A control job is a lot and a carrier is a box, and this package said for two releases that
/// correlating them had more than one sensible answer. It still has, in the parts a fab has to
/// decide: whether a job may name material E87 has never heard of, whether a carrier has to be at
/// a port, what a stopped carrier does to a lot. Those are
/// <see cref="ControlJobCarrierOptions"/>. What turned out to need no choosing is the shape -
/// E94 asks "is the material ready", E87 is the only thing in this stack that can answer, and the
/// answer is a quorum over a list of identifiers that E94 already keeps.
/// </para>
/// <para>
/// <strong>Only one direction listens, and that is the whole of this bridge's design.</strong>
/// <see cref="Attach"/> subscribes to the carrier manager and to nothing else. The other
/// direction - a job that has ended releasing its carriers - is
/// <see cref="ClaimCarriers"/> and <see cref="ReleaseCarriers"/>, which an application calls.
/// </para>
/// <para>
/// That is not tidiness. Both managers raise their events <em>inside their own lock</em> and both
/// say so: queue the work, do not call back in. A handler of
/// <see cref="CarrierManager.CarrierStateChanged"/> therefore holds E87's lock and takes E94's,
/// and a handler of <see cref="ControlJobManager.JobStateChanged"/> would hold E94's and take
/// E87's. On a machine those are two real threads - the link's receive loop carrying a host
/// command into the job manager, and the machine's own control software reading a carrier - so a
/// bridge that listened to both would be two locks taken in two orders, which is the one deadlock
/// a stack like this can build for itself. So the order here is <strong>carriers first, control
/// jobs last, always</strong>, and the other direction holds no lock at all when it starts:
/// <see cref="ReleaseCarriers"/> reads the job, the job manager's lock is released, and only then
/// does it call E87.
/// </para>
/// <para>
/// An application that wants an ending to reach the carriers by itself puts the job identifier on
/// a queue from its own <c>JobStateChanged</c> handler and calls
/// <see cref="ReleaseCarriers"/> from outside it - which is what both managers' own documentation
/// already asks of every handler, and the reason this bridge does not pretend to do it for them.
/// </para>
/// </remarks>
public sealed class ControlJobCarrierBridge
{
    private readonly CarrierManager _carriers;
    private readonly ControlJobManager _jobs;
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private EventHandler<CarrierStateChangedEventArgs>? _handler;

    /// <summary>Joins a carrier manager to a control job manager.</summary>
    /// <param name="carriers">E87's state machines.</param>
    /// <param name="jobs">E94's state machine.</param>
    /// <param name="options">The conventions. <see langword="null"/> takes the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="carriers"/> or <paramref name="jobs"/> is <see langword="null"/>.</exception>
    public ControlJobCarrierBridge(
        CarrierManager carriers,
        ControlJobManager jobs,
        ControlJobCarrierOptions? options = null)
    {
        _carriers = carriers ?? throw new ArgumentNullException(nameof(carriers));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        Options = options ?? new ControlJobCarrierOptions();
    }

    /// <summary>Raised when a job's carriers were all ready and E94 was told so.</summary>
    /// <remarks>
    /// <para>
    /// Raised when the quorum is met, whatever E94 then said - so a job that refused the
    /// transition on its own grounds comes out here with
    /// <see cref="ControlJobCarrierResult.IsAccepted"/> false and E94's words in
    /// <see cref="ControlJobCarrierResult.Reason"/>. That is the actionable case: the material
    /// arrived and the job would not take it.
    /// </para>
    /// <para>
    /// It is <em>not</em> raised while the material is still arriving, which would be every
    /// carrier transition on the machine. Raised on whichever thread did the work, and under
    /// <see cref="Attach"/> that is inside the carrier manager's lock. Queue the work.
    /// </para>
    /// </remarks>
    public event EventHandler<ControlJobCarrierEventArgs>? Readied;

    /// <summary>Raised when a job's carriers were all finished with and E94 was told so.</summary>
    public event EventHandler<ControlJobCarrierEventArgs>? Finished;

    /// <summary>Raised when a job's carriers are all accounted for and one of them was stopped.</summary>
    /// <remarks>
    /// <para>
    /// The quorum is met in fact and this bridge will not call it a completed lot: E94's
    /// machine-side ending records <see cref="ControlJobCompletion.Completed"/>, and a carrier
    /// that was stopped says the opposite. So the job stays where it is and waits for the host to
    /// stop or abort it, which is the ending that tells the truth.
    /// </para>
    /// <para>
    /// This is said out loud because the alternative is a job that quietly never ends. A machine
    /// whose fab counts a stopped carrier as dealt with sets
    /// <see cref="ControlJobCarrierOptions.CountsStoppedCarriers"/>, and then this is never
    /// raised and <see cref="Finished"/> is.
    /// </para>
    /// </remarks>
    public event EventHandler<ControlJobCarrierEventArgs>? Stalled;

    /// <summary>Gets the conventions this bridge follows.</summary>
    public ControlJobCarrierOptions Options { get; }

    /// <summary>Gets a value indicating whether this bridge is listening to the carrier manager.</summary>
    /// <remarks>There is nothing to ask about the control job manager: this bridge never subscribes to it, and the class remarks say why.</remarks>
    public bool IsAttached => _handler is not null;

    /// <summary>Listens to the carrier manager, so a job's material announces itself.</summary>
    /// <remarks>
    /// <para>
    /// Attaching twice does nothing the second time. What it listens for is in
    /// <see cref="ControlJobCarrierOptions.ReadiesWhenCarriersVerified"/> and
    /// <see cref="ControlJobCarrierOptions.FinishesWhenCarriersComplete"/>; with both off it
    /// listens and does nothing, which is a legitimate way to use <see cref="Ready"/> and
    /// <see cref="Finish"/> by hand.
    /// </para>
    /// <para>
    /// <strong>One case it cannot see, and it is the price of the lock order.</strong> A job
    /// selected when its carriers are <em>already</em> at their ports and agreed has no carrier
    /// transition left to happen, so nothing wakes this bridge and the job sits in
    /// <see cref="ControlJobState.Selected"/>. Noticing it would mean listening to the control job
    /// manager for the selection, which is the direction that cannot listen - see the class
    /// remarks. So an application calls <see cref="Ready"/> once after selecting a job. It is
    /// quiet when the material is not there, so the call is unconditional and costs a dictionary
    /// lookup.
    /// </para>
    /// </remarks>
    public void Attach()
    {
        lock (_gate)
        {
            if (_handler is not null)
            {
                return;
            }

            _handler = OnCarrierStateChanged;
            _carriers.CarrierStateChanged += _handler;
        }
    }

    /// <summary>Stops listening.</summary>
    /// <remarks>Whatever it has already told either side stays told: a bridge that unwound a running lot on being detached would be a worse surprise than one that did not.</remarks>
    public void Detach()
    {
        lock (_gate)
        {
            if (_handler is null)
            {
                return;
            }

            _carriers.CarrierStateChanged -= _handler;
            _handler = null;
        }
    }

    /// <summary>Asks whether a control job's material is ready, and changes nothing.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether every carrier it names is ready, and which are not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// What a host calls before telling a machine to select a job, and what an operator screen
    /// shows beside a job that is not starting: the answer names the carrier that is holding it
    /// up, which is the only part anybody can act on. It takes no view of the job's state - that
    /// is <see cref="Ready"/>'s business, and E94's.
    /// </remarks>
    public ControlJobCarrierResult Check(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        return _jobs.Find(controlJobId) is { } job
            ? Survey(job, ReadinessOf).Result
            : ControlJobCarrierResult.Denied(controlJobId, $"there is no control job '{controlJobId}'.");
    }

    /// <summary>Tells a control job its material is ready, if it is.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>What the carriers said, and then what the job said.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Quiet when the material is not ready yet: that is the ordinary case on a machine where
    /// carriers arrive one at a time, not an error. When it is ready, E94 is asked and whatever
    /// E94 answers is the answer - the rule about which state may become ready is not copied
    /// here, because a second copy of a rule is one that can be broken without a test noticing.
    /// </remarks>
    public ControlJobCarrierResult Ready(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        if (_jobs.Find(controlJobId) is not { } job)
        {
            return ControlJobCarrierResult.Denied(controlJobId, $"there is no control job '{controlJobId}'.");
        }

        var survey = Survey(job, ReadinessOf).Result;

        if (!survey.IsAccepted)
        {
            return survey;
        }

        var result = Answer(survey, _jobs.MaterialReady(controlJobId));

        Readied?.Invoke(this, new ControlJobCarrierEventArgs { Result = result });

        return result;
    }

    /// <summary>Tells a control job all its material has been dealt with, if it has.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>What the carriers said, and then what the job said.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The same shape as <see cref="Ready"/> at the other end, with one case of its own: every
    /// carrier accounted for and one of them stopped is not a finished lot, and it raises
    /// <see cref="Stalled"/> rather than passing in silence.
    /// </remarks>
    public ControlJobCarrierResult Finish(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        if (_jobs.Find(controlJobId) is not { } job)
        {
            return ControlJobCarrierResult.Denied(controlJobId, $"there is no control job '{controlJobId}'.");
        }

        var (survey, stopped, unsettled) = Survey(job, CompletionOf);

        if (!survey.IsAccepted)
        {
            // Nothing left to wait for and something that went wrong: the only state in which a
            // job's material will never account for itself, and the one worth interrupting for.
            if (stopped > 0 && unsettled == 0)
            {
                Stalled?.Invoke(this, new ControlJobCarrierEventArgs { Result = survey });
            }

            return survey;
        }

        var result = Answer(survey, _jobs.MaterialFinished(controlJobId));

        Finished?.Invoke(this, new ControlJobCarrierEventArgs { Result = result });

        return result;
    }

    /// <summary>Puts the carriers a running job names into access.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Which carriers the machine took, and which E87 refused, and why.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Only for a job that is running, <see cref="ControlJob.IsActive"/> - a job that has not
    /// started does not own its material yet, and that is this bridge's rule rather than either
    /// standard's: E87 knows nothing about jobs, and E94 does not know a carrier has an access
    /// state. A paused job's carriers are already in access, so calling this again is E87's own
    /// refusal, per carrier, reported rather than hidden.
    /// </para>
    /// <para>
    /// Carriers are taken one at a time and a refusal does not stop the rest, the same rule the
    /// other bridges in this package keep: a job of four carriers where one is already in access
    /// is still three the machine has to take, and rolling the three back to tidy up would lose
    /// the only evidence of the fourth.
    /// </para>
    /// </remarks>
    public ControlJobCarrierResult ClaimCarriers(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        if (_jobs.Find(controlJobId) is not { } job)
        {
            return ControlJobCarrierResult.Denied(controlJobId, $"there is no control job '{controlJobId}'.");
        }

        return !job.IsActive
            ? ControlJobCarrierResult.Denied(
                controlJobId,
                $"control job '{controlJobId}' is {job.State}; only a running job owns its material.")
            : Walk(job, carrierId => _carriers.BeginAccess(carrierId));
    }

    /// <summary>Ends access to the carriers a finished job named, the way that job ended.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Which carriers the machine let go, and which E87 refused, and why.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Only for a job that has ended and recorded how: releasing a running job's carriers would
    /// say the machine has finished material it is still working on. How it ended picks between
    /// E87's two endings through
    /// <see cref="ControlJobCarrierOptions.EndsCarriersEarly"/>, whose default needs no choosing -
    /// a completed job leaves its carriers complete and every other ending leaves them stopped.
    /// </para>
    /// <para>
    /// A cancelled job is the interesting one. It never ran, so its carriers were never in access
    /// and E87 refuses to end an access that never began - every carrier comes back in
    /// <see cref="ControlJobCarrierResult.Refused"/> with E87 saying so, which is correct and is
    /// worth reading rather than ignoring: a cancelled job whose carrier <em>is</em> in access is
    /// a machine telling two stories, and this is where that shows up.
    /// </para>
    /// </remarks>
    public ControlJobCarrierResult ReleaseCarriers(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        if (_jobs.Find(controlJobId) is not { } job)
        {
            return ControlJobCarrierResult.Denied(controlJobId, $"there is no control job '{controlJobId}'.");
        }

        if (job.Completion is not { } completion)
        {
            return ControlJobCarrierResult.Denied(
                controlJobId,
                $"control job '{controlJobId}' is {job.State}; only a job that has ended lets its material go.");
        }

        var stopped = Options.EndsCarriersEarly(completion);

        return Walk(job, carrierId => _carriers.EndAccess(carrierId, stopped));
    }

    // --------------------------------------------------------------- the pieces

    /// <summary>Where one of a job's identifiers stands against what was asked of it.</summary>
    /// <remarks>
    /// <see cref="Foreign"/> and <see cref="Unsettled"/> are the distinction this bridge turns on,
    /// and the carrier manager cannot draw it: an identifier it is not responsible for and a
    /// carrier that has not arrived yet are both simply absent from it. Only
    /// <see cref="ControlJobCarrierOptions.IsCarrier"/> can say which, and until it does
    /// everything absent is a carrier on its way.
    /// </remarks>
    private enum Standing
    {
        /// <summary>It satisfies the question.</summary>
        Satisfied = 0,

        /// <summary>It is not this carrier manager's material, so it is nobody's business here.</summary>
        Foreign = 1,

        /// <summary>It is ours and does not satisfy the question yet - it may not even have arrived.</summary>
        Unsettled = 2,

        /// <summary>It is ours and never will: it was stopped.</summary>
        Stopped = 3,
    }

    private static ControlJobCarrierResult Answer(ControlJobCarrierResult survey, ControlJobResult moved)
        => new()
        {
            IsAccepted = moved.IsAccepted,
            Reason = moved.Reason,
            ControlJobId = survey.ControlJobId,
            Carriers = survey.Carriers,
            Refused = survey.Refused,
        };

    /// <summary>The one copy of the rule that a job naming no material is not a job this bridge can speak for.</summary>
    /// <remarks>
    /// A job with an empty carrier list would otherwise pass every quorum here by being empty, and
    /// vacuous truth starting a lot is exactly what a bridge like this must not do.
    /// </remarks>
    private static ControlJobCarrierResult? NamesNothing(ControlJob job)
        => job.Carriers.Count == 0
            ? ControlJobCarrierResult.Denied(job.ControlJobId, $"control job '{job.ControlJobId}' names no carrier.")
            : null;

    private (ControlJobCarrierResult Result, int Stopped, int Unsettled) Survey(
        ControlJob job,
        Func<string, (Standing Standing, string Reason)> standing)
    {
        if (NamesNothing(job) is { } empty)
        {
            return (empty, 0, 0);
        }

        List<string> satisfied = [];
        Dictionary<string, string> refused = new(StringComparer.Ordinal);
        var stopped = 0;
        var unsettled = 0;

        foreach (var carrierId in job.Carriers)
        {
            var (verdict, reason) = standing(carrierId);

            if (verdict is Standing.Satisfied)
            {
                satisfied.Add(carrierId);
                continue;
            }

            refused[carrierId] = reason;

            switch (verdict)
            {
                // Somebody else's material, which the fab named as such. It is still reported,
                // because a job that went ahead over material nothing here can see is worth
                // being able to find out about.
                case Standing.Foreign:
                    break;

                case Standing.Stopped:
                    stopped++;
                    break;

                default:
                    unsettled++;
                    break;
            }
        }

        // Having nothing in the way is not the same as having something to go on: a job whose
        // every identifier the fab called somebody else's has told this bridge nothing at all,
        // and it would otherwise pass for ready on an empty quorum.
        var accepted = stopped == 0 && unsettled == 0 && satisfied.Count > 0;

        var result = new ControlJobCarrierResult
        {
            IsAccepted = accepted,
            Reason = accepted
                ? string.Empty
                : $"control job '{job.ControlJobId}' has {satisfied.Count} of {job.Carriers.Count} carrier(s) "
                    + $"accounted for: {string.Join(" ", refused.Values)}",
            ControlJobId = job.ControlJobId,
            Carriers = satisfied,
            Refused = refused,
        };

        return (result, stopped, unsettled);
    }

    private ControlJobCarrierResult Walk(ControlJob job, Func<string, CarrierActionResult> act)
    {
        if (NamesNothing(job) is { } empty)
        {
            return empty;
        }

        List<string> done = [];
        Dictionary<string, string> refused = new(StringComparer.Ordinal);

        foreach (var carrierId in job.Carriers)
        {
            var result = act(carrierId);

            if (result.IsAccepted)
            {
                done.Add(carrierId);
            }
            else
            {
                refused[carrierId] = result.ErrorText;
            }
        }

        return new ControlJobCarrierResult
        {
            IsAccepted = true,
            ControlJobId = job.ControlJobId,
            Carriers = done,
            Refused = refused,
        };
    }

    private (Standing Standing, string Reason) ReadinessOf(string carrierId)
    {
        if (!Options.IsCarrier(carrierId))
        {
            return (Standing.Foreign, $"'{carrierId}' is not material this carrier manager tracks.");
        }

        // Ours and absent, which is a carrier on its way and not a carrier that does not exist:
        // the two are the same thing to E87 and the opposite of each other to a lot.
        if (_carriers.FindCarrier(carrierId) is not { } carrier)
        {
            return (Standing.Unsettled, $"carrier '{carrierId}' has not arrived.");
        }

        if (!carrier.IsVerified)
        {
            return (
                Standing.Unsettled,
                $"carrier '{carrierId}' is not verified: identifier {carrier.IdStatus}, slot map {carrier.SlotMapStatus}.");
        }

        return Options.RequiresCarrierAtPort && carrier.PortId is null
            ? (Standing.Unsettled, $"carrier '{carrierId}' is verified and is not at a load port.")
            : (Standing.Satisfied, string.Empty);
    }

    private (Standing Standing, string Reason) CompletionOf(string carrierId)
    {
        if (!Options.IsCarrier(carrierId))
        {
            return (Standing.Foreign, $"'{carrierId}' is not material this carrier manager tracks.");
        }

        if (_carriers.FindCarrier(carrierId) is not { } carrier)
        {
            return (Standing.Unsettled, $"carrier '{carrierId}' has not arrived.");
        }

        return carrier.AccessStatus switch
        {
            CarrierAccessStatus.CarrierComplete => (Standing.Satisfied, string.Empty),

            CarrierAccessStatus.CarrierStopped when Options.CountsStoppedCarriers
                => (Standing.Satisfied, string.Empty),

            CarrierAccessStatus.CarrierStopped
                => (Standing.Stopped, $"carrier '{carrierId}' is {CarrierAccessStatus.CarrierStopped}, which is not a lot anybody finished."),

            _ => (Standing.Unsettled, $"carrier '{carrierId}' is {carrier.AccessStatus}."),
        };
    }

    private void OnCarrierStateChanged(object? sender, CarrierStateChangedEventArgs arguments)
    {
        // Which job, if any, this carrier belongs to. At most one job is selected or running at a
        // time - E94's own rule, enforced by the job manager - so there is nothing to choose
        // between, and a carrier no running job named is none of this bridge's business.
        if (_jobs.Current is not { } job || !job.Carriers.Contains(arguments.Carrier.CarrierId))
        {
            return;
        }

        switch (arguments.StateMachine)
        {
            // Every verification transition asks and Ready decides: the quorum lives in one
            // place, and checking the status here as well would be a second copy of it.
            case CarrierStateMachine.IdStatus or CarrierStateMachine.SlotMapStatus
                when Options.ReadiesWhenCarriersVerified:
                Ready(job.ControlJobId);
                break;

            // Both endings, because both mean the machine has let go of that carrier, and what a
            // stopped one does to the lot is Finish's answer rather than this switch's.
            case CarrierStateMachine.AccessStatus
                when Options.FinishesWhenCarriersComplete
                    && (CarrierAccessStatus)arguments.Current
                        is CarrierAccessStatus.CarrierComplete or CarrierAccessStatus.CarrierStopped:
                Finish(job.ControlJobId);
                break;

            default:
                break;
        }
    }
}
