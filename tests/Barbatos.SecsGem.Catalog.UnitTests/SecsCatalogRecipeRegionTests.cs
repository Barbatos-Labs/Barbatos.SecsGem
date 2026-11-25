// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Streams 19 and 20, re-read once every other stream had been served or argued.
/// </summary>
/// <remarks>
/// <para>
/// Both were refused as <em>rootless</em> - no vocabulary of their own - and that was true and was
/// not the reason. Provenance is not a verdict: the same measure swept stream 15 into the same
/// refusal and the item dictionary overturned it, so these two were owed the same re-read. Neither
/// survives it with rows to serve, but both stated reasons have been replaced, and the new ones are
/// about particular names rather than about a figure.
/// </para>
/// <para>
/// <strong>Three streams here are called Recipe Management</strong> - 15, 19 and 20 - and they are
/// not three parts of one standard. They use the same function numbers for different transactions,
/// spell one concept three ways, and overlap exactly twice. What follows is each of those measured.
/// </para>
/// </remarks>
public class SecsCatalogRecipeRegionTests
{
    private static SecsStreamInfo Stream(byte number)
    {
        Assert.True(SecsCatalog.TryGetStream(number, out var stream), $"stream {number}");
        return stream!;
    }

    private static bool IsDefined(string name) => SecsCatalog.TryGetDataItem(name, out _);

    /// <summary>Every primary of a stream whose own items and whose reply's items are all defined.</summary>
    private static byte[] Servable(byte number)
    {
        var stream = Stream(number);
        var byFunction = stream.Messages.ToDictionary(message => message.Function);

        return
        [
            .. stream.Messages
                .Where(message => message.Function % 2 == 1)
                .Where(message => message.DataItems.All(IsDefined))
                .Where(message =>
                    !byFunction.TryGetValue((byte)(message.Function + 1), out var reply)
                    || reply.DataItems.All(IsDefined))
                .Select(message => message.Function),
        ];
    }

    [Fact]
    public void Stream_19_has_no_row_whose_items_are_all_defined()
    {
        // The measure that let nine of stream 15's twenty-seven pairs in, applied here. Stream 19
        // has none, which the recipe chapter claimed and nothing had checked.
        Assert.Empty(Servable(19));
    }

    [Fact]
    public void And_the_one_name_that_addresses_every_transaction_is_not_the_reason()
    {
        var stream = Stream(19);

        // RCPNS addresses every transaction in stream 19 and the reference never defines it. Both
        // halves of that are asserted below and both are true; what this test used to conclude from
        // them - that the field naming which namespace a recipe lives in was never written down, so
        // the stream cannot be addressed - is what a later chapter retired. It was written down, in
        // streams 15 and 20, as RCPSPEC.
        Assert.False(IsDefined("RCPNS"));

        var primaries = stream.Messages.Where(message => message.Function % 2 == 1).ToArray();

        Assert.Equal(10, primaries.Length);

        // Nine of the ten primaries carry it. Written as nine and not ten because the tenth is
        // S19F1, which has no body at all - it is the request that asks for the list of
        // namespaces, and its reply is a list of RCPNS. So the one row that does not name a
        // namespace is the row whose whole purpose is to enumerate them, and the stream has no
        // transaction that this name is not the address of either way.
        Assert.Equal(
            9,
            primaries.Count(message => message.DataItems.Contains("RCPNS", StringComparer.Ordinal)));

        var list = Assert.Single(primaries, message => !message.DataItems.Contains("RCPNS", StringComparer.Ordinal));

        Assert.Equal((byte)1, list.Function);
        Assert.Empty(list.DataItems);
        Assert.Equal<string[]>(
            ["RCPNS"],
            [.. Assert.Single(stream.Messages, message => message.Function == 2).DataItems]);

        // Named by stream 19 alone - and the sentence that used to follow was "so there is no other
        // row anywhere to read it against", which is the error. The NAME is stream 19's alone; the
        // SLOT is not.
        Assert.Equal<byte[]>(
            [19],
            [.. SecsCatalog.Streams.Where(s => s.Anchoring.OwnGaps.Contains("RCPNS") || s.Anchoring.SharedGaps.Contains("RCPNS")).Select(s => s.Number)]);

        // And no rival spelling, which is the follow-up question that changes what a gap means: a
        // gap with a rival is a field the reference wrote down under another name. This one it did
        // not write down under a name the rival measure can see - its relations are anagram and
        // prefix, and RCPNS against RCPSPEC is neither.
        Assert.DoesNotContain(
            SecsCatalog.Provenance.RivalSpellings,
            rival => rival.UsedName is "RCPNS" || rival.DefinedName is "RCPNS");

        // What the measure cannot see, the rows can. Six row names appear in both stream 15 and
        // stream 19, and in all six the undefined RCPNS sits in the slot the defined RCPSPEC sits
        // in - so the format is read off the row, as stream 8 read CPBODY off PPBODY's slot, and the
        // reason these rows stay out is which row does the job rather than which name was defined.
        var shared = stream.Messages
            .Where(message => message.IsPrimary)
            .Where(message => message.DataItems.Contains("RCPNS", StringComparer.Ordinal))
            .Select(message => (Nineteen: message, Fifteen: SecsCatalog.Messages.SingleOrDefault(other =>
                other.Stream == 15 && string.Equals(other.Name, message.Name, StringComparison.Ordinal))))
            .Where(pair => pair.Fifteen is not null)
            .ToArray();

        Assert.Equal(6, shared.Length);

        foreach (var (nineteen, fifteen) in shared)
        {
            Assert.Equal(0, nineteen.DataItems.ToList().IndexOf("RCPNS"));
            Assert.Equal(0, fifteen!.DataItems.ToList().IndexOf("RCPSPEC"));
        }

        Assert.True(SecsCatalog.TryGetDataItem("RCPSPEC", out var specification));
        Assert.Equal("A", specification!.FormatToken);
    }

    [Fact]
    public void Stream_20_has_two_gap_free_pairs_and_a_third_this_repository_can_read()
    {
        // Which is where the first re-read earned its keep: the recorded refusal said stream 20
        // was rootless, and two of its rows are as serveable as anything in stream 15.
        Assert.Equal<byte[]>([17, 33], Servable(20));

        // And a third is readable without being gap-free, which the chapter that served stream
        // 15's rename established and this file had not caught up with. S20F15 carries two names
        // the dictionary does not define - and each of them is a name that IS defined with a word
        // in front, sitting in the same body two positions earlier.
        var move = Assert.Single(Stream(20).Messages, message => message.Function == 15);

        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "NEWRCPSPEC", "NEWRCPID"], [.. move.DataItems]);

        foreach (var (modifier, basis) in new[] { ("NEWRCPSPEC", "RCPSPEC"), ("NEWRCPID", "RCPID") })
        {
            Assert.False(IsDefined(modifier), modifier);
            Assert.True(SecsCatalog.TryGetDataItem(basis, out var item), basis);
            Assert.Equal("A", item!.FormatToken);
            Assert.Contains(basis, move.DataItems);
            Assert.EndsWith(basis, modifier, StringComparison.Ordinal);
        }

        // The measure cannot see either pair, because its two relations are anagram and prefix and
        // these extend at the FRONT - the blindness RSPACK against SPACK recorded. That is why the
        // reading has to come from the row rather than from the measure.
        Assert.DoesNotContain(
            SecsCatalog.Provenance.RivalSpellings,
            rival => rival.UsedName is "NEWRCPSPEC");

        // And the reply is one RMACK, so there is nothing else in the pair to read.
        Assert.Equal<string[]>(["RMACK"], [.. Stream(20).Messages.Single(m => m.Function == 16).DataItems]);
    }

    [Fact]
    public void And_all_three_are_things_stream_15_already_does()
    {
        var fifteen = Stream(15);
        var twenty = Stream(20);

        // Item for item, in order, with the same names. S20F17 is S15F13 and S20F33 is S15F15 -
        // "Directory Create" where stream 15 says "Namespace Create", which is the same thing in
        // a two-level space.
        foreach (var (theirs, ours) in new[] { ((byte)17, (byte)13), ((byte)33, (byte)15) })
        {
            var theirPrimary = Assert.Single(twenty.Messages, m => m.Function == theirs);
            var ourPrimary = Assert.Single(fifteen.Messages, m => m.Function == ours);

            Assert.Equal<string[]>([.. ourPrimary.DataItems], [.. theirPrimary.DataItems]);

            var theirReply = Assert.Single(twenty.Messages, m => m.Function == theirs + 1);
            var ourReply = Assert.Single(fifteen.Messages, m => m.Function == ours + 1);

            Assert.Equal<string[]>([.. ourReply.DataItems], [.. theirReply.DataItems]);
            Assert.Equal<string[]>(["RMACK"], [.. theirReply.DataItems]);
        }

        // The third is not a duplicate, and saying so is what makes the refusal honest. S20F15 is
        // a rename AND a move: it carries two namespaces where stream 15's rename carries one, so
        // a recipe can change its name and its namespace in one transaction. Stream 15 cannot do
        // that in one - its rename stays inside a namespace and its copy leaves the original
        // where it was.
        var ourRename = Assert.Single(fifteen.Messages, m => m.Function == 19);
        var ourCopy = Assert.Single(fifteen.Messages, m => m.Function == 21);

        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "RCPIDNEW"], [.. ourRename.DataItems]);
        Assert.Equal<string[]>(["RCPSPEC", "RCPID", "RCPSPECDST", "RCPIDDST"], [.. ourCopy.DataItems]);

        // What it would add over the copy and the delete a host already has is ATOMICITY - and
        // that is the one thing its reply cannot report. RMACK has a single refusal code, so a
        // host told Rejected cannot learn whether the source is still there. The advantage is
        // unobservable on the wire, which is the same shape of argument ACKC7's missing mismatch
        // code makes about a verify.
        Assert.Equal<string[]>(["RMACK"], [.. twenty.Messages.Single(m => m.Function == 16).DataItems]);
        Assert.True(SecsCatalog.TryGetDataItem("RMACK", out var rmack));
        Assert.Equal(3, rmack!.Codes.Count);

        // So it is still one capability under two stream numbers - serve one or the other, as
        // Barbatos.SecsGem.Subsystems says of stream 18 - and stream 15's is the firmer because it
        // is whole: fourteen pairs there against three of seventeen here. Declaring three rows of
        // stream 20 would turn an honest S9F3 for the stream into an S9F5 for fourteen functions,
        // which invites a host to keep asking.
        Assert.Equal(17, twenty.Messages.Count(message => message.Function % 2 == 1));
    }

    [Fact]
    public void Eighteen_of_stream_20s_rows_carry_an_undefined_name_and_a_rival_is_no_longer_a_reason()
    {
        var twenty = Stream(20);
        var gaps = twenty.Anchoring.OwnGaps.Concat(twenty.Anchoring.SharedGaps).ToArray();

        // The figure, measured rather than remembered - this test's name used to say fifteen.
        Assert.Equal(
            18,
            twenty.Messages.Count(message => message.DataItems.Any(name => !IsDefined(name))));

        // The three rival-spelled gaps, and the sentence that used to be here said a rival makes a
        // refusal FIRMER: reading a format off the definition would be correcting the reference.
        // Two chapters have overturned that for the identical cases in stream 15. RCPIDNEW is
        // served, because its anagram and its base in the same body are both an A[n]; RCPVER is
        // carried untouched at S15F36. So of these three only RCPSTATE still stands, and it stands
        // for a different reason: RCPSTAT is a U1[1] with NO CODE TABLE, so the two readings
        // DIFFER and one of them is a number with no meanings.
        foreach (var (used, defined) in new[] { ("RCPVER", "RCPVERS"), ("NEWRCPID", "RCPNEWID"), ("RCPSTATE", "RCPSTAT") })
        {
            Assert.Contains(used, gaps);
            Assert.True(IsDefined(defined), defined);

            Assert.Contains(
                SecsCatalog.Provenance.RivalSpellings,
                rival => rival.UsedName == used && rival.DefinedName == defined);

            // And the definition is named by no message at all, which is the companion measure:
            // a capability being specified does not mean anything can carry it.
            Assert.True(SecsCatalog.TryGetDataItem(defined, out var item));
            Assert.True(item!.IsNamedByNoMessage, defined);
        }

        Assert.Equal("A", SecsCatalog.DataItems.Single(item => item.Name == "RCPVERS").FormatToken);
        Assert.Equal("A", SecsCatalog.DataItems.Single(item => item.Name == "RCPNEWID").FormatToken);

        var state = SecsCatalog.DataItems.Single(item => item.Name == "RCPSTAT");

        Assert.Equal("U1", state.FormatToken);
        Assert.Empty(state.Codes);

        // The rest were never written down in any spelling.
        foreach (var name in new[] { "RCPATTRNAME", "RCPATTRVAL", "RCPLOCK", "CHECKSUM", "VFACK", "OWNERID", "LOCKID", "NOTIFYENABLE", "CHANGETYPE", "NEWRCPSPEC" })
        {
            Assert.False(IsDefined(name), name);
        }
    }

    [Fact]
    public void Two_of_stream_20s_gaps_are_the_field_their_row_turns_on()
    {
        var twenty = Stream(20);

        // The ENABLE rule, twice, and worth naming because these two rows would otherwise look
        // like the stream 2 shape - a gap only in the reply. NOTIFYENABLE is the enable-or-disable
        // flag S20F29 exists to carry, and CHANGETYPE is *what kind of change* S20F31 reports:
        // both sit in the primary, and carrying either opaquely would leave this library unable to
        // tell apart the two things its row distinguishes.
        var enable = Assert.Single(twenty.Messages, m => m.Function == 29);
        var report = Assert.Single(twenty.Messages, m => m.Function == 31);

        Assert.Equal<string[]>(["CEID", "NOTIFYENABLE"], [.. enable.DataItems]);
        Assert.Equal<string[]>(["CEID", "RCPSPEC", "RCPID", "CHANGETYPE"], [.. report.DataItems]);

        // Everything else in both rows is defined and already served here, which is what makes
        // each refusal about one name rather than about the row.
        foreach (var name in new[] { "CEID", "RCPSPEC", "RCPID", "ERACK", "ACKC6" })
        {
            Assert.True(IsDefined(name), name);
        }
    }

    [Fact]
    public void A_recipe_rename_is_spelled_three_ways_and_only_the_unused_one_is_defined()
    {
        // The sharpest case of the unused-definition measure in the whole reference. One concept,
        // three spellings, and the reference defines exactly the one no message names - so both
        // rows that want to rename a recipe carry a field it will not describe, and the
        // description it does give belongs to a name nothing sends.
        //
        // Which turned out not to stop either of them. S15F19 is served: both readings of
        // RCPIDNEW are an A[n], so the rivalry puts no byte in doubt. S20F15 is readable for the
        // same reason and stays out because what its single transaction adds over a copy and a
        // delete is atomicity, and RMACK has one refusal code - so a host told Rejected cannot
        // learn whether the source survived. It is the only row in the repository refused on that,
        // and it replaced a reading about what declaring its stream would claim, which
        // SecsCatalogRefusalPartitionTests retired.
        Assert.False(IsDefined("RCPIDNEW"));
        Assert.False(IsDefined("NEWRCPID"));

        Assert.True(SecsCatalog.TryGetDataItem("RCPNEWID", out var defined));
        Assert.True(defined!.IsNamedByNoMessage);

        Assert.Equal<string[]>(
            ["S15F19"],
            [.. RowsNaming("RCPIDNEW")]);

        Assert.Equal<string[]>(
            ["S20F15"],
            [.. RowsNaming("NEWRCPID")]);

        // Both are exact anagrams of the definition, which is what makes this a rival rather than
        // a coincidence: two of the reference's three anagram pairs are this one concept.
        Assert.Equal(
            2,
            SecsCatalog.Provenance.RivalSpellings.Count(rival =>
                rival.DefinedName == "RCPNEWID" && rival.Relation == SecsNameRelation.Anagram));
    }

    [Fact]
    public void A_recipe_checksum_is_spelled_three_ways_and_none_of_them_is_defined()
    {
        // The other half of the same story and the opposite result: three spellings across three
        // streams and not one definition anywhere, so there is nothing to be a rival of. All three
        // rows are refused, consistently, under three different names - and this is the first
        // place that has been said in one breath.
        Assert.Equal<string[]>(["S19F10"], [.. RowsNaming("CKSUM")]);
        Assert.Equal<string[]>(["S15F11", "S20F19"], [.. RowsNaming("CHECKSUM")]);
        Assert.Equal<string[]>(["S21F19"], [.. RowsNaming("CHKSUM")]);

        foreach (var name in new[] { "CKSUM", "CHECKSUM", "CHKSUM" })
        {
            Assert.False(IsDefined(name), name);

            // And none has a rival, so none of the three is a misspelling of something written
            // down elsewhere. The reference simply never says what a recipe checksum is.
            Assert.DoesNotContain(
                SecsCatalog.Provenance.RivalSpellings,
                rival => rival.UsedName == name);
        }
    }

    [Fact]
    public void The_three_recipe_streams_use_the_same_function_numbers_for_different_transactions()
    {
        // Why none of this can be read across. S15F11 verifies, S19F11 deletes, and S20F11 sends -
        // three streams, one function number, three transactions, and a routing table that cannot
        // tell them apart by number alone.
        Assert.Equal("Recipe Verify Request", Assert.Single(Stream(15).Messages, m => m.Function == 11).Name);
        Assert.Equal("Recipe Delete Request", Assert.Single(Stream(19).Messages, m => m.Function == 11).Name);
        Assert.Equal("Recipe Download Send", Assert.Single(Stream(20).Messages, m => m.Function == 11).Name);
    }

    private static IEnumerable<string> RowsNaming(string item)
        => SecsCatalog.Messages
            .Where(message => message.DataItems.Contains(item, StringComparer.Ordinal))
            .Select(message => $"S{message.Stream}F{message.Function}");
}
