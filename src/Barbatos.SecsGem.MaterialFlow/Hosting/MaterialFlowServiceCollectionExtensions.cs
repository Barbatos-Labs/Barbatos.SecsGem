// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.ControlJobs;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.MaterialFlow;
using Barbatos.SecsGem.ProcessJobs;
using Barbatos.SecsGem.Substrates;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Joins the material-flow standards to each other on a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method per join, and none of them declares a message of its own: a join is behaviour
/// between state machines, not a transaction. Both standards of a join must already be registered
/// - <c>AddCarrierEquipment</c>, <c>AddSubstrateTracking</c>, <c>AddProcessJobEquipment</c>,
/// <c>AddControlJobs</c> - because a bridge with one end is not one.
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

    /// <summary>Adds the control-job to carrier bridge, listening for the material a job named.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">The conventions: what counts as ready, and what an ending does to the carriers.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ControlJobCarrierOptions"/> and
    /// <see cref="ControlJobCarrierBridge"/>, and attaches the bridge to the carrier manager as
    /// soon as it is resolved - so a selected job whose carriers arrive and verify starts without
    /// another line of code.
    /// </para>
    /// <para>
    /// It needs <see cref="CarrierManager"/> and <see cref="ControlJobManager"/> in the container,
    /// so call it after <c>AddCarrierEquipment</c> and <c>AddControlJobs</c>. The three bridges in
    /// this package are independent: a machine may have any of them, all of them or none.
    /// </para>
    /// <para>
    /// <strong>It attaches to the carrier manager only</strong>, which is not an omission - see
    /// <see cref="ControlJobCarrierBridge"/> for why a bridge that listened to both managers would
    /// take their two locks in two orders. The other direction is
    /// <see cref="ControlJobCarrierBridge.ClaimCarriers"/> and
    /// <see cref="ControlJobCarrierBridge.ReleaseCarriers"/>, which the application calls.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddControlJobCarrierBridge(
        this ISecsGemBuilder builder,
        Action<ControlJobCarrierOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var options = new ControlJobCarrierOptions();

            configure?.Invoke(options);

            return options;
        });

        builder.Services.TryAddSingleton(services =>
        {
            var bridge = new ControlJobCarrierBridge(
                services.GetRequiredService<CarrierManager>(),
                services.GetRequiredService<ControlJobManager>(),
                services.GetRequiredService<ControlJobCarrierOptions>());

            bridge.Attach();

            return bridge;
        });

        return builder;
    }
}
