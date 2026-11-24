// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Catalog.UnitTests;

/// <summary>
/// Stream 2, the last stream here with rows nobody had argued about.
/// </summary>
/// <remarks>
/// <para>
/// Sixty-four rows, thirty-two primaries, and seven names the reference never defines - the most
/// gaps of any stream in the reference. That made it the one stream the group measures could say
/// nothing useful about, and the one left when every other stream had either been served or
/// refused with a reason on the record.
/// </para>
/// <para>
/// <strong>Three of its seven gaps were already being carried when this chapter started</strong>,
/// by <c>Barbatos.SecsGem.Objects</c> on the enhanced half: <c>RPACK</c>, <c>URACK</c> and
/// <c>DGDATA</c>. So the question for the remaining four was not whether an undefined name can be
/// served - that was settled - but where each one sits in its row.
/// </para>
/// </remarks>
public class SecsCatalogStream2Tests
{
    private static SecsStreamInfo Stream()
    {
        Assert.True(SecsCatalog.TryGetStream(2, out var stream));
        return stream!;
    }

    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    private static SecsMessageInfo Row(byte function)
        => Assert.Single(Stream().Messages, message => message.Function == function);

    [Fact]
    public void It_has_the_most_gaps_of_any_stream_and_every_one_is_its_own()
    {
        var anchoring = Stream().Anchoring;

        // Seven, and all of them the stream's own - so none is a vocabulary the whole reference
        // skipped, and none of these rows is left alone on that ground.
        Assert.Empty(anchoring.SharedGaps);
        Assert.Equal<string[]>(
            ["ACKC2", "DGDATA", "PRACK", "RIACK", "RPACK", "RSTC", "URACK"],
            [.. anchoring.OwnGaps]);

        // And no rival spellings, which the recipe chapter made the decisive follow-up question: a
        // gap with a rival is a field the reference wrote down under another name, and reading a
        // format off the rival would be correcting the reference. Rivals live only in streams 15,
        // 19 and 20, so every gap here is a field nobody wrote down.
        Assert.Empty(Stream().RivalSpellings);
    }

    [Fact]
    public void Three_of_the_seven_were_already_being_carried_before_this_chapter()
    {
        // RPACK and URACK answer S2F52 and S2F54, and DGDATA is S2F64's payload - the enhanced
        // half, which Barbatos.SecsGem.Objects serves because those rows are E39's. All three are
        // undefined and all three are carried, which is what made the treatment a precedent rather
        // than a proposal.
        foreach (var (name, function) in new[] { ("RPACK", (byte)52), ("URACK", (byte)54), ("DGDATA", (byte)64) })
        {
            Assert.False(SecsCatalog.TryGetDataItem(name, out _), name);
            Assert.Contains(name, Row(function).DataItems);
        }
    }

    [Fact]
    public void The_service_program_acknowledgement_is_undefined_and_its_eight_siblings_agree()
    {
        // ACKC2 answers S2F4 and S2F8 and the reference does not define it. The inference this
        // repository makes is the one ControlPrograms made for ACKC8: nought is acceptance, read
        // off the ACKCn items that *are* defined.
        Assert.False(SecsCatalog.TryGetDataItem("ACKC2", out _));
        Assert.Equal<byte[]>([4, 8], [.. Stream().Messages.Where(m => m.DataItems.Contains("ACKC2", StringComparer.Ordinal)).Select(m => m.Function)]);

        // Eight of them, and the inference is only worth making because they do not disagree:
        // every one is a B whose zero means accepted. What they disagree about is everything else,
        // which is why the enum here has two members and not seven.
        string[] siblings = ["ACKC3", "ACKC5", "ACKC6", "ACKC7", "ACKC7A", "ACKC10", "ACKC13", "ACKC15"];

        Assert.Equal(8, siblings.Length);

        foreach (var name in siblings)
        {
            var sibling = Item(name);

            Assert.Equal("B", sibling.FormatToken);
            Assert.StartsWith("Accepted", sibling.MeaningOf("0"), StringComparison.Ordinal);
        }

        // And there is no ninth: ACKC2 is not a missing row of a family the reference numbers
        // continuously, which is worth knowing before reading anything into the gap.
        Assert.DoesNotContain(
            SecsCatalog.DataItems.Select(item => item.Name),
            name => name is "ACKC1" or "ACKC2" or "ACKC4" or "ACKC8" or "ACKC9");
    }

    [Fact]
    public void The_processing_acknowledgement_is_undefined_and_the_row_it_answers_is_not()
    {
        // PRACK answers S2F28, and the gap is in the reply rather than in the request: S2F27
        // carries a PPID, which is defined, capped at 120 characters, and named by twenty-eight
        // rows of this reference. So what the message asks is never in doubt.
        Assert.False(SecsCatalog.TryGetDataItem("PRACK", out _));
        Assert.Equal<string[]>(["PPID"], [.. Row(27).DataItems]);
        Assert.Equal<string[]>(["PRACK"], [.. Row(28).DataItems]);

        var ppid = Item("PPID");

        Assert.Equal("A", ppid.FormatToken);
        Assert.Equal("120", ppid.Length);
        Assert.True(ppid.Streams.Count > 1);

        // Its two siblings are the commands this package already serves, and both are a B whose
        // zero means the command was taken.
        foreach (var name in new[] { "CMDA", "HCACK" })
        {
            Assert.Equal("B", Item(name).FormatToken);
        }
    }

    [Fact]
    public void The_reset_row_is_one_undefined_item_and_nothing_else_which_is_why_it_stays_out()
    {
        // The refusal, and the clearest one in the reference. S2F19's whole body is RSTC, and RSTC
        // is a name nobody wrote down - so there is no second field in the row to anchor a reading
        // against and no sibling anywhere that carries it. Strictly worse than S14F23's ENABLE,
        // which at least sat beside items that were defined.
        Assert.Equal<string[]>(["RSTC"], [.. Row(19).DataItems]);
        Assert.False(SecsCatalog.TryGetDataItem("RSTC", out _));

        // And it is the field the message turns on: a reset code is *which* reset. Carrying it
        // untouched would leave this library unable to tell one reset from another, which is the
        // rule that kept ENABLE, NRED, CELLNAME and CHKSUM out.
        Assert.Equal<byte[]>([19], [.. Stream().Messages.Where(m => m.DataItems.Contains("RSTC", StringComparer.Ordinal)).Select(m => m.Function)]);

        // Its acknowledgement is undefined too, which is the only row in the stream where both
        // halves are gaps.
        Assert.Equal<string[]>(["RIACK"], [.. Row(20).DataItems]);
        Assert.False(SecsCatalog.TryGetDataItem("RIACK", out _));
    }

    [Fact]
    public void A_service_program_is_text_where_a_recipe_and_a_control_program_are_bytes()
    {
        // The finding that shaped the store interface. SPD and SPR are both A[n] and stream 7's
        // PPBODY is a B, so a service program is source text where a recipe is opaque bytes - and
        // the store takes a string rather than a buffer.
        foreach (var name in new[] { "SPD", "SPR" })
        {
            Assert.Equal("A", Item(name).FormatToken);
        }

        Assert.Equal("B", Item("PPBODY").FormatToken);

        // And the format is *stated* here rather than inferred, which is the sharper half and the
        // opposite of stream 8's position: CPBODY has no definition at all, so
        // Barbatos.SecsGem.ControlPrograms had to read its format off PPBODY's slot. Nothing like
        // that was needed for a service program.
        Assert.False(SecsCatalog.TryGetDataItem("CPBODY", out _));

        // And it is identified by a number where a recipe is identified by a name, so there is no
        // identifier length to validate and no character set to worry about.
        Assert.Equal("U4", Item("SPID").FormatToken);
        Assert.Equal("A", Item("PPID").FormatToken);
    }

    [Fact]
    public void The_send_row_carries_no_identifier_which_is_what_makes_the_inquire_mandatory()
    {
        // S2F1 names the program and S2F3 carries only the text, so the identifier a program is
        // filed under is the one from the inquire before it. That correlation is the handshake's,
        // and this test is what stops a later chapter reading it as an invention.
        Assert.Equal<string[]>(["SPID", "LENGTH"], [.. Row(1).DataItems]);
        Assert.Equal<string[]>(["SPD"], [.. Row(3).DataItems]);
        Assert.DoesNotContain("SPID", Row(3).DataItems);

        // Stream 7's send does carry its identifier, which is the contrast: S7F3 is
        // self-contained and S2F3 is not.
        Assert.True(SecsCatalog.TryGetStream(7, out var seven));
        Assert.Contains("PPID", Assert.Single(seven!.Messages, m => m.Function == 3).DataItems);
    }

    [Fact]
    public void Every_item_the_reset_spooling_pair_carries_is_defined_and_two_of_them_are_tabled()
    {
        // S2F43 and S2F44 have no gaps at all, which was the surprise: the row that *owns* RSPACK
        // is the one nothing here served, so the spool chapter had been quoting its table from a
        // row it could not reach.
        Assert.Equal<string[]>(["STRID", "FCNID"], [.. Row(43).DataItems]);
        Assert.Equal<string[]>(["RSPACK", "STRID", "STRACK", "FCNID"], [.. Row(44).DataItems]);

        foreach (var name in new[] { "STRID", "FCNID", "RSPACK", "STRACK" })
        {
            Assert.True(SecsCatalog.TryGetDataItem(name, out _), name);
        }

        // RSPACK and STRACK are two items with one table, printed twice - which is why one enum
        // reads both and the position says which is which.
        var rspack = Item("RSPACK");
        var strack = Item("STRACK");

        Assert.Equal(rspack.Codes.Count, strack.Codes.Count);
        Assert.Equal(rspack.Codes[^1].Value, strack.Codes[^1].Value);
        Assert.Equal("Accepted", rspack.MeaningOf("0"));
        Assert.Equal("Accepted", strack.MeaningOf("0"));

        // Both named by S2F44 alone, so neither has another row here to disagree with.
        Assert.Equal<byte[]>([2], [.. rspack.Streams]);
        Assert.Equal<byte[]>([2], [.. strack.Streams]);
    }

    [Fact]
    public void The_multi_block_inquire_is_S6F5_the_other_way_round_with_a_fourth_grant_code()
    {
        var thirtyNine = Row(39);
        var six = Row(40);

        // Same two items as S6F5, which is what let the body be read off a row already served -
        // and the reverse direction, which is why both rows exist.
        Assert.Equal<string[]>(["DATAID", "DATALENGTH"], [.. thirtyNine.DataItems]);
        Assert.Equal(SecsMessageDirection.HostToEquipment, thirtyNine.Direction);

        Assert.True(SecsCatalog.TryGetStream(6, out var streamSix));
        var five = Assert.Single(streamSix!.Messages, m => m.Function == 5);

        Assert.Equal<string[]>(["DATAID", "DATALENGTH"], [.. five.DataItems]);
        Assert.Equal(SecsMessageDirection.EquipmentToHost, five.Direction);

        // The grants differ, and by exactly one code: GRANT has a duplicate-identifier refusal
        // that GRANT6 has not, because a stream 6 inquire names nothing that could be duplicated.
        Assert.Equal<string[]>(["GRANT"], [.. six.DataItems]);

        var grant = Item("GRANT");
        var grant6 = Item("GRANT6");

        Assert.Equal(grant6.Codes.Count + 1, grant.Codes.Count);
        Assert.Equal("Duplicate identifier / invalid", grant.MeaningOf("3"));
        Assert.Equal("4-63", grant.Codes[^1].Value);
        Assert.Equal("3-63", grant6.Codes[^1].Value);
    }

    [Fact]
    public void The_grant_item_is_served_from_four_packages_and_this_stream_is_the_fourth()
    {
        // GRANT is named by seven rows across five streams, and before this chapter three
        // packages read it. A fourth reader is the point at which the shared-item rule bites:
        // each package asserts against this table rather than against another package's enum.
        var grant = Item("GRANT");

        Assert.Equal<byte[]>([2, 3, 4, 8, 21], [.. grant.Streams]);
        Assert.Equal(5, grant.Codes.Count);
        Assert.Equal("OK, will accept", grant.MeaningOf("0"));
        Assert.Equal("Busy, try again", grant.MeaningOf("1"));
        Assert.Equal("No space available", grant.MeaningOf("2"));
    }

    [Fact]
    public void Nine_of_the_ten_rows_left_had_no_gap_or_a_gap_in_the_acknowledgement_only()
    {
        // The partition this chapter acted on, asserted so a later one can see what was decided
        // rather than re-deriving it. Six rows carry no undefined name at all; three carry one and
        // it is the acknowledgement; one is the reset, where the undefined name is the whole body.
        byte[] clean = [1, 5, 9, 11, 39, 43];
        byte[] acknowledgementOnly = [3, 7, 27];

        foreach (var function in clean)
        {
            Assert.All(
                Row(function).DataItems,
                name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), $"S2F{function} carries {name}"));

            // The reply too, since a row is a pair and a gap in either half is a gap in the row.
            var reply = Row((byte)(function + 1));

            Assert.All(
                reply.DataItems,
                name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), $"S2F{function + 1} carries {name}"));
        }

        foreach (var function in acknowledgementOnly)
        {
            Assert.All(
                Row(function).DataItems,
                name => Assert.True(SecsCatalog.TryGetDataItem(name, out _), $"S2F{function} carries {name}"));

            var undefined = Row((byte)(function + 1)).DataItems
                .Where(name => !SecsCatalog.TryGetDataItem(name, out _))
                .ToArray();

            Assert.Single(undefined);
        }
    }
}
