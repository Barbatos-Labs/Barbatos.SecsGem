// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds Barbatos.SecsGem to a dependency-injection container.
/// </summary>
public static class SecsGemServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SECS/GEM stack and binds <see cref="SecsGemOptions"/> from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// The configuration to bind <see cref="SecsGemOptions.SectionName"/> (<c>Barbatos:SecsGem</c>)
    /// from. <see langword="null"/> binds nothing and leaves the defaults in place.
    /// </param>
    /// <param name="configure">Code-level changes, applied <em>after</em> the configuration file.</param>
    /// <returns>A builder for the rest of the stack.</returns>
    /// <remarks>
    /// <para>
    /// Order matters and is deliberate: the file is bound first and the delegate runs second, so
    /// code wins over configuration. That is the opposite of the usual advice, and it is right
    /// here - a timer a developer pinned in code is a decision, while a value in
    /// <c>appsettings.json</c> is often a leftover from another machine.
    /// </para>
    /// <para>
    /// Options are validated on start, so a T8 longer than T3 fails the host rather than the
    /// first stalled message. See <see cref="SecsGemOptionsValidator"/>.
    /// </para>
    /// <example>
    /// <code>
    /// services.AddSecsGem(builder.Configuration).UseTcp();
    /// services.AddSecsSml().AddSecsJson().AddSecsXml().AddSecsIni().AddSecsMapper();
    /// </code>
    /// </example>
    /// </remarks>
    public static ISecsGemBuilder AddSecsGem(
        this IServiceCollection services,
        IConfiguration? configuration = null,
        Action<SecsGemOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var optionsBuilder = services.AddOptions<SecsGemOptions>();

        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(SecsGemOptions.SectionName));
        }

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SecsGemOptions>, SecsGemOptionsValidator>());

        // Every timer in this stack is driven through TimeProvider, which is what lets the
        // conformance tests advance T3, T5, T6, T7 and T8 instantly instead of sleeping.
        services.TryAddSingleton(TimeProvider.System);

        // The codec options follow from the connection options, so an application never has to
        // keep the two in step by hand.
        services.TryAddSingleton(provider =>
            provider.GetRequiredService<IOptions<SecsGemOptions>>().Value.CreateCodecOptions());

        // The registry, the transcoder and the native binary spoke live in the core assembly,
        // so a tool that only converts files can have them without any of this one.
        services.AddSecsSerialization();

        return new SecsGemBuilder(services);
    }
}

/// <summary>
/// Configures the parts of the stack that have a choice to make.
/// </summary>
/// <remarks>
/// Exposes <see cref="Services"/> on purpose. The format spokes and the mapper live in their own
/// assemblies, which depend on the core and not on this one, so their registration methods
/// extend <see cref="IServiceCollection"/> rather than this builder. That is the dependency
/// direction that keeps a new format from ever needing a change here.
/// </remarks>
public interface ISecsGemBuilder
{
    /// <summary>Gets the underlying service collection.</summary>
    IServiceCollection Services { get; }

    /// <summary>Uses asynchronous TCP sockets bridged to <see cref="System.IO.Pipelines"/>.</summary>
    /// <returns>This builder, for chaining.</returns>
    ISecsGemBuilder UseTcp();

    /// <summary>Uses an in-process transport - two entities talking with no network.</summary>
    /// <param name="isActive">Which side this entity presents as.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>For tests, samples and demos. See <see cref="InMemorySecsTransport"/>.</remarks>
    ISecsGemBuilder UseInMemoryTransport(bool isActive = true);

    /// <summary>Replaces the transport with one of your own.</summary>
    /// <typeparam name="TTransport">The transport type. Needs a constructor the container can call.</typeparam>
    /// <returns>This builder, for chaining.</returns>
    ISecsGemBuilder UseTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>()
        where TTransport : class, ISecsTransport;
}

/// <summary>The default <see cref="ISecsGemBuilder"/>.</summary>
/// <param name="services">The service collection.</param>
internal sealed class SecsGemBuilder(IServiceCollection services) : ISecsGemBuilder
{
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public ISecsGemBuilder UseTcp() => UseTransport<TcpSecsTransport>();

    public ISecsGemBuilder UseInMemoryTransport(bool isActive = true)
    {
        Services.Replace(ServiceDescriptor.Singleton<ISecsTransport>(_ => new InMemorySecsTransport(isActive)));
        return this;
    }

    public ISecsGemBuilder UseTransport<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransport>()
        where TTransport : class, ISecsTransport
    {
        // Replace, not TryAdd: calling UseTcp() after UseInMemoryTransport() should mean what it
        // says rather than silently losing to whichever ran first.
        Services.Replace(ServiceDescriptor.Singleton<ISecsTransport, TTransport>());
        return this;
    }
}
