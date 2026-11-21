// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The fourth provenance measure: gaps the source wrote down under another name.
/// </summary>
/// <remarks>
/// <para>
/// The first three measures ask how much of a stream is documented, whether a definition is used
/// at all, and whose vocabulary a stream's firm ground is. This one asks the thing a chapter needs
/// to know about a <em>gap</em>: did the source name this field somewhere else? A gap with no
/// rival is a hole to carry untouched; a gap with a rival is a field named twice, where choosing a
/// spelling is correcting the reference.
/// </para>
/// <para>
/// The measure was added because the recipe chapter computed it by hand, and the result is
/// sharper than that chapter could see: <strong>every rival in the whole reference is a recipe
/// name</strong>. No other stream has one, which is why the previous nine chapters never met this
/// hazard and why the recipe region loses eighteen pairs to it.
/// </para>
/// </remarks>
public sealed class SecsCatalogRivalTests
{
    private static IReadOnlyList<SecsDataItemRival> Rivals => SecsCatalog.Provenance.RivalSpellings;

    [Fact]
    public void The_whole_reference_has_eight_rival_spellings()
    {
        // Pinned by name, because the list is short enough to read and every entry decided
        // something. A refresh of the data that adds one has to be looked at.
        Assert.Equal<string[]>(
            [
                "NEWRCPID/RCPNEWID",
                "RCPIDDST/RCPID",
                "RCPIDNEW/RCPID",
                "RCPIDNEW/RCPNEWID",
                "RCPSPECDST/RCPSPEC",
                "RCPSTATE/RCPSTAT",
                "RCPVER/RCPVERS",
                "VALACK/VLAACK",
            ],
            [.. Rivals.Select(rival => $"{rival.UsedName}/{rival.DefinedName}")]);
    }

    [Fact]
    public void Every_one_of_them_is_a_recipe_name()
    {
        // The finding, and the reason the other nine chapters never met it. Streams 15, 19 and 20
        // are the recipe region; nothing else in twenty streams spells its vocabulary twice.
        Assert.All(
            Rivals,
            rival => Assert.All(rival.Streams, stream => Assert.Contains(stream, (byte[])[15, 19, 20])));

        Assert.Equal<byte[]>(
            [15, 19, 20],
            [.. SecsCatalog.Streams.Where(s => s.RivalSpellings.Count > 0).Select(s => s.Number)]);
    }

    [Fact]
    public void Three_are_anagrams_and_those_are_the_ones_that_carry_weight()
    {
        // An anagram is hard to reach by coincidence. A prefix relation is not, which is why the
        // relation is reported rather than flattened into one list.
        Assert.Equal<string[]>(
            ["NEWRCPID/RCPNEWID", "RCPIDNEW/RCPNEWID", "VALACK/VLAACK"],
            [
                .. Rivals
                    .Where(rival => rival.Relation == SecsNameRelation.Anagram)
                    .Select(rival => $"{rival.UsedName}/{rival.DefinedName}"),
            ]);

        // And the sharpest single row in the measure: streams 15 and 20 spell one concept two
        // different ways and the dictionary spells it a third, which no message uses.
        var fifteen = Assert.Single(Rivals, r => r.UsedName == "RCPIDNEW" && r.DefinedName == "RCPNEWID");
        var twenty = Assert.Single(Rivals, r => r.UsedName == "NEWRCPID");

        Assert.Equal<byte[]>([15], [.. fifteen.Streams]);
        Assert.Equal<byte[]>([20], [.. twenty.Streams]);
        Assert.Equal("RCPNEWID", twenty.DefinedName);
        Assert.False(fifteen.IsDefinitionUsedElsewhere);
        Assert.False(twenty.IsDefinitionUsedElsewhere);
    }

    [Fact]
    public void A_definition_that_messages_also_use_is_the_worse_reading()
    {
        // Four of the eight. For these the resemblance is probably a collision rather than a
        // rewording: both names are live in the messages, so nothing suggests they mean the same
        // thing - and VALACK against VLAACK is an anagram of an item in active use.
        Assert.Equal<string[]>(
            ["RCPIDDST/RCPID", "RCPIDNEW/RCPID", "RCPSPECDST/RCPSPEC", "VALACK/VLAACK"],
            [
                .. Rivals
                    .Where(rival => rival.IsDefinitionUsedElsewhere)
                    .Select(rival => $"{rival.UsedName}/{rival.DefinedName}"),
            ]);

        // The other four name a spelling the reference defined and then did not use anywhere.
        Assert.All(
            Rivals.Where(rival => !rival.IsDefinitionUsedElsewhere),
            rival =>
            {
                Assert.True(SecsCatalog.TryGetDataItem(rival.DefinedName, out var item));
                Assert.True(item!.IsNamedByNoMessage);
            });
    }

    [Fact]
    public void One_name_can_have_two_rivals_and_both_are_reported()
    {
        // RCPIDNEW extends the live RCPID and is an anagram of the unused RCPNEWID. A measure that
        // kept only the best match would hide half of what makes the row undecidable.
        var both = Rivals.Where(rival => rival.UsedName == "RCPIDNEW").ToArray();

        Assert.Equal(2, both.Length);
        Assert.Equal<SecsNameRelation[]>(
            [SecsNameRelation.UsedExtendsDefinition, SecsNameRelation.Anagram],
            [.. both.Select(rival => rival.Relation)]);
    }

    // ------------------------------------------------------------------- the bounds

    [Fact]
    public void The_bounds_drop_a_lock_against_a_location()
    {
        // LOCKID is stream 20's and LOC is a defined U4 in active use, so this is a prefix
        // relation by the letters and nonsense by the meaning. It is excluded because LOC is three
        // characters and a stem must be four - the floor exists for this pair, and this test is
        // what stops somebody lowering it.
        Assert.Contains("LOCKID", SecsCatalog.Provenance.UndefinedDataItems);
        Assert.True(SecsCatalog.TryGetDataItem("LOC", out var location));
        Assert.False(location!.IsNamedByNoMessage);

        Assert.DoesNotContain(Rivals, rival => rival.UsedName == "LOCKID");
    }

    [Fact]
    public void The_bounds_drop_a_one_character_item_name()
    {
        // The catalogue carries an item name of one letter - a transcription artifact in a stream 6
        // row - and without the stem floor it rivals seven definitions beginning with that letter.
        // Reported as an undefined name, which is true, and given no rivals, which is the only
        // useful answer.
        Assert.Contains("V", SecsCatalog.Provenance.UndefinedDataItems);
        Assert.DoesNotContain(Rivals, rival => rival.UsedName.Length < 3);
    }

    [Fact]
    public void Three_of_the_four_bounds_cannot_fire_on_this_data_and_this_is_the_arithmetic()
    {
        // The negative controls found that removing any of these three changes nothing, so rather
        // than leave three thresholds nobody can tell are load-bearing, the reason each is idle is
        // asserted here. A refreshed reference that makes one matter fails this test and says so.
        var definitions = SecsCatalog.DataItems.Select(item => item.Name).ToArray();

        // The short names exist - two definitions of two characters and one used name of one.
        Assert.Equal<string[]>(["MF", "SV"], [.. definitions.Where(name => name.Length < 3).Order(StringComparer.Ordinal)]);
        Assert.Equal<string[]>(["V"], [.. SecsCatalog.Provenance.UndefinedDataItems.Where(name => name.Length < 3)]);

        // A prefix relation already needs a stem of four, so a name shorter than three can only
        // reach the measure as an anagram - and none of them is an anagram of anything, which is
        // what makes both Shortest floors idle.
        foreach (var name in SecsCatalog.Provenance.UndefinedDataItems.Where(name => name.Length < 3))
        {
            Assert.DoesNotContain(definitions, other => Sorted(other) == Sorted(name));
        }

        // And no prefix relation has a stem of four with an extra part longer than three, which is
        // what makes LongestSuffix idle.
        foreach (var used in SecsCatalog.Provenance.UndefinedDataItems)
        {
            foreach (var defined in definitions)
            {
                var longer = used.Length > defined.Length ? used : defined;
                var shorter = used.Length > defined.Length ? defined : used;

                Assert.False(
                    shorter.Length >= 4
                        && longer.Length - shorter.Length > 3
                        && longer.StartsWith(shorter, StringComparison.Ordinal),
                    $"{used} and {defined} are a wide prefix pair, so LongestSuffix now matters.");
            }
        }
    }

    private static string Sorted(string name) => string.Concat(name.Order());

    [Fact]
    public void Every_pair_the_recipe_chapter_relied_on_survives_the_bounds()
    {
        // A regression guard on the thresholds rather than on the data: these are the pairs that
        // decided how much of stream 15 shipped, so a tightening of the bounds that dropped one
        // would quietly reopen an argument that has been settled.
        foreach (var pair in (string[])["RCPIDNEW/RCPNEWID", "RCPSTATE/RCPSTAT", "RCPVER/RCPVERS", "VALACK/VLAACK"])
        {
            Assert.Contains(pair, Rivals.Select(rival => $"{rival.UsedName}/{rival.DefinedName}"));
        }
    }

    [Fact]
    public void A_streams_rivals_are_the_whole_tables_rivals_filtered_to_it()
    {
        // Filtered rather than recomputed, so the two views can never disagree.
        foreach (var stream in SecsCatalog.Streams)
        {
            Assert.Equal(
                [.. Rivals.Where(rival => rival.Streams.Contains(stream.Number))],
                stream.RivalSpellings);
        }
    }

    [Fact]
    public void The_recipe_streams_rival_counts_are_what_the_chapters_quoted()
    {
        Assert.Equal(7, Stream(15).RivalSpellings.Count);
        Assert.Equal("RCPSTATE", Assert.Single(Stream(19).RivalSpellings).UsedName);
        Assert.Equal(3, Stream(20).RivalSpellings.Count);

        // RCPSTATE is in all three, which is the one rival the whole region shares - and the one
        // that keeps the execution status out of every recipe package here.
        Assert.All(
            (byte[])[15, 19, 20],
            number => Assert.Contains(
                "RCPSTATE",
                Stream(number).RivalSpellings.Select(rival => rival.UsedName)));
    }

    // --------------------------------------------- definitions specified and wired to nothing

    [Fact]
    public void Forty_four_unused_definitions_carry_a_table_of_values()
    {
        var detailed = SecsCatalog.Provenance.DetailedUnusedDefinitions;

        Assert.Equal(44, detailed.Count);

        // A subset of the unused ones by construction, asserted because the two are built from
        // different expressions.
        Assert.All(detailed, name => Assert.Contains(name, SecsCatalog.Provenance.UnusedDefinitions));

        Assert.All(
            detailed,
            name =>
            {
                Assert.True(SecsCatalog.TryGetDataItem(name, out var item));
                Assert.True(item!.IsNamedByNoMessage);
                Assert.NotEmpty(item.Codes);
            });
    }

    [Fact]
    public void The_reference_defined_the_recipe_refusal_reasons_and_wired_them_to_nothing()
    {
        Assert.True(SecsCatalog.TryGetDataItem("ACKC15", out var ackc15));

        // Four codes naming why a recipe was refused - busy, invalid identifier, invalid data -
        // and no message in the reference names the item. So stream 15's rows carry RMACK instead,
        // which has one refusal and no reason, and Barbatos.SecsGem.Recipes documents that it
        // cannot say why. The reason codes exist; nothing can carry them.
        Assert.True(ackc15!.IsNamedByNoMessage);
        Assert.Equal(4, ackc15.Codes.Count);
        Assert.Contains("ACKC15", SecsCatalog.Provenance.DetailedUnusedDefinitions);

        Assert.True(SecsCatalog.TryGetDataItem("RMACK", out var rmack));
        Assert.False(rmack!.IsNamedByNoMessage);
        Assert.Equal(3, rmack.Codes.Count);
    }

    [Fact]
    public void A_whole_namespace_command_vocabulary_went_unused()
    {
        // RMNSCMD enumerates create, delete, rename and list for a namespace, and no message names
        // it - so stream 15 spends four separate pairs on three of those four and has no clean
        // pair for the rename at all. Worth pinning because it is the clearest example of what
        // this measure is for: the capability is specified, and the messages do not reach it.
        Assert.True(SecsCatalog.TryGetDataItem("RMNSCMD", out var command));

        Assert.True(command!.IsNamedByNoMessage);
        Assert.Equal(5, command.Codes.Count);
        Assert.Contains("Create namespace", command.Codes.Select(code => code.Meaning));
        Assert.Contains("Rename namespace", command.Codes.Select(code => code.Meaning));
    }

    // ------------------------------------------------------------------ what it reads like

    [Fact]
    public void A_rival_describes_itself_for_a_reader()
    {
        Assert.Equal(
            "RCPIDNEW (S15) is an anagram of RCPNEWID, which no message names",
            Assert.Single(Rivals, r => r.UsedName == "RCPIDNEW" && r.Relation == SecsNameRelation.Anagram)
                .ToString());

        Assert.Equal(
            "RCPVER (S15,S20) is the stem of RCPVERS, which no message names",
            Assert.Single(Rivals, r => r.UsedName == "RCPVER").ToString());

        Assert.Equal(
            "VALACK (S15) is an anagram of VLAACK, which messages also use",
            Assert.Single(Rivals, r => r.UsedName == "VALACK").ToString());
    }

    [Fact]
    public void A_stream_with_no_gaps_the_source_renamed_has_no_rivals()
    {
        // Seventeen of twenty streams, and the nine packages built before the recipe chapter are
        // all in here - which is why none of them had to make this judgement.
        foreach (byte number in (byte[])[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 13, 14, 16, 17, 18, 21])
        {
            Assert.Empty(Stream(number).RivalSpellings);
        }
    }

    private static SecsStreamInfo Stream(byte number)
    {
        Assert.True(SecsCatalog.TryGetStream(number, out var stream));
        return stream!;
    }
}
