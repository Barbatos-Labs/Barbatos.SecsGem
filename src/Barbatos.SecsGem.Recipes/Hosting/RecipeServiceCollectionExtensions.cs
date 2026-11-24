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
    /// <remarks>
    /// Enough for a simulator or a controller that passes recipes straight on. A machine whose
    /// recipes must survive a restart calls <see cref="AddRecipeFileEquipment"/> instead, or brings
    /// a store of its own.
    /// </remarks>
    public static ISecsGemBuilder AddRecipeEquipment(this ISecsGemBuilder builder)
        => builder.AddRecipeEquipment<RecipeInMemoryStore>();

    /// <summary>Adds the equipment side, backed by a directory that outlives the process.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="directory">Where the records live. Created if it is not there.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <see cref="RecipeFileStore"/>, which adopts what is still in the directory - so a host does
    /// not have to send a recipe again after an upgrade or a power cut, and the namespaces, the
    /// locks and the selection are what they were. <c>AddGemFileSpooling</c> is the same offer for
    /// the GEM spool.
    /// </para>
    /// <para>
    /// The directory is a parameter and not a configuration option, because this package has no
    /// options type: <see cref="RecipeEquipment"/> takes none, stream 15 having no timer and no
    /// limit of its own. An application that keeps the path in configuration passes it from there.
    /// </para>
    /// <para>
    /// <see cref="RecipeFileStore.MaximumLength"/> keeps its default of one mebibyte. A machine
    /// that wants another builds the store itself and calls
    /// <see cref="AddRecipeEquipment(ISecsGemBuilder, IRecipeStore)"/>.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddRecipeFileEquipment(this ISecsGemBuilder builder, string directory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(directory);

        builder.Services.TryAddSingleton<IRecipeStore>(provider => new RecipeFileStore(
            directory,
            provider.GetService<ILogger<RecipeFileStore>>()));

        return builder.DeclareRecipeEquipment();
    }

    /// <summary>Adds the equipment side, backed by a store you have already built.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="store">The store.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="store"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// For a store that needs more than a type and a container can give it - a
    /// <see cref="RecipeFileStore"/> with a <see cref="RecipeFileStore.MaximumLength"/> of its own,
    /// or one an application has already loaded recipes into.
    /// </remarks>
    public static ISecsGemBuilder AddRecipeEquipment(this ISecsGemBuilder builder, IRecipeStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Services.TryAddSingleton(store);

        return builder.DeclareRecipeEquipment();
    }

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

        return builder.DeclareRecipeEquipment();
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

    /// <summary>The half of the equipment registration that does not depend on which store it is.</summary>
    /// <remarks>
    /// Three ways in and one declaration, which is <c>DeclareSpooling</c>'s arrangement in the GEM
    /// package: the routes and the end itself are the same whichever store answered, and a second
    /// copy of <see cref="RecipeEquipment.HandledMessages"/> in a second overload is a list that
    /// can fall behind the first.
    /// </remarks>
    private static ISecsGemBuilder DeclareRecipeEquipment(this ISecsGemBuilder builder)
    {
        // Built by hand for the same reason the GEM layer is: the logger is optional, and an
        // application that never called AddLogging should still get a working end.
        builder.Services.TryAddSingleton(provider => new RecipeEquipment(
            provider.GetRequiredService<IRecipeStore>(),
            provider.GetService<ILogger<RecipeEquipment>>()));

        builder.DeclareOnce(RecipeEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<RecipeEquipment>().HandleAsync(transaction, token));

        return builder;
    }
}
