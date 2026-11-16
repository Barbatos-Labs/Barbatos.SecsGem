// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// Stream 14 across a real link, with real object services at both ends.
/// </summary>
/// <remarks>
/// The attribute values go the whole way: built by a test, encoded by one end, decoded by the
/// other, stored, read back, and compared. <c>ATTRDATA</c> is the one item in E5 with no format of
/// its own, so that round trip is the claim this package makes.
/// </remarks>
public class ObjectServicesTests
{
    private static ObjectInMemoryProvider Carriers()
    {
        var provider = new ObjectInMemoryProvider();

        provider.Add("CAR-001", "Carrier");
        provider.Set("CAR-001", "SlotCount", SecsItem.U1(25));
        provider.Set("CAR-001", "LotId", SecsItem.A("LOT-2026-1115"));

        provider.Add("CAR-002", "Carrier");
        provider.Set("CAR-002", "SlotCount", SecsItem.U1(13));
        provider.Set("CAR-002", "LotId", SecsItem.A("LOT-2026-1116"));

        provider.Add("PORT-1", "LoadPort");
        provider.Set("PORT-1", "State", SecsItem.A("ReadyToLoad"));

        return provider;
    }

    // ---------------------------------------------------------------- namelists

    [Fact]
    public async Task Asking_which_object_types_exist_brings_back_the_names()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var types = await pair.Asker.GetObjectTypesAsync(ObjectPair.Token);

        Assert.Equal(2, types.Count);
        Assert.Contains("Carrier", types);
        Assert.Contains("LoadPort", types);
    }

    [Fact]
    public async Task Asking_which_instances_of_a_type_exist_brings_back_them()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var instances = await pair.Asker.GetInstancesAsync("Carrier", ObjectPair.Token);

        Assert.Equal(2, instances.Count);
        Assert.Contains("CAR-001", instances);
        Assert.Contains("CAR-002", instances);
        Assert.DoesNotContain("PORT-1", instances);
    }

    [Fact]
    public async Task Asking_about_a_type_with_no_instances_is_an_empty_list_rather_than_a_failure()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        Assert.Empty(await pair.Asker.GetInstancesAsync("ControlJob", ObjectPair.Token));
    }

    [Fact]
    public async Task Asking_which_attributes_an_object_has_brings_back_them()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var names = await pair.Asker.GetAttributeNamesAsync("CAR-001", "Carrier", ObjectPair.Token);

        Assert.Equal(2, names.Count);
        Assert.Contains("SlotCount", names);
        Assert.Contains("LotId", names);
    }

    // --------------------------------------------------------------------- gets

    [Fact]
    public async Task Reading_one_attribute_brings_back_its_value()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        string? lot = null;

        var found = await pair.Asker.GetAttributesAsync(
            "CAR-001",
            "Carrier",
            ["LotId"],
            values =>
            {
                // Copied inside the callback, which is the contract: the value is a view into the
                // reply, and the reply is gone when the method returns.
                lot = Assert.Single(values.Attributes).Value.GetString();
            },
            ObjectPair.Token);

        Assert.True(found);
        Assert.Equal("LOT-2026-1115", lot);
    }

    [Fact]
    public async Task Reading_with_an_empty_request_brings_back_every_attribute()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        List<string> names = [];

        await pair.Asker.GetAttributesAsync(
            "CAR-001",
            "Carrier",
            [],
            values => names.AddRange(values.Attributes.Select(attribute => attribute.Name)),
            ObjectPair.Token);

        // A zero-length ATTRID list is the only thing a request for everything can look like.
        Assert.Equal(2, names.Count);
        Assert.Contains("SlotCount", names);
        Assert.Contains("LotId", names);
    }

    [Fact]
    public async Task Reading_an_object_the_other_end_does_not_have_is_false_rather_than_a_failure()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var called = false;

        var found = await pair.Asker.GetAttributesAsync(
            "CAR-999",
            "Carrier",
            ["LotId"],
            _ => called = true,
            ObjectPair.Token);

        // S14F2 has no OBJACK in it, so absence is the only thing a get can say. False, not an
        // exception, and the callback never runs.
        Assert.False(found);
        Assert.False(called);
    }

    [Fact]
    public async Task Reading_several_objects_brings_back_one_entry_each()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        Dictionary<string, byte> slots = [];

        var count = await pair.Asker.GetAttributesAsync(
            ["CAR-001", "CAR-002"],
            ["SlotCount"],
            values => slots[values.ObjectSpecification] = Assert.Single(values.Attributes).Value.FirstValueOrDefault<byte>(),
            ObjectPair.Token);

        Assert.Equal(2, count);
        Assert.Equal(25, slots["CAR-001"]);
        Assert.Equal(13, slots["CAR-002"]);
    }

    [Fact]
    public async Task An_object_left_out_of_a_multi_get_is_one_the_other_end_does_not_have()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        List<string> seen = [];

        var count = await pair.Asker.GetAttributesAsync(
            ["CAR-001", "CAR-999", "CAR-002"],
            ["SlotCount"],
            values => seen.Add(values.ObjectSpecification),
            ObjectPair.Token);

        Assert.Equal(2, count);
        Assert.Equal(["CAR-001", "CAR-002"], seen);
    }

    [Fact]
    public async Task A_list_value_survives_the_round_trip()
    {
        var provider = Carriers();

        // ATTRDATA has no format of its own, so an attribute can be a structure. This is the
        // claim: the protocol carries whatever the object model says, and the library does not
        // need to know what that is.
        provider.Set(
            "CAR-001",
            "SlotMap",
            SecsItem.L(
                SecsItem.A("1"),
                SecsItem.L(SecsItem.U1(1), SecsItem.U1(2), SecsItem.U1(3)),
                SecsItem.Boolean(true)));

        await using var pair = ObjectPair.Start(provider);
        await pair.OpenAsync();

        var shape = string.Empty;

        await pair.Asker.GetAttributesAsync(
            "CAR-001",
            "Carrier",
            ["SlotMap"],
            values =>
            {
                var value = Assert.Single(values.Attributes).Value;

                Assert.True(value.IsList);
                Assert.Equal(3, value.Count);

                shape = string.Join(
                    ",",
                    value.Items[0].GetString(),
                    value.Items[1].Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    value.Items[2].FirstValueOrDefault<bool>().ToString());
            },
            ObjectPair.Token);

        Assert.Equal("1,3,True", shape);
    }

    // --------------------------------------------------------------------- sets

    [Fact]
    public async Task Writing_an_attribute_takes_and_a_later_read_shows_it()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "Carrier",
            [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-NEW") }],
            ObjectPair.Token);

        Assert.Equal(ObjectAcknowledgement.Successful, acknowledgement);

        string? lot = null;

        await pair.Asker.GetAttributesAsync(
            "CAR-001",
            "Carrier",
            ["LotId"],
            values => lot = Assert.Single(values.Attributes).Value.GetString(),
            ObjectPair.Token);

        Assert.Equal("LOT-NEW", lot);
    }

    [Fact]
    public async Task Writing_to_an_object_that_does_not_exist_says_which_part_was_wrong()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-999",
            "Carrier",
            [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-NEW") }],
            ObjectPair.Token);

        // Three of OBJACK's five codes name which part was wrong, which is unusual for an E5
        // acknowledgement and worth not throwing away.
        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, acknowledgement);
    }

    [Fact]
    public async Task Writing_the_wrong_type_for_an_object_that_exists_says_so()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "LoadPort",
            [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-NEW") }],
            ObjectPair.Token);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, acknowledgement);
    }

    [Fact]
    public async Task Writing_an_attribute_the_object_does_not_have_is_refused_and_changes_nothing()
    {
        var provider = Carriers();

        await using var pair = ObjectPair.Start(provider);
        await pair.OpenAsync();

        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "Carrier",
            [
                new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-NEW") },
                new ObjectAttribute { Name = "Invented", Value = SecsItem.A("x") },
            ],
            ObjectPair.Token);

        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, acknowledgement);

        // And the good attribute in the same set did not take either: a half-applied set is worse
        // than a refused one, so the provider checks everything before writing anything.
        string? lot = null;

        await pair.Asker.GetAttributesAsync(
            "CAR-001",
            "Carrier",
            ["LotId"],
            values => lot = Assert.Single(values.Attributes).Value.GetString(),
            ObjectPair.Token);

        Assert.Equal("LOT-2026-1115", lot);
    }

    [Fact]
    public async Task A_provider_that_allows_new_attributes_takes_one()
    {
        var provider = Carriers();
        provider.AllowNewAttributes = true;

        await using var pair = ObjectPair.Start(provider);
        await pair.OpenAsync();

        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "Carrier",
            [new ObjectAttribute { Name = "Invented", Value = SecsItem.A("x") }],
            ObjectPair.Token);

        Assert.Equal(ObjectAcknowledgement.Successful, acknowledgement);
    }

    [Fact]
    public async Task Writing_the_same_attribute_on_several_objects_reports_on_each()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        var results = await pair.Asker.SetAttributesAsync(
            ["CAR-001", "CAR-002", "CAR-999"],
            [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-SHARED") }],
            ObjectPair.Token);

        Assert.Equal(3, results.Count);
        Assert.Equal(ObjectAcknowledgement.Successful, results[0].Acknowledgement);
        Assert.Equal("CAR-001", results[0].ObjectSpecification);
        Assert.Equal(ObjectAcknowledgement.Successful, results[1].Acknowledgement);
        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, results[2].Acknowledgement);

        // One value, several objects, one transaction - which is what the message is for.
        Dictionary<string, string> lots = [];

        await pair.Asker.GetAttributesAsync(
            ["CAR-001", "CAR-002"],
            ["LotId"],
            values => lots[values.ObjectSpecification] = Assert.Single(values.Attributes).Value.GetString(),
            ObjectPair.Token);

        Assert.Equal("LOT-SHARED", lots["CAR-001"]);
        Assert.Equal("LOT-SHARED", lots["CAR-002"]);
    }

    // ----------------------------------------------------------- both directions

    [Fact]
    public async Task Either_end_can_ask_when_either_end_has_objects()
    {
        var jobs = new ObjectInMemoryProvider();
        jobs.Add("JOB-7", "ControlJob");
        jobs.Set("JOB-7", "State", SecsItem.A("Queued"));

        await using var pair = ObjectPair.Start(Carriers(), askerProvider: jobs);
        await pair.OpenAsync();

        // The asking end asks, as everywhere else in this file.
        Assert.Contains("Carrier", await pair.Asker.GetObjectTypesAsync(ObjectPair.Token));

        // And then the answering end asks the asking end, which is the thing stream 14 allows and
        // no other stream in this library does.
        var types = await pair.Answerer.GetObjectTypesAsync(ObjectPair.Token);

        Assert.Equal("ControlJob", Assert.Single(types));

        var state = string.Empty;

        await pair.Answerer.GetAttributesAsync(
            "JOB-7",
            "ControlJob",
            ["State"],
            values => state = Assert.Single(values.Attributes).Value.GetString(),
            ObjectPair.Token);

        Assert.Equal("Queued", state);
    }

    [Fact]
    public async Task An_end_with_no_objects_says_it_cannot_answer()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        Assert.False(pair.Asker.CanAnswer);
        Assert.True(pair.Answerer.CanAnswer);
    }

    // ------------------------------------------------------------- completeness

    [Fact]
    public async Task Every_primary_the_other_end_declares_can_be_sent()
    {
        await using var pair = ObjectPair.Start(Carriers());
        await pair.OpenAsync();

        await pair.Asker.GetObjectTypesAsync(ObjectPair.Token);
        await pair.Asker.GetInstancesAsync("Carrier", ObjectPair.Token);
        await pair.Asker.GetAttributeNamesAsync("CAR-001", "Carrier", ObjectPair.Token);
        await pair.Asker.GetAttributesAsync("CAR-001", "Carrier", ["LotId"], _ => { }, ObjectPair.Token);
        await pair.Asker.GetAttributesAsync(["CAR-001"], ["LotId"], _ => { }, ObjectPair.Token);

        await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "Carrier",
            [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-A") }],
            ObjectPair.Token);

        await pair.Asker.SetAttributesAsync(
            ["CAR-001"],
            [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-B") }],
            ObjectPair.Token);

        // The claim this test exists for: the seven primaries declared are the seven the methods
        // here can send. A method added without its pair in the list shows up as a gap.
        foreach (var message in ObjectServices.HandledMessages)
        {
            Assert.Contains(message, pair.Asked);
        }
    }
}
