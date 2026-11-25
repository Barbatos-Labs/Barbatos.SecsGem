// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;
using System.Text;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The recipe owner, S15F49, and the namespace attribute, S15F51 - both driven end to end.
/// </summary>
/// <remarks>
/// <para>
/// Two rows that had been refused under one measure, <strong>producer and reader</strong>: an
/// undefined field may be carried opaquely when the end that must read it is the end that produced
/// it, and these two were read as failing that because they cross from one end to the other. The
/// measure is right; it does not reach either row, because in neither case does anything have to
/// read the field.
/// </para>
/// <para>
/// <strong>Nothing reads an owner back.</strong> <c>RCPOWNER</c> is named by exactly one row in
/// the whole reference - this one - so there is no message anywhere that asks who owns a recipe,
/// and E5 never says what owning one prevents. The store records the item and the application
/// decides what ownership means, which is the recipe lock's own position: a transaction the
/// standard defines and whose consequences it leaves out.
/// </para>
/// <para>
/// <strong>Nothing mints a namespace attribute.</strong> That row was refused because the
/// reference gives a namespace attribute no set, so the machine would be minting a value a host
/// must parse - and the counter-example is in this repository: stream 21's item attributes have no
/// set row either, <c>ATTRDATA</c> appearing in one row of stream 21 and that row being the reply.
/// <c>Barbatos.SecsGem.ItemTransfer</c> serves the get and the value comes off its store as an
/// item the application put there. So does this one.
/// </para>
/// <para>
/// Which makes both rows rest on the finding that served the attribute pair: <strong>a SECS-II
/// item carries its own format</strong>. <c>RCPOWNER</c>'s rival is the sharpest in the reference -
/// <c>RCPOWCODE</c>, a defined <c>BOOLEAN[1]</c> named by no row, against a name - and
/// <see cref="An_owner_is_whatever_item_the_host_sent_and_the_rival_reading_costs_nothing"/> sends
/// both readings down one wire.
/// </para>
/// </remarks>
public sealed class RecipeOwnerTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down\n");

    [Fact(Timeout = 15_000)]
    public async Task An_owner_a_host_sets_is_the_item_the_store_keeps()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        using var owner = SecsItem.A("mes-7");

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SetOwnerAsync("PROD", "ETCH-7", owner, RecipePair.Token));

        var held = pair.Store.Owner("PROD", "ETCH-7");

        Assert.NotNull(held);
        Assert.Equal("mes-7", held!.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_owner_is_whatever_item_the_host_sent_and_the_rival_reading_costs_nothing()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "BY-NAME", Body);
        pair.Store.Add("PROD", "BY-FLAG", Body);

        await pair.OpenAsync();

        // The whole of why this row could be served. RCPOWNER is undefined and the dictionary's
        // RCPOWCODE is a BOOLEAN, so the reference can be read as wanting a name or as wanting a
        // flag - two readings that disagree about every byte. Neither is chosen anywhere in this
        // package: both arrive as themselves and both come back as themselves.
        using var name = SecsItem.A("mes-7");
        using var flag = SecsItem.Boolean(true);

        await pair.Host.SetOwnerAsync("PROD", "BY-NAME", name, RecipePair.Token);
        await pair.Host.SetOwnerAsync("PROD", "BY-FLAG", flag, RecipePair.Token);

        var byName = pair.Store.Owner("PROD", "BY-NAME");
        var byFlag = pair.Store.Owner("PROD", "BY-FLAG");

        Assert.Equal(SecsFormat.ASCII, byName!.Format);
        Assert.Equal("mes-7", byName.GetString());

        Assert.Equal(SecsFormat.Boolean, byFlag!.Format);
        Assert.True(byFlag.GetSpan<bool>()[0]);
    }

    [Fact(Timeout = 20_000)]
    public async Task An_owner_larger_than_the_codecs_pool_threshold_survives_the_message_it_came_in()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ONE", Body);
        pair.Store.Add("PROD", "TWO", Body);

        await pair.OpenAsync();

        // A B[n] above SecsCodecOptions.PooledPayloadThreshold, because text cannot fail this: an
        // A[n] decodes into a string, which owns nothing and survives any dispose. Only an array
        // format over a kilobyte borrows a buffer the codec hands back when the message goes.
        var first = new byte[4096];
        var second = new byte[4096];

        first.AsSpan().Fill(0xA1);
        second.AsSpan().Fill(0xB2);

        using (var one = SecsItem.B(first))
        using (var two = SecsItem.B(second))
        {
            await pair.Host.SetOwnerAsync("PROD", "ONE", one, RecipePair.Token);
            await pair.Host.SetOwnerAsync("PROD", "TWO", two, RecipePair.Token);
        }

        // TWO owners and not one, because ArrayPool keeps a single buffer per bucket in the
        // returning thread's own slot where nobody else can rent it - and the thread that disposes
        // a message is never the test's. One large value hides in that slot; two do not.
        Churn();

        var held = pair.Store.Owner("PROD", "ONE");

        Assert.NotNull(held);
        Assert.Equal(4096, held!.Count);
        Assert.All(held.GetSpan<byte>().ToArray(), b => Assert.Equal(0xA1, b));

        var other = pair.Store.Owner("PROD", "TWO");

        Assert.Equal(4096, other!.Count);
        Assert.All(other.GetSpan<byte>().ToArray(), b => Assert.Equal(0xB2, b));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_recipe_that_is_not_there_and_one_that_is_locked_both_refuse_an_owner()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        using var owner = SecsItem.A("mes-7");

        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SetOwnerAsync("PROD", "NOT-THERE", owner, RecipePair.Token));

        var (code, token) = await pair.Host.LockAsync("PROD", "ETCH-7", RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, code);

        // The lock's consequences are this store's invention, every one of them - E5 defines the
        // transaction and never says what it blocks - so a lock that stopped a rename and let an
        // owner through would be a lock with a hole in it.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SetOwnerAsync("PROD", "ETCH-7", owner, RecipePair.Token));

        Assert.Null(pair.Store.Owner("PROD", "ETCH-7"));

        await pair.Host.UnlockAsync("PROD", "ETCH-7", token!, RecipePair.Token);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SetOwnerAsync("PROD", "ETCH-7", owner, RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_owner_follows_its_recipe_through_a_rename_a_copy_and_a_delete()
    {
        await using var pair = RecipePair.Start();

        pair.Store.AddNamespace("DEV");
        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        using var owner = SecsItem.A("mes-7");

        await pair.Host.SetOwnerAsync("PROD", "ETCH-7", owner, RecipePair.Token);
        await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token);

        Assert.Null(pair.Store.Owner("PROD", "ETCH-7"));
        Assert.Equal("mes-7", pair.Store.Owner("PROD", "ETCH-8")!.GetString());

        await pair.Host.CopyAsync("PROD", "ETCH-8", "DEV", "ETCH-8", RecipePair.Token);

        Assert.Equal("mes-7", pair.Store.Owner("DEV", "ETCH-8")!.GetString());
        Assert.Equal("mes-7", pair.Store.Owner("PROD", "ETCH-8")!.GetString());

        await pair.Host.DeleteAsync("PROD", "ETCH-8", RecipePair.Token);

        Assert.Null(pair.Store.Owner("PROD", "ETCH-8"));
        Assert.Equal("mes-7", pair.Store.Owner("DEV", "ETCH-8")!.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_attribute_the_application_set_is_what_the_host_reads()
    {
        await using var pair = RecipePair.Start();

        // The only door there is, which is the point: no row in the reference sets a namespace
        // attribute, so a store without this method could answer S15F51 with nothing at all. That
        // is ItemStore.Add's position in stream 21 and ControlProgramStore.Add's in stream 8.
        pair.Store.SetNamespaceAttribute("PROD", "DESCRIPTION", SecsItem.A("production recipes"));
        pair.Store.SetNamespaceAttribute("PROD", "QUOTA", SecsItem.U4(64));

        await pair.OpenAsync();

        using var description = await pair.Host.RequestNamespaceAttributeAsync(
            "PROD",
            "DESCRIPTION",
            RecipePair.Token);

        Assert.Equal("production recipes", description!.GetString());

        using var quota = await pair.Host.RequestNamespaceAttributeAsync("PROD", "QUOTA", RecipePair.Token);

        Assert.Equal(SecsFormat.U4, quota!.Format);
        Assert.Equal(64u, quota.GetSpan<uint>()[0]);

        // And setting one creates the namespace, so the order of two configuration calls does not
        // matter. S15F1 lists it afterwards.
        Assert.Contains("PROD", await pair.Host.ListNamespacesAsync(RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_that_is_not_there_and_an_attribute_nobody_set_are_the_same_answer()
    {
        await using var pair = RecipePair.Start();

        pair.Store.SetNamespaceAttribute("PROD", "DESCRIPTION", SecsItem.A("production recipes"));

        await pair.OpenAsync();

        // S15F52 carries a name and a value and no acknowledgement item, so there is no code to
        // tell these apart - the row's own doing, and the same way five served rows in three
        // packages say no.
        Assert.Null(await pair.Host.RequestNamespaceAttributeAsync("DEV", "DESCRIPTION", RecipePair.Token));
        Assert.Null(await pair.Host.RequestNamespaceAttributeAsync("PROD", "QUOTA", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_delete_takes_its_attributes_with_it()
    {
        await using var pair = RecipePair.Start();

        pair.Store.SetNamespaceAttribute("DEV", "DESCRIPTION", SecsItem.A("scratch"));

        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteNamespaceAsync("DEV", RecipePair.Token));

        pair.Store.AddNamespace("DEV");

        // A namespace created again under the same name is a new one, and would otherwise answer
        // with what the last one had. That is the delete-and-rename rule a level up.
        Assert.Null(await pair.Host.RequestNamespaceAttributeAsync("DEV", "DESCRIPTION", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_attribute_the_reply_carried_is_still_the_stores_afterwards()
    {
        await using var pair = RecipePair.Start();

        // **The source item has to be a POOLED one**, which is the thing the first draft of this
        // test got wrong. SecsItem.B over an array the test owns borrows nothing, so a reply that
        // disposed it would destroy nothing and the control passed with the copy removed. An item
        // the CODEC decoded is the pooled kind - which is also the only kind a wire can produce,
        // and there is no wire that sets a namespace attribute, so this hazard reaches a store
        // only through an application that hands over a decoded item. It is still a hazard, and
        // RecipeCodec.AttributeBody is shared with S15F28 where a host does produce one.
        var first = new byte[4096];
        var second = new byte[4096];

        first.AsSpan().Fill(0xC3);
        second.AsSpan().Fill(0xD4);

        using (var one = SecsItem.B(first))
        using (var two = SecsItem.B(second))
        {
            pair.Store.SetNamespaceAttribute("PROD", "BLOB", SecsItem.Decode(one.ToByteArray()));
            pair.Store.SetNamespaceAttribute("DEV", "BLOB", SecsItem.Decode(two.ToByteArray()));
        }

        await pair.OpenAsync();

        // Two reads and not one, because ArrayPool keeps a single buffer per bucket in the
        // returning thread's own slot, and the thread that disposes a reply is never the test's.
        using (await pair.Host.RequestNamespaceAttributeAsync("PROD", "BLOB", RecipePair.Token))
        using (await pair.Host.RequestNamespaceAttributeAsync("DEV", "BLOB", RecipePair.Token))
        {
        }

        Churn();

        // Read off the store directly, which is what the reply would have eaten.
        var held = await pair.Store.GetNamespaceAttributeAsync("PROD", "BLOB", RecipePair.Token);
        var other = await pair.Store.GetNamespaceAttributeAsync("DEV", "BLOB", RecipePair.Token);

        Assert.Equal(4096, held!.Count);
        Assert.All(held.GetSpan<byte>().ToArray(), b => Assert.Equal(0xC3, b));
        Assert.All(other!.GetSpan<byte>().ToArray(), b => Assert.Equal(0xD4, b));
    }

    [Fact(Timeout = 20_000)]
    public async Task A_namespace_attribute_the_host_reads_outlives_the_reply_it_came_in()
    {
        await using var pair = RecipePair.Start();

        var first = new byte[4096];
        var second = new byte[4096];

        first.AsSpan().Fill(0xE5);
        second.AsSpan().Fill(0xF6);

        pair.Store.SetNamespaceAttribute("PROD", "BLOB", SecsItem.B(first));
        pair.Store.SetNamespaceAttribute("DEV", "BLOB", SecsItem.B(second));

        await pair.OpenAsync();

        // The other end of the same rule: RecipeHost disposes the reply before it returns, so an
        // item handed back borrowed is already in the pool by the time the caller sees it. Both
        // values are kept across a churn, because one would hide in the returning thread's slot.
        var held = await pair.Host.RequestNamespaceAttributeAsync("PROD", "BLOB", RecipePair.Token);
        var other = await pair.Host.RequestNamespaceAttributeAsync("DEV", "BLOB", RecipePair.Token);

        Churn();

        Assert.All(held!.GetSpan<byte>().ToArray(), b => Assert.Equal(0xE5, b));
        Assert.All(other!.GetSpan<byte>().ToArray(), b => Assert.Equal(0xF6, b));

        held.Dispose();
        other.Dispose();
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_that_implements_neither_row_refuses_one_and_empties_the_other()
    {
        await using var pair = RecipePair.Start(store: new BareStore());

        await pair.OpenAsync();

        using var owner = SecsItem.A("mes-7");

        // Both members are default implementations on the interface, so a store written before
        // this chapter still compiles and still answers: the owner set refuses with the one code
        // S15F50 has, and the namespace attribute comes back with nothing in it.
        Assert.Equal(
            RecipeAcknowledgement.Rejected,
            await pair.Host.SetOwnerAsync("PROD", "ETCH-7", owner, RecipePair.Token));

        Assert.Null(await pair.Host.RequestNamespaceAttributeAsync("PROD", "DESCRIPTION", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_body_neither_row_can_read_is_answered_the_way_its_own_reply_allows()
    {
        await using var pair = RecipePair.Start();

        await pair.OpenAsync();

        // S15F50 has an RMACK, so a body the machine cannot read is a refusal with a log line
        // beside it. S15F52 has no acknowledgement item at all, so the same mistake comes back as
        // the attribute it asked for with nothing in it, and an empty name.
        using var owner = await pair.HostLink.SendAsync(
            new SecsMessage(15, 49, replyExpected: true) { Item = SecsItem.A("not a list") },
            RecipePair.Token);

        Assert.Equal((byte)RecipeAcknowledgement.Rejected, owner.Item!.GetSpan<byte>()[0]);

        using var attribute = await pair.HostLink.SendAsync(
            new SecsMessage(15, 51, replyExpected: true) { Item = SecsItem.L(SecsItem.A("PROD")) },
            RecipePair.Token);

        Assert.True(attribute.Item is { IsList: true, Count: 2 });
        Assert.Equal(string.Empty, attribute.Item!.Items[0].GetString());
        Assert.True(attribute.Item.Items[1] is { IsList: true, Count: 0 });
    }

    // Rents and returns enough buffers of the right size class to overwrite anything the codec
    // handed back, so a borrowed view shows as corruption rather than as a value that happens to
    // still be there.
    private static void Churn()
    {
        List<byte[]> rented = [];

        for (var i = 0; i < 64; i++)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);

            buffer.AsSpan().Fill((byte)'z');
            rented.Add(buffer);
        }

        foreach (var buffer in rented)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // A store with no overrides at all, which is how the two default implementations get driven
    // over a wire rather than called directly.
    private sealed class BareStore : IRecipeStore
    {
        public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(
            string recipeNamespace,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(
            string recipeNamespace,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> RequestDownloadAsync(
            RecipeDownloadRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> SaveAsync(
            string recipeNamespace,
            string recipeId,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult((RecipeAcknowledgement.Rejected, ReadOnlyMemory<byte>.Empty));

        public ValueTask<RecipeAcknowledgement> DeleteAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> SelectAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);

        public ValueTask<RecipeAcknowledgement> AbortAsync(
            string recipeNamespace,
            string recipeId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Rejected);
    }
}
