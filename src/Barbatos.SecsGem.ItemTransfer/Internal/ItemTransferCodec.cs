// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.ItemTransfer;

/// <summary>
/// Every stream 21 body this package sends or reads, in one place.
/// </summary>
/// <remarks>
/// <para>
/// One end goes through this class, and that end is both ends: every row of stream 21 is marked as
/// travelling either way, so the same class writes a request and reads one.
/// </para>
/// <para>
/// <strong>What the provenance is.</strong> Not one of stream 21's twenty rows is a definition. Of
/// the eighteen item names it uses, seven are defined - and the split across the pairs is sharp
/// enough to have decided what this package implements:
/// </para>
/// <code>
/// ITEMID     A     defined, and named by no other stream - stream 21's own vocabulary
/// ITEMTYPE   A     defined, likewise
/// ATTRID     A     defined, and E39's - streams 1, 14 and 21 name it
/// ATTRDATA   L     defined, and E39's likewise
/// DATAID     U4    defined, and named by seven streams
/// LENGTH     U4    defined, and named by eight
/// GRANT      B     defined, four codes and a reserved range
/// ITEMDATA   --    undefined, named by stream 21 alone - carried as the encoded item
/// ACKC21     --    undefined, as are ABTACK and DELACK: zero is accepted, off 41 of 42 siblings
/// </code>
/// <para>
/// <strong>Three pairs need no inference at all</strong> - the directory, the attributes and the
/// transfer request - which is the first time in this repository that a stream has split that
/// cleanly. The attributes body follows <strong>S14F2's nesting</strong> rather than one this
/// package invented, because <c>ATTRID</c> and <c>ATTRDATA</c> are stream 14's items and
/// <c>Barbatos.SecsGem.Objects</c> already writes them that way.
/// </para>
/// <para>
/// <strong>What is left out, and the measurement.</strong> S21F9 to S21F14 are the blocked
/// transfer and its status, and they need <c>BLKSIZE</c>, <c>BLKNUM</c>, <c>BLKEND</c>,
/// <c>BLKACK</c> and <c>XFRSTAT</c>. Every one of the catalogue's seven block-transfer items is
/// undefined - the same measurement that kept S8F7 to S8F10 and S13F9/F10 out. S21F19/F20, the
/// transfer complete, needs <c>CHKSUM</c> and <c>XFRACK</c>, both undefined - and a checksum is
/// the one value carrying it opaquely does not rescue, because what two ends have to agree on is
/// the algorithm and the span, and the reference gives neither of those for any of the three
/// spellings it lists.
/// </para>
/// <para>
/// <strong>Nesting is the one thing a flat item list cannot pin down</strong>, so the readers are
/// more generous than the writers.
/// </para>
/// </remarks>
internal static class ItemTransferCodec
{
    // ------------------------------------------------------------------ the directory

    /// <summary>S21F1 - a bare <c>ITEMTYPE</c>. Empty asks for every kind.</summary>
    internal static SecsItem DirectoryRequestBody(string itemType)
        => SecsItem.A(itemType).Named("ITEMTYPE");

    /// <summary>Reads S21F1, descending into a list of one.</summary>
    internal static bool TryReadDirectoryRequest(SecsItem? item, out string itemType)
    {
        itemType = string.Empty;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            // A zero-length list is a machine asking for everything without an item to say it
            // with, which is the same question as an empty ITEMTYPE.
            return item.Count == 0 || (item.Count == 1 && TryReadDirectoryRequest(item.Items[0], out itemType));
        }

        itemType = Word(item);
        return true;
    }

    /// <summary>S21F2 - <c>L,n { L,3 { ITEMID, ITEMTYPE, LENGTH } }</c>.</summary>
    internal static SecsItem DirectoryBody(IReadOnlyList<Item>? items)
    {
        if (items is null || items.Count == 0)
        {
            return SecsItem.L();
        }

        var entries = new SecsItem[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(items[i].Id).Named("ITEMID"),
                SecsItem.A(items[i].Type).Named("ITEMTYPE"),
                SecsItem.U4(items[i].Length).Named("LENGTH"));
        }

        return SecsItem.L(entries);
    }

    /// <summary>Reads S21F2, nested or flat.</summary>
    internal static List<Item> ReadDirectory(SecsItem? body)
    {
        List<Item> items = [];

        if (body is null || body.Count == 0)
        {
            return items;
        }

        foreach (var entry in Entries(body, 3))
        {
            var id = Word(entry[0]);

            // Dropped rather than kept: an item with no identifier is not an item, and an empty
            // one would be a directory row nobody can ask about.
            if (id.Length == 0)
            {
                continue;
            }

            items.Add(new Item
            {
                Id = id,
                Type = Word(entry[1]),
                Length = UInt32(entry[2]),
            });
        }

        return items;
    }

    // ----------------------------------------------------------------- the attributes

    /// <summary>S21F3 - <c>L,2 { ITEMID, L,n { ATTRID } }</c>.</summary>
    internal static SecsItem AttributeRequestBody(ItemAttributeRequest request)
    {
        var names = new SecsItem[request.Attributes.Count];

        for (var i = 0; i < request.Attributes.Count; i++)
        {
            names[i] = SecsItem.A(request.Attributes[i]).Named("ATTRID");
        }

        return SecsItem.L(
            SecsItem.A(request.ItemId).Named("ITEMID"),
            SecsItem.L(names));
    }

    /// <summary>Reads S21F3.</summary>
    internal static bool TryReadAttributeRequest(SecsItem? body, out ItemAttributeRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count == 0)
        {
            return false;
        }

        var itemId = Word(body.Items[0]);

        if (itemId.Length == 0)
        {
            return false;
        }

        List<string> names = [];

        if (body.Count > 1 && body.Items[1].IsList)
        {
            foreach (var name in body.Items[1].Items)
            {
                var read = Word(name);

                if (read.Length > 0)
                {
                    names.Add(read);
                }
            }
        }

        request = new ItemAttributeRequest { ItemId = itemId, Attributes = names };
        return true;
    }

    /// <summary>S21F4 - <c>L,2 { ITEMID, L,n { L,2 { ATTRID, ATTRDATA } } }</c>.</summary>
    /// <remarks>
    /// <para>
    /// The nesting is S14F2's, because <c>ATTRID</c> and <c>ATTRDATA</c> are stream 14's items and
    /// <c>Barbatos.SecsGem.Objects</c> writes them that way. A flat item list cannot say this, so
    /// the shape comes from the stream that owns the vocabulary rather than from here.
    /// </para>
    /// <para>
    /// <strong>Every value is copied in, because an item handed to a message becomes that
    /// message's.</strong> A store hands back its own item - <c>ItemInMemoryStore</c> keeps the one
    /// its caller built - and a reply is disposed once it has been sent, which disposes its
    /// children: an attribute past <see cref="SecsCodecOptions.PooledPayloadThreshold"/> would have
    /// had its buffer handed back to the pool after the first S21F3 and lent out again before the
    /// second. This package's own remarks described that hazard for four chapters and left it to
    /// the caller, which was never a lifetime a caller could honour - the reply is the library's.
    /// </para>
    /// <para>
    /// <strong>The copy is here rather than in the two stores shipped with this package</strong>
    /// because <see cref="IItemStore"/> is public: a store an application writes cannot be made to
    /// keep bytes, and this is the one place every store's answer passes through on its way to a
    /// message.
    /// </para>
    /// <para>
    /// It stayed invisible until a sweep went looking, for the reason the same defect in
    /// <c>Barbatos.SecsGem.Recipes</c> did: <strong>text never borrows.</strong> An <c>A[n]</c>
    /// decodes into a string that owns nothing, and every test of this pair set text.
    /// </para>
    /// </remarks>
    internal static SecsItem AttributeBody(string itemId, IReadOnlyList<ItemAttribute>? attributes)
    {
        var entries = new SecsItem[attributes?.Count ?? 0];

        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(attributes![i].Name).Named("ATTRID"),
                SecsItem.Decode(attributes[i].Value.ToByteArray()).Named("ATTRDATA"));
        }

        return SecsItem.L(
            SecsItem.A(itemId).Named("ITEMID"),
            SecsItem.L(entries));
    }

    /// <summary>Reads S21F4.</summary>
    internal static bool TryReadAttributes(
        SecsItem? body,
        out string itemId,
        out List<(string Name, SecsItem Value)> attributes)
    {
        itemId = string.Empty;
        attributes = [];

        if (body is null || !body.IsList || body.Count == 0)
        {
            return false;
        }

        itemId = Word(body.Items[0]);

        if (body.Count > 1)
        {
            foreach (var entry in Entries(body.Items[1], 2))
            {
                var name = Word(entry[0]);

                if (name.Length > 0)
                {
                    attributes.Add((name, entry[1]));
                }
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ the transfer

    /// <summary>S21F5 - <c>L,3 { ITEMID, DATAID, LENGTH }</c>.</summary>
    internal static SecsItem TransferRequestBody(ItemTransferRequest request)
        => SecsItem.L(
            SecsItem.A(request.ItemId).Named("ITEMID"),
            SecsItem.U4(request.DataId).Named("DATAID"),
            SecsItem.U4(request.Length).Named("LENGTH"));

    /// <summary>Reads S21F5. A body short of its length reads as a length of zero.</summary>
    internal static bool TryReadTransferRequest(SecsItem? body, out ItemTransferRequest? request)
    {
        request = null;

        if (body is null || !body.IsList || body.Count < 2)
        {
            return false;
        }

        var itemId = Word(body.Items[0]);

        if (itemId.Length == 0)
        {
            return false;
        }

        request = new ItemTransferRequest
        {
            ItemId = itemId,
            DataId = UInt32(body.Items[1]),
            Length = body.Count > 2 ? UInt32(body.Items[2]) : 0,
        };

        return true;
    }

    /// <summary>S21F7 - <c>L,3 { ITEMID, DATAID, ITEMDATA }</c>.</summary>
    /// <remarks>
    /// The same three-item shape stream 13's S13F3 has - an identifier, a correlation number and a
    /// payload - which is a coincidence of design rather than a route: both payloads are undefined,
    /// so neither stream can read a format off the other.
    /// </remarks>
    internal static SecsItem DataBody(string itemId, uint dataId, SecsItem? data)
        => SecsItem.L(
            SecsItem.A(itemId).Named("ITEMID"),
            SecsItem.U4(dataId).Named("DATAID"),
            (data ?? SecsItem.L()).Named("ITEMDATA"));

    /// <summary>Reads S21F7.</summary>
    /// <remarks>
    /// Exactly three items, and deliberately no tolerance for two: the second of two could be the
    /// correlation number or the data, and <c>ITEMDATA</c> has no format to tell them apart by.
    /// </remarks>
    internal static bool TryReadData(
        SecsItem? body,
        out string itemId,
        out uint dataId,
        out ItemPayload? data)
    {
        itemId = string.Empty;
        dataId = 0;
        data = null;

        if (body is null || !body.IsList || body.Count < 3)
        {
            return false;
        }

        itemId = Word(body.Items[0]);

        if (itemId.Length == 0)
        {
            return false;
        }

        dataId = UInt32(body.Items[1]);
        data = ItemPayload.From(body.Items[2]);

        return true;
    }

    /// <summary>S21F15 - <c>L,2 { ITEMID, DATAID }</c>.</summary>
    internal static SecsItem AbortBody(string itemId, uint dataId)
        => SecsItem.L(
            SecsItem.A(itemId).Named("ITEMID"),
            SecsItem.U4(dataId).Named("DATAID"));

    /// <summary>Reads S21F15. A bare identifier reads as a correlation number of zero.</summary>
    internal static bool TryReadAbort(SecsItem? body, out string itemId, out uint dataId)
    {
        itemId = string.Empty;
        dataId = 0;

        if (body is null)
        {
            return false;
        }

        if (!body.IsList)
        {
            itemId = Word(body);
            return itemId.Length > 0;
        }

        if (body.Count == 0)
        {
            return false;
        }

        itemId = Word(body.Items[0]);

        if (itemId.Length == 0)
        {
            return false;
        }

        if (body.Count > 1)
        {
            dataId = UInt32(body.Items[1]);
        }

        return true;
    }

    /// <summary>S21F17 - a bare <c>ITEMID</c>.</summary>
    internal static SecsItem ItemIdBody(string itemId) => SecsItem.A(itemId).Named("ITEMID");

    /// <summary>Reads a bare <c>ITEMID</c>, descending into a list of one.</summary>
    internal static bool TryReadItemId(SecsItem? item, out string itemId)
    {
        itemId = string.Empty;

        if (item is null)
        {
            return false;
        }

        if (item.IsList)
        {
            return item.Count == 1 && TryReadItemId(item.Items[0], out itemId);
        }

        itemId = Word(item);
        return itemId.Length > 0;
    }

    // --------------------------------------------------------- acknowledgement codes

    /// <summary>Writes a one-item acknowledgement body.</summary>
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
    /// <remarks>
    /// Three shapes: a list of entries, which is what this library writes; one bare entry, which is
    /// a machine with one item not bothering to wrap it; and a flat run of fields. An entry short
    /// of its width is dropped rather than padded.
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

    private static uint UInt32(SecsItem item)
        => TryReadUnsigned(item, out var value) ? (uint)Math.Min(value, uint.MaxValue) : 0;

    /// <summary>Reads an unsigned field in whatever numeric format arrived.</summary>
    /// <remarks>
    /// The numbers in the twelve rows this package implements are a correlation number and a
    /// length, so a value too large for its field is clamped rather than refused - the same rule
    /// the other E5 packages here keep.
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
