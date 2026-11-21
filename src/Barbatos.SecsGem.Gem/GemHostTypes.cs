// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Options;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// How the host side behaves. Bound from <c>Barbatos:GemHost</c> and validated on start.
/// </summary>
/// <remarks>
/// Short, because a host has far fewer decisions to make than a machine. It has no control state
/// to defend, no spool, and no reports of its own to define - it answers for its identity, and it
/// decides whether to agree to talk.
/// </remarks>
public sealed class GemHostOptions
{
    /// <summary>The configuration section these are bound from.</summary>
    public const string SectionName = "Barbatos:GemHost";

    /// <summary>Gets or sets the host's own MDLN, for S1F2 and S1F14.</summary>
    /// <remarks>
    /// The <em>host's</em>, not the machine's. A host that reports the equipment's model name back
    /// at it has told the machine nothing, and the pair is harder to read in a log than it needs
    /// to be.
    /// </remarks>
    public string ModelName { get; set; } = "BRBTOS-HOST";

    /// <summary>Gets or sets the host's own SOFTREV.</summary>
    public string SoftwareRevision { get; set; } = "0.1.0";

    /// <summary>Gets or sets a value indicating whether an equipment-initiated S1F13 is accepted.</summary>
    /// <remarks>
    /// <para>
    /// On by default. Off answers COMMACK 1, which is a real thing for a host to say: it is how a
    /// host under maintenance tells a machine to keep retrying rather than leaving it to time out
    /// against silence.
    /// </para>
    /// <para>
    /// It does not stop <see cref="GemHost.EstablishCommunicationsAsync"/>. Refusing the machine's
    /// request and then making one of your own is contradictory, but it is the host's contradiction
    /// to make, and a flag that quietly disabled an explicit call would be worse.
    /// </para>
    /// </remarks>
    public bool AcceptCommunications { get; set; } = true;
}

/// <summary>Validates <see cref="GemHostOptions"/> on start.</summary>
public sealed class GemHostOptionsValidator : IValidateOptions<GemHostOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, GemHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.ModelName))
        {
            failures.Add($"{nameof(GemHostOptions.ModelName)} (MDLN) must not be empty.");
        }
        else if (options.ModelName.Length > GemOptions.MaxIdentityLength)
        {
            failures.Add(
                $"{nameof(GemHostOptions.ModelName)} is {options.ModelName.Length} characters; "
                + $"E30 allows {GemOptions.MaxIdentityLength}.");
        }

        if (options.SoftwareRevision.Length > GemOptions.MaxIdentityLength)
        {
            failures.Add(
                $"{nameof(GemHostOptions.SoftwareRevision)} is {options.SoftwareRevision.Length} characters; "
                + $"E30 allows {GemOptions.MaxIdentityLength}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>MDLN and SOFTREV, as the other end reports them.</summary>
public sealed class GemEquipmentIdentity
{
    /// <summary>Initialises an identity.</summary>
    /// <param name="modelName">MDLN.</param>
    /// <param name="softwareRevision">SOFTREV.</param>
    public GemEquipmentIdentity(string modelName, string softwareRevision)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        ArgumentNullException.ThrowIfNull(softwareRevision);

        ModelName = modelName;
        SoftwareRevision = softwareRevision;
    }

    /// <summary>Gets the model name.</summary>
    public string ModelName { get; }

    /// <summary>Gets the software revision.</summary>
    public string SoftwareRevision { get; }

    /// <summary>Returns the model and revision.</summary>
    /// <returns>For example <c>BRBTOS-1 0.1.0</c>.</returns>
    public override string ToString() => $"{ModelName} {SoftwareRevision}";
}

/// <summary>One entry of a status variable namelist - S1F12.</summary>
/// <summary>
/// What a machine calls one collection event, and which variables it says the event is about -
/// S1F24.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the machine's own account of an event, not the host's.</strong> The variables
/// here are what the equipment says the event concerns; the variables a host will actually
/// <em>receive</em> with it are the reports linked through S2F33 and S2F35, which can be
/// reconfigured at any time. The two are easy to confuse and a host that treated this as its
/// report definition would be surprised the first time somebody relinked.
/// </para>
/// <para>
/// An event the machine does not have comes back with a zero-length name, the same way an unknown
/// variable does in <see cref="GemVariableDescription"/> - so <see cref="Exists"/> is how you tell
/// rather than a missing entry.
/// </para>
/// </remarks>
public sealed class GemEventDescription
{
    /// <summary>Initialises a description.</summary>
    /// <param name="id">CEID.</param>
    /// <param name="name">CENAME.</param>
    /// <param name="variableIds">The VIDs the machine says the event is about, in the order it gave them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="variableIds"/> is <see langword="null"/>.</exception>
    public GemEventDescription(ulong id, string name, IReadOnlyList<ulong> variableIds)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(variableIds);

        Id = id;
        Name = name;
        VariableIds = variableIds;
    }

    /// <summary>Gets the CEID.</summary>
    public ulong Id { get; }

    /// <summary>Gets the name, or an empty string when the machine has no such event.</summary>
    public string Name { get; }

    /// <summary>Gets the variables the machine says the event is about, in the order it gave them.</summary>
    public IReadOnlyList<ulong> VariableIds { get; }

    /// <summary>Gets a value indicating whether the machine has this event.</summary>
    /// <remarks>
    /// E5 answers an unknown identifier with a zero-length name rather than by leaving it out,
    /// which keeps the reply aligned with the request. So an empty name is the equipment saying
    /// "not mine", and it is worth a property rather than a comparison at every call site.
    /// </remarks>
    public bool Exists => Name.Length > 0;

    /// <summary>Returns the name, the identifier and how many variables it carries.</summary>
    /// <returns>For example <c>PPChanged (CEID 3), 2 variable(s)</c>.</returns>
    public override string ToString()
        => Name.Length == 0
            ? $"CEID {Id} is not one this machine has"
            : $"{Name} (CEID {Id}), {VariableIds.Count} variable(s)";
}

/// <summary>One entry of a status variable namelist - S1F12.</summary>
public sealed class GemVariableDescription
{
    /// <summary>Initialises a description.</summary>
    /// <param name="id">SVID.</param>
    /// <param name="name">SVNAME.</param>
    /// <param name="units">UNITS.</param>
    public GemVariableDescription(ulong id, string name, string units)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(units);

        Id = id;
        Name = name;
        Units = units;
    }

    /// <summary>Gets the identifier.</summary>
    public ulong Id { get; }

    /// <summary>Gets the name, or an empty string when the equipment does not have this one.</summary>
    public string Name { get; }

    /// <summary>Gets the units, which are an empty string for a variable that has none.</summary>
    public string Units { get; }

    /// <summary>Gets a value indicating whether the equipment knows this identifier.</summary>
    /// <remarks>
    /// E5 answers an unknown SVID with its name and units both zero-length rather than by leaving
    /// it out, which keeps the reply aligned with the request. So an empty name is the equipment
    /// saying "not mine", and it is worth a property rather than a comparison at every call site.
    /// </remarks>
    public bool Exists => Name.Length > 0;

    /// <summary>Returns the identifier, name and units.</summary>
    /// <returns>For example <c>2 Pressure (mbar)</c>.</returns>
    public override string ToString() => Units.Length == 0
        ? $"{Id} {Name}"
        : $"{Id} {Name} ({Units})";
}

/// <summary>One entry of an equipment constant namelist - S2F30.</summary>
/// <remarks>
/// The bounds are <strong>encoded items</strong> rather than values, for the same reason
/// <see cref="GemSpooledMessage.Body"/> is: an ECMIN may be any format E5 allows, the reply it
/// arrived in is disposed as soon as the call returns, and bytes can be kept where a live item
/// tree cannot. Decode one with <c>SecsItem.Decode</c> when you need it.
/// </remarks>
public sealed class GemConstantDescription
{
    /// <summary>Initialises a description.</summary>
    /// <param name="id">ECID.</param>
    /// <param name="name">ECNAME.</param>
    /// <param name="units">UNITS.</param>
    /// <param name="minimum">ECMIN, encoded, or empty when the equipment documents none.</param>
    /// <param name="maximum">ECMAX, encoded, or empty.</param>
    /// <param name="default">ECDEF, encoded, or empty.</param>
    public GemConstantDescription(
        ulong id,
        string name,
        string units,
        ReadOnlyMemory<byte> minimum,
        ReadOnlyMemory<byte> maximum,
        ReadOnlyMemory<byte> @default)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(units);

        Id = id;
        Name = name;
        Units = units;
        Minimum = minimum;
        Maximum = maximum;
        Default = @default;
    }

    /// <summary>Gets the identifier.</summary>
    public ulong Id { get; }

    /// <summary>Gets the name, or an empty string when the equipment does not have this one.</summary>
    public string Name { get; }

    /// <summary>Gets the units, which are an empty string for a constant that has none.</summary>
    public string Units { get; }

    /// <summary>Gets ECMIN as an encoded item, or empty when the equipment documents no minimum.</summary>
    public ReadOnlyMemory<byte> Minimum { get; }

    /// <summary>Gets ECMAX as an encoded item, or empty when the equipment documents no maximum.</summary>
    public ReadOnlyMemory<byte> Maximum { get; }

    /// <summary>Gets ECDEF as an encoded item, or empty when the equipment documents no default.</summary>
    public ReadOnlyMemory<byte> Default { get; }

    /// <summary>Gets a value indicating whether the equipment knows this identifier.</summary>
    public bool Exists => Name.Length > 0;

    /// <summary>Returns the identifier, name and units.</summary>
    /// <returns>For example <c>100 ReadOnlyLimit</c>.</returns>
    public override string ToString() => Units.Length == 0
        ? $"{Id} {Name}"
        : $"{Id} {Name} ({Units})";
}

/// <summary>One alarm, as S5F1, S5F6 or S5F8 reports it.</summary>
public sealed class GemAlarmReport
{
    /// <summary>Initialises a report.</summary>
    /// <param name="code">ALCD.</param>
    /// <param name="id">ALID.</param>
    /// <param name="text">ALTX.</param>
    public GemAlarmReport(byte code, ulong id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Code = code;
        Id = id;
        Text = text;
    }

    /// <summary>Gets ALCD, whose top bit is the alarm's state and whose low bits are its category.</summary>
    public byte Code { get; }

    /// <summary>Gets the identifier.</summary>
    public ulong Id { get; }

    /// <summary>Gets the text.</summary>
    public string Text { get; }

    /// <summary>Gets a value indicating whether the alarm is in alarm, rather than cleared.</summary>
    /// <remarks>ALCD bit 7. The same message carries both the set and the clear, which is why this is a bit and not a function.</remarks>
    public bool IsSet => (Code & 0x80) != 0;

    /// <summary>Returns the state, the identifier and the text.</summary>
    /// <returns>For example <c>SET 9000 Chamber pressure out of range</c>.</returns>
    public override string ToString() => $"{(IsSet ? "SET" : "CLEARED")} {Id} {Text}";
}

/// <summary>One report inside an event report the equipment sent.</summary>
/// <remarks>
/// <strong><see cref="Values"/> is alive only while the handler runs.</strong> It is a view into
/// the message that arrived, and that message is disposed as soon as the event returns. Read what
/// you need, or copy it - a reference kept past the handler is a reference into a pooled buffer
/// somebody else now owns.
/// </remarks>
public sealed class GemReceivedReport
{
    internal GemReceivedReport(ulong id, SecsItem values)
    {
        Id = id;
        Values = values;
    }

    /// <summary>Gets RPTID.</summary>
    public ulong Id { get; }

    /// <summary>Gets the values, in the order the report defines them. Valid only inside the handler.</summary>
    public SecsItem Values { get; }

    /// <summary>Returns the identifier and how many values it carries.</summary>
    /// <returns>For example <c>6000 (2 values)</c>.</returns>
    public override string ToString() => $"{Id} ({Values.Count} values)";
}

/// <summary>An event report the equipment sent - S6F11 or S6F13.</summary>
public sealed class GemEventReportEventArgs : EventArgs
{
    internal GemEventReportEventArgs(
        uint dataId,
        ulong eventId,
        IReadOnlyList<GemReceivedReport> reports,
        byte function)
    {
        DataId = dataId;
        EventId = eventId;
        Reports = reports;
        Function = function;
    }

    /// <summary>Gets DATAID.</summary>
    public uint DataId { get; }

    /// <summary>Gets CEID.</summary>
    public ulong EventId { get; }

    /// <summary>Gets which stream 6 primary carried this - 11, 13 or 3.</summary>
    /// <remarks>
    /// <para>
    /// One event for three messages, and this is how a handler tells them apart. It was added when
    /// S6F3 arrived, because <see cref="Reports"/> cannot do it: an S6F3 carries no reports, and
    /// neither does an S6F11 for an event with nothing linked to it. Before that the annotated
    /// report was indistinguishable from the plain one too, which was a smaller version of the
    /// same hole.
    /// </para>
    /// <para>
    /// 11 is the plain event report, 13 the annotated one, and 3 the discrete variable data send -
    /// the row whose body the reference gives as <c>DATAID</c> and <c>CEID</c> and nothing else.
    /// </para>
    /// </remarks>
    public byte Function { get; }

    /// <summary>Gets the reports the event carries. Valid only inside the handler.</summary>
    public IReadOnlyList<GemReceivedReport> Reports { get; }

    /// <summary>Gets or sets the ACKC6 to answer with.</summary>
    /// <remarks>
    /// Accepted unless a handler says otherwise. E30 lets a host refuse a report, and a host that
    /// could not store what it was sent should say so rather than acknowledge it - which is the one
    /// decision about an inbound report that cannot be made for you.
    /// </remarks>
    public GemReportAcknowledgement Acknowledgement { get; set; } = GemReportAcknowledgement.Accepted;
}

/// <summary>An alarm the equipment sent - S5F1.</summary>
public sealed class GemHostAlarmEventArgs : EventArgs
{
    internal GemHostAlarmEventArgs(GemAlarmReport alarm) => Alarm = alarm;

    /// <summary>Gets the alarm.</summary>
    public GemAlarmReport Alarm { get; }

    /// <summary>Gets or sets the ACKC5 to answer with, when the equipment asked for a reply.</summary>
    public GemAlarmAcknowledgement Acknowledgement { get; set; } = GemAlarmAcknowledgement.Accepted;
}

/// <summary>One exception, as S5F9 posts it.</summary>
/// <remarks>
/// <para>
/// Four fields, all four of them items the reference defines: <c>TIMESTAMP</c>, <c>EXID</c>,
/// <c>EXTYPE</c> and <c>EXMESSAGE</c>. Three of the four are free text with no length the
/// reference pins down, which is the difference between an exception and an alarm - an alarm has a
/// numeric <c>ALID</c> the host can look up, and an exception is named by whatever the machine
/// calls it.
/// </para>
/// <para>
/// <strong>Nothing here is a code.</strong> <c>EXTYPE</c> has no table in the reference, so this
/// layer does not turn it into an enumeration: a machine's exception types are its own, and a
/// guessed set of names would be the kind of invention that is hardest to take back once an
/// application has switched on it.
/// </para>
/// </remarks>
public sealed class GemExceptionPost
{
    /// <summary>Initialises a post.</summary>
    /// <param name="postedAt">TIMESTAMP, or <see langword="null"/> when it would not parse.</param>
    /// <param name="id">EXID.</param>
    /// <param name="type">EXTYPE.</param>
    /// <param name="message">EXMESSAGE.</param>
    public GemExceptionPost(DateTime? postedAt, string id, string type, string message)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(message);

        PostedAt = postedAt;
        Id = id;
        Type = type;
        Message = message;
    }

    /// <summary>Gets TIMESTAMP in UTC, or <see langword="null"/> when the machine sent something unreadable.</summary>
    /// <remarks>
    /// Nullable rather than defaulted, because the two are different facts and a host deciding
    /// whether an exception is stale needs to know which it has. <c>default</c> would read as the
    /// first of January in year one, which no machine means.
    /// </remarks>
    public DateTime? PostedAt { get; }

    /// <summary>Gets EXID, the name this machine gives the exception.</summary>
    public string Id { get; }

    /// <summary>Gets EXTYPE, how the machine classifies it.</summary>
    public string Type { get; }

    /// <summary>Gets EXMESSAGE, the text for a person to read.</summary>
    public string Message { get; }

    /// <summary>Returns the type, the identifier and the message.</summary>
    /// <returns>For example <c>ALARM PRESSURE-HIGH Chamber pressure above limit</c>.</returns>
    public override string ToString() => Type.Length == 0
        ? $"{Id} {Message}"
        : $"{Type} {Id} {Message}";
}

/// <summary>An exception the equipment posted - S5F9.</summary>
public sealed class GemHostExceptionEventArgs : EventArgs
{
    internal GemHostExceptionEventArgs(GemExceptionPost exception) => Exception = exception;

    /// <summary>Gets the exception.</summary>
    public GemExceptionPost Exception { get; }

    /// <summary>Gets or sets the ACKC5 to answer with, when the equipment asked for a reply.</summary>
    /// <remarks>
    /// Unlike an alarm's, this arrives already set to
    /// <see cref="GemAlarmAcknowledgement.Error"/> when the body did not read - a handler that
    /// wants to accept such a post anyway has to say so, rather than the other way round.
    /// </remarks>
    public GemAlarmAcknowledgement Acknowledgement { get; set; } = GemAlarmAcknowledgement.Accepted;
}

/// <summary>The equipment asking permission to send something too large for one block - S6F5.</summary>
/// <remarks>
/// <para>
/// <strong>The only multi-block inquire in the reference's documented half.</strong> Stream 2 and
/// stream 4 each have one too, S2F39 and S4F25, and both are rows the reference illustrates rather
/// than defines - and both answer with <c>GRANT</c> where this one answers with <c>GRANT6</c>. So
/// this pair is where <c>DATALENGTH</c> gets read for the first time in this repository, with the
/// other two rows unimplemented and nothing to check the reading against.
/// </para>
/// <para>
/// <strong><see cref="Length"/> has no units.</strong> The reference types <c>DATALENGTH</c> as a
/// <c>U4</c> and records nothing about what it counts, so this layer passes the number through and
/// does not name it bytes - the same choice stream 21's <c>LENGTH</c> got, and for the same
/// reason. A host using it to decide whether it has room is relying on an agreement with that
/// machine, not on the standard.
/// </para>
/// </remarks>
public sealed class GemMultiBlockInquiryEventArgs : EventArgs
{
    internal GemMultiBlockInquiryEventArgs(uint dataId, uint length)
    {
        DataId = dataId;
        Length = length;
    }

    /// <summary>Gets DATAID.</summary>
    public uint DataId { get; }

    /// <summary>Gets DATALENGTH, in whatever the host and the machine have agreed it counts.</summary>
    public uint Length { get; }

    /// <summary>Gets or sets the GRANT6 to answer with.</summary>
    /// <remarks>
    /// Granted unless a handler says otherwise, because a handshake that exists so a host can
    /// refuse should not refuse by default. Setting <see cref="GemMultiBlockGrant.Other"/> puts a
    /// code on the wire that E5 reserves - legal, as equipment-specific, and not stopped here,
    /// since a host and a machine that have agreed what it means know more about their pair than
    /// this layer does.
    /// </remarks>
    public GemMultiBlockGrant Grant { get; set; } = GemMultiBlockGrant.Ok;
}

/// <summary>A group of trace samples the equipment sent - S6F1.</summary>
/// <remarks>
/// <para>
/// <strong><see cref="Samples"/> is alive only while the handler runs</strong>, like an event
/// report's values: each entry is a view into the message that arrived, and that message is
/// disposed as soon as the event returns.
/// </para>
/// <para>
/// <see cref="SampleNumber"/> and <see cref="SampledAt"/> describe the <em>last</em> sample in the
/// group, which is what lets a host put a group that arrived late back in order.
/// </para>
/// </remarks>
public sealed class GemTraceDataEventArgs : EventArgs
{
    internal GemTraceDataEventArgs(
        uint traceId,
        uint sampleNumber,
        DateTimeOffset? sampledAt,
        IReadOnlyList<SecsItem> samples)
    {
        TraceId = traceId;
        SampleNumber = sampleNumber;
        SampledAt = sampledAt;
        Samples = samples;
    }

    /// <summary>Gets TRID, the trace this group belongs to.</summary>
    public uint TraceId { get; }

    /// <summary>Gets SMPLN, the number of the last sample in this group.</summary>
    public uint SampleNumber { get; }

    /// <summary>Gets STIME, when the last sample was taken, or <see langword="null"/> when it was not a time.</summary>
    public DateTimeOffset? SampledAt { get; }

    /// <summary>Gets the samples, each a list of values in the order the trace asked for them. Valid only inside the handler.</summary>
    public IReadOnlyList<SecsItem> Samples { get; }

    /// <summary>Gets or sets the ACKC6 to answer with.</summary>
    public GemReportAcknowledgement Acknowledgement { get; set; } = GemReportAcknowledgement.Accepted;

    /// <summary>Returns the trace, the sample number and how many samples arrived.</summary>
    /// <returns>For example <c>trace 1 up to sample 20: 10 sample(s)</c>.</returns>
    public override string ToString() => $"trace {TraceId} up to sample {SampleNumber}: {Samples.Count} sample(s)";
}

/// <summary>Something an operator at the machine sent the host - S10F1.</summary>
/// <remarks>
/// The one inbound message with a person waiting on the other end of it. A handler that cannot
/// deal with the text now should say so through <see cref="Acknowledgement"/>: accepting it and
/// dropping it leaves somebody at a machine believing a host read what they typed.
/// </remarks>
public sealed class GemTerminalRequestEventArgs : EventArgs
{
    internal GemTerminalRequestEventArgs(byte terminalId, string text)
    {
        TerminalId = terminalId;
        Text = text;
    }

    /// <summary>Gets the terminal it came from.</summary>
    public byte TerminalId { get; }

    /// <summary>Gets the text, as the operator wrote it.</summary>
    /// <remarks>Not trimmed: TEXT is a variable-length field, so its spaces are somebody's and not padding.</remarks>
    public string Text { get; }

    /// <summary>Gets or sets the ACKC10 to answer with.</summary>
    public GemTerminalAcknowledgement Acknowledgement { get; set; } = GemTerminalAcknowledgement.Accepted;

    /// <summary>Returns where it came from and what it said.</summary>
    /// <returns>For example <c>terminal 0: cassette jammed in load port 2</c>.</returns>
    public override string ToString() => $"terminal {TerminalId}: {Text}";
}

/// <summary>An error report the equipment sent - stream 9.</summary>
/// <remarks>
/// A stream 9 message is the other end saying it could not make sense of something, and it never
/// expects a reply. Hearing them is most of the value: an S9F5 means a host asked for a function
/// the machine does not implement, and an S9F9 means a reply this host sent arrived too late.
/// </remarks>
public sealed class GemHostErrorEventArgs : EventArgs
{
    internal GemHostErrorEventArgs(byte function, ReadOnlyMemory<byte> offendingHeader, string description)
    {
        Function = function;
        OffendingHeader = offendingHeader;
        Description = description;
    }

    /// <summary>Gets the stream 9 function: 1, 3, 5, 7, 9, 11 or 13.</summary>
    public byte Function { get; }

    /// <summary>Gets the ten-byte header of the message being complained about, or empty for S9F13.</summary>
    /// <remarks>Copied out of the report, so it outlives the handler.</remarks>
    public ReadOnlyMemory<byte> OffendingHeader { get; }

    /// <summary>Gets S9F13's MEXP and EDID as text, or an empty string for every other function.</summary>
    public string Description { get; }

    /// <summary>Returns the designator and whatever detail the report carried.</summary>
    /// <returns>For example <c>S9F5 (header 00 07 81 03 00 00 40 01 00 00)</c>.</returns>
    public override string ToString() => Description.Length > 0
        ? $"S9F{Function} ({Description})"
        : $"S9F{Function} (header {Convert.ToHexString(OffendingHeader.Span)})";
}

/// <summary>The host's communication state changed.</summary>
public sealed class GemHostCommunicationStateChangedEventArgs : EventArgs
{
    internal GemHostCommunicationStateChangedEventArgs(bool isCommunicating, string? reason)
    {
        IsCommunicating = isCommunicating;
        Reason = reason;
    }

    /// <summary>Gets a value indicating whether application messages may flow.</summary>
    public bool IsCommunicating { get; }

    /// <summary>Gets what changed it, for a log.</summary>
    public string? Reason { get; }

    /// <summary>Returns the state and the reason.</summary>
    /// <returns>For example <c>Communicating (S1F13 from the equipment)</c>.</returns>
    public override string ToString()
        => $"{(IsCommunicating ? "Communicating" : "NotCommunicating")} ({Reason})";
}
