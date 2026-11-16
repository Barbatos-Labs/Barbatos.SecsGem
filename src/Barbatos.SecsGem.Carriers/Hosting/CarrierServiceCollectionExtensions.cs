// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Adds carrier management - SEMI E87 - to a connection built by <c>AddSecsGem</c>.</summary>
/// <remarks>
/// Either end, never both on one builder: the equipment declares five primaries and the host
/// declares none, and a process answering as both ends of one link is not a thing E87 describes.
/// </remarks>
public static class CarrierServiceCollectionExtensions
{
    /// <summary>Adds the equipment side: the state machines, the five requests, and the objects on stream 14.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <param name="configure">Declares the machine's load ports, and anything else to set up before the first message.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Registers <see cref="CarrierManager"/>, <see cref="CarrierEquipment"/>, and
    /// <see cref="CarrierObjectProvider"/> as the <see cref="IObjectProvider"/> that stream 14
    /// answers out of - then declares both the five carrier primaries and the eleven of object
    /// services. E87 needs both halves: the actions move the state machines, and the attributes,
    /// the slot map among them, are read over stream 14.
    /// </para>
    /// <para>
    /// <paramref name="configure"/> is where the load ports come from. They are hardware, so they
    /// are declared rather than created by a message - a host cannot invent a load port, and an
    /// action naming one that does not exist is answered <c>CAACK</c> 3.
    /// </para>
    /// <para>
    /// The provider composes rather than replaces: an application with objects of its own, or a
    /// machine that also calls <c>AddSubstrateTracking</c> for E90, gets stream 14 answering for
    /// all of them. See <c>AddObjectProvider</c>.
    /// </para>
    /// </remarks>
    public static ISecsGemBuilder AddCarrierEquipment(
        this ISecsGemBuilder builder,
        Action<CarrierManager>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(_ =>
        {
            var manager = new CarrierManager();

            configure?.Invoke(manager);

            return manager;
        });

        builder.Services.TryAddSingleton(provider => new CarrierEquipment(
            provider.GetRequiredService<CarrierManager>(),
            provider.GetService<ILogger<CarrierEquipment>>()));

        builder.DeclareOnce(CarrierEquipment.HandledMessages, static provider =>
            (transaction, token) => provider.GetRequiredService<CarrierEquipment>().HandleAsync(transaction, token));

        return builder.AddObjectProvider<CarrierObjectProvider>();
    }

    /// <summary>Adds the host side: the five requests, and a method per action worth naming.</summary>
    /// <param name="builder">The builder <c>AddSecsGem</c> returned.</param>
    /// <returns>The builder, for chaining.</returns>
    /// <remarks>
    /// Declares nothing in the routing table, because E87 gives a host no primary to answer - and
    /// adds <c>AddObjectServices</c>, because a host that cannot read a carrier's attributes is
    /// only half of one.
    /// </remarks>
    public static ISecsGemBuilder AddCarrierHost(this ISecsGemBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddSingleton(provider => new CarrierHost(
            provider.GetRequiredService<HsmsConnection>(),
            provider.GetService<ILogger<CarrierHost>>()));

        return builder.AddObjectServices();
    }
}
