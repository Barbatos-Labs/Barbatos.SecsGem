// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Logging;

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// An item store that outlives the process, at whichever end was switched off.
/// </summary>
/// <remarks>
/// <para>
/// The mechanism under this is <c>DurableRecordDirectory</c>, shared as source with the three other
/// transfer stores and tested hardest in <c>RecipeFileStoreTests</c>. What is tested here is what
/// belongs to <em>stream 21</em>: an item that is listed and described before anybody sends its
/// contents - three transactions a host may make hours apart - the attributes no message sets, and
/// the fact that both ends of this stream have a store.
/// </para>
/// <para>
/// These touch a real filesystem on purpose, for the reason <c>GemFileSpoolTests</c> gives.
/// </para>
/// </remarks>
public class ItemFileStoreTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ItemPayload Payload(string lot)
    {
        using var item = SecsItem.L(SecsItem.A(lot), SecsItem.U4(3u));

        return ItemPayload.From(item);
    }

    private static string[] Records(string directory)
        => [.. Directory.EnumerateFiles(directory, "*" + ItemFileStore.RecordExtension)];

    private static async ValueTask GrantAsync(ItemFileStore store, string itemId, uint dataId)
        => Assert.Equal(
            ItemTransferGrant.Accepted,
            await store.RequestAsync(new ItemTransferRequest { ItemId = itemId, DataId = dataId }, Token));

    // ----------------------------------------------------------------- what a restart keeps

    [Fact(Timeout = 15_000)]
    public async Task An_item_that_was_only_ever_listed_comes_back_with_no_contents()
    {
        using var temporary = new TempDirectory();

        using var revision = SecsItem.A("1.4.2");
        using var steps = SecsItem.U4(3u);

        await new ItemFileStore(temporary.Path).AddAsync(
            new Item { Id = "RECIPE-7", Type = "recipe", Length = 3u },
            attributes:
            [
                new ItemAttribute { Name = "Revision", Value = revision },
                new ItemAttribute { Name = "Steps", Value = steps },
            ],
            cancellationToken: Token);

        var reopened = new ItemFileStore(temporary.Path);

        // The shape only this stream has: the directory, the attributes and the data are three
        // transactions, so "listed, described, never sent" is a state a record has to be able to
        // hold. It is a record with no payload at all rather than one with an empty payload.
        Assert.Equal(1, reopened.Count);
        Assert.Null(await reopened.ContentsAsync("RECIPE-7", Token));

        var found = reopened.Find("RECIPE-7");

        Assert.NotNull(found);
        Assert.Equal("recipe", found.Type);
        Assert.Equal(3u, found.Length);

        var attributes = await reopened.AttributesAsync(new ItemAttributeRequest { ItemId = "RECIPE-7" }, Token);

        Assert.Equal(2, attributes.Count);
        Assert.Equal("Revision", attributes[0].Name);
        Assert.Equal("1.4.2", attributes[0].Value.GetString());
        Assert.Equal(3u, attributes[1].Value.FirstValue<uint>());
    }

    [Fact(Timeout = 15_000)]
    public async Task An_items_contents_come_back_as_the_item_that_arrived()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path);

        await store.AddAsync(new Item { Id = "LOG-1", Type = "log" }, cancellationToken: Token);
        await GrantAsync(store, "LOG-1", 9u);

        Assert.Equal(ItemAcknowledgement.Accepted, await store.SaveAsync("LOG-1", 9u, Payload("LOT-1"), Token));

        var reopened = new ItemFileStore(temporary.Path);
        var contents = await reopened.ContentsAsync("LOG-1", Token);

        Assert.NotNull(contents);
        Assert.Equal(Payload("LOT-1").Encoded.ToArray(), contents.Encoded.ToArray());

        // The type survives a transfer that does not mention one, and LENGTH is the value count of
        // what arrived - two values, not the encoded size. Both are the in-memory store's rules.
        Assert.Equal("log", reopened.Find("LOG-1")?.Type);
        Assert.Equal(2u, reopened.Find("LOG-1")?.Length);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_transfer_keeps_the_attributes_somebody_else_put_there()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path);

        using var owner = SecsItem.A("process-1");

        await store.AddAsync(
            new Item { Id = "LOG-1", Type = "log" },
            attributes: [new ItemAttribute { Name = "Owner", Value = owner }],
            cancellationToken: Token);

        await GrantAsync(store, "LOG-1", 9u);
        Assert.Equal(ItemAcknowledgement.Accepted, await store.SaveAsync("LOG-1", 9u, Payload("LOT-1"), Token));

        var reopened = new ItemFileStore(temporary.Path);
        var attributes = await reopened.AttributesAsync(new ItemAttributeRequest { ItemId = "LOG-1" }, Token);

        // Nothing in S21F7 says what kind of thing is arriving or what it belongs to, so a transfer
        // that dropped the attributes would be forgetting something no message can put back.
        Assert.Single(attributes);
        Assert.Equal("process-1", attributes[0].Value.GetString());
        Assert.Single(Records(temporary.Path));
    }

    [Fact(Timeout = 15_000)]
    public async Task An_attribute_is_a_fresh_item_every_time_it_is_asked_for()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path);

        using var revision = SecsItem.A("1.4.2");

        await store.AddAsync(
            new Item { Id = "RECIPE-7", Type = "recipe" },
            attributes: [new ItemAttribute { Name = "Revision", Value = revision }],
            cancellationToken: Token);

        // What the wire does to an attribute: ItemTransferCodec.AttributeBody puts the value INTO
        // the reply, a reply is disposed once it has been sent, and disposing a list disposes its
        // children. What is asserted here is the property that makes that safe - the value this
        // store hands out is the caller's to dispose, and the next request answers anyway. A store
        // keeping one live item would be lending the same one out twice; for a small value that
        // happens to cost nothing, because an unpooled payload owns nothing to release, so the
        // claim worth testing is this one rather than a hazard that needs a megabyte to show.
        var first = await store.AttributesAsync(new ItemAttributeRequest { ItemId = "RECIPE-7" }, Token);

        using (var disposable = SecsItem.L(first[0].Value))
        {
            Assert.Equal(1, disposable.Count);
        }

        var second = await store.AttributesAsync(new ItemAttributeRequest { ItemId = "RECIPE-7" }, Token);

        Assert.Equal("1.4.2", second[0].Value.GetString());

        // And the caller's own item is untouched by having been stored, because AddAsync copied it.
        Assert.Equal("1.4.2", revision.GetString());
    }

    [Fact(Timeout = 15_000)]
    public async Task The_grants_do_not_survive_and_the_send_after_a_restart_is_denied()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path);

        await store.AddAsync(new Item { Id = "LOG-1", Type = "log" }, cancellationToken: Token);
        await GrantAsync(store, "LOG-1", 9u);

        Assert.Equal(1, store.GrantedCount);

        var reopened = new ItemFileStore(temporary.Path);

        Assert.Equal(0, reopened.GrantedCount);
        Assert.Equal(ItemAcknowledgement.Denied, await reopened.SaveAsync("LOG-1", 9u, Payload("LOT-1"), Token));

        // The item itself is still there, listed and described: what ended is the transfer.
        Assert.Equal(1, reopened.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_directory_filtered_by_type_still_filters_after_a_restart()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path);

        await store.AddAsync(new Item { Id = "RECIPE-7", Type = "recipe" }, cancellationToken: Token);
        await store.AddAsync(new Item { Id = "LOG-1", Type = "log" }, cancellationToken: Token);
        await store.AddAsync(new Item { Id = "LOG-2", Type = "log" }, cancellationToken: Token);

        var reopened = new ItemFileStore(temporary.Path);

        Assert.Equal(3, (await reopened.ListAsync(string.Empty, Token)).Count);
        Assert.Equal(
            ["LOG-1", "LOG-2"],
            (await reopened.ListAsync("log", Token)).Select(item => item.Id));

        // Out of the index rather than off the disk, which is what the index is for: a directory
        // request must not become a scan of every record.
        Assert.Equal(3, Records(temporary.Path).Length);
    }

    [Fact(Timeout = 15_000)]
    public async Task An_identifier_a_file_system_would_mangle_is_an_item_like_any_other()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path);

        string[] awkward = ["CON", "AUX.item", "a/b\\c", "../escape", "log", "LOG", "LOG 1"];

        foreach (var id in awkward)
        {
            await store.AddAsync(new Item { Id = id, Type = "log" }, cancellationToken: Token);
        }

        var reopened = new ItemFileStore(temporary.Path);

        Assert.Equal(awkward.Length, reopened.Count);
        Assert.Equal(awkward.Length, Records(temporary.Path).Length);
        Assert.Equal([.. awkward.Order(StringComparer.Ordinal)], (await reopened.ListAsync("log", Token)).Select(item => item.Id));
    }

    // ----------------------------------------------------------------- the rules it shares

    [Fact(Timeout = 15_000)]
    public async Task The_rules_the_in_memory_store_makes_up_are_made_here_too()
    {
        using var temporary = new TempDirectory();
        var store = new ItemFileStore(temporary.Path) { Capacity = 1 };

        // A send with no grant, and one whose transfer number is not the granted one.
        Assert.Equal(ItemAcknowledgement.Denied, await store.SaveAsync("LOG-1", 9u, Payload("LOT-1"), Token));

        await GrantAsync(store, "LOG-1", 9u);

        Assert.Equal(ItemAcknowledgement.Denied, await store.SaveAsync("LOG-1", 8u, Payload("LOT-1"), Token));
        Assert.Equal(ItemAcknowledgement.Accepted, await store.SaveAsync("LOG-1", 9u, Payload("LOT-1"), Token));

        // Capacity, counted over the items held.
        Assert.Equal(
            ItemTransferGrant.NoSpace,
            await store.RequestAsync(new ItemTransferRequest { ItemId = "LOG-2", DataId = 1u }, Token));

        // An abort needs its own grant and leaves what is stored alone.
        Assert.Equal(ItemAcknowledgement.Denied, await store.AbortAsync("LOG-1", 9u, Token));
        await GrantAsync(store, "LOG-1", 10u);
        Assert.Equal(ItemAcknowledgement.Accepted, await store.AbortAsync("LOG-1", 10u, Token));
        Assert.NotNull(await store.ContentsAsync("LOG-1", Token));

        // And a delete takes the record with it, which is the one message in this stream that does.
        Assert.Equal(ItemAcknowledgement.Accepted, await store.DeleteAsync("LOG-1", Token));
        Assert.Equal(ItemAcknowledgement.Denied, await store.DeleteAsync("LOG-1", Token));
        Assert.Empty(Records(temporary.Path));
        Assert.Equal(0, new ItemFileStore(temporary.Path).Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task A_record_that_will_not_read_gives_no_contents_and_says_so()
    {
        using var temporary = new TempDirectory();
        var logger = new RecordingLogger<ItemFileStore>();
        var store = new ItemFileStore(temporary.Path, logger);

        await store.AddAsync(new Item { Id = "LOG-1", Type = "log" }, Payload("LOT-1"), cancellationToken: Token);
        File.Delete(Records(temporary.Path)[0]);

        Assert.Null(await store.ContentsAsync("LOG-1", Token));
        Assert.Contains(logger.From(LogLevel.Warning), line => line.Contains("LOG-1", StringComparison.Ordinal));

        // The directory still answers out of the index, so a peer that lost an item's contents can
        // still say which item it has lost.
        Assert.Equal("log", store.Find("LOG-1")?.Type);
    }

    [Fact(Timeout = 15_000)]
    public void A_directory_that_is_not_there_is_made_and_an_empty_one_says_nothing()
    {
        using var temporary = new TempDirectory();
        var path = Path.Combine(temporary.Path, "items", "deeper");

        var logger = new RecordingLogger<ItemFileStore>();
        var store = new ItemFileStore(path, logger);

        Assert.True(Directory.Exists(path));
        Assert.Equal(Path.GetFullPath(path), store.Directory);
        Assert.Equal(0, store.Count);
        Assert.Empty(logger.Entries);
    }

    // ----------------------------------------------------------------- and on the wire

    [Fact(Timeout = 30_000)]
    public async Task One_end_sends_an_item_and_the_other_end_still_has_it_after_a_restart()
    {
        using var temporary = new TempDirectory();

        await using (var pair = ItemPair.Start(right: new ItemFileStore(temporary.Path)))
        {
            await pair.OpenAsync();

            // The peer mints the transfer number itself, which is what makes the grant and the
            // send one pair: the store checks that the number coming back is the one it granted.
            var (grant, dataId) = await pair.Left.RequestAsync("LOG-1", 2u, ItemPair.Token);

            Assert.Equal(ItemTransferGrant.Accepted, grant);

            using var data = SecsItem.L(SecsItem.A("LOT-1"), SecsItem.U4(3u));

            Assert.Equal(
                ItemAcknowledgement.Accepted,
                await pair.Left.SendAsync("LOG-1", dataId, data, ItemPair.Token));
        }

        // The receiving end is gone and so is the store that took the item. A new one over the
        // same directory is what a peer that was switched off and on again has - and in this
        // stream either end can be that peer, because both call the same registration.
        var adopted = new ItemFileStore(temporary.Path);

        await using var restarted = ItemPair.Start(right: adopted);
        await restarted.OpenAsync();

        var listed = await restarted.Left.ListAsync(string.Empty, ItemPair.Token);

        Assert.Equal(["LOG-1"], listed.Select(item => item.Id));
        Assert.Equal(2u, listed[0].Length);

        var contents = await adopted.ContentsAsync("LOG-1", Token);

        Assert.NotNull(contents);

        using var decoded = contents.Decode();

        Assert.NotNull(decoded);
        Assert.Equal("LOT-1", decoded.Items[0].GetString());
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
            => Path = Directory.CreateDirectory(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "barbatos-items-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A test that cannot clean up after itself should not fail for it.
            }
        }
    }
}
