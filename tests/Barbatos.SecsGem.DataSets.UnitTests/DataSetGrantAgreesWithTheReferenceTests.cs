// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;

namespace Barbatos.SecsGem.DataSets.UnitTests;

/// <summary>
/// <see cref="DataSetGrant"/> against <c>GRANT6</c>, and the item it is not.
/// </summary>
/// <remarks>
/// <para>
/// <c>GRANT6</c> is named by streams 6 and 13, and this repository now reads it on both: stream 13
/// here, and stream 6 in <c>Barbatos.SecsGem.Gem</c>, whose <c>GemMultiBlockGrant</c> arrived with
/// S6F5 in the coverage chapter. So this is no longer a single-reader assertion made for safety's
/// sake - it is one half of a shared item, and the rule for those is that each side is asserted
/// against the reference's own table rather than against the other enum. That is what makes the
/// agreement transitive if a third package ever serves it.
/// </para>
/// <para>
/// The member names are the one thing the table cannot settle, so they are asserted directly and
/// in one direction only: <c>GemMultiBlockGrant</c> was written to match the four names here,
/// because this enum came first.
/// </para>
/// <para>
/// <strong>And <c>GRANT6</c> is not <c>GRANT</c>.</strong> The two differ by one code and by the
/// meaning of another, which is the kind of near-miss that makes an enum look right while being
/// wrong - three packages here serve <c>GRANT</c> and the names are one digit apart.
/// </para>
/// </remarks>
public class DataSetGrantAgreesWithTheReferenceTests
{
    private static SecsDataItemInfo Item(string name)
    {
        Assert.True(SecsCatalog.TryGetDataItem(name, out var item), name);
        return item!;
    }

    [Fact]
    public void Every_named_code_sits_where_the_reference_puts_it()
    {
        var table = Item("GRANT6");

        Assert.Equal("OK, permission granted", table.Codes[0].Meaning);
        Assert.Equal((byte)0, (byte)DataSetGrant.Ok);
        Assert.Equal((byte)1, (byte)DataSetGrant.Busy);
        Assert.Equal((byte)2, (byte)DataSetGrant.NoSpace);

        // Three named codes and a reserved row, so four members with the catch-all at its first
        // code.
        Assert.Equal((byte)3, (byte)DataSetGrant.Other);
        Assert.Equal("3-63", table.Codes[^1].Value);
        Assert.Equal(table.Codes.Count, Enum.GetValues<DataSetGrant>().Length);

        // The four names, which the table cannot give and the other package had to be written to.
        // A test on this side as well as that one, because a rename here would otherwise break an
        // agreement whose only record was a doc comment in a package this one cannot see.
        Assert.Equal<string[]>(["Ok", "Busy", "NoSpace", "Other"], Enum.GetNames<DataSetGrant>());
    }

    [Fact]
    public void It_is_one_code_shorter_than_GRANT_and_that_is_the_difference_worth_knowing()
    {
        var six = Item("GRANT6");
        var grant = Item("GRANT");

        // GRANT names four codes and reserves from 4; GRANT6 names three and reserves from 3. So
        // the catch-all sits at a different number in each, and an enum copied from the wrong one
        // would read a machine's reserved 3 as "no space" - or call a genuine refusal reserved.
        Assert.Equal(4, six.Codes.Count);
        Assert.Equal(5, grant.Codes.Count);
        Assert.Equal("3-63", six.Codes[^1].Value);
        Assert.Equal("4-63", grant.Codes[^1].Value);

        // And the second code reads differently: GRANT 2 is "no space available" where GRANT6 2
        // is "not interested / no space", which is a machine declining as well as a machine full.
        Assert.Equal("No space available", grant.Codes[2].Meaning);
        Assert.Equal("Not interested / no space", six.Codes[2].Meaning);

        Assert.Equal<byte[]>([6, 13], [.. six.Streams]);
    }
}
