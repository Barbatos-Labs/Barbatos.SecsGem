// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using Barbatos.SecsGem.Hsms;

namespace Barbatos.SecsGem.Gem.UnitTests;

/// <summary>
/// Holds one frame back on its way out of the equipment, so a test can say what must not have
/// happened yet.
/// </summary>
/// <remarks>
/// <para>
/// Most ordering questions in this layer are races between two threads of the equipment itself, and
/// a test that only reads the wire sees whichever order the scheduler happened to produce - which is
/// the right order nearly every time, and is why the first such race here surfaced only as an
/// occasional failure in a full run. This chooses the order instead of observing it: arm it, and
/// the next frame the equipment flushes is written but not delivered until <see cref="Release"/>.
/// Whatever the equipment is going to do <em>about</em> that frame can then be watched happening,
/// or not happening, while the frame is still stuck.
/// </para>
/// <para>
/// One flush, once. The connection serialises its writers behind a single lock, so while a flush is
/// held nothing else can reach the wire anyway; a second held flush would be a different test.
/// Disposing releases, so a test that fails with the gate shut does not leave the equipment's loops
/// waiting on it.
/// </para>
/// </remarks>
internal sealed class OutputGate : IDisposable
{
    private readonly object _lock = new();
    private TaskCompletionSource _held = NewSignal();
    private TaskCompletionSource _released = NewSignal();
    private GateState _state;

    private enum GateState
    {
        Open,
        Armed,
        Held,
    }

    /// <summary>Gets a task that completes once a flush is being held.</summary>
    /// <remarks>Replaced each time <see cref="HoldNextFlush"/> is called, so read it after arming.</remarks>
    public Task WhenHeld
    {
        get
        {
            lock (_lock)
            {
                return _held.Task;
            }
        }
    }

    /// <summary>Holds the next flush the equipment makes, until <see cref="Release"/>.</summary>
    public void HoldNextFlush()
    {
        lock (_lock)
        {
            _held = NewSignal();
            _released = NewSignal();
            _state = GateState.Armed;
        }
    }

    /// <summary>Lets the held flush - or, if none has arrived yet, the next one - go.</summary>
    public void Release()
    {
        lock (_lock)
        {
            _state = GateState.Open;
            _released.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => Release();

    /// <summary>Puts this gate on the equipment's side of a transport.</summary>
    /// <param name="inner">The transport the equipment would otherwise use.</param>
    /// <returns>A transport whose output this gate can hold.</returns>
    /// <remarks>Only what the equipment writes is gated. What the test writes as the host is not.</remarks>
    public ISecsTransport Wrap(ISecsTransport inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return new GatedTransport(inner, this);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Called by a flush on its way through. Returns what to wait for, or null to carry on.</summary>
    private Task? Enter()
    {
        lock (_lock)
        {
            if (_state != GateState.Armed)
            {
                return null;
            }

            _state = GateState.Held;
            _held.TrySetResult();

            return _released.Task;
        }
    }

    private sealed class GatedTransport(ISecsTransport inner, OutputGate gate) : ISecsTransport
    {
        public bool IsActive => inner.IsActive;

        public string Description => inner.Description;

        public async ValueTask<ISecsTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => new GatedConnection(await inner.ConnectAsync(cancellationToken).ConfigureAwait(false), gate);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class GatedConnection(ISecsTransportConnection inner, OutputGate gate) : ISecsTransportConnection
    {
        // One writer for the life of the connection: callers read Pipe on every send.
        private readonly DuplexPipe _pipe = new(inner.Pipe.Input, new GatedWriter(inner.Pipe.Output, gate));

        public IDuplexPipe Pipe => _pipe;

        public string RemoteDescription => inner.RemoteDescription;

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>A writer that waits at the flush, which is where bytes become the peer's to read.</summary>
    private sealed class GatedWriter(PipeWriter inner, OutputGate gate) : PipeWriter
    {
        public override void Advance(int bytes) => inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (gate.Enter() is { } released)
            {
                await released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
