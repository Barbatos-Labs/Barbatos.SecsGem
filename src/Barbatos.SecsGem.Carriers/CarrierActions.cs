// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// The answer to a carrier or port action - <c>CAACK</c>.
/// </summary>
/// <remarks>
/// Four codes, and the catalogue defines all four - which makes this the firmest thing in the
/// package. Three of them are refusals that say something different: a command this end does not
/// know, a command it knows but cannot do just now, and a command whose data was wrong.
/// </remarks>
public enum CarrierAcknowledgement : byte
{
    /// <summary>Accepted; it will be performed.</summary>
    Accepted = 0,

    /// <summary>Denied: not a command this end knows.</summary>
    InvalidCommand = 1,

    /// <summary>Denied: a command this end knows, but not from the state it is in.</summary>
    /// <remarks>The code almost every refusal in this package uses, because almost every rule E87 has is about which state a thing is in.</remarks>
    CannotPerformNow = 2,

    /// <summary>Denied: the carrier, the port or the data named in it was wrong.</summary>
    InvalidData = 3,
}

/// <summary>
/// The answer to the three carrier requests that are not actions - <c>ACKC3</c>.
/// </summary>
/// <remarks>
/// Stream 3 uses two different acknowledgement items and E87 uses both. An action goes out on
/// S3F17 and comes back as <see cref="CarrierAcknowledgement"/>, which can say <em>why</em> not; a
/// carrier identifier read, a reservation and a carrier out come back as <c>ACKC3</c>, which can
/// only say no. That is the standard rather than a simplification here, and it is the reason the
/// reservation action exists in both forms - <c>ReserveAtPort</c> over S3F17 says why it was
/// refused, and S3F29 does not.
/// </remarks>
public enum CarrierRequestAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Denied, with nothing further.</summary>
    Denied = 1,
}

/// <summary>
/// The action names E87 defines, as strings, because that is what they are on the wire.
/// </summary>
/// <remarks>
/// <para>
/// <c>CARRIERACTION</c> is an <c>A</c> item - free text - so this class is a convenience and
/// deliberately not a limit. A host may send an action this list does not name and a machine may
/// answer it: <see cref="CarrierManager"/> refuses what it does not know with
/// <see cref="CarrierAcknowledgement.InvalidCommand"/>, and an application can handle the rest
/// itself before handing the request over. An enum here would have made the library's vocabulary
/// the ceiling, which for a standard that keeps growing is the wrong way round.
/// </para>
/// <para>
/// Comparison is ordinal and case-sensitive, as names on a wire are.
/// </para>
/// </remarks>
public static class CarrierActions
{
    /// <summary>Settle a verification in the carrier's favour, and let it be processed.</summary>
    /// <remarks>The answer to a carrier waiting in <see cref="CarrierIdStatus.WaitingForHost"/> or <see cref="CarrierSlotMapStatus.WaitingForHost"/>; it settles whichever of the two is waiting, or both.</remarks>
    public const string ProceedWithCarrier = "ProceedWithCarrier";

    /// <summary>Settle a verification against the carrier: it is not to be processed.</summary>
    public const string CancelCarrier = "CancelCarrier";

    /// <summary>Tell the machine a carrier is coming before it arrives.</summary>
    /// <remarks>Creates the carrier object, so a host can bind a lot and a slot map to it ahead of time.</remarks>
    public const string CarrierNotification = "CarrierNotification";

    /// <summary>Take back a notification for a carrier that is not coming after all.</summary>
    public const string CancelCarrierNotification = "CancelCarrierNotification";

    /// <summary>Tie an expected carrier identifier to a port.</summary>
    /// <remarks>What makes an identifier verifiable without asking: the equipment reads a label, and a bound carrier is one it can check the label against on its own.</remarks>
    public const string BindCarrier = "BindCarrier";

    /// <summary>Undo a bind.</summary>
    public const string CancelBind = "CancelBind";

    /// <summary>The carrier is to leave the port.</summary>
    public const string CarrierOut = "CarrierOut";

    /// <summary>The carrier is to come in to the port.</summary>
    public const string CarrierIn = "CarrierIn";

    /// <summary>Undo a <see cref="CarrierOut"/> that has not happened yet.</summary>
    public const string CancelCarrierOut = "CancelCarrierOut";

    /// <summary>Undo a <see cref="CarrierIn"/> that has not happened yet.</summary>
    public const string CancelCarrierIn = "CancelCarrierIn";

    /// <summary>Start a finished carrier over.</summary>
    /// <remarks>The only way out of <see cref="CarrierAccessStatus.CarrierComplete"/> or <see cref="CarrierAccessStatus.CarrierStopped"/>: a carrier whose substrates have been touched is a new carrier or it is nothing.</remarks>
    public const string CarrierReCreate = "CarrierReCreate";

    /// <summary>Change who drives a port - manual or automatic.</summary>
    public const string ChangeAccess = "ChangeAccess";

    /// <summary>Hold a port for a carrier that has not arrived.</summary>
    public const string ReserveAtPort = "ReserveAtPort";

    /// <summary>Give a held port back.</summary>
    public const string CancelReservationAtPort = "CancelReservationAtPort";

    /// <summary>Put a port in or out of service.</summary>
    public const string ChangeServiceStatus = "ChangeServiceStatus";
}

/// <summary>What a host is asking for - S3F17.</summary>
/// <remarks>
/// One shape for every action, which is what the message is: <c>CARRIERACTION</c>,
/// <c>CARRIERID</c> and <c>PTN</c>. A port action leaves the carrier empty and a carrier action
/// that is not about a port leaves the port out, and the manager says which it needed.
/// </remarks>
public sealed class CarrierActionRequest
{
    /// <summary>What to do - <c>CARRIERACTION</c>. See <see cref="CarrierActions"/>.</summary>
    public required string Action { get; init; }

    /// <summary>Which carrier - <c>CARRIERID</c>. Empty for a port action.</summary>
    public string CarrierId { get; init; } = string.Empty;

    /// <summary>Which port - <c>PTN</c>. <see langword="null"/> for an action that does not name one.</summary>
    public byte? PortId { get; init; }

    /// <summary>What to change it to, for the actions that need an argument.</summary>
    /// <remarks>
    /// <c>ChangeAccess</c> needs to know which mode and <c>ChangeServiceStatus</c> whether to go
    /// in or out of service. E87 carries those in the object attributes rather than in S3F17, so
    /// this is how the two travel here: a host that sends <c>ChangeAccess</c> with no argument
    /// gets <see cref="CarrierAcknowledgement.InvalidData"/> rather than a guess.
    /// </remarks>
    public string Argument { get; init; } = string.Empty;
}

/// <summary>What a carrier is doing and where - S3F20.</summary>
/// <remarks>
/// Two readings of one item again, for the item with the least to go on.
/// <see cref="Status"/> is the text that arrived, kept whatever it says, and
/// <see cref="AccessStatus"/> is a reading only where a reading is possible - see
/// <see cref="CarrierEquipment.StatusOf"/> for why that is the access status and not one of the
/// other two.
/// </remarks>
public sealed class CarrierStatus
{
    /// <summary>Which carrier - <c>CARRIERID</c>.</summary>
    public required string CarrierId { get; init; }

    /// <summary>What <c>CARRIERSTATUS</c> carried, as it arrived.</summary>
    public required string Status { get; init; }

    /// <summary>The port it is on, or <see langword="null"/> when it is on none - <c>PTN</c>.</summary>
    /// <remarks>
    /// A real answer rather than a missing one. A carrier the host has only announced has not
    /// arrived anywhere, and one in storage is somewhere <c>PTN</c> cannot describe - that one is
    /// a <c>LOC</c>, which S3F24 answers with.
    /// </remarks>
    public byte? PortId { get; init; }

    /// <summary>This library's reading of the status, or <see langword="null"/> when none is possible.</summary>
    /// <remarks>
    /// <see cref="CarrierAccessStatus"/> and nothing else, because it is the only one of a
    /// carrier's three state machines whose every state names itself: <c>WaitingForHost</c>
    /// belongs to both of the other two, so a host hearing it could not tell whether the label or
    /// the slot map was waiting. A word this library did not write - another vendor's, or either
    /// verification state - comes back here as <see langword="null"/> with the text intact.
    /// </remarks>
    public CarrierAccessStatus? AccessStatus { get; init; }

    /// <summary>Returns the carrier, its status and where it is.</summary>
    /// <returns>For example <c>CAR-001 InAccess (port 1)</c>.</returns>
    public override string ToString()
        => PortId is { } port ? $"{CarrierId} {Status} (port {port})" : $"{CarrierId} {Status} (no port)";
}

/// <summary>One storage location, as S3F32 reports it.</summary>
/// <remarks>
/// <see cref="CarrierPortStatus"/>'s twin, for a place addressed by <c>LOC</c> rather than by
/// <c>PTN</c>. Two types rather than one with a nullable address, because the two are not
/// interchangeable: S3F21 asks about one port and S3F31 asks about every storage location, and a
/// host that confused a shelf for a load port would route material into the machine instead of
/// out of it.
/// </remarks>
public sealed class CarrierStorageStatus
{
    /// <summary>Which location - <c>LOC</c>.</summary>
    public required uint Location { get; init; }

    /// <summary>What <c>PORTSTATUS</c> carried, as it arrived.</summary>
    public required string Status { get; init; }

    /// <summary>This library's reading of it, or <see langword="null"/> when the word is not one of E87's four.</summary>
    /// <remarks>
    /// Never silently <see cref="PortTransferState.OutOfService"/>, for the reason
    /// <see cref="CarrierPortStatus.TransferState"/> gives: that is the zero of the enum, and a
    /// shelf nobody may use is a thing a host acts on.
    /// </remarks>
    public PortTransferState? TransferState { get; init; }

    /// <summary>The carrier in it, or empty when there is none.</summary>
    public string CarrierId { get; init; } = string.Empty;

    /// <summary>Returns the location, its status and what is in it.</summary>
    /// <returns>For example <c>location 101 ReadyToUnload (CAR-001)</c>.</returns>
    public override string ToString()
        => CarrierId.Length == 0 ? $"location {Location} {Status}" : $"location {Location} {Status} ({CarrierId})";
}

/// <summary>
/// What a port is doing and what is on it - S3F22.
/// </summary>
/// <remarks>
/// Two readings of one item, because <c>PORTSTATUS</c> has no dictionary entry.
/// <see cref="Status"/> is the text that arrived, kept whatever it says, and
/// <see cref="TransferState"/> is this library's reading of it when the word is one of E87's
/// four. A host that talks to equipment from more than one vendor reads the first; a host that
/// knows it is talking to this library reads the second.
/// </remarks>
public sealed class CarrierPortStatus
{
    /// <summary>Which port - <c>PTN</c>.</summary>
    public required byte PortId { get; init; }

    /// <summary>What <c>PORTSTATUS</c> carried, as it arrived.</summary>
    public required string Status { get; init; }

    /// <summary>This library's reading of it, or <see langword="null"/> when the word is not one of E87's four.</summary>
    /// <remarks>
    /// Never silently <see cref="PortTransferState.OutOfService"/>: that is the zero of the enum,
    /// and an unfamiliar status reading as a port nobody may use is the kind of mistake a host
    /// acts on.
    /// </remarks>
    public PortTransferState? TransferState { get; init; }

    /// <summary>The carrier on the port, or empty when there is none.</summary>
    public string CarrierId { get; init; } = string.Empty;

    /// <summary>Returns the port, its status and what is on it.</summary>
    /// <returns>For example <c>port 1 ReadyToUnload (CAR-001)</c>.</returns>
    public override string ToString()
        => CarrierId.Length == 0 ? $"port {PortId} {Status}" : $"port {PortId} {Status} ({CarrierId})";
}

/// <summary>What the machine answered - S3F18.</summary>
public sealed class CarrierActionResult
{
    /// <summary>The answer - <c>CAACK</c>.</summary>
    public required CarrierAcknowledgement Acknowledgement { get; init; }

    /// <summary>Why not, as a number - <c>ERRCODE</c>. Zero when accepted.</summary>
    public uint ErrorCode { get; init; }

    /// <summary>Why not, in words - <c>ERRTEXT</c>.</summary>
    /// <remarks>
    /// Worth reading. Every refusal this package produces names the state that refused it, because
    /// "cannot perform now" on its own sends somebody to a log file for an afternoon.
    /// </remarks>
    public string ErrorText { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the action was taken.</summary>
    public bool IsAccepted => Acknowledgement is CarrierAcknowledgement.Accepted;

    /// <summary>An accepted answer.</summary>
    internal static CarrierActionResult Ok { get; } = new() { Acknowledgement = CarrierAcknowledgement.Accepted };

    /// <summary>A refusal that says which state refused it.</summary>
    /// <param name="acknowledgement">Which kind of refusal.</param>
    /// <param name="text">What was wrong.</param>
    /// <param name="code">The error code; 1 by default, which E87 leaves to the equipment.</param>
    /// <returns>The result.</returns>
    internal static CarrierActionResult Refused(CarrierAcknowledgement acknowledgement, string text, uint code = 1)
        => new() { Acknowledgement = acknowledgement, ErrorCode = code, ErrorText = text };
}
