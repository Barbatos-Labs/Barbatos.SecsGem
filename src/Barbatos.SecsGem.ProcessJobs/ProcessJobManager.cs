// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>
/// The process jobs a machine has, and E40's rules about what may happen to them.
/// </summary>
/// <remarks>
/// <para>
/// No connection and no messages, the same shape E87's manager, E90's tracker and E94's manager
/// have - even though E40, unlike the last two, does have a message set of its own.
/// <see cref="ProcessJobEquipment"/> puts stream 16 in front of this, and keeping the two apart is
/// what lets the whole of E40's sequencing be tested without a wire.
/// </para>
/// <para>
/// <strong>Several jobs at once, on purpose.</strong> E94's manager refuses to select a second
/// control job, because a lot is one lot. A process job is one piece of work, and a tool with four
/// chambers runs four of them - so there is no "current job" here, and nothing stops two jobs
/// processing together. What is bounded instead is the queue: <see cref="JobSpace"/> is E40's
/// <c>PRJOBSPACE</c>, and a create past it is refused rather than quietly accepted by a machine
/// that cannot hold it.
/// </para>
/// <para>
/// Thread safety: one lock around every transition, with the events raised inside it after the
/// state has moved. A handler must queue rather than call back in.
/// </para>
/// </remarks>
public sealed class ProcessJobManager
{
    private readonly Dictionary<string, ProcessJob> _jobs = new(StringComparer.Ordinal);
    private readonly List<string> _queue = [];
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

    /// <summary>Raised after a process job moves.</summary>
    /// <remarks>
    /// Raised inside the manager's lock. Queue the work; do not call back in. This is also what
    /// <see cref="ProcessJobEquipment"/> turns into an S16F11, which is E40's own way of telling a
    /// host - no collection event, and no <c>CEID</c> for anybody to define.
    /// </remarks>
    public event EventHandler<ProcessJobStateChangedEventArgs>? JobStateChanged;

    /// <summary>Gets or sets how many jobs this machine can hold - <c>PRJOBSPACE</c>.</summary>
    /// <remarks>
    /// <see langword="null"/>, the default, is "no stated limit" rather than zero: a machine that
    /// has not said how many jobs it can hold has not said it can hold none. The item is
    /// <c>U2</c> in the catalogue, which is why this is a <see cref="ushort"/> and not an
    /// <see cref="int"/>.
    /// </remarks>
    public ushort? JobSpace { get; set; }

    /// <summary>Gets the jobs this machine knows about.</summary>
    public IReadOnlyCollection<ProcessJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return [.. _jobs.Values];
            }
        }
    }

    /// <summary>Gets the queued jobs, in the order they will be taken up.</summary>
    /// <remarks>
    /// Only the ones still <see cref="ProcessJobState.Queued"/>: a job the machine has taken up is
    /// no longer waiting its turn, which is what makes <see cref="QueuePosition"/> mean something.
    /// A snapshot, taken under the lock.
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

    /// <summary>Creates a process job, queued.</summary>
    /// <param name="processJobId">What to call it - <c>PRJOBID</c>.</param>
    /// <param name="recipeId">The recipe it runs - <c>PPID</c>.</param>
    /// <param name="material">The material it is for - <c>MTRLID</c>.</param>
    /// <param name="startsAutomatically">Whether the machine starts it once setup is done.</param>
    /// <param name="recipeMethod">Whether that recipe's variables may be tuned.</param>
    /// <param name="materialOrder">In what order the material is worked through.</param>
    /// <param name="pauseEventId">The collection event that pauses it, if any.</param>
    /// <param name="parameters">Its recipe parameters.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Four ways to be refused, and all four are things a host can put right: no identifier, no
    /// recipe, a name already taken, or a queue with no room. A job with no recipe is refused
    /// rather than created and left unrunnable - S16F1 carries a <c>PPID</c>, so a create without
    /// one is a create that is missing something, not a job to be filled in later.
    /// </remarks>
    public ProcessJobResult Create(
        string processJobId,
        string recipeId,
        IReadOnlyList<string>? material = null,
        bool startsAutomatically = true,
        ProcessJobRecipeMethod recipeMethod = ProcessJobRecipeMethod.WithoutVariableTuning,
        ProcessJobMaterialOrder materialOrder = ProcessJobMaterialOrder.ArrivalSequence,
        uint? pauseEventId = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(recipeId);

        if (processJobId.Length == 0)
        {
            return ProcessJobResult.Refused("a process job needs an identifier.");
        }

        if (recipeId.Length == 0)
        {
            return ProcessJobResult.Refused($"process job '{processJobId}' needs a recipe.");
        }

        lock (_gate)
        {
            if (_jobs.ContainsKey(processJobId))
            {
                return ProcessJobResult.Refused($"process job '{processJobId}' already exists.");
            }

            if (JobSpace is { } space && _jobs.Count >= space)
            {
                return ProcessJobResult.Refused(
                    $"this machine holds {_jobs.Count} of {space} process jobs.");
            }

            Dictionary<string, string> copied = new(StringComparer.Ordinal);

            if (parameters is not null)
            {
                foreach (var (name, value) in parameters)
                {
                    copied[name] = value;
                }
            }

            _jobs[processJobId] = new ProcessJob(processJobId)
            {
                RecipeId = recipeId,
                Material = material is null ? [] : [.. material],
                StartsAutomatically = startsAutomatically,
                RecipeMethod = recipeMethod,
                MaterialOrder = materialOrder,
                PauseEventId = pauseEventId,
                Parameters = copied,
            };

            _queue.Add(processJobId);

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Takes a job off the machine - S16F9's deselect.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A job that is still queued, or one that is over. Anything in between holds material, and a
    /// host that wants such a job gone cancels, stops or aborts it first - a decision, with a
    /// record of itself, rather than a tidy-up.
    /// </para>
    /// <para>
    /// Deselecting a queued job and cancelling one are not the same thing, and the difference is
    /// the record: a cancelled job stays on the machine as
    /// <see cref="ProcessJobState.ProcessComplete"/> with
    /// <see cref="ProcessJobCompletion.Cancelled"/>, where a host can read what became of it,
    /// while a deselected one is simply no longer there. Which of the two S16F9 means is the
    /// clearest guess in this package: the catalogue gives the message a name and no body.
    /// </para>
    /// </remarks>
    public ProcessJobResult Deselect(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not (ProcessJobState.Queued or ProcessJobState.ProcessComplete))
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; only a {ProcessJobState.Queued} or "
                    + $"{ProcessJobState.ProcessComplete} one can be deselected.");
            }

            _jobs.Remove(processJobId);
            _queue.Remove(processJobId);

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Finds a job.</summary>
    /// <param name="processJobId">Which one.</param>
    /// <returns>The job, or <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public ProcessJob? Find(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            return _jobs.GetValueOrDefault(processJobId);
        }
    }

    /// <summary>Where a job is in the queue - <c>QPOSN</c>.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Its place, counting from one, or zero for a job that is not waiting.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Counting from one, so zero can mean "not in the queue" - which is the answer for a job the
    /// machine has already taken up, and for one that is over. S16F20 has to say something, and a
    /// job that is processing is not at position one.
    /// </remarks>
    public ushort QueuePosition(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            var index = _queue.IndexOf(processJobId);

            return index < 0 ? (ushort)0 : (ushort)(index + 1);
        }
    }

    /// <summary>Puts the queue in the order given - S16F21.</summary>
    /// <param name="processJobIds">The queued jobs, in the order they should be taken up.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobIds"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Every queued job exactly once: a reorder that left one out would be a deletion wearing a
    /// reorder's clothes, one that named a job twice has no meaning, and one that named a job the
    /// machine has already taken up is asking for a wafer to go back in the pool.
    /// </remarks>
    public ProcessJobResult Reorder(IReadOnlyList<string> processJobIds)
    {
        ArgumentNullException.ThrowIfNull(processJobIds);

        lock (_gate)
        {
            // Each refusal carries the HCACK code that says which part was wrong, because S16F22
            // can say that and nothing else in E40 can: a list of the wrong length or with a name
            // twice is a bad parameter, and a job that is not in the queue is no such object.
            if (processJobIds.Count != _queue.Count)
            {
                return ProcessJobResult.Queue(
                    $"the queue holds {_queue.Count} job(s) and the new order names {processJobIds.Count}.",
                    ProcessJobQueueAcknowledgement.ParameterInvalid);
            }

            HashSet<string> named = new(StringComparer.Ordinal);

            foreach (var processJobId in processJobIds)
            {
                if (!named.Add(processJobId))
                {
                    return ProcessJobResult.Queue(
                        $"process job '{processJobId}' is named twice.",
                        ProcessJobQueueAcknowledgement.ParameterInvalid);
                }

                if (!_queue.Contains(processJobId))
                {
                    return ProcessJobResult.Queue(
                        $"process job '{processJobId}' is not in the queue.",
                        ProcessJobQueueAcknowledgement.NoSuchObject);
                }
            }

            _queue.Clear();
            _queue.AddRange(processJobIds);

            return ProcessJobResult.Ok;
        }
    }

    // --------------------------------------------------------- what a host asks

    /// <summary>Performs one of E40's commands - S16F3 and S16F23.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="command">What to do. See <see cref="ProcessJobCommands"/>.</param>
    /// <returns>Whether it was allowed, with the <c>CMDA</c> code that says which kind of no it is.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Any casing of a spelling this equipment knows; a command it does not know comes back as
    /// <see cref="ProcessJobCommandAcknowledgement.DoesNotExist"/> rather than as an exception,
    /// and one it knows but cannot do now as
    /// <see cref="ProcessJobCommandAcknowledgement.CannotPerformNow"/>. That distinction is
    /// E40's: <c>CMDA</c> is defined with both meanings, where E94 had one code for every no.
    /// </remarks>
    public ProcessJobResult Command(string processJobId, string command)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(command);

        return command.ToUpperInvariant() switch
        {
            ProcessJobCommands.Start => Start(processJobId),
            ProcessJobCommands.Pause => Pause(processJobId),
            ProcessJobCommands.Resume => Resume(processJobId),
            ProcessJobCommands.Stop => Stop(processJobId),
            ProcessJobCommands.Abort => Abort(processJobId),
            ProcessJobCommands.Cancel => Cancel(processJobId),
            _ => ProcessJobResult.Unknown($"'{command}' is not a command this equipment knows."),
        };
    }

    /// <summary>Starts a job that is waiting for a host to say so.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public ProcessJobResult Start(string processJobId)
        => Transition(
            processJobId,
            [ProcessJobState.WaitingForStart],
            ProcessJobState.Processing,
            "started");

    /// <summary>Asks a running job to pause.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Leaves the job <see cref="ProcessJobState.Pausing"/>, not <see cref="ProcessJobState.Paused"/>:
    /// a machine asked to pause has to get to a point where pausing is safe, and saying it is
    /// already there would be the state machine lying on the equipment's behalf. The machine calls
    /// <see cref="PauseComplete"/> when it arrives.
    /// </remarks>
    public ProcessJobResult Pause(string processJobId)
        => Transition(
            processJobId,
            [ProcessJobState.Processing],
            ProcessJobState.Pausing,
            "paused");

    /// <summary>Lets a held job carry on.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public ProcessJobResult Resume(string processJobId)
        => Transition(
            processJobId,
            [ProcessJobState.Paused],
            ProcessJobState.Processing,
            "resumed");

    /// <summary>Asks a job to stop once it has finished what is in progress.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// From <see cref="ProcessJobState.Processing"/>, <see cref="ProcessJobState.Paused"/> and also
    /// <see cref="ProcessJobState.Pausing"/>: a pause that has not landed yet is overtaken rather
    /// than waited for, because a host that has decided to stop has stopped caring where the pause
    /// got to. The machine calls <see cref="StopComplete"/> when the job is down.
    /// </remarks>
    public ProcessJobResult Stop(string processJobId)
        => Transition(
            processJobId,
            [ProcessJobState.Processing, ProcessJobState.Pausing, ProcessJobState.Paused],
            ProcessJobState.Stopping,
            "stopped");

    /// <summary>Asks a job to stop now.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// From every live state including <see cref="ProcessJobState.Stopping"/>, which is the one
    /// transition in this package put there by what goes wrong in a fab rather than by the shape of
    /// the standard: a tidy stop that hangs is exactly when somebody reaches for the other kind.
    /// Not from <see cref="ProcessJobState.Aborting"/>, which is already the other kind.
    /// </remarks>
    public ProcessJobResult Abort(string processJobId)
        => Transition(
            processJobId,
            [
                ProcessJobState.Processing,
                ProcessJobState.Pausing,
                ProcessJobState.Paused,
                ProcessJobState.Stopping,
            ],
            ProcessJobState.Aborting,
            "aborted");

    /// <summary>Calls a job off before it ever processes.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only before processing starts. A job that has run is stopped or aborted, so that what the
    /// record says about the material is true: a cancelled job touched none of it.
    /// </remarks>
    public ProcessJobResult Cancel(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not (ProcessJobState.Queued
                or ProcessJobState.SettingUp
                or ProcessJobState.WaitingForStart))
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State} and has processed; stop or abort it "
                    + "rather than cancelling it.");
            }

            Finish(job, ProcessJobCompletion.Cancelled);

            return ProcessJobResult.Ok;
        }
    }

    // ------------------------------------------------ what the machine does itself

    /// <summary>The machine takes a queued job up: recipe down, material in.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The machine's own transition, not a host command - which job to take up next is the
    /// machine's business, and the queue is only the order it was asked for them in. It leaves the
    /// queue here, so <see cref="QueuePosition"/> stops answering for it.
    /// </remarks>
    public ProcessJobResult BeginSetup(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ProcessJobState.Queued)
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; only a {ProcessJobState.Queued} one can be set up.");
            }

            _queue.Remove(processJobId);
            Move(job, ProcessJobState.SettingUp);

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Setup is done.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The one place <see cref="ProcessJob.StartsAutomatically"/> decides anything: a job that
    /// starts itself goes straight to <see cref="ProcessJobState.Processing"/>, and one that waits
    /// goes to <see cref="ProcessJobState.WaitingForStart"/> until a host sends <c>START</c>.
    /// </remarks>
    public ProcessJobResult SetupComplete(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ProcessJobState.SettingUp)
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; only a {ProcessJobState.SettingUp} one can finish setting up.");
            }

            Move(
                job,
                job.StartsAutomatically ? ProcessJobState.Processing : ProcessJobState.WaitingForStart);

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>The job has reached a point where it can be held.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public ProcessJobResult PauseComplete(string processJobId)
        => Transition(
            processJobId,
            [ProcessJobState.Pausing],
            ProcessJobState.Paused,
            "finished pausing");

    /// <summary>The job has stopped tidily.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public ProcessJobResult StopComplete(string processJobId)
        => Ending(processJobId, ProcessJobState.Stopping, ProcessJobCompletion.Stopped, "finished stopping");

    /// <summary>The job has stopped at once.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public ProcessJobResult AbortComplete(string processJobId)
        => Ending(processJobId, ProcessJobState.Aborting, ProcessJobCompletion.Aborted, "finished aborting");

    /// <summary>Every piece of the job's material has been through the recipe.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only from <see cref="ProcessJobState.Processing"/>. A paused job has not finished, and a job
    /// being stopped ends as <see cref="ProcessJobCompletion.Stopped"/> through
    /// <see cref="StopComplete"/> - calling either of those a completion would tell a host the
    /// material is done.
    /// </remarks>
    public ProcessJobResult MaterialProcessed(string processJobId)
        => Ending(processJobId, ProcessJobState.Processing, ProcessJobCompletion.Completed, "finished processing");

    // -------------------------------------------------- material and parameters

    /// <summary>Binds material to a job - S16F25.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="material">What it is for. Replaces whatever was there.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only before the job processes - <see cref="ProcessJobState.Queued"/> or
    /// <see cref="ProcessJobState.SettingUp"/>. Replacing the material under a job that is running
    /// is not a binding, it is a different job.
    /// </remarks>
    public ProcessJobResult BindMaterial(string processJobId, IReadOnlyList<string> material)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(material);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not (ProcessJobState.Queued or ProcessJobState.SettingUp))
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; material can only be bound to a "
                    + $"{ProcessJobState.Queued} or {ProcessJobState.SettingUp} one.");
            }

            job.Material = [.. material];

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Adds material to a job - S16F15.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="material">What to add.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// Up to and including <see cref="ProcessJobState.Processing"/>, because extending a lot that
    /// is already running is what this message is for; not once the job is ending or over, where
    /// the material would never be reached. Material the job already has is refused rather than
    /// added twice - the same wafer named twice is a mistake somewhere upstream, and nothing is
    /// added at all when one entry is wrong.
    /// </remarks>
    public ProcessJobResult AddMaterial(string processJobId, IReadOnlyList<string> material)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(material);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not (ProcessJobState.Queued
                or ProcessJobState.SettingUp
                or ProcessJobState.WaitingForStart
                or ProcessJobState.Processing))
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; material cannot be added to it any more.");
            }

            List<string> all = [.. job.Material];
            HashSet<string> seen = new(all, StringComparer.Ordinal);

            foreach (var entry in material)
            {
                if (!seen.Add(entry))
                {
                    return ProcessJobResult.Refused(
                        $"process job '{processJobId}' already has material '{entry}'.");
                }

                all.Add(entry);
            }

            job.Material = all;

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Changes what a queued job is for.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="recipeId">The recipe it runs, or <see langword="null"/> to leave it.</param>
    /// <param name="recipeMethod">Whether that recipe may be tuned, or <see langword="null"/> to leave it.</param>
    /// <param name="startsAutomatically">Who starts it, or <see langword="null"/> to leave it.</param>
    /// <param name="materialOrder">In what order its material goes, or <see langword="null"/> to leave it.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only while the job is <see cref="ProcessJobState.Queued"/>: once the machine has taken it up
    /// the recipe is going down and the material is coming in, and changing either underneath that
    /// is how a lot gets run twice. An empty recipe is refused here as it is at create - a job
    /// cannot be emptied of the thing it exists to run.
    /// </remarks>
    public ProcessJobResult Describe(
        string processJobId,
        string? recipeId = null,
        ProcessJobRecipeMethod? recipeMethod = null,
        bool? startsAutomatically = null,
        ProcessJobMaterialOrder? materialOrder = null)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State is not ProcessJobState.Queued)
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; only a {ProcessJobState.Queued} one can be changed.");
            }

            if (recipeId is { Length: 0 })
            {
                return ProcessJobResult.Refused($"process job '{processJobId}' needs a recipe.");
            }

            if (recipeId is not null)
            {
                job.RecipeId = recipeId;
            }

            if (recipeMethod is { } method)
            {
                job.RecipeMethod = method;
            }

            if (startsAutomatically is { } automatically)
            {
                job.StartsAutomatically = automatically;
            }

            if (materialOrder is { } order)
            {
                job.MaterialOrder = order;
            }

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Nominates the collection event that pauses a job - <c>PRPAUSEEVENTID</c>.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="pauseEventId">The event, or <see langword="null"/> for none.</param>
    /// <returns>Whether it was allowed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Any time before the job is over, unlike the rest of its description: a pause event is a
    /// reaction rather than a plan, and a host that notices something going wrong while a job runs
    /// is exactly who wants to set one.
    /// </remarks>
    public ProcessJobResult SetPauseEvent(string processJobId, uint? pauseEventId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.IsFinished)
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; it will not pause again.");
            }

            job.PauseEventId = pauseEventId;

            return ProcessJobResult.Ok;
        }
    }

    /// <summary>Changes one of a job's recipe parameters - S16F17.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="name">Which parameter - <c>CPNAME</c>.</param>
    /// <param name="value">Its new value - <c>CPVAL</c>.</param>
    /// <returns>Whether it was allowed, with the <c>CPACK</c> code that says which kind of no it is.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A parameter the job does not already have is
    /// <see cref="ProcessJobParameterAcknowledgement.NameDoesNotExist"/>: a change cannot invent a
    /// parameter, the same rule the in-memory object provider follows for attributes. The
    /// parameters a job has are the ones it was created with.
    /// </para>
    /// <para>
    /// <strong>And a running job is the interesting case.</strong> A change is allowed while the
    /// job is queued, setting up, waiting or paused; while it is
    /// <see cref="ProcessJobState.Processing"/> it is allowed only when the job's
    /// <see cref="ProcessJob.RecipeMethod"/> is
    /// <see cref="ProcessJobRecipeMethod.WithVariableTuning"/>. That is not this library's idea of
    /// a sensible rule - it is what the two code values of <c>PRRECIPEMETHOD</c> are for, and the
    /// one place in these four standards where a defined code value decides a behaviour.
    /// </para>
    /// </remarks>
    public ProcessJobResult ChangeParameter(string processJobId, string name, string value)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (!job.Parameters.ContainsKey(name))
            {
                return ProcessJobResult.Parameter(
                    $"process job '{processJobId}' has no parameter '{name}'.",
                    ProcessJobParameterAcknowledgement.NameDoesNotExist);
            }

            var changeable = job.State switch
            {
                ProcessJobState.Queued
                    or ProcessJobState.SettingUp
                    or ProcessJobState.WaitingForStart
                    or ProcessJobState.Paused => true,
                ProcessJobState.Processing => job.RecipeMethod is ProcessJobRecipeMethod.WithVariableTuning,
                _ => false,
            };

            if (!changeable)
            {
                return ProcessJobResult.Parameter(
                    $"process job '{processJobId}' is {job.State} with a recipe {job.RecipeMethod}; "
                    + $"'{name}' cannot be changed now.",
                    ProcessJobParameterAcknowledgement.CannotChangeNow);
            }

            Dictionary<string, string> changed = new(StringComparer.Ordinal);

            foreach (var (existing, held) in job.Parameters)
            {
                changed[existing] = held;
            }

            changed[name] = value;
            job.Parameters = changed;

            return ProcessJobResult.Ok;
        }
    }

    // --------------------------------------------------------- outliving the process

    /// <summary>Writes down everything this manager holds.</summary>
    /// <returns>A faithful record, including the states a restart will not claim again.</returns>
    /// <remarks>
    /// Taken under the manager's lock, so the jobs and the queue can never disagree.
    /// <see cref="JobSpace"/> is deliberately not in it: how many jobs a machine can hold is its
    /// own configuration, not something a host set, and a snapshot that carried it could put a
    /// restarted machine's limit back to whatever it was two upgrades ago.
    /// </remarks>
    public ProcessJobManagerSnapshot Capture()
    {
        lock (_gate)
        {
            return new ProcessJobManagerSnapshot
            {
                Jobs = [.. _jobs.Values.Select(job => new ProcessJobSnapshot
                {
                    ProcessJobId = job.ProcessJobId,
                    State = job.State,
                    Completion = job.Completion,
                    RecipeId = job.RecipeId,
                    RecipeMethod = job.RecipeMethod,
                    StartsAutomatically = job.StartsAutomatically,
                    MaterialOrder = job.MaterialOrder,
                    PauseEventId = job.PauseEventId,
                    Material = [.. job.Material],
                    Parameters = new Dictionary<string, string>(job.Parameters, StringComparer.Ordinal),
                })],
                Queue = [.. _queue],
            };
        }
    }

    /// <summary>Reads a snapshot back into a manager that has no jobs yet.</summary>
    /// <param name="snapshot">What <see cref="Capture"/> wrote.</param>
    /// <returns>What came back, and what a restarted machine was not allowed to claim.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The same rule E94's queue follows, because it is the same question: a job's definition is a
    /// host instruction and comes back whole - recipe, method, material, parameters, order, the
    /// event that pauses it - and so does the order of the queue, which E40 lets a host set.
    /// </para>
    /// <para>
    /// <strong>A job in the middle of running does not come back running.</strong>
    /// <see cref="ProcessJobState.Queued"/> is the one state a restart can honestly keep. E40 has
    /// one terminal state, <see cref="ProcessJobState.ProcessComplete"/>, with the reason recorded
    /// beside it, so everything that was in flight ends there:
    /// <see cref="ProcessJobState.SettingUp"/> and
    /// <see cref="ProcessJobState.WaitingForStart"/> end
    /// <see cref="ProcessJobCompletion.Cancelled"/>, because no material went through; the rest -
    /// processing, pausing, paused, stopping, aborting - end
    /// <see cref="ProcessJobCompletion.Aborted"/>, because some of it did.
    /// </para>
    /// <para>
    /// <strong>A snapshot does not raise the job space.</strong> If more jobs were captured than
    /// <see cref="JobSpace"/> now allows - a machine reconfigured downwards between runs - the
    /// ones past the limit are reported and skipped rather than loaded, because the limit is the
    /// machine's statement about itself and a restart is not an argument against it. The queue
    /// order decides which ones those are, so the jobs a host put first are the jobs that survive.
    /// </para>
    /// <para>
    /// Refused outright for a manager that already has jobs. Nothing is raised while restoring.
    /// </para>
    /// </remarks>
    public ProcessJobRestoreReport Restore(ProcessJobManagerSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_jobs.Count > 0)
            {
                return ProcessJobRestoreReport.Denied(
                    $"this manager already has {_jobs.Count} process job(s); a snapshot is read into an empty one.");
            }

            List<string> restored = [];
            Dictionary<string, string> adjusted = new(StringComparer.Ordinal);

            // Queued first and in order, so that a job space smaller than the snapshot keeps the
            // ones a host asked for first rather than whichever the dictionary happened to yield.
            Dictionary<string, int> places = new(StringComparer.Ordinal);

            for (var place = 0; place < snapshot.Queue.Count; place++)
            {
                places.TryAdd(snapshot.Queue[place], place);
            }

            var ordered = snapshot.Jobs
                .OrderBy(job => places.TryGetValue(job.ProcessJobId, out var place) ? place : int.MaxValue)
                .ToArray();

            foreach (var job in ordered)
            {
                if (job.ProcessJobId.Length == 0)
                {
                    continue;
                }

                if (JobSpace is { } limit && _jobs.Count >= limit)
                {
                    adjusted[job.ProcessJobId] =
                        $"the snapshot holds more jobs than this machine's job space of {limit}, so it was "
                        + "skipped: a restart is not an argument against the machine's own limit.";
                    continue;
                }

                var (state, completion) = job.State switch
                {
                    ProcessJobState.Queued => (ProcessJobState.Queued, (ProcessJobCompletion?)null),
                    ProcessJobState.ProcessComplete => (ProcessJobState.ProcessComplete, job.Completion),
                    ProcessJobState.SettingUp or ProcessJobState.WaitingForStart
                        => (ProcessJobState.ProcessComplete, ProcessJobCompletion.Cancelled),
                    _ => (ProcessJobState.ProcessComplete, ProcessJobCompletion.Aborted),
                };

                _jobs[job.ProcessJobId] = new ProcessJob(job.ProcessJobId)
                {
                    State = state,
                    Completion = completion,
                    RecipeId = job.RecipeId,
                    RecipeMethod = job.RecipeMethod,
                    StartsAutomatically = job.StartsAutomatically,
                    MaterialOrder = job.MaterialOrder,
                    PauseEventId = job.PauseEventId,
                    Material = [.. job.Material],
                    Parameters = new Dictionary<string, string>(job.Parameters, StringComparer.Ordinal),
                };

                if (state != job.State)
                {
                    adjusted[job.ProcessJobId] =
                        $"was {job.State} when the machine stopped, so it comes back {state} ({completion}): "
                        + "a restarted machine is not processing anything, and E40 records how a job ended "
                        + "beside the one ending it has.";
                }

                restored.Add(job.ProcessJobId);
            }

            foreach (var processJobId in snapshot.Queue)
            {
                if (_jobs.TryGetValue(processJobId, out var queued)
                    && queued.State is ProcessJobState.Queued
                    && !_queue.Contains(processJobId))
                {
                    _queue.Add(processJobId);
                }
            }

            return new ProcessJobRestoreReport
            {
                IsAccepted = true,
                Restored = restored,
                Adjusted = adjusted,
            };
        }
    }

    // --------------------------------------------------------------- the pieces

    private ProcessJobResult Transition(
        string processJobId,
        ProcessJobState[] from,
        ProcessJobState to,
        string what)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (Array.IndexOf(from, job.State) < 0)
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; only one that is "
                    + $"{string.Join(" or ", from)} can be {what}.");
            }

            Move(job, to);

            return ProcessJobResult.Ok;
        }
    }

    private ProcessJobResult Ending(
        string processJobId,
        ProcessJobState from,
        ProcessJobCompletion completion,
        string what)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        lock (_gate)
        {
            if (!TryFind(processJobId, out var job, out var refusal))
            {
                return refusal;
            }

            if (job.State != from)
            {
                return ProcessJobResult.Refused(
                    $"process job '{processJobId}' is {job.State}; only one that is {from} can have {what}.");
            }

            Finish(job, completion);

            return ProcessJobResult.Ok;
        }
    }

    private void Finish(ProcessJob job, ProcessJobCompletion completion)
    {
        _queue.Remove(job.ProcessJobId);
        job.Completion = completion;

        Move(job, ProcessJobState.ProcessComplete, completion);
    }

    private bool TryFind(
        string processJobId,
        [NotNullWhen(true)] out ProcessJob? job,
        out ProcessJobResult refusal)
    {
        if (!_jobs.TryGetValue(processJobId, out job))
        {
            refusal = ProcessJobResult.Refused($"there is no process job '{processJobId}'.");

            return false;
        }

        refusal = ProcessJobResult.Ok;

        return true;
    }

    /// <summary>Moves a job and announces it, in that order.</summary>
    /// <remarks>In that order, so a handler reading the job sees the state the event announces.</remarks>
    private void Move(ProcessJob job, ProcessJobState next, ProcessJobCompletion? completion = null)
    {
        if (job.State == next)
        {
            return;
        }

        var previous = job.State;
        job.State = next;

        JobStateChanged?.Invoke(this, new ProcessJobStateChangedEventArgs
        {
            Job = job,
            Previous = previous,
            Current = next,
            Completion = completion,
        });
    }
}
