// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Buffers.Binary;
using System.IO.Pipelines;
using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// T8, the network inter-character timeout, in all three of the places it can fire and the two
/// places it must not.
/// </summary>
/// <remarks>
/// <para>
/// Every interval here is driven by <c>FakeTimeProvider</c>, so no test waits on a wall clock and
/// a five-second T8 elapses instantly. What the tests have to synchronise on is the decoder's own
/// progress, which is what <see cref="HsmsPipeDecoder.IsReceivingFrame"/> reports: the decoder
/// arms T8 and then publishes that counter, in that order, so a test that waits for the counter
/// knows the timer is running before it moves the clock.
/// </para>
/// <para>
/// Three of these tests encode the distinction that makes T8 worth having. It fires on a frame
/// that began and stopped. It does <em>not</em> fire on a link that is simply quiet, and it does
/// <em>not</em> fire on a peer that is merely slow. secs4net documents all three behaviours and
/// implements none of them - its T8 timer has no callers at all.
/// </para>
/// </remarks>
public sealed class HsmsT8Tests
{
    private static readonly TimeSpan T8 = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan JustPastT8 = T8 + TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan JustUnderT8 = T8 - TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task A_stall_inside_the_length_prefix_trips_T8()
    {
        var fake = new FakeTimeProvider();
        var decoder = new HsmsPipeDecoder(Frames.Options(T8), fake);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(new byte[] { 0x00, 0x00 }, Frames.Token);

        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);
        await Frames.WaitUntilAsync(() => decoder.IsReceivingFrame, "the decoder to hold two prefix bytes");
        Assert.Equal(2, decoder.PendingFrameBytes);

        fake.Advance(JustPastT8);

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => decoding);
        Assert.Equal(SecsTimer.T8, error.Timer);
        Assert.Equal(T8, error.Interval);
    }

    [Fact]
    public async Task A_stall_inside_the_header_trips_T8()
    {
        var fake = new FakeTimeProvider();
        var decoder = new HsmsPipeDecoder(Frames.Options(T8), fake);

        // A complete prefix announcing a header-only message, then six of its ten header bytes.
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(PrefixFor(MessageHeader.Length), Frames.Token);
        await pipe.Writer.WriteAsync(new byte[6], Frames.Token);

        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);
        await Frames.WaitUntilAsync(
            () => decoder.PendingFrameBytes >= HsmsPipeDecoder.LengthPrefixSize + 6,
            "the decoder to hold six header bytes");

        fake.Advance(JustPastT8);

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => decoding);
        Assert.Equal(SecsTimer.T8, error.Timer);
    }

    [Fact]
    public async Task A_stall_inside_the_body_trips_T8()
    {
        var fake = new FakeTimeProvider();
        var options = Frames.Options(T8);
        var decoder = new HsmsPipeDecoder(options, fake);

        using var sent = Frames.Create("s6f11");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes.AsMemory(..^5), Frames.Token);

        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);
        await Frames.WaitUntilAsync(
            () => decoder.PendingFrameBytes >= bytes.Length - 5,
            "the decoder to hold all but the last five bytes");

        fake.Advance(JustPastT8);

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => decoding);
        Assert.Equal(SecsTimer.T8, error.Timer);
    }

    [Fact]
    public async Task An_idle_link_never_trips_T8()
    {
        var fake = new FakeTimeProvider();
        var options = Frames.Options(T8);
        var decoder = new HsmsPipeDecoder(options, fake);

        using var sent = Frames.Create("s1f2");
        var bytes = Frames.Encode(sent, options);

        // Nothing written at all: the decoder is waiting at a frame boundary.
        var pipe = new Pipe();
        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);

        Assert.False(decoder.IsReceivingFrame);
        fake.Advance(T8 * 100);

        // No frame has begun, so there is no gap to measure. A quiet link is normal - keeping it
        // honest is Linktest's job, not T8's.
        Assert.False(decoding.IsCompleted);
        Assert.False(decoder.IsReceivingFrame);

        // And the link is still perfectly usable after all that silence.
        await pipe.Writer.WriteAsync(bytes, Frames.Token);
        using var received = await decoding;
        Frames.AssertSameFrame(sent, received);
    }

    [Fact]
    public async Task T8_restarts_on_every_byte_so_a_merely_slow_peer_survives()
    {
        var fake = new FakeTimeProvider();
        var options = Frames.Options(T8);
        var decoder = new HsmsPipeDecoder(options, fake);
        var started = fake.GetUtcNow();

        using var sent = Frames.Create("s1f2");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);

        // One byte at a time, with all but a millisecond of T8 between each. Every byte restarts
        // the timer, so this peer is slow rather than broken and must not be disconnected.
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            await pipe.Writer.WriteAsync(bytes.AsMemory(i, 1), Frames.Token);
            await Frames.WaitUntilAsync(
                () => decoder.PendingFrameBytes >= i + 1,
                $"the decoder to take byte {i}");

            fake.Advance(JustUnderT8);
            Assert.False(decoding.IsCompleted);
        }

        // Far more than T8 has now passed in total - which is exactly the point. A per-message
        // timer would have fired many times over.
        Assert.True(
            fake.GetUtcNow() - started > T8 * 5,
            "the test should have spent many multiples of T8 dribbling bytes");

        // Now the peer really does stop, one byte short.
        fake.Advance(JustPastT8);

        var error = await Assert.ThrowsAsync<SecsTimeoutException>(() => decoding);
        Assert.Equal(SecsTimer.T8, error.Timer);
    }

    [Fact]
    public async Task A_frame_that_arrives_whole_never_arms_T8()
    {
        var fake = new FakeTimeProvider();
        var options = Frames.Options(T8);
        var decoder = new HsmsPipeDecoder(options, fake);

        using var sent = Frames.Create("s6f11");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes, Frames.Token);

        // The clock jumps well past T8 before a single byte is read. The fast path never arms the
        // timer, so this changes nothing.
        fake.Advance(T8 * 100);

        using var received = await Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);

        Frames.AssertSameFrame(sent, received);
        Assert.Equal(1L, decoder.FastPathFrames);
        Assert.Equal(0L, decoder.SlowPathFrames);
    }

    [Fact]
    public async Task A_non_positive_T8_disables_the_timer()
    {
        var fake = new FakeTimeProvider();
        var options = Frames.Options(TimeSpan.Zero);
        var decoder = new HsmsPipeDecoder(options, fake);

        using var sent = Frames.Create("s1f2");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes.AsMemory(..^1), Frames.Token);

        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, Frames.Token);
        await Frames.WaitUntilAsync(() => decoder.IsReceivingFrame, "the decoder to hold a partial frame");

        fake.Advance(TimeSpan.FromHours(1));
        Assert.False(decoding.IsCompleted);

        await pipe.Writer.WriteAsync(bytes.AsMemory(^1..), Frames.Token);
        using var received = await decoding;
        Frames.AssertSameFrame(sent, received);
    }

    [Fact]
    public async Task Cancelling_the_caller_is_not_reported_as_T8()
    {
        var fake = new FakeTimeProvider();
        var options = Frames.Options(T8);
        var decoder = new HsmsPipeDecoder(options, fake);

        using var sent = Frames.Create("s1f2");
        var bytes = Frames.Encode(sent, options);

        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(bytes.AsMemory(..^1), Frames.Token);

        using var cts = new CancellationTokenSource();
        var decoding = Frames.DecodeOneAsync(decoder, pipe.Reader, cts.Token);
        await Frames.WaitUntilAsync(() => decoder.IsReceivingFrame, "the decoder to hold a partial frame");

        await cts.CancelAsync();

        // A shutdown is a shutdown. Reporting it as a protocol timeout would send a spurious S9F9
        // and tell an engineer to go and look at the network.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
    }

    private static byte[] PrefixFor(int payloadLength)
    {
        var prefix = new byte[HsmsPipeDecoder.LengthPrefixSize];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payloadLength);
        return prefix;
    }
}
