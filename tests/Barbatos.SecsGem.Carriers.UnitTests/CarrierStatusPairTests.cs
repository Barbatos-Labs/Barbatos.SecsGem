// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Objects;

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// The transactions whose one item the catalogue does not define: S3F19/F20, S3F21/F22 and
/// S3F27/F28. S3F31/F32 carries the fourth and is in <see cref="CarrierStorageTests"/>, because it
/// needed a model behind it before it needed a body.
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

    /// <summary>The measurement the S3F20 reading rests on, and the reason it is the access status.</summary>
    /// <remarks>
    /// <c>CARRIERSTATUS</c> is one item and a carrier has three state machines, all three of them
    /// called "carrier something status", so the item's name settles nothing. What settles it is
    /// the vocabulary: a state can only travel as a single word if that word says which machine it
    /// came from, and exactly one of the twelve names fails that - the one state that exists to
    /// ask the host a question. So the two verification states cannot honestly be one item at all,
    /// and the access status can. A rename that broke this would break the argument, which is why
    /// it is a test rather than a sentence.
    /// </remarks>
    [Fact]
    public void The_access_status_is_the_only_one_of_the_three_a_single_word_can_carry()
    {
        var identifiers = Enum.GetNames<CarrierIdStatus>();
        var slotMaps = Enum.GetNames<CarrierSlotMapStatus>();
        var accesses = Enum.GetNames<CarrierAccessStatus>();

        string[] all = [.. identifiers, .. slotMaps, .. accesses];
        string[] shared = [.. all
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)];
        string[] expected = [nameof(CarrierIdStatus.WaitingForHost)];

        Assert.Equal(12, all.Length);
        Assert.Equal(expected, shared);

        // Every access state names itself; WaitingForHost names two machines at once.
        Assert.DoesNotContain(accesses, name => shared.Contains(name, StringComparer.Ordinal));
        Assert.Contains(nameof(CarrierIdStatus.WaitingForHost), identifiers);
        Assert.Contains(nameof(CarrierSlotMapStatus.WaitingForHost), slotMaps);
    }

    [Fact]
    public async Task A_carrier_reports_how_far_through_it_the_machine_has_got()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        Assert.True(pair.Manager.BeginAccess("CAR-001").IsAccepted);

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Equal("CAR-001", status!.CarrierId);
        Assert.Equal(CarrierAccessStatus.InAccess, status.AccessStatus);
        Assert.Equal(nameof(CarrierAccessStatus.InAccess), status.Status);
        Assert.Equal((byte)1, status.PortId);
        Assert.Equal("CAR-001 InAccess (port 1)", status.ToString());
    }

    /// <summary>Nought is a port, so a carrier on none says so with a zero-length item.</summary>
    /// <remarks>
    /// The rule S3F17 already keeps for an action that names no port, and it matters more here: a
    /// carrier the host has only announced has not arrived anywhere, and answering port 0 would
    /// put it on hardware.
    /// </remarks>
    [Fact]
    public async Task A_carrier_on_no_port_says_so_rather_than_naming_port_nought()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        // CarrierNotification: a carrier the host says is coming, which is at no port at all.
        Assert.True(await pair.Host.NotifyCarrierAsync("CAR-404", CarrierPair.Token) is { IsAccepted: true });

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-404", CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Null(status!.PortId);
        Assert.Equal(CarrierAccessStatus.NotAccessed, status.AccessStatus);
        Assert.Equal("CAR-404 NotAccessed (no port)", status.ToString());
    }

    /// <summary>A carrier in storage has no port, and the transaction that locates it is S3F23.</summary>
    [Fact]
    public async Task A_carrier_in_storage_has_no_port_and_its_shelf_comes_from_S3F23()
    {
        await using var pair = CarrierPair.Start(configure: manager =>
        {
            manager.AddStorageLocation(101);

            Assert.True(manager.SetStorageServiceStatus(101, inService: true).IsAccepted);
            Assert.True(manager.SetStorageTransferState(101, PortTransferState.ReadyToLoad).IsAccepted);
            Assert.True(manager.StoreCarrier(101, "CAR-009").IsAccepted);
        });

        await pair.OpenAsync();

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-009", CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Null(status!.PortId);
        Assert.Equal(101u, await pair.Host.FindCarrierAsync("CAR-009", CarrierPair.Token));
    }

    [Fact]
    public async Task A_carrier_the_machine_does_not_have_has_no_status()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        Assert.Null(await pair.Host.ReadCarrierStatusAsync("CAR-404", CarrierPair.Token));
    }

    /// <summary>The same agreement S3F22 and S3F28 have to keep, for the third undefined item.</summary>
    [Fact]
    public async Task S3F20_and_the_stream_14_attribute_agree_on_the_access_status()
    {
        await using var pair = CarrierPair.Start();
        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        Assert.True(pair.Manager.BeginAccess("CAR-001").IsAccepted);
        Assert.True(pair.Manager.EndAccess("CAR-001", stopped: true).IsAccepted);

        var overStream3 = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);
        var attribute = string.Empty;

        await pair.HostObjects.GetAttributesAsync(
            "CAR-001",
            CarrierObjectProvider.CarrierType,
            ["AccessStatus"],
            values => attribute = values.Attributes[0].Value.GetString(),
            CarrierPair.Token);

        Assert.Equal(nameof(CarrierAccessStatus.CarrierStopped), attribute);
        Assert.Equal(attribute, overStream3?.Status);
    }

    /// <summary>A verification state comes back as text, because one word cannot say which it is.</summary>
    /// <remarks>
    /// This is what the vocabulary measurement means on the wire. A machine whose integrator set
    /// <c>StatusOf</c> to the identifier state sends <c>WaitingForHost</c>, which is also a slot
    /// map state, so there is nothing for a host to read: the text survives and the reading is
    /// absent rather than wrong.
    /// </remarks>
    [Fact]
    public async Task A_verification_state_arrives_as_text_and_not_as_a_state()
    {
        await using var pair = CarrierPair.Start();

        pair.Equipment.StatusOf = carrier => carrier.IdStatus.ToString();

        await pair.OpenAsync();

        Assert.True(pair.Manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(pair.Manager.PlaceCarrier(1, "CAR-001").IsAccepted);
        Assert.True(pair.Manager.ReadCarrierId("CAR-001", verified: null).IsAccepted);

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Equal(nameof(CarrierIdStatus.WaitingForHost), status!.Status);
        Assert.Null(status.AccessStatus);
    }

    /// <summary>A number is not a state name, in either of the two items that carry one.</summary>
    /// <remarks>
    /// <c>Enum.TryParse</c> accepts the number as well as the name, so a machine sending the text
    /// <c>"2"</c> used to be read as whichever state happens to be 2 - a second inference, about
    /// numbering, stacked on top of reading an item the dictionary does not define. The docs here
    /// have always promised a reading only for E87's own words.
    /// </remarks>
    [Fact]
    public async Task A_number_is_not_a_state_name()
    {
        await using var pair = CarrierPair.Start(answer: transaction => transaction.F switch
        {
            19 => new SecsMessage(3, 20, replyExpected: false)
            {
                Item = SecsItem.L(
                    SecsItem.A("CAR-001").Named("CARRIERID"),
                    SecsItem.U1(1).Named("PTN"),
                    SecsItem.A("2").Named("CARRIERSTATUS")),
            },
            _ => new SecsMessage(3, 22, replyExpected: false)
            {
                Item = SecsItem.L(
                    SecsItem.U1(1).Named("PTN"),
                    SecsItem.A("2").Named("PORTSTATUS"),
                    SecsItem.A(string.Empty).Named("CARRIERID")),
            },
        });

        await pair.OpenAsync();

        var carrier = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);
        var port = await pair.Host.ReadPortStatusAsync(1, CarrierPair.Token);

        Assert.Equal("2", carrier?.Status);
        Assert.Null(carrier?.AccessStatus);
        Assert.Equal("2", port?.Status);
        Assert.Null(port?.TransferState);
    }

    /// <summary>E87's own words, in E87's own spelling.</summary>
    /// <remarks>
    /// The agreement tests here are byte for byte against the stream 14 attribute, so a reader
    /// that accepted <c>"inaccess"</c> would be claiming an equality that is not on the wire. A
    /// word a person typed is a different matter - <c>ChangeAccess</c> takes an argument from a
    /// host and reads it whatever the case - but a state a machine reports is a state or it is
    /// text.
    /// </remarks>
    [Fact]
    public async Task A_state_name_in_another_spelling_is_not_read()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 20, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("CAR-001").Named("CARRIERID"),
                SecsItem.U1(1).Named("PTN"),
                SecsItem.A("inaccess").Named("CARRIERSTATUS")),
        });

        await pair.OpenAsync();

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);

        Assert.Equal("inaccess", status?.Status);
        Assert.Null(status?.AccessStatus);
    }

    [Fact]
    public async Task A_status_word_from_another_vendor_arrives_as_text_for_a_carrier_too()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 20, replyExpected: false)
        {
            Item = SecsItem.L(
                SecsItem.A("CAR-001").Named("CARRIERID"),
                SecsItem.U1().Named("PTN"),
                SecsItem.A("AT_METROLOGY").Named("CARRIERSTATUS")),
        });

        await pair.OpenAsync();

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);

        Assert.NotNull(status);
        Assert.Equal("AT_METROLOGY", status!.Status);
        Assert.Null(status.AccessStatus);
        Assert.Null(status.PortId);
    }

    [Fact]
    public async Task The_status_a_machine_sends_is_whatever_its_hook_returns()
    {
        await using var pair = CarrierPair.Start();

        // What a fab that has agreed its own vocabulary does, which is why this is a hook and not
        // a fixed answer: E87 does not say which of a carrier's three states this item carries.
        pair.Equipment.StatusOf = carrier => carrier.IsVerified ? "READY" : "NOT_READY";

        await pair.OpenAsync();

        pair.LoadVerifiedCarrier();

        var status = await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token);

        Assert.Equal("READY", status?.Status);
        Assert.Null(status?.AccessStatus);
    }

    [Fact]
    public async Task A_reply_that_is_not_carrier_status_data_is_an_exception()
    {
        await using var pair = CarrierPair.Start(answer: _ => new SecsMessage(3, 20, replyExpected: false)
        {
            Item = SecsItem.A("sorry"),
        });

        await pair.OpenAsync();

        var thrown = await Assert.ThrowsAsync<SecsException>(
            async () => await pair.Host.ReadCarrierStatusAsync("CAR-001", CarrierPair.Token));

        Assert.Equal("S3F20 did not carry carrier status data.", thrown.Message);
    }

    [Fact]
    public void Every_stream_3_function_this_equipment_declares_is_a_request_it_answers()
    {
        // All nine of E87's half of stream 3, which this is the assertion that keeps honest: a
        // function declared and not handled would answer a timeout rather than S9F5.
        var declared = CarrierEquipment.HandledMessages
            .Where(message => message.Stream == 3)
            .Select(message => message.Function)
            .Order()
            .ToArray();

        Assert.Equal(new byte[] { 17, 19, 21, 23, 25, 27, 29, 31, 33 }, declared);
    }
}
