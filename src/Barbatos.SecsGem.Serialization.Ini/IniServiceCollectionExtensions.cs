// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Serialization;
using Barbatos.SecsGem.Serialization.Ini;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the INI spoke.
/// </summary>
public static class IniServiceCollectionExtensions
{
    /// <summary>Adds the INI reader and writer, making <c>ini</c> available to the transcoder.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddSecsIni(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, IniFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, IniFormatWriter>());
        return services;
    }
}
