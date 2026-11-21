// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// One variable the equipment publishes: a status variable or an event-associated data value.
/// </summary>
/// <remarks>
/// <para>
/// The value is a delegate rather than a field, because a status variable is a question about the
/// machine right now. Caching it in the model would mean reporting the chamber pressure from
/// whenever the model was last updated, which is the kind of bug that reads as a sensor fault.
/// </para>
/// <para>
/// <see cref="Read"/> returns a freshly built item and the <em>caller</em> owns it. The equipment
/// puts it into a reply and disposes the reply, so a <see cref="Read"/> that hands back a
/// long-lived item it also keeps will have that item disposed underneath it.
/// </para>
/// </remarks>
public sealed class GemVariable
{
    /// <summary>Gets the SVID or DVID.</summary>
    public required uint Id { get; init; }

    /// <summary>Gets the name, as a host-facing namelist would report it.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the units, in E5 unit notation. Empty when the value is dimensionless.</summary>
    public string Units { get; init; } = string.Empty;

    /// <summary>Gets whether this is a status variable or a data value.</summary>
    public GemVariableKind Kind { get; init; } = GemVariableKind.Status;

    /// <summary>Gets the delegate that reads the current value.</summary>
    /// <remarks>Must return a new item each call. The caller disposes it.</remarks>
    public required Func<SecsItem> Read { get; init; }

    /// <summary>Gets the collection event raised when this variable crosses one of its limits.</summary>
    /// <remarks>
    /// <para>
    /// E30 makes the limit bands host-configurable through S2F45 and leaves the collection event
    /// to the equipment, because S2F45 carries no CEID: the host says which band it cares about,
    /// and the machine documents what it calls the crossing. So the band comes from the host and
    /// this comes from the model.
    /// </para>
    /// <para>
    /// <see langword="null"/> means a crossing on this variable is tracked and not reported, which
    /// is what equipment that has not documented an event for it should do.
    /// </para>
    /// </remarks>
    public uint? LimitEventId { get; init; }
}

/// <summary>
/// One equipment constant: a setting the host may read, and sometimes write.
/// </summary>
/// <remarks>
/// A constant with no <see cref="Write"/> is read-only, and an attempt to set it is answered with
/// <see cref="GemEquipmentConstantAcknowledgement.OutOfRange"/> rather than being accepted and
/// quietly dropped. Range checking belongs inside <see cref="Write"/>: only the equipment knows
/// whether a value is reachable, and a model that held a minimum and a maximum would be describing
/// a machine it cannot see.
/// </remarks>
public sealed class GemEquipmentConstant
{
    /// <summary>Gets the ECID.</summary>
    public required uint Id { get; init; }

    /// <summary>Gets the name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the units, in E5 unit notation. Empty when the value is dimensionless.</summary>
    public string Units { get; init; } = string.Empty;

    /// <summary>Gets the delegate that reads the current value.</summary>
    /// <remarks>Must return a new item each call. The caller disposes it.</remarks>
    public required Func<SecsItem> Read { get; init; }

    /// <summary>Gets the delegate that applies a new value, or <see langword="null"/> when read-only.</summary>
    /// <remarks>
    /// Returns <see langword="false"/> to reject the value as out of range. The item passed in
    /// belongs to the incoming message: read it, do not keep it, and do not dispose it.
    /// </remarks>
    public Func<SecsItem, bool>? Write { get; init; }

    /// <summary>Gets the smallest value the host may set, for S2F30. Optional.</summary>
    /// <remarks>
    /// <para>
    /// These three exist for the namelist reply and nothing else. <see cref="Write"/> remains the
    /// authority on whether a value is acceptable, because a range is not always a pair of bounds -
    /// a setpoint may be limited by what the chamber is doing right now, which no static minimum
    /// can express.
    /// </para>
    /// <para>
    /// Left <see langword="null"/>, S2F30 reports a zero-length item in its place, which E5 allows
    /// and which says "no documented bound" rather than inventing one.
    /// </para>
    /// </remarks>
    public Func<SecsItem>? Minimum { get; init; }

    /// <summary>Gets the largest value the host may set, for S2F30. Optional.</summary>
    public Func<SecsItem>? Maximum { get; init; }

    /// <summary>Gets the factory default, for S2F30. Optional.</summary>
    public Func<SecsItem>? Default { get; init; }

    /// <summary>Gets a value indicating whether the host may set this constant.</summary>
    public bool IsWritable => Write is not null;
}

/// <summary>One collection event the equipment can report.</summary>
/// <remarks>
/// A definition, not a state. Whether an event is enabled and which reports it is linked to are
/// both set by the host at run time, through S2F37 and S2F35, and live in
/// <see cref="GemEquipmentModel"/> rather than here.
/// </remarks>
public sealed class GemCollectionEvent
{
    /// <summary>Gets the CEID.</summary>
    public required uint Id { get; init; }

    /// <summary>Gets the name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets a value indicating whether the event is enabled before the host says otherwise.</summary>
    /// <remarks>
    /// E30 leaves the initial state to the implementation. Enabled is the friendlier default for
    /// equipment that may be watched before it is configured.
    /// </remarks>
    public bool EnabledByDefault { get; init; } = true;

    /// <summary>Gets the variables this event is about, in order - the <c>VID</c> list of S1F24.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Not the same thing as a report.</strong> A report is what a <em>host</em> asked to be
    /// sent with an event, through S2F33 and S2F35, and it can be redefined at any time. This is
    /// what the <em>equipment</em> says the event is about, and it does not change: S1F24 is a
    /// namelist, in the same family as S1F12 and S2F30, and a namelist describes the machine rather
    /// than the conversation.
    /// </para>
    /// <para>
    /// Empty by default, which answers S1F24 with the event's name and no variables - the honest
    /// answer for equipment that has not said which variables an event is about, and not a refusal:
    /// an event with no associated variables is a legitimate thing for a machine to have.
    /// </para>
    /// </remarks>
    public IReadOnlyList<uint> VariableIds { get; init; } = [];
}

/// <summary>One report: an identifier and the variables it carries, in order.</summary>
/// <remarks>
/// A report defined here is a <em>default</em>. S2F33 lets the host replace the whole set, which is
/// the normal way a host configures equipment it did not write - so anything defined here should be
/// treated as a starting point rather than a guarantee.
/// </remarks>
public sealed class GemReport
{
    /// <summary>Gets the RPTID.</summary>
    public required uint Id { get; init; }

    /// <summary>Gets the variable identifiers this report carries, in report order.</summary>
    public required IReadOnlyList<uint> VariableIds { get; init; }
}

/// <summary>One alarm the equipment can raise.</summary>
public sealed class GemAlarm
{
    /// <summary>Gets the ALID.</summary>
    public required ulong Id { get; init; }

    /// <summary>Gets the alarm text, as S5F1 ALTX carries it.</summary>
    /// <remarks>E30 caps ALTX at 120 characters; longer text is truncated on the wire.</remarks>
    public required string Text { get; init; }

    /// <summary>Gets the collection event raised when the alarm is set, or zero for none.</summary>
    public uint SetEventId { get; init; }

    /// <summary>Gets the collection event raised when the alarm is cleared, or zero for none.</summary>
    public uint ClearEventId { get; init; }

    /// <summary>Gets a value indicating whether the alarm reports before the host says otherwise.</summary>
    public bool EnabledByDefault { get; init; } = true;
}

/// <summary>What a remote command handler is given.</summary>
/// <remarks>
/// The parameter items belong to the incoming S2F41 and are disposed with it. Read them inside the
/// handler; do not keep one past the call, and do not dispose one.
/// </remarks>
public sealed class GemRemoteCommandContext
{
    /// <summary>Initialises the context.</summary>
    /// <param name="command">The RCMD.</param>
    /// <param name="parameters">The CPNAME and CPVAL pairs, keyed by name.</param>
    public GemRemoteCommandContext(string command, IReadOnlyDictionary<string, SecsItem> parameters)
    {
        Command = command ?? throw new ArgumentNullException(nameof(command));
        Parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
    }

    /// <summary>Gets the command name, as S2F41 RCMD carries it.</summary>
    public string Command { get; }

    /// <summary>Gets the parameters, keyed by CPNAME. Ordinal comparison, as the wire is bytes.</summary>
    public IReadOnlyDictionary<string, SecsItem> Parameters { get; }

    /// <summary>Reads a parameter as text, when it is present and is a text item.</summary>
    /// <param name="name">The CPNAME.</param>
    /// <returns>The value, or <see langword="null"/> when absent.</returns>
    public string? GetText(string name)
        => Parameters.TryGetValue(name, out var item) && item.Format.IsText() ? item.GetString() : null;

    /// <summary>Reads a parameter as a number, when it is present and the width matches.</summary>
    /// <typeparam name="T">The element type, which must match the item's format width.</typeparam>
    /// <param name="name">The CPNAME.</param>
    /// <returns>The first element, or <see langword="null"/> when absent or not readable as <typeparamref name="T"/>.</returns>
    public T? GetValue<T>(string name)
        where T : unmanaged, IEquatable<T>
        => Parameters.TryGetValue(name, out var item) && item.TryGetSpan<T>(out var values) && values.Length > 0
            ? values[0]
            : null;
}

/// <summary>Performs a remote command.</summary>
/// <param name="context">The command and its parameters.</param>
/// <param name="cancellationToken">Cancels the work.</param>
/// <returns>The HCACK to answer with.</returns>
/// <remarks>
/// Return <see cref="GemHostCommandAcknowledgement.CompletionLater"/> for anything that will not be
/// finished by the time the handler returns, and raise a collection event when it is. A handler
/// that blocks until a wafer is processed is holding up the dispatch loop, and therefore every
/// other message on the link.
/// </remarks>
public delegate ValueTask<GemHostCommandAcknowledgement> GemRemoteCommandHandler(
    GemRemoteCommandContext context,
    CancellationToken cancellationToken);

/// <summary>One remote command the equipment accepts.</summary>
public sealed class GemRemoteCommand
{
    /// <summary>Gets the RCMD this command answers to.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the handler.</summary>
    public required GemRemoteCommandHandler Handler { get; init; }

    /// <summary>Gets a value indicating whether the command needs the on-line remote control state.</summary>
    /// <remarks>
    /// True for anything that changes what the machine is doing, which is almost everything. A
    /// command that only reads can be left available in on-line local, and E30 does not forbid it.
    /// </remarks>
    public bool RequiresRemoteControl { get; init; } = true;
}
