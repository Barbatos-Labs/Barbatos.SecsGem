// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// E94 over stream 14, which is the whole of its wire presence.
/// </summary>
/// <remarks>
/// E94 defines no messages: a control job is created with S14F5, commanded with S14F9, read with
/// S14F1 and removed with S14F7. So this provider is the standard's entire surface, and it is the
/// first one in this stack that uses all four - which is why the actionable half of the Objects
/// package had to be built before this package could exist.
/// </remarks>
public class ControlJobObjectProviderTests
{
    private static (ControlJobManager Manager, ControlJobObjectProvider Provider) Loaded()
    {
        var manager = new ControlJobManager();

        Assert.True(manager.Create(
            "CJ-1",
            ["CAR-001", "CAR-002"],
            ControlJobStartMethod.HostInitiated,
            "FIFO",
            "RECIPE-A").IsAccepted);

        return (manager, new ControlJobObjectProvider(manager));
    }

    private static async Task<Dictionary<string, SecsItem>> ReadAsync(
        ControlJobObjectProvider provider,
        string specification,
        string objectType = "",
        params string[] attributes)
    {
        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest
            {
                Objects = [specification],
                ObjectType = objectType,
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

    private static ValueTask<IReadOnlyList<ObjectSetResult>> SetAsync(
        ControlJobObjectProvider provider,
        string specification,
        params (string Name, SecsItem Value)[] attributes)
        => provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = [specification],
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes = [.. attributes.Select(pair => new ObjectAttribute
                {
                    Name = pair.Name,
                    Value = pair.Value,
                })],
            },
            TestContext.Current.CancellationToken);

    private static ValueTask<IReadOnlyList<ObjectActionResult>> ActAsync(
        ControlJobObjectProvider provider,
        string command,
        params string[] objects)
        => provider.ActAsync(
            new ObjectActionRequest
            {
                Objects = objects,
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Command = command,
            },
            TestContext.Current.CancellationToken);

    // ------------------------------------------------------------- the namelists

    [Fact]
    public async Task The_control_job_type_is_there_whether_or_not_a_job_is()
    {
        var provider = new ControlJobObjectProvider(new ControlJobManager());

        Assert.Equal(
            ControlJobObjectProvider.ControlJobType,
            Assert.Single(await provider.GetObjectTypesAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task The_instances_are_the_jobs_the_manager_has()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            "CJ-1",
            Assert.Single(await provider.GetInstancesAsync(
                ControlJobObjectProvider.ControlJobType,
                TestContext.Current.CancellationToken)));

        // Including the finished ones, until a host deletes them: a job's record outlives its run,
        // which is the only way a host can read how it ended.
        Assert.True(manager.Command("CJ-1", ControlJobCommands.Cancel).IsAccepted);

        Assert.Single(await provider.GetInstancesAsync(
            ControlJobObjectProvider.ControlJobType,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Somebody_elses_object_type_gets_nothing()
    {
        var (_, provider) = Loaded();

        // Left empty rather than answered, because this is how composition works: a machine doing
        // E87 and E94 asks both providers, and the one that does not own the type says nothing.
        Assert.Empty(await provider.GetInstancesAsync("Carrier", TestContext.Current.CancellationToken));

        Assert.Empty(await provider.GetAttributeNamesAsync(
            string.Empty,
            "Carrier",
            TestContext.Current.CancellationToken));

        Assert.Empty(await provider.GetAttributeNamesAsync(
            "CAR-001",
            string.Empty,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_attribute_names_are_there_by_type_and_by_instance()
    {
        var (_, provider) = Loaded();

        var byType = await provider.GetAttributeNamesAsync(
            string.Empty,
            ControlJobObjectProvider.ControlJobType,
            TestContext.Current.CancellationToken);

        var byInstance = await provider.GetAttributeNamesAsync(
            "CJ-1",
            string.Empty,
            TestContext.Current.CancellationToken);

        Assert.Equal(byType, byInstance);

        // A host discovers them with S14F17 rather than assuming them, which is the one honest
        // answer when the names cannot be checked against reference data.
        Assert.Contains("State", byType);
        Assert.Contains("CarrierInputSpec", byType);
        Assert.Contains("StartMethod", byType);
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task A_job_reads_as_every_attribute_it_has()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, "CJ-1");

        Assert.Equal("CJ-1", read["ObjID"].GetString());
        Assert.Equal(nameof(ControlJobState.Queued), read["State"].GetString());
        Assert.Equal(nameof(ControlJobStartMethod.HostInitiated), read["StartMethod"].GetString());
        Assert.Equal("FIFO", read["ProcessOrderMgmt"].GetString());
        Assert.Equal("RECIPE-A", read["ProcessingCtrlSpec"].GetString());

        // The material as a list of names, which is what E94's carrier input specification is.
        var carriers = read["CarrierInputSpec"];

        Assert.True(carriers.IsList);
        Assert.Equal(2, carriers.Count);
        Assert.Equal("CAR-001", carriers.Items[0].GetString());
        Assert.Equal("CAR-002", carriers.Items[1].GetString());
    }

    [Fact]
    public async Task A_job_that_has_not_ended_says_so_with_a_zero_length_item()
    {
        var (manager, provider) = Loaded();

        var running = await ReadAsync(provider, "CJ-1", attributes: ["Completion"]);

        // Not "Completed", not a word at all: an absent value is a zero-length item everywhere in
        // this library, and anything else here would read as an ending that has not happened.
        Assert.Equal(0, running["Completion"].Count);

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Cancel).IsAccepted);

        var ended = await ReadAsync(provider, "CJ-1", attributes: ["Completion"]);

        Assert.Equal(nameof(ControlJobCompletion.Cancelled), ended["Completion"].GetString());
    }

    [Fact]
    public async Task A_read_of_named_attributes_gives_those_and_no_others()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, "CJ-1", attributes: ["State", "ProcessingCtrlSpec"]);

        Assert.Equal(2, read.Count);
        Assert.Contains("State", read);
        Assert.Contains("ProcessingCtrlSpec", read);
    }

    [Fact]
    public async Task An_attribute_a_control_job_does_not_have_is_left_out()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, "CJ-1", attributes: ["State", "SlotMap"]);

        // S14F2 carries attributes and no acknowledgement code, so leaving it out is the only
        // thing the reply can say - and saying nothing about it is better than an empty value that
        // reads as "the job has no slot map".
        Assert.Equal("State", Assert.Single(read).Key);
    }

    [Fact]
    public async Task A_job_nobody_created_is_left_out_of_the_answer()
    {
        var (_, provider) = Loaded();

        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["CJ-1", "CJ-404"] },
            TestContext.Current.CancellationToken);

        Assert.Equal("CJ-1", Assert.Single(values).ObjectSpecification);
    }

    [Fact]
    public async Task A_read_for_somebody_elses_type_answers_nothing()
    {
        var (_, provider) = Loaded();

        Assert.Empty(await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["CJ-1"], ObjectType = "Substrate" },
            TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public async Task The_four_settable_attributes_go_through_the_manager()
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(
            provider,
            "CJ-1",
            ("CarrierInputSpec", SecsItem.L(SecsItem.A("CAR-009"), SecsItem.A("CAR-010"))),
            ("StartMethod", SecsItem.A(nameof(ControlJobStartMethod.Automatic))),
            ("ProcessOrderMgmt", SecsItem.A("SEQUENTIAL")),
            ("ProcessingCtrlSpec", SecsItem.A("RECIPE-B")));

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(results).Acknowledgement);

        var job = manager.Find("CJ-1");

        Assert.NotNull(job);
        Assert.Equal(["CAR-009", "CAR-010"], job.Carriers);
        Assert.Equal(ControlJobStartMethod.Automatic, job.StartMethod);
        Assert.Equal("SEQUENTIAL", job.ProcessOrder);
        Assert.Equal("RECIPE-B", job.ProcessingSpecification);
    }

    [Fact]
    public async Task A_state_a_host_assigned_is_refused()
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(provider, "CJ-1", ("State", SecsItem.A(nameof(ControlJobState.Executing))));

        // The refusal this whole provider is built around: a job a host declared Executing without
        // the machine starting it would be a lie told in an attribute.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        Assert.Equal(ControlJobState.Queued, manager.Find("CJ-1")!.State);
    }

    [Theory]
    [InlineData("ObjID")]
    [InlineData("Completion")]
    [InlineData("SlotMap")]
    public async Task Nothing_else_is_settable_either(string attribute)
    {
        var (_, provider) = Loaded();

        var results = await SetAsync(provider, "CJ-1", (attribute, SecsItem.A("anything")));

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
    }

    [Fact]
    public async Task One_read_only_attribute_stops_the_whole_set()
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(
            provider,
            "CJ-1",
            ("ProcessOrderMgmt", SecsItem.A("SEQUENTIAL")),
            ("State", SecsItem.A(nameof(ControlJobState.Paused))));

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);

        // Checked before anything is written, the rule every provider in this stack follows: a set
        // that half applied would leave a host with no way of knowing what took.
        Assert.Equal("FIFO", manager.Find("CJ-1")!.ProcessOrder);
    }

    [Fact]
    public async Task A_set_on_a_job_that_has_been_selected_is_refused()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.Select("CJ-1").IsAccepted);

        var results = await SetAsync(provider, "CJ-1", ("ProcessingCtrlSpec", SecsItem.A("RECIPE-B")));

        // Through the manager rather than onto the field, so the state machine refuses it here
        // exactly as it would anywhere else - and Error, because OBJACK has no code for a state.
        Assert.Equal(ObjectAcknowledgement.Error, Assert.Single(results).Acknowledgement);
        Assert.Equal("RECIPE-A", manager.Find("CJ-1")!.ProcessingSpecification);
    }

    [Fact]
    public async Task A_set_on_a_job_nobody_created_is_left_out()
    {
        var (_, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["CJ-404"],
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes = [new ObjectAttribute { Name = "ProcessOrderMgmt", Value = SecsItem.A("FIFO") }],
            },
            TestContext.Current.CancellationToken);

        // Left out, not refused: object services turns an unanswered object into
        // InvalidObjectInstance, and a provider that refused on its own behalf would answer for
        // objects belonging to the other packages on the same link.
        Assert.Empty(results);
    }

    [Fact]
    public async Task The_carriers_can_arrive_as_a_list_or_as_one_item()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "CJ-1", ("CarrierInputSpec", SecsItem.A("CAR-009"))))
                .Acknowledgement);

        // Tolerant read, exact write: E94 says a list, and a host that sends one carrier as one
        // item means one carrier.
        Assert.Equal("CAR-009", Assert.Single(manager.Find("CJ-1")!.Carriers));

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "CJ-1", ("CarrierInputSpec", SecsItem.L())))
                .Acknowledgement);

        Assert.Empty(manager.Find("CJ-1")!.Carriers);
    }

    [Theory]
    [InlineData("HostInitiated", ControlJobStartMethod.HostInitiated)]
    [InlineData("Automatic", ControlJobStartMethod.Automatic)]
    [InlineData("nonsense", ControlJobStartMethod.Automatic)]
    public async Task A_start_method_is_read_from_its_name(string sent, ControlJobStartMethod expected)
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "CJ-1", ("StartMethod", SecsItem.A(sent)))).Acknowledgement);

        // A word nobody defined reads as automatic, because a job nobody can start is worse than
        // one that starts itself.
        Assert.Equal(expected, manager.Find("CJ-1")!.StartMethod);
    }

    [Fact]
    public async Task A_start_method_is_also_read_from_a_number()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "CJ-1", ("StartMethod", SecsItem.U1(1)))).Acknowledgement);

        // The same tolerance stream 12 and stream 3 needed: a machine that sends E94's code where
        // this package writes a name is still understood.
        Assert.Equal(ControlJobStartMethod.HostInitiated, manager.Find("CJ-1")!.StartMethod);

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "CJ-1", ("StartMethod", SecsItem.I4(0)))).Acknowledgement);

        Assert.Equal(ControlJobStartMethod.Automatic, manager.Find("CJ-1")!.StartMethod);
    }

    // ------------------------------------------------------------------- acting

    [Fact]
    public async Task Every_command_reaches_the_state_machine()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await ActAsync(provider, ControlJobCommands.Select, "CJ-1")).Acknowledgement);

        Assert.Equal(ControlJobState.Selected, manager.Find("CJ-1")!.State);
        Assert.True(manager.MaterialReady("CJ-1").IsAccepted);

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await ActAsync(provider, ControlJobCommands.Start, "CJ-1")).Acknowledgement);

        Assert.Equal(ControlJobState.Executing, manager.Find("CJ-1")!.State);

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await ActAsync(provider, ControlJobCommands.Stop, "CJ-1")).Acknowledgement);

        Assert.Equal(ControlJobCompletion.Stopped, manager.Find("CJ-1")!.Completion);
    }

    [Fact]
    public async Task A_command_the_state_refuses_comes_back_as_an_error()
    {
        var (manager, provider) = Loaded();

        var result = Assert.Single(await ActAsync(provider, ControlJobCommands.Pause, "CJ-1"));

        // Error rather than a code naming the state, because OBJACK has five codes and none of
        // them means "not from Queued". The reason stays on the equipment side, where a log can
        // have it, and this is the one thing E94 loses by having no message set of its own.
        Assert.Equal(ObjectAcknowledgement.Error, result.Acknowledgement);
        Assert.Equal(ControlJobState.Queued, manager.Find("CJ-1")!.State);
    }

    [Fact]
    public async Task A_command_nobody_defined_is_an_error_and_not_an_exception()
    {
        var (_, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Error,
            Assert.Single(await ActAsync(provider, "Teleport", "CJ-1")).Acknowledgement);
    }

    [Fact]
    public async Task An_action_answers_for_the_jobs_it_has_and_leaves_out_the_rest()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.Create("CJ-2").IsAccepted);

        var results = await ActAsync(provider, ControlJobCommands.Cancel, "CJ-1", "CAR-001", "CJ-2");

        // Two of the three, in the order they were asked: the carrier belongs to another provider
        // on the same link, and answering for it would be answering for E87.
        Assert.Equal(2, results.Count);
        Assert.Equal("CJ-1", results[0].ObjectSpecification);
        Assert.Equal("CJ-2", results[1].ObjectSpecification);
        Assert.All(results, result => Assert.Equal(ObjectAcknowledgement.Successful, result.Acknowledgement));
    }

    [Fact]
    public async Task An_action_for_somebody_elses_type_answers_nothing()
    {
        var (manager, provider) = Loaded();

        Assert.Empty(await provider.ActAsync(
            new ObjectActionRequest
            {
                Objects = ["CJ-1"],
                ObjectType = "Carrier",
                Command = ControlJobCommands.Cancel,
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(ControlJobState.Queued, manager.Find("CJ-1")!.State);
    }

    [Fact]
    public async Task A_multi_object_action_carries_no_type_and_is_still_answered()
    {
        var (manager, provider) = Loaded();

        // S14F27 has no OBJTYPE at all, so an empty type has to mean "mine if I have it" - which
        // is exactly what a provider must do to be composable.
        var results = await provider.ActAsync(
            new ObjectActionRequest { Objects = ["CJ-1"], Command = ControlJobCommands.Cancel },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(results).Acknowledgement);
        Assert.Equal(ControlJobCompletion.Cancelled, manager.Find("CJ-1")!.Completion);
    }

    // ----------------------------------------------------------------- creating

    [Fact]
    public async Task A_host_can_queue_a_job_by_creating_one()
    {
        var manager = new ControlJobManager();
        var provider = new ControlJobObjectProvider(manager);

        var result = await provider.CreateAsync(
            new ObjectCreateRequest
            {
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes =
                [
                    new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-9") },
                    new ObjectAttribute
                    {
                        Name = "CarrierInputSpec",
                        Value = SecsItem.L(SecsItem.A("CAR-001"), SecsItem.A("CAR-002")),
                    },
                    new ObjectAttribute { Name = "StartMethod", Value = SecsItem.A("HostInitiated") },
                    new ObjectAttribute { Name = "ProcessOrderMgmt", Value = SecsItem.A("FIFO") },
                    new ObjectAttribute { Name = "ProcessingCtrlSpec", Value = SecsItem.A("RECIPE-A") },
                ],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, result.Acknowledgement);
        Assert.Equal("CJ-9", result.ObjectId);

        var job = manager.Find("CJ-9");

        Assert.NotNull(job);
        Assert.Equal(ControlJobState.Queued, job.State);
        Assert.Equal(2, job.Carriers.Count);
        Assert.Equal(ControlJobStartMethod.HostInitiated, job.StartMethod);
        Assert.Equal("FIFO", job.ProcessOrder);
        Assert.Equal("RECIPE-A", job.ProcessingSpecification);
        Assert.Equal("CJ-9", Assert.Single(manager.Queue));
    }

    [Theory]
    [InlineData("ObjID")]
    [InlineData("ObjectID")]
    [InlineData("ControlJobID")]
    public async Task A_host_can_name_the_job_three_ways(string attribute)
    {
        var manager = new ControlJobManager();
        var provider = new ControlJobObjectProvider(manager);

        var result = await provider.CreateAsync(
            new ObjectCreateRequest
            {
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes = [new ObjectAttribute { Name = attribute, Value = SecsItem.A("CJ-9") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, result.Acknowledgement);
        Assert.NotNull(manager.Find("CJ-9"));
    }

    [Fact]
    public async Task A_job_nobody_named_is_named_by_the_machine()
    {
        var manager = new ControlJobManager();
        var provider = new ControlJobObjectProvider(manager);
        List<string> named = [];

        for (var index = 0; index < 3; index++)
        {
            var result = await provider.CreateAsync(
                new ObjectCreateRequest { ObjectType = ControlJobObjectProvider.ControlJobType },
                TestContext.Current.CancellationToken);

            Assert.Equal(ObjectAcknowledgement.Successful, result.Acknowledgement);
            named.Add(result.ObjectId);
        }

        // Which is what S14F6 carrying an OBJID back is for - and the names have to differ, or the
        // second create would collide with the first.
        Assert.Equal(3, named.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, manager.Jobs.Count);
        Assert.All(named, name => Assert.NotNull(manager.Find(name)));
    }

    [Fact]
    public async Task A_name_already_taken_is_refused()
    {
        var (manager, provider) = Loaded();

        var result = await provider.CreateAsync(
            new ObjectCreateRequest
            {
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes = [new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-1") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, result.Acknowledgement);
        Assert.Equal(string.Empty, result.ObjectId);

        // And the job that was already there is untouched, which is the fault worth guarding: a
        // create that overwrote would reset a running machine's queue entry.
        Assert.Equal("FIFO", manager.Find("CJ-1")!.ProcessOrder);
        Assert.Single(manager.Jobs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Carrier")]
    public async Task A_create_has_to_say_it_is_creating_a_control_job(string objectType)
    {
        var manager = new ControlJobManager();
        var provider = new ControlJobObjectProvider(manager);

        var result = await provider.CreateAsync(
            new ObjectCreateRequest { ObjectType = objectType },
            TestContext.Current.CancellationToken);

        // The one place an empty type is refused rather than taken as "mine": guessing would make
        // every unnamed create a control job on a machine that also does E87 and E90.
        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, result.Acknowledgement);
        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public async Task An_attribute_a_control_job_does_not_have_stops_the_create()
    {
        var manager = new ControlJobManager();
        var provider = new ControlJobObjectProvider(manager);

        var result = await provider.CreateAsync(
            new ObjectCreateRequest
            {
                ObjectType = ControlJobObjectProvider.ControlJobType,
                Attributes =
                [
                    new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-9") },
                    new ObjectAttribute { Name = "ProcessOrderMgt", Value = SecsItem.A("FIFO") },
                ],
            },
            TestContext.Current.CancellationToken);

        // A misspelt attribute name, which is the likeliest mistake there is here. A job created
        // from it would run with a default where the host asked for something, so nothing is
        // queued at all.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, result.Acknowledgement);
        Assert.Empty(manager.Jobs);
    }

    // ----------------------------------------------------------------- deleting

    [Fact]
    public async Task A_finished_job_can_be_deleted_over_the_wire()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.Command("CJ-1", ControlJobCommands.Cancel).IsAccepted);

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            await provider.DeleteAsync(
                new ObjectDeleteRequest
                {
                    ObjectSpecification = "CJ-1",
                    ObjectType = ControlJobObjectProvider.ControlJobType,
                },
                TestContext.Current.CancellationToken));

        Assert.Empty(manager.Jobs);
    }

    [Fact]
    public async Task A_job_that_is_not_over_is_not_deleted()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Error,
            await provider.DeleteAsync(
                new ObjectDeleteRequest { ObjectSpecification = "CJ-1" },
                TestContext.Current.CancellationToken));

        Assert.Single(manager.Jobs);
    }

    [Fact]
    public async Task A_delete_naming_no_job_and_one_naming_the_wrong_type_are_told_apart()
    {
        var (_, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.InvalidObjectInstance,
            await provider.DeleteAsync(
                new ObjectDeleteRequest { ObjectSpecification = "CJ-404" },
                TestContext.Current.CancellationToken));

        // Three of OBJACK's five codes say which part of the request was wrong, and this is the
        // one message where all of them can be used.
        Assert.Equal(
            ObjectAcknowledgement.InvalidObjectType,
            await provider.DeleteAsync(
                new ObjectDeleteRequest { ObjectSpecification = "CJ-1", ObjectType = "Carrier" },
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Null_requests_are_rejected()
    {
        var (_, provider) = Loaded();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.GetAttributesAsync(null!, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.SetAttributesAsync(null!, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.ActAsync(null!, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.CreateAsync(null!, TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await provider.DeleteAsync(null!, TestContext.Current.CancellationToken));

        Assert.Throws<ArgumentNullException>(() => new ControlJobObjectProvider(null!));
    }
}
