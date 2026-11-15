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
    private readonly Channel<LinkEvent> _events =
        Channel.CreateUnbounded<LinkEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _gate = new();

    // Which alarms are in alarm right now, as opposed to which are allowed to report. S5F6 has to
    // answer the first question and S5F3 configures the second, so they cannot be the same set.
    private readonly ConcurrentDictionary<ulong, byte> _active = new();

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
    public GemEquipment(
        GemOptions options,
        GemEquipmentModel model,
        HsmsConnection connection,
        TimeProvider? timeProvider = null,
        ILogger<GemEquipment>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GemEquipment>.Instance;

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
    /// Returns <see langword="false"/> rather than throwing when the event is disabled, unknown, or
    /// the link is not communicating. Every one of those is a normal condition - a host that has
    /// disabled an event has said it does not want it - and an exception would make ordinary
    /// operation look like a fault.
    /// </remarks>
    public async ValueTask<bool> TriggerEventAsync(uint eventId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCommunicating || !Model.Events.ContainsKey(eventId) || !Model.IsEventEnabled(eventId))
        {
            return false;
        }

        using var message = BuildEventReport(eventId);
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

        if (reply is null && transaction.S == 2 && transaction.F == 41)
        {
            // The one handler that has to await: a remote command runs application code.
            reply = await OnHostCommandAsync(transaction.Message, cancellationToken).ConfigureAwait(false);
        }

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
            var enabled = body[0].FirstValueOrDefault<bool>();

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

        if (!IsCommunicating)
        {
            return false;
        }

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

            await SendReportAsync(message, cancellationToken).ConfigureAwait(false);
        }

        // The linked collection event is reported whether or not the alarm itself is enabled: they
        // are two separate subscriptions, and a host that disabled S5F1 may still be watching the
        // event.
        var linked = set ? alarm.SetEventId : alarm.ClearEventId;

        if (linked != 0)
        {
            await TriggerEventAsync(linked, cancellationToken).ConfigureAwait(false);
        }

        return true;
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
            // than this object is.
            _logger.LogWarning(ex, "S{S}F{F} could not be delivered.", message.S, message.F);
        }
    }

    // ----------------------------------------------------------------- bodies

    private SecsItem Identity() => SecsItem.L(
        SecsItem.A(_options.ModelName).Named("MDLN"),
        SecsItem.A(_options.SoftwareRevision).Named("SOFTREV"));

    private static SecsItem AlarmIdItem(ulong alarmId)
        => alarmId <= uint.MaxValue ? SecsItem.U4((uint)alarmId) : SecsItem.U8(alarmId);

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

    private static bool TryReadId(SecsItem item, out ulong value)
    {
        value = 0;

        if (item.Count == 0)
        {
            return false;
        }

        switch (item.Format)
        {
            case SecsFormat.U1:
                value = item.FirstValueOrDefault<byte>();
                return true;
            case SecsFormat.U2:
                value = item.FirstValueOrDefault<ushort>();
                return true;
            case SecsFormat.U4:
                value = item.FirstValueOrDefault<uint>();
                return true;
            case SecsFormat.U8:
                value = item.FirstValueOrDefault<ulong>();
                return true;
            case SecsFormat.I1:
                return NonNegative(item.FirstValueOrDefault<sbyte>(), out value);
            case SecsFormat.I2:
                return NonNegative(item.FirstValueOrDefault<short>(), out value);
            case SecsFormat.I4:
                return NonNegative(item.FirstValueOrDefault<int>(), out value);
            case SecsFormat.I8:
                return NonNegative(item.FirstValueOrDefault<long>(), out value);
            case SecsFormat.ASCII:
            case SecsFormat.JIS8:
                return ulong.TryParse(item.GetString().Trim(), CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }

        static bool NonNegative(long candidate, out ulong value)
        {
            value = candidate < 0 ? 0 : (ulong)candidate;
            return candidate >= 0;
        }
    }

    private static bool TryParseTimestamp(string text, out DateTime value)
    {
        var trimmed = text.Trim();
        string[] patterns = ["yyyyMMddHHmmssff", "yyyyMMddHHmmss", "yyMMddHHmmss"];

        foreach (var pattern in patterns)
        {
            if (DateTime.TryParseExact(trimmed, pattern, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }

    private enum LinkEvent
    {
        Selected,
        Deselected,
        RetryDue,
        HostEstablished,
    }
}
