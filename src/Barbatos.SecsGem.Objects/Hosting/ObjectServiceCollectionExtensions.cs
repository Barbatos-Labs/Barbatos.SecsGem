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
/// <see cref="ObjectServices"/> it can ask with; only the one with a provider declares the eleven
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
            Compose(provider.GetServices<IObjectProvider>()),
            provider.GetService<ILogger<ObjectServices>>()));

        return builder;
    }

    /// <summary>Puts every registered provider behind one, which is what stream 14 asks for.</summary>
    /// <param name="providers">Everything registered as an <see cref="IObjectProvider"/>.</param>
    /// <returns>The one to use, or <see langword="null"/> for an end that only asks.</returns>
    /// <remarks>
    /// <para>
    /// Composing rather than picking is the whole point. A machine that does E87 and E90 calls two
    /// registrations from two packages that know nothing about each other, and stream 14 has one
    /// provider per connection - so before this, the second registration quietly did nothing and
    /// the machine answered no substrate attributes at all with nothing anywhere saying so.
    /// </para>
    /// <para>
    /// One provider is used as it is: a composite of one would be a layer of indirection for
    /// nothing, and the common case is one.
    /// </para>
    /// </remarks>
    private static IObjectProvider? Compose(IEnumerable<IObjectProvider> providers)
    {
        IObjectProvider[] all = [.. providers];

        return all.Length switch
        {
            0 => null,
            1 => all[0],
            _ => new CompositeObjectProvider(all),
        };
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
    /// <strong>Providers compose; they do not replace each other.</strong> This registers
    /// <typeparamref name="TProvider"/> alongside whatever is already there, and
    /// <see cref="ObjectServices"/> gets all of them behind a
    /// <see cref="CompositeObjectProvider"/>. That is what lets a machine call two registrations
    /// from two packages - carrier management and substrate tracking, say - and have stream 14
    /// answer for both. Registering the same type twice adds it once.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddObjectProvider<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(
        this ISecsGemBuilder builder)
        where TProvider : class, IObjectProvider
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Enumerable rather than TryAdd: every provider registered contributes, and the factory
        // above puts them behind one composite. TryAddEnumerable keys on the implementation type,
        // so calling this twice for the same provider adds it once.
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IObjectProvider, TProvider>());
        builder.AddObjectServices();

        // Declared once however many providers there are - a machine calling this for carriers,
        // again for substrates and again for control jobs is three right registrations and one
        // stream 14. This package had its own marker type for that before the builder learned the
        // rule for everybody; see ISecsGemBuilder.DeclareOnce.
        //
        // Resolved inside the handler rather than inside the factory, the same cycle every other
        // registration here solves the same way.
        builder.DeclareOnce(ObjectServices.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<ObjectServices>().HandleAsync(transaction, token));

        return builder;
    }
}
