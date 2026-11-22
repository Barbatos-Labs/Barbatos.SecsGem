// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// The container wiring, where E87 needing two streams becomes one registration or two bugs.
/// </summary>
public class CarrierHostingTests
{
    private static ServiceProvider BuildEquipment(Action<CarrierManager>? configure = null)
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(configure ?? (manager =>
            {
                manager.AddPort(1);
                manager.AddPort(2);
            }));

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
            .AddCarrierHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_despite_the_cycle()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<CarrierEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<CarrierEquipment>(), equipment);
        Assert.Same(provider.GetRequiredService<CarrierManager>(), provider.GetRequiredService<CarrierManager>());
    }

    [Fact]
    public async Task The_equipment_declares_both_halves_of_E87()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Nine carrier primaries and eleven of object services. A machine that registered only
        // the actions would answer every S3F17 and never give up a slot map.
        foreach (var (stream, function) in CarrierEquipment.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        Assert.Equal(9, CarrierEquipment.HandledMessages.Count);
        Assert.Equal(12, ObjectServices.HandledMessages.Count);
    }

    [Fact]
    public async Task The_object_provider_the_equipment_registers_is_the_carrier_one()
    {
        await using var provider = BuildEquipment();

        // Which is what makes the attributes E87 defines readable: the generic in-memory provider
        // would answer stream 14 with objects nobody put there.
        Assert.IsType<CarrierObjectProvider>(provider.GetRequiredService<IObjectProvider>());
        Assert.True(provider.GetRequiredService<ObjectServices>().CanAnswer);
    }

    [Fact]
    public async Task The_ports_declared_at_registration_are_there_and_out_of_service()
    {
        await using var provider = BuildEquipment();

        var manager = provider.GetRequiredService<CarrierManager>();

        Assert.Equal(2, manager.Ports.Count);
        Assert.All(manager.Ports, port => Assert.Equal(PortTransferState.OutOfService, port.TransferState));
    }

    [Fact]
    public async Task A_machine_that_declares_no_port_refuses_every_action_that_names_one()
    {
        await using var provider = BuildEquipment(configure: _ => { });

        var manager = provider.GetRequiredService<CarrierManager>();

        Assert.Empty(manager.Ports);

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ReserveAtPort,
            CarrierId = "CAR-001",
            PortId = 1,
        });

        // Ports are hardware: a host cannot invent one by naming it.
        Assert.Equal(CarrierAcknowledgement.InvalidData, refusal.Acknowledgement);
    }

    [Fact]
    public async Task The_host_resolves_can_ask_and_declares_nothing()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<CarrierHost>();
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.NotNull(host);

        // E87 gives a host no primary to answer, so an empty routing table is the correct outcome.
        foreach (var (stream, function) in CarrierEquipment.HandledMessages)
        {
            Assert.False(router.TryGetHandler(stream, function, out _), $"a host should not answer S{stream}F{function}");
        }

        // But it does get object services, because a host that cannot read a carrier's attributes
        // is half of one - and with no provider of its own it declares nothing for those either.
        var objects = provider.GetRequiredService<ObjectServices>();

        Assert.False(objects.CanAnswer);
        Assert.False(router.TryGetHandler(14, 1, out _));
    }

    [Fact]
    public async Task A_provider_already_registered_is_composed_with_the_carrier_one()
    {
        var services = new ServiceCollection();
        var mine = new ObjectInMemoryProvider();

        mine.Add("JOB-7", "ControlJob");

        services.AddSingleton<IObjectProvider>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1));

        await using var provider = services.BuildServiceProvider();

        // An application whose objects go beyond carriers and ports keeps them: stream 14 answers
        // for both, because the providers compose rather than replacing each other.
        var composite = Assert.IsType<CompositeObjectProvider>(
            provider.GetRequiredService<ObjectServices>().Provider);

        Assert.Contains(mine, composite.Providers);
        Assert.Contains(composite.Providers, candidate => candidate is CarrierObjectProvider);

        var types = await composite.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("ControlJob", types);
        Assert.Contains(CarrierObjectProvider.CarrierType, types);
    }

    /// <summary>Registering the equipment twice declares both halves of E87 once.</summary>
    /// <remarks>
    /// Both halves is what makes this worth a test here: the five carrier primaries are this
    /// package's own set and the eleven of stream 14 are the object services package's, so a
    /// second call has two chances to collide. The services were already idempotent through
    /// <c>TryAdd</c>; <c>DeclareOnce</c> on the builder is where the routes caught up.
    /// </remarks>
    [Fact]
    public async Task Registering_the_equipment_twice_declares_both_halves_once()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddCarrierEquipment(manager => manager.AddPort(2));

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in CarrierEquipment.HandledMessages.Concat(ObjectServices.HandledMessages))
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        // And the first registration's configure is the one that ran, because the manager is a
        // TryAdd singleton: one port, not two, which is the same "first one wins" the services
        // have always had.
        Assert.Equal(1, Assert.Single(provider.GetRequiredService<CarrierManager>().Ports).PortId);
    }
}
