// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Diagnostics.CodeAnalysis;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds object services - SECS-II stream 14 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// Two registrations, and the difference is whether this end answers. Both give the application an
/// <see cref="ObjectServices"/> it can ask with; only the one with a provider declares the seven
/// primaries. That is the right default: an entity with no objects should answer S9F5 rather than
/// answer every <c>GetAttr</c> with an empty list.
/// </remarks>
public static class ObjectServiceCollectionExtensions
{
    /// <summary>Adds object services for an end that only asks.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Declares nothing in the routing table, because an end with no objects has nothing to answer with.</remarks>
    public static ISecsGemBuilder AddObjectServices(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new ObjectServices(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<IObjectProvider>(),
            provider.GetService<ILogger<ObjectServices>>()));

        return builder;
    }

    /// <summary>Adds object services backed by the in-memory provider, for an end that also answers.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>Enough for a simulator. An application whose objects are real brings its own provider.</remarks>
    public static ISecsGemBuilder AddObjectProvider(this ISecsGemBuilder builder)
        => builder.AddObjectProvider<ObjectInMemoryProvider>();

    /// <summary>Adds object services backed by a provider of your own.</summary>
    /// <typeparam name="TProvider">Where the objects and their attributes live.</typeparam>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers the provider, <see cref="ObjectServices"/>, and a handler for every pair in
    /// <see cref="ObjectServices.HandledMessages"/> - S14F1, F3, F13, F15, F17, F19 and F21.
    /// </para>
    /// <para>
    /// The provider is registered with <c>TryAdd</c>, so an <see cref="IObjectProvider"/> already
    /// in the container wins - which is how an application registers one with its own
    /// dependencies and still calls this for the routing.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddObjectProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(
        this ISecsGemBuilder builder)
        where TProvider : class, IObjectProvider
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IObjectProvider, TProvider>();
        builder.AddObjectServices();

        // Resolved inside the handler rather than inside the factory, the same cycle every other
        // registration here solves the same way.
        foreach (var (stream, function) in ObjectServices.HandledMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<ObjectServices>().HandleAsync(transaction, token));
        }

        return builder;
    }
}
