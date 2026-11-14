// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem;
using Barbatos.SecsGem.Serialization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Hub-and-Spoke serialization services.
/// </summary>
public static class SecsSerializationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the format registry, the transcoder and the native binary spoke.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Separate from <c>AddSecsGem</c> on purpose: a tool that converts <c>.sml</c> to
    /// <c>.json</c> has no business pulling in a socket, a state machine and five protocol
    /// timers. This is the serialization half on its own.
    /// </para>
    /// <para>
    /// <c>AddSecsGem</c> calls this itself, so an application that wants both writes only
    /// <c>AddSecsGem</c>.
    /// </para>
    /// <example>
    /// <code>
    /// services.AddSecsSerialization()
    ///         .AddSecsSml().AddSecsJson().AddSecsXml().AddSecsIni();
    ///
    /// var transcoder = provider.GetRequiredService&lt;SecsTranscoder&gt;();
    /// await transcoder.TranscodeFileAsync("recipe.sml", "recipe.json");
    /// </code>
    /// </example>
    /// </remarks>
    public static IServiceCollection AddSecsSerialization(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(SecsCodecOptions.Default);

        // The native binary format is always present: it is the hub written down, which is what
        // lets capture.secs2 -> capture.sml work with no special case in the transcoder.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, Secs2BinaryFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, Secs2BinaryFormatWriter>());

        services.TryAddSingleton<ISecsFormatRegistry>(provider => new SecsFormatRegistry(
            provider.GetServices<ISecsFormatReader>(),
            provider.GetServices<ISecsFormatWriter>()));

        services.TryAddSingleton<SecsTranscoder>();

        return services;
    }
}
