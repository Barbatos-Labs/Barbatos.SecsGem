// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.DataSets;

/// <summary>
/// Every stream 13 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// Both ends go through this class, so a change to a body is a change to both ends or it does not
/// compile.
/// </para>
/// <para>
/// <strong>What the provenance is.</strong> The catalogue shipped in this repository marks four of
/// stream 13's sixteen rows as definitions and the other twelve as a representative subset, which
/// is the firmest showing of any stream nothing here had implemented. Of the seven item names the
/// stream uses, five have dictionary entries and they cover every field written below except one:
/// </para>
/// <code>
/// DSID     U4   defined
/// DATAID   U4   defined, and already served as a U4 by three packages in this repository
/// LENGTH   U4   defined
/// GRANT6   B[1] defined, with its three codes and a reserved range
/// ACKC13   B[1] defined, with its four codes and no reserved range
/// DSDATA   --   defined nowhere, so it is never written as a format this library chose
/// </code>
/// <para>
/// <c>DSDATA</c> is carried through untouched: whatever item a host sends arrives at the store as
/// the bytes that encoded it, and whatever a store hands back goes out as the item those bytes
/// decode to. That is the one honest reading of an undefined payload - see
/// <see cref="DataSetPayload"/>.
/// </para>
/// <para>
/// <c>BLKNUM</c>, the seventh name, belongs to S13F9 and S13F10 and this package does not
/// implement them. Every one of the catalogue's seven block-transfer items is undefined -
/// <c>BLKNUM</c>, <c>BLKSIZE</c>, <c>BLKACK</c>, <c>BLKEND</c>, <c>CHECKSUM</c>, <c>CHKSUM</c>
/// and <c>CKSUM</c> - so that is a vocabulary the source never wrote down rather than one row it
/// missed.
/// </para>
/// <para>
/// <strong>Nesting is the one thing a flat item list cannot pin down</strong>, which is why the
/// readers are more generous than the writers. Each body is written one way and read in every
/// shape a machine might plausibly send it in.
/// </para>
/// </remarks>
internal static class DataSetCodec
{
    // ------------------------------------------------------------------- opening

    /// <summary>S13F1 - <c>L,2 { DSID, LENGTH }</c>.</summary>
    internal static SecsItem OpenSendBody(uint dataSetId, uint length)
        => SecsItem.L(
            SecsItem.U4(dataSetId).Named("DSID"),
            SecsItem.U4(length).Named("LENGTH"));

    /// <summary>Reads S13F1. A bare <c>DSID</c> with no length reads as a length of zero.</summary>
    internal static bool TryReadOpenSend(SecsItem? body, out uint dataSetId, out uint length)
    {
        dataSetId = 0;
        length = 0;

        if (body is null)
        {
            return false;
        }

        if (!body.IsList)
        {
            // A machine that declared no length. E5 asks for one, and refusing the whole open over
            // it would lose the identifier too - which is the only part this end can act on.
            return TryReadUnsigned(body, out var bare) && Set(bare, out dataSetId);
        }

        if (body.Count == 0)
        {
            return false;
        }

        if (!TryReadUnsigned(body.Items[0], out var id))
        {
            return false;
        }

        dataSetId = (uint)Math.Min(id, uint.MaxValue);

        if (body.Count > 1 && TryReadUnsigned(body.Items[1], out var declared))
        {
            length = (uint)Math.Min(declared, uint.MaxValue);
        }

        return true;

        static bool Set(ulong value, out uint target)
        {
            target = (uint)Math.Min(value, uint.MaxValue);
            return true;
        }
    }

    /// <summary>S13F6 - <c>L,2 { GRANT6, LENGTH }</c>.</summary>
    /// <remarks>
    /// S13F2 is the grant on its own, which is why there are two writers here and not one: on a
    /// send the host already said how long the data set is, and on a read only the equipment
    /// knows.
    /// </remarks>
    internal static SecsItem OpeningBody(byte grant, uint length)
        => SecsItem.L(
            SecsItem.B(grant).Named("GRANT6"),
            SecsItem.U4(length).Named("LENGTH"));

    /// <summary>Reads S13F2 or S13F6: a grant, with a length beside it or without one.</summary>
    internal static bool TryReadOpening(SecsItem? body, out byte grant, out uint length)
    {
        grant = 0;
        length = 0;

        if (body is null)
        {
            return false;
        }

        if (!body.IsList)
        {
            // S13F2's shape, and S13F6 from a machine that sent only the grant.
            if (ReadCode(body) is not { } bare)
            {
                return false;
            }

            grant = bare;
            return true;
        }

        if (body.Count == 0 || ReadCode(body.Items[0]) is not { } code)
        {
            return false;
        }

        grant = code;

        if (body.Count > 1 && TryReadUnsigned(body.Items[1], out var declared))
        {
            length = (uint)Math.Min(declared, uint.MaxValue);
        }

        return true;
    }

    // ----------------------------------------------------------------- the data

    /// <summary>S13F3 and S13F8 - <c>L,3 { DSID, DATAID, DSDATA }</c>.</summary>
    /// <param name="dataSetId"><c>DSID</c>.</param>
    /// <param name="dataId"><c>DATAID</c>.</param>
    /// <param name="data">
    /// <c>DSDATA</c>, which goes out exactly as it arrived here. <see langword="null"/> writes a
    /// zero-length list, which is the shape E5 uses for "nothing" everywhere else in this stack
    /// and the only one available when there is no format to be empty in.
    /// </param>
    internal static SecsItem ContentBody(uint dataSetId, uint dataId, SecsItem? data)
        => SecsItem.L(
            SecsItem.U4(dataSetId).Named("DSID"),
            SecsItem.U4(dataId).Named("DATAID"),
            (data ?? SecsItem.L()).Named("DSDATA"));

    /// <summary>Reads S13F3 or S13F8.</summary>
    /// <remarks>
    /// Exactly three items, and deliberately no tolerance for two: the second of two could be the
    /// correlation number or it could be the data, and <c>DSDATA</c> has no format to tell them
    /// apart by. Guessing would mean storing a host's correlation number as somebody's data set.
    /// </remarks>
    internal static bool TryReadContent(
        SecsItem? body,
        out uint dataSetId,
        out uint dataId,
        out DataSetPayload? data)
    {
        dataSetId = 0;
        dataId = 0;
        data = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        if (!TryReadUnsigned(body.Items[0], out var id) || !TryReadUnsigned(body.Items[1], out var correlation))
        {
            return false;
        }

        dataSetId = (uint)Math.Min(id, uint.MaxValue);
        dataId = (uint)Math.Min(correlation, uint.MaxValue);
        data = DataSetPayload.From(body.Items[2]);

        return true;
    }

    /// <summary>S13F7 - <c>L,2 { DSID, DATAID }</c>.</summary>
    internal static SecsItem RequestBody(uint dataSetId, uint dataId)
        => SecsItem.L(
            SecsItem.U4(dataSetId).Named("DSID"),
            SecsItem.U4(dataId).Named("DATAID"));

    /// <summary>Reads S13F7. A bare <c>DSID</c> reads as a correlation number of zero.</summary>
    internal static bool TryReadRequest(SecsItem? body, out uint dataSetId, out uint dataId)
    {
        dataSetId = 0;
        dataId = 0;

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

            dataSetId = (uint)Math.Min(bare, uint.MaxValue);
            return true;
        }

        if (body.Count == 0 || !TryReadUnsigned(body.Items[0], out var id))
        {
            return false;
        }

        dataSetId = (uint)Math.Min(id, uint.MaxValue);

        if (body.Count > 1 && TryReadUnsigned(body.Items[1], out var correlation))
        {
            dataId = (uint)Math.Min(correlation, uint.MaxValue);
        }

        return true;
    }

    // ------------------------------------------------------- identifiers and lists

    /// <summary>S13F5, S13F13 and S13F15 - a bare <c>DSID</c>.</summary>
    internal static SecsItem DataSetIdBody(uint dataSetId) => SecsItem.U4(dataSetId).Named("DSID");

    /// <summary>Reads a bare <c>DSID</c>, descending into a list of one if that is what arrived.</summary>
    internal static bool TryReadDataSetId(SecsItem? item, out uint dataSetId)
    {
        dataSetId = 0;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadDataSetId(item.Items[0], out dataSetId);
        }

        if (!TryReadUnsigned(item, out var value))
        {
            return false;
        }

        dataSetId = (uint)Math.Min(value, uint.MaxValue);
        return true;
    }

    /// <summary>S13F12 - <c>L,n { DSID }</c>, one <c>U4</c> item per data set.</summary>
    /// <remarks>
    /// Written as a list of items rather than as one <c>U4</c> of n values, because every other
    /// repeating body in this stack is a list and a host reading two shapes from one library is
    /// worse off than a host reading one. The reader takes both.
    /// </remarks>
    internal static SecsItem DirectoryBody(IReadOnlyList<uint>? dataSetIds)
    {
        if (dataSetIds is null || dataSetIds.Count == 0)
        {
            return SecsItem.L();
        }

        var items = new SecsItem[dataSetIds.Count];

        for (var i = 0; i < dataSetIds.Count; i++)
        {
            items[i] = SecsItem.U4(dataSetIds[i]).Named("DSID");
        }

        return SecsItem.L(items);
    }

    /// <summary>Reads S13F12, nested or flat.</summary>
    /// <remarks>
    /// Three shapes: a list of one-value items, which is what this library writes; one numeric item
    /// carrying every identifier at once, which is the shape a flat item list cannot rule out; and
    /// a zero-length list, which is a machine with no data sets. An entry that is not a number is
    /// skipped rather than read as zero - zero is a data set identifier.
    /// </remarks>
    internal static List<uint> ReadDirectory(SecsItem? body)
    {
        List<uint> dataSetIds = [];

        if (body is null || body.Count == 0)
        {
            return dataSetIds;
        }

        if (!body.IsList)
        {
            AddValues(body, dataSetIds);
            return dataSetIds;
        }

        foreach (var entry in body.Items)
        {
            if (entry.IsList)
            {
                // A machine that wrapped each identifier in a list of its own.
                if (entry.Count == 1 && TryReadDataSetId(entry.Items[0], out var wrapped))
                {
                    dataSetIds.Add(wrapped);
                }

                continue;
            }

            AddValues(entry, dataSetIds);
        }

        return dataSetIds;

        static void AddValues(SecsItem item, List<uint> into)
        {
            if (item.Count == 0)
            {
                return;
            }

            if (item.Count == 1)
            {
                if (TryReadUnsigned(item, out var single))
                {
                    into.Add((uint)Math.Min(single, uint.MaxValue));
                }

                return;
            }

            if (item.TryGetSpan<uint>(out var values))
            {
                foreach (var value in values)
                {
                    into.Add(value);
                }
            }
        }
    }

    // ---------------------------------------------------------------- the status

    /// <summary>S13F16 - <c>L,3 { DSID, LENGTH, ACKC13 }</c>.</summary>
    internal static SecsItem StatusBody(uint dataSetId, uint length, byte acknowledgement)
        => SecsItem.L(
            SecsItem.U4(dataSetId).Named("DSID"),
            SecsItem.U4(length).Named("LENGTH"),
            SecsItem.B(acknowledgement).Named("ACKC13"));

    /// <summary>Reads S13F16.</summary>
    internal static bool TryReadStatus(
        SecsItem? body,
        out uint dataSetId,
        out uint length,
        out byte acknowledgement)
    {
        dataSetId = 0;
        length = 0;
        acknowledgement = 0;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        if (!TryReadUnsigned(body.Items[0], out var id)
            || !TryReadUnsigned(body.Items[1], out var declared)
            || ReadCode(body.Items[2]) is not { } code)
        {
            return false;
        }

        dataSetId = (uint)Math.Min(id, uint.MaxValue);
        length = (uint)Math.Min(declared, uint.MaxValue);
        acknowledgement = code;

        return true;
    }

    // --------------------------------------------------------- acknowledgement codes

    /// <summary>Writes a one-item acknowledgement body - <c>ACKC13</c> or <c>GRANT6</c>.</summary>
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

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// Every number in this stream is an identifier, a correlation number or a length, so a value
    /// too large for its field is clamped rather than refused - the same rule stream 3 keeps one
    /// package over, and for the same reason: dropping the message would hide which data set the
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
