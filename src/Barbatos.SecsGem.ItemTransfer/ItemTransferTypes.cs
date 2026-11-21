// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ItemTransfer;

/// <summary>
/// <c>GRANT</c> - whether an item may be sent, and the codes are the reference's own.
/// </summary>
/// <remarks>
/// <para>
/// Quoted. <c>GRANT</c> is named across five streams, and this is the <strong>third</strong>
/// package here to serve it: <c>MaterialGrant</c> has it for S3F16 and <c>ControlProgramGrant</c>
/// for S8F2. One item cannot mean three things in one repository, so a test asserts all three agree
/// member for member rather than leaving it to the eye.
/// </para>
/// <para>
/// 4 to 63 are the table's own reserved range and read as <see cref="Other"/> with the arriving
/// code logged - never as the zero of this enum, which would turn somebody else's refusal into
/// permission to send.
/// </para>
/// </remarks>
public enum ItemTransferGrant : byte
{
    /// <summary>Send it.</summary>
    Accepted = 0,

    /// <summary>Busy; ask again later.</summary>
    Busy = 1,

    /// <summary>No space available for it.</summary>
    NoSpace = 2,

    /// <summary>The identifier was a duplicate, or not one this end recognises.</summary>
    DuplicateOrInvalid = 3,

    /// <summary>Some other refusal: the table reserves 4 to 63.</summary>
    /// <remarks>Every code from 4 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 4,
}

/// <summary>
/// The answer to a data send, a transfer abort or an item delete.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One enum for four items, and that is a smaller claim than it looks.</strong> Stream 21's
/// three replies this package implements carry three different item names - <c>ACKC21</c>,
/// <c>ABTACK</c> and <c>DELACK</c> - and the fourth, <c>XFRACK</c>, belongs to a pair that stays
/// out. <strong>None of the four has a dictionary entry</strong>, so there is no table for them to
/// differ on. Four two-member enums with identical members would be pretending to a distinction
/// this reference does not make.
/// </para>
/// <para>
/// <strong>Zero is accepted, read off the family rather than guessed.</strong> The catalogue
/// defines <strong>fifty</strong> items whose names end in <c>ACK</c>, forty-two of them with a
/// table of values - and <strong>forty-one of those forty-two have 0 = accepted</strong>, in the
/// words "accepted", "acknowledge", "OK", "done", "completed successfully" or "successful". The one
/// exception is <c>CPACK</c>, whose table starts at 1 for a documented reason: the list it appears
/// in carries the command parameters that were <em>wrong</em> and not the ones that were fine. That
/// is a stronger family measurement than the eight <c>ACKCn</c> siblings
/// <c>Barbatos.SecsGem.ControlPrograms</c> had to lean on, and it points the same way.
/// </para>
/// <para>
/// Nothing names anything above zero for any of the four, so every non-zero code reads as
/// <see cref="Denied"/> with the arriving code logged. There is deliberately no <c>Other</c>
/// member: with one named code, a third would be inventing a distinction between kinds of silence -
/// the same choice <c>ControlProgramAcknowledgement</c> makes for <c>ACKC8</c>.
/// </para>
/// </remarks>
public enum ItemAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Refused, with no reason given - none of these items has a table to carry one.</summary>
    /// <remarks>Every code from 1 upwards reads as this one, and the arriving code is logged.</remarks>
    Denied = 1,
}

/// <summary>
/// An item's contents - <c>ITEMDATA</c> - as the encoded item that carried them.
/// </summary>
/// <remarks>
/// <para>
/// <c>ITEMDATA</c> has no dictionary entry and is named by stream 21 alone, so there is no sibling
/// row and no other stream to read a format off. That is the same position stream 13's
/// <c>DSDATA</c> is in, and this is the same answer: keep the item <em>as encoded</em> - format
/// header, length header and payload - because a <see cref="SecsItem"/> decoded off the wire
/// borrows pooled memory, is released when its message is disposed, and has no clone.
/// </para>
/// <para>
/// <strong>This is deliberately a copy of <c>DataSetPayload</c>, and the duplication is the
/// honest option.</strong> The two packages must not depend on each other - an item transfer peer
/// is not a data set equipment, and neither standard mentions the other - and the problem is
/// identical, so the answer is identical. If a third package ever needs it, the type belongs in
/// <c>Barbatos.SecsGem.Core</c> rather than copied again.
/// </para>
/// <para>
/// <strong>It is not the same thing as a control program body.</strong>
/// <c>Barbatos.SecsGem.ControlPrograms</c> hands firmware about as plain bytes, because
/// <c>CPBODY</c> sits where stream 7's defined <c>PPBODY</c> sits. These bytes are an item tree,
/// header and all.
/// </para>
/// </remarks>
public sealed class ItemPayload
{
    private ItemPayload(ReadOnlyMemory<byte> encoded, SecsFormat? format, int? count)
    {
        Encoded = encoded;
        Format = format;
        Count = count;
    }

    /// <summary>Gets the encoded item: header and payload, as it crossed the wire.</summary>
    public ReadOnlyMemory<byte> Encoded { get; }

    /// <summary>Gets the format of the item that arrived, when it is known.</summary>
    /// <remarks><see langword="null"/> for a payload built from bytes by <see cref="FromEncoded"/> without one being given.</remarks>
    public SecsFormat? Format { get; }

    /// <summary>Gets how many values the item carried, when it is known.</summary>
    public int? Count { get; }

    /// <summary>Gets whether this is an item at all.</summary>
    /// <remarks>
    /// True for no bytes, and for an item that carried no values. <strong>Not the same as having no
    /// bytes</strong>: an encoded empty item still has a format byte and a length byte in front of
    /// it, so a zero-length list is two bytes and not none. That distinction cost stream 13's
    /// chapter a bug on its first test run.
    /// </remarks>
    public bool IsEmpty => Encoded.IsEmpty || Count == 0;

    /// <summary>Takes ownership of an item by encoding it.</summary>
    /// <param name="item">The item. Stays the caller's to dispose; this does not keep it.</param>
    /// <returns>The payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    public static ItemPayload From(SecsItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new ItemPayload(item.ToByteArray(), item.Format, item.Count);
    }

    /// <summary>Wraps bytes that are already an encoded item, without copying them.</summary>
    /// <param name="encoded">The encoded item. Must stay valid for as long as the payload is used.</param>
    /// <param name="format">The item's format, when the caller knows it.</param>
    /// <param name="count">How many values the item carries, when the caller knows it.</param>
    /// <returns>The payload.</returns>
    /// <remarks>The way in for a store that read an item off disk: those bytes are already its own.</remarks>
    public static ItemPayload FromEncoded(
        ReadOnlyMemory<byte> encoded,
        SecsFormat? format = null,
        int? count = null)
        => new(encoded, format, count);

    /// <summary>Decodes the item back.</summary>
    /// <returns>A fresh item the caller disposes, or <see langword="null"/> when there are no bytes.</returns>
    /// <exception cref="SecsException">The bytes are not a well-formed item.</exception>
    public SecsItem? Decode() => Encoded.IsEmpty ? null : SecsItem.Decode(Encoded);

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The format, the value count and the encoded length.</returns>
    public override string ToString()
        => Count is { } count
            ? $"{(Format is { } known ? known.ToString() : "unknown")}[{count}], {Encoded.Length} encoded bytes"
            : $"{(Format is { } format ? format.ToString() : "unknown")}, {Encoded.Length} encoded bytes";
}

/// <summary>One item, as S21F2 lists it.</summary>
/// <remarks>
/// All three of its fields are defined, which is unusual enough in this stream to be worth saying:
/// <c>ITEMID</c> is an <c>A</c>, <c>ITEMTYPE</c> an <c>A</c> and <c>LENGTH</c> a <c>U4</c>. The
/// first two are <strong>stream 21's own vocabulary</strong> - defined, and named by no other
/// stream in the catalogue - which makes this the last stream in the reference to have any.
/// </remarks>
public sealed class Item
{
    /// <summary>Gets what the item is called - <c>ITEMID</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Gets what kind of item it is - <c>ITEMTYPE</c>.</summary>
    /// <remarks>
    /// The reference gives no vocabulary for this, so the values are the two ends' own: a file, a
    /// recipe, a log, whatever a pair of machines agrees on. Nothing here interprets it, and
    /// <see cref="IItemStore.ListAsync"/> takes an empty one to mean every kind.
    /// </remarks>
    public string Type { get; init; } = string.Empty;

    /// <summary>Gets how large it is - <c>LENGTH</c>.</summary>
    /// <remarks>
    /// <strong>In what units is the sender's business.</strong> <c>LENGTH</c> is a <c>U4</c> and the
    /// reference says nothing about what it counts, so nothing here interprets it:
    /// <see cref="ItemInMemoryStore"/> reports the value count of the item it was sent and says so,
    /// and another store may mean bytes.
    /// </remarks>
    public uint Length { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The identifier, the type and the length.</returns>
    /// <remarks>
    /// Deliberately not "<c>n</c> bytes": the reference gives <c>LENGTH</c> no units, so a
    /// description that supplied some would be this library claiming what it documents it cannot
    /// know.
    /// </remarks>
    public override string ToString()
        => Type.Length == 0 ? $"{Id} (length {Length})" : $"{Id} [{Type}] (length {Length})";
}

/// <summary>One of an item's attributes - <c>ATTRID</c> and <c>ATTRDATA</c>.</summary>
/// <remarks>
/// <para>
/// Both items are defined - an <c>A</c> and an <c>L</c> - and both belong to
/// <strong>SEMI E39</strong>: the catalogue names them in streams 1, 14 and 21, and stream 14 is
/// the object services <c>Barbatos.SecsGem.Objects</c> implements. So this pair is E39's name and
/// value vocabulary applied to an item, and S21F4's body follows S14F2's nesting -
/// <c>L,2 { identifier, L,n { L,2 { ATTRID, ATTRDATA } } }</c> - rather than a shape this package
/// invented. Reading it any other way would make two packages here disagree about two items.
/// </para>
/// <para>
/// <strong>A value this library hands you is a live view</strong> into the message it arrived in,
/// and is gone when the call returns. Copy anything kept past that. This is the same contract
/// <c>ObjectAttribute</c> carries, for the same two items.
/// </para>
/// </remarks>
public sealed class ItemAttribute
{
    /// <summary>Gets what the attribute is called - <c>ATTRID</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets what it is - <c>ATTRDATA</c>.</summary>
    public required SecsItem Value { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The name and the value's format.</returns>
    public override string ToString() => $"{Name} = {Value.Format}";
}

/// <summary>What S21F3 is asking for.</summary>
public sealed class ItemAttributeRequest
{
    /// <summary>Gets which item - <c>ITEMID</c>.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets which attributes - the <c>ATTRID</c> list. Empty means every one the item has.</summary>
    /// <remarks>
    /// Empty meaning "all of them" is the reading stream 14 uses for the same item in S14F1, which
    /// is where this package takes it from rather than deciding it.
    /// </remarks>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The item and how many attributes were asked for.</returns>
    public override string ToString()
        => Attributes.Count == 0 ? $"{ItemId}, every attribute" : $"{ItemId}, {Attributes.Count} attribute(s)";
}

/// <summary>What S21F5 is asking permission for, and what S21F7 sends.</summary>
public sealed class ItemTransferRequest
{
    /// <summary>Gets which item - <c>ITEMID</c>.</summary>
    public required string ItemId { get; init; }

    /// <summary>Gets the sender's own correlation number - <c>DATAID</c>, a <c>U4</c> in the dictionary.</summary>
    /// <remarks>
    /// Taken to be a correlation number, which is what it is across all seven streams that name it
    /// and what three other packages here already serve it as.
    /// </remarks>
    public required uint DataId { get; init; }

    /// <summary>Gets how large the item is - <c>LENGTH</c>. Carried by S21F5 only.</summary>
    public uint Length { get; init; }

    /// <summary>Gets a short description, for logs.</summary>
    /// <returns>The item, the correlation number and the length.</returns>
    public override string ToString() => $"{ItemId} #{DataId}, length {Length}";
}
