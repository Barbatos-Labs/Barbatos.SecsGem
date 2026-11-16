// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material.UnitTests;

/// <summary>
/// What arrives from a machine that is not this library.
/// </summary>
/// <remarks>
/// <para>
/// This file carries more weight in this package than in any other, and the reason is the
/// provenance. Not one of stream 3's thirty-six catalogue rows is marked as a definition rather
/// than a representative subset, so the body shapes this library writes are a careful reading
/// rather than a quotation. What can be tested is the other half of the bargain: that a machine
/// which read the standard differently is still understood.
/// </para>
/// <para>
/// So each test here plays one end by hand - a shape this library would never emit - and asserts
/// that the other end copes, or fails honestly.
/// </para>
/// </remarks>
public class MaterialWireTests
{
    private static SecsMessage Reply(byte function, SecsItem body)
        => new(3, function, replyExpected: false) { Item = body };

    // -------------------------------------------- what the asking end tolerates

    [Fact]
    public async Task A_reply_with_no_acknowledgement_code_is_a_link_failure_and_never_an_acceptance()
    {
        await using var pair = MaterialPair.Start(hostAnswer: _ => Reply(6, SecsItem.L()));
        await pair.OpenAsync();

        // The failure this guards against: a reader that takes the first byte of an empty body
        // gets nought, and nought is ACKC3 Accepted.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Equipment.ReportFoundAsync(
                new MaterialLocation { MaterialId = "LOT-A", Location = 1 },
                MaterialPair.Token));
    }

    [Fact]
    public async Task An_acknowledgement_wrapped_in_a_list_of_one_is_still_read()
    {
        await using var pair = MaterialPair.Start(hostAnswer: _ => Reply(6, SecsItem.L(SecsItem.B(0))));
        await pair.OpenAsync();

        Assert.Equal(
            MaterialAcknowledgement.Accepted,
            await pair.Equipment.ReportFoundAsync(
                new MaterialLocation { MaterialId = "LOT-A", Location = 1 },
                MaterialPair.Token));
    }

    [Fact]
    public async Task An_undefined_acknowledgement_code_is_read_as_denied()
    {
        await using var pair = MaterialPair.Start(hostAnswer: _ => Reply(14, SecsItem.B(9)));
        await pair.OpenAsync();

        // ACKC3 has two codes. Whatever a host means by 9, it is not 0, and 0 is the only code
        // that means accepted.
        Assert.Equal(
            MaterialAcknowledgement.Denied,
            await pair.Equipment.ReportStatusChangeAsync(
                new MaterialStatusChange { MaterialId = "LOT-A", Location = 1, Status = 3 },
                MaterialPair.Token));
    }

    [Fact]
    public async Task A_reserved_grant_code_is_a_refusal_rather_than_a_grant()
    {
        await using var pair = MaterialPair.Start(equipmentAnswer: _ => Reply(16, SecsItem.B(40)));
        await pair.OpenAsync();

        var grant = await pair.Host.InquireMultiBlockAsync(1, MaterialPair.Token);

        Assert.Equal(MaterialGrant.Other, grant);
        Assert.NotEqual(MaterialGrant.Accepted, grant);
    }

    [Fact]
    public async Task Status_sent_as_one_bare_entry_reads_as_the_one_piece_of_material_it_is()
    {
        // A machine with one cassette, not bothering to wrap it in a list of one. The item list
        // cannot say whether that is legal, so it is accepted.
        await using var pair = MaterialPair.Start(equipmentAnswer: _ => Reply(
            2,
            SecsItem.L(
                SecsItem.A("LOT-A"),
                SecsItem.U4(3u),
                SecsItem.B(25))));

        await pair.OpenAsync();

        var material = await pair.Host.RequestStatusAsync(MaterialPair.Token);

        var only = Assert.Single(material);
        Assert.Equal("LOT-A", only.MaterialId);
        Assert.Equal(3u, only.Location);
        Assert.Equal(25, only.Quantity);
    }

    [Fact]
    public async Task Status_sent_as_a_flat_run_of_fields_reads_as_the_entries_it_is()
    {
        // Two pieces of material laid end to end with no inner lists at all.
        await using var pair = MaterialPair.Start(equipmentAnswer: _ => Reply(
            2,
            SecsItem.L(
                SecsItem.A("LOT-A"),
                SecsItem.U4(1u),
                SecsItem.B(1),
                SecsItem.A("LOT-B"),
                SecsItem.U4(2u),
                SecsItem.B(2))));

        await pair.OpenAsync();

        var material = await pair.Host.RequestStatusAsync(MaterialPair.Token);

        Assert.Equal(2, material.Count);
        Assert.Equal("LOT-A", material[0].MaterialId);
        Assert.Equal(1u, material[0].Location);
        Assert.Equal("LOT-B", material[1].MaterialId);
        Assert.Equal(2u, material[1].Location);
    }

    [Fact]
    public async Task An_entry_short_of_its_fields_is_dropped_rather_than_padded()
    {
        await using var pair = MaterialPair.Start(equipmentAnswer: _ => Reply(
            2,
            SecsItem.L(
                SecsItem.L(SecsItem.A("LOT-A"), SecsItem.U4(1u), SecsItem.B(1)),
                SecsItem.L(SecsItem.A("LOT-SHORT"), SecsItem.U4(2u)))));

        await pair.OpenAsync();

        var material = await pair.Host.RequestStatusAsync(MaterialPair.Token);

        // Padding the short one would invent a quantity. Dropping it loses a row a host can see
        // is missing; inventing one puts a number in a fab report that nobody sent.
        var only = Assert.Single(material);
        Assert.Equal("LOT-A", only.MaterialId);
    }

    [Fact]
    public async Task A_location_in_another_numeric_format_is_still_read()
    {
        await using var pair = MaterialPair.Start(equipmentAnswer: _ => Reply(
            2,
            SecsItem.L(SecsItem.L(
                SecsItem.A("LOT-A"),

                // LOC is U4 in the catalogue. A machine that sends U1 is being loose with a
                // format, not wrong about where the cassette is.
                SecsItem.U1(3),
                SecsItem.U1(25)))));

        await pair.OpenAsync();

        var material = await pair.Host.RequestStatusAsync(MaterialPair.Token);

        Assert.Equal(3u, Assert.Single(material).Location);
    }

    [Fact]
    public async Task A_status_change_whose_status_arrives_in_another_format_is_still_read()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        MaterialStatusChangeEventArgs? seen = null;
        pair.Host.MaterialStatusChanged += (_, args) => seen = args;

        // MATLSTATUS written as U1 rather than the B[1] this library chose, and LOC as text. Since
        // nothing in the catalogue defines MATLSTATUS at all, a machine is as entitled to this as
        // to anything else.
        using var primary = new SecsMessage(3, 13, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("LOT-A"), SecsItem.A("12"), SecsItem.U1(7)),
        };

        using var reply = await pair.EquipmentLink.SendAsync(primary, MaterialPair.Token);

        Assert.NotNull(seen);
        Assert.Equal(12u, seen.Change.Location);
        Assert.Equal(7, seen.Change.Status);
        Assert.Equal((byte)MaterialAcknowledgement.Accepted, reply.Item?.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_reply_that_is_not_a_list_is_a_link_failure()
    {
        await using var pair = MaterialPair.Start(equipmentAnswer: _ => Reply(2, SecsItem.A("nothing loaded")));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.RequestStatusAsync(MaterialPair.Token));
    }

    // -------------------------------------------- what the answering ends refuse

    [Fact]
    public async Task A_report_the_host_cannot_read_is_denied_inside_the_transaction()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 5, replyExpected: true) { Item = SecsItem.A("not a location") };
        using var reply = await pair.EquipmentLink.SendAsync(primary, MaterialPair.Token);

        Assert.Equal(6, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)MaterialAcknowledgement.Denied, reply.Item.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_transfer_request_the_equipment_cannot_read_is_denied()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 11, replyExpected: true) { Item = SecsItem.U4(1u) };
        using var reply = await pair.HostLink.SendAsync(primary, MaterialPair.Token);

        Assert.Equal(12, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)MaterialAcknowledgement.Denied, reply.Item.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_location_request_the_equipment_cannot_read_is_an_empty_list_because_E5_gives_it_no_code()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 9, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.U4(1u), SecsItem.U4(2u)),
        };

        using var reply = await pair.HostLink.SendAsync(primary, MaterialPair.Token);

        // S3F10 carries material and a location and nothing else - there is no code in it to say
        // "that was not a material identifier" - so the zero-length list is the only answer left.
        Assert.Equal(10, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Empty(reply.Item.Items.ToArray());
    }

    [Fact]
    public async Task A_material_identifier_wrapped_in_a_list_of_one_is_still_found()
    {
        var source = new MaterialInMemorySource();
        source.Load("LOT-A", location: 5);

        await using var pair = MaterialPair.Start(source);
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 9, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A("LOT-A")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, MaterialPair.Token);

        Assert.True(MaterialCodecProbe.IsLocation(reply, "LOT-A", 5u));
    }

    [Fact]
    public async Task A_multi_block_inquire_whose_data_id_is_unreadable_is_refused_rather_than_granted()
    {
        await using var pair = MaterialPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 15, replyExpected: true) { Item = SecsItem.A("not a number") };
        using var reply = await pair.HostLink.SendAsync(primary, MaterialPair.Token);

        // Granting a block whose identifier could not be read would have the equipment accepting
        // data it cannot file.
        Assert.Equal(16, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)MaterialGrant.DuplicateOrInvalid, reply.Item.FirstValueOrDefault<byte>());
    }
}

/// <summary>Reads a reply body in a test without reaching into the package's internals.</summary>
/// <remarks>
/// The codec is internal and there is no <c>InternalsVisibleTo</c> in this repository, so a test
/// that wants to look at a raw reply reads the items itself. Three lines, and it keeps the rule.
/// </remarks>
internal static class MaterialCodecProbe
{
    internal static bool IsLocation(SecsMessage message, string materialId, uint location)
        => message.Item is { IsList: true, Count: 2 } body
            && string.Equals(body.Items[0].GetString().TrimEnd(), materialId, StringComparison.Ordinal)
            && body.Items[1].FirstValueOrDefault<uint>() == location;
}
