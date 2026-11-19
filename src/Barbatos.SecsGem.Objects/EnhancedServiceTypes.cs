// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// <c>HCACK</c> - the answer to an enhanced remote command, S2F50.
/// </summary>
/// <remarks>
/// <para>
/// The same seven codes as GEM's own S2F42, spelled the same way on purpose, because a host that
/// moves between S2F41 and S2F49 is reading one item. A second enum rather than a reference to
/// <c>Barbatos.SecsGem.Gem</c>'s: this package does not depend on GEM and must not start, which is
/// the same reason the E40 package declares its own <c>HCACK</c> for S16F5.
/// </para>
/// <para>
/// <strong><see cref="NoSuchObject"/> is the code that only this stream can produce.</strong> It
/// is in the catalogue's <c>HCACK</c> definition, and nothing in GEM can ever return it: S2F41
/// commands the equipment, and there is only one of those. A code for "no such object" is the
/// catalogue saying out loud that a command is expected to be addressed to something.
/// </para>
/// <para>
/// <see cref="CompletionLater"/> is the one worth reading twice, as in GEM: the command was
/// accepted and has <em>not</em> finished, and completion will arrive as a collection event.
/// </para>
/// </remarks>
public enum EnhancedCommandAcknowledgement : byte
{
    /// <summary>Accepted and performed.</summary>
    Accepted = 0,

    /// <summary>There is no such command.</summary>
    InvalidCommand = 1,

    /// <summary>The command exists but cannot be performed now.</summary>
    CannotPerformNow = 2,

    /// <summary>At least one parameter is invalid - see <see cref="EnhancedCommandResult.Parameters"/>.</summary>
    InvalidParameter = 3,

    /// <summary>Accepted; completion will be signalled by a collection event.</summary>
    CompletionLater = 4,

    /// <summary>Rejected: the object is already in the condition the command asks for.</summary>
    AlreadyInDesiredCondition = 5,

    /// <summary>There is no such object to command.</summary>
    /// <remarks>The reason this stream exists, in one code. See the remarks on this enum.</remarks>
    NoSuchObject = 6,
}

/// <summary>
/// <c>CPACK</c> - what was wrong with one parameter of an enhanced remote command, S2F50.
/// </summary>
/// <remarks>
/// The catalogue defines 1, 2 and 3 and reserves 4 upwards; it defines no zero, because E5 expects
/// this list to carry only the parameters that failed. Nought and four are this library's, named
/// the way the E40 package named the same item for the same reason - a caller that has a code in
/// hand should not have to know which codes were the standard's.
/// </remarks>
public enum EnhancedParameterAcknowledgement : byte
{
    /// <summary>Accepted. Not a code the catalogue defines; this library's, for a parameter that was fine.</summary>
    Accepted = 0,

    /// <summary>No parameter of that name.</summary>
    NameDoesNotExist = 1,

    /// <summary>That value is not allowed.</summary>
    IllegalValue = 2,

    /// <summary>That value is not in a form this parameter takes.</summary>
    IllegalFormat = 3,

    /// <summary>Equipment specific: something else was wrong with it.</summary>
    Error = 4,
}

/// <summary>
/// <c>RPACK</c> and <c>URACK</c> - the answer to binding a report to an object, S2F52 and S2F54.
/// </summary>
/// <remarks>
/// <para>
/// One enum for two items, and that is the honest arrangement rather than a shortcut.
/// <strong>Neither <c>RPACK</c> nor <c>URACK</c> is in the catalogue's dictionary at all</strong> -
/// no format, no codes - so both are read here as <c>B[1]</c> with nought meaning accepted,
/// inferred from <c>DRACK</c>, <c>LRACK</c> and <c>ERACK</c>, which are the acknowledgements of
/// the three report messages E30 does define and all three of which are shaped exactly that way.
/// Declaring two identical enums to mirror two undefined names would be pretending to knowledge
/// nobody has.
/// </para>
/// <para>
/// A fab whose host sends something else reads <see cref="EnhancedReportBindResult.Code"/>, which
/// carries the byte that arrived whatever this enum makes of it.
/// </para>
/// </remarks>
public enum EnhancedReportAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Denied.</summary>
    Denied = 1,
}

/// <summary>Which object, and which of its variables - S2F55, S2F57 and S2F59.</summary>
/// <remarks>
/// One type for the three requests, because the only difference between them is which kind of
/// identifier the list holds: <c>ECID</c>, <c>SVID</c> or <c>VID</c>, all <c>U4</c>. The method
/// that was called says which, so a provider is not left guessing.
/// </remarks>
public sealed class EnhancedValueRequest
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>Which identifiers. An empty list asks for all of them.</summary>
    /// <remarks>
    /// "All of them" is the rule GEM's own S1F3 and S2F13 follow, and this stream is those
    /// messages with an object in front, so it follows them here too.
    /// </remarks>
    public IReadOnlyList<uint> Ids { get; init; } = [];
}

/// <summary>Which object, and which of its alarms - S2F61.</summary>
public sealed class EnhancedAlarmRequest
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>Which alarms - <c>ALID</c>. An empty list asks for all of them.</summary>
    public IReadOnlyList<uint> AlarmIds { get; init; } = [];
}

/// <summary>One alarm of one object - S2F62.</summary>
/// <remarks>
/// Shaped like GEM's own alarm report, down to <see cref="IsSet"/> reading bit 7 of
/// <see cref="Code"/>, because S2F62's three items are S5F1's three items. <see cref="Id"/> is
/// wider than the catalogue's <c>U4</c> on the way in and exact on the way out - tolerant read,
/// exact write, as everywhere else in this stack.
/// </remarks>
public sealed class EnhancedAlarm
{
    /// <summary>Which alarm - <c>ALID</c>.</summary>
    public required ulong Id { get; init; }

    /// <summary><c>ALCD</c>, whose top bit is the alarm's state and whose low bits are its category.</summary>
    public required byte Code { get; init; }

    /// <summary>What to tell somebody - <c>ALTX</c>.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the alarm is in alarm, rather than cleared.</summary>
    /// <remarks><c>ALCD</c> bit 7. One message carries both the set and the clear, which is why this is a bit and not a function.</remarks>
    public bool IsSet => (Code & 0x80) != 0;
}

/// <summary>Which object, and which diagnostic - S2F63.</summary>
public sealed class EnhancedDiagnosticRequest
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>Which diagnostic - <c>DATAID</c>.</summary>
    public required uint DataId { get; init; }
}

/// <summary>Telling one object to do something - S2F49.</summary>
/// <remarks>
/// GEM's S2F41 with a <c>DATAID</c> and an <c>OBJSPEC</c> in front, which is the whole of the
/// difference and the whole of the point: a cluster tool has four chambers, and S2F41 can only
/// address the machine.
/// </remarks>
public sealed class EnhancedCommandRequest
{
    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }

    /// <summary>What to do - <c>RCMD</c>.</summary>
    public required string Command { get; init; }

    /// <summary>The host's own correlation number - <c>DATAID</c>.</summary>
    /// <remarks>Echoed nowhere: S2F50 does not carry it. It is the host's handle on its own request.</remarks>
    public uint DataId { get; init; }

    /// <summary>What to do it with - <c>CPNAME</c> and <c>CPVAL</c>.</summary>
    /// <remarks>
    /// <see cref="ObjectParameter"/>, the same pair S14F9 carries, rather than a second type for
    /// the same two items. Both are <c>A</c> in the catalogue, so a parameter is a pair of strings
    /// on the way out and any format understood as text on the way in. GEM keeps <c>CPVAL</c> as a
    /// whole item instead; both readings are defensible and this package is consistent with itself.
    /// </remarks>
    public IReadOnlyList<ObjectParameter> Parameters { get; init; } = [];
}

/// <summary>What happened to an enhanced remote command - S2F50.</summary>
public sealed class EnhancedCommandResult
{
    /// <summary>Whether it was done - <c>HCACK</c>.</summary>
    public required EnhancedCommandAcknowledgement Acknowledgement { get; init; }

    /// <summary>What was wrong with which parameter - <c>CPNAME</c> and <c>CPACK</c>.</summary>
    /// <remarks>
    /// Empty for a command that had nothing wrong with it, which is what GEM's own S2F42 always
    /// sends. A list here with <see cref="Acknowledgement"/> anything but
    /// <see cref="EnhancedCommandAcknowledgement.InvalidParameter"/> is not forbidden by E5, and
    /// this library neither adds one nor takes one away.
    /// </remarks>
    public IReadOnlyList<EnhancedParameterResult> Parameters { get; init; } = [];

    /// <summary>Gets a value indicating whether the command was accepted.</summary>
    /// <remarks>
    /// True for <see cref="EnhancedCommandAcknowledgement.Accepted"/> and for
    /// <see cref="EnhancedCommandAcknowledgement.CompletionLater"/>, which is the trap this
    /// property exists to close: four is a success, and a caller testing for nought alone treats
    /// every long-running command as a failure. GEM's host side says the same thing about S2F42.
    /// </remarks>
    public bool IsAccepted => Acknowledgement
        is EnhancedCommandAcknowledgement.Accepted
        or EnhancedCommandAcknowledgement.CompletionLater;

    /// <summary>An accepted answer with nothing to say about parameters.</summary>
    /// <param name="acknowledgement">The code.</param>
    /// <returns>The result.</returns>
    public static EnhancedCommandResult From(EnhancedCommandAcknowledgement acknowledgement)
        => new() { Acknowledgement = acknowledgement };
}

/// <summary>What was wrong with one parameter - <c>CPNAME</c> and <c>CPACK</c>.</summary>
public sealed class EnhancedParameterResult
{
    /// <summary>Which parameter - <c>CPNAME</c>.</summary>
    public required string Name { get; init; }

    /// <summary>What was wrong with it - <c>CPACK</c>.</summary>
    public required EnhancedParameterAcknowledgement Acknowledgement { get; init; }
}

/// <summary>Tying a report to an object, or untying it - S2F51 and S2F53.</summary>
/// <remarks>
/// The reports themselves are GEM's: a host defines one with S2F33 and links it to an event with
/// S2F35. This pair says <em>whose</em> - which object's copy of those variables the report should
/// carry - which is the same question the rest of this stream asks.
/// </remarks>
public sealed class EnhancedReportBinding
{
    /// <summary>Which report - <c>RPTID</c>.</summary>
    public required uint ReportId { get; init; }

    /// <summary>Which object - <c>OBJSPEC</c>.</summary>
    public required string ObjectSpecification { get; init; }
}

/// <summary>The answer to a bind or an unbind - S2F52 and S2F54.</summary>
/// <remarks>
/// Carries the raw byte as well as the enum, because the two items it answers for are undefined in
/// the catalogue: a fab whose host uses a code this library does not name still has it.
/// </remarks>
public sealed class EnhancedReportBindResult
{
    /// <summary>What arrived, as a byte.</summary>
    public required byte Code { get; init; }

    /// <summary>What this library makes of it.</summary>
    public EnhancedReportAcknowledgement Acknowledgement => (EnhancedReportAcknowledgement)Code;

    /// <summary>Gets a value indicating whether it was accepted.</summary>
    /// <remarks>
    /// Only nought. An undefined acknowledgement code never reads as success anywhere in this
    /// stack, and these two codes are more undefined than most.
    /// </remarks>
    public bool IsAccepted => Code == 0;
}

/// <summary>
/// The variables, constants, alarms and diagnostics this end has, per object.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="IObjectProvider"/> for E39's other stream, and it exists for the
/// same reason: the protocol is the library's and the data is the application's. What is different
/// is <em>why</em> the application has to supply it. Stream 14's objects belong to other standards;
/// these variables belong to GEM - but GEM's own stores are not addressed by object, because
/// S1F3 and S2F13 have nowhere to put one. A machine with four chambers has four answers to
/// <c>SVID</c> 1234 and no standard way to keep them, so nothing but the application can.
/// </para>
/// <para>
/// <strong>Positional replies.</strong> S2F56, S2F58 and S2F60 carry values and no identifiers, so
/// the n-th value answers the n-th identifier asked for and a variable this end does not have is a
/// zero-length item in its place - the rule GEM follows for S1F4 and S2F14, where dropping it
/// instead would silently shift every later value by one. Return one item per requested
/// identifier; this library pads a short answer and trims a long one so a malformed provider
/// cannot put a host's readings out of step, and a test asserts that it does.
/// </para>
/// <para>
/// An empty request list asks for everything, and then there is nothing to align to: return what
/// you have, in whatever order you keep it.
/// </para>
/// <para>
/// Every method is asked inside a transaction the other end is waiting on, so none should take
/// long. A value returned from here becomes the reply's and is disposed with it, so build a fresh
/// item for each - the first of the two ownership rules on <see cref="ObjectAttribute"/>, and it
/// applies here unchanged.
/// </para>
/// <para>
/// Every method has a default that answers "nothing of mine", so a provider implements the
/// requests its machine actually serves. An equipment with no diagnostics does not write a method
/// saying so.
/// </para>
/// </remarks>
public interface IEnhancedServiceProvider
{
    /// <summary>Tells one object to do something - S2F49.</summary>
    /// <param name="request">Which object, what to do, and what to do it with.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The code to answer with, and anything wrong with a parameter.</returns>
    /// <remarks>
    /// <para>
    /// The default refuses with <see cref="EnhancedCommandAcknowledgement.InvalidCommand"/>, which
    /// is the honest answer from an end that serves no commands at all.
    /// </para>
    /// <para>
    /// <strong>The control state is yours to check.</strong> This package does not depend on GEM
    /// and so cannot see whether the equipment is on-line remote; an implementation that performs
    /// work must gate it the way <c>GemRemoteCommand.RequiresRemoteControl</c> does, or a host can
    /// drive a machine an operator has taken offline. The E87 package says the same thing about
    /// its carrier actions, and for the same reason.
    /// </para>
    /// </remarks>
    ValueTask<EnhancedCommandResult> CommandAsync(
        EnhancedCommandRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(EnhancedCommandResult.From(EnhancedCommandAcknowledgement.InvalidCommand));

    /// <summary>Reads an object's equipment constants - S2F55.</summary>
    /// <param name="request">Which object, and which <c>ECID</c>s.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One value per identifier asked for; a zero-length item for one this end does not have.</returns>
    ValueTask<IReadOnlyList<SecsItem>> GetEquipmentConstantsAsync(
        EnhancedValueRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<SecsItem>>([]);

    /// <summary>Reads an object's status variables - S2F57.</summary>
    /// <param name="request">Which object, and which <c>SVID</c>s.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One value per identifier asked for; a zero-length item for one this end does not have.</returns>
    ValueTask<IReadOnlyList<SecsItem>> GetStatusVariablesAsync(
        EnhancedValueRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<SecsItem>>([]);

    /// <summary>Reads an object's data variables - S2F59.</summary>
    /// <param name="request">Which object, and which <c>VID</c>s.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One value per identifier asked for; a zero-length item for one this end does not have.</returns>
    /// <remarks>
    /// Separate from <see cref="GetStatusVariablesAsync"/> because E30 keeps status variables and
    /// data variables apart and the difference is observable: S1F3 answers status variables and
    /// refuses data variables. A provider that treats the two alike is making a choice, and should
    /// make it knowingly.
    /// </remarks>
    ValueTask<IReadOnlyList<SecsItem>> GetDataVariablesAsync(
        EnhancedValueRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<SecsItem>>([]);

    /// <summary>Lists an object's alarms - S2F61.</summary>
    /// <param name="request">Which object, and which <c>ALID</c>s.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The alarms. Not positional: each entry names its own <c>ALID</c>, so one this end does not have is simply absent.</returns>
    ValueTask<IReadOnlyList<EnhancedAlarm>> GetAlarmsAsync(
        EnhancedAlarmRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<IReadOnlyList<EnhancedAlarm>>([]);

    /// <summary>Produces an object's diagnostic data - S2F63.</summary>
    /// <param name="request">Which object, and which <c>DATAID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The data, or <see langword="null"/> for a diagnostic this end does not have.</returns>
    /// <remarks>
    /// <c>DGDATA</c> is not in the catalogue's dictionary, so it is a whole item here for the same
    /// reason <c>ATTRDATA</c> is: an undefined item has no format of its own, and diagnostic data
    /// is whatever the machine produced.
    /// </remarks>
    ValueTask<SecsItem?> GetDiagnosticsAsync(
        EnhancedDiagnosticRequest request,
        CancellationToken cancellationToken)
        => ValueTask.FromResult<SecsItem?>(null);

    /// <summary>Ties a report to an object - S2F51.</summary>
    /// <param name="binding">Which report, which object.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><c>RPACK</c>.</returns>
    /// <remarks>The default denies, which is the honest answer from an end that does not bind reports.</remarks>
    ValueTask<EnhancedReportAcknowledgement> BindReportAsync(
        EnhancedReportBinding binding,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(EnhancedReportAcknowledgement.Denied);

    /// <summary>Unties a report from an object - S2F53.</summary>
    /// <param name="binding">Which report, which object.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><c>URACK</c>.</returns>
    ValueTask<EnhancedReportAcknowledgement> UnbindReportAsync(
        EnhancedReportBinding binding,
        CancellationToken cancellationToken)
        => ValueTask.FromResult(EnhancedReportAcknowledgement.Denied);
}
