// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 14's single gap, and the two rows it does not touch.
/// </summary>
/// <remarks>
/// <para>
/// The gapless streams are finished, so this is the first chapter measuring a stream that has a
/// gap - and stream 14's is the smallest of any stream with work left: <strong>one name</strong>.
/// That makes it the clean case for the rule the earlier chapters only stated: an own gap is a hole
/// in one row, so the rows it does not touch are unaffected by it.
/// </para>
/// <para>
/// Three rows were left. The gap is <c>ENABLE</c> and it is in S14F23, so S14F11 and S14F25 went
/// in and S14F23 did not.
/// </para>
/// </remarks>
public class SecsCatalogStream14Tests
{
    private static SecsStreamInfo Stream()
    {
        Assert.True(SecsCatalog.TryGetStream(14, out var stream));
        return stream!;
    }

    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    [Fact]
    public void The_stream_has_exactly_one_gap_and_it_is_its_own()
    {
        var stream = Stream();

        // One undefined name in twenty-eight rows, and no other stream with work left comes close:
        // stream 2 has seven, stream 21 eleven. An *own* gap rather than a shared one, which is
        // what makes it a hole in one row instead of a vocabulary to leave alone.
        Assert.Equal<string[]>(["ENABLE"], [.. stream.Provenance.UndefinedDataItems]);
        Assert.Equal<string[]>(["ENABLE"], [.. stream.Anchoring.OwnGaps]);
        Assert.Empty(stream.Anchoring.SharedGaps);
        Assert.Empty(stream.RivalSpellings);

        // And the gap is named by stream 14 alone, which is the definition of "own" - there is no
        // other stream to have written it down.
        Assert.False(SecsCatalog.TryGetDataItem("ENABLE", out _));
        Assert.Contains("ENABLE", SecsCatalog.Provenance.UndefinedDataItems);
    }

    [Fact]
    public void S14F23_stays_out_because_the_gap_is_the_field_the_message_turns_on()
    {
        var request = Assert.Single(Stream().Messages, message => message.Function == 23);

        Assert.Equal("Object Attribute Enable Request", request.Name);
        Assert.Equal<string[]>(["OBJSPEC", "OBJTYPE", "ENABLE", "ATTRID"], [.. request.DataItems]);

        // An own gap is normally carried untouched - that is the rule, and three packages here keep
        // it. It does not apply when the undefined field is the one the message is *for*: ENABLE is
        // the enable-or-disable flag, so carrying it opaquely would leave this library unable to
        // tell an enable from a disable. The same reason stream 21's S21F19 stays out on CHKSUM.
        //
        // And this gap is worse than that one. ENABLE has no dictionary entry at all, so there is
        // no format either - implementing the row would mean inventing what it is as well as what
        // it means.
        Assert.False(SecsCatalog.TryGetDataItem("ENABLE", out _));

        // The three items beside it are all defined and all already served here, which is what
        // makes the refusal about that one name rather than about the row.
        Assert.All(
            request.DataItems.Where(name => !string.Equals(name, "ENABLE", StringComparison.Ordinal)),
            name => Assert.False(Item(name).IsNamedByNoMessage));
    }

    [Fact]
    public void S14F11_went_in_because_ATTRRELN_arrives_with_its_table()
    {
        var stream = Stream();
        var request = Assert.Single(stream.Messages, message => message.Function == 11);
        var data = Assert.Single(stream.Messages, message => message.Function == 12);

        Assert.Equal<string[]>(["OBJTYPE", "ATTRID", "ATTRRELN", "ATTRDATA"], [.. request.DataItems]);
        Assert.Equal<string[]>(["OBJSPEC"], [.. data.DataItems]);

        // Eight relations, quoted. The operator is the verb of the query, so a table for it is the
        // difference between a message that can be served and one that cannot - and unlike
        // ENABLE's silence, this one is complete.
        var relation = Item("ATTRRELN");

        Assert.Equal("U1", relation.FormatToken);
        Assert.Equal(8, relation.Codes.Count);
        Assert.Equal("Equal to", relation.MeaningOf("0"));
        Assert.Equal("Not equal to", relation.MeaningOf("1"));
        Assert.Equal("Less than", relation.MeaningOf("2"));
        Assert.Equal("Less than or equal to", relation.MeaningOf("3"));
        Assert.Equal("Greater than", relation.MeaningOf("4"));
        Assert.Equal("Greater than or equal to", relation.MeaningOf("5"));
        Assert.Equal("Present", relation.MeaningOf("6"));
        Assert.Equal("Absent", relation.MeaningOf("7"));

        // No reserved range, so there is no catch-all to fold onto - the same position COMMACK is
        // in, and the reason an unnamed relation is a refusal here rather than an Other.
        Assert.DoesNotContain(relation.Codes, code => code.Value.Contains('-', StringComparison.Ordinal));
        Assert.Equal<byte[]>([14], [.. relation.Streams]);

        // The reply is a filtered instance namelist: one item, the same one S14F16 carries. So its
        // shape is read off an implemented row in the same stream.
        Assert.Equal<string[]>(
            ["OBJSPEC"],
            [.. Assert.Single(stream.Messages, message => message.Function == 16).DataItems]);
    }

    [Fact]
    public void S14F25_went_in_because_its_reply_is_a_pair_this_repository_already_reads()
    {
        var stream = Stream();
        var request = Assert.Single(stream.Messages, message => message.Function == 25);
        var data = Assert.Single(stream.Messages, message => message.Function == 26);

        Assert.Equal<string[]>(["OBJSPEC"], [.. request.DataItems]);
        Assert.Equal<string[]>(["ERRCODE", "ERRTEXT"], [.. data.DataItems]);

        // The same pair S1F20 carries, and the pair this repository has settled on passing through
        // untouched: both defined, neither tabled, so what a code means is between two ends. One
        // type reads it on both rows, because one item means one thing.
        Assert.Empty(Item("ERRCODE").Codes);
        Assert.Empty(Item("ERRTEXT").Codes);
        Assert.Contains<byte>(1, Item("ERRCODE").Streams);
        Assert.Contains<byte>(14, Item("ERRCODE").Streams);
    }

    [Fact]
    public void Every_stream_14_primary_but_one_is_served_and_the_stream_is_now_the_best_covered()
    {
        var primaries = Stream().Messages
            .Where(message => message.IsPrimary)
            .Select(message => message.Function)
            .ToList();

        Assert.Equal<byte[]>([1, 3, 5, 7, 9, 11, 13, 15, 17, 19, 21, 23, 25, 27], [.. primaries]);

        // Thirteen of fourteen. Stream 14 is also the stream where provenance says least and the
        // dictionary most: only S14F1 is a row the reference documents, and the other twenty-seven
        // rows are illustrations - yet every item but one is defined, which is the pattern every
        // chapter since stream 15 has learned to read.
        var documented = Stream().Messages.Where(message => !message.IsRepresentative).ToList();

        Assert.Equal<byte[]>([1], [.. documented.Select(message => message.Function)]);
        Assert.Equal(27, Stream().Provenance.RepresentativeCount);
        Assert.False(Stream().Provenance.IsEntirelyRepresentative);
    }
}
