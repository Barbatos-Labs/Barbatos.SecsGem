// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// The container wiring, and the one decision it encodes: whether this end answers.
/// </summary>
public class ObjectHostingTests
{
    private sealed class EmptyProvider : IObjectProvider
    {
        public ValueTask<IReadOnlyList<string>> GetObjectTypesAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<IReadOnlyList<string>> GetInstancesAsync(string objectType, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<IReadOnlyList<string>> GetAttributeNamesAsync(
            string objectSpecification,
            string objectType,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
            ObjectAttributeRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<ObjectAttributeValues>>([]);

        public ValueTask<IReadOnlyList<ObjectSetResult>> SetAttributesAsync(
            ObjectAttributeWrite write,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<ObjectSetResult>>([]);
    }

    private static ServiceProvider Build(bool withProvider)
    {
        var services = new ServiceCollection();
        var builder = services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Active;
            })
            .UseInMemoryTransport(isActive: true);

        if (withProvider)
        {
            builder.AddObjectProvider();
        }
        else
        {
            builder.AddObjectServices();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_graph_resolves_despite_the_cycle()
    {
        await using var provider = Build(withProvider: true);

        var services = provider.GetRequiredService<ObjectServices>();

        Assert.NotNull(services);
        Assert.Same(provider.GetRequiredService<ObjectServices>(), services);
        Assert.IsType<ObjectInMemoryProvider>(provider.GetRequiredService<IObjectProvider>());
    }

    [Fact]
    public async Task An_end_with_a_provider_declares_all_seven_primaries_and_can_answer()
    {
        await using var provider = Build(withProvider: true);

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(provider.GetRequiredService<ObjectServices>().CanAnswer);
        Assert.Equal(7, ObjectServices.HandledMessages.Count);

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task An_end_without_a_provider_declares_none_of_them()
    {
        await using var provider = Build(withProvider: false);

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // An end with no objects should answer S9F5 rather than answer every GetAttr with an
        // empty list, which is the whole difference between the two registrations.
        Assert.False(provider.GetRequiredService<ObjectServices>().CanAnswer);

        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            Assert.False(
                router.TryGetHandler(stream, function, out _),
                $"S{stream}F{function} should have no handler on an end with no objects");
        }
    }

    /// <summary>
    /// A provider already registered is composed with, not replaced.
    /// </summary>
    /// <remarks>
    /// This asserted the opposite until stream 14 had to answer for two packages at once. Keeping
    /// "the first one wins" would have meant a machine doing E87 and E90 answering for carriers
    /// and silently not for substrates, so the rule is now that every registered provider
    /// contributes - and an application that wants only its own simply does not call the other
    /// registration.
    /// </remarks>
    [Fact]
    public async Task A_provider_already_registered_is_composed_with_rather_than_replaced()
    {
        var services = new ServiceCollection();
        var mine = new EmptyProvider();

        services.AddSingleton<IObjectProvider>(mine);

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddObjectProvider();

        await using var provider = services.BuildServiceProvider();

        var objects = provider.GetRequiredService<ObjectServices>();

        Assert.True(objects.CanAnswer);

        // Through ObjectServices, because that is the path the library uses: resolving
        // IObjectProvider from a container gives whichever was registered last.
        var composite = Assert.IsType<CompositeObjectProvider>(objects.Provider);

        Assert.Equal(2, composite.Providers.Count);
        Assert.Contains(mine, composite.Providers);
        Assert.Contains(composite.Providers, candidate => candidate is ObjectInMemoryProvider);
    }

    [Fact]
    public async Task Registering_the_same_provider_twice_adds_it_once()
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddObjectProvider()
            .AddObjectProvider();

        await using var provider = services.BuildServiceProvider();

        // And the routing table survives it, which is the other half: the stream 14 primaries are
        // declared once however many times a provider is added.
        var router = provider.GetRequiredService<ISecsMessageRouter>();

        Assert.True(router.TryGetHandler(14, 1, out _));
        Assert.IsType<ObjectInMemoryProvider>(provider.GetRequiredService<ObjectServices>().Provider);
    }

    [Fact]
    public async Task Every_declared_primary_is_odd_because_a_secondary_is_not_a_primary()
    {
        await using var provider = Build(withProvider: true);

        Assert.All(
            ObjectServices.HandledMessages,
            message => Assert.True(message.Function % 2 == 1, $"S14F{message.Function} is not a primary"));

        Assert.Equal(ObjectServices.HandledMessages.Count, ObjectServices.HandledMessages.Distinct().Count());
    }
}
