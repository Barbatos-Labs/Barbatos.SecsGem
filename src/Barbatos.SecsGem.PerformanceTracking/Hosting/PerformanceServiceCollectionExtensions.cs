// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.PerformanceTracking;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds equipment performance tracking - SEMI E116 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method, because E116 has one side. The standard defines no messages, so there is no
/// equipment end answering primaries and no host end sending them: there is a machine that
/// accounts for its own time and exposes its modules as stream 14 objects, and a host that reads
/// those objects with <c>AddObjectServices</c>.
/// </remarks>
public static class PerformanceServiceCollectionExtensions
{
    /// <summary>Adds the performance tracker and exposes its modules on stream 14.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">Declares the machine's modules, and anything else to set up first.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="PerformanceTracker"/>, <see cref="PerformanceObjectProvider"/> as an
    /// <see cref="Barbatos.SecsGem.Objects.IObjectProvider"/>, and the eleven primaries of object
    /// services - which are the only messages E116 has, because they are stream 14's.
    /// </para>
    /// <para>
    /// The tracker takes the container's <see cref="TimeProvider"/>, which <c>AddSecsGem</c>
    /// already registers as <see cref="TimeProvider.System"/> unless the application put its own
    /// there first. That is the whole of the wiring a test has to do differently: register a
    /// <c>FakeTimeProvider</c> and every duration this package reports is yours to advance.
    /// </para>
    /// <para>
    /// <paramref name="configure"/> is where the modules come from. They are the machine's own
    /// hardware - a chamber, an aligner, the tool itself - so they are declared rather than
    /// created by a message, and a call about one that was never declared is refused.
    /// </para>
    /// <para>
    /// This composes with <c>AddCarrierEquipment</c>, <c>AddSubstrateTracking</c>,
    /// <c>AddControlJobs</c> and <c>AddProcessJobEquipment</c>: a machine that does several of
    /// them calls several, and stream 14 answers for all of their object types together.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddPerformanceTracking(
        this ISecsGemBuilder builder,
        Action<PerformanceTracker>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(services =>
        {
            var tracker = new PerformanceTracker(services.GetService<TimeProvider>());

            configure?.Invoke(tracker);

            return tracker;
        });

        return builder.AddObjectProvider<PerformanceObjectProvider>();
    }
}
