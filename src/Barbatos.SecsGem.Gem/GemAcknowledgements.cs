// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// S1F14 COMMACK - whether a communication request was accepted.
/// </summary>
/// <remarks>
/// Zero is "accepted" here, as it is in almost every GEM acknowledgement. The habit is worth
/// noticing because <see cref="GemOnlineAcknowledgement"/> and friends use the same convention,
/// and a handler that tests for truthiness rather than for zero has it exactly backwards.
/// </remarks>
public enum GemCommunicationAcknowledgement : byte
{
    /// <summary>Accepted. Both sides are now communicating.</summary>
    Accepted = 0,

    /// <summary>Denied. The requester should wait out its retry interval and ask again.</summary>
    Denied = 1,
}

/// <summary>S1F16 OFLACK - the answer to a host request to go off-line.</summary>
public enum GemOfflineAcknowledgement : byte
{
    /// <summary>Acknowledged. E30 defines no way to refuse this one.</summary>
    Acknowledged = 0,

    /// <summary>1 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>OFLACK</c> names 1 to 63 as reserved, so a code in that range is something the reference anticipated and did
    /// not define. It reads as this member with the arriving code logged, rather than being cast to
    /// an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 1,
}

/// <summary>S1F18 ONLACK - the answer to a host request to go on-line.</summary>
public enum GemOnlineAcknowledgement : byte
{
    /// <summary>Accepted. The equipment is now on-line.</summary>
    Accepted = 0,

    /// <summary>Refused. The equipment will not go on-line right now.</summary>
    Refused = 1,

    /// <summary>Already on-line, so there was nothing to do.</summary>
    AlreadyOnline = 2,

    /// <summary>3 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>ONLACK</c> names 3 to 63 as reserved, so a code in that range is something the reference anticipated and did
    /// not define. It reads as this member with the arriving code logged, rather than being cast to
    /// an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 3,
}

/// <summary>S2F16 EAC - the answer to new equipment constant values.</summary>
public enum GemEquipmentConstantAcknowledgement : byte
{
    /// <summary>Every constant was accepted and is now in force.</summary>
    Accepted = 0,

    /// <summary>Denied: at least one of the identifiers does not exist.</summary>
    UnknownConstant = 1,

    /// <summary>Denied: busy, try later.</summary>
    Busy = 2,

    /// <summary>Denied: at least one value is out of range.</summary>
    OutOfRange = 3,

    /// <summary>4 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>EAC</c> names 4 to 63 as reserved, so a code in that range is something the reference anticipated and did
    /// not define. It reads as this member with the arriving code logged, rather than being cast to
    /// an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 4,
}

/// <summary>S2F22 CMDA - the answer to the remote command that carries no parameters.</summary>
/// <remarks>
/// <para>
/// <strong>Three codes, not <c>HCACK</c>'s seven.</strong> S2F21 is the simple remote command and
/// S2F41 is the one with parameters; they carry different acknowledgement items, and this is the
/// smaller of the two. A handler answers in <see cref="GemHostCommandAcknowledgement"/> because
/// that is what E30's command model uses, so answering S2F21 means narrowing seven codes onto
/// three - see <c>GemEquipment</c> for the mapping, which is stated there rather than left to be
/// inferred.
/// </para>
/// <para>
/// <c>CMDA</c> is also <c>Barbatos.SecsGem.ProcessJobs</c>' acknowledgement for S16F4 and S16F24,
/// and one item cannot mean two things in one repository: these four members are that package's
/// four, name for name, and a test in each asserts both against the reference's table.
/// </para>
/// </remarks>
public enum GemRemoteCommandAcknowledgement : byte
{
    /// <summary>The command was performed.</summary>
    Performed = 0,

    /// <summary>There is no such command.</summary>
    DoesNotExist = 1,

    /// <summary>The command exists and cannot be performed now.</summary>
    /// <remarks>
    /// Where the narrowing from <c>HCACK</c> lands: everything that is neither acceptance nor "no
    /// such command" is refused as this, because S2F22 has no word for the rest.
    /// </remarks>
    CannotPerformNow = 2,

    /// <summary>A code from 3 up, which E5 reserves and does not name.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="CannotPerformNow"/> because the two are different findings, and
    /// this is the member the first version of this enum did not have. Folding a reserved code
    /// onto 2 would put words in the machine's mouth: it would report a refusal the machine never
    /// made, and an equipment-specific code is as likely to qualify an acceptance as to deny one.
    /// What this says instead is the only thing that is known - the machine answered, and in a
    /// vocabulary this library cannot read.
    /// </para>
    /// <para>
    /// It is still not success, which is the rule the whole stack keeps: a code E5 does not define
    /// never reads as "done". <c>Barbatos.SecsGem.ProcessJobs</c> gained the same member in the
    /// same chapter, for the same item, and each package's test drives a machine that answers a
    /// reserved code rather than only comparing the enum with the table.
    /// </para>
    /// </remarks>
    Other = 3,
}

/// <summary>S2F32 TIACK - the answer to a date and time set.</summary>
public enum GemTimeAcknowledgement : byte
{
    /// <summary>The clock was set.</summary>
    Accepted = 0,

    /// <summary>Error: the clock was not set.</summary>
    Error = 1,

    /// <summary>2 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>TIACK</c> names 2 to 63 as reserved, so a code in that range is something the reference
    /// anticipated and did not define. It reads as this member with the arriving code logged,
    /// rather than being cast to an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 2,
}

/// <summary>S2F34 DRACK - the answer to a report definition.</summary>
public enum GemDefineReportAcknowledgement : byte
{
    /// <summary>Every report was defined, or deleted.</summary>
    Accepted = 0,

    /// <summary>Denied: not enough space for the definitions.</summary>
    InsufficientSpace = 1,

    /// <summary>Denied: the message body is not valid.</summary>
    InvalidFormat = 2,

    /// <summary>Denied: at least one report identifier is already defined.</summary>
    ReportAlreadyDefined = 3,

    /// <summary>Denied: at least one variable identifier does not exist.</summary>
    UnknownVariable = 4,

    /// <summary>5 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>DRACK</c> names 5 to 63 as reserved, so a code in that range is something the reference anticipated and did
    /// not define. It reads as this member with the arriving code logged, rather than being cast to
    /// an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 5,
}

/// <summary>S2F36 LRACK - the answer to linking reports to an event.</summary>
public enum GemLinkReportAcknowledgement : byte
{
    /// <summary>Every link was made, or removed.</summary>
    Accepted = 0,

    /// <summary>Denied: not enough space for the links.</summary>
    InsufficientSpace = 1,

    /// <summary>Denied: the message body is not valid.</summary>
    InvalidFormat = 2,

    /// <summary>Denied: at least one event already has links defined.</summary>
    LinkAlreadyDefined = 3,

    /// <summary>Denied: at least one event identifier does not exist.</summary>
    UnknownEvent = 4,

    /// <summary>Denied: at least one report identifier does not exist.</summary>
    UnknownReport = 5,

    /// <summary>6 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>LRACK</c> names 6 to 63 as reserved, so a code in that range is something the reference anticipated and did
    /// not define. It reads as this member with the arriving code logged, rather than being cast to
    /// an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 6,
}

/// <summary>S2F38 ERACK - the answer to enabling or disabling events.</summary>
public enum GemEnableEventAcknowledgement : byte
{
    /// <summary>Done.</summary>
    Accepted = 0,

    /// <summary>Denied: at least one event identifier does not exist.</summary>
    UnknownEvent = 1,

    /// <summary>2 - anything in the range E5 reserves for the equipment and does not name.</summary>
    /// <remarks>
    /// <c>ERACK</c> names 2 to 63 as reserved, so a code in that range is something the reference anticipated and did
    /// not define. It reads as this member with the arriving code logged, rather than being cast to
    /// an enum value with no name - which is what this used to do.
    /// </remarks>
    Other = 2,
}

/// <summary>
/// S2F42 HCACK - the answer to a host command.
/// </summary>
/// <remarks>
/// <see cref="CompletionLater"/> is the one worth reading twice. It means the command was accepted
/// and has <em>not</em> finished, and that the equipment will report completion through a
/// collection event. A handler that returns <see cref="Accepted"/> for a long-running command is
/// telling the host the work is done when it has not started.
/// </remarks>
public enum GemHostCommandAcknowledgement : byte
{
    /// <summary>Accepted and performed.</summary>
    Accepted = 0,

    /// <summary>There is no such command.</summary>
    InvalidCommand = 1,

    /// <summary>The command exists but cannot be performed now.</summary>
    CannotPerformNow = 2,

    /// <summary>At least one parameter is invalid.</summary>
    InvalidParameter = 3,

    /// <summary>Accepted; completion will be signalled by a collection event.</summary>
    CompletionLater = 4,

    /// <summary>Rejected: the equipment is already in the condition the command asks for.</summary>
    AlreadyInDesiredCondition = 5,

    /// <summary>There is no such object to command.</summary>
    NoSuchObject = 6,

    /// <summary>Some other answer: E5 reserves 7 to 63 for the equipment to define.</summary>
    /// <remarks>
    /// <para>
    /// Every code from 7 upwards reads as this one, and the arriving code is logged. Added after
    /// the fact: this enum stopped at 6 while the table reserves 7 to 63, so an equipment-specific
    /// acknowledgement used to arrive as a cast with no name on it - printing as a number, missed
    /// by any <c>switch</c> without a default, and promised as an enum by the signature.
    /// </para>
    /// <para>
    /// It was never the dangerous direction, because an unnamed code is not
    /// <see cref="Accepted"/> and not <see cref="CompletionLater"/>, so a host asking "did it
    /// work" always got no. What it was is a hole, and it had to be closed in the chapter that
    /// served <c>HCACK</c> a second time: <c>Barbatos.SecsGem.Subsystems</c> quotes the same
    /// dictionary entry for S18F2, and one item cannot be read two ways in one repository.
    /// </para>
    /// </remarks>
    Other = 7,
}

/// <summary>ACKC5 - the answer to an alarm message, in stream 5.</summary>
public enum GemAlarmAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Error: not accepted.</summary>
    Error = 1,
}

/// <summary>ACKC6 - the answer to a report message, in stream 6.</summary>
public enum GemReportAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Error: not accepted.</summary>
    Error = 1,
}

/// <summary>S6F6 GRANT6 - whether the host will take a multi-block send.</summary>
/// <remarks>
/// <para>
/// The inquire-and-grant handshake E5 puts in front of a send too large for one block: the
/// equipment says how much is coming with S6F5 and waits to be told to go ahead. Three named
/// codes and a reserved range from 3.
/// </para>
/// <para>
/// <strong>The same item <c>Barbatos.SecsGem.DataSets</c> reads for S13F2.</strong> Its
/// <c>DataSetGrant</c> has these four members under these four names, and a test in each package
/// asserts both against the reference's own table rather than against the other enum - which is
/// what makes the agreement hold when a third package serves <c>GRANT6</c> one day. The three
/// rows that name it are S6F6, S13F2 and S13F6, and the two implemented here are the two the
/// reference documents rather than illustrates.
/// </para>
/// <para>
/// <strong>And <c>GRANT6</c> is not <c>GRANT</c>.</strong> The elder item names four codes and
/// reserves from 4 where this one names three and reserves from 3, so an enum copied from the
/// wrong one puts its catch-all a code too high and reads a machine's reserved 3 as a refusal it
/// did not make. Three packages here serve <c>GRANT</c>, and the two names are one digit apart.
/// </para>
/// </remarks>
public enum GemMultiBlockGrant : byte
{
    /// <summary>Permission granted; go ahead.</summary>
    Ok = 0,

    /// <summary>Busy; ask again later.</summary>
    Busy = 1,

    /// <summary>Not interested, or no space for it.</summary>
    /// <remarks>
    /// The table's one final refusal, and it carries both readings - a host that does not want the
    /// data and a host that cannot hold it answer the same code. <c>GRANT6</c> has no third
    /// refusal, which is the same shortage <c>Barbatos.SecsGem.DataSets</c> documents for S13F2.
    /// </remarks>
    NoSpace = 2,

    /// <summary>A code from 3 up, which E5 reserves and does not name.</summary>
    /// <remarks>Not a grant, so the send does not go ahead - a handshake nobody can read is not permission.</remarks>
    Other = 3,
}

/// <summary>Whether a variable is a status value or an event-associated data value.</summary>
/// <remarks>
/// E30 keeps SVIDs and DVVALs apart, and the difference is observable: S1F3 answers status
/// variables and refuses data variables, while a collection event report may name either. One type
/// with this discriminator rather than two near-identical types, because every other member is the
/// same.
/// </remarks>
public enum GemVariableKind
{
    /// <summary>A status variable. Readable at any time with S1F3.</summary>
    Status,

    /// <summary>A data variable. Meaningful only in the report of the event it belongs to.</summary>
    Data,
}

/// <summary><c>PRACK</c> - the answer to a request to start processing, S2F28.</summary>
/// <remarks>
/// <para>
/// <strong><c>PRACK</c> is not in the reference's dictionary</strong>, so this is an inference, and
/// the siblings it is read off are the two commands this package already serves: <c>CMDA</c> at
/// S2F22 and <c>HCACK</c> at S2F42, both <c>B[1]</c> whose zero means the command was taken. The
/// same route <c>ACKC2</c> takes and the same two-member result, because what the siblings agree
/// on is the zero and nothing else - <c>CMDA</c> names three codes and <c>HCACK</c> names seven.
/// </para>
/// <para>
/// <strong>The gap is in the reply, not in the row.</strong> S2F27 carries a <c>PPID</c>, which is
/// defined, capped at 120 characters and served at twenty-eight rows of this reference - so what
/// the message asks is never in doubt. An own gap is carried untouched unless it is the field the
/// message turns on, and an acknowledgement is not that field.
/// </para>
/// </remarks>
public enum GemInitiateProcessingAcknowledgement : byte
{
    /// <summary>The machine will start processing.</summary>
    Accepted = 0,

    /// <summary>It will not, with no reason the reference can carry.</summary>
    Error = 1,
}
