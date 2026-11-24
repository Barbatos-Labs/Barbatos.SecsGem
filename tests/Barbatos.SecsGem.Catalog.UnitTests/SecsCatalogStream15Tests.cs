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
/// The second half of this file is the finding that cost the first chapter two pairs it had
/// planned: <strong>the reference wrote down a complete recipe vocabulary that its own messages do
/// not use</strong>, and the names its messages do use are rival spellings of it.
/// </para>
/// <para>
/// <strong>And the third half is the re-reading that gave those two pairs back, with two
/// more.</strong> The rival measure records a relation and the first chapter to read it took the
/// relation for the verdict. It also records whether the definition being rivalled is one the
/// messages <em>use</em>, and four of the reference's eight rivals are that kind - a name whose
/// base is live, in the same body, two positions earlier. That is a modifier and not a
/// misspelling. The suffix statistic the refusal leaned on turns out to be measured over thirty
/// pairs of which <strong>exactly one</strong> ever shares a message body, and that one keeps its
/// format.
/// </para>
/// </remarks>
public sealed class SecsCatalogStream15Tests
{
    /// <summary>The nine pairs whose every field is defined, as primary function numbers.</summary>
    private static readonly byte[] Gapless = [1, 5, 7, 9, 13, 15, 17, 31, 53];

    /// <summary>The thirteen pairs this repository implements, as primary function numbers.</summary>
    /// <remarks>
    /// <see cref="Gapless"/> plus the rename, the copy, the lock and the unlock - four rows that
    /// carry one undefined name each and are served anyway, each on grounds a test below states.
    /// Kept as two arrays rather than one because the two are different claims: the first is about
    /// the reference and the second is about this repository, and a chapter that conflated them is
    /// what put the second four off for six chapters.
    /// </remarks>
    private static readonly byte[] Implemented = [1, 5, 7, 9, 13, 15, 17, 19, 21, 23, 25, 31, 53];

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
        Assert.Equal<byte[]>(Gapless, [.. CleanPairs(15)]);
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
            .Where(message => Gapless.Contains(message.Function)
                || Gapless.Contains((byte)(message.Function - 1)))
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
    public void The_suffix_route_is_a_coin_flip_over_pairs_that_never_meet_in_one_body()
    {
        // This is the figure the rename and the copy were refused on, and it is true: read a
        // format off a name that is this name plus a suffix and you are right about half the time.
        // The counter-example is inside the recipe vocabulary itself, where RCPBODYA is an A[n]
        // and RCPBODY is a B[n].
        Assert.True(SecsCatalog.TryGetDataItem("RCPBODY", out var body));
        Assert.True(SecsCatalog.TryGetDataItem("RCPBODYA", out var bodyA));
        Assert.Equal(SecsFormat.Binary, body!.Format);
        Assert.Equal(SecsFormat.ASCII, bodyA!.Format);

        var pairs = Suffixes();

        // Thirty pairs and thirteen keep the format, which is worse than a coin flip. The prose
        // this replaced said twenty-five and eleven; it was a count nobody had asserted, and the
        // conclusion is the same either way. Asserted here so the next chapter gets the figure
        // rather than the memory of it.
        Assert.Equal(30, pairs.Length);
        Assert.Equal(13, pairs.Count(pair => pair.Base.Format == pair.Extended.Format));

        // And here is why the figure does not reach stream 15's three. Of those thirty pairs,
        // exactly ONE is ever named by a single message - SV and SVNAME, in S1F8 - so the
        // statistic is drawn from names that never stand beside each other, where a format has to
        // be guessed from the dictionary rather than read from the row. The one pair that does
        // stand together keeps its format.
        var together = pairs
            .Where(pair => SecsCatalog.Messages.Any(message =>
                message.DataItems.Contains(pair.Base.Name) && message.DataItems.Contains(pair.Extended.Name)))
            .ToArray();

        var only = Assert.Single(together);

        Assert.Equal("SV", only.Base.Name);
        Assert.Equal("SVNAME", only.Extended.Name);
        Assert.Equal(only.Base.Format, only.Extended.Format);
    }

    [Fact]
    public void The_rival_measure_records_two_relations_and_the_first_reading_took_one_for_a_verdict()
    {
        var rivals = SecsCatalog.Provenance.RivalSpellings;

        // Eight in the whole reference, and they split evenly on a question the measure answers
        // and the chapter that refused these rows did not ask: is the definition being rivalled
        // one that the messages THEMSELVES use?
        Assert.Equal(8, rivals.Count);
        Assert.Equal(4, rivals.Count(rival => rival.IsDefinitionUsedElsewhere));

        // All four of the live ones are stream 15's. Three extend a name that is in the same body,
        // which is this chapter's finding; the fourth is VALACK, an anagram of VLAACK, whose
        // definition is live in ANOTHER stream - S2F46, with a table of its own. That one is the
        // opposite situation and is why S15F43/F44 stays out: a true table belonging to a row
        // nothing here serves, which is the trap CLAUDE.md records twice already.
        Assert.Equal<string[]>(
            ["RCPIDDST=RCPID", "RCPIDNEW=RCPID", "RCPSPECDST=RCPSPEC", "VALACK=VLAACK"],
            [
                .. rivals
                    .Where(rival => rival.IsDefinitionUsedElsewhere)
                    .Select(rival => $"{rival.UsedName}={rival.DefinedName}")
                    .Order(StringComparer.Ordinal),
            ]);

        Assert.True(SecsCatalog.TryGetDataItem("VLAACK", out var vlaack));
        Assert.False(vlaack!.IsNamedByNoMessage);
        Assert.Equal(2, vlaack.Codes.Count);
        Assert.Equal<byte[]>([2], [.. vlaack.Streams]);
    }

    [Fact]
    public void In_three_of_them_the_name_it_extends_is_in_the_same_body()
    {
        var stream = Stream(15);

        foreach (var (function, used, basis) in new[]
        {
            ((byte)19, "RCPIDNEW", "RCPID"),
            ((byte)21, "RCPSPECDST", "RCPSPEC"),
            ((byte)21, "RCPIDDST", "RCPID"),
        })
        {
            var items = Assert.Single(stream.Messages, message => message.Function == function)
                .DataItems
                .ToList();

            // The base comes first and the modified name after it, so the format is read off an
            // item in the row rather than out of the dictionary. That is the difference between
            // this and the thirty suffix pairs: nothing has to be guessed, because the thing the
            // name is a variant of is right there carrying a value.
            Assert.Contains(basis, items);
            Assert.True(items.IndexOf(basis) < items.IndexOf(used), $"{basis} before {used}");
        }

        // The copy's two are two apart rather than one, because its body is the source pair and
        // then the destination pair - the same RCPSPEC-then-RCPID order the four plain rows use.
        var copy = Assert.Single(stream.Messages, message => message.Function == 21).DataItems;

        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "RCPSPECDST", "RCPIDDST"], [.. copy]);
    }

    [Fact]
    public void And_the_renames_two_readings_are_the_same_format_so_the_rivalry_puts_no_byte_in_doubt()
    {
        // S15F19's gap is the only name in the reference with both relations at once: it extends
        // the live RCPID and it is an exact anagram of the unused RCPNEWID. The refusal was argued
        // from the second - a library choosing a spelling would be correcting the source - and the
        // premise is true. What does not follow is that anything changes on the wire.
        Assert.Equal(2, SecsCatalog.Provenance.RivalSpellings.Count(rival => rival.UsedName == "RCPIDNEW"));

        foreach (var name in (string[])["RCPID", "RCPNEWID"])
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
            Assert.Equal(SecsFormat.ASCII, item!.Format);
            Assert.Equal("n", item.Length);
        }

        // Both readings are an A[n], and a SECS-II body is positional - the names are
        // documentation and never travel. So the worst a wrong spelling can do here is mislabel a
        // comment, where RCPOWNER below would mislabel bytes.
        Assert.True(SecsCatalog.TryGetDataItem("RCPOWCODE", out var ownerCode));
        Assert.Equal(SecsFormat.Boolean, ownerCode!.Format);
    }

    // --------------------------------------------- the blind spot the measure has, and its ten

    [Fact]
    public void The_measure_cannot_see_a_twin_that_diverges_at_the_tail_and_there_are_ten()
    {
        // CLAUDE.md records one blind spot in this measure: a definition that extends a used name
        // at the FRONT - SPACK against RSPACK - which neither of its two relations catches. This
        // is the other, and it is what keeps seven of stream 15's pairs out now that the rival
        // measure has stopped keeping four of them out. A used name and a definition that share a
        // long stem and then diverge are not anagrams and neither is a prefix of the other, so the
        // measure reports nothing at all.
        var reported = new HashSet<string>(
            SecsCatalog.Provenance.RivalSpellings.Select(rival => $"{rival.UsedName}|{rival.DefinedName}"),
            StringComparer.Ordinal);

        var twins = SecsCatalog.Provenance.UndefinedDataItems
            .SelectMany(used => SecsCatalog.DataItems
                .Where(defined => Stem(used, defined.Name) >= 5
                    && Stem(used, defined.Name) < Math.Min(used.Length, defined.Name.Length)
                    && !reported.Contains($"{used}|{defined.Name}"))
                .Select(defined => $"{used}~{defined.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal<string[]>(
            [
                "CARRIERSTATUS~CARRIERACTION",
                "CARRIERSTATUS~CARRIERID",
                "CARRIERSTATUS~CARRIERSPEC",
                "RCPATTRNAME~RCPATTRDATA",
                "RCPATTRNAME~RCPATTRID",
                "RCPATTRVAL~RCPATTRDATA",
                "RCPATTRVAL~RCPATTRID",
                "RCPOWNER~RCPOWCODE",
                "RCPREV~RCPRENAME",
                "RCPVER2~RCPVERS",
            ],
            twins);

        // Seven of the ten are recipe names, the same concentration the anagram measure found. The
        // other three are CARRIERSTATUS, whose row Barbatos.SecsGem.Carriers serves by CHOOSING a
        // meaning and saying so in its README - which is allowed because nobody disagrees about
        // the FORMAT of a state name. It is text on every reading.
        Assert.Equal(7, twins.Count(twin => twin.StartsWith("RCP", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_tail_divergent_twin_blocks_a_row_when_the_two_readings_are_different_formats()
    {
        // Which is the line between the four pairs that went in and the six the twins keep out.
        // S15F49's RCPOWNER is either an owner's name - text, like every identifier in this
        // stream - or an owner code, since RCPOWCODE is a BOOLEAN. The host builds that primary
        // and the machine has to read it, so the end that would choose is not the end that parses.
        Assert.False(SecsCatalog.TryGetDataItem("RCPOWNER", out _));
        Assert.True(SecsCatalog.TryGetDataItem("RCPOWCODE", out var ownerCode));
        Assert.Equal(SecsFormat.Boolean, ownerCode!.Format);

        // The attribute rows are worse still. RCPATTRDATA is an A[n]; E39's ATTRDATA - which this
        // repository DOES serve, through Barbatos.SecsGem.Objects on stream 14 - is a list, which
        // is to say any format at all. So an attribute value is text on one reading and whatever
        // arrived on the other, and nine rows of stream 15 - seven of its pairs - carry one.
        Assert.True(SecsCatalog.TryGetDataItem("RCPATTRDATA", out var recipeAttribute));
        Assert.Equal(SecsFormat.ASCII, recipeAttribute!.Format);

        Assert.True(SecsCatalog.TryGetDataItem("ATTRDATA", out var objectAttribute));
        Assert.Equal(SecsFormat.List, objectAttribute!.Format);

        var carrying = Stream(15).Messages
            .Where(message => message.DataItems.Contains("RCPATTRNAME") || message.DataItems.Contains("RCPATTRVAL"))
            .Select(message => message.Function)
            .Order();

        Assert.Equal<byte[]>([4, 27, 28, 29, 44, 46, 48, 51, 52], [.. carrying]);
    }

    [Fact]
    public void RCPLOCKID_is_a_spelling_of_nothing_which_is_why_it_is_carried_as_the_item_that_arrived()
    {
        Assert.False(SecsCatalog.TryGetDataItem("RCPLOCKID", out _));

        // No rival of either relation...
        Assert.DoesNotContain(SecsCatalog.Provenance.RivalSpellings, rival => rival.UsedName == "RCPLOCKID");

        // ...and no tail-divergent twin either: the longest stem it shares with any definition in
        // the reference is RCP itself, which all twenty-four recipe definitions share and which
        // therefore distinguishes nothing. So there is no format to read and none is read - the
        // lock token goes back out as the item it came in, which is stream 13's answer and not
        // stream 8's.
        Assert.Equal(3, SecsCatalog.DataItems.Max(item => Stem("RCPLOCKID", item.Name)));
    }

    [Fact]
    public void The_locks_two_bodies_are_rows_this_package_already_serves_and_the_current_recipes_is_not()
    {
        var rows = Stream(15).Messages.ToDictionary(message => message.Function);

        // S15F24 is S15F10's body - a code and then a payload the dictionary does not define - and
        // S15F25 is S15F7's. Both siblings are served here, so the nesting comes from a row this
        // package already reads and writes, which is the only place a nesting can come from: the
        // catalogue's item list is a name set and not a body, as the stream 6 chapter established.
        Assert.Equal<string[]>(["RMACK", "PPBODY"], [.. rows[10].DataItems]);
        Assert.Equal<string[]>(["RMACK", "RCPLOCKID"], [.. rows[24].DataItems]);
        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "PPBODY"], [.. rows[7].DataItems]);
        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "RCPLOCKID"], [.. rows[25].DataItems]);

        Assert.Equal<string[]>(["RCPID", "RCPVER"], [.. rows[36].DataItems]);

        // S15F36 has no sibling, which is why the current recipe stays out on its SHAPE rather
        // than on RCPVER: nothing this package serves answers a request with a multi-item body
        // that carries no acknowledgement, so there is no way to read "nothing is selected" and no
        // row to read a nesting off. A body guessed wrong is unparseable rather than merely
        // uninterpretable.
        Assert.DoesNotContain(
            Implemented.Select(function => rows[(byte)(function + 1)]),
            reply => reply.DataItems.Count > 1 && !reply.DataItems.Contains("RMACK"));
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
        // there. Five of this package's thirteen pairs - the copy, the lock, the unlock, the
        // select and the operation abort - are outside it altogether, where it was two of nine
        // before the rename and the copy went in.
        Assert.Equal(20, nineteen.Count);
        Assert.Equal(54, fifteen.Count);
        Assert.Equal<byte[]>([21, 23, 25, 31, 53], [.. Implemented.Where(function => function > 20)]);
    }

    private static string Sorted(string name) => string.Concat(name.Order());

    /// <summary>How many leading characters two names share.</summary>
    private static int Stem(string left, string right)
    {
        var shared = 0;

        while (shared < left.Length && shared < right.Length && left[shared] == right[shared])
        {
            shared++;
        }

        return shared;
    }

    /// <summary>Every pair of definitions where one name is the other plus a suffix.</summary>
    private static (SecsDataItemInfo Base, SecsDataItemInfo Extended)[] Suffixes()
        =>
        [
            .. SecsCatalog.DataItems
                .SelectMany(left => SecsCatalog.DataItems
                    .Where(right => right.Name.Length > left.Name.Length
                        && right.Name.StartsWith(left.Name, StringComparison.Ordinal))
                    .Select(right => (Base: left, Extended: right))),
        ];

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
