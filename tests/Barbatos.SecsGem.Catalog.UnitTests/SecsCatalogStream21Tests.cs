// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The measurements that decided which half of stream 21 <c>Barbatos.SecsGem.ItemTransfer</c>
/// implements.
/// </summary>
/// <remarks>
/// <para>
/// They live here rather than in that package's own suite because they are facts about the
/// reference, not about the implementation: a refresh of the catalogue data has to walk past them.
/// Every figure quoted in that package's README is asserted below.
/// </para>
/// <para>
/// Stream 21 is the first stream here to contain <strong>both kinds of gap</strong> - a vocabulary
/// the source never wrote down anywhere, and a hole in one of its own rows - so it is also where
/// the difference between them stops being theoretical.
/// </para>
/// </remarks>
public sealed class SecsCatalogStream21Tests
{
    private static SecsStreamInfo Stream21
    {
        get
        {
            Assert.True(SecsCatalog.TryGetStream(21, out var stream));
            return stream!;
        }
    }

    [Fact]
    public void Not_one_of_the_twenty_rows_is_a_definition()
    {
        var stream = Stream21;

        // So the stream is entirely representative, which is true of nine streams and is not by
        // itself a veto: stream 18 is implemented here on the same footing and stream 17 was
        // refused. What decides it is the item dictionary and the subject matter, below.
        Assert.Equal(20, stream.Messages.Count);
        Assert.Equal(20, stream.Messages.Count(message => message.IsRepresentative));
        Assert.All(stream.Messages, message => Assert.Equal(SecsMessageDirection.Either, message.Direction));
    }

    [Fact]
    public void Every_row_travels_either_way_and_that_is_why_there_is_one_end_type()
    {
        // Ten pairs, every one of them marked both ways. Stream 4 is the only other stream here
        // like it, and both packages serving them have a single type rather than a host and an
        // equipment.
        Assert.Equal(
            Enumerable.Range(1, 20).Select(number => (byte)number),
            Stream21.Messages.Select(message => message.Function));
    }

    [Fact]
    public void Seven_of_the_eighteen_item_names_are_defined_and_two_of_those_are_the_streams_own()
    {
        var anchoring = Stream21.Anchoring;

        Assert.Equal(18, anchoring.DataItemCount);

        // The identifier and the kind are stream 21's own vocabulary - no other stream names
        // either - so nothing in this repository can contradict them and nothing can confirm them.
        Assert.Equal<string[]>(["ITEMID", "ITEMTYPE"], [.. anchoring.OwnAnchors]);

        // The other five are borrowed, which is the better kind of firm ground: each is defined
        // and already served somewhere here, so a test can make the two agree.
        Assert.Equal<string[]>(
            ["ATTRDATA", "ATTRID", "DATAID", "GRANT", "LENGTH"],
            [.. anchoring.BorrowedAnchors]);
    }

    [Fact]
    public void The_two_kinds_of_gap_fall_on_the_two_halves_of_the_stream()
    {
        var anchoring = Stream21.Anchoring;

        // A shared gap is a vocabulary the source never wrote down - the rule is to leave those
        // rows alone - and both of stream 21's are block-transfer items.
        Assert.Equal<string[]>(["BLKEND", "BLKNUM"], [.. anchoring.SharedGaps]);

        // An own gap is a hole in one row, so the field is carried untouched. Nine of them, and
        // the split is what decided the chapter: ITEMDATA, ACKC21, ABTACK and DELACK sit in rows
        // whose every other item is defined, while BLKSIZE, BLKACK, XFRSTAT, XFRACK and CHKSUM sit
        // in rows that are undefined through and through.
        Assert.Equal<string[]>(
            ["ABTACK", "ACKC21", "BLKACK", "BLKSIZE", "CHKSUM", "DELACK", "ITEMDATA", "XFRACK", "XFRSTAT"],
            [.. anchoring.OwnGaps]);
    }

    [Fact]
    public void The_implemented_half_uses_every_defined_item_and_meets_four_undefined_fields()
    {
        var implemented = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 15, 16, 17, 18 };

        var named = Stream21.Messages
            .Where(message => implemented.Contains(message.Function))
            .SelectMany(message => message.DataItems)
            .ToArray();

        // Four undefined names across the six pairs, and each is a single field of a row whose
        // every other field is defined: the payload and the three acknowledgements. That is the
        // shape that makes a stream implementable - a hole you can carry rather than a row you
        // cannot read.
        Assert.Equal<string[]>(
            ["ABTACK", "ACKC21", "DELACK", "ITEMDATA"],
            [.. named.Where(name => !SecsCatalog.TryGetDataItem(name, out _)).Distinct().Order()]);

        // Eighteen of the twenty-two fields these twelve rows carry are defined - and those
        // eighteen use all seven of stream 21's defined item names, so the half left out
        // contributes no firm ground this package is missing out on.
        Assert.Equal(22, named.Length);
        Assert.Equal(18, named.Count(name => SecsCatalog.TryGetDataItem(name, out _)));

        Assert.Equal<string[]>(
            [.. Stream21.Anchoring.OwnAnchors.Concat(Stream21.Anchoring.BorrowedAnchors).Order()],
            [.. named.Where(name => SecsCatalog.TryGetDataItem(name, out _)).Distinct().Order()]);
    }

    [Fact]
    public void The_four_pairs_that_are_left_out_are_undefined_where_it_counts()
    {
        var rows = Stream21.Messages.ToDictionary(message => message.Function);

        // The blocked transfer: its size, its numbering, its end marker and its acknowledgement.
        Assert.Equal<string[]>(["ITEMID", "DATAID", "BLKSIZE"], [.. rows[9].DataItems]);
        Assert.Equal<string[]>(["GRANT", "BLKSIZE"], [.. rows[10].DataItems]);
        Assert.Equal<string[]>(["ITEMID", "BLKNUM", "BLKEND", "ITEMDATA"], [.. rows[11].DataItems]);
        Assert.Equal<string[]>(["BLKNUM", "BLKACK"], [.. rows[12].DataItems]);

        // The transfer status is a block count, so it is part of the blocked transfer and not a
        // thing that could be kept without it: two of its three items are undefined, and one of
        // those two is the block number.
        Assert.Equal<string[]>(["XFRSTAT", "BLKNUM", "LENGTH"], [.. rows[14].DataItems]);

        // And the transfer complete is a checksum, both halves undefined.
        Assert.Equal<string[]>(["ITEMID", "DATAID", "CHKSUM"], [.. rows[19].DataItems]);
        Assert.Equal<string[]>(["XFRACK"], [.. rows[20].DataItems]);
    }

    [Fact]
    public void Every_block_transfer_item_in_the_reference_is_undefined()
    {
        // Seven for seven, which is the measurement that has now kept four stream halves out of
        // this repository: S8F7 to S8F10, S13F9 and S13F10, and S21F9 to S21F14.
        foreach (var name in (string[])["BLKNUM", "BLKSIZE", "BLKACK", "BLKEND", "CHECKSUM", "CHKSUM", "CKSUM"])
        {
            Assert.False(
                SecsCatalog.TryGetDataItem(name, out _),
                $"{name} is defined after all, which would reopen a blocked transfer.");
        }
    }

    [Fact]
    public void Zero_is_acceptance_in_forty_one_of_the_forty_two_acknowledgement_items_with_a_table()
    {
        var family = SecsCatalog.DataItems
            .Where(item => item.Name.EndsWith("ACK", StringComparison.Ordinal))
            .ToArray();

        // The family measurement that lets ACKC21, ABTACK and DELACK read a zero as acceptance
        // when none of the three has a table of its own. Fifty items whose name ends in ACK, of
        // which forty-two carry code values.
        Assert.Equal(50, family.Length);

        var tabled = family.Where(item => item.Codes.Count > 0).ToArray();

        Assert.Equal(42, tabled.Length);
        Assert.Equal(41, tabled.Count(item => item.Codes.Any(code => code.Value == "0")));
    }

    [Fact]
    public void The_one_exception_is_the_item_that_names_a_bad_parameter_rather_than_a_refused_message()
    {
        Assert.True(SecsCatalog.TryGetDataItem("CPACK", out var cpack));

        // CPACK is the counter-example, and it strengthens the inference instead of weakening it:
        // its table starts at 1 because its job is to say which command parameter was wrong, and
        // a per-parameter item has no use for "all fine" - that answer is HCACK's. Nothing in
        // stream 21 has that role, so nothing in stream 21 has a reason to skip a zero.
        Assert.DoesNotContain("0", cpack!.Codes.Select(code => code.Value));
        Assert.Equal("1", cpack.Codes[0].Value);
    }

    [Fact]
    public void Stream_21_holds_three_of_the_eight_acknowledgement_items_with_no_table_at_all()
    {
        var untabled = SecsCatalog.DataItems
            .Where(item => item.Name.EndsWith("ACK", StringComparison.Ordinal) && item.Codes.Count == 0)
            .Select(item => item.Name)
            .ToArray();

        // Eight of the fifty, and three of the eight are this stream's - which is why one package
        // needed the family measurement three times over. The other five are named by messages in
        // other streams or by none.
        Assert.Equal(8, untabled.Length);
    }

    [Fact]
    public void The_grant_table_is_named_by_seven_messages_across_five_streams()
    {
        Assert.True(SecsCatalog.TryGetDataItem("GRANT", out var grant));

        Assert.Equal<byte[]>([2, 3, 4, 8, 21], [.. grant!.Streams]);
        Assert.Equal(5, grant.Codes.Count);

        var carriers = SecsCatalog.Messages
            .Where(message => message.DataItems.Contains("GRANT"))
            .Select(message => $"S{message.Stream}F{message.Function}")
            .ToArray();

        // Two of the seven are stream 21's, and the second of those is the blocked transfer's -
        // so the item that anchors this package's grant table is also the one the left-out half
        // would have used.
        Assert.Equal<string[]>(
            ["S2F2", "S2F40", "S3F16", "S4F26", "S8F2", "S21F6", "S21F10"],
            carriers);
    }

    [Fact]
    public void The_two_items_the_attributes_borrow_are_E39s_and_are_served_here_already()
    {
        Assert.True(SecsCatalog.TryGetDataItem("ATTRID", out var attrid));
        Assert.True(SecsCatalog.TryGetDataItem("ATTRDATA", out var attrdata));

        // Streams 1, 14 and 21 name both. Stream 14 is E39's, and Barbatos.SecsGem.Objects serves
        // it - which is why the S21F4 body follows S14F2's nesting rather than one stream 21's
        // own package invented. A flat item list cannot say what the nesting is, so the shape has
        // to come from the stream that owns the vocabulary.
        Assert.Equal<byte[]>([1, 14, 21], [.. attrid!.Streams]);
        Assert.Equal<byte[]>([1, 14, 21], [.. attrdata!.Streams]);
        Assert.Equal(SecsFormat.List, attrdata.Format);
    }

    [Fact]
    public void The_payload_is_named_by_this_stream_and_by_nothing_else()
    {
        // Which is the whole reason it is carried as the encoded item rather than as a chosen
        // format: stream 8 could read CPBODY off PPBODY's slot because stream 8 is stream 7's
        // design, and there is no stream 21 was copied from. The same answer stream 13's DSDATA
        // got, and for the same absence.
        Assert.False(SecsCatalog.TryGetDataItem("ITEMDATA", out _));
        Assert.False(SecsCatalog.TryGetDataItem("DSDATA", out _));

        Assert.Contains("ITEMDATA", Stream21.Anchoring.OwnGaps);
    }
}
