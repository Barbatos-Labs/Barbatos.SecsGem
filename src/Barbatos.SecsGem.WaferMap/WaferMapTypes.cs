// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.WaferMap;

/// <summary>What a map's material identifier carries - <c>IDTYP</c>.</summary>
public enum WaferMapIdType : byte
{
    /// <summary>No wafer identifier: the material is known by where it sits, not by a name.</summary>
    None = 0,

    /// <summary>A wafer identifier is present.</summary>
    WaferId = 1,

    /// <summary>A wafer identifier and its orientation are present.</summary>
    WaferIdAndOrientation = 2,
}

/// <summary>The answer to map setup data - <c>SDACK</c>.</summary>
/// <remarks>
/// E5 defines 0 as accepted and <em>every</em> code from 1 to 63 as rejected, with the reason left
/// to the equipment or the host. So there are two members here rather than sixty-four: a number
/// whose meaning is "ask the vendor" is not something an application can branch on. The code that
/// arrived is logged when it is neither 0 nor 1, which is the right channel for a diagnostic
/// nobody can act on programmatically.
/// </remarks>
public enum WaferMapSetupAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Rejected - bad format, no space, or whatever else that end decided.</summary>
    Rejected = 1,
}

/// <summary>The answer to map data - <c>MDACK</c>.</summary>
/// <remarks>Unlike <see cref="WaferMapSetupAcknowledgement"/>, E5 pins this one down: two codes, and these are both of them.</remarks>
public enum WaferMapDataAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Rejected.</summary>
    Rejected = 1,
}

/// <summary>The answer to a map transmit inquire - <c>GRNT1</c>.</summary>
public enum WaferMapTransmitGrant : byte
{
    /// <summary>Send it.</summary>
    Granted = 0,

    /// <summary>Busy; ask again later.</summary>
    Busy = 1,

    /// <summary>Not interested at this time.</summary>
    NotInterested = 2,

    /// <summary>The material identifier or its type was not one that end recognised.</summary>
    InvalidMaterial = 3,

    /// <summary>Some other refusal: E5 reserves 4 to 63 and names none of them.</summary>
    /// <remarks>Every code from 4 upwards reads as this one. It is a refusal either way, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>Which bins a map data request wants - <c>SDBIN</c>.</summary>
public enum WaferMapBinSelection : byte
{
    /// <summary>Every coordinate, null locations included.</summary>
    AllCoordinates = 0,

    /// <summary>Only the locations that hold a die, which on a mostly-empty map is a far smaller message.</summary>
    NonNullOnly = 1,
}

/// <summary>What a map error report says - <c>MAPER</c>.</summary>
/// <remarks>
/// Code 0 says the map arrived correctly, which is why this type is not called an error code.
/// S12F19 expects no reply, so for a map sent with none of the acknowledged functions it is the
/// only confirmation either end gets.
/// </remarks>
public enum WaferMapError : byte
{
    /// <summary>The map was received correctly.</summary>
    ReceivedCorrectly = 0,

    /// <summary>The slot count did not match the map.</summary>
    SlotCountInvalid = 1,

    /// <summary>The body was not the shape that end expected.</summary>
    FormatError = 2,

    /// <summary>There is no carrier at that location.</summary>
    NoCarrierAtLocation = 3,
}

/// <summary>A point on a wafer, in the map's own coordinate system.</summary>
/// <param name="X">The column, or the X ordinate of a reference point.</param>
/// <param name="Y">The row, or the Y ordinate of a reference point.</param>
/// <remarks>
/// <para>
/// A value type with value semantics, because a coordinate is a value: two positions naming the
/// same die are the same position, and the map of a 300 mm wafer at a 1 mm pitch holds some seventy
/// thousand of them.
/// </para>
/// <para>
/// <strong>Range.</strong> <see cref="WaferMapSetup.ReferencePoints"/> travels as <c>I4</c> and
/// takes the full range of <see cref="int"/>. A coordinate inside map data - <c>STRP</c> and
/// <c>XYPOS</c> - travels as <c>I2</c>, so it has to be in [-32768, 32767]; sending one that is not
/// throws <see cref="ArgumentOutOfRangeException"/> rather than quietly wrapping a die position
/// round to the other side of the wafer.
/// </para>
/// </remarks>
public readonly record struct WaferMapPosition(int X, int Y);

/// <summary>A wafer map's setup: how to read the map that follows.</summary>
/// <remarks>
/// <para>
/// One type for the two messages that carry setup data, S12F1 and S12F4, because they are the same
/// information travelling in opposite directions. The two bodies are not identical, and this is
/// the whole of the difference:
/// </para>
/// <code>
/// S12F1  equipment -> host   MID IDTYP FNLOC FFROT ORLOC RPSEL REFP DUTMS XDIES YDIES ROWCT COLCT NULBC PRDCT PRAXI
/// S12F4  host -> equipment   MID IDTYP FNLOC       ORLOC RPSEL REFP DUTMS XDIES YDIES ROWCT COLCT NULBC PRDCT       BCEQU MLCL
/// </code>
/// <para>
/// So <see cref="FlatRotation"/> and <see cref="ProcessAxis"/> are carried only by S12F1, and
/// <see cref="BinCodeEquivalents"/> and <see cref="MapLocationCount"/> only by S12F4. Those four
/// are nullable or empty for that reason: a <see langword="null"/> here means "this message does
/// not carry that field", not "zero".
/// </para>
/// </remarks>
public sealed class WaferMapSetup
{
    /// <summary>The material this map belongs to - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>The first die's location on the wafer - <c>FNLOC</c>.</summary>
    /// <remarks>A code E5 defines and the catalogue shipped in this repository does not enumerate, so it stays a byte.</remarks>
    public byte FirstLocation { get; init; }

    /// <summary>The flat or notch rotation in degrees - <c>FFROT</c>. S12F1 only.</summary>
    public ushort? FlatRotation { get; init; }

    /// <summary>Where the map's origin sits on the wafer - <c>ORLOC</c>.</summary>
    public byte OriginLocation { get; init; }

    /// <summary>Which point of a die the coordinates report - <c>RPSEL</c>.</summary>
    public byte ReportPointSelection { get; init; }

    /// <summary>The reference points that tie the map to the wafer - <c>REFP</c>.</summary>
    public IReadOnlyList<WaferMapPosition> ReferencePoints { get; init; } = [];

    /// <summary>The unit <see cref="DieSizeX"/> and <see cref="DieSizeY"/> are counted in - <c>DUTMS</c>.</summary>
    /// <remarks>Free text, and the standard leaves the vocabulary open: a machine may send <c>um</c>, <c>mm</c> or something of its own.</remarks>
    public string DieUnits { get; init; } = string.Empty;

    /// <summary>The die's width, in <see cref="DieUnits"/> - <c>XDIES</c>.</summary>
    public ushort DieSizeX { get; init; }

    /// <summary>The die's height, in <see cref="DieUnits"/> - <c>YDIES</c>.</summary>
    public ushort DieSizeY { get; init; }

    /// <summary>How many rows the map has - <c>ROWCT</c>.</summary>
    public ushort RowCount { get; init; }

    /// <summary>How many columns the map has - <c>COLCT</c>.</summary>
    public uint ColumnCount { get; init; }

    /// <summary>The bin value that means "no die here" - <c>NULBC</c>.</summary>
    /// <remarks>
    /// One character, which is how E5 carries it, while the bins themselves are bytes. Compare a
    /// bin against <c>(byte)NullBinCode</c>. A space is the default because a text item holding
    /// <c>'\0'</c> is a surprise on the wire, and more than one host reads it as the end of the
    /// string.
    /// </remarks>
    public char NullBinCode { get; init; } = ' ';

    /// <summary>What is being made - <c>PRDCT</c>.</summary>
    public string ProductId { get; init; } = string.Empty;

    /// <summary>The axis the prober works along - <c>PRAXI</c>. S12F1 only.</summary>
    public byte? ProcessAxis { get; init; }

    /// <summary>The bin codes the host considers equivalent - <c>BCEQU</c>. S12F4 only.</summary>
    public ReadOnlyMemory<byte> BinCodeEquivalents { get; init; }

    /// <summary>How many locations the map holds - <c>MLCL</c>. S12F4 only.</summary>
    public uint? MapLocationCount { get; init; }
}

/// <summary>What the equipment asks for when it wants a map - S12F3.</summary>
/// <remarks>
/// A request, not a setup: the equipment is telling the host the conventions it wants the answer
/// in, so the host can lay out the map it is about to send. The answer is a
/// <see cref="WaferMapSetup"/>.
/// </remarks>
public sealed class WaferMapSetupRequest
{
    /// <summary>The material whose map is wanted - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>The map format wanted - <c>MAPFT</c>.</summary>
    /// <remarks>
    /// Free text. E5 gives no code table for it, and equipment in the field commonly sends the map
    /// data type it intends to use - <c>"1"</c>, <c>"2"</c> or <c>"3"</c> - which is why this is a
    /// string rather than an enum over a table that does not exist.
    /// </remarks>
    public string MapFormat { get; init; } = string.Empty;

    /// <summary>The first die's location - <c>FNLOC</c>.</summary>
    public byte FirstLocation { get; init; }

    /// <summary>The flat or notch rotation in degrees - <c>FFROT</c>.</summary>
    public ushort FlatRotation { get; init; }

    /// <summary>Where the origin sits - <c>ORLOC</c>.</summary>
    public byte OriginLocation { get; init; }

    /// <summary>The axis the prober works along - <c>PRAXI</c>.</summary>
    public byte ProcessAxis { get; init; }

    /// <summary>The bin codes this end considers equivalent - <c>BCEQU</c>.</summary>
    public ReadOnlyMemory<byte> BinCodeEquivalents { get; init; }

    /// <summary>The bin value that means "no die here" - <c>NULBC</c>.</summary>
    public char NullBinCode { get; init; } = ' ';
}

/// <summary>What the equipment asks before it sends a map - S12F5.</summary>
/// <remarks>
/// The size is in the inquire on purpose: the map of a quarter of a million locations is a large
/// message, and this is the host's chance to say <see cref="WaferMapTransmitGrant.Busy"/> before it
/// arrives rather than after.
/// </remarks>
public sealed class WaferMapTransmitInquiry
{
    /// <summary>The material whose map is about to be sent - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>The map format - <c>MAPFT</c>.</summary>
    public string MapFormat { get; init; } = string.Empty;

    /// <summary>How many locations the map holds - <c>MLCL</c>.</summary>
    public uint MapLocationCount { get; init; }
}

/// <summary>What the equipment asks for when it wants map data - S12F13, S12F15 and S12F17.</summary>
public sealed class WaferMapDataRequest
{
    /// <summary>The material whose map is wanted - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>Which bins are wanted - <c>SDBIN</c>.</summary>
    /// <remarks>
    /// Only S12F17 carries this, because only coordinate data can leave a location out. A request
    /// for row or array data is a request for the whole map, and this field does not travel with
    /// it.
    /// </remarks>
    public WaferMapBinSelection BinSelection { get; init; }
}

/// <summary>One row of a map - <c>RSINF</c> and its bins.</summary>
/// <remarks>
/// <see cref="StartColumn"/> and <see cref="Increment"/> are what make row data compact: a row
/// probed left to right from column 4 is <c>(row, 4, 1)</c> and a row probed the other way is
/// <c>(row, 4, -1)</c>, so the bins need no coordinates of their own.
/// </remarks>
public sealed class WaferMapRow
{
    /// <summary>Which row this is.</summary>
    public required int Row { get; init; }

    /// <summary>The column the first bin belongs to.</summary>
    public int StartColumn { get; init; }

    /// <summary>How far to step for each bin after the first.</summary>
    /// <remarks>One by default. Zero would be a row whose bins all land on the same die, which is not a row.</remarks>
    public int Increment { get; init; } = 1;

    /// <summary>The bins along the row - <c>BINLT</c>.</summary>
    public ReadOnlyMemory<byte> Bins { get; init; }
}

/// <summary>A map as rows - map data type 1, carried by S12F7 and S12F14.</summary>
public sealed class WaferMapRowData
{
    /// <summary>The material this map belongs to - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>The rows, in the order they were sent.</summary>
    public IReadOnlyList<WaferMapRow> Rows { get; init; } = [];
}

/// <summary>A map as one run of bins - map data type 2, carried by S12F9 and S12F16.</summary>
/// <remarks>
/// The smallest of the three formats and the least forgiving: there is one starting coordinate and
/// then every bin in order, so both ends have to agree already on the row and column counts from
/// the setup data. A map whose bin count does not match <c>ROWCT * COLCT</c> is the usual cause of
/// a <see cref="WaferMapError.SlotCountInvalid"/>.
/// </remarks>
public sealed class WaferMapArrayData
{
    /// <summary>The material this map belongs to - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>Where the first bin sits - <c>STRP</c>.</summary>
    public WaferMapPosition Start { get; init; }

    /// <summary>Every bin, in order from <see cref="Start"/> - <c>BINLT</c>.</summary>
    public ReadOnlyMemory<byte> Bins { get; init; }
}

/// <summary>Bins against a coordinate - <c>XYPOS</c> and <c>BINLT</c>.</summary>
/// <remarks>
/// <para>
/// <see cref="Bins"/> is a run, not a single value: one entry carrying four bins describes the four
/// locations from <see cref="Position"/> onward. A map sent location by location is an entry of one
/// bin each, which is the usual shape and the most verbose one.
/// </para>
/// <para>
/// <strong>Onward means along the row</strong>, X increasing, which is this library reading a
/// standard that does not say. Nothing in the wire format depends on it - a run is written and read
/// back as the same bytes at the same coordinate either way - but anything that has to take a run
/// apart does: <see cref="WaferMapInMemoryStore"/> splitting a partly-null run is the one place in
/// this package that walks a run, and it walks it along X.
/// </para>
/// </remarks>
public sealed class WaferMapPoint
{
    /// <summary>Where the first bin sits - <c>XYPOS</c>.</summary>
    public required WaferMapPosition Position { get; init; }

    /// <summary>The bins from <see cref="Position"/> onward - <c>BINLT</c>.</summary>
    public ReadOnlyMemory<byte> Bins { get; init; }
}

/// <summary>A map as coordinates - map data type 3, carried by S12F11 and S12F18.</summary>
/// <remarks>
/// The only format that can leave a location out, which is what
/// <see cref="WaferMapBinSelection.NonNullOnly"/> asks for: a wafer with forty good die reported
/// this way is forty entries rather than a full array of bins.
/// </remarks>
public sealed class WaferMapPointData
{
    /// <summary>The material this map belongs to - <c>MID</c>.</summary>
    public required string MaterialId { get; init; }

    /// <summary>What <see cref="MaterialId"/> carries - <c>IDTYP</c>.</summary>
    public WaferMapIdType IdType { get; init; }

    /// <summary>The coordinates and their bins, in the order they were sent.</summary>
    public IReadOnlyList<WaferMapPoint> Points { get; init; } = [];
}

/// <summary>A map error report that arrived - S12F19.</summary>
public sealed class WaferMapErrorEventArgs : EventArgs
{
    /// <summary>What the other end is reporting - <c>MAPER</c>.</summary>
    /// <remarks><see cref="WaferMapError.ReceivedCorrectly"/> is not a failure; see that member.</remarks>
    public required WaferMapError Error { get; init; }

    /// <summary>Where in the data the trouble is - <c>DATLC</c>.</summary>
    /// <remarks>A location code whose meaning belongs to the map being reported on; zero when the sender gave none.</remarks>
    public required byte Location { get; init; }
}
