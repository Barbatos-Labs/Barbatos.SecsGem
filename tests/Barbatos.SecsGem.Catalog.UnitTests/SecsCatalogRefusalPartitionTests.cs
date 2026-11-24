// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Gem;

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Every primary this repository does not serve, and which measure decides each one.
/// </summary>
/// <remarks>
/// <para>
/// Fifty-nine of the reference's two hundred and seventy-four primaries are not declared anywhere
/// here, and after seven chapters of re-reading them one at a time the list had never been written
/// down in one place. <strong>This file is that list, and it is a partition</strong>: every refused
/// primary appears exactly once, under the one measure that decides it. A row with two reasons is
/// filed under the one that would still refuse it if the other were answered - which is the lesson
/// of the refusals that fell, because in every one of those the weaker reason was doing the work.
/// </para>
/// <para>
/// The point of pinning it is that four of the seven measures have overturned a refusal, and three
/// have been shown not to scale. A later chapter re-reading one of these rows should find its
/// reason here rather than re-deriving it, and should find the figures measured rather than
/// remembered: the per-stream lists below come out of the catalogue and the routing tables, not out
/// of anybody's notes.
/// </para>
/// <para>
/// <strong>What the partition shows at a glance.</strong> Twenty-five of the fifty-nine are refused
/// although every field of them reads - twenty-four for what declaring their stream would claim and
/// one for a name whose object appears nowhere else - and the other thirty-four carry a field this
/// library cannot honestly put on a wire. So the frontier is not "rows nobody has got to"; it is
/// two kinds of claim, and neither is about effort.
/// </para>
/// </remarks>
public class SecsCatalogRefusalPartitionTests
{
    /// <summary>The one measure that decides a refusal.</summary>
    private enum Reason
    {
        /// <summary>The undefined name is what the message is for, so carrying it opaquely leaves the row unreadable.</summary>
        FieldTheRowTurnsOn,

        /// <summary>The undefined field is written by one end and must be understood by the other.</summary>
        ProducerAndReader,

        /// <summary>A rival or a twin gives the name a second reading, and the two put different bytes out.</summary>
        ReadingsDiffer,

        /// <summary>The body's nesting is settled by no served sibling - a list of entries, here.</summary>
        Shape,

        /// <summary>The name's object has no second instance anywhere, and the reply is only an acknowledgement.</summary>
        NameAlone,

        /// <summary>Every field reads; what keeps the row out is what declaring its stream would claim.</summary>
        StreamClaim,

        /// <summary>One undefined name addresses every transaction of the stream.</summary>
        AddressingMissing,
    }

    /// <summary>A refused primary, and the name or the word that decides it.</summary>
    private sealed record Refusal(byte Stream, byte Function, Reason Reason, string Decides);

    private static readonly Refusal[] Refused =
    [
        // ---------------------------------------------------------- the field the row turns on
        new(2, 19, Reason.FieldTheRowTurnsOn, "RSTC"),
        new(6, 21, Reason.FieldTheRowTurnsOn, "CELLNAME"),
        new(6, 29, Reason.FieldTheRowTurnsOn, "NRED"),
        new(14, 23, Reason.FieldTheRowTurnsOn, "ENABLE"),
        new(15, 33, Reason.FieldTheRowTurnsOn, "RCPSTATE"),
        new(15, 37, Reason.FieldTheRowTurnsOn, "RCPEXIST"),
        new(15, 39, Reason.FieldTheRowTurnsOn, "SPACEAVAIL"),
        new(15, 41, Reason.FieldTheRowTurnsOn, "AUTOSEL"),
        new(15, 45, Reason.FieldTheRowTurnsOn, "CMPRESULT"),

        // ---------------------------------------------------------- producer and reader
        new(8, 7, Reason.ProducerAndReader, "BLKNUM"),
        new(8, 9, Reason.ProducerAndReader, "BLKNUM"),
        new(13, 9, Reason.ProducerAndReader, "BLKNUM"),
        new(15, 11, Reason.ProducerAndReader, "CHECKSUM"),
        new(15, 49, Reason.ProducerAndReader, "RCPOWNER"),
        new(21, 9, Reason.ProducerAndReader, "BLKSIZE"),
        new(21, 11, Reason.ProducerAndReader, "BLKNUM"),
        new(21, 13, Reason.ProducerAndReader, "BLKNUM"),
        new(21, 19, Reason.ProducerAndReader, "CHKSUM"),

        // ---------------------------------------------------------- two readings, different bytes
        new(15, 27, Reason.ReadingsDiffer, "RCPATTRVAL"),
        new(15, 29, Reason.ReadingsDiffer, "RCPATTRVAL"),
        new(15, 43, Reason.ReadingsDiffer, "VALACK"),
        new(15, 51, Reason.ReadingsDiffer, "RCPATTRVAL"),

        // ---------------------------------------------------------- a shape
        new(15, 3, Reason.Shape, "a directory is a list of entries"),
        new(15, 47, Reason.Shape, "a history is a list of entries"),

        // ---------------------------------------------------------- a name with no second instance
        new(7, 21, Reason.NameAlone, "Reference"),

        // ---------------------------------------------------------- what declaring the stream claims
        new(17, 1, Reason.StreamClaim, "stream 17"),
        new(17, 3, Reason.StreamClaim, "stream 17"),
        new(17, 5, Reason.StreamClaim, "stream 17"),
        new(17, 7, Reason.StreamClaim, "stream 17"),
        new(17, 9, Reason.StreamClaim, "stream 17"),
        new(17, 11, Reason.StreamClaim, "stream 17"),
        new(17, 13, Reason.StreamClaim, "stream 17"),
        new(20, 1, Reason.StreamClaim, "stream 20"),
        new(20, 3, Reason.StreamClaim, "stream 20"),
        new(20, 5, Reason.StreamClaim, "stream 20"),
        new(20, 7, Reason.StreamClaim, "stream 20"),
        new(20, 9, Reason.StreamClaim, "stream 20"),
        new(20, 11, Reason.StreamClaim, "stream 20"),
        new(20, 13, Reason.StreamClaim, "stream 20"),
        new(20, 15, Reason.StreamClaim, "stream 20"),
        new(20, 17, Reason.StreamClaim, "stream 20"),
        new(20, 19, Reason.StreamClaim, "stream 20"),
        new(20, 21, Reason.StreamClaim, "stream 20"),
        new(20, 23, Reason.StreamClaim, "stream 20"),
        new(20, 25, Reason.StreamClaim, "stream 20"),
        new(20, 27, Reason.StreamClaim, "stream 20"),
        new(20, 29, Reason.StreamClaim, "stream 20"),
        new(20, 31, Reason.StreamClaim, "stream 20"),
        new(20, 33, Reason.StreamClaim, "stream 20"),

        // ---------------------------------------------------------- the addressing is missing
        new(19, 1, Reason.AddressingMissing, "RCPNS"),
        new(19, 3, Reason.AddressingMissing, "RCPNS"),
        new(19, 5, Reason.AddressingMissing, "RCPNS"),
        new(19, 7, Reason.AddressingMissing, "RCPNS"),
        new(19, 9, Reason.AddressingMissing, "RCPNS"),
        new(19, 11, Reason.AddressingMissing, "RCPNS"),
        new(19, 13, Reason.AddressingMissing, "RCPNS"),
        new(19, 15, Reason.AddressingMissing, "RCPNS"),
        new(19, 17, Reason.AddressingMissing, "RCPNS"),
        new(19, 19, Reason.AddressingMissing, "RCPNS"),
    ];

    [Fact]
    public void Fifty_nine_of_the_references_primaries_are_not_served_and_each_is_here_once()
    {
        // The arithmetic first, measured off the catalogue rather than carried forward: the
        // reference has 274 primaries and this repository declares 215 of them.
        var primaries = SecsCatalog.Messages.Count(message => message.IsPrimary);

        Assert.Equal(274, primaries);
        Assert.Equal(59, Refused.Length);
        Assert.Equal(215, primaries - Refused.Length);

        // Exactly once each, which is what makes this a partition rather than a list of notes.
        Assert.Equal(
            Refused.Length,
            Refused.Select(refusal => (refusal.Stream, refusal.Function)).Distinct().Count());

        // And every one of them is a real row with a real reply, so a renumbering of the data
        // cannot leave an entry here pointing at nothing.
        foreach (var refusal in Refused)
        {
            Assert.True(
                SecsCatalog.TryGetMessage(refusal.Stream, refusal.Function, out var row),
                $"S{refusal.Stream}F{refusal.Function}");

            Assert.True(row!.IsPrimary);
            Assert.Equal((byte)(refusal.Function + 1), row.ReplyFunction);
            Assert.True(SecsCatalog.TryGetMessage(refusal.Stream, (byte)(refusal.Function + 1), out _));
        }
    }

    [Fact]
    public void The_fifty_nine_fall_in_eleven_streams_and_four_of_them_are_whole()
    {
        // Per stream, pinned. The four streams nothing here serves at all are 17, 19 and 20 - and
        // stream 19 and 20 are the two the reference calls Recipe Management beside stream 15.
        Assert.Equal<byte[]>(
            [2, 6, 7, 8, 13, 14, 15, 17, 19, 20, 21],
            [.. Refused.Select(refusal => refusal.Stream).Distinct().Order()]);

        foreach (var (stream, functions) in new (byte Stream, byte[] Functions)[]
        {
            (2, [19]),
            (6, [21, 29]),
            (7, [21]),
            (8, [7, 9]),
            (13, [9]),
            (14, [23]),
            (15, [3, 11, 27, 29, 33, 37, 39, 41, 43, 45, 47, 49, 51]),
            (17, [1, 3, 5, 7, 9, 11, 13]),
            (19, [1, 3, 5, 7, 9, 11, 13, 15, 17, 19]),
            (20, [1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27, 29, 31, 33]),
            (21, [9, 11, 13, 19]),
        })
        {
            Assert.Equal<byte[]>(
                functions,
                [.. Refused.Where(refusal => refusal.Stream == stream).Select(refusal => refusal.Function).Order()]);
        }

        // And the other nine streams of the twenty are served whole, which is the figure worth
        // keeping beside the fifty-nine: the refusals are in eleven streams, not spread thin.
        Assert.Equal(20, SecsCatalog.Streams.Count);
        Assert.Equal(9, SecsCatalog.Streams.Count(stream => !Refused.Any(r => r.Stream == stream.Number)));

        // Streams 17, 19 and 20 are refused whole, which is the only place a routing table decides
        // anything: there is no row of them to declare without declaring the stream.
        foreach (byte stream in (byte[])[17, 19, 20])
        {
            Assert.True(SecsCatalog.TryGetStream(stream, out var info));

            Assert.Equal(
                info!.Messages.Count(message => message.IsPrimary),
                Refused.Count(refusal => refusal.Stream == stream));
        }
    }

    [Fact]
    public void Twenty_five_of_them_carry_no_field_this_library_cannot_read()
    {
        // The division the partition exists to show. Twenty-four are refused for what declaring
        // their stream would claim and one for a name, and in none of the twenty-five is the
        // objection about a field at all.
        var readable = Refused
            .Where(refusal => refusal.Reason is Reason.StreamClaim or Reason.NameAlone)
            .ToArray();

        Assert.Equal(25, readable.Length);
        Assert.Equal(24, readable.Count(refusal => refusal.Reason == Reason.StreamClaim));

        // The other thirty-four carry a field this library cannot honestly put on a wire, and the
        // four measures that decide them are the four kinds above.
        Assert.Equal(34, Refused.Length - readable.Length);

        foreach (var (reason, count) in new (Reason Reason, int Count)[]
        {
            (Reason.FieldTheRowTurnsOn, 9),
            (Reason.ProducerAndReader, 9),
            (Reason.ReadingsDiffer, 4),
            (Reason.Shape, 2),
            (Reason.NameAlone, 1),
            (Reason.StreamClaim, 24),
            (Reason.AddressingMissing, 10),
        })
        {
            Assert.Equal(count, Refused.Count(refusal => refusal.Reason == reason));
        }
    }

    [Fact]
    public void Every_name_an_item_measure_decides_on_is_undefined_and_carried_by_the_row_it_decides()
    {
        // What makes each entry checkable rather than a label. For the four item measures the
        // deciding name has to be a name the reference never defines, and it has to be in the row
        // it is cited for - either in the primary or in the reply, since a reply's gap refuses the
        // pair just as a primary's does.
        foreach (var refusal in Refused.Where(refusal =>
            refusal.Reason is Reason.FieldTheRowTurnsOn
                or Reason.ProducerAndReader
                or Reason.ReadingsDiffer
                or Reason.AddressingMissing))
        {
            Assert.False(
                SecsCatalog.TryGetDataItem(refusal.Decides, out _),
                $"{refusal.Decides} is defined, so S{refusal.Stream}F{refusal.Function} cannot be refused for it");

            Assert.True(SecsCatalog.TryGetMessage(refusal.Stream, refusal.Function, out var primary));
            Assert.True(SecsCatalog.TryGetMessage(refusal.Stream, (byte)(refusal.Function + 1), out var reply));

            Assert.True(
                primary!.DataItems.Contains(refusal.Decides, StringComparer.Ordinal)
                    || reply!.DataItems.Contains(refusal.Decides, StringComparer.Ordinal),
                $"S{refusal.Stream}F{refusal.Function} does not carry {refusal.Decides}");
        }
    }

    [Fact]
    public void And_the_rows_refused_on_a_stream_claim_include_five_with_no_gap_at_all()
    {
        // Which is the whole force of that kind: these rows could be served tomorrow on their
        // items, and what stops them is that declaring them tells a host this repository speaks a
        // stream it would answer S9F5 for almost everywhere. S20F17 and S20F33 are the delete and
        // the namespace create stream 15 already serves; S17F9 carries S2F23's five items in
        // S2F23's order, and S17F13 is the trace reset that can say why it refused.
        var gapFree = Refused
            .Where(refusal => refusal.Reason == Reason.StreamClaim)
            .Where(refusal =>
                SecsCatalog.TryGetMessage(refusal.Stream, refusal.Function, out var primary)
                && SecsCatalog.TryGetMessage(refusal.Stream, (byte)(refusal.Function + 1), out var reply)
                && primary!.DataItems.Concat(reply!.DataItems).All(name => SecsCatalog.TryGetDataItem(name, out _)))
            .Select(refusal => $"S{refusal.Stream}F{refusal.Function}")
            .ToArray();

        Assert.Equal<string[]>(["S17F9", "S17F13", "S20F17", "S20F33"], gapFree);

        // And a fifth that is readable without being gap-free, which is the finding of the second
        // stream 20 re-read: NEWRCPSPEC and NEWRCPID are each a defined name with a word in front,
        // sitting in the same body two positions earlier.
        Assert.Contains(Refused, refusal => refusal is { Stream: 20, Function: 15, Reason: Reason.StreamClaim });
    }

    [Fact]
    public void Nothing_in_the_one_package_this_project_can_see_declares_any_of_them()
    {
        // The other half of "not served" is asserted package by package - each one has a test that
        // its own leave-outs are unrouted. This project can see Barbatos.SecsGem.Gem, so the four
        // rows of GEM's own streams get the cross-check here rather than on trust.
        List<(byte Stream, byte Function)> declared =
        [
            .. GemEquipment.HandledMessages,
            .. GemEquipment.ProcessProgramMessages,
            .. GemEquipment.FormattedProcessProgramMessages,
            .. GemEquipment.ProcessProgramMatrixMessages,
            .. GemEquipment.SpoolMessages,
            .. GemEquipment.TerminalMessages,
            .. GemEquipment.ServiceProgramMessages,
            .. GemEquipment.TraceMessages,
            .. GemEquipment.LimitMessages,
            .. GemHost.HandledMessages,
        ];

        foreach (var refusal in Refused)
        {
            Assert.DoesNotContain((refusal.Stream, refusal.Function), declared);
        }

        // And the rows that are GEM's own subject matter, named so a sweep can see which four the
        // cross-check actually bites on.
        Assert.Equal<string[]>(
            ["S2F19", "S6F21", "S6F29", "S7F21"],
            [
                .. Refused
                    .Where(refusal => refusal.Stream is 2 or 6 or 7)
                    .Select(refusal => $"S{refusal.Stream}F{refusal.Function}"),
            ]);
    }
}
