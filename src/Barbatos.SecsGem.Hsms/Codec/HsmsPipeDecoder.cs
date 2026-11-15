// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using CommunityToolkit.HighPerformance.Buffers;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Turns the byte stream of an HSMS link into a sequence of <see cref="HsmsMessage"/> frames,
/// enforcing T8 while it does.
/// </summary>
/// <remarks>
/// <para><strong>Two paths, and why.</strong></para>
/// <para>
/// An HSMS frame is a 4-byte big-endian length, a 10-byte header, then the item tree. Because the
/// length arrives before the body, the decoder can tell at a glance whether a frame is already
/// here in full:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <strong>Fast path</strong> - the whole frame is in the reader's buffer. The item tree is
///     built straight out of the <see cref="ReadOnlySequence{T}"/> with no staging copy, and T8
///     is never armed, because a frame that arrived complete had no gap in it to measure. On a
///     healthy link carrying ordinary GEM traffic this is very nearly every message.
///   </description></item>
///   <item><description>
///     <strong>Slow path</strong> - the frame spans reads. The bytes are gathered into one pooled
///     staging buffer, <em>consuming from the reader as they arrive</em>, with T8 running for the
///     whole gather and restarting on every byte that turns up.
///   </description></item>
/// </list>
/// <para>
/// The slow path copies on purpose. Leaving a partial frame unconsumed in the pipe would be one
/// copy cheaper and would deadlock: a <see cref="Pipe"/> pauses its writer once unconsumed data
/// passes its pause threshold - 64 KiB by default - so a decoder waiting for the rest of a larger
/// message would be waiting on a writer that is waiting on the decoder. Consuming as we go keeps
/// back pressure working and makes the decoder correct against any <see cref="IDuplexPipe"/>, not
/// only one whose thresholds happen to exceed
/// <see cref="SecsGemOptions.MaxMessageLength"/>.
/// </para>
/// <para><strong>T8 is an inter-character timer, which is not the same as a message timer.</strong></para>
/// <para>SEMI E37 scopes T8 to the gap between bytes of <em>one</em> message. So:</para>
/// <list type="bullet">
///   <item><description>
///     An idle link never trips it. No byte of a frame has arrived, so there is no gap to
///     measure, and a connection may sit quiet indefinitely - that is what Linktest is for.
///   </description></item>
///   <item><description>
///     It restarts on every byte. A peer dribbling one byte every <c>T8/2</c> is slow, not
///     broken, and is not disconnected.
///   </description></item>
///   <item><description>
///     It fires when a frame has begun and then stalls - mid length prefix, mid header or mid
///     body. That is a peer that died with a message in flight, which TCP alone will not report
///     for minutes.
///   </description></item>
/// </list>
/// <para>
/// This is the behaviour secs4net documents but does not have: its <c>StartT8Timer</c> and
/// <c>StopT8Timer</c> have no callers at all, the one call site being commented out inside its
/// decoder. <see cref="FastPathFrames"/>, <see cref="SlowPathFrames"/> and
/// <see cref="IsReceivingFrame"/> exist here so the claim is observable rather than asserted.
/// </para>
/// <para>
/// <strong>Failure and synchronisation.</strong> A frame whose body does not decode is still
/// consumed, so the link stays synchronised and the connection can answer S9F7 and carry on. The
/// one exception is <see cref="HsmsMessageTooLongException"/>, which by its nature leaves the
/// stream unsynchronised; see that type for why.
/// </para>
/// <para>
/// <strong>Threading.</strong> One decoder drives one <see cref="PipeReader"/> from one loop, and
/// <see cref="DecodeAsync"/> is not reentrant. The diagnostic counters are safe to read from
/// another thread.
/// </para>
/// </remarks>
public sealed class HsmsPipeDecoder
{
    /// <summary>The 4-byte big-endian length that precedes every HSMS frame.</summary>
    public const int LengthPrefixSize = 4;

    private readonly SecsCodecOptions _codec;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _t8;
    private readonly int _maxMessageLength;

    private long _fastPathFrames;
    private long _slowPathFrames;
    private int _pendingFrameBytes;

    /// <summary>Initialises a new decoder.</summary>
    /// <param name="options">
    /// The connection's settings. T8, <see cref="SecsGemOptions.MaxMessageLength"/> and the codec
    /// options are read from it.
    /// </param>
    /// <param name="timeProvider">
    /// Drives T8. <see langword="null"/> uses <see cref="TimeProvider.System"/>; a test passes
    /// <c>FakeTimeProvider</c>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public HsmsPipeDecoder(SecsGemOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _codec = options.CreateCodecOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _t8 = options.T8;
        _maxMessageLength = options.MaxMessageLength;
    }

    /// <summary>Gets the number of frames that arrived whole and took the zero-copy path.</summary>
    /// <remarks>
    /// Diagnostic. A link whose traffic sits mostly on the slow path is a link with small TCP
    /// segments or a congested peer, and is worth a look.
    /// </remarks>
    public long FastPathFrames => Interlocked.Read(ref _fastPathFrames);

    /// <summary>Gets the number of frames that spanned reads and were gathered into a staging buffer.</summary>
    /// <remarks>Diagnostic. See <see cref="FastPathFrames"/>.</remarks>
    public long SlowPathFrames => Interlocked.Read(ref _slowPathFrames);

    /// <summary>Gets how many bytes of an incomplete frame the decoder is holding.</summary>
    /// <remarks>
    /// Zero whenever the decoder sits at a frame boundary, which is also exactly when T8 is not
    /// armed. Non-zero means a message is part way in.
    /// </remarks>
    public int PendingFrameBytes => Volatile.Read(ref _pendingFrameBytes);

    /// <summary>Gets a value indicating whether a message is part way received, and so whether T8 is running.</summary>
    /// <remarks>
    /// The honest answer to "is this link idle, or stalled?", and the reason a health check can
    /// tell the two apart.
    /// </remarks>
    public bool IsReceivingFrame => PendingFrameBytes > 0;

    /// <summary>Reads frames until the link closes.</summary>
    /// <param name="reader">The link's input.</param>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>
    /// Each frame in turn. The caller owns every frame it receives and must dispose it; see
    /// <see cref="HsmsMessage"/> for what that releases.
    /// </returns>
    /// <exception cref="SecsTimeoutException">A frame began arriving and then stalled for longer than T8.</exception>
    /// <exception cref="SecsDecodeException">A frame's length or body is malformed, or the link closed mid-frame.</exception>
    /// <exception cref="HsmsMessageTooLongException">A peer announced a message past <see cref="SecsGemOptions.MaxMessageLength"/>.</exception>
    /// <remarks>
    /// The enumeration ends - rather than throwing - when the link closes cleanly at a frame
    /// boundary. A link that closes part way through a frame is a truncation and throws, because
    /// silently dropping half a message is how a host ends up waiting out T3 for a reply that was
    /// already lost.
    /// </remarks>
    public async IAsyncEnumerable<HsmsMessage> DecodeAsync(
        PipeReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (true)
        {
            var frame = await ReadFrameAsync(reader, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                yield break;
            }

            yield return frame;
        }
    }

    /// <summary>Reads one frame, or returns <see langword="null"/> at a clean end of stream.</summary>
    private async ValueTask<HsmsMessage?> ReadFrameAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        // Disarmed on creation: at a frame boundary there is no gap to measure. The first Arm()
        // runs only once at least one byte of this frame is in hand.
        using var t8 = new T8Guard(_t8, _timeProvider, cancellationToken);

        var prefixed = await BufferAtLeastAsync(reader, LengthPrefixSize, t8).ConfigureAwait(false);
        if (prefixed is null)
        {
            return null;
        }

        var buffer = prefixed.Value;
        var length = ReadLengthPrefix(buffer);

        // The length is a number the peer chose. Both bounds are checked before it sizes anything.
        if (length < MessageHeader.Length)
        {
            reader.AdvanceTo(buffer.Start, buffer.End);
            throw new SecsDecodeException(
                $"A frame announces {length} byte(s); an HSMS header alone is {MessageHeader.Length}.");
        }

        if (length > (uint)_maxMessageLength)
        {
            // Fill in MHEAD for the S9F11 that follows, but only from bytes already in hand: see
            // HsmsMessageTooLongException.Header for why this must not wait for more.
            MessageHeader? offending = buffer.Length >= LengthPrefixSize + MessageHeader.Length
                ? MessageHeader.Parse(buffer.Slice(LengthPrefixSize, MessageHeader.Length))
                : null;

            reader.AdvanceTo(buffer.Start, buffer.End);
            throw new HsmsMessageTooLongException(length, _maxMessageLength) { Header = offending };
        }

        var total = LengthPrefixSize + (long)length;

        if (buffer.Length >= total)
        {
            // Fast path. The frame is all here, so it decodes in place and T8 is never armed. The
            // AdvanceTo runs even when decoding throws: the frame is consumed either way, so a
            // malformed body costs one S9F7 rather than a desynchronised link.
            try
            {
                var frame = DecodeFrame(buffer.Slice(LengthPrefixSize, length));
                Interlocked.Increment(ref _fastPathFrames);
                return frame;
            }
            finally
            {
                reader.AdvanceTo(buffer.GetPosition(total));
            }
        }

        // Slow path. Consume the prefix only, then gather the rest under T8.
        reader.AdvanceTo(buffer.GetPosition(LengthPrefixSize));

        // Arm before publishing the counter, never after. PendingFrameBytes is how an observer -
        // a health check, or a test about to advance a FakeTimeProvider - learns that T8 is
        // running, and that has to be true by the time it can see it.
        t8.Arm();
        Volatile.Write(ref _pendingFrameBytes, LengthPrefixSize);

        using var staging = MemoryOwner<byte>.Allocate((int)length);
        try
        {
            await ReadExactAsync(reader, staging.Memory, t8).ConfigureAwait(false);
            var frame = DecodeFrame(new ReadOnlySequence<byte>(staging.Memory));
            Interlocked.Increment(ref _slowPathFrames);
            return frame;
        }
        finally
        {
            Volatile.Write(ref _pendingFrameBytes, 0);
        }
    }

    /// <summary>
    /// Waits until the reader has buffered at least <paramref name="count"/> bytes, consuming
    /// none of them.
    /// </summary>
    /// <returns>
    /// The buffered sequence, which the caller must settle with exactly one
    /// <see cref="PipeReader.AdvanceTo(SequencePosition)"/>; or <see langword="null"/> when the
    /// link closed cleanly with nothing buffered.
    /// </returns>
    private async ValueTask<ReadOnlySequence<byte>?> BufferAtLeastAsync(PipeReader reader, int count, T8Guard t8)
    {
        while (true)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(t8.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (t8.Fired)
            {
                throw new SecsTimeoutException(SecsTimer.T8, _t8);
            }

            var buffer = result.Buffer;
            var buffered = buffer.Length;

            if (buffered >= count)
            {
                // Deliberately not advanced: the caller decides how much of the frame to take,
                // and the sequence goes invalid the moment AdvanceTo runs.
                return buffer;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                Volatile.Write(ref _pendingFrameBytes, 0);
                return buffered == 0
                    ? null
                    : throw new SecsDecodeException(
                        $"The link closed with {buffered} stray byte(s), fewer than the {count} a frame needs to announce its length.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);

            if (buffered > 0)
            {
                // A frame has begun and is incomplete: from here a stall is a T8, and every byte
                // that turns up restarts it. Armed before the counter is published - see the
                // slow-path entry in ReadFrameAsync for why that order matters.
                t8.Arm();
                Volatile.Write(ref _pendingFrameBytes, (int)buffered);
            }
        }
    }

    /// <summary>Gathers exactly as many bytes as <paramref name="destination"/> holds, consuming as they arrive.</summary>
    private async ValueTask ReadExactAsync(PipeReader reader, Memory<byte> destination, T8Guard t8)
    {
        var offset = 0;

        while (offset < destination.Length)
        {
            ReadResult result;
            try
            {
                result = await reader.ReadAsync(t8.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (t8.Fired)
            {
                throw new SecsTimeoutException(SecsTimer.T8, _t8);
            }

            var buffer = result.Buffer;
            var take = (int)Math.Min(buffer.Length, destination.Length - offset);

            if (take > 0)
            {
                Copy(buffer, take, destination, offset);
                offset += take;
                reader.AdvanceTo(buffer.GetPosition(take));
                t8.Arm();
                Volatile.Write(ref _pendingFrameBytes, LengthPrefixSize + offset);
                continue;
            }

            if (result.IsCompleted)
            {
                reader.AdvanceTo(buffer.End);
                throw new SecsDecodeException(
                    $"The link closed {destination.Length - offset} byte(s) short of a complete message.");
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }

        // Local and non-async, so the span never has to survive an await.
        static void Copy(in ReadOnlySequence<byte> source, int take, Memory<byte> destination, int offset)
            => source.Slice(0, take).CopyTo(destination.Span[offset..]);
    }

    /// <summary>Turns one complete frame - header and body, without the length prefix - into a message.</summary>
    /// <remarks>
    /// Nothing in the result points back into <paramref name="frame"/>: the value decoders always
    /// copy into their own storage, which is what lets the fast path decode straight out of the
    /// reader's buffer and then release it.
    /// </remarks>
    private HsmsMessage DecodeFrame(in ReadOnlySequence<byte> frame)
    {
        var header = MessageHeader.Parse(frame);

        if (!header.IsDataMessage)
        {
            // A control message, or a data message in a presentation this stack does not speak.
            // Either way the body is not ours to read, and the header is what the connection needs.
            return new HsmsMessage(header);
        }

        var body = frame.Slice(MessageHeader.Length);
        if (body.IsEmpty)
        {
            return new HsmsMessage(header, new SecsMessage(header.Stream, header.Function, header.ReplyExpected));
        }

        var remaining = body;
        SecsItem item;
        try
        {
            item = SecsItem.Decode(ref remaining, _codec);
        }
        catch (SecsDecodeException ex)
        {
            // Re-thrown carrying the header, which is what turns "something in a body was wrong"
            // into an S9F7 the peer can match to the message it sent. The original text, byte
            // offset included, is kept verbatim.
            throw new SecsDecodeException($"{header} has an undecodable body. {ex.Message}", header);
        }

        if (!remaining.IsEmpty)
        {
            var trailing = remaining.Length;
            item.Dispose();
            throw new SecsDecodeException(
                $"{header} carries {trailing} byte(s) after its root item; a SECS-II body holds exactly one.",
                header);
        }

        return new HsmsMessage(
            header,
            new SecsMessage(header.Stream, header.Function, header.ReplyExpected) { Item = item });
    }

    private static uint ReadLengthPrefix(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.FirstSpan.Length >= LengthPrefixSize)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(buffer.FirstSpan);
        }

        Span<byte> window = stackalloc byte[LengthPrefixSize];
        buffer.Slice(0, LengthPrefixSize).CopyTo(window);
        return BinaryPrimitives.ReadUInt32BigEndian(window);
    }

    /// <summary>
    /// T8, as a cancellation token that is live only while a frame is part way in.
    /// </summary>
    /// <remarks>
    /// Expressing the timer as a token rather than a callback is what keeps it honest: it can
    /// only fire while the decoder is actually waiting for the rest of a frame, because that is
    /// the only place the token is ever passed. There is no way to leave it armed across a frame
    /// boundary, and leaving it armed is the mistake that turns T8 into an idle-link timer.
    /// </remarks>
    private sealed class T8Guard : IDisposable
    {
        private readonly TimeSpan _interval;
        private readonly TimeProvider _timeProvider;
        private readonly CancellationToken _outer;
        private CancellationTokenSource? _t8;
        private CancellationTokenSource? _linked;

        public T8Guard(TimeSpan interval, TimeProvider timeProvider, CancellationToken outer)
        {
            _interval = interval;
            _timeProvider = timeProvider;
            _outer = outer;
        }

        /// <summary>Gets the token to wait on: the caller's alone until <see cref="Arm"/> runs.</summary>
        public CancellationToken Token => _linked?.Token ?? _outer;

        /// <summary>Gets a value indicating whether T8 - rather than the caller - caused a cancellation.</summary>
        public bool Fired => _t8 is { IsCancellationRequested: true };

        /// <summary>Starts T8, or restarts it when it is already running.</summary>
        /// <remarks>A non-positive interval disables the timer, which is how T8 is turned off.</remarks>
        public void Arm()
        {
            if (_interval <= TimeSpan.Zero)
            {
                return;
            }

            if (_t8 is null)
            {
                _t8 = new CancellationTokenSource(_interval, _timeProvider);
                _linked = CancellationTokenSource.CreateLinkedTokenSource(_t8.Token, _outer);
            }
            else
            {
                _t8.CancelAfter(_interval);
            }
        }

        public void Dispose()
        {
            _linked?.Dispose();
            _t8?.Dispose();
        }
    }
}
