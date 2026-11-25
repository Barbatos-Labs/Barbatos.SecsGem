// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The attribute pair, S15F27 and S15F29, driven end to end.
/// </summary>
/// <remarks>
/// <para>
/// The two rows were refused for chapters on a measure called <em>two readings, different bytes</em>:
/// <c>RCPATTRVAL</c> is undefined and has two candidate readings that disagree - the RCP family's
/// own <c>RCPATTRDATA</c>, a defined <c>A[n]</c>, against E39's <c>ATTRDATA</c>, a defined
/// <c>L[n]</c> that ten rows name and three of them are served here. Every word of that is true.
/// </para>
/// <para>
/// <strong>And it decides nothing, because a SECS-II item carries its own format.</strong> Nothing
/// in this package chooses between text and a list: the item a host sends is stored whole and goes
/// back as itself, so the host that reads an attribute is the host that wrote it and the format
/// travels in the item header. That is <c>DSDATA</c>'s answer in stream 13, <c>ITEMDATA</c>'s in
/// stream 21 and the recipe lock token's in this very package. The test that matters for the whole
/// design is <see cref="A_list_goes_out_as_a_list_and_text_as_text"/>.
/// </para>
/// <para>
/// Which is also why the measure does not retire for every row that carried it: S15F51 and S20F1
/// ask for a <em>namespace</em> attribute, the reference gives that no set, and a machine minting a
/// value a host must parse really does have to choose. Those two are refused on producer and
/// reader now, which is where <c>RCPOWNER</c> keeps S15F49 out.
/// </para>
/// </remarks>
public sealed class RecipeAttributeTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down\n");

    [Fact(Timeout = 15_000)]
    public async Task An_attribute_a_host_set_comes_back_as_the_item_it_sent()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("night shift"), RecipePair.Token));

        var value = await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "OWNER", RecipePair.Token);

        Assert.NotNull(value);
        Assert.Equal("night shift", value!.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_list_goes_out_as_a_list_and_text_as_text()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // The finding, on a wire. One attribute is set as E39 would have it and the other as the
        // RCP family would, and both come back as what they were sent as - so the two readings the
        // refusal was built on never have to be chosen between.
        await pair.Host.SetAttributeAsync(
            "PROD",
            "ETCH-7",
            "LIMITS",
            SecsItem.L(SecsItem.U4(10u), SecsItem.U4(90u)),
            RecipePair.Token);

        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), RecipePair.Token);

        var limits = await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "LIMITS", RecipePair.Token);
        var owner = await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "OWNER", RecipePair.Token);

        Assert.NotNull(limits);
        Assert.True(limits!.IsList);
        Assert.Equal(2, limits.Count);
        Assert.Equal(90u, limits.Items[1].FirstValueOrDefault<uint>());

        Assert.NotNull(owner);
        Assert.False(owner!.IsList);
        Assert.Equal("days", owner.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_attribute_nobody_set_comes_back_as_nothing_rather_than_as_an_error()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // S15F28 carries no acknowledgement item, so a zero-length value is the only way the row
        // can say no - which is not an invention: S7F6, S7F26, S6F16, S6F20 and S6F28 all say no
        // that way, in three packages. The host turns it into a null so a caller can tell it from
        // an attribute that really is an empty list.
        Assert.Null(await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "OWNER", RecipePair.Token));
        Assert.Null(await pair.Host.RequestAttributeAsync("PROD", "NOT-THERE", "OWNER", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Setting_an_attribute_on_a_recipe_that_is_not_there_is_refused()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("x"), RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_locked_recipe_refuses_an_attribute_like_every_other_mutation()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        var (acknowledgement, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(token);

        // A lock whose consequences are this store's invention is worth nothing if one of them is
        // missing, and setting an attribute on a locked recipe is a mutation like a rename.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("x"), RecipePair.Token));

        // Reading one is not, which is the other half of what a lock means here.
        Assert.Null(await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "OWNER", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Attributes_are_part_of_the_recipe_through_a_rename_a_copy_and_a_delete()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), RecipePair.Token);

        // One rule for all three, stated rather than left to be found: the reference says nothing
        // about any of them, so what this store does is its own and is the same in each case.
        await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token);

        Assert.Equal(
            "days",
            (await pair.Host.RequestAttributeAsync("PROD", "ETCH-8", "OWNER", RecipePair.Token))!.GetString());

        Assert.Null(await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "OWNER", RecipePair.Token));

        await pair.Host.CopyAsync("PROD", "ETCH-8", "PROD", "ETCH-9", RecipePair.Token);

        Assert.Equal(
            "days",
            (await pair.Host.RequestAttributeAsync("PROD", "ETCH-9", "OWNER", RecipePair.Token))!.GetString());

        await pair.Host.DeleteAsync("PROD", "ETCH-8", RecipePair.Token);

        Assert.Null(await pair.Host.RequestAttributeAsync("PROD", "ETCH-8", "OWNER", RecipePair.Token));

        // And the copy keeps its own, which is what makes the delete a delete of one recipe's
        // attributes rather than of the name's.
        Assert.Equal(
            "days",
            (await pair.Host.RequestAttributeAsync("PROD", "ETCH-9", "OWNER", RecipePair.Token))!.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_body_that_is_not_the_rows_is_answered_by_the_only_thing_each_row_can_say()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        // S15F28 has no acknowledgement, so an unreadable S15F27 gets an empty attribute back; the
        // set has an RMACK and refuses. Two rows of one pair answering a bad body two different
        // ways, because the reference gave them different replies.
        using var getPrimary = new SecsMessage(15, 27, replyExpected: true)
        {
            Name = "RecipeAttributeGetRequest",
            Item = SecsItem.A("PROD"),
        };

        using var get = await pair.HostLink.SendAsync(getPrimary, RecipePair.Token);

        Assert.Equal(28, get.F);
        Assert.True(get.Item!.IsList);
        Assert.Equal(2, get.Item.Count);
        Assert.True(get.Item.Items[1] is { IsList: true, Count: 0 });

        using var setPrimary = new SecsMessage(15, 29, replyExpected: true)
        {
            Name = "RecipeAttributeSetRequest",
            Item = SecsItem.L(SecsItem.A("PROD"), SecsItem.A("ETCH-7")),
        };

        using var set = await pair.HostLink.SendAsync(setPrimary, RecipePair.Token);

        Assert.Equal(30, set.F);
        Assert.Equal((byte)RecipeAcknowledgement.Rejected, set.Item!.FirstValueOrDefault<byte>());
    }
}
