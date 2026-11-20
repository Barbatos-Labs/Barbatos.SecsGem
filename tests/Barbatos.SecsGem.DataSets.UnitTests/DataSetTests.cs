// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.DataSets.UnitTests;

/// <summary>
/// Stream 13 end to end: a real host and a real equipment over one link.
/// </summary>
public sealed class DataSetTests
{
    [Fact]
    public async Task An_open_to_send_is_granted()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        var opening = await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 1024, DataSetPair.Token);

        Assert.True(opening.IsGranted);
        Assert.Equal(DataSetGrant.Ok, opening.Grant);

        // S13F2 carries no length: the host declared it, so there is nothing for the equipment to
        // report back.
        Assert.Equal(0u, opening.Length);
    }

    [Fact]
    public async Task An_open_to_send_beyond_capacity_has_no_room()
    {
        var store = new DataSetInMemoryStore { Capacity = 1 };
        store.Add(1, DataSetPayload.From(SecsItem.A("already here")));

        await using var pair = DataSetPair.Start(store);
        await pair.OpenAsync();

        var opening = await pair.Host.OpenAsync(2, DataSetOpenMode.Send, 8, DataSetPair.Token);

        Assert.False(opening.IsGranted);
        Assert.Equal(DataSetGrant.NoSpace, opening.Grant);
    }

    [Fact]
    public async Task A_data_set_crosses_unchanged()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        // Deliberately a nested list with three formats in it. DSDATA has no definition, so the
        // one thing this package promises about a data set is that it does not touch it.
        using var original = SecsItem.L(
            SecsItem.A("recipe-7"),
            SecsItem.U4(1, 2, 3),
            SecsItem.L(SecsItem.B(0xDE, 0xAD), SecsItem.F4(1.5f)));

        var expected = original.ToByteArray();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, (uint)expected.Length, DataSetPair.Token);

        using var sent = SecsItem.Decode(expected);
        var acknowledgement = await pair.Host.SendAsync(41, sent, DataSetPair.Token);

        Assert.Equal(DataSetAcknowledgement.Accepted, acknowledgement);

        var held = pair.Memory.Get(41);
        Assert.NotNull(held);
        Assert.Equal(expected, held.Encoded.ToArray());
        Assert.Equal(SecsFormat.List, held.Format);
    }

    [Fact]
    public async Task A_send_to_a_data_set_nothing_opened_is_refused()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        using var data = SecsItem.A("no open for this one");
        var acknowledgement = await pair.Host.SendAsync(41, data, DataSetPair.Token);

        // The in-memory store's rule, not this package's: ACKC13 has a word for it, which is what
        // makes the sequence enforceable by a store at all.
        Assert.Equal(DataSetAcknowledgement.InvalidDataSet, acknowledgement);
        Assert.Null(pair.Memory.Get(41));
    }

    [Fact]
    public async Task An_open_to_read_reports_the_length_the_open_declared()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 4096, DataSetPair.Token);

        using var data = SecsItem.A("short");
        await pair.Host.SendAsync(41, data, DataSetPair.Token);
        await pair.Host.CloseAsync(41, DataSetPair.Token);

        var opening = await pair.Host.OpenAsync(41, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);

        Assert.Equal(DataSetGrant.Ok, opening.Grant);

        // The number the host declared, not the encoded size of what arrived: LENGTH has no
        // definition that says what it counts, so the store reports what it was told.
        Assert.Equal(4096u, opening.Length);
    }

    [Fact]
    public async Task An_open_to_read_of_a_data_set_that_is_not_there_has_no_room()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        var opening = await pair.Host.OpenAsync(999, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);

        // GRANT6 has three codes and none of them is "no such data set", so this is the only
        // refusal available - and the reason S13F15 is the transaction to send first.
        Assert.Equal(DataSetGrant.NoSpace, opening.Grant);
        Assert.Equal(0u, opening.Length);
    }

    [Fact]
    public async Task A_get_returns_the_data_set()
    {
        var store = new DataSetInMemoryStore();
        store.Add(41, DataSetPayload.From(SecsItem.A("the contents")), 12);

        await using var pair = DataSetPair.Start(store);
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);

        var payload = await pair.Host.GetAsync(41, DataSetPair.Token);

        Assert.NotNull(payload);

        using var item = payload.Decode();
        Assert.NotNull(item);
        Assert.Equal("the contents", item.GetString());
    }

    [Fact]
    public async Task A_get_of_a_data_set_that_is_not_there_returns_null()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        // S13F8 has no acknowledgement code at all, so "not there" arrives as an empty DSDATA in
        // an otherwise ordinary reply.
        Assert.Null(await pair.Host.GetAsync(999, DataSetPair.Token));
    }

    [Fact]
    public async Task The_directory_lists_what_is_there_in_order()
    {
        // The identifiers are spread across the range on purpose, and it is the only reason this
        // test proves anything. A uint hashes to itself, so a ConcurrentDictionary of small
        // consecutive keys enumerates them in ascending order by accident - 9, 2 and 5 come back
        // as 2, 5, 9 with no sort in sight, which is how the first version of this test passed
        // while a control that removed the sort found nothing at all. These seven do not: they
        // enumerate as 1, 3, 4294967295, 7, ...
        uint[] identifiers = [101, 7, 4294967295, 3, 65537, 17, 1];

        var store = new DataSetInMemoryStore { Capacity = identifiers.Length };

        foreach (var identifier in identifiers)
        {
            store.Add(identifier, DataSetPayload.From(SecsItem.U4(identifier)));
        }

        await using var pair = DataSetPair.Start(store);
        await pair.OpenAsync();

        Assert.Equal<uint>(
            [1, 3, 7, 17, 101, 65537, 4294967295],
            await pair.Host.ListAsync(DataSetPair.Token));
    }

    [Fact]
    public async Task The_directory_of_an_equipment_with_nothing_is_empty()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ListAsync(DataSetPair.Token));
    }

    [Fact]
    public async Task An_open_for_send_is_not_in_the_directory_until_the_data_arrives()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 64, DataSetPair.Token);

        // The declared length is held, but a length with nothing behind it is not a data set - so
        // neither the directory nor the status admits to one.
        Assert.Empty(await pair.Host.ListAsync(DataSetPair.Token));
        Assert.False((await pair.Host.StatusAsync(41, DataSetPair.Token)).Exists);

        using var data = SecsItem.A("here it is");
        await pair.Host.SendAsync(41, data, DataSetPair.Token);

        Assert.Equal<uint>([41], await pair.Host.ListAsync(DataSetPair.Token));
        Assert.True((await pair.Host.StatusAsync(41, DataSetPair.Token)).Exists);
    }

    [Fact]
    public async Task A_get_of_a_data_set_nothing_opened_hands_back_nothing()
    {
        var store = new DataSetInMemoryStore();
        store.Add(41, DataSetPayload.From(SecsItem.A("held, but not opened")), 20);

        await using var pair = DataSetPair.Start(store);
        await pair.OpenAsync();

        // The other half of the in-memory store's sequence rule, and the quieter half: a get has
        // no acknowledgement code to refuse with, so the refusal is an empty DSDATA.
        Assert.Null(await pair.Host.GetAsync(41, DataSetPair.Token));

        await pair.Host.OpenAsync(41, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);

        Assert.NotNull(await pair.Host.GetAsync(41, DataSetPair.Token));
    }

    [Fact]
    public async Task A_close_closes_and_a_second_close_is_refused()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 16, DataSetPair.Token);

        Assert.Equal(DataSetAcknowledgement.Accepted, await pair.Host.CloseAsync(41, DataSetPair.Token));
        Assert.Equal(DataSetAcknowledgement.InvalidDataSet, await pair.Host.CloseAsync(41, DataSetPair.Token));
    }

    [Fact]
    public async Task A_close_does_not_delete()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 16, DataSetPair.Token);

        using var data = SecsItem.A("kept");
        await pair.Host.SendAsync(41, data, DataSetPair.Token);
        await pair.Host.CloseAsync(41, DataSetPair.Token);

        // Stream 13 has no message that removes a data set, so a close that deleted would be this
        // library inventing one.
        var status = await pair.Host.StatusAsync(41, DataSetPair.Token);
        Assert.True(status.Exists);
    }

    [Fact]
    public async Task The_status_says_a_data_set_is_there_and_how_long()
    {
        var store = new DataSetInMemoryStore();
        store.Add(41, DataSetPayload.From(SecsItem.A("x")), 2048);

        await using var pair = DataSetPair.Start(store);
        await pair.OpenAsync();

        var status = await pair.Host.StatusAsync(41, DataSetPair.Token);

        Assert.True(status.Exists);
        Assert.Equal(41u, status.DataSetId);
        Assert.Equal(2048u, status.Length);
        Assert.Equal(DataSetAcknowledgement.Accepted, status.Acknowledgement);
    }

    [Fact]
    public async Task The_status_of_a_data_set_that_is_not_there_says_so()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        var status = await pair.Host.StatusAsync(999, DataSetPair.Token);

        // The thing GRANT6 cannot say, which is the whole reason this transaction is worth having.
        Assert.False(status.Exists);
        Assert.Equal(DataSetAcknowledgement.InvalidDataSet, status.Acknowledgement);
        Assert.Equal(999u, status.DataSetId);
    }

    [Fact]
    public async Task A_data_set_fetched_from_one_link_can_be_sent_over_another()
    {
        var upstream = new DataSetInMemoryStore();

        using var original = SecsItem.L(
            SecsItem.A("forwarded"),
            SecsItem.U2(7, 8));

        var expected = original.ToByteArray();
        upstream.Add(41, DataSetPayload.FromEncoded(expected, SecsFormat.List), (uint)expected.Length);

        await using var first = DataSetPair.Start(upstream);
        await first.OpenAsync();
        await first.Host.OpenAsync(41, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);

        var fetched = await first.Host.GetAsync(41, DataSetPair.Token);
        Assert.NotNull(fetched);

        await using var second = DataSetPair.Start();
        await second.OpenAsync();
        await second.Host.OpenAsync(41, DataSetOpenMode.Send, (uint)expected.Length, DataSetPair.Token);

        Assert.Equal(
            DataSetAcknowledgement.Accepted,
            await second.Host.SendAsync(41, fetched, DataSetPair.Token));

        // The payload overload leaves the payload alone, so it is still sendable - and what landed
        // downstream is byte for byte what came off the first link.
        Assert.Equal(expected, fetched.Encoded.ToArray());
        Assert.Equal(expected, second.Memory.Get(41)!.Encoded.ToArray());
    }

    [Fact]
    public async Task The_host_asks_every_primary_the_equipment_declares()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        await pair.Host.OpenAsync(41, DataSetOpenMode.Send, 8, DataSetPair.Token);

        using var data = SecsItem.A("all seven");
        await pair.Host.SendAsync(41, data, DataSetPair.Token);
        await pair.Host.CloseAsync(41, DataSetPair.Token);
        await pair.Host.OpenAsync(41, DataSetOpenMode.Request, cancellationToken: DataSetPair.Token);
        await pair.Host.GetAsync(41, DataSetPair.Token);
        await pair.Host.ListAsync(DataSetPair.Token);
        await pair.Host.StatusAsync(41, DataSetPair.Token);

        // "Every" asserted rather than claimed: the set the equipment declares and the set the
        // host managed to send are compared, not counted.
        Assert.Equal(
            [.. DataSetEquipment.HandledMessages.Order()],
            pair.Asked.Distinct().Order());
    }

    [Fact]
    public void The_equipment_declares_seven_and_the_host_declares_none()
    {
        Assert.Equal<(byte, byte)>(
            [(13, 1), (13, 3), (13, 5), (13, 7), (13, 11), (13, 13), (13, 15)],
            DataSetEquipment.HandledMessages);

        // All seven primaries are the host's, so nothing in stream 13 ever arrives at a host
        // unasked. This is the only end in the repository with an empty list.
        Assert.Empty(DataSetHost.HandledMessages);
    }

    [Fact]
    public async Task An_open_the_host_was_not_asked_for_is_refused_before_the_wire()
    {
        await using var pair = DataSetPair.Start();
        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => pair.Host.OpenAsync(41, (DataSetOpenMode)3, 8, DataSetPair.Token).AsTask());

        Assert.Equal("mode", thrown.ParamName);
        Assert.Empty(pair.Asked);
    }
}
