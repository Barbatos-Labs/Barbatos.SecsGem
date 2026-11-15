// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Gem;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the GEM equipment layer to a connection built by <c>AddSecsGem</c>.</summary>
public static class GemServiceCollectionExtensions
{
    /// <summary>Adds the GEM equipment layer, with a model that needs nothing from the container.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configureModel">Declares what this equipment publishes.</param>
    /// <param name="configuration">Binds <c>Barbatos:Gem</c> when given.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <param name="runAsHostedService">Whether to run the GEM loop for the lifetime of the host.</param>
    /// <returns>The builder, for chaining.</returns>
    public static ISecsGemBuilder AddGemEquipment(
        this ISecsGemBuilder builder,
        Action<GemEquipmentModelBuilder> configureModel,
        IConfiguration? configuration = null,
        Action<GemOptions>? configure = null,
        bool runAsHostedService = true)
    {
        ArgumentNullException.ThrowIfNull(configureModel);

        return builder.AddGemEquipment(
            (model, _) => configureModel(model),
            configuration,
            configure,
            runAsHostedService);
    }

    /// <summary>Adds the GEM equipment layer, with a model built from the container.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configureModel">Declares what this equipment publishes, with services to hand.</param>
    /// <param name="configuration">Binds <c>Barbatos:Gem</c> when given.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <param name="runAsHostedService">Whether to run the GEM loop for the lifetime of the host.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This is the overload real equipment wants. A status variable reads the machine, and the
    /// machine is a service - so the model cannot be built until the container is, and a
    /// <c>Read</c> delegate written during configuration would have nothing to read from.
    /// </para>
    /// <para>
    /// The GEM loop is registered as a hosted service by default. It is what establishes
    /// communications, so an equipment object whose loop never runs would sit in
    /// <see cref="GemCommunicationState.WaitingToRetry"/> for ever while looking perfectly
    /// configured. Pass <paramref name="runAsHostedService"/> as <see langword="false"/> only when
    /// something else drives <see cref="GemEquipment.RunAsync"/> - a test, usually.
    /// </para>
    /// <para>
    /// The connection still needs its own <c>RunAsHostedService()</c>. Two loops, two services,
    /// one job each.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddGemEquipment(
        this ISecsGemBuilder builder,
        Action<GemEquipmentModelBuilder, IServiceProvider> configureModel,
        IConfiguration? configuration = null,
        Action<GemOptions>? configure = null,
        bool runAsHostedService = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configureModel);

        var optionsBuilder = builder.Services.AddOptions<GemOptions>();

        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(GemOptions.SectionName));
        }

        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        optionsBuilder.ValidateOnStart();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<GemOptions>, GemOptionsValidator>());

        builder.Services.TryAddSingleton(provider =>
        {
            var model = new GemEquipmentModelBuilder();
            configureModel(model, provider);
            return model.Build();
        });

        // Built by hand rather than by the activator, for the same reason the connection is: the
        // logger is optional, and an application that never called AddLogging should still get a
        // working piece of equipment.
        builder.Services.TryAddSingleton(provider => new GemEquipment(
            provider.GetRequiredService<IOptions<GemOptions>>().Value,
            provider.GetRequiredService<GemEquipmentModel>(),
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ILogger<GemEquipment>>(),

            // GetService, not GetRequiredService: process programs are opt-in, and equipment that
            // does not manage them resolves null here and never declares stream 7. Order does not
            // matter - AddGemProcessPrograms may come before or after this call.
            provider.GetService<IGemProcessProgramStore>(),

            // Also GetService: spooling is opt-in the same way, and null here is what makes
            // IsSpooling false and leaves S6F23 undeclared.
            provider.GetService<IGemSpool>(),

            // The other half of stream 7, opt-in independently of the first: a formatted process
            // program is structure E5 defines where an unformatted one is a block of bytes, so
            // equipment may offer either, both, or neither.
            provider.GetService<IGemFormattedProcessProgramStore>()));

        // Every GEM message, registered as a factory - and the equipment is resolved inside the
        // handler rather than inside the factory. That distinction is the whole game.
        //
        // The cycle is real: the equipment needs the connection, the connection needs the router,
        // and the router is what these handlers are being registered into. The factory runs while
        // the router is being built, so resolving the equipment there would re-enter the
        // half-built router and recurse until the stack ran out. Resolving when a message arrives
        // costs one cached singleton lookup and happens long after the graph is complete.
        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<GemEquipment>().HandleAsync(transaction, token));
        }

        if (runAsHostedService)
        {
            builder.Services.AddHostedService<GemEquipmentHostedService>();
        }

        return builder;
    }

    /// <summary>Adds process program management, backed by the in-memory store.</summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Enough for a simulator or equipment whose recipes are small and regenerated at start-up.
    /// Anything that must survive a restart wants its own store.
    /// </remarks>
    public static ISecsGemBuilder AddGemProcessPrograms(this ISecsGemBuilder builder)
        => builder.AddGemProcessPrograms<GemInMemoryProcessProgramStore>();

    /// <summary>Adds process program management, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This is what declares the unformatted half of stream 7 - S7F1, S7F3 and S7F5 - along with
    /// the delete and directory messages, S7F17 and S7F19, which both halves share. Equipment that
    /// does not call it answers none of those, and the connection reports S9F3 - which is the
    /// honest answer rather than an acknowledgement code meaning "no". For the structured half, see
    /// <see cref="AddGemFormattedProcessPrograms(ISecsGemBuilder)"/>.
    /// </para>
    /// <para>
    /// Call it once. A second call registers the same stream and function twice, which the routing
    /// table refuses.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddGemProcessPrograms<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IGemProcessProgramStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IGemProcessProgramStore, TStore>();
        return DeclareProcessPrograms(builder);
    }

    /// <summary>Adds process program management, backed by a store you have already built.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="store">The store.</param>
    /// <returns>The builder, for chaining.</returns>
    public static ISecsGemBuilder AddGemProcessPrograms(this ISecsGemBuilder builder, IGemProcessProgramStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Services.TryAddSingleton(store);
        return DeclareProcessPrograms(builder);
    }

    /// <summary>Adds formatted process program management, backed by the in-memory store.</summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The structured half of stream 7: S7F23 and S7F25, where a process program is a PPID, the
    /// MDLN and SOFTREV it was written for, and a list of CCODE commands rather than a block of
    /// bytes. Independent of <see cref="AddGemProcessPrograms(ISecsGemBuilder)"/> - equipment may
    /// offer either half, both, or neither.
    /// </para>
    /// <para>
    /// S7F17 and S7F19 come with the unformatted half, because a pair may be declared only once and
    /// deleting or listing a program is not per-format. Call both methods to answer them, and the
    /// delete and directory handlers then cover both stores; a formatted program appears in S7F20
    /// beside the unformatted ones.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddGemFormattedProcessPrograms(this ISecsGemBuilder builder)
        => builder.AddGemFormattedProcessPrograms<GemInMemoryFormattedProcessProgramStore>();

    /// <summary>Adds formatted process program management, backed by a store of your own.</summary>
    /// <typeparam name="TStore">The store.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Call it once. A second call registers the same stream and function twice, which the routing
    /// table refuses.
    /// </remarks>
    public static ISecsGemBuilder AddGemFormattedProcessPrograms<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TStore>(
        this ISecsGemBuilder builder)
        where TStore : class, IGemFormattedProcessProgramStore
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IGemFormattedProcessProgramStore, TStore>();
        return DeclareFormattedProcessPrograms(builder);
    }

    /// <summary>Adds formatted process program management, backed by a store you have already built.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="store">The store.</param>
    /// <returns>The builder, for chaining.</returns>
    public static ISecsGemBuilder AddGemFormattedProcessPrograms(
        this ISecsGemBuilder builder,
        IGemFormattedProcessProgramStore store)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(store);

        builder.Services.TryAddSingleton(store);
        return DeclareFormattedProcessPrograms(builder);
    }

    /// <summary>Adds spooling, backed by a bounded in-memory spool sized from the options.</summary>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Uses <see cref="GemOptions.SpoolCapacity"/> and <see cref="GemOptions.SpoolOverflow"/>.
    /// Nothing here is durable: equipment that must hold reports across its own restart wants an
    /// <see cref="IGemSpool"/> over a file or a database.
    /// </remarks>
    public static ISecsGemBuilder AddGemSpooling(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IGemSpool>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<GemOptions>>().Value;
            return new GemInMemorySpool(options.SpoolCapacity, options.SpoolOverflow);
        });

        return DeclareSpooling(builder);
    }

    /// <summary>Adds spooling, backed by a spool of your own.</summary>
    /// <typeparam name="TSpool">The spool.</typeparam>
    /// <param name="builder">The builder.</param>
    /// <returns>The builder, for chaining.</returns>
    public static ISecsGemBuilder AddGemSpooling<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TSpool>(
        this ISecsGemBuilder builder)
        where TSpool : class, IGemSpool
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton<IGemSpool, TSpool>();
        return DeclareSpooling(builder);
    }

    /// <summary>Adds spooling, backed by a spool you have already built.</summary>
    /// <param name="builder">The builder.</param>
    /// <param name="spool">The spool.</param>
    /// <returns>The builder, for chaining.</returns>
    public static ISecsGemBuilder AddGemSpooling(this ISecsGemBuilder builder, IGemSpool spool)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(spool);

        builder.Services.TryAddSingleton(spool);
        return DeclareSpooling(builder);
    }

    private static ISecsGemBuilder DeclareSpooling(ISecsGemBuilder builder)
    {
        foreach (var (stream, function) in GemEquipment.SpoolMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<GemEquipment>().HandleAsync(transaction, token));
        }

        return builder;
    }

    private static ISecsGemBuilder DeclareProcessPrograms(ISecsGemBuilder builder)
    {
        foreach (var (stream, function) in GemEquipment.ProcessProgramMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<GemEquipment>().HandleAsync(transaction, token));
        }

        return builder;
    }

    private static ISecsGemBuilder DeclareFormattedProcessPrograms(ISecsGemBuilder builder)
    {
        foreach (var (stream, function) in GemEquipment.FormattedProcessProgramMessages)
        {
            builder.OnPrimary(stream, function, static provider =>
                (transaction, token) => provider.GetRequiredService<GemEquipment>().HandleAsync(transaction, token));
        }

        return builder;
    }
}

/// <summary>Runs the GEM state models for the lifetime of the host.</summary>
internal sealed class GemEquipmentHostedService(GemEquipment equipment) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => equipment.RunAsync(stoppingToken);
}
