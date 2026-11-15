// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.IO.Pipelines;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Hsms;

/// <summary>
/// An HSMS-SS connection: the state machine, the timers, the transactions and the automatic
/// Stream 9 reporting - SEMI E37.
/// </summary>
/// <remarks>
/// <para><strong>One loop owns the state.</strong></para>
/// <para>
/// Everything that can change <see cref="State"/> is turned into an event and posted to a single
/// channel that one loop reads. The frame pump posts frames; the T7, T6 and Linktest timers post
/// expiries; a failing background Select posts a close. None of them touch the state themselves.
/// </para>
/// <para>
/// That is not ceremony. The alternative is what secs4net does: its
/// <c>CommunicationStateChanging</c> calls <c>Disconnect()</c> and then <c>Start()</c> from inside
/// a <see cref="System.Threading.Timer"/> callback, so a timer firing during a transition re-enters
/// the transition it is already in the middle of. There is no lock that makes that correct, because
/// the problem is re-entrancy rather than concurrency. An actor has no re-entrancy to have.
/// </para>
/// <para><strong>Three loops, and why not one.</strong></para>
/// <list type="bullet">
///   <item><description>
///     The <em>pump</em> decodes frames and posts them. It also re-enters
///     <see cref="HsmsPipeDecoder.DecodeAsync"/> after a recoverable decode failure, which is how
///     one malformed body costs an S9F7 rather than the link.
///   </description></item>
///   <item><description>
///     The <em>actor</em> runs the state machine and answers control messages. It must never
///     block on user code, or a Linktest would queue behind a slow report handler and the peer
///     would drop a link that was working.
///   </description></item>
///   <item><description>
///     The <em>dispatch</em> loop drains received primaries into handlers. Its queue is bounded at
///     <see cref="SecsGemOptions.PrimaryMessageQueueCapacity"/>, so a slow handler becomes back
///     pressure rather than unbounded memory growth.
///   </description></item>
/// </list>
/// <para><strong>Timers.</strong> T3 and T6 are awaited timeouts on a transaction, T5 separates
/// connection attempts, T7 and the Linktest interval are timers on the session, and T8 belongs to
/// the decoder. Every one of them runs on <see cref="TimeProvider"/>, so a conformance test drives
/// the lot instantly.</para>
/// <para><strong>Lifetime.</strong> <see cref="RunAsync"/> keeps the link up - connecting,
/// selecting, and reconnecting no sooner than every T5 - until it is cancelled. Call it once.</para>
/// </remarks>
public sealed class HsmsConnection : IAsyncDisposable
{
    /// <summary>
    /// How many events may queue for the actor.
    /// </summary>
    /// <remarks>
    /// Small on purpose: this is a transition queue, not a message buffer. Received primaries wait
    /// in their own queue, sized by <see cref="SecsGemOptions.PrimaryMessageQueueCapacity"/>, and
    /// filling that is what applies back pressure to the peer.
    /// </remarks>
    private const int EventQueueCapacity = 64;

    private readonly SecsGemOptions _options;
    private readonly ISecsTransport _transport;
    private readonly ISecsMessageRouter _router;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly HsmsPipeEncoder _encoder;
    private readonly PendingTransactions _pending;

    private Session? _session;
    private HsmsConnectionState _state;
    private long _fastPathFrames;
    private long _slowPathFrames;
    private int _started;
    private bool _disposed;

    /// <summary>Initialises a new connection.</summary>
    /// <param name="options">The connection settings.</param>
    /// <param name="transport">Where the bytes come from.</param>
    /// <param name="router">Which handler answers which primary, and which streams this entity claims.</param>
    /// <param name="timeProvider">Drives every timer. <see langword="null"/> uses <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Where transitions, timer expiries and protocol errors go. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/>, <paramref name="transport"/> or <paramref name="router"/> is <see langword="null"/>.</exception>
    public HsmsConnection(
        SecsGemOptions options,
        ISecsTransport transport,
        ISecsMessageRouter router,
        TimeProvider? timeProvider = null,
        ILogger<HsmsConnection>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(router);

        _options = options;
        _transport = transport;
        _router = router;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<HsmsConnection>.Instance;
        _encoder = new HsmsPipeEncoder(options);
        _pending = new PendingTransactions(_timeProvider);
    }

    /// <summary>Raised after every state transition, on the actor loop.</summary>
    /// <remarks>
    /// Handlers run before the next transition is processed, so they see the states in order - and
    /// so a slow handler delays the connection. Do real work elsewhere.
    /// </remarks>
    public event EventHandler<HsmsConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>Gets where the connection is in its lifecycle.</summary>
    public HsmsConnectionState State => _state;

    /// <summary>Gets a value indicating whether data messages can flow right now.</summary>
    public bool IsSelected => _state == HsmsConnectionState.Selected;

    /// <summary>Gets the device id this entity answers to.</summary>
    public ushort DeviceId => _options.DeviceId;

    /// <summary>Gets how many frames have arrived whole, across every session on this connection.</summary>
    /// <remarks>See <see cref="HsmsPipeDecoder.FastPathFrames"/>. Accumulated across reconnections.</remarks>
    public long FastPathFrames => Interlocked.Read(ref _fastPathFrames) + (_session?.Decoder.FastPathFrames ?? 0);

    /// <summary>Gets how many frames have spanned reads, across every session on this connection.</summary>
    /// <remarks>See <see cref="HsmsPipeDecoder.SlowPathFrames"/>. Accumulated across reconnections.</remarks>
    public long SlowPathFrames => Interlocked.Read(ref _slowPathFrames) + (_session?.Decoder.SlowPathFrames ?? 0);

    /// <summary>Gets how many sent primaries are still waiting for their secondary.</summary>
    public int OpenTransactions => _pending.OpenDataTransactions;

    /// <summary>Keeps the link up until cancelled.</summary>
    /// <param name="cancellationToken">Stops the connection and closes the link.</param>
    /// <returns>A task that completes once the loop has stopped.</returns>
    /// <exception cref="InvalidOperationException">Already running. Call this once.</exception>
    /// <remarks>
    /// Connects, selects, serves the link, and on losing it waits out T5 before trying again -
    /// forever. A transport failure is logged and retried rather than thrown, because a host that
    /// gives up the first time a piece of equipment power cycles is not much use in a fab.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException($"{nameof(RunAsync)} has already been called on this connection.");
        }

        _logger.LogInformation(
            "SECS/GEM connection starting: device {DeviceId} over {Transport}",
            _options.DeviceId,
            _transport.Description);

        if (_router.IsEmpty)
        {
            _logger.LogWarning(
                "No message handlers are registered, so every received primary will be answered with S9F5. "
                + "Register handlers with OnPrimary, or a catch-all with OnUnhandledPrimary.");
        }

        var firstAttempt = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!firstAttempt)
            {
                // T5, connect separation: a floor on how often we knock, not a delay before the
                // first knock. Equipment that is power cycling should not be hammered.
                try
                {
                    await Task.Delay(_options.T5, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            firstAttempt = false;

            try
            {
                await using var connection = await _transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await RunSessionAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The SECS/GEM link failed and will be retried after T5 ({T5}).", _options.T5);
            }
        }

        _logger.LogInformation("SECS/GEM connection stopped.");
    }

    /// <summary>Sends a primary message and waits for its secondary, under T3.</summary>
    /// <param name="primary">The primary. Must have W set. The caller keeps ownership.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The secondary. The caller owns it and should dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="primary"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="primary"/> has W clear - use <see cref="PostAsync"/>.</exception>
    /// <exception cref="SecsException">The connection is not selected.</exception>
    /// <exception cref="SecsTimeoutException">T3 expired with no reply.</exception>
    /// <remarks>
    /// On a T3 expiry this sends S9F9 to the peer when
    /// <see cref="SecsGemOptions.SendS9F9OnReplyTimeout"/> is set, and then throws. E5 says the
    /// entity whose transaction timer expired reports it; a peer that was merely slow otherwise
    /// has no idea its reply was abandoned.
    /// </remarks>
    public async ValueTask<SecsMessage> SendAsync(SecsMessage primary, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!primary.ReplyExpected)
        {
            throw new ArgumentException(
                $"{primary} was built with W clear, so no reply will come. Use {nameof(PostAsync)}.",
                nameof(primary));
        }

        var session = RequireSelectedSession();
        var systemBytes = _pending.NextSystemBytes();
        var header = DataHeader(primary, systemBytes);

        // Registered before the flush, never after: on an in-memory link the reply can be back
        // before this line would have run, and a reply with no waiter is discarded as late.
        var waiter = _pending.ExpectSecondary(systemBytes);

        try
        {
            await SendFrameAsync(session, header, primary, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.Forget(systemBytes);
            throw;
        }

        try
        {
            return await _pending
                .AwaitSecondaryAsync(waiter, systemBytes, primary, _options.T3, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SecsTimeoutException) when (_options.SendS9F9OnReplyTimeout)
        {
            _logger.LogWarning("T3 expired for {Message}; reporting S9F9.", header);
            await TryPostErrorReportAsync(session, S9Messages.TransactionTimerTimeout(header)).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Sends a message that expects no reply.</summary>
    /// <param name="message">The message. Must have W clear. The caller keeps ownership.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes once the message has been flushed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="message"/> has W set - use <see cref="SendAsync"/>.</exception>
    /// <exception cref="SecsException">The connection is not selected.</exception>
    public async ValueTask PostAsync(SecsMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (message.ReplyExpected)
        {
            throw new ArgumentException(
                $"{message} was built with W set, so the peer will expect a reply. Use {nameof(SendAsync)}.",
                nameof(message));
        }

        var session = RequireSelectedSession();
        var header = DataHeader(message, _pending.NextSystemBytes());
        await SendFrameAsync(session, header, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a Linktest and waits for its response, under T6.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the peer answers.</returns>
    /// <exception cref="SecsException">There is no link.</exception>
    /// <exception cref="SecsTimeoutException">T6 expired with no response.</exception>
    /// <remarks>
    /// The connection sends these on its own every
    /// <see cref="SecsGemOptions.LinkTestInterval"/>; this is for a health check that wants to ask
    /// now and know the answer.
    /// </remarks>
    public async ValueTask LinkTestAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var session = RequireSession();
        await ControlTransactionAsync(session, MessageType.LinkTestRequest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a Deselect.req and, once the peer agrees, leaves the Selected state.</summary>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the peer has answered and the transition has been asked for.</returns>
    /// <exception cref="SecsException">There is no link, the connection is not Selected, or the peer refused.</exception>
    /// <exception cref="SecsTimeoutException">T6 expired with no response.</exception>
    /// <remarks>
    /// <para>
    /// HSMS-SS interop rarely needs this - a host that wants the link down sends Separate.req or
    /// simply closes the socket. The GEM control-state model is what gives it a reason to exist:
    /// going off-line deliberately is not the same event as a cable being pulled, and a peer that
    /// is told so can keep its own books straight.
    /// </para>
    /// <para>
    /// The transition is <em>posted</em> to the actor rather than applied here. This method runs on
    /// the caller's thread and <see cref="_state"/> has exactly one writer, so a deselect that
    /// assigned it directly would be precisely the re-entrancy this design exists to prevent. The
    /// task therefore completes when the peer has answered; the state follows a moment later, and
    /// <see cref="StateChanged"/> is still raised in order with everything else.
    /// </para>
    /// </remarks>
    public async ValueTask DeselectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var session = RequireSession();

        if (_state != HsmsConnectionState.Selected)
        {
            throw new SecsException($"A deselect needs the Selected state; this connection is {_state}.");
        }

        var response = await ControlTransactionAsync(session, MessageType.DeselectRequest, cancellationToken).ConfigureAwait(false);

        if (response.DeselectStatus != DeselectStatus.Success)
        {
            throw new SecsException($"The peer refused the deselect with {response.DeselectStatus}.");
        }

        session.Events.TryWrite(new SessionEvent(SessionEventKind.LocalDeselected));
    }

    /// <summary>Stops the connection and closes any live link.</summary>
    /// <returns>A task that completes once everything has been released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var session = _session;
        if (session is not null)
        {
            await session.Cts.CancelAsync().ConfigureAwait(false);
        }

        _pending.FailAll(new SecsException("The connection was disposed."));
    }

    // ----------------------------------------------------------------- the session

    private async Task RunSessionAsync(ISecsTransportConnection connection, CancellationToken cancellationToken)
    {
        var decoder = new HsmsPipeDecoder(_options, _timeProvider);
        var events = Channel.CreateBounded<SessionEvent>(
            new BoundedChannelOptions(EventQueueCapacity) { SingleReader = true, SingleWriter = false });
        var primaries = Channel.CreateBounded<Dispatch>(
            new BoundedChannelOptions(_options.PrimaryMessageQueueCapacity) { SingleReader = true, SingleWriter = true });

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new Session(connection, decoder, sessionCts, events.Writer);
        _session = session;

        using var t7 = new EventTimer(_timeProvider, () => events.Writer.TryWrite(new SessionEvent(SessionEventKind.T7Expired)));
        using var linkTest = new EventTimer(_timeProvider, () => events.Writer.TryWrite(new SessionEvent(SessionEventKind.LinkTestDue)));

        var pump = PumpAsync(decoder, connection.Pipe.Input, events.Writer, sessionCts.Token);
        var dispatch = DispatchAsync(primaries.Reader, sessionCts.Token);

        try
        {
            _logger.LogInformation("Link established with {Peer}.", connection.RemoteDescription);

            // T7 is armed before the state is published, never after. Reaching Connected is what
            // tells an observer - a health check, or a test about to move a FakeTimeProvider -
            // that T7 is running, and that has to be true by the time they can see it. The same
            // ordering applies to the Linktest timer below.
            t7.StartOnce(_options.T7);
            SetState(HsmsConnectionState.Connected, "link established");

            if (_transport.IsActive)
            {
                // E37 gives the active entity the job of selecting. The await happens on its own
                // task because the response arrives through the actor loop below - selecting on
                // the actor would be waiting for itself.
                _ = SelectInBackgroundAsync(session, events.Writer, sessionCts.Token);
            }

            await ActorAsync(session, events, primaries.Writer, t7, linkTest, sessionCts.Token).ConfigureAwait(false);
        }
        finally
        {
            await sessionCts.CancelAsync().ConfigureAwait(false);
            events.Writer.TryComplete();
            primaries.Writer.TryComplete();
            _session = null;

            Interlocked.Add(ref _fastPathFrames, decoder.FastPathFrames);
            Interlocked.Add(ref _slowPathFrames, decoder.SlowPathFrames);

            _pending.FailAll(new SecsException("The link closed before the reply arrived."));
            SetState(HsmsConnectionState.NotConnected, "link closed");

            await WhenAllQuietly(pump, dispatch).ConfigureAwait(false);
            DrainAndDispose(events.Reader, primaries.Reader);
            session.Dispose();
        }
    }

    /// <summary>Decodes frames and posts them, resuming after a failure the link can survive.</summary>
    private async Task PumpAsync(
        HsmsPipeDecoder decoder,
        PipeReader input,
        ChannelWriter<SessionEvent> events,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var frame in decoder.DecodeAsync(input, cancellationToken).ConfigureAwait(false))
                {
                    await Post(events, new SessionEvent(SessionEventKind.Frame, frame), cancellationToken)
                        .ConfigureAwait(false);
                }

                events.TryWrite(new SessionEvent(SessionEventKind.Closed));
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SecsDecodeException ex) when (ex.Header is not null)
            {
                // The offending frame was consumed, so the next one starts where it should. An
                // async iterator cannot resume past an exception, so the loop re-enters
                // DecodeAsync - on the same reader, which has not lost its place.
                events.TryWrite(new SessionEvent(SessionEventKind.Recoverable, Error: ex));
                continue;
            }
            catch (Exception ex)
            {
                events.TryWrite(new SessionEvent(SessionEventKind.Fatal, Error: ex));
                return;
            }
        }

        static async ValueTask Post(ChannelWriter<SessionEvent> events, SessionEvent item, CancellationToken cancellationToken)
        {
            if (!events.TryWrite(item))
            {
                await events.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs the state machine. The only place <see cref="_state"/> changes.</summary>
    private async Task ActorAsync(
        Session session,
        Channel<SessionEvent> events,
        ChannelWriter<Dispatch> primaries,
        EventTimer t7,
        EventTimer linkTest,
        CancellationToken cancellationToken)
    {
        await foreach (var current in events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (current.Kind)
            {
                case SessionEventKind.Frame:
                    if (await OnFrameAsync(session, current.Frame!, primaries, t7, linkTest, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    break;

                case SessionEventKind.Recoverable:
                    await OnRecoverableFailureAsync(session, current.Error!, cancellationToken).ConfigureAwait(false);
                    break;

                case SessionEventKind.Fatal:
                    await OnFatalFailureAsync(session, current.Error!, cancellationToken).ConfigureAwait(false);
                    return;

                case SessionEventKind.Closed:
                    _logger.LogInformation("The peer closed the link.");
                    return;

                case SessionEventKind.T7Expired:
                    _logger.LogWarning(
                        "T7 expired after {T7}: the link reached {State} and never reached Selected. Dropping it.",
                        _options.T7,
                        _state);
                    return;

                case SessionEventKind.LinkTestDue:
                    if (_state == HsmsConnectionState.Selected)
                    {
                        _ = LinkTestInBackgroundAsync(session, events.Writer, cancellationToken);
                    }

                    break;

                case SessionEventKind.LocalDeselected:
                    if (_state == HsmsConnectionState.Selected)
                    {
                        linkTest.Stop();

                        // Back in NOT SELECTED, so T7 runs again - the same reasoning as a
                        // Deselect.req arriving from the peer, and armed before the state is
                        // published for the same reason.
                        t7.StartOnce(_options.T7);
                        SetState(HsmsConnectionState.Connected, "Deselect.req to the peer");
                    }

                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Handles one frame. Returns <see langword="true"/> when the link must close.</summary>
    private async ValueTask<bool> OnFrameAsync(
        Session session,
        HsmsMessage frame,
        ChannelWriter<Dispatch> primaries,
        EventTimer t7,
        EventTimer linkTest,
        CancellationToken cancellationToken)
    {
        var header = frame.Header;

        // A body in a presentation this stack does not speak. Rejecting it is the whole reason
        // PType is mapped at all.
        if (header.MessageType == MessageType.DataMessage && header.PType != 0)
        {
            frame.Dispose();
            _logger.LogWarning("A data message arrived with PType {PType}; rejecting it.", header.PType);
            await SendControlAsync(session, MessageType.RejectRequest, header.SystemBytes, (byte)RejectReason.PtypeNotSupported, cancellationToken).ConfigureAwait(false);
            return false;
        }

        switch (header.MessageType)
        {
            case MessageType.SelectRequest:
                frame.Dispose();
                if (_state == HsmsConnectionState.Selected)
                {
                    // HSMS-SS permits one selection, so a second request is answered rather than
                    // allowed to replace the first.
                    await SendControlAsync(session, MessageType.SelectResponse, header.SystemBytes, (byte)SelectStatus.AlreadyActive, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SendControlAsync(session, MessageType.SelectResponse, header.SystemBytes, (byte)SelectStatus.Success, cancellationToken).ConfigureAwait(false);
                    t7.Stop();
                    StartLinkTest(linkTest);
                    SetState(HsmsConnectionState.Selected, "Select.req from the peer");
                }

                return false;

            case MessageType.SelectResponse:
                frame.Dispose();
                if (header.SelectStatus == SelectStatus.Success)
                {
                    t7.Stop();
                    StartLinkTest(linkTest);
                    SetState(HsmsConnectionState.Selected, "Select.rsp accepted");
                }
                else
                {
                    _logger.LogWarning("The peer refused Select with {Status}.", header.SelectStatus);
                }

                _pending.TryCompleteControl(header.SystemBytes, header);
                return false;

            case MessageType.DeselectRequest:
                frame.Dispose();
                if (_state == HsmsConnectionState.Selected)
                {
                    await SendControlAsync(session, MessageType.DeselectResponse, header.SystemBytes, (byte)DeselectStatus.Success, cancellationToken).ConfigureAwait(false);
                    linkTest.Stop();

                    // Back in NOT SELECTED, so T7 is running again: a peer that deselects and then
                    // goes quiet must not leave the link half-open. Armed before the state is
                    // published, as everywhere else.
                    t7.StartOnce(_options.T7);
                    SetState(HsmsConnectionState.Connected, "Deselect.req from the peer");
                }
                else
                {
                    await SendControlAsync(session, MessageType.DeselectResponse, header.SystemBytes, (byte)DeselectStatus.NotSelected, cancellationToken).ConfigureAwait(false);
                }

                return false;

            case MessageType.DeselectResponse:
            case MessageType.LinkTestResponse:
                frame.Dispose();
                _pending.TryCompleteControl(header.SystemBytes, header);
                return false;

            case MessageType.LinkTestRequest:
                frame.Dispose();
                await SendControlAsync(session, MessageType.LinkTestResponse, header.SystemBytes, 0, cancellationToken).ConfigureAwait(false);
                return false;

            case MessageType.RejectRequest:
                frame.Dispose();
                _logger.LogWarning("The peer rejected a message: {Reason}.", header.RejectReason);

                // Unblock whoever was waiting on the control transaction it rejected, rather than
                // leaving them to wait out T6 for an answer that has already arrived in another form.
                _pending.TryCompleteControl(header.SystemBytes, header);
                return false;

            case MessageType.SeparateRequest:
                frame.Dispose();
                _logger.LogInformation("The peer sent Separate.req; closing the link.");
                return true;

            case MessageType.DataMessage:
                await OnDataMessageAsync(session, frame, primaries, cancellationToken).ConfigureAwait(false);
                return false;

            default:
                frame.Dispose();
                _logger.LogWarning("An unknown SType {SType} arrived; rejecting it.", (byte)header.MessageType);
                await SendControlAsync(session, MessageType.RejectRequest, header.SystemBytes, (byte)RejectReason.StypeNotSupported, cancellationToken).ConfigureAwait(false);
                return false;
        }
    }

    private async ValueTask OnDataMessageAsync(
        Session session,
        HsmsMessage frame,
        ChannelWriter<Dispatch> primaries,
        CancellationToken cancellationToken)
    {
        var header = frame.Header;

        if (_state != HsmsConnectionState.Selected)
        {
            frame.Dispose();
            _logger.LogWarning("A data message arrived while {State}; rejecting it.", _state);
            await SendControlAsync(session, MessageType.RejectRequest, header.SystemBytes, (byte)RejectReason.EntityNotSelected, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (header.DeviceId != _options.DeviceId)
        {
            frame.Dispose();
            _logger.LogWarning(
                "A message arrived for device {Arrived} but this entity is {Expected}.",
                header.DeviceId,
                _options.DeviceId);

            if (_options.SendS9F1OnUnknownDeviceId)
            {
                await TryPostErrorReportAsync(session, S9Messages.UnrecognizedDeviceId(header)).ConfigureAwait(false);
            }

            return;
        }

        var message = frame.DetachData();
        frame.Dispose();

        if (message is null)
        {
            return;
        }

        // A secondary answers something this entity sent.
        if (!message.IsPrimary)
        {
            if (!_pending.TryCompleteSecondary(header.SystemBytes, message))
            {
                _logger.LogWarning(
                    "{Message} arrived with no open transaction; it is a reply that lost its race with T3.",
                    header);
                message.Dispose();
            }

            return;
        }

        if (_router.TryGetHandler(message.S, message.F, out var handler) || (handler = _router.CatchAll) is not null)
        {
            var transaction = new SecsTransaction(
                message,
                header,
                (secondary, replyHeader, token) => SendFrameAsync(session, replyHeader, secondary, token));

            // Blocks once the queue is full, which stops the pump, which stops reading, which is
            // TCP back pressure. An unbounded queue would turn a slow handler into unbounded
            // memory growth instead.
            await primaries.WriteAsync(new Dispatch(transaction, handler!), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Nobody claims it. Read what is needed before the message goes.
        var isErrorReport = S9Messages.IsErrorReport(message);
        var knownStream = _router.IsKnownStream(message.S);
        var identity = $"S{message.S}F{message.F}";
        message.Dispose();

        if (isErrorReport)
        {
            // Answering an error report with an error report builds a loop that saturates the link.
            _logger.LogWarning("The peer reported {Message}, which this entity does not handle.", identity);
            return;
        }

        if (!_options.SendS9F3F5OnUnhandledMessage)
        {
            _logger.LogWarning("{Message} has no handler and S9F3/F5 reporting is off; dropping it.", identity);
            return;
        }

        var report = knownStream
            ? S9Messages.UnrecognizedFunction(header)
            : S9Messages.UnrecognizedStream(header);

        _logger.LogWarning(
            "{Message} has no handler; reporting {Report}.",
            identity,
            knownStream ? "S9F5" : "S9F3");

        await TryPostErrorReportAsync(session, report).ConfigureAwait(false);
    }

    private async Task DispatchAsync(ChannelReader<Dispatch> primaries, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (transaction, handler) in primaries.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                using (transaction)
                {
                    try
                    {
                        await handler(transaction, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "The handler for {Transaction} threw.", transaction);
                    }

                    if (transaction.ReplyExpected && !transaction.IsReplied)
                    {
                        // Nothing automatic can fix this: only the handler knows what the right
                        // answer was. Said plainly here, because the symptom the peer sees is a
                        // T3 expiry several seconds later and a long way from the cause.
                        _logger.LogError(
                            "{Transaction} asked for a reply and its handler did not send one. The peer will wait out T3.",
                            transaction);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session is closing.
        }
    }

    // ----------------------------------------------------------------- control transactions

    private async Task SelectInBackgroundAsync(Session session, ChannelWriter<SessionEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            var response = await ControlTransactionAsync(session, MessageType.SelectRequest, cancellationToken).ConfigureAwait(false);

            if (response.SelectStatus != SelectStatus.Success)
            {
                events.TryWrite(new SessionEvent(
                    SessionEventKind.Fatal,
                    Error: new SecsException($"The peer refused Select with {response.SelectStatus}.")));
            }

            // The transition itself happened on the actor when the Select.rsp frame arrived; this
            // task only exists to enforce T6 and to report a refusal.
        }
        catch (OperationCanceledException)
        {
            // The session is closing.
        }
        catch (Exception ex)
        {
            events.TryWrite(new SessionEvent(SessionEventKind.Fatal, Error: ex));
        }
    }

    private async Task LinkTestInBackgroundAsync(Session session, ChannelWriter<SessionEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await ControlTransactionAsync(session, MessageType.LinkTestRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The session is closing.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Linktest failed; dropping the link.");
            events.TryWrite(new SessionEvent(SessionEventKind.Fatal, Error: ex));
        }
    }

    private async Task<MessageHeader> ControlTransactionAsync(
        Session session,
        MessageType request,
        CancellationToken cancellationToken)
    {
        var systemBytes = _pending.NextSystemBytes();
        var waiter = _pending.ExpectControlResponse(systemBytes);

        try
        {
            await SendControlAsync(session, request, systemBytes, 0, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.Forget(systemBytes);
            throw;
        }

        return await _pending
            .AwaitControlResponseAsync(waiter, systemBytes, _options.T6, cancellationToken)
            .ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- sending

    private MessageHeader DataHeader(SecsMessage message, int systemBytes) => new()
    {
        SessionId = _options.DeviceId,
        Stream = message.S,
        Function = message.F,
        ReplyExpected = message.ReplyExpected,
        PType = 0,
        MessageType = MessageType.DataMessage,
        SystemBytes = systemBytes,
    };

    private ValueTask SendControlAsync(
        Session session,
        MessageType type,
        int systemBytes,
        byte status,
        CancellationToken cancellationToken)
    {
        var header = new MessageHeader
        {
            // E37 uses 0xFFFF for control messages. The one exception is Select.rsp, where some
            // equipment echoes the session id it was selected with; echoing the request's is the
            // interoperable choice and costs nothing.
            SessionId = type is MessageType.SelectResponse or MessageType.DeselectResponse
                ? _options.DeviceId
                : (ushort)0xFFFF,
            Function = status,
            PType = 0,
            MessageType = type,
            SystemBytes = systemBytes,
        };

        return SendFrameAsync(session, header, data: null, cancellationToken);
    }

    private async ValueTask SendFrameAsync(
        Session session,
        MessageHeader header,
        SecsMessage? data,
        CancellationToken cancellationToken)
    {
        // One writer at a time: a PipeWriter cannot have two messages interleaved into it, and an
        // interleaved frame is a desynchronised link.
        await session.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _encoder
                .WriteAsync(session.Connection.Pipe.Output, header, data, cancellationToken)
                .ConfigureAwait(false);

            if (result.IsCompleted)
            {
                throw new SecsException($"The link closed before {header} could be sent.");
            }
        }
        finally
        {
            session.SendLock.Release();
        }
    }

    /// <summary>Sends an error report, swallowing a failure to send it.</summary>
    /// <remarks>
    /// An S9 message is already the handling of a failure. Letting it throw would replace a
    /// diagnosable problem with an undiagnosable one, so the report is best-effort and the
    /// original failure is the one that propagates.
    /// </remarks>
    private async ValueTask TryPostErrorReportAsync(Session session, SecsMessage report)
    {
        using var owned = report;

        try
        {
            await SendFrameAsync(session, DataHeader(owned, _pending.NextSystemBytes()), owned, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not send {Report}.", owned);
        }
    }

    // ----------------------------------------------------------------- failures

    private async ValueTask OnRecoverableFailureAsync(Session session, Exception error, CancellationToken cancellationToken)
    {
        if (error is SecsDecodeException { Header: { } header })
        {
            _logger.LogWarning(error, "A message failed to decode; reporting S9F7.");
            await TryPostErrorReportAsync(session, S9Messages.IllegalData(header)).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(error, "A recoverable protocol failure.");
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    private async ValueTask OnFatalFailureAsync(Session session, Exception error, CancellationToken cancellationToken)
    {
        switch (error)
        {
            case HsmsMessageTooLongException tooLong:
                _logger.LogError(
                    "The peer announced a {Length:N0}-byte message, past the {Limit:N0}-byte limit. "
                    + "Reporting S9F11 and dropping the link, which is no longer synchronised.",
                    tooLong.Length,
                    tooLong.Limit);

                await TryPostErrorReportAsync(session, S9Messages.DataTooLong(tooLong.Header ?? default)).ConfigureAwait(false);
                break;

            case SecsTimeoutException { Timer: SecsTimer.T8 } t8:
                _logger.LogError(
                    "T8 expired after {Interval}: a message began arriving and stalled. Dropping the link.",
                    t8.Interval);
                break;

            default:
                _logger.LogError(error, "The link failed.");
                break;
        }
    }

    // ----------------------------------------------------------------- plumbing

    private Session RequireSession()
    {
        var session = _session;
        return session is null || _state == HsmsConnectionState.NotConnected
            ? throw new SecsException("There is no link.")
            : session;
    }

    private Session RequireSelectedSession()
    {
        var session = _session;
        return session is null || _state != HsmsConnectionState.Selected
            ? throw new SecsException($"The connection is {_state}; data messages flow only when Selected.")
            : session;
    }

    private void StartLinkTest(EventTimer linkTest)
    {
        if (_options.LinkTestInterval > TimeSpan.Zero)
        {
            linkTest.StartPeriodic(_options.LinkTestInterval);
        }
    }

    private void SetState(HsmsConnectionState next, string reason)
    {
        var previous = _state;
        if (previous == next)
        {
            return;
        }

        _state = next;
        _logger.LogInformation("Connection state: {Previous} -> {Current} ({Reason})", previous, next, reason);

        try
        {
            StateChanged?.Invoke(this, new HsmsConnectionStateChangedEventArgs(previous, next, reason));
        }
        catch (Exception ex)
        {
            // A subscriber's exception is theirs. Letting it unwind here would abandon the actor
            // loop and take the link down with it.
            _logger.LogError(ex, "A {Event} handler threw.", nameof(StateChanged));
        }
    }

    private static async Task WhenAllQuietly(params Task[] tasks)
    {
        foreach (var task in tasks)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Both loops report through the logger already; this is only draining them.
            }
        }
    }

    private static void DrainAndDispose(ChannelReader<SessionEvent> events, ChannelReader<Dispatch> primaries)
    {
        // Whatever was queued when the session ended still owns pooled buffers.
        while (events.TryRead(out var pendingEvent))
        {
            pendingEvent.Frame?.Dispose();
        }

        while (primaries.TryRead(out var pendingDispatch))
        {
            pendingDispatch.Transaction.Dispose();
        }
    }

    private enum SessionEventKind
    {
        Frame,
        Recoverable,
        Fatal,
        Closed,
        T7Expired,
        LinkTestDue,
        LocalDeselected,
    }

    private readonly record struct SessionEvent(
        SessionEventKind Kind,
        HsmsMessage? Frame = null,
        Exception? Error = null);

    private readonly record struct Dispatch(SecsTransaction Transaction, SecsPrimaryHandler Handler);

    /// <summary>One live link and the things scoped to it.</summary>
    private sealed class Session(
        ISecsTransportConnection connection,
        HsmsPipeDecoder decoder,
        CancellationTokenSource cts,
        ChannelWriter<SessionEvent> events) : IDisposable
    {
        public ISecsTransportConnection Connection { get; } = connection;

        public HsmsPipeDecoder Decoder { get; } = decoder;

        public CancellationTokenSource Cts { get; } = cts;

        /// <summary>The actor's inbox, so a caller-thread operation can ask for a transition.</summary>
        public ChannelWriter<SessionEvent> Events { get; } = events;

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public void Dispose() => SendLock.Dispose();
    }

    /// <summary>A timer whose only job is to post an event to the actor.</summary>
    /// <remarks>
    /// The callback does nothing but a non-blocking <c>TryWrite</c>, which is the whole discipline:
    /// a timer that mutated state would be doing it on a thread pool thread, in the middle of
    /// whatever transition the actor was already running.
    /// </remarks>
    private sealed class EventTimer : IDisposable
    {
        private readonly ITimer _timer;

        public EventTimer(TimeProvider timeProvider, Action post)
            => _timer = timeProvider.CreateTimer(
                _ => post(),
                state: null,
                dueTime: Timeout.InfiniteTimeSpan,
                period: Timeout.InfiniteTimeSpan);

        public void StartOnce(TimeSpan due)
        {
            if (due > TimeSpan.Zero)
            {
                _timer.Change(due, Timeout.InfiniteTimeSpan);
            }
        }

        public void StartPeriodic(TimeSpan period)
        {
            if (period > TimeSpan.Zero)
            {
                _timer.Change(period, period);
            }
        }

        public void Stop() => _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public void Dispose() => _timer.Dispose();
    }
}
