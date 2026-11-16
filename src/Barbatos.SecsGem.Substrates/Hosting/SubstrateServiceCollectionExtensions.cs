// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Substrates;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds substrate tracking - SEMI E90 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method, because E90 has one side. The standard defines no messages, so there is no
/// equipment end answering primaries and no host end sending them: there is a machine that tracks
/// substrates and exposes them as stream 14 objects, and a host that reads those objects with
/// <c>AddObjectServices</c>.
/// </remarks>
public static class SubstrateServiceCollectionExtensions
{
    /// <summary>Adds the substrate tracker and exposes it on stream 14.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">Declares the machine's substrate locations, and anything else to set up first.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="SubstrateTracker"/>, <see cref="SubstrateObjectProvider"/> as an
    /// <see cref="Barbatos.SecsGem.Objects.IObjectProvider"/>, and the eleven primaries of object
    /// services - which are the only messages E90 has, because they are stream 14's.
    /// </para>
    /// <para>
    /// <paramref name="configure"/> is where the locations come from. They are the machine's own
    /// geography - a slot, an aligner, a chuck - so they are declared rather than created by a
    /// message, and a move to one that was never declared is refused.
    /// </para>
    /// <para>
    /// This composes with <c>AddCarrierEquipment</c>: a machine that does E87 and E90 calls both,
    /// and stream 14 answers for carriers, load ports, substrates and substrate locations together.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddSubstrateTracking(
        this ISecsGemBuilder builder,
        Action<SubstrateTracker>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var tracker = new SubstrateTracker();

            configure?.Invoke(tracker);

            return tracker;
        });

        return builder.AddObjectProvider<SubstrateObjectProvider>();
    }
}
