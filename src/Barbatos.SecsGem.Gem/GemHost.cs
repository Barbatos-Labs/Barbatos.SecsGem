// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;
using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// The host side of a GEM conversation: the other end of <see cref="GemEquipment"/>.
/// </summary>
/// <remarks>
/// <para>
/// SEMI E30 models the <em>equipment</em>. A host has no control state to defend, no reports of its
/// own and nothing to spool - it sends primaries and reads replies, which the HSMS layer already
/// does. So this class is not a second state machine. It is the GEM message set written down once
/// from the other direction, which turns out to be most of what a host application was going to
/// write by hand: building the body, checking the reply is the shape E5 defines, and turning an
/// acknowledgement byte into something that cannot be read backwards.
/// </para>
/// <para>
/// That last part is the reason it exists. <c>HCACK</c> 4 means accepted and <c>HCACK</c> 1 means
/// refused; <c>DRACK</c> 0 means accepted and <c>LRACK</c> 0 means accepted and <c>PPGNT</c> 0
/// means send it. A host written against <c>reply.Item?.FirstValueOrDefault&lt;byte&gt;()</c> is one
/// typo away from treating a refusal as a success, and no test it writes will tell it so.
/// </para>
/// <para>
/// <strong>What it covers.</strong> Every primary <see cref="GemEquipment"/> answers - the twenty
/// in <see cref="GemEquipment.HandledMessages"/>, the five in
/// <see cref="GemEquipment.ProcessProgramMessages"/>, the two in
/// <see cref="GemEquipment.FormattedProcessProgramMessages"/> and the one in
/// <see cref="GemEquipment.SpoolMessages"/> - has a method here, and a test asserts that rather
/// than trusting it. The equipment's own primaries are answered by
/// <see cref="HandleAsync"/>: S1F13, S1F1, S5F1, S6F11, S6F13 and stream 9.
/// </para>
/// <para>
/// <strong>Who owns the reply.</strong> Methods whose answer is an acknowledgement code, a list of
/// names or any other shape E5 pins down return that, decoded, and dispose the reply themselves.
/// The four whose answer is equipment data of a shape only the equipment knows - S1F4, S2F14,
/// S6F16 and S6F20 - return the <see cref="SecsMessage"/> itself, and the caller disposes it. A
/// typed wrapper over "whatever this machine calls a value" would be a guess with an API attached.
/// </para>
/// <para>
/// One instance sits on one <see cref="HsmsConnection"/>. It does not run the connection, it uses
/// it - and unlike the equipment it has no loop of its own to run, because a host is an
/// application that already has its own control flow. E30 gives the <em>equipment</em> the duty of
/// retrying until communications are established; a host decides when it wants to talk.
/// </para>
/// </remarks>
public sealed class GemHost : IAsyncDisposable
{
    private readonly GemHostOptions _options;
    private readonly HsmsConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private int _dataId;
    private bool _communicating;
    private bool _disposed;

    /// <summary>Initialises the host over a connection.</summary>
    /// <param name="options">The host options.</param>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="timeProvider">The clock, for the timestamp S2F31 sets. <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> or <paramref name="connection"/> is <see langword="null"/>.</exception>
    public GemHost(
        GemHostOptions options,
        HsmsConnection connection,
        TimeProvider? timeProvider = null,
        ILogger<GemHost>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<GemHost>.Instance;

        // Subscribed here rather than in a run loop, because there is no run loop. The only thing
        // this listens for is the link going away: communications established over a link that has
        // since dropped are not established, and a host that still believed they were would send
        // into a closed socket and blame the equipment.
        _connection.StateChanged += OnConnectionStateChanged;
    }

    /// <summary>Every primary this object answers.</summary>
    /// <remarks>
    /// <para>
    /// Stream 9 is in here, and it matters: those messages expect no reply, but an entity that has
    /// not declared stream 9 answers an incoming S9F5 with another S9F5, and the two ends then
    /// explain the problem to each other until something gives out.
    /// </para>
    /// <para>
    /// S1F1 is here too. A machine is entitled to ask a host whether it is there.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (1, 1),    // Are You There
        (1, 13),   // Establish Communications Request
        (5, 1),    // Alarm Report Send
        (6, 1),    // Trace Data Send
        (6, 11),   // Event Report Send
        (6, 13),   // Annotated Event Report Send
        (10, 1),   // Terminal Request
        (9, 1),    // Unrecognized Device ID
        (9, 3),    // Unrecognized Stream Type
        (9, 5),    // Unrecognized Function Type
        (9, 7),    // Illegal Data
        (9, 9),    // Transaction Timer Timeout
        (9, 11),   // Data Too Long
        (9, 13),   // Conversation Timeout
    ];

    /// <summary>Raised after an event report arrives, before it is acknowledged.</summary>
    /// <remarks>
    /// The handler may set <see cref="GemEventReportEventArgs.Acknowledgement"/>. Everything it
    /// reads out of <see cref="GemEventReportEventArgs.Reports"/> is alive only until it returns.
    /// </remarks>
    public event EventHandler<GemEventReportEventArgs>? EventReportReceived;

    /// <summary>Raised after an alarm arrives, before it is acknowledged.</summary>
    public event EventHandler<GemHostAlarmEventArgs>? AlarmReceived;

    /// <summary>Raised when the equipment reports an error in stream 9.</summary>
    public event EventHandler<GemHostErrorEventArgs>? ErrorReported;

    /// <summary>Raised when a group of trace samples arrives - S6F1.</summary>
    /// <remarks>
    /// Everything in <see cref="GemTraceDataEventArgs.Samples"/> is alive only until the handler
    /// returns. A trace of one variable every second for an hour is 3,600 samples: read what you
    /// are keeping and keep the value, not the item.
    /// </remarks>
    public event EventHandler<GemTraceDataEventArgs>? TraceDataReceived;

    /// <summary>Raised when an operator at the machine sends the host something - S10F1.</summary>
    /// <remarks>
    /// There is a person waiting on the other end of this one, so a handler that cannot deal with
    /// the message now should say so through
    /// <see cref="GemTerminalRequestEventArgs.Acknowledgement"/> rather than accept it and drop it.
    /// </remarks>
    public event EventHandler<GemTerminalRequestEventArgs>? TerminalRequestReceived;

    /// <summary>Raised after the host's communication state changes.</summary>
    public event EventHandler<GemHostCommunicationStateChangedEventArgs>? CommunicationStateChanged;

    /// <summary>Gets a value indicating whether application messages may flow.</summary>
    /// <remarks>
    /// Set by an accepted S1F13 in either direction, and cleared when the link leaves Selected.
    /// <strong>Not</strong> the same question as <see cref="HsmsConnection.IsSelected"/>: a
    /// selected link is a socket, and this is an agreement.
    /// </remarks>
    public bool IsCommunicating => Volatile.Read(ref _communicating);

    // ----------------------------------------------------------------- inbound

    /// <summary>Handles one inbound primary. Wired to every pair in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The primary and the means to answer it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the reply has been sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Stream 9 before the switch, because it is the one family that is complete without a
        // reply. Answering an error report would be answering a message that reported a message.
        if (transaction.S == 9)
        {
            OnErrorReport(transaction.Message);
            return;
        }

        var reply = (transaction.S, transaction.F) switch
        {
            (1, 1) => OnAreYouThere(),
            (1, 13) => OnEstablishCommunications(),
            (5, 1) => OnAlarmReport(transaction.Message),
            (6, 1) => OnTraceData(transaction.Message),
            (6, 11) => OnEventReport(transaction.Message, 12),
            (6, 13) => OnEventReport(transaction.Message, 14),
            (10, 1) => OnTerminalRequest(transaction.Message),
            _ => null,
        };

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the GEM host handler with nothing to answer it.",
                transaction.S,
                transaction.F);

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

    // ----------------------------------------------------------------- stream 1

    /// <summary>Asks the equipment whether it is there - S1F1.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The equipment's identity, or <see langword="null"/> when it answered a zero-length list.</returns>
    /// <remarks>
    /// <see langword="null"/> is not a failure. E30 has off-line equipment answer S1F1 with a
    /// zero-length list rather than its identity, which is how a host learns the machine is alive
    /// but not available without asking a second question.
    /// </remarks>
    public async ValueTask<GemEquipmentIdentity?> AreYouThereAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(1, 1, replyExpected: true) { Name = "AreYouThere" },
            cancellationToken).ConfigureAwait(false);

        return ReadIdentity(reply.Item);
    }

    /// <summary>Establishes communications - S1F13.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>COMMACK.</returns>
    /// <remarks>
    /// An accepted answer sets <see cref="IsCommunicating"/>. A denial does not: the equipment has
    /// said it is not ready, and E30 leaves it to try again.
    /// </remarks>
    public async ValueTask<GemCommunicationAcknowledgement> EstablishCommunicationsAsync(
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(1, 13, replyExpected: true)
            {
                Name = "EstablishCommunicationsRequest",
                Item = Identity(),
            },
            cancellationToken).ConfigureAwait(false);

        // S1F14 is L[2]{COMMACK, identity}, so the code is inside the list rather than the body.
        var commack = (GemCommunicationAcknowledgement)ReadCode(reply, 0);

        if (commack == GemCommunicationAcknowledgement.Accepted)
        {
            SetCommunicating(true, "S1F14 accepted the host's request");
        }

        return commack;
    }

    /// <summary>Reads status variables - S1F3.</summary>
    /// <param name="svids">The identifiers. An empty list asks for every status variable.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>S1F4. <strong>The caller owns it</strong> and must dispose it.</returns>
    /// <remarks>
    /// The body is a list with one item per identifier requested, in the order requested. E5
    /// answers an unknown identifier with a zero-length item in its place rather than by leaving it
    /// out, so the reply stays aligned with the request - which is the only reason the positions
    /// can be trusted.
    /// </remarks>
    public ValueTask<SecsMessage> ReadStatusAsync(
        IReadOnlyList<ulong> svids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(svids);

        return RequestAsync(
            new SecsMessage(1, 3, replyExpected: true)
            {
                Name = "SelectedEquipmentStatusRequest",
                Item = GemItems.IdList(svids),
            },
            cancellationToken);
    }

    /// <summary>Reads the status variable namelist - S1F11.</summary>
    /// <param name="svids">The identifiers. An empty list asks for every status variable.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One description per entry, in the order the equipment gave them.</returns>
    public async ValueTask<IReadOnlyList<GemVariableDescription>> ReadStatusNamelistAsync(
        IReadOnlyList<ulong> svids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(svids);

        using var reply = await RequestAsync(
            new SecsMessage(1, 11, replyExpected: true)
            {
                Name = "StatusVariableNamelistRequest",
                Item = GemItems.IdList(svids),
            },
            cancellationToken).ConfigureAwait(false);

        List<GemVariableDescription> descriptions = [];

        foreach (var entry in Entries(reply))
        {
            if (entry is { IsList: true, Count: 3 } && GemItems.TryReadId(entry[0], out var id))
            {
                descriptions.Add(new GemVariableDescription(
                    id,
                    GemItems.ReadText(entry[1]),
                    GemItems.ReadText(entry[2])));
            }
        }

        return descriptions;
    }

    /// <summary>Asks the equipment to go off-line - S1F15.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>OFLACK.</returns>
    public async ValueTask<GemOfflineAcknowledgement> RequestOfflineAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(1, 15, replyExpected: true) { Name = "RequestOffLine" },
            cancellationToken).ConfigureAwait(false);

        return (GemOfflineAcknowledgement)ReadCode(reply);
    }

    /// <summary>Asks the equipment to go on-line - S1F17.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ONLACK.</returns>
    /// <remarks>
    /// <see cref="GemOnlineAcknowledgement.AlreadyOnline"/> is not a failure, and E30 is explicit
    /// about that: the host asked for a state the machine is already in.
    /// </remarks>
    public async ValueTask<GemOnlineAcknowledgement> RequestOnlineAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(1, 17, replyExpected: true) { Name = "RequestOnLine" },
            cancellationToken).ConfigureAwait(false);

        return (GemOnlineAcknowledgement)ReadCode(reply);
    }

    // ----------------------------------------------------------------- stream 2

    /// <summary>Reads equipment constants - S2F13.</summary>
    /// <param name="ecids">The identifiers. An empty list asks for every constant.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>S2F14. <strong>The caller owns it</strong> and must dispose it.</returns>
    public ValueTask<SecsMessage> ReadConstantsAsync(
        IReadOnlyList<ulong> ecids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ecids);

        return RequestAsync(
            new SecsMessage(2, 13, replyExpected: true)
            {
                Name = "EquipmentConstantRequest",
                Item = GemItems.IdList(ecids),
            },
            cancellationToken);
    }

    /// <summary>Writes equipment constants - S2F15.</summary>
    /// <param name="values">The identifiers and their new values.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>EAC.</returns>
    /// <remarks>
    /// <para>
    /// The value items are placed in the request and <strong>disposed with it</strong>, which is
    /// how <see cref="SecsMessage"/> owns its body everywhere else in this stack. Build them for
    /// the call; do not hold them afterwards.
    /// </para>
    /// <para>
    /// E30 makes this all-or-nothing: an equipment that refuses one constant applies none of them.
    /// </para>
    /// </remarks>
    public async ValueTask<GemEquipmentConstantAcknowledgement> WriteConstantsAsync(
        IReadOnlyList<(ulong Id, SecsItem Value)> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var entries = new SecsItem[values.Count];

        for (var i = 0; i < values.Count; i++)
        {
            entries[i] = SecsItem.L(
                GemItems.IdItem(values[i].Id).Named("ECID"),
                values[i].Value.Named("ECV"));
        }

        using var reply = await RequestAsync(
            new SecsMessage(2, 15, replyExpected: true)
            {
                Name = "NewEquipmentConstantSend",
                Item = SecsItem.L(entries),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemEquipmentConstantAcknowledgement)ReadCode(reply);
    }

    /// <summary>Sets the limit bands on some variables - S2F45.</summary>
    /// <param name="definitions">
    /// The bands. A variable with no limits has its limits removed; an empty list removes every
    /// limit on every variable.
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>VLAACK, and what happened to each variable and each band.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definitions"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F46 was not the shape E5 defines.</exception>
    /// <remarks>
    /// <para>
    /// Accepted means the bands are in force, not that anything has crossed one. The crossings
    /// arrive afterwards as the collection event the equipment documents for the variable, which
    /// is why S2F45 carries no CEID: the band is the host's and the event is the machine's.
    /// </para>
    /// <para>
    /// The detail is per variable and per band, because a host that sent twenty needs to know
    /// which one it got wrong rather than that something was.
    /// </para>
    /// </remarks>
    public async ValueTask<GemVariableLimitReply> DefineVariableLimitsAsync(
        IReadOnlyList<GemVariableLimitDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        using var reply = await RequestAsync(
            new SecsMessage(2, 45, replyExpected: true)
            {
                Name = "DefineVariableLimitAttributes",
                Item = GemLimitCodec.WriteDefinitions(NextDataId(), definitions),
            },
            cancellationToken).ConfigureAwait(false);

        return GemLimitCodec.TryReadResults(reply.Item)
            ?? throw new SecsException("S2F46 was not the shape E5 defines for it.");
    }

    /// <summary>Removes every limit on every variable - S2F45 with an empty list.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>VLAACK, and nothing else: there is no variable left to report on.</returns>
    public ValueTask<GemVariableLimitReply> DeleteAllVariableLimitsAsync(CancellationToken cancellationToken = default)
        => DefineVariableLimitsAsync([], cancellationToken);

    /// <summary>Asks what limits are in force - S2F47.</summary>
    /// <param name="variableIds">The variables. An empty list asks about every one that has limits.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One entry per variable the equipment reported.</returns>
    /// <remarks>
    /// The entries carry the variable's own documented range as well as the bands in force, which
    /// is what tells a host whether the band it is about to ask for is one the value could ever be
    /// outside. Equipment that documents no range reports a zero-length item for it, and
    /// <see cref="GemVariableLimitAttributes.Minimum"/> is <see langword="null"/> for that.
    /// </remarks>
    public async ValueTask<IReadOnlyList<GemVariableLimitAttributes>> ReadVariableLimitsAsync(
        IReadOnlyList<ulong> variableIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variableIds);

        using var reply = await RequestAsync(
            new SecsMessage(2, 47, replyExpected: true)
            {
                Name = "VariableLimitAttributeRequest",
                Item = GemItems.IdList(variableIds),
            },
            cancellationToken).ConfigureAwait(false);

        return GemLimitCodec.ReadAttributes(reply.Item);
    }

    /// <summary>Starts a trace - S2F23.</summary>
    /// <param name="trace">What to sample, how often, and how many samples travel together.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>TIAACK.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="trace"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The period is not positive, or is longer than the <c>hhmmsscc</c> field can carry.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Refused where it would be refused: DSPER travels as six or eight characters, so a period
    /// finer than a hundredth of a second rounds away and one of a hundred hours or more does not
    /// fit at all. Both are caught here rather than sent and denied, because a TIAACK 3 for a
    /// period the host could have checked is a round trip spent on arithmetic.
    /// </para>
    /// <para>
    /// The samples arrive afterwards as <see cref="TraceDataReceived"/>, in groups of REPGSZ.
    /// </para>
    /// </remarks>
    public async ValueTask<GemTraceAcknowledgement> StartTraceAsync(
        GemTraceRequest trace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(trace.Period, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trace.Period.TotalHours, 99d);

        return await InitializeTraceAsync(
            trace.Id,
            WritePeriod(trace.Period),
            trace.TotalSamples,
            trace.GroupSize,
            trace.VariableIds,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops a trace - S2F23 asking for no samples.</summary>
    /// <param name="traceId">TRID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>TIAACK.</returns>
    /// <remarks>
    /// E5 defines S2F23 as <em>initialising</em> a trace, so re-initialising one to take no
    /// samples and cancelling it are the same request - see <see cref="GemTraceRequest"/> for why
    /// that reading is the safe one. Accepted for a trace that was not running: the host wanted it
    /// stopped, and it is stopped.
    /// </remarks>
    public ValueTask<GemTraceAcknowledgement> StopTraceAsync(
        uint traceId,
        CancellationToken cancellationToken = default)
        => InitializeTraceAsync(traceId, "000001", 0, 0, [], cancellationToken);

    /// <summary>Reads the equipment's clock - S2F17.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The instant in UTC, or <see langword="null"/> when TIME was not one of E5's formats.</returns>
    public async ValueTask<DateTimeOffset?> ReadClockAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(2, 17, replyExpected: true) { Name = "DateAndTimeRequest" },
            cancellationToken).ConfigureAwait(false);

        if (reply.Item is not { } item || !item.Format.IsText())
        {
            return null;
        }

        return GemItems.TryParseTimestamp(item.GetString(), out var value)
            ? new DateTimeOffset(value, TimeSpan.Zero)
            : null;
    }

    /// <summary>Sets the equipment's clock - S2F31.</summary>
    /// <param name="when">The instant. <see langword="null"/> sends this host's current time.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>TIACK.</returns>
    /// <remarks>
    /// Sent in E5's sixteen-character form, which carries hundredths. The equipment keeps the
    /// difference as an offset rather than touching the operating system clock.
    /// </remarks>
    public async ValueTask<GemTimeAcknowledgement> SetClockAsync(
        DateTimeOffset? when = null,
        CancellationToken cancellationToken = default)
    {
        var instant = (when ?? _timeProvider.GetUtcNow()).UtcDateTime;

        using var reply = await RequestAsync(
            new SecsMessage(2, 31, replyExpected: true)
            {
                Name = "DateAndTimeSetRequest",
                Item = SecsItem.A(instant.ToString("yyyyMMddHHmmssff", CultureInfo.InvariantCulture)).Named("TIME"),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemTimeAcknowledgement)ReadCode(reply);
    }

    /// <summary>Reads the equipment constant namelist - S2F29.</summary>
    /// <param name="ecids">The identifiers. An empty list asks for every constant.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One description per entry, in the order the equipment gave them.</returns>
    public async ValueTask<IReadOnlyList<GemConstantDescription>> ReadConstantNamelistAsync(
        IReadOnlyList<ulong> ecids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ecids);

        using var reply = await RequestAsync(
            new SecsMessage(2, 29, replyExpected: true)
            {
                Name = "EquipmentConstantNamelistRequest",
                Item = GemItems.IdList(ecids),
            },
            cancellationToken).ConfigureAwait(false);

        List<GemConstantDescription> descriptions = [];

        foreach (var entry in Entries(reply))
        {
            if (entry is { IsList: true, Count: 6 } && GemItems.TryReadId(entry[0], out var id))
            {
                descriptions.Add(new GemConstantDescription(
                    id,
                    GemItems.ReadText(entry[1]),
                    GemItems.ReadText(entry[5]),
                    Encode(entry[2]),
                    Encode(entry[3]),
                    Encode(entry[4])));
            }
        }

        return descriptions;
    }

    /// <summary>Defines reports - S2F33.</summary>
    /// <param name="reports">Each report and the variables it carries, in order.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>DRACK.</returns>
    /// <remarks>
    /// A report that is already defined is redefined. To remove one, define it with no variables;
    /// to remove all of them, call <see cref="DeleteAllReportsAsync"/>.
    /// </remarks>
    public async ValueTask<GemDefineReportAcknowledgement> DefineReportsAsync(
        IReadOnlyList<(ulong ReportId, IReadOnlyList<ulong> VariableIds)> reports,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reports);

        var definitions = new SecsItem[reports.Count];

        for (var i = 0; i < reports.Count; i++)
        {
            definitions[i] = SecsItem.L(
                GemItems.IdItem(reports[i].ReportId).Named("RPTID"),
                GemItems.IdList(reports[i].VariableIds));
        }

        return await SendReportDefinitionsAsync(SecsItem.L(definitions), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes every report definition - S2F33 with an empty list.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>DRACK.</returns>
    /// <remarks>
    /// E30 gives the empty list this meaning, and it is the one place where sending what the host
    /// literally asked for would do far less than it meant. Worth doing before defining a set, so
    /// a host inherits nothing from whoever had the link last.
    /// </remarks>
    public ValueTask<GemDefineReportAcknowledgement> DeleteAllReportsAsync(CancellationToken cancellationToken = default)
        => SendReportDefinitionsAsync(SecsItem.L(), cancellationToken);

    /// <summary>Links reports to collection events - S2F35.</summary>
    /// <param name="links">Each event and the reports it should carry.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>LRACK.</returns>
    public async ValueTask<GemLinkReportAcknowledgement> LinkEventsAsync(
        IReadOnlyList<(ulong EventId, IReadOnlyList<ulong> ReportIds)> links,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(links);

        var entries = new SecsItem[links.Count];

        for (var i = 0; i < links.Count; i++)
        {
            entries[i] = SecsItem.L(
                GemItems.IdItem(links[i].EventId).Named("CEID"),
                GemItems.IdList(links[i].ReportIds));
        }

        using var reply = await RequestAsync(
            new SecsMessage(2, 35, replyExpected: true)
            {
                Name = "LinkEventReport",
                Item = SecsItem.L(NextDataId(), SecsItem.L(entries)),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemLinkReportAcknowledgement)ReadCode(reply);
    }

    /// <summary>Enables or disables collection events - S2F37.</summary>
    /// <param name="enable">Whether the equipment should report them.</param>
    /// <param name="ceids">The events. An empty list means every event the equipment has.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ERACK.</returns>
    public async ValueTask<GemEnableEventAcknowledgement> EnableEventsAsync(
        bool enable,
        IReadOnlyList<ulong> ceids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ceids);

        using var reply = await RequestAsync(
            new SecsMessage(2, 37, replyExpected: true)
            {
                Name = "EnableDisableEventReport",
                Item = SecsItem.L(SecsItem.Boolean(enable).Named("CEED"), GemItems.IdList(ceids)),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemEnableEventAcknowledgement)ReadCode(reply);
    }

    /// <summary>Sends a remote command - S2F41.</summary>
    /// <param name="command">RCMD.</param>
    /// <param name="parameters">The CPNAME and CPVAL pairs, which may be empty.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>HCACK.</returns>
    /// <remarks>
    /// <para>
    /// <strong>HCACK 4 is a success.</strong> Zero means the command is done and four means it has
    /// been accepted and will finish later, which a host learns about through a collection event.
    /// Anything else is a refusal. This is the acknowledgement code most likely to be read
    /// backwards, which is most of the reason this class exists.
    /// </para>
    /// <para>
    /// Parameter values are text. E5 allows CPVAL to be any item, and a host that needs one builds
    /// the S2F41 itself - but a remote command whose parameters are a lot number and a recipe name
    /// is the overwhelming majority of them.
    /// </para>
    /// </remarks>
    public async ValueTask<GemHostCommandAcknowledgement> SendCommandAsync(
        string command,
        IReadOnlyList<(string Name, string Value)> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(parameters);

        var entries = new SecsItem[parameters.Count];

        for (var i = 0; i < parameters.Count; i++)
        {
            entries[i] = SecsItem.L(
                SecsItem.A(parameters[i].Name).Named("CPNAME"),
                SecsItem.A(parameters[i].Value).Named("CPVAL"));
        }

        using var reply = await RequestAsync(
            new SecsMessage(2, 41, replyExpected: true)
            {
                Name = "HostCommandSend",
                Item = SecsItem.L(SecsItem.A(command).Named("RCMD"), SecsItem.L(entries)),
            },
            cancellationToken).ConfigureAwait(false);

        // S2F42 is L[2]{HCACK, L[n]{parameter acknowledgements}}, so the code is inside the list.
        var hcack = ReadCode(reply, 0);

        if (hcack < (byte)GemHostCommandAcknowledgement.Other)
        {
            return (GemHostCommandAcknowledgement)hcack;
        }

        // E5 reserves 7 to 63 for the equipment. Reading one of those as itself would hand back an
        // enum value with no name, which is what this used to do.
        _logger.LogInformation(
            "S2F42 carried HCACK {Code}, which E5 reserves for the equipment and does not name.",
            hcack);

        return GemHostCommandAcknowledgement.Other;
    }

    // ----------------------------------------------------------------- stream 5

    /// <summary>Enables or disables an alarm - S5F3.</summary>
    /// <param name="enable">Whether the equipment should report it.</param>
    /// <param name="alarmId">The alarm, or zero for every alarm.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC5.</returns>
    /// <remarks>
    /// ALED is sent as E5 defines it: a binary item whose top bit is the choice. Zero as the
    /// identifier means all of them, which E5 also allows as a zero-length ALID.
    /// </remarks>
    public async ValueTask<GemAlarmAcknowledgement> EnableAlarmAsync(
        bool enable,
        ulong alarmId = 0,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(5, 3, replyExpected: true)
            {
                Name = "EnableDisableAlarmSend",
                Item = SecsItem.L(
                    SecsItem.B(enable ? (byte)0x80 : (byte)0x00).Named("ALED"),
                    alarmId == 0 ? SecsItem.L() : GemItems.IdItem(alarmId).Named("ALID")),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemAlarmAcknowledgement)ReadCode(reply);
    }

    /// <summary>Lists alarms and whether each is in alarm - S5F5.</summary>
    /// <param name="alarmIds">The alarms. An empty list asks for every alarm the equipment has.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One report per entry.</returns>
    public ValueTask<IReadOnlyList<GemAlarmReport>> ListAlarmsAsync(
        IReadOnlyList<ulong> alarmIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alarmIds);

        return ReadAlarmListAsync(
            new SecsMessage(5, 5, replyExpected: true)
            {
                Name = "ListAlarmsRequest",
                Item = GemItems.IdList(alarmIds),
            },
            cancellationToken);
    }

    /// <summary>Lists the alarms the equipment is allowed to report - S5F7.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One report per enabled alarm.</returns>
    /// <remarks>
    /// A different question from <see cref="ListAlarmsAsync"/>: that one answers which alarms are
    /// in alarm, and this one answers which the host has asked to hear about.
    /// </remarks>
    public ValueTask<IReadOnlyList<GemAlarmReport>> ListEnabledAlarmsAsync(CancellationToken cancellationToken = default)
        => ReadAlarmListAsync(
            new SecsMessage(5, 7, replyExpected: true) { Name = "ListEnabledAlarmRequest" },
            cancellationToken);

    // ----------------------------------------------------------------- stream 6

    /// <summary>Asks for one collection event's reports - S6F15.</summary>
    /// <param name="eventId">CEID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>S6F16. <strong>The caller owns it</strong> and must dispose it.</returns>
    /// <remarks>A zero-length body means the equipment does not have that event.</remarks>
    public ValueTask<SecsMessage> RequestEventReportAsync(
        ulong eventId,
        CancellationToken cancellationToken = default)
        => RequestAsync(
            new SecsMessage(6, 15, replyExpected: true)
            {
                Name = "EventReportRequest",
                Item = GemItems.IdItem(eventId).Named("CEID"),
            },
            cancellationToken);

    /// <summary>Asks for one report - S6F19.</summary>
    /// <param name="reportId">RPTID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>S6F20. <strong>The caller owns it</strong> and must dispose it.</returns>
    /// <remarks>A zero-length body means that report is not defined.</remarks>
    public ValueTask<SecsMessage> RequestReportAsync(
        ulong reportId,
        CancellationToken cancellationToken = default)
        => RequestAsync(
            new SecsMessage(6, 19, replyExpected: true)
            {
                Name = "IndividualReportRequest",
                Item = GemItems.IdItem(reportId).Named("RPTID"),
            },
            cancellationToken);

    /// <summary>Asks for the spooled data, or tells the equipment to throw it away - S6F23.</summary>
    /// <param name="request">RSDC.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>RSDA.</returns>
    /// <remarks>
    /// <para>
    /// Accepted means the equipment agreed, not that the messages have arrived. A transmit is
    /// acknowledged now and sent afterwards - inside the handler it would hold the link, and this
    /// very transaction, well past T3 - so the reports arrive as ordinary primaries after this
    /// returns.
    /// </para>
    /// <para>
    /// Denied means there was nothing spooled, or the equipment was busy. Equipment that does not
    /// spool at all answers S9F5 instead, which arrives as <see cref="ErrorReported"/> and throws
    /// here: "I do not do that" and "no, not now" are different answers, and a host deciding
    /// whether to wait needs them apart.
    /// </para>
    /// </remarks>
    public async ValueTask<GemSpooledDataAcknowledgement> RequestSpooledDataAsync(
        GemSpooledDataRequest request,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(6, 23, replyExpected: true)
            {
                Name = "RequestSpooledData",
                Item = SecsItem.U1((byte)request).Named("RSDC"),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemSpooledDataAcknowledgement)ReadCode(reply);
    }

    // ----------------------------------------------------------------- stream 10

    /// <summary>Shows one block of text on an operator's terminal - S10F3.</summary>
    /// <param name="text">TEXT.</param>
    /// <param name="terminalId">TID. Zero is the usual value for equipment with one terminal.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC10.</returns>
    /// <remarks>
    /// Accepted means the equipment took it for display, not that anybody has read it.
    /// <see cref="GemTerminalAcknowledgement.WillNotDisplay"/> is a decision the machine made and
    /// <see cref="GemTerminalAcknowledgement.TerminalNotAvailable"/> is a fault - only the second
    /// is worth retrying.
    /// </remarks>
    public async ValueTask<GemTerminalAcknowledgement> DisplayTerminalMessageAsync(
        string text,
        byte terminalId = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var reply = await RequestAsync(
            new SecsMessage(10, 3, replyExpected: true)
            {
                Name = "TerminalDisplaySingle",
                Item = SecsItem.L(
                    SecsItem.B(terminalId).Named("TID"),
                    SecsItem.A(text).Named("TEXT")),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemTerminalAcknowledgement)ReadCode(reply);
    }

    /// <summary>Shows several blocks of text on an operator's terminal - S10F5.</summary>
    /// <param name="lines">The blocks, in order.</param>
    /// <param name="terminalId">TID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC10.</returns>
    /// <remarks>
    /// A one-entry list still sends S10F5. Which function carries one block is the caller's
    /// choice, and this stack's equipment reads either - but a host talking to somebody else's
    /// machine should send the single-block form for a single block.
    /// </remarks>
    public async ValueTask<GemTerminalAcknowledgement> DisplayTerminalMessageAsync(
        IReadOnlyList<string> lines,
        byte terminalId = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var blocks = new SecsItem[lines.Count];

        for (var i = 0; i < lines.Count; i++)
        {
            blocks[i] = SecsItem.A(lines[i]).Named("TEXT");
        }

        using var reply = await RequestAsync(
            new SecsMessage(10, 5, replyExpected: true)
            {
                Name = "MultiBlockTerminalDisplay",
                Item = SecsItem.L(SecsItem.B(terminalId).Named("TID"), SecsItem.L(blocks)),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemTerminalAcknowledgement)ReadCode(reply);
    }

    /// <summary>Shows text on every terminal the equipment has - S10F9.</summary>
    /// <param name="text">TEXT.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC10.</returns>
    /// <remarks>
    /// There is no TID in a broadcast: it is for all of them, which is why this takes no terminal
    /// and why equipment with one terminal cannot tell a broadcast from a display without looking
    /// at <see cref="GemTerminalMessage.IsBroadcast"/>.
    /// </remarks>
    public async ValueTask<GemTerminalAcknowledgement> BroadcastAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        using var reply = await RequestAsync(
            new SecsMessage(10, 9, replyExpected: true)
            {
                Name = "BroadcastMessage",
                Item = SecsItem.A(text).Named("TEXT"),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemTerminalAcknowledgement)ReadCode(reply);
    }

    // ----------------------------------------------------------------- stream 7

    /// <summary>Asks whether the equipment will accept a process program - S7F1.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="length">How many bytes the body will be.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>PPGNT.</returns>
    /// <remarks>
    /// Worth asking first when a recipe is large: the point of S7F1 existing is that the equipment
    /// gets to refuse before the bytes are on the wire rather than after.
    /// </remarks>
    public async ValueTask<GemProcessProgramGrant> InquireProcessProgramAsync(
        string id,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        using var reply = await RequestAsync(
            new SecsMessage(7, 1, replyExpected: true)
            {
                Name = "ProcessProgramLoadInquire",
                Item = SecsItem.L(SecsItem.A(id).Named("PPID"), SecsItem.U4((uint)length).Named("LENGTH")),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramGrant)ReadCode(reply);
    }

    /// <summary>Sends a process program - S7F3.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="body">PPBODY.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC7.</returns>
    public async ValueTask<GemProcessProgramAcknowledgement> SendProcessProgramAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        using var reply = await RequestAsync(
            new SecsMessage(7, 3, replyExpected: true)
            {
                Name = "ProcessProgramSend",
                Item = SecsItem.L(
                    SecsItem.A(id).Named("PPID"),
                    SecsItem.B(body.ToArray()).Named("PPBODY")),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramAcknowledgement)ReadCode(reply);
    }

    /// <summary>Asks for a process program back - S7F5.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>PPBODY, or <see langword="null"/> when the equipment has no such program.</returns>
    /// <remarks>
    /// S7F6 has no acknowledgement code to carry a refusal, so a zero-length body is the refusal -
    /// which is why this is nullable rather than empty.
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<byte>?> RequestProcessProgramAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        using var reply = await RequestAsync(
            new SecsMessage(7, 5, replyExpected: true)
            {
                Name = "ProcessProgramRequest",
                Item = SecsItem.A(id).Named("PPID"),
            },
            cancellationToken).ConfigureAwait(false);

        if (reply.Item is not { IsList: true, Count: 2 } body || body[1].Format != SecsFormat.Binary)
        {
            return null;
        }

        // Copied, because the reply is disposed on the way out of this method.
        return body[1].GetSpan<byte>().ToArray();
    }

    /// <summary>Deletes process programs - S7F17.</summary>
    /// <param name="ids">The identifiers. An empty list means every program.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC7.</returns>
    public async ValueTask<GemProcessProgramAcknowledgement> DeleteProcessProgramsAsync(
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var entries = new SecsItem[ids.Count];

        for (var i = 0; i < ids.Count; i++)
        {
            entries[i] = SecsItem.A(ids[i]).Named("PPID");
        }

        using var reply = await RequestAsync(
            new SecsMessage(7, 17, replyExpected: true)
            {
                Name = "DeleteProcessProgramSend",
                Item = SecsItem.L(entries),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramAcknowledgement)ReadCode(reply);
    }

    /// <summary>Asks what process programs the equipment holds - S7F19.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every PPID, in the order the equipment gave them.</returns>
    public async ValueTask<IReadOnlyList<string>> ListProcessProgramsAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(7, 19, replyExpected: true) { Name = "CurrentEppdRequest" },
            cancellationToken).ConfigureAwait(false);

        List<string> ids = [];

        foreach (var entry in Entries(reply))
        {
            if (entry.Format.IsText())
            {
                ids.Add(GemItems.ReadText(entry));
            }
        }

        return ids;
    }

    /// <summary>Sends a formatted process program - S7F23.</summary>
    /// <param name="program">The program.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC7.</returns>
    /// <remarks>
    /// <see cref="GemProcessProgramAcknowledgement.ModeUnsupported"/> here means the equipment does
    /// formatted process programs but not this flavour of one - a command code or a parameter in a
    /// form it does not carry - rather than that it does not do them at all.
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> SendFormattedProcessProgramAsync(
        GemFormattedProcessProgram program,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);

        using var reply = await RequestAsync(
            new SecsMessage(7, 23, replyExpected: true)
            {
                Name = "FormattedProcessProgramSend",
                Item = GemFormattedProcessProgramCodec.Write(program),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramAcknowledgement)ReadCode(reply);
    }

    /// <summary>Asks for a formatted process program back - S7F25.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The program, or <see langword="null"/> when the equipment has no such program.</returns>
    public async ValueTask<GemFormattedProcessProgram?> RequestFormattedProcessProgramAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        using var reply = await RequestAsync(
            new SecsMessage(7, 25, replyExpected: true)
            {
                Name = "FormattedProcessProgramRequest",
                Item = SecsItem.A(id).Named("PPID"),
            },
            cancellationToken).ConfigureAwait(false);

        // The same codec the equipment reads S7F23 with. A zero-length body is the refusal, and it
        // reads as a length error - which is the honest answer to "did I get a program".
        GemFormattedProcessProgramCodec.TryRead(reply.Item, out var program);

        return program;
    }

    /// <summary>Stops listening to the connection.</summary>
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

        return ValueTask.CompletedTask;
    }

    // ----------------------------------------------------------------- inbound handlers

    private static ReadOnlyMemory<byte> Encode(SecsItem item)
    {
        // A zero-length list is how E5 says "there is no bound documented for this one", and an
        // empty memory is how this says the same thing without making a caller decode two bytes to
        // find out.
        if (item is { IsList: true, Count: 0 })
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        var writer = new ArrayBufferWriter<byte>();
        item.EncodeTo(writer);

        return writer.WrittenSpan.ToArray();
    }

    private static GemEquipmentIdentity? ReadIdentity(SecsItem? item)
        => item is { IsList: true, Count: 2 }
            ? new GemEquipmentIdentity(GemItems.ReadText(item[0]), GemItems.ReadText(item[1]))
            : null;

    /// <summary>The children of a list body, or nothing when the body is not a list.</summary>
    /// <remarks>
    /// A span rather than a sequence, because that is what <see cref="SecsItem.Items"/> is - and so
    /// every loop over it has to finish before the reply is disposed, which is exactly the right
    /// constraint to have the compiler enforce.
    /// </remarks>
    private static ReadOnlySpan<SecsItem> Entries(SecsMessage reply)
        => reply.Item is { IsList: true } list ? list.Items : [];

    /// <summary>Reads an acknowledgement code, or says why the reply was not one.</summary>
    /// <param name="reply">The secondary.</param>
    /// <param name="index">Where in a list body the code is, or -1 when the body <em>is</em> the code.</param>
    /// <returns>The code.</returns>
    /// <exception cref="SecsException">The reply did not carry a code at all.</exception>
    /// <remarks>
    /// Throwing rather than returning a refusal. Every one of these enums has zero meaning accepted,
    /// so a missing body read as zero would turn a broken machine into a silent success - and a
    /// reply that is not the shape E5 defines is a protocol fault the operator wants to see, not
    /// something for a host to paper over.
    /// </remarks>
    private static byte ReadCode(SecsMessage reply, int index = -1)
    {
        var item = index < 0
            ? reply.Item
            : reply.Item is { IsList: true } list && list.Count > index ? list[index] : null;

        if (!GemItems.TryReadCode(item, out var code))
        {
            throw new SecsException(
                $"S{reply.S}F{reply.F} did not carry the acknowledgement code E5 defines for it.");
        }

        return code;
    }

    private async ValueTask<GemDefineReportAcknowledgement> SendReportDefinitionsAsync(
        SecsItem definitions,
        CancellationToken cancellationToken)
    {
        using var reply = await RequestAsync(
            new SecsMessage(2, 33, replyExpected: true)
            {
                Name = "DefineReport",
                Item = SecsItem.L(NextDataId(), definitions),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemDefineReportAcknowledgement)ReadCode(reply);
    }

    private async ValueTask<IReadOnlyList<GemAlarmReport>> ReadAlarmListAsync(
        SecsMessage request,
        CancellationToken cancellationToken)
    {
        using var reply = await RequestAsync(request, cancellationToken).ConfigureAwait(false);

        List<GemAlarmReport> alarms = [];

        foreach (var entry in Entries(reply))
        {
            if (entry is { IsList: true, Count: 3 } && GemItems.TryReadId(entry[1], out var id))
            {
                GemItems.TryReadCode(entry[0], out var code);
                alarms.Add(new GemAlarmReport(code, id, GemItems.ReadText(entry[2])));
            }
        }

        return alarms;
    }

    private SecsMessage OnAreYouThere() => new(1, 2, replyExpected: false)
    {
        Name = "OnLineData",
        Item = Identity(),
    };

    private SecsMessage OnEstablishCommunications()
    {
        var accepted = _options.AcceptCommunications;

        if (accepted)
        {
            SetCommunicating(true, "S1F13 from the equipment");
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

    private SecsMessage OnAlarmReport(SecsMessage request)
    {
        var code = (byte)0;
        var id = 0UL;
        var text = string.Empty;

        if (request.Item is { IsList: true, Count: 3 } body)
        {
            GemItems.TryReadCode(body[0], out code);
            GemItems.TryReadId(body[1], out id);
            text = GemItems.ReadText(body[2]);
        }

        var args = new GemHostAlarmEventArgs(new GemAlarmReport(code, id, text));

        _logger.LogInformation("Alarm {Alarm} from the equipment.", args.Alarm);
        AlarmReceived?.Invoke(this, args);

        return new SecsMessage(5, 2, replyExpected: false)
        {
            Name = "AlarmReportAcknowledge",
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC5"),
        };
    }

    private SecsMessage OnEventReport(SecsMessage request, byte replyFunction)
    {
        var acknowledgement = GemReportAcknowledgement.Error;
        var dataId = 0u;
        var eventId = 0UL;
        List<GemReceivedReport> reports = [];

        if (request.Item is { IsList: true, Count: 3 } body)
        {
            acknowledgement = GemReportAcknowledgement.Accepted;

            if (GemItems.TryReadId(body[0], out var data))
            {
                dataId = (uint)Math.Min(data, uint.MaxValue);
            }

            GemItems.TryReadId(body[1], out eventId);

            if (body[2].IsList)
            {
                foreach (var entry in body[2].Items)
                {
                    if (entry is { IsList: true, Count: 2 } && GemItems.TryReadId(entry[0], out var reportId))
                    {
                        // The values item belongs to the inbound message, which is why the event
                        // args say so: this is a view, and the handler is the whole of its life.
                        reports.Add(new GemReceivedReport(reportId, entry[1]));
                    }
                }
            }
        }

        var args = new GemEventReportEventArgs(dataId, eventId, reports)
        {
            Acknowledgement = acknowledgement,
        };

        EventReportReceived?.Invoke(this, args);

        return new SecsMessage(6, replyFunction, replyExpected: false)
        {
            Name = replyFunction == 12 ? "EventReportAcknowledge" : "AnnotatedEventReportAcknowledge",
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC6"),
        };
    }

    private static string WritePeriod(TimeSpan period) => string.Create(
        CultureInfo.InvariantCulture,
        $"{(int)period.TotalHours:D2}{period.Minutes:D2}{period.Seconds:D2}{period.Milliseconds / 10:D2}");

    private async ValueTask<GemTraceAcknowledgement> InitializeTraceAsync(
        uint traceId,
        string period,
        int totalSamples,
        int groupSize,
        IReadOnlyList<uint> variableIds,
        CancellationToken cancellationToken)
    {
        var variables = new SecsItem[variableIds.Count];

        for (var i = 0; i < variableIds.Count; i++)
        {
            variables[i] = GemItems.IdItem(variableIds[i]).Named("SVID");
        }

        using var reply = await RequestAsync(
            new SecsMessage(2, 23, replyExpected: true)
            {
                Name = "TraceInitialize",
                Item = SecsItem.L(
                    SecsItem.U4(traceId).Named("TRID"),
                    SecsItem.A(period).Named("DSPER"),
                    SecsItem.U4((uint)totalSamples).Named("TOTSMP"),
                    SecsItem.U4((uint)groupSize).Named("REPGSZ"),
                    SecsItem.L(variables)),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemTraceAcknowledgement)ReadCode(reply);
    }

    private SecsMessage OnTraceData(SecsMessage request)
    {
        var acknowledgement = GemReportAcknowledgement.Error;
        var traceId = 0u;
        var sampleNumber = 0u;
        DateTimeOffset? sampledAt = null;
        SecsItem[] samples = [];

        if (request.Item is { IsList: true, Count: 4 } body)
        {
            acknowledgement = GemReportAcknowledgement.Accepted;

            if (GemItems.TryReadId(body[0], out var id))
            {
                traceId = (uint)Math.Min(id, uint.MaxValue);
            }

            if (GemItems.TryReadId(body[1], out var number))
            {
                sampleNumber = (uint)Math.Min(number, uint.MaxValue);
            }

            if (body[2].Format.IsText() && GemItems.TryParseTimestamp(body[2].GetString(), out var taken))
            {
                sampledAt = new DateTimeOffset(taken, TimeSpan.Zero);
            }

            if (body[3].IsList)
            {
                // The sample items belong to the inbound message, which is why the event arguments
                // say so: this is a view, and the handler is the whole of its life.
                samples = body[3].Items.ToArray();
            }
        }

        var args = new GemTraceDataEventArgs(traceId, sampleNumber, sampledAt, samples)
        {
            Acknowledgement = acknowledgement,
        };

        TraceDataReceived?.Invoke(this, args);

        return new SecsMessage(6, 2, replyExpected: false)
        {
            Name = "TraceDataAcknowledge",
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC6"),
        };
    }

    private SecsMessage OnTerminalRequest(SecsMessage request)
    {
        var terminalId = (byte)0;
        var text = string.Empty;

        if (request.Item is { IsList: true, Count: 2 } body)
        {
            GemItems.TryReadCode(body[0], out terminalId);

            var blocks = GemItems.ReadTextBlocks(body[1]);
            text = blocks.Count > 0 ? blocks[0] : string.Empty;
        }

        var args = new GemTerminalRequestEventArgs(terminalId, text);

        _logger.LogInformation("Terminal {Terminal} sent: {Text}", terminalId, text);
        TerminalRequestReceived?.Invoke(this, args);

        return new SecsMessage(10, 2, replyExpected: false)
        {
            Name = "TerminalAcknowledge",
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC10"),
        };
    }

    private void OnErrorReport(SecsMessage message)
    {
        ReadOnlyMemory<byte> header = ReadOnlyMemory<byte>.Empty;
        var description = string.Empty;

        if (message.F == 13)
        {
            if (message.Item is { IsList: true, Count: 2 } body)
            {
                description = $"{GemItems.ReadText(body[0])}: {GemItems.ReadText(body[1])}";
            }
        }
        else if (message.Item is { } item && item.Format == SecsFormat.Binary && item.Count > 0)
        {
            // Copied. Ten bytes, and the alternative is an event argument pointing into a buffer
            // the transaction hands back to the pool.
            header = item.GetSpan<byte>().ToArray();
        }

        var args = new GemHostErrorEventArgs(message.F, header, description);

        _logger.LogWarning("{Report} from the equipment.", args);
        ErrorReported?.Invoke(this, args);
    }

    private void OnConnectionStateChanged(object? sender, HsmsConnectionStateChangedEventArgs e)
    {
        if (e.Previous == HsmsConnectionState.Selected && e.Current != HsmsConnectionState.Selected)
        {
            SetCommunicating(false, "the link left Selected");
        }
    }

    private void SetCommunicating(bool communicating, string reason)
    {
        if (Volatile.Read(ref _communicating) == communicating)
        {
            return;
        }

        Volatile.Write(ref _communicating, communicating);
        _logger.LogInformation("Host communication state: {State} ({Reason}).", communicating, reason);
        CommunicationStateChanged?.Invoke(this, new GemHostCommunicationStateChangedEventArgs(communicating, reason));
    }

    private SecsItem Identity() => SecsItem.L(
        SecsItem.A(_options.ModelName).Named("MDLN"),
        SecsItem.A(_options.SoftwareRevision).Named("SOFTREV"));

    private SecsItem NextDataId()
        => SecsItem.U4((uint)Interlocked.Increment(ref _dataId)).Named("DATAID");

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The primary is built by the method that called this and is of no use afterwards, so it is
        // disposed here. The secondary is the caller's, whether it keeps it or reads one byte out
        // of it - which is why every method that returns a decoded answer disposes it itself.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
