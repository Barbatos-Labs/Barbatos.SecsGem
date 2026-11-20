// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Material;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds material status - SECS-II stream 3 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// Either end, never both on one builder. Unlike most of this library the two ends here declare
/// <em>different</em> messages - the equipment answers five, the host answers four - so the
/// routing table would not catch the mistake, and a process answering as both ends of one link is
/// not a thing E5 describes. Two connections in one process means two builders.
/// </remarks>
public static class MaterialServiceCollectionExtensions
{
    /// <summary>Adds the equipment side, backed by the in-memory inventory.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Enough for a simulator. A machine whose inventory is real wants its own source.</remarks>
    public static ISecsGemBuilder AddMaterialEquipment(this ISecsGemBuilder builder)
        => builder.AddMaterialEquipment<MaterialInMemorySource>();

    /// <summary>Adds the equipment side, backed by an inventory of your own.</summary>
    /// <typeparam name="TSource">Where the machine keeps what it knows about its material.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="MaterialEquipment"/>, the source as a singleton, and a handler for
    /// every pair in <see cref="MaterialEquipment.HandledMessages"/> - S3F1, S3F3, S3F9, S3F11 and
    /// S3F15.
    /// </para>
    /// <para>
    /// The source is registered with <c>TryAdd</c>, so an <see cref="IMaterialSource"/> already in
    /// the container wins. That is what lets an application register its own with its own
    /// dependencies and still call this.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddMaterialEquipment<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSource>(
        this ISecsGemBuilder builder)
        where TSource : class, IMaterialSource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IMaterialSource, TSource>();

        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new MaterialEquipment(
            provider.GetRequiredService<IMaterialSource>(),
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<MaterialEquipment>>()));

        // Resolved inside the handler rather than inside the factory, the same cycle every other
        // registration here solves the same way: the end needs the connection, the connection
        // needs the router, and the router is what this is being registered into.
        builder.DeclareOnce(MaterialEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<MaterialEquipment>().HandleAsync(transaction, token));

        return builder;
    }

    /// <summary>Adds the host side: the five questions out, and the four reports in.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Registers <see cref="MaterialHost"/> and a handler for every pair in
    /// <see cref="MaterialHost.HandledMessages"/> - S3F5, S3F7, S3F13 and S3F35. Nothing else to
    /// declare: the other five messages are ones this end sends.
    /// </remarks>
    public static ISecsGemBuilder AddMaterialHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new MaterialHost(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<MaterialHost>>()));

        builder.DeclareOnce(MaterialHost.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<MaterialHost>().HandleAsync(transaction, token));

        return builder;
    }

    /// <summary>Adds the interlocked material handoff - stream 4's S4F1 to S4F18, for either end.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="MaterialHandoff"/> and a handler for every pair in
    /// <see cref="MaterialHandoff.HandledMessages"/> - all nine primaries, which is also every
    /// primary this end sends. <strong>Both ends of a link call this same method</strong>: the
    /// catalogue marks all eleven rows as travelling either way, because a handoff is between a
    /// sender and a receiver of material rather than between a host and a machine.
    /// </para>
    /// <para>
    /// Composes with <see cref="AddMaterialEquipment(ISecsGemBuilder)"/> or
    /// <see cref="AddMaterialHost"/> on the same connection - stream 3 and stream 4 declare
    /// nothing in common - and works on its own, for a machine that hands material over and
    /// answers no status questions about it.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddMaterialHandoff(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new MaterialHandoff(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<MaterialHandoff>>()));

        builder.DeclareOnce(MaterialHandoff.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<MaterialHandoff>().HandleAsync(transaction, token));

        return builder;
    }
}
