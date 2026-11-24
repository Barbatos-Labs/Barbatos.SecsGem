// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// S15F35/F36 - which recipe is selected, the read-back this stream had no other way to give.
/// </summary>
/// <remarks>
/// <para>
/// Refused for a dozen chapters on a <em>shape</em>: no row this package serves answers a request
/// with a multi-item body carrying no acknowledgement, so there was said to be no sibling to read a
/// nesting off and no way to say "nothing is selected". The first half is true of this package and
/// was never true of the repository - S7F6 is a <c>PPID</c> and a <c>PPBODY</c>, flat, which is an
/// identifier and a second field exactly as this row is recorded. The second half is answered by
/// five served rows: <strong>a reply with no acknowledgement item says no with a zero-length
/// body</strong>, as S7F6, S7F26, S6F16, S6F20 and S6F28 all do.
/// </para>
/// <para>
/// What it is <em>for</em> is the argument that served S6F27 and S7F35: the package has served the
/// select since it shipped and had no way to ask what is selected, so a host that restarts could
/// set the machine up and never read it back. <c>RecipeInMemoryStore</c> has cleared its selection
/// on delete since that class was written, with a comment about the host that would otherwise be
/// told the machine is set up to run something deleted - and until this row went in, nothing could
/// be that host. <see cref="A_deleted_recipe_stops_being_the_selected_one_where_a_host_can_see_it"/>
/// is that comment's first test.
/// </para>
/// </remarks>
public class RecipeCurrentTests
{
    private static readonly byte[] Body = [1, 2, 3];

    // ----------------------------------------------------- the read-back

    [Fact(Timeout = 15_000)]
    public async Task The_current_recipe_comes_back_as_the_identifier_that_was_selected()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CreateNamespaceAsync("PROD", RecipePair.Token));

        var (grant, acknowledgement) = await pair.Host.DownloadAsync("PROD", "ETCH-7", Body, RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, grant);
        Assert.Equal(RecipeAcknowledgement.Accepted, acknowledgement);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token));

        var current = await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token);

        Assert.NotNull(current);
        Assert.Equal("ETCH-7", current!.Value.RecipeId);

        // No version, which is this store being honest rather than incomplete: nothing in stream 15
        // sets a recipe's version, so a store holding only what the wire gave it has none.
        Assert.Null(current.Value.Version);
    }

    [Fact(Timeout = 15_000)]
    public async Task Nothing_selected_is_an_empty_body_and_not_a_recipe_called_nothing()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CreateNamespaceAsync("PROD", RecipePair.Token));

        Assert.Null(await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token));

        // Read from the wire, because the distinction lives only there: S15F36 has no RMACK, so the
        // zero-length body is the whole of "nothing is selected". Returning a selection with an
        // empty identifier would tell a host the machine is set up to run a recipe called "".
        using var primary = new SecsMessage(15, 35, replyExpected: true)
        {
            Name = "CurrentRecipeRequest",
            Item = SecsItem.A("PROD").Named("RCPSPEC"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(36, reply.F);
        Assert.True(reply.Item!.IsList);
        Assert.Equal(0, reply.Item.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_selection_in_one_namespace_is_not_another_namespaces_answer()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        foreach (var space in new[] { "PROD", "DEV" })
        {
            Assert.Equal(
                RecipeAcknowledgement.Accepted,
                await pair.Host.CreateNamespaceAsync(space, RecipePair.Token));
        }

        var (grant, _) = await pair.Host.DownloadAsync("PROD", "ETCH-7", Body, RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, grant);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token));

        // S15F35 carries one RCPSPEC and asks about it. A host asking about an empty namespace must
        // not be told about a recipe in another one - which is the whole reason the store's answer
        // is per namespace rather than "whatever is selected".
        Assert.Equal("ETCH-7", (await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token))!.Value.RecipeId);
        Assert.Null(await pair.Host.RequestCurrentRecipeAsync("DEV", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_deleted_recipe_stops_being_the_selected_one_where_a_host_can_see_it()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.CreateNamespaceAsync("PROD", RecipePair.Token));

        var (grant, _) = await pair.Host.DownloadAsync("PROD", "ETCH-7", Body, RecipePair.Token);

        Assert.Equal(RecipeAcknowledgement.Accepted, grant);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.SelectAsync("PROD", "ETCH-7", RecipePair.Token));

        Assert.NotNull(await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token));

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        // The store has cleared its selection on delete since it was written, and the comment
        // saying why names a host that could read it afterwards. This row is that host.
        Assert.Null(await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token));
    }

    // ----------------------------------------------------- the version, carried untouched

    [Fact(Timeout = 15_000)]
    public async Task The_version_goes_out_as_the_item_the_store_kept()
    {
        await using var pair = RecipePair.Start(store: new VersionedStore());
        await pair.OpenAsync();

        var current = await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token);

        Assert.NotNull(current);
        Assert.Equal("ETCH-7", current!.Value.RecipeId);

        // The test the whole design turns on, and the lock's own test's twin: a store that keeps a
        // number gets a number back. RCPVER is undefined, so nothing here chooses a format - and a
        // library that read it as text would hand the host a string where a U4 went in.
        var version = current.Value.Version;

        Assert.NotNull(version);
        Assert.Equal(SecsFormat.U4, version!.Format);
        Assert.Equal(1, version.Count);

        using var decoded = version.Decode();

        Assert.NotNull(decoded);
        Assert.Equal(7u, decoded!.FirstValueOrDefault<uint>());
    }

    [Fact(Timeout = 15_000)]
    public void A_version_that_arrived_as_text_prints_as_the_text()
    {
        // A version is a label a person reads, so its ToString is the text when there is text to
        // print - which is the one place this type differs from RecipeLockToken, whose value means
        // nothing to anybody and whose ToString is its shape.
        Assert.Equal("1.4.2", RecipeVersion.FromText("1.4.2").ToString());

        using var number = SecsItem.U4(7u);
        var opaque = RecipeVersion.From(number);

        Assert.Contains("U4", opaque.ToString(), StringComparison.Ordinal);
        Assert.False(opaque.IsEmpty);

        // An item that carried no values is empty, and an encoded empty item is still bytes - the
        // distinction the lock token's own remarks record.
        using var blank = SecsItem.A(string.Empty);

        Assert.True(RecipeVersion.From(blank).IsEmpty);
        Assert.False(RecipeVersion.From(blank).Encoded.IsEmpty);
        Assert.True(RecipeVersion.FromText("x").Matches(RecipeVersion.FromText("x")));
        Assert.False(RecipeVersion.FromText("x").Matches(null));
    }

    // ----------------------------------------------------- what cannot be read

    [Fact(Timeout = 15_000)]
    public async Task A_request_this_end_cannot_read_answers_the_same_empty_body_and_logs_it()
    {
        await using var pair = RecipePair.Start();
        await pair.OpenAsync();

        // A body with nothing in it, which is what unreadable has to mean here: a namespace arrives
        // in whatever format the host chose, and this package reads a number as the digits of a
        // name - so a U4 9 is the namespace "9" and perfectly readable. Only an empty item is not.
        using var primary = new SecsMessage(15, 35, replyExpected: true)
        {
            Name = "CurrentRecipeRequest",
            Item = SecsItem.L(),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        // The row has no code to tell "I could not read that" from "nothing is selected", so both
        // are the empty body and the log is the only place they differ. Assert the log, or the line
        // can be deleted and nothing notices.
        Assert.Equal(0, reply.Item!.Count);

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F35", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_empty_identifier_in_the_reply_is_neither_a_selection_nor_nothing()
    {
        await using var pair = RecipePair.Start(
            answerEquipment: transaction => transaction.Message is { S: 15, F: 35 }
                ? new SecsMessage(15, 36, replyExpected: false)
                {
                    Item = SecsItem.L(
                        SecsItem.A(string.Empty).Named("RCPID"),
                        SecsItem.L().Named("RCPVER")),
                }
                : null);

        await pair.OpenAsync();

        // A machine played by hand, because this library never writes this body: an RCPID that is
        // there and empty. The host reads it as nothing rather than as a recipe called "", and says
        // so - a selection with no name is a thing a host would go on to ask about.
        Assert.Null(await pair.Host.RequestCurrentRecipeAsync("PROD", RecipePair.Token));

        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("empty RCPID", StringComparison.Ordinal));
    }

    /// <summary>A store that keeps a version, which the in-memory one has no way to be given.</summary>
    private sealed class VersionedStore : IRecipeStore
    {
        public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>(["PROD"]);

        public ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(string recipeNamespace, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(string recipeNamespace, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<RecipeAcknowledgement> RequestDownloadAsync(RecipeDownloadRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<RecipeAcknowledgement> SaveAsync(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult((RecipeAcknowledgement.Rejected, default(ReadOnlyMemory<byte>)));

        public ValueTask<RecipeAcknowledgement> DeleteAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<RecipeAcknowledgement> SelectAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<RecipeAcknowledgement> AbortAsync(string recipeNamespace, string recipeId, CancellationToken cancellationToken)
            => ValueTask.FromResult(RecipeAcknowledgement.Accepted);

        public ValueTask<(string RecipeId, RecipeVersion? Version)?> CurrentAsync(
            string recipeNamespace,
            CancellationToken cancellationToken)
        {
            using var item = SecsItem.U4(7u);

            return ValueTask.FromResult<(string, RecipeVersion?)?>(("ETCH-7", RecipeVersion.From(item)));
        }
    }
}
