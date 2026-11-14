// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// TCP transport: asynchronous sockets bridged to <see cref="System.IO.Pipelines"/>.
/// </summary>
/// <remarks>
/// <para>
/// There is no <see cref="NetworkStream"/> anywhere in this type, and no <c>byte[]</c> allocated
/// per read. Bytes go from <see cref="Socket.ReceiveAsync(Memory{byte}, SocketFlags, CancellationToken)"/>
/// straight into memory the <see cref="Pipe"/> owns, and out of the pipe straight into
/// <see cref="Socket.SendAsync(ReadOnlyMemory{byte}, SocketFlags, CancellationToken)"/> - so a
/// megabyte process program never touches the Large Object Heap on its way through.
/// </para>
/// <para>
/// In passive mode the listening socket is bound in <see cref="ConnectAsync"/>, not in the
/// constructor. secs4net binds in its constructor, which turns "port 5000 is already in use"
/// into an exception while the dependency-injection container is still building its graph - far
/// from the code that could report it usefully.
/// </para>
/// </remarks>
public sealed class TcpSecsTransport : ISecsTransport
{
    private readonly SecsGemOptions _options;
    private readonly ILogger<TcpSecsTransport> _logger;
    private readonly IPEndPoint _endPoint;
    private Socket? _listener;
    private bool _disposed;

    /// <summary>Initialises a new instance from configuration.</summary>
    /// <param name="options">The connection options.</param>
    /// <param name="logger">Where connection progress is reported.</param>
    public TcpSecsTransport(IOptions<SecsGemOptions> options, ILogger<TcpSecsTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
        _endPoint = new IPEndPoint(IPAddress.Parse(_options.IpAddress), _options.Port);
    }

    /// <inheritdoc/>
    public bool IsActive => _options.Mode == HsmsConnectionMode.Active;

    /// <inheritdoc/>
    public string Description => $"tcp://{_endPoint} ({(IsActive ? "active" : "passive")})";

    /// <inheritdoc/>
    public async ValueTask<ISecsTransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var socket = IsActive
            ? await ConnectOutAsync(cancellationToken).ConfigureAwait(false)
            : await AcceptAsync(cancellationToken).ConfigureAwait(false);

        socket.NoDelay = true;
        socket.ReceiveBufferSize = _options.SocketReceiveBufferSize;

        _logger.LogInformation("SECS link established with {Remote} over {Transport}.", socket.RemoteEndPoint, Description);
        return new SocketConnection(socket, _options.SocketReceiveBufferSize);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _listener?.Dispose();
        _listener = null;
        return ValueTask.CompletedTask;
    }

    private async ValueTask<Socket> ConnectOutAsync(CancellationToken cancellationToken)
    {
        var socket = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_endPoint, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async ValueTask<Socket> AcceptAsync(CancellationToken cancellationToken)
    {
        // Bound on first use, so a port clash surfaces where it can be handled - and so a
        // restart that reuses the port does not need the container rebuilt.
        if (_listener is null)
        {
            var listener = new Socket(_endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(_endPoint);

            // HSMS-SS permits one selected connection, so a backlog of one is honest: a second
            // caller is refused by the stack rather than accepted and then dropped.
            listener.Listen(backlog: 1);
            _listener = listener;
            _logger.LogInformation("Listening for a SECS connection on {EndPoint}.", _endPoint);
        }

        return await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bridges one socket to a duplex pipe with a receive loop and a send loop.
    /// </summary>
    private sealed class SocketConnection : ISecsTransportConnection
    {
        private readonly Socket _socket;
        private readonly Pipe _receive;
        private readonly Pipe _send;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _receiveLoop;
        private readonly Task _sendLoop;
        private bool _disposed;

        internal SocketConnection(Socket socket, int bufferSize)
        {
            _socket = socket;

            // Resume reading once the consumer has drained to half the high mark: the pause
            // threshold IS the back pressure that keeps a slow handler from turning into
            // unbounded memory.
            var pipeOptions = new PipeOptions(
                pauseWriterThreshold: bufferSize * 8,
                resumeWriterThreshold: bufferSize * 4,
                useSynchronizationContext: false);

            _receive = new Pipe(pipeOptions);
            _send = new Pipe(pipeOptions);

            Pipe = new DuplexPipe(_receive.Reader, _send.Writer);

            _receiveLoop = Task.Run(() => ReceiveLoopAsync(bufferSize, _shutdown.Token));
            _sendLoop = Task.Run(() => SendLoopAsync(_shutdown.Token));
        }

        public IDuplexPipe Pipe { get; }

        public string RemoteDescription => _socket.RemoteEndPoint?.ToString() ?? "unknown";

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await _shutdown.CancelAsync().ConfigureAwait(false);

            try
            {
                if (_socket.Connected)
                {
                    _socket.Shutdown(SocketShutdown.Both);
                }
            }
            catch (SocketException)
            {
                // The peer is already gone; nothing to shut down.
            }

            _socket.Dispose();

            await Task.WhenAll(_receiveLoop, _sendLoop).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _shutdown.Dispose();
        }

        private async Task ReceiveLoopAsync(int bufferSize, CancellationToken cancellationToken)
        {
            Exception? failure = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var buffer = _receive.Writer.GetMemory(bufferSize);
                    var read = await _socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    _receive.Writer.Advance(read);

                    var flush = await _receive.Writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flush.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await _receive.Writer.CompleteAsync(failure).ConfigureAwait(false);
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            Exception? failure = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var result = await _send.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    var buffer = result.Buffer;

                    foreach (var segment in buffer)
                    {
                        var remaining = segment;
                        while (!remaining.IsEmpty)
                        {
                            var sent = await _socket.SendAsync(remaining, SocketFlags.None, cancellationToken).ConfigureAwait(false);
                            remaining = remaining[sent..];
                        }
                    }

                    _send.Reader.AdvanceTo(buffer.End);

                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            await _send.Reader.CompleteAsync(failure).ConfigureAwait(false);
        }
    }
}
