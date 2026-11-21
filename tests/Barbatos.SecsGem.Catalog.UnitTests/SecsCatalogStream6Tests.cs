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
}
