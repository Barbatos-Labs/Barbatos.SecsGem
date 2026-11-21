// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>
/// Where a process job is in its life - E40, and <c>PRSTATE</c>.
/// </summary>
/// <remarks>
/// <para>
/// A process job is one piece of work: this material, through this recipe, once. A control job
/// (E94) is a host asking for a lot to run; a process job is the machine actually running part of
/// it, and the two are separate standards because a lot can be several jobs and a job can outlive
/// the reason it was asked for.
/// </para>
/// <para>
/// <strong>These names and numbers are the standard's own.</strong> E87, E90 and E94 left this
/// library inferring a state model from the shape of the problem; E40 does not. The catalogue
/// defines <c>PRSTATE</c> as <c>U1</c> with ten code values, and the nine below are nine of them -
/// spelled as it spells them, numbered as it numbers them. Code 5 is reserved there, so it is
/// missing here: a gap in an enum is worth more than a renumbering that would put this library's
/// <c>Pausing</c> on the wire as somebody else's state.
/// </para>
/// <code>
/// Queued ---------- the machine takes it up ------------&gt; SettingUp
/// SettingUp ------- set up, host-initiated -------------&gt; WaitingForStart
///     |      ------ set up, starts by itself -----------&gt; Processing
///
/// WaitingForStart - START ------------------------------&gt; Processing
/// Processing ------ PAUSE -------&gt; Pausing -------------&gt; Paused
/// Paused ---------- RESUME -----------------------------&gt; Processing
///
/// Processing ------ its material is all processed ------&gt; ProcessComplete
/// Processing, Pausing, Paused   STOP ----&gt; Stopping ---&gt; ProcessComplete
/// Processing, Pausing, Paused, Stopping   ABORT ----&gt; Aborting ---&gt; ProcessComplete
/// Queued, SettingUp, WaitingForStart   CANCEL ---------&gt; ProcessComplete
/// </code>
/// <para>
/// What the transient states are for. <see cref="Pausing"/>, <see cref="Stopping"/> and
/// <see cref="Aborting"/> are states, not moments: a machine asked to pause has to reach a point
/// where pausing is safe, and the interval between the command and that point is the one a host
/// most wants to see. They are also where this library's reading of E40 does most of its work, so
/// a transient state only ever leaves by the machine saying it is done -
/// <see cref="ProcessJobManager.PauseComplete"/> and its two neighbours - or by being overtaken:
/// a stop may overtake a pause, and an abort may overtake either, because a graceful ending that
/// hangs is exactly when somebody reaches for the other kind.
/// </para>
/// <para>
/// <see cref="ProcessComplete"/> is the only ending, with how it ended in
/// <see cref="ProcessJob.Completion"/> - the same arrangement E94's package uses, and for the same
/// reason: a host acts on the difference between a job that finished its material and one that
/// somebody aborted. Nothing is reachable from it, and the job leaves the machine when a host
/// deselects it.
/// </para>
/// <para>
/// <strong>On provenance.</strong> The states, their numbers and most of a job's attributes are in
/// the catalogue. The <em>order</em> of the transitions is not, nor is the shape of any stream 16
/// body: not one of that stream's twenty-eight rows is a definition rather than part of a
/// representative subset. So what is checked here is the vocabulary, and what is checked by tests
/// alone is the sequence - <see cref="ProcessJobManager"/> is the one file to change when a fab
/// disagrees about that.
/// </para>
/// </remarks>
public enum ProcessJobState : byte
{
    /// <summary>Created and pooled, awaiting selection.</summary>
    Queued = 0,

    /// <summary>The machine has taken it up: recipe going down, material coming in.</summary>
    SettingUp = 1,

    /// <summary>Set up, and waiting for a host to say go.</summary>
    /// <remarks>Only reached by a job whose <see cref="ProcessJob.StartsAutomatically"/> is <see langword="false"/>.</remarks>
    WaitingForStart = 2,

    /// <summary>Running.</summary>
    Processing = 3,

    /// <summary>Over, however it ended. See <see cref="ProcessJob.Completion"/>.</summary>
    ProcessComplete = 4,

    // 5 is "Reserved" in PRSTATE, and so is left out rather than filled in.

    /// <summary>Asked to pause, and getting to a point where it can.</summary>
    Pausing = 6,

    /// <summary>Running, but held.</summary>
    Paused = 7,

    /// <summary>Asked to stop tidily, and finishing what is in progress.</summary>
    Stopping = 8,

    /// <summary>Asked to stop now, and stopping.</summary>
    Aborting = 9,
}

/// <summary>How a process job ended - this library's own, not E40's.</summary>
/// <remarks>
/// E40 has one terminal state and no item that says why it was reached, so unlike
/// <see cref="ProcessJobState"/> these four names are a reading rather than a quotation. They are
/// the four E40's commands can produce, and a host acts on the difference: a job that worked
/// through its material is not one somebody aborted, and neither is one cancelled before it ran.
/// </remarks>
public enum ProcessJobCompletion
{
    /// <summary>Every piece of its material went through the recipe.</summary>
    Completed = 0,

    /// <summary>Stopped tidily, part way through: what was in progress was finished first.</summary>
    Stopped = 1,

    /// <summary>Stopped at once, part way through.</summary>
    Aborted = 2,

    /// <summary>Called off before it ever started processing.</summary>
    Cancelled = 3,
}

/// <summary>Whether a recipe's variables may be tuned - <c>PRRECIPEMETHOD</c>.</summary>
/// <remarks>
/// The catalogue defines this as <c>U1</c> with two code values and no zero, so this enum starts at
/// one. It is not a label: <see cref="WithVariableTuning"/> is what decides whether a host may
/// change a parameter on a job that is already processing.
/// </remarks>
public enum ProcessJobRecipeMethod : byte
{
    /// <summary>The recipe runs as it is. Its parameters cannot be changed once processing starts.</summary>
    WithoutVariableTuning = 1,

    /// <summary>The recipe's variables may be tuned, including while the job processes.</summary>
    WithVariableTuning = 2,
}

/// <summary>In what order a job's material is to be worked through - <c>PRMTRLORDER</c>.</summary>
/// <remarks>The three code values the catalogue defines, and nothing else: this package carries the choice rather than acting on it, because which wafer goes next is the machine's business.</remarks>
public enum ProcessJobMaterialOrder : byte
{
    /// <summary>In the order the material arrives.</summary>
    ArrivalSequence = 1,

    /// <summary>In whatever order gets the most through.</summary>
    MaximizeThroughput = 2,

    /// <summary>In the order the job lists it.</summary>
    ListedSequence = 3,
}

/// <summary>The answer to a process job request - <c>PJACK</c>.</summary>
/// <remarks>
/// <c>PJACK</c> has no dictionary entry, so these two values are taken from <c>PREACK</c>, which
/// does and which plays the same part: zero accepted, one rejected. Two values is thin for a
/// create that can fail four ways, and this package does not invent codes a host has no way to
/// read - the reason stays on the equipment side in <see cref="ProcessJobResult.Reason"/>, where a
/// log can have it. A <em>command</em> says more, because <c>CMDA</c> is defined: see
/// <see cref="ProcessJobCommandAcknowledgement"/>.
/// </remarks>
public enum ProcessJobAcknowledgement : byte
{
    /// <summary>It worked.</summary>
    Accepted = 0,

    /// <summary>It did not.</summary>
    Rejected = 1,
}

/// <summary>The answer to a process job command - <c>CMDA</c>.</summary>
/// <remarks>
/// <para>
/// Defined, with its three meanings, which makes E40's command path the only one in the last four
/// standards that can tell a host <em>why</em>: a command the machine has never heard of is not a
/// command it cannot do yet. E94's refusals all arrive as one <c>OBJACK</c> code, because stream
/// 14 has nothing better.
/// </para>
/// <para>
/// <strong>Three meanings and a reserved range</strong>, and <see cref="Other"/> is the range. It
/// was added when <c>Barbatos.SecsGem.Gem</c> came to read the same item for S2F22 and the two
/// readings had to be made to agree: this enum had the three named codes and folded everything
/// above them onto <see cref="CannotPerformNow"/>, which is a refusal the machine had not made.
/// The comparison that found it was the item's own table, not the other package's enum.
/// </para>
/// </remarks>
public enum ProcessJobCommandAcknowledgement : byte
{
    /// <summary>Performed, or completed.</summary>
    Performed = 0,

    /// <summary>There is no such command.</summary>
    DoesNotExist = 1,

    /// <summary>A command this machine has, refused from the state the job is in.</summary>
    CannotPerformNow = 2,

    /// <summary>A code from 3 up, which E5 reserves and does not name.</summary>
    /// <remarks>
    /// Still not success - the rule the whole stack keeps is that a code E5 does not define never
    /// reads as "done". What it is not is a refusal either, which is the distinction this member
    /// exists to keep: an equipment-specific code may as easily qualify an acceptance as deny
    /// one, and all that is known is that the machine answered in a vocabulary this library
    /// cannot read.
    /// </remarks>
    Other = 3,
}

/// <summary>The answer to a queue reorder - <c>HCACK</c>.</summary>
/// <remarks>
/// <para>
/// S16F22 answers with <c>HCACK</c>, which is E5's general host-command acknowledgement and is
/// defined with seven meanings. Four of them are reachable from a reorder: it was done, at least
/// one parameter is invalid - a count that does not match, or a job named twice - no such object
/// exists, for a job that is not in the queue, and cannot perform now. The other three are in this
/// enum because the item defines them, not because this package sends them.
/// </para>
/// <para>
/// A queue reorder is the only message in E40 that answers this way, and it is the richest
/// refusal in the whole standard: <c>PJACK</c> can only say no.
/// </para>
/// </remarks>
public enum ProcessJobQueueAcknowledgement : byte
{
    /// <summary>Done.</summary>
    Acknowledged = 0,

    /// <summary>There is no such command.</summary>
    CommandDoesNotExist = 1,

    /// <summary>Not now.</summary>
    CannotPerformNow = 2,

    /// <summary>At least one parameter is invalid.</summary>
    ParameterInvalid = 3,

    /// <summary>Accepted, and it will be done later.</summary>
    WillBePerformed = 4,

    /// <summary>Rejected: it is already so.</summary>
    AlreadyInDesiredCondition = 5,

    /// <summary>No such object exists.</summary>
    NoSuchObject = 6,
}

/// <summary>The answer to one parameter of a change - <c>CPACK</c>.</summary>
/// <remarks>
/// Codes one, two and three are the catalogue's. Zero is not in its list - <c>CPACK</c> names only
/// the ways a parameter can be wrong - but every other acknowledgement in E5 uses zero for success
/// and so does this one. <see cref="CannotChangeNow"/> is four, the first of the range the
/// catalogue leaves to the equipment, and it is this package's: a parameter refused because of the
/// job's state is not a parameter with an illegal value.
/// </remarks>
public enum ProcessJobParameterAcknowledgement : byte
{
    /// <summary>Changed.</summary>
    Accepted = 0,

    /// <summary>No parameter of that name.</summary>
    NameDoesNotExist = 1,

    /// <summary>That value is not allowed.</summary>
    IllegalValue = 2,

    /// <summary>That value is not in a form this parameter takes.</summary>
    IllegalFormat = 3,

    /// <summary>Equipment specific: not from the state the job is in.</summary>
    CannotChangeNow = 4,
}

/// <summary>
/// A process job, as E40 models one.
/// </summary>
/// <remarks>
/// Public getters and internal setters, the rule E87's carrier, E90's substrate and E94's control
/// job all follow: the only thing that can move a process job is
/// <see cref="ProcessJobManager"/>, where the transitions live.
/// </remarks>
public sealed class ProcessJob
{
    internal ProcessJob(string processJobId) => ProcessJobId = processJobId;

    /// <summary>What the job is called - <c>PRJOBID</c>, which the catalogue defines as <c>A</c>.</summary>
    public string ProcessJobId { get; }

    /// <summary>Where it is in its life - <c>PRSTATE</c>.</summary>
    public ProcessJobState State { get; internal set; }

    /// <summary>How it ended, or <see langword="null"/> while it has not.</summary>
    public ProcessJobCompletion? Completion { get; internal set; }

    /// <summary>The recipe it runs - <c>PPID</c>.</summary>
    public string RecipeId { get; internal set; } = string.Empty;

    /// <summary>Whether that recipe's variables may be tuned - <c>PRRECIPEMETHOD</c>.</summary>
    public ProcessJobRecipeMethod RecipeMethod { get; internal set; } = ProcessJobRecipeMethod.WithoutVariableTuning;

    /// <summary>Whether the machine starts it once setup is done - <c>PRPROCESSSTART</c>.</summary>
    /// <remarks>
    /// The catalogue gives this item as <c>BOOLEAN</c> and no code values, so which way round it
    /// reads is this package's choice: true means the machine starts the job itself, false means it
    /// waits in <see cref="ProcessJobState.WaitingForStart"/> for a host's <c>START</c>. It is the
    /// one thing that decides which of the two states setup ends in.
    /// </remarks>
    public bool StartsAutomatically { get; internal set; } = true;

    /// <summary>In what order its material is to be worked through - <c>PRMTRLORDER</c>.</summary>
    public ProcessJobMaterialOrder MaterialOrder { get; internal set; } = ProcessJobMaterialOrder.ArrivalSequence;

    /// <summary>The collection event that pauses this job, or <see langword="null"/> - <c>PRPAUSEEVENTID</c>.</summary>
    /// <remarks>
    /// E40 lets a host nominate an event that pauses a job when it happens, and the catalogue
    /// defines the item as <c>U4</c> - a <c>CEID</c>, in other words. This package carries the
    /// number and does not watch for it: a <c>CEID</c> belongs to the equipment that defines it, so
    /// an application with a GEM layer handles that event and calls
    /// <see cref="ProcessJobManager.Command"/> with <see cref="ProcessJobCommands.Pause"/>.
    /// </remarks>
    public uint? PauseEventId { get; internal set; }

    /// <summary>The material it is for - <c>MTRLID</c>.</summary>
    /// <remarks>
    /// Identifiers as strings, deliberately not E90 substrates or E87 carriers: a machine can run
    /// process jobs over whatever material model it has, and correlating the two is the
    /// application's business.
    /// </remarks>
    public IReadOnlyList<string> Material { get; internal set; } = [];

    /// <summary>Its recipe parameters - <c>CPNAME</c> and <c>CPVAL</c>.</summary>
    /// <remarks>Both are <c>A</c> items in the catalogue, so a parameter is a pair of strings.</remarks>
    public IReadOnlyDictionary<string, string> Parameters { get; internal set; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets a value indicating whether the job is over.</summary>
    public bool IsFinished => State is ProcessJobState.ProcessComplete;

    /// <summary>Gets a value indicating whether the job holds material it has not let go of.</summary>
    /// <remarks>
    /// Everything from the moment the machine takes the job up to the moment it is over. The
    /// condition an application reaches for when deciding whether that material may be touched by
    /// anything else, and one worth having in one place: a paused job and a job being aborted both
    /// still own their wafers.
    /// </remarks>
    public bool IsActive => State is not (ProcessJobState.Queued or ProcessJobState.ProcessComplete);
}

/// <summary>The answer to anything that would move a process job.</summary>
/// <remarks>
/// Carries the acknowledgement code the message in question actually has, which is why there are
/// three of them rather than one: a create answers with <c>PJACK</c>, a command with <c>CMDA</c>,
/// a parameter change with <c>CPACK</c>. The reason is always here, whatever the wire can carry.
/// </remarks>
public sealed class ProcessJobResult
{
    /// <summary>Whether it was allowed.</summary>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not.</summary>
    /// <remarks>Every refusal names the state that refused it. Empty when accepted.</remarks>
    public string Reason { get; init; } = string.Empty;

    /// <summary>What a <c>CMDA</c> should say about it.</summary>
    /// <remarks>
    /// <see cref="ProcessJobCommandAcknowledgement.DoesNotExist"/> only for a command this
    /// equipment has no such thing as; a command refused from a state is
    /// <see cref="ProcessJobCommandAcknowledgement.CannotPerformNow"/>, which is the distinction
    /// E40 can make and E94 cannot.
    /// </remarks>
    public ProcessJobCommandAcknowledgement CommandAcknowledgement { get; init; }

    /// <summary>What a <c>CPACK</c> should say about it.</summary>
    /// <remarks>Only a parameter change reads this; everything else leaves it <see cref="ProcessJobParameterAcknowledgement.Accepted"/>.</remarks>
    public ProcessJobParameterAcknowledgement ParameterAcknowledgement { get; init; }

    /// <summary>What an <c>HCACK</c> should say about it.</summary>
    /// <remarks>
    /// Only a queue reorder reads this, and it is here rather than worked out from
    /// <see cref="Reason"/> by whoever sends the reply: a code picked by looking for words in a
    /// sentence is a code that changes when somebody rewords the sentence.
    /// </remarks>
    public ProcessJobQueueAcknowledgement QueueAcknowledgement { get; init; }

    /// <summary>What a <c>PJACK</c> should say about it.</summary>
    public ProcessJobAcknowledgement Acknowledgement
        => IsAccepted ? ProcessJobAcknowledgement.Accepted : ProcessJobAcknowledgement.Rejected;

    /// <summary>An accepted answer.</summary>
    internal static ProcessJobResult Ok { get; } = new() { IsAccepted = true };

    /// <summary>A refusal from a state.</summary>
    /// <param name="reason">What was wrong, naming the state.</param>
    /// <returns>The result.</returns>
    internal static ProcessJobResult Refused(string reason) => new()
    {
        IsAccepted = false,
        Reason = reason,
        CommandAcknowledgement = ProcessJobCommandAcknowledgement.CannotPerformNow,
        ParameterAcknowledgement = ProcessJobParameterAcknowledgement.CannotChangeNow,
        QueueAcknowledgement = ProcessJobQueueAcknowledgement.CannotPerformNow,
    };

    /// <summary>A refusal about a queue reorder.</summary>
    /// <param name="reason">What was wrong.</param>
    /// <param name="acknowledgement">Which of <c>HCACK</c>'s codes says so.</param>
    /// <returns>The result.</returns>
    internal static ProcessJobResult Queue(
        string reason,
        ProcessJobQueueAcknowledgement acknowledgement) => new()
    {
        IsAccepted = false,
        Reason = reason,
        CommandAcknowledgement = ProcessJobCommandAcknowledgement.CannotPerformNow,
        ParameterAcknowledgement = ProcessJobParameterAcknowledgement.CannotChangeNow,
        QueueAcknowledgement = acknowledgement,
    };

    /// <summary>A refusal because there is no such command.</summary>
    /// <param name="reason">What was wrong.</param>
    /// <returns>The result.</returns>
    internal static ProcessJobResult Unknown(string reason) => new()
    {
        IsAccepted = false,
        Reason = reason,
        CommandAcknowledgement = ProcessJobCommandAcknowledgement.DoesNotExist,
        ParameterAcknowledgement = ProcessJobParameterAcknowledgement.NameDoesNotExist,
    };

    /// <summary>A refusal about one parameter.</summary>
    /// <param name="reason">What was wrong.</param>
    /// <param name="acknowledgement">Which of <c>CPACK</c>'s codes says so.</param>
    /// <returns>The result.</returns>
    internal static ProcessJobResult Parameter(
        string reason,
        ProcessJobParameterAcknowledgement acknowledgement) => new()
    {
        IsAccepted = false,
        Reason = reason,
        CommandAcknowledgement = ProcessJobCommandAcknowledgement.CannotPerformNow,
        ParameterAcknowledgement = acknowledgement,
    };
}

/// <summary>A process job changed state - E40.</summary>
/// <remarks>
/// E40 reports these to a host itself, on S16F11, which is the one thing it has that E90 and E94
/// do not: a state change is a transaction rather than a collection event somebody had to define.
/// The equipment side sends it from this event; the event is raised after the state has moved and
/// inside the manager's lock, so a handler reading the job sees the state the event announces and
/// must not call back in.
/// </remarks>
public sealed class ProcessJobStateChangedEventArgs : EventArgs
{
    /// <summary>The job.</summary>
    public required ProcessJob Job { get; init; }

    /// <summary>What it was.</summary>
    public required ProcessJobState Previous { get; init; }

    /// <summary>What it is.</summary>
    public required ProcessJobState Current { get; init; }

    /// <summary>How it ended, when this transition is the ending.</summary>
    public ProcessJobCompletion? Completion { get; init; }
}

/// <summary>
/// The command names a host sends in <c>RCMD</c>.
/// </summary>
/// <remarks>
/// E40's own command item, <c>PRCMDNAME</c>, is defined as <c>A</c> with no code values, and so is
/// the <c>RCMD</c> that S16F3 carries - so unlike E94's these six spellings are not quoted from
/// the catalogue. They are taken from <c>CTLJOBCMD</c>, the one place in it that spells job
/// commands at all, which at least means a fab doing both standards sends one word per verb rather
/// than two.
/// </remarks>
public static class ProcessJobCommands
{
    /// <summary>Start it, for a job waiting on a host to say so.</summary>
    public const string Start = "START";

    /// <summary>Hold it, once it reaches a point where it can.</summary>
    public const string Pause = "PAUSE";

    /// <summary>Let it carry on.</summary>
    public const string Resume = "RESUME";

    /// <summary>End it tidily: finish what is in progress, then stop.</summary>
    public const string Stop = "STOP";

    /// <summary>End it now.</summary>
    public const string Abort = "ABORT";

    /// <summary>Call it off before it ever processes.</summary>
    public const string Cancel = "CANCEL";
}
