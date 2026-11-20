// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// Two of the three transactions whose one item the catalogue does not define: S3F21/F22 and
/// S3F27/F28. The third, S3F31/F32, is in <see cref="CarrierStorageTests"/>, because it needed a
/// model behind it before it needed a body.
/// </summary>
/// <remarks>
/// <para>
/// Both carry exactly one item with no dictionary entry - <c>PORTSTATUS</c> and <c>SLOTMAP</c> -
/// and neither reading is a guess, because this same package already serves both values as
/// stream 14 attributes. The tests that matter most here are the two that assert the two routes
/// agree: a host asking S3F22 and a host asking for the <c>TransferState</c> attribute must get
/// the same answer, or this machine contradicts itself depending on which question was asked.
/// </para>
/// <para>
/// The other claim worth a test is what the host side does with a word it does not know.
/// <c>PORTSTATUS</c> being undefined means another vendor is entitled to its own vocabulary, and
/// an unfamiliar word must not read as <see cref="PortTransferState.OutOfService"/> just because
/// that is the zero of the enum - a port nobody may use is a thing a host acts on.
/// </para>
/// </remarks>
public class CarrierStatusPairTests
{
    private static readonly byte[] SlotMap = [1, 1, 0, 1, 1];

    /// <summary>A carrier with a slot map this test chose, which <c>LoadVerifiedCarrier</c> does not allow.</summary>
    /// <remarks>
    /// The harness's helper reads an agreed four-slot map of its own, and E87 lets a slot map be
    /// read exactly once - so a test that needs a particular map, or an unagreed one, has to walk
    /// the arrival itself.
    /// </remarks>
    private static void Arrive(CarrierPair pair, string carrierId, ReadOnlyMemory<byte> slotMap, bool? verified = true)
    {
        Assert.True(pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(pair.Manager.PlaceCarrier(1, carrierId).IsAccepted);
        Assert.True(pair.Manager.ReadCarrierId(carrierId, verified: true).IsAccepted);
        Assert.True(pair.Manager.ReadSlotMap(carrierId, slotMap, verified).IsAccepted);
    }

    [Fact]
    public async Task A_port_reports_what_it_is_doing_and_what_is_on_it()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.True(pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(pair.Manager.PlaceCarrier(1, "CAR-001").IsAccepted);

        var status = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Equal((byte)1, status!.PortId);
        Assert.Equal(PortTransferState.TransferBlocked, status.TransferState);
        Assert.Equal(nameof(PortTransferState.TransferBlocked), status.Status);

        // Which carrier is on it, which is what CARRIERID beside PORTSTATUS is for.
        Assert.Equal("CAR-001", status.CarrierId);
    }

    [Fact]
    public async Task An_empty_port_says_so_rather_than_naming_a_carrier()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();


        var status = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Equal(PortTransferState.TransferBlocked, status!.TransferState);
        Assert.Equal(string.Empty, status.CarrierId);
    }

    [Fact]
    public async Task An_out_of_service_port_is_reported_as_out_of_service()
    {
        // Port 2, declared and left alone: the harness puts port 1 in service, and this is the
        // one value a host must never be handed by accident, so it needs a port nobody touched.
        await using var pair = CarrierPair.Start(configure: manager => manager.AddPort(2));
        await pair.OpenAsync();

        var status = await pair.Host.ReadPortStatusAsync(2, CarrierPair.Token);

        Assert.Equal(PortTransferState.OutOfService, status!.TransferState);
        Assert.Equal((byte)2, status.PortId);
    }

    [Fact]
    public async Task A_port_the_machine_does_not_have_is_the_zero_length_list()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        // Like S3F24: S3F22 has no acknowledgement code, so there is no way to say "no such port"
        // except by saying nothing.
        Assert.Null(await pair.Host.ReadPortStatusAsync(9, CarrierPair.Token));
    }

    /// <summary>
    /// The test the whole provenance argument rests on.
    /// </summary>
    /// <remarks>
    /// <c>PORTSTATUS</c> has no dictionary entry, so this library's reading of it is only
    /// defensible because the same package already serves the same value over stream 14. If the
    /// two ever disagree, one of them is wrong and a host gets a different answer depending on
    /// which question it asked.
    /// </remarks>
    [Fact]
    public async Task S3F22_and_the_stream_14_attribute_say_the_same_thing()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        // ReadyToLoad rather than ReadyToUnload: E87 only lets a port offer to be unloaded when
        // there is something on it to take, which is the state machine working.
        Assert.True(pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);

        var status = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);
        var attribute = string.Empty;

        await pair.HostObjects.GetAttributesAsync(
            CarrierObjectProvider.PortSpecification(1),
            CarrierObjectProvider.LoadPortType,
            ["TransferState"],
            values => attribute = values.Attributes[0].Value.GetString(),
            CarrierPair.Token);

        Assert.Equal(nameof(PortTransferState.ReadyToLoad), attribute);
        Assert.Equal(attribute, status!.Status);
    }

    /// <summary>
    /// What another vendor's equipment is entitled to send, over the wire rather than in a
    /// constructor.
    /// </summary>
    /// <remarks>
    /// <c>PORTSTATUS</c> has no dictionary entry, so a machine this library did not write may use
    /// any word it likes. The reading has to be absent rather than wrong: casting the text's
    /// failure to parse into <see cref="PortTransferState.OutOfService"/> - the zero of the enum -
    /// would tell a host the port is one nobody may use, which is a thing a host acts on.
    /// </remarks>
    [Fact]
    public async Task A_status_word_from_another_vendor_arrives_as_text_and_not_as_a_state()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 22, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.U1(1).Named("PTN"),
                SecsItem.A("DOOR_OPEN").Named("PORTSTATUS"),
                SecsItem.A("CAR-001").Named("CARRIERID")),
        });

        await pair.OpenAsync();

        var status = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Equal("DOOR_OPEN", status!.Status);
        Assert.Null(status.TransferState);
        Assert.Equal("CAR-001", status.CarrierId);
        Assert.Equal("port 1 DOOR_OPEN (CAR-001)", status.ToString());
    }

    [Fact]
    public void A_status_word_this_library_does_not_know_is_kept_and_not_read()
    {
        // What another vendor's equipment is entitled to send. The text survives, and the reading
        // is absent rather than wrong.
        var status = new CarrierPortStatus
        {
            PortId = 1,
            Status = "DOOR_OPEN",
            TransferState = null,
            CarrierId = string.Empty,
        };

        Assert.Null(status.TransferState);
        Assert.Equal("DOOR_OPEN", status.Status);
        Assert.Equal("port 1 DOOR_OPEN", status.ToString());
    }

    // ------------------------------------------------------------ the slot map

    [Fact]
    public async Task A_verified_slot_map_comes_back_byte_for_byte()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Arrive(pair, "CAR-001", SlotMap);

        var map = await pair.Host.ReadSlotMapAsync("CAR-001", CarrierPair.Token);

        Assert.NotNull(map);
        Assert.Equal(SlotMap, map!.Value.ToArray());
    }

    [Fact]
    public async Task A_slot_map_nobody_has_agreed_is_not_offered_as_an_empty_one()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        // Read, and only the host can settle it - so the map exists and nobody has agreed it.
        Arrive(pair, "CAR-001", SlotMap, verified: null);

        // Null rather than zero bytes. E87 gives the slot map a state machine precisely so that
        // "not read" and "read as empty" are different answers, and a host handed an empty map
        // would have no way to tell which it had.
        Assert.Null(await pair.Host.ReadSlotMapAsync("CAR-001", CarrierPair.Token));
    }

    [Fact]
    public async Task A_carrier_the_machine_does_not_have_is_the_zero_length_list()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.Null(await pair.Host.ReadSlotMapAsync("CAR-404", CarrierPair.Token));
    }

    [Fact]
    public async Task An_empty_map_on_a_verified_carrier_is_an_empty_map()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Arrive(pair, "CAR-001", ReadOnlyMemory<byte>.Empty);

        var map = await pair.Host.ReadSlotMapAsync("CAR-001", CarrierPair.Token);

        // The other half of the distinction above: agreed and empty is zero bytes, not null.
        Assert.NotNull(map);
        Assert.Equal(0, map!.Value.Length);
    }

    [Fact]
    public async Task S3F28_and_the_stream_14_attribute_say_the_same_thing()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Arrive(pair, "CAR-001", SlotMap);

        var map = await pair.Host.ReadSlotMapAsync("CAR-001", CarrierPair.Token);
        byte[] attribute = [];

        await pair.HostObjects.GetAttributesAsync(
            "CAR-001",
            CarrierObjectProvider.CarrierType,
            ["SlotMap"],
            values => attribute = [.. values.Attributes[0].Value.GetMemory<byte>().Span],
            CarrierPair.Token);

        Assert.Equal(SlotMap, attribute);
        Assert.Equal(attribute, map!.Value.ToArray());
    }

    [Fact]
    public async Task The_two_new_requests_are_declared_and_answered()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier("CAR-001");

        _ = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);
        _ = await pair.Host.ReadSlotMapAsync("CAR-001", CarrierPair.Token);

        // Both reached the equipment rather than coming back as an S9F5, which is what declaring
        // them is for.
        Assert.Contains(((byte)3, (byte)21), pair.Asked);
        Assert.Contains(((byte)3, (byte)27), pair.Asked);
    }

    [Fact]
    public void Every_stream_3_function_this_equipment_declares_is_a_request_it_answers()
    {
        // The one pair this package still does not implement is S3F19/F20, and the Carriers README
        // says why. This is the assertion that keeps the two lists honest: a function declared and
        // not handled would answer a timeout rather than S9F5.
        var declared = CarrierEquipment.HandledMessages
            .Where(message => message.Stream == 3)
            .Select(message => message.Function)
            .Order()
            .ToArray();

        Assert.Equal(new byte[] { 17, 21, 23, 25, 27, 29, 31, 33 }, declared);
    }
}
