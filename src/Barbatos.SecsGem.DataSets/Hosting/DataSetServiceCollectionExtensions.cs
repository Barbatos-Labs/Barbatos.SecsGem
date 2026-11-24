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
    /// <remarks>
    /// Enough for a simulator or an equipment that passes data sets straight on. An equipment whose
    /// data sets must survive a restart calls <see cref="AddDataSetFileEquipment"/> instead, or
    /// brings a store of its own.
    /// </remarks>
    public static ISecsGemBuilder AddDataSetEquipment(this ISecsGemBuilder builder)
        => builder.AddDataSetEquipment<DataSetInMemoryStore>();

    /// <summary>Adds the equipment side, backed by a directory that outlives the process.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="directory">Where the records live. Created if it is not there.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <see cref="DataSetFileStore"/>, which adopts what is still in the directory - so a host does
    /// not have to send a data set again after a power cut, and an equipment that was asked for one
    /// still has it. The machine cannot regenerate a host's data set: <c>DSDATA</c> is defined
    /// nowhere, so what is stored is whatever the two ends agreed it is.
    /// </para>
    /// <para>
    /// The directory is a parameter and not a configuration option, because this package has no
    /// options type: <see cref="DataSetEquipment"/> takes none, stream 13 having no timer of its
    /// own. An application that keeps the path in configuration passes it from there.
    /// </para>
    /// <para>
    /// <see cref="DataSetFileStore.Capacity"/> keeps its default. An equipment that wants another,
    /// or that offers a data set of its own for a host to fetch, builds the store itself and calls
    /// <see cref="AddDataSetEquipment(ISecsGemBuilder, IDataSetStore)"/> - stream 13 has no message
    /// announcing a data set, so <see cref="DataSetFileStore.AddAsync"/> is how one gets there.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddDataSetFileEquipment(this ISecsGemBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(directory);

        builder.Services.TryAddSingleton<IDataSetStore>(provider => new DataSetFileStore(
            directory,
            provider.GetService<ILogger<DataSetFileStore>>()));

        return builder.DeclareDataSetEquipment();
    }

    /// <summary>Adds the equipment side, backed by a store you have already built.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="store">The store.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a store that needs more than a type and a container can give it - a
    /// <see cref="DataSetFileStore"/> with a capacity of its own, or one an application has already
    /// put a data set into for a host to find.
    /// </remarks>
    public static ISecsGemBuilder AddDataSetEquipment(this ISecsGemBuilder builder, IDataSetStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Services.TryAddSingleton(store);

        return builder.DeclareDataSetEquipment();
    }

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

        return builder.DeclareDataSetEquipment();
    }

    /// <summary>The half of the equipment registration that does not depend on which store it is.</summary>
    /// <remarks>
    /// Three ways in and one declaration, which is <c>DeclareSpooling</c>'s arrangement in the GEM
    /// package: the routes and the end are the same whichever store answered, and a second copy of
    /// <see cref="DataSetEquipment.HandledMessages"/> in a second overload is a list that can fall
    /// behind the first.
    /// </remarks>
    private static ISecsGemBuilder DeclareDataSetEquipment(this ISecsGemBuilder builder)
    {
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
