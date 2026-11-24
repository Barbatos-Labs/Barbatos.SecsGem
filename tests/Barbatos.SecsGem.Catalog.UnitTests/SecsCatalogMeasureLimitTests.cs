// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// What the measures can and cannot decide, measured once rather than per stream.
/// </summary>
/// <remarks>
/// <para>
/// Three chapters produced three new measures in a row - the rival spellings' used-definition
/// flag, the tail-divergent twin, and the question of which end writes an undefined field and
/// which end reads it - and each was applied to the one stream that produced it. This file is the
/// sweep that applied all three to every refusal left in the repository, and its findings are
/// about the measures rather than about any stream.
/// </para>
/// <para>
/// <strong>The headline is that two of the three do not scale into verdicts.</strong> The
/// tail-divergent twin pairs a used name with any definition sharing a long stem, and most of those
/// pairs are family members rather than rival spellings - so it marks a place to look and settles
/// nothing. The producer-and-reader question does decide, and it decides more than the stream it
/// came from: it is why no blocked transfer is implemented here, which had been argued from an
/// item count.
/// </para>
/// </remarks>
public class SecsCatalogMeasureLimitTests
{
    private static SecsStreamInfo Stream(byte number)
    {
        Assert.True(SecsCatalog.TryGetStream(number, out var stream), $"S{number}");
        return stream!;
    }

    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    [Fact]
    public void The_tail_divergent_twin_is_a_flag_and_not_a_reading()
    {
        // Run mechanically over every undefined name, the twin measure reports ten pairs and would
        // call a format "agreed" whenever the candidates share one. That is how it reads the
        // recipe attribute rows: RCPATTRNAME and RCPATTRVAL both have RCPATTRID and RCPATTRDATA as
        // twins, and all four names are an A[n], so the measure says the format is settled.
        foreach (var name in (string[])["RCPATTRID", "RCPATTRDATA"])
        {
            Assert.Equal("A", Item(name).FormatToken);
            Assert.True(Item(name).IsNamedByNoMessage, name);
        }

        Assert.False(SecsCatalog.TryGetDataItem("RCPATTRNAME", out _));
        Assert.False(SecsCatalog.TryGetDataItem("RCPATTRVAL", out _));

        // And the pairing is nonsense in half of those cases, which is the finding: RCPATTRVAL
        // against RCPATTRID pairs a VALUE with an IDENTIFIER, and RCPATTRNAME against RCPATTRDATA
        // pairs a NAME with DATA. A long shared stem says the two names are in one family, not
        // that either is a spelling of the other - so a twin cannot be read the way a rival can,
        // where RCPIDNEW really is RCPNEWID's letters in another order.
        //
        // The rival measure's own relations are textual and exact, which is why they can be read:
        var rename = Assert.Single(
            SecsCatalog.Provenance.RivalSpellings,
            rival => rival is { UsedName: "RCPIDNEW", DefinedName: "RCPNEWID" });

        Assert.Equal(SecsNameRelation.Anagram, rename.Relation);
        Assert.Equal(
            string.Concat("RCPIDNEW".Order()),
            string.Concat(rename.DefinedName.Order()));
    }

    [Fact]
    public void An_undefined_field_is_safe_to_carry_only_where_its_writer_reads_it_back()
    {
        var twenty = Stream(20).Messages.ToDictionary(message => message.Function);
        var fifteen = Stream(15).Messages.ToDictionary(message => message.Function);

        // Stream 20's lock is stream 15's lock, and the measure that let stream 15's in says so:
        // the machine mints a token in the reply and reads the same token back in the next
        // primary, so nothing in between needs to know what it is.
        Assert.Equal<string[]>(["RMACK", "LOCKID"], [.. twenty[22].DataItems]);
        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "LOCKID"], [.. twenty[23].DataItems]);
        Assert.Equal<string[]>(["RMACK", "RCPLOCKID"], [.. fifteen[24].DataItems]);
        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "RCPLOCKID"], [.. fifteen[25].DataItems]);

        // So the two rows do one job and the question is which is firmer - and stream 20's carries
        // one field more: an OWNERID the HOST mints and the machine has to read. That fails the
        // same test the token passes, and it is why stream 20's lock is refused at the row level
        // and not only because the stream duplicates stream 15. The same field keeps S15F49's
        // owner out.
        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "OWNERID"], [.. twenty[21].DataItems]);
        Assert.Equal<string[]>(["RCPSPEC", "RCPID"], [.. fifteen[23].DataItems]);

        foreach (var name in (string[])["LOCKID", "OWNERID", "RCPLOCKID"])
        {
            Assert.False(SecsCatalog.TryGetDataItem(name, out _), name);
        }
    }

    [Fact]
    public void Which_is_also_why_no_blocked_transfer_is_implemented_here()
    {
        // The standing refusal of every blocked transfer was argued from a count: all seven of the
        // catalogue's block-transfer items are undefined. True, and the same is true of a lock
        // token. What separates them is the direction.
        var rows = new (byte Stream, byte Function, string Name)[]
        {
            (8, 7, "BLKNUM"),
            (8, 9, "BLKNUM"),
            (13, 9, "BLKNUM"),
            (21, 9, "BLKSIZE"),
            (21, 11, "BLKNUM"),
        };

        foreach (var (stream, function, name) in rows)
        {
            var row = Assert.Single(Stream(stream).Messages, message => message.Function == function);

            Assert.Contains(name, row.DataItems);
            Assert.False(SecsCatalog.TryGetDataItem(name, out _), name);

            // Every one is in a PRIMARY, which is the point: a block number is written by the end
            // that is sending and read by the end that is receiving, so it is the one thing an
            // opaque field cannot be - a value the other end has to understand. A lock token never
            // leaves the hand that wrote it.
            Assert.True(row.IsPrimary, $"S{stream}F{function}");
        }

        // And the one blocked row whose payload travels the other way fails for the same reason
        // read backwards: S21F19's CHKSUM is a value the receiver must COMPUTE and compare, so
        // carrying it untouched is useless rather than lossless.
        var complete = Assert.Single(Stream(21).Messages, message => message.Function == 19);

        Assert.Contains("CHKSUM", complete.DataItems);
    }

    [Fact]
    public void Every_refusal_left_in_four_streams_is_one_the_item_measures_are_silent_on()
    {
        // The other end of the sweep: these rows have no undefined item at all, so nothing the
        // dictionary can say will decide them. Each is refused on a shape, a name, or what a
        // routing table claims - and those are the only three kinds of reason left anywhere in
        // the repository:
        //
        //   S6F25                shape - its item list is S6F16's, which is served carrying a
        //                        whole event report, so whether a report list follows cannot be
        //                        settled from the record
        //   S7F11, F13, F15      a family with no request and no directory anywhere, so a matrix
        //                        program could be written and never read
        //   S7F21                a name that names no action on the row's fields
        //   S7F35                shape - two readings that put different bytes out
        //   S17F9, S17F13        a routing table is a claim about the stream, and four of this
        //                        stream's seven primaries are somebody's extrapolation
        //   S20F17, S20F33       rows this repository already serves under stream 15's numbers
        //
        // Stream 7 is the extreme case and the reason the rule exists: ALL TWENTY-TWO of its
        // pairs are gap-free, so "every item in this row is defined" was true of all six of its
        // refusals and discriminated none of them.
        Assert.Equal(22, Stream(7).Messages.Count(message => message.IsPrimary));
        Assert.Equal(22, CleanPairs(7).Count());

        // Stream 6 has seven, and S6F25 is the only one of them refused - S6F27 beside it is
        // served, with the identical item list, which is why the shape had to decide.
        Assert.Equal<byte[]>([1, 3, 5, 15, 19, 25, 27], [.. CleanPairs(6)]);

        // Streams 17 and 20 have two each and all four are refused, on the two stream-level
        // reasons rather than on anything about the rows.
        Assert.Equal<byte[]>([9, 13], [.. CleanPairs(17)]);
        Assert.Equal<byte[]>([17, 33], [.. CleanPairs(20)]);

        // And stream 1's twelve, every one of which is served now that S1F9 is in: it was the
        // last gap-free row in the repository refused on an item at all.
        Assert.Equal<byte[]>([1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23], [.. CleanPairs(1)]);
    }

    private static IEnumerable<byte> CleanPairs(byte number)
    {
        var stream = Stream(number);
        var byFunction = stream.Messages.ToDictionary(message => message.Function);

        return stream.Messages
            .Where(message => message.IsPrimary
                && message.ReplyFunction is { } reply
                && byFunction.ContainsKey(reply)
                && message.DataItems.Concat(byFunction[reply].DataItems)
                    .All(name => SecsCatalog.TryGetDataItem(name, out _)))
            .Select(message => message.Function);
    }
}
