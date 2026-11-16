// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Substrates;

namespace Barbatos.SecsGem.MaterialFlow.UnitTests;

/// <summary>
/// The join between E87 and E90, which is a convention and is tested like one.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here opens a link, because the bridge has no wire: it is two state machines meeting,
/// and every rule it keeps is a rule about them. What the tests are really checking is that the
/// convention is <em>replaceable</em> - each default is asserted, and then asserted again with the
/// option set the other way.
/// </para>
/// <para>
/// The lifecycle test is the one that matters most. A wafer whose destination is the slot it came
/// out of must still be able to get to a chuck and back, and the only reason that works is that
/// E90 derives the transport state from a move rather than from a description. A bridge that got
/// it wrong would create twenty-five wafers already at their destination, and nothing downstream
/// could move any of them.
/// </para>
/// </remarks>
public class CarrierSubstrateBridgeTests
{
    private const string CarrierName = "CAR-001";
    private const string Chuck = "CHUCK";

    /// <summary>Twenty-five slots with slots 13 and 24 empty, the sample's own map.</summary>
    private static readonly byte[] SlotMap =
    [
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1,
    ];

    private sealed record Machine(
        CarrierManager Carriers,
        SubstrateTracker Substrates,
        CarrierSubstrateBridge Bridge);

    private static Machine Build(
        Action<CarrierSubstrateOptions>? configure = null,
        bool attach = false,
        bool verified = true,
        string lotId = "LOT-7",
        byte[]? slotMap = null)
    {
        var carriers = new CarrierManager();
        var substrates = new SubstrateTracker();
        var options = new CarrierSubstrateOptions();

        configure?.Invoke(options);

        var bridge = new CarrierSubstrateBridge(carriers, substrates, options);

        if (attach)
        {
            bridge.Attach();
        }

        // The chuck is the machine's own geography, declared the way E90 wants it - the slot
        // locations are the bridge's business and it declares them as it needs them.
        substrates.AddLocation(Chuck);

        carriers.AddPort(1);
        Assert.True(carriers.SetServiceStatus(1, inService: true).IsAccepted);
        Assert.True(carriers.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);

        // A host saying a carrier is coming, with its lot: the only way E87 lets a lot identifier
        // onto a carrier without a stream 14 round trip.
        Assert.True(carriers.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierNotification,
            CarrierId = CarrierName,
            Argument = lotId,
        }).IsAccepted);

        Assert.True(carriers.PlaceCarrier(1, CarrierName).IsAccepted);
        Assert.True(carriers.ReadCarrierId(CarrierName, verified: true).IsAccepted);
        Assert.True(carriers.ReadSlotMap(CarrierName, slotMap ?? SlotMap, verified ? true : null).IsAccepted);

        return new Machine(carriers, substrates, bridge);
    }

    // -------------------------------------------------------------------- opening

    [Fact]
    public void A_verified_slot_map_becomes_one_substrate_per_occupied_slot()
    {
        var machine = Build();

        var result = machine.Bridge.Open(CarrierName);

        Assert.True(result.IsAccepted);
        Assert.Equal(23, result.Count);
        Assert.Empty(result.Refused);
        Assert.Equal(23, machine.Substrates.Substrates.Count);

        // The two empty slots are the two that are not there, which is the whole content of a
        // slot map.
        Assert.DoesNotContain(result.Substrates, entry => entry.Slot is 13 or 24);
        Assert.Contains(result.Substrates, entry => entry.Slot is 12);
        Assert.Contains(result.Substrates, entry => entry.Slot is 25);
    }

    [Fact]
    public void Each_substrate_is_named_and_placed_and_labelled_by_the_convention()
    {
        var machine = Build();

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        var wafer = machine.Substrates.FindSubstrate("CAR-001-03");

        Assert.NotNull(wafer);
        Assert.Equal("CAR-001-SLOT-03", wafer.LocationId);
        Assert.Equal("CAR-001-SLOT-03", wafer.SourceLocationId);
        Assert.Equal((byte)3, wafer.SlotId);
        Assert.Equal("LOT-7", wafer.LotId);

        // AtSource, not AtDestination, even though its destination is where it is: a destination
        // is where a wafer is going, and E90 derives the state from a move.
        Assert.Equal("CAR-001-SLOT-03", wafer.DestinationLocationId);
        Assert.Equal(SubstrateTransportState.AtSource, wafer.TransportState);
        Assert.Equal(SubstrateProcessingState.NeedsProcessing, wafer.ProcessingState);
    }

    [Fact]
    public void The_slot_locations_are_declared_as_they_are_needed()
    {
        var machine = Build();

        // One, the chuck: an application does not have to know a carrier's capacity up front.
        Assert.Equal(Chuck, Assert.Single(machine.Substrates.Locations).LocationId);

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        Assert.Equal(24, machine.Substrates.Locations.Count);
        Assert.NotNull(machine.Substrates.FindLocation("CAR-001-SLOT-01"));

        // And not for the empty slots: a slot with no wafer needs no location, and declaring
        // twenty-five where twenty-three are used would put two empty locations in every report.
        Assert.Null(machine.Substrates.FindLocation("CAR-001-SLOT-13"));
    }

    [Fact]
    public void A_slot_map_the_host_has_not_agreed_creates_nothing()
    {
        var machine = Build(verified: false);

        Assert.Equal(CarrierSlotMapStatus.WaitingForHost, machine.Carriers.FindCarrier(CarrierName)?.SlotMapStatus);

        var result = machine.Bridge.Open(CarrierName);

        // The rule carried over from E87: a map nobody has agreed is not a bill of materials, and
        // tracking wafers from one would have the machine believing in wafers nobody confirmed.
        Assert.False(result.IsAccepted);
        Assert.Contains(nameof(CarrierSlotMapStatus.WaitingForHost), result.Reason, StringComparison.Ordinal);
        Assert.Empty(machine.Substrates.Substrates);
    }

    [Fact]
    public void A_carrier_nobody_mapped_creates_nothing()
    {
        var carriers = new CarrierManager();
        var substrates = new SubstrateTracker();
        var bridge = new CarrierSubstrateBridge(carriers, substrates);

        var missing = bridge.Open("CAR-404");

        Assert.False(missing.IsAccepted);
        Assert.Contains("no carrier", missing.Reason, StringComparison.Ordinal);

        Assert.True(carriers.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierNotification,
            CarrierId = CarrierName,
        }).IsAccepted);

        var unmapped = bridge.Open(CarrierName);

        Assert.False(unmapped.IsAccepted);
        Assert.Contains(nameof(CarrierSlotMapStatus.SlotMapNotRead), unmapped.Reason, StringComparison.Ordinal);
        Assert.Empty(substrates.Substrates);
    }

    /// <summary>The bridge opens the carrier it was asked for, and not whichever one it can find.</summary>
    /// <remarks>
    /// Obvious, and the suite could not tell: a negative control that made the lookup fall back to
    /// any carrier at all broke nothing, because every other test that opens an unknown carrier
    /// has nothing else on the machine for it to fall back to. This one does.
    /// </remarks>
    [Fact]
    public void A_carrier_that_is_not_there_is_not_some_other_carrier()
    {
        var machine = Build();

        var result = machine.Bridge.Open("CAR-404");

        Assert.False(result.IsAccepted);
        Assert.Contains("CAR-404", result.Reason, StringComparison.Ordinal);
        Assert.Empty(machine.Substrates.Substrates);
        Assert.Empty(machine.Bridge.OpenCarriers);

        // And the carrier that is there is still openable, which is the other half: the refusal
        // was about the name, not about the machine.
        Assert.Equal(23, machine.Bridge.Open(CarrierName).Count);
    }

    [Fact]
    public void Opening_the_same_carrier_twice_is_refused_rather_than_tracking_a_wafer_twice()
    {
        var machine = Build();

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        var again = machine.Bridge.Open(CarrierName);

        Assert.False(again.IsAccepted);
        Assert.Contains("already has 23 substrate(s)", again.Reason, StringComparison.Ordinal);
        Assert.Equal(23, machine.Substrates.Substrates.Count);
        Assert.Equal(23, machine.Bridge.SubstratesOf(CarrierName).Count);
    }

    [Fact]
    public void A_slot_the_tracker_refuses_is_reported_and_the_rest_are_created()
    {
        var machine = Build();

        // Somebody else is already tracking slot three's wafer under that name - a sorter, a
        // previous carrier, a bug upstream. Whichever it is, it is not this bridge's to remove.
        machine.Substrates.AddLocation("CAR-001-SLOT-03");
        Assert.True(machine.Substrates.Add("CAR-001-03", "CAR-001-SLOT-03").IsAccepted);

        var result = machine.Bridge.Open(CarrierName);

        Assert.True(result.IsAccepted);
        Assert.Equal(22, result.Count);

        var refusal = Assert.Single(result.Refused);

        Assert.Equal((byte)3, refusal.Key);
        Assert.Contains("already", refusal.Value, StringComparison.Ordinal);

        // Twenty-two wafers the machine needs to know about, plus the one that was already there.
        Assert.Equal(23, machine.Substrates.Substrates.Count);
        Assert.Equal(22, machine.Bridge.SubstratesOf(CarrierName).Count);
    }

    [Fact]
    public void A_map_with_nothing_in_it_opens_to_nothing_and_is_still_accepted()
    {
        var machine = Build(slotMap: [0, 0, 0]);

        var result = machine.Bridge.Open(CarrierName);

        // An empty carrier is a real thing: a map that says so is a map that was read.
        Assert.True(result.IsAccepted);
        Assert.Equal(0, result.Count);
        Assert.Empty(machine.Substrates.Substrates);
        Assert.Empty(machine.Bridge.OpenCarriers);
    }

    // ---------------------------------------------------------------- the options

    [Fact]
    public void The_occupancy_rule_can_be_replaced()
    {
        // A mapper that reports 2 for a wafer and 1 for an empty slot, which SLOTMAP having no
        // dictionary entry makes perfectly legal.
        var machine = Build(
            options => options.IsOccupied = code => code == 2,
            slotMap: [2, 1, 2, 1, 2]);

        var result = machine.Bridge.Open(CarrierName);

        Assert.Equal(3, result.Count);
        Assert.Equal([1, 3, 5], result.Substrates.Select(entry => (int)entry.Slot));
    }

    [Fact]
    public void The_naming_can_be_replaced()
    {
        var machine = Build(options =>
        {
            options.SlotLocationName = (carrier, slot) => $"{carrier}/{slot}";
            options.SubstrateName = (carrier, slot) => $"W-{carrier}-{slot}";
        });

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        var wafer = machine.Substrates.FindSubstrate("W-CAR-001-3");

        Assert.NotNull(wafer);
        Assert.Equal("CAR-001/3", wafer.LocationId);
    }

    [Fact]
    public void A_naming_convention_that_gives_an_empty_name_refuses_that_slot_alone()
    {
        var machine = Build(options =>
            options.SubstrateName = (carrier, slot) => slot == 3 ? string.Empty : $"{carrier}-{slot:00}");

        var result = machine.Bridge.Open(CarrierName);

        // Refused rather than named something invented: a wafer the machine cannot name is a
        // wafer nobody can ask about later.
        Assert.True(result.IsAccepted);
        Assert.Equal(22, result.Count);
        Assert.Equal("the naming convention gave an empty name.", result.Refused[3]);
        Assert.Null(machine.Substrates.FindSubstrate("CAR-001-03"));
    }

    [Fact]
    public void A_wafer_need_not_go_back_to_its_own_slot()
    {
        var machine = Build(options => options.ReturnsToItsOwnSlot = false);

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        var wafer = machine.Substrates.FindSubstrate("CAR-001-03");

        // For a sorter: the destination is somewhere else and the application sets it.
        Assert.Equal(string.Empty, wafer?.DestinationLocationId);
        Assert.Equal(SubstrateTransportState.AtSource, wafer?.TransportState);
    }

    [Fact]
    public void The_lot_and_the_usage_are_copied_unless_they_are_not()
    {
        var copied = Build();

        Assert.True(copied.Bridge.Open(CarrierName).IsAccepted);
        Assert.Equal("LOT-7", copied.Substrates.FindSubstrate("CAR-001-03")?.LotId);

        var kept = Build(options =>
        {
            options.CopiesLotId = false;
            options.CopiesUsage = false;
        });

        Assert.True(kept.Bridge.Open(CarrierName).IsAccepted);
        Assert.Equal(string.Empty, kept.Substrates.FindSubstrate("CAR-001-03")?.LotId);
    }

    [Fact]
    public void The_substrate_type_is_whatever_the_fab_calls_it_and_nothing_by_default()
    {
        var unnamed = Build();

        Assert.True(unnamed.Bridge.Open(CarrierName).IsAccepted);

        // Not "Wafer": E90 does not define the vocabulary, and a package that guessed would put
        // its guess in a host's reports.
        Assert.Equal(string.Empty, unnamed.Substrates.FindSubstrate("CAR-001-03")?.SubstrateType);

        var named = Build(options => options.SubstrateType = "300mm Si");

        Assert.True(named.Bridge.Open(CarrierName).IsAccepted);
        Assert.Equal("300mm Si", named.Substrates.FindSubstrate("CAR-001-03")?.SubstrateType);
    }

    // ------------------------------------------------------------- the lifecycle

    [Fact]
    public void A_wafer_created_by_the_bridge_can_be_processed_and_come_back()
    {
        var machine = Build();

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        const string Wafer = "CAR-001-03";

        // The whole point of the destination convention: it does not brick the wafer.
        Assert.True(machine.Substrates.MoveTo(Wafer, Chuck).IsAccepted);
        Assert.Equal(SubstrateTransportState.AtWork, machine.Substrates.FindSubstrate(Wafer)?.TransportState);

        Assert.True(machine.Substrates.BeginProcessing(Wafer).IsAccepted);
        Assert.True(machine.Substrates.EndProcessing(Wafer, SubstrateProcessingState.Processed).IsAccepted);
        Assert.True(machine.Substrates.MoveTo(Wafer, "CAR-001-SLOT-03").IsAccepted);

        var wafer = machine.Substrates.FindSubstrate(Wafer);

        Assert.Equal(SubstrateTransportState.AtDestination, wafer?.TransportState);
        Assert.Equal(SubstrateProcessingState.Processed, wafer?.ProcessingState);

        // And once it is there, E90 lets go of it - which is the moment the bridge can.
        Assert.False(machine.Substrates.MoveTo(Wafer, Chuck).IsAccepted);
    }

    // -------------------------------------------------------------------- closing

    [Fact]
    public void Closing_forgets_the_wafers_that_are_back_and_keeps_the_rest()
    {
        var machine = Build();

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        const string Wafer = "CAR-001-03";

        Assert.True(machine.Substrates.MoveTo(Wafer, Chuck).IsAccepted);

        var early = machine.Bridge.Close(CarrierName);

        // Accepted, and nothing forgotten: E90 only lets go of a substrate at its destination or
        // lost, so a close while the lot is being worked on is a close that can be made again.
        Assert.True(early.IsAccepted);
        Assert.Equal(0, early.Count);
        Assert.Equal(23, early.Refused.Count);
        Assert.Equal(23, machine.Substrates.Substrates.Count);
        Assert.Equal(23, machine.Bridge.SubstratesOf(CarrierName).Count);

        // Put them all where they belong, and then it works. The one on the chuck goes back first,
        // because the chuck holds one wafer at a time - which is E90's rule and this test's own
        // first draft got wrong.
        Assert.True(machine.Substrates.MoveTo(Wafer, "CAR-001-SLOT-03").IsAccepted);

        foreach (var entry in machine.Bridge.SubstratesOf(CarrierName))
        {
            if (entry.SubstrateId == Wafer)
            {
                continue;
            }

            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, Chuck).IsAccepted);
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, entry.LocationId).IsAccepted);
        }

        var late = machine.Bridge.Close(CarrierName);

        Assert.True(late.IsAccepted);
        Assert.Equal(23, late.Count);
        Assert.Empty(late.Refused);
        Assert.Empty(machine.Substrates.Substrates);
        Assert.Empty(machine.Bridge.OpenCarriers);
        Assert.Empty(machine.Bridge.SubstratesOf(CarrierName));
    }

    [Fact]
    public void Closing_a_carrier_this_bridge_never_opened_is_refused()
    {
        var machine = Build();

        var result = machine.Bridge.Close(CarrierName);

        Assert.False(result.IsAccepted);
        Assert.Contains("no substrates for carrier", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_carrier_can_be_opened_again_once_it_has_been_closed()
    {
        var machine = Build();

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        foreach (var entry in machine.Bridge.SubstratesOf(CarrierName))
        {
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, Chuck).IsAccepted);
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, entry.LocationId).IsAccepted);
        }

        Assert.Equal(23, machine.Bridge.Close(CarrierName).Count);

        // Which is what a second lot in the same carrier looks like: E87 calls that a re-created
        // carrier, and this side of the join simply has nothing of it left.
        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);
        Assert.Equal(23, machine.Substrates.Substrates.Count);
    }

    // ------------------------------------------------------------- the attachment

    [Fact]
    public void An_attached_bridge_opens_a_carrier_the_moment_its_map_is_agreed()
    {
        var carriers = new CarrierManager();
        var substrates = new SubstrateTracker();
        var bridge = new CarrierSubstrateBridge(carriers, substrates);

        bridge.Attach();

        Assert.True(bridge.IsAttached);

        carriers.AddPort(1);
        Assert.True(carriers.SetServiceStatus(1, inService: true).IsAccepted);
        Assert.True(carriers.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(carriers.PlaceCarrier(1, CarrierName).IsAccepted);
        Assert.True(carriers.ReadCarrierId(CarrierName, verified: true).IsAccepted);

        Assert.Empty(substrates.Substrates);

        Assert.True(carriers.ReadSlotMap(CarrierName, SlotMap, verified: true).IsAccepted);

        // Not a line of application code between the mapper and the tracker, which is the thing
        // this package exists to delete.
        Assert.Equal(23, substrates.Substrates.Count);
        Assert.Equal(CarrierName, Assert.Single(bridge.OpenCarriers));
    }

    [Fact]
    public void An_attached_bridge_ignores_a_map_the_host_has_not_agreed()
    {
        var machine = Build(attach: true, verified: false);

        Assert.Empty(machine.Substrates.Substrates);
        Assert.Empty(machine.Bridge.OpenCarriers);
    }

    [Fact]
    public void Attaching_twice_listens_once_and_detaching_stops_it()
    {
        var machine = Build(attach: true);

        machine.Bridge.Attach();

        // Twenty-three rather than forty-six: a second subscription would have opened the carrier
        // twice, and the second open is refused - but the first would still have run twice.
        Assert.Equal(23, machine.Substrates.Substrates.Count);

        machine.Bridge.Detach();

        Assert.False(machine.Bridge.IsAttached);

        // What it created stays: a bridge that forgot a machine's wafers on being detached would
        // be a worse surprise than one that did not.
        Assert.Equal(23, machine.Substrates.Substrates.Count);
        Assert.Equal(CarrierName, Assert.Single(machine.Bridge.OpenCarriers));
    }

    [Fact]
    public void A_detached_bridge_does_nothing_by_itself()
    {
        var carriers = new CarrierManager();
        var substrates = new SubstrateTracker();
        var bridge = new CarrierSubstrateBridge(carriers, substrates);

        bridge.Attach();
        bridge.Detach();

        carriers.AddPort(1);
        Assert.True(carriers.SetServiceStatus(1, inService: true).IsAccepted);
        Assert.True(carriers.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(carriers.PlaceCarrier(1, CarrierName).IsAccepted);
        Assert.True(carriers.ReadCarrierId(CarrierName, verified: true).IsAccepted);
        Assert.True(carriers.ReadSlotMap(CarrierName, SlotMap, verified: true).IsAccepted);

        Assert.Empty(substrates.Substrates);

        // And by hand it still works, which is the point of the two being separate.
        Assert.Equal(23, bridge.Open(CarrierName).Count);
    }

    [Fact]
    public void An_attached_bridge_with_the_option_off_waits_to_be_asked()
    {
        var machine = Build(options => options.OpensWhenSlotMapVerified = false, attach: true);

        Assert.Empty(machine.Substrates.Substrates);
        Assert.Equal(23, machine.Bridge.Open(CarrierName).Count);
    }

    [Fact]
    public void A_finished_carrier_keeps_its_substrates_unless_it_is_told_not_to()
    {
        var kept = Build(attach: true);

        // Every wafer put back where it belongs first, so E90 would allow forgetting them: a test
        // where the removals would have been refused anyway cannot tell the option from the rule,
        // and this one could not until a negative control said so.
        foreach (var entry in kept.Bridge.SubstratesOf(CarrierName))
        {
            Assert.True(kept.Substrates.MoveTo(entry.SubstrateId, Chuck).IsAccepted);
            Assert.True(kept.Substrates.MoveTo(entry.SubstrateId, entry.LocationId).IsAccepted);
        }

        Assert.True(kept.Carriers.BeginAccess(CarrierName).IsAccepted);
        Assert.True(kept.Carriers.EndAccess(CarrierName).IsAccepted);

        Assert.Equal(
            CarrierAccessStatus.CarrierComplete,
            kept.Carriers.FindCarrier(CarrierName)?.AccessStatus);

        // The cautious default: a finished carrier still has its records, and plenty of
        // applications want them.
        Assert.Equal(23, kept.Substrates.Substrates.Count);

        var forgotten = Build(options => options.ClosesWhenCarrierFinished = true, attach: true);

        foreach (var entry in forgotten.Bridge.SubstratesOf(CarrierName))
        {
            Assert.True(forgotten.Substrates.MoveTo(entry.SubstrateId, Chuck).IsAccepted);
            Assert.True(forgotten.Substrates.MoveTo(entry.SubstrateId, entry.LocationId).IsAccepted);
        }

        Assert.True(forgotten.Carriers.BeginAccess(CarrierName).IsAccepted);
        Assert.True(forgotten.Carriers.EndAccess(CarrierName).IsAccepted);

        Assert.Empty(forgotten.Substrates.Substrates);
        Assert.Empty(forgotten.Bridge.OpenCarriers);
    }

    [Fact]
    public void A_carrier_stopped_part_way_closes_like_one_that_finished()
    {
        var machine = Build(options => options.ClosesWhenCarrierFinished = true, attach: true);

        foreach (var entry in machine.Bridge.SubstratesOf(CarrierName))
        {
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, Chuck).IsAccepted);
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, entry.LocationId).IsAccepted);
        }

        Assert.True(machine.Carriers.BeginAccess(CarrierName).IsAccepted);
        Assert.True(machine.Carriers.EndAccess(CarrierName, stopped: true).IsAccepted);

        // Both endings mean the machine has let go, and whether the wafers can be forgotten is
        // E90's answer rather than this bridge's.
        Assert.Equal(CarrierAccessStatus.CarrierStopped, machine.Carriers.FindCarrier(CarrierName)?.AccessStatus);
        Assert.Empty(machine.Substrates.Substrates);
    }

    // ----------------------------------------------------------------- the events

    [Fact]
    public void Opening_and_closing_are_announced_with_what_happened()
    {
        var machine = Build();
        List<CarrierSubstrateResult> opened = [];
        List<CarrierSubstrateResult> closed = [];

        machine.Bridge.Opened += (_, args) => opened.Add(args.Result);
        machine.Bridge.Closed += (_, args) => closed.Add(args.Result);

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        Assert.Equal(23, Assert.Single(opened).Count);
        Assert.Equal(CarrierName, opened[0].CarrierId);
        Assert.Empty(closed);

        Assert.True(machine.Bridge.Close(CarrierName).IsAccepted);

        Assert.Equal(0, Assert.Single(closed).Count);
        Assert.Equal(23, closed[0].Refused.Count);
    }

    [Fact]
    public void A_refused_open_announces_nothing()
    {
        var machine = Build(verified: false);
        var announcements = 0;

        machine.Bridge.Opened += (_, _) => announcements++;
        machine.Bridge.Closed += (_, _) => announcements++;

        Assert.False(machine.Bridge.Open(CarrierName).IsAccepted);
        Assert.False(machine.Bridge.Close(CarrierName).IsAccepted);
        Assert.False(machine.Bridge.Open("CAR-404").IsAccepted);

        Assert.Equal(0, announcements);
    }

    // ------------------------------------------------------------------- the rest

    [Fact]
    public void The_collections_a_caller_gets_are_snapshots()
    {
        var machine = Build();

        Assert.True(machine.Bridge.Open(CarrierName).IsAccepted);

        var entries = machine.Bridge.SubstratesOf(CarrierName);
        var carriers = machine.Bridge.OpenCarriers;

        foreach (var entry in entries)
        {
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, Chuck).IsAccepted);
            Assert.True(machine.Substrates.MoveTo(entry.SubstrateId, entry.LocationId).IsAccepted);
        }

        Assert.True(machine.Bridge.Close(CarrierName).IsAccepted);

        // The lists a caller already has do not empty themselves under it.
        Assert.Equal(23, entries.Count);
        Assert.Single(carriers);
        Assert.Empty(machine.Bridge.OpenCarriers);
    }

    [Fact]
    public void Nothing_is_known_about_a_carrier_nobody_opened()
    {
        var machine = Build();

        Assert.Empty(machine.Bridge.SubstratesOf("CAR-404"));
        Assert.Empty(machine.Bridge.OpenCarriers);
    }

    [Fact]
    public void Null_arguments_are_rejected()
    {
        var machine = Build();

        Assert.Throws<ArgumentNullException>(() => new CarrierSubstrateBridge(null!, machine.Substrates));
        Assert.Throws<ArgumentNullException>(() => new CarrierSubstrateBridge(machine.Carriers, null!));
        Assert.Throws<ArgumentNullException>(() => machine.Bridge.Open(null!));
        Assert.Throws<ArgumentNullException>(() => machine.Bridge.Close(null!));
        Assert.Throws<ArgumentNullException>(() => machine.Bridge.SubstratesOf(null!));
    }

    [Fact]
    public void A_bridge_with_no_options_of_its_own_takes_the_defaults()
    {
        var carriers = new CarrierManager();
        var bridge = new CarrierSubstrateBridge(carriers, new SubstrateTracker());

        Assert.True(bridge.Options.OpensWhenSlotMapVerified);
        Assert.False(bridge.Options.ClosesWhenCarrierFinished);
        Assert.True(bridge.Options.ReturnsToItsOwnSlot);
        Assert.True(bridge.Options.CopiesLotId);
        Assert.True(bridge.Options.CopiesUsage);
        Assert.Equal(string.Empty, bridge.Options.SubstrateType);
        Assert.False(bridge.IsAttached);

        // The two defaults a fab is most likely to disagree with, asserted as values rather than
        // described in a comment.
        Assert.Equal("CAR-001-SLOT-03", bridge.Options.SlotLocationName(CarrierName, 3));
        Assert.Equal("CAR-001-03", bridge.Options.SubstrateName(CarrierName, 3));
        Assert.True(bridge.Options.IsOccupied(1));
        Assert.True(bridge.Options.IsOccupied(255));
        Assert.False(bridge.Options.IsOccupied(0));
    }
}
