// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Productivity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds equipment productivity accounting - SEMI E10 and E79 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method, and it declares nothing in the routing table, because these two standards define
/// nothing on a wire. What it is for is the clock: <c>AddSecsGem</c> already registers a
/// <see cref="TimeProvider"/>, and a package whose whole output is durations should measure with
/// the one the application chose rather than reaching for the system clock behind its back. That
/// is the only reason this hangs off the SECS builder at all.
/// </remarks>
public static class ProductivityServiceCollectionExtensions
{
    /// <summary>Adds a productivity tracker for one tool.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">Anything to set up first - the opening state, say.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers one <see cref="ProductivityTracker"/>, because E10 measures one piece of
    /// equipment and a connection is one piece of equipment. An application accounting for four
    /// tools out of one process builds its own four and registers them under keys of its own; this
    /// method is for the common case, which is a tool that is itself.
    /// </para>
    /// <para>
    /// Nothing is declared and no handler is wired. The numbers reach a host as whatever the
    /// application already publishes - a GEM status variable, or one of E39's enhanced stream 2
    /// requests if the fab wants them per module - and this package deliberately does not choose.
    /// See the README.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddProductivityTracking(
        this ISecsGemBuilder builder,
        Action<ProductivityTracker>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(services =>
        {
            var tracker = new ProductivityTracker(services.GetService<TimeProvider>());

            configure?.Invoke(tracker);

            return tracker;
        });

        return builder;
    }
}
