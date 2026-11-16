// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Objects.UnitTests;

/// <summary>
/// What arrives from an end that is not this library.
/// </summary>
/// <remarks>
/// Twenty-seven of stream 14's twenty-eight catalogue rows are marked as a representative subset,
/// so the nesting this library writes is a reading rather than a quotation. What these tests pin
/// down is the other half: that a body nested differently, or a code in another format, is still
/// understood - and that a body which cannot be understood fails loudly rather than quietly.
/// </remarks>
public class ObjectWireTests
{
    private static SecsMessage Reply(byte function, SecsItem body)
        => new(14, function, replyExpected: false) { Item = body };

    private static ObjectAttribute Attribute(string name, string value)
        => new() { Name = name, Value = SecsItem.A(value) };

    // ----------------------------------------------- what the asking end accepts

    [Fact]
    public async Task A_set_answered_with_no_code_is_a_link_failure_and_never_a_success()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(4, SecsItem.L()));
        await pair.OpenAsync();

        // Nought is OBJACK Successful, so a reader that takes the first byte of an empty body
        // reports a configuration change that never happened.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Asker.SetAttributesAsync("CAR-001", "Carrier", [Attribute("LotId", "x")], ObjectPair.Token));
    }

    [Fact]
    public async Task A_set_reply_that_carries_only_the_code_is_still_read()
    {
        // No echo of the attributes, which the item list has but which costs a copy per value - so
        // an end that leaves it out is being economical rather than wrong.
        await using var pair = ObjectPair.Start(answer: _ => Reply(4, SecsItem.L(SecsItem.B(3))));
        await pair.OpenAsync();

        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "Carrier",
            [Attribute("LotId", "x")],
            ObjectPair.Token);

        Assert.Equal(ObjectAcknowledgement.InvalidObjectInstance, acknowledgement);
    }

    [Fact]
    public async Task An_acknowledgement_in_a_numeric_format_is_still_read()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(
            4,
            SecsItem.L(SecsItem.L(), SecsItem.U4(4u))));

        await pair.OpenAsync();

        // OBJACK is B[1] in the catalogue. U4 is a machine being loose with a format, not a
        // machine saying something else.
        Assert.Equal(
            ObjectAcknowledgement.InvalidAttribute,
            await pair.Asker.SetAttributesAsync("CAR-001", "Carrier", [Attribute("LotId", "x")], ObjectPair.Token));
    }

    [Fact]
    public async Task An_undefined_acknowledgement_code_reads_as_an_error()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(
            4,
            SecsItem.L(SecsItem.L(), SecsItem.B(9))));

        await pair.OpenAsync();

        // E5 names five codes. Whatever 9 means, it is not Successful.
        var acknowledgement = await pair.Asker.SetAttributesAsync(
            "CAR-001",
            "Carrier",
            [Attribute("LotId", "x")],
            ObjectPair.Token);

        Assert.Equal(ObjectAcknowledgement.Error, acknowledgement);
        Assert.NotEqual(ObjectAcknowledgement.Successful, acknowledgement);
    }

    [Fact]
    public async Task A_namelist_sent_as_one_bare_name_reads_as_the_one_name_it_is()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(14, SecsItem.A("Carrier")));
        await pair.OpenAsync();

        Assert.Equal("Carrier", Assert.Single(await pair.Asker.GetObjectTypesAsync(ObjectPair.Token)));
    }

    [Fact]
    public async Task Attributes_sent_flat_rather_than_nested_read_as_the_pairs_they_are()
    {
        // ATTRID, ATTRDATA, ATTRID, ATTRDATA with no inner lists. The item list cannot say whether
        // that is legal, so it is accepted.
        await using var pair = ObjectPair.Start(answer: _ => Reply(
            2,
            SecsItem.L(
                SecsItem.A("CAR-001"),
                SecsItem.L(
                    SecsItem.A("LotId"),
                    SecsItem.A("LOT-FLAT"),
                    SecsItem.A("SlotCount"),
                    SecsItem.U1(25)))));

        await pair.OpenAsync();

        Dictionary<string, string> read = [];

        var found = await pair.Asker.GetAttributesAsync(
            "CAR-001",
            "Carrier",
            [],
            values =>
            {
                foreach (var attribute in values.Attributes)
                {
                    read[attribute.Name] = attribute.Value.Format.IsText()
                        ? attribute.Value.GetString()
                        : attribute.Value.FirstValueOrDefault<byte>().ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            },
            ObjectPair.Token);

        Assert.True(found);
        Assert.Equal("LOT-FLAT", read["LotId"]);
        Assert.Equal("25", read["SlotCount"]);
    }

    [Fact]
    public async Task An_odd_number_of_flat_attribute_fields_is_a_link_failure()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(
            2,
            SecsItem.L(
                SecsItem.A("CAR-001"),
                SecsItem.L(SecsItem.A("LotId"), SecsItem.A("LOT-A"), SecsItem.A("SlotCount")))));

        await pair.OpenAsync();

        // A name with no value is a body somebody truncated. Reading it as a pair would pair
        // "SlotCount" with whatever came next, which here is nothing and elsewhere is wrong.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Asker.GetAttributesAsync("CAR-001", "Carrier", [], _ => { }, ObjectPair.Token));
    }

    [Fact]
    public async Task A_get_reply_that_is_not_a_list_is_a_link_failure()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(2, SecsItem.A("no such object")));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Asker.GetAttributesAsync("CAR-001", "Carrier", [], _ => { }, ObjectPair.Token));
    }

    [Fact]
    public async Task A_multi_set_entry_that_leaves_the_echo_out_is_still_read()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(
            22,
            SecsItem.L(
                SecsItem.L(SecsItem.A("CAR-001"), SecsItem.B(0)),
                SecsItem.L(SecsItem.A("CAR-002"), SecsItem.B(2)))));

        await pair.OpenAsync();

        var results = await pair.Asker.SetAttributesAsync(
            ["CAR-001", "CAR-002"],
            [Attribute("LotId", "x")],
            ObjectPair.Token);

        // The code is the last item of the entry, so an entry of two items reads as well as one of
        // three.
        Assert.Equal(2, results.Count);
        Assert.Equal(ObjectAcknowledgement.Successful, results[0].Acknowledgement);
        Assert.Equal(ObjectAcknowledgement.InvalidObjectType, results[1].Acknowledgement);
    }

    [Fact]
    public async Task A_multi_get_reply_whose_entry_is_not_a_list_is_a_link_failure()
    {
        await using var pair = ObjectPair.Start(answer: _ => Reply(
            20,
            SecsItem.L(SecsItem.A("CAR-001"))));

        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Asker.GetAttributesAsync(["CAR-001"], [], _ => { }, ObjectPair.Token));
    }

    // ------------------------------------------- what the answering end refuses

    [Fact]
    public async Task A_get_request_the_other_end_cannot_read_is_an_empty_list()
    {
        await using var pair = ObjectPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(14, 1, replyExpected: true) { Item = SecsItem.A("not a request") };
        using var reply = await pair.AskerLink.SendAsync(primary, ObjectPair.Token);

        // S14F2 has no acknowledgement code, so a zero-length list is the only answer E5 leaves
        // for a request that could not be read.
        Assert.Equal(2, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Empty(reply.Item.Items.ToArray());
    }

    [Fact]
    public async Task A_set_request_the_other_end_cannot_read_is_an_error_rather_than_a_success()
    {
        await using var pair = ObjectPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(14, 3, replyExpected: true) { Item = SecsItem.U4(1u) };
        using var reply = await pair.AskerLink.SendAsync(primary, ObjectPair.Token);

        Assert.Equal(4, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal(2, reply.Item.Count);
        Assert.Equal((byte)ObjectAcknowledgement.Error, reply.Item.Items[1].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task An_object_type_request_wrapped_in_a_list_of_one_is_still_answered()
    {
        var provider = new ObjectInMemoryProvider();
        provider.Add("CAR-001", "Carrier");

        await using var pair = ObjectPair.Start(provider);
        await pair.OpenAsync();

        using var primary = new SecsMessage(14, 15, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("Carrier")),
        };

        using var reply = await pair.AskerLink.SendAsync(primary, ObjectPair.Token);

        Assert.Equal(16, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Equal("CAR-001", reply.Item.Items[0].GetString().TrimEnd());
    }

    /// <summary>
    /// The echo in a set reply carries the values that were set.
    /// </summary>
    /// <remarks>
    /// <strong>What this does not prove.</strong> The codec copies those values rather than putting
    /// the request's own items into the reply, and that copy is reasoned rather than tested: one
    /// item in two message trees is a pooling fault, and a pooling fault is invisible through the
    /// public surface of a single transaction - the asking end reads bytes off a wire either way.
    /// What is asserted here is the content of the echo and that the request is still intact
    /// beside it. The reason for the copy is in the codec, next to the code that does it.
    /// </remarks>
    [Fact]
    public async Task A_set_reply_echoes_the_values_that_were_set()
    {
        var provider = new ObjectInMemoryProvider();
        provider.Add("CAR-001", "Carrier");
        provider.Set("CAR-001", "LotId", SecsItem.A("LOT-OLD"));

        await using var pair = ObjectPair.Start(provider);
        await pair.OpenAsync();

        using var primary = new SecsMessage(14, 3, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A("CAR-001"),
                SecsItem.A("Carrier"),
                SecsItem.L(SecsItem.L(SecsItem.A("LotId"), SecsItem.A("LOT-NEW")))),
        };

        using var reply = await pair.AskerLink.SendAsync(primary, ObjectPair.Token);

        Assert.NotNull(reply.Item);
        Assert.Equal(2, reply.Item.Count);
        Assert.Equal((byte)ObjectAcknowledgement.Successful, reply.Item.Items[1].FirstValueOrDefault<byte>());

        var echoed = reply.Item.Items[0];

        Assert.True(echoed.IsList);
        Assert.Equal("LOT-NEW", echoed.Items[0].Items[1].GetString());
        Assert.Equal("LOT-NEW", primary.Item!.Items[2].Items[0].Items[1].GetString());
    }
}
