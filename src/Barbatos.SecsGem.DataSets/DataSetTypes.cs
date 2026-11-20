// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.DataSets;

/// <summary>
/// <c>GRANT6</c> - whether a data set may be opened, and the codes are the reference's own.
/// </summary>
/// <remarks>
/// <para>
/// Quoted rather than inferred. <c>GRANT6</c> is a <c>B[1]</c> in the dictionary <em>with a table
/// of values</em>, and the table is short because the question is: there is room and the equipment
/// is willing, or there is not.
/// </para>
/// <para>
/// 3 to 63 are the table's own reserved and equipment-specific range, and they all read as
/// <see cref="Other"/> with the arriving code logged - the same rule every other acknowledgement
/// in this stack keeps. Never as the zero of this enum, which here would turn somebody else's
/// refusal into permission to send.
/// </para>
/// </remarks>
public enum DataSetGrant : byte
{
    /// <summary>Permission granted; go ahead.</summary>
    Ok = 0,

    /// <summary>Busy; ask again later.</summary>
    Busy = 1,

    /// <summary>Not interested, or no space for it.</summary>
    NoSpace = 2,

    /// <summary>Some other refusal: the table reserves 3 to 63.</summary>
    /// <remarks>Every code from 3 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 3,
}

/// <summary>
/// <c>ACKC13</c> - the answer to a data set operation.
/// </summary>
/// <remarks>
/// <para>
/// The four names are the dictionary's own, and a second entry corroborates them: <c>ACKC15</c>,
/// recipe management's acknowledgement, carries the identical four codes in the identical order.
/// Two independent item names agreeing is better evidence than one.
/// </para>
/// <para>
/// <strong>Unlike <see cref="DataSetGrant"/>, this table declares no reserved range.</strong> It
/// lists 0 to 3 and stops. That is not a statement that 4 cannot arrive - of the 88 code tables in
/// the catalogue shipped with this repository, 53 declare no range either, so silence is this
/// source's habit rather than a promise. The item is a <c>B[1]</c>, so 252 of its 256 values are
/// unnamed, and <see cref="Other"/> is this library's handling of them rather than a code E5
/// reserved. The distinction is in the docs because it is the difference between quoting a
/// reference and covering for one.
/// </para>
/// <para>
/// There is no length error here, where <c>ACKC7</c> has one for process programs. See
/// <see cref="DataSetOpenRequest.Length"/> for why that matters and what this package does about
/// it.
/// </para>
/// </remarks>
public enum DataSetAcknowledgement : byte
{
    /// <summary>Done.</summary>
    Accepted = 0,

    /// <summary>Denied: the data set is not available.</summary>
    /// <remarks>The equipment knows the data set and cannot hand it over now - busy, offline, mid-process.</remarks>
    NotAvailable = 1,

    /// <summary>Denied: that is not a data set this equipment has.</summary>
    /// <remarks>
    /// The code for a <c>DSID</c> nothing opened and nothing stored. A store that enforces the
    /// open-before-send sequence refuses with this one; see <see cref="DataSetInMemoryStore"/>.
    /// </remarks>
    InvalidDataSet = 2,

    /// <summary>Denied: the data set is known and what arrived in it is wrong.</summary>
    /// <remarks>
    /// Including a payload whose size does not match the <c>LENGTH</c> the open declared, which is
    /// a judgement only a store can make - see <see cref="DataSetOpenRequest.Length"/>.
    /// </remarks>
    InvalidData = 3,

    /// <summary>Some code this table does not name.</summary>
    /// <remarks>Every code from 4 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>
/// Which open a host asked for: S13F1 to put a data set in, or S13F5 to take one out.
/// </summary>
/// <remarks>
/// Valued by function number, as <c>MaterialHandoffStep</c> is in
/// <c>Barbatos.SecsGem.Material</c>: the number is the fact, and an enum that renumbers it invents
/// a second identity for the same message.
/// </remarks>
public enum DataSetOpenMode : byte
{
    /// <summary>S13F1 Open Data Set Send - the host is about to send one.</summary>
    Send = 1,

    /// <summary>S13F5 Open Data Set Request - the host wants to read one.</summary>
    Request = 5,
}

/// <summary>
/// A data set's contents - <c>DSDATA</c> - as the encoded item that carried them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This type exists because <c>DSDATA</c> is the one item stream 13 needs that the
/// catalogue never defines.</strong> Every other name in the fourteen rows this package implements
/// has a dictionary entry: <c>DSID</c>, <c>DATAID</c> and <c>LENGTH</c> are each a <c>U4</c>, and
/// both code tables arrive with their values. <c>DSDATA</c> arrives with nothing, so this library
/// does not get to say what format a data set is in - and does not.
/// </para>
/// <para>
/// So what is kept is the item <em>as encoded</em>: format header, length header and payload,
/// exactly the bytes that crossed the wire. <see cref="Decode"/> turns them back into a
/// <see cref="SecsItem"/> whenever anybody wants to look.
/// </para>
/// <para>
/// <strong>It is not the same thing as a process program body, and the same C# type for both is a
/// trap worth naming.</strong> <c>Barbatos.SecsGem.Gem</c> hands a process program about as a
/// <see cref="ReadOnlyMemory{T}"/> of bytes because <c>PPBODY</c> <em>is defined</em>, as a
/// <c>B</c> - so there those bytes are the recipe. Here they are an item tree, header and all.
/// Code that assumed stream 7's meaning would find two spare bytes in front of its data and a
/// format byte it never wrote.
/// </para>
/// <para>
/// <strong>Why a copy rather than the arriving item.</strong> A <see cref="SecsItem"/> decoded off
/// the wire borrows pooled memory and is released when its message is disposed, and there is no
/// clone on it - so a store that kept one would be holding a buffer the next message is about to
/// reuse. The spool in <c>Barbatos.SecsGem.Gem</c> met this first and answered it the same way:
/// <c>GemSpooledMessage.Body</c> is the encoded item tree for exactly this reason. Taking
/// ownership costs one copy, which is the price of the item not being yours;
/// <see cref="FromEncoded"/> is the way in for a store that already owns its bytes and should not
/// pay it.
/// </para>
/// </remarks>
public sealed class DataSetPayload
{
    private DataSetPayload(ReadOnlyMemory<byte> encoded, SecsFormat? format, int? count)
    {
        Encoded = encoded;
        Format = format;
        Count = count;
    }

    /// <summary>Gets the encoded item: header and payload, as it crossed the wire.</summary>
    /// <remarks>Empty for a data set that arrived with no body at all, which is a machine saying it has nothing rather than a refusal.</remarks>
    public ReadOnlyMemory<byte> Encoded { get; }

    /// <summary>Gets the format of the item that arrived, when it is known.</summary>
    /// <remarks>
    /// <see langword="null"/> only for a payload built from bytes by <see cref="FromEncoded"/>
    /// without one being given - the format is in those bytes, and reading it would mean decoding
    /// them, which is the caller's call and not this property's.
    /// </remarks>
    public SecsFormat? Format { get; }

    /// <summary>Gets how many values the item carried, when it is known.</summary>
    /// <remarks>
    /// Items for a list, characters for text, numbers for anything numeric.
    /// <see langword="null"/> under the same condition as <see cref="Format"/>: a payload built
    /// from bytes by <see cref="FromEncoded"/> without being told.
    /// </remarks>
    public int? Count { get; }

    /// <summary>Gets whether this is a data set at all.</summary>
    /// <remarks>
    /// <para>
    /// True for a payload with no bytes, and for one whose item carried no values - a zero-length
    /// list, an empty string, no bytes in a <c>B</c>. Both mean the same thing in stream 13,
    /// because S13F8 has no acknowledgement code: an empty <c>DSDATA</c> <em>is</em> how an
    /// equipment says it has not got the data set asked for.
    /// </para>
    /// <para>
    /// <strong>It is not the same as having no bytes</strong>, which is the trap here and the
    /// reason this is a property rather than a check on <see cref="Encoded"/>: an encoded empty
    /// item still has a format byte and a length byte in front of it, so a zero-length list
    /// encodes to two bytes and not to none. <see cref="Decode"/> keys on the bytes, because that
    /// is the question it is answering; this keys on the values.
    /// </para>
    /// <para>
    /// A payload from <see cref="FromEncoded"/> that was given no count reports false whenever it
    /// has bytes, since an unknown count is not a known zero.
    /// </para>
    /// </remarks>
    public bool IsEmpty => Encoded.IsEmpty || Count == 0;

    /// <summary>Takes ownership of an item by encoding it.</summary>
    /// <param name="item">The item. Stays the caller's to dispose; this does not keep it.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// One copy, the same one <c>ObjectAttributeStore</c> in <c>Barbatos.SecsGem.Objects</c> makes
    /// to keep an attribute value past its message. A data set large enough for that copy to
    /// matter belongs in a store that writes it through to somewhere rather than holding it, which
    /// is what <c>GemFileSpool</c> does with the same bytes for the same reason - and
    /// <see cref="FromEncoded"/> is how those bytes come back without a second copy.
    /// </remarks>
    public static DataSetPayload From(SecsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new DataSetPayload(item.ToByteArray(), item.Format, item.Count);
    }

    /// <summary>Wraps bytes that are already an encoded item, without copying them.</summary>
    /// <param name="encoded">The encoded item. Must stay valid for as long as the payload is used.</param>
    /// <param name="format">The item's format, when the caller knows it.</param>
    /// <param name="count">How many values the item carries, when the caller knows it.</param>
    /// <returns>The payload.</returns>
    /// <remarks>
    /// The way in for a store that read a data set off disk or out of a database: those bytes are
    /// already the store's own, and copying them again would be this library charging for
    /// ownership it was handed. Neither header is read off them, because reading them would mean
    /// decoding a data set this method exists to avoid touching - so a caller who knows says so,
    /// and one who does not leaves both unknown.
    /// </remarks>
    public static DataSetPayload FromEncoded(
        ReadOnlyMemory<byte> encoded,
        SecsFormat? format = null,
        int? count = null)
        => new(encoded, format, count);

    /// <summary>Decodes the data set back into an item.</summary>
    /// <returns>A fresh item, or <see langword="null"/> when there are no bytes to decode.</returns>
    /// <exception cref="SecsException">The bytes are not a well-formed item.</exception>
    /// <remarks>
    /// The item is the caller's to dispose, like anything else that comes out of
    /// <c>SecsItem.Decode</c>. An item that carried no values still decodes - it is a real
    /// zero-length item and not an absence; see <see cref="IsEmpty"/> for the difference.
    /// </remarks>
    public SecsItem? Decode()
        => Encoded.IsEmpty ? null : SecsItem.Decode(Encoded);

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The format and the encoded length.</returns>
    public override string ToString()
        => Count is { } count
            ? $"{(Format is { } known ? known.ToString() : "unknown")}[{count}], {Encoded.Length} encoded bytes"
            : $"{(Format is { } format ? format.ToString() : "unknown")}, {Encoded.Length} encoded bytes";
}

/// <summary>What S13F1 or S13F5 asked for.</summary>
/// <remarks>
/// One type for both opens, because they differ in one field and in which way the length travels -
/// see <see cref="Length"/>.
/// </remarks>
public sealed class DataSetOpenRequest
{
    /// <summary>Gets which data set - <c>DSID</c>.</summary>
    public required uint DataSetId { get; init; }

    /// <summary>Gets which open this is.</summary>
    public required DataSetOpenMode Mode { get; init; }

    /// <summary>Gets how large the host says the data set is - <c>LENGTH</c>. Zero for an S13F5, which does not carry one.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This package does not check it.</strong> E5 declares the length up front so the
    /// equipment can refuse for space before the bytes are on the wire, and that part works: it is
    /// handed to the store, which is the only thing that knows whether it has room.
    /// </para>
    /// <para>
    /// What cannot be done is comparing it with what then arrives. <c>LENGTH</c> is a <c>U4</c> and
    /// the dictionary says nothing about what it counts, and <c>DSDATA</c> has no defined format to
    /// measure - so a mismatch is not something this library can compute without inventing both
    /// halves of the arithmetic. <c>Barbatos.SecsGem.Gem</c> does check the equivalent for a
    /// process program, and can, because <c>PPBODY</c> is a <c>B</c> and <c>ACKC7</c> has a length
    /// error code to report it with. <c>ACKC13</c> has no such code. A store that wants to refuse
    /// a mismatch under its own definition of length says
    /// <see cref="DataSetAcknowledgement.InvalidData"/>.
    /// </para>
    /// </remarks>
    public uint Length { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The mode, the identifier and the length.</returns>
    public override string ToString()
        => Mode == DataSetOpenMode.Send
            ? $"open data set {DataSetId} to send, {Length} long"
            : $"open data set {DataSetId} to read";
}

/// <summary>What S13F2 or S13F6 answered an open with.</summary>
/// <remarks>
/// <strong>The asymmetry is E5's.</strong> S13F2 carries the grant alone, because on a send the
/// host already knows how long the data set is - it said so. S13F6 carries the grant
/// <em>and</em> the length, because on a read only the equipment knows. So one of the two fields
/// here is meaningful one way round, which is a fact about the two rows rather than a shortcut.
/// </remarks>
public sealed class DataSetOpening
{
    /// <summary>Gets the grant - <c>GRANT6</c>.</summary>
    public required DataSetGrant Grant { get; init; }

    /// <summary>Gets how long the equipment says the data set is - <c>LENGTH</c>. Carried by S13F6 only.</summary>
    /// <remarks>Zero after an S13F1, which is answered by a reply that has no length in it - not a data set of length zero.</remarks>
    public uint Length { get; init; }

    /// <summary>Gets whether the open was granted.</summary>
    public bool IsGranted => Grant == DataSetGrant.Ok;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The grant and the length.</returns>
    public override string ToString()
        => $"{Grant}, {Length} long";
}

/// <summary>A data set arriving - S13F3 - or going back out in S13F8.</summary>
public sealed class DataSetContent
{
    /// <summary>Gets which data set - <c>DSID</c>.</summary>
    public required uint DataSetId { get; init; }

    /// <summary>Gets the host's own correlation number - <c>DATAID</c>.</summary>
    /// <remarks>See <see cref="DataSetRequest.DataId"/> for why that is what it is taken to mean.</remarks>
    public required uint DataId { get; init; }

    /// <summary>Gets the contents - <c>DSDATA</c>.</summary>
    public required DataSetPayload Data { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifiers and the payload.</returns>
    public override string ToString()
        => $"data set {DataSetId} #{DataId}: {Data}";
}

/// <summary>What S13F7 asked for.</summary>
public sealed class DataSetRequest
{
    /// <summary>Gets which data set - <c>DSID</c>.</summary>
    public required uint DataSetId { get; init; }

    /// <summary>Gets the host's own correlation number - <c>DATAID</c>, to be echoed in S13F8.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The one inference in this package's reading of the rows, and it is argued rather
    /// than assumed.</strong> The dictionary gives <c>DATAID</c> a format - <c>U4</c> - and no
    /// description, so two readings are open: a number the host invents to match a reply to its
    /// request, or a name for one piece of data inside the set.
    /// </para>
    /// <para>
    /// The rows settle it. Stream 13 has a <em>separate</em> item for which piece - <c>BLKNUM</c>,
    /// in the blocked send this package leaves out - so <c>DATAID</c> is not that. And every one of
    /// the thirty-two messages in the catalogue that names <c>DATAID</c> is a report, a request or
    /// a multi-block inquire, which is where a correlation number belongs; three packages in this
    /// repository already serve it that way, all three as a <c>U4</c>, agreeing with the
    /// dictionary.
    /// </para>
    /// </remarks>
    public required uint DataId { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifiers.</returns>
    public override string ToString()
        => $"data set {DataSetId} #{DataId}";
}

/// <summary>What S13F16 answered a status request with.</summary>
/// <remarks>
/// The one reply in stream 13 that carries a length <em>and</em> an acknowledgement, which is how a
/// host finds out whether a data set exists and how big it is in one transaction rather than two.
/// </remarks>
public sealed class DataSetStatus
{
    /// <summary>Gets which data set - <c>DSID</c>.</summary>
    public required uint DataSetId { get; init; }

    /// <summary>Gets how long the equipment says it is - <c>LENGTH</c>.</summary>
    public uint Length { get; init; }

    /// <summary>Gets whether it is there - <c>ACKC13</c>.</summary>
    /// <remarks>
    /// <see cref="DataSetAcknowledgement.Accepted"/> for a data set the equipment has;
    /// <see cref="DataSetAcknowledgement.InvalidDataSet"/> for one it does not.
    /// </remarks>
    public required DataSetAcknowledgement Acknowledgement { get; init; }

    /// <summary>Gets whether the equipment has this data set.</summary>
    public bool Exists => Acknowledgement == DataSetAcknowledgement.Accepted;

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifier, the length and the acknowledgement.</returns>
    public override string ToString()
        => $"data set {DataSetId}: {Acknowledgement}, {Length} long";
}
