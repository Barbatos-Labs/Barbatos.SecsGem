// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// E87's attributes over stream 14, which is where the three unimplementable transactions went.
/// </summary>
/// <remarks>
/// S3F20 wants a <c>CARRIERSTATUS</c>, S3F22 a <c>PORTSTATUS</c> and S3F28 a <c>SLOTMAP</c>, and
/// the catalogue has no format for any of the three. These tests are the alternative working: the
/// same information, as object attributes, in items whose formats are defined - and a slot map
/// that needs no format at all because <c>ATTRDATA</c> has none.
/// </remarks>
public class CarrierObjectProviderTests
{
    private static (CarrierManager Manager, CarrierObjectProvider Provider) Loaded()
    {
        var manager = new CarrierManager();

        manager.AddPort(1);
        manager.SetServiceStatus(1, inService: true);
        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadCarrierId("CAR-001", verified: true);
        manager.ReadSlotMap("CAR-001", new byte[] { 1, 1, 0, 1 }, verified: true);

        return (manager, new CarrierObjectProvider(manager));
    }

    private static async Task<Dictionary<string, SecsItem>> ReadAsync(
        CarrierObjectProvider provider,
        string specification,
        params string[] attributes)
    {
        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = [specification], Attributes = attributes },
            TestContext.Current.CancellationToken);

        Dictionary<string, SecsItem> read = [];

        foreach (var attribute in Assert.Single(values).Attributes)
        {
            read[attribute.Name] = attribute.Value;
        }

        return read;
    }

    [Fact]
    public async Task Both_object_types_are_there_whether_or_not_a_carrier_is()
    {
        var provider = new CarrierObjectProvider(new CarrierManager());

        var types = await provider.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        // A host discovering an empty list would conclude this equipment does not do E87 at all.
        Assert.Equal(2, types.Count);
        Assert.Contains(CarrierObjectProvider.CarrierType, types);
        Assert.Contains(CarrierObjectProvider.LoadPortType, types);
    }

    [Fact]
    public async Task The_instances_are_the_carriers_and_the_ports()
    {
        var (_, provider) = Loaded();

        Assert.Equal(
            "CAR-001",
            Assert.Single(await provider.GetInstancesAsync(CarrierObjectProvider.CarrierType, TestContext.Current.CancellationToken)));

        Assert.Equal(
            CarrierObjectProvider.PortSpecification(1),
            Assert.Single(await provider.GetInstancesAsync(CarrierObjectProvider.LoadPortType, TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task A_carrier_reports_its_states_and_its_slot_map()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, "CAR-001");

        Assert.Equal("CAR-001", read["CarrierID"].GetString());
        Assert.Equal(nameof(CarrierIdStatus.IdVerificationOk), read["IDStatus"].GetString());
        Assert.Equal(nameof(CarrierSlotMapStatus.SlotMapVerificationOk), read["SlotMapStatus"].GetString());
        Assert.Equal(nameof(CarrierAccessStatus.NotAccessed), read["AccessStatus"].GetString());

        // The slot map as the bytes it is. This is the whole argument for putting it here: no
        // format had to be invented for it, because an attribute may be any item.
        Assert.Equal([1, 1, 0, 1], read["SlotMap"].GetSpan<byte>().ToArray());
        Assert.Equal(4, read["Capacity"].FirstValueOrDefault<byte>());
        Assert.Equal(3, read["SubstrateCount"].FirstValueOrDefault<byte>());
        Assert.Equal(1, read["PortID"].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_carrier_that_is_not_at_a_port_says_so_with_an_empty_item_rather_than_port_zero()
    {
        var manager = new CarrierManager();

        manager.Perform(new CarrierActionRequest { Action = CarrierActions.CarrierNotification, CarrierId = "CAR-002" });

        var provider = new CarrierObjectProvider(manager);
        var read = await ReadAsync(provider, "CAR-002", "PortID");

        // Port 0 is a port. A carrier that is not at one has to be able to say that.
        Assert.Equal(0, read["PortID"].Count);
    }

    [Fact]
    public async Task A_port_reports_its_four_states()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, CarrierObjectProvider.PortSpecification(1));

        Assert.Equal(1, read["PortID"].FirstValueOrDefault<byte>());
        Assert.Equal(nameof(PortTransferState.TransferBlocked), read["TransferState"].GetString());
        Assert.Equal(nameof(PortAccessMode.Manual), read["AccessMode"].GetString());
        Assert.Equal(nameof(PortAssociationState.Associated), read["AssociationState"].GetString());
        Assert.Equal(nameof(PortReservationState.NotReserved), read["ReservationState"].GetString());
        Assert.Equal("CAR-001", read["AssociatedCarrierID"].GetString());
    }

    [Fact]
    public async Task An_object_the_machine_does_not_have_is_left_out()
    {
        var (_, provider) = Loaded();

        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["CAR-001", "CAR-999", "LoadPort7"], Attributes = ["CarrierID"] },
            TestContext.Current.CancellationToken);

        Assert.Equal("CAR-001", Assert.Single(values).ObjectSpecification);
    }

    [Fact]
    public async Task Writing_a_state_attribute_is_refused_so_stream_14_is_not_a_back_door()
    {
        var (manager, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["CAR-001"],
                Attributes = [new ObjectAttribute { Name = "AccessStatus", Value = SecsItem.A("CarrierComplete") }],
            },
            TestContext.Current.CancellationToken);

        // Letting this through would put a carrier into CarrierComplete without accessing it, and
        // the state machine this package exists for would be decoration.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        Assert.Equal(CarrierAccessStatus.NotAccessed, manager.FindCarrier("CAR-001")?.AccessStatus);
    }

    [Fact]
    public async Task Writing_the_lot_and_the_usage_is_allowed_because_they_are_labels()
    {
        var (manager, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["CAR-001"],
                Attributes =
                [
                    new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-7") },
                    new ObjectAttribute { Name = "Usage", Value = SecsItem.A("Production") },
                ],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(results).Acknowledgement);

        var carrier = manager.FindCarrier("CAR-001");

        Assert.NotNull(carrier);
        Assert.Equal("LOT-7", carrier.LotId);
        Assert.Equal("Production", carrier.Usage);
    }

    [Fact]
    public async Task A_set_naming_one_read_only_attribute_changes_nothing_at_all()
    {
        var (manager, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["CAR-001"],
                Attributes =
                [
                    new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-7") },
                    new ObjectAttribute { Name = "SlotMap", Value = SecsItem.B(1, 1) },
                ],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        Assert.Equal(string.Empty, manager.FindCarrier("CAR-001")?.LotId);
    }

    [Fact]
    public async Task Writing_the_access_mode_goes_through_the_state_machine()
    {
        var (manager, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = [CarrierObjectProvider.PortSpecification(1)],
                Attributes = [new ObjectAttribute { Name = "AccessMode", Value = SecsItem.A(nameof(PortAccessMode.Auto)) }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(results).Acknowledgement);
        Assert.Equal(PortAccessMode.Auto, manager.FindPort(1)?.AccessMode);
    }

    [Fact]
    public async Task Writing_the_access_mode_is_refused_during_a_transfer_just_as_the_action_is()
    {
        var manager = new CarrierManager();

        manager.AddPort(1);
        manager.SetServiceStatus(1, inService: true);
        manager.SetTransferState(1, PortTransferState.ReadyToLoad);

        var provider = new CarrierObjectProvider(manager);

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = [CarrierObjectProvider.PortSpecification(1)],
                Attributes = [new ObjectAttribute { Name = "AccessMode", Value = SecsItem.A(nameof(PortAccessMode.Auto)) }],
            },
            TestContext.Current.CancellationToken);

        // The same refusal the action would have given, which is the point of routing the write
        // through it: a host gets one answer whichever message it asks with.
        Assert.Equal(ObjectAcknowledgement.Error, Assert.Single(results).Acknowledgement);
        Assert.Equal(PortAccessMode.Manual, manager.FindPort(1)?.AccessMode);
    }

    [Fact]
    public async Task An_access_mode_that_is_not_a_mode_is_an_invalid_attribute()
    {
        var (_, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = [CarrierObjectProvider.PortSpecification(1)],
                Attributes = [new ObjectAttribute { Name = "AccessMode", Value = SecsItem.A("Sideways") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
    }

    [Fact]
    public async Task Writing_to_an_object_that_does_not_exist_says_which_part_was_wrong()
    {
        var (_, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["CAR-999", "LoadPort9"],
                Attributes = [new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-7") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, result.Acknowledgement));
    }

    [Fact]
    public void A_port_specification_survives_being_written_and_read_back()
    {
        foreach (byte portId in new byte[] { 0, 1, 7, 255 })
        {
            var specification = CarrierObjectProvider.PortSpecification(portId);

            Assert.Equal(portId, CarrierObjectProvider.TryReadPortSpecification(specification));
        }

        // And a carrier that happens to look like a port does not become one.
        Assert.Null(CarrierObjectProvider.TryReadPortSpecification("CAR-001"));
        Assert.Null(CarrierObjectProvider.TryReadPortSpecification("LoadPort"));
        Assert.Null(CarrierObjectProvider.TryReadPortSpecification("LoadPortA"));
    }

    [Fact]
    public async Task The_attribute_names_a_host_discovers_are_the_ones_it_can_read()
    {
        var (_, provider) = Loaded();

        // The contract S14F17 exists for: whatever names come back must be readable with S14F1.
        // A namelist that advertises an attribute nothing answers is worse than a short list.
        var carrierNames = await provider.GetAttributeNamesAsync(
            "CAR-001",
            CarrierObjectProvider.CarrierType,
            TestContext.Current.CancellationToken);

        var carrier = await ReadAsync(provider, "CAR-001", [.. carrierNames]);

        Assert.Equal(carrierNames.Count, carrier.Count);

        var portNames = await provider.GetAttributeNamesAsync(
            CarrierObjectProvider.PortSpecification(1),
            CarrierObjectProvider.LoadPortType,
            TestContext.Current.CancellationToken);

        var port = await ReadAsync(provider, CarrierObjectProvider.PortSpecification(1), [.. portNames]);

        Assert.Equal(portNames.Count, port.Count);
    }
}
