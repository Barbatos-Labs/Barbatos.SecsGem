// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.WaferMap;

/// <summary>
/// Every stream 12 body, written and read in one place.
/// </summary>
/// <remarks>
/// <para>
/// The two ends of a wafer map conversation read and write the same nineteen bodies from opposite
/// directions, and the only way to be sure they agree is for there to be one copy of each. Both
/// <see cref="WaferMapEquipment"/> and <see cref="WaferMapHost"/> go through this class, so a
/// change to a body is a change to both ends or it does not compile.
/// </para>
/// <para>
/// <strong>What is firm and what is inferred.</strong> The item lists come from the catalogue
/// shipped in this repository, which flags S12F1 and S12F19 as a representative subset and the
/// other seventeen functions as definitions. What a flat item list cannot express is nesting, and
/// four bodies need it: S12F7, S12F11, S12F14 and S12F18 each carry a repeating pair - a row
/// descriptor and its bins, or a coordinate and its bins - and a body with one <c>RSINF</c> beside
/// one <c>BINLT</c> could describe exactly one row, which is not a map. So those four are written
/// as a list of two-element entries:
/// </para>
/// <code>
/// S12F7   L,3 { MID, IDTYP, L,n { L,2 { RSINF, BINLT } } }      map data type 1
/// S12F9   L,4 { MID, IDTYP, STRP, BINLT }                       map data type 2
/// S12F11  L,3 { MID, IDTYP, L,n { L,2 { XYPOS, BINLT } } }      map data type 3
/// S12F14  as S12F7      S12F16  as S12F9      S12F18  as S12F11
/// </code>
/// <para>
/// The readers do not insist on it. A body whose third element is a bare <c>RSINF</c> followed by a
/// bare <c>BINLT</c> - the flat shape, one row - reads as a map of one row rather than as a format
/// error, because a machine that sends it is not wrong about anything that matters. If a vendor
/// turns out to nest differently again, this file is the only one to change.
/// </para>
/// <para>
/// <strong>Formats are read tolerantly and written exactly.</strong> Writing follows the
/// catalogue - <c>MID</c> as <c>A</c>, <c>ROWCT</c> as <c>U2</c>, <c>BINLT</c> as <c>B</c>, and so
/// on - because a receiver is entitled to the format the standard names. Reading accepts any
/// numeric format for a numeric field and either <c>B</c> or <c>U1</c> for bins, because a map
/// refused over <c>U2</c> where <c>U4</c> was expected is a wafer nobody can probe.
/// </para>
/// </remarks>
internal static class WaferMapCodec
{
    // ---------------------------------------------------------------- setup data

    /// <summary>S12F1 - Map Setup Data Send.</summary>
    internal static SecsItem SetupSendBody(WaferMapSetup setup)
        => SecsItem.L(
            SecsItem.A(setup.MaterialId).Named("MID"),
            SecsItem.U1((byte)setup.IdType).Named("IDTYP"),
            SecsItem.U1(setup.FirstLocation).Named("FNLOC"),
            SecsItem.U2(setup.FlatRotation ?? 0).Named("FFROT"),
            SecsItem.U1(setup.OriginLocation).Named("ORLOC"),
            SecsItem.U1(setup.ReportPointSelection).Named("RPSEL"),
            ReferencePoints(setup.ReferencePoints),
            SecsItem.A(setup.DieUnits).Named("DUTMS"),
            SecsItem.U2(setup.DieSizeX).Named("XDIES"),
            SecsItem.U2(setup.DieSizeY).Named("YDIES"),
            SecsItem.U2(setup.RowCount).Named("ROWCT"),
            SecsItem.U4(setup.ColumnCount).Named("COLCT"),
            SecsItem.A(setup.NullBinCode.ToString()).Named("NULBC"),
            SecsItem.A(setup.ProductId).Named("PRDCT"),
            SecsItem.U1(setup.ProcessAxis ?? 0).Named("PRAXI"));

    /// <summary>Reads S12F1.</summary>
    internal static bool TryReadSetupSend(SecsItem? body, out WaferMapSetup? setup)
    {
        setup = null;

        if (body is null || !body.IsList || body.Count < 15)
        {
            return false;
        }

        var items = body.Items;

        setup = new WaferMapSetup
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            FirstLocation = Byte(items[2]),
            FlatRotation = UInt16(items[3]),
            OriginLocation = Byte(items[4]),
            ReportPointSelection = Byte(items[5]),
            ReferencePoints = ReadReferencePoints(items[6]),
            DieUnits = Text(items[7]),
            DieSizeX = UInt16(items[8]),
            DieSizeY = UInt16(items[9]),
            RowCount = UInt16(items[10]),
            ColumnCount = UInt32(items[11]),
            NullBinCode = Character(items[12]),
            ProductId = Text(items[13]),
            ProcessAxis = Byte(items[14]),
        };

        return true;
    }

    /// <summary>S12F3 - Map Setup Data Request.</summary>
    internal static SecsItem SetupRequestBody(WaferMapSetupRequest request)
        => SecsItem.L(
            SecsItem.A(request.MaterialId).Named("MID"),
            SecsItem.U1((byte)request.IdType).Named("IDTYP"),
            SecsItem.A(request.MapFormat).Named("MAPFT"),
            SecsItem.U1(request.FirstLocation).Named("FNLOC"),
            SecsItem.U2(request.FlatRotation).Named("FFROT"),
            SecsItem.U1(request.OriginLocation).Named("ORLOC"),
            SecsItem.U1(request.ProcessAxis).Named("PRAXI"),
            SecsItem.B(request.BinCodeEquivalents.ToArray()).Named("BCEQU"),
            SecsItem.A(request.NullBinCode.ToString()).Named("NULBC"));

    /// <summary>Reads S12F3.</summary>
    internal static bool TryReadSetupRequest(SecsItem? body, out WaferMapSetupRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 9)
        {
            return false;
        }

        var items = body.Items;

        request = new WaferMapSetupRequest
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            MapFormat = Text(items[2]),
            FirstLocation = Byte(items[3]),
            FlatRotation = UInt16(items[4]),
            OriginLocation = Byte(items[5]),
            ProcessAxis = Byte(items[6]),
            BinCodeEquivalents = Bins(items[7]),
            NullBinCode = Character(items[8]),
        };

        return true;
    }

    /// <summary>S12F4 - Map Setup Data. A zero-length list is E5 saying there is no map.</summary>
    internal static SecsItem SetupBody(WaferMapSetup? setup)
        => setup is null
            ? SecsItem.L()
            : SecsItem.L(
                SecsItem.A(setup.MaterialId).Named("MID"),
                SecsItem.U1((byte)setup.IdType).Named("IDTYP"),
                SecsItem.U1(setup.FirstLocation).Named("FNLOC"),
                SecsItem.U1(setup.OriginLocation).Named("ORLOC"),
                SecsItem.U1(setup.ReportPointSelection).Named("RPSEL"),
                ReferencePoints(setup.ReferencePoints),
                SecsItem.A(setup.DieUnits).Named("DUTMS"),
                SecsItem.U2(setup.DieSizeX).Named("XDIES"),
                SecsItem.U2(setup.DieSizeY).Named("YDIES"),
                SecsItem.U2(setup.RowCount).Named("ROWCT"),
                SecsItem.U4(setup.ColumnCount).Named("COLCT"),
                SecsItem.A(setup.ProductId).Named("PRDCT"),
                SecsItem.B(setup.BinCodeEquivalents.ToArray()).Named("BCEQU"),
                SecsItem.A(setup.NullBinCode.ToString()).Named("NULBC"),
                SecsItem.U4(setup.MapLocationCount ?? 0).Named("MLCL"));

    /// <summary>Reads S12F4. <see langword="true"/> with a <see langword="null"/> setup is "no map for that material".</summary>
    internal static bool TryReadSetup(SecsItem? body, out WaferMapSetup? setup)
    {
        setup = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 15)
        {
            return false;
        }

        var items = body.Items;

        setup = new WaferMapSetup
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            FirstLocation = Byte(items[2]),
            OriginLocation = Byte(items[3]),
            ReportPointSelection = Byte(items[4]),
            ReferencePoints = ReadReferencePoints(items[5]),
            DieUnits = Text(items[6]),
            DieSizeX = UInt16(items[7]),
            DieSizeY = UInt16(items[8]),
            RowCount = UInt16(items[9]),
            ColumnCount = UInt32(items[10]),
            ProductId = Text(items[11]),
            BinCodeEquivalents = Bins(items[12]),
            NullBinCode = Character(items[13]),
            MapLocationCount = UInt32(items[14]),
        };

        return true;
    }

    // ------------------------------------------------------------ the handshake

    /// <summary>S12F5 - Map Transmit Inquire.</summary>
    internal static SecsItem TransmitInquiryBody(WaferMapTransmitInquiry inquiry)
        => SecsItem.L(
            SecsItem.A(inquiry.MaterialId).Named("MID"),
            SecsItem.U1((byte)inquiry.IdType).Named("IDTYP"),
            SecsItem.A(inquiry.MapFormat).Named("MAPFT"),
            SecsItem.U4(inquiry.MapLocationCount).Named("MLCL"));

    /// <summary>Reads S12F5.</summary>
    internal static bool TryReadTransmitInquiry(SecsItem? body, out WaferMapTransmitInquiry? inquiry)
    {
        inquiry = null;

        if (body is null || !body.IsList || body.Count < 4)
        {
            return false;
        }

        var items = body.Items;

        inquiry = new WaferMapTransmitInquiry
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            MapFormat = Text(items[2]),
            MapLocationCount = UInt32(items[3]),
        };

        return true;
    }

    // ------------------------------------------------------------- map data out

    /// <summary>S12F7 and S12F14 - map data type 1.</summary>
    internal static SecsItem RowDataBody(WaferMapRowData? data)
    {
        if (data is null)
        {
            return SecsItem.L();
        }

        var entries = new SecsItem[data.Rows.Count];

        for (var i = 0; i < data.Rows.Count; i++)
        {
            var row = data.Rows[i];

            entries[i] = SecsItem.L(
                SecsItem.I2(
                    Narrow(row.Row, nameof(WaferMapRow.Row)),
                    Narrow(row.StartColumn, nameof(WaferMapRow.StartColumn)),
                    Narrow(row.Increment, nameof(WaferMapRow.Increment))).Named("RSINF"),
                SecsItem.B(row.Bins.ToArray()).Named("BINLT"));
        }

        return SecsItem.L(
            SecsItem.A(data.MaterialId).Named("MID"),
            SecsItem.U1((byte)data.IdType).Named("IDTYP"),
            SecsItem.L(entries));
    }

    /// <summary>Reads S12F7 and S12F14.</summary>
    internal static bool TryReadRowData(SecsItem? body, out WaferMapRowData? data)
    {
        data = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 3)
        {
            return false;
        }

        var items = body.Items;
        List<WaferMapRow> rows = [];

        foreach (var entry in Entries(items, 2))
        {
            if (!TryReadRow(entry.First, entry.Second, out var row) || row is null)
            {
                return false;
            }

            rows.Add(row);
        }

        data = new WaferMapRowData
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            Rows = rows,
        };

        return true;
    }

    /// <summary>S12F9 and S12F16 - map data type 2.</summary>
    internal static SecsItem ArrayDataBody(WaferMapArrayData? data)
        => data is null
            ? SecsItem.L()
            : SecsItem.L(
                SecsItem.A(data.MaterialId).Named("MID"),
                SecsItem.U1((byte)data.IdType).Named("IDTYP"),
                Coordinate(data.Start, "STRP"),
                SecsItem.B(data.Bins.ToArray()).Named("BINLT"));

    /// <summary>Reads S12F9 and S12F16.</summary>
    internal static bool TryReadArrayData(SecsItem? body, out WaferMapArrayData? data)
    {
        data = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 4 || !TryReadCoordinate(body.Items[2], out var start))
        {
            return false;
        }

        var items = body.Items;

        data = new WaferMapArrayData
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            Start = start,
            Bins = Bins(items[3]),
        };

        return true;
    }

    /// <summary>S12F11 and S12F18 - map data type 3.</summary>
    internal static SecsItem PointDataBody(WaferMapPointData? data)
    {
        if (data is null)
        {
            return SecsItem.L();
        }

        var entries = new SecsItem[data.Points.Count];

        for (var i = 0; i < data.Points.Count; i++)
        {
            var point = data.Points[i];

            entries[i] = SecsItem.L(
                Coordinate(point.Position, "XYPOS"),
                SecsItem.B(point.Bins.ToArray()).Named("BINLT"));
        }

        return SecsItem.L(
            SecsItem.A(data.MaterialId).Named("MID"),
            SecsItem.U1((byte)data.IdType).Named("IDTYP"),
            SecsItem.L(entries));
    }

    /// <summary>Reads S12F11 and S12F18.</summary>
    internal static bool TryReadPointData(SecsItem? body, out WaferMapPointData? data)
    {
        data = null;

        if (body is null || !body.IsList)
        {
            return false;
        }

        if (body.Count == 0)
        {
            return true;
        }

        if (body.Count < 3)
        {
            return false;
        }

        var items = body.Items;
        List<WaferMapPoint> points = [];

        foreach (var entry in Entries(items, 2))
        {
            if (!TryReadCoordinate(entry.First, out var position))
            {
                return false;
            }

            points.Add(new WaferMapPoint { Position = position, Bins = Bins(entry.Second) });
        }

        data = new WaferMapPointData
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),
            Points = points,
        };

        return true;
    }

    // -------------------------------------------------------------- map data in

    /// <summary>S12F13, S12F15 and S12F17 - a request for map data.</summary>
    /// <param name="request">What is wanted.</param>
    /// <param name="withBinSelection">S12F17 carries <c>SDBIN</c>; the other two do not.</param>
    internal static SecsItem DataRequestBody(WaferMapDataRequest request, bool withBinSelection)
    {
        var material = SecsItem.A(request.MaterialId).Named("MID");
        var idType = SecsItem.U1((byte)request.IdType).Named("IDTYP");

        return withBinSelection
            ? SecsItem.L(material, idType, SecsItem.B((byte)request.BinSelection).Named("SDBIN"))
            : SecsItem.L(material, idType);
    }

    /// <summary>Reads S12F13, S12F15 and S12F17.</summary>
    internal static bool TryReadDataRequest(SecsItem? body, out WaferMapDataRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        var items = body.Items;

        request = new WaferMapDataRequest
        {
            MaterialId = Text(items[0]),
            IdType = IdType(items[1]),

            // Absent on S12F13 and S12F15, where the whole map is the only thing a request can
            // mean - and reading the default rather than refusing keeps one reader for all three.
            BinSelection = body.Count > 2 && Byte(items[2]) == (byte)WaferMapBinSelection.NonNullOnly
                ? WaferMapBinSelection.NonNullOnly
                : WaferMapBinSelection.AllCoordinates,
        };

        return true;
    }

    // ------------------------------------------------------------ error reports

    /// <summary>S12F19 - Map Error Report Send.</summary>
    internal static SecsItem ErrorReportBody(WaferMapError error, byte location)
        => SecsItem.L(
            SecsItem.B((byte)error).Named("MAPER"),
            SecsItem.U1(location).Named("DATLC"));

    /// <summary>Reads S12F19.</summary>
    internal static bool TryReadErrorReport(SecsItem? body, out WaferMapError error, out byte location)
    {
        error = WaferMapError.ReceivedCorrectly;
        location = 0;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        var code = Byte(body.Items[0]);

        if (code > (byte)WaferMapError.NoCarrierAtLocation)
        {
            return false;
        }

        error = (WaferMapError)code;
        location = Byte(body.Items[1]);

        return true;
    }

    // ---------------------------------------------------- acknowledgement codes

    /// <summary>Writes a one-item acknowledgement body - <c>SDACK</c>, <c>GRNT1</c> or <c>MDACK</c>.</summary>
    internal static SecsItem CodeBody(byte code, string name) => SecsItem.B(code).Named(name);

    /// <summary>Reads an acknowledgement code, descending into a list of one if that is what arrived.</summary>
    /// <returns>The code, or <see langword="null"/> when there is none to read.</returns>
    /// <remarks>
    /// The descent is not generosity for its own sake. E5 writes these replies as a single item and
    /// most equipment sends one, but a <c>L,1</c> wrapper is common enough in the field that
    /// refusing it would mean reporting a perfectly good acknowledgement as a protocol failure.
    /// </remarks>
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

    // --------------------------------------------------------------- the pieces

    /// <summary>Reads the repeating entries of a map data body, flat or nested.</summary>
    /// <param name="items">The body.</param>
    /// <param name="index">Where the entries start - always 2, after <c>MID</c> and <c>IDTYP</c>.</param>
    /// <returns>The entries, each a descriptor and its bins.</returns>
    /// <remarks>
    /// Three shapes arrive in practice and all three are accepted: a list of two-element lists,
    /// which is what this library writes; a list of pairs flattened into one list; and the two
    /// items left bare in the body, which is one entry. See the remarks on this class for why.
    /// </remarks>
    private static List<(SecsItem First, SecsItem Second)> Entries(ReadOnlySpan<SecsItem> items, int index)
    {
        List<(SecsItem, SecsItem)> entries = [];

        if (!items[index].IsList)
        {
            // Flat: MID, IDTYP, <descriptor>, <bins>.
            if (items.Length > index + 1)
            {
                entries.Add((items[index], items[index + 1]));
            }

            return entries;
        }

        var list = items[index];

        for (var i = 0; i < list.Count; i++)
        {
            var element = list.Items[i];

            if (element.IsList && element.Count >= 2)
            {
                entries.Add((element.Items[0], element.Items[1]));
                continue;
            }

            // A flattened list of pairs: descriptor, bins, descriptor, bins.
            if (!element.IsList && i + 1 < list.Count)
            {
                entries.Add((element, list.Items[i + 1]));
                i++;
            }
        }

        return entries;
    }

    private static bool TryReadRow(SecsItem descriptor, SecsItem bins, out WaferMapRow? row)
    {
        row = null;

        if (descriptor.IsList || descriptor.Count < 3)
        {
            return false;
        }

        Span<int> values = stackalloc int[3];

        if (!TryReadSigned(descriptor, values))
        {
            return false;
        }

        row = new WaferMapRow
        {
            Row = values[0],
            StartColumn = values[1],
            Increment = values[2],
            Bins = Bins(bins),
        };

        return true;
    }

    private static SecsItem Coordinate(WaferMapPosition position, string name)
        => SecsItem.I2(
            Narrow(position.X, nameof(WaferMapPosition.X)),
            Narrow(position.Y, nameof(WaferMapPosition.Y))).Named(name);

    private static bool TryReadCoordinate(SecsItem item, out WaferMapPosition position)
    {
        position = default;

        if (item.IsList || item.Count < 2)
        {
            return false;
        }

        Span<int> values = stackalloc int[2];

        if (!TryReadSigned(item, values))
        {
            return false;
        }

        position = new WaferMapPosition(values[0], values[1]);
        return true;
    }

    private static SecsItem ReferencePoints(IReadOnlyList<WaferMapPosition> points)
    {
        var items = new SecsItem[points.Count];

        for (var i = 0; i < points.Count; i++)
        {
            items[i] = SecsItem.I4(points[i].X, points[i].Y).Named("REFP");
        }

        return SecsItem.L(items);
    }

    private static List<WaferMapPosition> ReadReferencePoints(SecsItem item)
    {
        List<WaferMapPosition> points = [];

        if (item.IsList)
        {
            foreach (var element in item.Items)
            {
                if (TryReadCoordinate(element, out var point))
                {
                    points.Add(point);
                }
            }

            return points;
        }

        // One item holding every ordinate: REFP is I4[2] per point, so 2n values are n points.
        // Written by more than one host and cheap to accept. The buffer is an array rather than a
        // stackalloc because its size comes off the wire, and a sender claiming forty thousand
        // ordinates would otherwise take the stack with it.
        if (item.Count >= 2 && item.Count % 2 == 0)
        {
            var values = new int[item.Count];

            if (TryReadSigned(item, values))
            {
                for (var i = 0; i + 1 < values.Length; i += 2)
                {
                    points.Add(new WaferMapPosition(values[i], values[i + 1]));
                }
            }
        }

        return points;
    }

    /// <summary>Narrows a coordinate to the <c>I2</c> that map data travels as.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value does not fit, which would otherwise wrap a die to the far side of the wafer.</exception>
    private static short Narrow(int value, string name)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(value, short.MaxValue, name);
        ArgumentOutOfRangeException.ThrowIfLessThan(value, short.MinValue, name);

        return (short)value;
    }

    /// <summary>Copies bins out of a pooled item, accepting either format E5 allows for them.</summary>
    /// <remarks>
    /// The copy is the point. An item belongs to its message and a message is returned to the pool
    /// when the transaction ends, so a map that outlives the handler cannot hold the item's memory.
    /// </remarks>
    private static ReadOnlyMemory<byte> Bins(SecsItem item)
        => item.Format is SecsFormat.Binary or SecsFormat.U1 && item.Count > 0
            ? item.GetSpan<byte>().ToArray()
            : ReadOnlyMemory<byte>.Empty;

    private static string Text(SecsItem item)
        => item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;

    /// <summary>Reads <c>NULBC</c>, which E5 carries as one character and some machines as a number.</summary>
    private static char Character(SecsItem item)
    {
        if (item.Format.IsText())
        {
            var text = item.Count > 0 ? item.GetString() : string.Empty;
            return text.Length > 0 ? text[0] : ' ';
        }

        return TryReadUnsigned(item, out var value) && value <= char.MaxValue ? (char)value : ' ';
    }

    private static WaferMapIdType IdType(SecsItem item)
    {
        var code = Byte(item);

        return code <= (byte)WaferMapIdType.WaferIdAndOrientation ? (WaferMapIdType)code : WaferMapIdType.None;
    }

    private static byte Byte(SecsItem item) => (byte)Math.Min(Unsigned(item), byte.MaxValue);

    private static ushort UInt16(SecsItem item) => (ushort)Math.Min(Unsigned(item), ushort.MaxValue);

    private static uint UInt32(SecsItem item) => (uint)Math.Min(Unsigned(item), uint.MaxValue);

    private static ulong Unsigned(SecsItem item) => TryReadUnsigned(item, out var value) ? value : 0;

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// Clamping rather than failing is deliberate for this stream. The fields read this way are
    /// counts and codes - rows, columns, locations, an origin - and a machine that sends a row
    /// count of four billion has a bug a refused map will not help anybody find. The clamp is
    /// visible in the data; a thrown exception in the middle of a map transfer is not.
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

    /// <summary>Reads a signed coordinate field, which unlike a count is meaningful below zero.</summary>
    private static bool TryReadSigned(SecsItem item, Span<int> values)
    {
        if (item.Count < values.Length)
        {
            return false;
        }

        switch (item.Format)
        {
            case SecsFormat.I1:
            {
                var source = item.GetSpan<sbyte>();

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = source[i];
                }

                return true;
            }

            case SecsFormat.I2:
            {
                var source = item.GetSpan<short>();

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = source[i];
                }

                return true;
            }

            case SecsFormat.I4:
            {
                item.GetSpan<int>()[..values.Length].CopyTo(values);
                return true;
            }

            case SecsFormat.Binary:
            case SecsFormat.U1:
            {
                var source = item.GetSpan<byte>();

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = source[i];
                }

                return true;
            }

            case SecsFormat.U2:
            {
                var source = item.GetSpan<ushort>();

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = source[i];
                }

                return true;
            }

            case SecsFormat.U4:
            {
                var source = item.GetSpan<uint>();

                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = (int)Math.Min(source[i], int.MaxValue);
                }

                return true;
            }

            default:
                return false;
        }
    }
}
