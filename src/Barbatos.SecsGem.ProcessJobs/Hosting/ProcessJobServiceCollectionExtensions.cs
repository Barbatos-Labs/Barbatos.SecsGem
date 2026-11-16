// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.ProcessJobs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds process jobs - SEMI E40 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// Two methods, because E40 has two sides: twelve of its fourteen transactions start at the host,
/// and two start at the equipment. That makes it the second standard in this stack where both ends
/// send primaries, after stream 3's material status.
/// </remarks>
public static class ProcessJobServiceCollectionExtensions
{
    /// <summary>Adds the equipment side: the manager, the twelve primaries, and the job as an object.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">Sets the machine up first - <see cref="ProcessJobManager.JobSpace"/>, and any job it starts life with.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="ProcessJobManager"/>, <see cref="ProcessJobEquipment"/>,
    /// <see cref="ProcessJobObjectProvider"/> as an
    /// <see cref="Barbatos.SecsGem.Objects.IObjectProvider"/>, and handlers for stream 16's twelve
    /// host primaries <strong>and</strong> the eleven of object services - because E40 needs both:
    /// stream 16 drives a job and stream 14 describes it.
    /// </para>
    /// <para>
    /// <paramref name="configure"/> usually only sets <c>PRJOBSPACE</c>. A process job is a host's
    /// instruction and arrives as an S16F1; pre-creating one is for a machine that keeps its queue
    /// across a restart.
    /// </para>
    /// <para>
    /// This composes with <c>AddCarrierEquipment</c>, <c>AddSubstrateTracking</c> and
    /// <c>AddControlJobs</c>: a machine that does all four answers stream 14 for carriers, load
    /// ports, substrates, substrate locations, control jobs and process jobs together.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddProcessJobEquipment(
        this ISecsGemBuilder builder,
        Action<ProcessJobManager>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var manager = new ProcessJobManager();

            configure?.Invoke(manager);

            return manager;
        });

        builder.Services.TryAddSingleton(services => new ProcessJobEquipment(
            services.GetRequiredService<ProcessJobManager>(),
            services.GetRequiredService<HsmsConnection>(),
            services.GetService<TimeProvider>(),
            services.GetService<ILogger<ProcessJobEquipment>>()));

        // Declared once by the builder, which is where that rule now lives for every capability
        // in this stack. Deferred, like every other one: the handler needs the equipment, the
        // equipment needs the connection, and the connection needs the routing table these
        // declarations go into. Resolving the equipment here would close that circle.
        builder.DeclareOnce(ProcessJobEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<ProcessJobEquipment>().HandleAsync(transaction, token));

        return builder.AddObjectProvider<ProcessJobObjectProvider>();
    }

    /// <summary>Adds the host side: the twelve primaries it sends, and the two it answers.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Registers <see cref="ProcessJobHost"/> and handlers for S16F11 and S16F29, the two the
    /// equipment sends unprompted, plus <c>AddObjectServices</c> so a host can read a job's
    /// attributes over stream 14. No provider: a host has no process jobs of its own, and an end
    /// with no objects should answer S9F5 rather than answer every <c>GetAttr</c> with an empty
    /// list.
    /// </remarks>
    public static ISecsGemBuilder AddProcessJobHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(services => new ProcessJobHost(
            services.GetRequiredService<HsmsConnection>(),
            services.GetService<ILogger<ProcessJobHost>>()));

        builder.DeclareOnce(ProcessJobHost.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<ProcessJobHost>().HandleAsync(transaction, token));

        return builder.AddObjectServices();
    }
}

