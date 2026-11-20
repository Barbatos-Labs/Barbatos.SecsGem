// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.ControlJobs;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Substrates;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.MaterialFlow.UnitTests;

/// <summary>
/// The container wiring, where a bridge with one end would be a bridge to nowhere.
/// </summary>
public class MaterialFlowHostingTests
{
    private static readonly byte[] SlotMap = [1, 1, 0, 1];

    private static ServiceProvider Build(Action<CarrierSubstrateOptions>? configure = null)
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager =>
            {
                manager.AddPort(1);
                manager.SetServiceStatus(1, inService: true);
            })
            .AddSubstrateTracking(tracker => tracker.AddLocation("CHUCK"))
            .AddCarrierSubstrateBridge(configure);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_bridge_resolves_attached_so_a_slot_map_needs_no_other_code()
    {
        await using var provider = Build();

        var bridge = provider.GetRequiredService<CarrierSubstrateBridge>();

        Assert.True(bridge.IsAttached);
        Assert.Same(bridge, provider.GetRequiredService<CarrierSubstrateBridge>());

        var carriers = provider.GetRequiredService<CarrierManager>();

        Assert.True(carriers.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(carriers.PlaceCarrier(1, "CAR-001").IsAccepted);
        Assert.True(carriers.ReadCarrierId("CAR-001", verified: true).IsAccepted);
        Assert.True(carriers.ReadSlotMap("CAR-001", SlotMap, verified: true).IsAccepted);

        // Three wafers out of a four-slot carrier, and not a line of application code between the
        // mapper and the tracker.
        Assert.Equal(3, provider.GetRequiredService<SubstrateTracker>().Substrates.Count);
        Assert.Equal("CAR-001", Assert.Single(bridge.OpenCarriers));
    }

    [Fact]
    public async Task The_convention_comes_from_the_registration()
    {
        await using var provider = Build(options =>
        {
            options.SubstrateName = (carrier, slot) => $"{carrier}.{slot}";
            options.SubstrateType = "300mm Si";
            options.ReturnsToItsOwnSlot = false;
        });

        var bridge = provider.GetRequiredService<CarrierSubstrateBridge>();

        Assert.Equal("CAR-001.3", bridge.Options.SubstrateName("CAR-001", 3));
        Assert.Equal("300mm Si", bridge.Options.SubstrateType);
        Assert.False(bridge.Options.ReturnsToItsOwnSlot);

        // And the options object in the container is the one the bridge is using, so an
        // application can read it back rather than keeping its own copy.
        Assert.Same(provider.GetRequiredService<CarrierSubstrateOptions>(), bridge.Options);
    }

    [Fact]
    public async Task The_bridge_declares_no_message_of_its_own()
    {
        await using var provider = Build();

        _ = provider.GetRequiredService<CarrierSubstrateBridge>();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // E87's five and stream 14's eleven are there because those registrations put them there.
        // The join itself is behaviour between two state machines, not a transaction, and a
        // package that invented a message for it would be inventing a standard.
        Assert.True(router.TryGetHandler(3, 17, out _));
        Assert.True(router.TryGetHandler(14, 1, out _));
        Assert.False(router.TryGetHandler(3, 19, out _));
    }

    [Fact]
    public async Task Registering_the_bridge_twice_adds_one()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddSubstrateTracking()
            .AddCarrierSubstrateBridge()
            .AddCarrierSubstrateBridge();

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<CarrierSubstrateBridge>();

        Assert.Same(bridge, provider.GetRequiredService<CarrierSubstrateBridge>());
        Assert.True(bridge.IsAttached);
    }

    [Fact]
    public async Task A_bridge_with_nothing_to_bridge_says_so_when_it_is_resolved()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager => manager.AddPort(1))
            .AddCarrierSubstrateBridge();

        await using var provider = services.BuildServiceProvider();

        // E87 is registered and E90 is not, so there is no tracker to bridge to. Failing at
        // resolve is the right moment to find out: the alternative is a machine that reads slot
        // maps and silently tracks nothing.
        var thrown = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<CarrierSubstrateBridge>());

        Assert.Contains(nameof(SubstrateTracker), thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>The E40 bridge resolves attached, so a job that ends settles its lot.</summary>
    /// <remarks>
    /// This test exists because a negative control went looking for it and found nothing: the
    /// registration could have been leaving the bridge detached and the whole suite would have
    /// passed.
    /// </remarks>
    [Fact]
    public async Task The_process_job_bridge_resolves_attached_and_settles_a_stopped_job()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddSubstrateTracking(tracker =>
            {
                tracker.AddLocation("CHUCK");
                tracker.AddLocation("SLOT-1");
            })
            .AddProcessJobEquipment()
            .AddProcessJobSubstrateBridge();

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<ProcessJobSubstrateBridge>();

        Assert.True(bridge.IsAttached);
        Assert.Same(bridge, provider.GetRequiredService<ProcessJobSubstrateBridge>());

        var substrates = provider.GetRequiredService<SubstrateTracker>();
        var jobs = provider.GetRequiredService<ProcessJobManager>();

        Assert.True(substrates.Add("W01", "SLOT-1", slotId: 1).IsAccepted);
        Assert.True(jobs.Create("PJ-1", "RECIPE-7", ["W01"]).IsAccepted);
        Assert.True(jobs.BeginSetup("PJ-1").IsAccepted);
        Assert.True(jobs.SetupComplete("PJ-1").IsAccepted);
        Assert.True(substrates.MoveTo("W01", "CHUCK").IsAccepted);
        Assert.True(substrates.BeginProcessing("W01").IsAccepted);

        Assert.True(jobs.Command("PJ-1", ProcessJobCommands.Stop).IsAccepted);
        Assert.True(jobs.StopComplete("PJ-1").IsAccepted);

        // Not a line of application code between E40's ending and E90's.
        Assert.Equal(SubstrateProcessingState.Stopped, substrates.FindSubstrate("W01")?.ProcessingState);
    }

    [Fact]
    public async Task The_process_job_bridge_takes_its_convention_from_the_registration()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddSubstrateTracking()
            .AddProcessJobEquipment()
            .AddProcessJobSubstrateBridge(options => options.SkipsUntouched = false);

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<ProcessJobSubstrateBridge>();

        Assert.False(bridge.Options.SkipsUntouched);
        Assert.True(bridge.Options.EndsWorkInProgress);
        Assert.Same(provider.GetRequiredService<ProcessJobSubstrateOptions>(), bridge.Options);
    }

    [Fact]
    public async Task A_process_job_bridge_with_no_tracker_says_so_when_it_is_resolved()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddProcessJobEquipment()
            .AddProcessJobSubstrateBridge();

        await using var provider = services.BuildServiceProvider();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ProcessJobSubstrateBridge>());

        Assert.Contains(nameof(SubstrateTracker), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_control_job_bridge_resolves_attached_so_a_lot_starts_itself()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment(manager =>
            {
                manager.AddPort(1);
                manager.SetServiceStatus(1, inService: true);
                manager.SetTransferState(1, PortTransferState.ReadyToLoad);
            })
            .AddControlJobs(jobs => jobs.Create("CJ-1", ["CAR-001"]))
            .AddControlJobCarrierBridge();

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<ControlJobCarrierBridge>();

        Assert.True(bridge.IsAttached);
        Assert.Same(bridge, provider.GetRequiredService<ControlJobCarrierBridge>());

        var jobs = provider.GetRequiredService<ControlJobManager>();
        var carriers = provider.GetRequiredService<CarrierManager>();

        Assert.True(jobs.Select("CJ-1").IsAccepted);
        Assert.True(carriers.PlaceCarrier(1, "CAR-001").IsAccepted);
        Assert.True(carriers.ReadCarrierId("CAR-001", verified: true).IsAccepted);
        Assert.True(carriers.ReadSlotMap("CAR-001", SlotMap, verified: true).IsAccepted);

        // A carrier arrived, was agreed, and the lot is running - with no application code
        // between the two standards.
        Assert.Equal(ControlJobState.Executing, jobs.Find("CJ-1")!.State);
    }

    [Fact]
    public async Task The_control_job_conventions_come_from_the_registration()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddCarrierEquipment()
            .AddControlJobs()
            .AddControlJobCarrierBridge(options => options.RequiresCarrierAtPort = false);

        await using var provider = services.BuildServiceProvider();

        var bridge = provider.GetRequiredService<ControlJobCarrierBridge>();

        Assert.False(bridge.Options.RequiresCarrierAtPort);
        Assert.True(bridge.Options.IsCarrier("CAR-001"));
        Assert.Same(provider.GetRequiredService<ControlJobCarrierOptions>(), bridge.Options);
    }

    [Fact]
    public async Task A_control_job_bridge_with_no_carrier_manager_says_so_when_it_is_resolved()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddControlJobs()
            .AddControlJobCarrierBridge();

        await using var provider = services.BuildServiceProvider();

        var thrown = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ControlJobCarrierBridge>());

        Assert.Contains(nameof(CarrierManager), thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_registration_adds_no_message_of_its_own()
    {
        static ISecsGemBuilder Compose(IServiceCollection services)
            => services
                .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
                .UseInMemoryTransport(isActive: false)
                .AddCarrierEquipment()
                .AddControlJobs();

        var without = new ServiceCollection();
        var with = new ServiceCollection();

        Compose(without);
        Compose(with).AddControlJobCarrierBridge();

        await using var alone = without.BuildServiceProvider();
        await using var bridged = with.BuildServiceProvider();

        var bare = alone.GetRequiredService<ISecsMessageRouter>();
        var joined = bridged.GetRequiredService<ISecsMessageRouter>();

        Assert.NotNull(bridged.GetRequiredService<ControlJobCarrierBridge>());

        // Both ends declare what they declare - E87's S3F17, E94's stream 14 - and the join
        // declares nothing, because a join is behaviour and not a transaction. Comparing the two
        // graphs is the honest form of that claim: asserting an empty router would be false about
        // the container and would say nothing at all about this registration.
        foreach (var stream in new byte[] { 1, 2, 3, 6, 14, 16, 17 })
        {
            for (byte function = 1; function < 64; function += 2)
            {
                Assert.Equal(
                    bare.TryGetHandler(stream, function, out _),
                    joined.TryGetHandler(stream, function, out _));
            }
        }
    }

    [Fact]
    public void A_null_builder_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => MaterialFlowServiceCollectionExtensions.AddCarrierSubstrateBridge(null!));

        Assert.Throws<ArgumentNullException>(
            () => MaterialFlowServiceCollectionExtensions.AddProcessJobSubstrateBridge(null!));

        Assert.Throws<ArgumentNullException>(
            () => MaterialFlowServiceCollectionExtensions.AddControlJobCarrierBridge(null!));
    }
}
