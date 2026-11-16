// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// Stream 3 across a real link, with a real equipment at one end and a real host at the other.
/// </summary>
/// <remarks>
/// Both directions are exercised here, because stream 3 runs in both: the host asks five questions
/// and the equipment reports four things unasked. A field that survives a round trip has been
/// written and read by the two halves of the codec independently, which given how little of this
/// stream the catalogue pins down is the only claim worth making.
/// </remarks>
public class MaterialTests
{
    // ------------------------------------------------------- what the host asks

    [Fact]
    public async Task Asking_what_the_machine_holds_brings_back_every_piece_and_where_it_is()
    {
        var source = new MaterialInMemorySource();
        source.Load("LOT-A", location: 3, quantity: 25);
        source.Load("LOT-B", location: 7, quantity: 1);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        var material = await pair.Host.RequestStatusAsync(MaterialPair.Token);

        Assert.Equal(2, material.Count);

        var a = Assert.Single(material, m => m.MaterialId == "LOT-A");
        Assert.Equal(3u, a.Location);
        Assert.Equal(25, a.Quantity);

        var b = Assert.Single(material, m => m.MaterialId == "LOT-B");
        Assert.Equal(7u, b.Location);
        Assert.Equal(1, b.Quantity);
    }

    [Fact]
    public async Task An_empty_machine_answers_an_empty_list_rather_than_a_failure()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var material = await pair.Host.RequestStatusAsync(MaterialPair.Token);

        // A machine holding nothing is a normal state, not a fault. S3F2 with L,0 says so.
        Assert.Empty(material);
    }

    [Fact]
    public async Task Asking_how_long_material_has_left_brings_back_the_number_the_machine_sent()
    {
        var source = new MaterialInMemorySource();
        source.Load("LOT-A", location: 3, quantity: 25, timeToCompletion: 1_517);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        var material = await pair.Host.RequestTimeToCompletionAsync(MaterialPair.Token);

        // Not converted to a TimeSpan anywhere, because the catalogue gives TTC no unit: 1,517 of
        // whatever the machine counts in arrives as 1,517.
        var only = Assert.Single(material);
        Assert.Equal("LOT-A", only.MaterialId);
        Assert.Equal(3u, only.Location);
        Assert.Equal(1_517u, only.TimeToCompletion);
    }

    [Fact]
    public async Task Finding_material_the_machine_has_gives_its_location()
    {
        var source = new MaterialInMemorySource();
        source.Load("LOT-A", location: 42);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        var found = await pair.Host.FindMaterialAsync("LOT-A", MaterialPair.Token);

        Assert.NotNull(found);
        Assert.Equal("LOT-A", found.MaterialId);
        Assert.Equal(42u, found.Location);
    }

    [Fact]
    public async Task Finding_material_the_machine_does_not_have_is_null_rather_than_a_failure()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        // S3F10 carries no acknowledgement code, so E5 leaves only the zero-length list to mean
        // "not here" - and null, not an exception, is what that should surface as.
        Assert.Null(await pair.Host.FindMaterialAsync("nobody", MaterialPair.Token));
    }

    [Fact]
    public async Task An_accepted_transfer_moves_the_material_and_a_later_question_shows_it()
    {
        var source = new MaterialInMemorySource();
        source.Load("LOT-A", location: 1);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        var acknowledgement = await pair.Host.RequestTransferAsync(
            new MaterialTransfer { MaterialId = "LOT-A", Destination = 9 },
            MaterialPair.Token);

        Assert.Equal(MaterialAcknowledgement.Accepted, acknowledgement);

        // Two messages agreeing with each other, which is the point: DESTLOC went out on S3F11 and
        // came back as LOC on S3F10, through both halves of the codec and the inference about
        // DESTLOC that this package had to make.
        var found = await pair.Host.FindMaterialAsync("LOT-A", MaterialPair.Token);

        Assert.NotNull(found);
        Assert.Equal(9u, found.Location);
    }

    [Fact]
    public async Task A_transfer_of_material_the_machine_does_not_have_is_denied()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        var acknowledgement = await pair.Host.RequestTransferAsync(
            new MaterialTransfer { MaterialId = "nobody", Destination = 9 },
            MaterialPair.Token);

        // Accepted here would have the host waiting for an S3F35 that can never arrive.
        Assert.Equal(MaterialAcknowledgement.Denied, acknowledgement);
    }

    [Fact]
    public async Task A_denied_transfer_leaves_the_material_where_it_was()
    {
        var source = new MaterialInMemorySource
        {
            TransferAnswer = MaterialAcknowledgement.Denied,
        };

        source.Load("LOT-A", location: 1);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        Assert.Equal(
            MaterialAcknowledgement.Denied,
            await pair.Host.RequestTransferAsync(
                new MaterialTransfer { MaterialId = "LOT-A", Destination = 9 },
                MaterialPair.Token));

        var found = await pair.Host.FindMaterialAsync("LOT-A", MaterialPair.Token);

        Assert.NotNull(found);
        Assert.Equal(1u, found.Location);
    }

    [Fact]
    public async Task A_multi_block_inquire_is_granted()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            MaterialGrant.Accepted,
            await pair.Host.InquireMultiBlockAsync(dataId: 1_517, cancellationToken: MaterialPair.Token));
    }

    [Fact]
    public async Task A_machine_with_no_space_refuses_the_block_before_it_is_sent()
    {
        var source = new MaterialInMemorySource { Grant = MaterialGrant.NoSpace };

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        Assert.Equal(
            MaterialGrant.NoSpace,
            await pair.Host.InquireMultiBlockAsync(dataId: 1, cancellationToken: MaterialPair.Token));
    }

    // -------------------------------------------- what the equipment says unasked

    [Fact]
    public async Task Material_found_reaches_the_host_and_is_acknowledged()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialLocationEventArgs? seen = null;
        pair.Host.MaterialFound += (_, args) => seen = args;

        var acknowledgement = await pair.Equipment.ReportFoundAsync(
            new MaterialLocation { MaterialId = "LOT-C", Location = 4 },
            MaterialPair.Token);

        Assert.Equal(MaterialAcknowledgement.Accepted, acknowledgement);
        Assert.NotNull(seen);
        Assert.Equal("LOT-C", seen.Material.MaterialId);
        Assert.Equal(4u, seen.Material.Location);
    }

    [Fact]
    public async Task Material_lost_reaches_the_host_with_where_it_was_last_seen()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialLocationEventArgs? seen = null;
        pair.Host.MaterialLost += (_, args) => seen = args;

        await pair.Equipment.ReportLostAsync(
            new MaterialLocation { MaterialId = "LOT-D", Location = 11 },
            MaterialPair.Token);

        Assert.NotNull(seen);
        Assert.Equal("LOT-D", seen.Material.MaterialId);
        Assert.Equal(11u, seen.Material.Location);
    }

    [Fact]
    public async Task A_status_change_reaches_the_host_with_the_status_byte_intact()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialStatusChangeEventArgs? seen = null;
        pair.Host.MaterialStatusChanged += (_, args) => seen = args;

        await pair.Equipment.ReportStatusChangeAsync(
            new MaterialStatusChange { MaterialId = "LOT-E", Location = 2, Status = 200 },
            MaterialPair.Token);

        // MATLSTATUS has no dictionary entry at all, so this is the round trip of the weakest
        // inference in the package: written B[1], read back as the same 200.
        Assert.NotNull(seen);
        Assert.Equal("LOT-E", seen.Change.MaterialId);
        Assert.Equal(2u, seen.Change.Location);
        Assert.Equal(200, seen.Change.Status);
    }

    [Fact]
    public async Task A_movement_event_reaches_the_host_with_both_locations()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialMovementEventArgs? seen = null;
        pair.Host.MaterialMoved += (_, args) => seen = args;

        await pair.Equipment.ReportMovementAsync(
            new MaterialMovement { MaterialId = "LOT-F", Location = 1, Destination = 99 },
            MaterialPair.Token);

        // LOC and DESTLOC in one body, which is the message that makes the DESTLOC inference
        // testable: if the two were written in the wrong order or the wrong format, 1 and 99 would
        // not come back as 1 and 99.
        Assert.NotNull(seen);
        Assert.Equal(1u, seen.Movement.Location);
        Assert.Equal(99u, seen.Movement.Destination);
    }

    [Fact]
    public async Task A_host_that_denies_a_report_sends_the_denial_back()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        pair.Host.MaterialFound += (_, args) => args.Acknowledgement = MaterialAcknowledgement.Denied;

        var acknowledgement = await pair.Equipment.ReportFoundAsync(
            new MaterialLocation { MaterialId = "LOT-C", Location = 4 },
            MaterialPair.Token);

        // The acknowledgement is the host's decision, not the library's.
        Assert.Equal(MaterialAcknowledgement.Denied, acknowledgement);
    }

    [Fact]
    public async Task A_host_subscribed_to_nothing_still_acknowledges()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        // No handler attached to anything. An application that only wants to ask questions should
        // not have to answer four events to keep a machine from timing out.
        Assert.Equal(
            MaterialAcknowledgement.Accepted,
            await pair.Equipment.ReportFoundAsync(
                new MaterialLocation { MaterialId = "LOT-C", Location = 4 },
                MaterialPair.Token));

        Assert.Equal(
            MaterialAcknowledgement.Accepted,
            await pair.Equipment.ReportMovementAsync(
                new MaterialMovement { MaterialId = "LOT-C", Location = 4, Destination = 5 },
                MaterialPair.Token));
    }

    // ------------------------------------------------------------- completeness

    [Fact]
    public async Task Each_end_can_send_every_primary_the_other_declares()
    {
        var source = new MaterialInMemorySource();
        source.Load("LOT-A", location: 1);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        await pair.Host.RequestStatusAsync(MaterialPair.Token);
        await pair.Host.RequestTimeToCompletionAsync(MaterialPair.Token);
        await pair.Host.FindMaterialAsync("LOT-A", MaterialPair.Token);
        await pair.Host.RequestTransferAsync(new MaterialTransfer { MaterialId = "LOT-A", Destination = 2 }, MaterialPair.Token);
        await pair.Host.InquireMultiBlockAsync(1, MaterialPair.Token);

        await pair.Equipment.ReportFoundAsync(new MaterialLocation { MaterialId = "LOT-A", Location = 2 }, MaterialPair.Token);
        await pair.Equipment.ReportLostAsync(new MaterialLocation { MaterialId = "LOT-A", Location = 2 }, MaterialPair.Token);
        await pair.Equipment.ReportStatusChangeAsync(new MaterialStatusChange { MaterialId = "LOT-A", Location = 2, Status = 1 }, MaterialPair.Token);
        await pair.Equipment.ReportMovementAsync(new MaterialMovement { MaterialId = "LOT-A", Location = 2, Destination = 3 }, MaterialPair.Token);

        // The claim this test exists for, in both directions: the five the equipment declares are
        // the five the host can send, and the four the host declares are the four the equipment
        // can send. A method added to one end without the other shows up here.
        foreach (var pairOfNumbers in MaterialEquipment.HandledMessages)
        {
            Assert.Contains(pairOfNumbers, pair.AskedOfEquipment);
        }

        foreach (var pairOfNumbers in MaterialHost.HandledMessages)
        {
            Assert.Contains(pairOfNumbers, pair.AskedOfHost);
        }
    }
}
