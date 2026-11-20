// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.DataSets;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds data set transfer - SECS-II stream 13 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// <para>
/// Either end, and unusually for this repository the two do not collide: the host declares no
/// message at all, so calling both on one builder would not be refused by the routing table. It is
/// still not a thing E5 describes - one process answering as both ends of the same link - and two
/// connections in one process means two builders.
/// </para>
/// <para>
/// Stream 13 shares no stream and function pair with any other package here, so either
/// registration sits happily beside GEM, stream 12, stream 3 and the rest.
/// </para>
/// </remarks>
public static class DataSetServiceCollectionExtensions
{
    /// <summary>Adds the equipment side, backed by the in-memory store.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Enough for a simulator or an equipment that passes data sets straight on. Anything that must survive a restart wants its own store.</remarks>
    public static ISecsGemBuilder AddDataSetEquipment(this ISecsGemBuilder builder)
        => builder.AddDataSetEquipment<DataSetInMemoryStore>();

    /// <summary>Adds the equipment side, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="DataSetEquipment"/>, the store as a singleton, and a handler for every
    /// pair in <see cref="DataSetEquipment.HandledMessages"/> - all seven primaries this package
    /// implements.
    /// </para>
    /// <para>
    /// The store is registered with <c>TryAdd</c>, so an <see cref="IDataSetStore"/> already in the
    /// container wins. That is what lets an application register its own store with its own
    /// dependencies and still call this.
    /// </para>
    /// <para>
    /// No deferred factory here, unlike every other end in this repository: this one takes no
    /// <see cref="HsmsConnection"/>, because stream 13 gives the equipment no primary to send - so
    /// there is no cycle between the end, the connection and the router to break.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddDataSetEquipment<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IDataSetStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IDataSetStore, TStore>();

        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new DataSetEquipment(
            provider.GetRequiredService<IDataSetStore>(),
            provider.GetService<ILogger<DataSetEquipment>>()));

        builder.DeclareOnce(DataSetEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<DataSetEquipment>().HandleAsync(transaction, token));

        return builder;
    }

    /// <summary>Adds the host side: the end that drives every transaction.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="DataSetHost"/> and <strong>nothing else</strong>. Every message in
    /// stream 13 is a primary the host sends or a reply to one, so there is no route to declare and
    /// no loop to run - the only registration in this repository that adds no handler to the
    /// message table. <see cref="DataSetHost.HandledMessages"/> is empty for the same reason.
    /// </para>
    /// <para>
    /// A host that wants to be told about data sets it did not ask for is wanting something stream
    /// 13 does not have. There is no "a new data set exists" primary, which is what
    /// <see cref="DataSetHost.ListAsync"/> and <see cref="DataSetHost.StatusAsync"/> are for.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddDataSetHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new DataSetHost(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<DataSetHost>>()));

        return builder;
    }
}
