// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Serialization.Xml;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the XML spoke.
/// </summary>
public static class XmlServiceCollectionExtensions
{
    /// <summary>Adds the XML reader and writer, making <c>xml</c> available to the transcoder.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// Also registers the hub - <see cref="ISecsFormatRegistry"/>,
    /// <see cref="SecsTranscoder"/> and the native binary spoke - through <c>TryAdd</c>, so this
    /// call on its own is enough to resolve a transcoder and calling it beside another
    /// <c>AddSecsXxx()</c> builds one hub rather than two. The hub types live in
    /// <c>Barbatos.SecsGem.Core</c>, but the method that registers them cannot: that package has
    /// no dependency on a container, so that a file converter, an analyzer or a WebAssembly tool
    /// can use the item model without one.
    /// </remarks>
    public static IServiceCollection AddSecsXml(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        SecsSerializationRegistration.AddHub(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, XmlFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, XmlFormatWriter>());
        return services;
    }
}
