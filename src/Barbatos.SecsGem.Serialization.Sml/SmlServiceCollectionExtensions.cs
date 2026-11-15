// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Serialization.Sml;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the SML spoke.
/// </summary>
public static class SmlServiceCollectionExtensions
{
    /// <summary>Adds the SML reader and writer, making <c>sml</c> available to the transcoder.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This is the whole of what a format spoke does to join in: register an
    /// <see cref="ISecsFormatReader"/> and an <see cref="ISecsFormatWriter"/> into the
    /// enumerable the registry is built from. Nothing in the core assembly changes, and
    /// conversion to and from every other registered format comes for free.
    /// </para>
    /// <para>
    /// It also registers the hub - <see cref="ISecsFormatRegistry"/>,
    /// <see cref="SecsTranscoder"/> and the native binary spoke - so this call on its own is
    /// enough to resolve a transcoder. The hub types live in <c>Barbatos.SecsGem.Core</c>, but
    /// the method that registers them cannot: that package has no dependency on a container, so
    /// that a file converter, an analyzer or a WebAssembly tool can use the item model without
    /// one.
    /// </para>
    /// <para>
    /// Everything goes in through <c>TryAdd</c>, so calling this twice is harmless, calling it
    /// beside <c>AddSecsJson()</c> builds one hub rather than two, and an application that wants
    /// its own SML dialect can register a different implementation type alongside - or replace
    /// this one outright.
    /// </para>
    /// <example>
    /// <code>
    /// services.AddSecsSml().AddSecsJson();
    ///
    /// var transcoder = provider.GetRequiredService&lt;SecsTranscoder&gt;();
    /// await transcoder.TranscodeFileAsync("recipe.sml", "recipe.json");
    /// </code>
    /// </example>
    /// </remarks>
    public static IServiceCollection AddSecsSml(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        SecsSerializationRegistration.AddHub(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, SmlFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, SmlFormatWriter>());
        return services;
    }
}
