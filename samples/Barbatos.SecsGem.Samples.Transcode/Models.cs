// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Mapping;

namespace Barbatos.SecsGem.Samples.Transcode;

/// <summary>
/// A plain record with no attributes at all.
/// </summary>
/// <remarks>
/// This is the convention-over-configuration case. Nothing here mentions SECS-II, and the
/// mapping is still exact:
/// <list type="bullet">
///   <item><description>the record becomes an <c>L</c> of three items;</description></item>
///   <item><description>item order is the constructor's parameter order;</description></item>
///   <item><description><c>string</c> becomes <c>A</c>, <c>int</c> becomes <c>I4</c>;</description></item>
///   <item><description><c>string[]</c> becomes a list of <c>A</c> items, because one ASCII item cannot hold three strings.</description></item>
/// </list>
/// </remarks>
/// <param name="Name">The person's name.</param>
/// <param name="Age">The person's age.</param>
/// <param name="Emails">Their email addresses.</param>
public sealed record Person(string Name, int Age, string[] Emails);

/// <summary>
/// A GEM S1F3 request, with attributes where the protocol disagrees with the convention.
/// </summary>
/// <remarks>
/// <c>[SecsMessage]</c> turns the type into a whole message, so nobody writes
/// <c>new SecsMessage(1, 3)</c> by hand. <c>[SecsItem(SecsFormat.U4)]</c> is needed because a
/// SVID is <c>U4</c> on the wire while the natural C# type for a list of ids is
/// <c>IReadOnlyList&lt;uint&gt;</c> - which would infer <c>U4</c> anyway, so the attribute is
/// there to pin it against someone later changing the property to <c>int</c>.
/// </remarks>
[SecsMessage(1, 3, Name = "SelectedEquipmentStatusRequest")]
public sealed class StatusVariableRequest
{
    /// <summary>Gets or sets the status variable ids being asked for.</summary>
    [SecsItem(SecsFormat.U4, Name = "SVID")]
    public uint[] StatusVariableIds { get; set; } = [];
}

/// <summary>
/// A GEM S6F11 event report, the shape that exercises every mapping rule at once.
/// </summary>
/// <remarks>
/// Nested objects, a list of objects, a fixed-width ASCII timestamp, an enum, a
/// <see cref="DateTime"/> through a built-in converter, and a member that never travels.
/// </remarks>
[SecsMessage(6, 11, Name = "EventReportSend")]
public sealed class EventReportSend
{
    /// <summary>Gets or sets the data id.</summary>
    public uint DataId { get; set; }

    /// <summary>Gets or sets the collection event id.</summary>
    public uint CollectionEventId { get; set; }

    /// <summary>Gets or sets the reports in this event.</summary>
    public List<EventReport> Reports { get; set; } = [];

    /// <summary>Gets or sets when the equipment raised the event.</summary>
    /// <remarks>Written as a 16-character GEM timestamp by <c>SecsDateTimeConverter</c>.</remarks>
    public DateTime Timestamp { get; set; }

    /// <summary>Gets or sets the equipment's current state.</summary>
    /// <remarks>An enum maps through its underlying type, so this is a <c>U1</c> on the wire.</remarks>
    public EquipmentControlState ControlState { get; set; }

    /// <summary>Gets or sets when this object was built locally.</summary>
    /// <remarks>Never sent: it is bookkeeping, not protocol.</remarks>
    [SecsIgnore]
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>One report inside an <see cref="EventReportSend"/>.</summary>
public sealed class EventReport
{
    /// <summary>Gets or sets the report id.</summary>
    public uint ReportId { get; set; }

    /// <summary>Gets or sets the variable values in this report.</summary>
    public List<VariableValue> Values { get; set; } = [];
}

/// <summary>One variable value inside an <see cref="EventReport"/>.</summary>
public sealed class VariableValue
{
    /// <summary>Gets or sets the variable's name, padded to the 16 characters GEM equipment expects.</summary>
    [SecsItem(Length = 16, Name = "VNAME")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the value, as text.</summary>
    [SecsItem(Name = "VVALUE")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>The GEM equipment control state.</summary>
public enum EquipmentControlState : byte
{
    /// <summary>Equipment offline.</summary>
    EquipmentOffline = 1,

    /// <summary>Attempting to go online.</summary>
    AttemptOnline = 2,

    /// <summary>Host offline.</summary>
    HostOffline = 3,

    /// <summary>Online, local control.</summary>
    OnlineLocal = 4,

    /// <summary>Online, remote control.</summary>
    OnlineRemote = 5,
}
