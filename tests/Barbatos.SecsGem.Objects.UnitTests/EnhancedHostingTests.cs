// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// The container wiring for E39's enhanced stream 2, and the decision it encodes: which end this
/// is.
/// </summary>
public class EnhancedHostingTests
{
    private static ServiceProvider Build(
        bool answering,
        bool twice = false,
        bool alsoStreamFourteen = false,
        IEnhancedServiceProvider? mine = null)
    {
        var services = new ServiceCollection();

        if (mine is not null)
        {
            services.AddSingleton(mine);
        }

        var builder = services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = answering ? HsmsConnectionMode.Passive : HsmsConnectionMode.Active;
            })
            .UseInMemoryTransport(isActive: !answering);

        if (answering)
        {
            builder.AddEnhancedServiceProvider<EnhancedInMemoryProvider>();

            if (twice)
            {
                builder.AddEnhancedServiceProvider<EnhancedInMemoryProvider>();
            }
        }
        else
        {
            builder.AddEnhancedServices();
        }

        if (alsoStreamFourteen)
        {
            builder.AddObjectProvider();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_asking_end_resolves_and_declares_nothing()
    {
        await using var provider = Build(answering: false);

        var host = provider.GetRequiredService<EnhancedServicesHost>();
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.NotNull(host);
        Assert.Same(host, provider.GetRequiredService<EnhancedServicesHost>());

        // Every primary in this range is host to equipment, so a host answers none of them and
        // declaring them would make it answer every request with an empty list.
        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages)
        {
            Assert.False(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} should have no handler on a host");
        }
    }

    [Fact]
    public async Task The_answering_end_declares_all_eight()
    {
        await using var provider = Build(answering: true);

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.Equal(8, EnhancedServicesEquipment.HandledMessages.Count);
        Assert.IsType<EnhancedInMemoryProvider>(provider.GetRequiredService<IEnhancedServiceProvider>());

        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task Every_declared_primary_is_odd_because_a_secondary_is_not_a_primary()
    {
        await using var provider = Build(answering: true);

        Assert.NotNull(provider.GetRequiredService<EnhancedServicesEquipment>());
        Assert.All(
            EnhancedServicesEquipment.HandledMessages,
            message => Assert.True(message.Function % 2 == 1, $"S2F{message.Function} is not a primary"));

        Assert.Equal(
            EnhancedServicesEquipment.HandledMessages.Count,
            EnhancedServicesEquipment.HandledMessages.Distinct().Count());
    }

    /// <summary>
    /// One provider, unlike stream 14, and the difference is deliberate.
    /// </summary>
    /// <remarks>
    /// Object providers compose because stream 14's object types belong to several standards at
    /// once. These replies are positional - S2F56, S2F58 and S2F60 carry values and no identifiers
    /// - so two providers each answering half a request cannot be merged into one list without
    /// putting a host's readings out of step. So the first registration wins, and a test says so
    /// rather than leaving a reader to wonder whether the asymmetry was an oversight.
    /// </remarks>
    [Fact]
    public async Task A_provider_already_registered_keeps_its_place()
    {
        var mine = new EnhancedInMemoryProvider();

        mine.AddObject("PM1");

        var services = new ServiceCollection();

        services.AddSingleton<IEnhancedServiceProvider>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddEnhancedServiceProvider<EnhancedInMemoryProvider>();

        await using var provider = services.BuildServiceProvider();

        Assert.Same(mine, provider.GetRequiredService<IEnhancedServiceProvider>());
        Assert.Same(mine, provider.GetRequiredService<EnhancedServicesEquipment>().Provider);
    }

    [Fact]
    public async Task Adding_it_twice_is_not_a_start_up_failure()
    {
        await using var provider = Build(answering: true, twice: true);

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(2, 57, out _));
        Assert.NotNull(provider.GetRequiredService<EnhancedServicesEquipment>());
    }

    [Fact]
    public async Task Both_halves_of_E39_answer_on_one_machine()
    {
        await using var provider = Build(answering: true, alsoStreamFourteen: true);

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Nineteen primaries from one standard across two streams, and no collision: this is the
        // arrangement the package exists to make possible, and it is cheap to assert.
        foreach (var (stream, function) in EnhancedServicesEquipment.HandledMessages.Concat(ObjectServices.HandledMessages))
        {
            Assert.True(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} should have a handler");
        }

        Assert.Equal(22, EnhancedServicesEquipment.HandledMessages.Count + ObjectServices.HandledMessages.Count);
        Assert.NotNull(provider.GetRequiredService<ObjectServices>());
        Assert.NotNull(provider.GetRequiredService<EnhancedServicesEquipment>());
    }

    [Fact]
    public void A_builder_nobody_passed_throws()
    {
        Assert.Throws<ArgumentNullException>(() => ((ISecsGemBuilder)null!).AddEnhancedServices());
        Assert.Throws<ArgumentNullException>(() =>
            ((ISecsGemBuilder)null!).AddEnhancedServiceProvider<EnhancedInMemoryProvider>());
    }
}
