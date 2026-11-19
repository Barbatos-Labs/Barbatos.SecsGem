// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Productivity.UnitTests;

/// <summary>
/// The container wiring, which is about one thing: the clock.
/// </summary>
/// <remarks>
/// E10 and E79 define nothing on a wire, so this registration declares no handler and adds no
/// message. What it is for is that a package whose whole output is durations should measure with
/// the <see cref="TimeProvider"/> the application chose.
/// </remarks>
public class ProductivityHostingTests
{
    private static ServiceProvider Build(FakeTimeProvider? clock = null, bool twice = false)
    {
        var services = new ServiceCollection();

        if (clock is not null)
        {
            // Before AddSecsGem, which registers TimeProvider.System with TryAddSingleton: the
            // application's own clock wins because it got there first.
            services.AddSingleton<TimeProvider>(clock);
        }

        var builder = services
            .AddSecsGem(configure: link =>
            {
                link.DeviceId = 7;
                link.Mode = HsmsConnectionMode.Passive;
            })
            .UseInMemoryTransport(isActive: false)
            .AddProductivityTracking(tracker =>
                tracker.MoveTo(EquipmentState.Standby, "waiting for the first lot"));

        if (twice)
        {
            builder.AddProductivityTracking();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_tracker_resolves_once_in_the_state_it_was_configured_in()
    {
        await using var provider = Build();

        var tracker = provider.GetRequiredService<ProductivityTracker>();

        Assert.Equal(EquipmentState.Standby, tracker.State);
        Assert.Equal("waiting for the first lot", tracker.StateReason);
        Assert.Same(tracker, provider.GetRequiredService<ProductivityTracker>());
    }

    [Fact]
    public async Task The_tracker_measures_with_the_container_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        await using var provider = Build(clock);

        var tracker = provider.GetRequiredService<ProductivityTracker>();

        Assert.True(tracker.MoveTo(EquipmentState.Productive).IsAccepted);

        clock.Advance(TimeSpan.FromHours(6));

        var report = tracker.Report();

        // The only reason this registration hangs off the SECS builder at all: advancing the
        // application's clock advances every figure the package reports.
        Assert.Equal(TimeSpan.FromHours(6), report.Productive);
        Assert.Equal(1d, report.OperationalUtilisation);
    }

    [Fact]
    public async Task Nothing_is_declared_because_neither_standard_has_a_message()
    {
        await using var provider = Build();

        var router = provider.GetRequiredService<ISecsMessageRouter>();

        // Every stream that could plausibly carry this - 1, 2 and 6 - and nothing of ours in any
        // of them, because there is nothing of ours to put there.
        foreach (var stream in new byte[] { 1, 2, 6, 14 })
        {
            for (byte function = 1; function < 64; function += 2)
            {
                Assert.False(
                    router.TryGetHandler(stream, function, out _),
                    $"S{stream}F{function} was declared by a package with no messages");
            }
        }
    }

    [Fact]
    public async Task Adding_it_twice_is_not_a_start_up_failure()
    {
        await using var provider = Build(twice: true);

        // And the first configuration survives, which is what TryAddSingleton means: a second
        // call must not quietly reset a shift's accounting.
        var tracker = provider.GetRequiredService<ProductivityTracker>();

        Assert.Equal(EquipmentState.Standby, tracker.State);
    }

    [Fact]
    public async Task Without_an_application_clock_the_tracker_gets_the_one_AddSecsGem_registered()
    {
        await using var provider = Build();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());
        Assert.True(provider.GetRequiredService<ProductivityTracker>().MoveTo(EquipmentState.Productive).IsAccepted);
    }

    [Fact]
    public void A_builder_nobody_passed_throws()
        => Assert.Throws<ArgumentNullException>(() => ((ISecsGemBuilder)null!).AddProductivityTracking());
}
