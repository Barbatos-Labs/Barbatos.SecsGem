// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// <see cref="ControlProgramGrant"/> against <c>GRANT</c>, the reference's own table for it.
/// </summary>
/// <remarks>
/// <para>
/// <c>GRANT</c> is one item and four packages here serve it - this one for S8F2, Barbatos.SecsGem.Material for S3F16, Barbatos.SecsGem.ItemTransfer for S21F6 and Barbatos.SecsGem.Gem for S2F2 and S2F40, which the stream 2 chapter added. The working agreement is that one item cannot mean two things in
/// one repository, and the way that is kept is here: <strong>each package asserts against the
/// reference's table rather than against the other packages' enums</strong>, so the agreement is
/// transitive and no pairwise list has to be maintained.
/// </para>
/// <para>
/// This package already asserted its five names and numbers as literals, which catches a change
/// here and not a drift between here and the table. That is how a stale <c>GRANT</c> figure
/// survived a chapter once.
/// </para>
/// </remarks>
public class ControlProgramGrantAgreesWithTheReferenceTests
{
    private static SecsDataItemInfo Granted
    {
        get
        {
            Assert.True(SecsCatalog.TryGetDataItem("GRANT", out var item));
            return item!;
        }
    }

    [Fact]
    public void Every_named_code_sits_where_the_reference_puts_it()
    {
        var table = Granted;

        Assert.Equal("OK, will accept", table.Codes[0].Meaning);
        Assert.Equal((byte)0, (byte)ControlProgramGrant.Accepted);
        Assert.Equal((byte)1, (byte)ControlProgramGrant.Busy);
        Assert.Equal((byte)2, (byte)ControlProgramGrant.NoSpace);
        Assert.Equal((byte)3, (byte)ControlProgramGrant.DuplicateOrInvalid);
        Assert.Equal((byte)4, (byte)ControlProgramGrant.Other);
        // One member per row, the catch-all included, so neither side has a code the other lacks.
        Assert.Equal(table.Codes.Count, Enum.GetValues<ControlProgramGrant>().Length);
    }

    [Fact]
    public void The_catch_all_sits_at_the_first_reserved_code()
    {
        var table = Granted;
        var reserved = table.Codes[^1];

        // The reserved row is the last, and the member at its first code is where a reader folds
        // everything it cannot name. Asserting the boundary rather than trusting it is what the
        // acknowledgement sweep in Barbatos.SecsGem.Gem was for.
        Assert.Contains('-', reserved.Value);
        Assert.StartsWith($"{(byte)ControlProgramGrant.Other}-", reserved.Value, StringComparison.Ordinal);
    }
}
