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
}
