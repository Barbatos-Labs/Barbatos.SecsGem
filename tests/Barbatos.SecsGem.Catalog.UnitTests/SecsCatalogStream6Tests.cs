// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 6, measured because two of its rows are not what E5 is usually quoted as saying.
/// </summary>
/// <remarks>
/// <para>
/// The coverage chapter implemented S6F3 and S6F5, the last two of the reference's stream 6
/// definitions this repository had not served. Both came with a decision that only a measurement
/// can defend, so the measurements are here rather than in a comment in
/// <c>Barbatos.SecsGem.Gem</c>: a reader who doubts the body of S6F3 should be able to run
/// something.
/// </para>
/// <para>
/// <strong>S6F3 is the sharper of the two.</strong> E5 as it is usually quoted has the discrete
/// variable data send carrying values, nested under <c>DSID</c>, <c>DVNAME</c> and <c>DVVAL</c>.
/// This reference gives the row two items and neither of those two names appears in any of its 524
/// rows - so there is nowhere in it to read the nesting off, and a gap the whole reference shares
/// is a vocabulary it never wrote down. Those rows are left alone, which here means the row is
/// implemented exactly as given rather than filled in from memory.
/// </para>
/// </remarks>
public class SecsCatalogStream6Tests
{
    private static SecsStreamInfo Stream()
    {
        Assert.True(SecsCatalog.TryGetStream(6, out var stream));
        return stream!;
    }

    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    [Fact]
    public void The_reference_gives_S6F3_two_items_and_neither_is_a_value()
    {
        var row = Assert.Single(Stream().Messages, message => message.Function == 3);

        Assert.Equal("Discrete Variable Data Send", row.Name);
        Assert.False(row.IsRepresentative);
        Assert.Equal<string[]>(["DATAID", "CEID"], [.. row.DataItems]);

        // Both defined, so the row is implementable as it stands - there is no field to carry
        // untouched and nothing to leave out.
        Assert.False(Item("DATAID").IsNamedByNoMessage);
        Assert.False(Item("CEID").IsNamedByNoMessage);
    }

    [Fact]
    public void The_items_that_would_have_carried_its_values_are_named_by_no_row_at_all()
    {
        // This is the measurement that settles the body. DVNAME and DVVAL are both defined in the
        // item dictionary and both named by nothing - a shared gap, and the firmest kind: not one
        // hole in one row, but a vocabulary the source never put in a message anywhere.
        var name = Item("DVNAME");
        var value = Item("DVVAL");

        Assert.True(name.IsNamedByNoMessage);
        Assert.True(value.IsNamedByNoMessage);
        Assert.Empty(name.Streams);
        Assert.Empty(value.Streams);

        // Neither carries a table of values either, which is why they are not among the 44 the
        // sweep partitions: that set is the unused definitions that do carry one.
        Assert.False(name.IsCoded);
        Assert.False(value.IsCoded);
        Assert.DoesNotContain("DVNAME", SecsCatalog.Provenance.DetailedUnusedDefinitions);
        Assert.DoesNotContain("DVVAL", SecsCatalog.Provenance.DetailedUnusedDefinitions);
        Assert.Contains("DVNAME", SecsCatalog.Provenance.UnusedDefinitions);
        Assert.Contains("DVVAL", SecsCatalog.Provenance.UnusedDefinitions);

        // And DSID, the third name the usual nesting needs, is real here but belongs to stream 13
        // - the data set transfer. It is not stream 6 vocabulary, so borrowing it would be
        // reaching into another stream for a shape this one never asked for.
        Assert.Equal<byte[]>([13], [.. Item("DSID").Streams]);
    }

    [Fact]
    public void S6F5_is_the_only_multi_block_inquire_the_reference_documents()
    {
        // Three rows in the reference ask this question - S2F39, S4F25 and S6F5 - and they are the
        // three that name DATALENGTH. Only this one is a definition, which is why it is the only
        // one implemented here.
        var length = Item("DATALENGTH");

        Assert.Equal<byte[]>([2, 4, 6], [.. length.Streams]);

        var inquires = SecsCatalog.Streams
            .SelectMany(stream => stream.Messages)
            .Where(message => message.DataItems.Contains("DATALENGTH"))
            .ToList();

        Assert.Equal(3, inquires.Count);
        Assert.Equal(
            [(byte)6],
            [.. inquires.Where(row => !row.IsRepresentative).Select(row => row.Stream)]);

        // And the other two answer with GRANT, not GRANT6. One code's difference and one meaning's
        // - the near-miss the Gem and DataSets agreement tests both guard - so the two families
        // could not have shared an enum even if both halves had been implementable.
        Assert.Equal<string[]>(["DATAID", "DATALENGTH"], [.. inquires[^1].DataItems]);
        Assert.Equal("GRANT6", Assert.Single(Grant(6)));
        Assert.Equal("GRANT", Assert.Single(Grant(2)));
        Assert.Equal("GRANT", Assert.Single(Grant(4)));

        static IEnumerable<string> Grant(byte stream)
        {
            Assert.True(SecsCatalog.TryGetStream(stream, out var info));

            return info!.Messages
                .Where(message => message.DataItems.Any(item => item.StartsWith("GRANT", StringComparison.Ordinal))
                    && message.Function is 6 or 26 or 40)
                .SelectMany(message => message.DataItems)
                .Where(item => item.StartsWith("GRANT", StringComparison.Ordinal))
                .Distinct();
        }
    }

    [Fact]
    public void Every_stream_6_definition_is_now_served_and_the_rest_are_illustrations()
    {
        var stream = Stream();

        // Six primaries the reference documents, and all six are implemented in
        // Barbatos.SecsGem.Gem as of the coverage chapter: F1, F3, F5, F11, F15 and F19.
        var definitions = stream.Messages
            .Where(message => message.IsPrimary && !message.IsRepresentative)
            .Select(message => message.Function)
            .ToList();

        Assert.Equal<byte[]>([1, 3, 5, 11, 15, 19], [.. definitions]);

        // The stream is not entirely representative, which is what let it be served at all, and
        // its own vocabulary is substantial rather than borrowed - the other half of the question
        // the anchoring measure asks.
        Assert.False(stream.Provenance.IsEntirelyRepresentative);
        Assert.NotEmpty(stream.Anchoring.OwnAnchors);

        // S6F23, the spooled data request, is a representative row this repository serves anyway -
        // E30 specifies the spooling capability in prose, which is a second source. Recorded here
        // so the figure above is not read as "only definitions are ever implemented".
        Assert.Contains(
            stream.Messages,
            message => message.Function == 23 && message.IsRepresentative);
    }

    [Fact]
    public void Every_name_stream_6_uses_and_the_reference_never_defines_belongs_to_stream_6_alone()
    {
        var anchoring = Stream().Anchoring;

        // Four gaps, all of them the stream's own. Which matters because the two kinds are read
        // differently: a gap the whole reference shares is a vocabulary it never wrote down and
        // those rows are left alone, and a gap that belongs to one stream is a hole in a row, so
        // the field is carried untouched. Stream 6 has none of the first kind.
        Assert.Empty(anchoring.SharedGaps);
        Assert.Equal<string[]>(["CELLNAME", "NRED", "SPACK", "V"], [.. anchoring.OwnGaps]);
    }

    [Fact]
    public void The_value_item_is_carried_by_a_row_the_reference_illustrates_in_full()
    {
        var stream = Stream();

        // This is the measurement that decided four of the seven rows left in this stream, and it
        // overturned a reading made before the replies were looked at: V has no definition
        // anywhere, so the obvious conclusion is that any row carrying it cannot be served.
        Assert.False(SecsCatalog.TryGetDataItem("V", out _));

        // Except that V is carried by six rows of this stream, and one of them is a definition
        // rather than an illustration - so the reference does show what a V-bearing row looks
        // like, and the repository has been carrying it opaquely at S6F11 since before this
        // chapter. A gap cannot block a row when the reference illustrates the gap in full.
        var carrying = stream.Messages
            .Where(message => message.DataItems.Contains("V", StringComparer.Ordinal))
            .ToArray();

        Assert.Equal<byte[]>([8, 9, 11, 13, 18, 21], [.. carrying.Select(message => message.Function)]);
        Assert.Contains(carrying, message => message.Function == 11 && !message.IsRepresentative);
    }

    [Fact]
    public void The_two_gaps_that_do_block_a_row_are_each_the_only_thing_that_row_is_for()
    {
        var stream = Stream();

        // The other half of reading an own gap twice. V is a payload, so carrying it untouched
        // leaves the message usable. These two are not payloads: CELLNAME is what makes S6F21 a
        // *cell* report rather than the event report it is otherwise identical to, and NRED is the
        // enable-or-disable flag S6F29 exists to carry. Carrying either opaquely would leave this
        // library unable to tell the two things the row distinguishes.
        foreach (var (name, function) in new[] { ("CELLNAME", (byte)21), ("NRED", (byte)29) })
        {
            Assert.False(SecsCatalog.TryGetDataItem(name, out _), name);

            var rows = stream.Messages
                .Where(message => message.DataItems.Contains(name, StringComparer.Ordinal))
                .ToArray();

            Assert.Equal(function, Assert.Single(rows).Function);
        }
    }

    [Fact]
    public void The_spool_reply_names_an_item_the_reference_never_defines_and_three_names_were_in_play()
    {
        var row = Assert.Single(Stream().Messages, message => message.Function == 24);

        // S6F24 carries one item and the reference does not define it. Which would be unremarkable
        // except for what this repository had been calling it.
        Assert.Equal<string[]>(["SPACK"], [.. row.DataItems]);
        Assert.False(SecsCatalog.TryGetDataItem("SPACK", out _));

        // RSPACK is a real item with a real table, and GemHost used to read S6F24 under that name
        // and this suite used to check the enum against that table. It belongs to S2F44 - reset
        // spooling, a row nothing here serves. The same mistake TRACK was, in the same file.
        Assert.True(SecsCatalog.TryGetDataItem("RSPACK", out var rspack));
        Assert.Equal<byte[]>([2], [.. rspack!.Streams]);

        // RSDA and RSDC are what E30 calls these two fields and what this package puts on the
        // wire. Both are defined and neither is named by any of the reference's rows, which is the
        // companion measure: a capability being specified does not mean anything carries it.
        foreach (var name in new[] { "RSDA", "RSDC" })
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
            Assert.True(item!.IsNamedByNoMessage, name);
        }

        // So the enum's codes are grounded on ACKC6 instead - stream 6's own acknowledgement,
        // carried by four of its rows, with a table identical to the one that was being quoted.
        // Same codes, honest provenance: the correction changes a name and not a byte.
        var ackc6 = Item("ACKC6");

        Assert.Equal(rspack.Codes.Count, ackc6.Codes.Count);
        Assert.Equal("Accepted", ackc6.MeaningOf("0"));
        Assert.Equal("1-63", ackc6.Codes[^1].Value);
        Assert.Equal("1-63", rspack.Codes[^1].Value);
    }

    [Fact]
    public void The_rival_measure_cannot_pair_the_spool_names_and_the_reason_is_which_relations_it_has()
    {
        // A gap with a rival spelling is a field the reference wrote down under another name, and
        // reading a format off it would be correcting the reference rather than reading it. So
        // whether SPACK has a rival is a real question, and the measure says no.
        Assert.DoesNotContain(
            Stream().RivalSpellings,
            rival => rival.UsedName is "SPACK");

        // And the reason is worth recording rather than leaving to be rediscovered: the measure
        // has two textual relations, anagram and prefix in either direction. RSPACK extends SPACK
        // at the *front*, which is neither - so this pair is invisible to it by construction, not
        // by accident. It was found by asking which streams name RSPACK.
        Assert.Equal<SecsNameRelation[]>(
            [SecsNameRelation.Anagram, SecsNameRelation.DefinitionExtendsUsed, SecsNameRelation.UsedExtendsDefinition],
            [.. Enum.GetValues<SecsNameRelation>().Order()]);

        Assert.EndsWith("SPACK", "RSPACK", StringComparison.Ordinal);
        Assert.False("RSPACK".StartsWith("SPACK", StringComparison.Ordinal));
    }

    [Fact]
    public void The_reference_records_fewer_items_for_S6F16_than_for_S6F11_though_both_carry_a_report()
    {
        var stream = Stream();
        var eleven = Assert.Single(stream.Messages, message => message.Function == 11);
        var sixteen = Assert.Single(stream.Messages, message => message.Function == 16);

        // The fact that decided S6F25, and it is a fact about this whole catalogue rather than
        // about stream 6: an item list is the set of names a row's table mentions, and not a
        // description of the body. S6F11 and S6F16 both carry an event report - this repository
        // builds them from one method - and the reference records four items for one and two for
        // the other.
        Assert.Equal<string[]>(["DATAID", "CEID", "RPTID", "V"], [.. eleven.DataItems]);
        Assert.Equal<string[]>(["DATAID", "CEID"], [.. sixteen.DataItems]);

        // So a row recorded as DATAID and CEID may or may not carry reports after them, and S6F25
        // is recorded exactly that way. For a chapter that was the end of it: the two rows that
        // could settle the nesting disagreed, and a body guessed wrong is unparseable rather than
        // merely uninterpretable. That was the only refusal this repository ever made about a
        // shape instead of a field, and it was wrong.
        var twentyFive = Assert.Single(stream.Messages, message => message.Function == 25);

        Assert.Equal<string[]>(["DATAID", "CEID"], [.. twentyFive.DataItems]);
        Assert.All(twentyFive.DataItems, name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), name));

        // What breaks the tie is asking WHY S6F16's body exceeds its record, and the answer is in
        // the name of the row that asked for it. A reply to a request for an event report has to
        // carry one, which is the whole of S6F16's excess - and it is the only row of this stream
        // served carrying an RPTID its own record does not name. Nothing asks an S6F25 for
        // anything.
        var fifteen = Assert.Single(stream.Messages, message => message.Function == 15);

        Assert.Equal("Event Report Request", fifteen.Name);
        Assert.Equal((byte)16, fifteen.ReplyFunction);
        Assert.False(sixteen.IsPrimary);
        Assert.True(twentyFive.IsPrimary);

        // Three rows of this stream carry that record, and the split is the measurement. The only
        // secondary among them is the one whose served body exceeds it; the other two are
        // equipment sends, and S6F3 has been served as exactly these two items since the chapter
        // that measured DVNAME and DVVAL. So the sibling that plays the same part in its pair
        // agrees with the record, and the one that does not is explained.
        var recorded = stream.Messages
            .Where(message => message.DataItems.SequenceEqual<string>(["DATAID", "CEID"]))
            .ToArray();

        Assert.Equal<byte[]>([3, 16, 25], [.. recorded.Select(message => message.Function)]);

        Assert.Equal<byte[]>(
            [3, 25],
            [.. recorded.Where(message => message.IsPrimary).Select(message => message.Function)]);
    }

    [Fact]
    public void RPTID_is_named_by_the_notification_familys_own_data_row_and_not_by_its_send()
    {
        var stream = Stream();

        // The other half of the tie-break, and the half that rules out the third reading. RPTID is
        // the item that makes a report list a report list, and this stream names it in seven rows.
        var naming = stream.Messages
            .Where(message => message.DataItems.Contains("RPTID", StringComparer.Ordinal))
            .Select(message => message.Function)
            .ToArray();

        Assert.Equal<byte[]>([11, 13, 17, 18, 19, 20, 28], [.. naming]);

        // S6F28 is in that list, which is the point: it is S6F25's own family - the Notification
        // Report *Data* row - and the reference says it carries report identifiers. So the family
        // does name RPTID where it means one, and the reading that would have given S6F25 a list
        // of identifiers rather than a list of reports is refused by the very record that looked
        // like it supported it. The other three rows of the family name no RPTID at all.
        foreach (var function in new byte[] { 25, 26, 27 })
        {
            var row = Assert.Single(stream.Messages, message => message.Function == function);

            Assert.DoesNotContain("RPTID", row.DataItems);
        }

        // And the row that does is the reply to the request, which is the same relation S6F16 has
        // to S6F15 - a secondary may carry what its primary asked for. S6F28's record needs no
        // excess to explain, because it names the item outright.
        var twentySeven = Assert.Single(stream.Messages, message => message.Function == 27);

        Assert.Equal("Notification Report Request", twentySeven.Name);
        Assert.Equal((byte)28, twentySeven.ReplyFunction);
    }
}
