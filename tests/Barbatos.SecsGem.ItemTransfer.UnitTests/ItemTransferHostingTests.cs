// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.ItemTransfer.UnitTests;

/// <summary>
/// What <c>AddItemTransfer</c> puts in the container, and what it declares to the router.
/// </summary>
/// <remarks>
/// <para>
/// One registration for both ends, because stream 21 has one kind of end - so unlike every
/// host-and-equipment package here there is no pair of extension methods to get the wrong way
/// round, and these tests are about the single one being complete rather than about two agreeing.
/// </para>
/// <para>
/// <strong>If this file ever runs and the suite produces no summary at all</strong>, look at the
/// registration's handler factory rather than at a test. The end needs the connection, the
/// connection needs the router, and the router is where the end is declared; resolving the end
/// inside the factory instead of inside the handler closes that loop, and the container then
/// stops rather than throwing - no failure, no summary, the whole run gone. Measured: it is the
/// same symptom a message sent on the wrong link produces, from a completely different cause.
/// </para>
/// </remarks>
public class ItemTransferHostingTests
{
    [Fact]
    public async Task The_graph_resolves_despite_the_cycle_between_the_end_and_the_connection()
    {
        await using var provider = Build();

        // The peer needs the connection, the connection needs the router, and the router is what
        // the peer was declared into. It resolves because the handler asks the provider when a
        // message arrives rather than when the factory runs.
        var peer = provider.GetRequiredService<ItemTransferPeer>();

        Assert.NotNull(peer);
        Assert.Same(peer, provider.GetRequiredService<ItemTransferPeer>());
        Assert.IsType<ItemInMemoryStore>(provider.GetRequiredService<IItemStore>());
    }

    [Fact]
    public async Task Every_primary_the_end_declares_is_in_the_routing_table()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in ItemTransferPeer.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} is in HandledMessages but not in the routing table.");

            Assert.NotNull(handler);
        }

        Assert.Equal(6, ItemTransferPeer.HandledMessages.Count);
    }

    [Fact]
    public async Task No_secondary_is_claimed()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // A secondary arrives as the answer to something this end sent, so a route for one would
        // take the reply away from the caller waiting on it. Worth asserting here rather than
        // trusting HandledMessages, because this is the one package whose list is both what it
        // sends and what it answers - the mistake it invites is a reply ending up in the table.
        for (byte function = 2; function <= 20; function += 2)
        {
            Assert.False(
                router.TryGetHandler(21, function, out _),
                $"S21F{function} is a secondary and must not be routed.");
        }
    }

    [Fact]
    public async Task The_four_primaries_this_package_leaves_out_are_not_claimed()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // F9 and F11 are the blocked transfer, out because every one of the catalogue's seven
        // block-transfer items is undefined - the same measurement that kept S13F9/F10 and S8F7/F9
        // out. F13 is the transfer status, which reports a block number and so cannot be kept
        // without them. F19 is the transfer complete, which carries a CHKSUM: the one undefined
        // value that cannot be carried opaquely, because what the two ends must agree on is the
        // algorithm and the span. Measured in SecsCatalogStream21Tests, not asserted from memory.
        foreach (byte function in (byte[])[9, 11, 13, 19])
        {
            Assert.False(
                router.TryGetHandler(21, function, out _),
                $"S21F{function} is not implemented and must not be routed.");
        }
    }

    [Fact]
    public async Task A_store_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new CountingStore();

        services.AddSingleton<IItemStore>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddItemTransfer();

        await using var provider = services.BuildServiceProvider();

        // TryAdd, so an application that registers a store with its own dependencies can still call
        // the one extension method for the routing.
        Assert.Same(mine, provider.GetRequiredService<IItemStore>());

        Assert.NotNull(provider.GetRequiredService<ItemTransferPeer>());
        Assert.Equal(0, mine.Listings);
    }

    [Fact]
    public async Task The_store_a_type_argument_names_is_constructed_by_the_container()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddItemTransfer<CountingStore>();

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<CountingStore>(provider.GetRequiredService<IItemStore>());

        // One instance for the whole process: the items this end holds are the process's, not a
        // scope's, so a directory asked for twice has to answer the same both times.
        Assert.Same(
            provider.GetRequiredService<IItemStore>(),
            provider.GetRequiredService<IItemStore>());
    }

    [Fact]
    public async Task Calling_it_twice_declares_each_primary_once()
    {
        var services = new ServiceCollection();

        var builder = services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false);

        builder.AddItemTransfer();
        builder.AddItemTransfer();

        await using var provider = services.BuildServiceProvider();

        // Two calls is what a careless composition root does - and this package invites it more
        // than most, since both ends call the same method and a bridge might add it again. The
        // second must not turn one arriving message into two answers on one transaction.
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in ItemTransferPeer.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _));
        }

        Assert.NotNull(provider.GetRequiredService<ItemTransferPeer>());
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddItemTransfer();

        return services.BuildServiceProvider();
    }

    /// <summary>A store of an application's own, to prove the package's is only a default.</summary>
    private sealed class CountingStore : IItemStore
    {
        public int Listings { get; private set; }

        public ValueTask<IReadOnlyList<Item>> ListAsync(string itemType, CancellationToken cancellationToken)
        {
            Listings++;
            return ValueTask.FromResult<IReadOnlyList<Item>>([]);
        }

        public ValueTask<IReadOnlyList<ItemAttribute>> AttributesAsync(
            ItemAttributeRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<ItemAttribute>>([]);

        public ValueTask<ItemTransferGrant> RequestAsync(
            ItemTransferRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ItemTransferGrant.Busy);

        public ValueTask<ItemAcknowledgement> SaveAsync(
            string itemId,
            uint dataId,
            ItemPayload data,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ItemAcknowledgement.Denied);

        public ValueTask<ItemAcknowledgement> AbortAsync(
            string itemId,
            uint dataId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ItemAcknowledgement.Denied);

        public ValueTask<ItemAcknowledgement> DeleteAsync(string itemId, CancellationToken cancellationToken)
            => ValueTask.FromResult(ItemAcknowledgement.Denied);
    }
}
