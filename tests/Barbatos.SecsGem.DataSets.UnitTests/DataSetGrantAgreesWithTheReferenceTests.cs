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
/// <c>GRANT6</c> is named by streams 6 and 13, and of those two this repository serves stream 13
/// here and stream 6 in <c>Barbatos.SecsGem.Gem</c> - which does not model this item, so in
/// practice one package reads it. The assertion is worth having anyway: the table is the only
/// thing that can say whether these three codes are the right three.
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
