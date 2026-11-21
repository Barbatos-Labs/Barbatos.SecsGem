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
    /// <remarks>Enough for a simulator or a controller that passes firmware straight on. Anything that must survive a restart wants its own store.</remarks>
    public static ISecsGemBuilder AddControlProgramEquipment(this ISecsGemBuilder builder)
        => builder.AddControlProgramEquipment<ControlProgramInMemoryStore>();

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
