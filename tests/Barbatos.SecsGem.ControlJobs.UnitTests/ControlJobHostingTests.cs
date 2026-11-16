// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Barbatos.SecsGem.Substrates;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// The container wiring, and the test that only a third package could write.
/// </summary>
public class ControlJobHostingTests
{
    private static ServiceProvider Build(Action<ControlJobManager>? configure = null)
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddControlJobs(configure);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_graph_resolves_despite_the_cycle()
    {
        await using var provider = Build();

        var manager = provider.GetRequiredService<ControlJobManager>();

        Assert.Same(manager, provider.GetRequiredService<ControlJobManager>());
        Assert.Empty(manager.Jobs);
        Assert.True(provider.GetRequiredService<ObjectServices>().CanAnswer);
    }

    [Fact]
    public async Task A_machine_can_start_life_with_a_queue()
    {
        await using var provider = Build(manager =>
        {
            Assert.True(manager.Create("CJ-1", ["CAR-001"]).IsAccepted);
            Assert.True(manager.Create("CJ-2").IsAccepted);
        });

        var manager = provider.GetRequiredService<ControlJobManager>();

        // Unusual but real: a machine that keeps its queue across a restart has jobs before any
        // host has said anything. Everything else about E94 arrives as an S14F5.
        Assert.Equal(2, manager.Jobs.Count);
        Assert.Equal(["CJ-1", "CJ-2"], manager.Queue);
    }

    [Fact]
    public async Task Control_jobs_declare_the_stream_14_primaries_and_nothing_else()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // E94 has no messages of its own, so object services is the only thing it can declare -
        // and all eleven of them, because a host that could read a job but not create one would be
        // half of E94.
        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        Assert.Equal(11, ObjectServices.HandledMessages.Count);
        Assert.False(router.TryGetHandler(3, 17, out _));
        Assert.IsType<ControlJobObjectProvider>(provider.GetRequiredService<IObjectProvider>());
    }

    [Fact]
    public async Task Registering_control_jobs_twice_adds_one_manager_and_one_provider()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddControlJobs()
            .AddControlJobs();

        await using var provider = services.BuildServiceProvider();

        // And the routing table survives it: the stream 14 primaries are declared once however
        // many times a provider is added.
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(14, 9, out _));
        Assert.IsType<ControlJobObjectProvider>(provider.GetRequiredService<ObjectServices>().Provider);
    }

    /// <summary>
    /// A machine that does E87, E90 and E94 answers stream 14 for all five object types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test this file exists for, and the one no single package can write. E87 contributes
    /// carriers and load ports, E90 substrates and substrate locations, E94 control jobs - three
    /// providers, one stream, one connection. That composition was broken until the object
    /// services package learned to compose two providers, and a third package is the first chance
    /// to prove it was not fixed for exactly two.
    /// </para>
    /// <para>
    /// It asserts the whole path from the outside: the type names, an instance of each, an
    /// attribute read through the composite, and a control job command that reaches E94's state
    /// machine while E87's and E90's objects are sitting on the same provider.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_machine_that_does_E87_E90_and_E94_answers_for_all_three()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
            .AddControlJobs(manager => Assert.True(manager.Create("CJ-1", ["CAR-001"]).IsAccepted));

        await using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<SubstrateTracker>().Add("W01", "SLOT-1", slotId: 1).IsAccepted);

        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);

        var composite = Assert.IsType<CompositeObjectProvider>(objects);

        Assert.Equal(3, composite.Providers.Count);

        var types = await objects.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(CarrierObjectProvider.CarrierType, types);
        Assert.Contains(CarrierObjectProvider.LoadPortType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateLocationType, types);
        Assert.Contains(ControlJobObjectProvider.ControlJobType, types);

        // The instances, not just the type names.
        Assert.Equal(
            "CJ-1",
            Assert.Single(await objects.GetInstancesAsync(
                ControlJobObjectProvider.ControlJobType,
                TestContext.Current.CancellationToken)));

        Assert.Equal(
            "W01",
            Assert.Single(await objects.GetInstancesAsync(
                SubstrateObjectProvider.SubstrateType,
                TestContext.Current.CancellationToken)));

        // An attribute through the composite, which is the read a host actually does.
        var values = await objects.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["CJ-1"], Attributes = ["State"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            nameof(ControlJobState.Queued),
            Assert.Single(Assert.Single(values).Attributes).Value.GetString());

        // And an action, which is the half of stream 14 that E94 needed built: it has to find the
        // one provider of three that owns the object.
        var acted = await objects.ActAsync(
            new ObjectActionRequest { Objects = ["CJ-1"], Command = ControlJobCommands.Select },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(acted).Acknowledgement);
        Assert.Equal(
            ControlJobState.Selected,
            provider.GetRequiredService<ControlJobManager>().Find("CJ-1")!.State);
    }

    [Fact]
    public async Task A_create_reaches_the_one_provider_that_can_make_the_object()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddControlJobs();

        await using var provider = services.BuildServiceProvider();

        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);

        // E87's provider refuses a create - a host cannot make a load port by asking - so the
        // composite has to carry on to the next one rather than return the first answer it gets.
        var result = await objects.CreateAsync(
            new ObjectCreateRequest
            {
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes = [new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-9") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, result.Acknowledgement);
        Assert.Equal("CJ-9", result.ObjectId);
        Assert.NotNull(provider.GetRequiredService<ControlJobManager>().Find("CJ-9"));

        // And a type nobody owns is still refused, which is the other half of that rule.
        var refused = await objects.CreateAsync(
            new ObjectCreateRequest { ObjectType = "Chamber" },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, refused.Acknowledgement);
    }

    [Fact]
    public async Task A_provider_already_registered_is_composed_with_rather_than_replaced()
    {
        var services = new ServiceCollection();
        var mine = new ObjectInMemoryProvider();

        mine.Add("CHAMBER-A", "Chamber");

        services.AddSingleton<IObjectProvider>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddControlJobs();

        await using var provider = services.BuildServiceProvider();

        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);

        var types = await objects.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        // An application whose objects go beyond control jobs keeps them.
        Assert.Contains("Chamber", types);
        Assert.Contains(ControlJobObjectProvider.ControlJobType, types);
    }

    [Fact]
    public void A_null_builder_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => ControlJobServiceCollectionExtensions.AddControlJobs(null!));
}
