// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// What arrives from a machine that is not this library.
/// </summary>
/// <remarks>
/// Stream 3 has no firm row in the catalogue, so the bodies are a reading of the item lists. What
/// can be tested is the half that matters for interoperability: a reply shaped differently is
/// still understood, and a reply that cannot be understood fails loudly rather than reading as a
/// success.
/// </remarks>
public class CarrierWireTests
{
    private static SecsMessage Reply(byte function, SecsItem body)
        => new(3, function, replyExpected: false) { Item = body };

    private static CarrierActionRequest Proceed { get; } =
        new() { Action = CarrierActions.ProceedWithCarrier, CarrierId = "CAR-001" };

    [Fact]
    public async Task A_reply_with_no_acknowledgement_code_is_a_link_failure_and_never_an_acceptance()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(18, SecsItem.L()));
        await pair.OpenAsync();

        // Nought is CAACK Accepted, so a reader that takes the first byte of an empty body reports
        // a carrier action that was never performed.
        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.PerformAsync(Proceed, CarrierPair.Token));
    }

    [Fact]
    public async Task A_bare_code_with_no_list_around_it_is_still_read()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(18, SecsItem.U1(0)));
        await pair.OpenAsync();

        var result = await pair.Host.PerformAsync(Proceed, CarrierPair.Token);

        Assert.True(result.IsAccepted);
        Assert.Equal(string.Empty, result.ErrorText);
    }

    [Fact]
    public async Task An_error_pair_nested_in_a_list_is_still_read()
    {
        // Some revisions of E87 write S3F18 as the code and then a list of error pairs. The
        // catalogue lists the three items flat, so this library writes them flat and reads both.
        await using var pair = CarrierPair.Start(answer: _ => Reply(
            18,
            SecsItem.L(
                SecsItem.U1(2),
                SecsItem.L(SecsItem.L(SecsItem.U4(27u), SecsItem.A("port 1 is TransferBlocked"))))));

        await pair.OpenAsync();

        var result = await pair.Host.PerformAsync(Proceed, CarrierPair.Token);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, result.Acknowledgement);
        Assert.Equal(27u, result.ErrorCode);
        Assert.Equal("port 1 is TransferBlocked", result.ErrorText);
    }

    [Fact]
    public async Task An_undefined_acknowledgement_code_reads_as_a_refusal()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(
            18,
            SecsItem.L(SecsItem.U1(9), SecsItem.U4(0u), SecsItem.A(string.Empty))));

        await pair.OpenAsync();

        var result = await pair.Host.PerformAsync(Proceed, CarrierPair.Token);

        // CAACK has four codes. Whatever 9 means, it is not 0, and 0 is the only one that means
        // the action will be performed.
        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, result.Acknowledgement);
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task A_code_in_another_numeric_format_is_still_read()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(
            18,
            SecsItem.L(SecsItem.B(3), SecsItem.U4(1u), SecsItem.A("no"))));

        await pair.OpenAsync();

        // CAACK is U1 in the catalogue; B is a machine being loose with a format rather than
        // saying something else.
        Assert.Equal(
            CarrierAcknowledgement.InvalidData,
            (await pair.Host.PerformAsync(Proceed, CarrierPair.Token)).Acknowledgement);
    }

    [Fact]
    public async Task A_location_reply_that_is_not_a_list_is_a_link_failure()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(24, SecsItem.A("somewhere")));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token));
    }

    [Fact]
    public async Task A_location_in_another_numeric_format_is_still_read()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(
            24,
            SecsItem.L(SecsItem.A("CAR-001"), SecsItem.A("4200"))));

        await pair.OpenAsync();

        // LOC is U4. A machine that sends it as text is still telling you where the carrier is.
        Assert.Equal(4_200u, await pair.Host.FindCarrierAsync("CAR-001", CarrierPair.Token));
    }

    [Fact]
    public async Task An_identifier_read_reply_with_no_code_is_a_link_failure()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(26, SecsItem.L()));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.ReadCarrierIdAsync(1, CarrierPair.Token));
    }

    [Fact]
    public async Task A_reservation_reply_with_no_code_is_a_link_failure()
    {
        await using var pair = CarrierPair.Start(answer: _ => Reply(30, SecsItem.L()));
        await pair.OpenAsync();

        await Assert.ThrowsAsync<SecsException>(async () =>
            await pair.Host.ReserveAsync("CAR-001", 1, CarrierPair.Token));
    }

    // -------------------------------------------- what the equipment end refuses

    [Fact]
    public async Task An_action_body_the_equipment_cannot_read_is_invalid_data()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 17, replyExpected: true) { Item = SecsItem.A("not an action") };
        using var reply = await pair.HostLink.SendAsync(primary, CarrierPair.Token);

        Assert.Equal(18, reply.F);

        var result = CarrierReplyProbe.ReadAction(reply);

        Assert.Equal(CarrierAcknowledgement.InvalidData, result.Acknowledgement);
        Assert.NotEqual(string.Empty, result.ErrorText);
    }

    [Fact]
    public async Task An_action_with_no_port_item_at_all_is_still_read()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.Manager.Perform(new CarrierActionRequest { Action = CarrierActions.CarrierNotification, CarrierId = "CAR-001" });
        pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        pair.Manager.PlaceCarrier(1, "CAR-001");
        pair.Manager.ReadCarrierId("CAR-001", verified: null);

        // Two items where this library writes three. The action does not need a port, so a machine
        // that leaves it out is not wrong about anything.
        using var primary = new SecsMessage(3, 17, replyExpected: true)
        {
            Item = SecsItem.L(SecsItem.A(CarrierActions.ProceedWithCarrier), SecsItem.A("CAR-001")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, CarrierPair.Token);

        Assert.True(CarrierReplyProbe.ReadAction(reply).IsAccepted);
        Assert.Equal(CarrierIdStatus.IdVerificationOk, pair.Manager.FindCarrier("CAR-001")?.IdStatus);
    }

    [Fact]
    public async Task A_port_sent_as_text_is_still_read()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 17, replyExpected: true)
        {
            Item = SecsItem.L(
                SecsItem.A(CarrierActions.ReserveAtPort),
                SecsItem.A("CAR-001"),
                SecsItem.A("1")),
        };

        using var reply = await pair.HostLink.SendAsync(primary, CarrierPair.Token);

        Assert.True(CarrierReplyProbe.ReadAction(reply).IsAccepted);
        Assert.Equal(PortReservationState.Reserved, pair.Manager.FindPort(1)?.ReservationState);
    }

    [Fact]
    public async Task An_identifier_read_naming_no_port_is_denied_rather_than_answered_for_port_zero()
    {
        await using var pair = CarrierPair.Start(configure: manager => manager.AddPort(0));
        await pair.OpenAsync();

        pair.Manager.SetServiceStatus(0, inService: true);
        pair.Manager.SetTransferState(0, PortTransferState.ReadyToLoad);
        pair.Manager.PlaceCarrier(0, "CAR-ZERO");
        pair.Manager.ReadCarrierId("CAR-ZERO", verified: true);

        using var primary = new SecsMessage(3, 25, replyExpected: true) { Item = SecsItem.U1() };
        using var reply = await pair.HostLink.SendAsync(primary, CarrierPair.Token);

        // Port 0 has a carrier on it with a verified label. An absent PTN must not find it.
        Assert.Equal(26, reply.F);
        Assert.NotNull(reply.Item);
        Assert.Equal((byte)CarrierRequestAcknowledgement.Denied, reply.Item.Items[0].FirstValueOrDefault<byte>());
    }

    [Fact]
    public async Task A_location_request_the_equipment_cannot_read_is_an_empty_list()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        using var primary = new SecsMessage(3, 23, replyExpected: true) { Item = SecsItem.U4(1u) };
        using var reply = await pair.HostLink.SendAsync(primary, CarrierPair.Token);

        // S3F24 has no code in it to refuse with, so the zero-length list does double duty: no
        // such carrier, and no such request.
        Assert.Equal(24, reply.F);
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.Empty(reply.Item.Items.ToArray());
    }
}

/// <summary>Reads an S3F18 body in a test without reaching into the package.</summary>
/// <remarks>
/// The codec is internal and this repository has no <c>InternalsVisibleTo</c>, so a test that
/// wants to look at a raw reply reads the items itself.
/// </remarks>
internal static class CarrierReplyProbe
{
    internal static CarrierActionResult ReadAction(SecsMessage reply)
    {
        Assert.NotNull(reply.Item);
        Assert.True(reply.Item.IsList);
        Assert.True(reply.Item.Count >= 1);

        return new CarrierActionResult
        {
            Acknowledgement = (CarrierAcknowledgement)reply.Item.Items[0].FirstValueOrDefault<byte>(),
            ErrorCode = reply.Item.Count > 1 ? reply.Item.Items[1].FirstValueOrDefault<uint>() : 0u,
            ErrorText = reply.Item.Count > 2 && reply.Item.Items[2].Count > 0
                ? reply.Item.Items[2].GetString().TrimEnd()
                : string.Empty,
        };
    }
}
