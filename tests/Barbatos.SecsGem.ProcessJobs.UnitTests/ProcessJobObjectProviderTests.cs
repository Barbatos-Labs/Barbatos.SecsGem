// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.ProcessJobs.UnitTests;

/// <summary>
/// The half of E40 that is stream 14: a job described, as against driven.
/// </summary>
/// <remarks>
/// S16F1 carries three items, so everything else a job has is an attribute - and every attribute
/// name here is a data item the catalogue defines, five of them with their code values. That makes
/// this the one provider in the stack whose vocabulary is quoted rather than read.
/// </remarks>
public class ProcessJobObjectProviderTests
{
    private const string Recipe = "RECIPE-7";

    private static (ProcessJobManager Manager, ProcessJobObjectProvider Provider) Loaded()
    {
        var manager = new ProcessJobManager();

        Assert.True(manager.Create("PJ-1", Recipe, ["W01"]).IsAccepted);

        return (manager, new ProcessJobObjectProvider(manager));
    }

    private static async Task<Dictionary<string, SecsItem>> ReadAsync(
        ProcessJobObjectProvider provider,
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
        ProcessJobObjectProvider provider,
        string specification,
        params (string Name, SecsItem Value)[] attributes)
        => provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = [specification],
                ObjectType = ProcessJobObjectProvider.ProcessJobType,
                Attributes = [.. attributes.Select(pair => new ObjectAttribute
                {
                    Name = pair.Name,
                    Value = pair.Value,
                })],
            },
            TestContext.Current.CancellationToken);

    // ------------------------------------------------------------- the namelists

    [Fact]
    public async Task The_process_job_type_is_there_whether_or_not_a_job_is()
    {
        var provider = new ProcessJobObjectProvider(new ProcessJobManager());

        Assert.Equal(
            ProcessJobObjectProvider.ProcessJobType,
            Assert.Single(await provider.GetObjectTypesAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task Every_attribute_name_is_a_data_item_the_catalogue_defines()
    {
        var (_, provider) = Loaded();

        var names = await provider.GetAttributeNamesAsync(
            string.Empty,
            ProcessJobObjectProvider.ProcessJobType,
            TestContext.Current.CancellationToken);

        // Asserted against literals on purpose: these seven are the dictionary's own spellings,
        // and that is the claim this provider makes that E87's, E90's and E94's cannot.
        Assert.Equal(
            ["PRJOBID", "PRSTATE", "PPID", "PRRECIPEMETHOD", "PRPROCESSSTART", "PRMTRLORDER", "PRPAUSEEVENTID"],
            names);

        // By instance as well as by type, so a host can ask either way round.
        Assert.Equal(
            names,
            await provider.GetAttributeNamesAsync("PJ-1", string.Empty, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Somebody_elses_object_type_gets_nothing()
    {
        var (_, provider) = Loaded();

        Assert.Empty(await provider.GetInstancesAsync("Carrier", TestContext.Current.CancellationToken));

        Assert.Empty(await provider.GetAttributeNamesAsync(
            string.Empty,
            "Carrier",
            TestContext.Current.CancellationToken));

        Assert.Empty(await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["PJ-1"], ObjectType = "Substrate" },
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_instances_are_the_jobs_the_manager_has()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.Create("PJ-2", Recipe).IsAccepted);

        Assert.Equal(
            2,
            (await provider.GetInstancesAsync(
                ProcessJobObjectProvider.ProcessJobType,
                TestContext.Current.CancellationToken)).Count);
    }

    // ------------------------------------------------------------------ reading

    [Fact]
    public async Task A_job_reads_as_the_formats_the_catalogue_gives_its_items()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.Describe(
            "PJ-1",
            recipeMethod: ProcessJobRecipeMethod.WithVariableTuning,
            startsAutomatically: false,
            materialOrder: ProcessJobMaterialOrder.MaximizeThroughput).IsAccepted);

        var read = await ReadAsync(provider, "PJ-1");

        Assert.Equal("PJ-1", read["PRJOBID"].GetString());
        Assert.Equal(SecsFormat.ASCII, read["PPID"].Format);

        // The state as a number, because PRSTATE is U1 with ten code values: a name here would be
        // this library's spelling of something the catalogue already numbers.
        Assert.Equal(SecsFormat.U1, read["PRSTATE"].Format);
        Assert.Equal((byte)ProcessJobState.Queued, read["PRSTATE"].FirstValueOrDefault<byte>());

        Assert.Equal(SecsFormat.U1, read["PRRECIPEMETHOD"].Format);
        Assert.Equal(2, read["PRRECIPEMETHOD"].FirstValueOrDefault<byte>());
        Assert.Equal(SecsFormat.Boolean, read["PRPROCESSSTART"].Format);
        Assert.False(read["PRPROCESSSTART"].FirstValueOrDefault<bool>());
        Assert.Equal(SecsFormat.U1, read["PRMTRLORDER"].Format);
        Assert.Equal(2, read["PRMTRLORDER"].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_job_with_no_pause_event_says_so_with_a_zero_length_item()
    {
        var (manager, provider) = Loaded();

        var none = await ReadAsync(provider, "PJ-1", attributes: ["PRPAUSEEVENTID"]);

        // Not event zero, which is a CEID somebody could have defined.
        Assert.Equal(SecsFormat.U4, none["PRPAUSEEVENTID"].Format);
        Assert.Equal(0, none["PRPAUSEEVENTID"].Count);

        Assert.True(manager.SetPauseEvent("PJ-1", 2_500).IsAccepted);

        var set = await ReadAsync(provider, "PJ-1", attributes: ["PRPAUSEEVENTID"]);

        Assert.Equal(2_500u, set["PRPAUSEEVENTID"].FirstValueOrDefault<uint>());
    }

    [Fact]
    public async Task A_read_of_named_attributes_gives_those_and_no_others()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, "PJ-1", attributes: ["PRSTATE", "SlotMap"]);

        // And an attribute a process job does not have is left out rather than answered empty.
        Assert.Equal("PRSTATE", Assert.Single(read).Key);
    }

    [Fact]
    public async Task A_job_nobody_created_is_left_out_of_the_answer()
    {
        var (_, provider) = Loaded();

        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["PJ-1", "PJ-404"] },
            TestContext.Current.CancellationToken);

        Assert.Equal("PJ-1", Assert.Single(values).ObjectSpecification);
    }

    // ------------------------------------------------------------------ writing

    [Fact]
    public async Task The_five_settable_attributes_go_through_the_manager()
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(
            provider,
            "PJ-1",
            ("PPID", SecsItem.A("RECIPE-9")),
            ("PRRECIPEMETHOD", SecsItem.U1(2)),
            ("PRPROCESSSTART", SecsItem.Boolean(false)),
            ("PRMTRLORDER", SecsItem.U1(3)),
            ("PRPAUSEEVENTID", SecsItem.U4(2_500u)));

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(results).Acknowledgement);

        var job = manager.Find("PJ-1");

        Assert.NotNull(job);
        Assert.Equal("RECIPE-9", job.RecipeId);
        Assert.Equal(ProcessJobRecipeMethod.WithVariableTuning, job.RecipeMethod);
        Assert.False(job.StartsAutomatically);
        Assert.Equal(ProcessJobMaterialOrder.ListedSequence, job.MaterialOrder);
        Assert.Equal(2_500u, job.PauseEventId);
    }

    [Theory]
    [InlineData("PRSTATE")]
    [InlineData("PRJOBID")]
    [InlineData("SlotMap")]
    public async Task Nothing_else_is_settable(string attribute)
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(provider, "PJ-1", (attribute, SecsItem.A("anything")));

        // The refusal every provider in this stack makes: a host that could write the state would
        // start a job by saying it had started.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        Assert.Equal(ProcessJobState.Queued, manager.Find("PJ-1")!.State);
    }

    [Fact]
    public async Task One_read_only_attribute_stops_the_whole_set()
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(
            provider,
            "PJ-1",
            ("PPID", SecsItem.A("RECIPE-9")),
            ("PRSTATE", SecsItem.U1(3)));

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);

        // Checked before anything is written: a set that half applied would leave a host with no
        // way of knowing what took.
        Assert.Equal(Recipe, manager.Find("PJ-1")!.RecipeId);
    }

    [Fact]
    public async Task A_set_on_a_job_the_machine_has_taken_up_is_refused()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);

        var results = await SetAsync(provider, "PJ-1", ("PPID", SecsItem.A("RECIPE-9")));

        // Through the manager, so the state machine refuses it here exactly as it would anywhere
        // else - and Error, because OBJACK has no code for a state.
        Assert.Equal(ObjectAcknowledgement.Error, Assert.Single(results).Acknowledgement);
        Assert.Equal(Recipe, manager.Find("PJ-1")!.RecipeId);
    }

    [Fact]
    public async Task A_pause_event_can_still_be_set_while_the_job_runs()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);

        // The one attribute that is not part of the description: a pause event is a reaction, and
        // a host that notices something going wrong mid-run is exactly who sets one.
        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRPAUSEEVENTID", SecsItem.U4(7u)))).Acknowledgement);

        Assert.Equal(7u, manager.Find("PJ-1")!.PauseEventId);

        // And a zero-length item clears it, which is how this stack says "no value" everywhere.
        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRPAUSEEVENTID", SecsItem.U4()))).Acknowledgement);

        Assert.Null(manager.Find("PJ-1")!.PauseEventId);
    }

    [Fact]
    public async Task A_set_on_a_job_nobody_created_is_left_out()
    {
        var (_, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["PJ-404"],
                ObjectType = ProcessJobObjectProvider.ProcessJobType,
                Attributes = [new ObjectAttribute { Name = "PPID", Value = SecsItem.A("RECIPE-9") }],
            },
            TestContext.Current.CancellationToken);

        // Left out rather than refused, so object services can report it as
        // InvalidObjectInstance and the other packages on the link keep answering for their own.
        Assert.Empty(results);
    }

    // ---------------------------------------------------------- tolerant reading

    [Theory]
    [InlineData(2, ProcessJobRecipeMethod.WithVariableTuning)]
    [InlineData(1, ProcessJobRecipeMethod.WithoutVariableTuning)]
    [InlineData(9, ProcessJobRecipeMethod.WithoutVariableTuning)]
    public async Task A_recipe_method_is_read_from_any_numeric_format(byte sent, ProcessJobRecipeMethod expected)
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRRECIPEMETHOD", SecsItem.U4(sent)))).Acknowledgement);

        // A code the catalogue does not define reads as the one that needs nothing of the machine,
        // which is the safe answer rather than the favourable one.
        Assert.Equal(expected, manager.Find("PJ-1")!.RecipeMethod);
    }

    [Fact]
    public async Task A_recipe_method_is_read_from_text_too()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRRECIPEMETHOD", SecsItem.A("2")))).Acknowledgement);

        Assert.Equal(ProcessJobRecipeMethod.WithVariableTuning, manager.Find("PJ-1")!.RecipeMethod);
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public async Task A_process_start_flag_is_read_from_text(string sent, bool expected)
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRPROCESSSTART", SecsItem.A(sent)))).Acknowledgement);

        // The item's one defined thing is its format, so a machine that sends a word or a digit
        // where a BOOLEAN was expected is still understood.
        Assert.Equal(expected, manager.Find("PJ-1")!.StartsAutomatically);
    }

    [Fact]
    public async Task A_process_start_flag_is_read_from_a_number_as_well_as_a_boolean()
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRPROCESSSTART", SecsItem.U1(0)))).Acknowledgement);

        Assert.False(manager.Find("PJ-1")!.StartsAutomatically);

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRPROCESSSTART", SecsItem.Boolean(true)))).Acknowledgement);

        Assert.True(manager.Find("PJ-1")!.StartsAutomatically);
    }

    [Theory]
    [InlineData(1, ProcessJobMaterialOrder.ArrivalSequence)]
    [InlineData(2, ProcessJobMaterialOrder.MaximizeThroughput)]
    [InlineData(3, ProcessJobMaterialOrder.ListedSequence)]
    [InlineData(0, ProcessJobMaterialOrder.ArrivalSequence)]
    [InlineData(7, ProcessJobMaterialOrder.ArrivalSequence)]
    public async Task A_material_order_is_read_from_its_code(byte sent, ProcessJobMaterialOrder expected)
    {
        var (manager, provider) = Loaded();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            Assert.Single(await SetAsync(provider, "PJ-1", ("PRMTRLORDER", SecsItem.U1(sent)))).Acknowledgement);

        Assert.Equal(expected, manager.Find("PJ-1")!.MaterialOrder);
    }

    [Fact]
    public async Task An_empty_recipe_is_refused_through_the_provider_as_well()
    {
        var (manager, provider) = Loaded();

        var results = await SetAsync(provider, "PJ-1", ("PPID", SecsItem.A(string.Empty)));

        // A job cannot be emptied of the thing it exists to run, however the emptying arrives.
        Assert.Equal(ObjectAcknowledgement.Error, Assert.Single(results).Acknowledgement);
        Assert.Equal(Recipe, manager.Find("PJ-1")!.RecipeId);
    }

    // ------------------------------------------------------------------- acting

    [Fact]
    public async Task A_command_reaches_the_state_machine_and_a_refusal_comes_back_as_error()
    {
        var (manager, provider) = Loaded();

        Assert.True(manager.BeginSetup("PJ-1").IsAccepted);
        Assert.True(manager.SetupComplete("PJ-1").IsAccepted);

        var acted = await provider.ActAsync(
            new ObjectActionRequest
            {
                Objects = ["PJ-1"],
                ObjectType = ProcessJobObjectProvider.ProcessJobType,
                Command = ProcessJobCommands.Pause,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(acted).Acknowledgement);
        Assert.Equal(ProcessJobState.Pausing, manager.Find("PJ-1")!.State);

        var refused = await provider.ActAsync(
            new ObjectActionRequest
            {
                Objects = ["PJ-1"],
                ObjectType = ProcessJobObjectProvider.ProcessJobType,
                Command = ProcessJobCommands.Resume,
            },
            TestContext.Current.CancellationToken);

        // One code for every no, which is why a host that can send S16F3 should.
        Assert.Equal(ObjectAcknowledgement.Error, Assert.Single(refused).Acknowledgement);
    }

    [Fact]
    public async Task An_action_answers_for_the_jobs_it_has_and_leaves_out_the_rest()
    {
        var (_, provider) = Loaded();

        var acted = await provider.ActAsync(
            new ObjectActionRequest { Objects = ["PJ-1", "CAR-001"], Command = ProcessJobCommands.Cancel },
            TestContext.Current.CancellationToken);

        Assert.Equal("PJ-1", Assert.Single(acted).ObjectSpecification);
    }

    [Fact]
    public async Task An_action_for_somebody_elses_type_answers_nothing()
    {
        var (manager, provider) = Loaded();

        Assert.Empty(await provider.ActAsync(
            new ObjectActionRequest
            {
                Objects = ["PJ-1"],
                ObjectType = "ControlJob",
                Command = ProcessJobCommands.Cancel,
            },
            TestContext.Current.CancellationToken));

        Assert.Equal(ProcessJobState.Queued, manager.Find("PJ-1")!.State);
    }

    // -------------------------------------------------------- create and delete

    [Fact]
    public async Task A_create_and_a_delete_are_refused_because_E40_has_its_own()
    {
        var (manager, concrete) = Loaded();

        // Through the interface, because that is the only way to reach them: they are default
        // interface methods, and this provider overrides neither - which is itself the test.
        IObjectProvider provider = concrete;

        var created = await provider.CreateAsync(
            new ObjectCreateRequest { ObjectType = ProcessJobObjectProvider.ProcessJobType },
            TestContext.Current.CancellationToken);

        // The default refusals of IObjectProvider, left in place on purpose: S16F1 insists on a
        // recipe and S16F9 keeps the record straight, so a create or a delete over stream 14 would
        // be a second way to do something E40 already does properly.
        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, created.Acknowledgement);
        Assert.Single(manager.Jobs);

        Assert.Equal(
            ObjectAcknowledgement.InvalidObjectType,
            await provider.DeleteAsync(
                new ObjectDeleteRequest { ObjectSpecification = "PJ-1" },
                TestContext.Current.CancellationToken));

        Assert.Single(manager.Jobs);
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

        Assert.Throws<ArgumentNullException>(() => new ProcessJobObjectProvider(null!));
    }
}
