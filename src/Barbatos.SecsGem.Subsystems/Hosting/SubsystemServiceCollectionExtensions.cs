// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Subsystems;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds subsystem control and data - SECS-II stream 18 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// <para>
/// Either end, never both on one builder - and the reason is worth knowing, because it is not the
/// one the routing table enforces. The two registrations declare <strong>disjoint</strong> sets:
/// six primaries at the equipment and two at the host. So two calls would not collide and nothing
/// would complain; what would be wrong is the machine answering questions it is the one that asks.
/// Two connections in one process means two builders.
/// </para>
/// <para>
/// Stream 18 shares no stream and function pair with any other package here, so either
/// registration sits happily beside GEM, stream 14's object services and the rest.
/// </para>
/// </remarks>
public static class SubsystemServiceCollectionExtensions
{
    /// <summary>Adds the equipment side, backed by the in-memory store.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Enough for a simulator or a controller that passes requests straight on. A machine whose subsystems are real hardware writes its own store.</remarks>
    public static ISecsGemBuilder AddSubsystemEquipment(this ISecsGemBuilder builder)
        => builder.AddSubsystemEquipment<SubsystemInMemoryStore>();

    /// <summary>Adds the equipment side, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="SubsystemEquipment"/>, the store as a singleton, and a handler for
    /// every pair in <see cref="SubsystemEquipment.HandledMessages"/> - the six primaries a host
    /// sends.
    /// </para>
    /// <para>
    /// The store is registered with <c>TryAdd</c>, so an <see cref="ISubsystemStore"/> already in
    /// the container wins. That is what lets an application register its own store with its own
    /// dependencies and still call this.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddSubsystemEquipment<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, ISubsystemStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<ISubsystemStore, TStore>();

        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new SubsystemEquipment(
            provider.GetRequiredService<ISubsystemStore>(),
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<SubsystemEquipment>>()));

        // Resolved inside the handler rather than inside the factory, the same cycle every other
        // end in this repository solves the same way: the end needs the connection, the connection
        // needs the router, and the router is what this is being registered into.
        builder.DeclareOnce(SubsystemEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<SubsystemEquipment>().HandleAsync(transaction, token));

        return builder;
    }

    /// <summary>Adds the host side: the end that asks, and that is told.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Registers <see cref="SubsystemHost"/> and a handler for the two primaries that arrive at a
    /// host - the event report and the alarm report. Everything else this end does, it initiates.
    /// </remarks>
    public static ISecsGemBuilder AddSubsystemHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new SubsystemHost(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<SubsystemHost>>()));

        builder.DeclareOnce(SubsystemHost.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<SubsystemHost>().HandleAsync(transaction, token));

        return builder;
    }
}
