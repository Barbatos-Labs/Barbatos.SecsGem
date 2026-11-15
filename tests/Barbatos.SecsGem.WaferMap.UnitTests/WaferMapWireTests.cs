// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.WaferMap.UnitTests;

/// <summary>
/// What arrives from a machine that is not this library.
/// </summary>
/// <remarks>
/// <para>
/// Two real ends can only exercise what this library itself writes, so every tolerance in the codec
/// and every refusal in the two ends would be untested by the round-trip tests alone. Here one end
/// is written by hand: either the host answers with a body a test composed, or a raw primary is
/// pushed down the equipment's own connection at the real host.
/// </para>
/// <para>
/// These are the shapes that decide whether this package works against somebody else's prober. A
/// nesting this library does not write, a count in a format it does not choose, an acknowledgement
/// code E5 leaves to the vendor - each one is a day of somebody staring at a hex dump if it is got
/// wrong.
/// </para>
/// </remarks>
public class WaferMapWireTests
{
    private static SecsMessage Reply(byte function, SecsItem body)
        => new(12, function, replyExpected: false) { Item = body };

    // -------------------------------------------- what the equipment tolerates

    [Fact]
    public async Task A_reply_with_no_acknowledgement_code_is_a_link_failure_and_never_an_acceptance()
    {
        // The failure this guards against: a reader that takes the first byte of an empty body,
        // gets nought, and reports a map the host never took as accepted.
        await using var pair = WaferMapPair.Start(answer: _ => Reply(2, SecsItem.L()));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Equipment.SendSetupAsync(
                new WaferMapSetup { MaterialId = "W01" },
                WaferMapPair.Token));
    }

    [Fact]
    public async Task Map_data_answered_with_no_code_is_a_link_failure_too()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(8, SecsItem.L()));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Equipment.SendRowDataAsync(
                new WaferMapRowData { MaterialId = "W01" },
                WaferMapPair.Token));
    }

    [Fact]
    public async Task A_transmit_inquire_answered_with_no_code_is_a_link_failure()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(6, SecsItem.L()));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Equipment.InquireTransmitAsync(
                new WaferMapTransmitInquiry { MaterialId = "W01" },
                WaferMapPair.Token));
    }

    [Fact]
    public async Task An_acknowledgement_wrapped_in_a_list_of_one_is_still_read()
    {
        // E5 writes S12F2 as a single item and most equipment sends one, but an L,1 wrapper is
        // common enough in the field that refusing it would report a good acknowledgement as a
        // protocol failure.
        await using var pair = WaferMapPair.Start(answer: _ => Reply(2, SecsItem.L(SecsItem.B(0))));
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendSetupAsync(
            new WaferMapSetup { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.Equal(WaferMapSetupAcknowledgement.Accepted, acknowledgement);
    }

    [Fact]
    public async Task A_vendor_specific_rejection_code_is_still_a_rejection()
    {
        // SDACK 1 to 63 are all rejections and E5 names none of the reasons. Code 5 is as valid a
        // refusal as code 1, and reading it as anything but a refusal would be the worst way round.
        await using var pair = WaferMapPair.Start(answer: _ => Reply(2, SecsItem.B(5)));
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendSetupAsync(
            new WaferMapSetup { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.Equal(WaferMapSetupAcknowledgement.Rejected, acknowledgement);
    }

    [Fact]
    public async Task A_reserved_grant_code_is_a_refusal_rather_than_a_grant()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(6, SecsItem.B(7)));
        await pair.OpenAsync();

        var grant = await pair.Equipment.InquireTransmitAsync(
            new WaferMapTransmitInquiry { MaterialId = "W01" },
            WaferMapPair.Token);

        // GRNT1 4 to 63 are reserved. Whatever a vendor means by 7, it is not nought, and nought
        // is the only code that means send it.
        Assert.Equal(WaferMapTransmitGrant.Other, grant);
        Assert.NotEqual(WaferMapTransmitGrant.Granted, grant);
    }

    [Fact]
    public async Task An_undefined_acknowledgement_for_map_data_is_read_as_rejected()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(10, SecsItem.B(3)));
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendArrayDataAsync(
            new WaferMapArrayData { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.Equal(WaferMapDataAcknowledgement.Rejected, acknowledgement);
    }

    [Fact]
    public async Task A_map_sent_flat_rather_than_nested_reads_as_the_one_row_it_is()
    {
        // The nesting of S12F14 is the one thing the catalogue cannot pin down, so a sender that
        // leaves RSINF and BINLT bare in the body is not wrong about anything that matters.
        await using var pair = WaferMapPair.Start(answer: _ => Reply(
            14,
            SecsItem.L(
                SecsItem.A("W01"),
                SecsItem.U1(1),
                SecsItem.I2(6, 0, 1),
                SecsItem.B(4, 5, 6))));

        await pair.OpenAsync();

        var data = await pair.Equipment.RequestRowDataAsync(
            new WaferMapDataRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(data);
        var row = Assert.Single(data.Rows);
        Assert.Equal(6, row.Row);
        Assert.Equal(1, row.Increment);
        Assert.Equal([4, 5, 6], row.Bins.ToArray());
    }

    [Fact]
    public async Task A_flattened_list_of_entries_reads_as_the_entries_it_is()
    {
        // A list of descriptors and bins alternating, rather than a list of pairs.
        await using var pair = WaferMapPair.Start(answer: _ => Reply(
            18,
            SecsItem.L(
                SecsItem.A("W01"),
                SecsItem.U1(1),
                SecsItem.L(
                    SecsItem.I2(1, 2),
                    SecsItem.B(7),
                    SecsItem.I2(3, 4),
                    SecsItem.B(8)))));

        await pair.OpenAsync();

        var data = await pair.Equipment.RequestPointDataAsync(
            new WaferMapDataRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(data);
        Assert.Equal(2, data.Points.Count);
        Assert.Equal(new WaferMapPosition(1, 2), data.Points[0].Position);
        Assert.Equal([7], data.Points[0].Bins.ToArray());
        Assert.Equal(new WaferMapPosition(3, 4), data.Points[1].Position);
        Assert.Equal([8], data.Points[1].Bins.ToArray());
    }

    [Fact]
    public async Task Reference_points_in_one_item_read_as_the_points_they_are()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(
            4,
            SecsItem.L(
                SecsItem.A("W01"),
                SecsItem.U1(1),
                SecsItem.U1(0),
                SecsItem.U1(0),
                SecsItem.U1(0),

                // REFP is I4[2] per point, so one item of four values is two points. More than one
                // host writes it this way.
                SecsItem.I4(-100, -200, 300, 400),
                SecsItem.A("um"),
                SecsItem.U2(1),
                SecsItem.U2(1),
                SecsItem.U2(2),
                SecsItem.U4(2u),
                SecsItem.A("P"),
                SecsItem.B(),
                SecsItem.A("."),
                SecsItem.U4(4u))));

        await pair.OpenAsync();

        var setup = await pair.Equipment.RequestSetupAsync(
            new WaferMapSetupRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(setup);
        Assert.Equal(
            [new WaferMapPosition(-100, -200), new WaferMapPosition(300, 400)],
            setup.ReferencePoints);
    }

    [Fact]
    public async Task Counts_and_codes_sent_in_other_numeric_formats_are_still_read()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(
            4,
            SecsItem.L(
                SecsItem.A("W01"),

                // IDTYP as U2 rather than U1, ROWCT as U4 rather than U2, COLCT as U1 rather than
                // U4, MLCL as U2. Every one of them is a machine being loose with a format, and
                // every one of them is a map that should still arrive.
                SecsItem.U2(2),
                SecsItem.U1(1),
                SecsItem.U1(2),
                SecsItem.U1(3),
                SecsItem.L(),
                SecsItem.A("mm"),
                SecsItem.U2(10),
                SecsItem.U2(20),
                SecsItem.U4(41u),
                SecsItem.U1(37),
                SecsItem.A("PRD"),
                SecsItem.B(),

                // NULBC as a number rather than as the one character E5 writes it as.
                SecsItem.U1(46),
                SecsItem.U2(1_517))));

        await pair.OpenAsync();

        var setup = await pair.Equipment.RequestSetupAsync(
            new WaferMapSetupRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(setup);
        Assert.Equal(WaferMapIdType.WaferIdAndOrientation, setup.IdType);
        Assert.Equal(41, setup.RowCount);
        Assert.Equal(37u, setup.ColumnCount);
        Assert.Equal('.', setup.NullBinCode);
        Assert.Equal(1_517u, setup.MapLocationCount);
    }

    [Fact]
    public async Task A_reply_that_is_not_map_data_at_all_is_a_link_failure()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(4, SecsItem.A("no map for you")));
        await pair.OpenAsync();

        // Not a zero-length list, which means "I have none" - a text item where a list belongs is
        // a machine this end cannot talk to, and saying so beats inventing an empty map.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Equipment.RequestSetupAsync(
                new WaferMapSetupRequest { MaterialId = "W01" },
                WaferMapPair.Token));
    }

    [Fact]
    public async Task A_coordinate_that_is_not_a_number_is_a_link_failure()
    {
        await using var pair = WaferMapPair.Start(answer: _ => Reply(
            16,
            SecsItem.L(
                SecsItem.A("W01"),
                SecsItem.U1(1),
                SecsItem.A("1,2"),
                SecsItem.B(1, 2))));

        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Equipment.RequestArrayDataAsync(
                new WaferMapDataRequest { MaterialId = "W01" },
                WaferMapPair.Token));
    }

    // ------------------------------------------------ what the host refuses

    [Fact]
    public async Task A_setup_the_host_cannot_read_is_refused_inside_the_transaction()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(12, 1, replyExpected: true)
        {
            Item = SecsItem.A("this is not a setup"),
        };

        using var reply = await pair.EquipmentLink.SendAsync(primary, WaferMapPair.Token);

        // The equipment learns in the transaction it is already waiting on, rather than waiting out
        // its T3 for a message the host decided not to send.
        Assert.Equal(12, reply.S);
        Assert.Equal(2, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)WaferMapSetupAcknowledgement.Rejected, reply.Item.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task Map_data_the_host_cannot_read_is_refused_in_every_one_of_the_three_formats()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        foreach (var (function, reply) in new (byte Primary, byte Reply)[] { (7, 8), (9, 10), (11, 12) })
        {
            using var primary = new SecsMessage(12, function, replyExpected: true)
            {
                Item = SecsItem.A("this is not a map"),
            };

            using var answer = await pair.EquipmentLink.SendAsync(primary, WaferMapPair.Token);

            Assert.Equal(reply, answer.F);
            Assert.NotNull(answer.Item);
            Assert.Equal(
                (byte)WaferMapDataAcknowledgement.Rejected,
                answer.Item.FirstValueOrDefault<byte>());
        }
    }

    [Fact]
    public async Task A_transmit_inquire_the_host_cannot_read_is_refused_as_an_invalid_material()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(12, 5, replyExpected: true) { Item = SecsItem.B(1, 2, 3) };
        using var reply = await pair.EquipmentLink.SendAsync(primary, WaferMapPair.Token);

        Assert.Equal(6, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)WaferMapTransmitGrant.InvalidMaterial, reply.Item.FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_request_the_host_cannot_read_is_answered_with_no_map_because_E5_gives_it_no_code()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(12, 13, replyExpected: true) { Item = SecsItem.A("nonsense") };
        using var reply = await pair.EquipmentLink.SendAsync(primary, WaferMapPair.Token);

        // S12F14 carries map data and nothing else - there is no acknowledgement code in it to
        // carry a refusal - so a zero-length list is the only answer E5 leaves open.
        Assert.Equal(14, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Empty(reply.Item.Items.ToArray());
    }

    [Fact]
    public async Task An_error_report_whose_code_E5_does_not_define_is_dropped_rather_than_renamed()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        var seen = 0;
        pair.Host.ErrorReported += (_, _) => Interlocked.Increment(ref seen);

        using var bad = new SecsMessage(12, 19, replyExpected: false)
        {
            Item = SecsItem.L(SecsItem.B(9), SecsItem.U1(0)),
        };

        await pair.EquipmentLink.PostAsync(bad, WaferMapPair.Token);

        // Then a good one, which is what makes this test able to fail: it proves the first was
        // dropped rather than that nothing arrives at all.
        await pair.Equipment.ReportErrorAsync(WaferMapError.NoCarrierAtLocation, 1, WaferMapPair.Token);

        await WaferMapPair.WaitUntilAsync(() => seen > 0, "the second report to arrive");

        Assert.Equal(1, seen);
    }
}
