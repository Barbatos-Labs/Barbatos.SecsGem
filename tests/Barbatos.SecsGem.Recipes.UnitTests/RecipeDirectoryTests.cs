// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Text;
using System.Buffers;
using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The recipe directory, S15F3, driven end to end.
/// </summary>
/// <remarks>
/// <para>
/// The row was refused on a <strong>shape</strong>: a directory is a list of entries, and a list
/// has as many readings as there are nestings where a flat body has one. Every item of S15F4 was
/// already carried in this package - <c>RCPID</c> everywhere, <c>RCPVER</c> untouched at S15F36,
/// <c>LENGTH</c> read at S15F5, the attribute pair at S15F27 and S15F29 - so the nesting really
/// was the whole of what was left.
/// </para>
/// <para>
/// <strong>And a nesting here has never come from the catalogue's item list.</strong> It comes from
/// a row this repository already serves, playing the same part in the same kind of pair - which for
/// a directory reply is <c>S7F36</c>, the formatted process program directory: a list of entries,
/// one per program, each entry being its row's items in order and ending in a nested list of the
/// group that repeats. This is that with a recipe's columns, and
/// <see cref="A_directory_entry_is_the_shape_S7F36_writes"/> is where the two are compared item by
/// item on a wire.
/// </para>
/// <para>
/// What the row buys is the one thing a host could not do before: find out what a namespace holds.
/// S15F1 lists the namespaces and nothing listed the recipes, so a host had to be told their
/// identifiers out of band - which is why <c>RecipeInMemoryStore.Add</c> used to carry a remark
/// saying so.
/// </para>
/// </remarks>
public sealed class RecipeDirectoryTests
{
    private static readonly byte[] Body = Encoding.ASCII.GetBytes("STEP 1: pump down\n");
    private static readonly byte[] Longer = Encoding.ASCII.GetBytes("STEP 1: pump down\nSTEP 2: strike\n");

    [Fact(Timeout = 15_000)]
    public async Task A_directory_lists_the_recipes_of_one_namespace_with_their_lengths()
    {
        await using var pair = RecipePair.Start();

        pair.Store.AddNamespace("DEV");
        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.Add("PROD", "CLEAN-1", Longer);
        pair.Store.Add("DEV", "TRY-1", Body);

        await pair.OpenAsync();

        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);

        // Sorted by identifier, which is the store's choice and the reason it is stated: a host
        // diffing two answers needs the order to be the same both times.
        Assert.Equal<string[]>(["CLEAN-1", "ETCH-7"], [.. directory.Select(entry => entry.Id)]);
        Assert.Equal((uint)Longer.Length, directory[0].Length);
        Assert.Equal((uint)Body.Length, directory[1].Length);

        // One namespace only. The recipe in DEV is not in PROD's directory, which is the two-level
        // space this stream is the only one here to have.
        Assert.DoesNotContain(directory, entry => entry.Id == "TRY-1");
    }

    [Fact(Timeout = 15_000)]
    public async Task A_directory_entry_is_the_shape_S7F36_writes()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.SetAttributeAsync(
            "PROD",
            "ETCH-7",
            "LIMITS",
            SecsItem.L(SecsItem.U4(10u), SecsItem.U4(90u)),
            RecipePair.Token);

        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), RecipePair.Token);

        // Read off the wire rather than through the host method, because the nesting is the whole
        // claim of this chapter and a typed result would hide it.
        using var primary = new SecsMessage(15, 3, replyExpected: true)
        {
            Name = "RecipeDirectoryRequest",
            Item = SecsItem.A("PROD").Named("RCPSPEC"),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(4, reply.F);
        Assert.True(reply.Item!.IsList);
        Assert.Equal(1, reply.Item.Count);

        var entry = reply.Item.Items[0];

        // L,4 { RCPID, RCPVER, LENGTH, L,n { L,2 { RCPATTRNAME, RCPATTRVAL } } } - S7F36's entry
        // with a recipe's columns, where S7F36 writes PPID, MDLN, SOFTREV and a nested list.
        Assert.True(entry.IsList);
        Assert.Equal(4, entry.Count);
        Assert.Equal("ETCH-7", entry.Items[0].GetString());
        Assert.True(entry.Items[1] is { IsList: true, Count: 0 });
        Assert.Equal((uint)Body.Length, entry.Items[2].FirstValueOrDefault<uint>());

        var attributes = entry.Items[3];

        Assert.True(attributes.IsList);
        Assert.Equal(2, attributes.Count);

        // Sorted by name, so LIMITS comes before OWNER - and the pair inside is written by the
        // same method S15F28 uses, which is what stops the two rows disagreeing about the order.
        Assert.Equal("LIMITS", attributes.Items[0].Items[0].GetString());
        Assert.True(attributes.Items[0].Items[1].IsList);
        Assert.Equal(2, attributes.Items[0].Items[1].Count);

        Assert.Equal("OWNER", attributes.Items[1].Items[0].GetString());
        Assert.Equal("days", attributes.Items[1].Items[1].GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_attribute_goes_into_the_directory_as_the_item_it_was_set_with()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.SetAttributeAsync(
            "PROD",
            "ETCH-7",
            "LIMITS",
            SecsItem.L(SecsItem.U4(10u), SecsItem.U4(90u)),
            RecipePair.Token);

        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), RecipePair.Token);

        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);

        var entry = Assert.Single(directory);

        Assert.Equal<string[]>(["LIMITS", "OWNER"], [.. entry.Attributes.Select(attribute => attribute.Name)]);

        // The thesis of the attribute chapter, carried into this row: a list arrives as a list and
        // text as text, because a SECS-II item carries its own format and nothing here chooses.
        Assert.True(entry.Attributes[0].Value.IsList);
        Assert.Equal(90u, entry.Attributes[0].Value.Items[1].FirstValueOrDefault<uint>());

        Assert.False(entry.Attributes[1].Value.IsList);
        Assert.Equal("days", entry.Attributes[1].Value.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_value_larger_than_the_codecs_pool_threshold_survives_the_reply_it_came_in()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        // The defect writing the second reader found, and the format matters: a payload of
        // PooledPayloadThreshold bytes or more - 1024 by default - is decoded into a buffer the
        // codec hands back to ArrayPool when the message is disposed, so an item read straight out
        // of a reply is only safe while that reply is. **Text never takes that path** - an A[n]
        // decodes into a StringItem holding a string, pooled or interned but never borrowed - so
        // the first draft of this test used a 4 KiB A[n] and could not fail. An attribute is
        // whatever item a host sent, and a B[n] is one of them.
        var large = new byte[4096];

        large.AsSpan().Fill((byte)'x');

        // Two of them, because ArrayPool keeps one buffer per bucket in the returning thread's own
        // slot and the rest where anybody can rent them - and the thread that disposes a message is
        // not this one. One large attribute hides inside that slot; the second cannot.
        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "NOTE", SecsItem.B(large), RecipePair.Token);
        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "ALSO", SecsItem.B(large), RecipePair.Token);

        // Read once each way first, because that read is also what would destroy a store's own
        // item: an item handed to a message is disposed with it, so a reply carrying the store's
        // value straight out leaves the store holding a buffer that is back in the pool.
        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);
        var kept = await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "NOTE", RecipePair.Token);

        // Then the pool is churned by hand rather than by another exchange, because a second
        // exchange would decode the same bytes into the same size class and write the value back
        // unchanged - which is how the first draft of this test passed. Renting the whole bucket
        // and filling it is what any later decode of a different payload would do to a buffer that
        // had been handed back.
        Churn();

        // Four readers, and they fail for three different mistakes. The two AFTER the churn are
        // what the equipment still holds: they catch a store whose item was disposed on its way out
        // and a store that kept the item the host's own message arrived in. The two from BEFORE it
        // are the host's copies, and they catch a reply handed back borrowed.
        using var again = (await pair.Host.RequestAttributeAsync("PROD", "ETCH-7", "NOTE", RecipePair.Token))!;
        var directoryAgain = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);

        var entry = Assert.Single(directory);
        var entryAgain = Assert.Single(directoryAgain);

        Assert.All(entryAgain.Attributes, attribute => Assert.Equal(large, attribute.Value.GetSpan<byte>().ToArray()));
        Assert.Equal(large, again.GetSpan<byte>().ToArray());
        Assert.All(entry.Attributes, attribute => Assert.Equal(large, attribute.Value.GetSpan<byte>().ToArray()));
        Assert.Equal(large, kept!.GetSpan<byte>().ToArray());

        kept.Dispose();
        foreach (var attribute in entry.Attributes) { attribute.Value.Dispose(); }
        foreach (var attribute in entryAgain.Attributes) { attribute.Value.Dispose(); }
    }

    [Fact(Timeout = 15_000)]
    public async Task A_namespace_with_no_recipes_and_one_that_is_not_there_are_the_same_body()
    {
        await using var pair = RecipePair.Start();

        pair.Store.AddNamespace("EMPTY");

        await pair.OpenAsync();

        // S15F4 carries no acknowledgement item, so there is no code to tell the two apart - the
        // convention S7F6, S7F26, S6F16, S6F20 and S6F28 all use. The store knows the difference
        // and the wire cannot, which is said on IRecipeStore.ListRecipesAsync rather than left to
        // be discovered.
        Assert.Empty(await pair.Host.RequestDirectoryAsync("EMPTY", RecipePair.Token));
        Assert.Empty(await pair.Host.RequestDirectoryAsync("NOBODY", RecipePair.Token));

        Assert.NotNull(await pair.Store.ListRecipesAsync("EMPTY", RecipePair.Token));
        Assert.Null(await pair.Store.ListRecipesAsync("NOBODY", RecipePair.Token));

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("NOBODY", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_store_that_serves_no_directory_answers_an_empty_body()
    {
        // The default implementation, which is what a store written against an earlier version of
        // IRecipeStore gets: null, and therefore a zero-length body. The row is declared either
        // way, because a host asking must not get an S9F5 from a machine that has the stream.
        await using var pair = RecipePair.Start(store: new DirectorylessStore());

        await pair.OpenAsync();

        Assert.Empty(await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_request_this_end_cannot_read_is_answered_and_logged()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        using var primary = new SecsMessage(15, 3, replyExpected: true)
        {
            Name = "RecipeDirectoryRequest",
            Item = SecsItem.L(SecsItem.A("PROD"), SecsItem.A("ETCH-7")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, RecipePair.Token);

        Assert.Equal(4, reply.F);
        Assert.True(reply.Item is { IsList: true, Count: 0 });

        Assert.Contains(
            pair.EquipmentLog.From(LogLevel.Warning),
            message => message.Contains("S15F3", StringComparison.Ordinal)
                && message.Contains("could not read as a namespace", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_entry_with_no_identifier_is_dropped_and_counted()
    {
        // A machine that sends a directory row naming no recipe has said nothing about anything,
        // which is the reading S15F36 gives an empty RCPID. Played by hand, because neither store
        // here would write one.
        await using var pair = RecipePair.Start(answerEquipment: transaction => transaction.F == 3
            ? new SecsMessage(15, 4, replyExpected: false)
            {
                Name = "RecipeDirectoryData",
                Item = SecsItem.L(
                    SecsItem.L(SecsItem.A("ETCH-7"), SecsItem.L(), SecsItem.U4(18u), SecsItem.L()),
                    SecsItem.L(SecsItem.A(string.Empty), SecsItem.L(), SecsItem.U4(0u), SecsItem.L())),
            }
            : null);

        await pair.OpenAsync();

        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);

        Assert.Equal("ETCH-7", Assert.Single(directory).Id);

        Assert.Contains(
            pair.HostLog.From(LogLevel.Warning),
            message => message.Contains("S15F4 carried 2 entries", StringComparison.Ordinal));
    }

    [Fact(Timeout = 15_000)]
    public async Task A_short_entry_is_read_for_what_it_has()
    {
        // The other direction, and lenient on purpose: a machine that keeps no versions, no lengths
        // or no attributes has nothing to put in those slots, and S15F4 has no acknowledgement item
        // to say so with. A version that did arrive is kept as it came, the way S15F36 keeps one.
        await using var pair = RecipePair.Start(answerEquipment: transaction => transaction.F == 3
            ? new SecsMessage(15, 4, replyExpected: false)
            {
                Name = "RecipeDirectoryData",
                Item = SecsItem.L(
                    SecsItem.L(SecsItem.A("ETCH-7")),
                    SecsItem.L(SecsItem.A("CLEAN-1"), SecsItem.A("rev-3"))),
            }
            : null);

        await pair.OpenAsync();

        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);

        Assert.Equal(2, directory.Count);

        Assert.Null(directory[0].Version);
        Assert.Equal(0u, directory[0].Length);
        Assert.Empty(directory[0].Attributes);

        Assert.NotNull(directory[1].Version);
        Assert.Equal("rev-3", directory[1].Version!.ToString());
    }

    [Fact(Timeout = 15_000)]
    public async Task A_deleted_recipe_leaves_the_directory_and_takes_its_attributes_with_it()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);
        pair.Store.Add("PROD", "CLEAN-1", Body);

        await pair.OpenAsync();

        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), RecipePair.Token);

        Assert.Equal(
            RecipeAcknowledgement.Accepted,
            await pair.Host.DeleteAsync("PROD", "ETCH-7", RecipePair.Token));

        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);

        Assert.Equal("CLEAN-1", Assert.Single(directory).Id);
        Assert.Empty(directory[0].Attributes);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_rename_moves_a_recipe_in_the_directory_with_its_attributes()
    {
        await using var pair = RecipePair.Start();

        pair.Store.Add("PROD", "ETCH-7", Body);

        await pair.OpenAsync();

        await pair.Host.SetAttributeAsync("PROD", "ETCH-7", "OWNER", SecsItem.A("days"), RecipePair.Token);
        await pair.Host.RenameAsync("PROD", "ETCH-7", "ETCH-8", RecipePair.Token);

        var directory = await pair.Host.RequestDirectoryAsync("PROD", RecipePair.Token);
        var entry = Assert.Single(directory);

        Assert.Equal("ETCH-8", entry.Id);
        Assert.Equal((uint)Body.Length, entry.Length);
        Assert.Equal("days", Assert.Single(entry.Attributes).Value.GetString());
    }

    // Writes a known byte over every 4 KiB buffer the shared pool is holding, which is what a later
    // decode of a different payload would do to one that had been handed back. Nothing in the
    // library can do this on purpose, so the test does it.
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

    // A store with every required member and no directory, which is the interface default.
    private sealed class DirectorylessStore : IRecipeStore
    {
        public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>(["PROD"]);

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
