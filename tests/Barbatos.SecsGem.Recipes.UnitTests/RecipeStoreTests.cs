// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The rules <see cref="RecipeInMemoryStore"/> makes up, tested where they are made.
/// </summary>
/// <remarks>
/// <para>
/// These are this class's choices, not E5's: seven of the nine replies this package sends carry
/// one <c>RMACK</c> and no way to say why, so every refusal below is indistinguishable on the wire
/// from every other. A test against the store is the only place the <em>reason</em> can be pinned.
/// </para>
/// <para>
/// The two-level space is what makes this store different from the other three transfer stores
/// here: it can create and delete the container as well as the contents, so it has state that can
/// go inconsistent between two transactions.
/// </para>
/// </remarks>
public class RecipeStoreTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_grant_does_not_outlive_the_namespace_it_was_given_for()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
                Token));

        // Deleting it is allowed, because it holds no recipes - a grant is not a recipe.
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.DeleteNamespaceAsync("PROD", Token));

        // So the send has to be refused, and this is the rule that cannot be seen from the wire:
        // without it the recipe would land in a namespace S15F2 does not list, present to Find and
        // invisible to a host. Two transactions mean the machine's state can change in between.
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.SaveAsync("PROD", "ETCH-7", Body, Token));

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.GrantedCount);
    }

    [Fact]
    public async Task A_grant_is_for_one_recipe_and_does_not_license_another()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");

        await store.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
            Token);

        // Same namespace, same length, different identifier. A store that keyed the grant on the
        // namespace alone would write one recipe under another's name.
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.SaveAsync("PROD", "ETCH-8", Body, Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.SaveAsync("PROD", "ETCH-7", Body, Token));
    }

    [Fact]
    public async Task A_grant_is_for_one_namespace_and_does_not_license_another()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");
        store.AddNamespace("DEV");

        await store.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "DEV", Id = "ETCH-7", Length = (uint)Body.Length },
            Token);

        // The other half of the same rule, and the one that matters in a fab: a recipe granted for
        // the development namespace must not land in production.
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.SaveAsync("PROD", "ETCH-7", Body, Token));
        Assert.Null(store.Find("PROD", "ETCH-7"));
    }

    [Fact]
    public async Task A_recipe_exactly_the_length_it_declared_is_taken()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");
        store.MaximumLength = (uint)Body.Length;

        // The boundary, both ways: the maximum is inclusive, and the length check is an equality
        // rather than a limit - a recipe shorter than declared is as wrong as one longer, because
        // the declaration is what the machine reserved for.
        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await store.RequestDownloadAsync(
                new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
                Token));

        Assert.Equal(RecipeAcknowledgement.Rejected, await store.SaveAsync("PROD", "ETCH-7", Body[..3], Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.SaveAsync("PROD", "ETCH-7", Body, Token));
    }

    [Fact]
    public async Task A_namespace_a_recipe_was_seeded_into_is_listed()
    {
        var store = new RecipeInMemoryStore();

        // Add is how a simulator offers a recipe a host can go and fetch, since stream 15 has no
        // message announcing one - so it has to create the namespace too, or S15F2 would not list
        // the place the recipe is in.
        store.Add("PROD", "ETCH-7", Body);

        Assert.Equal<string[]>(["PROD"], [.. await store.ListNamespacesAsync(Token)]);
        Assert.True(store.HasNamespace("PROD"));
    }

    [Fact]
    public async Task A_namespace_emptied_by_hand_can_then_be_deleted()
    {
        var store = new RecipeInMemoryStore();
        store.Add("PROD", "ETCH-7", Body);

        Assert.Equal(RecipeAcknowledgement.Rejected, await store.DeleteNamespaceAsync("PROD", Token));

        Assert.Equal(RecipeAcknowledgement.Accepted, await store.DeleteAsync("PROD", "ETCH-7", Token));
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.DeleteNamespaceAsync("PROD", Token));
    }

    [Fact]
    public async Task A_namespace_that_is_not_there_is_not_deleted()
    {
        var store = new RecipeInMemoryStore();

        // Refused rather than silently accepted, because a host that got an acceptance would
        // believe a namespace it never had is now gone - and S15F18 cannot distinguish that from
        // the non-empty refusal above.
        Assert.Equal(RecipeAcknowledgement.Rejected, await store.DeleteNamespaceAsync("NOPE", Token));
    }

    [Fact]
    public async Task A_recipe_sent_twice_is_replaced_and_not_duplicated()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");

        var second = Encoding.ASCII.GetBytes("STEP 2");

        await store.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
            Token);
        await store.SaveAsync("PROD", "ETCH-7", Body, Token);

        await store.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)second.Length },
            Token);
        await store.SaveAsync("PROD", "ETCH-7", second, Token);

        // Replaced, which is what a recipe download means - there is no S15F7 that appends, and
        // the stream has no revision this package can read, RCPVER being undefined.
        Assert.Equal(1, store.Count);
        Assert.Equal<byte[]>(second, [.. store.Find("PROD", "ETCH-7")!.Body.Span]);
    }

    [Fact]
    public async Task The_same_identifier_in_two_namespaces_is_two_recipes()
    {
        var store = new RecipeInMemoryStore();
        store.Add("PROD", "ETCH-7", Body);
        store.Add("DEV", "ETCH-7", Encoding.ASCII.GetBytes("STEP 2"));

        // Which is the point of a namespace, and the thing streams 8, 13 and 21 cannot express:
        // their identifiers are flat, so a name is a name. Here the pair is the key.
        Assert.Equal(2, store.Count);

        var (_, production) = await store.LoadAsync("PROD", "ETCH-7", Token);
        var (_, development) = await store.LoadAsync("DEV", "ETCH-7", Token);

        Assert.Equal<byte[]>(Body, [.. production.Span]);
        Assert.NotEqual<byte[]>(Body, [.. development.Span]);

        // And deleting one leaves the other.
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.DeleteAsync("PROD", "ETCH-7", Token));
        Assert.NotNull(store.Find("DEV", "ETCH-7"));
    }

    [Fact]
    public async Task Selecting_a_recipe_in_one_namespace_does_not_select_the_other()
    {
        var store = new RecipeInMemoryStore();
        store.Add("PROD", "ETCH-7", Body);
        store.Add("DEV", "ETCH-7", Encoding.ASCII.GetBytes("STEP 2"));

        await store.SelectAsync("DEV", "ETCH-7", Token);

        Assert.Equal("DEV", store.Selected?.Namespace);

        // Deleting the one that was NOT selected leaves the selection alone - the check is on the
        // pair, not on the identifier, which is the mistake a flat store would make.
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.DeleteAsync("PROD", "ETCH-7", Token));
        Assert.Equal("DEV", store.Selected?.Namespace);
    }

    [Fact]
    public async Task An_abort_is_for_one_transfer_and_leaves_another_alone()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");

        await store.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)Body.Length },
            Token);
        await store.RequestDownloadAsync(
            new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-8", Length = (uint)Body.Length },
            Token);

        Assert.Equal(2, store.GrantedCount);
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.AbortAsync("PROD", "ETCH-7", Token));
        Assert.Equal(1, store.GrantedCount);

        // The one that was not aborted still completes.
        Assert.Equal(RecipeAcknowledgement.Accepted, await store.SaveAsync("PROD", "ETCH-8", Body, Token));
    }

    [Fact]
    public async Task A_cancelled_call_throws_rather_than_refusing()
    {
        var store = new RecipeInMemoryStore();
        store.AddNamespace("PROD");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Cancellation is not a refusal: a store that returned Rejected would tell the host its
        // recipe was rejected when in fact nothing was ever asked. Every method checks, and these
        // are the two that would do damage if they did not.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync("PROD", "ETCH-7", Body, cancelled.Token).AsTask());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.DeleteNamespaceAsync("PROD", cancelled.Token).AsTask());

        Assert.True(store.HasNamespace("PROD"));
    }
}
