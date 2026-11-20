// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material;

/// <summary>
/// The answer to an offer of material - <c>RSACK</c>, and the codes are the reference's own.
/// </summary>
/// <remarks>
/// <para>
/// Unusually for this stack, nothing here is inferred. <c>RSACK</c> is a <c>B[1]</c> in the
/// dictionary <em>with a table of values</em>, so these five names are quoted rather than read -
/// and the four refusals are the four reasons E5 thinks a port cannot take a box, which is more
/// than most of this stream says out loud.
/// </para>
/// <para>
/// 5 to 63 belong to the equipment, and they all read as <see cref="Other"/> with the arriving
/// code logged - the same rule <see cref="MaterialGrant"/> keeps for the same reason.
/// </para>
/// </remarks>
public enum MaterialSendAcknowledgement : byte
{
    /// <summary>The receiver will take it.</summary>
    WillAccept = 0,

    /// <summary>There is no such port.</summary>
    InvalidPort = 1,

    /// <summary>The port already has material on it.</summary>
    PortOccupied = 2,

    /// <summary>Busy; offer it again later.</summary>
    Busy = 3,

    /// <summary>The receiver is not in a mode where a host or a handler may drive it.</summary>
    NotInRemoteMode = 4,

    /// <summary>Some other refusal: E5 reserves 5 to 63 for the equipment to define.</summary>
    /// <remarks>Every code from 5 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 5,
}

/// <summary>The answer to a request to be sent material - <c>RRACK</c>.</summary>
/// <remarks>
/// Quoted from the dictionary's own table, as <see cref="MaterialSendAcknowledgement"/> is, and
/// shorter: a request to receive is granted or it is not, and the refusal carries no reason. 2 to
/// 63 belong to the equipment and read as <see cref="Other"/>.
/// </remarks>
public enum MaterialReceiveAcknowledgement : byte
{
    /// <summary>Granted.</summary>
    Granted = 0,

    /// <summary>Denied, with no reason given.</summary>
    Denied = 1,

    /// <summary>Some other refusal: E5 reserves 2 to 63 for the equipment to define.</summary>
    /// <remarks>Every code from 2 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 2,
}

/// <summary>
/// Which step of the interlocked handoff a message is - the nine primaries of S4F1 to S4F18.
/// </summary>
/// <remarks>
/// <para>
/// One enum rather than nine events, because all nine carry the same body - a port and a piece of
/// material - and what distinguishes them is which step of the handshake they announce. The three
/// faults at the end are the ones worth wiring to something: E5 gives this stream a message for
/// material that got stuck at either end and for a transfer that never finished, which is more
/// than it gives most streams.
/// </para>
/// <para>
/// <strong>The order is E5's, not an inference</strong>, and this package does not enforce it -
/// see <see cref="MaterialHandoff"/> for why a sequence nothing in this repository can check is
/// documented rather than policed.
/// </para>
/// </remarks>
public enum MaterialHandoffStep
{
    /// <summary>S4F1 - the sender offers material. Answered with <see cref="MaterialSendAcknowledgement"/>.</summary>
    ReadyToSend = 1,

    /// <summary>S4F3 - the sender is sending it now. No reply.</summary>
    SendMaterial = 3,

    /// <summary>S4F5 - the transfer finished. No reply.</summary>
    HandshakeComplete = 5,

    /// <summary>S4F7 - the sender has withdrawn the offer. No reply.</summary>
    NotReadyToSend = 7,

    /// <summary>S4F9 - the material is stuck at the sending end. No reply.</summary>
    StuckInSender = 9,

    /// <summary>S4F11 - the material is stuck at the receiving end. No reply.</summary>
    StuckInReceiver = 11,

    /// <summary>S4F13 - the transfer did not complete in time. No reply.</summary>
    SendIncompleteTimeout = 13,

    /// <summary>S4F15 - the receiver has it. No reply.</summary>
    MaterialReceived = 15,

    /// <summary>S4F17 - the receiver asks to be sent material. Answered with <see cref="MaterialReceiveAcknowledgement"/>.</summary>
    RequestToReceive = 17,
}

/// <summary>One step of an interlocked handoff, as it arrived.</summary>
/// <remarks>
/// The body of all nine is <c>L,2 { PTN, MID }</c>, so the arguments are the same whichever step
/// it is. <see cref="Step"/> is the function number it came in on, which is why the enum's values
/// are those function numbers.
/// </remarks>
public sealed class MaterialHandoffEventArgs : EventArgs
{
    /// <summary>Which step of the handshake this is.</summary>
    public required MaterialHandoffStep Step { get; init; }

    /// <summary>Which port it is about - <c>PTN</c>.</summary>
    public required byte PortId { get; init; }

    /// <summary>Which piece of material - <c>MID</c>. Empty when the sender named none.</summary>
    public string MaterialId { get; init; } = string.Empty;

    /// <summary>Returns the step, the port and the material.</summary>
    /// <returns>For example <c>ReadyToSend port 1 (LOT-7)</c>.</returns>
    public override string ToString()
        => MaterialId.Length == 0 ? $"{Step} port {PortId}" : $"{Step} port {PortId} ({MaterialId})";
}
