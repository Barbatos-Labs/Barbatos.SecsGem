// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// One complete HSMS frame: its 10-byte header, and the SECS-II body when it had one.
/// </summary>
/// <remarks>
/// <para>
/// This is the unit <see cref="HsmsPipeDecoder"/> produces and <see cref="HsmsPipeEncoder"/>
/// consumes, and it is deliberately <em>not</em> a <see cref="SecsMessage"/>. A
/// <see cref="SecsMessage"/> is S, F and an item tree - what SEMI E5 defines. The session id,
/// the system bytes and the SType belong to the connection carrying it, which is E37's business.
/// Keeping the two apart is what lets one composed message be sent twice, on two connections,
/// with two different transaction ids and no copying.
/// </para>
/// <para>
/// <see cref="Data"/> is <see langword="null"/> for every frame whose body this layer did not
/// decode, and there are three such cases:
/// </para>
/// <list type="bullet">
///   <item><description>
///     A control message (Select, Deselect, Linktest, Reject, Separate). It has no body at all.
///   </description></item>
///   <item><description>
///     A data message whose <see cref="MessageHeader.PType"/> is not zero: the body is in a
///     presentation this stack does not speak. The connection answers it with
///     <see cref="RejectReason.PtypeNotSupported"/> rather than guessing.
///   </description></item>
///   <item><description>
///     A control message that arrived carrying a body, which E37 does not allow. The extra bytes
///     are consumed and dropped; the header is still surfaced so the connection can reject it.
///   </description></item>
/// </list>
/// <para>
/// <see cref="MessageHeader.IsDataMessage"/> separates the first case from the other two.
/// </para>
/// <para>
/// <strong>Ownership.</strong> An instance owns its <see cref="Data"/>: disposing the frame
/// disposes the body and returns its pooled buffers. That is what the decoder needs, because the
/// body it built belongs to nobody else. A caller that wants to send a message it intends to keep
/// - a retry, or the same report to two hosts - should use
/// <see cref="HsmsPipeEncoder.WriteAsync(System.IO.Pipelines.PipeWriter, MessageHeader, SecsMessage?, CancellationToken)"/>
/// instead of wrapping it in a frame.
/// </para>
/// </remarks>
public sealed class HsmsMessage : IDisposable
{
    private SecsMessage? _data;
    private bool _disposed;

    /// <summary>Initialises a new frame.</summary>
    /// <param name="header">The 10-byte header.</param>
    /// <param name="data">The decoded body, or <see langword="null"/> when the frame had none. Ownership passes to this instance.</param>
    public HsmsMessage(MessageHeader header, SecsMessage? data = null)
    {
        Header = header;
        _data = data;
    }

    /// <summary>Gets the message header.</summary>
    public MessageHeader Header { get; }

    /// <summary>Gets the decoded SECS-II body, or <see langword="null"/> when this frame has none.</summary>
    /// <remarks>
    /// See the type's remarks for the three cases in which this is <see langword="null"/> from the
    /// start, and <see cref="DetachData"/> for the one that makes it <see langword="null"/> later.
    /// </remarks>
    public SecsMessage? Data => _data;

    /// <summary>Gets a value indicating whether this is an HSMS control message rather than a data message.</summary>
    public bool IsControl => Header.MessageType != MessageType.DataMessage;

    /// <summary>Gets the transaction id - header bytes 6-9.</summary>
    public int SystemBytes => Header.SystemBytes;

    /// <summary>Gets the session id - header bytes 0-1.</summary>
    public ushort SessionId => Header.SessionId;

    /// <summary>Wraps a data message in a frame, taking ownership of it.</summary>
    /// <param name="data">The message. This frame owns it from here and disposes it.</param>
    /// <param name="sessionId">The session id to stamp into the header.</param>
    /// <param name="systemBytes">The transaction id to stamp into the header.</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    public static HsmsMessage FromData(SecsMessage data, ushort sessionId, int systemBytes)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new HsmsMessage(
            new MessageHeader
            {
                SessionId = sessionId,
                Stream = data.S,
                Function = data.F,
                ReplyExpected = data.ReplyExpected,
                PType = 0,
                MessageType = MessageType.DataMessage,
                SystemBytes = systemBytes,
            },
            data);
    }

    /// <summary>Builds a control frame, which has no body.</summary>
    /// <param name="type">The control message type. <see cref="MessageType.DataMessage"/> is not one.</param>
    /// <param name="systemBytes">The transaction id. A response must echo the request's.</param>
    /// <param name="sessionId">The session id. E37 uses <c>0xFFFF</c> for control messages, which is the default.</param>
    /// <param name="status">Header byte 3 - the select status, deselect status or reject reason. Zero for a request.</param>
    /// <returns>The frame.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="type"/> is <see cref="MessageType.DataMessage"/>.</exception>
    public static HsmsMessage Control(MessageType type, int systemBytes, ushort sessionId = 0xFFFF, byte status = 0)
    {
        if (type == MessageType.DataMessage)
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "A data message is not a control message.");
        }

        return new HsmsMessage(new MessageHeader
        {
            SessionId = sessionId,
            Function = status,
            PType = 0,
            MessageType = type,
            SystemBytes = systemBytes,
        });
    }

    /// <summary>Takes the body out of the frame, so that disposing the frame no longer disposes it.</summary>
    /// <returns>The body, or <see langword="null"/> when there was none left to take.</returns>
    /// <remarks>
    /// How ownership moves from the frame to whatever will outlive it - a transaction handed to a
    /// handler, or a secondary handed to the caller blocked in <c>SendAsync</c>. Making that step
    /// explicit is the point: a frame and its body having two owners, or none, are the two ways a
    /// pooled buffer goes wrong, and both of them are silent.
    /// </remarks>
    public SecsMessage? DetachData() => Interlocked.Exchange(ref _data, null);

    /// <summary>Disposes the body, returning any pooled buffers it holds.</summary>
    /// <remarks>
    /// Idempotent: a second call does nothing rather than throwing. Safe after
    /// <see cref="DetachData"/>, where there is nothing left to dispose.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Exchange(ref _data, null)?.Dispose();
    }

    /// <summary>Returns a diagnostic rendering of the frame.</summary>
    /// <returns>The header's rendering, with the body's item count when there is a body.</returns>
    public override string ToString()
        => Data?.Item is { } item
            ? $"{Header} {item}"
            : Header.ToString();
}
