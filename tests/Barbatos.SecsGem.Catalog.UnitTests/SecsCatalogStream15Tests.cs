// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// The measurements that decided how much of stream 15 <c>Barbatos.SecsGem.Recipes</c> implements -
/// and, for the first time here, measurements that <em>overturned</em> a refusal and ones that
/// refused what the chapter had expected to build.
/// </summary>
/// <remarks>
/// <para>
/// Stream 15 was swept into chapter 42's refusal of stream 19 on a figure the two share: 108 rows
/// across streams 15, 19 and 20, not one of them a definition. That figure is
/// <see cref="SecsStreamProvenance"/>, which the working agreement says is not a veto - stream 21
/// has the same zero and ships. The measure that decides is the item dictionary, and on that the
/// three streams are nothing like each other.
/// </para>
/// <para>
/// The second half of this file is the finding that cost the chapter two pairs it had planned:
/// <strong>the reference wrote down a complete recipe vocabulary that its own messages do not
/// use</strong>, and the names its messages do use are rival spellings of it.
/// </para>
/// </remarks>
public sealed class SecsCatalogStream15Tests
{
    /// <summary>The nine pairs this repository implements, as primary function numbers.</summary>
    private static readonly byte[] Implemented = [1, 5, 7, 9, 13, 15, 17, 31, 53];

    private static SecsStreamInfo Stream(byte number)
    {
        Assert.True(SecsCatalog.TryGetStream(number, out var stream));
        return stream!;
    }

    // ------------------------------------------------ what put stream 15 ahead of the other three

    [Fact]
    public void Stream_15_has_nine_pairs_that_need_no_inference_where_stream_19_has_none()
    {
        // The measure that reopened the refusal. A pair needs no inference when every field of the
        // primary and every field of its reply is defined - and nine is three times what stream 21
        // shipped on.
        Assert.Equal<byte[]>(Implemented, [.. CleanPairs(15)]);
        Assert.Empty(CleanPairs(19));

        // Stream 20 has two and stream 17 has two, so stream 15 is not marginally ahead of the
        // field - it has more than four times the next best.
        Assert.Equal<byte[]>([17, 33], [.. CleanPairs(20)]);
        Assert.Equal<byte[]>([9, 13], [.. CleanPairs(17)]);
    }

    [Fact]
    public void Stream_17s_only_readable_pairs_are_a_trace_which_stream_2_already_does()
    {
        var rows = Stream(17).Messages.ToDictionary(message => message.Function);

        // Worth recording beside stream 15, because it is why stream 17's refusal stands while
        // stream 15's did not: its two readable pairs duplicate GEM's trace, and everything else
        // is control jobs, whose CJID, CJSTATE and CJACK are undefined and whose capability
        // Barbatos.SecsGem.ControlJobs already serves through stream 14.
        Assert.Equal("Trace Request", rows[9].Name);
        Assert.Equal("Trace Reset Request", rows[13].Name);

        foreach (var name in (string[])["CJID", "CJSTATE", "CJACK", "DCID"])
        {
            Assert.False(SecsCatalog.TryGetDataItem(name, out _), name);
        }
    }

    [Fact]
    public void Stream_15_runs_one_way()
    {
        var stream = Stream(15);

        // All twenty-seven primaries are the host's and all twenty-seven replies the equipment's,
        // which makes this the third one-way stream here after 8 and 13 - so the host end declares
        // no route and the equipment end takes no connection.
        Assert.All(
            stream.Messages.Where(message => message.IsPrimary),
            message => Assert.Equal(SecsMessageDirection.HostToEquipment, message.Direction));

        Assert.All(
            stream.Messages.Where(message => !message.IsPrimary),
            message => Assert.Equal(SecsMessageDirection.EquipmentToHost, message.Direction));
    }

    [Fact]
    public void The_nine_pairs_lean_on_five_items_and_two_of_those_are_anchored_in_documented_rows()
    {
        var named = Stream(15).Messages
            .Where(message => Implemented.Contains(message.Function)
                || Implemented.Contains((byte)(message.Function - 1)))
            .SelectMany(message => message.DataItems)
            .Distinct()
            .Order(StringComparer.Ordinal);

        Assert.Equal<string[]>(["LENGTH", "PPBODY", "RCPID", "RCPSPEC", "RMACK"], [.. named]);

        // LENGTH and PPBODY sit in rows the reference documented - four of PPBODY's fifteen
        // carriers are definitions, all of them stream 7's - so the recipe body is not read off a
        // slot the way stream 8's CPBODY was. It IS stream 7's item, in a row whose siblings are
        // definitions. That is the firmest footing any payload has had in this repository.
        Assert.Equal(4, SecsCatalog.Messages.Count(m => m.DataItems.Contains("PPBODY") && !m.IsRepresentative));
        Assert.Equal(3, SecsCatalog.Messages.Count(m => m.DataItems.Contains("LENGTH") && !m.IsRepresentative));
    }

    [Fact]
    public void The_acknowledgement_has_a_quoted_table_with_a_reserved_range()
    {
        Assert.True(SecsCatalog.TryGetDataItem("RMACK", out var rmack));

        // Unlike every acknowledgement the last three chapters met, this one is defined. So the
        // package quotes three codes instead of reading a zero off forty-one siblings - and it
        // gets an Other member honestly, which ItemAcknowledgement could not.
        Assert.Equal(SecsFormat.Binary, rmack!.Format);
        Assert.Equal(3, rmack.Codes.Count);
        Assert.Equal<string[]>(["0", "1", "2-63"], [.. rmack.Codes.Select(code => code.Value)]);
        Assert.Equal<byte[]>([15, 19, 20], [.. rmack.Streams]);
    }

    // ------------------------------------------- the vocabulary the reference does not use

    [Fact]
    public void Twenty_two_of_the_twenty_four_recipe_definitions_are_named_by_no_message()
    {
        var recipeItems = SecsCatalog.DataItems
            .Where(item => item.Name.StartsWith("RCP", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(24, recipeItems.Length);

        // Only RCPID and RCPSPEC are used at all. The reference defined a whole recipe vocabulary -
        // with formats, and in two cases with code tables - and then wrote its messages against
        // other names. That is the second provenance measure applied to a standard rather than to
        // one item, and it is a far larger instance than the LOCID example the Catalog README uses.
        Assert.Equal<string[]>(
            ["RCPID", "RCPSPEC"],
            [.. recipeItems.Where(item => !item.IsNamedByNoMessage).Select(item => item.Name).Order(StringComparer.Ordinal)]);

        Assert.Equal(22, recipeItems.Count(item => item.IsNamedByNoMessage));
    }

    [Fact]
    public void The_whole_reference_has_three_anagram_relations_and_the_recipe_streams_own_two()
    {
        var defined = SecsCatalog.DataItems.ToArray();

        var pairs = SecsCatalog.Streams
            .SelectMany(stream => stream.Messages)
            .SelectMany(message => message.DataItems)
            .Distinct()
            .Where(name => !SecsCatalog.TryGetDataItem(name, out _))
            .SelectMany(name => defined
                .Where(item => Sorted(item.Name) == Sorted(name))
                .Select(item => $"{name}={item.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Seventy-four names are used by a message and defined nowhere. Three of those are an
        // exact anagram of something the dictionary does define, and two of the three are recipe
        // names - the same concept spelled one way by stream 15, another by stream 20, and a third
        // by the definition neither of them uses.
        Assert.Equal<string[]>(
            ["NEWRCPID=RCPNEWID", "RCPIDNEW=RCPNEWID", "VALACK=VLAACK"],
            pairs);
    }

    [Fact]
    public void That_is_why_the_rename_and_copy_pairs_stay_out()
    {
        // The chapter expected to carry these two on a suffix route - read RCPIDNEW off RCPID and
        // RCPSPECDST off RCPSPEC, the way stream 8 read CPID off PPID's slot. The measurement
        // refused it, and this test is the refusal.
        Assert.True(SecsCatalog.TryGetDataItem("RCPNEWID", out var defined));
        Assert.False(SecsCatalog.TryGetDataItem("RCPIDNEW", out _));

        // The name S15F19 uses is undefined; the dictionary's own name for what is plainly the
        // same field is defined, as an A[n] - and used by no message. So reading a format off
        // RCPID would not be filling a hole, it would be choosing between two spellings of one
        // item while a rival spelling sits in the dictionary unused. Get it backwards and a rename
        // writes a recipe under a name nobody asked for.
        Assert.True(defined!.IsNamedByNoMessage);

        // And the suffix route is weak even where no rival exists: of the twenty-five pairs where
        // the dictionary defines both a name and that name plus a suffix, only eleven keep the
        // format. Worse than a coin flip - and the counter-example is inside this very vocabulary,
        // where RCPBODYA is an A[n] and RCPBODY is a B[n].
        Assert.True(SecsCatalog.TryGetDataItem("RCPBODY", out var body));
        Assert.True(SecsCatalog.TryGetDataItem("RCPBODYA", out var bodyA));
        Assert.Equal(SecsFormat.Binary, body!.Format);
        Assert.Equal(SecsFormat.ASCII, bodyA!.Format);
    }

    [Fact]
    public void The_rows_that_stay_out_are_the_rows_that_reach_for_a_rival_spelling()
    {
        var stream = Stream(15);

        // The division is clean, and it is the chapter's whole argument: every row this package
        // implements uses only the five items above, and every row it leaves out reaches for a
        // name that is undefined. Not one of the nine needs a name the dictionary spells
        // differently.
        var holes = stream.Messages
            .Where(message => !Implemented.Contains(message.Function)
                && !Implemented.Contains((byte)(message.Function - 1)))
            .SelectMany(message => message.DataItems)
            .Distinct()
            .Where(name => !SecsCatalog.TryGetDataItem(name, out _))
            .Order(StringComparer.Ordinal);

        Assert.Equal<string[]>(
            [
                "AUTOSEL", "CHECKSUM", "CMPRESULT", "RCPATTRNAME", "RCPATTRVAL", "RCPEXIST",
                "RCPIDDST", "RCPIDNEW", "RCPLOCKID", "RCPOWNER", "RCPSPECDST", "RCPSTATE",
                "RCPVER", "RCPVER2", "SPACEAVAIL", "SPACETOTAL", "VALACK", "VFACK",
            ],
            [.. holes]);

        // Three of those eighteen have a defined near-twin the messages never use: RCPSTATE for
        // RCPSTAT, RCPVER for RCPVERS, RCPIDNEW for RCPNEWID. A fourth, VALACK, is an anagram of
        // VLAACK, which messages DO use - so that one is not even a spelling the reference
        // abandoned, it is a collision with a live item.
        Assert.True(SecsCatalog.TryGetDataItem("RCPSTAT", out _));
        Assert.True(SecsCatalog.TryGetDataItem("RCPVERS", out _));
        Assert.True(SecsCatalog.TryGetDataItem("VLAACK", out var vlaack));
        Assert.False(vlaack!.IsNamedByNoMessage);
    }

    [Fact]
    public void The_name_clash_with_stream_19_is_real_and_is_not_a_wire_hazard()
    {
        var clash = Assert.Single(SecsCatalog.Provenance.StreamNameClashes);

        Assert.Equal("Recipe Management", clash.Name);
        Assert.Equal<byte[]>([15, 19], [.. clash.Streams]);

        // What chapter 42 refused stream 19 for, stated precisely so the two halves are not
        // confused. The hazard is that a reader picks the wrong stream NUMBER from a tool's
        // manual, not that a message is ambiguous in flight: S15F11 verifies and S19F11 deletes,
        // and they are different messages on the wire.
        var fifteen = Stream(15).Messages.ToDictionary(message => message.Function);
        var nineteen = Stream(19).Messages.ToDictionary(message => message.Function);

        Assert.Equal("Recipe Verify Request", fifteen[11].Name);
        Assert.Equal("Recipe Delete Request", nineteen[11].Name);

        // And the contested range is only the first twenty functions, because stream 19 stops
        // there. Two of this package's nine pairs - select and operation abort - are outside it
        // altogether.
        Assert.Equal(20, nineteen.Count);
        Assert.Equal(54, fifteen.Count);
        Assert.Equal<byte[]>([31, 53], [.. Implemented.Where(function => function > 20)]);
    }

    private static string Sorted(string name) => string.Concat(name.Order());

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
