// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Subsystems.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this package that cannot be tested any other way.
/// </summary>
/// <remarks>
/// The handlers depend on the end, the end depends on the connection, and the connection depends on
/// the router the handlers are registered into. That cycle is real at both ends here, because both
/// ends of stream 18 send primaries - and the only proof it resolves is resolving it.
/// </remarks>
public class SubsystemHostingTests
{
    private sealed class CountingStore : ISubsystemStore
    {
        public int Commands { get; private set; }

        public ValueTask<SubsystemCommandResult> CommandAsync(
            SubsystemCommand command,
            CancellationToken cancellationToken)
        {
            Commands++;

            return ValueTask.FromResult(new SubsystemCommandResult
            {
                Subsystem = command.Subsystem,
                Acknowledgement = SubsystemCommandAcknowledgement.Accepted,
            });
        }

        public ValueTask<string?> StatusAsync(SubsystemId id, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(null);

        public ValueTask<string?> ReadAsync(SubsystemId id, uint variableId, CancellationToken cancellationToken)
            => ValueTask.FromResult<string?>(null);

        public ValueTask<SubsystemAcknowledgement> WriteAsync(
            SubsystemVariable variable,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(SubsystemAcknowledgement.UnknownVariable);

        public ValueTask<IReadOnlyList<Subsystem>> ListAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<Subsystem>>([]);

        public ValueTask<SubsystemCodeCondition?> ReadCodeAsync(string targetId, CancellationToken cancellationToken)
            => ValueTask.FromResult<SubsystemCodeCondition?>(null);
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
            .AddSubsystemEquipment();

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
            .AddSubsystemHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_and_brings_the_in_memory_store_with_it()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<SubsystemEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<SubsystemEquipment>(), equipment);
        Assert.IsType<SubsystemInMemoryStore>(provider.GetRequiredService<ISubsystemStore>());
    }

    [Fact]
    public async Task The_host_graph_resolves_despite_the_cycle()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<SubsystemHost>();

        Assert.NotNull(host);
        Assert.Same(provider.GetRequiredService<SubsystemHost>(), host);
    }

    [Fact]
    public async Task Every_message_the_equipment_answers_is_in_its_routing_table()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in SubsystemEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task Every_message_the_host_answers_is_in_its_routing_table()
    {
        await using var provider = BuildHost();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in SubsystemHost.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task Neither_end_claims_the_other_s_primaries()
    {
        await using var equipment = BuildEquipment();
        await using var host = BuildHost();

        var equipmentRouter = equipment.GetRequiredService<ISecsMessageRouter>();
        var hostRouter = host.GetRequiredService<ISecsMessageRouter>();

        // The thing the routing table cannot catch, because the two sets are disjoint: two calls
        // on one builder would not collide. So it is asserted here instead.
        foreach (var (stream, function) in SubsystemHost.HandledMessages)
        {
            Assert.False(
                equipmentRouter.TryGetHandler(stream, function, out _),
                $"the equipment claimed S{stream}F{function}, which it is the one that sends.");
        }

        foreach (var (stream, function) in SubsystemEquipment.HandledMessages)
        {
            Assert.False(
                hostRouter.TryGetHandler(stream, function, out _),
                $"the host claimed S{stream}F{function}, which it is the one that sends.");
        }
    }

    [Fact]
    public async Task Neither_end_claims_a_secondary_or_a_function_outside_the_sixteen()
    {
        await using var equipment = BuildEquipment();
        await using var host = BuildHost();

        var equipmentRouter = equipment.GetRequiredService<ISecsMessageRouter>();
        var hostRouter = host.GetRequiredService<ISecsMessageRouter>();

        // Every even function is somebody's reply, and nothing above 16 exists in stream 18.
        for (byte function = 2; function <= 20; function += 2)
        {
            Assert.False(equipmentRouter.TryGetHandler(18, function, out _), $"S18F{function} is a reply.");
            Assert.False(hostRouter.TryGetHandler(18, function, out _), $"S18F{function} is a reply.");
        }

        Assert.False(equipmentRouter.TryGetHandler(18, 17, out _));
        Assert.False(hostRouter.TryGetHandler(18, 17, out _));
    }

    [Fact]
    public async Task A_store_already_registered_wins_over_the_in_memory_one()
    {
        var services = new ServiceCollection();
        var mine = new CountingStore();

        services.AddSingleton<ISubsystemStore>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddSubsystemEquipment();

        await using var provider = services.BuildServiceProvider();

        // TryAdd, so an application that registers its own store with its own dependencies can
        // still call AddSubsystemEquipment for the routing.
        Assert.Same(mine, provider.GetRequiredService<ISubsystemStore>());

        Assert.NotNull(provider.GetRequiredService<SubsystemEquipment>());
        Assert.Equal(0, mine.Commands);
    }
}
