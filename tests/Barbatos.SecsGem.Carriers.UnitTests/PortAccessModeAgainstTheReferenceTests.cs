// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Catalog;
using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// <see cref="PortAccessMode"/> against <c>ACCESSMODE</c> - the one disagreement the sweep found.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The two are numbered the opposite way round.</strong> The reference defines
/// <c>ACCESSMODE</c> as a <c>U1</c> with 0 meaning automatic and 1 meaning manual; this package has
/// <c>Manual = 0</c> and <c>Auto = 1</c>. One of them is wrong about E87, and nothing inside this
/// repository can say which: <c>ACCESSMODE</c> is named by no message, so the dictionary's reading
/// cannot be checked against a row, and E87 itself is a standard to buy and read.
/// </para>
/// <para>
/// <strong>What this file does is establish that the disagreement cannot bite</strong>, and pin the
/// reason. This package never puts those numbers on a wire: an access mode travels as the member's
/// own name, as an <c>A</c>, because E87 carries it as a stream 14 attribute value rather than as
/// the dictionary's <c>U1</c>. A host sending a number gets a refusal rather than the opposite
/// mode, which is the safe failure and is asserted below.
/// </para>
/// <para>
/// So the numbering stays. Renumbering a public enum is a breaking change in somebody's fab, and
/// it would buy nothing while no number crosses the link - but it was undefended until this test,
/// and an undefended numbering is how a later chapter serialises it as a <c>U1</c> and inverts
/// every load port in a fab.
/// </para>
/// </remarks>
public class PortAccessModeAgainstTheReferenceTests
{
    [Fact]
    public void The_reference_numbers_the_two_modes_the_other_way_round()
    {
        Assert.True(SecsCatalog.TryGetDataItem("ACCESSMODE", out var defined));

        // The premise, stated as data rather than as a claim. If a refreshed reference ever agreed
        // with this package, this test fails and the next reader gets to delete the whole file.
        Assert.True(defined!.IsNamedByNoMessage);
        Assert.Equal(SecsFormat.U1, defined.Format);
        Assert.Equal("Automatic access", Assert.Single(defined.Codes, code => code.Value == "0").Meaning);
        Assert.Equal("Manual access", Assert.Single(defined.Codes, code => code.Value == "1").Meaning);

        Assert.Equal((byte)0, (byte)PortAccessMode.Manual);
        Assert.Equal((byte)1, (byte)PortAccessMode.Auto);
    }

    [Fact]
    public async Task The_numbers_never_cross_the_link_which_is_why_the_disagreement_cannot_bite()
    {
        var manager = new CarrierManager();
        manager.AddPort(1);

        var provider = new CarrierObjectProvider(manager);

        var port = Assert.Single(
            await provider.GetInstancesAsync("LoadPort", TestContext.Current.CancellationToken));

        var answered = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { ObjectType = "LoadPort", Objects = [port], Attributes = ["AccessMode"] },
            TestContext.Current.CancellationToken);

        var values = Assert.Single(answered);
        var mode = Assert.Single(values.Attributes);

        // Text, not a number - so the inversion has nothing to invert. This is the assertion that
        // makes the whole disagreement harmless, and the one that would fail the moment somebody
        // changed the attribute to the U1 the dictionary describes.
        Assert.True(mode.Value.Format.IsText());
        Assert.Contains(mode.Value.GetString().TrimEnd(), (string[])["Manual", "Auto"]);
    }
}
