// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.PerformanceTracking;
using Barbatos.SecsGem.Productivity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceFlow.UnitTests;

/// <summary>
/// The container wiring, where a bridge with one end would be a bridge to nowhere.
/// </summary>
/// <remarks>
/// The fault the first material join taught this repository to test for. A registration that
/// forgot to attach, or resolved a tracker nobody else has, passes every unit test of the mapping
/// and does nothing at all in an application.
/// </remarks>
public class PerformanceFlowHostingTests
{
    private const string Tool = "EQUIPMENT";

    private static ServiceProvider Build(FakeTimeProvider clock, bool bridge = true)
    {
        var services = new ServiceCollection();

        services.AddSingleton<TimeProvider>(clock);

        var builder = services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddPerformanceTracking(tracker =>
            {
                tracker.AddModule(Tool);
                tracker.AddModule("PM1");
            })
            .AddProductivityTracking();

        if (bridge)
        {
            builder.AddPerformanceProductivityBridge(
                Tool,
                options => options.BlockedStates["NO_MATERIAL"] = EquipmentState.Standby);
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_bridge_resolves_attached_and_already_in_line()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        await using var provider = Build(clock);

        var bridge = provider.GetRequiredService<PerformanceProductivityBridge>();

        Assert.True(bridge.IsAttached);
        Assert.Equal(Tool, bridge.ModuleId);
        Assert.Same(bridge, provider.GetRequiredService<PerformanceProductivityBridge>());

        // Already in line: an idle module on a scheduled tool is standby, and E10 would otherwise
        // still be non-scheduled until the first transition - charging the whole first stretch to
        // a state nobody was in.
        Assert.Equal(EquipmentState.Standby, provider.GetRequiredService<ProductivityTracker>().State);
    }

    [Fact]
    public async Task A_shift_needs_no_application_code_between_the_two_standards()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        await using var provider = Build(clock);

        // Resolved so the bridge is built and attached before anything happens.
        Assert.NotNull(provider.GetRequiredService<PerformanceProductivityBridge>());

        var performance = provider.GetRequiredService<PerformanceTracker>();
        var productivity = provider.GetRequiredService<ProductivityTracker>();

        clock.Advance(TimeSpan.FromHours(2));
        Assert.True(performance.BeginTask(Tool, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted);
        clock.Advance(TimeSpan.FromHours(10));
        Assert.True(performance.Block(Tool, "NO_MATERIAL").IsAccepted);
        clock.Advance(TimeSpan.FromHours(12));

        var report = productivity.Report();

        Assert.Equal(TimeSpan.FromHours(24), report.Total);
        Assert.Equal(TimeSpan.FromHours(10), report.Productive);
        Assert.Equal(TimeSpan.FromHours(14), report.Standby);
        Assert.Equal(0, report.Failures);
    }

    [Fact]
    public async Task Without_the_bridge_the_two_standards_know_nothing_about_each_other()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        await using var provider = Build(clock, bridge: false);

        var performance = provider.GetRequiredService<PerformanceTracker>();
        var productivity = provider.GetRequiredService<ProductivityTracker>();

        Assert.True(performance.BeginTask(Tool, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted);
        clock.Advance(TimeSpan.FromHours(8));

        // The whole point of the package, stated as the absence of it: E10 sat in non-scheduled
        // for eight hours while a chamber produced.
        Assert.Equal(EquipmentState.NonScheduled, productivity.State);
        Assert.Equal(TimeSpan.FromHours(8), productivity.Report().NonScheduled);
    }

    /// <summary>
    /// The join adds no message of its own.
    /// </summary>
    /// <remarks>
    /// Asserted as a difference rather than as an absence, and the first attempt at it was wrong
    /// for an instructive reason: <c>AddPerformanceTracking</c> <em>does</em> declare stream 14,
    /// because E116's modules are objects. So "nothing is declared here" is false about the
    /// container and true about this registration, and the only honest way to say it is to build
    /// the graph twice and compare.
    /// </remarks>
    [Fact]
    public async Task The_registration_adds_no_message_of_its_own()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        await using var with = Build(clock, bridge: true);
        await using var without = Build(clock, bridge: false);

        var joined = with.GetRequiredService<ISecsMessageRouter>();
        var bare = without.GetRequiredService<ISecsMessageRouter>();

        Assert.NotNull(with.GetRequiredService<PerformanceProductivityBridge>());

        foreach (var stream in new byte[] { 1, 2, 3, 6, 14, 16 })
        {
            for (byte function = 1; function < 64; function += 2)
            {
                Assert.Equal(
                    bare.TryGetHandler(stream, function, out _),
                    joined.TryGetHandler(stream, function, out _));
            }
        }
    }

    [Fact]
    public void A_builder_nobody_passed_throws_and_so_does_a_nameless_module()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ((ISecsGemBuilder)null!).AddPerformanceProductivityBridge(Tool));

        var services = new ServiceCollection();
        var builder = services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false);

        Assert.Throws<ArgumentException>(() => builder.AddPerformanceProductivityBridge(string.Empty));
    }

    [Fact]
    public async Task A_bridge_over_a_tracker_nobody_registered_fails_at_start_up()
    {
        var services = new ServiceCollection();

        // No AddProductivityTracking, which is the mistake: a bridge with one end is a bridge to
        // nowhere, and it should be a resolution failure rather than a quietly dead join.
        services
            .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
            .UseInMemoryTransport(isActive: false)
            .AddPerformanceTracking(tracker => tracker.AddModule(Tool))
            .AddPerformanceProductivityBridge(Tool);

        await using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<PerformanceProductivityBridge>());
    }
}
