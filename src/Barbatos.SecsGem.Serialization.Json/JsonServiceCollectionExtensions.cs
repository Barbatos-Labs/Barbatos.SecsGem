// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Serialization.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the JSON spoke.
/// </summary>
public static class JsonServiceCollectionExtensions
{
    /// <summary>Adds the JSON reader and writer, making <c>json</c> available to the transcoder.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddSecsJson(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, JsonFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, JsonFormatWriter>());
        return services;
    }
}
