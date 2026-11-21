// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// Stream 21 end to end: two real peers over one link.
/// </summary>
public sealed class ItemTransferTests
{
    private static void Seed(ItemInMemoryStore store)
    {
        using var temperature = SecsItem.F8(23.9d);
        using var operatorName = SecsItem.A("A. Operator");

        store.Add(
            new Item { Id = "LOG-2026-11-21", Type = "log", Length = 3 },
            ItemPayload.From(SecsItem.A("the log itself")),
            [
                new ItemAttribute { Name = "Temperature", Value = temperature },
                new ItemAttribute { Name = "Operator", Value = operatorName },
            ]);

        store.Add(new Item { Id = "RECIPE-7", Type = "recipe", Length = 9 });
    }

    [Fact]
    public async Task A_directory_comes_back_with_the_types_and_the_lengths()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        var items = await pair.Left.ListAsync(cancellationToken: ItemPair.Token);

        Assert.Equal(2, items.Count);

        // All three fields of S21F2 are defined - ITEMID, ITEMTYPE and LENGTH - which is the first
        // pair in this stream that needs no inference at all.
        Assert.Equal("LOG-2026-11-21", items[0].Id);
        Assert.Equal("log", items[0].Type);
        Assert.Equal(3u, items[0].Length);
        Assert.Equal("RECIPE-7", items[1].Id);
        Assert.Equal("recipe", items[1].Type);
    }

    [Fact]
    public async Task A_directory_can_be_asked_for_one_kind()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        var recipes = await pair.Left.ListAsync("recipe", ItemPair.Token);

        Assert.Equal("RECIPE-7", Assert.Single(recipes).Id);

        // The reference gives ITEMTYPE no vocabulary, so a type nobody uses is simply a type with
        // nothing in it rather than an error.
        Assert.Empty(await pair.Left.ListAsync("nothing-like-this", ItemPair.Token));
    }

    [Fact]
    public async Task A_directory_of_a_peer_with_nothing_is_empty()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Left.ListAsync(cancellationToken: ItemPair.Token));
    }

    [Fact]
    public async Task Either_end_may_ask_the_other_for_its_directory()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        pair.LeftStore.Add(new Item { Id = "ONLY-MINE", Type = "log" });

        await pair.OpenAsync();

        // The thing that makes this package one type rather than two: every row of stream 21 is
        // marked as travelling either way, so a peer is not a host or an equipment.
        Assert.Equal(2, (await pair.Left.ListAsync(cancellationToken: ItemPair.Token)).Count);
        Assert.Equal("ONLY-MINE", Assert.Single(await pair.Right.ListAsync(cancellationToken: ItemPair.Token)).Id);
    }

    [Fact]
    public async Task Attributes_come_back_with_their_formats_intact()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        var read = await pair.Left.AttributesAsync(
            new ItemAttributeRequest { ItemId = "LOG-2026-11-21" },
            attributes => attributes
                .Select(attribute => (attribute.Name, attribute.Value.Format, Text: attribute.Value.ToString()))
                .ToArray(),
            ItemPair.Token);

        Assert.Equal(2, read.Length);

        // ATTRDATA is an L in the dictionary and E39's own item, so a value is whatever item the
        // other end put there - a float stays a float.
        Assert.Equal("Temperature", read[0].Name);
        Assert.Equal(SecsFormat.F8, read[0].Format);
        Assert.Equal("Operator", read[1].Name);
        Assert.Equal(SecsFormat.ASCII, read[1].Format);
    }

    [Fact]
    public async Task An_empty_attribute_request_asks_for_all_of_them()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        var all = await pair.Left.AttributesAsync(
            new ItemAttributeRequest { ItemId = "LOG-2026-11-21" },
            attributes => attributes.Count,
            ItemPair.Token);

        var one = await pair.Left.AttributesAsync(
            new ItemAttributeRequest { ItemId = "LOG-2026-11-21", Attributes = ["Operator"] },
            attributes => attributes.Select(attribute => attribute.Name).ToArray(),
            ItemPair.Token);

        // Empty meaning "all of them" is stream 14's reading of the same item in S14F1, taken from
        // there rather than decided here.
        Assert.Equal(2, all);
        Assert.Equal<string[]>(["Operator"], one);
    }

    [Fact]
    public async Task An_item_with_no_attributes_and_an_item_that_is_not_there_both_come_back_empty()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        // S21F4 carries no acknowledgement code, so an empty list is the only refusal it has - and
        // "this item has no attributes" and "there is no such item" arrive the same way.
        Assert.Equal(
            0,
            await pair.Left.AttributesAsync(
                new ItemAttributeRequest { ItemId = "RECIPE-7" },
                attributes => attributes.Count,
                ItemPair.Token));

        Assert.Equal(
            0,
            await pair.Left.AttributesAsync(
                new ItemAttributeRequest { ItemId = "NOPE" },
                attributes => attributes.Count,
                ItemPair.Token));
    }

    [Fact]
    public async Task An_item_crosses_unchanged_once_it_has_been_granted()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var contents = SecsItem.L(
            SecsItem.A("a log line"),
            SecsItem.U4(1, 2, 3),
            SecsItem.B(0xDE, 0xAD));

        var expected = contents.ToByteArray();

        var (grant, dataId) = await pair.Left.RequestAsync("LOG-NEW", (uint)expected.Length, ItemPair.Token);

        Assert.Equal(ItemTransferGrant.Accepted, grant);

        using var sending = SecsItem.Decode(expected);

        Assert.Equal(
            ItemAcknowledgement.Accepted,
            await pair.Left.SendAsync("LOG-NEW", dataId, sending, ItemPair.Token));

        // ITEMDATA has no dictionary entry and is named by stream 21 alone, so the only promise
        // this package makes about an item is that it does not touch it.
        var held = pair.RightStore.Contents("LOG-NEW");

        Assert.NotNull(held);
        Assert.Equal(expected, held.Encoded.ToArray());
        Assert.Equal(SecsFormat.List, held.Format);
    }

    [Fact]
    public async Task A_send_nobody_granted_is_refused()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var contents = SecsItem.A("not offered first");

        // The in-memory store's rule. The acknowledgement has one refusal and no way to say which
        // of several things went wrong, because none of stream 21's acknowledgement items has a
        // table of values at all.
        Assert.Equal(
            ItemAcknowledgement.Denied,
            await pair.Left.SendAsync("LOG-NEW", 1, contents, ItemPair.Token));

        Assert.Null(pair.RightStore.Find("LOG-NEW"));
    }

    [Fact]
    public async Task A_send_quoting_the_wrong_correlation_number_is_refused()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        using var contents = SecsItem.A("x");

        var (_, dataId) = await pair.Left.RequestAsync("LOG-NEW", 1, ItemPair.Token);

        // DATAID is the only correlation this pair has, so a send that quotes a different one is a
        // send for a transfer nobody granted.
        Assert.Equal(
            ItemAcknowledgement.Denied,
            await pair.Left.SendAsync("LOG-NEW", dataId + 1, contents, ItemPair.Token));
    }

    [Fact]
    public async Task An_item_fetched_from_one_peer_can_be_sent_to_another()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        var held = pair.RightStore.Contents("LOG-2026-11-21");
        Assert.NotNull(held);

        var (grant, dataId) = await pair.Right.RequestAsync("LOG-COPY", 16, ItemPair.Token);
        Assert.Equal(ItemTransferGrant.Accepted, grant);

        // The payload overload leaves the payload alone, so a store can hand the same item to
        // several links.
        Assert.Equal(
            ItemAcknowledgement.Accepted,
            await pair.Right.SendAsync("LOG-COPY", dataId, held, ItemPair.Token));

        Assert.Equal(
            held.Encoded.ToArray(),
            pair.LeftStore.Contents("LOG-COPY")!.Encoded.ToArray());
    }

    [Fact]
    public async Task An_abort_forgets_the_grant_and_keeps_what_is_stored()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        var (_, dataId) = await pair.Left.RequestAsync("LOG-2026-11-21", 4, ItemPair.Token);

        Assert.Equal(
            ItemAcknowledgement.Accepted,
            await pair.Left.AbortAsync("LOG-2026-11-21", dataId, ItemPair.Token));

        // S21F17 is the message that deletes, so an abort that deleted would be doing its job.
        Assert.NotNull(pair.RightStore.Find("LOG-2026-11-21"));
        Assert.Equal(0, pair.RightStore.GrantedCount);

        // And a second abort has nothing to abort.
        Assert.Equal(
            ItemAcknowledgement.Denied,
            await pair.Left.AbortAsync("LOG-2026-11-21", dataId, ItemPair.Token));
    }

    [Fact]
    public async Task A_delete_removes_the_item_and_a_second_one_is_refused()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        await pair.OpenAsync();

        // The one destructive message this package implements, and the only one stream 21 has -
        // streams 8 and 13 have none at all.
        Assert.Equal(
            ItemAcknowledgement.Accepted,
            await pair.Left.DeleteAsync("RECIPE-7", ItemPair.Token));

        Assert.Null(pair.RightStore.Find("RECIPE-7"));

        Assert.Equal(
            ItemAcknowledgement.Denied,
            await pair.Left.DeleteAsync("RECIPE-7", ItemPair.Token));
    }

    [Fact]
    public async Task A_request_beyond_the_capacity_has_no_room()
    {
        await using var pair = ItemPair.Start();
        pair.RightStore.Capacity = 1;
        pair.RightStore.Add(new Item { Id = "ALREADY" });

        await pair.OpenAsync();

        var (grant, _) = await pair.Left.RequestAsync("ANOTHER", 4, ItemPair.Token);

        Assert.Equal(ItemTransferGrant.NoSpace, grant);
    }

    [Fact]
    public async Task Each_request_carries_its_own_correlation_number()
    {
        await using var pair = ItemPair.Start();
        await pair.OpenAsync();

        var (_, first) = await pair.Left.RequestAsync("A", 4, ItemPair.Token);
        var (_, second) = await pair.Left.RequestAsync("B", 4, ItemPair.Token);

        // DATAID is this end's own, and two transfers in flight have to be told apart by it:
        // every refusal the store can give - a send nobody granted, an abort of another transfer -
        // is a comparison against the number the grant carried.
        Assert.NotEqual(first, second);
        Assert.NotEqual(0u, first);

        using var contents = SecsItem.A("the first item");

        // And the first number is still the first transfer's: a send quoting it must land, not be
        // overtaken by the second request.
        Assert.Equal(
            ItemAcknowledgement.Accepted,
            await pair.Left.SendAsync("A", first, contents, ItemPair.Token));
    }

    [Fact]
    public async Task Either_end_sends_every_primary_a_peer_declares()
    {
        await using var pair = ItemPair.Start();
        Seed(pair.RightStore);
        Seed(pair.LeftStore);
        await pair.OpenAsync();

        foreach (var (peer, item) in new[] { (pair.Left, "LOG-2026-11-21"), (pair.Right, "RECIPE-7") })
        {
            await peer.ListAsync(cancellationToken: ItemPair.Token);
            await peer.AttributesAsync(
                new ItemAttributeRequest { ItemId = item },
                attributes => attributes.Count,
                ItemPair.Token);

            var (_, dataId) = await peer.RequestAsync(item, 4, ItemPair.Token);

            using var contents = SecsItem.A("x");
            await peer.SendAsync(item, dataId, contents, ItemPair.Token);

            await peer.AbortAsync(item, dataId, ItemPair.Token);
            await peer.DeleteAsync(item, ItemPair.Token);
        }

        // "Every" asserted rather than claimed, and in both directions - which is the whole point
        // of there being one type.
        Assert.Equal(
            [.. ItemTransferPeer.HandledMessages.Order()],
            pair.AskedRight.Distinct().Order());

        Assert.Equal(
            [.. ItemTransferPeer.HandledMessages.Order()],
            pair.AskedLeft.Distinct().Order());
    }

    [Fact]
    public void A_peer_declares_six_of_the_ten_pairs()
    {
        Assert.Equal<(byte, byte)>(
            [(21, 1), (21, 3), (21, 5), (21, 7), (21, 15), (21, 17)],
            ItemTransferPeer.HandledMessages);

        // The blocked transfer and its status stay out: every block-transfer item in the reference
        // is undefined. So does the transfer complete, which needs CHKSUM.
        foreach (var function in new byte[] { 9, 11, 13, 19 })
        {
            Assert.DoesNotContain(function, ItemTransferPeer.HandledMessages.Select(m => m.Function));
        }
    }

    [Fact]
    public void The_grant_agrees_with_the_two_other_packages_that_serve_it()
    {
        // GRANT is one dictionary entry and three packages here now serve it: Material for S3F16,
        // ControlPrograms for S8F2 and this one for S21F6. One item cannot mean three things in one
        // repository.
        Assert.Equal<string[]>(
            ["Accepted", "Busy", "NoSpace", "DuplicateOrInvalid", "Other"],
            Enum.GetNames<ItemTransferGrant>());

        Assert.Equal(0, (byte)ItemTransferGrant.Accepted);
        Assert.Equal(1, (byte)ItemTransferGrant.Busy);
        Assert.Equal(2, (byte)ItemTransferGrant.NoSpace);
        Assert.Equal(3, (byte)ItemTransferGrant.DuplicateOrInvalid);
        Assert.Equal(4, (byte)ItemTransferGrant.Other);
    }

    [Fact]
    public void The_acknowledgement_has_two_members_for_three_items()
    {
        // ACKC21, ABTACK and DELACK are three different undefined items with no table between
        // them, so three identical two-member enums would be pretending to a distinction the
        // reference does not make.
        Assert.Equal<string[]>(["Accepted", "Denied"], Enum.GetNames<ItemAcknowledgement>());
        Assert.Equal(0, (byte)ItemAcknowledgement.Accepted);
        Assert.Equal(1, (byte)ItemAcknowledgement.Denied);
    }

    [Fact]
    public void A_payload_outlives_the_item_it_was_made_from()
    {
        var item = SecsItem.L(SecsItem.A("kept"), SecsItem.U2(7));
        var payload = ItemPayload.From(item);

        // The same reason DataSetPayload exists one package over, and the duplication is named in
        // both: an item decoded off the wire borrows pooled memory and is released with its
        // message, and there is no clone on it.
        item.Dispose();

        using var again = payload.Decode();

        Assert.NotNull(again);
        Assert.Equal(SecsFormat.List, again.Format);
        Assert.Equal("kept", again.Items[0].GetString());
    }

    [Fact]
    public void An_item_that_carried_nothing_is_empty_though_its_bytes_are_not()
    {
        using var nothing = SecsItem.L();

        var payload = ItemPayload.From(nothing);

        // An encoded empty item has a format byte and a length byte in front of it, so a
        // zero-length list is two bytes and not none. That distinction cost stream 13's chapter a
        // bug, and this type inherits both the shape and the lesson.
        Assert.Equal(2, payload.Encoded.Length);
        Assert.Equal(0, payload.Count);
        Assert.True(payload.IsEmpty);
    }

    [Fact]
    public void An_item_describes_itself_for_a_log()
    {
        // "length 3", not "3 bytes", and the difference is a claim: LENGTH has no units in the
        // reference, so a description that supplied some would contradict what this package
        // documents about the one figure it cannot know.
        Assert.Equal(
            "LOG-1 [log] (length 3)",
            new Item { Id = "LOG-1", Type = "log", Length = 3 }.ToString());

        // A type the other end did not give is left out rather than printed as nothing.
        Assert.Equal("LOG-1 (length 0)", new Item { Id = "LOG-1" }.ToString());
    }
}
