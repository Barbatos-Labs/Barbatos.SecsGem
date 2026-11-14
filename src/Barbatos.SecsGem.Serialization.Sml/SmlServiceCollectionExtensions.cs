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
    /// Registered through <c>TryAddEnumerable</c> keyed on the implementation type, so calling
    /// this twice is harmless - and an application that wants its own SML dialect can register
    /// a different implementation type alongside, or replace this one outright.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSecsSml(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatReader, SmlFormatReader>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsFormatWriter, SmlFormatWriter>());
        return services;
    }
}
