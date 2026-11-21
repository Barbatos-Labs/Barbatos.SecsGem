// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Subsystems;

/// <summary>
/// Which subsystem - <c>SSID</c>, which the dictionary does not define, so this type carries either
/// reading rather than choosing one.
/// </summary>
/// <remarks>
/// <para>
/// <c>SSID</c> is the spine of stream 18: eleven of its sixteen rows carry one, and it is named by
/// no message outside the stream. It has <strong>no dictionary entry</strong>, and unlike the
/// payload in stream 13 it cannot simply be passed through unexamined - it is a key, and a key this
/// library cannot compare is of no use to anybody.
/// </para>
/// <para>
/// <strong>So the measurement, and why it does not settle it.</strong> Eight items in the
/// catalogue are an identifier beside a name: <c>CEID</c>/<c>CENAME</c>, <c>DSID</c>/<c>DSNAME</c>,
/// <c>ECID</c>/<c>ECNAME</c>, <c>SPID</c>/<c>SPNAME</c> and <c>SVID</c>/<c>SVNAME</c> are all a
/// <c>U4</c> beside an <c>A</c>; <c>EQID</c>/<c>EQNAME</c>, <c>RCPID</c>/<c>RCPNAME</c> and
/// <c>TRJOBID</c>/<c>TRJOBNAME</c> are all an <c>A</c> beside an <c>A</c>. Five against three is
/// not a rule. Stream 18 does not settle it from inside either: <c>TARGETID</c>, the only other
/// identifier in the stream, is an <c>A</c>, and <c>ALID</c>, which is also in it, is a <c>U4</c>.
/// </para>
/// <para>
/// A wrong guess here is total rather than partial - text cannot be read as a number and a number
/// cannot be read as text, so a single choice would make this package useless against half the
/// machines in the world. Hence a type with both readings: the caller says which, this library
/// writes exactly that, and either arrives back. The same answer stream 13 gave to an undefined
/// payload, for the same reason, in the only shape a key can take it.
/// </para>
/// <para>
/// <strong>Equality is strict.</strong> <see cref="Numbered"/> 7 and <see cref="Named"/> "7" are
/// two different subsystems, because nothing in E5 says a machine means them as one and a store
/// that conflated them would be inventing the equivalence. That is the rule
/// <c>Barbatos.SecsGem.Carriers</c> arrived at the hard way when a reader meaning to be generous
/// read <c>"2"</c> as whichever state happened to be 2.
/// </para>
/// </remarks>
public readonly struct SubsystemId : IEquatable<SubsystemId>
{
    private readonly string? _name;
    private readonly uint _number;
    private readonly bool _isNumber;

    private SubsystemId(string? name, uint number, bool isNumber)
    {
        _name = name;
        _number = number;
        _isNumber = isNumber;
    }

    /// <summary>Gets whether this identifier travels as a number.</summary>
    /// <remarks>
    /// <para>
    /// <see langword="false"/> for a name, and for the default value.
    /// </para>
    /// <para>
    /// <strong>Which way round that is matters, and the first version of this type had it
    /// backwards.</strong> The discriminator is a field of its own rather than "is the name null",
    /// because a struct arrives default from anywhere - an unassigned field, an array, a
    /// <c>required</c> property somebody forgot - and with the null check the default was the
    /// <em>number zero</em>. Some machine somewhere numbers its subsystems from zero, so an unset
    /// identifier would have addressed a real one. The empty name is the safe default because no
    /// machine has a subsystem called nothing, and its own test caught this on the first run.
    /// </para>
    /// </remarks>
    public bool IsNumber => _isNumber;

    /// <summary>Gets the number, when <see cref="IsNumber"/> is <see langword="true"/>. Zero otherwise.</summary>
    public uint Number => _number;

    /// <summary>Gets the identifier as text: the name, or the number in decimal.</summary>
    /// <remarks>
    /// Always usable, so a log or a dictionary key never has to branch. It is <em>not</em> an
    /// identity: two identifiers with the same text are still different if one is a number, which
    /// is what <see cref="Equals(SubsystemId)"/> says.
    /// </remarks>
    public string Name => _isNumber ? _number.ToString(CultureInfo.InvariantCulture) : _name ?? string.Empty;

    /// <summary>An identifier that travels as an <c>A</c> item.</summary>
    /// <param name="name">The name. Null becomes empty, which is a subsystem a machine declined to name.</param>
    /// <returns>The identifier.</returns>
    public static SubsystemId Named(string? name) => new(name ?? string.Empty, 0, false);

    /// <summary>An identifier that travels as a <c>U4</c> item.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The identifier.</returns>
    public static SubsystemId Numbered(uint number) => new(null, number, true);

    /// <summary>Compares two identifiers by kind and value.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they are the same.</returns>
    public static bool operator ==(SubsystemId left, SubsystemId right) => left.Equals(right);

    /// <summary>Compares two identifiers by kind and value.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(SubsystemId left, SubsystemId right) => !left.Equals(right);

    /// <inheritdoc />
    public bool Equals(SubsystemId other)
        => _isNumber == other._isNumber
            && (_isNumber
                ? _number == other._number
                : string.Equals(_name ?? string.Empty, other._name ?? string.Empty, StringComparison.Ordinal));

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SubsystemId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
        => _isNumber
            ? HashCode.Combine(true, _number)
            : HashCode.Combine(false, StringComparer.Ordinal.GetHashCode(_name ?? string.Empty));

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The name, or the number with a marker that it is one.</returns>
    public override string ToString() => _isNumber ? $"#{_number}" : _name ?? string.Empty;
}

/// <summary>
/// <c>SSACK</c> - the answer to a write, an event report, an alarm report or a code read.
/// </summary>
/// <remarks>
/// <para>
/// Quoted from the dictionary's own table, and the reason stream 18 is in this repository at all:
/// a stream whose rows are a composition of other streams' would not get an acknowledgement item
/// of its own, and this one has one whose three refusals <em>name stream 18's own data-write
/// transaction</em>. That is the two halves of the reference agreeing about a stream, which is
/// exactly what stream 17 could not show.
/// </para>
/// <para>
/// <strong>One item, four replies, and a table written for one of them.</strong> <c>SSACK</c> is
/// the whole of S18F12 and S18F14, half of S18F8 and part of S18F16 - but
/// <see cref="UnknownVariable"/>, <see cref="ValueOutOfRange"/> and
/// <see cref="ReadOnlyVariable"/> only mean anything in a write. Acknowledging an event report with
/// "value out of range" would be noise, so in the other three transactions the honest answers are
/// <see cref="Accepted"/> and <see cref="Other"/>, and this package says so rather than inventing
/// meanings the table does not give.
/// </para>
/// <para>
/// 4 to 63 are the table's own reserved and equipment-specific range, and they all read as
/// <see cref="Other"/> with the arriving code logged - never as the zero of this enum, which would
/// turn somebody else's refusal into acceptance.
/// </para>
/// </remarks>
public enum SubsystemAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Denied: no such variable. A write only.</summary>
    UnknownVariable = 1,

    /// <summary>Denied: the value is outside what the variable takes. A write only.</summary>
    ValueOutOfRange = 2,

    /// <summary>Denied: the variable is read-only. A write only.</summary>
    ReadOnlyVariable = 3,

    /// <summary>Some other answer: the table reserves 4 to 63.</summary>
    /// <remarks>Every code from 4 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>
/// <c>HCACK</c> - the answer to a subsystem command.
/// </summary>
/// <remarks>
/// <para>
/// The same dictionary entry <c>Barbatos.SecsGem.Gem</c> quotes for S2F42, which is why the names
/// and the numbers match member for member: one item cannot mean two things in one message set, so
/// a test in this package asserts the two enums agree. They are two types rather than one because
/// this package does not depend on GEM - a subsystem controller need not implement E30, and that
/// independence is the point of stream 18 being its own package.
/// </para>
/// <para>
/// <see cref="CompletionLater"/> is the one worth reading twice: the command was accepted and has
/// <em>not</em> finished, and the subsystem will say so later with an event report. A handler that
/// answers <see cref="Accepted"/> for slow work is telling the host it is done.
/// </para>
/// </remarks>
public enum SubsystemCommandAcknowledgement : byte
{
    /// <summary>Accepted and performed.</summary>
    Accepted = 0,

    /// <summary>There is no such command.</summary>
    InvalidCommand = 1,

    /// <summary>The command exists but cannot be performed now.</summary>
    CannotPerformNow = 2,

    /// <summary>At least one parameter is invalid; which ones are in the reply's parameter list.</summary>
    InvalidParameter = 3,

    /// <summary>Accepted; completion will be signalled by an event report.</summary>
    CompletionLater = 4,

    /// <summary>Rejected: the subsystem is already in the condition the command asks for.</summary>
    AlreadyInDesiredCondition = 5,

    /// <summary>There is no such subsystem.</summary>
    NoSuchSubsystem = 6,

    /// <summary>Some other answer: the table reserves 7 to 63.</summary>
    /// <remarks>Every code from 7 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 7,
}

/// <summary>
/// <c>CPACK</c> - what was wrong with one command parameter.
/// </summary>
/// <remarks>
/// <strong>There is no "accepted" here, and that is the dictionary's doing rather than an
/// omission.</strong> <c>CPACK</c>'s table starts at 1 and reserves 4 to 63: it has no code zero,
/// because the list in S18F2 carries the parameters that were <em>wrong</em> and not the ones that
/// were fine. A member for success would invite a reply that acknowledges every parameter, which
/// is a different message from the one E5 describes. The same entry serves GEM's S2F42.
/// </remarks>
public enum SubsystemParameterAcknowledgement : byte
{
    /// <summary>The parameter name is not one this subsystem knows.</summary>
    UnknownName = 1,

    /// <summary>The value is not legal for that parameter.</summary>
    IllegalValue = 2,

    /// <summary>The value is in a format that parameter does not take.</summary>
    IllegalFormat = 3,

    /// <summary>Some other complaint: the table reserves 4 to 63.</summary>
    /// <remarks>Every code from 4 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>
/// The category in the low bits of <c>ALCD</c>, quoted from the dictionary's table.
/// </summary>
/// <remarks>
/// <c>ALCD</c> is one byte carrying two things: the top bit says whether the alarm is set or
/// cleared, and the low seven bits are this. The table names eight categories and leaves 9 to 127
/// to the equipment, which all read as <see cref="Other"/>.
/// </remarks>
public enum SubsystemAlarmCategory : byte
{
    /// <summary>No category given.</summary>
    None = 0,

    /// <summary>Somebody could be hurt.</summary>
    PersonalSafety = 1,

    /// <summary>The machine could be damaged.</summary>
    EquipmentSafety = 2,

    /// <summary>A parameter is drifting.</summary>
    ParameterControlWarning = 3,

    /// <summary>A parameter is wrong.</summary>
    ParameterControlError = 4,

    /// <summary>Something has gone wrong that cannot be recovered from.</summary>
    IrrecoverableError = 5,

    /// <summary>The machine's own status needs attention.</summary>
    EquipmentStatusWarning = 6,

    /// <summary>An attention flag.</summary>
    AttentionFlags = 7,

    /// <summary>Data is suspect.</summary>
    DataIntegrity = 8,

    /// <summary>Some other category: the table leaves 9 to 127 to the equipment.</summary>
    /// <remarks>Every category from 9 upwards reads as this one, and the arriving value is logged.</remarks>
    Other = 9,
}

/// <summary>One subsystem, as S18F10 lists it.</summary>
/// <remarks>
/// Three items, and two of the three have no dictionary entry - see <see cref="SubsystemId"/> for
/// the identifier and <see cref="State"/> for the state.
/// </remarks>
public sealed class Subsystem
{
    /// <summary>Gets which subsystem - <c>SSID</c>.</summary>
    public required SubsystemId Id { get; init; }

    /// <summary>Gets what it is called - <c>SSNAME</c>.</summary>
    /// <remarks>Undefined in the dictionary, and written as an <c>A</c>: a name is text in all eight of the catalogue's identifier-and-name pairs, which is the one part of this that is not a choice.</remarks>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets what state it is in - <c>SSSTATE</c>.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This package writes it as an <c>A</c>, and that is an inference it owns.</strong>
    /// <c>SSSTATE</c> has no dictionary entry - and neither does any other item in this catalogue
    /// whose name ends in <c>STATE</c> that a message actually uses: <c>CJSTATE</c>,
    /// <c>JOBSTATE</c> and <c>RCPSTATE</c> are all undefined too, four for four, so the gap is the
    /// source's habit rather than anything about stream 18. (<c>PRSTATE</c> <em>is</em> defined,
    /// with ten codes, and no message in the catalogue names it.)
    /// </para>
    /// <para>
    /// Text rather than a code, for two reasons that are about this item and not about taste: it
    /// sits beside <c>SSNAME</c> in the one row that carries both, which is text; and E5 names no
    /// states for a subsystem, so there is no table to be a code <em>of</em> - a number here would
    /// be this library inventing a vocabulary, which is the mistake the carrier chapters spent
    /// three attempts not making.
    /// </para>
    /// <para>
    /// Reading is tolerant: a machine that sends its state as a number arrives here as that
    /// number's digits. Writing is not, because only the equipment end originates a state and it
    /// has to write something.
    /// </para>
    /// </remarks>
    public string State { get; init; } = string.Empty;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifier, the name and the state.</returns>
    public override string ToString()
        => Name.Length == 0 ? $"{Id} ({State})" : $"{Id} {Name} ({State})";
}

/// <summary>A command for one subsystem - S18F1.</summary>
public sealed class SubsystemCommand
{
    /// <summary>Gets which subsystem - <c>SSID</c>.</summary>
    public required SubsystemId Subsystem { get; init; }

    /// <summary>Gets the command - <c>RCMD</c>.</summary>
    public required string Command { get; init; }

    /// <summary>Gets its parameters - the <c>CPNAME</c> and <c>CPVAL</c> pairs, which may be empty.</summary>
    /// <remarks>Both are <c>A</c> in the dictionary, so both are text here and neither is inferred.</remarks>
    public IReadOnlyList<SubsystemParameter> Parameters { get; init; } = [];

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The subsystem, the command and how many parameters it carries.</returns>
    public override string ToString() => $"{Command} on {Subsystem}, {Parameters.Count} parameter(s)";
}

/// <summary>One command parameter - <c>CPNAME</c> and <c>CPVAL</c>.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Value">What it is set to.</param>
public readonly record struct SubsystemParameter(string Name, string Value);

/// <summary>What was wrong with one parameter - <c>CPNAME</c> and <c>CPACK</c>.</summary>
/// <param name="Name">Which parameter.</param>
/// <param name="Acknowledgement">What was wrong with it.</param>
/// <remarks>Only the parameters that were wrong appear; see <see cref="SubsystemParameterAcknowledgement"/>.</remarks>
public readonly record struct SubsystemParameterFault(
    string Name,
    SubsystemParameterAcknowledgement Acknowledgement);

/// <summary>The answer to a command - S18F2.</summary>
public sealed class SubsystemCommandResult
{
    /// <summary>Gets which subsystem answered - <c>SSID</c>.</summary>
    public required SubsystemId Subsystem { get; init; }

    /// <summary>Gets the answer - <c>HCACK</c>.</summary>
    public required SubsystemCommandAcknowledgement Acknowledgement { get; init; }

    /// <summary>Gets the parameters that were wrong, if any.</summary>
    /// <remarks>Empty for every answer but <see cref="SubsystemCommandAcknowledgement.InvalidParameter"/>, and E5 does not require it even then.</remarks>
    public IReadOnlyList<SubsystemParameterFault> Faults { get; init; } = [];

    /// <summary>Gets whether the command was taken.</summary>
    /// <remarks>
    /// <strong>Two codes are successes.</strong> Zero means done and four means accepted and not
    /// yet finished, which is the acknowledgement in this stream most likely to be read backwards.
    /// </remarks>
    public bool IsAccepted => Acknowledgement is SubsystemCommandAcknowledgement.Accepted
        or SubsystemCommandAcknowledgement.CompletionLater;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The subsystem and the answer.</returns>
    public override string ToString()
        => Faults.Count == 0
            ? $"{Subsystem}: {Acknowledgement}"
            : $"{Subsystem}: {Acknowledgement}, {Faults.Count} bad parameter(s)";
}

/// <summary>One of a subsystem's variables - S18F6 and S18F7.</summary>
public sealed class SubsystemVariable
{
    /// <summary>Gets which subsystem - <c>SSID</c>.</summary>
    public required SubsystemId Subsystem { get; init; }

    /// <summary>Gets which variable - <c>VID</c>, a <c>U4</c> in the dictionary.</summary>
    public required uint VariableId { get; init; }

    /// <summary>Gets its value - <c>SV</c>.</summary>
    /// <remarks>
    /// <c>SV</c> is an <c>A</c> in this catalogue, so it is written as text. That is narrower than
    /// E30, where a status variable may be any item - so a machine that sends a number arrives here
    /// as its digits. The tolerance is in the reading and the dictionary is followed in the
    /// writing, which is the rule everywhere in this stack.
    /// </remarks>
    public string Value { get; init; } = string.Empty;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The subsystem, the variable and its value.</returns>
    public override string ToString() => $"{Subsystem} VID {VariableId} = {Value}";
}

/// <summary>An event a subsystem raised - S18F11.</summary>
public sealed class SubsystemEvent
{
    /// <summary>Gets which subsystem - <c>SSID</c>.</summary>
    public required SubsystemId Subsystem { get; init; }

    /// <summary>Gets which event - <c>CEID</c>, a <c>U4</c> in the dictionary.</summary>
    public required uint EventId { get; init; }

    /// <summary>Gets when it happened - <c>TIME</c>, an <c>A[16]</c> in the dictionary.</summary>
    /// <remarks>
    /// Carried as the text that arrived and not parsed. E5's sixteen-character form is
    /// <c>YYYYMMDDhhmmsscc</c>, and a machine that sends the twelve-character form instead is
    /// still saying something a host can read - whereas a parse that failed would throw away the
    /// event along with the timestamp.
    /// </remarks>
    public string Time { get; init; } = string.Empty;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The subsystem, the event and the time.</returns>
    public override string ToString()
        => Time.Length == 0 ? $"{Subsystem} CEID {EventId}" : $"{Subsystem} CEID {EventId} at {Time}";
}

/// <summary>An alarm a subsystem raised - S18F13.</summary>
public sealed class SubsystemAlarm
{
    /// <summary>Gets which subsystem - <c>SSID</c>.</summary>
    public required SubsystemId Subsystem { get; init; }

    /// <summary>Gets which alarm - <c>ALID</c>, a <c>U4</c> in the dictionary.</summary>
    public required uint AlarmId { get; init; }

    /// <summary>Gets whether the alarm is set, which is the top bit of <c>ALCD</c>.</summary>
    /// <remarks><see langword="false"/> is the same message reporting that the alarm has cleared, which is the thing about <c>ALCD</c> most easily missed.</remarks>
    public required bool IsSet { get; init; }

    /// <summary>Gets what kind of alarm it is, which is the low seven bits of <c>ALCD</c>.</summary>
    public SubsystemAlarmCategory Category { get; init; } = SubsystemAlarmCategory.None;

    /// <summary>Gets the text - <c>ALTX</c>, an <c>A[120]</c> in the dictionary.</summary>
    /// <remarks>Written truncated to 120 characters rather than refused: an alarm with a long description is still an alarm.</remarks>
    public string Text { get; init; } = string.Empty;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>Whether it is set, the subsystem, the identifier, the category and the text.</returns>
    public override string ToString()
        => $"{(IsSet ? "set" : "cleared")} {Subsystem} ALID {AlarmId} {Category}: {Text}";
}

/// <summary>What a 2D code read found - S18F16.</summary>
/// <remarks>
/// The one pair in stream 18 that is not GEM's message set re-addressed, and - not by coincidence
/// worth relying on, but worth stating - the only pair whose every item name the dictionary
/// defines: <c>TARGETID</c> and <c>CONDITION</c> are <c>A[n]</c>, <c>MID</c> is <c>A[n]</c>,
/// <c>STATUS</c> is a <c>U1[1]</c> and <c>SSACK</c> has its table.
/// </remarks>
public sealed class SubsystemCodeCondition
{
    /// <summary>Gets what was read - <c>TARGETID</c>.</summary>
    public required string TargetId { get; init; }

    /// <summary>Gets whether the read succeeded - <c>SSACK</c>.</summary>
    public required SubsystemAcknowledgement Acknowledgement { get; init; }

    /// <summary>Gets the code that was read - <c>MID</c>.</summary>
    public string MaterialId { get; init; } = string.Empty;

    /// <summary>Gets the reader's status - <c>STATUS</c>, a <c>U1[1]</c>.</summary>
    /// <remarks>
    /// A bare number, because the dictionary gives <c>STATUS</c> a format and <strong>no table of
    /// values</strong>. There is nothing to turn it into an enum against, so it is not turned into
    /// one - the alternative being a vocabulary this library made up for a reader it has never
    /// seen.
    /// </remarks>
    public byte Status { get; init; }

    /// <summary>Gets what the reader says about the code - <c>CONDITION</c>.</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>Gets whether the read succeeded.</summary>
    public bool IsRead => Acknowledgement == SubsystemAcknowledgement.Accepted;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The target, the acknowledgement and what was read.</returns>
    public override string ToString()
        => $"{TargetId}: {Acknowledgement}, MID '{MaterialId}', STATUS {Status}, '{Condition}'";
}
