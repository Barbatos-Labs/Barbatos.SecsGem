// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Builds the Stream 9 error messages - SEMI E5.
/// </summary>
/// <remarks>
/// <para>
/// Stream 9 is how one entity tells the other that something it sent could not be handled, and
/// almost every one of them carries the same thing: MHEAD, the ten header bytes of the offending
/// message. That is what lets the peer match the complaint to the message it sent rather than
/// guessing from a timestamp.
/// </para>
/// <para>
/// Every S9 message is sent with W=0. Asking for a reply to an error report invites a loop, and a
/// peer that answered an S9F5 with another S9F5 would never stop.
/// </para>
/// <para>
/// <see cref="HsmsConnection"/> sends these on its own; the factories are public because a GEM
/// layer, a test and a capture tool all have reason to build one by hand.
/// </para>
/// </remarks>
public static class S9Messages
{
    /// <summary>S9F1 Unrecognized Device ID - the message was addressed to someone else.</summary>
    /// <param name="offending">The header of the message that arrived.</param>
    /// <returns>The message. Dispose it.</returns>
    /// <remarks>
    /// Sent when a data message's device id is not this entity's. Note that it is answered at all:
    /// silently dropping the message leaves the sender waiting out T3 with no idea it had the wrong
    /// address.
    /// </remarks>
    public static SecsMessage UnrecognizedDeviceId(in MessageHeader offending) => Report(1, offending, "UnrecognizedDeviceId");

    /// <summary>S9F3 Unrecognized Stream Type - this entity implements nothing in that stream.</summary>
    /// <param name="offending">The header of the message that arrived.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage UnrecognizedStream(in MessageHeader offending) => Report(3, offending, "UnrecognizedStream");

    /// <summary>S9F5 Unrecognized Function Type - the stream is implemented, that function is not.</summary>
    /// <param name="offending">The header of the message that arrived.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage UnrecognizedFunction(in MessageHeader offending) => Report(5, offending, "UnrecognizedFunction");

    /// <summary>S9F7 Illegal Data - the header was fine and the body did not decode.</summary>
    /// <param name="offending">The header of the message that arrived.</param>
    /// <returns>The message. Dispose it.</returns>
    public static SecsMessage IllegalData(in MessageHeader offending) => Report(7, offending, "IllegalData");

    /// <summary>S9F9 Transaction Timer Timeout - a primary this entity sent got no reply within T3.</summary>
    /// <param name="sent">The header of the primary that went unanswered - SHEAD, not MHEAD.</param>
    /// <returns>The message. Dispose it.</returns>
    /// <remarks>
    /// E5 says the entity whose transaction timer expired reports it. secs4net never sends this,
    /// which leaves a peer that was merely slow with no idea its reply was abandoned - and leaves
    /// an engineer reading two logs that disagree about what happened.
    /// </remarks>
    public static SecsMessage TransactionTimerTimeout(in MessageHeader sent) => Report(9, sent, "TransactionTimerTimeout");

    /// <summary>S9F11 Data Too Long - the message was longer than this entity will accept.</summary>
    /// <param name="offending">The header of the message that arrived.</param>
    /// <returns>The message. Dispose it.</returns>
    /// <remarks>
    /// The header is reported as ten zero bytes when the link closed before it could be read. A
    /// too-long frame is rejected from its length prefix, before its body is trusted, so the
    /// header is read only to fill in this report.
    /// </remarks>
    public static SecsMessage DataTooLong(in MessageHeader offending) => Report(11, offending, "DataTooLong");

    /// <summary>S9F13 Conversation Timeout - a multi-block or multi-message conversation stalled.</summary>
    /// <param name="expectedMessage">MEXP: the message that failed to arrive, for example <c>S6F12</c>.</param>
    /// <param name="errorDescription">EDID: a short description of what was being attempted.</param>
    /// <returns>The message. Dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expectedMessage"/> or <paramref name="errorDescription"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The one S9 message whose body is not MHEAD: a conversation has no single header to point
    /// at, so E5 asks for the name of what was expected instead.
    /// </remarks>
    public static SecsMessage ConversationTimeout(string expectedMessage, string errorDescription)
    {
        ArgumentNullException.ThrowIfNull(expectedMessage);
        ArgumentNullException.ThrowIfNull(errorDescription);

        return new SecsMessage(9, 13, replyExpected: false)
        {
            Name = "ConversationTimeout",
            Item = SecsItem.L(
                SecsItem.A(expectedMessage).Named("MEXP"),
                SecsItem.A(errorDescription).Named("EDID")),
        };
    }

    /// <summary>Gets a value indicating whether a message is a Stream 9 error report.</summary>
    /// <param name="message">The message.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Worth checking before reporting an error about an error. An entity that answers S9F7 with
    /// another S9F7 has built a loop that saturates the link.
    /// </remarks>
    public static bool IsErrorReport(SecsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.S == 9;
    }

    private static SecsMessage Report(byte function, in MessageHeader header, string name)
    {
        var bytes = new byte[MessageHeader.Length];
        header.WriteTo(bytes);

        return new SecsMessage(9, function, replyExpected: false)
        {
            Name = name,
            Item = SecsItem.B(bytes).Named(function == 9 ? "SHEAD" : "MHEAD"),
        };
    }
}
