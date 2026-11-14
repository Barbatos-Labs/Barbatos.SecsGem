// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem;

/// <summary>
/// The HSMS message type, carried in byte 5 of the message header (SType) - SEMI E37.
/// </summary>
/// <remarks>
/// Odd values are requests, even values are the matching responses, which is what lets the
/// control-transaction manager pair them without a lookup table.
/// <see cref="SeparateRequest"/> is the exception: it is odd but expects no response.
/// </remarks>
public enum MessageType : byte
{
    /// <summary>A SECS-II data message. The only type that carries an item tree.</summary>
    DataMessage = 0,

    /// <summary>Select.req - asks to move the connection from NOT SELECTED to SELECTED.</summary>
    SelectRequest = 1,

    /// <summary>Select.rsp - carries a <see cref="SelectStatus"/> in header byte 3.</summary>
    SelectResponse = 2,

    /// <summary>Deselect.req - asks to leave the SELECTED state gracefully.</summary>
    DeselectRequest = 3,

    /// <summary>Deselect.rsp - carries a <see cref="DeselectStatus"/> in header byte 3.</summary>
    DeselectResponse = 4,

    /// <summary>Linktest.req - the HSMS keep-alive.</summary>
    LinkTestRequest = 5,

    /// <summary>Linktest.rsp.</summary>
    LinkTestResponse = 6,

    /// <summary>Reject.req - the peer sent something this entity will not accept. Reason in header byte 3.</summary>
    RejectRequest = 7,

    /// <summary>Separate.req - an abrupt, unacknowledged teardown of the connection.</summary>
    SeparateRequest = 9,
}

/// <summary>
/// The status byte of a Select.rsp (header byte 3) - SEMI E37.
/// </summary>
public enum SelectStatus : byte
{
    /// <summary>The selection succeeded; the connection is now SELECTED.</summary>
    Success = 0,

    /// <summary>Communication is already active - this entity is already selected.</summary>
    AlreadyActive = 1,

    /// <summary>The entity is not ready to be selected.</summary>
    NotReady = 2,

    /// <summary>The connection is exhausted: HSMS-SS permits a single selection at a time.</summary>
    ConnectionExhaust = 3,
}

/// <summary>
/// The status byte of a Deselect.rsp (header byte 3) - SEMI E37.
/// </summary>
public enum DeselectStatus : byte
{
    /// <summary>The deselection succeeded.</summary>
    Success = 0,

    /// <summary>The entity is not currently selected, so there is nothing to deselect.</summary>
    NotSelected = 1,

    /// <summary>The entity is busy and cannot deselect right now.</summary>
    Busy = 2,
}

/// <summary>
/// The reason byte of a Reject.req (header byte 3) - SEMI E37.
/// </summary>
/// <remarks>
/// secs4net never sends or interprets Reject.req; Barbatos.SecsGem does, which is how a peer
/// learns it sent an unsupported PType or an out-of-state control message instead of simply
/// being dropped.
/// </remarks>
public enum RejectReason : byte
{
    /// <summary>The SType in the rejected message is not supported.</summary>
    StypeNotSupported = 1,

    /// <summary>The PType in the rejected message is not supported.</summary>
    PtypeNotSupported = 2,

    /// <summary>The message was well-formed but not legal in the current connection state.</summary>
    TransactionNotOpen = 3,

    /// <summary>A data message arrived while the connection was not SELECTED.</summary>
    EntityNotSelected = 4,
}
