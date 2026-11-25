// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The third provenance measure: where a stream's firm ground comes from.
/// </summary>
/// <remarks>
/// Half of this file is about the measure and half is the evidence for one refusal, because the
/// measure was added to make that refusal checkable rather than argued. A chapter that says "this
/// stream cannot be implemented from this reference" should leave behind something a later reader
/// can re-run.
/// </remarks>
public sealed class SecsCatalogAnchoringTests
{
    // ------------------------------------------------------------------ the measure

    [Fact]
    public void Every_stream_s_four_buckets_are_exactly_the_names_it_uses()
    {
        foreach (var stream in SecsCatalog.Streams)
        {
            var expected = stream.Messages
                .SelectMany(message => message.DataItems)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var actual = stream.Anchoring.OwnAnchors
                .Concat(stream.Anchoring.BorrowedAnchors)
                .Concat(stream.Anchoring.SharedGaps)
                .Concat(stream.Anchoring.OwnGaps)
                .Order(StringComparer.Ordinal)
                .ToArray();

            // A partition, not a selection: every name lands in exactly one bucket, so the four
            // counts can be read as a breakdown rather than as four unrelated figures.
            Assert.Equal(expected, actual);
            Assert.Equal(expected.Length, stream.Anchoring.DataItemCount);
            Assert.Equal(stream.Number, stream.Anchoring.Stream);
        }
    }

    [Fact]
    public void An_anchor_is_defined_and_a_gap_is_not()
    {
        foreach (var stream in SecsCatalog.Streams)
        {
            foreach (var name in stream.Anchoring.OwnAnchors.Concat(stream.Anchoring.BorrowedAnchors))
            {
                Assert.True(SecsCatalog.TryGetDataItem(name, out _), $"{name} is an anchor and should be defined.");
            }

            foreach (var name in stream.Anchoring.SharedGaps.Concat(stream.Anchoring.OwnGaps))
            {
                Assert.False(SecsCatalog.TryGetDataItem(name, out _), $"{name} is a gap and should not be defined.");
            }
        }
    }

    [Fact]
    public void Own_means_this_stream_alone_and_borrowed_or_shared_means_more_than_one()
    {
        foreach (var stream in SecsCatalog.Streams)
        {
            foreach (var name in stream.Anchoring.OwnAnchors.Concat(stream.Anchoring.OwnGaps))
            {
                Assert.Equal<byte[]>([stream.Number], [.. StreamsNaming(name)]);
            }

            foreach (var name in stream.Anchoring.BorrowedAnchors.Concat(stream.Anchoring.SharedGaps))
            {
                Assert.True(
                    StreamsNaming(name).Count > 1,
                    $"{name} is shared and should be named by more than one stream.");
            }
        }
    }

    [Fact]
    public void The_gaps_are_exactly_the_stream_s_undefined_items()
    {
        foreach (var stream in SecsCatalog.Streams)
        {
            // The new measure splits what the old one listed, so the two had better agree - and
            // the split is the useful part: a gap other streams share is the source's habit, and
            // one this stream has alone has no other stream and no definition behind it - which is
            // not the same as nothing to read it against, as RCPNS turned out to show.
            Assert.Equal(
                stream.Provenance.UndefinedDataItems.Order(StringComparer.Ordinal),
                stream.Anchoring.SharedGaps.Concat(stream.Anchoring.OwnGaps).Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void Every_list_is_ordered()
    {
        foreach (var stream in SecsCatalog.Streams)
        {
            Assert.Equal(stream.Anchoring.OwnAnchors.Order(StringComparer.Ordinal), stream.Anchoring.OwnAnchors);
            Assert.Equal(stream.Anchoring.BorrowedAnchors.Order(StringComparer.Ordinal), stream.Anchoring.BorrowedAnchors);
            Assert.Equal(stream.Anchoring.SharedGaps.Order(StringComparer.Ordinal), stream.Anchoring.SharedGaps);
            Assert.Equal(stream.Anchoring.OwnGaps.Order(StringComparer.Ordinal), stream.Anchoring.OwnGaps);
        }
    }

    [Fact]
    public void An_item_knows_which_streams_name_it()
    {
        foreach (var item in SecsCatalog.DataItems)
        {
            // Derived independently from the message table rather than from the same pass that
            // built the property.
            Assert.Equal(StreamsNaming(item.Name), item.Streams);
            Assert.Equal(item.Streams.Order(), item.Streams);
        }
    }

    [Fact]
    public void An_item_no_stream_names_is_exactly_an_item_no_message_names()
    {
        foreach (var item in SecsCatalog.DataItems)
        {
            // Two properties, one fact, and asserted rather than assumed: the list being empty and
            // the flag being set have to mean the same thing or one of them is lying.
            Assert.Equal(item.IsNamedByNoMessage, item.Streams.Count == 0);
        }

        Assert.Equal(
            SecsCatalog.Provenance.UnusedDefinitions.Count,
            SecsCatalog.DataItems.Count(item => item.Streams.Count == 0));
    }

    [Fact]
    public void The_measure_tells_a_systematic_gap_from_a_hole_in_one_row()
    {
        var dataSets = Stream(13);

        // The distinction two chapters needed. BLKNUM is undefined everywhere it appears, so it is
        // a vocabulary the source never wrote down and the blocked transfers stay out; DSDATA is
        // undefined in one stream only, so it is a hole in a row and the payload is carried
        // untouched. Same word in the old measure, different answer in this one.
        Assert.Contains("BLKNUM", dataSets.Anchoring.SharedGaps);
        Assert.Contains("DSDATA", dataSets.Anchoring.OwnGaps);

        Assert.Contains("BLKNUM", Stream(8).Anchoring.SharedGaps);
    }

    [Fact]
    public void The_measure_tells_a_stream_s_own_vocabulary_from_a_borrowed_one()
    {
        // Stream 12 brought its own words and almost nothing else's; stream 17 brought nothing of
        // its own at all, which is what an extrapolation looks like counted.
        Assert.Equal(27, Stream(12).Anchoring.OwnAnchors.Count);
        Assert.Single(Stream(12).Anchoring.BorrowedAnchors);

        Assert.Empty(Stream(17).Anchoring.OwnAnchors);
        Assert.Equal(14, Stream(17).Anchoring.BorrowedAnchors.Count);
    }

    [Fact]
    public void Six_streams_have_no_vocabulary_of_their_own_and_two_of_them_ship()
    {
        var bare = SecsCatalog.Streams
            .Where(stream => stream.Anchoring.HasNoOwnVocabulary)
            .Select(stream => stream.Number)
            .ToArray();

        Assert.Equal<byte[]>([8, 15, 16, 17, 19, 20], bare);

        // And the caveat, pinned: having no vocabulary of your own is not a verdict. Stream 8 is
        // implemented here because it is stream 7's design, and stream 16 because E40 defines the
        // behaviour its rows carry.
        Assert.Contains((byte)8, bare);
        Assert.Contains((byte)16, bare);
    }

    // ------------------------------------------------- the evidence for one refusal

    [Fact]
    public void Two_streams_share_a_name_and_they_are_the_two_recipe_streams()
    {
        var clash = Assert.Single(SecsCatalog.Provenance.StreamNameClashes);

        Assert.Equal("Recipe Management", clash.Name);
        Assert.Equal<byte[]>([15, 19], [.. clash.Streams]);
        Assert.Equal("\"Recipe Management\" is S15 and S19", clash.ToString());
    }

    [Fact]
    public void The_two_streams_that_share_a_name_claim_the_same_function_numbers()
    {
        var fifteen = Stream(15).Messages.Select(message => message.Function).ToHashSet();
        var nineteen = Stream(19).Messages.Select(message => message.Function).ToHashSet();

        // All twenty of stream 19's function numbers are also stream 15's. On its own that is
        // ordinary - S1F3 and S2F3 are unrelated too - and the next test is why it is not
        // ordinary here.
        Assert.Equal(20, nineteen.Count);
        Assert.True(nineteen.IsSubsetOf(fifteen));
    }

    [Fact]
    public void The_same_function_number_is_a_delete_in_one_and_a_verify_in_the_other()
    {
        // This was recorded as the sharpest reason this repository had found for leaving a stream
        // alone, and the one line of it about harm rather than tidiness. It is still the sharpest
        // hazard here and it is no longer a reason: the hazard is a router that matches on the
        // function number alone, which RecipeHostingTests pins shut, and a correct one cannot be
        // made to do it by anything in the reference - the stream number is in every header.
        // Stream 19's rows are refused per row in SecsCatalogRefusalPartitionTests, and four of
        // them because this repository answers the same transaction under stream 15's numbers.
        //
        // Which is the other half of the same fact, counted differently: the matches below are
        // names that fall on the SAME function number in both streams - two primaries and their
        // replies - and four more primaries share a name across different numbers, the upload, the
        // verify, the delete and the validate. All six are what gave RCPNS a format, since the
        // defined RCPSPEC stands in its slot in every one of them.
        Assert.Equal("Recipe Delete Request", SecsCatalog.NameOf(19, 11));
        Assert.Equal("Recipe Verify Request", SecsCatalog.NameOf(15, 11));

        Assert.Equal("Recipe Verify Request", SecsCatalog.NameOf(19, 9));
        Assert.Equal("Recipe Upload Request", SecsCatalog.NameOf(15, 9));

        // Four of the twenty carry the identical name - the directory pair and the download send
        // pair - which is what makes the rest dangerous rather than merely different: the two
        // streams look interchangeable exactly where anybody would check first.
        var identical = Enumerable.Range(1, 20)
            .Select(function => (byte)function)
            .Where(function => SecsCatalog.NameOf(19, function) == SecsCatalog.NameOf(15, function))
            .ToArray();

        Assert.Equal<byte[]>([3, 4, 7, 8], identical);

        // And two more are the same transaction under a name one word apart, which is worse than a
        // clean difference: S19F1 is "Recipe Namespace List Request" and S15F1 is "Recipe Namespace
        // Request".
        Assert.StartsWith("Recipe Namespace", SecsCatalog.NameOf(19, 1), StringComparison.Ordinal);
        Assert.StartsWith("Recipe Namespace", SecsCatalog.NameOf(15, 1), StringComparison.Ordinal);
        Assert.NotEqual(SecsCatalog.NameOf(19, 1), SecsCatalog.NameOf(15, 1));
    }

    [Fact]
    public void The_recipe_family_has_not_one_documented_row()
    {
        byte[] family = [15, 19, 20];

        var rows = family.Sum(number => Stream(number).Provenance.MessageCount);
        var defined = family.Sum(number => Stream(number).Provenance.DefinedCount);

        Assert.Equal(108, rows);
        Assert.Equal(0, defined);

        foreach (var number in family)
        {
            Assert.True(Stream(number).Provenance.IsEntirelyRepresentative);
        }
    }

    [Fact]
    public void The_recipe_vocabulary_is_split_between_two_namings_and_the_dictionary_picked_one()
    {
        // Four roles, two sets of names. In the only case where the dictionary commits to a
        // definition it commits to streams 15 and 20's name.
        //
        // That used to end "- which is why a library that implemented stream 19 would be choosing
        // the undefined vocabulary over the defined one", and a later chapter struck the clause.
        // Item names never travel: a SECS-II body is positional, so the two spellings put the same
        // bytes on a wire and choosing one mislabels a comment. Worse, the fact read the other way
        // is what RETIRED stream 19's refusal - RCPSPEC being defined is exactly what gives RCPNS
        // a format, since the two stand in the same slot in six identically named rows.
        // SecsCatalogRefusalPartitionTests has that, and this test keeps the four roles.
        Assert.True(SecsCatalog.TryGetDataItem("RCPSPEC", out var specification));
        Assert.Equal<byte[]>([15, 20], [.. specification!.Streams]);

        Assert.False(SecsCatalog.TryGetDataItem("RCPNS", out _));
        Assert.Equal<byte[]>([19], [.. StreamsNaming("RCPNS")]);

        // The other three roles have no definition on either side.
        foreach (var (fifteen, nineteen) in new[]
        {
            ("RCPVER", "RCPREV"),
            ("RCPATTRNAME", "RCPPNAME"),
            ("RCPATTRVAL", "RCPPVAL"),
        })
        {
            Assert.False(SecsCatalog.TryGetDataItem(fifteen, out _), $"{fifteen} should be undefined.");
            Assert.False(SecsCatalog.TryGetDataItem(nineteen, out _), $"{nineteen} should be undefined.");
            Assert.DoesNotContain((byte)19, StreamsNaming(fifteen));
            Assert.Equal<byte[]>([19], [.. StreamsNaming(nineteen)]);
        }
    }

    [Fact]
    public void Stream_19_has_no_anchor_of_its_own_and_every_one_it_has_is_somebody_else_s()
    {
        var anchoring = Stream(19).Anchoring;

        Assert.Empty(anchoring.OwnAnchors);

        // Five borrowed anchors, and not one of them a recipe item: a length, a timestamp, a
        // process program body, an identifier shared with the other two recipe streams, and the
        // acknowledgement shared with them as well.
        Assert.Equal<string[]>(["LENGTH", "PPBODY", "RCPID", "RMACK", "TIME"], [.. anchoring.BorrowedAnchors]);

        // And five of its six gaps are its own, which is the opposite of stream 13's one.
        Assert.Equal<string[]>(["CKSUM", "RCPNS", "RCPPNAME", "RCPPVAL", "RCPREV"], [.. anchoring.OwnGaps]);
        Assert.Equal<string[]>(["RCPSTATE"], [.. anchoring.SharedGaps]);
    }

    [Fact]
    public void The_recipe_body_is_the_one_thing_the_family_did_not_have_to_invent()
    {
        // Worth recording because it is the one piece of good news, and because it is why the
        // refusal is about the vocabulary rather than about the payload: all three recipe streams
        // carry a recipe as PPBODY, which stream 7 defines as a B.
        Assert.True(SecsCatalog.TryGetDataItem("PPBODY", out var body));
        Assert.Equal(SecsFormat.Binary, body!.Format);
        Assert.Equal<byte[]>([7, 15, 19, 20], [.. body.Streams]);
    }

    // ---------------------------------------------------------------- the pieces

    private static SecsStreamInfo Stream(byte number)
        => SecsCatalog.Streams.Single(stream => stream.Number == number);

    private static IReadOnlyList<byte> StreamsNaming(string name)
        => [.. SecsCatalog.Messages
            .Where(message => message.DataItems.Contains(name, StringComparer.Ordinal))
            .Select(message => message.Stream)
            .Distinct()
            .Order()];
}
