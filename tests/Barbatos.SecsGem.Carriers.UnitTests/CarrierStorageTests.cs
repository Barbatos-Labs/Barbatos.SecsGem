// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// Storage locations - the places inside a machine that are not load ports - and S3F31/F32.
/// </summary>
/// <remarks>
/// <para>
/// S3F31 is the one request in E87's half of stream 3 that names nothing, which is what made it
/// the last of the nine to be implemented: <em>tell me about all of your storage locations</em>
/// needs somewhere for a carrier to be that is not a port, and that is a model rather than a
/// message. So most of what is tested here is the model - a location has one state machine, it
/// keeps a port's rules about loading and unloading, and a carrier is in one place at a time.
/// </para>
/// <para>
/// Two tests carry more weight than the rest, and they are the same shape as the two the
/// S3F22/S3F28 chapter needed: <c>S3F32_and_S3F24_agree_on_where_a_carrier_is</c>, because this
/// package answers both out of one model and a host that got two numbers would have to pick one;
/// and <c>S3F32_and_S3F22_say_the_same_thing_about_the_same_state</c>, because <c>PORTSTATUS</c>
/// has no dictionary entry and the only ground for reading it in S3F32 is that it is the same item
/// name S3F22 carries.
/// </para>
/// </remarks>
public class CarrierStorageTests
{
    private const uint Shelf = 101;

    /// <summary>A manager with one storage location, in service and empty.</summary>
    private static CarrierManager WithStorage(uint location = Shelf)
    {
        var manager = new CarrierManager();

        manager.AddStorageLocation(location);

        Assert.True(manager.SetStorageServiceStatus(location, inService: true).IsAccepted);

        return manager;
    }

    /// <summary>Puts a carrier into a storage location, the way a machine would.</summary>
    private static void Store(CarrierManager manager, string carrierId, uint location = Shelf)
    {
        Assert.True(manager.SetStorageTransferState(location, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(manager.StoreCarrier(location, carrierId).IsAccepted);
    }

    // ------------------------------------------------------------------- the model

    /// <summary>Hardware is declared, not messaged into being - the rule ports already keep.</summary>
    /// <remarks>
    /// S3F31 could not create one even if this package let it: the request has no items, so there
    /// is nothing in it to name a location with.
    /// </remarks>
    [Fact]
    public void A_storage_location_is_declared_and_starts_out_of_service()
    {
        var manager = new CarrierManager();

        var shelf = manager.AddStorageLocation(Shelf);

        Assert.Equal(Shelf, shelf.Location);
        Assert.Equal(PortTransferState.OutOfService, shelf.TransferState);
        Assert.False(shelf.IsInService);
        Assert.False(shelf.IsOccupied);
        Assert.Null(shelf.CarrierId);

        // Declaring it twice is the same location, not a second one: configuration gets read more
        // than once in a machine that reloads it.
        Assert.Same(shelf, manager.AddStorageLocation(Shelf));
        Assert.Same(shelf, manager.FindStorageLocation(Shelf));
        Assert.Single(manager.StorageLocations);
    }

    [Fact]
    public void A_location_nobody_declared_is_not_there()
    {
        var manager = WithStorage();

        Assert.Null(manager.FindStorageLocation(999));

        // Nought is a location like any other, and refusing it by number rather than by being
        // zero is what keeps that true.
        var refusal = manager.StoreCarrier(0, "CAR-001");

        Assert.Equal(CarrierAcknowledgement.InvalidData, refusal.Acknowledgement);
        Assert.Equal("there is no storage location 0.", refusal.ErrorText);
        Assert.Equal("there is no storage location 999.", manager.FetchCarrier(999).ErrorText);
        Assert.Equal("there is no storage location 999.", manager.SetStorageServiceStatus(999, true).ErrorText);
        Assert.Equal(
            "there is no storage location 999.",
            manager.SetStorageTransferState(999, PortTransferState.ReadyToLoad).ErrorText);
    }

    [Fact]
    public void A_location_out_of_service_refuses_all_three_in_service_states()
    {
        var manager = new CarrierManager();

        manager.AddStorageLocation(Shelf);

        foreach (var state in new[]
        {
            PortTransferState.TransferBlocked,
            PortTransferState.ReadyToLoad,
            PortTransferState.ReadyToUnload,
        })
        {
            var refusal = manager.SetStorageTransferState(Shelf, state);

            Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
            Assert.Equal("storage location 101 is OutOfService.", refusal.ErrorText);
        }
    }

    /// <summary>A port's rule, kept for a location: service is a decision, readiness is a condition.</summary>
    /// <remarks>
    /// The refusal names the method rather than an action, and that difference is real: a port's
    /// service status is reachable by a host through <c>ChangeServiceStatus</c>, and a storage
    /// location's is not, because no E87 carrier action names a <c>LOC</c>.
    /// </remarks>
    [Fact]
    public void Out_of_service_is_not_reachable_through_the_transfer_state()
    {
        var manager = WithStorage();

        var refusal = manager.SetStorageTransferState(Shelf, PortTransferState.OutOfService);

        Assert.Equal(CarrierAcknowledgement.InvalidData, refusal.Acknowledgement);
        Assert.Equal("OutOfService is a service decision; use SetStorageServiceStatus.", refusal.ErrorText);
        Assert.True(manager.FindStorageLocation(Shelf)!.IsInService);
    }

    [Fact]
    public void A_carrier_goes_into_a_location_only_from_ready_to_load()
    {
        var manager = WithStorage();

        var refusal = manager.StoreCarrier(Shelf, "CAR-001");

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Equal(
            "storage location 101 is TransferBlocked and a carrier may only be put into one that is ReadyToLoad.",
            refusal.ErrorText);

        // And nothing was created on the way out: a refusal that left CAR-001 behind would have a
        // machine holding a carrier nobody ever put anywhere.
        Assert.Null(manager.FindCarrier("CAR-001"));
        Assert.Empty(manager.Carriers);
    }

    [Fact]
    public void A_carrier_comes_out_only_from_ready_to_unload()
    {
        var manager = WithStorage();

        Store(manager, "CAR-001");

        var refusal = manager.FetchCarrier(Shelf);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Equal(
            "storage location 101 is TransferBlocked and a carrier may only be taken out of one that is ReadyToUnload.",
            refusal.ErrorText);
        Assert.True(manager.FindStorageLocation(Shelf)!.IsOccupied);
    }

    [Fact]
    public void An_empty_location_has_nothing_to_unload_and_a_full_one_cannot_be_loaded()
    {
        var manager = WithStorage();

        Assert.Equal(
            "storage location 101 has nothing in it to unload.",
            manager.SetStorageTransferState(Shelf, PortTransferState.ReadyToUnload).ErrorText);

        Store(manager, "CAR-001");

        Assert.Equal(
            "storage location 101 already has carrier 'CAR-001' in it.",
            manager.SetStorageTransferState(Shelf, PortTransferState.ReadyToLoad).ErrorText);
    }

    [Fact]
    public void A_location_holding_a_carrier_cannot_be_taken_out_of_service()
    {
        var manager = WithStorage();

        Store(manager, "CAR-001");

        var refusal = manager.SetStorageServiceStatus(Shelf, inService: false);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Equal("storage location 101 still has carrier 'CAR-001' in it.", refusal.ErrorText);

        // The carrier would be stranded somewhere nobody is allowed to touch - a port's rule, and
        // a shelf has the same problem.
        Assert.True(manager.FindStorageLocation(Shelf)!.IsInService);
    }

    [Fact]
    public void Storing_and_fetching_move_the_carrier_and_the_location_together()
    {
        var manager = WithStorage();

        Store(manager, "CAR-001");

        var shelf = manager.FindStorageLocation(Shelf)!;
        var carrier = manager.FindCarrier("CAR-001")!;

        Assert.Equal("CAR-001", shelf.CarrierId);
        Assert.Equal(Shelf, carrier.StorageLocation);
        Assert.Null(carrier.PortId);
        Assert.Equal(PortTransferState.TransferBlocked, shelf.TransferState);
        Assert.Equal("location 101 TransferBlocked (CAR-001)", shelf.ToString());

        Assert.True(manager.SetStorageTransferState(Shelf, PortTransferState.ReadyToUnload).IsAccepted);
        Assert.True(manager.FetchCarrier(Shelf).IsAccepted);

        Assert.Null(shelf.CarrierId);
        Assert.Null(carrier.StorageLocation);
        Assert.Equal(PortTransferState.TransferBlocked, shelf.TransferState);
        Assert.Equal("location 101 TransferBlocked", shelf.ToString());

        // The carrier is still known - it was taken out of a shelf, not out of existence.
        Assert.Same(carrier, manager.FindCarrier("CAR-001"));
    }

    /// <summary>One state machine, so one event, and the states need no cast.</summary>
    [Fact]
    public void A_location_raises_its_transitions_in_order()
    {
        var manager = WithStorage();
        List<(PortTransferState Previous, PortTransferState Current)> seen = [];

        manager.StorageStateChanged += (_, args) =>
        {
            Assert.Equal(Shelf, args.StorageLocation.Location);

            // Raised after the location has moved, as the port and carrier transitions are: a
            // handler reading the thing it was just told about has to see the state the event is
            // announcing, or the notification is about a past that no longer exists.
            Assert.Equal(args.Current, args.StorageLocation.TransferState);

            seen.Add((args.Previous, args.Current));
        };

        Store(manager, "CAR-001");

        (PortTransferState Previous, PortTransferState Current)[] expected =
        [
            (PortTransferState.TransferBlocked, PortTransferState.ReadyToLoad),
            (PortTransferState.ReadyToLoad, PortTransferState.TransferBlocked),
        ];

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void A_location_that_is_already_in_the_state_asked_for_raises_nothing()
    {
        var manager = WithStorage();
        var raised = 0;

        manager.StorageStateChanged += (_, _) => raised++;

        Assert.True(manager.SetStorageServiceStatus(Shelf, inService: true).IsAccepted);
        Assert.True(manager.SetStorageTransferState(Shelf, PortTransferState.TransferBlocked).IsAccepted);

        Assert.Equal(0, raised);
    }

    [Fact]
    public void The_locations_come_back_ordered_by_LOC()
    {
        var manager = new CarrierManager();

        manager.AddStorageLocation(203);
        manager.AddStorageLocation(7);
        manager.AddStorageLocation(101);

        // Ordered, because S3F32 reports them: a host that asks twice and gets the same shelves
        // shuffled has to sort them itself to see whether anything moved.
        uint[] expected = [7, 101, 203];

        Assert.Equal(expected, manager.StorageLocations.Select(location => location.Location));
    }

    // -------------------------------------------------------- a carrier is in one place

    [Fact]
    public void A_carrier_on_a_port_cannot_also_be_in_storage()
    {
        var manager = WithStorage();

        manager.AddPort(1);

        Assert.True(manager.SetServiceStatus(1, inService: true).IsAccepted);
        Assert.True(manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(manager.PlaceCarrier(1, "CAR-001").IsAccepted);
        Assert.True(manager.SetStorageTransferState(Shelf, PortTransferState.ReadyToLoad).IsAccepted);

        var refusal = manager.StoreCarrier(Shelf, "CAR-001");

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Equal("carrier 'CAR-001' is on port 1; a carrier is in one place.", refusal.ErrorText);
        Assert.False(manager.FindStorageLocation(Shelf)!.IsOccupied);
        Assert.Null(manager.FindCarrier("CAR-001")!.StorageLocation);
    }

    [Fact]
    public void A_carrier_in_storage_cannot_also_be_placed_on_a_port()
    {
        var manager = WithStorage();

        manager.AddPort(1);

        Assert.True(manager.SetServiceStatus(1, inService: true).IsAccepted);

        Store(manager, "CAR-001");

        Assert.True(manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);

        var refusal = manager.PlaceCarrier(1, "CAR-001");

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Equal("carrier 'CAR-001' is in storage location 101; a carrier is in one place.", refusal.ErrorText);

        // The port is untouched: it did not take a carrier it was refused.
        Assert.Equal(PortAssociationState.NotAssociated, manager.FindPort(1)!.AssociationState);
        Assert.Equal(PortTransferState.ReadyToLoad, manager.FindPort(1)!.TransferState);
    }

    [Fact]
    public void A_carrier_already_in_one_location_is_not_put_into_another()
    {
        var manager = WithStorage();

        manager.AddStorageLocation(102);

        Assert.True(manager.SetStorageServiceStatus(102, inService: true).IsAccepted);

        Store(manager, "CAR-001");

        Assert.True(manager.SetStorageTransferState(102, PortTransferState.ReadyToLoad).IsAccepted);

        var refusal = manager.StoreCarrier(102, "CAR-001");

        Assert.Equal("carrier 'CAR-001' is in storage location 101; a carrier is in one place.", refusal.ErrorText);
        Assert.Equal("CAR-001", manager.FindStorageLocation(Shelf)!.CarrierId);
        Assert.Null(manager.FindStorageLocation(102)!.CarrierId);
    }

    // ------------------------------------------------------- outliving the process

    [Fact]
    public void A_location_comes_back_in_service_and_empty()
    {
        var manager = WithStorage();

        Store(manager, "CAR-001");

        var snapshot = manager.Capture();

        // Faithful: the record says what was true, including what a restart will not claim.
        var recorded = Assert.Single(snapshot.StorageLocations);

        Assert.Equal(Shelf, recorded.Location);
        Assert.Equal(PortTransferState.TransferBlocked, recorded.TransferState);
        Assert.Equal("CAR-001", recorded.CarrierId);
        Assert.Equal(Shelf, Assert.Single(snapshot.Carriers).StorageLocation);

        var restored = WithStorage();
        var report = restored.Restore(snapshot);

        Assert.True(report.IsAccepted);
        Assert.Contains("location 101", report.Restored);

        var shelf = restored.FindStorageLocation(Shelf)!;

        Assert.True(shelf.IsInService);
        Assert.False(shelf.IsOccupied);
        Assert.Null(restored.FindCarrier("CAR-001")!.StorageLocation);
        Assert.Equal(
            "had 'CAR-001' in it; what is in a storage location is sensed, not remembered.",
            report.Adjusted["location 101"]);
    }

    [Fact]
    public void A_location_that_was_ready_comes_back_blocked()
    {
        var manager = WithStorage();

        Assert.True(manager.SetStorageTransferState(Shelf, PortTransferState.ReadyToLoad).IsAccepted);

        var restored = WithStorage();
        var report = restored.Restore(manager.Capture());

        Assert.Equal(PortTransferState.TransferBlocked, restored.FindStorageLocation(Shelf)!.TransferState);
        Assert.Equal(
            "was ReadyToLoad; a restarted machine has sensed nothing, so it is TransferBlocked.",
            report.Adjusted["location 101"]);
    }

    [Fact]
    public void A_location_out_of_service_comes_back_out_of_service()
    {
        var manager = new CarrierManager();

        manager.AddStorageLocation(Shelf);

        var restored = WithStorage();

        Assert.True(restored.Restore(manager.Capture()).IsAccepted);

        // Whether a shelf is usable at all is somebody's decision, and a restart does not change
        // anybody's mind.
        Assert.False(restored.FindStorageLocation(Shelf)!.IsInService);
    }

    [Fact]
    public void A_snapshot_does_not_invent_a_storage_location()
    {
        var manager = WithStorage();

        Store(manager, "CAR-001");

        // A machine whose configuration no longer mentions shelf 101.
        var restored = new CarrierManager();
        var report = restored.Restore(manager.Capture());

        Assert.True(report.IsAccepted);
        Assert.DoesNotContain("location 101", report.Restored);
        Assert.Null(restored.FindStorageLocation(Shelf));
        Assert.Equal(
            "the snapshot has this storage location and this manager does not; locations are declared "
            + "from configuration, so it was skipped rather than invented.",
            report.Adjusted["location 101"]);
    }

    // -------------------------------------------------------------- S3F31 and S3F32

    [Fact]
    public async Task Every_storage_location_comes_back_from_one_request()
    {
        await using var pair = CarrierPair.Start(configure: manager =>
        {
            manager.AddStorageLocation(101);
            manager.AddStorageLocation(102);

            Assert.True(manager.SetStorageServiceStatus(101, inService: true).IsAccepted);
            Assert.True(manager.SetStorageServiceStatus(102, inService: true).IsAccepted);

            Store(manager, "CAR-001", 101);
        });

        await pair.OpenAsync();

        var locations = await pair.Host.ReadStorageStatusAsync(CarrierPair.Token);

        Assert.Equal(2, locations.Count);

        Assert.Equal(101u, locations[0].Location);
        Assert.Equal(PortTransferState.TransferBlocked, locations[0].TransferState);
        Assert.Equal("CAR-001", locations[0].CarrierId);
        Assert.Equal("location 101 TransferBlocked (CAR-001)", locations[0].ToString());

        Assert.Equal(102u, locations[1].Location);
        Assert.Equal(string.Empty, locations[1].CarrierId);
        Assert.Equal("location 102 TransferBlocked", locations[1].ToString());
    }

    /// <summary>The empty list means "none", not "I will not say" - and only here.</summary>
    /// <remarks>
    /// S3F22, S3F24 and S3F28 all answer a zero-length list for something the machine does not
    /// have, because each names one port or one carrier that might be missing. S3F31 names
    /// nothing, so there is nothing to be missing: a machine with no shelves has no shelves.
    /// </remarks>
    [Fact]
    public async Task A_machine_with_no_storage_says_so_rather_than_refusing()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.Empty(await pair.Host.ReadStorageStatusAsync(CarrierPair.Token));
        Assert.Contains(((byte)3, (byte)31), pair.Asked);
    }

    /// <summary>The first of the two agreement tests: one model, one answer about where a box is.</summary>
    /// <remarks>
    /// S3F24 reports one <c>LOC</c> for a carrier and S3F32 lists every location with what is in
    /// it. Both are answered out of <see cref="CarrierManager"/>, so they can only disagree if
    /// this package lets them - and a host that got 101 from one and 0 from the other would have
    /// to decide which to believe.
    /// </remarks>
    [Fact]
    public async Task S3F32_and_S3F24_agree_on_where_a_carrier_is()
    {
        await using var pair = CarrierPair.Start(configure: manager =>
        {
            manager.AddStorageLocation(Shelf);

            Assert.True(manager.SetStorageServiceStatus(Shelf, inService: true).IsAccepted);

            Store(manager, "CAR-001");
        });

        await pair.OpenAsync();

        var located = await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token);
        var listed = await pair.Host.ReadStorageStatusAsync(CarrierPair.Token);
        var holding = Assert.Single(listed, location => location.CarrierId == "CAR-001");

        Assert.Equal(holding.Location, located);

        // And not nought, which is what the old default would have said: a carrier on a shelf
        // reported as being nowhere is worse than no answer, because nought is a location.
        Assert.Equal(Shelf, located);
    }

    /// <summary>A carrier at a port still reports the port, which is what it did before storage existed.</summary>
    [Fact]
    public async Task A_carrier_at_a_port_reports_the_port_and_one_in_storage_reports_the_shelf()
    {
        await using var pair = CarrierPair.Start(configure: manager =>
        {
            manager.AddStorageLocation(Shelf);
            Assert.True(manager.SetStorageServiceStatus(Shelf, inService: true).IsAccepted);
            Store(manager, "CAR-002");
        });

        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        Assert.Equal(1u, await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token));
        Assert.Equal(Shelf, await pair.Host.FindCarrierAsync("CAR-002", CarrierPair.Token));
    }

    /// <summary>The second agreement test: one item name, one reading.</summary>
    /// <remarks>
    /// <c>PORTSTATUS</c> has no dictionary entry, and S3F32's reading is one hop further from firm
    /// ground than S3F22's - there is no stream 14 attribute for a storage location to check it
    /// against, only the item name it shares with the transaction three functions earlier. So the
    /// test is that the two carry the same thing for the same state: a host that reads one and
    /// then the other must not need two vocabularies for one machine.
    /// </remarks>
    [Fact]
    public async Task S3F32_and_S3F22_say_the_same_thing_about_the_same_state()
    {
        await using var pair = CarrierPair.Start(configure: manager =>
        {
            manager.AddStorageLocation(Shelf);

            Assert.True(manager.SetStorageServiceStatus(Shelf, inService: true).IsAccepted);

            Store(manager, "CAR-002");

            Assert.True(manager.SetStorageTransferState(Shelf, PortTransferState.ReadyToUnload).IsAccepted);
        });

        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        Assert.True(pair.Manager.SetTransferState(1, PortTransferState.ReadyToUnload).IsAccepted);

        var port = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);
        var shelf = Assert.Single(await pair.Host.ReadStorageStatusAsync(CarrierPair.Token));

        Assert.NotNull(port);
        Assert.Equal(nameof(PortTransferState.ReadyToUnload), port!.Status);
        Assert.Equal(port.Status, shelf.Status);
        Assert.Equal(port.TransferState, shelf.TransferState);
    }

    /// <summary>A location out of service reports it, which is the whole point of reporting status.</summary>
    [Fact]
    public async Task A_location_out_of_service_reports_itself_as_one_nobody_may_use()
    {
        await using var pair = CarrierPair.Start(configure: manager => manager.AddStorageLocation(Shelf));

        await pair.OpenAsync();

        var shelf = Assert.Single(await pair.Host.ReadStorageStatusAsync(CarrierPair.Token));

        Assert.Equal(PortTransferState.OutOfService, shelf.TransferState);
        Assert.Equal(nameof(PortTransferState.OutOfService), shelf.Status);
        Assert.Equal(string.Empty, shelf.CarrierId);
    }

    /// <summary>An unfamiliar word arrives as text with no reading, as it does for a port.</summary>
    [Fact]
    public async Task A_status_word_from_another_vendor_arrives_as_text_and_not_as_a_state()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 32, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.L(
                    SecsItem.U4(101).Named("LOC"),
                    SecsItem.A("SHELF_JAMMED").Named("PORTSTATUS"),
                    SecsItem.A("CAR-001").Named("CARRIERID"))),
        });

        await pair.OpenAsync();

        var shelf = Assert.Single(await pair.Host.ReadStorageStatusAsync(CarrierPair.Token));

        Assert.Equal("SHELF_JAMMED", shelf.Status);
        Assert.Null(shelf.TransferState);
        Assert.Equal("CAR-001", shelf.CarrierId);
        Assert.Equal("location 101 SHELF_JAMMED (CAR-001)", shelf.ToString());
    }

    /// <summary>A machine that writes the three items flat is still understood.</summary>
    /// <remarks>
    /// An item list cannot say whether three items repeated are a list of entries or one run, so
    /// the reader takes both - the same tolerance <c>Barbatos.SecsGem.Material</c> needs for S3F2
    /// in this same stream, for this same reason.
    /// </remarks>
    [Fact]
    public async Task A_flat_body_reads_as_entries_too()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 32, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.U4(101).Named("LOC"),
                SecsItem.A("TransferBlocked").Named("PORTSTATUS"),
                SecsItem.A(string.Empty).Named("CARRIERID"),
                SecsItem.U4(102).Named("LOC"),
                SecsItem.A("ReadyToLoad").Named("PORTSTATUS"),
                SecsItem.A(string.Empty).Named("CARRIERID")),
        });

        await pair.OpenAsync();

        var locations = await pair.Host.ReadStorageStatusAsync(CarrierPair.Token);

        Assert.Equal(2, locations.Count);
        Assert.Equal(101u, locations[0].Location);
        Assert.Equal(PortTransferState.TransferBlocked, locations[0].TransferState);
        Assert.Equal(102u, locations[1].Location);
        Assert.Equal(PortTransferState.ReadyToLoad, locations[1].TransferState);
    }

    [Fact]
    public async Task A_reply_that_is_not_storage_status_data_is_an_exception_rather_than_an_empty_list()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 32, replyExpected: false)
        {
            Item = SecsItem.A("sorry"),
        });

        await pair.OpenAsync();

        // Empty would be a lie: it means "this machine has no storage locations", and what
        // happened is that it said something this reader does not understand.
        var thrown = await Assert.ThrowsAsync<SecsException>(
            async () => await pair.Host.ReadStorageStatusAsync(CarrierPair.Token));

        Assert.Equal("S3F32 did not carry storage status data.", thrown.Message);
    }
}
