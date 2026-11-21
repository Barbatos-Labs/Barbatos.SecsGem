// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Recipes.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this package that cannot be tested any other way.
/// </summary>
/// <remarks>
/// Lighter than most packages' version of this test, and for a reason worth asserting: stream 15
/// runs one way, so the equipment end takes no connection and there is no cycle between the end,
/// the connection and the router to resolve - while the host end registers no handler at all.
/// </remarks>
public class RecipeHostingTests
{
    [Fact]
    public async Task The_equipment_graph_resolves_and_brings_the_in_memory_store_with_it()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<RecipeEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<RecipeEquipment>(), equipment);
        Assert.IsType<RecipeInMemoryStore>(provider.GetRequiredService<IRecipeStore>());
    }

    [Fact]
    public async Task The_host_graph_resolves()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<RecipeHost>();

        Assert.NotNull(host);
        Assert.Same(provider.GetRequiredService<RecipeHost>(), host);
    }

    [Fact]
    public async Task Every_message_the_equipment_answers_is_in_the_routing_table()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in RecipeEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }

        Assert.Equal(9, RecipeEquipment.HandledMessages.Count);
    }

    [Fact]
    public async Task The_equipment_claims_no_reply()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The replies are this end's to send, so claiming one would be answering itself.
        for (byte function = 2; function <= 54; function += 2)
        {
            Assert.False(router.TryGetHandler(15, function, out _), $"S15F{function} is a reply.");
        }
    }

    [Fact]
    public async Task The_eighteen_pairs_this_package_leaves_out_are_not_claimed()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Every primary stream 15 has that is not one of the nine. They are out because their rows
        // reach for names the dictionary does not define while near-twins of those names sit in it
        // unused - measured in SecsCatalogStream15Tests, not asserted from memory. The rename and
        // the copy, F19 and F21, are the two this chapter expected to carry and did not.
        foreach (byte function in (byte[])[3, 11, 19, 21, 23, 25, 27, 29, 33, 35, 37, 39, 41, 43, 45, 47, 49, 51])
        {
            Assert.False(
                router.TryGetHandler(15, function, out _),
                $"S15F{function} is not implemented and must not be routed.");
        }
    }

    [Fact]
    public async Task The_host_registration_adds_no_route_at_all()
    {
        await using var provider = BuildHost();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The claim in RecipeHost.HandledMessages, asserted against the table rather than left in
        // a comment: all twenty-seven primaries are the host's, so a host answers nothing. The
        // third such registration here, beside the stream 8 and stream 13 hosts.
        for (byte function = 1; function <= 54; function++)
        {
            Assert.False(
                router.TryGetHandler(15, function, out _),
                $"S15F{function} should have no handler at a host.");
        }
    }

    [Fact]
    public async Task Nothing_in_this_package_claims_stream_19()
    {
        await using var equipment = BuildEquipment();
        await using var host = BuildHost();

        // The clash chapter 42 refused stream 19 for, turned into a routing fact. Streams 15 and
        // 19 share a name and all twenty of stream 19's function numbers while meaning different
        // transactions - S15F11 verifies where S19F11 deletes - so a package that served stream 15
        // and quietly answered stream 19 as well would be the exact hazard that refusal named.
        foreach (var provider in new[] { equipment, host })
        {
            var router = provider.GetRequiredService<ISecsMessageRouter>();

            for (byte function = 1; function <= 20; function++)
            {
                Assert.False(
                    router.TryGetHandler(19, function, out _),
                    $"S19F{function} is another standard's message and must not be routed here.");
            }
        }
    }

    [Fact]
    public async Task A_store_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new CountingStore();

        services.AddSingleton<IRecipeStore>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddRecipeEquipment();

        await using var provider = services.BuildServiceProvider();

        // TryAdd, so an application that registers its own store with its own dependencies can
        // still call AddRecipeEquipment for the routing.
        Assert.Same(mine, provider.GetRequiredService<IRecipeStore>());

        Assert.NotNull(provider.GetRequiredService<RecipeEquipment>());
        Assert.Equal(0, mine.Listings);
    }

    [Fact]
    public async Task The_store_a_type_argument_names_is_constructed_by_the_container()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddRecipeEquipment<CountingStore>();

        await using var provider = services.BuildServiceProvider();

        Assert.IsType<CountingStore>(provider.GetRequiredService<IRecipeStore>());

        // One instance for the whole process: the recipes a machine holds are the process's, not a
        // scope's, so a namespace list asked for twice has to answer the same both times.
        Assert.Same(
            provider.GetRequiredService<IRecipeStore>(),
            provider.GetRequiredService<IRecipeStore>());
    }

    [Fact]
    public async Task Calling_the_equipment_registration_twice_declares_each_primary_once()
    {
        var services = new ServiceCollection();

        var builder = services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false);

        builder.AddRecipeEquipment();
        builder.AddRecipeEquipment();

        await using var provider = services.BuildServiceProvider();

        // Two calls is what a careless composition root does, and the second must not turn one
        // arriving message into two answers on one transaction.
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in RecipeEquipment.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _));
        }

        Assert.NotNull(provider.GetRequiredService<RecipeEquipment>());
    }

    private static ServiceProvider BuildEquipment()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddRecipeEquipment();

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Active;
            })
            .UseInMemoryTransport(isActive: true)
            .AddRecipeHost();

        return services.BuildServiceProvider();
    }

    /// <summary>A store of an application's own, to prove the package's is only a default.</summary>
    private sealed class CountingStore : IRecipeStore
    {
        public int Listings { get; private set; }

        public ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken cancellationToken)
        {
            Listings++;
            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }

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
            => ValueTask.FromResult((RecipeAcknowledgement.Rejected, default(ReadOnlyMemory<byte>)));

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
