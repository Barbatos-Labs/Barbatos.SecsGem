// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Hsms;
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

    [Fact]
    public void A_null_builder_is_rejected()
        => Assert.Throws<ArgumentNullException>(
            () => MaterialFlowServiceCollectionExtensions.AddCarrierSubstrateBridge(null!));
}
