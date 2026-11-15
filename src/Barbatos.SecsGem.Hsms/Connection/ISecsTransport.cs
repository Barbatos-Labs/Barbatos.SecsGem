// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// Where the bytes come from and go to.
/// </summary>
/// <remarks>
/// <para>
/// The HSMS state machine, the decoder and the transaction manager all work against an
/// <see cref="IDuplexPipe"/> and know nothing about sockets. Three things fall out of that:
/// </para>
/// <list type="bullet">
///   <item><description>
///     The whole protocol stack can be tested over an in-memory pipe - two entities talking in
///     one process, with no port, no firewall and no timing flakiness. That is what
///     <see cref="InMemorySecsTransport"/> is for.
///   </description></item>
///   <item><description>
///     TLS, an SSH tunnel or a vendor's proprietary wrapper is a transport, not a change to the
///     protocol code.
///   </description></item>
///   <item><description>
///     SECS-I over RS-232 can be added later as another transport, which is the one thing the
///     original secs4net design has no room for.
///   </description></item>
/// </list>
/// </remarks>
public interface ISecsTransport : IAsyncDisposable
{
    /// <summary>Gets a value indicating whether this transport connects out rather than waiting to be connected to.</summary>
    bool IsActive { get; }

    /// <summary>Gets a short description for logs, for example <c>tcp://127.0.0.1:5000 (passive)</c>.</summary>
    string Description { get; }

    /// <summary>Establishes one connection, waiting for a peer if this transport is passive.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The connection. Dispose it to close the link.</returns>
    ValueTask<ISecsTransportConnection> ConnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// One established link, as a duplex pipe.
/// </summary>
public interface ISecsTransportConnection : IAsyncDisposable
{
    /// <summary>Gets the pipe: <see cref="IDuplexPipe.Input"/> carries bytes in, <see cref="IDuplexPipe.Output"/> carries them out.</summary>
    IDuplexPipe Pipe { get; }

    /// <summary>Gets a description of the peer, for logs.</summary>
    string RemoteDescription { get; }
}

/// <summary>
/// A transport that pairs two entities inside one process.
/// </summary>
/// <remarks>
/// <para>
/// Not a mock: the two halves are real <see cref="Pipe"/> instances, so the decoder sees the
/// same segmented, partially-filled reads it sees on a socket. It simply has no port.
/// </para>
/// <para>
/// This is what makes the HSMS conformance tests possible: a host and a piece of equipment
/// complete a Select, exchange a transaction and tear down, deterministically, in microseconds,
/// with a <c>FakeTimeProvider</c> driving every timer.
/// </para>
/// </remarks>
public sealed class InMemorySecsTransport : ISecsTransport
{
    private readonly Pipe _hostToEquipment;
    private readonly Pipe _equipmentToHost;
    private readonly bool _isActive;

    /// <summary>Initialises a new instance with a pair of pipes of its own.</summary>
    /// <param name="isActive">Which side this instance presents as.</param>
    /// <remarks>
    /// Use this when the test itself plays the peer, driving <see cref="PeerPipe"/> with an
    /// encoder and a decoder. To put two real connections on the two ends instead, use
    /// <see cref="CreatePair"/>.
    /// </remarks>
    public InMemorySecsTransport(bool isActive = true)
        : this(new Pipe(), new Pipe(), isActive)
    {
    }

    private InMemorySecsTransport(Pipe hostToEquipment, Pipe equipmentToHost, bool isActive)
    {
        _hostToEquipment = hostToEquipment;
        _equipmentToHost = equipmentToHost;
        _isActive = isActive;
    }

    /// <inheritdoc/>
    public bool IsActive => _isActive;

    /// <inheritdoc/>
    public string Description => $"memory:// ({(_isActive ? "active" : "passive")})";

    /// <summary>Gets the end this transport presents - the active side by default.</summary>
    public IDuplexPipe LocalPipe => _isActive
        ? new DuplexPipe(_equipmentToHost.Reader, _hostToEquipment.Writer)
        : new DuplexPipe(_hostToEquipment.Reader, _equipmentToHost.Writer);

    /// <summary>Gets the other end, to hand to the peer entity under test.</summary>
    /// <remarks>
    /// For the single-transport case, where the test is the peer. On a transport from
    /// <see cref="CreatePair"/> the other end already has a connection on it, and driving this as
    /// well would be two readers on one pipe.
    /// </remarks>
    public IDuplexPipe PeerPipe => _isActive
        ? new DuplexPipe(_hostToEquipment.Reader, _equipmentToHost.Writer)
        : new DuplexPipe(_equipmentToHost.Reader, _hostToEquipment.Writer);

    /// <summary>Creates two transports wired to each other, for two real connections.</summary>
    /// <returns>The active end and the passive end, over one shared pair of pipes.</returns>
    /// <remarks>
    /// <para>
    /// This is what makes an HSMS conformance test possible without a port: a host and a piece of
    /// equipment, both running their own state machine and their own timers, completing a Select,
    /// exchanging a transaction and tearing down - deterministically, in microseconds, with a
    /// <c>FakeTimeProvider</c> driving every timer on both sides.
    /// </para>
    /// <para>
    /// Both halves are real <see cref="Pipe"/> instances, so each connection sees the same
    /// segmented, partially-filled reads it sees on a socket. Nothing is stubbed; the only thing
    /// missing is the network.
    /// </para>
    /// </remarks>
    public static (InMemorySecsTransport Active, InMemorySecsTransport Passive) CreatePair()
    {
        var hostToEquipment = new Pipe();
        var equipmentToHost = new Pipe();

        return (
            new InMemorySecsTransport(hostToEquipment, equipmentToHost, isActive: true),
            new InMemorySecsTransport(hostToEquipment, equipmentToHost, isActive: false));
    }

    /// <inheritdoc/>
    public ValueTask<ISecsTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<ISecsTransportConnection>(new InMemoryConnection(LocalPipe));

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _hostToEquipment.Writer.Complete();
        _hostToEquipment.Reader.Complete();
        _equipmentToHost.Writer.Complete();
        _equipmentToHost.Reader.Complete();
        return ValueTask.CompletedTask;
    }

    private sealed class InMemoryConnection(IDuplexPipe pipe) : ISecsTransportConnection
    {
        public IDuplexPipe Pipe => pipe;

        public string RemoteDescription => "memory://peer";

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// Pairs a reader and a writer into an <see cref="IDuplexPipe"/>.
/// </summary>
/// <param name="input">The side bytes arrive on.</param>
/// <param name="output">The side bytes leave on.</param>
public sealed class DuplexPipe(PipeReader input, PipeWriter output) : IDuplexPipe
{
    /// <inheritdoc/>
    public PipeReader Input { get; } = input ?? throw new ArgumentNullException(nameof(input));

    /// <inheritdoc/>
    public PipeWriter Output { get; } = output ?? throw new ArgumentNullException(nameof(output));
}
