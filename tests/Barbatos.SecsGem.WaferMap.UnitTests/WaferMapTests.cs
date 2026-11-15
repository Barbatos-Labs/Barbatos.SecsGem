// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.WaferMap.UnitTests;

/// <summary>
/// Stream 12 across a real link, with a real equipment at one end and a real host at the other.
/// </summary>
/// <remarks>
/// Every test here goes the whole way: the equipment builds a body, a frame crosses a pipe, the
/// host decodes it, the store decides, the reply comes back and the equipment decodes that. So a
/// field that survives the round trip has been written and read by the two halves of the codec
/// independently, which is the only claim worth making about a map format.
/// </remarks>
public class WaferMapTests
{
    private static WaferMapSetup Setup(string material = "W01") => new()
    {
        MaterialId = material,
        IdType = WaferMapIdType.WaferIdAndOrientation,
        FirstLocation = 2,
        FlatRotation = 270,
        OriginLocation = 3,
        ReportPointSelection = 1,
        ReferencePoints = [new WaferMapPosition(-15_000, 0), new WaferMapPosition(15_000, 60_000)],
        DieUnits = "um",
        DieSizeX = 5_400,
        DieSizeY = 7_200,
        RowCount = 41,
        ColumnCount = 37,
        NullBinCode = '.',
        ProductId = "BRBTOS-A7",
        ProcessAxis = 1,
    };

    // ------------------------------------------------------------- setup data

    [Fact]
    public async Task Map_setup_data_goes_out_and_comes_back_accepted()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendSetupAsync(Setup(), WaferMapPair.Token);

        Assert.Equal(WaferMapSetupAcknowledgement.Accepted, acknowledgement);

        // And the host kept what it was sent, field for field, which is the half a sender cannot
        // see from its own end.
        var held = await store.LoadSetupAsync(
            new WaferMapSetupRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(held);
        Assert.Equal("W01", held.MaterialId);
        Assert.Equal(WaferMapIdType.WaferIdAndOrientation, held.IdType);
        Assert.Equal(2, held.FirstLocation);
        Assert.Equal((ushort)270, held.FlatRotation);
        Assert.Equal(3, held.OriginLocation);
        Assert.Equal(1, held.ReportPointSelection);
        Assert.Equal("um", held.DieUnits);
        Assert.Equal(5_400, held.DieSizeX);
        Assert.Equal(7_200, held.DieSizeY);
        Assert.Equal(41, held.RowCount);
        Assert.Equal(37u, held.ColumnCount);
        Assert.Equal('.', held.NullBinCode);
        Assert.Equal("BRBTOS-A7", held.ProductId);
        Assert.Equal((byte)1, held.ProcessAxis);
    }

    [Fact]
    public async Task A_reference_point_survives_with_its_sign_and_its_full_range()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await pair.Equipment.SendSetupAsync(Setup(), WaferMapPair.Token);

        var held = await store.LoadSetupAsync(
            new WaferMapSetupRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        // REFP is I4, not I2: a reference point is in die units from the wafer centre, and on a
        // 300 mm wafer in nanometres that is well past what a coordinate inside map data can hold.
        Assert.NotNull(held);
        Assert.Equal(
            [new WaferMapPosition(-15_000, 0), new WaferMapPosition(15_000, 60_000)],
            held.ReferencePoints);
    }

    [Fact]
    public async Task A_setup_the_store_refuses_comes_back_rejected()
    {
        // Capacity nought: the store has no room for anything, which is the one refusal an
        // in-memory store can produce without being asked to lie.
        var store = new WaferMapInMemoryStore { Capacity = 0 };
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendSetupAsync(Setup(), WaferMapPair.Token);

        Assert.Equal(WaferMapSetupAcknowledgement.Rejected, acknowledgement);
    }

    [Fact]
    public async Task A_setup_asked_for_comes_back_with_the_fields_only_S12F4_carries()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await store.SaveSetupAsync(
            new WaferMapSetup
            {
                MaterialId = "W02",
                IdType = WaferMapIdType.WaferId,
                RowCount = 9,
                ColumnCount = 11,
                NullBinCode = '0',
                BinCodeEquivalents = new byte[] { 1, 2, 3 },
                MapLocationCount = 99,
                FlatRotation = 90,
                ProcessAxis = 4,
            },
            WaferMapPair.Token);

        var setup = await pair.Equipment.RequestSetupAsync(
            new WaferMapSetupRequest { MaterialId = "W02", MapFormat = "1" },
            WaferMapPair.Token);

        Assert.NotNull(setup);
        Assert.Equal(9, setup.RowCount);
        Assert.Equal(11u, setup.ColumnCount);
        Assert.Equal('0', setup.NullBinCode);

        // BCEQU and MLCL travel on S12F4 and on nothing else.
        Assert.Equal([1, 2, 3], setup.BinCodeEquivalents.ToArray());
        Assert.Equal(99u, setup.MapLocationCount);

        // FFROT and PRAXI are the other way round: S12F4 has no field for them, so what the host
        // happened to be holding does not reach the equipment, and null says so rather than zero.
        Assert.Null(setup.FlatRotation);
        Assert.Null(setup.ProcessAxis);
    }

    [Fact]
    public async Task A_setup_the_host_does_not_have_is_no_setup_rather_than_a_failure()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        var setup = await pair.Equipment.RequestSetupAsync(
            new WaferMapSetupRequest { MaterialId = "nobody" },
            WaferMapPair.Token);

        // E5 answers "I have none" with a zero-length list, and there is no acknowledgement code
        // on S12F4 to say anything else. Null, not an exception.
        Assert.Null(setup);
    }

    // ------------------------------------------------------------ the handshake

    [Fact]
    public async Task A_transmit_inquire_is_granted()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        var grant = await pair.Equipment.InquireTransmitAsync(
            new WaferMapTransmitInquiry { MaterialId = "W01", MapFormat = "2", MapLocationCount = 1_517 },
            WaferMapPair.Token);

        Assert.Equal(WaferMapTransmitGrant.Granted, grant);
    }

    [Fact]
    public async Task A_busy_host_refuses_the_transmit_before_the_map_is_sent()
    {
        var store = new WaferMapInMemoryStore { Grant = WaferMapTransmitGrant.Busy };
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var grant = await pair.Equipment.InquireTransmitAsync(
            new WaferMapTransmitInquiry { MaterialId = "W01", MapLocationCount = 250_000 },
            WaferMapPair.Token);

        Assert.Equal(WaferMapTransmitGrant.Busy, grant);
    }

    // --------------------------------------------------------------- maps out

    [Fact]
    public async Task Row_data_survives_the_round_trip_with_its_increments_and_its_signs()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var sent = new WaferMapRowData
        {
            MaterialId = "W01",
            IdType = WaferMapIdType.WaferId,
            Rows =
            [
                new WaferMapRow { Row = 0, StartColumn = 3, Increment = 1, Bins = new byte[] { 1, 1, 2 } },

                // Probed the other way: RSINF is I2, so a negative increment and a negative row
                // are both inside the format and both easy to lose by writing it unsigned.
                new WaferMapRow { Row = -4, StartColumn = 9, Increment = -1, Bins = new byte[] { 2, 0, 255 } },
            ],
        };

        var acknowledgement = await pair.Equipment.SendRowDataAsync(sent, WaferMapPair.Token);

        Assert.Equal(WaferMapDataAcknowledgement.Accepted, acknowledgement);

        var held = await store.LoadRowDataAsync(
            new WaferMapDataRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(held);
        Assert.Equal(2, held.Rows.Count);
        Assert.Equal(0, held.Rows[0].Row);
        Assert.Equal(3, held.Rows[0].StartColumn);
        Assert.Equal(1, held.Rows[0].Increment);
        Assert.Equal([1, 1, 2], held.Rows[0].Bins.ToArray());
        Assert.Equal(-4, held.Rows[1].Row);
        Assert.Equal(9, held.Rows[1].StartColumn);
        Assert.Equal(-1, held.Rows[1].Increment);
        Assert.Equal([2, 0, 255], held.Rows[1].Bins.ToArray());
    }

    [Fact]
    public async Task Array_data_survives_the_round_trip_from_a_negative_origin()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendArrayDataAsync(
            new WaferMapArrayData
            {
                MaterialId = "W01",
                IdType = WaferMapIdType.None,
                Start = new WaferMapPosition(-20, -18),
                Bins = new byte[] { 1, 2, 3, 4, 5, 6 },
            },
            WaferMapPair.Token);

        Assert.Equal(WaferMapDataAcknowledgement.Accepted, acknowledgement);

        var held = await store.LoadArrayDataAsync(
            new WaferMapDataRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(held);
        Assert.Equal(new WaferMapPosition(-20, -18), held.Start);
        Assert.Equal([1, 2, 3, 4, 5, 6], held.Bins.ToArray());
    }

    [Fact]
    public async Task Point_data_survives_the_round_trip_including_a_run_of_bins_at_one_coordinate()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendPointDataAsync(
            new WaferMapPointData
            {
                MaterialId = "W01",
                IdType = WaferMapIdType.WaferId,
                Points =
                [
                    new WaferMapPoint { Position = new WaferMapPosition(1, 1), Bins = new byte[] { 7 } },
                    new WaferMapPoint { Position = new WaferMapPosition(-3, 12), Bins = new byte[] { 1, 2, 3 } },
                ],
            },
            WaferMapPair.Token);

        Assert.Equal(WaferMapDataAcknowledgement.Accepted, acknowledgement);

        var held = await store.LoadPointDataAsync(
            new WaferMapDataRequest { MaterialId = "W01" },
            WaferMapPair.Token);

        Assert.NotNull(held);
        Assert.Equal(2, held.Points.Count);
        Assert.Equal(new WaferMapPosition(1, 1), held.Points[0].Position);
        Assert.Equal([7], held.Points[0].Bins.ToArray());
        Assert.Equal(new WaferMapPosition(-3, 12), held.Points[1].Position);
        Assert.Equal([1, 2, 3], held.Points[1].Bins.ToArray());
    }

    [Fact]
    public async Task A_map_the_store_refuses_comes_back_rejected()
    {
        var store = new WaferMapInMemoryStore { Capacity = 0 };
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        var acknowledgement = await pair.Equipment.SendArrayDataAsync(
            new WaferMapArrayData { MaterialId = "W01", Bins = new byte[] { 1 } },
            WaferMapPair.Token);

        Assert.Equal(WaferMapDataAcknowledgement.Rejected, acknowledgement);
    }

    [Fact]
    public async Task A_coordinate_too_large_for_the_wire_is_refused_rather_than_wrapped()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        // XYPOS and STRP are I2. Narrowing 40,000 would send -25,536 and put the die on the far
        // side of the wafer, which is the kind of bug that is found in a bin summary a week later.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await pair.Equipment.SendArrayDataAsync(
                new WaferMapArrayData
                {
                    MaterialId = "W01",
                    Start = new WaferMapPosition(40_000, 0),
                    Bins = new byte[] { 1 },
                },
                WaferMapPair.Token));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await pair.Equipment.SendPointDataAsync(
                new WaferMapPointData
                {
                    MaterialId = "W01",
                    Points = [new WaferMapPoint { Position = new WaferMapPosition(0, -40_000) }],
                },
                WaferMapPair.Token));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await pair.Equipment.SendRowDataAsync(
                new WaferMapRowData
                {
                    MaterialId = "W01",
                    Rows = [new WaferMapRow { Row = 100_000 }],
                },
                WaferMapPair.Token));
    }

    // ---------------------------------------------------------------- maps in

    [Fact]
    public async Task A_row_map_asked_for_comes_back_row_by_row()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await store.SaveRowDataAsync(
            new WaferMapRowData
            {
                MaterialId = "W05",
                IdType = WaferMapIdType.WaferId,
                Rows = [new WaferMapRow { Row = 7, StartColumn = 0, Bins = new byte[] { 3, 3 } }],
            },
            WaferMapPair.Token);

        var data = await pair.Equipment.RequestRowDataAsync(
            new WaferMapDataRequest { MaterialId = "W05" },
            WaferMapPair.Token);

        Assert.NotNull(data);
        Assert.Equal("W05", data.MaterialId);
        Assert.Equal(WaferMapIdType.WaferId, data.IdType);
        var row = Assert.Single(data.Rows);
        Assert.Equal(7, row.Row);
        Assert.Equal([3, 3], row.Bins.ToArray());
    }

    [Fact]
    public async Task An_array_map_asked_for_comes_back()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await store.SaveArrayDataAsync(
            new WaferMapArrayData
            {
                MaterialId = "W06",
                Start = new WaferMapPosition(0, 0),
                Bins = new byte[] { 9, 8, 7 },
            },
            WaferMapPair.Token);

        var data = await pair.Equipment.RequestArrayDataAsync(
            new WaferMapDataRequest { MaterialId = "W06" },
            WaferMapPair.Token);

        Assert.NotNull(data);
        Assert.Equal([9, 8, 7], data.Bins.ToArray());
    }

    [Fact]
    public async Task A_point_map_asked_for_comes_back()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await store.SavePointDataAsync(
            new WaferMapPointData
            {
                MaterialId = "W07",
                Points = [new WaferMapPoint { Position = new WaferMapPosition(4, 5), Bins = new byte[] { 1 } }],
            },
            WaferMapPair.Token);

        var data = await pair.Equipment.RequestPointDataAsync(
            new WaferMapDataRequest { MaterialId = "W07" },
            WaferMapPair.Token);

        Assert.NotNull(data);
        var point = Assert.Single(data.Points);
        Assert.Equal(new WaferMapPosition(4, 5), point.Position);
    }

    [Fact]
    public async Task Asking_for_non_null_bins_leaves_the_null_locations_behind()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        // The null bin code lives on the setup, so the setup has to be there for the filter to
        // mean anything - which is itself the behaviour being asserted.
        await store.SaveSetupAsync(
            new WaferMapSetup { MaterialId = "W08", NullBinCode = (char)0xFF, RowCount = 1, ColumnCount = 4 },
            WaferMapPair.Token);

        await store.SavePointDataAsync(
            new WaferMapPointData
            {
                MaterialId = "W08",
                Points =
                [
                    new WaferMapPoint { Position = new WaferMapPosition(0, 0), Bins = new byte[] { 1, 0xFF, 2, 0xFF } },
                ],
            },
            WaferMapPair.Token);

        var filtered = await pair.Equipment.RequestPointDataAsync(
            new WaferMapDataRequest { MaterialId = "W08", BinSelection = WaferMapBinSelection.NonNullOnly },
            WaferMapPair.Token);

        // SDBIN crossed the wire, the store split the run, and what came back is the two locations
        // that hold a die - each with the coordinate it actually sits at.
        Assert.NotNull(filtered);
        Assert.Equal(2, filtered.Points.Count);
        Assert.Equal(new WaferMapPosition(0, 0), filtered.Points[0].Position);
        Assert.Equal([1], filtered.Points[0].Bins.ToArray());
        Assert.Equal(new WaferMapPosition(2, 0), filtered.Points[1].Position);
        Assert.Equal([2], filtered.Points[1].Bins.ToArray());

        // And the unfiltered request still gets the whole run, so the filter is the request's
        // doing rather than something the store did to the map when it arrived.
        var whole = await pair.Equipment.RequestPointDataAsync(
            new WaferMapDataRequest { MaterialId = "W08", BinSelection = WaferMapBinSelection.AllCoordinates },
            WaferMapPair.Token);

        Assert.NotNull(whole);
        Assert.Equal([1, 0xFF, 2, 0xFF], Assert.Single(whole.Points).Bins.ToArray());
    }

    [Fact]
    public async Task A_map_the_host_does_not_have_is_null_in_all_three_formats()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        var request = new WaferMapDataRequest { MaterialId = "nobody" };

        Assert.Null(await pair.Equipment.RequestRowDataAsync(request, WaferMapPair.Token));
        Assert.Null(await pair.Equipment.RequestArrayDataAsync(request, WaferMapPair.Token));
        Assert.Null(await pair.Equipment.RequestPointDataAsync(request, WaferMapPair.Token));
    }

    [Fact]
    public async Task A_map_saved_as_rows_is_not_handed_back_as_an_array()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await pair.Equipment.SendRowDataAsync(
            new WaferMapRowData
            {
                MaterialId = "W09",
                Rows = [new WaferMapRow { Row = 0, Bins = new byte[] { 1 } }],
            },
            WaferMapPair.Token);

        var request = new WaferMapDataRequest { MaterialId = "W09" };

        // The in-memory store does not convert between the three formats, and says so: converting
        // would mean inventing the row and column counts for locations nobody sent.
        Assert.NotNull(await pair.Equipment.RequestRowDataAsync(request, WaferMapPair.Token));
        Assert.Null(await pair.Equipment.RequestArrayDataAsync(request, WaferMapPair.Token));
    }

    // ----------------------------------------------------------- error reports

    [Fact]
    public async Task A_map_error_report_reaches_the_host()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        WaferMapErrorEventArgs? seen = null;
        pair.Host.ErrorReported += (_, args) => seen = args;

        await pair.Equipment.ReportErrorAsync(WaferMapError.SlotCountInvalid, 3, WaferMapPair.Token);

        await WaferMapPair.WaitUntilAsync(() => seen is not null, "the host to see the error report");

        Assert.Equal(WaferMapError.SlotCountInvalid, seen!.Error);
        Assert.Equal(3, seen.Location);
    }

    [Fact]
    public async Task A_map_error_report_reaches_the_equipment_too_because_either_end_may_send_it()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        WaferMapErrorEventArgs? seen = null;
        pair.Equipment.ErrorReported += (_, args) => seen = args;

        await pair.Host.ReportErrorAsync(WaferMapError.FormatError, 11, WaferMapPair.Token);

        await WaferMapPair.WaitUntilAsync(() => seen is not null, "the equipment to see the error report");

        Assert.Equal(WaferMapError.FormatError, seen!.Error);
        Assert.Equal(11, seen.Location);
    }

    [Fact]
    public async Task A_report_that_the_map_arrived_correctly_is_not_a_failure()
    {
        await using var pair = WaferMapPair.Start();
        await pair.OpenAsync();

        WaferMapErrorEventArgs? seen = null;
        pair.Equipment.ErrorReported += (_, args) => seen = args;

        await pair.Host.ReportErrorAsync(WaferMapError.ReceivedCorrectly, 0, WaferMapPair.Token);

        await WaferMapPair.WaitUntilAsync(() => seen is not null, "the equipment to see the report");

        // MAPER 0 is a confirmation, and for a map sent with no acknowledged function it is the
        // only one either end gets. A reader that treats every S12F19 as a fault has it backwards.
        Assert.Equal(WaferMapError.ReceivedCorrectly, seen!.Error);
    }

    // ------------------------------------------------------------ completeness

    [Fact]
    public async Task The_equipment_can_send_every_primary_the_host_declares()
    {
        var store = new WaferMapInMemoryStore();
        await using var pair = WaferMapPair.Start(store);
        await pair.OpenAsync();

        await pair.Equipment.SendSetupAsync(Setup(), WaferMapPair.Token);
        await pair.Equipment.RequestSetupAsync(new WaferMapSetupRequest { MaterialId = "W01" }, WaferMapPair.Token);
        await pair.Equipment.InquireTransmitAsync(new WaferMapTransmitInquiry { MaterialId = "W01" }, WaferMapPair.Token);

        await pair.Equipment.SendRowDataAsync(
            new WaferMapRowData { MaterialId = "W01", Rows = [new WaferMapRow { Row = 0 }] },
            WaferMapPair.Token);

        await pair.Equipment.SendArrayDataAsync(
            new WaferMapArrayData { MaterialId = "W01" },
            WaferMapPair.Token);

        await pair.Equipment.SendPointDataAsync(
            new WaferMapPointData { MaterialId = "W01" },
            WaferMapPair.Token);

        var request = new WaferMapDataRequest { MaterialId = "W01" };

        await pair.Equipment.RequestRowDataAsync(request, WaferMapPair.Token);
        await pair.Equipment.RequestArrayDataAsync(request, WaferMapPair.Token);
        await pair.Equipment.RequestPointDataAsync(request, WaferMapPair.Token);
        await pair.Equipment.ReportErrorAsync(WaferMapError.ReceivedCorrectly, 0, WaferMapPair.Token);

        // S12F19 expects no reply, so it is the one that has to be waited for rather than awaited.
        await WaferMapPair.WaitUntilAsync(
            () => pair.Asked.Contains(((byte)12, (byte)19)),
            "the error report to arrive");

        // The claim this test exists for: the list the host declares and the methods the equipment
        // offers are the same set. A method added to one end without the other shows up here.
        foreach (var (stream, function) in WaferMapHost.HandledMessages)
        {
            Assert.Contains((stream, function), pair.Asked);
        }
    }
}
