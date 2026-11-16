// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.ControlJobs.UnitTests;

/// <summary>
/// A host running a control job over a link, with nothing but stream 14 between the two ends.
/// </summary>
/// <remarks>
/// The claim this package is built on, tested rather than asserted: E94 needs no message set of
/// its own, because S14F5, S14F9, S14F1 and S14F7 are enough to queue a job, drive it, watch it
/// and forget it. These tests go through a real connection, a real codec and a real router.
/// </remarks>
public class ControlJobWireTests
{
    [Fact]
    public async Task A_host_runs_a_whole_control_job_over_stream_14_alone()
    {
        await using var pair = ControlJobPair.Start();

        await pair.OpenAsync();

        // Queue it - S14F5.
        var created = await pair.Host.CreateAsync(
            ControlJobObjectProvider.ControlJobType,
            [
                new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-1") },
                new ObjectAttribute
                {
                    Name = "CarrierInputSpec",
                    Value = SecsItem.L(SecsItem.A("CAR-001"), SecsItem.A("CAR-002")),
                },
                new ObjectAttribute { Name = "StartMethod", Value = SecsItem.A("HostInitiated") },
                new ObjectAttribute { Name = "ProcessingCtrlSpec", Value = SecsItem.A("RECIPE-A") },
            ],
            ControlJobPair.Token);

        Assert.Equal(ObjectAcknowledgement.Successful, created.Acknowledgement);
        Assert.Equal("CJ-1", created.ObjectId);
        Assert.Equal(ControlJobState.Queued, pair.Job().State);
        Assert.Equal(2, pair.Job().Carriers.Count);

        // Choose it - S14F9.
        Assert.Equal(
            ObjectAcknowledgement.Successful,
            await pair.Host.ActAsync(
                "CJ-1",
                ControlJobObjectProvider.ControlJobType,
                ControlJobCommands.Select,
                cancellationToken: ControlJobPair.Token));

        Assert.Equal(ControlJobState.Selected, pair.Job().State);

        // The machine's own transition, which no host command can stand in for: the carriers have
        // arrived. A host-initiated job then waits.
        Assert.True(pair.Manager.MaterialReady("CJ-1").IsAccepted);
        Assert.Equal(ControlJobState.WaitingForStart, pair.Job().State);

        // Read it - S14F1 - and the state is the one the machine is in, not the one the host asked
        // for.
        var state = string.Empty;

        Assert.True(await pair.Host.GetAttributesAsync(
            "CJ-1",
            ControlJobObjectProvider.ControlJobType,
            ["State", "ProcessingCtrlSpec"],
            values =>
            {
                foreach (var attribute in values.Attributes)
                {
                    if (attribute.Name is "State")
                    {
                        state = attribute.Value.GetString();
                    }
                }
            },
            ControlJobPair.Token));

        Assert.Equal(nameof(ControlJobState.WaitingForStart), state);

        // Start it, pause it, let it carry on - three more S14F9s.
        foreach (var command in new[]
        {
            ControlJobCommands.Start,
            ControlJobCommands.Pause,
            ControlJobCommands.Resume,
        })
        {
            Assert.Equal(
                ObjectAcknowledgement.Successful,
                await pair.Host.ActAsync(
                    "CJ-1",
                    ControlJobObjectProvider.ControlJobType,
                    command,
                    cancellationToken: ControlJobPair.Token));
        }

        Assert.Equal(ControlJobState.Executing, pair.Job().State);

        // The machine finishes the material, and the host reads how it ended.
        Assert.True(pair.Manager.MaterialFinished("CJ-1").IsAccepted);

        var completion = string.Empty;

        Assert.True(await pair.Host.GetAttributesAsync(
            "CJ-1",
            ControlJobObjectProvider.ControlJobType,
            ["Completion"],
            values => completion = Assert.Single(values.Attributes).Value.GetString(),
            ControlJobPair.Token));

        Assert.Equal(nameof(ControlJobCompletion.Completed), completion);

        // Forget it - S14F7.
        Assert.Equal(
            ObjectAcknowledgement.Successful,
            await pair.Host.DeleteAsync("CJ-1", ControlJobObjectProvider.ControlJobType, ControlJobPair.Token));

        Assert.Empty(pair.Manager.Jobs);

        // Four functions and their replies, which is the whole of E94 on a wire.
        pair.AssertAsked(14, 5);
        pair.AssertAsked(14, 9);
        pair.AssertAsked(14, 1);
        pair.AssertAsked(14, 7);
    }

    [Fact]
    public async Task A_command_the_machine_refuses_comes_back_as_a_refusal_and_not_a_timeout()
    {
        await using var pair = ControlJobPair.Start(manager =>
            Assert.True(manager.Create("CJ-1").IsAccepted));

        await pair.OpenAsync();

        var acknowledgement = await pair.Host.ActAsync(
            "CJ-1",
            ControlJobObjectProvider.ControlJobType,
            ControlJobCommands.Pause,
            cancellationToken: ControlJobPair.Token);

        // The part a link can break: a refusal has to survive being turned into an OBJACK and read
        // back, rather than becoming an exception in a handler and a T3 timeout at the host.
        Assert.Equal(ObjectAcknowledgement.Error, acknowledgement);
        Assert.Equal(ControlJobState.Queued, pair.Job().State);
    }

    [Fact]
    public async Task A_host_can_change_a_queued_job_and_is_refused_once_it_is_selected()
    {
        await using var pair = ControlJobPair.Start(manager =>
            Assert.True(manager.Create("CJ-1", ["CAR-001"]).IsAccepted));

        await pair.OpenAsync();

        Assert.Equal(
            ObjectAcknowledgement.Successful,
            await pair.Host.SetAttributesAsync(
                "CJ-1",
                ControlJobObjectProvider.ControlJobType,
                [new ObjectAttribute { Name = "ProcessingCtrlSpec", Value = SecsItem.A("RECIPE-B") }],
                ControlJobPair.Token));

        Assert.Equal("RECIPE-B", pair.Job().ProcessingSpecification);

        Assert.True(pair.Manager.Select("CJ-1").IsAccepted);

        Assert.Equal(
            ObjectAcknowledgement.Error,
            await pair.Host.SetAttributesAsync(
                "CJ-1",
                ControlJobObjectProvider.ControlJobType,
                [new ObjectAttribute { Name = "ProcessingCtrlSpec", Value = SecsItem.A("RECIPE-C") }],
                ControlJobPair.Token));

        // The recipe under a job whose carriers are being brought in is not a thing a host may
        // change, and the refusal reaches it rather than being silently dropped.
        Assert.Equal("RECIPE-B", pair.Job().ProcessingSpecification);
    }

    [Fact]
    public async Task A_state_a_host_tries_to_assign_is_refused_over_the_wire_too()
    {
        await using var pair = ControlJobPair.Start(manager =>
            Assert.True(manager.Create("CJ-1").IsAccepted));

        await pair.OpenAsync();

        Assert.Equal(
            ObjectAcknowledgement.InvalidAttribute,
            await pair.Host.SetAttributesAsync(
                "CJ-1",
                ControlJobObjectProvider.ControlJobType,
                [new ObjectAttribute { Name = "State", Value = SecsItem.A(nameof(ControlJobState.Executing)) }],
                ControlJobPair.Token));

        // The refusal this package is built around, and the one most worth proving end to end: a
        // host that could write State would start a job by saying it had started.
        Assert.Equal(ControlJobState.Queued, pair.Job().State);
    }

    [Fact]
    public async Task A_host_can_command_several_jobs_at_once()
    {
        await using var pair = ControlJobPair.Start(manager =>
        {
            Assert.True(manager.Create("CJ-1").IsAccepted);
            Assert.True(manager.Create("CJ-2").IsAccepted);
            Assert.True(manager.Select("CJ-1").IsAccepted);
        });

        await pair.OpenAsync();

        var results = await pair.Host.ActAsync(
            ["CJ-1", "CJ-2", "CJ-404"],
            ControlJobCommands.Cancel,
            cancellationToken: ControlJobPair.Token);

        // S14F27, the multi-object action, which carries no OBJTYPE at all - and the one object
        // nobody created comes back as InvalidObjectInstance from object services rather than
        // being quietly missing from the reply.
        Assert.Equal(3, results.Count);

        Dictionary<string, ObjectAcknowledgement> byObject = [];

        foreach (var result in results)
        {
            byObject[result.ObjectSpecification] = result.Acknowledgement;
        }

        Assert.Equal(ObjectAcknowledgement.Successful, byObject["CJ-1"]);
        Assert.Equal(ObjectAcknowledgement.Successful, byObject["CJ-2"]);
        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, byObject["CJ-404"]);

        Assert.Equal(ControlJobCompletion.Cancelled, pair.Job("CJ-1").Completion);
        Assert.Equal(ControlJobCompletion.Cancelled, pair.Job("CJ-2").Completion);
        pair.AssertAsked(14, 27);
    }

    [Fact]
    public async Task A_host_discovers_the_jobs_and_their_attributes_rather_than_assuming_them()
    {
        await using var pair = ControlJobPair.Start(manager =>
            Assert.True(manager.Create("CJ-1").IsAccepted));

        await pair.OpenAsync();

        Assert.Equal(
            ControlJobObjectProvider.ControlJobType,
            Assert.Single(await pair.Host.GetObjectTypesAsync(ControlJobPair.Token)));

        Assert.Equal(
            "CJ-1",
            Assert.Single(await pair.Host.GetInstancesAsync(
                ControlJobObjectProvider.ControlJobType,
                ControlJobPair.Token)));

        var names = await pair.Host.GetAttributeNamesAsync(
            "CJ-1",
            ControlJobObjectProvider.ControlJobType,
            ControlJobPair.Token);

        // S14F13, S14F15 and S14F17: with E94's attribute names unverifiable against anything in
        // this repository, a host asking for them is the only honest way to find out what a given
        // machine calls them.
        Assert.Contains("State", names);
        Assert.Contains("StartMethod", names);
        pair.AssertAsked(14, 13);
        pair.AssertAsked(14, 15);
        pair.AssertAsked(14, 17);
    }

    [Fact]
    public async Task A_create_for_a_type_the_machine_does_not_have_is_refused_rather_than_ignored()
    {
        await using var pair = ControlJobPair.Start();

        await pair.OpenAsync();

        var result = await pair.Host.CreateAsync("Chamber", cancellationToken: ControlJobPair.Token);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, result.Acknowledgement);
        Assert.Empty(pair.Manager.Jobs);
    }
}
