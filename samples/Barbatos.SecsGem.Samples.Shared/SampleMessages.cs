// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Samples.Shared;

/// <summary>Message shapes shared by the equipment and host samples.</summary>
/// <remarks>
/// Hand-built item trees rather than mapped objects, because a sample about the wire should show
/// what actually goes on the wire. <c>Barbatos.SecsGem.Mapper</c> is the other way round and the
/// transcode sample covers it.
/// </remarks>
public static class SampleMessages
{
    /// <summary>S1F1 Are You There - a primary with no body at all.</summary>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage AreYouThere() => new(1, 1) { Name = "AreYouThere" };

    /// <summary>S1F13 Establish Communications Request - what a host says first, after Select.</summary>
    /// <param name="model">MDLN.</param>
    /// <param name="revision">SOFTREV.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage EstablishCommunications(string model = "HOST", string revision = "1.0.0") => new(1, 13)
    {
        Name = "EstablishCommunicationsRequest",
        Item = SecsItem.L(
            SecsItem.A(model, length: 6).Named("MDLN"),
            SecsItem.A(revision).Named("SOFTREV")),
    };

    /// <summary>S1F14 Establish Communications Request Acknowledge.</summary>
    /// <param name="accepted"><see langword="true"/> for COMMACK 0, accepted.</param>
    /// <param name="model">MDLN.</param>
    /// <param name="revision">SOFTREV.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage EstablishCommunicationsAcknowledge(
        bool accepted = true,
        string model = "BRBTOS",
        string revision = "0.1.0") => new(1, 14, replyExpected: false)
    {
        Name = "EstablishCommunicationsAcknowledge",
        Item = SecsItem.L(
            SecsItem.B((byte)(accepted ? 0 : 1)).Named("COMMACK"),
            SecsItem.L(
                SecsItem.A(model, length: 6).Named("MDLN"),
                SecsItem.A(revision).Named("SOFTREV"))),
    };

    /// <summary>S6F12 Event Report Acknowledge.</summary>
    /// <param name="accepted"><see langword="true"/> for ACKC6 0, accepted.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage EventReportAcknowledge(bool accepted = true) => new(6, 12, replyExpected: false)
    {
        Name = "EventReportAcknowledge",
        Item = SecsItem.B((byte)(accepted ? 0 : 1)).Named("ACKC6"),
    };

    /// <summary>S2F41 Host Command Send.</summary>
    /// <param name="command">RCMD.</param>
    /// <param name="parameters">CPNAME and CPVAL pairs.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage HostCommand(string command, params (string Name, string Value)[] parameters) => new(2, 41)
    {
        Name = "HostCommandSend",
        Item = SecsItem.L(
            SecsItem.A(command).Named("RCMD"),
            SecsItem.L(parameters.Select(parameter => SecsItem.L(
                SecsItem.A(parameter.Name).Named("CPNAME"),
                SecsItem.A(parameter.Value).Named("CPVAL"))))),
    };

    /// <summary>S2F42 Host Command Acknowledge.</summary>
    /// <param name="accepted"><see langword="true"/> for HCACK 0, accepted.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage HostCommandAcknowledge(bool accepted = true) => new(2, 42, replyExpected: false)
    {
        Name = "HostCommandAcknowledge",
        Item = SecsItem.L(
            SecsItem.B((byte)(accepted ? 0 : 1)).Named("HCACK"),
            SecsItem.L()),
    };

    /// <summary>S1F2 On Line Data - the model name and software revision.</summary>
    /// <param name="model">MDLN. Six characters on real equipment.</param>
    /// <param name="revision">SOFTREV.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage OnLineData(string model = "BRBTOS", string revision = "0.1.0") => new(1, 2, replyExpected: false)
    {
        Name = "OnLineData",
        Item = SecsItem.L(
            SecsItem.A(model, length: 6).Named("MDLN"),
            SecsItem.A(revision).Named("SOFTREV")),
    };

    /// <summary>S6F11 Event Report Send - one event carrying two reports.</summary>
    /// <param name="eventId">CEID.</param>
    /// <returns>The message. Dispose it.</returns>
    /// <remarks>
    /// The shape a GEM link spends its day carrying, and the one worth watching a decoder handle:
    /// four levels of nesting, three different numeric widths and a boolean.
    /// </remarks>
    public static SecsMessage EventReportSend(uint eventId = 7001) => new(6, 11)
    {
        Name = "EventReportSend",
        Item = SecsItem.L(
            SecsItem.U4(1u).Named("DATAID"),
            SecsItem.U4(eventId).Named("CEID"),
            SecsItem.L(
                Report(100u, "PRESSURE", SecsItem.F8(1013.25d)),
                Report(101u, "TEMPERATURE", SecsItem.F4(23.5f)))),
    };

    /// <summary>S5F1 Alarm Report Send.</summary>
    /// <param name="alarmId">ALID.</param>
    /// <param name="text">ALTX.</param>
    /// <param name="set"><see langword="true"/> when the alarm is being raised rather than cleared.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage AlarmReportSend(ulong alarmId, string text, bool set = true) => new(5, 1)
    {
        Name = "AlarmReportSend",
        Item = SecsItem.L(
            SecsItem.B((byte)(set ? 0b1000_0000 : 0)).Named("ALCD"),
            SecsItem.U8(alarmId).Named("ALID"),
            SecsItem.A(text, length: 40).Named("ALTX")),
    };

    private static SecsItem Report(uint reportId, string name, SecsItem value) => SecsItem.L(
        SecsItem.U4(reportId).Named("RPTID"),
        SecsItem.L(
            SecsItem.A(name).Named("VID"),
            value.Named("VALUE")));
}
