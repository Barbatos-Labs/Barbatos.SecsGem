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
/// The two exceptions are the interesting part. <c>COMMACK</c> names both its codes and reserves
/// nothing, so there is no range to fold and no member to add. <c>TRACK</c> has <strong>no table
/// of values at all</strong>, so this package's six trace codes come from E30's prose rather than
/// from the dictionary - a different kind of gap, and one this test deliberately does not pretend
/// to check.
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
        { "RSPACK", 1, 2 },
        { "ACKC10", 3, 4 },
        { "PPGNT", 6, 7 },
        { "ACKC7", 7, 8 },
        { "HCACK", 7, 8 },
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
    public void The_two_acknowledgements_without_a_reserved_range_are_the_two_this_package_reads_differently()
    {
        // COMMACK names 0 and 1 and reserves nothing, so GemCommunicationAcknowledgement has two
        // members and GemHost reads it with the plain cast - correctly, and this test is why that
        // is not an oversight.
        Assert.True(SecsCatalog.TryGetDataItem("COMMACK", out var commack));
        Assert.Equal(2, commack!.Codes.Count);
        Assert.DoesNotContain(commack.Codes, code => code.Value.Contains('-', StringComparison.Ordinal));
        Assert.Equal(2, Enum.GetValues<GemCommunicationAcknowledgement>().Length);

        // TRACK is defined and carries no code values at all, so there is nothing here to agree
        // with. The six members of GemTraceAcknowledgement are E30's, not the dictionary's, and
        // that is said here rather than left for somebody to discover.
        Assert.True(SecsCatalog.TryGetDataItem("TRACK", out var track));
        Assert.Empty(track!.Codes);
        Assert.False(track.IsNamedByNoMessage);
    }

    [Fact]
    public void Every_guarded_enum_has_its_catch_all_member_at_the_boundary()
    {
        // The other half of the claim: the table says where the reserved range starts, and the
        // enum must have a member there. Named Other everywhere except the two whose single
        // non-zero member already covers the whole range - ACKC5 and RSPACK name only a zero, so
        // Error and Denied are the range.
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

        Assert.Equal((byte)1, (byte)GemAlarmAcknowledgement.Error);
        Assert.Equal((byte)1, (byte)GemSpooledDataAcknowledgement.Denied);
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
    }

    private static void AssertHighest<T>(byte boundary)
        where T : struct, Enum
        => Assert.Equal(
            boundary,
            Enum.GetValues<T>().Max(value => Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture)));
}
