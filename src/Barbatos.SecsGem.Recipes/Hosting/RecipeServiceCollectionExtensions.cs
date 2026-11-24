// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Recipes;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds recipe management - SECS-II stream 15 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// <para>
/// Either end, and as with streams 8 and 13 the two do not collide: the host declares no message
/// at all, so calling both on one builder would not be refused by the routing table. It is still
/// not a thing E5 describes, and two connections in one process means two builders.
/// </para>
/// <para>
/// Stream 15 shares no stream and function pair with any other package here. It sits beside
/// <c>AddGemEquipment</c>, which answers stream 7 - and that is worth a word, because stream 7 is
/// where this stream's recipe body comes from. A machine can serve both: stream 7's process
/// programs and stream 15's namespaced recipes are the same <c>PPBODY</c> addressed two ways, the
/// way E39's <c>OBJSPEC</c> and stream 18's <c>SSID</c> address GEM's variables two ways.
/// </para>
/// </remarks>
public static class RecipeServiceCollectionExtensions
{
    /// <summary>Adds the equipment side, backed by the in-memory store.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Enough for a simulator or a controller that passes recipes straight on. Anything that must survive a restart wants its own store.</remarks>
    public static ISecsGemBuilder AddRecipeEquipment(this ISecsGemBuilder builder)
        => builder.AddRecipeEquipment<RecipeInMemoryStore>();

    /// <summary>Adds the equipment side, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="RecipeEquipment"/>, the store as a singleton, and a handler for every
    /// pair in <see cref="RecipeEquipment.HandledMessages"/> - the thirteen primaries this package
    /// implements.
    /// </para>
    /// <para>
    /// The store is registered with <c>TryAdd</c>, so an <see cref="IRecipeStore"/> already in the
    /// container wins. That is what lets an application register its own store with its own
    /// dependencies and still call this.
    /// </para>
    /// <para>
    /// No deferred factory, because this end takes no <see cref="HsmsConnection"/>: stream 15 gives
    /// the equipment no primary to send, so there is no cycle between the end, the connection and
    /// the router to break. <c>Barbatos.SecsGem.ControlPrograms</c> and
    /// <c>Barbatos.SecsGem.DataSets</c> are the other two registrations here that can be written
    /// this way, and for the same reason - the three one-way streams.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddRecipeEquipment<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IRecipeStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IRecipeStore, TStore>();

        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new RecipeEquipment(
            provider.GetRequiredService<IRecipeStore>(),
            provider.GetService<ILogger<RecipeEquipment>>()));

        builder.DeclareOnce(RecipeEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<RecipeEquipment>().HandleAsync(transaction, token));

        return builder;
    }

    /// <summary>Adds the host side: the end that drives every transaction.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Registers <see cref="RecipeHost"/> and <strong>nothing else</strong>. Every message in
    /// stream 15 is a primary the host sends or a reply to one, so there is no route to declare -
    /// the third such registration in this repository, beside <c>AddControlProgramHost</c> and
    /// <c>AddDataSetHost</c>. <see cref="RecipeHost.HandledMessages"/> is empty for the same
    /// reason.
    /// </para>
    /// <para>
    /// A host that wants to be told when a machine's recipes change is wanting something stream 15
    /// does not have. <see cref="RecipeHost.ListNamespacesAsync"/> is how it finds out what is
    /// there; the directory of one namespace - S15F3/F4 - is among the pairs this package does not
    /// implement, because its reply needs three names the dictionary does not define and two of
    /// the three have a twin in it whose format disagrees.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddRecipeHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new RecipeHost(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<RecipeHost>>()));

        return builder;
    }
}
