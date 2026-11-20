// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.DataSets.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this package that cannot be tested any other way.
/// </summary>
/// <remarks>
/// Lighter than the other packages' version of this test, and for a reason worth asserting: the
/// equipment end takes no connection, so there is no cycle between the end, the connection and the
/// router to resolve. The host end registers no handler at all.
/// </remarks>
public class DataSetHostingTests
{
    private sealed class CountingStore : IDataSetStore
    {
        public int Opens { get; private set; }

        public ValueTask<DataSetOpening> OpenAsync(DataSetOpenRequest request, CancellationToken cancellationToken)
        {
            Opens++;
            return ValueTask.FromResult(new DataSetOpening { Grant = DataSetGrant.Ok });
        }

        public ValueTask<DataSetAcknowledgement> SaveAsync(DataSetContent content, CancellationToken cancellationToken)
            => ValueTask.FromResult(DataSetAcknowledgement.Accepted);

        public ValueTask<DataSetPayload?> LoadAsync(DataSetRequest request, CancellationToken cancellationToken)
            => ValueTask.FromResult<DataSetPayload?>(null);

        public ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<uint>>([]);

        public ValueTask<DataSetAcknowledgement> CloseAsync(uint dataSetId, CancellationToken cancellationToken)
            => ValueTask.FromResult(DataSetAcknowledgement.Accepted);

        public ValueTask<DataSetStatus> StatusAsync(uint dataSetId, CancellationToken cancellationToken)
            => ValueTask.FromResult(new DataSetStatus
            {
                DataSetId = dataSetId,
                Acknowledgement = DataSetAcknowledgement.InvalidDataSet,
            });
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
            .AddDataSetEquipment();

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
            .AddDataSetHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_and_brings_the_in_memory_store_with_it()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<DataSetEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<DataSetEquipment>(), equipment);
        Assert.IsType<DataSetInMemoryStore>(provider.GetRequiredService<IDataSetStore>());
    }

    [Fact]
    public async Task The_host_graph_resolves()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<DataSetHost>();

        Assert.NotNull(host);
        Assert.Same(provider.GetRequiredService<DataSetHost>(), host);
    }

    [Fact]
    public async Task Every_message_the_equipment_answers_is_in_the_routing_table()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in DataSetEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task The_equipment_claims_no_secondary_and_not_the_blocked_send()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The replies are this end's to send, so claiming one would be answering itself.
        Assert.False(router.TryGetHandler(13, 2, out _));
        Assert.False(router.TryGetHandler(13, 4, out _));

        // And the blocked send stays out: every block-transfer item in the catalogue is undefined.
        Assert.False(router.TryGetHandler(13, 9, out _));
        Assert.False(router.TryGetHandler(13, 10, out _));
    }

    [Fact]
    public async Task The_host_registration_adds_no_route_at_all()
    {
        await using var provider = BuildHost();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The claim in DataSetHost.HandledMessages, asserted against the table rather than left in
        // a comment: all seven primaries are the host's own, so nothing in stream 13 arrives here.
        for (byte function = 1; function <= 16; function++)
        {
            Assert.False(
                router.TryGetHandler(13, function, out _),
                $"S13F{function} should have no handler at a host.");
        }
    }

    [Fact]
    public async Task A_store_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new CountingStore();

        services.AddSingleton<IDataSetStore>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddDataSetEquipment();

        await using var provider = services.BuildServiceProvider();

        // TryAdd, so an application that registers its own store with its own dependencies can
        // still call AddDataSetEquipment for the routing.
        Assert.Same(mine, provider.GetRequiredService<IDataSetStore>());

        var equipment = provider.GetRequiredService<DataSetEquipment>();
        Assert.NotNull(equipment);
        Assert.Equal(0, mine.Opens);
    }
}
