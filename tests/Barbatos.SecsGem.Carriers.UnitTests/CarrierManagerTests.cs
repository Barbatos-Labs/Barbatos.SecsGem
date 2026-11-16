// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers.UnitTests;

/// <summary>
/// E87's state machines, with no link, no transport and no message anywhere near them.
/// </summary>
/// <remarks>
/// <para>
/// This is the file that matters in this package. A state machine is the one part of a protocol
/// stack that can be tested exhaustively without a wire, and the reason
/// <see cref="CarrierManager"/> has no connection is so that these tests can be about E87 rather
/// than about plumbing: each one names a rule the standard has and shows the refusal.
/// </para>
/// <para>
/// The rules are E87 as this library understands it, and nothing in this repository can check them
/// the way the catalogue checks a message body. What these tests can do is make the behaviour
/// explicit, so a fab that disagrees has something concrete to point at.
/// </para>
/// </remarks>
public class CarrierManagerTests
{
    private static CarrierManager WithPort(byte portId = 1, bool inService = true)
    {
        var manager = new CarrierManager();

        manager.AddPort(portId);

        if (inService)
        {
            Assert.True(manager.SetServiceStatus(portId, inService: true).IsAccepted);
        }

        return manager;
    }

    /// <summary>A carrier placed, verified and ready to be accessed - the happy path, in one place.</summary>
    private static CarrierManager WithVerifiedCarrier(string carrierId = "CAR-001", byte portId = 1)
    {
        var manager = WithPort(portId);

        Assert.True(manager.SetTransferState(portId, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(manager.PlaceCarrier(portId, carrierId).IsAccepted);
        Assert.True(manager.ReadCarrierId(carrierId, verified: true).IsAccepted);
        Assert.True(manager.ReadSlotMap(carrierId, new byte[] { 1, 1, 0, 1 }, verified: true).IsAccepted);

        return manager;
    }

    // --------------------------------------------------------------- the load port

    [Fact]
    public void A_port_starts_out_of_service_and_nothing_may_be_put_on_it()
    {
        var manager = new CarrierManager();
        var port = manager.AddPort(1);

        Assert.Equal(PortTransferState.OutOfService, port.TransferState);
        Assert.False(port.IsInService);

        var refusal = manager.SetTransferState(1, PortTransferState.ReadyToLoad);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("OutOfService", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_carrier_can_only_be_placed_on_a_port_that_is_ready_to_load()
    {
        var manager = WithPort();

        // In service but blocked: a carrier appearing now is a material handling fault, and
        // accepting it would hide the fault.
        var refusal = manager.PlaceCarrier(1, "CAR-001");

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("TransferBlocked", refusal.ErrorText, StringComparison.Ordinal);
        Assert.Contains("ReadyToLoad", refusal.ErrorText, StringComparison.Ordinal);

        Assert.True(manager.SetTransferState(1, PortTransferState.ReadyToLoad).IsAccepted);
        Assert.True(manager.PlaceCarrier(1, "CAR-001").IsAccepted);
    }

    [Fact]
    public void Placing_a_carrier_associates_the_port_and_blocks_further_transfer()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");

        var port = manager.FindPort(1);

        Assert.NotNull(port);
        Assert.Equal(PortAssociationState.Associated, port.AssociationState);
        Assert.Equal("CAR-001", port.AssociatedCarrierId);
        Assert.Equal(PortTransferState.TransferBlocked, port.TransferState);

        // And the carrier knows where it is, which is what a host asks about with S3F23.
        Assert.Equal((byte)1, manager.FindCarrier("CAR-001")?.PortId);
    }

    [Fact]
    public void A_reserved_port_takes_only_the_carrier_it_is_reserved_for()
    {
        var manager = WithPort();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ReserveAtPort,
            CarrierId = "CAR-001",
            PortId = 1,
        }).IsAccepted);

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);

        var refusal = manager.PlaceCarrier(1, "CAR-999");

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("CAR-001", refusal.ErrorText, StringComparison.Ordinal);

        // The right one goes on.
        Assert.True(manager.PlaceCarrier(1, "CAR-001").IsAccepted);
    }

    [Fact]
    public void A_carrier_can_only_be_removed_from_a_port_that_is_ready_to_unload()
    {
        var manager = WithVerifiedCarrier();

        var refusal = manager.RemoveCarrier(1);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("ReadyToUnload", refusal.ErrorText, StringComparison.Ordinal);

        Assert.True(manager.SetTransferState(1, PortTransferState.ReadyToUnload).IsAccepted);
        Assert.True(manager.RemoveCarrier(1).IsAccepted);

        var port = manager.FindPort(1);

        Assert.NotNull(port);
        Assert.Equal(PortAssociationState.NotAssociated, port.AssociationState);
        Assert.Null(port.AssociatedCarrierId);
        Assert.Null(manager.FindCarrier("CAR-001")?.PortId);
    }

    [Fact]
    public void A_port_holding_a_carrier_cannot_be_taken_out_of_service()
    {
        var manager = WithVerifiedCarrier();

        var refusal = manager.SetServiceStatus(1, inService: false);

        // The carrier would be stranded on a port nobody is allowed to touch.
        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("CAR-001", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_port_with_nothing_on_it_cannot_be_ready_to_unload()
    {
        var manager = WithPort();

        var refusal = manager.SetTransferState(1, PortTransferState.ReadyToUnload);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("no carrier", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Out_of_service_is_not_reachable_through_the_transfer_state()
    {
        var manager = WithPort();

        var refusal = manager.SetTransferState(1, PortTransferState.OutOfService);

        // Service is a different decision from readiness, and conflating the two is how a port
        // ends up out of service because a sensor cleared.
        Assert.Equal(CarrierAcknowledgement.InvalidData, refusal.Acknowledgement);
        Assert.Contains("ChangeServiceStatus", refusal.ErrorText, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the verifications

    [Fact]
    public void An_identifier_the_machine_cannot_verify_leaves_the_carrier_waiting_for_the_host()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");

        Assert.True(manager.ReadCarrierId("CAR-001", verified: null).IsAccepted);
        Assert.Equal(CarrierIdStatus.WaitingForHost, manager.FindCarrier("CAR-001")?.IdStatus);
    }

    [Fact]
    public void An_identifier_cannot_be_read_twice()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadCarrierId("CAR-001", verified: true);

        var refusal = manager.ReadCarrierId("CAR-001", verified: false);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("IdVerificationOk", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Proceeding_settles_both_verifications_when_both_are_waiting()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadCarrierId("CAR-001", verified: null);
        manager.ReadSlotMap("CAR-001", new byte[] { 1, 1 }, verified: null);

        var carrier = manager.FindCarrier("CAR-001");

        Assert.NotNull(carrier);
        Assert.False(carrier.IsVerified);

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ProceedWithCarrier,
            CarrierId = "CAR-001",
        }).IsAccepted);

        // One action for the carrier rather than one per verification, so proceeding proceeds with
        // everything outstanding.
        Assert.Equal(CarrierIdStatus.IdVerificationOk, carrier.IdStatus);
        Assert.Equal(CarrierSlotMapStatus.SlotMapVerificationOk, carrier.SlotMapStatus);
        Assert.True(carrier.IsVerified);
    }

    [Fact]
    public void Cancelling_fails_both_verifications_when_both_are_waiting()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadCarrierId("CAR-001", verified: null);
        manager.ReadSlotMap("CAR-001", new byte[] { 1 }, verified: null);

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CancelCarrier,
            CarrierId = "CAR-001",
        }).IsAccepted);

        var carrier = manager.FindCarrier("CAR-001");

        Assert.NotNull(carrier);
        Assert.Equal(CarrierIdStatus.IdVerificationFailed, carrier.IdStatus);
        Assert.Equal(CarrierSlotMapStatus.SlotMapVerificationFailed, carrier.SlotMapStatus);
        Assert.False(carrier.IsVerified);
    }

    [Fact]
    public void Proceeding_with_a_carrier_that_is_not_waiting_is_refused_and_says_what_it_is()
    {
        var manager = WithVerifiedCarrier();

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ProceedWithCarrier,
            CarrierId = "CAR-001",
        });

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("not waiting for the host", refusal.ErrorText, StringComparison.Ordinal);
        Assert.Contains("IdVerificationOk", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slot_map_counts_the_slots_that_hold_something()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadSlotMap("CAR-001", new byte[] { 1, 0, 1, 0, 1 }, verified: true);

        var carrier = manager.FindCarrier("CAR-001");

        Assert.NotNull(carrier);
        Assert.Equal(3, carrier.SubstrateCount);
        Assert.Equal(5, carrier.Capacity);
        Assert.Equal([1, 0, 1, 0, 1], carrier.SlotMap.ToArray());
    }

    [Fact]
    public void A_slot_map_is_copied_rather_than_held()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");

        var slots = new byte[] { 1, 1, 1 };

        manager.ReadSlotMap("CAR-001", slots, verified: true);

        // The caller's buffer is the caller's, and this object outlives the call.
        slots[0] = 0;
        slots[1] = 0;

        Assert.Equal([1, 1, 1], manager.FindCarrier("CAR-001")?.SlotMap.ToArray());
    }

    // ---------------------------------------------------------------- the access

    [Fact]
    public void An_unverified_carrier_cannot_be_accessed()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadCarrierId("CAR-001", verified: null);

        var refusal = manager.BeginAccess("CAR-001");

        // The rule this package exists for: a machine that starts on a carrier the host has not
        // confirmed is how a lot gets processed against the wrong recipe.
        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("not verified", refusal.ErrorText, StringComparison.Ordinal);
        Assert.Contains("WaitingForHost", refusal.ErrorText, StringComparison.Ordinal);
        Assert.Contains("SlotMapNotRead", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verified_carrier_can_be_accessed_and_completed()
    {
        var manager = WithVerifiedCarrier();

        Assert.True(manager.BeginAccess("CAR-001").IsAccepted);
        Assert.Equal(CarrierAccessStatus.InAccess, manager.FindCarrier("CAR-001")?.AccessStatus);

        Assert.True(manager.EndAccess("CAR-001").IsAccepted);
        Assert.Equal(CarrierAccessStatus.CarrierComplete, manager.FindCarrier("CAR-001")?.AccessStatus);
    }

    [Fact]
    public void A_carrier_stopped_early_says_so_rather_than_saying_complete()
    {
        var manager = WithVerifiedCarrier();

        manager.BeginAccess("CAR-001");

        Assert.True(manager.EndAccess("CAR-001", stopped: true).IsAccepted);
        Assert.Equal(CarrierAccessStatus.CarrierStopped, manager.FindCarrier("CAR-001")?.AccessStatus);
    }

    [Fact]
    public void A_finished_carrier_cannot_be_accessed_again_without_being_re_created()
    {
        var manager = WithVerifiedCarrier();

        manager.BeginAccess("CAR-001");
        manager.EndAccess("CAR-001");

        var refusal = manager.BeginAccess("CAR-001");

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("CarrierComplete", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_creating_a_finished_carrier_puts_it_back_to_the_start()
    {
        var manager = WithVerifiedCarrier();

        manager.BeginAccess("CAR-001");
        manager.EndAccess("CAR-001");

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierReCreate,
            CarrierId = "CAR-001",
        }).IsAccepted);

        var carrier = manager.FindCarrier("CAR-001");

        Assert.NotNull(carrier);
        Assert.Equal(CarrierAccessStatus.NotAccessed, carrier.AccessStatus);
        Assert.Equal(CarrierIdStatus.IdNotRead, carrier.IdStatus);
        Assert.Equal(CarrierSlotMapStatus.SlotMapNotRead, carrier.SlotMapStatus);
        Assert.Empty(carrier.SlotMap.ToArray());

        // And it has to be verified again before anything touches it, which is the point: the
        // substrates in it have been handled, so this is a new carrier with an old name.
        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, manager.BeginAccess("CAR-001").Acknowledgement);
    }

    [Fact]
    public void A_carrier_in_access_cannot_be_re_created()
    {
        var manager = WithVerifiedCarrier();

        manager.BeginAccess("CAR-001");

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierReCreate,
            CarrierId = "CAR-001",
        });

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("stop it before", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_carrier_in_access_cannot_be_taken_out()
    {
        var manager = WithVerifiedCarrier();

        manager.BeginAccess("CAR-001");

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierOut,
            CarrierId = "CAR-001",
        });

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("InAccess", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Taking_a_finished_carrier_out_makes_the_port_ready_to_unload()
    {
        var manager = WithVerifiedCarrier();

        manager.BeginAccess("CAR-001");
        manager.EndAccess("CAR-001");

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierOut,
            CarrierId = "CAR-001",
        }).IsAccepted);

        Assert.Equal(PortTransferState.ReadyToUnload, manager.FindPort(1)?.TransferState);
    }

    // ------------------------------------------------------------- access and hold

    [Fact]
    public void Access_mode_cannot_change_during_a_transfer()
    {
        var manager = WithPort();

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ChangeAccess,
            PortId = 1,
            Argument = nameof(PortAccessMode.Auto),
        });

        // A handler that was told the port is automatic has started moving; an operator taking it
        // manual mid-move is how a carrier gets dropped.
        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("ReadyToLoad", refusal.ErrorText, StringComparison.Ordinal);
        Assert.Equal(PortAccessMode.Manual, manager.FindPort(1)?.AccessMode);
    }

    [Fact]
    public void Access_mode_changes_when_no_transfer_is_in_progress()
    {
        var manager = WithPort();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ChangeAccess,
            PortId = 1,
            Argument = "auto",
        }).IsAccepted);

        Assert.Equal(PortAccessMode.Auto, manager.FindPort(1)?.AccessMode);
    }

    [Fact]
    public void An_access_mode_that_is_not_one_of_the_two_is_invalid_data()
    {
        var manager = WithPort();

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ChangeAccess,
            PortId = 1,
            Argument = "Sideways",
        });

        Assert.Equal(CarrierAcknowledgement.InvalidData, refusal.Acknowledgement);
        Assert.Contains("Manual", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_reservation_cannot_be_taken_twice_and_can_be_given_back()
    {
        var manager = WithPort();

        var reserve = new CarrierActionRequest
        {
            Action = CarrierActions.ReserveAtPort,
            CarrierId = "CAR-001",
            PortId = 1,
        };

        Assert.True(manager.Perform(reserve).IsAccepted);

        var refusal = manager.Perform(reserve);

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("already reserved", refusal.ErrorText, StringComparison.Ordinal);

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CancelReservationAtPort,
            PortId = 1,
        }).IsAccepted);

        Assert.Equal(PortReservationState.NotReserved, manager.FindPort(1)?.ReservationState);
        Assert.True(manager.Perform(reserve).IsAccepted);
    }

    [Fact]
    public void Cancelling_a_reservation_that_is_not_there_is_refused()
    {
        var manager = WithPort();

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CancelReservationAtPort,
            PortId = 1,
        });

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("not reserved", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Binding_a_carrier_holds_the_port_for_it_and_creates_the_carrier()
    {
        var manager = WithPort();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.BindCarrier,
            CarrierId = "CAR-001",
            PortId = 1,
        }).IsAccepted);

        var port = manager.FindPort(1);

        Assert.NotNull(port);
        Assert.Equal(PortReservationState.Reserved, port.ReservationState);
        Assert.Equal("CAR-001", port.ReservedForCarrierId);

        // The carrier exists before it arrives, which is what lets a host attach a lot to it.
        Assert.NotNull(manager.FindCarrier("CAR-001"));
    }

    // ------------------------------------------------------------- notifications

    [Fact]
    public void A_notification_creates_a_carrier_before_it_arrives_and_can_be_taken_back()
    {
        var manager = new CarrierManager();

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CarrierNotification,
            CarrierId = "CAR-001",
            Argument = "LOT-7",
        }).IsAccepted);

        Assert.Equal("LOT-7", manager.FindCarrier("CAR-001")?.LotId);

        Assert.True(manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CancelCarrierNotification,
            CarrierId = "CAR-001",
        }).IsAccepted);

        Assert.Null(manager.FindCarrier("CAR-001"));
    }

    [Fact]
    public void A_notification_cannot_be_taken_back_once_the_carrier_has_arrived()
    {
        var manager = WithPort();

        manager.Perform(new CarrierActionRequest { Action = CarrierActions.CarrierNotification, CarrierId = "CAR-001" });
        manager.SetTransferState(1, PortTransferState.ReadyToLoad);
        manager.PlaceCarrier(1, "CAR-001");

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.CancelCarrierNotification,
            CarrierId = "CAR-001",
        });

        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, refusal.Acknowledgement);
        Assert.Contains("has arrived", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void Notifying_twice_about_one_carrier_is_refused()
    {
        var manager = new CarrierManager();
        var notify = new CarrierActionRequest { Action = CarrierActions.CarrierNotification, CarrierId = "CAR-001" };

        Assert.True(manager.Perform(notify).IsAccepted);
        Assert.Equal(CarrierAcknowledgement.CannotPerformNow, manager.Perform(notify).Acknowledgement);
    }

    // ------------------------------------------------------- refusals and events

    [Fact]
    public void An_unknown_action_is_an_invalid_command_rather_than_an_exception()
    {
        var manager = WithPort();

        var refusal = manager.Perform(new CarrierActionRequest
        {
            Action = "TeleportCarrier",
            CarrierId = "CAR-001",
            PortId = 1,
        });

        // CARRIERACTION is free text, so a host may ask for something this library has never heard
        // of. The answer is the code E5 has for exactly that, not a crash.
        Assert.Equal(CarrierAcknowledgement.InvalidCommand, refusal.Acknowledgement);
        Assert.Contains("TeleportCarrier", refusal.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_action_naming_hardware_that_does_not_exist_is_invalid_data()
    {
        var manager = WithPort();

        var noPort = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ReserveAtPort,
            CarrierId = "CAR-001",
            PortId = 9,
        });

        Assert.Equal(CarrierAcknowledgement.InvalidData, noPort.Acknowledgement);
        Assert.Contains("no port 9", noPort.ErrorText, StringComparison.Ordinal);

        var noCarrier = manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ProceedWithCarrier,
            CarrierId = "CAR-999",
        });

        Assert.Equal(CarrierAcknowledgement.InvalidData, noCarrier.Acknowledgement);
        Assert.Contains("CAR-999", noCarrier.ErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public void An_action_that_needs_a_port_and_has_none_says_which_action_needed_one()
    {
        var manager = WithPort();

        foreach (var action in new[]
        {
            CarrierActions.ReserveAtPort,
            CarrierActions.CancelReservationAtPort,
            CarrierActions.BindCarrier,
            CarrierActions.ChangeAccess,
            CarrierActions.CarrierIn,
        })
        {
            var refusal = manager.Perform(new CarrierActionRequest { Action = action, CarrierId = "CAR-001" });

            Assert.Equal(CarrierAcknowledgement.InvalidData, refusal.Acknowledgement);
            Assert.Contains("port", refusal.ErrorText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_refusal_this_package_produces_says_something()
    {
        var manager = WithPort();

        // A sweep rather than a rule: whatever the reason, ERRTEXT is the only place a host can
        // read it, and "cannot perform now" on its own sends somebody to a log file.
        List<CarrierActionResult> refusals =
        [
            manager.PlaceCarrier(1, "CAR-001"),
            manager.RemoveCarrier(1),
            manager.SetTransferState(1, PortTransferState.ReadyToUnload),
            manager.BeginAccess("CAR-001"),
            manager.EndAccess("CAR-001"),
            manager.Perform(new CarrierActionRequest { Action = "Nonsense" }),
            manager.Perform(new CarrierActionRequest { Action = CarrierActions.ProceedWithCarrier }),
            manager.Perform(new CarrierActionRequest { Action = CarrierActions.CarrierOut, CarrierId = "CAR-404" }),
        ];

        Assert.All(refusals, refusal =>
        {
            Assert.False(refusal.IsAccepted);
            Assert.NotEqual(string.Empty, refusal.ErrorText);
            Assert.NotEqual(0u, refusal.ErrorCode);
        });
    }

    [Fact]
    public void A_state_change_is_announced_after_the_state_has_moved()
    {
        var manager = WithPort();

        CarrierIdStatus? seenOnCarrier = null;
        CarrierStateChangedEventArgs? carrierArgs = null;

        manager.CarrierStateChanged += (_, args) =>
        {
            carrierArgs = args;
            seenOnCarrier = args.Carrier.IdStatus;
        };

        PortTransferState? seenOnPort = null;
        PortStateChangedEventArgs? portArgs = null;

        manager.PortStateChanged += (_, args) =>
        {
            portArgs = args;
            seenOnPort = args.Port.TransferState;
        };

        manager.SetTransferState(1, PortTransferState.ReadyToLoad);

        Assert.NotNull(portArgs);
        Assert.Equal(PortStateMachine.TransferState, portArgs.StateMachine);
        Assert.Equal((int)PortTransferState.TransferBlocked, portArgs.Previous);
        Assert.Equal((int)PortTransferState.ReadyToLoad, portArgs.Current);

        // The point of the assertion: a handler that reads the object it was just told about sees
        // the state the event is announcing, not the one before it.
        Assert.Equal(PortTransferState.ReadyToLoad, seenOnPort);

        manager.PlaceCarrier(1, "CAR-001");
        manager.ReadCarrierId("CAR-001", verified: true);

        Assert.NotNull(carrierArgs);
        Assert.Equal(CarrierStateMachine.IdStatus, carrierArgs.StateMachine);
        Assert.Equal((int)CarrierIdStatus.IdVerificationOk, carrierArgs.Current);
        Assert.Equal(CarrierIdStatus.IdVerificationOk, seenOnCarrier);
    }

    [Fact]
    public void A_state_that_does_not_move_is_not_announced()
    {
        var manager = WithPort();
        var moves = 0;

        manager.PortStateChanged += (_, _) => moves++;

        // Already in service, and already manual.
        manager.SetServiceStatus(1, inService: true);
        manager.Perform(new CarrierActionRequest
        {
            Action = CarrierActions.ChangeAccess,
            PortId = 1,
            Argument = nameof(PortAccessMode.Manual),
        });

        Assert.Equal(0, moves);
    }

    [Fact]
    public void A_port_declared_twice_is_the_same_port()
    {
        var manager = new CarrierManager();

        var first = manager.AddPort(1);

        manager.SetServiceStatus(1, inService: true);

        var second = manager.AddPort(1);

        // Hardware does not appear twice, and a second declaration must not reset the state the
        // first one has got into.
        Assert.Same(first, second);
        Assert.Equal(PortTransferState.TransferBlocked, second.TransferState);
        Assert.Single(manager.Ports);
    }
}
