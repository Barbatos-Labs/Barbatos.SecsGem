// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Subsystems;

/// <summary>
/// Every stream 18 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Both ends go through this class, so a change to a body is a change to both ends or it does not
/// compile.
/// </para>
/// <para>
/// <strong>What the provenance is.</strong> Not one of stream 18's sixteen rows is a definition -
/// all sixteen are flagged as a representative subset, the same showing stream 3 and stream 16
/// have, both of which ship here. What makes the stream readable anyway is the item table:
/// seventeen of the twenty names it uses are defined, and the three that are not are the
/// subsystem's own identity, name and state.
/// </para>
/// <code>
/// SSID       --   undefined - see SubsystemId, which carries either reading
/// SSNAME     --   undefined - written A, as a name is in all eight of the catalogue's ID/NAME pairs
/// SSSTATE    --   undefined - written A; every STATE item a message uses is undefined, four for four
/// SSACK      B[1] defined, four codes written for the data write and reserving 4-63
/// HCACK      B[1] defined, seven codes, the same entry GEM's S2F42 quotes
/// CPACK      B[1] defined, three codes and no zero - the list carries what was wrong
/// RCMD       A    defined
/// CPNAME     A    defined
/// CPVAL      A    defined
/// VID        U4   defined
/// SV         A    defined - narrower than E30, where a status variable may be any item
/// CEID       U4   defined
/// TIME       A16  defined
/// ALCD       B[1] defined, a bit field: top bit set/cleared, low bits a category
/// ALID       U4   defined
/// ALTX       A120 defined
/// TARGETID   A    defined
/// MID        A    defined
/// STATUS     U1   defined, and with no table of values
/// CONDITION  A    defined
/// </code>
/// <para>
/// The bodies below are therefore a careful reading of item lists whose items are nearly all
/// defined, rather than a quotation of rows that are. Where a row mirrors one of GEM's - and six
/// of the eight pairs do - the nesting follows the GEM row it mirrors, because that one <em>is</em>
/// a definition: S18F1 is S2F41's <c>L,2 { RCMD, L,n { L,2 { CPNAME, CPVAL } } }</c> with an
/// <c>SSID</c> in front, and reading it any other way would make this package disagree with the
/// firmer half of the same reference.
/// </para>
/// <para>
/// <strong>Nesting is the one thing a flat item list cannot pin down</strong>, which is why the
/// readers are more generous than the writers. Each body is written one way and read in every
/// shape a machine might plausibly send it in.
/// </para>
/// </remarks>
internal static class SubsystemCodec
{
    /// <summary>What <c>ALTX</c> is capped at in the dictionary.</summary>
    private const int AlarmTextLength = 120;

    // ------------------------------------------------------------- the identifier

    /// <summary>Writes an <c>SSID</c> in whichever format the caller chose.</summary>
    internal static SecsItem SubsystemIdBody(SubsystemId id)
        => (id.IsNumber ? SecsItem.U4(id.Number) : SecsItem.A(id.Name)).Named("SSID");

    /// <summary>
    /// Reads an <c>SSID</c> by the format it arrived in, descending into a list of one.
    /// </summary>
    /// <remarks>
    /// <strong>By format and never by content.</strong> An <c>A</c> carrying "7" is the name "7"
    /// and not the number 7, because this library cannot know a machine means them as one and
    /// guessing would make two subsystems into one. The carrier package learned this with
    /// <c>Enum.TryParse</c>, which accepted a number where a name was promised and silently put a
    /// port into the wrong access mode.
    /// </remarks>
    internal static bool TryReadSubsystemId(SecsItem? item, out SubsystemId id)
    {
        id = default;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadSubsystemId(item.Items[0], out id);
        }

        if (item.Format.IsText())
        {
            id = SubsystemId.Named(Text(item));
            return true;
        }

        if (!TryReadUnsigned(item, out var number))
        {
            return false;
        }

        id = SubsystemId.Numbered((uint)Math.Min(number, uint.MaxValue));
        return true;
    }

    // ----------------------------------------------------------------- commanding

    /// <summary>S18F1 - <c>L,3 { SSID, RCMD, L,n { L,2 { CPNAME, CPVAL } } }</c>.</summary>
    internal static SecsItem CommandBody(SubsystemCommand command)
    {
        var parameters = new SecsItem[command.Parameters.Count];

        for (var i = 0; i < command.Parameters.Count; i++)
        {
            parameters[i] = SecsItem.L(
                SecsItem.A(command.Parameters[i].Name).Named("CPNAME"),
                SecsItem.A(command.Parameters[i].Value).Named("CPVAL"));
        }

        return SecsItem.L(
            SubsystemIdBody(command.Subsystem),
            SecsItem.A(command.Command).Named("RCMD"),
            SecsItem.L(parameters));
    }

    /// <summary>Reads S18F1.</summary>
    internal static bool TryReadCommand(SecsItem? body, out SubsystemCommand? command)
    {
        command = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out var id))
        {
            return false;
        }

        List<SubsystemParameter> parameters = [];

        if (body.Count > 2)
        {
            foreach (var entry in Entries(body.Items[2], 2))
            {
                parameters.Add(new SubsystemParameter(Text(entry[0]), Text(entry[1])));
            }
        }

        command = new SubsystemCommand
        {
            Subsystem = id,
            Command = Text(body.Items[1]),
            Parameters = parameters,
        };

        return true;
    }

    /// <summary>S18F2 - <c>L,3 { SSID, HCACK, L,n { L,2 { CPNAME, CPACK } } }</c>.</summary>
    internal static SecsItem CommandResultBody(SubsystemCommandResult result)
    {
        var faults = new SecsItem[result.Faults.Count];

        for (var i = 0; i < result.Faults.Count; i++)
        {
            faults[i] = SecsItem.L(
                SecsItem.A(result.Faults[i].Name).Named("CPNAME"),
                SecsItem.B((byte)result.Faults[i].Acknowledgement).Named("CPACK"));
        }

        return SecsItem.L(
            SubsystemIdBody(result.Subsystem),
            SecsItem.B((byte)result.Acknowledgement).Named("HCACK"),
            SecsItem.L(faults));
    }

    /// <summary>Reads S18F2.</summary>
    internal static bool TryReadCommandResult(
        SecsItem? body,
        out SubsystemId id,
        out byte acknowledgement,
        out List<(string Name, byte Code)> faults)
    {
        id = default;
        acknowledgement = 0;
        faults = [];

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out id) || ReadCode(body.Items[1]) is not { } code)
        {
            return false;
        }

        acknowledgement = code;

        if (body.Count > 2)
        {
            foreach (var entry in Entries(body.Items[2], 2))
            {
                if (ReadCode(entry[1]) is { } fault)
                {
                    faults.Add((Text(entry[0]), fault));
                }
            }
        }

        return true;
    }

    // -------------------------------------------------------------------- status

    /// <summary>S18F4 - <c>L,2 { SSID, SSSTATE }</c>.</summary>
    internal static SecsItem StatusBody(SubsystemId id, string state)
        => SecsItem.L(
            SubsystemIdBody(id),
            SecsItem.A(state).Named("SSSTATE"));

    /// <summary>Reads S18F4.</summary>
    internal static bool TryReadStatus(SecsItem? body, out SubsystemId id, out string state)
    {
        id = default;
        state = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out id))
        {
            return false;
        }

        state = Word(body.Items[1]);
        return true;
    }

    // ----------------------------------------------------------------- variables

    /// <summary>S18F5 - <c>L,2 { SSID, VID }</c>.</summary>
    internal static SecsItem VariableRequestBody(SubsystemId id, uint variableId)
        => SecsItem.L(
            SubsystemIdBody(id),
            SecsItem.U4(variableId).Named("VID"));

    /// <summary>Reads S18F5.</summary>
    internal static bool TryReadVariableRequest(SecsItem? body, out SubsystemId id, out uint variableId)
    {
        id = default;
        variableId = 0;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out id) || !TryReadUnsigned(body.Items[1], out var vid))
        {
            return false;
        }

        variableId = (uint)Math.Min(vid, uint.MaxValue);
        return true;
    }

    /// <summary>S18F6 and S18F7 - <c>L,3 { SSID, VID, SV }</c>.</summary>
    /// <remarks>
    /// One writer for the two, because the two rows carry the same three items in the same order -
    /// which is also why the read end of a variable and the write end of one cannot be told apart
    /// by their bodies, only by their function numbers.
    /// </remarks>
    internal static SecsItem VariableBody(SubsystemVariable variable)
        => SecsItem.L(
            SubsystemIdBody(variable.Subsystem),
            SecsItem.U4(variable.VariableId).Named("VID"),
            SecsItem.A(variable.Value).Named("SV"));

    /// <summary>Reads S18F6 or S18F7.</summary>
    internal static bool TryReadVariable(SecsItem? body, out SubsystemVariable? variable)
    {
        variable = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out var id) || !TryReadUnsigned(body.Items[1], out var vid))
        {
            return false;
        }

        variable = new SubsystemVariable
        {
            Subsystem = id,
            VariableId = (uint)Math.Min(vid, uint.MaxValue),
            Value = Word(body.Items[2]),
        };

        return true;
    }

    /// <summary>S18F8 - <c>L,2 { SSID, SSACK }</c>.</summary>
    internal static SecsItem WriteResultBody(SubsystemId id, byte acknowledgement)
        => SecsItem.L(
            SubsystemIdBody(id),
            SecsItem.B(acknowledgement).Named("SSACK"));

    /// <summary>Reads S18F8, tolerating a bare <c>SSACK</c> from a machine that left the identifier off.</summary>
    internal static bool TryReadWriteResult(SecsItem? body, out byte acknowledgement)
    {
        acknowledgement = 0;

        if (body is null)
        {
            return false;
        }

        if (!body.IsList)
        {
            if (ReadCode(body) is not { } bare)
            {
                return false;
            }

            acknowledgement = bare;
            return true;
        }

        // The identifier is echoed and this end already knows which subsystem it asked, so the
        // code is what matters: it is read from the last item rather than the second, which is the
        // same position in both shapes.
        if (body.Count == 0 || ReadCode(body.Items[^1]) is not { } code)
        {
            return false;
        }

        acknowledgement = code;
        return true;
    }

    // ---------------------------------------------------------------- the listing

    /// <summary>S18F10 - <c>L,n { L,3 { SSID, SSNAME, SSSTATE } }</c>.</summary>
    internal static SecsItem ListBody(IReadOnlyList<Subsystem>? subsystems)
    {
        if (subsystems is null || subsystems.Count == 0)
        {
            return SecsItem.L();
        }

        var entries = new SecsItem[subsystems.Count];

        for (var i = 0; i < subsystems.Count; i++)
        {
            entries[i] = SecsItem.L(
                SubsystemIdBody(subsystems[i].Id),
                SecsItem.A(subsystems[i].Name).Named("SSNAME"),
                SecsItem.A(subsystems[i].State).Named("SSSTATE"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S18F10, nested or flat.</summary>
    internal static List<Subsystem> ReadList(SecsItem? body)
    {
        List<Subsystem> subsystems = [];

        if (body is null || body.Count == 0)
        {
            return subsystems;
        }

        foreach (var entry in Entries(body, 3))
        {
            if (!TryReadSubsystemId(entry[0], out var id))
            {
                continue;
            }

            subsystems.Add(new Subsystem
            {
                Id = id,
                Name = Word(entry[1]),
                State = Word(entry[2]),
            });
        }

        return subsystems;
    }

    // ----------------------------------------------------------- events and alarms

    /// <summary>S18F11 - <c>L,3 { SSID, CEID, TIME }</c>.</summary>
    internal static SecsItem EventBody(SubsystemEvent report)
        => SecsItem.L(
            SubsystemIdBody(report.Subsystem),
            SecsItem.U4(report.EventId).Named("CEID"),
            SecsItem.A(report.Time).Named("TIME"));

    /// <summary>Reads S18F11.</summary>
    internal static bool TryReadEvent(SecsItem? body, out SubsystemEvent? report)
    {
        report = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out var id) || !TryReadUnsigned(body.Items[1], out var ceid))
        {
            return false;
        }

        report = new SubsystemEvent
        {
            Subsystem = id,
            EventId = (uint)Math.Min(ceid, uint.MaxValue),
            Time = body.Count > 2 ? Word(body.Items[2]) : string.Empty,
        };

        return true;
    }

    /// <summary>S18F13 - <c>L,4 { SSID, ALCD, ALID, ALTX }</c>.</summary>
    internal static SecsItem AlarmBody(SubsystemAlarm alarm)
        => SecsItem.L(
            SubsystemIdBody(alarm.Subsystem),
            SecsItem.B(AlarmCode(alarm.IsSet, alarm.Category)).Named("ALCD"),
            SecsItem.U4(alarm.AlarmId).Named("ALID"),
            SecsItem.A(Truncate(alarm.Text, AlarmTextLength)).Named("ALTX"));

    /// <summary>Reads S18F13.</summary>
    internal static bool TryReadAlarm(SecsItem? body, out SubsystemAlarm? alarm, out byte category)
    {
        alarm = null;
        category = 0;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        if (!TryReadSubsystemId(body.Items[0], out var id)
            || ReadCode(body.Items[1]) is not { } code
            || !TryReadUnsigned(body.Items[2], out var alid))
        {
            return false;
        }

        // The top bit is whether the alarm is set; the rest is the category, which the caller maps
        // because only it has the logger to report an unnamed one with.
        category = (byte)(code & 0x7F);

        alarm = new SubsystemAlarm
        {
            Subsystem = id,
            AlarmId = (uint)Math.Min(alid, uint.MaxValue),
            IsSet = (code & 0x80) != 0,
            Text = body.Count > 3 ? Word(body.Items[3]) : string.Empty,
        };

        return true;
    }

    /// <summary>Builds an <c>ALCD</c> from its two halves.</summary>
    internal static byte AlarmCode(bool isSet, SubsystemAlarmCategory category)
        => (byte)((isSet ? 0x80 : 0x00) | ((byte)category & 0x7F));

    // ------------------------------------------------------------- the 2D code read

    /// <summary>S18F15 - a bare <c>TARGETID</c>.</summary>
    internal static SecsItem TargetBody(string targetId) => SecsItem.A(targetId).Named("TARGETID");

    /// <summary>Reads S18F15, descending into a list of one.</summary>
    internal static bool TryReadTarget(SecsItem? item, out string targetId)
    {
        targetId = string.Empty;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadTarget(item.Items[0], out targetId);
        }

        targetId = Word(item);
        return true;
    }

    /// <summary>S18F16 - <c>L,5 { TARGETID, SSACK, MID, STATUS, CONDITION }</c>.</summary>
    internal static SecsItem CodeConditionBody(SubsystemCodeCondition condition)
        => SecsItem.L(
            SecsItem.A(condition.TargetId).Named("TARGETID"),
            SecsItem.B((byte)condition.Acknowledgement).Named("SSACK"),
            SecsItem.A(condition.MaterialId).Named("MID"),
            SecsItem.U1(condition.Status).Named("STATUS"),
            SecsItem.A(condition.Condition).Named("CONDITION"));

    /// <summary>Reads S18F16.</summary>
    internal static bool TryReadCodeCondition(
        SecsItem? body,
        out string targetId,
        out byte acknowledgement,
        out string materialId,
        out byte status,
        out string condition)
    {
        targetId = string.Empty;
        acknowledgement = 0;
        materialId = string.Empty;
        status = 0;
        condition = string.Empty;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        if (ReadCode(body.Items[1]) is not { } code)
        {
            return false;
        }

        targetId = Word(body.Items[0]);
        acknowledgement = code;

        if (body.Count > 2)
        {
            materialId = Word(body.Items[2]);
        }

        if (body.Count > 3)
        {
            status = Byte(body.Items[3]);
        }

        if (body.Count > 4)
        {
            condition = Word(body.Items[4]);
        }

        return true;
    }

    // --------------------------------------------------------- acknowledgement codes

    /// <summary>Writes a one-item acknowledgement body - <c>SSACK</c>.</summary>
    internal static SecsItem CodeBody(byte code, string name) => SecsItem.B(code).Named(name);

    /// <summary>Reads an acknowledgement code, descending into a list of one if that is what arrived.</summary>
    /// <returns>The code, or <see langword="null"/> when there is none to read.</returns>
    internal static byte? ReadCode(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? ReadCode(item.Items[0]) : null;
        }

        if (item.Count == 0)
        {
            return null;
        }

        if (item.Format is SecsFormat.Binary or SecsFormat.U1)
        {
            return item.FirstValueOrDefault<byte>();
        }

        return TryReadUnsigned(item, out var value) && value <= byte.MaxValue ? (byte)value : null;
    }

    // ------------------------------------------------------------------- the pieces

    /// <summary>Reads the repeating entries of a multi-entry body, nested or flat.</summary>
    /// <param name="body">The body.</param>
    /// <param name="width">How many fields an entry has.</param>
    /// <returns>The entries, each exactly <paramref name="width"/> items wide.</returns>
    /// <remarks>
    /// Three shapes are accepted: a list of entries, which is what this library writes; one bare
    /// entry, which is a machine with one subsystem not bothering to wrap it; and a flat run of
    /// fields. An entry short of <paramref name="width"/> fields is dropped rather than padded - a
    /// subsystem with no identifier is not a subsystem, and inventing one would put a state on
    /// something that does not exist.
    /// </remarks>
    private static List<SecsItem[]> Entries(SecsItem body, int width)
    {
        List<SecsItem[]> entries = [];

        if (body.Count == 0 || !body.IsList)
        {
            return entries;
        }

        if (body.Items[0].IsList)
        {
            foreach (var entry in body.Items)
            {
                if (entry.IsList && entry.Count >= width)
                {
                    entries.Add([.. entry.Items[..width]]);
                }
            }

            return entries;
        }

        for (var i = 0; i + width <= body.Count; i += width)
        {
            entries.Add([.. body.Items[i..(i + width)]]);
        }

        return entries;
    }

    /// <summary>Reads a text field, taking a number as the digits it is written with.</summary>
    /// <remarks>
    /// The tolerance that lets <c>SSSTATE</c>, <c>SSNAME</c> and <c>SV</c> be read from a machine
    /// that sends them numerically. It is deliberately <em>not</em> used for <c>SSID</c>, where the
    /// format is the identity - see <see cref="TryReadSubsystemId"/>.
    /// </remarks>
    private static string Word(SecsItem item)
    {
        if (item.IsList || item.Count == 0)
        {
            return string.Empty;
        }

        return item.Format.IsText()
            ? item.GetString().TrimEnd()
            : TryReadUnsigned(item, out var value)
                ? value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
    }

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    private static string Truncate(string? value, int length)
        => value is null ? string.Empty : value.Length <= length ? value : value[..length];

    private static byte Byte(SecsItem item) => (byte)Math.Min(Unsigned(item), byte.MaxValue);

    private static ulong Unsigned(SecsItem item) => TryReadUnsigned(item, out var value) ? value : 0;

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// Every number in this stream is an identifier, a variable number or a code, so a value too
    /// large for its field is clamped rather than refused - the same rule the other E5 packages
    /// here keep, and for the same reason: dropping the message would hide which subsystem the
    /// sender's bug is about.
    /// </remarks>
    private static bool TryReadUnsigned(SecsItem item, out ulong value)
    {
        value = 0;

        if (item.IsList || item.Count == 0)
        {
            return false;
        }

        switch (item.Format)
        {
            case SecsFormat.Binary:
            case SecsFormat.U1:
                value = item.FirstValueOrDefault<byte>();
                return true;
            case SecsFormat.U2:
                value = item.FirstValueOrDefault<ushort>();
                return true;
            case SecsFormat.U4:
                value = item.FirstValueOrDefault<uint>();
                return true;
            case SecsFormat.U8:
                value = item.FirstValueOrDefault<ulong>();
                return true;
            case SecsFormat.I1:
                return NonNegative(item.FirstValueOrDefault<sbyte>(), out value);
            case SecsFormat.I2:
                return NonNegative(item.FirstValueOrDefault<short>(), out value);
            case SecsFormat.I4:
                return NonNegative(item.FirstValueOrDefault<int>(), out value);
            case SecsFormat.I8:
                return NonNegative(item.FirstValueOrDefault<long>(), out value);
            default:
                return false;
        }
    }

    private static bool NonNegative(long signed, out ulong value)
    {
        value = signed < 0 ? 0 : (ulong)signed;
        return signed >= 0;
    }
}
