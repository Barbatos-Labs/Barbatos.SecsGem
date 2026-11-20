// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this package that cannot be tested any other way.
/// </summary>
/// <remarks>
/// The handlers depend on the end, the end depends on the connection, and the connection depends
/// on the router the handlers are registered into. That cycle is real, and the only proof it
/// resolves is resolving it.
/// </remarks>
public class MaterialHostingTests
{
    private sealed class EmptySource : IMaterialSource
    {
        public ValueTask<IReadOnlyList<MaterialStatus>> GetStatusAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<MaterialStatus>>([]);

        public ValueTask<IReadOnlyList<MaterialCompletion>> GetTimeToCompletionAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<MaterialCompletion>>([]);

        public ValueTask<MaterialLocation?> FindAsync(string materialId, CancellationToken cancellationToken)
            => ValueTask.FromResult<MaterialLocation?>(null);

        public ValueTask<MaterialAcknowledgement> TransferAsync(MaterialTransfer transfer, CancellationToken cancellationToken)
            => ValueTask.FromResult(MaterialAcknowledgement.Denied);

        public ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken cancellationToken)
            => ValueTask.FromResult(MaterialGrant.NoSpace);
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
            .AddMaterialEquipment();

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
            .AddMaterialHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_and_brings_the_in_memory_inventory_with_it()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<MaterialEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<MaterialEquipment>(), equipment);
        Assert.IsType<MaterialInMemorySource>(provider.GetRequiredService<IMaterialSource>());
    }

    [Fact]
    public async Task The_host_graph_resolves_despite_the_cycle()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<MaterialHost>();

        Assert.NotNull(host);
        Assert.Same(provider.GetRequiredService<MaterialHost>(), host);
    }

    [Fact]
    public async Task Each_end_declares_its_own_messages_and_not_the_other_end_messages()
    {
        await using var equipment = BuildEquipment();
        await using var host = BuildHost();

        var equipmentRouter = equipment.GetRequiredService<ISecsMessageRouter>();
        var hostRouter = host.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in MaterialEquipment.HandledMessages)
        {
            Assert.True(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the equipment should answer S{stream}F{function}");

            Assert.False(
                hostRouter.TryGetHandler(stream, function, out _),
                $"the host should not answer S{stream}F{function}, which it is the one that sends");
        }

        foreach (var (stream, function) in MaterialHost.HandledMessages)
        {
            Assert.True(
                hostRouter.TryGetHandler(stream, function, out _),
                $"the host should answer S{stream}F{function}");

            Assert.False(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the equipment should not answer S{stream}F{function}, which it is the one that sends");
        }
    }

    [Fact]
    public async Task A_source_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new EmptySource();

        services.AddSingleton<IMaterialSource>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddMaterialEquipment();

        await using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IMaterialSource>());
    }

    [Fact]
    public async Task Both_ends_declare_nine_messages_between_them_and_none_twice()
    {
        await using var equipment = BuildEquipment();
        await using var host = BuildHost();

        List<(byte Stream, byte Function)> all =
        [
            .. MaterialEquipment.HandledMessages,
            .. MaterialHost.HandledMessages,
        ];

        // Nine of stream 3's eighteen material functions are primaries, and each belongs to
        // exactly one end. The two lists overlapping would be a design error the routing table
        // could not catch, because the two ends live on different connections.
        Assert.Equal(9, all.Count);
        Assert.Equal(9, all.Distinct().Count());
        Assert.All(all, pair => Assert.True(pair.Function % 2 == 1, $"S3F{pair.Function} is not a primary"));
    }

    /// <summary>The handoff registration is the one both ends call.</summary>
    /// <remarks>
    /// Stream 4's handshake is symmetric, so there is no equipment half and no host half of it:
    /// <c>AddMaterialHandoff</c> declares all nine primaries wherever it is called, and it
    /// composes with either of the stream 3 registrations because the two streams share no
    /// declaration.
    /// </remarks>
    [Fact]
    public async Task The_handoff_declares_all_nine_at_whichever_end_registers_it()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddMaterialEquipment()
            .AddMaterialHandoff()
            .AddMaterialHandoff();

        await using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<MaterialHandoff>();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in MaterialHandoff.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        // And stream 3's equipment half is still there beside it.
        foreach (var (stream, function) in MaterialEquipment.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        Assert.Equal(9, MaterialHandoff.HandledMessages.Count);
        Assert.All(MaterialHandoff.HandledMessages, pair => Assert.True(pair.Function % 2 == 1, $"S4F{pair.Function} is not a primary"));

        // The handoff is a singleton like the other two, so an application can hold the one whose
        // answer delegates it set.
        Assert.Same(provider.GetRequiredService<MaterialHandoff>(), provider.GetRequiredService<MaterialHandoff>());
    }

    /// <summary>Either registration called twice is harmless.</summary>
    /// <remarks>
    /// It was not: the services were idempotent through <c>TryAdd</c> and the route declarations
    /// were not, so a second call failed at start-up with "S3F1 already has a handler" - true, and
    /// unhelpful. <c>DeclareOnce</c> on the builder settles it for every capability in this stack;
    /// this is stream 3's share of the proof.
    /// </remarks>
    [Fact]
    public async Task Registering_either_end_twice_declares_its_messages_once()
    {
        var equipment = new ServiceCollection();

        equipment
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddMaterialEquipment()
            .AddMaterialEquipment();

        await using var equipmentProvider = equipment.BuildServiceProvider();

        var equipmentRouter = equipmentProvider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in MaterialEquipment.HandledMessages)
        {
            Assert.True(equipmentRouter.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        var host = new ServiceCollection();

        host
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Active)
            .UseInMemoryTransport(isActive: true)
            .AddMaterialHost()
            .AddMaterialHost();

        await using var hostProvider = host.BuildServiceProvider();

        var hostRouter = hostProvider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in MaterialHost.HandledMessages)
        {
            Assert.True(hostRouter.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }
    }
}
