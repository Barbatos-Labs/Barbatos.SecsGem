// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Substrates;

namespace Barbatos.SecsGem.MaterialFlow;

/// <summary>
/// Carries a process job's ending through to the substrates it was running.
/// </summary>
/// <remarks>
/// <para>
/// E40 says what happened to the job; E90 says what happened to each wafer; neither says the other
/// thing. A job that is stopped part way leaves the wafer on the chuck
/// <see cref="SubstrateProcessingState.InProcess"/> and the rest of the lot
/// <see cref="SubstrateProcessingState.NeedsProcessing"/> - for ever, because E90's own rules will
/// not let anything move them later: work ends only from <c>InProcess</c> and skipping is only
/// from <c>NeedsProcessing</c>, and both of those are states nobody is going to come back to on
/// behalf of a job that is already over.
/// </para>
/// <para>
/// So this bridge does one thing: when a job ends, it settles the job's material. Work in progress
/// ends the way the job ended - stopped for stopped, aborted for aborted, the one mapping in this
/// package that needed no choosing - and wafers nobody got to are skipped, which is E90's word for
/// exactly that. Both halves are options.
/// </para>
/// <para>
/// <strong>What it will not do is tidy up.</strong> A job the machine called complete with a wafer
/// still in process, or a job cancelled before it ran with a wafer already processed, is a machine
/// telling two different stories - and those come back in
/// <see cref="ProcessJobSubstrateResult.Unexpected"/> untouched. A bridge that quietly picked one
/// story would destroy the evidence of the other, and the evidence is the only thing anybody can
/// act on.
/// </para>
/// <para>
/// <strong>Locks.</strong> As with the carrier bridge: its own lock covers nothing but its
/// bookkeeping, and attached it runs inside the process job manager's lock and calls into the
/// substrate tracker from there. Jobs first, then substrates, never the other way.
/// </para>
/// </remarks>
public sealed class ProcessJobSubstrateBridge
{
    private readonly ProcessJobManager _jobs;
    private readonly SubstrateTracker _substrates;
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private EventHandler<ProcessJobStateChangedEventArgs>? _handler;

    /// <summary>Joins a process job manager to a substrate tracker.</summary>
    /// <param name="jobs">E40's state machines.</param>
    /// <param name="substrates">E90's state machines.</param>
    /// <param name="options">The convention. <see langword="null"/> takes the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="jobs"/> or <paramref name="substrates"/> is <see langword="null"/>.</exception>
    public ProcessJobSubstrateBridge(
        ProcessJobManager jobs,
        SubstrateTracker substrates,
        ProcessJobSubstrateOptions? options = null)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _substrates = substrates ?? throw new ArgumentNullException(nameof(substrates));
        Options = options ?? new ProcessJobSubstrateOptions();
    }

    /// <summary>Raised after a job's substrates are settled.</summary>
    /// <remarks>Raised on whichever thread did the work, which attached is inside the job manager's lock. Queue the work.</remarks>
    public event EventHandler<ProcessJobSubstrateEventArgs>? Settled;

    /// <summary>Raised when a job being set up names material the tracker does not have.</summary>
    /// <remarks>
    /// Only when there is something to say: a job whose material is all there raises nothing. The
    /// bridge cannot refuse the transition - it hears about it afterwards - so this is a report,
    /// and an application that wants to refuse calls <see cref="Check"/> first.
    /// </remarks>
    public event EventHandler<ProcessJobSubstrateEventArgs>? MaterialMissing;

    /// <summary>Gets the convention this bridge follows.</summary>
    public ProcessJobSubstrateOptions Options { get; }

    /// <summary>Gets a value indicating whether this bridge is listening to the job manager.</summary>
    public bool IsAttached => _handler is not null;

    /// <summary>Listens to the job manager, so an ending settles its substrates by itself.</summary>
    /// <remarks>Attaching twice listens once.</remarks>
    public void Attach()
    {
        lock (_gate)
        {
            if (_handler is not null)
            {
                return;
            }

            _handler = OnJobStateChanged;
            _jobs.JobStateChanged += _handler;
        }
    }

    /// <summary>Stops listening.</summary>
    /// <remarks>What it already settled stays settled: a wafer whose work ended does not un-end.</remarks>
    public void Detach()
    {
        lock (_gate)
        {
            if (_handler is null)
            {
                return;
            }

            _jobs.JobStateChanged -= _handler;
            _handler = null;
        }
    }

    /// <summary>Checks a job's material against the tracker.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>Every piece of material the tracker does not have, in <see cref="ProcessJobSubstrateResult.Unexpected"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// What a host should call before it tells a machine to set a job up. E40 carries material as
    /// identifiers and knows nothing about substrates, so a job can perfectly well name a wafer
    /// nothing is tracking - and the machine would then process material it cannot account for.
    /// </remarks>
    public ProcessJobSubstrateResult Check(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        if (_jobs.Find(processJobId) is not { } job)
        {
            return ProcessJobSubstrateResult.Denied(processJobId, $"there is no process job '{processJobId}'.");
        }

        Dictionary<string, string> missing = new(StringComparer.Ordinal);

        foreach (var material in job.Material)
        {
            if (_substrates.FindSubstrate(material) is null)
            {
                missing[material] = "nothing is tracking this material.";
            }
        }

        return new ProcessJobSubstrateResult
        {
            IsAccepted = true,
            ProcessJobId = processJobId,
            Unexpected = missing,
        };
    }

    /// <summary>Settles a finished job's substrates.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <returns>What was ended, what was skipped, and what did not fit.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Only a job that is <see cref="ProcessJobState.ProcessComplete"/>: a job still running has
    /// nothing to settle, and settling one that is
    /// <see cref="ProcessJobState.Stopping"/> would end work the machine has not finished stopping.
    /// </remarks>
    public ProcessJobSubstrateResult Settle(string processJobId)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        if (_jobs.Find(processJobId) is not { } job)
        {
            return ProcessJobSubstrateResult.Denied(processJobId, $"there is no process job '{processJobId}'.");
        }

        // One check, not two. Having a completion and being ProcessComplete are the same fact in
        // E40's manager - it sets the one in the step that moves the job to the other - and
        // asking both was a second copy of the rule that no test could tell from the first.
        if (job.Completion is not { } completion)
        {
            return ProcessJobSubstrateResult.Denied(
                processJobId,
                $"process job '{processJobId}' is {job.State}; only one that is "
                + $"{ProcessJobState.ProcessComplete} has an ending to carry.");
        }

        lock (_gate)
        {
            List<string> ended = [];
            List<string> skipped = [];
            Dictionary<string, string> unexpected = new(StringComparer.Ordinal);

            foreach (var material in job.Material)
            {
                Settle(material, completion, ended, skipped, unexpected);
            }

            var result = new ProcessJobSubstrateResult
            {
                IsAccepted = true,
                ProcessJobId = processJobId,
                Ended = ended,
                Skipped = skipped,
                Unexpected = unexpected,
            };

            Settled?.Invoke(this, new ProcessJobSubstrateEventArgs { Result = result });

            return result;
        }
    }

    /// <summary>The E90 word for an E40 ending, where there is one.</summary>
    /// <remarks>
    /// Two of the four need no translating, which is the whole reason this method is three lines
    /// and not a table of choices. A job that completed has nothing to end - every wafer of it
    /// should already be processed - and a cancelled job never started any work at all.
    /// </remarks>
    private static SubstrateProcessingState? EndingFor(ProcessJobCompletion completion) => completion switch
    {
        ProcessJobCompletion.Stopped => SubstrateProcessingState.Stopped,
        ProcessJobCompletion.Aborted => SubstrateProcessingState.Aborted,
        _ => null,
    };

    private void Settle(
        string material,
        ProcessJobCompletion completion,
        List<string> ended,
        List<string> skipped,
        Dictionary<string, string> unexpected)
    {
        if (_substrates.FindSubstrate(material) is not { } substrate)
        {
            unexpected[material] = "nothing is tracking this material.";

            return;
        }

        switch (substrate.ProcessingState)
        {
            case SubstrateProcessingState.InProcess when EndingFor(completion) is { } ending:
                if (!Options.EndsWorkInProgress)
                {
                    unexpected[material] = $"still {substrate.ProcessingState}, and this bridge was told not to end it.";

                    return;
                }

                var result = _substrates.EndProcessing(material, ending);

                if (result.IsAccepted)
                {
                    ended.Add(material);
                }
                else
                {
                    unexpected[material] = result.Reason;
                }

                return;

            // A job the machine called complete, with a wafer still on a chuck. Reported rather
            // than ended: there is no honest state to end it in, because "processed" would be a
            // claim about a recipe that nobody made.
            case SubstrateProcessingState.InProcess:
                unexpected[material] = $"still {substrate.ProcessingState} after a {completion} job.";

                return;

            case SubstrateProcessingState.NeedsProcessing when completion is not ProcessJobCompletion.Completed:
                if (!Options.SkipsUntouched)
                {
                    unexpected[material] = $"still {substrate.ProcessingState}, and this bridge was told not to skip it.";

                    return;
                }

                var skip = _substrates.Skip(material);

                if (skip.IsAccepted)
                {
                    skipped.Add(material);
                }
                else
                {
                    unexpected[material] = skip.Reason;
                }

                return;

            // A completed job whose material never got processed, or a cancelled one whose
            // material did. Both are the machine telling two stories, and both are left alone.
            case SubstrateProcessingState.NeedsProcessing:
                unexpected[material] = $"never processed under a {completion} job.";

                return;

            case SubstrateProcessingState.Processed when completion is ProcessJobCompletion.Cancelled:
                unexpected[material] = $"already {substrate.ProcessingState} under a {completion} job.";

                return;

            default:
                // Everything else is a wafer that has already been accounted for: processed under
                // a job that completed, stopped under one that was stopped, lost, rejected.
                return;
        }
    }

    private void OnJobStateChanged(object? sender, ProcessJobStateChangedEventArgs arguments)
    {
        switch (arguments.Current)
        {
            case ProcessJobState.SettingUp when Options.ChecksMaterialWhenSettingUp:
                var checked_ = Check(arguments.Job.ProcessJobId);

                // Only when there is something to say: a job whose material is all there is the
                // normal case, and an event for it would be noise in every lot.
                if (checked_.Unexpected.Count > 0)
                {
                    MaterialMissing?.Invoke(this, new ProcessJobSubstrateEventArgs { Result = checked_ });
                }

                return;

            case ProcessJobState.ProcessComplete when Options.SettlesWhenTheJobEnds:
                Settle(arguments.Job.ProcessJobId);

                return;

            default:
                return;
        }
    }
}
