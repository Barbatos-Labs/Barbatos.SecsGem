// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceTracking.UnitTests;

/// <summary>
/// The container wiring, where a module nobody declared and a clock nobody passed are both
/// start-up faults rather than runtime ones.
/// </summary>
public class PerformanceHostingTests
{
    private const string Chamber = "PM1";

    private static ServiceProvider Build(
        FakeTimeProvider? clock = null,
        bool twice = false,
        bool alsoInMemory = false)
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
            .AddPerformanceTracking(tracker =>
            {
                tracker.AddModule(Chamber);
                tracker.AddModule("PM2");
            });

        if (twice)
        {
            builder.AddPerformanceTracking();
        }

        if (alsoInMemory)
        {
            builder.AddObjectProvider();
        }

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_tracker_resolves_once_with_its_modules_declared()
    {
        await using var provider = Build();

        var tracker = provider.GetRequiredService<PerformanceTracker>();

        Assert.Equal(2, tracker.Modules.Count);
        Assert.NotNull(tracker.FindModule(Chamber));
        Assert.Same(tracker, provider.GetRequiredService<PerformanceTracker>());
    }

    [Fact]
    public async Task The_tracker_measures_with_the_container_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero));

        await using var provider = Build(clock);

        var tracker = provider.GetRequiredService<PerformanceTracker>();

        Assert.True(tracker.BeginTask(Chamber, "RECIPE-7", PerformanceTaskTypes.Process).IsAccepted);

        clock.Advance(TimeSpan.FromMinutes(30));

        // The whole of the wiring a test has to do differently: the package took the clock the
        // application put in the container, so advancing it advances every figure it reports.
        Assert.Equal(TimeSpan.FromMinutes(30), tracker.Statistics(Chamber)!.TimeExecuting);
        Assert.Equal(clock.GetUtcNow() - TimeSpan.FromMinutes(30), tracker.FindModule(Chamber)!.StateSince);
    }

    [Fact]
    public async Task Stream_fourteen_answers_for_a_module_with_nothing_else_registered()
    {
        await using var provider = Build();

        var objects = provider.GetRequiredService<ObjectServices>();

        Assert.NotNull(objects);

        // Through the provider the connection will actually use, rather than through a type the
        // test newed up: a registration that forgot the provider would pass the other way round.
        var registered = Assert.Single(provider.GetServices<IObjectProvider>());

        var instances = await registered.GetInstancesAsync(
            PerformanceObjectProvider.ModuleType,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, instances.Count);
        Assert.Contains(Chamber, instances);
    }

    [Fact]
    public async Task It_composes_with_another_provider_on_the_same_stream()
    {
        await using var provider = Build(alsoInMemory: true);

        IObjectProvider[] providers = [.. provider.GetServices<IObjectProvider>()];

        Assert.Equal(2, providers.Length);

        // Resolved out of the enumerable rather than by its own type, because that enumerable is
        // the registration: a provider the container does not list is a provider stream 14 will
        // never ask.
        var store = Assert.Single(providers.OfType<ObjectInMemoryProvider>());

        store.Add("THING-1", "Thing");

        var composed = new CompositeObjectProvider(providers);

        var types = await composed.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(PerformanceObjectProvider.ModuleType, types);
        Assert.Contains("Thing", types);
    }

    [Fact]
    public async Task Adding_it_twice_is_not_a_start_up_failure()
    {
        // The fault the DeclareOnce change fixed: two registrations claiming stream 14 used to
        // throw, so an application that called this and another object-bearing standard could not
        // start at all.
        await using var provider = Build(twice: true);

        var tracker = provider.GetRequiredService<PerformanceTracker>();

        Assert.Equal(2, tracker.Modules.Count);
        Assert.Single(provider.GetServices<IObjectProvider>());
    }

    [Fact]
    public void A_builder_nobody_passed_throws()
        => Assert.Throws<ArgumentNullException>(() => ((ISecsGemBuilder)null!).AddPerformanceTracking());

    [Fact]
    public async Task Without_an_application_clock_the_tracker_gets_the_one_AddSecsGem_registered()
    {
        await using var provider = Build();

        Assert.Same(TimeProvider.System, provider.GetRequiredService<TimeProvider>());

        var tracker = provider.GetRequiredService<PerformanceTracker>();

        Assert.True(tracker.BeginTask(Chamber, "RECIPE-7").IsAccepted);
        Assert.True(tracker.EndTask(Chamber).IsAccepted);
    }
}
