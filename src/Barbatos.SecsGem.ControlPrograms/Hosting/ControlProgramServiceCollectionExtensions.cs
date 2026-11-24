// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.ControlPrograms;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds control program transfer - SECS-II stream 8 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// <para>
/// Either end, and as with stream 13 the two do not collide: the host declares no message at all,
/// so calling both on one builder would not be refused by the routing table. It is still not a
/// thing E5 describes, and two connections in one process means two builders.
/// </para>
/// <para>
/// Stream 8 shares no stream and function pair with any other package here. In particular it sits
/// happily beside <c>AddGemEquipment</c>, which answers stream 7 - the design stream 8 borrows -
/// and a machine that takes both recipes and firmware over one link is the ordinary case.
/// </para>
/// </remarks>
public static class ControlProgramServiceCollectionExtensions
{
    /// <summary>Adds the equipment side, backed by the in-memory store.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Enough for a simulator or a controller that passes firmware straight on. A machine whose
    /// firmware must survive a restart calls <see cref="AddControlProgramFileEquipment"/> instead,
    /// or brings a store of its own.
    /// </remarks>
    public static ISecsGemBuilder AddControlProgramEquipment(this ISecsGemBuilder builder)
        => builder.AddControlProgramEquipment<ControlProgramInMemoryStore>();

    /// <summary>Adds the equipment side, backed by a directory that outlives the process.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="directory">Where the records live. Created if it is not there.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <see cref="ControlProgramFileStore"/>, which adopts what is still in the directory - so a
    /// machine does not need a host to send its firmware again after a power cut.
    /// <c>AddGemFileSpooling</c> and <c>AddRecipeFileEquipment</c> are the same offer in the GEM and
    /// recipe packages.
    /// </para>
    /// <para>
    /// The directory is a parameter and not a configuration option, because this package has no
    /// options type: <see cref="ControlProgramEquipment"/> takes none, stream 8 having no timer of
    /// its own. An application that keeps the path in configuration passes it from there.
    /// </para>
    /// <para>
    /// <see cref="ControlProgramFileStore.Capacity"/> and
    /// <see cref="ControlProgramFileStore.MaxLength"/> keep their defaults. A machine that wants
    /// others, or that loads a named program at start-up, builds the store itself and calls
    /// <see cref="AddControlProgramEquipment(ISecsGemBuilder, IControlProgramStore)"/> - and it will
    /// have to, because <see cref="ControlProgramFileStore.AddAsync"/> is the only way a named
    /// program gets in: no row of stream 8 creates one.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddControlProgramFileEquipment(this ISecsGemBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(directory);

        builder.Services.TryAddSingleton<IControlProgramStore>(provider => new ControlProgramFileStore(
            directory,
            provider.GetService<ILogger<ControlProgramFileStore>>()));

        return builder.DeclareControlProgramEquipment();
    }

    /// <summary>Adds the equipment side, backed by a store you have already built.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="store">The store.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a store that needs more than a type and a container can give it - a
    /// <see cref="ControlProgramFileStore"/> with a limit of its own, or one an application has
    /// already loaded named programs into.
    /// </remarks>
    public static ISecsGemBuilder AddControlProgramEquipment(
        this ISecsGemBuilder builder,
        IControlProgramStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Services.TryAddSingleton(store);

        return builder.DeclareControlProgramEquipment();
    }

    /// <summary>Adds the equipment side, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ControlProgramEquipment"/>, the store as a singleton, and a handler for
    /// every pair in <see cref="ControlProgramEquipment.HandledMessages"/> - the six primaries this
    /// package implements.
    /// </para>
    /// <para>
    /// The store is registered with <c>TryAdd</c>, so an <see cref="IControlProgramStore"/> already
    /// in the container wins. That is what lets an application register its own store with its own
    /// dependencies and still call this.
    /// </para>
    /// <para>
    /// No deferred factory, because this end takes no <see cref="HsmsConnection"/>: stream 8 gives
    /// the equipment no primary to send, so there is no cycle between the end, the connection and
    /// the router to break. <c>Barbatos.SecsGem.DataSets</c> is the other registration here that
    /// can be written this way, and for the same reason.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddControlProgramEquipment<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IControlProgramStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IControlProgramStore, TStore>();

        return builder.DeclareControlProgramEquipment();
    }

    /// <summary>The half of the equipment registration that does not depend on which store it is.</summary>
    /// <remarks>
    /// Three ways in and one declaration, which is <c>DeclareSpooling</c>'s arrangement in the GEM
    /// package: the routes and the end are the same whichever store answered, and a second copy of
    /// <see cref="ControlProgramEquipment.HandledMessages"/> in a second overload is a list that can
    /// fall behind the first.
    /// </remarks>
    private static ISecsGemBuilder DeclareControlProgramEquipment(this ISecsGemBuilder builder)
    {
        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new ControlProgramEquipment(
            provider.GetRequiredService<IControlProgramStore>(),
            provider.GetService<ILogger<ControlProgramEquipment>>()));

        builder.DeclareOnce(ControlProgramEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<ControlProgramEquipment>().HandleAsync(transaction, token));

        return builder;
    }

    /// <summary>Adds the host side: the end that drives every transaction.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ControlProgramHost"/> and <strong>nothing else</strong>. Every message
    /// in stream 8 is a primary the host sends or a reply to one, so there is no route to declare -
    /// one of two such registrations in this repository, the other being
    /// <c>AddDataSetHost</c>. <see cref="ControlProgramHost.HandledMessages"/> is empty for the
    /// same reason.
    /// </para>
    /// <para>
    /// A host that wants to be told when a machine's firmware changes is wanting something stream 8
    /// does not have. <see cref="ControlProgramHost.ListAsync"/> and
    /// <see cref="ControlProgramHost.RevisionAsync"/> are how it finds out.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddControlProgramHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new ControlProgramHost(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<ControlProgramHost>>()));

        return builder;
    }
}
