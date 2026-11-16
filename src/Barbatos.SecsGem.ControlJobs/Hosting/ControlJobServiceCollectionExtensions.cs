// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.ControlJobs;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds control jobs - SEMI E94 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// One method, as with E90, and for the same reason: E94 defines no messages of its own. A host
/// creates, reads, commands and deletes control jobs over stream 14, so the equipment end is a
/// state machine exposed as objects and the host end is <c>AddObjectServices</c>.
/// </remarks>
public static class ControlJobServiceCollectionExtensions
{
    /// <summary>Adds the control job manager and exposes it on stream 14.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">Queues the jobs a machine starts life with, and anything else to set up first.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ControlJobManager"/>, <see cref="ControlJobObjectProvider"/> as an
    /// <see cref="Barbatos.SecsGem.Objects.IObjectProvider"/>, and the eleven primaries of object
    /// services - which are the only messages E94 has, because they are stream 14's.
    /// </para>
    /// <para>
    /// Unlike E87's ports and E90's locations, <paramref name="configure"/> usually leaves the
    /// manager empty: a control job is a host's instruction, not the machine's furniture, and it
    /// arrives as an S14F5. Pre-queueing one is for a machine that keeps its queue across a restart.
    /// </para>
    /// <para>
    /// This composes with <c>AddCarrierEquipment</c> and <c>AddSubstrateTracking</c>: a machine
    /// that does E87, E90 and E94 calls all three, and stream 14 answers for carriers, load ports,
    /// substrates, substrate locations and control jobs together.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddControlJobs(
        this ISecsGemBuilder builder,
        Action<ControlJobManager>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var manager = new ControlJobManager();

            configure?.Invoke(manager);

            return manager;
        });

        return builder.AddObjectProvider<ControlJobObjectProvider>();
    }
}
