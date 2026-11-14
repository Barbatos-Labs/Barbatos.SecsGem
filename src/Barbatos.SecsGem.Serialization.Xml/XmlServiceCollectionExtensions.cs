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
    public static IServiceCollection AddSecsXml(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, XmlFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, XmlFormatWriter>());
        return services;
    }
}
