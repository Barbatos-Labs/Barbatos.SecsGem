// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The seven gapless streams, and the one row of their last nine that still stays out.
/// </summary>
/// <remarks>
/// <para>
/// Three chapters have now been decided by the same pair of measures - gaps and own anchors - so
/// this file asserts the whole partition rather than one stream of it: seven streams have no gaps
/// of either kind, six have no vocabulary of their own, and no stream is both. Streams 4, 7, 12
/// and 9 were finished by earlier chapters; streams 1, 5 and 10 are finished here.
/// </para>
/// <para>
/// The nine rows left in those three were all rows the reference illustrates rather than defines,
/// and eight went in. The ninth is S1F9/F10, and the two measures that let the others in are the
/// two that keep it out.
/// </para>
/// </remarks>
public class SecsCatalogGaplessTests
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
    public void The_three_streams_finished_here_are_gapless_with_vocabularies_of_their_own()
    {
        foreach (var number in (byte[])[1, 5, 10])
        {
            var stream = Stream(number);

            Assert.Empty(stream.Anchoring.SharedGaps);
            Assert.Empty(stream.Anchoring.OwnGaps);
            Assert.NotEmpty(stream.Anchoring.OwnAnchors);
            Assert.Empty(stream.RivalSpellings);
        }

        // Named by nobody else, which is what "own" means - and the three acknowledgement items
        // among them are why these streams' refusals could be quoted rather than inferred.
        Assert.Contains("SFCD", Stream(1).Anchoring.OwnAnchors);
        Assert.Contains("ACKC5", Stream(5).Anchoring.OwnAnchors);
        Assert.Contains("ACKC10", Stream(10).Anchoring.OwnAnchors);
    }

    [Fact]
    public void Seven_streams_are_gapless_six_are_rootless_and_none_is_both()
    {
        var gapless = SecsCatalog.Streams
            .Where(stream => stream.Anchoring.SharedGaps.Count == 0 && stream.Anchoring.OwnGaps.Count == 0)
            .Select(stream => stream.Number)
            .ToList();

        var rootless = SecsCatalog.Streams
            .Where(stream => stream.Anchoring.HasNoOwnVocabulary)
            .Select(stream => stream.Number)
            .ToList();

        Assert.Equal<byte[]>([1, 4, 5, 7, 9, 10, 12], [.. gapless]);
        Assert.Equal<byte[]>([8, 15, 16, 17, 19, 20], [.. rootless]);
        Assert.Empty(gapless.Intersect(rootless));

        // The same partition SecsCatalogStream4Tests and SecsCatalogStream7Tests assert from their
        // own side. Here because it is now the measure three chapters have turned on, and a figure
        // asserted in one place drifts.
        Assert.All<byte>([15, 17, 19, 20], number => Assert.Contains(number, rootless));
    }

    [Fact]
    public void S1F9_stays_out_because_its_two_items_are_text_nobody_wrote_values_for()
    {
        var stream = Stream(1);
        var request = Assert.Single(stream.Messages, message => message.Function == 9);
        var data = Assert.Single(stream.Messages, message => message.Function == 10);

        Assert.Empty(request.DataItems);
        Assert.Equal<string[]>(["TSIP", "TSOP"], [.. data.DataItems]);

        // Both are A[n] with no table, and both are named by stream 1 alone - so there is no
        // sibling anywhere to read a reading off, which is the test that let the other eight rows
        // in. What a "transfer status input port" contains the reference never says.
        foreach (var name in (string[])["TSIP", "TSOP"])
        {
            var item = Item(name);

            Assert.Equal("A", item.FormatToken);
            Assert.Empty(item.Codes);
            Assert.Equal<byte[]>([1], [.. item.Streams]);
        }

        // And the second reason, which is independent of the first: what the row reports is
        // material transfer, and this repository serves that subject properly in stream 3 and in
        // E87. A pass-through of two string lists whose meaning nobody states would be an API with
        // nothing behind it, beside two packages that have something.
        Assert.Contains("Material Status", Stream(3).Name, StringComparison.Ordinal);
    }

    [Fact]
    public void S1F5_and_S1F7_differ_by_one_item_and_their_selector_has_no_table()
    {
        var stream = Stream(1);

        Assert.Equal<string[]>(["SFCD"], [.. Assert.Single(stream.Messages, m => m.Function == 5).DataItems]);
        Assert.Equal<string[]>(["SV"], [.. Assert.Single(stream.Messages, m => m.Function == 6).DataItems]);
        Assert.Equal<string[]>(["SFCD"], [.. Assert.Single(stream.Messages, m => m.Function == 7).DataItems]);
        Assert.Equal<string[]>(["SVNAME", "SV"], [.. Assert.Single(stream.Messages, m => m.Function == 8).DataItems]);

        // The whole difference between the two pairs is that one item, which is why one model
        // concept answers both. And SFCD has no table, so which form a code selects is the
        // machine's to document - carried as a byte, exactly as HOCMDNAME is carried as a string.
        var selector = Item("SFCD");

        Assert.Equal("B", selector.FormatToken);
        Assert.Empty(selector.Codes);

        // SV is the item S1F4 already carries, so the reply's shape is read off an implemented
        // sibling in the same stream rather than inferred.
        Assert.Contains<byte>(1, Item("SV").Streams);
        Assert.Contains("SV", Assert.Single(stream.Messages, m => m.Function == 4).DataItems);
    }

    [Fact]
    public void S1F19_is_E39s_vocabulary_and_its_reply_is_the_only_attribute_get_that_can_say_why()
    {
        var get = Assert.Single(Stream(1).Messages, message => message.Function == 19);
        var data = Assert.Single(Stream(1).Messages, message => message.Function == 20);

        Assert.Equal<string[]>(["OBJTYPE", "OBJID", "ATTRID"], [.. get.DataItems]);
        Assert.Equal<string[]>(["ATTRDATA", "ERRCODE", "ERRTEXT"], [.. data.DataItems]);

        // Every name is shared with stream 14, which is what put this row in
        // Barbatos.SecsGem.Objects rather than in Barbatos.SecsGem.Gem: its vocabulary is E39's,
        // and Gem must not depend on that package.
        Assert.All(
            get.DataItems,
            name => Assert.Contains<byte>(14, Item(name).Streams));

        Assert.Contains<byte>(14, Item("ATTRDATA").Streams);

        // And the reason it is not a duplicate of S14F1: S14F2 carries no acknowledgement at all,
        // so an object the other end has not got comes back as an empty list with no reason. This
        // reply has ERRCODE and ERRTEXT, which makes it the only attribute get in the reference
        // that can tell a caller why.
        var fourteen = Assert.Single(SecsCatalog.Streams.Single(s => s.Number == 14).Messages, m => m.Function == 2);

        Assert.Equal<string[]>(["OBJSPEC", "ATTRID", "ATTRDATA"], [.. fourteen.DataItems]);
        Assert.DoesNotContain("ERRCODE", fourteen.DataItems);
        Assert.DoesNotContain("OBJACK", fourteen.DataItems);
    }

    [Fact]
    public void S10F7_is_a_primary_with_no_reply_and_ACKC10_2_answers_a_different_question()
    {
        var stream = Stream(10);
        var notice = Assert.Single(stream.Messages, message => message.Function == 7);

        // A primary that travels either way, with nothing coming back: there is no S10F8 row in
        // the reference at all.
        Assert.True(notice.IsPrimary);
        Assert.Equal(SecsMessageDirection.Either, notice.Direction);
        Assert.Null(notice.ReplyFunction);
        Assert.DoesNotContain(stream.Messages, message => message.Function == 8);
        Assert.Equal<string[]>(["TID"], [.. notice.DataItems]);

        // The reason this row used to be left out was that "ACKC10 2 already says it". It does not:
        // code 2 is what a *reply* carries when a host asked and the terminal was not there, where
        // this row is volunteered before anybody asks. One answers a request and the other
        // prevents it.
        Assert.Equal("Terminal not available", Item("ACKC10").MeaningOf("2"));
        Assert.All(
            stream.Messages.Where(message => message.DataItems.Contains("ACKC10")),
            message => Assert.False(message.IsPrimary));
    }

    [Fact]
    public void The_exception_recovery_rows_add_one_item_to_what_S5F9_already_carries()
    {
        var stream = Stream(5);

        var posted = Assert.Single(stream.Messages, message => message.Function == 9);
        var documented = posted.DataItems.Concat(["ACKC5"]).ToHashSet(StringComparer.Ordinal);

        // S5F11, F13 and F15 name nothing S5F9 and S5F10 do not - which is the test that let them
        // in, the same one stream 7's six rows passed.
        foreach (var function in (byte[])[11, 12, 13, 14, 15, 16])
        {
            var row = Assert.Single(stream.Messages, message => message.Function == function);

            Assert.All(row.DataItems, name => Assert.Contains(name, documented));
        }

        // S5F17 and S5F18 are the exception, and the one name they add is tabled: ACKA is a
        // BOOLEAN that arrives with TRUE accepted and FALSE not, so the reading is quoted. It is
        // shared with stream 17, which is rootless and will never be served here - so stream 5 is
        // its only implementable home, the same position TRACK is in for stream 4.
        var abort = Assert.Single(stream.Messages, message => message.Function == 18);

        Assert.Equal<string[]>(["EXID", "ACKA", "ERRCODE", "ERRTEXT"], [.. abort.DataItems]);

        var acka = Item("ACKA");

        Assert.Equal("BOOLEAN", acka.FormatToken);
        Assert.Equal("Accepted", acka.MeaningOf("TRUE"));
        Assert.Equal("Not accepted", acka.MeaningOf("FALSE"));
        Assert.Equal<byte[]>([5, 17], [.. acka.Streams]);
        Assert.True(Stream(17).Anchoring.HasNoOwnVocabulary);
    }
}
