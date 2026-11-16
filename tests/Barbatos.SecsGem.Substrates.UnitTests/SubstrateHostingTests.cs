// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Substrates.UnitTests;

/// <summary>
/// The container wiring, including the one thing no single package can check.
/// </summary>
public class SubstrateHostingTests
{
    private static ServiceProvider Build(Action<SubstrateTracker>? configure = null)
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddSubstrateTracking(configure ?? (tracker =>
            {
                tracker.AddLocation("SLOT-1");
                tracker.AddLocation("CHUCK");
            }));

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_graph_resolves_and_the_locations_declared_at_registration_are_there()
    {
        await using var provider = Build();

        var tracker = provider.GetRequiredService<SubstrateTracker>();

        Assert.Same(tracker, provider.GetRequiredService<SubstrateTracker>());
        Assert.Equal(2, tracker.Locations.Count);
        Assert.All(tracker.Locations, location => Assert.Equal(SubstrateLocationState.Unoccupied, location.State));
    }

    [Fact]
    public async Task Substrate_tracking_declares_the_stream_14_primaries_and_nothing_else()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // E90 has no messages of its own, so the only thing it can declare is object services -
        // and that is not a shortfall, it is the standard.
        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        Assert.False(router.TryGetHandler(3, 17, out _));
        Assert.IsType<SubstrateObjectProvider>(provider.GetRequiredService<IObjectProvider>());
    }

    /// <summary>
    /// A machine that does both E87 and E90 answers stream 14 for both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test this file exists for, and the one neither package can write on its own. Carrier
    /// management registers an object provider for carriers and load ports; substrate tracking
    /// registers one for substrates and locations; stream 14 has one provider per connection. A
    /// registration that quietly kept the first and dropped the second would leave a machine
    /// answering carrier attributes and no substrate attributes at all, with nothing anywhere
    /// saying so - the worst shape a bug can have.
    /// </para>
    /// <para>
    /// So this asserts the composition from the outside: both object types, and an instance of
    /// each actually readable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_machine_that_does_both_E87_and_E90_answers_stream_14_for_both()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"));

        await using var provider = services.BuildServiceProvider();

        var tracker = provider.GetRequiredService<SubstrateTracker>();

        Assert.True(tracker.Add("W01", "SLOT-1", slotId: 1).IsAccepted);

        // Through ObjectServices, which is the path the library itself uses: resolving
        // IObjectProvider from the container gives whichever was registered last, and the
        // composition is what this test is about.
        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);
        Assert.IsType<CompositeObjectProvider>(objects);

        var types = await objects.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(CarrierObjectProvider.CarrierType, types);
        Assert.Contains(CarrierObjectProvider.LoadPortType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateLocationType, types);

        // And the instances are reachable, not just the type names.
        var ports = await objects.GetInstancesAsync(
            CarrierObjectProvider.LoadPortType,
            TestContext.Current.CancellationToken);

        Assert.Equal(CarrierObjectProvider.PortSpecification(1), Assert.Single(ports));

        var substrates = await objects.GetInstancesAsync(
            SubstrateObjectProvider.SubstrateType,
            TestContext.Current.CancellationToken);

        Assert.Equal("W01", Assert.Single(substrates));

        var values = await objects.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["W01"], Attributes = ["SlotID"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, Assert.Single(Assert.Single(values).Attributes).Value.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_provider_already_registered_is_composed_with_rather_than_replaced()
    {
        var services = new ServiceCollection();
        var mine = new ObjectInMemoryProvider();

        mine.Add("JOB-7", "ControlJob");

        services.AddSingleton<IObjectProvider>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"));

        await using var provider = services.BuildServiceProvider();

        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);

        var types = await objects.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        // An application with objects of its own keeps them: calling two methods that each add a
        // provider and getting both is less surprising than getting whichever ran first.
        Assert.Contains("ControlJob", types);
        Assert.Contains(SubstrateObjectProvider.SubstrateType, types);
    }
}
