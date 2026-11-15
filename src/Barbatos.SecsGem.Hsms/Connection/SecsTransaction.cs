// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// A primary message that arrived, together with the one thing needed to answer it.
/// </summary>
/// <remarks>
/// <para>
/// The system bytes pair a secondary with its primary, and getting them wrong is one of the
/// classic SECS bugs: the peer waits out T3 while a correctly-formed reply sits on the wire under
/// the wrong transaction id. So a handler is never given the number to copy - it is given this,
/// and <see cref="ReplyAsync"/> stamps the right header itself.
/// </para>
/// <para>
/// <strong>Ownership.</strong> This owns <see cref="Message"/> and disposes it. The dispatch loop
/// disposes the transaction once the handler returns, so a handler that wants to keep the message
/// past that point must copy what it needs out of it.
/// </para>
/// </remarks>
public sealed class SecsTransaction : IDisposable
{
    private readonly Func<SecsMessage, MessageHeader, CancellationToken, ValueTask>? _reply;
    private readonly MessageHeader _header;
    private int _replied;
    private bool _disposed;

    /// <summary>Initialises a new transaction.</summary>
    /// <param name="message">The primary that arrived. This instance owns it.</param>
    /// <param name="header">The header it arrived under, which the reply has to echo.</param>
    /// <param name="reply">How to put a secondary on the wire, or <see langword="null"/> when there is no link to reply on.</param>
    internal SecsTransaction(
        SecsMessage message,
        MessageHeader header,
        Func<SecsMessage, MessageHeader, CancellationToken, ValueTask>? reply)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        _header = header;
        _reply = reply;
    }

    /// <summary>Gets the primary message.</summary>
    public SecsMessage Message { get; }

    /// <summary>Gets the transaction id the reply must echo.</summary>
    public int SystemBytes => _header.SystemBytes;

    /// <summary>Gets the session id the message arrived under.</summary>
    public ushort SessionId => _header.SessionId;

    /// <summary>Gets a value indicating whether the peer is waiting for a reply.</summary>
    /// <remarks>
    /// When this is <see langword="false"/> the message is a notification - an S6F11 sent with
    /// W=0, say - and <see cref="ReplyAsync"/> will refuse. Handle it and return.
    /// </remarks>
    public bool ReplyExpected => _header.ReplyExpected;

    /// <summary>Gets a value indicating whether a reply has already gone out.</summary>
    public bool IsReplied => Volatile.Read(ref _replied) != 0;

    /// <summary>Gets the stream of the primary.</summary>
    public byte S => Message.S;

    /// <summary>Gets the function of the primary.</summary>
    public byte F => Message.F;

    /// <summary>Sends the secondary that answers this primary.</summary>
    /// <param name="secondary">
    /// The reply. Its <see cref="SecsMessage.ReplyExpected"/> is ignored and written as
    /// <see langword="false"/>: a secondary never asks for one.
    /// </param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes once the reply has been flushed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="secondary"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The peer did not ask for a reply, a reply has already been sent, or the link this
    /// transaction arrived on has gone.
    /// </exception>
    /// <remarks>
    /// The caller keeps ownership of <paramref name="secondary"/>, so the same message can answer
    /// two transactions.
    /// </remarks>
    public ValueTask ReplyAsync(SecsMessage secondary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secondary);

        if (!ReplyExpected)
        {
            throw new InvalidOperationException(
                $"{Message} was sent with W=0, so the peer is not waiting for a reply.");
        }

        if (Interlocked.Exchange(ref _replied, 1) != 0)
        {
            throw new InvalidOperationException($"{Message} has already been answered.");
        }

        if (_reply is null)
        {
            throw new InvalidOperationException(
                $"The link {Message} arrived on has closed, so there is nowhere to send a reply.");
        }

        // The secondary echoes the primary's session id and system bytes, and never asks for a
        // reply of its own. The stream and function come from the message the caller built.
        var header = new MessageHeader
        {
            SessionId = _header.SessionId,
            Stream = secondary.S,
            Function = secondary.F,
            ReplyExpected = false,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = _header.SystemBytes,
        };

        return _reply(secondary, header, cancellationToken);
    }

    /// <summary>Disposes the primary message.</summary>
    /// <remarks>Idempotent. The dispatch loop calls this once the handler returns.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Message.Dispose();
    }

    /// <summary>Returns a diagnostic rendering of the transaction.</summary>
    /// <returns><c>S1F3 W [0x0000002A]</c>.</returns>
    public override string ToString() => $"{_header}";
}
