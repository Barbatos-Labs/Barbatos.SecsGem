// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.ControlJobs;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Barbatos.SecsGem.Substrates;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// The container wiring, and the test that only a fourth package could write.
/// </summary>
public class ProcessJobHostingTests
{
    private const string Recipe = "RECIPE-7";

    private static ServiceProvider BuildEquipment(Action<ProcessJobManager>? configure = null)
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddProcessJobEquipment(configure);

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
            .AddProcessJobHost();

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_equipment_graph_resolves_despite_the_cycle()
    {
        await using var provider = BuildEquipment();

        var equipment = provider.GetRequiredService<ProcessJobEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(equipment, provider.GetRequiredService<ProcessJobEquipment>());
        Assert.Same(
            provider.GetRequiredService<ProcessJobManager>(),
            provider.GetRequiredService<ProcessJobManager>());
    }

    [Fact]
    public async Task A_machine_can_declare_how_many_jobs_it_holds_and_start_with_some()
    {
        await using var provider = BuildEquipment(manager =>
        {
            manager.JobSpace = 4;
            Assert.True(manager.Create("PJ-1", Recipe).IsAccepted);
        });

        var manager = provider.GetRequiredService<ProcessJobManager>();

        // PRJOBSPACE is the machine's own fact about itself, so it belongs in the registration
        // rather than in a message: a host cannot tell a tool how many jobs it can hold.
        Assert.Equal((ushort)4, manager.JobSpace);
        Assert.Single(manager.Jobs);
    }

    [Fact]
    public async Task The_equipment_declares_both_halves_of_E40()
    {
        await using var provider = BuildEquipment();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Twelve of stream 16 and eleven of object services. A machine that declared only the
        // twelve would drive a job it could never be asked to describe.
        foreach (var (stream, function) in ProcessJobEquipment.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert.True(router.TryGetHandler(stream, function, out _), $"S{stream}F{function} should have a handler");
        }

        Assert.Equal(12, ProcessJobEquipment.HandledMessages.Count);
        Assert.Equal(14, ObjectServices.HandledMessages.Count);

        // And not the two the equipment sends: an end that answered its own primaries would be
        // talking to itself.
        Assert.False(router.TryGetHandler(16, 11, out _));
        Assert.False(router.TryGetHandler(16, 29, out _));

        Assert.IsType<ProcessJobObjectProvider>(provider.GetRequiredService<IObjectProvider>());
    }

    [Fact]
    public async Task The_host_declares_the_two_the_equipment_sends_and_no_objects_of_its_own()
    {
        await using var provider = BuildHost();

        var host = provider.GetRequiredService<ProcessJobHost>();
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.NotNull(host);
        Assert.True(router.TryGetHandler(16, 11, out _));
        Assert.True(router.TryGetHandler(16, 29, out _));

        // None of the twelve, because those are the equipment's.
        foreach (var (stream, function) in ProcessJobEquipment.HandledMessages)
        {
            Assert.False(router.TryGetHandler(stream, function, out _), $"a host should not answer S{stream}F{function}");
        }

        // Object services, so a host can read a job's attributes - and no provider, so it declares
        // none of stream 14's primaries and answers S9F5 for them instead.
        var objects = provider.GetRequiredService<ObjectServices>();

        Assert.False(objects.CanAnswer);
        Assert.False(router.TryGetHandler(14, 1, out _));
    }

    [Fact]
    public async Task Registering_the_equipment_twice_adds_one_of_everything()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddProcessJobEquipment()
            .AddProcessJobEquipment();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(16, 1, out _));
        Assert.IsType<ProcessJobObjectProvider>(provider.GetRequiredService<ObjectServices>().Provider);
    }

    /// <summary>
    /// A machine that does E87, E90, E94 and E40 answers stream 14 for all six object types.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The test this file exists for. E87 contributes carriers and load ports, E90 substrates and
    /// substrate locations, E94 control jobs, E40 process jobs - four providers, one stream, one
    /// connection. The composition was broken until the E90 chapter fixed it, the E94 chapter was
    /// the first proof it worked for three, and this is the first proof it was not fixed for
    /// exactly three.
    /// </para>
    /// <para>
    /// It also puts the two job standards side by side, which is the arrangement a 300 mm tool
    /// actually has: a control job is the lot a host asked for, and a process job is the machine
    /// running part of it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_machine_that_does_all_four_material_standards_answers_for_all_of_them()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking(tracker => tracker.AddLocation("SLOT-1"))
            .AddControlJobs(manager => Assert.True(manager.Create("CJ-1", ["CAR-001"]).IsAccepted))
            .AddProcessJobEquipment(manager => Assert.True(manager.Create("PJ-1", Recipe, ["W01"]).IsAccepted));

        await using var provider = services.BuildServiceProvider();

        Assert.True(provider.GetRequiredService<SubstrateTracker>().Add("W01", "SLOT-1", slotId: 1).IsAccepted);

        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);

        var composite = Assert.IsType<CompositeObjectProvider>(objects);

        Assert.Equal(4, composite.Providers.Count);

        var types = await objects.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(CarrierObjectProvider.CarrierType, types);
        Assert.Contains(CarrierObjectProvider.LoadPortType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateLocationType, types);
        Assert.Contains(ControlJobObjectProvider.ControlJobType, types);
        Assert.Contains(ProcessJobObjectProvider.ProcessJobType, types);

        // The instances, not only the type names.
        Assert.Equal(
            "PJ-1",
            Assert.Single(await objects.GetInstancesAsync(
                ProcessJobObjectProvider.ProcessJobType,
                TestContext.Current.CancellationToken)));

        // An attribute through the composite, from the fourth provider of four.
        var values = await objects.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["PJ-1"], Attributes = ["PRSTATE"] },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (byte)ProcessJobState.Queued,
            Assert.Single(Assert.Single(values).Attributes).Value.FirstValueOrDefault<byte>());

        // And an action, which has to find the one provider of four that owns the object - with a
        // control job of the same shape sitting beside it.
        var acted = await objects.ActAsync(
            new ObjectActionRequest { Objects = ["PJ-1", "CJ-1"], Command = "CANCEL" },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, acted.Count);
        Assert.All(acted, result => Assert.Equal(ObjectAcknowledgement.Successful, result.Acknowledgement));
        Assert.Equal(
            ProcessJobState.ProcessComplete,
            provider.GetRequiredService<ProcessJobManager>().Find("PJ-1")!.State);
        Assert.Equal(
            ControlJobState.Completed,
            provider.GetRequiredService<ControlJobManager>().Find("CJ-1")!.State);
    }

    [Fact]
    public async Task A_machine_that_does_E40_and_E87_declares_both_message_sets()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddProcessJobEquipment();

        await using var provider = services.BuildServiceProvider();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Stream 3 from E87, stream 16 from E40, stream 14 from both: three streams, and no
        // registration treading on another's.
        Assert.True(router.TryGetHandler(3, 17, out _));
        Assert.True(router.TryGetHandler(16, 1, out _));
        Assert.True(router.TryGetHandler(14, 1, out _));
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
            .AddProcessJobEquipment();

        await using var provider = services.BuildServiceProvider();

        var objects = provider.GetRequiredService<ObjectServices>().Provider;

        Assert.NotNull(objects);

        var types = await objects.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Chamber", types);
        Assert.Contains(ProcessJobObjectProvider.ProcessJobType, types);
    }

    [Fact]
    public void A_null_builder_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => ProcessJobServiceCollectionExtensions.AddProcessJobEquipment(null!));

        Assert.Throws<ArgumentNullException>(
            () => ProcessJobServiceCollectionExtensions.AddProcessJobHost(null!));
    }
}
