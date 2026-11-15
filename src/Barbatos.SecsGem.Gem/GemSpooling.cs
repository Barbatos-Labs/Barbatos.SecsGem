// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers;

namespace Barbatos.SecsGem.Gem;

/// <summary>The GEM spooling state, as SEMI E30 draws it.</summary>
/// <remarks>
/// E30 nests these as INACTIVE and ACTIVE with SPOOL NOT FULL and SPOOL FULL beneath it. Flattened
/// here for the same reason the other two state models are: a flat state is one a caller can switch
/// on.
/// </remarks>
public enum GemSpoolState
{
    /// <summary>Nothing is held. Either spooling is switched off, or there is nothing to hold.</summary>
    Inactive,

    /// <summary>Holding messages, with room for more.</summary>
    Active,

    /// <summary>Holding messages and out of room. What happens next is the overflow policy.</summary>
    Full,
}

/// <summary>What to do when the spool is full and another message needs holding.</summary>
public enum GemSpoolOverflowPolicy
{
    /// <summary>
    /// Refuse the new message and keep what is already held.
    /// </summary>
    /// <remarks>
    /// The default, and the right one for event reports. A host replaying a spool wants the
    /// <em>beginning</em> of what it missed: the first events after the link dropped are the ones
    /// that explain the rest, and a gap in the middle of a sequence is harder to reason about than
    /// a sequence that stops.
    /// </remarks>
    DiscardNewest,

    /// <summary>
    /// Drop the oldest held message to make room for the new one.
    /// </summary>
    /// <remarks>
    /// For equipment where the most recent state matters more than the history - a trace of a
    /// value, rather than a sequence of events.
    /// </remarks>
    DiscardOldest,
}

/// <summary>S6F23 RSDC - what the host wants done with the spooled data.</summary>
public enum GemSpooledDataRequest
{
    /// <summary>Send it.</summary>
    Transmit = 0,

    /// <summary>Throw it away.</summary>
    Purge = 1,
}

/// <summary>S6F24 RSDA - the answer to S6F23.</summary>
public enum GemSpooledDataAcknowledgement : byte
{
    /// <summary>Accepted.</summary>
    Accepted = 0,

    /// <summary>Denied - busy, or there is nothing spooled to act on.</summary>
    Denied = 1,
}

/// <summary>
/// One message held for a host that was not there.
/// </summary>
/// <remarks>
/// <para>
/// The body is <strong>encoded bytes, not a live item tree</strong>, and that is the whole point of
/// this type existing. A <see cref="SecsMessage"/> above the pooled threshold holds rented memory;
/// a thousand of them waiting for a host that returns in an hour would pin a thousand buffers out
/// of <c>ArrayPool</c> for that hour. Encoding at spool time hands the buffers straight back.
/// </para>
/// <para>
/// It is also what a durable spool needs anyway: bytes can be written to a file, and an item tree
/// cannot.
/// </para>
/// </remarks>
public sealed class GemSpooledMessage
{
    /// <summary>Initialises a spooled message from its parts.</summary>
    /// <param name="stream">S.</param>
    /// <param name="function">F.</param>
    /// <param name="replyExpected">The W bit.</param>
    /// <param name="body">The encoded item tree, or empty for a message with no body.</param>
    /// <param name="spooledAt">When it was held.</param>
    /// <param name="name">The message name, for logs and text formats.</param>
    public GemSpooledMessage(
        byte stream,
        byte function,
        bool replyExpected,
        ReadOnlyMemory<byte> body,
        DateTimeOffset spooledAt,
        string? name = null)
    {
        Stream = stream;
        Function = function;
        ReplyExpected = replyExpected;
        Body = body;
        SpooledAt = spooledAt;
        Name = name;
    }

    /// <summary>Gets S.</summary>
    public byte Stream { get; }

    /// <summary>Gets F.</summary>
    public byte Function { get; }

    /// <summary>Gets the W bit the message was built with.</summary>
    public bool ReplyExpected { get; }

    /// <summary>Gets the encoded item tree. Empty when the message had no body.</summary>
    public ReadOnlyMemory<byte> Body { get; }

    /// <summary>Gets when the message was held.</summary>
    public DateTimeOffset SpooledAt { get; }

    /// <summary>Gets the message name, when it had one.</summary>
    public string? Name { get; }

    /// <summary>Encodes a message for spooling.</summary>
    /// <param name="message">The message. Not consumed; the caller still owns it.</param>
    /// <param name="spooledAt">When it was held.</param>
    /// <returns>The spooled form.</returns>
    public static GemSpooledMessage From(SecsMessage message, DateTimeOffset spooledAt)
    {
        ArgumentNullException.ThrowIfNull(message);

        ReadOnlyMemory<byte> body = ReadOnlyMemory<byte>.Empty;

        if (message.Item is { } item)
        {
            var writer = new ArrayBufferWriter<byte>();
            item.EncodeTo(writer);
            body = writer.WrittenSpan.ToArray();
        }

        return new GemSpooledMessage(
            message.S,
            message.F,
            message.ReplyExpected,
            body,
            spooledAt,
            message.Name);
    }

    /// <summary>Decodes this back into a message.</summary>
    /// <returns>The message. <strong>The caller owns it</strong> and must dispose it.</returns>
    public SecsMessage ToMessage() => new(Stream, Function, ReplyExpected)
    {
        Name = Name,
        Item = Body.IsEmpty ? null : SecsItem.Decode(Body),
    };

    /// <summary>Returns the designator and when it was held.</summary>
    /// <returns>For example <c>S6F11 W spooled 2026-11-15T09:30:15Z</c>.</returns>
    public override string ToString()
        => $"S{Stream}F{Function}{(ReplyExpected ? " W" : string.Empty)} spooled {SpooledAt:O}";
}

/// <summary>
/// Where messages wait for a host that is not there.
/// </summary>
/// <remarks>
/// <para>
/// Taking from the spool is deliberately two steps - <see cref="TryPeekAsync"/> then
/// <see cref="RemoveHeadAsync"/> - rather than one dequeue. A transmit that fails half way must
/// leave the rest spooled <em>in order</em>, and a dequeue that has to put something back puts it
/// at the wrong end. Peek, send, and only then commit.
/// </para>
/// <para>
/// Every method may be called from the equipment's loop, so a slow one delays the link coming back
/// to full service.
/// </para>
/// </remarks>
public interface IGemSpool
{
    /// <summary>How many messages are held.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The count.</returns>
    ValueTask<int> CountAsync(CancellationToken cancellationToken);

    /// <summary>Holds a message.</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="false"/> when the spool was full and the policy refused it.</returns>
    ValueTask<bool> TryAppendAsync(GemSpooledMessage message, CancellationToken cancellationToken);

    /// <summary>Looks at the oldest held message without removing it.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The message, or <see langword="null"/> when nothing is held.</returns>
    ValueTask<GemSpooledMessage?> TryPeekAsync(CancellationToken cancellationToken);

    /// <summary>Removes the oldest held message, having dealt with it.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="true"/> when one was removed.</returns>
    ValueTask<bool> RemoveHeadAsync(CancellationToken cancellationToken);

    /// <summary>Throws everything away.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the spool is empty.</returns>
    ValueTask PurgeAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A spool that keeps everything in memory.
/// </summary>
/// <remarks>
/// Bounded, and it never grows past its capacity - which is the only honest way to hold data for a
/// host that may never come back. Equipment that must survive its own restart wants an
/// <see cref="IGemSpool"/> over a file or a database; E30 does not require durability, and this one
/// does not pretend to it.
/// </remarks>
public sealed class GemInMemorySpool : IGemSpool
{
    private readonly Queue<GemSpooledMessage> _held = new();
    private readonly object _gate = new();

    /// <summary>Initialises the spool.</summary>
    /// <param name="capacity">How many messages it will hold. Must be positive.</param>
    /// <param name="overflow">What to do when it is full.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public GemInMemorySpool(int capacity = 1_000, GemSpoolOverflowPolicy overflow = GemSpoolOverflowPolicy.DiscardNewest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
        Overflow = overflow;
    }

    /// <summary>Gets how many messages this spool will hold.</summary>
    public int Capacity { get; }

    /// <summary>Gets what happens when it is full.</summary>
    public GemSpoolOverflowPolicy Overflow { get; }

    /// <summary>Gets how many messages have been dropped because it was full.</summary>
    public int Discarded { get; private set; }

    /// <inheritdoc/>
    public ValueTask<int> CountAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_held.Count);
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryAppendAsync(GemSpooledMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_gate)
        {
            if (_held.Count >= Capacity)
            {
                if (Overflow == GemSpoolOverflowPolicy.DiscardNewest)
                {
                    Discarded++;
                    return ValueTask.FromResult(false);
                }

                _held.Dequeue();
                Discarded++;
            }

            _held.Enqueue(message);
            return ValueTask.FromResult(true);
        }
    }

    /// <inheritdoc/>
    public ValueTask<GemSpooledMessage?> TryPeekAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_held.TryPeek(out var message) ? message : null);
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> RemoveHeadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_held.TryDequeue(out _));
        }
    }

    /// <inheritdoc/>
    public ValueTask PurgeAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _held.Clear();
        }

        return ValueTask.CompletedTask;
    }
}
