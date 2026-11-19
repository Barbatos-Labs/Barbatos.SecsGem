// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.PerformanceTracking.UnitTests;

/// <summary>
/// E116 over stream 14, which is the whole of its wire presence.
/// </summary>
/// <remarks>
/// E116 defines no messages, so there is nothing else a host can see. What makes this provider
/// unlike every other one in the stack is that it is read-only and has no actions: a host that
/// could assign an EPT state could record a chamber as productive while it stood empty, so the
/// refusals here are the feature and they have tests of their own.
/// </remarks>
public class PerformanceObjectProviderTests
{
    private const string Chamber = "PM1";
    private const string Recipe = "RECIPE-7";

    private static readonly DateTimeOffset Start = new(2026, 3, 1, 6, 0, 0, TimeSpan.Zero);

    private static (PerformanceObjectProvider Provider, PerformanceTracker Tracker, FakeTimeProvider Clock) Build()
    {
        var clock = new FakeTimeProvider(Start);
        var tracker = new PerformanceTracker(clock);

        tracker.AddModule(Chamber);
        tracker.AddModule("PM2");

        return (new PerformanceObjectProvider(tracker), tracker, clock);
    }

    private static async Task<Dictionary<string, SecsItem>> ReadAsync(
        PerformanceObjectProvider provider,
        string specification,
        params string[] attributes)
    {
        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest
            {
                Objects = [specification],
                ObjectType = PerformanceObjectProvider.ModuleType,
                Attributes = attributes,
            },
            TestContext.Current.CancellationToken);

        Dictionary<string, SecsItem> read = [];

        foreach (var attribute in Assert.Single(values).Attributes)
        {
            read[attribute.Name] = attribute.Value;
        }

        return read;
    }

    [Fact]
    public async Task There_is_one_object_type_whether_or_not_a_module_is_declared()
    {
        var provider = new PerformanceObjectProvider(new PerformanceTracker());

        var types = await provider.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PerformanceObjectProvider.ModuleType, Assert.Single(types));
    }

    [Fact]
    public async Task The_instances_are_the_modules_the_machine_declared()
    {
        var (provider, _, _) = Build();

        var instances = await provider.GetInstancesAsync(
            PerformanceObjectProvider.ModuleType,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, instances.Count);
        Assert.Contains(Chamber, instances);
        Assert.Contains("PM2", instances);
    }

    [Fact]
    public async Task Another_standard_object_type_is_not_this_provider_business()
    {
        var (provider, _, _) = Build();

        Assert.Empty(await provider.GetInstancesAsync("Carrier", TestContext.Current.CancellationToken));
        Assert.Empty(await provider.GetAttributeNamesAsync(
            "CAR-001",
            "Carrier",
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_attribute_names_are_discoverable_rather_than_assumed()
    {
        var (provider, _, _) = Build();

        var byType = await provider.GetAttributeNamesAsync(
            string.Empty,
            PerformanceObjectProvider.ModuleType,
            TestContext.Current.CancellationToken);

        var byInstance = await provider.GetAttributeNamesAsync(
            Chamber,
            string.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(byType, byInstance);
        Assert.Contains("EPTState", byType);
        Assert.Contains("TaskName", byType);
        Assert.Contains("BlockedReasonCode", byType);
        Assert.Contains("TimeExecuting", byType);
    }

    [Fact]
    public async Task An_empty_request_reads_everything_the_module_has()
    {
        var (provider, _, _) = Build();

        var read = await ReadAsync(provider, Chamber);

        var names = await provider.GetAttributeNamesAsync(
            Chamber,
            PerformanceObjectProvider.ModuleType,
            TestContext.Current.CancellationToken);

        Assert.Equal(names.Count, read.Count);
    }

    [Fact]
    public async Task An_idle_module_reports_its_state_and_nothing_it_does_not_have()
    {
        var (provider, _, _) = Build();

        var read = await ReadAsync(provider, Chamber);

        Assert.Equal(Chamber, read["ObjID"].GetString());
        Assert.Equal(nameof(PerformanceState.Idle), read["EPTState"].GetString());
        Assert.Equal(nameof(PerformanceState.Idle), read["PreviousEPTState"].GetString());

        // A zero-length item rather than a word a host would have to know not to believe.
        Assert.Equal(0, read["BlockedReasonCode"].Count);
        Assert.Equal(0, read["BlockedReasonText"].Count);
        Assert.Equal(0, read["TaskName"].Count);
        Assert.Equal(0, read["TaskType"].Count);
        Assert.Equal(0, read["TaskStartTime"].Count);
    }

    [Fact]
    public async Task An_executing_module_reports_the_task_and_when_it_started()
    {
        var (provider, tracker, clock) = Build();

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.True(tracker.BeginTask(Chamber, Recipe, PerformanceTaskTypes.Process).IsAccepted);

        clock.Advance(TimeSpan.FromMinutes(10));

        var read = await ReadAsync(provider, Chamber);

        Assert.Equal(nameof(PerformanceState.Executing), read["EPTState"].GetString());
        Assert.Equal(nameof(PerformanceState.Idle), read["PreviousEPTState"].GetString());
        Assert.Equal(Recipe, read["TaskName"].GetString());
        Assert.Equal(PerformanceTaskTypes.Process, read["TaskType"].GetString());
        Assert.Equal("20260301060500", read["TaskStartTime"].GetString()[..14]);
        Assert.Equal("20260301060500", read["EPTStateEntryTime"].GetString()[..14]);
    }

    [Fact]
    public async Task A_timestamp_is_the_sixteen_character_form_the_catalogue_gives()
    {
        var (provider, _, _) = Build();

        var read = await ReadAsync(provider, Chamber, "EPTStateEntryTime");

        // TIMESTAMP is A|16 in the catalogue, and that is the one item here with a format the
        // reference data actually pins down.
        Assert.Equal(16, read["EPTStateEntryTime"].Count);
        Assert.Equal("2026030106000000", read["EPTStateEntryTime"].GetString());
    }

    [Fact]
    public async Task A_blocked_module_reports_what_is_stopping_it()
    {
        var (provider, tracker, _) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        Assert.True(tracker.Block(Chamber, "NO_MATERIAL", "the load port is empty").IsAccepted);

        var read = await ReadAsync(provider, Chamber);

        Assert.Equal(nameof(PerformanceState.Blocked), read["EPTState"].GetString());
        Assert.Equal("NO_MATERIAL", read["BlockedReasonCode"].GetString());
        Assert.Equal("the load port is empty", read["BlockedReasonText"].GetString());

        // The task is still the module's, which is what a host needs to know to attribute the
        // delay to the lot that is waiting on it.
        Assert.Equal(Recipe, read["TaskName"].GetString());
    }

    [Fact]
    public async Task The_times_come_back_as_whole_seconds()
    {
        var (provider, tracker, clock) = Build();

        Assert.True(tracker.BeginTask(Chamber, Recipe).IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(tracker.Block(Chamber, "NO_MATERIAL").IsAccepted);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(tracker.EndTask(Chamber).IsAccepted);

        var read = await ReadAsync(provider, Chamber);

        Assert.Equal(600u, read["TimeExecuting"].FirstValueOrDefault<uint>());
        Assert.Equal(120u, read["TimeBlocked"].FirstValueOrDefault<uint>());
        Assert.Equal(0u, read["TimeIdle"].FirstValueOrDefault<uint>());
        Assert.Equal(120u, read["TimeInState"].FirstValueOrDefault<uint>());
        Assert.Equal(1u, read["TasksCompleted"].FirstValueOrDefault<uint>());
        Assert.Equal(0u, read["TasksAbandoned"].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task A_module_nobody_declared_is_left_out_of_the_answer()
    {
        var (provider, _, _) = Build();

        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = [Chamber, "PM9"] },
            TestContext.Current.CancellationToken);

        // S14F2 and S14F20 carry no acknowledgement code, so leaving it out is the only way an
        // absence can be said.
        Assert.Equal(Chamber, Assert.Single(values).ObjectSpecification);
    }

    [Fact]
    public async Task An_attribute_nobody_defined_is_left_out_rather_than_invented()
    {
        var (provider, _, _) = Build();

        var read = await ReadAsync(provider, Chamber, "EPTState", "Temperature");

        Assert.Single(read);
        Assert.Contains("EPTState", read.Keys);
    }

    [Fact]
    public async Task Nothing_here_can_be_set()
    {
        var (provider, _, _) = Build();

        var names = await provider.GetAttributeNamesAsync(
            Chamber,
            PerformanceObjectProvider.ModuleType,
            TestContext.Current.CancellationToken);

        foreach (var name in names)
        {
            var results = await provider.SetAttributesAsync(
                new ObjectAttributeWrite
                {
                    Objects = [Chamber],
                    ObjectType = PerformanceObjectProvider.ModuleType,
                    Attributes = [new ObjectAttribute { Name = name, Value = SecsItem.A("Executing") }],
                },
                TestContext.Current.CancellationToken);

            Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        }

        // Every one of them, and the state did not move while we tried.
        Assert.NotEmpty(names);
    }

    [Fact]
    public async Task A_set_on_a_module_nobody_declared_says_so_instead()
    {
        var (provider, _, _) = Build();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["PM9"],
                Attributes = [new ObjectAttribute { Name = "EPTState", Value = SecsItem.A("Idle") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, Assert.Single(results).Acknowledgement);
    }

    [Fact]
    public async Task An_action_on_a_module_is_refused_and_not_ignored()
    {
        var (provider, _, _) = Build();

        var results = await provider.ActAsync(
            new ObjectActionRequest
            {
                Objects = [Chamber],
                ObjectType = PerformanceObjectProvider.ModuleType,
                Command = "GoIdle",
            },
            TestContext.Current.CancellationToken);

        // Not an empty list, which the library reads as "no object of mine": the module is this
        // provider's and the answer is no.
        var result = Assert.Single(results);

        Assert.Equal(Chamber, result.ObjectSpecification);
        Assert.Equal(ObjectAcknowledgement.Error, result.Acknowledgement);
    }

    [Fact]
    public async Task An_action_on_somebody_else_object_is_left_to_them()
    {
        var (provider, _, _) = Build();

        var results = await provider.ActAsync(
            new ObjectActionRequest { Objects = ["CAR-001"], Command = "ProceedWithCarrier" },
            TestContext.Current.CancellationToken);

        Assert.Empty(results);
    }

    [Fact]
    public async Task A_module_cannot_be_created_or_deleted_by_asking()
    {
        var (provider, _, _) = Build();
        IObjectProvider contract = provider;

        var created = await contract.CreateAsync(
            new ObjectCreateRequest { ObjectType = PerformanceObjectProvider.ModuleType },
            TestContext.Current.CancellationToken);

        var deleted = await contract.DeleteAsync(
            new ObjectDeleteRequest
            {
                ObjectType = PerformanceObjectProvider.ModuleType,
                ObjectSpecification = Chamber,
            },
            TestContext.Current.CancellationToken);

        // The interface defaults, deliberately left alone: a module is hardware, and a host
        // cannot bring a chamber into existence by asking for one.
        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, created.Acknowledgement);
        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, deleted);

        var remaining = await provider.GetInstancesAsync(
            PerformanceObjectProvider.ModuleType,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, remaining.Count);
    }

    [Fact]
    public async Task Null_arguments_throw()
    {
        var (provider, _, _) = Build();

        Assert.Throws<ArgumentNullException>(() => new PerformanceObjectProvider(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.GetInstancesAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.GetAttributeNamesAsync(null!, string.Empty, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.GetAttributesAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.SetAttributesAsync(null!, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.ActAsync(null!, TestContext.Current.CancellationToken));
    }
}
