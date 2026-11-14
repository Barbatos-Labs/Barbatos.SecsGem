// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Mapping;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the object mapper.
/// </summary>
public static class MapperServiceCollectionExtensions
{
    /// <summary>Adds <see cref="SecsSerializerOptions"/> and the mapper services.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional changes to the default options.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers the options as a singleton so the type contracts it caches are shared: the
    /// first message of a given type pays for reflection, every later one does not. Resolving a
    /// new options object per message would throw that cache away each time.
    /// </para>
    /// <para>
    /// Every converter registered as <see cref="ISecsConverter"/> in the container is picked up
    /// and placed ahead of the built-in ones, so an application can override how a type maps
    /// without rebuilding the options.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddSecsMapper(
        this IServiceCollection services,
        Action<SecsSerializerOptionsBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions();

        if (configure is not null)
        {
            services.Configure<SecsSerializerOptionsBuilder>(configure);
        }

        services.TryAddSingleton(provider =>
        {
            var builder = provider.GetRequiredService<IOptions<SecsSerializerOptionsBuilder>>().Value;
            var registered = provider.GetServices<ISecsConverter>().ToArray();
            return builder.Build(registered);
        });

        return services;
    }

    /// <summary>Registers a converter, which takes precedence over the built-in ones.</summary>
    /// <typeparam name="TConverter">The converter type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddSecsConverter<TConverter>(this IServiceCollection services)
        where TConverter : class, ISecsConverter
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecsConverter, TConverter>());
        return services;
    }
}
