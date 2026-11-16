// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.MaterialFlow;
using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Substrates;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Joins SEMI E87 to SEMI E90 on a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method, and it declares no message of its own: the join is behaviour between two state
/// machines, not a transaction. Both standards must already be registered -
/// <c>AddCarrierEquipment</c> and <c>AddSubstrateTracking</c> - because a bridge with one end is
/// not one.
/// </remarks>
public static class MaterialFlowServiceCollectionExtensions
{
    /// <summary>Adds the carrier-to-substrate bridge, listening for verified slot maps.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">The convention: the naming, what counts as an occupied slot, what happens at either end.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="CarrierSubstrateOptions"/> and <see cref="CarrierSubstrateBridge"/>,
    /// and attaches the bridge to the carrier manager as soon as it is resolved - so a machine
    /// that reads a slot map and agrees it has its substrates without another line of code.
    /// </para>
    /// <para>
    /// It needs <see cref="CarrierManager"/> and <see cref="SubstrateTracker"/> in the container.
    /// Call it after the two registrations that put them there; a graph without them fails to
    /// resolve the bridge, which is the right moment to find out.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddCarrierSubstrateBridge(
        this ISecsGemBuilder builder,
        Action<CarrierSubstrateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new CarrierSubstrateOptions();

            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton(services =>
        {
            var bridge = new CarrierSubstrateBridge(
                services.GetRequiredService<CarrierManager>(),
                services.GetRequiredService<SubstrateTracker>(),
                services.GetRequiredService<CarrierSubstrateOptions>());

            // Attached here rather than in a hosted service, because there is nothing to start:
            // the bridge is two event subscriptions, and a machine whose first slot map arrives
            // before anything called StartAsync would otherwise miss it.
            bridge.Attach();

            return bridge;
        });

        return builder;
    }

    /// <summary>Adds the process job to substrate bridge, listening for jobs that end.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">The convention: what happens to work in progress and to wafers nobody got to.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ProcessJobSubstrateOptions"/> and
    /// <see cref="ProcessJobSubstrateBridge"/>, and attaches the bridge as soon as it is resolved -
    /// so a job that is stopped settles the lot it was running without another line of code.
    /// </para>
    /// <para>
    /// It needs <see cref="Barbatos.SecsGem.ProcessJobs.ProcessJobManager"/> and
    /// <see cref="SubstrateTracker"/> in the container, so call it after
    /// <c>AddProcessJobEquipment</c> and <c>AddSubstrateTracking</c>. The two bridges in this
    /// package are independent: a machine may have either, both or neither.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddProcessJobSubstrateBridge(
        this ISecsGemBuilder builder,
        Action<ProcessJobSubstrateOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new ProcessJobSubstrateOptions();

            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton(services =>
        {
            var bridge = new ProcessJobSubstrateBridge(
                services.GetRequiredService<ProcessJobManager>(),
                services.GetRequiredService<SubstrateTracker>(),
                services.GetRequiredService<ProcessJobSubstrateOptions>());

            bridge.Attach();

            return bridge;
        });

        return builder;
    }
}
