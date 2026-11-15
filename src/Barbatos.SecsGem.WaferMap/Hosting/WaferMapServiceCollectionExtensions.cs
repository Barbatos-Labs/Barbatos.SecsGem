// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.WaferMap;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds wafer mapping - SECS-II stream 12 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// Either end, never both on one builder: both declare S12F19, which the routing table refuses,
/// and one process answering as both ends of the same link is not a thing E5 describes. Two
/// connections in one process means two builders.
/// </remarks>
public static class WaferMapServiceCollectionExtensions
{
    /// <summary>Adds the equipment side: the end that sends maps and asks for them.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="WaferMapEquipment"/> and a handler for S12F19, the one message in
    /// stream 12 that arrives at this end. Everything else the equipment does, it initiates - so
    /// there is nothing more to declare and no loop to run.
    /// </para>
    /// <para>
    /// This sits happily beside <c>AddGemEquipment</c> on the same builder: stream 12 and the GEM
    /// message set share no stream and function pair, and a prober that does both is the normal
    /// case.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddWaferMapEquipment(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new WaferMapEquipment(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<WaferMapEquipment>>()));

        foreach (var (stream, function) in WaferMapEquipment.HandledMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<WaferMapEquipment>().HandleAsync(transaction, token));
        }

        return builder;
    }

    /// <summary>Adds the host side, backed by the in-memory store.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Enough for a simulator or a host that passes maps straight on. Anything that must survive a restart wants its own store.</remarks>
    public static ISecsGemBuilder AddWaferMapHost(this ISecsGemBuilder builder)
        => builder.AddWaferMapHost<WaferMapInMemoryStore>();

    /// <summary>Adds the host side, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="WaferMapHost"/>, the store as a singleton, and a handler for every
    /// pair in <see cref="WaferMapHost.HandledMessages"/> - all ten primaries of stream 12.
    /// </para>
    /// <para>
    /// The store is registered with <c>TryAdd</c>, so an <see cref="IWaferMapStore"/> already in
    /// the container wins. That is what lets an application register its own store with its own
    /// dependencies and still call this.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddWaferMapHost<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IWaferMapStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IWaferMapStore, TStore>();

        builder.Services.TryAddSingleton(provider => new WaferMapHost(
            provider.GetRequiredService<IWaferMapStore>(),
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<WaferMapHost>>()));

        // Resolved inside the handler rather than inside the factory, the same cycle the GEM
        // registration solves the same way: the host needs the connection, the connection needs
        // the router, and the router is what this is being registered into.
        foreach (var (stream, function) in WaferMapHost.HandledMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<WaferMapHost>().HandleAsync(transaction, token));
        }

        return builder;
    }
}
