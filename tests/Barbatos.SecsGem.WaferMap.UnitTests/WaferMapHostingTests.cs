// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.WaferMap.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this package that cannot be tested any other way.
/// </summary>
/// <remarks>
/// The handlers depend on the end, the end depends on the connection, and the connection depends on
/// the router the handlers are registered into. That cycle is real, and the only proof it resolves
/// is resolving it.
/// </remarks>
public class WaferMapHostingTests
{
    private sealed class CountingStore : IWaferMapStore
    {
        public int Saves { get; private set; }

        public ValueTask<WaferMapSetupAcknowledgement> SaveSetupAsync(WaferMapSetup setup, CancellationToken cancellationToken)
        {
            Saves++;
            return ValueTask.FromResult(WaferMapSetupAcknowledgement.Accepted);
        }

        public ValueTask<WaferMapSetup?> LoadSetupAsync(WaferMapSetupRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<WaferMapSetup?>(null);

        public ValueTask<WaferMapTransmitGrant> InquireTransmitAsync(WaferMapTransmitInquiry inquiry, CancellationToken cancellationToken)
            => ValueTask.FromResult(WaferMapTransmitGrant.Granted);

        public ValueTask<WaferMapDataAcknowledgement> SaveRowDataAsync(WaferMapRowData data, CancellationToken cancellationToken)
            => ValueTask.FromResult(WaferMapDataAcknowledgement.Accepted);

        public ValueTask<WaferMapDataAcknowledgement> SaveArrayDataAsync(WaferMapArrayData data, CancellationToken cancellationToken)
            => ValueTask.FromResult(WaferMapDataAcknowledgement.Accepted);

        public ValueTask<WaferMapDataAcknowledgement> SavePointDataAsync(WaferMapPointData data, CancellationToken cancellationToken)
            => ValueTask.FromResult(WaferMapDataAcknowledgement.Accepted);

        public ValueTask<WaferMapRowData?> LoadRowDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<WaferMapRowData?>(null);

        public ValueTask<WaferMapArrayData?> LoadArrayDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<WaferMapArrayData?>(null);

        public ValueTask<WaferMapPointData?> LoadPointDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<WaferMapPointData?>(null);
    }

    private static ServiceProvider BuildEquipment()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Active;
            })
            .UseInMemoryTransport(isActive: true)
            .AddWaferMapEquipment();

        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildHost()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddWaferMapHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_despite_the_cycle()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<WaferMapEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<WaferMapEquipment>(), equipment);
    }

    [Fact]
    public async Task The_host_graph_resolves_and_brings_the_in_memory_store_with_it()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<WaferMapHost>();

        Assert.NotNull(host);
        Assert.Same(provider.GetRequiredService<WaferMapHost>(), host);
        Assert.IsType<WaferMapInMemoryStore>(provider.GetRequiredService<IWaferMapStore>());
    }

    [Fact]
    public async Task Every_message_the_host_answers_is_in_the_routing_table()
    {
        await using var provider = BuildHost();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in WaferMapHost.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task The_equipment_declares_the_one_message_that_arrives_at_it()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(12, 19, out var handler));
        Assert.NotNull(handler);

        // And nothing else in stream 12: an equipment that claimed S12F1 would be answering a
        // message it is the one that sends.
        Assert.False(router.TryGetHandler(12, 1, out _));
        Assert.False(router.TryGetHandler(12, 13, out _));
    }

    [Fact]
    public async Task A_store_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new CountingStore();

        services.AddSingleton<IWaferMapStore>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddWaferMapHost();

        await using var provider = services.BuildServiceProvider();

        // TryAdd, so an application that registers its own store with its own dependencies can
        // still call AddWaferMapHost for the routing.
        Assert.Same(mine, provider.GetRequiredService<IWaferMapStore>());
    }
}
