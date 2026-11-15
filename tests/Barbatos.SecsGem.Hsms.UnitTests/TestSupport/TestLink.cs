// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Microsoft.Extensions.Time.Testing;

namespace Barbatos.SecsGem.Hsms.UnitTests;

/// <summary>
/// A live <see cref="HsmsConnection"/> with the test standing in for the peer on the other end.
/// </summary>
/// <remarks>
/// <para>
/// Not a mock. The connection under test runs its real actor loop over a real
/// <see cref="InMemorySecsTransport"/>, and the test drives the other end of the same pipes with
/// the real encoder and decoder. Every frame in these tests is the frame that would have gone out
/// on a socket.
/// </para>
/// <para>
/// Every timer runs on a <see cref="FakeTimeProvider"/>, so T3, T5, T6, T7 and T8 only elapse when
/// a test says so. Nothing here waits on a wall clock; the only real-time waits are the bounded
/// ones that synchronise on the connection's own progress, and they fail loudly rather than hang.
/// </para>
/// </remarks>
internal sealed class TestLink : IAsyncDisposable
{
    private readonly InMemorySecsTransport _transport;
    private readonly CancellationTokenSource _cts = new();
    private readonly IAsyncEnumerator<HsmsMessage> _inbound;
    private readonly HsmsPipeEncoder _encoder;
    private readonly List<HsmsConnectionStateChangedEventArgs> _transitions = [];

    private TestLink(
        InMemorySecsTransport transport,
        HsmsConnection connection,
        SecsGemOptions options,
        FakeTimeProvider clock)
    {
        _transport = transport;
        Connection = connection;
        Options = options;
        Clock = clock;
        _encoder = new HsmsPipeEncoder(options);

        PeerDecoder = new HsmsPipeDecoder(options);
        _inbound = PeerDecoder.DecodeAsync(transport.PeerPipe.Input, _cts.Token).GetAsyncEnumerator(_cts.Token);

        connection.StateChanged += (_, args) =>
        {
            lock (_transitions)
            {
                _transitions.Add(args);
            }
        };

        Running = connection.RunAsync(_cts.Token);
    }

    /// <summary>Gets the connection under test.</summary>
    public HsmsConnection Connection { get; }

    /// <summary>Gets the options both ends share.</summary>
    public SecsGemOptions Options { get; }

    /// <summary>Gets the clock every timer runs on.</summary>
    public FakeTimeProvider Clock { get; }

    /// <summary>Gets the task running the connection's own loop.</summary>
    public Task Running { get; }

    /// <summary>Gets the decoder the peer side reads with, for its path counters.</summary>
    public HsmsPipeDecoder PeerDecoder { get; }

    /// <summary>Gets the state transitions seen so far, oldest first.</summary>
    public IReadOnlyList<HsmsConnectionStateChangedEventArgs> Transitions
    {
        get
        {
            lock (_transitions)
            {
                return [.. _transitions];
            }
        }
    }

    /// <summary>Starts a connection with the test as its peer.</summary>
    /// <param name="active">
    /// <see langword="true"/> for an entity that connects out and selects - the host role.
    /// <see langword="false"/> for one that waits to be selected - the equipment role.
    /// </param>
    /// <param name="routes">Handlers to register, or <see langword="null"/> for none.</param>
    /// <param name="configure">Changes to the options.</param>
    /// <returns>The link. Dispose it.</returns>
    public static TestLink Start(
        bool active = true,
        Action<SecsMessageRouterBuilder>? routes = null,
        Action<SecsGemOptions>? configure = null)
    {
        var options = Frames.Options();
        options.LinkTestInterval = TimeSpan.FromMinutes(1);
        configure?.Invoke(options);

        var routerBuilder = new SecsMessageRouterBuilder();
        routes?.Invoke(routerBuilder);

        var clock = new FakeTimeProvider();
        var transport = new InMemorySecsTransport(active);
        var connection = new HsmsConnection(options, transport, routerBuilder.Build(), clock);

        return new TestLink(transport, connection, options, clock);
    }

    /// <summary>Completes the Select handshake, whichever side is supposed to start it.</summary>
    /// <returns>A task that completes once the connection reports itself Selected.</returns>
    /// <remarks>
    /// For an active connection the test answers the Select.req it sent. For a passive one the test
    /// sends Select.req and reads the Select.rsp back. Either way the connection ends up Selected
    /// through the real handshake rather than by having its state poked.
    /// </remarks>
    public async Task SelectAsync()
    {
        if (_transport.IsActive)
        {
            using var request = await ReadAsync().ConfigureAwait(false);
            Assert.Equal(MessageType.SelectRequest, request.Header.MessageType);
            await SendAsync(HsmsMessage.Control(MessageType.SelectResponse, request.SystemBytes, status: (byte)SelectStatus.Success))
                .ConfigureAwait(false);
        }
        else
        {
            await SendAsync(HsmsMessage.Control(MessageType.SelectRequest, 0x5001)).ConfigureAwait(false);
            using var response = await ReadAsync().ConfigureAwait(false);
            Assert.Equal(MessageType.SelectResponse, response.Header.MessageType);
            Assert.Equal(SelectStatus.Success, response.Header.SelectStatus);
        }

        await WaitForStateAsync(HsmsConnectionState.Selected).ConfigureAwait(false);
    }

    /// <summary>Sends a frame to the connection, as the peer would.</summary>
    /// <param name="frame">The frame. Disposed by this call.</param>
    /// <returns>A task that completes once the frame has been flushed.</returns>
    public async ValueTask SendAsync(HsmsMessage frame)
    {
        using (frame)
        {
            await _encoder
                .WriteAsync(_transport.PeerPipe.Output, frame.Header, frame.Data, Frames.Token)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Sends a data message to the connection under the shared device id.</summary>
    /// <param name="message">The message. The caller keeps ownership.</param>
    /// <param name="systemBytes">The transaction id.</param>
    /// <param name="deviceId">The device id to address it to. Defaults to the one the connection answers to.</param>
    /// <returns>A task that completes once the frame has been flushed.</returns>
    public async ValueTask SendDataAsync(SecsMessage message, int systemBytes, ushort? deviceId = null)
    {
        var header = new MessageHeader
        {
            SessionId = deviceId ?? Options.DeviceId,
            Stream = message.S,
            Function = message.F,
            ReplyExpected = message.ReplyExpected,
            PType = 0,
            MessageType = MessageType.DataMessage,
            SystemBytes = systemBytes,
        };

        await _encoder.WriteAsync(_transport.PeerPipe.Output, header, message, Frames.Token).ConfigureAwait(false);
    }

    /// <summary>Sends bytes straight at the connection, for frames the encoder refuses to build.</summary>
    /// <param name="frame">The complete frame, length prefix included.</param>
    /// <returns>A task that completes once the bytes have been flushed.</returns>
    /// <remarks>
    /// A peer is free to send anything. Hand-built bytes are the only way to test what happens
    /// when it sends something malformed, which is the case that matters most.
    /// </remarks>
    public async ValueTask SendRawAsync(ReadOnlyMemory<byte> frame)
    {
        await _transport.PeerPipe.Output.WriteAsync(frame, Frames.Token).ConfigureAwait(false);
    }

    /// <summary>Reads the next frame the connection sent.</summary>
    /// <returns>The frame. Dispose it.</returns>
    /// <remarks>
    /// Bounded in real time so a connection that sends nothing fails the test instead of hanging
    /// the run. The bound has nothing to do with protocol time, which is entirely on the fake clock.
    /// </remarks>
    public async ValueTask<HsmsMessage> ReadAsync()
    {
        var moved = await _inbound.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), Frames.Token).ConfigureAwait(false);

        return moved
            ? _inbound.Current
            : throw new InvalidOperationException("The connection closed its output instead of sending a frame.");
    }

    /// <summary>Waits for the connection to reach a state.</summary>
    /// <param name="state">The state.</param>
    /// <returns>A task that completes once it is reached.</returns>
    public Task WaitForStateAsync(HsmsConnectionState state)
        => Frames.WaitUntilAsync(() => Connection.State == state, $"the connection to reach {state}");

    /// <summary>Waits until the state-change event has reported at least this many transitions.</summary>
    /// <param name="count">How many to wait for.</param>
    /// <returns>A task that completes once they have been reported.</returns>
    /// <remarks>
    /// <para>
    /// Waiting for a state is <strong>not</strong> waiting for the event that reports it. The
    /// connection sets the state and then raises <c>StateChanged</c>, so a test that waits on
    /// <see cref="WaitForStateAsync"/> and then reads <see cref="Transitions"/> is reading a list
    /// that may still be one entry behind.
    /// </para>
    /// <para>
    /// It is a race a test wins almost every time on an idle machine and loses every few runs of
    /// the full suite, where twenty-four assemblies are running at once. Any assertion that a
    /// transition <em>is</em> in the list wants this first; an assertion that one is not can only
    /// be made weaker by the gap, not wrong.
    /// </para>
    /// </remarks>
    public Task WaitForTransitionsAsync(int count)
        => Frames.WaitUntilAsync(
            () => Transitions.Count >= count,
            $"{count} transition(s) to be reported");

    /// <summary>Walks the fake clock forward in small steps until something happens.</summary>
    /// <param name="step">How far to move per step. Keep it well under the timer being tested.</param>
    /// <param name="condition">What is being waited for.</param>
    /// <param name="what">A description, for the failure message.</param>
    /// <returns>A task that completes once the condition holds.</returns>
    /// <remarks>
    /// <para>
    /// A timer only exists once the code under test has armed it, and there is no observable moment
    /// at which that happens - <c>Task.WaitAsync</c> arms its timeout somewhere inside an await
    /// that has no counter attached. Stepping, rather than making one big jump, means the clock
    /// cannot run past a timer that was armed a microsecond later.
    /// </para>
    /// <para>
    /// Small steps also keep one timer's test from tripping another: a single jump of several
    /// times T3 would sail past the Linktest interval too, and the test would be measuring
    /// something it did not mean to.
    /// </para>
    /// </remarks>
    public async Task AdvanceUntilAsync(TimeSpan step, Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return;
            }

            Clock.Advance(step);
            await Task.Delay(1, Frames.Token).ConfigureAwait(false);
        }

        Assert.Fail($"Timed out waiting for {what}, after advancing the clock by {step * 400}.");
    }

    /// <summary>Stops the connection and releases the link.</summary>
    /// <returns>A task that completes once everything has stopped.</returns>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await Running.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The loop is being torn down; a cancellation or a closed pipe here is expected.
        }

        try
        {
            await _inbound.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Same.
        }

        await Connection.DisposeAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }
}
