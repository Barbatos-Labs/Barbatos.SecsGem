// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.ControlPrograms.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this package that cannot be tested any other way.
/// </summary>
/// <remarks>
/// Lighter than most packages' version of this test, and for a reason worth asserting: the equipment
/// end takes no connection, so there is no cycle between the end, the connection and the router to
/// resolve, and the host end registers no handler at all.
/// </remarks>
public class ControlProgramHostingTests
{
    private sealed class CountingStore : IControlProgramStore
    {
        public int Inquiries { get; private set; }

        public ValueTask<ControlProgramGrant> InquireAsync(uint length, CancellationToken cancellationToken)
        {
            Inquiries++;
            return ValueTask.FromResult(ControlProgramGrant.Accepted);
        }

        public ValueTask<ControlProgramAcknowledgement> SaveAsync(
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ControlProgramAcknowledgement.Accepted);

        public ValueTask<ReadOnlyMemory<byte>?> LoadAsync(
            string controlProgramId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<ReadOnlyMemory<byte>?>(null);

        public ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<ControlProgramAcknowledgement> AbortAsync(
            string controlProgramId,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(ControlProgramAcknowledgement.Denied);

        public ValueTask<string?> RevisionAsync(string controlProgramId, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(null);
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
            .AddControlProgramEquipment();

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
            .AddControlProgramHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_and_brings_the_in_memory_store_with_it()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<ControlProgramEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<ControlProgramEquipment>(), equipment);
        Assert.IsType<ControlProgramInMemoryStore>(provider.GetRequiredService<IControlProgramStore>());
    }

    [Fact]
    public async Task The_host_graph_resolves()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<ControlProgramHost>();

        Assert.NotNull(host);
        Assert.Same(provider.GetRequiredService<ControlProgramHost>(), host);
    }

    [Fact]
    public async Task Every_message_the_equipment_answers_is_in_the_routing_table()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in ControlProgramEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task The_equipment_claims_no_secondary_and_neither_blocked_pair()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The replies are this end's to send, so claiming one would be answering itself.
        for (byte function = 2; function <= 16; function += 2)
        {
            Assert.False(router.TryGetHandler(8, function, out _), $"S8F{function} is a reply.");
        }

        // And the blocked transfer stays out: every block-transfer item in the reference is
        // undefined, which is the same measurement that kept S13F9 and S13F10 out of
        // Barbatos.SecsGem.DataSets.
        Assert.False(router.TryGetHandler(8, 7, out _));
        Assert.False(router.TryGetHandler(8, 9, out _));
    }

    [Fact]
    public async Task The_host_registration_adds_no_route_at_all()
    {
        await using var provider = BuildHost();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // The claim in ControlProgramHost.HandledMessages, asserted against the table rather than
        // left in a comment: all eight primaries are the host's own.
        for (byte function = 1; function <= 16; function++)
        {
            Assert.False(
                router.TryGetHandler(8, function, out _),
                $"S8F{function} should have no handler at a host.");
        }
    }

    [Fact]
    public async Task A_store_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new CountingStore();

        services.AddSingleton<IControlProgramStore>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddControlProgramEquipment();

        await using var provider = services.BuildServiceProvider();

        // TryAdd, so an application that registers its own store with its own dependencies can
        // still call AddControlProgramEquipment for the routing.
        Assert.Same(mine, provider.GetRequiredService<IControlProgramStore>());

        Assert.NotNull(provider.GetRequiredService<ControlProgramEquipment>());
        Assert.Equal(0, mine.Inquiries);
    }
}
