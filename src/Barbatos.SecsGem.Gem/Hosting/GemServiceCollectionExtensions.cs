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
            provider.GetService<ILogger<GemEquipment>>()));

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
}

/// <summary>Runs the GEM state models for the lifetime of the host.</summary>
internal sealed class GemEquipmentHostedService(GemEquipment equipment) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => equipment.RunAsync(stoppingToken);
}
