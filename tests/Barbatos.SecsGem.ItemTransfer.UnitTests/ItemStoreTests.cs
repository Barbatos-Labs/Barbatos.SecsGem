// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// The rules <see cref="ItemInMemoryStore"/> makes up, tested where they are made.
/// </summary>
/// <remarks>
/// <para>
/// These are this class's choices, not E5's: what <c>LENGTH</c> counts, whether a correlation
/// number has to match, and what a resend does to what the directory already said. E5 gives none
/// of them, so each is a decision that has to be visible somewhere other than a comment.
/// </para>
/// <para>
/// Tested against the store rather than over a link, because a link cannot reach some of them -
/// an abort quoting the wrong transfer and a resend of a listed item both answer the same byte as
/// the case they must be distinguished from.
/// </para>
/// </remarks>
public class ItemStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_abort_quoting_another_transfer_is_refused()
    {
        var store = new ItemInMemoryStore();

        Assert.Equal(
            ItemTransferGrant.Accepted,
            await store.RequestAsync(new ItemTransferRequest { ItemId = "ITEM", DataId = 7 }, Token));

        // The same rule the send keeps, and for the same reason: two transfers of one item can be
        // in flight, and an abort that forgot which one would cancel the wrong one. The grant
        // survives, which is the part a wire test cannot see.
        Assert.Equal(ItemAcknowledgement.Denied, await store.AbortAsync("ITEM", 8, Token));
        Assert.Equal(1, store.GrantedCount);

        Assert.Equal(ItemAcknowledgement.Accepted, await store.AbortAsync("ITEM", 7, Token));
        Assert.Equal(0, store.GrantedCount);
    }

    [Fact]
    public async Task The_length_the_directory_reports_counts_values_and_not_bytes()
    {
        var store = new ItemInMemoryStore();

        await store.RequestAsync(new ItemTransferRequest { ItemId = "ITEM", DataId = 1 }, Token);

        using var sent = SecsItem.U4(10, 20, 30);
        var encoded = ItemPayload.From(sent);

        Assert.Equal(ItemAcknowledgement.Accepted, await store.SaveAsync("ITEM", 1, encoded, Token));

        // Three values, fourteen bytes encoded. LENGTH is a U4 and the reference says nothing
        // about what it counts, so this store counts the item it was sent and says so - rather
        // than quietly reporting a header it did not promise to measure.
        var listed = Assert.Single(await store.ListAsync(string.Empty, Token));

        Assert.Equal(3u, listed.Length);
        Assert.True(encoded.Encoded.Length > 3);
    }

    [Fact]
    public async Task A_received_item_keeps_the_type_it_was_listed_with()
    {
        var store = new ItemInMemoryStore();

        // The type is how the other end filters a directory, and nothing in stream 21 carries it
        // on a send: S21F5 and S21F7 have an identifier and a correlation number and no ITEMTYPE
        // at all. So a store that rebuilt the row on arrival would silently unfile the item.
        store.Add(new Item { Id = "ITEM", Type = "recipe" });

        await store.RequestAsync(new ItemTransferRequest { ItemId = "ITEM", DataId = 1 }, Token);

        using var sent = SecsItem.A("the item itself");

        Assert.Equal(
            ItemAcknowledgement.Accepted,
            await store.SaveAsync("ITEM", 1, ItemPayload.From(sent), Token));

        Assert.Equal("recipe", store.Find("ITEM")?.Type);
        Assert.Equal("ITEM", Assert.Single(await store.ListAsync("recipe", Token)).Id);
    }

    [Fact]
    public async Task An_item_that_was_never_listed_arrives_with_no_type()
    {
        var store = new ItemInMemoryStore();

        await store.RequestAsync(new ItemTransferRequest { ItemId = "ITEM", DataId = 1 }, Token);

        using var sent = SecsItem.A("the item itself");

        await store.SaveAsync("ITEM", 1, ItemPayload.From(sent), Token);

        // Empty rather than invented: ITEMTYPE is the two ends' own vocabulary - the reference
        // gives it no values - so there is nothing for this end to guess.
        Assert.Equal(string.Empty, store.Find("ITEM")?.Type);
    }
}
