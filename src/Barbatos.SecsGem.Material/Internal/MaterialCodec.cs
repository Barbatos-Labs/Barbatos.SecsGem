// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.Material;

/// <summary>
/// Every stream 3 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Both ends go through this class, so a change to a body is a change to both ends or it does not
/// compile. That matters more here than anywhere else in this repository, because of what the
/// provenance is.
/// </para>
/// <para>
/// <strong>Not one row of stream 3 is firm.</strong> The catalogue shipped in this repository
/// carries a flag for entries its source marks as a representative subset rather than a
/// definition, and for stream 3 it is set on all thirty-six rows - where stream 12, for
/// comparison, has seventeen of nineteen firm. Five of the item names stream 3 uses have no
/// dictionary entry at all. So the bodies below are read off the item lists and the formats of the
/// items that <em>are</em> defined, and they are a careful reading rather than a quotation. Two
/// fields had to be inferred outright:
/// </para>
/// <code>
/// DESTLOC      written U4[1], as LOC is - it is a location, and the two sit side by side in S3F35
/// MATLSTATUS   written B[1], as every other single-byte field in this stream is
/// </code>
/// <para>
/// Both are read out of any numeric format, so a machine that disagrees about the format is still
/// understood; it is only what this library <em>writes</em> that the inference decides. If a
/// machine turns out to disagree, this file is the only one to change.
/// </para>
/// <para>
/// <strong>The multi-entry bodies.</strong> S3F2 and S3F4 report on material, plural: a machine
/// with four cassettes answers S3F1 with four of something. An item list cannot say whether that
/// is a list of three-element entries or a flat run, so they are written as
/// <c>L,n { L,3 { ... } }</c> and read three ways - that, a flat list of fields repeated, and a
/// single bare entry. The same tolerance stream 12 needed, for the same reason.
/// </para>
/// </remarks>
internal static class MaterialCodec
{
    // --------------------------------------------------------------- the host asks

    /// <summary>S3F2 - Material Status Data.</summary>
    internal static SecsItem StatusBody(IReadOnlyList<MaterialStatus> material)
    {
        var entries = new SecsItem[material.Count];

        for (var i = 0; i < material.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(material[i].MaterialId).Named("MID"),
                SecsItem.U4(material[i].Location).Named("LOC"),
                SecsItem.B(material[i].Quantity).Named("QUA"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S3F2.</summary>
    internal static bool TryReadStatus(SecsItem? body, out List<MaterialStatus> material)
    {
        material = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var entry in Entries(body, 3))
        {
            material.Add(new MaterialStatus
            {
                MaterialId = Text(entry[0]),
                Location = UInt32(entry[1]),
                Quantity = Byte(entry[2]),
            });
        }

        return true;
    }

    /// <summary>S3F4 - Time to Completion Data.</summary>
    internal static SecsItem CompletionBody(IReadOnlyList<MaterialCompletion> material)
    {
        var entries = new SecsItem[material.Count];

        for (var i = 0; i < material.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(material[i].MaterialId).Named("MID"),
                SecsItem.U4(material[i].Location).Named("LOC"),
                SecsItem.U4(material[i].TimeToCompletion).Named("TTC"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S3F4.</summary>
    internal static bool TryReadCompletion(SecsItem? body, out List<MaterialCompletion> material)
    {
        material = [];

        if (body is null || !body.IsList)
        {
            return false;
        }

        foreach (var entry in Entries(body, 3))
        {
            material.Add(new MaterialCompletion
            {
                MaterialId = Text(entry[0]),
                Location = UInt32(entry[1]),
                TimeToCompletion = UInt32(entry[2]),
            });
        }

        return true;
    }

    /// <summary>S3F9 - Material Location Request. One bare <c>MID</c>, not a list.</summary>
    internal static SecsItem MaterialIdBody(string materialId) => SecsItem.A(materialId).Named("MID");

    /// <summary>Reads a bare <c>MID</c>, descending into a list of one if that is what arrived.</summary>
    internal static string? TryReadMaterialId(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? TryReadMaterialId(item.Items[0]) : null;
        }

        return item.Format.IsText() ? Text(item) : null;
    }

    /// <summary>S3F5, S3F7, S3F10 - material and where it is.</summary>
    internal static SecsItem LocationBody(MaterialLocation material)
        => SecsItem.L(
            SecsItem.A(material.MaterialId).Named("MID"),
            SecsItem.U4(material.Location).Named("LOC"));

    /// <summary>Reads S3F5, S3F7 and S3F10. A zero-length list means "no such material".</summary>
    internal static bool TryReadLocation(SecsItem? body, out MaterialLocation? material)
    {
        material = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 2)
        {
            return false;
        }

        material = new MaterialLocation
        {
            MaterialId = Text(body.Items[0]),
            Location = UInt32(body.Items[1]),
        };

        return true;
    }

    /// <summary>S3F11 - Material Transfer Request.</summary>
    internal static SecsItem TransferBody(MaterialTransfer transfer)
        => SecsItem.L(
            SecsItem.A(transfer.MaterialId).Named("MID"),
            SecsItem.U4(transfer.Destination).Named("DESTLOC"));

    /// <summary>Reads S3F11.</summary>
    internal static bool TryReadTransfer(SecsItem? body, out MaterialTransfer? transfer)
    {
        transfer = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        transfer = new MaterialTransfer
        {
            MaterialId = Text(body.Items[0]),
            Destination = UInt32(body.Items[1]),
        };

        return true;
    }

    /// <summary>S3F13 - Material Status Change Send.</summary>
    internal static SecsItem StatusChangeBody(MaterialStatusChange change)
        => SecsItem.L(
            SecsItem.A(change.MaterialId).Named("MID"),
            SecsItem.U4(change.Location).Named("LOC"),
            SecsItem.B(change.Status).Named("MATLSTATUS"));

    /// <summary>Reads S3F13.</summary>
    internal static bool TryReadStatusChange(SecsItem? body, out MaterialStatusChange? change)
    {
        change = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        change = new MaterialStatusChange
        {
            MaterialId = Text(body.Items[0]),
            Location = UInt32(body.Items[1]),
            Status = Byte(body.Items[2]),
        };

        return true;
    }

    /// <summary>S3F35 - Material Movement Event Report Send.</summary>
    internal static SecsItem MovementBody(MaterialMovement movement)
        => SecsItem.L(
            SecsItem.A(movement.MaterialId).Named("MID"),
            SecsItem.U4(movement.Location).Named("LOC"),
            SecsItem.U4(movement.Destination).Named("DESTLOC"));

    /// <summary>Reads S3F35.</summary>
    internal static bool TryReadMovement(SecsItem? body, out MaterialMovement? movement)
    {
        movement = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        movement = new MaterialMovement
        {
            MaterialId = Text(body.Items[0]),
            Location = UInt32(body.Items[1]),
            Destination = UInt32(body.Items[2]),
        };

        return true;
    }

    /// <summary>S3F15 - Materials Multi-block Inquire. One bare <c>DATAID</c>.</summary>
    /// <summary>S4F1, F3, F5, F7, F9, F11, F13, F15 and F17 - all nine carry the same two items.</summary>
    /// <param name="portId">Which port - <c>PTN</c>.</param>
    /// <param name="materialId">Which piece of material - <c>MID</c>.</param>
    /// <returns>The body.</returns>
    /// <remarks>
    /// <para>
    /// Nothing inferred: <c>PTN</c> is a <c>U1[1]</c> and <c>MID</c> an <c>A[n]</c> in the
    /// dictionary, and every item name in all 27 rows of stream 4 is defined - which is true of no
    /// other stream this package or its siblings touch. What the source does not mark as a
    /// definition is any of the rows, exactly as for stream 3, so the <em>shape</em> is read off
    /// the item list: two items in the order the catalogue lists them.
    /// </para>
    /// <para>
    /// A port here is written as a port and not as a zero-length item for "no port", unlike S3F17
    /// in the carrier half: every one of these nine messages is <em>about</em> a port, so there is
    /// no absent one to express.
    /// </para>
    /// </remarks>
    internal static SecsItem HandoffBody(byte portId, string materialId)
        => SecsItem.L(
            SecsItem.U1(portId).Named("PTN"),
            SecsItem.A(materialId).Named("MID"));

    /// <summary>Reads any of the nine handoff primaries.</summary>
    /// <param name="body">The body.</param>
    /// <param name="portId">Which port it is about.</param>
    /// <param name="materialId">Which piece of material, or empty.</param>
    /// <returns><see langword="true"/> when the body was one this reader understands.</returns>
    /// <remarks>
    /// A bare <c>PTN</c> with no material reads as a port and an empty identifier, because a
    /// handoff step is about a port first: a machine that names the port and leaves the material
    /// out has still said which interlock it means, and dropping the message would stall a
    /// handshake over an item E5 does not say is required.
    /// </remarks>
    internal static bool TryReadHandoff(SecsItem? body, out byte portId, out string materialId)
    {
        portId = 0;
        materialId = string.Empty;

        if (body is null)
        {
            return false;
        }

        if (!body.IsList)
        {
            if (!TryReadUnsigned(body, out var bare))
            {
                return false;
            }

            portId = (byte)Math.Min(bare, byte.MaxValue);

            return true;
        }

        if (body.Count == 0 || !TryReadUnsigned(body.Items[0], out var port))
        {
            return false;
        }

        portId = (byte)Math.Min(port, byte.MaxValue);

        if (body.Count > 1)
        {
            materialId = Text(body.Items[1]);
        }

        return true;
    }

    internal static SecsItem DataIdBody(uint dataId) => SecsItem.U4(dataId).Named("DATAID");

    /// <summary>Reads a bare <c>DATAID</c>.</summary>
    internal static uint? TryReadDataId(SecsItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? TryReadDataId(item.Items[0]) : null;
        }

        return TryReadUnsigned(item, out var value) ? (uint)Math.Min(value, uint.MaxValue) : null;
    }

    // --------------------------------------------------------- acknowledgement codes

    /// <summary>Writes a one-item acknowledgement body - <c>ACKC3</c> or <c>GRANT</c>.</summary>
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
    /// entry, which is a machine with one piece of material not bothering to wrap it; and a flat
    /// run of fields, which is a machine that wrapped nothing at all. An entry short of
    /// <paramref name="width"/> fields is dropped rather than padded - a status with no location is
    /// not a status, and inventing a location of zero would put material on a load port.
    /// </remarks>
    private static List<SecsItem[]> Entries(SecsItem body, int width)
    {
        List<SecsItem[]> entries = [];

        if (body.Count == 0)
        {
            return entries;
        }

        // A list of lists: the normal shape.
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

        // Bare fields: one entry, or several laid end to end.
        for (var i = 0; i + width <= body.Count; i += width)
        {
            entries.Add([.. body.Items[i..(i + width)]]);
        }

        return entries;
    }

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    private static byte Byte(SecsItem item) => (byte)Math.Min(Unsigned(item), byte.MaxValue);

    private static uint UInt32(SecsItem item) => (uint)Math.Min(Unsigned(item), uint.MaxValue);

    private static ulong Unsigned(SecsItem item) => TryReadUnsigned(item, out var value) ? value : 0;

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// Every number in this stream is a location, a quantity, a count of time or a code, so a
    /// value too large for its field is clamped rather than refused: a machine that sends a
    /// location of four billion has a bug, and dropping the message it came in would hide which
    /// material the bug is about.
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
            case SecsFormat.ASCII:
            case SecsFormat.JIS8:
                return ulong.TryParse(item.GetString().Trim(), CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }

        static bool NonNegative(long candidate, out ulong value)
        {
            value = candidate < 0 ? 0 : (ulong)candidate;
            return candidate >= 0;
        }
    }

    // ----------------------------------------------------------------- the handoff protocol

    /// <summary>Writes S4F27's body: the nine items that describe a link.</summary>
    /// <remarks>
    /// Positional, like every other body in this stream, because the reference gives the row an
    /// item list and no names on the wire. <c>TRROLE</c> goes out as the table's own word rather
    /// than as a number - it is an <c>A</c>, and SOURCE and DESTINATION are what the table says.
    /// </remarks>
    internal static SecsItem HandoffLinkBody(MaterialHandoffLink link) => SecsItem.L(
        SecsItem.A(link.EquipmentName).Named("EQNAME"),
        SecsItem.A(link.Link).Named("TRLINK"),
        SecsItem.A(link.ObjectName).Named("TROBJNAME"),
        SecsItem.A(link.ObjectType).Named("TROBJTYPE"),
        SecsItem.A(link.Port).Named("TRPORT"),
        SecsItem.A(RoleWord(link.Role)).Named("TRROLE"),
        SecsItem.A(link.PartnerName).Named("TRPTNR"),
        SecsItem.A(link.PartnerPort).Named("TRPTPORT"),
        SecsItem.A(link.Location).Named("TRLOCATION"));

    /// <summary>Reads S4F27's body.</summary>
    /// <param name="body">The item.</param>
    /// <param name="link">The link.</param>
    /// <returns><see langword="false"/> when the body is not nine items, or names no link.</returns>
    /// <remarks>
    /// A link with no <c>TRLINK</c> is refused rather than kept under an empty name: every later
    /// message in the protocol addresses a link by that string, so one without it could never be
    /// acted on and keeping it would put an unusable entry in an application's table.
    /// </remarks>
    internal static bool TryReadHandoffLink(SecsItem? body, out MaterialHandoffLink? link)
    {
        link = null;

        if (body is not { IsList: true, Count: 9 })
        {
            return false;
        }

        var name = Text(body[1]);

        if (name.Length == 0)
        {
            return false;
        }

        link = new MaterialHandoffLink
        {
            Link = name,
            EquipmentName = Text(body[0]),
            ObjectName = Text(body[2]),
            ObjectType = Text(body[3]),
            Port = Text(body[4]),
            Role = ReadRole(Text(body[5])),
            PartnerName = Text(body[6]),
            PartnerPort = Text(body[7]),
            Location = Text(body[8]),
        };

        return true;
    }

    /// <summary>Writes S4F29's body: a link, an index, a command name and its parameters.</summary>
    internal static SecsItem HandoffCommandBody(
        string link,
        uint index,
        string command,
        IReadOnlyList<(string Name, string Value)> parameters)
    {
        var pairs = new SecsItem[parameters.Count];

        for (var i = 0; i < parameters.Count; i++)
        {
            pairs[i] = SecsItem.L(
                SecsItem.A(parameters[i].Name).Named("CPNAME"),
                SecsItem.A(parameters[i].Value).Named("CPVAL"));
        }

        return SecsItem.L(
            SecsItem.A(link).Named("TRLINK"),
            SecsItem.U4(index).Named("MCINDEX"),
            SecsItem.A(command).Named("HOCMDNAME"),
            SecsItem.L(pairs));
    }

    /// <summary>Writes the body of S4F31: a link, an index and an acknowledgement.</summary>
    internal static SecsItem HandoffCompleteBody(string link, uint index, byte code) => SecsItem.L(
        SecsItem.A(link).Named("TRLINK"),
        SecsItem.U4(index).Named("MCINDEX"),
        SecsItem.B(code).Named("HOACK"));

    /// <summary>Writes the body of S4F33: a link, an acknowledgement and a reason.</summary>
    internal static SecsItem HandoffVerifiedBody(string link, byte code, uint errorCode, string errorText)
        => SecsItem.L(
            SecsItem.A(link).Named("TRLINK"),
            SecsItem.B(code).Named("HOACK"),
            SecsItem.U4(errorCode).Named("ERRCODE"),
            SecsItem.A(errorText).Named("ERRTEXT"));

    /// <summary>Writes the body of S4F35 and S4F39: a link alone.</summary>
    /// <remarks>
    /// A one-item list rather than the bare <c>TRLINK</c>, which is a choice and worth saying: the
    /// reference gives these rows an item list of one, and every other row in the protocol is a
    /// list. A reader here accepts either, because a machine sending the bare item is doing the
    /// sensible thing with an ambiguous row.
    /// </remarks>
    internal static SecsItem HandoffLinkOnlyBody(string link)
        => SecsItem.L(SecsItem.A(link).Named("TRLINK"));

    /// <summary>Writes the body of S4F37 and S4F41: a link and an acknowledgement.</summary>
    internal static SecsItem HandoffAnswerBody(string link, byte code, string item) => SecsItem.L(
        SecsItem.A(link).Named("TRLINK"),
        SecsItem.B(code).Named(item));

    /// <summary>Reads whichever of the seven per-link bodies arrived.</summary>
    /// <param name="notice">Which row it is, which decides the shape expected.</param>
    /// <param name="body">The item.</param>
    /// <param name="link">The link name.</param>
    /// <param name="index">MCINDEX, or zero.</param>
    /// <param name="command">HOCMDNAME, or empty.</param>
    /// <param name="parameters">The CPNAME and CPVAL pairs, or empty.</param>
    /// <param name="code">The acknowledgement byte, or zero.</param>
    /// <param name="errorCode">ERRCODE, or zero.</param>
    /// <param name="errorText">ERRTEXT, or empty.</param>
    /// <returns><see langword="false"/> when the body names no link.</returns>
    /// <remarks>
    /// One reader for four shapes, switching on the function the way the writers are switched by
    /// the caller. The link is the only field whose absence is fatal: a message addressed to no
    /// link cannot be acted on, where a missing index or a missing reason leaves a step that still
    /// says which link it is about and which step it is.
    /// </remarks>
    internal static bool TryReadHandoffNotice(
        MaterialHandoffNotice notice,
        SecsItem? body,
        out string link,
        out uint index,
        out string command,
        out List<(string Name, string Value)> parameters,
        out byte code,
        out uint errorCode,
        out string errorText)
    {
        link = string.Empty;
        index = 0;
        command = string.Empty;
        parameters = [];
        code = 0;
        errorCode = 0;
        errorText = string.Empty;

        if (body is null)
        {
            return false;
        }

        // S4F35 and S4F39 carry one item, so a machine that sent the bare TRLINK rather than a
        // list of one is read rather than refused: the row is a list of one and the two encodings
        // are indistinguishable in intent.
        var items = body.IsList ? body : SecsItem.L(body);

        if (items.Count == 0)
        {
            return false;
        }

        link = Text(items[0]);

        if (link.Length == 0)
        {
            return false;
        }

        switch (notice)
        {
            case MaterialHandoffNotice.Command when items.Count >= 3:
                index = UInt32(items[1]);
                command = Text(items[2]);

                if (items.Count >= 4 && items[3].IsList)
                {
                    foreach (var pair in items[3].Items)
                    {
                        if (pair is { IsList: true, Count: 2 })
                        {
                            parameters.Add((Text(pair[0]), Text(pair[1])));
                        }
                    }
                }

                break;

            case MaterialHandoffNotice.CommandComplete when items.Count >= 3:
                index = UInt32(items[1]);
                code = ReadCode(items[2]) ?? 0;
                break;

            case MaterialHandoffNotice.Verified when items.Count >= 2:
                code = ReadCode(items[1]) ?? 0;
                errorCode = items.Count >= 3 ? UInt32(items[2]) : 0;
                errorText = items.Count >= 4 ? Text(items[3]) : string.Empty;
                break;

            case MaterialHandoffNotice.CancelReadyAcknowledge or MaterialHandoffNotice.HaltAcknowledge
                when items.Count >= 2:
                code = ReadCode(items[1]) ?? 0;
                break;

            default:
                break;
        }

        return true;
    }

    /// <summary>The word <c>TRROLE</c> carries for a role.</summary>
    /// <remarks>
    /// <see cref="MaterialHandoffRole.Other"/> goes out as an empty string rather than as the word
    /// "Other", which no table names: a role this end cannot name is a role it does not state.
    /// </remarks>
    private static string RoleWord(MaterialHandoffRole role) => role switch
    {
        MaterialHandoffRole.Source => "SOURCE",
        MaterialHandoffRole.Destination => "DESTINATION",
        _ => string.Empty,
    };

    /// <summary>Reads <c>TRROLE</c>, ordinal and case-sensitive.</summary>
    /// <remarks>
    /// Ordinal because the table's words are identifiers on a wire, which is the comparison this
    /// repository uses everywhere but for a stream's prose name. A machine sending "source" in
    /// lower case is not sending the word the table names, and reading it anyway would be deciding
    /// which side is holding the material on the strength of a guess.
    /// </remarks>
    private static MaterialHandoffRole ReadRole(string word) => word switch
    {
        "SOURCE" => MaterialHandoffRole.Source,
        "DESTINATION" => MaterialHandoffRole.Destination,
        _ => MaterialHandoffRole.Other,
    };
}
