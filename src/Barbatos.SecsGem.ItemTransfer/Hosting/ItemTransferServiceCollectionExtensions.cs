// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.ItemTransfer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds item transfer - SECS-II stream 21 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// <para>
/// <strong>One registration, because there is one kind of end.</strong> Every row of stream 21
/// travels either way, so both ends of a link call this and both declare the same six primaries -
/// which is how <c>AddMaterialHandoff</c> works for stream 4, and for the same reason. There is no
/// host half and no equipment half to get the wrong way round.
/// </para>
/// <para>
/// Stream 21 shares no stream and function pair with any other package here, so this sits happily
/// beside GEM and the rest.
/// </para>
/// </remarks>
public static class ItemTransferServiceCollectionExtensions
{
    /// <summary>Adds item transfer, backed by the in-memory store.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Enough for a simulator or a peer that passes items straight on. A peer whose items must
    /// survive a restart calls <see cref="AddItemFileTransfer"/> instead, or brings a store of its
    /// own.
    /// </remarks>
    public static ISecsGemBuilder AddItemTransfer(this ISecsGemBuilder builder)
        => builder.AddItemTransfer<ItemInMemoryStore>();

    /// <summary>Adds item transfer, backed by a directory that outlives the process.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="directory">Where the records live. Created if it is not there.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <see cref="ItemFileStore"/>, which adopts what is still in the directory - and this is the
    /// one stream here where <strong>either end</strong> may be the one that was switched off, since
    /// both ends call this registration. Two peers in one process need two directories.
    /// </para>
    /// <para>
    /// The directory is a parameter and not a configuration option, because this package has no
    /// options type: <see cref="ItemTransferPeer"/> takes none, stream 21 having no timer of its
    /// own. An application that keeps the path in configuration passes it from there.
    /// </para>
    /// <para>
    /// <see cref="ItemFileStore.Capacity"/> keeps its default. A peer that wants another, or that
    /// offers an item for the other end to fetch, builds the store itself and calls
    /// <see cref="AddItemTransfer(ISecsGemBuilder, IItemStore)"/> - and it will have to in order to
    /// give an item <em>attributes</em>, because no row of stream 21 sets one:
    /// <see cref="ItemFileStore.AddAsync"/> is the only way in.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddItemFileTransfer(this ISecsGemBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(directory);

        builder.Services.TryAddSingleton<IItemStore>(provider => new ItemFileStore(
            directory,
            provider.GetService<ILogger<ItemFileStore>>()));

        return builder.DeclareItemTransfer();
    }

    /// <summary>Adds item transfer, backed by a store you have already built.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="store">The store.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a store that needs more than a type and a container can give it - an
    /// <see cref="ItemFileStore"/> with a capacity of its own, or one an application has already
    /// put items and attributes into for the other end to find.
    /// </remarks>
    public static ISecsGemBuilder AddItemTransfer(this ISecsGemBuilder builder, IItemStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Services.TryAddSingleton(store);

        return builder.DeclareItemTransfer();
    }

    /// <summary>Adds item transfer, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ItemTransferPeer"/>, the store as a singleton, and a handler for every
    /// pair in <see cref="ItemTransferPeer.HandledMessages"/> - the six primaries this package
    /// implements, which this end both sends and answers.
    /// </para>
    /// <para>
    /// <strong>A process that only ever sends still needs the store</strong>, because the other end
    /// may ask it for a directory. That is what being a peer means here.
    /// </para>
    /// <para>
    /// The store is registered with <c>TryAdd</c>, so an <see cref="IItemStore"/> already in the
    /// container wins - which is what lets an application register its own with its own
    /// dependencies and still call this.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddItemTransfer<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IItemStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IItemStore, TStore>();

        return builder.DeclareItemTransfer();
    }

    /// <summary>The half of the registration that does not depend on which store it is.</summary>
    /// <remarks>
    /// Three ways in and one declaration, which is <c>DeclareSpooling</c>'s arrangement in the GEM
    /// package: the routes and the peer are the same whichever store answered, and a second copy of
    /// <see cref="ItemTransferPeer.HandledMessages"/> in a second overload is a list that can fall
    /// behind the first.
    /// </remarks>
    private static ISecsGemBuilder DeclareItemTransfer(this ISecsGemBuilder builder)
    {
        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new ItemTransferPeer(
            provider.GetRequiredService<IItemStore>(),
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<ItemTransferPeer>>()));

        // Resolved inside the handler rather than inside the factory, the same cycle every end that
        // sends a primary has: the end needs the connection, the connection needs the router, and
        // the router is what this is being registered into.
        builder.DeclareOnce(ItemTransferPeer.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<ItemTransferPeer>().HandleAsync(transaction, token));

        return builder;
    }
}
