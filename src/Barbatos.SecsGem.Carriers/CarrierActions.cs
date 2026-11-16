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
