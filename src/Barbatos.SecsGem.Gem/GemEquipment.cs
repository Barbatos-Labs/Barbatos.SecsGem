// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// The GEM (SEMI E30) equipment side: two state models, the standard message set, and the reporting
/// that hangs off them.
/// </summary>
/// <remarks>
/// <para>
/// This sits on an <see cref="HsmsConnection"/> and adds what E30 adds: the difference between a
/// link that carries bytes and two entities that have agreed to talk, and the difference between a
/// host that may watch and a host that may command.
/// </para>
/// <para>
/// Like the connection underneath it, this is an actor. Link transitions arrive on a channel and
/// are processed by one loop, because <see cref="HsmsConnection.StateChanged"/> is raised <em>on
/// the connection's own actor loop</em> - so establishing communications from inside that handler
/// would stall the link it is trying to establish communications over. Everything slow happens on
/// this object's loop instead.
/// </para>
/// <para>
/// Inbound messages are handled through <see cref="HandleAsync"/>, which the DI registration wires
/// to every stream and function in <see cref="HandledMessages"/>. They arrive on the connection's
/// dispatch loop, which is single-reader, so handlers never run concurrently with each other -
/// only with this object's own loop, which is why the mutable model is swapped rather than edited.
/// </para>
/// <para>
/// That one overlap is also the one place to be careful. A handler that <em>answers</em> from the
/// current state is fine, because only transitions write it. A handler that <em>transitions</em>
/// is not: an S1F13 from the host arriving while the equipment's own S1F13 is still in flight
/// would have two writers racing for the communication state, and the loser silently undoes the
/// winner. So the establish transition is posted to this object's loop and applied there, in
/// order, after whatever the loop was already doing. The control state is cheaper - a host asking
/// for off-line has to be answered with the state its request produced - so those transitions
/// happen in the handler, under a gate that keeps the field and the event consistent.
/// </para>
/// </remarks>
public sealed class GemEquipment : IAsyncDisposable
{
    private readonly GemOptions _options;
    private readonly HsmsConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly IGemProcessProgramStore? _programs;
    private readonly IGemFormattedProcessProgramStore? _formatted;
    private readonly IGemSpool? _spool;
    private readonly Channel<LinkEvent> _events =
        Channel.CreateUnbounded<LinkEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _gate = new();

    // Which alarms are in alarm right now, as opposed to which are allowed to report. S5F6 has to
    // answer the first question and S5F3 configures the second, so they cannot be the same set.
    private readonly ConcurrentDictionary<ulong, byte> _active = new();

    private GemSpoolState _spoolState;
    private int _spoolActual;
    private int _spoolTotal;
    private int _spoolDiscarded;
    private DateTimeOffset? _spoolStart;
    private DateTimeOffset? _spoolFull;

    private GemCommunicationState _communication;
    private GemControlState _control;
    private ITimer? _retry;
    private TimeSpan _clockOffset;
    private int _dataId;
    private int _running;
    private bool _disposed;

    /// <summary>Initialises the equipment over a connection.</summary>
    /// <param name="options">The GEM options.</param>
    /// <param name="model">What this equipment publishes.</param>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="timeProvider">The clock. <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <param name="processPrograms">
    /// Where process programs are kept. <see langword="null"/> means this equipment does not manage
    /// them, and stream 7 is not answered at all - see <see cref="ProcessProgramMessages"/>.
    /// </param>
    /// <param name="spool">
    /// Where reports wait for an absent host. <see langword="null"/>, or
    /// <see cref="GemOptions.SpoolingEnabled"/> left off, means a report that cannot be delivered is
    /// logged and dropped - see <see cref="SpoolMessages"/>.
    /// </param>
    /// <param name="formattedProcessPrograms">
    /// Where formatted process programs are kept. <see langword="null"/> means this equipment does
    /// not manage them, and S7F23 and S7F25 are not answered - see
    /// <see cref="FormattedProcessProgramMessages"/>. Independent of
    /// <paramref name="processPrograms"/>: equipment may offer either half of stream 7, both, or
    /// neither.
    /// </param>
    public GemEquipment(
        GemOptions options,
        GemEquipmentModel model,
        HsmsConnection connection,
        TimeProvider? timeProvider = null,
        ILogger<GemEquipment>? logger = null,
        IGemProcessProgramStore? processPrograms = null,
        IGemSpool? spool = null,
        IGemFormattedProcessProgramStore? formattedProcessPrograms = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GemEquipment>.Instance;
        _programs = processPrograms;
        _formatted = formattedProcessPrograms;
        _spool = spool;

        _communication = options.CommunicationEnabled
            ? GemCommunicationState.WaitingToRetry
            : GemCommunicationState.Disabled;
        _control = GemControlState.EquipmentOffline;
    }

    /// <summary>Every stream and function this object answers.</summary>
    /// <remarks>
    /// The routing table is also the declaration of what an entity understands, which is what lets
    /// the connection answer S9F5 for anything else without being told to. Registering this list is
    /// therefore not a convenience - it is how the equipment states its own capability.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (1, 1),    // Are You There
        (1, 3),    // Selected Equipment Status Request
        (1, 11),   // Status Variable Namelist Request
        (1, 13),   // Establish Communications Request
        (1, 15),   // Request Off-Line
        (1, 17),   // Request On-Line
        (2, 13),   // Equipment Constant Request
        (2, 15),   // New Equipment Constant Send
        (2, 17),   // Date and Time Request
        (2, 29),   // Equipment Constant Namelist Request
        (2, 31),   // Date and Time Set Request
        (2, 33),   // Define Report
        (2, 35),   // Link Event Report
        (2, 37),   // Enable/Disable Event Report
        (2, 41),   // Host Command Send
        (5, 3),    // Enable/Disable Alarm Send
        (5, 5),    // List Alarms Request
        (5, 7),    // List Enabled Alarm Request
        (6, 15),   // Event Report Request
        (6, 19),   // Individual Report Request
    ];

    /// <summary>The process program messages, answered only when a store is configured.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="HandledMessages"/> on purpose. Equipment that does not manage
    /// process programs should not declare stream 7 at all: the routing table is the declaration of
    /// what an entity understands, so leaving these out means the connection answers S9F3 - "I do
    /// not do stream 7" - which is both true and useful. Declaring them and then refusing every one
    /// would be neither.
    /// </para>
    /// <para>
    /// <c>AddGemProcessPrograms</c> registers these alongside the store, so the two cannot get out
    /// of step.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> ProcessProgramMessages { get; } =
    [
        (7, 1),    // Process Program Load Inquire
        (7, 3),    // Process Program Send
        (7, 5),    // Process Program Request
        (7, 17),   // Delete Process Program Send
        (7, 19),   // Current EPPD Request
    ];

    /// <summary>The formatted process program messages, answered only when a formatted store is configured.</summary>
    /// <remarks>
    /// <para>
    /// The other half of stream 7, and opt-in separately: a formatted process program is structure
    /// E5 defines - PPID, MDLN, SOFTREV and a list of CCODE commands - where an unformatted one is
    /// a block of bytes whose meaning is between the host and the machine. Equipment may offer
    /// either, both, or neither.
    /// </para>
    /// <para>
    /// S7F17 and S7F19 are not here, because they are not per-format and a pair may be declared
    /// only once: they belong to <see cref="ProcessProgramMessages"/>, and their handlers cover
    /// whichever stores are configured. Equipment that offers only formatted programs and wants the
    /// delete and directory messages registers both capabilities.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> FormattedProcessProgramMessages { get; } =
    [
        (7, 23),   // Formatted Process Program Send
        (7, 25),   // Formatted Process Program Request
    ];

    /// <summary>The spooling messages, answered only when a spool is configured and enabled.</summary>
    /// <remarks>
    /// Separate from <see cref="HandledMessages"/> for the same reason stream 7 is: spooling is a
    /// capability E30 has equipment declare, and equipment that does not spool should answer S9F5
    /// for S6F23 rather than RSDA 1. "I do not do that" and "no, not now" are different answers to
    /// a host deciding whether to wait.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> SpoolMessages { get; } =
    [
        (6, 23),   // Request Spooled Data
    ];

    /// <summary>Raised after the communication state changes.</summary>
    public event EventHandler<GemCommunicationStateChangedEventArgs>? CommunicationStateChanged;

    /// <summary>Raised after the control state changes.</summary>
    public event EventHandler<GemControlStateChangedEventArgs>? ControlStateChanged;

    /// <summary>Gets what this equipment publishes.</summary>
    public GemEquipmentModel Model { get; }

    /// <summary>Gets the communication state.</summary>
    public GemCommunicationState CommunicationState => _communication;

    /// <summary>Gets the control state.</summary>
    public GemControlState ControlState => _control;

    /// <summary>Gets a value indicating whether application messages may flow.</summary>
    public bool IsCommunicating => _communication == GemCommunicationState.Communicating;

    /// <summary>Gets a value indicating whether the host may send remote commands.</summary>
    public bool IsOnlineRemote => _control == GemControlState.OnlineRemote;

    /// <summary>Gets a value indicating whether an alarm is in alarm right now.</summary>
    /// <param name="alarmId">The ALID.</param>
    /// <returns><see langword="true"/> when set and not yet cleared.</returns>
    /// <remarks>
    /// Not the same question as <see cref="GemEquipmentModel.IsAlarmEnabled"/>. Enabled is whether
    /// the host wants to hear about it; this is whether it is happening. S5F6 reports the second in
    /// ALCD's top bit, so an implementation that only tracked the first would list every alarm as
    /// quiet while the chamber was out of range.
    /// </remarks>
    public bool IsAlarmSet(ulong alarmId) => _active.ContainsKey(alarmId);

    /// <summary>Gets a value indicating whether this equipment holds reports for an absent host.</summary>
    public bool IsSpooling => _spool is not null && _options.SpoolingEnabled;

    /// <summary>Gets the spooling state.</summary>
    /// <remarks>
    /// <see cref="GemSpoolState.Full"/> means the last attempt to hold a message was refused, or
    /// something had to be dropped to make room. It is defined that way rather than as "the count
    /// reached the capacity" because capacity belongs to the <see cref="IGemSpool"/>, and a custom
    /// one need not have a number to compare against.
    /// </remarks>
    public GemSpoolState SpoolState => _spoolState;

    /// <summary>Gets how many messages are held and not yet transmitted.</summary>
    /// <remarks>E30's SpoolCountActual, as this object has counted it.</remarks>
    public int SpoolCountActual => Volatile.Read(ref _spoolActual);

    /// <summary>Gets how many messages have been held since spooling last became active.</summary>
    /// <remarks>E30's SpoolCountTotal. Successful holds only; refusals are <see cref="SpoolCountDiscarded"/>.</remarks>
    public int SpoolCountTotal => Volatile.Read(ref _spoolTotal);

    /// <summary>Gets how many messages were lost because the spool was full.</summary>
    /// <remarks>
    /// E30 leaves this to be inferred from the difference between total and actual. Counting it
    /// outright is cheaper than asking a host to do arithmetic to find out whether it missed
    /// anything.
    /// </remarks>
    public int SpoolCountDiscarded => Volatile.Read(ref _spoolDiscarded);

    /// <summary>Gets when spooling last became active, or <see langword="null"/> when it is not.</summary>
    public DateTimeOffset? SpoolStartTime => _spoolStart;

    /// <summary>Gets when the spool last filled up, or <see langword="null"/> when it has not.</summary>
    public DateTimeOffset? SpoolFullTime => _spoolFull;

    /// <summary>Gets the time this equipment reports, including any offset a host has set.</summary>
    /// <remarks>
    /// Never the operating system clock. S2F31 moves this and nothing else, so a host that sets the
    /// clock changes what S2F18 answers and what timestamps reports carry, and changes nothing
    /// about the machine.
    /// </remarks>
    public DateTime Now => _timeProvider.GetUtcNow().UtcDateTime + _clockOffset;

    /// <summary>Drives the state models for as long as the token allows.</summary>
    /// <param name="cancellationToken">Stops the loop.</param>
    /// <returns>A task that completes when cancelled.</returns>
    /// <exception cref="InvalidOperationException">Already running.</exception>
    /// <remarks>
    /// Run this alongside <see cref="HsmsConnection.RunAsync"/>, not instead of it. The connection
    /// keeps the link up; this keeps E30's books.
    /// </remarks>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            throw new InvalidOperationException($"{nameof(RunAsync)} has already been called on this instance.");
        }

        _connection.StateChanged += OnConnectionStateChanged;

        try
        {
            // Before anything else: a spool that outlived the last process may already be holding
            // messages, and this is the only place that looks.
            await AdoptSpooledMessagesAsync(cancellationToken).ConfigureAwait(false);

            // The link may already be up: this object can be started after the connection.
            if (_connection.IsSelected)
            {
                _events.Writer.TryWrite(LinkEvent.Selected);
            }

            await foreach (var current in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (current)
                {
                    case LinkEvent.Selected:
                    case LinkEvent.RetryDue:
                        await EstablishAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    case LinkEvent.Deselected:
                        OnLinkLost();
                        break;

                    case LinkEvent.HostEstablished:
                        OnCommunicating("S1F13 from the host");
                        break;

                    case LinkEvent.TransmitSpool:
                        await TransmitSpoolAsync(cancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Asked to stop.
        }
        finally
        {
            _connection.StateChanged -= OnConnectionStateChanged;
            StopRetry();
        }
    }

    /// <summary>Reports a collection event, when it is enabled and there is somebody to tell.</summary>
    /// <param name="eventId">The CEID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when a report went out.</returns>
    /// <remarks>
    /// <para>
    /// Returns <see langword="false"/> rather than throwing when the event is disabled, unknown, or
    /// the link is not communicating. Every one of those is a normal condition - a host that has
    /// disabled an event has said it does not want it - and an exception would make ordinary
    /// operation look like a fault.
    /// </para>
    /// <para>
    /// <see langword="false"/> means nothing reached the host, and with spooling on it may still
    /// have been <em>kept</em>: a report held for an absent host has not gone out, so saying it had
    /// would be a lie. <see cref="SpoolCountActual"/> is what distinguishes kept from dropped. An
    /// event the host disabled is never spooled, because holding it would only deliver it later.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> TriggerEventAsync(uint eventId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Model.Events.ContainsKey(eventId) || !Model.IsEventEnabled(eventId))
        {
            // Unknown, or the host has said it does not want it. Never spooled either way: holding
            // a report the host disabled would only deliver it later.
            return false;
        }

        if (!IsCommunicating && !IsSpooling)
        {
            return false;
        }

        // Built now whichever way this goes, because the values belong to the moment the event
        // happened. A spool that read them when a host finally came back would report the chamber
        // as it is now and date it an hour ago.
        using var message = BuildEventReport(eventId);

        if (!IsCommunicating)
        {
            await SpoolAsync(message, cancellationToken).ConfigureAwait(false);
            return false;
        }

        await SendReportAsync(message, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Raises an alarm, and the collection event linked to its setting.</summary>
    /// <param name="alarmId">The ALID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when a report went out.</returns>
    public ValueTask<bool> SetAlarmAsync(ulong alarmId, CancellationToken cancellationToken = default)
        => AlarmAsync(alarmId, set: true, cancellationToken);

    /// <summary>Clears an alarm, and raises the collection event linked to its clearing.</summary>
    /// <param name="alarmId">The ALID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when a report went out.</returns>
    public ValueTask<bool> ClearAlarmAsync(ulong alarmId, CancellationToken cancellationToken = default)
        => AlarmAsync(alarmId, set: false, cancellationToken);

    /// <summary>Moves the equipment on-line by its own decision.</summary>
    /// <param name="remote">Whether control goes to the host rather than to the machine.</param>
    /// <returns><see langword="true"/> when the state changed.</returns>
    /// <remarks>
    /// Refused unless communication is established: E30's ATTEMPT ON-LINE exists because on-line
    /// without a host to be on-line to is not a state worth being in.
    /// </remarks>
    public bool GoOnline(bool remote = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCommunicating)
        {
            return false;
        }

        SetControl(GemControlState.AttemptOnline, "the equipment asked to go on-line");
        SetControl(
            remote ? GemControlState.OnlineRemote : GemControlState.OnlineLocal,
            "the equipment went on-line");

        return true;
    }

    /// <summary>Moves the equipment off-line by its own decision.</summary>
    /// <returns><see langword="true"/> when the state changed.</returns>
    public bool GoOffline()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_control == GemControlState.EquipmentOffline)
        {
            return false;
        }

        SetControl(GemControlState.EquipmentOffline, "the equipment went off-line");
        return true;
    }

    /// <summary>Handles one inbound primary. Wired to every pair in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The primary and the means to answer it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the reply has been sent.</returns>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var reply = (transaction.S, transaction.F) switch
        {
            (1, 1) => OnAreYouThere(),
            (1, 3) => OnStatusRequest(transaction.Message),
            (1, 11) => OnStatusNamelist(transaction.Message),
            (1, 13) => OnEstablishCommunications(),
            (1, 15) => OnRequestOffline(),
            (1, 17) => OnRequestOnline(),
            (2, 13) => OnConstantRequest(transaction.Message),
            (2, 15) => OnNewConstants(transaction.Message),
            (2, 17) => OnTimeRequest(),
            (2, 29) => OnConstantNamelist(transaction.Message),
            (2, 31) => OnTimeSet(transaction.Message),
            (2, 33) => OnDefineReport(transaction.Message),
            (2, 35) => OnLinkEventReport(transaction.Message),
            (2, 37) => OnEnableEvent(transaction.Message),
            (5, 3) => OnEnableAlarm(transaction.Message),
            (5, 5) => OnListAlarms(transaction.Message),
            (5, 7) => OnListEnabledAlarms(),
            (6, 15) => OnEventReportRequest(transaction.Message),
            (6, 19) => OnIndividualReportRequest(transaction.Message),
            _ => null,
        };

        // The handlers that have to await, because they run application code: a remote command, and
        // anything that touches the process program store.
        reply ??= (transaction.S, transaction.F) switch
        {
            (2, 41) => await OnHostCommandAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 1) => await OnProgramInquireAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 3) => await OnProgramSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 5) => await OnProgramRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 17) => await OnProgramDeleteAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 19) => await OnProgramListAsync(cancellationToken).ConfigureAwait(false),
            (7, 23) => await OnFormattedProgramSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 25) => await OnFormattedProgramRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (6, 23) => await OnRequestSpooledDataAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

        if (reply is null)
        {
            _logger.LogWarning("S{S}F{F} reached the GEM handler with nothing to answer it.", transaction.S, transaction.F);
            return;
        }

        using (reply)
        {
            if (transaction.ReplyExpected)
            {
                await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Stops the retry timer and releases what this object owns.</summary>
    /// <returns>A completed task.</returns>
    /// <remarks>The connection is not owned here, and is not disposed.</remarks>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _connection.StateChanged -= OnConnectionStateChanged;
        StopRetry();
        _events.Writer.TryComplete();

        return ValueTask.CompletedTask;
    }

    // ----------------------------------------------------------------- establishing

    private void OnConnectionStateChanged(object? sender, HsmsConnectionStateChangedEventArgs e)
    {
        // Raised on the connection's actor loop. Nothing slow may happen here, so the work is
        // posted to this object's own loop and the connection carries on.
        if (e.Current == HsmsConnectionState.Selected)
        {
            _events.Writer.TryWrite(LinkEvent.Selected);
        }
        else if (e.Previous == HsmsConnectionState.Selected)
        {
            _events.Writer.TryWrite(LinkEvent.Deselected);
        }
    }

    private async ValueTask EstablishAsync(CancellationToken cancellationToken)
    {
        StopRetry();

        if (!_options.CommunicationEnabled)
        {
            SetCommunication(GemCommunicationState.Disabled, "communication is disabled by configuration");
            return;
        }

        if (!_connection.IsSelected)
        {
            SetCommunication(GemCommunicationState.WaitingToRetry, "no link");
            return;
        }

        if (IsCommunicating)
        {
            return;
        }

        SetCommunication(GemCommunicationState.WaitingForAcknowledge, "S1F13 sent");

        try
        {
            using var request = new SecsMessage(1, 13, replyExpected: true)
            {
                Name = "EstablishCommunicationsRequest",
                Item = Identity(),
            };

            using var response = await _connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var commack = FirstByte(response.Item);

            if (commack == (byte)GemCommunicationAcknowledgement.Accepted)
            {
                OnCommunicating("S1F14 COMMACK=0");
                return;
            }

            _logger.LogInformation("The host denied S1F13 with COMMACK={Commack}; retrying in {Interval}.", commack, _options.EstablishCommunicationsRetryInterval);
            ArmRetry("the host denied S1F13");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecsException ex)
        {
            _logger.LogInformation(ex, "S1F13 did not complete; retrying in {Interval}.", _options.EstablishCommunicationsRetryInterval);
            ArmRetry("S1F13 did not complete");
        }
    }

    private void OnCommunicating(string reason)
    {
        // A second S1F13 on an established conversation changes nothing. E30 lets either side ask
        // at any time, and a host that asks again has not said anything about its report
        // definitions - so re-running this would reset the model underneath a host that had
        // already configured it, and flap the control state on the way through.
        if (IsCommunicating)
        {
            return;
        }

        if (_options.ResetLinkStateOnDisconnect)
        {
            // A host that has just reconnected has not re-sent its report definitions, and E30
            // never promised they survived. Starting from the configured defaults is the only
            // state both sides can agree on without asking.
            Model.Reset();
        }

        SetCommunication(GemCommunicationState.Communicating, reason);

        if (_options.DefaultControlState != GemControlState.EquipmentOffline)
        {
            GoOnline(_options.DefaultControlState == GemControlState.OnlineRemote);
        }
    }

    private void OnLinkLost()
    {
        StopRetry();
        SetCommunication(
            _options.CommunicationEnabled ? GemCommunicationState.WaitingToRetry : GemCommunicationState.Disabled,
            "the link is no longer selected");

        // Off-line, because there is nobody to be on-line to. Not HostOffline: the host did not
        // ask for this, and conflating the two loses the only information an operator has about why.
        SetControl(GemControlState.EquipmentOffline, "the link went down");
    }

    private void ArmRetry(string reason)
    {
        SetCommunication(GemCommunicationState.WaitingToRetry, reason);

        StopRetry();
        _retry = _timeProvider.CreateTimer(
            static state => ((GemEquipment)state!)._events.Writer.TryWrite(LinkEvent.RetryDue),
            this,
            _options.EstablishCommunicationsRetryInterval,
            Timeout.InfiniteTimeSpan);
    }

    private void StopRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }

    private void SetCommunication(GemCommunicationState state, string? reason)
    {
        GemCommunicationState previous;

        lock (_gate)
        {
            previous = _communication;

            if (previous == state)
            {
                return;
            }

            _communication = state;
        }

        _logger.LogInformation("Communication state {Previous} -> {Current} ({Reason}).", previous, state, reason);
        CommunicationStateChanged?.Invoke(this, new GemCommunicationStateChangedEventArgs(previous, state, reason));
    }

    private void SetControl(GemControlState state, string? reason)
    {
        GemControlState previous;

        lock (_gate)
        {
            previous = _control;

            if (previous == state)
            {
                return;
            }

            _control = state;
        }

        _logger.LogInformation("Control state {Previous} -> {Current} ({Reason}).", previous, state, reason);
        ControlStateChanged?.Invoke(this, new GemControlStateChangedEventArgs(previous, state, reason));
    }

    // ----------------------------------------------------------------- inbound, stream 1

    private SecsMessage OnAreYouThere() => new(1, 2, replyExpected: false)
    {
        Name = "OnLineData",

        // E30 is specific about this: off-line equipment answers S1F1 with a zero-length list
        // rather than its identity. It is how a host learns the machine is alive but not available
        // without having to ask a second question.
        Item = _control == GemControlState.EquipmentOffline || _control == GemControlState.HostOffline
            ? SecsItem.L()
            : Identity(),
    };

    private SecsMessage OnEstablishCommunications()
    {
        // Whether to accept depends only on configuration, so the answer can be given here. The
        // transition is posted to this object's loop instead of applied here, because this handler
        // runs on the connection's dispatch loop while the equipment's own S1F13 may still be in
        // flight on the GEM loop - and two writers to the communication state is exactly how an
        // established conversation gets overwritten with WaitingToRetry a moment later.
        var accepted = _options.CommunicationEnabled;

        if (accepted)
        {
            _events.Writer.TryWrite(LinkEvent.HostEstablished);
        }

        return new SecsMessage(1, 14, replyExpected: false)
        {
            Name = "EstablishCommunicationsRequestAcknowledge",
            Item = SecsItem.L(
                SecsItem.B(accepted
                    ? (byte)GemCommunicationAcknowledgement.Accepted
                    : (byte)GemCommunicationAcknowledgement.Denied).Named("COMMACK"),
                accepted ? Identity() : SecsItem.L()),
        };
    }

    private SecsMessage OnRequestOffline()
    {
        SetControl(GemControlState.HostOffline, "S1F15 from the host");

        return new SecsMessage(1, 16, replyExpected: false)
        {
            Name = "OffLineAcknowledge",
            Item = SecsItem.B((byte)GemOfflineAcknowledgement.Acknowledged).Named("OFLACK"),
        };
    }

    private SecsMessage OnRequestOnline()
    {
        GemOnlineAcknowledgement onlack;

        if (_control is GemControlState.OnlineLocal or GemControlState.OnlineRemote)
        {
            onlack = GemOnlineAcknowledgement.AlreadyOnline;
        }
        else if (!IsCommunicating || _control == GemControlState.EquipmentOffline)
        {
            // The equipment's own off-line is not the host's to overrule. E30 gives it ONLACK 1
            // for exactly this, and answering 0 would promise a transition that is not going to
            // happen.
            onlack = GemOnlineAcknowledgement.Refused;
        }
        else
        {
            GoOnline(_options.DefaultControlState != GemControlState.OnlineLocal);
            onlack = GemOnlineAcknowledgement.Accepted;
        }

        return new SecsMessage(1, 18, replyExpected: false)
        {
            Name = "OnLineAcknowledge",
            Item = SecsItem.B((byte)onlack).Named("ONLACK"),
        };
    }

    private SecsMessage OnStatusRequest(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var values = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var variable in Model.Variables.Values.Where(v => v.Kind == GemVariableKind.Status).OrderBy(v => v.Id))
            {
                values.Add(variable.Read().Named(variable.Name));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                // E5 says an unknown identifier is answered with a zero-length item in its place,
                // which keeps the reply positionally aligned with the request. Dropping it instead
                // would silently shift every later value by one.
                values.Add(
                    id <= uint.MaxValue
                    && Model.Variables.TryGetValue((uint)id, out var variable)
                    && variable.Kind == GemVariableKind.Status
                        ? variable.Read().Named(variable.Name)
                        : SecsItem.L());
            }
        }

        return new SecsMessage(1, 4, replyExpected: false)
        {
            Name = "SelectedEquipmentStatusData",
            Item = SecsItem.L(values),
        };
    }

    // ----------------------------------------------------------------- inbound, stream 2

    private SecsMessage OnConstantRequest(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var values = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var constant in Model.Constants.Values.OrderBy(c => c.Id))
            {
                values.Add(constant.Read().Named(constant.Name));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                values.Add(
                    id <= uint.MaxValue && Model.Constants.TryGetValue((uint)id, out var constant)
                        ? constant.Read().Named(constant.Name)
                        : SecsItem.L());
            }
        }

        return new SecsMessage(2, 14, replyExpected: false)
        {
            Name = "EquipmentConstantData",
            Item = SecsItem.L(values),
        };
    }

    private SecsMessage OnNewConstants(SecsMessage request)
    {
        var eac = GemEquipmentConstantAcknowledgement.Accepted;
        List<(GemEquipmentConstant Constant, SecsItem Value)> pending = [];

        if (request.Item is not { IsList: true } body)
        {
            eac = GemEquipmentConstantAcknowledgement.UnknownConstant;
        }
        else
        {
            foreach (var pair in body.Items)
            {
                if (!pair.IsList || pair.Count != 2 || !TryReadId(pair[0], out var id) || id > uint.MaxValue)
                {
                    eac = GemEquipmentConstantAcknowledgement.UnknownConstant;
                    break;
                }

                if (!Model.Constants.TryGetValue((uint)id, out var constant) || !constant.IsWritable)
                {
                    eac = GemEquipmentConstantAcknowledgement.UnknownConstant;
                    break;
                }

                pending.Add((constant, pair[1]));
            }
        }

        // Existence and writability are checked for the whole batch before anything is applied, so
        // EAC 1 means nothing changed. A value the equipment then refuses is EAC 3, and the writes
        // that already succeeded stand - E30 promises atomicity for neither, and pretending
        // otherwise would need a rollback only the application could implement.
        if (eac == GemEquipmentConstantAcknowledgement.Accepted)
        {
            foreach (var (constant, value) in pending)
            {
                if (!constant.Write!(value))
                {
                    eac = GemEquipmentConstantAcknowledgement.OutOfRange;
                    break;
                }
            }
        }

        return new SecsMessage(2, 16, replyExpected: false)
        {
            Name = "NewEquipmentConstantAcknowledge",
            Item = SecsItem.B((byte)eac).Named("EAC"),
        };
    }

    private SecsMessage OnTimeRequest() => new(2, 18, replyExpected: false)
    {
        Name = "DateAndTimeData",
        Item = SecsItem.A(Now.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture)).Named("TIME"),
    };

    private SecsMessage OnTimeSet(SecsMessage request)
    {
        var tiack = GemTimeAcknowledgement.Error;

        if (_options.AcceptHostClockSet
            && request.Item is { } item
            && item.Format.IsText()
            && TryParseTimestamp(item.GetString(), out var requested))
        {
            // An offset, not a clock change. See the remarks on GemOptions.AcceptHostClockSet.
            _clockOffset = requested - _timeProvider.GetUtcNow().UtcDateTime;
            tiack = GemTimeAcknowledgement.Accepted;
        }

        return new SecsMessage(2, 32, replyExpected: false)
        {
            Name = "DateAndTimeSetAcknowledge",
            Item = SecsItem.B((byte)tiack).Named("TIACK"),
        };
    }

    private SecsMessage OnDefineReport(SecsMessage request)
    {
        var drack = GemDefineReportAcknowledgement.InvalidFormat;

        if (TryReadDataIdList(request.Item, out var entries))
        {
            List<(uint, IReadOnlyList<uint>)> definitions = [];
            var wellFormed = true;

            foreach (var entry in entries)
            {
                if (!entry.IsList || entry.Count != 2 || !TryReadId(entry[0], out var reportId) || reportId > uint.MaxValue)
                {
                    wellFormed = false;
                    break;
                }

                definitions.Add(((uint)reportId, ReadIdList(entry[1]).Select(id => (uint)id).ToArray()));
            }

            if (wellFormed)
            {
                drack = Model.DefineReports(definitions);
            }
        }

        return new SecsMessage(2, 34, replyExpected: false)
        {
            Name = "DefineReportAcknowledge",
            Item = SecsItem.B((byte)drack).Named("DRACK"),
        };
    }

    private SecsMessage OnLinkEventReport(SecsMessage request)
    {
        var lrack = GemLinkReportAcknowledgement.InvalidFormat;

        if (TryReadDataIdList(request.Item, out var entries))
        {
            List<(uint, IReadOnlyList<uint>)> links = [];
            var wellFormed = true;

            foreach (var entry in entries)
            {
                if (!entry.IsList || entry.Count != 2 || !TryReadId(entry[0], out var eventId) || eventId > uint.MaxValue)
                {
                    wellFormed = false;
                    break;
                }

                links.Add(((uint)eventId, ReadIdList(entry[1]).Select(id => (uint)id).ToArray()));
            }

            if (wellFormed)
            {
                lrack = Model.LinkEventReports(links);
            }
        }

        return new SecsMessage(2, 36, replyExpected: false)
        {
            Name = "LinkEventReportAcknowledge",
            Item = SecsItem.B((byte)lrack).Named("LRACK"),
        };
    }

    private SecsMessage OnEnableEvent(SecsMessage request)
    {
        var erack = GemEnableEventAcknowledgement.UnknownEvent;

        if (request.Item is { IsList: true, Count: 2 } body)
        {
            var enabled = body[0].FirstValueOrDefault<bool>();
            var ids = ReadIdList(body[1]).Where(id => id <= uint.MaxValue).Select(id => (uint)id).ToArray();

            erack = ids.Length == ReadIdList(body[1]).Count
                ? Model.SetEventsEnabled(ids, enabled)
                : GemEnableEventAcknowledgement.UnknownEvent;
        }

        return new SecsMessage(2, 38, replyExpected: false)
        {
            Name = "EnableDisableEventReportAcknowledge",
            Item = SecsItem.B((byte)erack).Named("ERACK"),
        };
    }

    private async ValueTask<SecsMessage> OnHostCommandAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        var hcack = GemHostCommandAcknowledgement.InvalidCommand;

        if (request.Item is { IsList: true, Count: 2 } body && body[0].Format.IsText())
        {
            var name = body[0].GetString().TrimEnd();

            if (!Model.RemoteCommands.TryGetValue(name, out var command))
            {
                hcack = GemHostCommandAcknowledgement.InvalidCommand;
            }
            else if (command.RequiresRemoteControl && _control != GemControlState.OnlineRemote)
            {
                // The control state is the whole point of E30's control model: a command that
                // changes what the machine does is refused unless the host is actually in charge.
                hcack = GemHostCommandAcknowledgement.CannotPerformNow;
            }
            else
            {
                var parameters = ReadCommandParameters(body[1]);
                var context = new GemRemoteCommandContext(name, parameters);

                try
                {
                    hcack = await command.Handler(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A handler that throws is a bug in the application, not a reason to drop the
                    // link. The host is told the command could not be performed and the link
                    // carries on serving everything else.
                    _logger.LogError(ex, "The handler for remote command '{Command}' threw.", name);
                    hcack = GemHostCommandAcknowledgement.CannotPerformNow;
                }
            }
        }

        return new SecsMessage(2, 42, replyExpected: false)
        {
            Name = "HostCommandAcknowledge",
            Item = SecsItem.L(
                SecsItem.B((byte)hcack).Named("HCACK"),
                SecsItem.L()),
        };
    }

    // ----------------------------------------------------------------- inbound, stream 5

    private SecsMessage OnEnableAlarm(SecsMessage request)
    {
        var ackc5 = GemAlarmAcknowledgement.Error;

        if (request.Item is { IsList: true, Count: 2 } body)
        {
            // Both encodings, on purpose. E5 types ALED as B[1] with bit 8 meaning enable, while
            // this layer's own tests have always sent a BOOLEAN - and reading a binary item as
            // <bool> happens to work, because the typed accessor reinterprets the byte and 0x80 is
            // not zero. Happening to work is not the same as being read, and a host that sent the
            // conformant encoding deserves better than an accident.
            var enabled = body[0].Format == SecsFormat.Boolean
                ? body[0].FirstValueOrDefault<bool>()
                : (body[0].FirstValueOrDefault<byte>() & 0x80) != 0;

            // A zero-length ALID means "every alarm", and so does an explicit zero.
            var alarmId = body[1].Count == 0
                ? 0UL
                : TryReadId(body[1], out var id) ? id : ulong.MaxValue;

            if (alarmId != ulong.MaxValue)
            {
                ackc5 = Model.SetAlarmEnabled(alarmId, enabled);
            }
        }

        return new SecsMessage(5, 4, replyExpected: false)
        {
            Name = "EnableDisableAlarmAcknowledge",
            Item = SecsItem.B((byte)ackc5).Named("ACKC5"),
        };
    }

    // ----------------------------------------------------------------- inbound, stream 7

    private async ValueTask<SecsMessage?> OnProgramInquireAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_programs is null)
        {
            return null;
        }

        var grant = GemProcessProgramGrant.InvalidId;

        if (request.Item is { IsList: true, Count: 2 } body
            && body[0].Format.IsText()
            && TryReadId(body[1], out var length))
        {
            grant = await _programs
                .InquireAsync(body[0].GetString().TrimEnd(), (int)Math.Min(length, int.MaxValue), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SecsMessage(7, 2, replyExpected: false)
        {
            Name = "ProcessProgramLoadGrant",
            Item = SecsItem.B((byte)grant).Named("PPGNT"),
        };
    }

    private async ValueTask<SecsMessage?> OnProgramSendAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_programs is null)
        {
            return null;
        }

        // ACKC7 has no "the body made no sense" code, and LengthError is the closest honest answer
        // for one this stack could not read at all.
        var acknowledgement = GemProcessProgramAcknowledgement.LengthError;

        // The binary check is part of the guard, not a fallback to an empty body. Handing the store
        // `default` for a PPBODY that arrived as text would save a program of zero bytes and answer
        // ACKC7 0 - telling the host its recipe landed when what landed was nothing.
        if (request.Item is { IsList: true, Count: 2 } body
            && body[0].Format.IsText()
            && body[1].Format == SecsFormat.Binary)
        {
            // GetMemory rather than a copy: the store decides what to keep, and most of them will
            // write straight to disk. The memory is only valid for the length of this call, which
            // is why IGemProcessProgramStore says so.
            acknowledgement = await _programs
                .SaveAsync(body[0].GetString().TrimEnd(), body[1].GetMemory<byte>(), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SecsMessage(7, 4, replyExpected: false)
        {
            Name = "ProcessProgramAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC7"),
        };
    }

    private async ValueTask<SecsMessage?> OnProgramRequestAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_programs is null)
        {
            return null;
        }

        var id = request.Item is { } item && item.Format.IsText() ? item.GetString().TrimEnd() : string.Empty;
        var body = id.Length == 0
            ? null
            : await _programs.LoadAsync(id, cancellationToken).ConfigureAwait(false);

        return new SecsMessage(7, 6, replyExpected: false)
        {
            Name = "ProcessProgramData",

            // A zero-length list for "no such program". E5 has no acknowledgement code in S7F6 to
            // carry a refusal, so the empty body is the refusal.
            Item = body is null
                ? SecsItem.L()
                : SecsItem.L(
                    SecsItem.A(id).Named("PPID"),
                    SecsItem.B(body.Value.ToArray()).Named("PPBODY")),
        };
    }

    private async ValueTask<SecsMessage?> OnProgramDeleteAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        // Either store is enough. S7F17 is not per-format - a host deleting RECIPE-7 means the
        // program, not the flavour it was transferred in - so this answers as long as the equipment
        // keeps process programs at all.
        if (_programs is null && _formatted is null)
        {
            return null;
        }

        var ids = ReadTextList(request.Item);

        // An empty list means every program. E5 is explicit about that, and it is the one place
        // where answering the literal request would do far less than the host asked for.
        var acknowledgement = ids.Count == 0
            ? await DeleteEveryProgramAsync(cancellationToken).ConfigureAwait(false)
            : GemProcessProgramAcknowledgement.Accepted;

        foreach (var id in ids)
        {
            var result = await DeleteProgramAsync(id, cancellationToken).ConfigureAwait(false);

            if (result != GemProcessProgramAcknowledgement.Accepted)
            {
                acknowledgement = result;
            }
        }

        return new SecsMessage(7, 18, replyExpected: false)
        {
            Name = "DeleteProcessProgramAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC7"),
        };
    }

    private async ValueTask<SecsMessage?> OnProgramListAsync(CancellationToken cancellationToken)
    {
        if (_programs is null && _formatted is null)
        {
            return null;
        }

        // One directory covering both halves, in the order the stores gave them and without
        // repeating an identifier that exists in both: a host asking what is stored is asking about
        // programs, and the same PPID in both stores is one program as far as S7F20 is concerned.
        List<string> ids = [];
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (_programs is not null)
        {
            foreach (var id in await _programs.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        if (_formatted is not null)
        {
            foreach (var id in await _formatted.ListAsync(cancellationToken).ConfigureAwait(false))
            {
                if (seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        var entries = new List<SecsItem>(ids.Count);

        foreach (var id in ids)
        {
            entries.Add(SecsItem.A(id).Named("PPID"));
        }

        return new SecsMessage(7, 20, replyExpected: false)
        {
            Name = "CurrentEppdData",
            Item = SecsItem.L(entries),
        };
    }

    private async ValueTask<SecsMessage?> OnFormattedProgramSendAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_formatted is null)
        {
            return null;
        }

        var acknowledgement = GemFormattedProcessProgramCodec.TryRead(request.Item, out var program);

        if (program is not null)
        {
            acknowledgement = await _formatted.SaveAsync(program, cancellationToken).ConfigureAwait(false);
        }

        return new SecsMessage(7, 24, replyExpected: false)
        {
            Name = "FormattedProcessProgramAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC7"),
        };
    }

    private async ValueTask<SecsMessage?> OnFormattedProgramRequestAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_formatted is null)
        {
            return null;
        }

        var id = request.Item is { } item && item.Format.IsText() ? item.GetString().TrimEnd() : string.Empty;
        var program = id.Length == 0
            ? null
            : await _formatted.LoadAsync(id, cancellationToken).ConfigureAwait(false);

        return new SecsMessage(7, 26, replyExpected: false)
        {
            Name = "FormattedProcessProgramData",

            // A zero-length list for "no such program", the refusal S7F6 uses for the same reason:
            // S7F26 has no acknowledgement code either, so the empty body is the refusal.
            Item = program is null ? SecsItem.L() : GemFormattedProcessProgramCodec.Write(program),
        };
    }

    /// <summary>Deletes one identifier from every store this equipment has.</summary>
    /// <remarks>
    /// <para>
    /// Two stores, one ACKC7, so the merge rule has to be written down. A store that answered
    /// anything other than "done" or "no such program" - busy, or performed later - wins, because
    /// that is the answer the host has to act on and it stays true whatever the other half did:
    /// telling a host Accepted while one store is still working would have it believe the
    /// identifier is free.
    /// </para>
    /// <para>
    /// Failing that, deleted by either store is deleted, and <c>NotFound</c> holds only when no
    /// store had it. Answering NotFound because the other half did not recognise the name would be
    /// refusing something that just happened.
    /// </para>
    /// </remarks>
    private async ValueTask<GemProcessProgramAcknowledgement> DeleteProgramAsync(
        string id,
        CancellationToken cancellationToken)
    {
        var unformatted = _programs is null
            ? GemProcessProgramAcknowledgement.NotFound
            : await _programs.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        var formatted = _formatted is null
            ? GemProcessProgramAcknowledgement.NotFound
            : await _formatted.DeleteAsync(id, cancellationToken).ConfigureAwait(false);

        if (unformatted is not (GemProcessProgramAcknowledgement.Accepted or GemProcessProgramAcknowledgement.NotFound))
        {
            return unformatted;
        }

        if (formatted is not (GemProcessProgramAcknowledgement.Accepted or GemProcessProgramAcknowledgement.NotFound))
        {
            return formatted;
        }

        return unformatted == GemProcessProgramAcknowledgement.Accepted
            || formatted == GemProcessProgramAcknowledgement.Accepted
            ? GemProcessProgramAcknowledgement.Accepted
            : GemProcessProgramAcknowledgement.NotFound;
    }

    private async ValueTask<GemProcessProgramAcknowledgement> DeleteEveryProgramAsync(CancellationToken cancellationToken)
    {
        var acknowledgement = GemProcessProgramAcknowledgement.Accepted;

        if (_programs is not null)
        {
            acknowledgement = await _programs.DeleteAllAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_formatted is not null)
        {
            var formatted = await _formatted.DeleteAllAsync(cancellationToken).ConfigureAwait(false);

            if (formatted != GemProcessProgramAcknowledgement.Accepted)
            {
                acknowledgement = formatted;
            }
        }

        return acknowledgement;
    }

    /// <summary>Reads a list of text items, tolerating a bare one where a list was expected.</summary>
    private static List<string> ReadTextList(SecsItem? item)
    {
        List<string> values = [];

        if (item is null)
        {
            return values;
        }

        if (!item.IsList)
        {
            if (item.Format.IsText() && item.Count > 0)
            {
                values.Add(item.GetString().TrimEnd());
            }

            return values;
        }

        foreach (var element in item.Items)
        {
            if (element.Format.IsText() && element.Count > 0)
            {
                values.Add(element.GetString().TrimEnd());
            }
        }

        return values;
    }

    // ----------------------------------------------------------------- namelists and listings

    private SecsMessage OnStatusNamelist(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var entries = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var variable in Model.Variables.Values
                .Where(v => v.Kind == GemVariableKind.Status)
                .OrderBy(v => v.Id))
            {
                entries.Add(StatusNamelistEntry(variable.Id, variable));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                var known = id <= uint.MaxValue
                    && Model.Variables.TryGetValue((uint)id, out var variable)
                    && variable.Kind == GemVariableKind.Status;

                entries.Add(StatusNamelistEntry((uint)Math.Min(id, uint.MaxValue), known ? Model.Variables[(uint)id] : null));
            }
        }

        return new SecsMessage(1, 12, replyExpected: false)
        {
            Name = "StatusVariableNamelistReply",
            Item = SecsItem.L(entries),
        };
    }

    private SecsMessage OnConstantNamelist(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var entries = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var constant in Model.Constants.Values.OrderBy(c => c.Id))
            {
                entries.Add(ConstantNamelistEntry(constant.Id, constant));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                var known = id <= uint.MaxValue && Model.Constants.TryGetValue((uint)id, out _);
                entries.Add(ConstantNamelistEntry(
                    (uint)Math.Min(id, uint.MaxValue),
                    known ? Model.Constants[(uint)id] : null));
            }
        }

        return new SecsMessage(2, 30, replyExpected: false)
        {
            Name = "EquipmentConstantNamelist",
            Item = SecsItem.L(entries),
        };
    }

    private SecsMessage OnListAlarms(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var entries = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var alarm in Model.Alarms.Values.OrderBy(a => a.Id))
            {
                entries.Add(AlarmListEntry(alarm));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                entries.Add(Model.Alarms.TryGetValue(id, out var alarm) ? AlarmListEntry(alarm) : SecsItem.L());
            }
        }

        return new SecsMessage(5, 6, replyExpected: false)
        {
            Name = "ListAlarmsData",
            Item = SecsItem.L(entries),
        };
    }

    private SecsMessage OnListEnabledAlarms()
    {
        var entries = new List<SecsItem>();

        foreach (var alarm in Model.Alarms.Values.Where(a => Model.IsAlarmEnabled(a.Id)).OrderBy(a => a.Id))
        {
            entries.Add(AlarmListEntry(alarm));
        }

        // The same three-element shape as S5F6. E5 defines S5F8 as the enabled subset of the same
        // listing, and a host that can parse one should not need a second parser for the other -
        // the reference table this catalogue was built from lists only ALID here, which would make
        // the two replies gratuitously different.
        return new SecsMessage(5, 8, replyExpected: false)
        {
            Name = "ListEnabledAlarmData",
            Item = SecsItem.L(entries),
        };
    }

    private SecsMessage OnEventReportRequest(SecsMessage request)
    {
        var ids = ReadIdList(request.Item);
        var eventId = ids.Count > 0 && ids[0] <= uint.MaxValue ? (uint)ids[0] : 0u;

        // Answered even when the event is disabled. S2F37 governs what the equipment sends on its
        // own; a host asking directly has already said it wants this one now.
        return new SecsMessage(6, 16, replyExpected: false)
        {
            Name = "EventReportData",
            Item = Model.Events.ContainsKey(eventId) ? EventReportBody(eventId) : SecsItem.L(),
        };
    }

    private SecsMessage OnIndividualReportRequest(SecsMessage request)
    {
        var ids = ReadIdList(request.Item);
        var reportId = ids.Count > 0 && ids[0] <= uint.MaxValue ? (uint)ids[0] : 0u;
        var defined = Model.VariablesForReport(reportId).Count > 0;

        return new SecsMessage(6, 20, replyExpected: false)
        {
            Name = "IndividualReportData",
            Item = defined ? SecsItem.L(ReportValues(reportId)) : SecsItem.L(),
        };
    }

    private static SecsItem StatusNamelistEntry(uint id, GemVariable? variable) => SecsItem.L(
        SecsItem.U4(id).Named("SVID"),
        SecsItem.A(variable?.Name ?? string.Empty).Named("SVNAME"),
        SecsItem.A(variable?.Units ?? string.Empty).Named("UNITS"));

    private static SecsItem ConstantNamelistEntry(uint id, GemEquipmentConstant? constant) => SecsItem.L(
        SecsItem.U4(id).Named("ECID"),
        SecsItem.A(constant?.Name ?? string.Empty).Named("ECNAME"),
        Bound(constant?.Minimum).Named("ECMIN"),
        Bound(constant?.Maximum).Named("ECMAX"),
        Bound(constant?.Default).Named("ECDEF"),
        SecsItem.A(constant?.Units ?? string.Empty).Named("UNITS"));

    private SecsItem AlarmListEntry(GemAlarm alarm) => SecsItem.L(
        SecsItem.B((byte)(IsAlarmSet(alarm.Id) ? 0x80 : 0x00)).Named("ALCD"),
        AlarmIdItem(alarm.Id).Named("ALID"),
        SecsItem.A(AlarmText(alarm)).Named("ALTX"));

    /// <summary>A documented bound, or a zero-length item meaning there is not one.</summary>
    private static SecsItem Bound(Func<SecsItem>? read) => read is null ? SecsItem.L() : read();

    private static string AlarmText(GemAlarm alarm) => alarm.Text.Length > GemOptions.MaxAlarmTextLength
        ? alarm.Text[..GemOptions.MaxAlarmTextLength]
        : alarm.Text;

    // ----------------------------------------------------------------- spooling

    private async ValueTask<SecsMessage?> OnRequestSpooledDataAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_spool is null)
        {
            return null;
        }

        var acknowledgement = GemSpooledDataAcknowledgement.Denied;

        // RSDC is read tolerantly, like every other numeric field here. It matters more than usual
        // for this one: the reference table in Barbatos.SecsGem.Catalog flags S6F23 as a
        // representative subset and gives its body as DATAID, which is not what E30 says, so a
        // host built from one reading or the other should still be understood.
        if (request.Item is { } item && TryReadId(item, out var command))
        {
            switch (command)
            {
                case (ulong)GemSpooledDataRequest.Purge:
                    await _spool.PurgeAsync(cancellationToken).ConfigureAwait(false);
                    OnSpoolEmptied("the host purged the spool");
                    acknowledgement = GemSpooledDataAcknowledgement.Accepted;
                    break;

                case (ulong)GemSpooledDataRequest.Transmit when SpoolCountActual > 0:
                    // Acknowledged now, transmitted later. Sending a thousand messages inside this
                    // handler would hold the dispatch loop - and this very transaction - open well
                    // past T3, so the work goes to this object's own loop.
                    _events.Writer.TryWrite(LinkEvent.TransmitSpool);
                    acknowledgement = GemSpooledDataAcknowledgement.Accepted;
                    break;

                default:
                    // Transmit with nothing spooled, or a command E30 does not define. RSDA 1.
                    break;
            }
        }

        return new SecsMessage(6, 24, replyExpected: false)
        {
            Name = "RequestSpooledDataAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("RSDA"),
        };
    }

    /// <summary>Holds a message that could not be delivered. Does nothing when spooling is off.</summary>
    private async ValueTask<bool> SpoolAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!IsSpooling)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();

        if (_spoolState == GemSpoolState.Inactive)
        {
            // A new spooling episode, so the counters start again. E30's SpoolStartTime is what a
            // host uses to work out how much history it is being offered.
            _spoolStart = now;
            Volatile.Write(ref _spoolTotal, 0);
            Volatile.Write(ref _spoolDiscarded, 0);
        }

        var before = SpoolCountActual;
        bool accepted;
        int after;

        try
        {
            accepted = await _spool!
                .TryAppendAsync(GemSpooledMessage.From(message, now), cancellationToken)
                .ConfigureAwait(false);

            // The spool's own count rather than an increment of ours. Under DiscardOldest an append
            // succeeds while something else is dropped, so counting our own calls would drift from
            // what is actually held - and SpoolCountActual is a number a host acts on.
            after = await _spool.CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A durable spool is a filesystem, and a filesystem fails in ways memory does not: a
            // full disk, a revoked permission, a volume that went away. None of them is a reason to
            // take down the equipment, and none is a reason to tell the caller the report was kept.
            // From the host's point of view the spool cannot take any more, which is what Full
            // means and what SpoolFullTime records.
            _logger.LogError(
                exception,
                "S{S}F{F} could not be spooled; the spool is not accepting messages.",
                message.S,
                message.F);

            Interlocked.Increment(ref _spoolDiscarded);
            SetSpoolState(GemSpoolState.Full, "the spool could not be written");

            return false;
        }

        Volatile.Write(ref _spoolActual, after);

        if (accepted)
        {
            Interlocked.Increment(ref _spoolTotal);
        }

        if (!accepted || after <= before)
        {
            Interlocked.Increment(ref _spoolDiscarded);
            SetSpoolState(GemSpoolState.Full, "the spool is full");
        }
        else
        {
            SetSpoolState(GemSpoolState.Active, "a message was spooled");
        }

        return accepted;
    }

    private async ValueTask TransmitSpoolAsync(CancellationToken cancellationToken)
    {
        if (_spool is null)
        {
            return;
        }

        var budget = _options.MaxSpoolTransmit <= 0 ? int.MaxValue : _options.MaxSpoolTransmit;
        var sent = 0;

        while (sent < budget && IsCommunicating)
        {
            GemSpooledMessage? held;

            try
            {
                held = await _spool.TryPeekAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A durable spool reads from a disk, and a disk that cannot be read is no reason to
                // drop what is on it. Everything stays held and the next S6F23 tries again.
                _logger.LogError(
                    exception,
                    "The spool could not be read after {Sent} message(s); the rest are still held.",
                    sent);

                return;
            }

            if (held is null)
            {
                break;
            }

            SecsMessage message;

            try
            {
                message = held.ToMessage();
            }
            catch (SecsException exception)
            {
                // The body is not a decodable item tree. A spool cannot know that - it stores bytes
                // - so this is the equipment's to catch, and it is caught separately from the send
                // below on purpose: that one means the link failed and the message must stay, while
                // this one can never be sent at all. Leaving it at the head would stall every S6F23
                // from now on against the same record.
                _logger.LogError(exception, "A spooled {Held} could not be decoded and has been dropped.", held);

                if (!await TakeSpoolHeadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            using (message)
            {
                try
                {
                    if (message.ReplyExpected)
                    {
                        using var acknowledgement = await _connection
                            .SendAsync(message, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _connection.PostAsync(message, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (SecsException ex)
                {
                    // Peeked, not taken, so it is still at the head and still first. This is the
                    // whole reason IGemSpool separates peeking from removing: a transmit that fails
                    // half way must leave the rest in order, and a dequeue that has to put
                    // something back puts it at the wrong end.
                    _logger.LogWarning(
                        ex,
                        "Spool transmit stopped after {Sent} message(s); {Held} still held.",
                        sent,
                        SpoolCountActual);

                    return;
                }
            }

            if (!await TakeSpoolHeadAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            sent++;
        }

        if (SpoolCountActual > 0)
        {
            SetSpoolState(GemSpoolState.Active, "the spool was partly transmitted");
        }

        _logger.LogInformation(
            "Transmitted {Sent} spooled message(s); {Held} still held.",
            sent,
            SpoolCountActual);
    }

    /// <summary>Removes the head, having dealt with it, and brings the counters with it.</summary>
    /// <returns><see langword="false"/> when the spool could not be written and transmitting must stop.</returns>
    private async ValueTask<bool> TakeSpoolHeadAsync(CancellationToken cancellationToken)
    {
        int remaining;

        try
        {
            await _spool!.RemoveHeadAsync(cancellationToken).ConfigureAwait(false);
            remaining = await _spool.CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "The spool head could not be removed; transmitting has stopped.");
            return false;
        }

        Volatile.Write(ref _spoolActual, remaining);

        // The state follows the count immediately rather than after the loop. Otherwise the two
        // disagree for as long as the last send takes - count zero, state Active - and anything
        // watching both, a host reading status variables included, can see a spool that is empty
        // and active at the same time.
        if (remaining == 0)
        {
            OnSpoolEmptied("the spool was transmitted");
        }

        return true;
    }

    /// <summary>Takes on whatever a durable spool was already holding when this object was built.</summary>
    /// <remarks>
    /// <para>
    /// Nothing else would find them. S6F23 Transmit is gated on <see cref="SpoolCountActual"/>, so
    /// an equipment that started at zero over a spool holding a hundred messages would answer
    /// RSDA 1 - "there is nothing spooled" - while a hundred reports sat on the disk. That is the
    /// difference between a spool that is durable and a spool that merely writes files.
    /// </para>
    /// <para>
    /// <see cref="SpoolStartTime"/> comes from the oldest held message rather than from now,
    /// because E30 has a host read it to work out how much history it is being offered, and the
    /// restart is not when the history began. The two are the same number only for equipment that
    /// has never been restarted.
    /// </para>
    /// <para>
    /// Spooling switched off adopts nothing, held messages or not: equipment that is not spooling
    /// should not report a spool, and the records stay on the disk for a deployment that turns it
    /// back on.
    /// </para>
    /// </remarks>
    private async ValueTask AdoptSpooledMessagesAsync(CancellationToken cancellationToken)
    {
        if (!IsSpooling)
        {
            return;
        }

        GemSpooledMessage? oldest;
        int held;

        try
        {
            // Peeked before counting, because a durable spool may drop a record it cannot read as
            // it reads, and a count taken first would then be one too many.
            oldest = await _spool!.TryPeekAsync(cancellationToken).ConfigureAwait(false);

            if (oldest is null)
            {
                return;
            }

            held = await _spool.CountAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "The spool could not be read at start-up; starting with nothing held.");
            return;
        }

        if (held == 0)
        {
            return;
        }

        // The state and the start time before the count, so that anything watching sees a spool
        // that is consistent at every instant: a non-zero SpoolCountActual implies Active, never
        // the other way round for a moment. Writing the count first makes a host - or a test -
        // that polls the count able to read it as Active-with-nothing-held's opposite, which is a
        // state this equipment is never actually in.
        _spoolStart = oldest.SpooledAt;
        SetSpoolState(GemSpoolState.Active, $"{held} message(s) were already held at start-up");

        Volatile.Write(ref _spoolTotal, held);
        Volatile.Write(ref _spoolActual, held);
    }

    private void OnSpoolEmptied(string reason)
    {
        Volatile.Write(ref _spoolActual, 0);
        _spoolStart = null;
        SetSpoolState(GemSpoolState.Inactive, reason);
    }

    private void SetSpoolState(GemSpoolState state, string? reason)
    {
        if (_spoolState == state)
        {
            return;
        }

        if (state == GemSpoolState.Full)
        {
            _spoolFull = _timeProvider.GetUtcNow();
        }

        _logger.LogInformation("Spool state {Previous} -> {Current} ({Reason}).", _spoolState, state, reason);
        _spoolState = state;
    }

    // ----------------------------------------------------------------- outbound

    private async ValueTask<bool> AlarmAsync(ulong alarmId, bool set, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Model.Alarms.TryGetValue(alarmId, out var alarm))
        {
            return false;
        }

        // The machine is in alarm whether or not there is a host to tell, so the condition is
        // recorded before anything about reporting is decided. S5F6 asks what is wrong with the
        // machine, not what the machine managed to send.
        if (set)
        {
            _active[alarmId] = 0;
        }
        else
        {
            _active.TryRemove(alarmId, out _);
        }

        if (!IsCommunicating && !IsSpooling)
        {
            return false;
        }

        // Captured before anything is awaited, so the answer describes what this call did rather
        // than what the link happened to be doing by the time it finished.
        var delivered = IsCommunicating;

        if (Model.IsAlarmEnabled(alarmId))
        {
            // ALCD carries the alarm category in the low bits and "is set" in bit 7, which is why
            // a clear and a set are the same message with one bit between them.
            var alcd = (byte)(set ? 0x80 : 0x00);

            using var message = new SecsMessage(5, 1, replyExpected: _options.AlarmReportsExpectReply)
            {
                Name = "AlarmReportSend",
                Item = SecsItem.L(
                    SecsItem.B(alcd).Named("ALCD"),
                    AlarmIdItem(alarmId).Named("ALID"),
                    SecsItem.A(AlarmText(alarm)).Named("ALTX")),
            };

            if (delivered)
            {
                await SendReportAsync(message, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SpoolAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }

        // The linked collection event is reported whether or not the alarm itself is enabled: they
        // are two separate subscriptions, and a host that disabled S5F1 may still be watching the
        // event.
        var linked = set ? alarm.SetEventId : alarm.ClearEventId;

        if (linked != 0)
        {
            await TriggerEventAsync(linked, cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    private SecsMessage BuildEventReport(uint eventId)
        => new(6, 11, replyExpected: _options.EventReportsExpectReply)
        {
            Name = "EventReportSend",
            Item = EventReportBody(eventId),
        };

    /// <summary>The body S6F11 and S6F16 share: DATAID, CEID and the linked reports.</summary>
    private SecsItem EventReportBody(uint eventId)
    {
        var reports = new List<SecsItem>();

        foreach (var reportId in Model.ReportsForEvent(eventId))
        {
            reports.Add(SecsItem.L(
                SecsItem.U4(reportId).Named("RPTID"),
                SecsItem.L(ReportValues(reportId))));
        }

        return SecsItem.L(
            SecsItem.U4((uint)Interlocked.Increment(ref _dataId)).Named("DATAID"),
            SecsItem.U4(eventId).Named("CEID"),
            SecsItem.L(reports));
    }

    private List<SecsItem> ReportValues(uint reportId)
    {
        var values = new List<SecsItem>();

        foreach (var variableId in Model.VariablesForReport(reportId))
        {
            values.Add(Model.Variables.TryGetValue(variableId, out var variable)
                ? variable.Read().Named(variable.Name)
                : SecsItem.L());
        }

        return values;
    }

    private async ValueTask SendReportAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        try
        {
            if (message.ReplyExpected)
            {
                using var acknowledgement = await _connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
                var code = FirstByte(acknowledgement.Item);

                if (code != 0)
                {
                    _logger.LogWarning("The host answered S{S}F{F} with {Code}.", message.S, message.F + 1, code);
                }
            }
            else
            {
                await _connection.PostAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SecsException ex)
        {
            // A report that could not be delivered is not a reason to tear anything down. The
            // connection has its own view of whether the link is alive, and it is better informed
            // than this object is. What this does decide is whether the report is kept.
            if (await SpoolAsync(message, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogWarning(ex, "S{S}F{F} could not be delivered; it was spooled.", message.S, message.F);
            }
            else
            {
                _logger.LogWarning(ex, "S{S}F{F} could not be delivered.", message.S, message.F);
            }
        }
    }

    // ----------------------------------------------------------------- bodies

    private SecsItem Identity() => SecsItem.L(
        SecsItem.A(_options.ModelName).Named("MDLN"),
        SecsItem.A(_options.SoftwareRevision).Named("SOFTREV"));

    private static SecsItem AlarmIdItem(ulong alarmId) => GemItems.IdItem(alarmId);

    private static byte FirstByte(SecsItem? item)
    {
        if (item is null)
        {
            return 0xFF;
        }

        if (item.IsList)
        {
            return item.Count > 0 ? FirstByte(item[0]) : (byte)0xFF;
        }

        return item.TryGetSpan<byte>(out var bytes) && bytes.Length > 0 ? bytes[0] : (byte)0xFF;
    }

    private static Dictionary<string, SecsItem> ReadCommandParameters(SecsItem list)
    {
        var parameters = new Dictionary<string, SecsItem>(StringComparer.Ordinal);

        if (!list.IsList)
        {
            return parameters;
        }

        foreach (var pair in list.Items)
        {
            if (pair.IsList && pair.Count == 2 && pair[0].Format.IsText())
            {
                parameters[pair[0].GetString().TrimEnd()] = pair[1];
            }
        }

        return parameters;
    }

    /// <summary>Reads a list of identifiers, accepting whatever numeric format the host chose.</summary>
    /// <remarks>
    /// GEM says an identifier is "a number", not "a U4". Equipment that only accepts U4 works with
    /// the host it was tested against and fails with the next one, which is the single most common
    /// shape of SECS interop bug.
    /// </remarks>
    private static List<ulong> ReadIdList(SecsItem? item)
    {
        List<ulong> ids = [];

        if (item is null)
        {
            return ids;
        }

        if (!item.IsList)
        {
            // A bare value where a list was expected is one identifier. Tolerated, because hosts
            // send it and refusing achieves nothing.
            if (TryReadId(item, out var single))
            {
                ids.Add(single);
            }

            return ids;
        }

        foreach (var element in item.Items)
        {
            if (TryReadId(element, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool TryReadDataIdList(SecsItem? item, out ReadOnlySpan<SecsItem> entries)
    {
        entries = default;

        // <L[2] <DATAID> <L[n] ...>> - DATAID is echoed back by nothing here, so it is read and
        // ignored rather than validated.
        if (item is not { IsList: true, Count: 2 })
        {
            return false;
        }

        if (!item[1].IsList)
        {
            return false;
        }

        entries = item[1].Items;
        return true;
    }

    // Both of these moved to GemItems once there was a host in this assembly reading the same
    // fields from the other direction. Kept as forwarders so every call site here reads the same
    // as it did: the alternative was fifteen edits to prove nothing.
    private static bool TryReadId(SecsItem item, out ulong value) => GemItems.TryReadId(item, out value);

    private static bool TryParseTimestamp(string text, out DateTime value)
        => GemItems.TryParseTimestamp(text, out value);

    private enum LinkEvent
    {
        Selected,
        Deselected,
        RetryDue,
        HostEstablished,
        TransmitSpool,
    }
}
