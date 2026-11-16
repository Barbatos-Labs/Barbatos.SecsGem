// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;

namespace Barbatos.SecsGem.ControlJobs;

/// <summary>
/// The control jobs a machine has, and E94's rules about what may happen to them.
/// </summary>
/// <remarks>
/// <para>
/// No connection and no messages, the same shape E87's manager and E90's tracker have: E94's
/// commands arrive as object actions on stream 14, so the state machine never sees a wire and can
/// be tested exhaustively without one.
/// </para>
/// <para>
/// <strong>What is enforced.</strong> A job can only be started from
/// <see cref="ControlJobState.WaitingForStart"/>, paused from
/// <see cref="ControlJobState.Executing"/> and resumed from
/// <see cref="ControlJobState.Paused"/>; it can only be cancelled before it has run and only
/// aborted or stopped after it has; a finished job cannot be moved at all; and a job cannot be
/// selected while another one is already selected or running, because a machine runs one control
/// job at a time. That last rule is the one most worth having: it is the difference between a
/// queue and a race.
/// </para>
/// <para>
/// Thread safety: one lock around every transition, with the events raised inside it after the
/// state has moved. A handler must queue rather than call back in.
/// </para>
/// </remarks>
public sealed class ControlJobManager
{
    private readonly Dictionary<string, ControlJob> _jobs = new(StringComparer.Ordinal);
    private readonly List<string> _queue = [];
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    /// <summary>Raised after a control job moves.</summary>
    /// <remarks>Raised inside the manager's lock. Queue the work; do not call back in.</remarks>
    public event EventHandler<ControlJobStateChangedEventArgs>? JobStateChanged;

    /// <summary>Gets the jobs this machine knows about.</summary>
    public IReadOnlyCollection<ControlJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Values];
            }
        }
    }

    /// <summary>Gets the jobs waiting their turn, in the order they will be offered.</summary>
    /// <remarks>
    /// E94 lets a host reorder the queue, and a queue with no order is not one - so this is a list
    /// rather than a set, and <see cref="Reorder"/> is how a host changes it.
    /// </remarks>
    public IReadOnlyList<string> Queue
    {
        get
        {
            lock (_gate)
            {
                return [.. _queue];
            }
        }
    }

    /// <summary>Gets the job that is selected or running, or <see langword="null"/>.</summary>
    /// <remarks>At most one: a machine runs one control job at a time, which is what makes the queue mean anything.</remarks>
    public ControlJob? Current
    {
        get
        {
            lock (_gate)
            {
                return FindCurrent();
            }
        }
    }

    /// <summary>Creates a control job, queued.</summary>
    /// <param name="controlJobId">What to call it.</param>
    /// <param name="carriers">The material it is for.</param>
    /// <param name="startMethod">Who starts it once its material is ready.</param>
    /// <param name="processOrder">How the material is to be worked through.</param>
    /// <param name="processingSpecification">What recipe it carries.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A job starts <see cref="ControlJobState.Queued"/>, which is the only state E94 lets one
    /// begin in, and goes to the back of the queue. A host creates one over the wire with S14F5.
    /// </remarks>
    public ControlJobResult Create(
        string controlJobId,
        IReadOnlyList<string>? carriers = null,
        ControlJobStartMethod startMethod = ControlJobStartMethod.Automatic,
        string processOrder = "",
        string processingSpecification = "")
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        if (controlJobId.Length == 0)
        {
            return ControlJobResult.Refused("a control job needs an identifier.");
        }

        lock (_gate)
        {
            if (_jobs.ContainsKey(controlJobId))
            {
                return ControlJobResult.Refused($"control job '{controlJobId}' already exists.");
            }

            _jobs[controlJobId] = new ControlJob(controlJobId)
            {
                Carriers = carriers is null ? [] : [.. carriers],
                StartMethod = startMethod,
                ProcessOrder = processOrder,
                ProcessingSpecification = processingSpecification,
            };

            _queue.Add(controlJobId);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Forgets a finished job.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only once it is over. Deleting a job that is still running would leave the material it owns
    /// with nothing to account for it - and a host that wants it gone sooner cancels or aborts it
    /// first, which is a decision rather than a tidy-up.
    /// </remarks>
    public ControlJobResult Delete(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (!job.IsFinished)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only a finished one can be deleted.");
            }

            _jobs.Remove(controlJobId);
            _queue.Remove(controlJobId);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Finds a job.</summary>
    /// <param name="controlJobId">Which one.</param>
    /// <returns>The job, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    public ControlJob? Find(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            return _jobs.GetValueOrDefault(controlJobId);
        }
    }

    /// <summary>Puts the queue in the order given.</summary>
    /// <param name="controlJobIds">The queued jobs, in the order they should be offered.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobIds"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Every queued job must appear exactly once: a reorder that left one out would be a deletion
    /// wearing a reorder's clothes, and one that named a job twice has no meaning at all. A job
    /// that is selected or running is not in the queue and naming it is refused.
    /// </remarks>
    public ControlJobResult Reorder(IReadOnlyList<string> controlJobIds)
    {
        ArgumentNullException.ThrowIfNull(controlJobIds);

        lock (_gate)
        {
            if (controlJobIds.Count != _queue.Count)
            {
                return ControlJobResult.Refused(
                    $"the queue holds {_queue.Count} job(s) and the new order names {controlJobIds.Count}.");
            }

            HashSet<string> named = new(StringComparer.Ordinal);

            foreach (var controlJobId in controlJobIds)
            {
                if (!named.Add(controlJobId))
                {
                    return ControlJobResult.Refused($"control job '{controlJobId}' is named twice.");
                }

                if (!_queue.Contains(controlJobId))
                {
                    return ControlJobResult.Refused($"control job '{controlJobId}' is not in the queue.");
                }
            }

            _queue.Clear();
            _queue.AddRange(controlJobIds);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Changes what a queued job is for.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <param name="carriers">The material it is for, or <see langword="null"/> to leave it.</param>
    /// <param name="startMethod">Who starts it, or <see langword="null"/> to leave it.</param>
    /// <param name="processOrder">How the material is worked through, or <see langword="null"/> to leave it.</param>
    /// <param name="processingSpecification">What recipe it carries, or <see langword="null"/> to leave it.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only while the job is <see cref="ControlJobState.Queued"/>. A host may change its mind about
    /// a job that has not been chosen yet; changing the material under a job whose carriers are
    /// already being brought in is the kind of edit that loses wafers, so it is refused. This is
    /// what the settable attributes on <see cref="ControlJobObjectProvider"/> go through.
    /// </remarks>
    public ControlJobResult Describe(
        string controlJobId,
        IReadOnlyList<string>? carriers = null,
        ControlJobStartMethod? startMethod = null,
        string? processOrder = null,
        string? processingSpecification = null)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.Queued)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only a {ControlJobState.Queued} one can be changed.");
            }

            if (carriers is not null)
            {
                job.Carriers = [.. carriers];
            }

            if (startMethod is { } method)
            {
                job.StartMethod = method;
            }

            if (processOrder is not null)
            {
                job.ProcessOrder = processOrder;
            }

            if (processingSpecification is not null)
            {
                job.ProcessingSpecification = processingSpecification;
            }

            return ControlJobResult.Ok;
        }
    }

    // ------------------------------------------------------------- the commands

    /// <summary>Performs one of E94's commands.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <param name="command">What to do. See <see cref="ControlJobCommands"/>.</param>
    /// <returns>Whether it was allowed, with the refusing state named when it was not.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// A command this method does not know is refused rather than thrown, because <c>RCMD</c> is
    /// free text and a host is entitled to ask for something this library has never heard of.
    /// </remarks>
    public ControlJobResult Command(string controlJobId, string command)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);
        ArgumentNullException.ThrowIfNull(command);

        return command switch
        {
            ControlJobCommands.Select => Select(controlJobId),
            ControlJobCommands.Deselect => Deselect(controlJobId),
            ControlJobCommands.Start => Start(controlJobId),
            ControlJobCommands.Pause => Pause(controlJobId),
            ControlJobCommands.Resume => Resume(controlJobId),
            ControlJobCommands.Abort => Finish(controlJobId, ControlJobCompletion.Aborted),
            ControlJobCommands.Stop => Finish(controlJobId, ControlJobCompletion.Stopped),
            ControlJobCommands.Cancel => Finish(controlJobId, ControlJobCompletion.Cancelled),
            _ => ControlJobResult.Refused($"'{command}' is not a command this equipment knows."),
        };
    }

    /// <summary>Chooses a job to run next.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    public ControlJobResult Select(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.Queued)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only a {ControlJobState.Queued} one can be selected.");
            }

            // One at a time. A machine with two selected jobs has two things believing they own
            // the material, which is the fault this rule exists to make impossible.
            if (FindCurrent() is { } current)
            {
                return ControlJobResult.Refused(
                    $"control job '{current.ControlJobId}' is already {current.State}.");
            }

            _queue.Remove(controlJobId);
            Move(job, ControlJobState.Selected);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Puts a selected job back in the queue.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only from <see cref="ControlJobState.Selected"/> or
    /// <see cref="ControlJobState.WaitingForStart"/>. A running job is stopped or aborted, not
    /// deselected: deselecting one would leave material half processed with nothing recording why.
    /// It goes back to the front of the queue, because it was next a moment ago.
    /// </remarks>
    public ControlJobResult Deselect(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not (ControlJobState.Selected or ControlJobState.WaitingForStart))
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only a {ControlJobState.Selected} or "
                    + $"{ControlJobState.WaitingForStart} one can be deselected.");
            }

            _queue.Insert(0, controlJobId);
            Move(job, ControlJobState.Queued);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>The material a selected job needs is ready.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The machine's own transition rather than a host command - the carriers have arrived, the
    /// substrates are mapped - and the one place <see cref="ControlJobStartMethod"/> decides
    /// anything: an automatic job starts here, and a host-initiated one waits.
    /// </remarks>
    public ControlJobResult MaterialReady(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.Selected)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; material can only become ready for a "
                    + $"{ControlJobState.Selected} one.");
            }

            Move(
                job,
                job.StartMethod is ControlJobStartMethod.Automatic
                    ? ControlJobState.Executing
                    : ControlJobState.WaitingForStart);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Starts a job that is waiting for a host to say so.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    public ControlJobResult Start(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.WaitingForStart)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only one that is "
                    + $"{ControlJobState.WaitingForStart} can be started.");
            }

            Move(job, ControlJobState.Executing);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Holds a running job.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    public ControlJobResult Pause(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.Executing)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only an {ControlJobState.Executing} one can be paused.");
            }

            Move(job, ControlJobState.Paused);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>Lets a held job carry on.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    public ControlJobResult Resume(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.Paused)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only a {ControlJobState.Paused} one can be resumed.");
            }

            Move(job, ControlJobState.Executing);

            return ControlJobResult.Ok;
        }
    }

    /// <summary>All the job's material has been dealt with.</summary>
    /// <param name="controlJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The machine's own ending, as against the three a host can ask for. Only from
    /// <see cref="ControlJobState.Executing"/>: a paused job has not finished, it has stopped, and
    /// calling that completion would tell a host the lot is done.
    /// </remarks>
    public ControlJobResult MaterialFinished(string controlJobId)
    {
        ArgumentNullException.ThrowIfNull(controlJobId);

        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ControlJobState.Executing)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State}; only an {ControlJobState.Executing} one can finish.");
            }

            Finish(job, ControlJobCompletion.Completed);

            return ControlJobResult.Ok;
        }
    }

    // --------------------------------------------------------------- the pieces

    private ControlJobResult Finish(string controlJobId, ControlJobCompletion completion)
    {
        lock (_gate)
        {
            if (!TryFind(controlJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.IsFinished)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is already {job.State} ({job.Completion}).");
            }

            // Cancel is for a job that has not run and abort and stop are for one that has, which
            // is E94 keeping the record honest: a cancelled job touched no material.
            var hasRun = job.State is ControlJobState.Executing or ControlJobState.Paused;

            if (completion is ControlJobCompletion.Cancelled && hasRun)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State} and has run; abort or stop it rather than cancelling it.");
            }

            if (completion is not ControlJobCompletion.Cancelled && !hasRun)
            {
                return ControlJobResult.Refused(
                    $"control job '{controlJobId}' is {job.State} and has not run; cancel it rather than "
                    + $"{(completion is ControlJobCompletion.Aborted ? "aborting" : "stopping")} it.");
            }

            Finish(job, completion);

            return ControlJobResult.Ok;
        }
    }

    private void Finish(ControlJob job, ControlJobCompletion completion)
    {
        _queue.Remove(job.ControlJobId);
        job.Completion = completion;

        Move(job, ControlJobState.Completed, completion);
    }

    private ControlJob? FindCurrent()
    {
        foreach (var job in _jobs.Values)
        {
            if (job.State is ControlJobState.Selected
                or ControlJobState.WaitingForStart
                or ControlJobState.Executing
                or ControlJobState.Paused)
            {
                return job;
            }
        }

        return null;
    }

    private bool TryFind(
        string controlJobId,
        [NotNullWhen(true)] out ControlJob? job,
        out ControlJobResult refusal)
    {
        if (!_jobs.TryGetValue(controlJobId, out job))
        {
            refusal = ControlJobResult.Refused($"there is no control job '{controlJobId}'.");

            return false;
        }

        refusal = ControlJobResult.Ok;

        return true;
    }

    /// <summary>Moves a job and announces it, in that order.</summary>
    /// <remarks>In that order, so a handler reading the job sees the state the event announces.</remarks>
    private void Move(ControlJob job, ControlJobState next, ControlJobCompletion? completion = null)
    {
        if (job.State == next)
        {
            return;
        }

        var previous = job.State;
        job.State = next;

        JobStateChanged?.Invoke(this, new ControlJobStateChangedEventArgs
        {
            Job = job,
            Previous = previous,
            Current = next,
            Completion = completion,
        });
    }
}
