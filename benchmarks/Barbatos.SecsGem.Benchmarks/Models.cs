// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Mapping;

namespace Barbatos.SecsGem.Benchmarks;

/// <summary>The equipment's control state, as GEM defines it.</summary>
public enum ControlState
{
    /// <summary>Off line.</summary>
    Offline = 1,

    /// <summary>On line, local control.</summary>
    OnlineLocal = 4,

    /// <summary>On line, remote control.</summary>
    OnlineRemote = 5,
}

/// <summary>One variable in a report: an identifier and its value as text.</summary>
/// <remarks>
/// Every member is <c>{ get; set; }</c> on purpose. An <c>init</c> accessor no constructor
/// parameter covers is one of the shapes the source generator declines, and a type the generator
/// declined would quietly turn the Generated arm of these benchmarks into the Reflection arm.
/// </remarks>
public sealed class VariableValue
{
    /// <summary>Gets or sets the variable name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the value, as the equipment reports it.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>One report inside an event: its identifier and the variables it carries.</summary>
public sealed class EventReport
{
    /// <summary>Gets or sets the report identifier.</summary>
    public uint ReportId { get; set; }

    /// <summary>Gets or sets the variables in this report.</summary>
    public List<VariableValue> Values { get; set; } = [];
}

/// <summary>S6F11 - the message GEM equipment sends more than any other.</summary>
/// <remarks>
/// Carrying <see cref="SecsMessageAttribute"/> means the generator finds this type from the
/// attribute alone, and finds <see cref="EventReport"/> and <see cref="VariableValue"/> through the
/// closure over its members - which is the behaviour worth measuring, since a real message is
/// never one flat type.
/// </remarks>
[SecsMessage(6, 11, Name = "EventReportSend")]
public sealed class EventReportSend
{
    /// <summary>Gets or sets the data identifier.</summary>
    public uint DataId { get; set; }

    /// <summary>Gets or sets the collection event identifier.</summary>
    public uint CollectionEventId { get; set; }

    /// <summary>Gets or sets the moment the equipment raised the event.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Gets or sets the control state at the time of the event.</summary>
    public ControlState State { get; set; }

    /// <summary>Gets or sets the reports this event carries.</summary>
    public List<EventReport> Reports { get; set; } = [];
}

/// <summary>A positional record with no attributes at all.</summary>
/// <remarks>
/// The case the mapper exists to make effortless, and the one where the two resolvers reach the
/// same answer by different routes: the constructor parameter list is the declaration order, so
/// reflection is exact here too. Measuring it shows what the generator costs when correctness is
/// not the argument for it.
/// </remarks>
public sealed record Person(string Name, int Age, string[] Emails);

/// <summary>Builds the objects the mapper benchmarks map.</summary>
internal static class SampleObjects
{
    /// <summary>Builds an event report with the given shape.</summary>
    public static EventReportSend Report(int reports, int valuesPerReport)
    {
        var report = new EventReportSend
        {
            DataId = 1,
            CollectionEventId = 7_001,
            Timestamp = new DateTime(2026, 11, 15, 9, 30, 15, 250, DateTimeKind.Utc),
            State = ControlState.OnlineRemote,
            Reports = new List<EventReport>(reports),
        };

        for (var r = 0; r < reports; r++)
        {
            var values = new List<VariableValue>(valuesPerReport);

            for (var v = 0; v < valuesPerReport; v++)
            {
                values.Add(new VariableValue
                {
                    Name = $"VID-{r}-{v}",
                    Value = $"{(r * 100) + v}.25",
                });
            }

            report.Reports.Add(new EventReport
            {
                ReportId = (uint)(5_000 + r),
                Values = values,
            });
        }

        return report;
    }

    /// <summary>Builds the attribute-free record.</summary>
    public static Person Person()
        => new("Pham The Hung", 36, ["hung@barbatos.dev", "labs@barbatos.dev"]);
}
