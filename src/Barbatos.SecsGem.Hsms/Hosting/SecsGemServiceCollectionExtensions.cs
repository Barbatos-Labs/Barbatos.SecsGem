// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Linq;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
        // so a tool that only converts files can have them without any of this one. Registering
        // them is not that assembly's job, though - it has no dependency on a container - so
        // this is the same shared code every AddSecsXxx() runs. The codec options registered
        // just above survive it, because the hub adds its own default through TryAdd.
        SecsSerializationRegistration.AddHub(services);

        // The routing table is collected now and frozen the first time the router is resolved.
        // A second AddSecsGem call shares the first call's table rather than quietly building a
        // second one that nothing would ever read.
        var routerBuilder = services
            .FirstOrDefault(descriptor => descriptor.ServiceType == typeof(SecsMessageRouterBuilder))
            ?.ImplementationInstance as SecsMessageRouterBuilder;

        if (routerBuilder is null)
        {
            routerBuilder = new SecsMessageRouterBuilder();
            services.AddSingleton(routerBuilder);
        }

        // Build(provider), not Build(): a handler may be registered as a factory, and the whole
        // point of deferring it is that the provider exists by the time the router is resolved.
        services.TryAddSingleton(provider => provider.GetRequiredService<SecsMessageRouterBuilder>().Build(provider));

        // Built by hand rather than by the activator: HsmsConnection takes an optional logger, and
        // an application that never called AddLogging should still get a connection.
        services.TryAddSingleton(provider => new HsmsConnection(
            provider.GetRequiredService<IOptions<SecsGemOptions>>().Value,
            provider.GetRequiredService<ISecsTransport>(),
            provider.GetRequiredService<ISecsMessageRouter>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<HsmsConnection>>()));

        return new SecsGemBuilder(services, routerBuilder);
    }
}

/// <summary>
/// Runs an <see cref="HsmsConnection"/> for the lifetime of the host.
/// </summary>
/// <remarks>
/// Registered by <see cref="ISecsGemBuilder.RunAsHostedService"/>. An application that hosts the
/// link itself calls <see cref="HsmsConnection.RunAsync"/> instead; there is nothing else in here.
/// </remarks>
internal sealed class SecsGemHostedService(HsmsConnection connection) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => connection.RunAsync(stoppingToken);
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

    /// <summary>Registers the handler for one received primary message.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F. Must be odd - only a primary is received and answered.</param>
    /// <param name="handler">The handler.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The routing table is also the declaration of what this entity understands, which is what
    /// makes automatic S9F3 and S9F5 possible: a primary that reaches the end of the table is
    /// answered with S9F5 when its stream is registered and S9F3 when it is not.
    /// </para>
    /// <example>
    /// <code>
    /// services.AddSecsGem(builder.Configuration)
    ///     .UseTcp()
    ///     .OnPrimary(1, 1, (transaction, token) => transaction.ReplyAsync(onLineData, token))
    ///     .RunAsHostedService();
    /// </code>
    /// </example>
    /// </remarks>
    ISecsGemBuilder OnPrimary(byte stream, byte function, SecsPrimaryHandler handler);

    /// <summary>Registers a handler built from the container when the router is resolved.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F. Must be odd.</param>
    /// <param name="factory">Builds the handler from the service provider.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// The overload to reach for when a handler needs a service, since a closure written during
    /// configuration cannot have one. Resolve inside the returned handler rather than inside the
    /// factory for anything that depends on the connection - the factory runs while the router is
    /// still being built. See <see cref="SecsMessageRouterBuilder.OnPrimary(byte, byte, Func{IServiceProvider, SecsPrimaryHandler})"/>
    /// for why. This is how <c>AddGemEquipment</c> registers the whole GEM message set.
    /// </remarks>
    ISecsGemBuilder OnPrimary(byte stream, byte function, Func<IServiceProvider, SecsPrimaryHandler> factory);

    /// <summary>Registers the handler for every primary no other handler claims.</summary>
    /// <param name="handler">The handler.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// Turns off automatic S9F3 and S9F5: an entity that claims every message cannot also claim
    /// not to recognise one. For a proxy, a logger or a gateway.
    /// </remarks>
    ISecsGemBuilder OnUnhandledPrimary(SecsPrimaryHandler handler);

    /// <summary>Registers a catch-all built from the container when the router is resolved.</summary>
    /// <param name="factory">Builds the handler from the service provider.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>Registering one turns off automatic S9F3 and S9F5.</remarks>
    ISecsGemBuilder OnUnhandledPrimary(Func<IServiceProvider, SecsPrimaryHandler> factory);

    /// <summary>Runs the connection for the lifetime of the host.</summary>
    /// <returns>This builder, for chaining.</returns>
    /// <remarks>
    /// Connects, selects and reconnects on its own from host start to host stop. An application
    /// that wants to own the lifetime calls <see cref="HsmsConnection.RunAsync"/> itself and does
    /// not call this.
    /// </remarks>
    ISecsGemBuilder RunAsHostedService();
}

/// <summary>The default <see cref="ISecsGemBuilder"/>.</summary>
/// <param name="services">The service collection.</param>
/// <param name="router">The routing table being collected.</param>
internal sealed class SecsGemBuilder(IServiceCollection services, SecsMessageRouterBuilder router) : ISecsGemBuilder
{
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public ISecsGemBuilder UseTcp() => UseTransport<TcpSecsTransport>();

    public ISecsGemBuilder OnPrimary(byte stream, byte function, SecsPrimaryHandler handler)
    {
        router.OnPrimary(stream, function, handler);
        return this;
    }

    public ISecsGemBuilder OnPrimary(byte stream, byte function, Func<IServiceProvider, SecsPrimaryHandler> factory)
    {
        router.OnPrimary(stream, function, factory);
        return this;
    }

    public ISecsGemBuilder OnUnhandledPrimary(SecsPrimaryHandler handler)
    {
        router.OnUnhandledPrimary(handler);
        return this;
    }

    public ISecsGemBuilder OnUnhandledPrimary(Func<IServiceProvider, SecsPrimaryHandler> factory)
    {
        router.OnUnhandledPrimary(factory);
        return this;
    }

    public ISecsGemBuilder RunAsHostedService()
    {
        Services.AddHostedService<SecsGemHostedService>();
        return this;
    }

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
