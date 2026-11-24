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
    private readonly IGemTerminal? _terminal;
    private readonly IGemServiceProgramStore? _servicePrograms;

    // The SPID of the last accepted S2F1, because S2F3 carries no identifier of its own. A long
    // rather than a nullable uint so Interlocked can consume it: -1 means nothing is granted.
    private long _grantedServiceProgram = -1;

    // Which (stream, function) pairs a host has told this machine to stop spooling - S2F43. A set
    // rather than a list because SpoolAsync consults it on every held message, and empty is the
    // overwhelmingly common case.
    private readonly ConcurrentDictionary<(byte Stream, byte Function), bool> _spoolSuppressed = new();
    private readonly Channel<LinkEvent> _events =
        Channel.CreateUnbounded<LinkEvent>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _gate = new();

    // A trace is a timer and a part-filled group of samples. The timer only nudges the loop - the
    // sampling itself happens there, so reading a variable is serialised with every other thing
    // this object does rather than racing it from a timer thread.
    private readonly ConcurrentDictionary<uint, TraceRun> _traces = new();
    private readonly ConcurrentQueue<uint> _traceDue = new();

    // Which side of each band each variable was on, last time anybody looked. A crossing is a
    // change in this, which is why it is remembered rather than recomputed: the value alone cannot
    // say whether it has just arrived.
    private readonly ConcurrentDictionary<(uint Variable, uint Limit), bool> _zones = new();

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
    /// <param name="terminal">
    /// Where a message from the host reaches an operator. <see langword="null"/> means this
    /// equipment has no terminal, and stream 10 is not answered - see
    /// <see cref="TerminalMessages"/>.
    /// </param>
    /// <param name="servicePrograms">
    /// Where this equipment keeps its service programs. <see langword="null"/> means it has none,
    /// and S2F1 to S2F11 are not answered - see <see cref="ServiceProgramMessages"/>.
    /// </param>
    /// <remarks>
    /// Four optional capabilities now, each appended rather than grouped, because adding a
    /// parameter with a default is source-compatible and rearranging them is not. The next one
    /// wants a record of capabilities instead: five optional stores on a constructor is where the
    /// argument-at-a-time habit stops paying for itself, and <c>AddGemEquipment</c> is the only
    /// caller that has to know.
    /// </remarks>
    public GemEquipment(
        GemOptions options,
        GemEquipmentModel model,
        HsmsConnection connection,
        TimeProvider? timeProvider = null,
        ILogger<GemEquipment>? logger = null,
        IGemProcessProgramStore? processPrograms = null,
        IGemSpool? spool = null,
        IGemFormattedProcessProgramStore? formattedProcessPrograms = null,
        IGemTerminal? terminal = null,
        IGemServiceProgramStore? servicePrograms = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GemEquipment>.Instance;
        _programs = processPrograms;
        _formatted = formattedProcessPrograms;
        _spool = spool;
        _terminal = terminal;
        _servicePrograms = servicePrograms;

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
        (1, 5),    // Formatted Status Request
        (1, 7),    // Fixed Form Request
        (1, 11),   // Status Variable Namelist Request
        (1, 21),   // Data Variable Namelist Request
        (1, 23),   // Collection Event Namelist Request
        (2, 21),   // Remote Command - the one with no parameters
        (2, 25),   // Loopback Diagnostic Request
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
        (5, 17),   // Exception Recover Abort Request
        (6, 15),   // Event Report Request
        (6, 19),   // Individual Report Request
        (6, 7),    // Data Transfer Request
        (6, 17),   // Annotated Individual Report Request
        (6, 27),   // Notification Report Request
        (2, 27),   // Initiate Processing Request
        (2, 39),   // Multi-block Inquire - the host asking this end, where S6F5 is the reverse
    ];

    /// <summary>The service program messages, answered only when a store is configured.</summary>
    /// <remarks>
    /// <para>
    /// Opt-in for the reason <see cref="ProcessProgramMessages"/> is: the routing table is the
    /// declaration of what an entity understands, so equipment with no service programs leaves
    /// these out and the connection answers S9F5 - "I do not do those functions of stream 2" -
    /// which is true and useful where an <c>ACKC2</c> meaning "no" would be neither.
    /// </para>
    /// <para>
    /// <strong>S9F5 and not S9F3</strong>, which is the one way this differs from stream 7: stream
    /// 2 is a stream this equipment understands whatever it does with service programs, because
    /// S2F13 and the rest of GEM live there. So the refusal names the function, not the stream.
    /// </para>
    /// <para>
    /// <c>AddGemServicePrograms</c> registers these alongside the store, so the two cannot get out
    /// of step.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> ServiceProgramMessages { get; } =
    [
        (2, 1),    // Service Program Load Inquire
        (2, 3),    // Service Program Send
        (2, 5),    // Service Program Request
        (2, 7),    // Service Program Run Send
        (2, 9),    // Service Program Results Request
        (2, 11),   // Service Program Directory Request
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
        (7, 7),    // Process Program Directory Request
        (7, 9),    // Process Program Verify Send
        (7, 17),   // Delete Process Program Send
        (7, 19),   // Current EPPD Request
        (7, 31),   // Process Program Available Request
        (7, 37),   // Process Program Related Remote Command Send
        (7, 41),   // ... and the one with parameters
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
        (7, 27),   // Formatted Process Program Verify Send
        (7, 33),   // Formatted Process Program Load Inquire
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
        (2, 43),   // Reset Spooling Streams and Functions
    ];

    /// <summary>Every message this equipment can hold for a host that is not there.</summary>
    /// <remarks>
    /// <para>
    /// The other half of <see cref="SpoolMessages"/>: those are the messages a host sends
    /// <em>about</em> spooling, and these are the ones that get spooled. The list exists because
    /// S2F43 lets a host name a whole stream, and "every function of stream 5" has to mean
    /// something concrete - it means these.
    /// </para>
    /// <para>
    /// Everything here is news rather than a question, which is the rule that decides membership:
    /// a report, an alarm or an exception notice is worth hearing late, where a grant nobody is
    /// there to give is not permission and is never held. That is why S6F5 is absent and S6F9 is
    /// present even though both carry data.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> SpoolableMessages { get; } =
    [
        (5, 1),    // Alarm Report Send
        (5, 9),    // Exception Post Notify
        (5, 11),   // Exception Clear Notify
        (5, 13),   // Exception Recover Notify
        (5, 15),   // Exception Recover Complete Notify
        (6, 1),    // Trace Data Send
        (6, 3),    // Discrete Variable Data Send
        (6, 9),    // Unformatted Data Send
        (6, 11),   // Event Report Send
    ];

    /// <summary>Gets the messages a host has told this equipment to stop spooling - S2F43.</summary>
    /// <remarks>
    /// Empty until a host sends S2F43, which is the state almost every machine stays in. A pair in
    /// here is dropped rather than held when it cannot be delivered, and the drop is logged -
    /// because from the outside a suppressed report and a report nobody sent look identical.
    /// </remarks>
    public IReadOnlyCollection<(byte Stream, byte Function)> SpoolSuppressedMessages => [.. _spoolSuppressed.Keys];

    /// <summary>The terminal services messages, answered only when a terminal is configured.</summary>
    /// <remarks>
    /// <para>
    /// E30's terminal services capability, opt-in for the same reason the others are: a machine
    /// with no operator terminal should answer S9F3 for stream 10 rather than refuse each message
    /// with ACKC10 2. "I have no terminal" and "the terminal is not available" are different
    /// things, and only one of them is worth a host retrying.
    /// </para>
    /// <para>
    /// S10F1 - the operator typing something for the host - is not here. It is a primary this
    /// equipment <em>sends</em>, through <see cref="SendTerminalRequestAsync"/>, and this list is
    /// what it answers.
    /// </para>
    /// <para>
    /// Nor is S10F7, "terminal not accessible". It is the one entry in stream 10 that
    /// <c>Barbatos.SecsGem.Catalog</c> flags as representative rather than defined, it has no
    /// reply function, and ACKC10 2 on a display already says the same thing to the same host - so
    /// there is nothing to gain by guessing at its shape.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> TerminalMessages { get; } =
    [
        (10, 3),   // Terminal Display (Single)
        (10, 5),   // Multi-block Terminal Display
        (10, 9),   // Broadcast Message
    ];

    /// <summary>The trace data collection messages, answered only when traces are enabled.</summary>
    /// <remarks>
    /// <para>
    /// E30's trace data collection: the host names some status variables, an interval and a count,
    /// and the equipment samples on its own clock and reports the samples in groups. It is the
    /// answer to a host that wants a value every second without asking for it every second.
    /// </para>
    /// <para>
    /// Opt-in like the other capabilities, and the only one that needs nothing from the
    /// application to work - a trace samples variables the model already has. So the switch is
    /// <c>AddGemTraces</c> and nothing else: a machine that does not want to offer traces leaves
    /// it out and answers S9F5 for S2F23, rather than accepting a trace it will not run.
    /// </para>
    /// <para>
    /// S6F1 is the equipment's to send, so it is not here. It goes out through the same path as an
    /// event report, which means a trace group that cannot be delivered is spooled like any other
    /// report rather than lost.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> TraceMessages { get; } =
    [
        (2, 23),   // Trace Initialize Send
    ];

    /// <summary>The limits monitoring messages, answered only when limits are enabled.</summary>
    /// <remarks>
    /// <para>
    /// E30's other way of not polling: the host says which band it cares about, and the equipment
    /// reports a collection event when a variable leaves it or comes back. Opt-in like the rest,
    /// and needing nothing from the application beyond the event the model names - see
    /// <see cref="GemVariable.LimitEventId"/>.
    /// </para>
    /// <para>
    /// Three of the four messages in this capability are representative entries in this
    /// repository's catalogue rather than definitions, which is the one thing worth knowing before
    /// integrating against a particular host. <see cref="GemVariableLimit"/> carries the whole of
    /// that caveat, and the bodies live in one place for the same reason.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> LimitMessages { get; } =
    [
        (2, 45),   // Define Variable Limit Attributes
        (2, 47),   // Variable Limit Attribute Request
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

                    case LinkEvent.TraceDue:
                        await SampleTracesAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Posts an exception - S5F9, answered by S5F10.</summary>
    /// <param name="exceptionId">EXID, the name this machine gives the exception.</param>
    /// <param name="type">EXTYPE, how the machine classifies it.</param>
    /// <param name="message">EXMESSAGE, the text for a person to read.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when the post went out over a live link.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Not an alarm, and not modelled like one.</strong> An alarm is a condition: it has a
    /// numeric <c>ALID</c> declared in the model, a host can enable or disable it with S5F3, and
    /// S5F6 serves the set of alarms that are currently on. None of that applies here. The
    /// documented half of stream 5 has no exception namelist, no enable message and no way to ask
    /// what exceptions are outstanding - the three message pairs that would have done it, S5F11
    /// through S5F16, are rows the reference illustrates rather than defines. So this takes its
    /// three text fields as arguments instead of an identifier into a table, because there is no
    /// table for an identifier to point into.
    /// </para>
    /// <para>
    /// <strong>Spooled like a report</strong>, which is the one way it does behave like S5F1: an
    /// exception a machine could not deliver is exactly the kind of thing a host wants when it
    /// comes back, and nothing else will tell it. <see langword="false"/> therefore covers both
    /// kept and dropped, and <see cref="SpoolCountActual"/> is what separates them - the same
    /// contract <see cref="TriggerEventAsync"/> has.
    /// </para>
    /// <para>
    /// <c>TIMESTAMP</c> is this object's own clock, so a host that moved it with S2F31 moves this
    /// too. That is deliberate: the timestamps a machine reports should agree with each other and
    /// with the time it answers S2F18 with, whatever the operating system underneath thinks.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> PostExceptionAsync(
        string exceptionId,
        string type,
        string message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(exceptionId);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(message);

        if (!IsCommunicating && !IsSpooling)
        {
            return false;
        }

        var delivered = IsCommunicating;

        using var post = new SecsMessage(5, 9, replyExpected: _options.ExceptionPostsExpectReply)
        {
            Name = "ExceptionPostNotify",
            Item = SecsItem.L(
                SecsItem.A(Now.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture)).Named("TIMESTAMP"),
                SecsItem.A(exceptionId).Named("EXID"),
                SecsItem.A(type).Named("EXTYPE"),
                SecsItem.A(message).Named("EXMESSAGE")),
        };

        if (delivered)
        {
            await SendReportAsync(post, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SpoolAsync(post, cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    /// <summary>Reports that an event happened, with no values attached - S6F3, answered by S6F4.</summary>
    /// <param name="eventId">The CEID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when a report went out.</returns>
    /// <remarks>
    /// <para>
    /// <strong>What the reference says this row carries is <c>DATAID</c> and <c>CEID</c>.</strong>
    /// Nothing else - no report list, no values. That is worth stating plainly, because E5 as it
    /// is usually quoted has S6F3 carrying data values under <c>DVNAME</c> and <c>DVVAL</c>, and
    /// building that body here would be writing a message this reference does not describe. The
    /// check is not an opinion: the source behind the catalogue defines both of those items and
    /// then names neither of them in any of its 524 rows, so there is no row anywhere in it to
    /// read their nesting off. A gap the whole reference shares is a vocabulary it never wrote
    /// down, and those rows are left alone. <c>SecsCatalogStream6Tests</c> pins the measurement.
    /// </para>
    /// <para>
    /// So this is the bare notification: the host learns that event <paramref name="eventId"/>
    /// occurred and asks for what it wants with S6F15 or S6F19. Useful for an event whose values
    /// are expensive to gather, or one a host has linked no report to - which is also the thing it
    /// is most easily confused with, since <see cref="TriggerEventAsync"/> on an event with no
    /// linked reports sends an S6F11 whose report list is empty. The difference is the message,
    /// and a host reads it from <c>GemEventReportEventArgs.Function</c>.
    /// </para>
    /// <para>
    /// Gated on the same two conditions as <see cref="TriggerEventAsync"/> - the event has to be
    /// one this machine declares, and the host must not have disabled it. S2F37 disables a
    /// <em>CEID</em>, and this message names one, so routing around it with the shorter message
    /// would make the host's own instruction depend on which primary the machine happened to pick.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> SendDiscreteVariableDataAsync(
        uint eventId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!Model.Events.ContainsKey(eventId) || !Model.IsEventEnabled(eventId))
        {
            return false;
        }

        if (!IsCommunicating && !IsSpooling)
        {
            return false;
        }

        var delivered = IsCommunicating;

        using var message = new SecsMessage(6, 3, replyExpected: _options.EventReportsExpectReply)
        {
            Name = "DiscreteVariableDataSend",
            Item = SecsItem.L(
                SecsItem.U4((uint)Interlocked.Increment(ref _dataId)).Named("DATAID"),
                SecsItem.U4(eventId).Named("CEID")),
        };

        if (delivered)
        {
            await SendReportAsync(message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SpoolAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    /// <summary>Sends a block of data the host did not ask for - S6F9, answered by S6F10.</summary>
    /// <param name="data">V, as this machine wants it carried. <strong>Consumed by this call.</strong></param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when it went out, <see langword="false"/> when it was spooled or dropped.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Not an event report</strong>, and that is the whole reason the row exists: there is
    /// no <c>CEID</c> anywhere in it. S6F3 and S6F11 both say "event N happened"; this says "here
    /// is a block of data, filed under this number". Equipment with something to hand over that no
    /// collection event describes - a log an operator dumped, a trace the machine gathered on its
    /// own - has nowhere else in stream 6 to put it.
    /// </para>
    /// <para>
    /// <c>V</c> has no definition in the reference, so the item is carried exactly as it is given.
    /// That reading is not an invention here: S6F11 carries <c>V</c> as well and is one of the rows
    /// E5 illustrates in full, so the precedent is inside this very stream.
    /// </para>
    /// <para>
    /// <strong>Spooled like a report</strong>, because that is what it is to a host that was away:
    /// data it would have been given. The host answers with an <c>ACKC6</c> which goes to the log
    /// rather than to the caller, the same as every other report this machine sends - a spooled
    /// message has no answer yet, so a return value that sometimes carried one would be worse than
    /// none.
    /// </para>
    /// <para>
    /// The identifier is this machine's own counter, the one S6F3, S6F5 and S6F11 share. A host
    /// that wants the same block again asks for that number with S6F7.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> SendUnformattedDataAsync(
        SecsItem data,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(data);

        if (!IsCommunicating && !IsSpooling)
        {
            // Consumed either way, because the caller was told it would be. Leaving it alive on
            // this one path would make the contract depend on the link state.
            data.Dispose();
            return false;
        }

        var delivered = IsCommunicating;

        using var message = new SecsMessage(6, 9, replyExpected: true)
        {
            Name = "UnformattedDataSend",
            Item = SecsItem.L(
                SecsItem.U4((uint)Interlocked.Increment(ref _dataId)).Named("DATAID"),
                data),
        };

        if (delivered)
        {
            await SendReportAsync(message, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SpoolAsync(message, cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    /// <summary>Gets or sets what this machine answers a request to abort a recovery with - S5F18.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A delegate rather than a member on the model</strong>, and the reason is the one
    /// <c>PostExceptionAsync</c> gives: the documented half of stream 5 has no exception table for
    /// an identifier to point into, so there is nothing in the model for an abort to be declared
    /// against. It is a decision about a condition the machine is in right now, which is exactly
    /// what a delegate is for.
    /// </para>
    /// <para>
    /// <strong>Refuses by default</strong>, which is the opposite way round from
    /// <c>Barbatos.SecsGem.Material</c>'s answer delegates and deliberate. Those accept because a
    /// link that works before it is configured is useful and an offer of material costs nothing to
    /// take. This one stops something the machine is in the middle of doing: a machine that has
    /// not been told how to abort a recovery cannot abort one, and answering TRUE would tell a
    /// host the recovery had stopped when nothing stopped it. The refusal carries a reason saying
    /// so.
    /// </para>
    /// </remarks>
    public Func<string, GemExceptionAbortResult> AnswerExceptionAbort { get; set; }
        = static _ => GemExceptionAbortResult.Refuse(
            errorText: "this equipment has no handler for aborting an exception recovery");

    /// <summary>Answers S6F7 - the data this machine has filed under one DATAID, or nothing.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A delegate rather than a member on the model</strong>, for the reason
    /// <see cref="AnswerExceptionAbort"/> gives: a <c>DATAID</c> names a thing that existed for one
    /// transaction, so there is nothing in the model for it to point into. What the machine filed
    /// under 41 a minute ago is a fact about right now.
    /// </para>
    /// <para>
    /// <strong>Returns nothing by default</strong>, and a machine that answers nothing is answering
    /// honestly - S6F8 has no acknowledgement item, so an empty body is the only way the row can
    /// say "not here" and it is what a machine with no handler should say.
    /// </para>
    /// <para>
    /// <strong>The item returned is consumed</strong>: it goes into the reply, and the reply
    /// disposes it. Return a fresh item each time, the way <c>GemVariable.Read</c> does.
    /// </para>
    /// </remarks>
    public Func<uint, SecsItem?> AnswerDataTransfer { get; set; } = static _ => null;

    /// <summary>Answers S2F27 - whether this machine will start processing the named program.</summary>
    /// <remarks>
    /// <para>
    /// <strong>A delegate rather than a member on the model</strong>, for the reason
    /// <see cref="AnswerExceptionAbort"/> gives: whether this machine can start processing now is a
    /// fact about the machine at this moment, not a declaration it can publish in advance.
    /// </para>
    /// <para>
    /// <strong>Refuses by default</strong>, which is the same way round as
    /// <see cref="AnswerExceptionAbort"/> and the opposite of
    /// <see cref="AnswerMultiBlockInquiry"/>. Starting processing is the machine doing something: a
    /// machine that has not been told how to start a job cannot start one, and answering
    /// <c>PRACK</c> nought would tell a host that material was being processed when nothing was.
    /// </para>
    /// <para>
    /// The parameter is the <c>PPID</c>, trimmed. Equipment that manages process programs will
    /// want to check it against its own store, which is why this is not wired to one here - the
    /// row is declared whether or not stream 7 is.
    /// </para>
    /// </remarks>
    public Func<string, GemInitiateProcessingAcknowledgement> AnswerInitiateProcessing { get; set; }
        = static _ => GemInitiateProcessingAcknowledgement.Error;

    /// <summary>Answers S2F39 - whether a host may send a message too big for one block.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Grants by default</strong>, because a handshake whose only purpose is to let this
    /// end refuse should not refuse by silence. The same way round as
    /// <c>GemMultiBlockInquiryEventArgs.Grant</c>, which is this handshake in the other direction -
    /// S6F5 is the machine asking the host, and S2F39 is the host asking the machine.
    /// </para>
    /// <para>
    /// The parameters are <c>DATAID</c> and <c>DATALENGTH</c>, in whatever units the two ends have
    /// agreed <c>DATALENGTH</c> counts; the reference names none.
    /// </para>
    /// </remarks>
    public Func<uint, uint, GemGrant> AnswerMultiBlockInquiry { get; set; }
        = static (_, _) => GemGrant.Accepted;

    /// <summary>Says a posted exception has gone - S5F11, answered by S5F12.</summary>
    /// <param name="exceptionId">EXID, the identifier the post used.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when it went out over a live link.</returns>
    /// <remarks>
    /// The second of an exception's four stages, and the shortest body in the family: a
    /// <c>TIMESTAMP</c> and the <c>EXID</c>, with nothing to add. Spooled like the post, for the
    /// reason the post is - a host that was away wants to know a condition cleared as much as it
    /// wants to know it arrived, and nothing in stream 5 will tell it a second time.
    /// </remarks>
    public ValueTask<bool> ClearExceptionAsync(
        string exceptionId,
        CancellationToken cancellationToken = default)
        => NotifyExceptionAsync(GemExceptionStage.Cleared, exceptionId, string.Empty, cancellationToken);

    /// <summary>Says recovery from an exception has started - S5F13, answered by S5F14.</summary>
    /// <param name="exceptionId">EXID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when it went out over a live link.</returns>
    /// <remarks>
    /// The stage a host may interrupt: S5F17 is the only message in stream 5 a host sends about an
    /// exception, and what it asks is that this recovery stop. See
    /// <see cref="AnswerExceptionAbort"/>.
    /// </remarks>
    public ValueTask<bool> NotifyExceptionRecoveringAsync(
        string exceptionId,
        CancellationToken cancellationToken = default)
        => NotifyExceptionAsync(GemExceptionStage.Recovering, exceptionId, string.Empty, cancellationToken);

    /// <summary>Says recovery from an exception has finished - S5F15, answered by S5F16.</summary>
    /// <param name="exceptionId">EXID.</param>
    /// <param name="message">EXMESSAGE - how it finished, for a person to read.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when it went out over a live link.</returns>
    /// <remarks>
    /// The one recovery stage that carries an <c>EXMESSAGE</c>, which is the only place the
    /// outcome can be said: <c>ACKC5</c> on the reply says whether the host took the notice, not
    /// whether the recovery worked. A machine that recovered by giving up should say so here.
    /// </remarks>
    public ValueTask<bool> NotifyExceptionRecoveredAsync(
        string exceptionId,
        string message,
        CancellationToken cancellationToken = default)
        => NotifyExceptionAsync(GemExceptionStage.Recovered, exceptionId, message, cancellationToken);

    private async ValueTask<bool> NotifyExceptionAsync(
        GemExceptionStage stage,
        string exceptionId,
        string message,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(exceptionId);
        ArgumentNullException.ThrowIfNull(message);

        if (!IsCommunicating && !IsSpooling)
        {
            return false;
        }

        var delivered = IsCommunicating;
        var timestamp = SecsItem.A(Now.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture)).Named("TIMESTAMP");
        var id = SecsItem.A(exceptionId).Named("EXID");

        using var notice = new SecsMessage(5, (byte)stage, replyExpected: _options.ExceptionPostsExpectReply)
        {
            Name = stage switch
            {
                GemExceptionStage.Cleared => "ExceptionClearNotify",
                GemExceptionStage.Recovering => "ExceptionRecoverNotify",
                _ => "ExceptionRecoverCompleteNotify",
            },

            // Three items for the recovery and two for the other two, which is what the reference
            // gives them - so the body is built by stage rather than by always sending three and
            // leaving one empty. An EXMESSAGE nobody asked for is a field a reader has to ignore.
            Item = stage == GemExceptionStage.Recovered
                ? SecsItem.L(timestamp, id, SecsItem.A(message).Named("EXMESSAGE"))
                : SecsItem.L(timestamp, id),
        };

        if (delivered)
        {
            await SendReportAsync(notice, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SpoolAsync(notice, cancellationToken).ConfigureAwait(false);
        }

        return delivered;
    }

    /// <summary>Says a terminal cannot be reached - S10F7, which has no reply.</summary>
    /// <param name="terminalId">TID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when it went out.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Not <c>ACKC10</c> 2 by another route</strong>, which is the reason this row used to
    /// be left out and does not survive reading it. <c>ACKC10</c> 2 answers a display request: a
    /// host asked, and the machine says that terminal is not available. S10F7 is a <em>primary</em>
    /// with no reply at all, travelling either way - somebody volunteering that a terminal has gone
    /// before anybody tries it. A host that stops sending to terminal 2 on the strength of this
    /// never has to make the failing request.
    /// </para>
    /// <para>
    /// <strong>Not spooled</strong>, for the reason <see cref="SendTerminalRequestAsync"/> is not:
    /// stream 10 is about a person standing at a machine, and which terminals were unreachable an
    /// hour ago is not news anybody can act on. Reports and alarms are spooled because a host reads
    /// those as history.
    /// </para>
    /// <para>
    /// Needs no <c>IGemTerminal</c>, as the terminal request does not: this is the machine saying
    /// something about its own hardware, not answering for a screen.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> ReportTerminalNotAccessibleAsync(
        byte terminalId = 0,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCommunicating)
        {
            return false;
        }

        using var message = new SecsMessage(10, 7, replyExpected: false)
        {
            Name = "TerminalNotAccessible",
            Item = SecsItem.B(terminalId).Named("TID"),
        };

        try
        {
            await _connection.PostAsync(message, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SecsException ex)
        {
            _logger.LogWarning(ex, "S10F7 could not be delivered.");
            return false;
        }
    }

    /// <summary>Tells the host a process program here has changed - S7F29, answered by S7F30.</summary>
    /// <param name="programId">The PPID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The ACKC7 the host answered with, or <see langword="null"/> when there was nobody to tell.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The only thing in stream 7 the machine says first.</strong> Everything else here is
    /// the host asking, so without this a host that did not make a change has no way to learn of
    /// one - an operator editing a recipe at the machine is invisible until somebody asks S7F19.
    /// </para>
    /// <para>
    /// <strong>Not spooled</strong>, which separates it from a report or an alarm. A held
    /// "RECIPE-7 changed" would reach a host that can get something strictly better by asking:
    /// S7F7 or S7F19 answers with the directory as it is now, where the spooled notice says only
    /// that something was true an hour ago. Reports and alarms are spooled because a host reads
    /// those as history; this is a prompt to look, and a stale prompt is worse than none.
    /// </para>
    /// <para>
    /// The host's ACKC7 is handed back rather than swallowed, because a host answering anything
    /// other than <c>Accepted</c> is saying it did not take the notice - and the machine is the
    /// only place that can be logged.
    /// </para>
    /// </remarks>
    public ValueTask<GemProcessProgramAcknowledgement?> NotifyProgramChangedAsync(
        string programId,
        CancellationToken cancellationToken = default)
        => NotifyProgramAsync(programId, 29, cancellationToken);

    /// <summary>Tells the host which process program this machine has selected - S7F39, answered by S7F40.</summary>
    /// <param name="programId">The PPID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>The ACKC7 the host answered with, or <see langword="null"/> when there was nobody to tell.</returns>
    /// <remarks>
    /// S7F29's sibling and not its synonym: a program changing is about the store, and a program
    /// being selected is about what the machine is going to run next. The same body and the same
    /// acknowledgement, so they share an implementation, and the same refusal to spool for the same
    /// reason - what is selected now is a question a host can ask.
    /// </remarks>
    public ValueTask<GemProcessProgramAcknowledgement?> NotifyProgramSelectedAsync(
        string programId,
        CancellationToken cancellationToken = default)
        => NotifyProgramAsync(programId, 39, cancellationToken);

    private async ValueTask<GemProcessProgramAcknowledgement?> NotifyProgramAsync(
        string programId,
        byte function,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(programId);

        if (!IsCommunicating)
        {
            return null;
        }

        using var notify = new SecsMessage(7, function, replyExpected: true)
        {
            Name = function == 29 ? "ProcessProgramChangeNotify" : "ProcessProgramSelectedNotify",
            Item = SecsItem.A(programId).Named("PPID"),
        };

        try
        {
            using var reply = await _connection.SendAsync(notify, cancellationToken).ConfigureAwait(false);

            if (!GemItems.TryReadCode(reply.Item, out var code))
            {
                _logger.LogWarning("S7F{Function} carried no ACKC7.", function + 1);
                return null;
            }

            if (code < (byte)GemProcessProgramAcknowledgement.Other)
            {
                return (GemProcessProgramAcknowledgement)code;
            }

            _logger.LogInformation(
                "S7F{Function} answered ACKC7 {Code}, which the table reserves and does not name.",
                function + 1,
                code);

            return GemProcessProgramAcknowledgement.Other;
        }
        catch (SecsException ex)
        {
            // A notice nobody took is not a reason to tear anything down, and it is not spooled -
            // the host can ask for the directory whenever it likes.
            _logger.LogWarning(ex, "S7F{Function} could not be delivered.", function);
            return null;
        }
    }

    /// <summary>Asks the host to accept something too large for one block - S6F5, answered by S6F6.</summary>
    /// <param name="length">DATALENGTH, in whatever this machine and its host have agreed it counts.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The GRANT6 the host answered with.</returns>
    /// <remarks>
    /// <para>
    /// <strong>A question, so it is never spooled.</strong> Every other outbound message here can
    /// be held for an absent host, because it is news that keeps. This one is a request for
    /// permission, and permission nobody is there to give is not permission - a grant delivered an
    /// hour later would answer a send that has already been abandoned. With no link, this answers
    /// <see cref="GemMultiBlockGrant.NoSpace"/>: the table has no code for "nobody was listening",
    /// and of the two refusals it does have, <see cref="GemMultiBlockGrant.Busy"/> would invite an
    /// immediate retry against the same absent host.
    /// </para>
    /// <para>
    /// <strong>The decision is the caller's, not this layer's.</strong> Nothing here sends the
    /// large message afterwards, and nothing here measures one to decide whether it needs asking
    /// about. E5 leaves both to the machine: how many bytes fit in a block is a property of the
    /// link, and whether a given report is worth the extra round trip is a property of the
    /// application. An application that wires this into its own reporting checks the grant and
    /// then calls <see cref="TriggerEventAsync"/>.
    /// </para>
    /// <para>
    /// <c>DATALENGTH</c> is passed through as the number the caller gave. The reference types it
    /// as a <c>U4</c> and says nothing about its units, so this does not name them either - the
    /// same reading stream 21's <c>LENGTH</c> got.
    /// </para>
    /// </remarks>
    public async ValueTask<GemMultiBlockGrant> InquireMultiBlockAsync(
        uint length,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCommunicating)
        {
            _logger.LogInformation(
                "S6F5 was not sent: the link is not communicating, so there is nobody to grant it.");

            return GemMultiBlockGrant.NoSpace;
        }

        using var inquire = new SecsMessage(6, 5, replyExpected: true)
        {
            Name = "MultiBlockDataSendInquire",
            Item = SecsItem.L(
                SecsItem.U4((uint)Interlocked.Increment(ref _dataId)).Named("DATAID"),
                SecsItem.U4(length).Named("DATALENGTH")),
        };

        using var reply = await _connection.SendAsync(inquire, cancellationToken).ConfigureAwait(false);

        if (!GemItems.TryReadCode(reply.Item, out var code))
        {
            _logger.LogWarning("S6F6 carried no GRANT6; the send does not go ahead.");
            return GemMultiBlockGrant.NoSpace;
        }

        if (code < (byte)GemMultiBlockGrant.Other)
        {
            return (GemMultiBlockGrant)code;
        }

        _logger.LogInformation(
            "S6F6 answered GRANT6 {Code}, which the table reserves and does not name.",
            code);

        return GemMultiBlockGrant.Other;
    }

    /// <summary>Checks a variable against the limits the host asked for, and reports a crossing.</summary>
    /// <param name="variableId">The SVID.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> when a crossing was found and a report went out.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Called by the application, after a value changes.</strong> Nothing in this layer
    /// knows when a chamber's pressure moves - a variable is a delegate it reads on demand - so
    /// the alternative was a timer on every monitored variable with a period nobody asked for.
    /// A machine that already raises events when it does things has the right moment to hand.
    /// </para>
    /// <para>
    /// A crossing is a <em>change</em> of side: the first check on a band records which side the
    /// value is on and reports nothing, because there is nothing to have crossed from. A value
    /// that wobbles inside the band is silent, which is what makes the band a deadband.
    /// </para>
    /// <para>
    /// <see langword="false"/> covers everything that is not a reported crossing: no limits
    /// defined, nothing crossed, a value that is not a number, no
    /// <see cref="GemVariable.LimitEventId"/> documented for the variable, or nobody to tell. The
    /// crossing is still recorded in every one of those cases, so the next one is measured from
    /// here.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> CheckLimitsAsync(uint variableId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var limits = Model.LimitsFor(variableId);

        if (limits.Count == 0 || !Model.Variables.TryGetValue(variableId, out var variable))
        {
            return false;
        }

        double value;

        using (var item = variable.Read())
        {
            value = GemLimitCodec.ReadNumber(item);
        }

        if (double.IsNaN(value))
        {
            // A band cannot contain a string. A host that put a limit on a text variable gets no
            // events rather than a wrong one.
            return false;
        }

        var crossed = false;

        foreach (var limit in limits)
        {
            var inside = limit.Contains(value);
            var key = (variableId, limit.Id);
            var known = _zones.TryGetValue(key, out var was);

            _zones[key] = inside;

            if (known && was != inside)
            {
                crossed = true;
            }
        }

        if (!crossed || variable.LimitEventId is not { } eventId)
        {
            return false;
        }

        return await TriggerEventAsync(eventId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends the host something an operator typed - S10F1.</summary>
    /// <param name="text">TEXT, as the operator wrote it.</param>
    /// <param name="terminalId">TID. Zero is the usual value for equipment with one terminal.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>
    /// The host's ACKC10, or <see langword="null"/> when there was nobody to tell.
    /// </returns>
    /// <exception cref="SecsException">S10F2 came back without an acknowledgement code.</exception>
    /// <remarks>
    /// <para>
    /// The other direction of terminal services, and the application's to start: nothing calls
    /// into an operator, so this is a method rather than something on
    /// <see cref="IGemTerminal"/>. It needs no terminal configured - that interface is for the
    /// messages coming the other way.
    /// </para>
    /// <para>
    /// <strong>Not spooled.</strong> A message from an operator is a prompt for a person at the
    /// host, and handing it over an hour later when both of them have gone home is worse than
    /// saying it did not go: <see langword="null"/> lets the caller tell the operator the host is
    /// not listening, which is the thing they can act on. Reports and alarms are spooled because a
    /// host reads those as history.
    /// </para>
    /// </remarks>
    public async ValueTask<GemTerminalAcknowledgement?> SendTerminalRequestAsync(
        string text,
        byte terminalId = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCommunicating)
        {
            return null;
        }

        using var message = new SecsMessage(10, 1, replyExpected: true)
        {
            Name = "TerminalRequest",
            Item = SecsItem.L(
                SecsItem.B(terminalId).Named("TID"),
                SecsItem.A(text).Named("TEXT")),
        };

        using var reply = await _connection.SendAsync(message, cancellationToken).ConfigureAwait(false);

        if (!GemItems.TryReadCode(reply.Item, out var code))
        {
            throw new SecsException("S10F2 came back without the ACKC10 E5 defines for it.");
        }

        return (GemTerminalAcknowledgement)code;
    }

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

        // S6F23 starts work that puts frames on the link, and those frames must follow its answer,
        // so it is answered in a method of its own: the switches below build the reply and send it
        // as their last act, which leaves no place for something that has to come after it.
        if (transaction.S == 6 && transaction.F == 23)
        {
            await HandleSpooledDataRequestAsync(transaction, cancellationToken).ConfigureAwait(false);
            return;
        }

        var reply = (transaction.S, transaction.F) switch
        {
            (1, 1) => OnAreYouThere(),
            (1, 3) => OnStatusRequest(transaction.Message),
            (1, 5) => OnStatusForm(transaction.Message, 6),
            (1, 7) => OnStatusForm(transaction.Message, 8),
            (1, 11) => OnStatusNamelist(transaction.Message),
            (1, 21) => OnDataVariableNamelist(transaction.Message),
            (1, 23) => OnCollectionEventNamelist(transaction.Message),
            (2, 25) => OnLoopback(transaction.Message),
            (1, 13) => OnEstablishCommunications(),
            (1, 15) => OnRequestOffline(),
            (1, 17) => OnRequestOnline(),
            (2, 13) => OnConstantRequest(transaction.Message),
            (2, 15) => OnNewConstants(transaction.Message),
            (2, 17) => OnTimeRequest(),
            (2, 29) => OnConstantNamelist(transaction.Message),
            (2, 23) => OnTraceInitialize(transaction.Message),
            (2, 45) => OnDefineLimits(transaction.Message),
            (2, 47) => OnLimitRequest(transaction.Message),
            (2, 31) => OnTimeSet(transaction.Message),
            (2, 33) => OnDefineReport(transaction.Message),
            (2, 35) => OnLinkEventReport(transaction.Message),
            (2, 37) => OnEnableEvent(transaction.Message),
            (5, 3) => OnEnableAlarm(transaction.Message),
            (5, 17) => OnExceptionAbort(transaction.Message),
            (5, 5) => OnListAlarms(transaction.Message),
            (5, 7) => OnListEnabledAlarms(),
            (6, 15) => OnEventReportRequest(transaction.Message),
            (6, 19) => OnIndividualReportRequest(transaction.Message),
            (6, 7) => OnDataTransferRequest(transaction.Message),
            (6, 17) => OnAnnotatedIndividualReportRequest(transaction.Message),
            (6, 27) => OnNotificationReportRequest(transaction.Message),
            (2, 27) => OnInitiateProcessing(transaction.Message),
            (2, 39) => OnMultiBlockInquireRequest(transaction.Message),
            (2, 43) => OnSpoolReset(transaction.Message),
            _ => null,
        };

        // The handlers that have to await, because they run application code: a remote command, and
        // anything that touches the process program store.
        reply ??= (transaction.S, transaction.F) switch
        {
            (2, 21) => await OnRemoteCommandAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 41) => await OnHostCommandAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 1) => await OnServiceProgramInquireAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 3) => await OnServiceProgramSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 5) => await OnServiceProgramRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 7) => await OnServiceProgramRunAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 9) => await OnServiceProgramResultsAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (2, 11) => await OnServiceProgramDirectoryAsync(cancellationToken).ConfigureAwait(false),
            (7, 1) => await OnProgramInquireAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 3) => await OnProgramSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 5) => await OnProgramRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 7) => await OnProgramListAsync(8, cancellationToken).ConfigureAwait(false),
            (7, 9) => await OnProgramVerifyAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 17) => await OnProgramDeleteAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 19) => await OnProgramListAsync(20, cancellationToken).ConfigureAwait(false),
            (7, 23) => await OnFormattedProgramSendAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 25) => await OnFormattedProgramRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 27) => await OnFormattedProgramVerifyAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 31) => await OnProgramAvailableAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 33) => await OnFormattedProgramInquireAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            (7, 37) => await OnProgramCommandAsync(transaction.Message, 38, cancellationToken).ConfigureAwait(false),
            (7, 41) => await OnProgramCommandAsync(transaction.Message, 42, cancellationToken).ConfigureAwait(false),
            (10, 3) => await OnTerminalDisplayAsync(transaction.Message, multiBlock: false, cancellationToken).ConfigureAwait(false),
            (10, 5) => await OnTerminalDisplayAsync(transaction.Message, multiBlock: true, cancellationToken).ConfigureAwait(false),
            (10, 9) => await OnBroadcastAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
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

        // Traces hold a timer each and a part-filled group of pooled items, so they are released
        // rather than left to a finalizer that items do not have.
        CancelAllTraces("the equipment was disposed");

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

            // A trace is host configuration too, and the same argument applies with more force: a
            // trace the host has forgotten about would keep sampling and keep reporting, and the
            // host would have no TRID to cancel it with.
            CancelAllTraces("the host re-established communications");

            // Model.Reset has already dropped the limit definitions; this drops the zones that
            // went with them, so the first look after a reconnect establishes a zone rather than
            // reporting a crossing out of a band nobody is watching any more.
            _zones.Clear();
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

    // ----------------------------------------------------------------- stream 2, service programs

    private async ValueTask<SecsMessage?> OnServiceProgramInquireAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_servicePrograms is null)
        {
            return null;
        }

        // DuplicateOrInvalid for a body this end could not read, which is the one code GRANT has
        // that GRANT6 has not - "Duplicate identifier / invalid" - and the only one of the four
        // that fits a malformed inquire.
        var grant = GemGrant.DuplicateOrInvalid;

        if (request.Item is { IsList: true, Count: 2 } body
            && TryReadId(body[0], out var id)
            && TryReadId(body[1], out var length))
        {
            var spid = (uint)Math.Min(id, uint.MaxValue);

            grant = await _servicePrograms
                .InquireAsync(spid, (int)Math.Min(length, int.MaxValue), cancellationToken)
                .ConfigureAwait(false);

            // The identifier the S2F3 after this one will be filed under, because that row carries
            // none of its own. A refused inquire leaves nothing granted, so the send that followed
            // it would have nowhere to go - which is the refusal doing its job.
            Interlocked.Exchange(
                ref _grantedServiceProgram,
                grant == GemGrant.Accepted ? spid : -1);
        }

        return new SecsMessage(2, 2, replyExpected: false)
        {
            Name = "ServiceProgramLoadGrant",
            Item = SecsItem.B((byte)grant).Named("GRANT"),
        };
    }

    private async ValueTask<SecsMessage?> OnServiceProgramSendAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_servicePrograms is null)
        {
            return null;
        }

        var acknowledgement = GemServiceProgramAcknowledgement.Error;

        // Consumed, not peeked: one send per inquire, because the handshake is per transfer. A
        // second S2F3 with no second S2F1 is refused.
        var granted = Interlocked.Exchange(ref _grantedServiceProgram, -1);

        if (granted < 0)
        {
            _logger.LogWarning(
                "S2F3 arrived with no S2F1 granted before it, so there was no SPID to file the program under.");
        }
        else if (request.Item is { } body && body.Format.IsText())
        {
            // Not trimmed, unlike a PPID. PPID is an A[120] and arrives padded, where SPD is an
            // A[n] that is exactly as long as the program - so trimming here would quietly edit a
            // program whose last line ends in a space.
            acknowledgement = await _servicePrograms
                .SaveAsync((uint)granted, body.GetString(), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SecsMessage(2, 4, replyExpected: false)
        {
            Name = "ServiceProgramSendAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC2"),
        };
    }

    private async ValueTask<SecsMessage?> OnServiceProgramRequestAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_servicePrograms is null)
        {
            return null;
        }

        var text = request.Item is { } item && TryReadId(item, out var id)
            ? await _servicePrograms
                .LoadAsync((uint)Math.Min(id, uint.MaxValue), cancellationToken)
                .ConfigureAwait(false)
            : null;

        return new SecsMessage(2, 6, replyExpected: false)
        {
            Name = "ServiceProgramData",

            // A zero-length SPD for "no such program", and it stays inside the format the row
            // declares rather than becoming an empty list the way S7F6's refusal does: S7F6 is a
            // list of two items and can be a list of none, where S2F6 is one A[n]. Nothing is lost
            // by the ambiguity, because a service program with no text is not a program.
            Item = SecsItem.A(text ?? string.Empty).Named("SPD"),
        };
    }

    private async ValueTask<SecsMessage?> OnServiceProgramRunAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_servicePrograms is null)
        {
            return null;
        }

        var acknowledgement = GemServiceProgramAcknowledgement.Error;

        if (request.Item is { } item && TryReadId(item, out var id))
        {
            acknowledgement = await _servicePrograms
                .RunAsync((uint)Math.Min(id, uint.MaxValue), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SecsMessage(2, 8, replyExpected: false)
        {
            Name = "ServiceProgramRunAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC2"),
        };
    }

    private async ValueTask<SecsMessage?> OnServiceProgramResultsAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_servicePrograms is null)
        {
            return null;
        }

        var results = request.Item is { } item && TryReadId(item, out var id)
            ? await _servicePrograms
                .ResultsAsync((uint)Math.Min(id, uint.MaxValue), cancellationToken)
                .ConfigureAwait(false)
            : null;

        return new SecsMessage(2, 10, replyExpected: false)
        {
            Name = "ServiceProgramResultsData",
            Item = SecsItem.A(results ?? string.Empty).Named("SPR"),
        };
    }

    private async ValueTask<SecsMessage?> OnServiceProgramDirectoryAsync(CancellationToken cancellationToken)
    {
        if (_servicePrograms is null)
        {
            return null;
        }

        var ids = await _servicePrograms.ListAsync(cancellationToken).ConfigureAwait(false);
        var entries = new SecsItem[ids.Count];

        for (var i = 0; i < ids.Count; i++)
        {
            entries[i] = SecsItem.U4(ids[i]).Named("SPID");
        }

        return new SecsMessage(2, 12, replyExpected: false)
        {
            Name = "ServiceProgramDirectoryData",
            Item = SecsItem.L(entries),
        };
    }

    // ----------------------------------------------------------------- stream 2, the three singles

    private SecsMessage OnInitiateProcessing(SecsMessage request)
    {
        var acknowledgement = GemInitiateProcessingAcknowledgement.Error;

        if (request.Item is { } item && item.Format.IsText())
        {
            // Trimmed, because this one really is a PPID - an A[120] that arrives padded - and it
            // is the same identifier stream 7 files recipes under.
            acknowledgement = AnswerInitiateProcessing(item.GetString().TrimEnd());
        }

        return new SecsMessage(2, 28, replyExpected: false)
        {
            Name = "InitiateProcessingAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("PRACK"),
        };
    }

    private SecsMessage OnMultiBlockInquireRequest(SecsMessage request)
    {
        var grant = GemGrant.DuplicateOrInvalid;

        if (request.Item is { IsList: true, Count: 2 } body
            && TryReadId(body[0], out var dataId)
            && TryReadId(body[1], out var length))
        {
            grant = AnswerMultiBlockInquiry(
                (uint)Math.Min(dataId, uint.MaxValue),
                (uint)Math.Min(length, uint.MaxValue));
        }

        return new SecsMessage(2, 40, replyExpected: false)
        {
            Name = "MultiBlockGrant",
            Item = SecsItem.B((byte)grant).Named("GRANT"),
        };
    }

    private SecsMessage? OnSpoolReset(SecsMessage request)
    {
        if (_spool is null)
        {
            return null;
        }

        var acknowledgement = GemSpoolResetAcknowledgement.Accepted;
        List<SecsItem> results = [];

        if (request.Item is { IsList: true } body)
        {
            foreach (var entry in body.Items)
            {
                if (entry is not { IsList: true, Count: 2 }
                    || !GemItems.TryReadCode(entry[0], out var stream)
                    || !entry[1].IsList)
                {
                    // One unreadable entry refuses the message rather than being skipped: a host
                    // that gets RSPACK 0 with a stream missing from the reply would have no way to
                    // tell which of its instructions took.
                    acknowledgement = GemSpoolResetAcknowledgement.Error;
                    continue;
                }

                List<byte> functions = [];

                foreach (var named in entry[1].Items)
                {
                    if (GemItems.TryReadCode(named, out var function))
                    {
                        functions.Add(function);
                    }
                }

                // An empty function list names the whole stream. The reading is this library's,
                // because the reference gives the row two items and no words - but an entry that
                // named a stream and no functions would otherwise mean nothing at all, and that is
                // the worse of the two readings.
                if (functions.Count == 0)
                {
                    foreach (var pair in SpoolableMessages)
                    {
                        if (pair.Stream == stream)
                        {
                            _spoolSuppressed[pair] = true;
                        }
                    }
                }
                else
                {
                    foreach (var function in functions)
                    {
                        _spoolSuppressed[(stream, function)] = true;
                    }
                }

                results.Add(SecsItem.L(
                    SecsItem.U1(stream).Named("STRID"),
                    SecsItem.B(0).Named("STRACK"),
                    SecsItem.L([.. functions.Select(f => SecsItem.U1(f).Named("FCNID"))])));
            }
        }
        else
        {
            // Not a list at all. An empty list is a different thing and is accepted below: it
            // names no stream, so it is no instruction - and reading "all of them" into it would
            // be inventing a meaning the row does not carry.
            acknowledgement = GemSpoolResetAcknowledgement.Error;
        }

        return new SecsMessage(2, 44, replyExpected: false)
        {
            Name = "ResetSpoolingAcknowledge",
            Item = SecsItem.L(
                SecsItem.B((byte)acknowledgement).Named("RSPACK"),
                SecsItem.L(results)),
        };
    }

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

    /// <summary>Answers S7F19 and S7F7, which ask the same question by two function numbers.</summary>
    /// <param name="replyFunction">20 for the current EPPD, 8 for the directory.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The reply, or null when this equipment keeps no programs.</returns>
    /// <remarks>
    /// <para>
    /// <strong>One answer, because they are one question.</strong> The reference names S7F8
    /// "Process Program Directory Data" and S7F20 "Current EPPD Data" - EPPD being the equipment
    /// process program directory - and gives both bodies the same single item, a list of
    /// <c>PPID</c>. There is nothing in it to tell the two apart, so serving them differently
    /// would be inventing a distinction. The pair that was documented is S7F19/F20; S7F7/F8 is a
    /// row the reference illustrates, implemented because a host that asks the older function
    /// number deserves the same answer rather than an S9F5.
    /// </para>
    /// <para>
    /// The reply function is a parameter rather than two handlers because the only difference is
    /// that number: a copy of this method would be a copy of the merge rule below, and the two
    /// would drift.
    /// </para>
    /// </remarks>
    private async ValueTask<SecsMessage?> OnProgramListAsync(byte replyFunction, CancellationToken cancellationToken)
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

        return new SecsMessage(7, replyFunction, replyExpected: false)
        {
            Name = replyFunction == 20 ? "CurrentEppdData" : "ProcessProgramDirectoryData",
            Item = SecsItem.L(entries),
        };
    }

    /// <summary>Answers S7F9, which asks whether a body the host is holding is the one here.</summary>
    /// <remarks>
    /// <para>
    /// <strong><c>ACKC7</c> has no code for "those bytes are not these bytes"</strong>, which is
    /// the one thing a verify exists to discover. Its seven named codes are acceptance, permission
    /// not granted, a length error, matrix overflow, PPID not found, mode unsupported and performed
    /// later. So the mapping has to be stated rather than derived: a match is <c>Accepted</c>, an
    /// unknown identifier is <c>NotFound</c>, a body of a different length is <c>LengthError</c> -
    /// which is exactly what it says - and a body of the right length with the wrong contents is
    /// <c>PermissionNotGranted</c>, the table's nearest word for "I will not confirm that".
    /// </para>
    /// <para>
    /// That last one is a shortage in the item and not a measurement, so the comparison is logged:
    /// the code the host receives cannot distinguish a refusal from a mismatch, and the only place
    /// the difference survives is the machine's own diagnostics.
    /// </para>
    /// </remarks>
    private async ValueTask<SecsMessage?> OnProgramVerifyAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_programs is null)
        {
            return null;
        }

        var acknowledgement = GemProcessProgramAcknowledgement.LengthError;

        if (request.Item is { IsList: true, Count: 2 } body
            && body[0].Format.IsText()
            && body[1].Format == SecsFormat.Binary)
        {
            var id = body[0].GetString().TrimEnd();
            var offered = body[1].GetMemory<byte>();

            var held = id.Length == 0
                ? null
                : await _programs.LoadAsync(id, cancellationToken).ConfigureAwait(false);

            if (held is null)
            {
                acknowledgement = GemProcessProgramAcknowledgement.NotFound;
            }
            else if (held.Value.Length != offered.Length)
            {
                acknowledgement = GemProcessProgramAcknowledgement.LengthError;
            }
            else if (held.Value.Span.SequenceEqual(offered.Span))
            {
                acknowledgement = GemProcessProgramAcknowledgement.Accepted;
            }
            else
            {
                _logger.LogInformation(
                    "S7F9 offered {Length} byte(s) for '{Id}' that are not the bytes held. ACKC7 has no code "
                        + "for a mismatch, so the answer is {Code} and this line is the only record of why.",
                    offered.Length,
                    id,
                    GemProcessProgramAcknowledgement.PermissionNotGranted);

                acknowledgement = GemProcessProgramAcknowledgement.PermissionNotGranted;
            }
        }

        return new SecsMessage(7, 10, replyExpected: false)
        {
            Name = "ProcessProgramVerifyAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC7"),
        };
    }

    /// <summary>Answers S7F31, which asks whether a program is here at all.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Two of <c>PPGNT</c>'s six codes, and the other four would be answers to a different
    /// question.</strong> The item exists for S7F2, where the equipment is deciding whether to
    /// accept a transfer - so <c>Ok</c> means "go ahead and send", <c>NoSpace</c> and <c>Busy</c>
    /// and <c>WillNotAccept</c> are refusals of a transfer nobody has offered here. What is left is
    /// the pair that happens to answer availability exactly: <c>AlreadyHave</c> for a program this
    /// machine holds, and <c>InvalidId</c> for one it does not or for an identifier it cannot read.
    /// </para>
    /// <para>
    /// Both stores, for the reason S7F17 and S7F19 cover both: a host asking whether RECIPE-7 is
    /// here is asking about the program, not about the flavour it was transferred in.
    /// </para>
    /// </remarks>
    private async ValueTask<SecsMessage?> OnProgramAvailableAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_programs is null && _formatted is null)
        {
            return null;
        }

        var grant = GemProcessProgramGrant.InvalidId;

        if (request.Item is { } item && item.Format.IsText() && item.Count > 0)
        {
            var id = item.GetString().TrimEnd();

            if (id.Length > 0 && await HasProgramAsync(id, cancellationToken).ConfigureAwait(false))
            {
                grant = GemProcessProgramGrant.AlreadyHave;
            }
        }

        return new SecsMessage(7, 32, replyExpected: false)
        {
            Name = "ProcessProgramAvailableData",
            Item = SecsItem.B((byte)grant).Named("PPGNT"),
        };
    }

    /// <summary>Answers S7F33, the formatted half's load inquire.</summary>
    /// <remarks>
    /// <para>
    /// S7F1's twin, item for item, and it cannot answer everything S7F1 can.
    /// <see cref="IGemProcessProgramStore"/> has an <c>InquireAsync</c> whose whole purpose is to
    /// let a store refuse on capacity; <see cref="IGemFormattedProcessProgramStore"/> has none, so
    /// <c>NoSpace</c>, <c>Busy</c> and <c>WillNotAccept</c> are unreachable here. Adding the method
    /// would break every store anybody has already written, and the refusal that matters is still
    /// available at S7F23, where the store has the program in its hands.
    /// </para>
    /// <para>
    /// So <c>LENGTH</c> is read and discarded - read, because a body that does not carry it is not
    /// this message and gets <c>InvalidId</c>; discarded, because there is nothing here that could
    /// act on it without inventing a capacity the store never declared.
    /// </para>
    /// </remarks>
    private async ValueTask<SecsMessage?> OnFormattedProgramInquireAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_formatted is null)
        {
            return null;
        }

        var grant = GemProcessProgramGrant.InvalidId;

        if (request.Item is { IsList: true, Count: 2 } body
            && body[0].Format.IsText()
            && TryReadId(body[1], out _))
        {
            var id = body[0].GetString().TrimEnd();

            if (id.Length > 0)
            {
                grant = await _formatted.LoadAsync(id, cancellationToken).ConfigureAwait(false) is null
                    ? GemProcessProgramGrant.Ok
                    : GemProcessProgramGrant.AlreadyHave;
            }
        }

        return new SecsMessage(7, 34, replyExpected: false)
        {
            Name = "FormattedProcessProgramLoadGrant",
            Item = SecsItem.B((byte)grant).Named("PPGNT"),
        };
    }

    /// <summary>Answers S7F27, the formatted half's verify.</summary>
    /// <remarks>
    /// The same shortage in <c>ACKC7</c> that S7F9 documents, and one thing it does not share: the
    /// comparison here is over structure rather than bytes, so there is no length to report and
    /// <c>LengthError</c> is kept for a body this stack could not read at all. A formatted program
    /// that differs differs in its model, its revision or one of its commands, and the item has no
    /// word for any of those.
    /// </remarks>
    private async ValueTask<SecsMessage?> OnFormattedProgramVerifyAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_formatted is null)
        {
            return null;
        }

        var acknowledgement = GemFormattedProcessProgramCodec.TryRead(request.Item, out var offered);

        if (offered is not null)
        {
            var held = await _formatted.LoadAsync(offered.Id, cancellationToken).ConfigureAwait(false);

            if (held is null)
            {
                acknowledgement = GemProcessProgramAcknowledgement.NotFound;
            }
            else if (Matches(held, offered))
            {
                acknowledgement = GemProcessProgramAcknowledgement.Accepted;
            }
            else
            {
                _logger.LogInformation(
                    "S7F27 offered a formatted program for '{Id}' that is not the one held. ACKC7 has no code "
                        + "for a mismatch, so the answer is {Code}.",
                    offered.Id,
                    GemProcessProgramAcknowledgement.PermissionNotGranted);

                acknowledgement = GemProcessProgramAcknowledgement.PermissionNotGranted;
            }
        }

        return new SecsMessage(7, 28, replyExpected: false)
        {
            Name = "FormattedProcessProgramVerifyAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC7"),
        };
    }

    /// <summary>Answers S7F37 and S7F41, the remote commands that name a process program.</summary>
    /// <param name="request">The primary.</param>
    /// <param name="replyFunction">38 or 42.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The reply, or null when this equipment keeps no programs.</returns>
    /// <remarks>
    /// <para>
    /// <strong>S2F21 and S2F41 with a <c>PPID</c> in front</strong>, and served from the same
    /// command table for that reason: the items are <c>PPID</c>, <c>RCMD</c> and - for S7F41 - the
    /// <c>CPNAME</c> and <c>CPVAL</c> pairs, every one of them a name S2F41 already uses. The
    /// program reaches a handler as <c>GemRemoteCommandContext.ProcessProgramId</c>.
    /// </para>
    /// <para>
    /// <strong>A command naming a program this machine has not got is refused before any handler
    /// runs</strong>, and refused as <c>CannotPerformNow</c> rather than <c>DoesNotExist</c>:
    /// <c>CMDA</c> 1 says the *command* does not exist, which would be a lie about a command the
    /// machine has. There is no code for "no such program", so the honest answer is the one that
    /// is true - it cannot be done now, with the recipe absent.
    /// </para>
    /// <para>
    /// Both rows are declared with the process program capability rather than with the core set,
    /// because stream 7 is declared by equipment that manages programs: a machine with no recipes
    /// should answer S9F3 for the whole stream rather than refuse one function of it.
    /// </para>
    /// </remarks>
    private async ValueTask<SecsMessage?> OnProgramCommandAsync(
        SecsMessage request,
        byte replyFunction,
        CancellationToken cancellationToken)
    {
        if (_programs is null && _formatted is null)
        {
            return null;
        }

        var cmda = GemRemoteCommandAcknowledgement.DoesNotExist;

        if (request.Item is { IsList: true, Count: >= 2 } body && body[0].Format.IsText())
        {
            var programId = body[0].GetString().TrimEnd();
            var name = ReadCommandName(body[1]);

            if (programId.Length == 0 || !await HasProgramAsync(programId, cancellationToken).ConfigureAwait(false))
            {
                cmda = GemRemoteCommandAcknowledgement.CannotPerformNow;
            }
            else if (name.Length > 0 && Model.RemoteCommands.TryGetValue(name, out var command))
            {
                cmda = await RunProgramCommandAsync(command, name, programId, body, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new SecsMessage(7, replyFunction, replyExpected: false)
        {
            Name = replyFunction == 38
                ? "ProcessProgramRelatedRemoteCommandAcknowledge"
                : "ProcessProgramRelatedRemoteCommandWithParametersAcknowledge",
            Item = SecsItem.B((byte)cmda).Named("CMDA"),
        };
    }

    private async ValueTask<GemRemoteCommandAcknowledgement> RunProgramCommandAsync(
        GemRemoteCommand command,
        string name,
        string programId,
        SecsItem body,
        CancellationToken cancellationToken)
    {
        if (command.RequiresRemoteControl && _control != GemControlState.OnlineRemote)
        {
            return GemRemoteCommandAcknowledgement.CannotPerformNow;
        }

        // The third item when there is one, read exactly as S2F41's parameter list is read. S7F37
        // carries two items and no list, which is the same thing as an empty one.
        var parameters = body.Count > 2
            ? ReadCommandParameters(body[2])
            : new Dictionary<string, SecsItem>(StringComparer.Ordinal);

        try
        {
            var hcack = await command
                .Handler(
                    new GemRemoteCommandContext(name, parameters) { ProcessProgramId = programId },
                    cancellationToken)
                .ConfigureAwait(false);

            return NarrowToCommandAcknowledgement(hcack);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The handler for remote command '{Command}' threw.", name);
            return GemRemoteCommandAcknowledgement.CannotPerformNow;
        }
    }

    /// <summary>Whether either store holds a program.</summary>
    /// <remarks>
    /// <c>LoadAsync</c> rather than a scan of <c>ListAsync</c>: a store over a filesystem can
    /// answer the first for one identifier far more cheaply than it can enumerate everything, and
    /// the result of the second would be discarded but for one entry.
    /// </remarks>
    private async ValueTask<bool> HasProgramAsync(string id, CancellationToken cancellationToken)
    {
        if (_programs is not null
            && await _programs.LoadAsync(id, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        return _formatted is not null
            && await _formatted.LoadAsync(id, cancellationToken).ConfigureAwait(false) is not null;
    }

    /// <summary>Whether two formatted programs are the same program.</summary>
    /// <remarks>
    /// Every field, including the model and revision the host sent, because a program written for
    /// another machine is not this one even where its commands match. Ordinal throughout: these are
    /// identifiers and values off a wire, where PUMP and pump are two things.
    /// </remarks>
    private static bool Matches(GemFormattedProcessProgram held, GemFormattedProcessProgram offered)
    {
        if (!string.Equals(held.ModelName, offered.ModelName, StringComparison.Ordinal)
            || !string.Equals(held.SoftwareRevision, offered.SoftwareRevision, StringComparison.Ordinal)
            || held.Commands.Count != offered.Commands.Count)
        {
            return false;
        }

        for (var i = 0; i < held.Commands.Count; i++)
        {
            var a = held.Commands[i];
            var b = offered.Commands[i];

            if (a.Code != b.Code || a.Parameters.Count != b.Parameters.Count)
            {
                return false;
            }

            for (var j = 0; j < a.Parameters.Count; j++)
            {
                if (!string.Equals(a.Parameters[j], b.Parameters[j], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
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

    // ----------------------------------------------------------------- inbound, limits

    private SecsMessage OnDefineLimits(SecsMessage request)
    {
        if (!GemLimitCodec.TryReadDefinitions(request.Item, out var entries))
        {
            // VLAACK 1 with no detail, because there was no readable variable to report detail
            // against. E5's two codes for it are match and mismatch, and this is a mismatch.
            return new SecsMessage(2, 46, replyExpected: false)
            {
                Name = "VariableLimitAttributeAcknowledge",
                Item = GemLimitCodec.WriteResults(accepted: false, []),
            };
        }

        if (entries.Count == 0)
        {
            // An empty variable list removes every limit on every variable, the way an empty
            // S2F33 removes every report definition.
            Model.DefineVariableLimits([]);
            _zones.Clear();

            return new SecsMessage(2, 46, replyExpected: false)
            {
                Name = "VariableLimitAttributeAcknowledge",
                Item = GemLimitCodec.WriteResults(accepted: true, []),
            };
        }

        List<GemVariableLimitResult> results = [];
        var accepted = true;

        foreach (var entry in entries)
        {
            // All or nothing per variable. A half-configured variable is the worse failure for a
            // monitoring feature - the host would believe it is watching a band that is not there
            // - so a variable with one unusable band keeps none of them, and every band in it
            // carries a reason: 4 for the one that was not a band, 1 for the rest, because the
            // definition they belong to is the thing in error.
            if (entry.Limits.Exists(limit => limit.Band is null))
            {
                var answers = new List<(uint, GemVariableLimitAcknowledgement)>(entry.Limits.Count);

                foreach (var (id, band) in entry.Limits)
                {
                    answers.Add((id, band is null
                        ? GemVariableLimitAcknowledgement.ValueError
                        : GemVariableLimitAcknowledgement.DefinitionError));
                }

                results.Add(new GemVariableLimitResult(entry.VariableId, accepted: false, answers));
                accepted = false;

                continue;
            }

            var bands = new GemVariableLimit[entry.Limits.Count];

            for (var i = 0; i < bands.Length; i++)
            {
                bands[i] = entry.Limits[i].Band!;
            }

            // One variable at a time, because each is independent and the merge back into request
            // order is then nothing at all.
            var (ok, one) = Model.DefineVariableLimits(
                [new GemVariableLimitDefinition(entry.VariableId, bands)]);

            accepted &= ok;
            results.AddRange(one);

            // The zones go with the definition that made them. Whether the value used to be inside
            // the old band says nothing about the new one, and reporting a crossing for the
            // difference would be reporting the host's own edit back at it.
            ForgetLimitZones(entry.VariableId);
        }

        return new SecsMessage(2, 46, replyExpected: false)
        {
            Name = "VariableLimitAttributeAcknowledge",
            Item = GemLimitCodec.WriteResults(accepted, results),
        };
    }

    private SecsMessage OnLimitRequest(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var entries = new List<SecsItem>();

        // An empty list asks about every variable that has limits, which is the only answer that
        // is short enough to be useful on a machine with hundreds of variables.
        var variables = requested.Count == 0
            ? Model.DefinedLimits.Order().ToArray()
            : requested.Where(id => id <= uint.MaxValue).Select(id => (uint)id).ToArray();

        foreach (var variableId in variables)
        {
            Model.Variables.TryGetValue(variableId, out var variable);

            entries.Add(GemLimitCodec.WriteAttributes(
                variableId,
                variable?.Units ?? string.Empty,

                // E5 has the equipment report the variable's own documented range here. This layer
                // does not carry one for a status variable, so it reports a zero-length item -
                // which is what E5 uses for "no documented bound" and what the equipment constant
                // namelist already does for ECMIN and ECMAX.
                minimum: null,
                maximum: null,
                Model.LimitsFor(variableId)));
        }

        return new SecsMessage(2, 48, replyExpected: false)
        {
            Name = "VariableLimitAttributesSend",
            Item = SecsItem.L(entries),
        };
    }

    // ----------------------------------------------------------------- inbound, trace data

    private SecsMessage OnTraceInitialize(SecsMessage request)
    {
        var tiaack = ReadTrace(request.Item, out var trace);

        if (trace is not null)
        {
            tiaack = trace.TotalSamples == 0
                ? CancelTrace(trace.Id)
                : StartTrace(trace);
        }

        return new SecsMessage(2, 24, replyExpected: false)
        {
            Name = "TraceInitializeAcknowledge",
            Item = SecsItem.B((byte)tiaack).Named("TIAACK"),
        };
    }

    /// <summary>Reads S2F23's body: TRID, DSPER, TOTSMP, REPGSZ and the variables.</summary>
    /// <param name="body">The inbound body.</param>
    /// <param name="trace">What was asked for, or <see langword="null"/> when the body could not be read.</param>
    /// <returns>The TIAACK to answer with when <paramref name="trace"/> is <see langword="null"/>.</returns>
    /// <remarks>
    /// TIAACK has no code for "that body made no sense" - its five refusals each name a field - so
    /// the code for an unreadable body is a best effort. What a host must read from it is that it
    /// is not zero; <see cref="GemTraceAcknowledgement.InvalidPeriod"/> is the one chosen because
    /// DSPER is the field a host most often gets wrong, and a body this stack cannot read has no
    /// period in it either.
    /// </remarks>
    private static GemTraceAcknowledgement ReadTrace(SecsItem? body, out GemTraceRequest? trace)
    {
        trace = null;

        if (body is not { IsList: true, Count: 5 }
            || !body[4].IsList
            || !GemItems.TryReadId(body[0], out var id)
            || id > uint.MaxValue)
        {
            return GemTraceAcknowledgement.InvalidPeriod;
        }

        if (!GemItems.TryReadId(body[2], out var total)
            || !GemItems.TryReadId(body[3], out var group)
            || total > int.MaxValue
            || group > int.MaxValue)
        {
            return GemTraceAcknowledgement.InvalidGroupSize;
        }

        List<uint> variables = [];

        foreach (var entry in body[4].Items)
        {
            if (!GemItems.TryReadId(entry, out var variable) || variable > uint.MaxValue)
            {
                return GemTraceAcknowledgement.UnknownVariable;
            }

            variables.Add((uint)variable);
        }

        // The period last, because it is the one field whose format E5 fixes as text rather than a
        // number: hhmmss or hhmmsscc, so a tenth of a second is not expressible and neither is a
        // week.
        var period = TimeSpan.Zero;

        if (body[1].Format.IsText() && TryReadPeriod(body[1].GetString(), out var parsed))
        {
            period = parsed;
        }

        trace = new GemTraceRequest((uint)id, period, (int)total, (int)group, variables);

        return GemTraceAcknowledgement.Accepted;
    }

    private static bool TryReadPeriod(string text, out TimeSpan period)
    {
        period = TimeSpan.Zero;

        var trimmed = text.Trim();

        if (trimmed.Length is not (6 or 8)
            || !int.TryParse(trimmed.AsSpan(0, 2), CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(trimmed.AsSpan(2, 2), CultureInfo.InvariantCulture, out var minutes)
            || !int.TryParse(trimmed.AsSpan(4, 2), CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var centiseconds = 0;

        if (trimmed.Length == 8
            && !int.TryParse(trimmed.AsSpan(6, 2), CultureInfo.InvariantCulture, out centiseconds))
        {
            return false;
        }

        period = new TimeSpan(0, hours, minutes, seconds, centiseconds * 10);

        return period > TimeSpan.Zero;
    }

    private GemTraceAcknowledgement StartTrace(GemTraceRequest trace)
    {
        if (trace.VariableIds.Count == 0 || trace.VariableIds.Count > _options.MaxTraceVariables)
        {
            return GemTraceAcknowledgement.TooManyVariables;
        }

        foreach (var variable in trace.VariableIds)
        {
            // Status variables only. E30 traces a value a host could have read with S1F3, and a
            // data variable only has a value inside the event it belongs to - so tracing one would
            // sample whatever it happened to hold.
            if (!Model.Variables.TryGetValue(variable, out var known) || known.Kind != GemVariableKind.Status)
            {
                return GemTraceAcknowledgement.UnknownVariable;
            }
        }

        if (trace.Period <= TimeSpan.Zero)
        {
            return GemTraceAcknowledgement.InvalidPeriod;
        }

        if (trace.GroupSize < 1 || trace.GroupSize > trace.TotalSamples)
        {
            return GemTraceAcknowledgement.InvalidGroupSize;
        }

        // A trace that replaces one already running under the same TRID does not need a slot: E5
        // defines S2F23 as initialising a trace, and initialising the one that exists is what a
        // host does to change its period.
        if (!_traces.ContainsKey(trace.Id) && _traces.Count >= _options.MaxTraces)
        {
            return GemTraceAcknowledgement.NoMoreTracesAllowed;
        }

        CancelTrace(trace.Id);

        var run = new TraceRun(this, trace);

        _traces[trace.Id] = run;

        // Periodic, and only a nudge: the callback runs on a timer thread and does nothing but
        // tell the loop which trace is due.
        run.Timer = _timeProvider.CreateTimer(
            static state =>
            {
                var due = (TraceRun)state!;

                due.Owner._traceDue.Enqueue(due.Request.Id);
                due.Owner._events.Writer.TryWrite(LinkEvent.TraceDue);
            },
            run,
            trace.Period,
            trace.Period);

        _logger.LogInformation("Trace {Trace} started: {Request}.", trace.Id, trace);

        return GemTraceAcknowledgement.Accepted;
    }

    private GemTraceAcknowledgement CancelTrace(uint id)
    {
        if (_traces.TryRemove(id, out var run))
        {
            run.Dispose();
            _logger.LogInformation("Trace {Trace} cancelled.", id);
        }

        // Accepted either way. Cancelling a trace that is not running is not a failure - the host
        // wanted it stopped, and it is stopped.
        return GemTraceAcknowledgement.Accepted;
    }

    private void ForgetLimitZones(uint variableId)
    {
        foreach (var key in _zones.Keys)
        {
            if (key.Variable == variableId)
            {
                _zones.TryRemove(key, out _);
            }
        }
    }

    private void CancelAllTraces(string reason)
    {
        foreach (var id in _traces.Keys)
        {
            if (_traces.TryRemove(id, out var run))
            {
                run.Dispose();
            }
        }

        _logger.LogInformation("Every trace was cancelled ({Reason}).", reason);
    }

    private async ValueTask SampleTracesAsync(CancellationToken cancellationToken)
    {
        while (_traceDue.TryDequeue(out var id))
        {
            if (!_traces.TryGetValue(id, out var run))
            {
                // Cancelled between the timer firing and the loop getting here, which is the one
                // race a nudge-and-look design has and the one it handles by looking.
                continue;
            }

            var values = new SecsItem[run.Request.VariableIds.Count];

            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Model.Variables.TryGetValue(run.Request.VariableIds[i], out var variable)
                    ? variable.Read().Named(variable.Name)
                    : SecsItem.L();
            }

            run.Group.Add(SecsItem.L(values));
            run.Taken++;
            run.LastSampleAt = _timeProvider.GetUtcNow();

            if (run.Group.Count >= run.Request.GroupSize)
            {
                await SendTraceGroupAsync(run, cancellationToken).ConfigureAwait(false);
            }

            if (run.Taken < run.Request.TotalSamples)
            {
                continue;
            }

            // The last group goes even when it is short of REPGSZ. The host asked for TOTSMP
            // samples and these are the end of them, so holding them back for a group that will
            // never fill would lose them.
            if (run.Group.Count > 0)
            {
                await SendTraceGroupAsync(run, cancellationToken).ConfigureAwait(false);
            }

            CancelTrace(id);
        }
    }

    private async ValueTask SendTraceGroupAsync(TraceRun run, CancellationToken cancellationToken)
    {
        // The list takes the samples, and the message takes the list: everything in the group is
        // disposed with the message, which is why the group is cleared rather than emptied.
        var samples = SecsItem.L(run.Group);

        run.Group.Clear();

        using var message = new SecsMessage(6, 1, replyExpected: _options.EventReportsExpectReply)
        {
            Name = "TraceDataSend",
            Item = SecsItem.L(
                SecsItem.U4(run.Request.Id).Named("TRID"),
                SecsItem.U4((uint)run.Taken).Named("SMPLN"),
                SecsItem.A(run.LastSampleAt.UtcDateTime.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture)).Named("STIME"),
                samples),
        };

        // The same path an event report takes, so a group that cannot be delivered is spooled
        // rather than dropped. SMPLN and STIME describe the last sample in the group, which is
        // what lets a host put a late group back in order.
        await SendReportAsync(message, cancellationToken).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- inbound, stream 10

    private async ValueTask<SecsMessage?> OnTerminalDisplayAsync(
        SecsMessage request,
        bool multiBlock,
        CancellationToken cancellationToken)
    {
        if (_terminal is null)
        {
            return null;
        }

        // Not "the terminal said no": nothing was shown to anybody, because there was nothing this
        // stack could read. ACKC10 has no code for a body it could not parse and 1 is the closest
        // honest one - the message arrived and will not be displayed.
        var acknowledgement = GemTerminalAcknowledgement.WillNotDisplay;

        if (request.Item is { IsList: true, Count: 2 } body && GemItems.TryReadCode(body[0], out var terminalId))
        {
            // One reader for both functions. S10F3 carries a single block and S10F5 a list of
            // them, and a host that used the wrong one of the two is understood either way: the
            // text is the same text, and refusing it over the shape of its container would help
            // nobody standing at the machine.
            var blocks = GemItems.ReadTextBlocks(body[1]);

            acknowledgement = await _terminal
                .DisplayAsync(new GemTerminalMessage(terminalId, blocks), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SecsMessage(10, multiBlock ? (byte)6 : (byte)4, replyExpected: false)
        {
            Name = multiBlock ? "MultiBlockTerminalDisplayAcknowledge" : "TerminalDisplayAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC10"),
        };
    }

    private async ValueTask<SecsMessage?> OnBroadcastAsync(SecsMessage request, CancellationToken cancellationToken)
    {
        if (_terminal is null)
        {
            return null;
        }

        var acknowledgement = GemTerminalAcknowledgement.WillNotDisplay;
        var blocks = GemItems.ReadTextBlocks(request.Item);

        if (blocks.Count > 0)
        {
            // No TID in an S10F9 body at all: a broadcast is for every terminal, so zero here is
            // the absence of a choice rather than terminal number zero. IsBroadcast is what an
            // implementation with several terminals reads.
            acknowledgement = await _terminal
                .DisplayAsync(new GemTerminalMessage(0, blocks, isBroadcast: true), cancellationToken)
                .ConfigureAwait(false);
        }

        return new SecsMessage(10, 10, replyExpected: false)
        {
            Name = "BroadcastMessageAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC10"),
        };
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

    /// <summary>Answers S1F21 with the data variables' names and units.</summary>
    /// <remarks>
    /// S1F12's sibling, and deliberately built the same way: an empty request asks for every data
    /// variable and a named one is answered id for id, with an unknown identifier coming back with
    /// its name and units both zero-length rather than being left out. The reply stays aligned
    /// with the request, which is what lets a host read the two side by side.
    /// </remarks>
    private SecsMessage OnDataVariableNamelist(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var entries = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var variable in Model.Variables.Values
                .Where(variable => variable.Kind == GemVariableKind.Data)
                .OrderBy(variable => variable.Id))
            {
                entries.Add(DataVariableNamelistEntry(variable.Id, variable));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                var known = id <= uint.MaxValue
                    && Model.Variables.TryGetValue((uint)id, out var variable)
                    && variable.Kind == GemVariableKind.Data;

                entries.Add(DataVariableNamelistEntry(
                    (uint)Math.Min(id, uint.MaxValue),
                    known ? Model.Variables[(uint)id] : null));
            }
        }

        return new SecsMessage(1, 22, replyExpected: false)
        {
            Name = "DataVariableNamelist",
            Item = SecsItem.L(entries),
        };
    }

    /// <summary>Answers S1F23 with the events' names and the variables each is about.</summary>
    /// <remarks>
    /// <para>
    /// The third namelist, and the only one that nests: each entry is the identifier, the name and
    /// a list of <c>VID</c>. That shape is read off S1F12's and S2F30's - identifier, name, then
    /// whatever else the row names - rather than invented, and the nesting is the one part a flat
    /// item list cannot pin down, so it is said here.
    /// </para>
    /// <para>
    /// The variables come from <see cref="GemCollectionEvent.VariableIds"/>, which is what the
    /// equipment says the event is about - not from the reports a host linked with S2F35. A
    /// namelist describes the machine; a report describes the conversation.
    /// </para>
    /// </remarks>
    private SecsMessage OnCollectionEventNamelist(SecsMessage request)
    {
        var requested = ReadIdList(request.Item);
        var entries = new List<SecsItem>();

        if (requested.Count == 0)
        {
            foreach (var collectionEvent in Model.Events.Values.OrderBy(known => known.Id))
            {
                entries.Add(CollectionEventNamelistEntry(collectionEvent.Id, collectionEvent));
            }
        }
        else
        {
            foreach (var id in requested)
            {
                var known = id <= uint.MaxValue && Model.Events.TryGetValue((uint)id, out _);

                entries.Add(CollectionEventNamelistEntry(
                    (uint)Math.Min(id, uint.MaxValue),
                    known ? Model.Events[(uint)id] : null));
            }
        }

        return new SecsMessage(1, 24, replyExpected: false)
        {
            Name = "CollectionEventNamelist",
            Item = SecsItem.L(entries),
        };
    }

    /// <summary>Answers S2F25 by sending the same bytes back.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The one message in this package that means nothing at all</strong>, and that is its
    /// use: <c>ABS</c> goes out and the same <c>ABS</c> comes back, so a host learns that the link
    /// carries a body in both directions without changing anything on the machine. No control
    /// state is consulted for the same reason - a diagnostic that an off-line machine refused
    /// would be a diagnostic you could not run when you most need it.
    /// </para>
    /// <para>
    /// Echoed as it arrived, whatever format that was. E5 defines <c>ABS</c> as a binary, and a
    /// machine sending text is being loose rather than meaning something else - but an echo that
    /// corrected the format would be answering a different question from the one asked.
    /// </para>
    /// </remarks>
    private static SecsMessage OnLoopback(SecsMessage request)
        => new(2, 26, replyExpected: false)
        {
            Name = "LoopbackDiagnosticData",
            Item = EchoBody(request.Item),
        };

    /// <summary>Answers S2F21, the remote command with no parameters.</summary>
    /// <remarks>
    /// <para>
    /// The same command table S2F41 uses and the same control-state rule, because a command that
    /// changes what the machine does is refused unless the host is in charge - the message being
    /// shorter does not make it safer.
    /// </para>
    /// <para>
    /// <strong>Seven codes narrowed onto three.</strong> A handler answers in
    /// <c>GemHostCommandAcknowledgement</c>, which is <c>HCACK</c>; S2F22 carries <c>CMDA</c>,
    /// which names only performed, does-not-exist and cannot-perform-now. So
    /// <c>InvalidCommand</c> becomes <c>DoesNotExist</c>, <c>Accepted</c> becomes
    /// <c>Performed</c>, and everything else - including "completion later" and "already in the
    /// desired condition" - becomes <c>CannotPerformNow</c>. That loses detail, and losing it is
    /// the message's doing rather than this library's: a host that wants the fuller answer sends
    /// S2F41 with an empty parameter list.
    /// </para>
    /// </remarks>
    private async ValueTask<SecsMessage> OnRemoteCommandAsync(
        SecsMessage request,
        CancellationToken cancellationToken)
    {
        var cmda = GemRemoteCommandAcknowledgement.DoesNotExist;
        var name = ReadCommandName(request.Item);

        if (name.Length > 0 && Model.RemoteCommands.TryGetValue(name, out var command))
        {
            if (command.RequiresRemoteControl && _control != GemControlState.OnlineRemote)
            {
                cmda = GemRemoteCommandAcknowledgement.CannotPerformNow;
            }
            else
            {
                try
                {
                    var hcack = await command
                        .Handler(new GemRemoteCommandContext(name, new Dictionary<string, SecsItem>(StringComparer.Ordinal)), cancellationToken)
                        .ConfigureAwait(false);

                    cmda = NarrowToCommandAcknowledgement(hcack);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The handler for remote command '{Command}' threw.", name);
                    cmda = GemRemoteCommandAcknowledgement.CannotPerformNow;
                }
            }
        }

        return new SecsMessage(2, 22, replyExpected: false)
        {
            Name = "RemoteCommandAcknowledge",
            Item = SecsItem.B((byte)cmda).Named("CMDA"),
        };
    }

    /// <summary>Answers S5F17, the one message a host sends about an exception.</summary>
    /// <remarks>
    /// <para>
    /// The body is a bare <c>EXID</c> rather than a list, which is the only row in this family
    /// shaped that way - so a machine that sent it wrapped in a list of one is read too, for the
    /// reason stream 4's link-only rows are: the two encodings are indistinguishable in intent.
    /// </para>
    /// <para>
    /// An identifier this end cannot read is refused rather than passed to the delegate as an empty
    /// string: a handler asked to abort the recovery of "" would have to guess which recovery was
    /// meant, and S5F18 has an <c>ERRTEXT</c> to say so in instead.
    /// </para>
    /// </remarks>
    private SecsMessage OnExceptionAbort(SecsMessage request)
    {
        var item = request.Item is { IsList: true, Count: 1 } wrapped ? wrapped[0] : request.Item;
        var id = item is not null ? GemItems.ReadText(item) : string.Empty;

        var result = id.Length == 0
            ? GemExceptionAbortResult.Refuse(errorText: "S5F17 named no exception")
            : Answer(id);

        return new SecsMessage(5, 18, replyExpected: false)
        {
            Name = "ExceptionRecoverAbortAcknowledge",
            Item = SecsItem.L(
                SecsItem.A(id).Named("EXID"),
                SecsItem.Boolean(result.Accepted).Named("ACKA"),
                SecsItem.U4(result.ErrorCode).Named("ERRCODE"),
                SecsItem.A(result.ErrorText).Named("ERRTEXT")),
        };

        GemExceptionAbortResult Answer(string exceptionId)
        {
            try
            {
                return AnswerExceptionAbort(exceptionId);
            }
            catch (Exception ex)
            {
                // The same position every other application callback in this type is in: a handler
                // that throws has not decided anything, and refusing is the answer that leaves the
                // recovery where it was.
                _logger.LogError(ex, "The handler for aborting exception '{Exception}' threw.", exceptionId);

                return GemExceptionAbortResult.Refuse(errorText: "the handler for this abort threw");
            }
        }
    }

    /// <summary>Answers S1F5 and S1F7 - status in one of this machine's predefined forms.</summary>
    /// <param name="request">The primary, carrying an <c>SFCD</c>.</param>
    /// <param name="replyFunction">6 for values alone, 8 for names with them.</param>
    /// <returns>The reply.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The two rows differ by one item and that is the whole difference.</strong> S1F6
    /// carries <c>SV</c> and S1F8 carries <c>SVNAME</c> and <c>SV</c>, so the same form answers
    /// both - one with names and one without. A host that knows the form sends S1F5 and saves the
    /// names; one meeting a machine for the first time sends S1F7.
    /// </para>
    /// <para>
    /// <strong>A form this machine has not got is a zero-length list</strong>, which is the refusal
    /// these rows have: neither carries an acknowledgement code, exactly as S7F6 and S7F26 do not.
    /// So does a body that is not an <c>SFCD</c> at all - there is nothing else to answer with, and
    /// an empty list is at least true.
    /// </para>
    /// </remarks>
    private SecsMessage OnStatusForm(SecsMessage request, byte replyFunction)
    {
        var values = new List<SecsItem>();

        if (GemItems.TryReadCode(request.Item, out var code)
            && Model.StatusForms.TryGetValue(code, out var variableIds))
        {
            foreach (var variableId in variableIds)
            {
                // Build is what guarantees these exist and are status variables, so there is no
                // unknown-identifier case to answer here - unlike S1F3, which takes identifiers
                // from the host and has to cope with whatever arrives.
                var variable = Model.Variables[variableId];
                var value = variable.Read().Named(variable.Name);

                values.Add(replyFunction == 8
                    ? SecsItem.L(SecsItem.A(variable.Name).Named("SVNAME"), value)
                    : value);
            }
        }

        return new SecsMessage(1, replyFunction, replyExpected: false)
        {
            Name = replyFunction == 6 ? "FormattedStatusData" : "FixedFormData",
            Item = SecsItem.L(values),
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

    private SecsMessage OnAnnotatedIndividualReportRequest(SecsMessage request)
    {
        var ids = ReadIdList(request.Item);
        var reportId = ids.Count > 0 && ids[0] <= uint.MaxValue ? (uint)ids[0] : 0u;

        // No "is this report defined" branch, which is worth a line because the sibling handler
        // above has one and it is dead there too: a report the machine does not define has no
        // variables, so the body comes out as the same empty list either way. A control removed
        // the branch and the whole suite stayed green, which is what a conditional that cannot
        // change the answer looks like from the outside - and leaving it would imply a distinction
        // S6F18 has no way to make, since the row carries no acknowledgement.
        return new SecsMessage(6, 18, replyExpected: false)
        {
            Name = "AnnotatedIndividualReportData",
            Item = SecsItem.L(AnnotatedReportValues(reportId)),
        };
    }

    private SecsMessage OnDataTransferRequest(SecsMessage request)
    {
        var ids = ReadIdList(request.Item);
        var dataId = ids.Count > 0 && ids[0] <= uint.MaxValue ? (uint)ids[0] : 0u;
        var data = AnswerDataTransfer(dataId);

        return new SecsMessage(6, 8, replyExpected: false)
        {
            Name = "DataTransferData",
            // An empty list for an identifier this machine has nothing filed under, which is the
            // shape S6F16 and S6F20 already use for the same answer. The row carries no
            // acknowledgement to put a reason in.
            Item = data is null
                ? SecsItem.L()
                : SecsItem.L(SecsItem.U4(dataId).Named("DATAID"), data),
        };
    }

    private SecsMessage OnNotificationReportRequest(SecsMessage request)
    {
        var ids = ReadIdList(request.Item);
        var eventId = ids.Count > 0 && ids[0] <= uint.MaxValue ? (uint)ids[0] : 0u;

        if (!Model.Events.ContainsKey(eventId))
        {
            return new SecsMessage(6, 28, replyExpected: false)
            {
                Name = "NotificationReportData",
                Item = SecsItem.L(),
            };
        }

        var reports = new List<SecsItem>();

        foreach (var reportId in Model.ReportsForEvent(eventId))
        {
            reports.Add(SecsItem.U4(reportId).Named("RPTID"));
        }

        // Answered for an event with nothing linked to it, and the empty inner list is the answer:
        // "this event exists and no report is attached" is a different fact from "no such event",
        // and the two shapes keep them apart without an acknowledgement item.
        return new SecsMessage(6, 28, replyExpected: false)
        {
            Name = "NotificationReportData",
            Item = SecsItem.L(
                SecsItem.U4(eventId).Named("CEID"),
                SecsItem.L(reports)),
        };
    }

    private static SecsItem StatusNamelistEntry(uint id, GemVariable? variable) => SecsItem.L(
        SecsItem.U4(id).Named("SVID"),
        SecsItem.A(variable?.Name ?? string.Empty).Named("SVNAME"),
        SecsItem.A(variable?.Units ?? string.Empty).Named("UNITS"));

    private static SecsItem DataVariableNamelistEntry(uint id, GemVariable? variable) => SecsItem.L(
        SecsItem.U4(id).Named("VID"),
        SecsItem.A(variable?.Name ?? string.Empty).Named("DVVALNAME"),
        SecsItem.A(variable?.Units ?? string.Empty).Named("UNITS"));

    private static SecsItem CollectionEventNamelistEntry(uint id, GemCollectionEvent? known) => SecsItem.L(
        SecsItem.U4(id).Named("CEID"),
        SecsItem.A(known?.Name ?? string.Empty).Named("CENAME"),
        SecsItem.L([.. (known?.VariableIds ?? []).Select(vid => SecsItem.U4(vid).Named("VID"))]));

    /// <summary>Reads a command name from S2F21's body, which is the name and nothing else.</summary>
    /// <remarks>
    /// A list of one is tolerated, because S2F41 wraps its name in a list of two and a machine
    /// built from that habit may wrap this one as well.
    /// </remarks>
    /// <summary>Narrows a handler's <c>HCACK</c> onto the <c>CMDA</c> a reply can carry.</summary>
    /// <param name="hcack">What the handler answered.</param>
    /// <returns>The code for S2F22, S7F38 or S7F42.</returns>
    /// <remarks>
    /// <para>
    /// Seven codes onto three, in one place because three messages do it. A handler always answers
    /// in <see cref="GemHostCommandAcknowledgement"/>, because that is E30's command model and a
    /// command does not change meaning with the stream that asked for it - but S2F22, S7F38 and
    /// S7F42 all carry <c>CMDA</c>, which names performed, does-not-exist and cannot-perform-now
    /// and nothing else.
    /// </para>
    /// <para>
    /// So "will be performed with completion signalled later" and "already in the desired
    /// condition" both arrive as a refusal, which is not what the machine meant. The loss belongs
    /// to the message rather than to this library, and a host that needs the fuller answer sends
    /// S2F41. What matters here is that the three messages lose it the same way: this used to be
    /// written out inline, and a second copy was added before anybody noticed it was a copy.
    /// </para>
    /// </remarks>
    private static GemRemoteCommandAcknowledgement NarrowToCommandAcknowledgement(
        GemHostCommandAcknowledgement hcack) => hcack switch
    {
        GemHostCommandAcknowledgement.Accepted => GemRemoteCommandAcknowledgement.Performed,
        GemHostCommandAcknowledgement.InvalidCommand => GemRemoteCommandAcknowledgement.DoesNotExist,
        _ => GemRemoteCommandAcknowledgement.CannotPerformNow,
    };

    private static string ReadCommandName(SecsItem? item)
    {
        if (item is null)
        {
            return string.Empty;
        }

        if (item.IsList)
        {
            return item.Count == 1 ? ReadCommandName(item.Items[0]) : string.Empty;
        }

        return item.Format.IsText() && item.Count > 0 ? item.GetString().TrimEnd() : string.Empty;
    }

    /// <summary>Copies a loopback body so the reply owns its own bytes.</summary>
    /// <remarks>
    /// The arriving item borrows pooled memory and is released with its message, so echoing the
    /// item itself would answer with memory that had gone back to the pool. A zero-length body is
    /// echoed as a zero-length binary, which is what <c>ABS</c> is.
    /// </remarks>
    private static SecsItem EchoBody(SecsItem? item)
    {
        if (item is null || item.IsList || item.Count == 0)
        {
            return SecsItem.B().Named("ABS");
        }

        if (item.Format is SecsFormat.Binary && item.TryGetSpan<byte>(out var bytes))
        {
            return SecsItem.B(bytes.ToArray()).Named("ABS");
        }

        return item.Format.IsText()
            ? SecsItem.A(item.GetString()).Named("ABS")
            : SecsItem.B().Named("ABS");
    }

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

    /// <summary>Answers S6F23, and begins the transmit it asked for only once the answer has gone out.</summary>
    /// <remarks>
    /// <para>
    /// <strong>The order is the contract.</strong> The transmit runs on this object's own loop,
    /// which is another thread from the one answering. Queued before the S6F24 is sent, it leaves
    /// the acknowledgement and the first spooled message racing for the connection's one write
    /// lock, and when the loop wins a host is handed an S6F11 ahead of the S6F24 that announced it.
    /// A host that matches a secondary to its primary by system bytes copes. One that takes the
    /// next frame to be its answer reads the data as the answer and reports a reply that never
    /// came, which is how a test host behaves.
    /// </para>
    /// <para>
    /// <see cref="SecsTransaction.ReplyAsync"/> completes once the reply has been flushed and the
    /// write lock released, so anything written to the connection after it returns is behind the
    /// S6F24 on the wire. The transmit is queued after it, and not at all when the reply could not
    /// be sent: a host that never saw RSDA 0 has no reason to expect data and will ask again, and
    /// everything is still held for it.
    /// </para>
    /// <para>
    /// Sending the messages from here would remove the question, and would hold the dispatch loop -
    /// and this very transaction - open well past T3 for a long spool. So the work stays on the
    /// loop and only its start is ordered.
    /// </para>
    /// </remarks>
    private async ValueTask HandleSpooledDataRequestAsync(SecsTransaction transaction, CancellationToken cancellationToken)
    {
        if (_spool is null)
        {
            _logger.LogWarning("S{S}F{F} reached the GEM handler with nothing to answer it.", transaction.S, transaction.F);
            return;
        }

        var acknowledgement = GemSpooledDataAcknowledgement.Denied;
        var transmit = false;

        // RSDC is read tolerantly, like every other numeric field here. It matters more than usual
        // for this one: the reference table in Barbatos.SecsGem.Catalog flags S6F23 as a
        // representative subset and gives its body as DATAID, which is not what E30 says, so a
        // host built from one reading or the other should still be understood.
        if (transaction.Message.Item is { } item && TryReadId(item, out var command))
        {
            switch (command)
            {
                case (ulong)GemSpooledDataRequest.Purge:
                    await _spool.PurgeAsync(cancellationToken).ConfigureAwait(false);
                    OnSpoolEmptied("the host purged the spool");
                    acknowledgement = GemSpooledDataAcknowledgement.Accepted;
                    break;

                case (ulong)GemSpooledDataRequest.Transmit when SpoolCountActual > 0:
                    // Acknowledged now, transmitted later, and "later" is after the reply below.
                    transmit = true;
                    acknowledgement = GemSpooledDataAcknowledgement.Accepted;
                    break;

                default:
                    // Transmit with nothing spooled, or a command E30 does not define. RSDA 1.
                    break;
            }
        }

        // A host that left W clear is not waiting for an answer, and is still entitled to the data
        // it asked for.
        if (transaction.ReplyExpected)
        {
            using var reply = new SecsMessage(6, 24, replyExpected: false)
            {
                Name = "RequestSpooledDataAcknowledge",
                Item = SecsItem.B((byte)acknowledgement).Named("RSDA"),
            };

            await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
        }

        if (transmit)
        {
            _events.Writer.TryWrite(LinkEvent.TransmitSpool);
        }
    }

    /// <summary>Holds a message that could not be delivered. Does nothing when spooling is off.</summary>
    private async ValueTask<bool> SpoolAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!IsSpooling)
        {
            return false;
        }

        if (_spoolSuppressed.ContainsKey((message.S, message.F)))
        {
            // The host asked for this one not to be held - S2F43. Logged because the alternative
            // is invisible: a suppressed report and a report nobody sent leave the same trace,
            // which is none.
            _logger.LogInformation(
                "S{S}F{F} was not spooled, because a host reset spooling for it.",
                message.S,
                message.F);

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

        // And the count last - see the note on OnSpoolEmptied.
        Volatile.Write(ref _spoolActual, after);

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

        // The state before the count, here as everywhere - see the note on OnSpoolEmptied. Writing
        // the count first lets anything watching both see a spool that is empty and active at the
        // same time, which is a state this equipment is never in.
        if (remaining == 0)
        {
            OnSpoolEmptied("the spool was transmitted");
        }
        else
        {
            Volatile.Write(ref _spoolActual, remaining);
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

        // The state and the start time before the count - see the note on OnSpoolEmptied.
        _spoolStart = oldest.SpooledAt;
        SetSpoolState(GemSpoolState.Active, $"{held} message(s) were already held at start-up");

        Volatile.Write(ref _spoolTotal, held);
        Volatile.Write(ref _spoolActual, held);
    }

    /// <summary>Records that nothing is held any more.</summary>
    /// <param name="reason">Why, for the log.</param>
    /// <remarks>
    /// <para>
    /// <strong>The count is published last here, and in every other place that writes it.</strong>
    /// <see cref="SpoolCountActual"/> and <see cref="SpoolState"/> are two views of one fact and a
    /// reader can catch them mid-update. Every write of the count is a <c>Volatile.Write</c> and
    /// every read a <c>Volatile.Read</c>, so a reader that sees a new count also sees everything
    /// written before it - the state and the start time included. That makes "a non-zero count
    /// implies Active or Full" and "a zero count implies Inactive" hold at every instant, for
    /// free, as long as the count goes last.
    /// </para>
    /// <para>
    /// Write it first and the pair is briefly incoherent: a host polling status variables, or a
    /// test polling the count, can read a spool that holds two messages and calls itself Inactive.
    /// Three of the four writers had it the wrong way round, and the full suite found them one at
    /// a time by losing the race under load.
    /// </para>
    /// </remarks>
    private void OnSpoolEmptied(string reason)
    {
        _spoolStart = null;
        SetSpoolState(GemSpoolState.Inactive, reason);
        Volatile.Write(ref _spoolActual, 0);
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

    /// <summary>The S6F18 body: every value of a report with its identifier and units attached.</summary>
    /// <remarks>
    /// <para>
    /// The same values <see cref="ReportValues"/> gathers, wrapped one level deeper.
    /// </para>
    /// <para>
    /// <strong>The empty-list fallback is unreachable through this library</strong> and is kept as
    /// defence rather than as behaviour, which is worth saying because the obvious reading is that
    /// it is a live path. <c>GemEquipmentModelBuilder.Build</c> refuses a report that names a
    /// variable the model does not hold, so there is no way to reach a machine where one is
    /// missing. <see cref="ReportValues"/> has carried the same unreachable fallback since it was
    /// written. <c>GemStream6Tests</c> asserts the refusal, so the claim is checked rather than
    /// assumed.
    /// </para>
    /// </remarks>
    private List<SecsItem> AnnotatedReportValues(uint reportId)
    {
        var values = new List<SecsItem>();

        foreach (var variableId in Model.VariablesForReport(reportId))
        {
            var value = SecsItem.L();
            var units = string.Empty;

            if (Model.Variables.TryGetValue(variableId, out var variable))
            {
                value = variable.Read().Named(variable.Name);
                units = variable.Units;
            }

            values.Add(SecsItem.L(
                SecsItem.U4(variableId).Named("VID"),
                value,
                SecsItem.A(units).Named("UNITS")));
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
        TraceDue,
    }

    /// <summary>One running trace: its definition, its timer, and the group it is filling.</summary>
    /// <remarks>
    /// The samples in <see cref="Group"/> are live items read from the model, so a trace holds
    /// pooled memory between reports - <see cref="GemTraceRequest.GroupSize"/> samples of it at
    /// most, which is why the group size is validated rather than trusted. Disposing the run
    /// returns them.
    /// </remarks>
    private sealed class TraceRun(GemEquipment owner, GemTraceRequest request) : IDisposable
    {
        internal GemEquipment Owner { get; } = owner;

        internal GemTraceRequest Request { get; } = request;

        internal List<SecsItem> Group { get; } = [];

        internal ITimer? Timer { get; set; }

        internal int Taken { get; set; }

        internal DateTimeOffset LastSampleAt { get; set; }

        public void Dispose()
        {
            Timer?.Dispose();
            Timer = null;

            foreach (var sample in Group)
            {
                sample.Dispose();
            }

            Group.Clear();
        }
    }
}
