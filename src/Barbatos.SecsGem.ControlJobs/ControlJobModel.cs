// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlJobs;

/// <summary>
/// Where a control job is in its life - E94.
/// </summary>
/// <remarks>
/// <para>
/// A control job is a host's instruction to run some material: these carriers, in this order,
/// with that recipe. The state machine is how a host and a machine agree on whether it has started,
/// whether it can be paused, and whether it is over.
/// </para>
/// <code>
/// Queued ---------- Select ------------------------------&gt; Selected
/// Selected -------- Deselect ----------------------------&gt; Queued
///     |     ------- its material is ready, host-initiated &gt; WaitingForStart
///     |     ------- its material is ready, automatic -----&gt; Executing
///
/// WaitingForStart - Start -------------------------------&gt; Executing
/// Executing &lt;----- Pause / Resume ----------------------&gt; Paused
///
/// Executing ------- all its material is done ------------&gt; Completed
/// Executing, Paused  Abort / Stop ----------------------&gt; Completed
/// Queued, Selected, WaitingForStart   Cancel -----------&gt; Completed
/// </code>
/// <para>
/// <see cref="Completed"/> is the only ending, and how it ended is
/// <see cref="ControlJob.Completion"/> rather than another state - which is this library reading
/// E94 as it does: the standard has one terminal state and records the reason beside it. Nothing
/// is reachable from it.
/// </para>
/// <para>
/// <strong>On provenance.</strong> As with E87 and E90, the catalogue that checks the message set
/// in this stack says nothing about this: E94 defines behaviour, and its messages are stream 14's.
/// These transitions are E94 as this library understands it, checked by tests rather than against
/// reference data, and <see cref="ControlJobManager"/> is the one file to change when a fab
/// disagrees. What <em>is</em> checked is the wire: every item S14F5, S14F9 and S14F1 carry is
/// defined in the catalogue, which is better than either of the two chapters before this one.
/// </para>
/// </remarks>
public enum ControlJobState
{
    /// <summary>Created, and waiting its turn.</summary>
    Queued = 0,

    /// <summary>Chosen to run next. Its material is being brought in.</summary>
    Selected = 1,

    /// <summary>Ready, and waiting for a host to say go.</summary>
    /// <remarks>Only reached by a job whose <see cref="ControlJobStartMethod"/> is <see cref="ControlJobStartMethod.HostInitiated"/>.</remarks>
    WaitingForStart = 2,

    /// <summary>Running.</summary>
    Executing = 3,

    /// <summary>Running, but held.</summary>
    Paused = 4,

    /// <summary>Over, however it ended. See <see cref="ControlJob.Completion"/>.</summary>
    Completed = 5,
}

/// <summary>How a control job ended - E94.</summary>
/// <remarks>
/// Four ways, and the differences are what a host acts on: a job that finished its material is not
/// a job somebody aborted, and neither is a job that was cancelled before it ever ran.
/// </remarks>
public enum ControlJobCompletion
{
    /// <summary>Every piece of its material was dealt with.</summary>
    Completed = 0,

    /// <summary>Stopped at once, part way through, without finishing what was in progress.</summary>
    Aborted = 1,

    /// <summary>Stopped tidily, part way through: what was in progress was finished first.</summary>
    Stopped = 2,

    /// <summary>Called off before it ever started running.</summary>
    Cancelled = 3,
}

/// <summary>Who starts a control job once its material is ready - E94.</summary>
public enum ControlJobStartMethod
{
    /// <summary>The machine starts it itself.</summary>
    Automatic = 0,

    /// <summary>It waits in <see cref="ControlJobState.WaitingForStart"/> until a host sends Start.</summary>
    HostInitiated = 1,
}

/// <summary>
/// A control job, as E94 models one.
/// </summary>
/// <remarks>
/// Public getters and internal setters, the rule E87's carrier and E90's substrate both follow:
/// the only thing that can move a control job is <see cref="ControlJobManager"/>, where the
/// transitions live.
/// </remarks>
public sealed class ControlJob
{
    internal ControlJob(string controlJobId) => ControlJobId = controlJobId;

    /// <summary>What the job is called - its <c>OBJID</c>.</summary>
    public string ControlJobId { get; }

    /// <summary>Where it is in its life.</summary>
    public ControlJobState State { get; internal set; }

    /// <summary>How it ended, or <see langword="null"/> while it has not.</summary>
    public ControlJobCompletion? Completion { get; internal set; }

    /// <summary>The material it is for - E94's carrier input specification.</summary>
    /// <remarks>
    /// Carrier identifiers as strings, and deliberately not E87 carrier objects: a machine can run
    /// control jobs over whatever material model it has, and a package that insisted on E87 would
    /// make this one useless to a machine that does not implement it. Correlating the two is the
    /// application's business.
    /// </remarks>
    public IReadOnlyList<string> Carriers { get; internal set; } = [];

    /// <summary>Who starts it once its material is ready.</summary>
    public ControlJobStartMethod StartMethod { get; internal set; }

    /// <summary>How the material is to be worked through, in the fab's own words.</summary>
    /// <remarks>E94's process order management. Free text, because the vocabulary is the fab's.</remarks>
    public string ProcessOrder { get; internal set; } = string.Empty;

    /// <summary>What recipe or process job specification it carries, in the fab's own words.</summary>
    public string ProcessingSpecification { get; internal set; } = string.Empty;

    /// <summary>Gets a value indicating whether the job is over.</summary>
    public bool IsFinished => State is ControlJobState.Completed;

    /// <summary>Gets a value indicating whether the job is running, paused included.</summary>
    /// <remarks>
    /// The condition an application reaches for when deciding whether material may be touched, and
    /// one worth having in one place: a paused job still owns its material.
    /// </remarks>
    public bool IsActive => State is ControlJobState.Executing or ControlJobState.Paused;
}

/// <summary>The answer to anything that would move a control job.</summary>
/// <remarks>
/// E94 has no acknowledgement code of its own. Its commands arrive as object actions on S14F9, so
/// what goes back on the wire is <c>OBJACK</c> - and the mapping from this to that lives in
/// <see cref="ControlJobObjectProvider"/>, which is the only place that knows the request came
/// from a wire at all.
/// </remarks>
public sealed class ControlJobResult
{
    /// <summary>Whether it was allowed.</summary>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not.</summary>
    /// <remarks>Every refusal names the state that refused it. Empty when accepted.</remarks>
    public string Reason { get; init; } = string.Empty;

    /// <summary>An accepted answer.</summary>
    internal static ControlJobResult Ok { get; } = new() { IsAccepted = true };

    /// <summary>A refusal.</summary>
    /// <param name="reason">What was wrong, naming the state.</param>
    /// <returns>The result.</returns>
    internal static ControlJobResult Refused(string reason) => new() { IsAccepted = false, Reason = reason };
}

/// <summary>A control job changed state - E94.</summary>
/// <remarks>
/// E94 defines collection events for these, and this is where they come out. As with E87 and E90,
/// this package does not publish them: a <c>CEID</c> belongs to the equipment that defines it. The
/// events are raised after the state has moved and inside the manager's lock, so a handler reading
/// the job sees the state the event announces and must not call back in.
/// </remarks>
public sealed class ControlJobStateChangedEventArgs : EventArgs
{
    /// <summary>The job.</summary>
    public required ControlJob Job { get; init; }

    /// <summary>What it was.</summary>
    public required ControlJobState Previous { get; init; }

    /// <summary>What it is.</summary>
    public required ControlJobState Current { get; init; }

    /// <summary>How it ended, when this transition is the ending.</summary>
    public ControlJobCompletion? Completion { get; init; }
}

/// <summary>
/// The command names E94 defines, as strings, because <c>RCMD</c> is one.
/// </summary>
/// <remarks>
/// A convenience and not a limit, the same as the carrier actions in the E87 package: a host may
/// send a command this list does not name, and <see cref="ControlJobManager"/> refuses what it
/// does not know rather than making it impossible to ask.
/// </remarks>
public static class ControlJobCommands
{
    /// <summary>Choose this job to run next.</summary>
    public const string Select = "Select";

    /// <summary>Put it back in the queue.</summary>
    public const string Deselect = "Deselect";

    /// <summary>Start it, for a job waiting on a host to say so.</summary>
    public const string Start = "Start";

    /// <summary>Hold it.</summary>
    public const string Pause = "Pause";

    /// <summary>Let it carry on.</summary>
    public const string Resume = "Resume";

    /// <summary>End it now, without finishing what is in progress.</summary>
    public const string Abort = "Abort";

    /// <summary>End it tidily: finish what is in progress, then stop.</summary>
    public const string Stop = "Stop";

    /// <summary>Call it off before it ever runs.</summary>
    public const string Cancel = "Cancel";
}
