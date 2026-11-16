// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Substrates.UnitTests;

/// <summary>
/// E90 over stream 14, which is the whole of its wire presence.
/// </summary>
/// <remarks>
/// E90 defines no messages, so there is nothing else a host can see: a substrate is an object, and
/// everything about it arrives as an attribute. These tests are that surface.
/// </remarks>
public class SubstrateObjectProviderTests
{
    private static (SubstrateTracker Tracker, SubstrateObjectProvider Provider) Loaded()
    {
        var tracker = new SubstrateTracker();

        tracker.AddLocation("SLOT-1");
        tracker.AddLocation("CHUCK");
        tracker.AddLocation("SLOT-OUT");

        tracker.Add("W01", "SLOT-1", slotId: 3);
        tracker.Describe("W01", lotId: "LOT-7", substrateType: "Wafer", destinationLocationId: "SLOT-OUT");
        tracker.MoveTo("W01", "CHUCK");
        tracker.BeginProcessing("W01");

        return (tracker, new SubstrateObjectProvider(tracker));
    }

    private static async Task<Dictionary<string, SecsItem>> ReadAsync(
        SubstrateObjectProvider provider,
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

    [Fact]
    public async Task Both_object_types_are_there_whether_or_not_a_substrate_is()
    {
        var provider = new SubstrateObjectProvider(new SubstrateTracker());

        var types = await provider.GetObjectTypesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, types.Count);
        Assert.Contains(SubstrateObjectProvider.SubstrateType, types);
        Assert.Contains(SubstrateObjectProvider.SubstrateLocationType, types);
    }

    [Fact]
    public async Task The_instances_are_the_substrates_and_the_locations()
    {
        var (_, provider) = Loaded();

        Assert.Equal(
            "W01",
            Assert.Single(await provider.GetInstancesAsync(
                SubstrateObjectProvider.SubstrateType,
                TestContext.Current.CancellationToken)));

        Assert.Equal(
            3,
            (await provider.GetInstancesAsync(
                SubstrateObjectProvider.SubstrateLocationType,
                TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task A_substrate_reports_its_states_and_its_labels()
    {
        var (_, provider) = Loaded();

        var read = await ReadAsync(provider, "W01");

        Assert.Equal("W01", read["SubstrateID"].GetString());

        // E90 calls the transport state the substrate state on the wire, and this is where that
        // naming is honoured.
        Assert.Equal(nameof(SubstrateTransportState.AtWork), read["SubstrateState"].GetString());
        Assert.Equal(nameof(SubstrateProcessingState.InProcess), read["SubstrateProcessingState"].GetString());
        Assert.Equal("CHUCK", read["SubstrateLocID"].GetString());
        Assert.Equal("SLOT-1", read["SubstrateSourceLocID"].GetString());
        Assert.Equal("SLOT-OUT", read["SubstrateDestinationLocID"].GetString());
        Assert.Equal(3, read["SlotID"].FirstValueOrDefault<byte>());
        Assert.Equal("LOT-7", read["LotID"].GetString());
        Assert.Equal("Wafer", read["SubstrateType"].GetString());
    }

    [Fact]
    public async Task A_location_reports_whether_something_is_in_it()
    {
        var (_, provider) = Loaded();

        var chuck = await ReadAsync(provider, "CHUCK", SubstrateObjectProvider.SubstrateLocationType);

        Assert.Equal("CHUCK", chuck["SubstrateLocID"].GetString());
        Assert.Equal(nameof(SubstrateLocationState.Occupied), chuck["SubstrateLocState"].GetString());
        Assert.Equal("W01", chuck["SubstrateID"].GetString());

        var slot = await ReadAsync(provider, "SLOT-1", SubstrateObjectProvider.SubstrateLocationType);

        Assert.Equal(nameof(SubstrateLocationState.Unoccupied), slot["SubstrateLocState"].GetString());
        Assert.Equal(string.Empty, slot["SubstrateID"].GetString());
    }

    [Fact]
    public async Task An_object_the_machine_does_not_have_is_left_out()
    {
        var (_, provider) = Loaded();

        var values = await provider.GetAttributesAsync(
            new ObjectAttributeRequest { Objects = ["W01", "W99", "NOWHERE"], Attributes = ["SubstrateID"] },
            TestContext.Current.CancellationToken);

        Assert.Equal("W01", Assert.Single(values).ObjectSpecification);
    }

    [Fact]
    public async Task Writing_a_state_attribute_is_refused_so_stream_14_is_not_a_back_door()
    {
        var (tracker, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["W01"],
                Attributes =
                [
                    new ObjectAttribute
                    {
                        Name = "SubstrateProcessingState",
                        Value = SecsItem.A(nameof(SubstrateProcessingState.Processed)),
                    },
                ],
            },
            TestContext.Current.CancellationToken);

        // Marking a wafer processed without processing it would make the state machine decoration.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        Assert.Equal(SubstrateProcessingState.InProcess, tracker.FindSubstrate("W01")?.ProcessingState);
    }

    [Fact]
    public async Task Writing_a_label_is_allowed_and_reaches_the_tracker()
    {
        var (tracker, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["W01"],
                Attributes =
                [
                    new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-9") },
                    new ObjectAttribute { Name = "AcquiredID", Value = SecsItem.A("SCRIBE-123") },
                ],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(results).Acknowledgement);

        var substrate = tracker.FindSubstrate("W01");

        Assert.NotNull(substrate);
        Assert.Equal("LOT-9", substrate.LotId);
        Assert.Equal("SCRIBE-123", substrate.AcquiredId);
    }

    [Fact]
    public async Task A_set_naming_one_read_only_attribute_changes_nothing_at_all()
    {
        var (tracker, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["W01"],
                Attributes =
                [
                    new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-9") },
                    new ObjectAttribute { Name = "SubstrateLocID", Value = SecsItem.A("SLOT-OUT") },
                ],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
        Assert.Equal("LOT-7", tracker.FindSubstrate("W01")?.LotId);
    }

    [Fact]
    public async Task Writing_a_destination_goes_through_the_tracker_and_is_refused_when_it_names_nowhere()
    {
        var (tracker, provider) = Loaded();

        var ok = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["W01"],
                Attributes = [new ObjectAttribute { Name = "SubstrateDestinationLocID", Value = SecsItem.A("SLOT-1") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.Successful, Assert.Single(ok).Acknowledgement);
        Assert.Equal("SLOT-1", tracker.FindSubstrate("W01")?.DestinationLocationId);

        var refused = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["W01"],
                Attributes = [new ObjectAttribute { Name = "SubstrateDestinationLocID", Value = SecsItem.A("NOWHERE") }],
            },
            TestContext.Current.CancellationToken);

        // The rule lives in the tracker, so it holds whichever way the destination is set.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(refused).Acknowledgement);
        Assert.Equal("SLOT-1", tracker.FindSubstrate("W01")?.DestinationLocationId);
    }

    [Fact]
    public async Task A_location_has_nothing_a_host_may_set()
    {
        var (_, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["CHUCK"],
                Attributes = [new ObjectAttribute { Name = "SubstrateLocState", Value = SecsItem.A("Unoccupied") }],
            },
            TestContext.Current.CancellationToken);

        // Its occupancy follows the substrates and its name is its name.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, Assert.Single(results).Acknowledgement);
    }

    [Fact]
    public async Task Writing_to_an_object_that_does_not_exist_says_which_part_was_wrong()
    {
        var (_, provider) = Loaded();

        var results = await provider.SetAttributesAsync(
            new ObjectAttributeWrite
            {
                Objects = ["W99"],
                Attributes = [new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-9") }],
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, Assert.Single(results).Acknowledgement);
    }

    [Fact]
    public async Task The_attribute_names_a_host_discovers_are_the_ones_it_can_read()
    {
        var (_, provider) = Loaded();

        // The contract S14F17 exists for: a namelist that advertises an attribute nothing answers
        // is worse than a short list.
        var substrateNames = await provider.GetAttributeNamesAsync(
            "W01",
            SubstrateObjectProvider.SubstrateType,
            TestContext.Current.CancellationToken);

        var substrate = await ReadAsync(provider, "W01", SubstrateObjectProvider.SubstrateType, [.. substrateNames]);

        Assert.Equal(substrateNames.Count, substrate.Count);

        var locationNames = await provider.GetAttributeNamesAsync(
            "CHUCK",
            SubstrateObjectProvider.SubstrateLocationType,
            TestContext.Current.CancellationToken);

        var location = await ReadAsync(provider, "CHUCK", SubstrateObjectProvider.SubstrateLocationType, [.. locationNames]);

        Assert.Equal(locationNames.Count, location.Count);
    }

    [Fact]
    public async Task A_name_with_no_type_given_resolves_to_whichever_object_has_it()
    {
        var (_, provider) = Loaded();

        var substrate = await provider.GetAttributeNamesAsync("W01", string.Empty, TestContext.Current.CancellationToken);
        var location = await provider.GetAttributeNamesAsync("CHUCK", string.Empty, TestContext.Current.CancellationToken);

        Assert.Contains("SubstrateProcessingState", substrate);
        Assert.Contains("SubstrateLocState", location);
        Assert.DoesNotContain("SubstrateLocState", substrate);
    }
}
