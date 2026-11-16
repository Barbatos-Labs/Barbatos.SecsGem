// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// E87 across a real link: the five transactions, and the attributes over stream 14 beside them.
/// </summary>
/// <remarks>
/// The state machines are tested without a link elsewhere. What these add is what a link can
/// break - a refusal that has to survive being turned into <c>CAACK</c> and an <c>ERRTEXT</c> and
/// read back, an absent port that must not become port 0, and the stream 14 half that carries
/// everything S3F20, S3F22 and S3F28 could not.
/// </remarks>
public class CarrierPairTests
{
    [Fact]
    public async Task A_refusal_comes_back_with_the_state_that_refused_it()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        // Verified, so it is not waiting for anybody - and the text has to survive the round trip,
        // because it is the only thing a host can act on.
        var result = await pair.Host.ProceedWithCarrierAsync("CAR-001", CarrierPair.Token);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, result.Acknowledgement);
        Assert.Contains("not waiting for the host", result.ErrorText, StringComparison.Ordinal);
        Assert.NotEqual(0u, result.ErrorCode);
    }

    [Fact]
    public async Task Proceeding_over_the_wire_settles_a_waiting_verification()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        pair.Manager.PlaceCarrier(1, "CAR-001");
        pair.Manager.ReadCarrierId("CAR-001", verified: null);
        pair.Manager.ReadSlotMap("CAR-001", new byte[] { 1, 1 }, verified: null);

        var carrier = pair.Manager.FindCarrier("CAR-001");

        Assert.NotNull(carrier);
        Assert.False(carrier.IsVerified);

        var result = await pair.Host.ProceedWithCarrierAsync("CAR-001", CarrierPair.Token);

        Assert.True(result.IsAccepted);
        Assert.True(carrier.IsVerified);

        // And now the machine may start on it, which it could not a message ago.
        Assert.True(pair.Manager.BeginAccess("CAR-001").IsAccepted);
    }

    [Fact]
    public async Task Cancelling_over_the_wire_fails_the_verification()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        pair.Manager.PlaceCarrier(1, "CAR-001");
        pair.Manager.ReadCarrierId("CAR-001", verified: null);

        Assert.True((await pair.Host.CancelCarrierAsync("CAR-001", CarrierPair.Token)).IsAccepted);

        Assert.Equal(CarrierIdStatus.IdVerificationFailed, pair.Manager.FindCarrier("CAR-001")?.IdStatus);
        Assert.False(pair.Manager.BeginAccess("CAR-001").IsAccepted);
    }

    [Fact]
    public async Task A_notification_and_a_bind_arrive_as_actions()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.True((await pair.Host.NotifyCarrierAsync("CAR-007", CarrierPair.Token)).IsAccepted);
        Assert.NotNull(pair.Manager.FindCarrier("CAR-007"));

        Assert.True((await pair.Host.BindCarrierAsync("CAR-007", 1, CarrierPair.Token)).IsAccepted);

        var port = pair.Manager.FindPort(1);

        Assert.NotNull(port);
        Assert.Equal(PortReservationState.Reserved, port.ReservationState);
        Assert.Equal("CAR-007", port.ReservedForCarrierId);
    }

    [Fact]
    public async Task An_action_with_no_port_does_not_arrive_as_port_zero()
    {
        await using var pair = CarrierPair.Start(configure: manager => manager.AddPort(0));
        await pair.OpenAsync();

        pair.Manager.SetServiceStatus(0, inService: true);

        // A reservation with no port must be refused for having no port - not applied to port 0,
        // which exists here precisely so the test can tell the difference. An absent PTN travels
        // as a zero-length item, and this is the assertion that says so.
        var result = await pair.Host.PerformAsync(
            new CarrierActionRequest { Action = CarrierActions.ReserveAtPort, CarrierId = "CAR-001" },
            CarrierPair.Token);

        Assert.Equal(CarrierAcknowledgement.InvalidData, result.Acknowledgement);
        Assert.Contains("needs a port", result.ErrorText, StringComparison.Ordinal);

        var port = pair.Manager.FindPort(0);

        Assert.NotNull(port);
        Assert.Equal(PortReservationState.NotReserved, port.ReservationState);
        Assert.Null(port.ReservedForCarrierId);
    }

    [Fact]
    public async Task An_unknown_action_comes_back_as_an_invalid_command()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        var result = await pair.Host.PerformAsync(
            new CarrierActionRequest { Action = "TeleportCarrier", CarrierId = "CAR-001" },
            CarrierPair.Token);

        // A host may ask for an action this library has never heard of, and gets the code E5 has
        // for that rather than a timeout.
        Assert.Equal(CarrierAcknowledgement.InvalidCommand, result.Acknowledgement);
        Assert.Contains("TeleportCarrier", result.ErrorText, StringComparison.Ordinal);
    }

    // ------------------------------------------------- the other four transactions

    [Fact]
    public async Task A_location_request_brings_back_where_the_carrier_is()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        Assert.Equal(1u, await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token));
    }

    [Fact]
    public async Task A_location_request_for_a_carrier_the_machine_does_not_have_is_null()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        // S3F24 has no acknowledgement code, so the zero-length list is all E5 leaves to say it.
        Assert.Null(await pair.Host.FindCarrierAsync("CAR-404", CarrierPair.Token));
    }

    [Fact]
    public async Task A_machine_can_report_a_location_of_its_own_choosing()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();
        pair.Equipment.LocationOf = carrier => carrier.CarrierId == "CAR-001" ? 4_200u : 0u;

        // A location is the machine's own numbering - a port is not a location - which is why this
        // is a hook rather than a hard-coded port number.
        Assert.Equal(4_200u, await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token));
    }

    [Fact]
    public async Task An_identifier_read_brings_back_what_is_on_the_port()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        Assert.Equal("CAR-001", await pair.Host.ReadCarrierIdAsync(1, CarrierPair.Token));
    }

    [Fact]
    public async Task An_identifier_read_says_nothing_when_the_label_has_not_been_read()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        pair.Manager.PlaceCarrier(1, "CAR-001");

        // Handing back an identifier the machine has not read and believed would be telling the
        // host something it does not know yet.
        Assert.Null(await pair.Host.ReadCarrierIdAsync(1, CarrierPair.Token));
    }

    [Fact]
    public async Task An_identifier_read_on_an_empty_port_says_nothing()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.Null(await pair.Host.ReadCarrierIdAsync(1, CarrierPair.Token));
    }

    [Fact]
    public async Task A_reservation_over_the_older_message_is_accepted_once_and_then_denied()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.Equal(
            CarrierRequestAcknowledgement.Accepted,
            await pair.Host.ReserveAsync("CAR-001", 1, CarrierPair.Token));

        // ACKC3 cannot say why, which is the reason to prefer S3F17 - and the reason this test
        // asserts only that it said no.
        Assert.Equal(
            CarrierRequestAcknowledgement.Denied,
            await pair.Host.ReserveAsync("CAR-002", 1, CarrierPair.Token));
    }

    [Fact]
    public async Task A_carrier_out_over_the_older_message_makes_the_port_ready_to_unload()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();
        pair.Manager.BeginAccess("CAR-001");
        pair.Manager.EndAccess("CAR-001");

        Assert.Equal(
            CarrierRequestAcknowledgement.Accepted,
            await pair.Host.CarrierOutAsync("CAR-001", CarrierPair.Token));

        Assert.Equal(PortTransferState.ReadyToUnload, pair.Manager.FindPort(1)?.TransferState);
    }

    [Fact]
    public async Task A_carrier_out_while_the_carrier_is_in_access_is_denied()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();
        pair.Manager.BeginAccess("CAR-001");

        Assert.Equal(
            CarrierRequestAcknowledgement.Denied,
            await pair.Host.CarrierOutAsync("CAR-001", CarrierPair.Token));
    }

    // ------------------------------------------------- the stream 14 half of E87

    [Fact]
    public async Task A_host_reads_a_carrier_slot_map_over_stream_14()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        byte[]? slots = null;
        var status = string.Empty;

        var found = await pair.HostObjects.GetAttributesAsync(
            "CAR-001",
            CarrierObjectProvider.CarrierType,
            ["SlotMap", "AccessStatus"],
            values =>
            {
                foreach (var attribute in values.Attributes)
                {
                    if (attribute.Name == "SlotMap")
                    {
                        slots = attribute.Value.GetSpan<byte>().ToArray();
                    }
                    else
                    {
                        status = attribute.Value.GetString();
                    }
                }
            },
            CarrierPair.Token);

        // The three transactions this package does not implement, working: a slot map and a
        // carrier state, over a wire, in items whose formats are defined.
        Assert.True(found);
        Assert.Equal([1, 1, 0, 1], slots);
        Assert.Equal(nameof(CarrierAccessStatus.NotAccessed), status);
    }

    [Fact]
    public async Task A_host_discovers_the_ports_and_reads_their_states_over_stream_14()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        var instances = await pair.HostObjects.GetInstancesAsync(
            CarrierObjectProvider.LoadPortType,
            CarrierPair.Token);

        var specification = Assert.Single(instances);
        var read = new Dictionary<string, string>(StringComparer.Ordinal);

        await pair.HostObjects.GetAttributesAsync(
            specification,
            CarrierObjectProvider.LoadPortType,
            ["TransferState", "AssociationState", "AssociatedCarrierID"],
            values =>
            {
                foreach (var attribute in values.Attributes)
                {
                    read[attribute.Name] = attribute.Value.GetString();
                }
            },
            CarrierPair.Token);

        Assert.Equal(nameof(PortTransferState.TransferBlocked), read["TransferState"]);
        Assert.Equal(nameof(PortAssociationState.Associated), read["AssociationState"]);
        Assert.Equal("CAR-001", read["AssociatedCarrierID"]);
    }

    [Fact]
    public async Task A_host_sets_a_lot_identifier_over_stream_14_but_cannot_set_a_state()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        var ok = await pair.HostObjects.SetAttributesAsync(
            "CAR-001",
            CarrierObjectProvider.CarrierType,
            [new ObjectAttribute { Name = "LotID", Value = SecsItem.A("LOT-9") }],
            CarrierPair.Token);

        Assert.Equal(ObjectAcknowledgement.Successful, ok);
        Assert.Equal("LOT-9", pair.Manager.FindCarrier("CAR-001")?.LotId);

        var refused = await pair.HostObjects.SetAttributesAsync(
            "CAR-001",
            CarrierObjectProvider.CarrierType,
            [new ObjectAttribute { Name = "AccessStatus", Value = SecsItem.A("CarrierComplete") }],
            CarrierPair.Token);

        // Over a wire as in memory: stream 14 is not a way round the state machine.
        Assert.Equal(ObjectAcknowledgement.InvalidAttribute, refused);
        Assert.Equal(CarrierAccessStatus.NotAccessed, pair.Manager.FindCarrier("CAR-001")?.AccessStatus);
    }

    // ------------------------------------------------------------- completeness

    [Fact]
    public async Task Every_primary_the_equipment_declares_can_be_sent_and_the_host_declares_none()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        await pair.Host.PerformAsync(new CarrierActionRequest { Action = CarrierActions.ProceedWithCarrier, CarrierId = "CAR-001" }, CarrierPair.Token);
        await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token);
        await pair.Host.ReadCarrierIdAsync(1, CarrierPair.Token);
        await pair.Host.ReserveAsync("CAR-001", 1, CarrierPair.Token);
        await pair.Host.CarrierOutAsync("CAR-001", CarrierPair.Token);

        foreach (var message in CarrierEquipment.HandledMessages)
        {
            Assert.Contains(message, pair.Asked);
        }

        // E87 gives a host no primary to receive, and saying so with an empty list beats leaving
        // somebody to wonder which messages they forgot to register.
        Assert.Empty(CarrierHost.HandledMessages);
    }
}
