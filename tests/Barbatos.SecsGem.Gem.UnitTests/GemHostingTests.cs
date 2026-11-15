// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// The container wiring, which is the one part of this layer that cannot be tested any other way.
/// </summary>
/// <remarks>
/// The GEM handlers depend on the equipment, the equipment depends on the connection, and the
/// connection depends on the router the handlers are registered into. That cycle is real, and the
/// only proof it resolves is resolving it.
/// </remarks>
public class GemHostingTests
{
    private static ServiceProvider Build(
        Action<GemEquipmentModelBuilder>? model = null,
        Action<GemOptions>? configure = null)
    {
        var services = new ServiceCollection();

        services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddGemEquipment(
                model ?? (builder => builder.AddStatusVariable(1, "A", () => SecsItem.U4(1u))),
                configure: configure,
                runAsHostedService: false);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_whole_graph_resolves_despite_the_cycle()
    {
        await using var provider = Build();

        var equipment = provider.GetRequiredService<GemEquipment>();

        Assert.NotNull(equipment);
        Assert.Same(provider.GetRequiredService<GemEquipment>(), equipment);
        Assert.Same(provider.GetRequiredService<HsmsConnection>(), provider.GetRequiredService<HsmsConnection>());
    }

    [Fact]
    public async Task Every_GEM_message_is_in_the_routing_table()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        foreach (var (stream, function) in GemEquipment.HandledMessages)
        {
            Assert.True(
                router.TryGetHandler(stream, function, out var handler),
                $"S{stream}F{function} should have a handler.");

            Assert.NotNull(handler);
        }
    }

    [Fact]
    public async Task The_routing_table_is_also_the_declaration_of_what_this_entity_understands()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Streams 1, 2 and 5 are understood, so an unknown function in them is S9F5. Stream 7 is
        // not, so anything in it is S9F3. That fork is the reason registering the handlers is a
        // statement of capability rather than a convenience.
        Assert.True(router.IsKnownStream(1));
        Assert.True(router.IsKnownStream(2));
        Assert.True(router.IsKnownStream(5));
        Assert.False(router.IsKnownStream(7));

        // And no catch-all, which is what keeps the automatic S9F3 and S9F5 switched on.
        Assert.Null(router.CatchAll);
    }

    [Fact]
    public async Task A_model_can_be_built_from_the_container()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new Thermometer(451d));

        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddGemEquipment(
                (model, provider) =>
                {
                    // The overload real equipment needs: a status variable reads the machine, and
                    // the machine is a service.
                    var thermometer = provider.GetRequiredService<Thermometer>();
                    model.AddStatusVariable(1, "Temperature", () => SecsItem.F8(thermometer.Celsius));
                },
                runAsHostedService: false);

        await using var provider = services.BuildServiceProvider();

        var equipment = provider.GetRequiredService<GemEquipment>();
        using var value = equipment.Model.Variables[1].Read();

        Assert.Equal(451d, value.FirstValueOrDefault<double>());
    }

    [Fact]
    public async Task A_model_name_too_long_for_MDLN_fails_rather_than_being_truncated_on_the_wire()
    {
        await using var provider = Build(configure: options => options.ModelName = new string('x', 21));

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GemOptions>>().Value);

        Assert.Contains("E30 allows 20", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_zero_retry_interval_is_refused_because_it_is_a_denial_loop()
    {
        await using var provider = Build(configure: options => options.EstablishCommunicationsRetryInterval = TimeSpan.Zero);

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GemOptions>>().Value);

        Assert.Contains("must be positive", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_transient_control_state_cannot_be_the_default_one()
    {
        await using var provider = Build(configure: options => options.DefaultControlState = GemControlState.AttemptOnline);

        var error = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<GemOptions>>().Value);

        Assert.Contains("AttemptOnline is transient", error.Message, StringComparison.Ordinal);
    }

    private sealed class Thermometer(double celsius)
    {
        public double Celsius { get; } = celsius;
    }
}
