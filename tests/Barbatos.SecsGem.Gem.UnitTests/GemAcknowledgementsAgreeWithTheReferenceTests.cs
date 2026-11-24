// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Every acknowledgement enum here against the code table its item defines.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This file exists because twelve of them were wrong the same way.</strong> E5 gives most
/// acknowledgement items a reserved range - 7 to 63 for <c>ACKC7</c>, 3 to 63 for <c>ONLACK</c>,
/// and so on - and <c>GemHost</c> used to cast the arriving code straight to its enum. An
/// equipment answering S1F18 with 40 produced an <c>ONLACK</c> value with no name, and a caller
/// switching on it fell through every case.
/// </para>
/// <para>
/// <c>HCACK</c> was fixed on its own two chapters earlier, and the other twelve were not, because
/// nothing compared the enums with the tables. This test is that comparison, so the fourteenth
/// cannot be missed the way the twelve were - and it is driven as well as compared, in
/// <c>GemReservedAcknowledgementTests</c>, because a boundary that is right in an enum and unused
/// in the reader would pass this file and fail a machine.
/// </para>
/// <para>
/// <strong>And the fourteenth was missed.</strong> The coverage chapter that implemented S2F21
/// wrote <c>GemRemoteCommandAcknowledgement</c> with <c>CMDA</c>'s three named codes and no member
/// at 3, which is the hole this file was built to stop - the author of the file and the author of
/// the enum being the same, two chapters apart. It was caught by the shared-item rule rather than
/// by this test, because the item had to be compared with
/// <c>Barbatos.SecsGem.ProcessJobs</c>' reading of it, and that package turned out to have the
/// same hole. Both have a member at 3 now, and <c>CMDA</c> is in the table below so a third
/// chapter cannot repeat it. What the episode says about the method is that an enum written in the
/// same chapter as its reader gets no second pair of eyes, and the table is the only second pair
/// there is.
/// </para>
/// <para>
/// The three exceptions are the interesting part. <c>COMMACK</c> names both its codes and reserves
/// nothing, so there is no range to fold and no member to add. And two that this file used to check
/// against the wrong item entirely - S2F24's and S6F24's, found two chapters apart by the same
/// question: which streams name the item this package says it is reading.
/// </para>
/// </remarks>
public class GemAcknowledgementsAgreeWithTheReferenceTests
{
    /// <summary>Each enum, the item it reads, and where that item's reserved range starts.</summary>
    /// <remarks>
    /// The boundary is the enum's catch-all member, so this table is the whole claim in one place:
    /// a reader that disagreed with it would be folding the wrong codes.
    /// </remarks>
    public static TheoryData<string, byte, int> Guarded => new()
    {
        { "OFLACK", 1, 2 },
        { "ONLACK", 3, 4 },
        { "EAC", 4, 5 },
        { "TIACK", 2, 3 },
        { "DRACK", 5, 6 },
        { "LRACK", 6, 7 },
        { "ERACK", 2, 3 },
        { "ACKC5", 1, 2 },
        { "ACKC6", 1, 2 },
        { "ACKC10", 3, 4 },
        { "PPGNT", 6, 7 },
        { "ACKC7", 7, 8 },
        { "HCACK", 7, 8 },
        { "CMDA", 3, 4 },
        { "GRANT6", 3, 4 },
    };

    [Theory]
    [MemberData(nameof(Guarded))]
    public void The_reserved_range_starts_where_this_package_says_it_does(string item, byte reserved, int members)
    {
        Assert.True(SecsCatalog.TryGetDataItem(item, out var defined));

        // The named codes run 0 to reserved-1, and then one row covers the rest. So the table has
        // exactly `reserved` named codes plus that row, which is the arithmetic the enum mirrors.
        Assert.Equal(members, defined!.Codes.Count);
        Assert.Equal(reserved, (byte)(defined.Codes.Count - 1));

        var last = defined.Codes[^1];

        Assert.Contains('-', last.Value);
        Assert.StartsWith($"{reserved}-", last.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void The_acknowledgements_without_a_reserved_range_are_the_ones_this_package_reads_differently()
    {
        // COMMACK names 0 and 1 and reserves nothing, so GemCommunicationAcknowledgement has two
        // members and GemHost reads it with the plain cast - correctly, and this test is why that
        // is not an oversight.
        Assert.True(SecsCatalog.TryGetDataItem("COMMACK", out var commack));
        Assert.Equal(2, commack!.Codes.Count);
        Assert.DoesNotContain(commack.Codes, code => code.Value.Contains('-', StringComparison.Ordinal));
        Assert.Equal(2, Enum.GetValues<GemCommunicationAcknowledgement>().Length);

        // And the trace acknowledgement, which is where this test was wrong for four chapters.
        //
        // It used to assert against TRACK, on the ground that TRACK "has no table of values at
        // all, so this package's six trace codes come from E30's prose". Every word of that is
        // true of TRACK and none of it is relevant: the item S2F24 carries is TIAACK, and TRACK is
        // a different item altogether - an A[n] named by streams 4 and 17, where it is a
        // *transfer* acknowledgement. The stream 4 sweep found it by asking which streams name
        // TRACK and getting an answer that did not include stream 2.
        Assert.True(SecsCatalog.TryGetDataItem("TRACK", out var track));
        Assert.Equal<byte[]>([4, 17], [.. track!.Streams]);
        Assert.Empty(track.Codes);

        // TIAACK does have a table, and it names two codes: accepted and denied, with no reserved
        // range. So GemTraceAcknowledgement's six members are not a gap in the dictionary being
        // filled from E30's prose - they are a disagreement with the dictionary, and that is a
        // materially different claim from the one this file used to make.
        Assert.True(SecsCatalog.TryGetDataItem("TIAACK", out var tiaack));
        Assert.Equal<byte[]>([2], [.. tiaack!.Streams]);
        Assert.Equal(2, tiaack.Codes.Count);
        Assert.Equal("Accepted", tiaack.MeaningOf("0"));
        Assert.Equal("Denied", tiaack.MeaningOf("1"));
        Assert.DoesNotContain(tiaack.Codes, code => code.Value.Contains('-', StringComparison.Ordinal));

        // Which has a consequence worth stating, because it is the interoperability half: a
        // machine built to the dictionary answers 1 for *every* denial, and this package would
        // report that as TooManyVariables. The six names are only trustworthy from a machine built
        // to E30's prose. What is safe either way is that non-zero means denied.
        Assert.Equal(6, Enum.GetValues<GemTraceAcknowledgement>().Length);
        Assert.Equal((byte)1, (byte)GemTraceAcknowledgement.TooManyVariables);
        Assert.Equal((byte)0, (byte)GemTraceAcknowledgement.Accepted);

        // And the spool acknowledgement, which is the same mistake found again by the same
        // question - and worse, because it was wrong in three places at once.
        //
        // This file used to carry a { "RSPACK", 1, 2 } row, GemHost logged the arriving code as
        // RSPACK, and the enum's own summary called it RSDA. One byte, three names. RSPACK does
        // have a table and that row passed, which is exactly why nothing caught it: the table is
        // real, it just belongs to S2F44 - reset spooling streams, a row nothing here serves.
        Assert.True(SecsCatalog.TryGetDataItem("RSPACK", out var rspack));
        Assert.Equal<byte[]>([2], [.. rspack!.Streams]);

        // What S6F24 actually carries is SPACK, which the reference does not define at all - no
        // format and no table - so there is nothing for this enum to be compared against. That is
        // what makes it an exception rather than a row in the table above.
        Assert.False(SecsCatalog.TryGetDataItem("SPACK", out _));

        // So the two codes are grounded on ACKC6 instead: stream 6's own acknowledgement, carried
        // by four of its rows, zero accepted and 1 to 63 an unexplained refusal. Its table is
        // identical to the one that was being quoted, which is the whole reason this correction
        // moves a name and not a byte - and is why it had to be found by reading rather than by
        // running anything.
        Assert.True(SecsCatalog.TryGetDataItem("ACKC6", out var ackc6));
        Assert.Equal(rspack.Codes.Count, ackc6!.Codes.Count);
        Assert.Equal(rspack.Codes[^1].Value, ackc6.Codes[^1].Value);
        Assert.Equal("Accepted", ackc6.MeaningOf("0"));

        Assert.Equal(2, Enum.GetValues<GemSpooledDataAcknowledgement>().Length);
        Assert.Equal((byte)0, (byte)GemSpooledDataAcknowledgement.Accepted);
        Assert.Equal((byte)1, (byte)GemSpooledDataAcknowledgement.Denied);

        // GemSpooledDataAcknowledgement and GemReportAcknowledgement therefore read one table from
        // two enums, which this repository allows only where each side says so. They do: the spool
        // reply is a different row with a different item name, and folding them into one type
        // would leave a caller of RequestSpooledDataAsync switching on a member called Error.
        Assert.Equal(
            Enum.GetValues<GemReportAcknowledgement>().Length,
            Enum.GetValues<GemSpooledDataAcknowledgement>().Length);
    }

    [Fact]
    public void Every_guarded_enum_has_its_catch_all_member_at_the_boundary()
    {
        // The other half of the claim: the table says where the reserved range starts, and the
        // enum must have a member there. Named Other everywhere except the three whose single
        // non-zero member already covers the whole range - ACKC5, ACKC6 and the spool reply name
        // only a zero, so Error and Denied are the range.
        Assert.Equal((byte)1, (byte)GemOfflineAcknowledgement.Other);
        Assert.Equal((byte)3, (byte)GemOnlineAcknowledgement.Other);
        Assert.Equal((byte)4, (byte)GemEquipmentConstantAcknowledgement.Other);
        Assert.Equal((byte)2, (byte)GemTimeAcknowledgement.Other);
        Assert.Equal((byte)5, (byte)GemDefineReportAcknowledgement.Other);
        Assert.Equal((byte)6, (byte)GemLinkReportAcknowledgement.Other);
        Assert.Equal((byte)2, (byte)GemEnableEventAcknowledgement.Other);
        Assert.Equal((byte)3, (byte)GemTerminalAcknowledgement.Other);
        Assert.Equal((byte)6, (byte)GemProcessProgramGrant.Other);
        Assert.Equal((byte)7, (byte)GemProcessProgramAcknowledgement.Other);
        Assert.Equal((byte)7, (byte)GemHostCommandAcknowledgement.Other);
        Assert.Equal((byte)3, (byte)GemRemoteCommandAcknowledgement.Other);
        Assert.Equal((byte)3, (byte)GemMultiBlockGrant.Other);

        Assert.Equal((byte)1, (byte)GemAlarmAcknowledgement.Error);
        Assert.Equal((byte)1, (byte)GemReportAcknowledgement.Error);
        Assert.Equal((byte)1, (byte)GemSpooledDataAcknowledgement.Denied);
    }

    [Fact]
    public void The_two_items_served_from_a_second_package_agree_with_the_table_and_so_name_for_name()
    {
        // CMDA is read here for S2F22 and in Barbatos.SecsGem.ProcessJobs for S16F4 and S16F24;
        // GRANT6 here for S6F6 and in Barbatos.SecsGem.DataSets for S13F2. The repository's rule
        // is that each package asserts against the reference's own table rather than against the
        // other enum, which is what keeps the agreement transitive - so this half checks the
        // table, and a matching test in each of those packages checks the same table from its
        // side.
        Assert.True(SecsCatalog.TryGetDataItem("CMDA", out var cmda));
        Assert.Equal("Command performed / completed", cmda!.Codes[0].Meaning);
        Assert.Equal("Command does not exist", cmda.Codes[1].Meaning);
        Assert.Equal("Cannot perform now", cmda.Codes[2].Meaning);
        Assert.Equal("3-63", cmda.Codes[^1].Value);

        Assert.True(SecsCatalog.TryGetDataItem("GRANT6", out var grant));
        Assert.Equal("OK, permission granted", grant!.Codes[0].Meaning);
        Assert.Equal("Busy, try again", grant.Codes[1].Meaning);
        Assert.Equal("Not interested / no space", grant.Codes[2].Meaning);
        Assert.Equal("3-63", grant.Codes[^1].Value);

        // The member names are deliberately the other package's, which is the one thing a table
        // cannot say. Ok rather than Granted, to match DataSetGrant exactly: two enums for one
        // item that disagree on a name are a trap for anybody reading both.
        Assert.Equal<string[]>(
            ["Ok", "Busy", "NoSpace", "Other"],
            Enum.GetNames<GemMultiBlockGrant>());

        Assert.Equal<string[]>(
            ["Performed", "DoesNotExist", "CannotPerformNow", "Other"],
            Enum.GetNames<GemRemoteCommandAcknowledgement>());

        // And GRANT6 is not GRANT, which four packages here read: it names one code fewer and
        // reserves from one code lower, so a catch-all copied across would read a machine's
        // reserved 3 as a refusal it never made.
        Assert.True(SecsCatalog.TryGetDataItem("GRANT", out var elder));
        Assert.Equal(5, elder!.Codes.Count);
        Assert.Equal("4-63", elder.Codes[^1].Value);
        Assert.Equal(4, grant.Codes.Count);
    }

    [Fact]
    public void No_guarded_enum_has_a_member_beyond_its_boundary()
    {
        // Because a member above the catch-all could never be reached: the reader folds everything
        // from the boundary upwards onto one value. A member at 8 in an enum whose item reserves
        // 7 to 63 would be dead code pretending to be a code.
        AssertHighest<GemOfflineAcknowledgement>(1);
        AssertHighest<GemOnlineAcknowledgement>(3);
        AssertHighest<GemEquipmentConstantAcknowledgement>(4);
        AssertHighest<GemTimeAcknowledgement>(2);
        AssertHighest<GemDefineReportAcknowledgement>(5);
        AssertHighest<GemLinkReportAcknowledgement>(6);
        AssertHighest<GemEnableEventAcknowledgement>(2);
        AssertHighest<GemAlarmAcknowledgement>(1);
        AssertHighest<GemSpooledDataAcknowledgement>(1);
        AssertHighest<GemTerminalAcknowledgement>(3);
        AssertHighest<GemProcessProgramGrant>(6);
        AssertHighest<GemProcessProgramAcknowledgement>(7);
        AssertHighest<GemHostCommandAcknowledgement>(7);
        AssertHighest<GemRemoteCommandAcknowledgement>(3);
        AssertHighest<GemMultiBlockGrant>(3);
        AssertHighest<GemReportAcknowledgement>(1);
    }

    private static void AssertHighest<T>(byte boundary)
        where T : struct, Enum
        => Assert.Equal(
            boundary,
            Enum.GetValues<T>().Max(value => Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture)));
}
