// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.PerformanceFlow;
using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.Productivity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds the join between E116 and E10 to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method, and it declares nothing in the routing table, because neither standard it joins has
/// a message. It needs both trackers, so call <c>AddPerformanceTracking</c> and
/// <c>AddProductivityTracking</c> first - and the module name, which is the one thing with no
/// default that could be right.
/// </remarks>
public static class PerformanceFlowServiceCollectionExtensions
{
    /// <summary>Makes a module's E116 state drive the tool's E10 accounting.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="moduleId">Which module <em>is</em> the tool.</param>
    /// <param name="configure">The conventions - what your task types and blocked reasons mean, and whether the tool is scheduled.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="moduleId"/> is <see langword="null"/> or empty.</exception>
    /// <remarks>
    /// <para>
    /// Registers one <see cref="PerformanceProductivityBridge"/>, attached, and brings the tool in
    /// line with wherever the module already is - so a machine that declared its modules in
    /// <c>AddPerformanceTracking</c> does not have to remember to call anything.
    /// </para>
    /// <para>
    /// <strong>It resolves both trackers rather than creating either.</strong> A bridge over a
    /// tracker nobody else has is a bridge to nowhere, which is the fault the first material join
    /// taught this repository to test for: the registration asserts both ends exist by asking the
    /// container for them.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddPerformanceProductivityBridge(
        this ISecsGemBuilder builder,
        string moduleId,
        Action<PerformanceProductivityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(moduleId);

        builder.Services.TryAddSingleton(services =>
        {
            var options = new PerformanceProductivityOptions();

            configure?.Invoke(options);

            var bridge = new PerformanceProductivityBridge(
                services.GetRequiredService<PerformanceTracker>(),
                services.GetRequiredService<ProductivityTracker>(),
                moduleId,
                options);

            bridge.Attach();

            // And in line at once, because a tool whose E10 state only starts following its module
            // at the next transition charges the whole first stretch to whatever E10 was left in.
            bridge.Apply();

            return bridge;
        });

        return builder;
    }
}
