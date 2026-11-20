// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// A manager that outlives the process, and the line between what a host said and what the
/// machine saw.
/// </summary>
/// <remarks>
/// E87's own README used to list this as missing, with the reason beside it: "a machine that
/// reboots with a carrier on a port re-reads the label, which is also what E87 expects". That
/// sentence is the whole specification of these tests. A host's instructions - which ports are in
/// service, who drives them, what a carrier's lot is, which port is reserved for what - survive a
/// restart, because a restart does not change what anybody asked for. Everything the machine
/// sensed is sensed again.
/// </remarks>
public class CarrierStateTests
{
    private const string First = "CAR-001";
    private const string Second = "CAR-002";

    private static readonly byte[] SlotMap = [1, 1, 0, 1];

    private static CarrierManager Build(params byte[] ports)
    {
        var manager = new CarrierManager();

        foreach (var portId in ports.Length > 0 ? ports : [(byte)1])
        {
            manager.AddPort(portId);
            Assert.True(manager.SetServiceStatus(portId, inService: true).IsAccepted);
            Assert.True(manager.SetTransferState(portId, PortTransferState.ReadyToLoad).IsAccepted);
        }

        return manager;
    }

    private static void Arrive(CarrierManager manager, byte portId, string carrierId)
    {
        Assert.True(manager.PlaceCarrier(portId, carrierId).IsAccepted);
        Assert.True(manager.ReadCarrierId(carrierId, verified: true).IsAccepted);
        Assert.True(manager.ReadSlotMap(carrierId, SlotMap, verified: true).IsAccepted);
    }

    [Fact]
    public void What_a_host_said_comes_back()
    {
        var manager = Build();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierNotification,
            CarrierId = First,
            Argument = "LOT-7",
        }).IsAccepted);

        var restarted = Build();
        var report = restarted.Restore(manager.Capture());

        Assert.True(report.IsAccepted);

        var carrier = restarted.FindCarrier(First)!;

        Assert.Equal("LOT-7", carrier.LotId);
        Assert.Contains(First, report.Restored, StringComparer.Ordinal);
    }

    [Fact]
    public void A_carrier_comes_back_unread_because_E87_wants_the_label_read_again()
    {
        var manager = Build();

        Arrive(manager, 1, First);

        var snapshot = manager.Capture();

        // The file is faithful: it says the label was read and agreed.
        Assert.Equal(CarrierIdStatus.IdVerificationOk, snapshot.Carriers[0].IdStatus);
        Assert.Equal(CarrierSlotMapStatus.SlotMapVerificationOk, snapshot.Carriers[0].SlotMapStatus);
        Assert.Equal(4, snapshot.Carriers[0].SlotMap.Count);
        Assert.Equal((byte)1, snapshot.Carriers[0].PortId);

        var restarted = Build();
        var report = restarted.Restore(snapshot);
        var carrier = restarted.FindCarrier(First)!;

        // And the manager is not: a machine that claimed a verification it never performed would
        // process a lot nobody confirmed.
        Assert.Equal(CarrierIdStatus.IdNotRead, carrier.IdStatus);
        Assert.Equal(CarrierSlotMapStatus.SlotMapNotRead, carrier.SlotMapStatus);
        Assert.False(carrier.IsVerified);
        Assert.True(carrier.SlotMap.IsEmpty);
        Assert.Null(carrier.PortId);
        Assert.Contains("re-read", report.Adjusted[First], StringComparison.Ordinal);
    }

    [Fact]
    public void A_carrier_that_was_in_access_comes_back_stopped()
    {
        var manager = Build();

        Arrive(manager, 1, First);
        Assert.True(manager.BeginAccess(First).IsAccepted);

        var restarted = Build();
        var report = restarted.Restore(manager.Capture());

        // Not NotAccessed, which E87 itself rules out once the substrates have been touched, and
        // not InAccess, which would claim a machine that has just started is working on something.
        // Stopped is "finished early: a fault", and losing power part way through is a fault.
        Assert.Equal(CarrierAccessStatus.CarrierStopped, restarted.FindCarrier(First)!.AccessStatus);
        Assert.Contains("fault", report.Adjusted[First], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CarrierAccessStatus.NotAccessed)]
    [InlineData(CarrierAccessStatus.CarrierComplete)]
    [InlineData(CarrierAccessStatus.CarrierStopped)]
    public void Every_other_access_state_is_history_and_comes_back_as_itself(CarrierAccessStatus was)
    {
        var manager = Build();

        Arrive(manager, 1, First);

        if (was is not CarrierAccessStatus.NotAccessed)
        {
            Assert.True(manager.BeginAccess(First).IsAccepted);
            Assert.True(manager.EndAccess(First, stopped: was is CarrierAccessStatus.CarrierStopped).IsAccepted);
        }

        var restarted = Build();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);
        Assert.Equal(was, restarted.FindCarrier(First)!.AccessStatus);
    }

    [Fact]
    public void A_ports_instructions_come_back_and_what_is_on_it_does_not()
    {
        var manager = Build(1, 2);

        Arrive(manager, 1, First);
        Assert.True(manager.SetServiceStatus(2, inService: false).IsAccepted);

        var restarted = Build(1, 2);
        var report = restarted.Restore(manager.Capture());

        var one = restarted.FindPort(1)!;
        var two = restarted.FindPort(2)!;

        // In service is a host instruction, so port 1 is in service and port 2 is not. What is
        // sitting on port 1 is a thing the machine senses, and it has not sensed anything yet.
        Assert.True(one.IsInService);
        Assert.Equal(PortTransferState.TransferBlocked, one.TransferState);
        Assert.Equal(PortAssociationState.NotAssociated, one.AssociationState);
        Assert.Null(one.AssociatedCarrierId);
        Assert.Equal(PortTransferState.OutOfService, two.TransferState);
        Assert.Contains("sensed", report.Adjusted["port 1"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_reservation_is_a_host_instruction_and_survives()
    {
        var manager = Build();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ReserveAtPort,
            CarrierId = Second,
            PortId = 1,
        }).IsAccepted);

        Assert.Equal(PortReservationState.Reserved, manager.FindPort(1)!.ReservationState);

        var restarted = Build();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        var port = restarted.FindPort(1)!;

        // A carrier on its way to a reserved port is still on its way, and a restart that gave
        // that port away would send it to the wrong place.
        Assert.Equal(PortReservationState.Reserved, port.ReservationState);
        Assert.Equal(Second, port.ReservedForCarrierId);
    }

    [Fact]
    public void A_snapshot_does_not_invent_hardware()
    {
        var manager = Build(1, 2);

        var restarted = Build(1);
        var report = restarted.Restore(manager.Capture());

        Assert.True(report.IsAccepted);
        Assert.Null(restarted.FindPort(2));
        Assert.Contains("declared from", report.Adjusted["port 2"], StringComparison.Ordinal);
        Assert.Equal("port 1", Assert.Single(report.Restored));
    }

    [Fact]
    public void A_manager_that_already_has_carriers_refuses()
    {
        var manager = Build();

        Arrive(manager, 1, First);

        var busy = Build();

        Arrive(busy, 1, Second);

        var report = busy.Restore(manager.Capture());

        Assert.False(report.IsAccepted);
        Assert.Contains("already has", report.Reason, StringComparison.Ordinal);

        // And nothing was half applied: the carrier it had is untouched and the stranger is absent.
        Assert.True(busy.FindCarrier(Second)!.IsVerified);
        Assert.Null(busy.FindCarrier(First));
    }

    [Fact]
    public void Restoring_raises_nothing()
    {
        var manager = Build();

        Arrive(manager, 1, First);
        Assert.True(manager.BeginAccess(First).IsAccepted);

        var restarted = Build();
        var moves = 0;

        restarted.CarrierStateChanged += (_, _) => moves++;
        restarted.PortStateChanged += (_, _) => moves++;

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        // A host that heard "carrier stopped" as a transition would believe the machine had just
        // stopped one, rather than that it had been restarted. The report is how an application
        // tells it something true instead.
        Assert.Equal(0, moves);
    }

    [Fact]
    public void A_restored_carrier_goes_through_E87_again_from_the_beginning()
    {
        var manager = Build();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierNotification,
            CarrierId = First,
            Argument = "LOT-7",
        }).IsAccepted);

        var restarted = Build();

        Assert.True(restarted.Restore(manager.Capture()).IsAccepted);

        // The port comes back blocked, so the machine has to say it can take a carrier again -
        // which it does once it has looked. A restore that left the port ready to load would be
        // inviting a material handler to put a box on something nobody had checked.
        Assert.False(restarted.PlaceCarrier(1, First).IsAccepted);
        Assert.True(restarted.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);

        // Which is the point of coming back unread rather than coming back absent: the lot is
        // still known, and the sequence that confirms the box runs again as normal.
        Arrive(restarted, 1, First);

        var carrier = restarted.FindCarrier(First)!;

        Assert.True(carrier.IsVerified);
        Assert.Equal("LOT-7", carrier.LotId);
        Assert.True(restarted.BeginAccess(First).IsAccepted);
    }

    [Fact]
    public void An_empty_manager_takes_a_snapshot_of_nothing()
    {
        var restarted = Build();
        var report = restarted.Restore(new CarrierManagerSnapshot());

        Assert.True(report.IsAccepted);
        Assert.Empty(report.Restored);
        Assert.Empty(report.Adjusted);
    }

    [Fact]
    public void A_snapshot_nobody_passed_is_rejected()
        => Assert.Throws<ArgumentNullException>(() => new CarrierManager().Restore(null!));
}
