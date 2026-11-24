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
/// <see cref="HandleAsync"/>: S1F1, S1F13, S2F25, S5F1, S5F9, S6F1, S6F3, S6F5, S6F11, S6F13,
/// S10F1 and stream 9.
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

        // The loopback goes either way: a machine testing the link asks the host, and a host
        // that refused it would be the thing under suspicion refusing to be measured.
        (2, 25),   // Loopback Diagnostic Request
        (5, 1),    // Alarm Report Send

        // An exception's four stages, which E5 gives four messages and nearly one body.
        (5, 9),    // Exception Post Notify
        (5, 11),   // Exception Clear Notify
        (5, 13),   // Exception Recover Notify
        (5, 15),   // Exception Recover Complete Notify
        (6, 1),    // Trace Data Send
        (6, 3),    // Discrete Variable Data Send
        (6, 5),    // Multi-block Data Send Inquire
        (6, 9),    // Unformatted Data Send
        (6, 11),   // Event Report Send
        (6, 13),   // Annotated Event Report Send

        // The only two things a machine says first in stream 7. Declared unconditionally, unlike
        // the equipment's stream 7, because a host has no process program capability to opt into -
        // it either hears about a recipe changing or it does not.
        (7, 29),   // Process Program Change Notify
        (7, 39),   // Process Program Selected Notify
        (10, 1),   // Terminal Request
        (10, 7),   // Terminal Not Accessible
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

    /// <summary>Raised after an exception post arrives, before it is acknowledged - S5F9.</summary>
    /// <remarks>
    /// Separate from <see cref="AlarmReceived"/> although both are stream 5 and both answer with
    /// <c>ACKC5</c>. An alarm is a condition with an identifier the host can look up in a namelist
    /// and a state it can read back with S5F6; an exception post is an occurrence, named by free
    /// text, that nothing in the documented half of stream 5 will tell the host about a second
    /// time. Merging them would hand a host one event whose two halves it has to tell apart.
    /// </remarks>
    public event EventHandler<GemHostExceptionEventArgs>? ExceptionReceived;

    /// <summary>Raised when the equipment says one of its terminals cannot be reached - S10F7.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Not <c>ACKC10</c> 2 by another route.</strong> That code answers a display request -
    /// a host asked, and the machine says the terminal is not available. This is a primary with no
    /// reply, volunteered before anybody tries: a host that stops sending to terminal 2 on the
    /// strength of it never has to make the failing request. The two were conflated in the reason
    /// this row used to be left out.
    /// </para>
    /// <para>
    /// <strong>The machine sends it and does not answer it.</strong> The reference marks the row as
    /// travelling either way, and there is no method here to send it: a host telling a machine that
    /// one of the host's terminals has gone would be a message nothing in this library answers, and
    /// that is the one thing the Gem README says a host method must not be. The event exists
    /// because this direction is the useful one.
    /// </para>
    /// <para>
    /// Nothing to acknowledge, so these arguments carry no settable code - the row has no reply at
    /// all, which is the same position stream 9 is in.
    /// </para>
    /// </remarks>
    public event EventHandler<GemTerminalUnavailableEventArgs>? TerminalNotAccessible;

    /// <summary>Raised when the equipment says a process program changed - S7F29, or was selected - S7F39.</summary>
    /// <remarks>
    /// <para>
    /// One event for two messages, with <see cref="GemProcessProgramNoticeEventArgs.Function"/>
    /// saying which: a program changing is about the store and a program being selected is about
    /// what the machine will run next, and the bodies are identical, so the message number is the
    /// only thing that separates them.
    /// </para>
    /// <para>
    /// <strong>Neither is spooled by the machine</strong>, so a host that was away does not get
    /// these on reconnecting - it asks <see cref="ReadProcessProgramDirectoryAsync"/> instead,
    /// which answers with the directory as it is now rather than with news from an hour ago.
    /// Treat the event as a prompt to look, not as a log.
    /// </para>
    /// </remarks>
    public event EventHandler<GemProcessProgramNoticeEventArgs>? ProcessProgramNoticeReceived;

    /// <summary>Raised when the equipment asks permission to send something large - S6F5.</summary>
    /// <remarks>
    /// The handler sets <see cref="GemMultiBlockInquiryEventArgs.Grant"/>, and the default is to
    /// grant. A host with nothing attached to this event therefore says yes, which is the right
    /// default for a handshake whose only purpose is to let a host say no: silence that refused
    /// would stop a machine from reporting at all.
    /// </remarks>
    public event EventHandler<GemMultiBlockInquiryEventArgs>? MultiBlockInquired;

    /// <summary>Raised when the equipment sends unformatted data - S6F9.</summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="EventReportReceived"/>, which S6F3 shares with S6F11 because both
    /// of those say an event happened. This one says nothing happened: it carries a block of data
    /// and the identifier the machine filed it under, with no <c>CEID</c> anywhere in the row. A
    /// host folding it onto an event report would have to invent the event.
    /// </para>
    /// <para>
    /// A handler that sets nothing accepts. The data itself is a view into the inbound message and
    /// does not outlive the handler.
    /// </para>
    /// </remarks>
    public event EventHandler<GemUnformattedDataEventArgs>? UnformattedDataReceived;

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
            (2, 25) => OnLoopback(transaction.Message),
            (5, 1) => OnAlarmReport(transaction.Message),
            (5, 9) => OnExceptionNotice(transaction.Message, GemExceptionStage.Posted),
            (5, 11) => OnExceptionNotice(transaction.Message, GemExceptionStage.Cleared),
            (5, 13) => OnExceptionNotice(transaction.Message, GemExceptionStage.Recovering),
            (5, 15) => OnExceptionNotice(transaction.Message, GemExceptionStage.Recovered),
            (6, 1) => OnTraceData(transaction.Message),
            (6, 3) => OnDiscreteVariableData(transaction.Message),
            (6, 5) => OnMultiBlockInquire(transaction.Message),
            (6, 9) => OnUnformattedData(transaction.Message),
            (6, 11) => OnEventReport(transaction.Message, 12),
            (6, 13) => OnEventReport(transaction.Message, 14),
            (7, 29) => OnProgramNotice(transaction.Message, 30),
            (7, 39) => OnProgramNotice(transaction.Message, 40),
            (10, 1) => OnTerminalRequest(transaction.Message),
            (10, 7) => OnTerminalNotAccessible(transaction.Message),
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

    /// <summary>Asks for the data variables' names and units - S1F21, answered by S1F22.</summary>
    /// <param name="vids">Which variables, or empty for every one the machine has.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One description per entry, in the order the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="vids"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <see cref="ReadStatusNamelistAsync"/>'s sibling for the other kind of variable. A machine
    /// answers an identifier it does not have with its name and units both zero-length rather than
    /// by leaving it out, so the reply stays aligned with the request and
    /// <see cref="GemVariableDescription.Exists"/> is how you tell.
    /// </remarks>
    public async ValueTask<IReadOnlyList<GemVariableDescription>> ReadDataVariableNamelistAsync(
        IReadOnlyList<ulong> vids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vids);

        using var reply = await RequestAsync(
            new SecsMessage(1, 21, replyExpected: true)
            {
                Name = "DataVariableNamelistRequest",
                Item = GemItems.IdList(vids),
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

    /// <summary>Asks what the machine's events are called and are about - S1F23, answered by S1F24.</summary>
    /// <param name="ceids">Which events, or empty for every one the machine has.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One description per entry, in the order the machine answered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="ceids"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// The third namelist, and the only one whose entries nest: each carries the identifier, the
    /// name and the variables the <em>equipment</em> says the event is about.
    /// </para>
    /// <para>
    /// <strong>That is not the report a host linked with S2F35.</strong> This answer describes the
    /// machine and does not change when the reporting is reconfigured, which is what makes it
    /// worth asking once at start-up rather than after every change.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<GemEventDescription>> ReadCollectionEventNamelistAsync(
        IReadOnlyList<ulong> ceids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ceids);

        using var reply = await RequestAsync(
            new SecsMessage(1, 23, replyExpected: true)
            {
                Name = "CollectionEventNamelistRequest",
                Item = GemItems.IdList(ceids),
            },
            cancellationToken).ConfigureAwait(false);

        List<GemEventDescription> descriptions = [];

        foreach (var entry in Entries(reply))
        {
            if (entry is not { IsList: true, Count: >= 2 } || !GemItems.TryReadId(entry[0], out var id))
            {
                continue;
            }

            List<ulong> variables = [];

            if (entry.Count > 2 && entry[2] is { IsList: true } list)
            {
                foreach (var vid in list.Items)
                {
                    if (GemItems.TryReadId(vid, out var read))
                    {
                        variables.Add(read);
                    }
                }
            }

            descriptions.Add(new GemEventDescription(id, GemItems.ReadText(entry[1]), variables));
        }

        return descriptions;
    }

    /// <summary>Sends a command with no parameters - S2F21, answered by S2F22.</summary>
    /// <param name="command">What to do - <c>RCMD</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What happened - <c>CMDA</c>, which names three outcomes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>CMDA</c>.</exception>
    /// <remarks>
    /// <strong>Use <see cref="SendCommandAsync"/> unless you need this one.</strong> S2F41 carries
    /// parameters and answers with <c>HCACK</c>'s seven codes; this carries none and answers with
    /// three, so "completion later" and "already in the desired condition" come back as
    /// <see cref="GemRemoteCommandAcknowledgement.CannotPerformNow"/>. The narrowing is the
    /// message's, not this library's - an empty parameter list on S2F41 asks the same thing and
    /// gets the fuller answer.
    /// </remarks>
    public async ValueTask<GemRemoteCommandAcknowledgement> SendRemoteCommandAsync(
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var reply = await RequestAsync(
            new SecsMessage(2, 21, replyExpected: true)
            {
                Name = "RemoteCommand",
                Item = SecsItem.A(command).Named("RCMD"),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemRemoteCommandAcknowledgement)ReadNamedCode(reply, 3, "CMDA");
    }

    /// <summary>Sends bytes and checks the same ones come back - S2F25, answered by S2F26.</summary>
    /// <param name="body">What to send - <c>ABS</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine echoed exactly what it was sent.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The one message here that changes nothing</strong>, which is what makes it the right
    /// first thing to send when a link is behaving oddly: it proves a body crosses in both
    /// directions without touching the machine's state. A machine is expected to answer it
    /// off-line as well as on, so a refusal is itself a finding.
    /// </para>
    /// <para>
    /// Returns whether the echo matched rather than the bytes, because a loopback whose answer you
    /// have to compare yourself is a loopback half done. <see cref="LoopbackDataAsync"/> hands back
    /// the bytes for a caller that wants to see them.
    /// </para>
    /// </remarks>
    public async ValueTask<bool> LoopbackAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var echoed = await LoopbackDataAsync(body, cancellationToken).ConfigureAwait(false);

        return echoed.Span.SequenceEqual(body.Span);
    }

    /// <summary>Sends bytes and hands back what came of them - S2F25, answered by S2F26.</summary>
    /// <param name="body">What to send - <c>ABS</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The echoed bytes, copied out of the reply.</returns>
    /// <remarks>
    /// Copied, because the reply's item borrows pooled memory and is released with it. Empty when
    /// the machine answered with no body at all, which is not the same as answering with zero
    /// bytes - and neither is an error this method raises, because what to make of a bad echo is
    /// the caller's question.
    /// </remarks>
    public async ValueTask<ReadOnlyMemory<byte>> LoopbackDataAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(2, 25, replyExpected: true)
            {
                Name = "LoopbackDiagnosticRequest",
                Item = SecsItem.B(body.ToArray()).Named("ABS"),
            },
            cancellationToken).ConfigureAwait(false);

        return GemItems.ReadBytes(reply.Item);
    }

    /// <summary>Asks the equipment to go off-line - S1F15.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>OFLACK.</returns>
    public async ValueTask<GemOfflineAcknowledgement> RequestOfflineAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(1, 15, replyExpected: true) { Name = "RequestOffLine" },
            cancellationToken).ConfigureAwait(false);

        return (GemOfflineAcknowledgement)ReadNamedCode(reply, 1, "OFLACK");
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

        return (GemOnlineAcknowledgement)ReadNamedCode(reply, 3, "ONLACK");
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

        return (GemEquipmentConstantAcknowledgement)ReadNamedCode(reply, 4, "EAC");
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

        return (GemTimeAcknowledgement)ReadNamedCode(reply, 2, "TIACK");
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

        return (GemLinkReportAcknowledgement)ReadNamedCode(reply, 6, "LRACK");
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

        return (GemEnableEventAcknowledgement)ReadNamedCode(reply, 2, "ERACK");
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

        return (GemAlarmAcknowledgement)ReadNamedCode(reply, 1, "ACKC5");
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

    /// <summary>Asks for one report with its names and units beside the values - S6F17.</summary>
    /// <param name="reportId">RPTID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>S6F18. <strong>The caller owns it</strong> and must dispose it.</returns>
    /// <remarks>
    /// <para>
    /// The annotated twin of <see cref="RequestReportAsync"/>, and the difference is worth the
    /// extra row: S6F20 answers with values in the order the report defines them, so a host has to
    /// have read S2F11 or S1F12 already to know what the third number means. S6F18 answers with
    /// each value identified and its units attached, which is enough on its own.
    /// </para>
    /// <para>
    /// Pass the reply to <see cref="ReadAnnotatedReport"/> while you still hold it. A zero-length
    /// body means that report is not defined, exactly as it does for S6F19.
    /// </para>
    /// </remarks>
    public ValueTask<SecsMessage> RequestAnnotatedReportAsync(
        ulong reportId,
        CancellationToken cancellationToken = default)
        => RequestAsync(
            new SecsMessage(6, 17, replyExpected: true)
            {
                Name = "AnnotatedIndividualReportRequest",
                Item = GemItems.IdItem(reportId).Named("RPTID"),
            },
            cancellationToken);

    /// <summary>Reads an S6F18 body into its annotated values.</summary>
    /// <param name="reply">The S6F18 from <see cref="RequestAnnotatedReportAsync"/>.</param>
    /// <returns>One entry per value, or an empty list for a report the equipment does not define.</returns>
    /// <remarks>
    /// <strong>Each value is a view into <paramref name="reply"/></strong>, which the caller owns:
    /// the entries are valid for as long as that message is held. A static method rather than
    /// something that hands back owned data, because <c>V</c> has no format in the reference and
    /// this library will not invent one in order to copy it - the same decision S6F11 made.
    /// </remarks>
    public static IReadOnlyList<GemAnnotatedValue> ReadAnnotatedReport(SecsMessage reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        List<GemAnnotatedValue> values = [];

        if (reply.Item is { IsList: true } body)
        {
            foreach (var entry in body.Items)
            {
                if (entry is { IsList: true, Count: 3 } && GemItems.TryReadId(entry[0], out var variableId))
                {
                    values.Add(new GemAnnotatedValue(variableId, entry[1], GemItems.ReadText(entry[2])));
                }
            }
        }

        return values;
    }

    /// <summary>Asks for the data the equipment filed under one identifier - S6F7.</summary>
    /// <param name="dataId">DATAID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>S6F8. <strong>The caller owns it</strong> and must dispose it.</returns>
    /// <remarks>
    /// <para>
    /// The pull half of the unformatted pair: S6F9 is the machine sending data it chose to send,
    /// and this asks for data by the identifier it was sent under. A host knows such an identifier
    /// because it saw one go past - in an S6F5 inquiry, or in an S6F9 it could not store at the
    /// time - so this is how the second attempt gets made.
    /// </para>
    /// <para>
    /// <c>DATAID</c> is defined and carried untouched; what it selects is between the host and the
    /// machine, exactly as a <c>CEID</c> or an <c>RPTID</c> is. A zero-length body means the
    /// equipment has nothing filed under it.
    /// </para>
    /// </remarks>
    public ValueTask<SecsMessage> RequestDataTransferAsync(
        ulong dataId,
        CancellationToken cancellationToken = default)
        => RequestAsync(
            new SecsMessage(6, 7, replyExpected: true)
            {
                Name = "DataTransferRequest",
                Item = GemItems.IdItem(dataId).Named("DATAID"),
            },
            cancellationToken);

    /// <summary>Asks which reports are linked to one collection event - S6F27.</summary>
    /// <param name="eventId">CEID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The linked report identifiers, empty for an event the equipment does not have.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The read-back of S2F35</strong>, which this package has been able to write for as
    /// long as it has existed and until now could not read. A host that restarts, or one that has
    /// taken over from another, holds no record of what the last host linked - and S2F35 is no use
    /// for probing, since sending it is what changes the table.
    /// </para>
    /// <para>
    /// Report identifiers only. What each report contains is a question for S2F11, and what the
    /// values are is one for <see cref="RequestReportAsync"/>.
    /// </para>
    /// </remarks>
    public async ValueTask<IReadOnlyList<uint>> RequestNotificationReportsAsync(
        ulong eventId,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(6, 27, replyExpected: true)
            {
                Name = "NotificationReportRequest",
                Item = GemItems.IdItem(eventId).Named("CEID"),
            },
            cancellationToken).ConfigureAwait(false);

        List<uint> reports = [];

        if (reply.Item is { IsList: true, Count: 2 } body && body[1].IsList)
        {
            foreach (var entry in body[1].Items)
            {
                if (GemItems.TryReadId(entry, out var reportId))
                {
                    reports.Add((uint)Math.Min(reportId, uint.MaxValue));
                }
            }
        }

        return reports;
    }

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

        // RSDA, which is what the equipment half of this package puts on the wire and what E30
        // calls this field - not RSPACK, which the item dictionary gives to S2F44, a row nothing
        // here serves. The two ends of one transaction naming one byte differently is the whole
        // of what that mistake was, and GemAcknowledgementsAgreeWithTheReferenceTests now records
        // where this enum's table actually comes from.
        return (GemSpooledDataAcknowledgement)ReadNamedCode(reply, 1, "RSDA");
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

        return (GemTerminalAcknowledgement)ReadNamedCode(reply, 3, "ACKC10");
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

        return (GemTerminalAcknowledgement)ReadNamedCode(reply, 3, "ACKC10");
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

        return (GemTerminalAcknowledgement)ReadNamedCode(reply, 3, "ACKC10");
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

        return (GemProcessProgramGrant)ReadNamedCode(reply, 6, "PPGNT");
    }

    /// <summary>Asks for status in one of the machine's predefined forms - S1F5, answered by S1F6.</summary>
    /// <param name="formCode">SFCD - which form.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The reply, which <strong>the caller disposes</strong>. A zero-length body means no such form.</returns>
    /// <remarks>
    /// <para>
    /// Returns the message for the reason <see cref="ReadStatusAsync"/> does: the body is whatever
    /// this machine calls a value, and a typed wrapper over that would be a guess with an API
    /// attached. The values are a view into the reply and die with it.
    /// </para>
    /// <para>
    /// <strong>Which form a code selects is the machine's to document.</strong> <c>SFCD</c> has no
    /// table in the reference, so nothing here can tell you what form 1 is - and a host that does
    /// not know sends <see cref="ReadFixedFormAsync"/> instead, which answers with the names.
    /// </para>
    /// </remarks>
    public ValueTask<SecsMessage> ReadFormattedStatusAsync(
        byte formCode,
        CancellationToken cancellationToken = default)
        => RequestAsync(
            new SecsMessage(1, 5, replyExpected: true)
            {
                Name = "FormattedStatusRequest",
                Item = SecsItem.B(formCode).Named("SFCD"),
            },
            cancellationToken);

    /// <summary>Asks for a form with the variables named - S1F7, answered by S1F8.</summary>
    /// <param name="formCode">SFCD - which form.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The reply, which <strong>the caller disposes</strong>. A zero-length body means no such form.</returns>
    /// <remarks>
    /// S1F5's twin with one item more: S1F8 carries an <c>SVNAME</c> beside every <c>SV</c>, so a
    /// host meeting a machine for the first time can learn what a form contains without a
    /// namelist. Equipment written to this library nests them per entry -
    /// <c>L{ L,2{ SVNAME, SV } }</c> - which is how S1F12 and S2F30 carry a name beside a value,
    /// and the reference gives the row an item list rather than a shape.
    /// </remarks>
    public ValueTask<SecsMessage> ReadFixedFormAsync(
        byte formCode,
        CancellationToken cancellationToken = default)
        => RequestAsync(
            new SecsMessage(1, 7, replyExpected: true)
            {
                Name = "FixedFormRequest",
                Item = SecsItem.B(formCode).Named("SFCD"),
            },
            cancellationToken);

    /// <summary>Asks the machine to stop recovering from an exception - S5F17, answered by S5F18.</summary>
    /// <param name="exceptionId">EXID - which exception.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine accepted, and its reason when it did not.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exceptionId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>ACKA</c>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The only message in stream 5 a host sends about an exception</strong>, and the only
    /// one of the four recovery rows that is the host's. The other three are the machine telling a
    /// host what became of a condition it posted; this is a host saying stop.
    /// </para>
    /// <para>
    /// <c>ACKA</c> is a <c>BOOLEAN</c> with a table - TRUE accepted, FALSE not - so the answer is
    /// read rather than inferred, and the reason beside it is passed through untouched. See
    /// <see cref="GemExceptionAbortResult"/>.
    /// </para>
    /// </remarks>
    public async ValueTask<GemExceptionAbortResult> AbortExceptionRecoveryAsync(
        string exceptionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exceptionId);

        using var reply = await RequestAsync(
            new SecsMessage(5, 17, replyExpected: true)
            {
                Name = "ExceptionRecoverAbortRequest",
                Item = SecsItem.A(exceptionId).Named("EXID"),
            },
            cancellationToken).ConfigureAwait(false);

        if (reply.Item is not { IsList: true, Count: >= 2 } body)
        {
            throw new SecsException("S5F18 did not carry an EXID and an ACKA.");
        }

        // BOOLEAN on the wire, and tolerant of a machine that sent a B[1] or a U1 instead - the
        // same tolerance every other acknowledgement reader here has, for the same reason: a
        // machine that encoded the right value in the wrong width has still answered.
        var accepted = body[1].Format == SecsFormat.Boolean
            ? body[1].FirstValueOrDefault<bool>()
            : GemItems.TryReadCode(body[1], out var code) && code != 0;

        if (accepted)
        {
            return GemExceptionAbortResult.Accept();
        }

        var errorCode = body.Count >= 3 && GemItems.TryReadId(body[2], out var number)
            ? (uint)Math.Min(number, uint.MaxValue)
            : 0u;

        return GemExceptionAbortResult.Refuse(
            errorCode,
            body.Count >= 4 ? GemItems.ReadText(body[3]) : string.Empty);
    }

    /// <summary>Asks whether a body is the one the machine holds - S7F9, answered by S7F10.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="body">PPBODY, the bytes to compare against.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC7.</returns>
    /// <remarks>
    /// <para>
    /// <strong><c>Accepted</c> is the only answer that means "yes".</strong> <c>ACKC7</c> has no
    /// code for "those bytes are not these bytes", so a machine written to this library answers
    /// <c>NotFound</c> for an identifier it has not got, <c>LengthError</c> for a body of the wrong
    /// length, and <c>PermissionNotGranted</c> for one of the right length and the wrong contents -
    /// the table's nearest word for "I will not confirm that". Another machine may choose
    /// differently, so treat anything but <c>Accepted</c> as "not verified" rather than reading the
    /// reason out of the code.
    /// </para>
    /// <para>
    /// Worth sending after <see cref="SendProcessProgramAsync"/> on a link that has been
    /// misbehaving, which is the only use the message really has: it is the one transaction in
    /// stream 7 that reads the store without moving a body back.
    /// </para>
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> VerifyProcessProgramAsync(
        string id,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        using var reply = await RequestAsync(
            new SecsMessage(7, 9, replyExpected: true)
            {
                Name = "ProcessProgramVerifySend",
                Item = SecsItem.L(
                    SecsItem.A(id).Named("PPID"),
                    SecsItem.B(body.ToArray()).Named("PPBODY")),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramAcknowledgement)ReadNamedCode(reply, 7, "ACKC7");
    }

    /// <summary>Asks whether a program is on the machine at all - S7F31, answered by S7F32.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>PPGNT.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Two of <c>PPGNT</c>'s codes answer this and the rest answer S7F1.</strong> The item
    /// exists for a load grant, so <c>AlreadyHave</c> is yes and <c>InvalidId</c> is no, while
    /// <c>Ok</c> means "go ahead and send" - which is a reply to a question nobody asked here.
    /// Compare on <c>AlreadyHave</c>, not on "anything but a refusal".
    /// </para>
    /// <para>
    /// Cheaper than <see cref="ReadProcessProgramDirectoryAsync"/> for one identifier, and the
    /// difference is not academic: a machine holding several hundred recipes answers this with one
    /// byte.
    /// </para>
    /// </remarks>
    public async ValueTask<GemProcessProgramGrant> IsProcessProgramAvailableAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        using var reply = await RequestAsync(
            new SecsMessage(7, 31, replyExpected: true)
            {
                Name = "ProcessProgramAvailableRequest",
                Item = SecsItem.A(id).Named("PPID"),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramGrant)ReadNamedCode(reply, 6, "PPGNT");
    }

    /// <summary>Asks permission to send a formatted program - S7F33, answered by S7F34.</summary>
    /// <param name="id">PPID.</param>
    /// <param name="length">LENGTH.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>PPGNT.</returns>
    /// <remarks>
    /// S7F1's twin for the formatted half of the stream. Equipment written to this library cannot
    /// answer <c>NoSpace</c> here, because its formatted store exposes no capacity - so a
    /// <c>PPGNT</c> of <c>Ok</c> is not a promise that S7F23 will be accepted, and the
    /// acknowledgement to that message remains the one that decides.
    /// </remarks>
    public async ValueTask<GemProcessProgramGrant> InquireFormattedProcessProgramAsync(
        string id,
        int length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        using var reply = await RequestAsync(
            new SecsMessage(7, 33, replyExpected: true)
            {
                Name = "FormattedProcessProgramLoadInquire",
                Item = SecsItem.L(SecsItem.A(id).Named("PPID"), SecsItem.U4((uint)length).Named("LENGTH")),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramGrant)ReadNamedCode(reply, 6, "PPGNT");
    }

    /// <summary>Asks whether a formatted program is the one the machine holds - S7F27, answered by S7F28.</summary>
    /// <param name="program">The program to compare against.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>ACKC7.</returns>
    /// <remarks>
    /// S7F9's twin, with the same shortage in <c>ACKC7</c> - and one difference worth knowing: the
    /// comparison is over structure, so equipment written to this library compares the model name
    /// and the software revision as well as the commands. A program whose commands match but which
    /// was written for another machine is not the same program.
    /// </remarks>
    public async ValueTask<GemProcessProgramAcknowledgement> VerifyFormattedProcessProgramAsync(
        GemFormattedProcessProgram program,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(program);

        using var reply = await RequestAsync(
            new SecsMessage(7, 27, replyExpected: true)
            {
                Name = "FormattedProcessProgramVerifySend",
                Item = GemFormattedProcessProgramCodec.Write(program),
            },
            cancellationToken).ConfigureAwait(false);

        return (GemProcessProgramAcknowledgement)ReadNamedCode(reply, 7, "ACKC7");
    }

    /// <summary>Sends a remote command about one process program - S7F37, or S7F41 with parameters.</summary>
    /// <param name="programId">PPID - which program the command is about.</param>
    /// <param name="command">RCMD.</param>
    /// <param name="parameters">The CPNAME and CPVAL pairs. Empty sends S7F37, which carries none.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>CMDA.</returns>
    /// <remarks>
    /// <para>
    /// <strong>S2F21 and S2F41 with a program named.</strong> The same command table on the other
    /// end, the same control-state rule, and the same three <c>CMDA</c> codes - so "will complete
    /// later" arrives as <c>CannotPerformNow</c> here as it does for S2F22.
    /// </para>
    /// <para>
    /// Which function goes out is decided by <paramref name="parameters"/>, because the reference
    /// gives S7F37 two items and S7F41 three: an empty list is the message that carries no list
    /// rather than the message carrying an empty one. A machine that has the program but not the
    /// command answers <c>DoesNotExist</c>; one that has the command but not the program answers
    /// <c>CannotPerformNow</c>, since <c>CMDA</c> has no code for a missing program.
    /// </para>
    /// </remarks>
    public async ValueTask<GemRemoteCommandAcknowledgement> SendProgramCommandAsync(
        string programId,
        string command,
        IReadOnlyList<(string Name, string Value)> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(programId);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(parameters);

        var withParameters = parameters.Count > 0;
        var items = new List<SecsItem>(parameters.Count);

        foreach (var (name, value) in parameters)
        {
            items.Add(SecsItem.L(
                SecsItem.A(name).Named("CPNAME"),
                SecsItem.A(value).Named("CPVAL")));
        }

        var body = withParameters
            ? SecsItem.L(
                SecsItem.A(programId).Named("PPID"),
                SecsItem.A(command).Named("RCMD"),
                SecsItem.L(items))
            : SecsItem.L(
                SecsItem.A(programId).Named("PPID"),
                SecsItem.A(command).Named("RCMD"));

        using var reply = await RequestAsync(
            new SecsMessage(7, withParameters ? (byte)41 : (byte)37, replyExpected: true)
            {
                Name = withParameters
                    ? "ProcessProgramRelatedRemoteCommandWithParametersSend"
                    : "ProcessProgramRelatedRemoteCommandSend",
                Item = body,
            },
            cancellationToken).ConfigureAwait(false);

        return (GemRemoteCommandAcknowledgement)ReadNamedCode(reply, 3, "CMDA");
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

        return (GemProcessProgramAcknowledgement)ReadNamedCode(reply, 7, "ACKC7");
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

        return (GemProcessProgramAcknowledgement)ReadNamedCode(reply, 7, "ACKC7");
    }

    /// <summary>Asks what process programs the equipment holds - S7F19.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every PPID, in the order the equipment gave them.</returns>
    public ValueTask<IReadOnlyList<string>> ListProcessProgramsAsync(CancellationToken cancellationToken = default)
        => ReadProgramDirectoryAsync(19, cancellationToken);

    /// <summary>Asks for the process program directory - S7F7, answered by S7F8.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Every PPID the machine holds.</returns>
    /// <remarks>
    /// <para>
    /// <strong>The same question <see cref="ListProcessProgramsAsync"/> asks</strong>, by the other
    /// function number the reference gives it. S7F8 is "Process Program Directory Data" and S7F20
    /// is "Current EPPD Data" - the equipment process program directory - and the reference gives
    /// both bodies one item, a list of <c>PPID</c>. Nothing in it distinguishes them.
    /// </para>
    /// <para>
    /// Both are here because a host does not choose which function the machine on the other end
    /// understands. S7F19/F20 is the pair the reference documents and the one to prefer; this is
    /// for a machine that answers S9F5 to it.
    /// </para>
    /// </remarks>
    public ValueTask<IReadOnlyList<string>> ReadProcessProgramDirectoryAsync(
        CancellationToken cancellationToken = default)
        => ReadProgramDirectoryAsync(7, cancellationToken);

    private async ValueTask<IReadOnlyList<string>> ReadProgramDirectoryAsync(
        byte function,
        CancellationToken cancellationToken)
    {
        using var reply = await RequestAsync(
            new SecsMessage(7, function, replyExpected: true)
            {
                Name = function == 19 ? "CurrentEppdRequest" : "ProcessProgramDirectoryRequest",
            },
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

        return (GemProcessProgramAcknowledgement)ReadNamedCode(reply, 7, "ACKC7");
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

    /// <summary>
    /// Reads an acknowledgement code and folds the reserved range onto one member.
    /// </summary>
    /// <param name="reply">The reply.</param>
    /// <param name="reserved">The first code E5 reserves - which is where the enum's catch-all member sits.</param>
    /// <param name="item">The item's name, for the log.</param>
    /// <param name="index">Which item of a list body, or -1 for the whole body.</param>
    /// <returns>The code, or <paramref name="reserved"/> when the arriving one is in the reserved range.</returns>
    /// <remarks>
    /// <para>
    /// <strong>Twelve of this class's acknowledgements used to cast a code straight to its enum</strong>,
    /// and every one of their items reserves a range the enum cannot express - so an equipment
    /// answering S1F18 with 40 produced an <c>ONLACK</c> value with no name, and a caller
    /// switching on it fell through every case.
    /// </para>
    /// <para>
    /// The thirteenth, <c>HCACK</c>, was fixed on its own two chapters earlier. This helper is that
    /// fix applied to the rest, after the catalogue's fourth provenance measure turned the whole
    /// set up at once. A test drives every one of them with a reserved code, and another asserts
    /// against the catalogue that each enum's boundary is the one its item names - so the
    /// fourteenth cannot be forgotten the way the twelve were.
    /// </para>
    /// <para>
    /// <c>COMMACK</c> is the one acknowledgement here with no reserved range, so it is read without
    /// this. <c>TIAACK</c> names two codes where this package has six, which is a different problem and is
    /// documented where that enum is.
    /// </para>
    /// </remarks>
    private byte ReadNamedCode(SecsMessage reply, byte reserved, string item, int index = -1)
    {
        var code = ReadCode(reply, index);

        if (code < reserved)
        {
            return code;
        }

        _logger.LogInformation(
            "S{S}F{F} carried {Item} {Code}, which E5 reserves for the equipment and does not name.",
            reply.S,
            reply.F,
            item,
            code);

        return reserved;
    }

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

        return (GemDefineReportAcknowledgement)ReadNamedCode(reply, 5, "DRACK");
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

    /// <summary>Answers S2F25 by sending the same bytes back.</summary>
    /// <remarks>
    /// The host half of the one diagnostic that changes nothing. A machine is entitled to test the
    /// link in its own direction, and a host that answered only its own loopback would leave the
    /// machine unable to tell a dead link from a deaf host.
    /// </remarks>
    private static SecsMessage OnLoopback(SecsMessage request)
        => new(2, 26, replyExpected: false)
        {
            Name = "LoopbackDiagnosticData",
            Item = SecsItem.B(GemItems.ReadBytes(request.Item).ToArray()).Named("ABS"),
        };

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

    /// <summary>Answers S5F9, S5F11, S5F13 and S5F15 - an exception's four stages.</summary>
    /// <param name="request">The primary.</param>
    /// <param name="stage">Which stage, which decides how many items the body should carry.</param>
    /// <returns>The acknowledgement, on the function one above the primary's.</returns>
    /// <remarks>
    /// <para>
    /// One handler, because the four bodies are nearly one body: a <c>TIMESTAMP</c> and an
    /// <c>EXID</c> in every one, an <c>EXTYPE</c> in the post alone, and an <c>EXMESSAGE</c> in the
    /// post and the recovery. Four handlers would be four copies of the timestamp decision below.
    /// </para>
    /// <para>
    /// <strong>Error until the body reads</strong>, which is the stricter of the two patterns in
    /// this file and the right one for all four. An alarm that arrives malformed still names a
    /// condition the host can ask about again; nothing in stream 5 will repeat any of these, so
    /// acknowledging one this end could not read would lose it quietly. S5F6 is why the alarm
    /// handler can afford to be generous and this one cannot.
    /// </para>
    /// </remarks>
    private SecsMessage OnExceptionNotice(SecsMessage request, GemExceptionStage stage)
    {
        // How many items the stage carries. The guard is per stage rather than "two or more",
        // because a body of the wrong arity is a body this end did not understand - and a clear
        // that arrived with four items is not a clear.
        var expected = stage switch
        {
            GemExceptionStage.Posted => 4,
            GemExceptionStage.Recovered => 3,
            _ => 2,
        };

        var acknowledgement = GemAlarmAcknowledgement.Error;
        var postedAt = default(DateTime?);
        var id = string.Empty;
        var type = string.Empty;
        var text = string.Empty;

        if (request.Item is { IsList: true } body && body.Count == expected)
        {
            acknowledgement = GemAlarmAcknowledgement.Accepted;

            // A timestamp that will not parse leaves the instant unknown rather than failing the
            // notice: TIMESTAMP is when the machine says it happened, and the exception is the news.
            if (GemItems.TryParseTimestamp(GemItems.ReadText(body[0]), out var instant))
            {
                postedAt = instant;
            }

            id = GemItems.ReadText(body[1]);

            // The post carries both extra items and the recovery carries only the message, so the
            // position of EXMESSAGE differs between them - which is the whole reason this reads by
            // stage rather than by position.
            if (stage == GemExceptionStage.Posted)
            {
                type = GemItems.ReadText(body[2]);
                text = GemItems.ReadText(body[3]);
            }
            else if (stage == GemExceptionStage.Recovered)
            {
                text = GemItems.ReadText(body[2]);
            }
        }

        var args = new GemHostExceptionEventArgs(new GemExceptionPost(postedAt, id, type, text), stage)
        {
            Acknowledgement = acknowledgement,
        };

        _logger.LogInformation(
            "Exception {Exception} from the equipment: {Stage}.",
            args.Exception,
            stage);

        ExceptionReceived?.Invoke(this, args);

        return new SecsMessage(5, (byte)((byte)stage + 1), replyExpected: false)
        {
            Name = stage switch
            {
                GemExceptionStage.Posted => "ExceptionPostAcknowledge",
                GemExceptionStage.Cleared => "ExceptionClearAcknowledge",
                GemExceptionStage.Recovering => "ExceptionRecoverAcknowledge",
                _ => "ExceptionRecoverCompleteAcknowledge",
            },
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC5"),
        };
    }

    private SecsMessage OnDiscreteVariableData(SecsMessage request)
    {
        var acknowledgement = GemReportAcknowledgement.Error;
        var dataId = 0u;
        var eventId = 0UL;

        if (request.Item is { IsList: true, Count: 2 } body)
        {
            acknowledgement = GemReportAcknowledgement.Accepted;

            if (GemItems.TryReadId(body[0], out var data))
            {
                dataId = (uint)Math.Min(data, uint.MaxValue);
            }

            GemItems.TryReadId(body[1], out eventId);
        }

        // Raised on the same event S6F11 raises, with no reports, because a host watching for
        // "event N happened" wants to hear it however the machine said so. What tells the two
        // apart is GemEventReportEventArgs.Function, which is why that property exists: an empty
        // report list cannot, since S6F11 with nothing linked to it is also empty.
        var args = new GemEventReportEventArgs(dataId, eventId, [], 3)
        {
            Acknowledgement = acknowledgement,
        };

        EventReportReceived?.Invoke(this, args);

        return new SecsMessage(6, 4, replyExpected: false)
        {
            Name = "DiscreteVariableDataAcknowledge",
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC6"),
        };
    }

    private SecsMessage OnUnformattedData(SecsMessage request)
    {
        var acknowledgement = GemReportAcknowledgement.Error;

        if (request.Item is { IsList: true, Count: 2 } body && GemItems.TryReadId(body[0], out var data))
        {
            // The event args carry the acceptance as their own initialiser, so only a handler's
            // refusal is read back here. Writing both ways round would make that documented
            // default unreachable, which is a thing a control found in this very file once.
            var args = new GemUnformattedDataEventArgs((uint)Math.Min(data, uint.MaxValue), body[1]);

            UnformattedDataReceived?.Invoke(this, args);
            acknowledgement = args.Acknowledgement;
        }
        else
        {
            // Nothing is raised, because there is no data to hand anybody - and ACKC6 1 is all the
            // row can say, the same byte a handler uses to refuse data it understood. The log is
            // the only place the two are told apart.
            _logger.LogWarning(
                "S6F9 arrived with a body this host could not read as DATAID and V, so it was refused.");
        }

        return new SecsMessage(6, 10, replyExpected: false)
        {
            Name = "UnformattedDataAcknowledge",
            Item = SecsItem.B((byte)acknowledgement).Named("ACKC6"),
        };
    }

    /// <summary>Reads S10F7 and raises it. There is no reply, so this answers nothing.</summary>
    /// <remarks>
    /// Returns a message because the dispatch switch is built that way and every other arm has one
    /// to give, and the connection never sends it: <c>transaction.ReplyExpected</c> is false for a
    /// row E5 gives no secondary, so the reply is disposed unsent. The alternative was a second
    /// shape of arm for one message.
    /// </remarks>
    private SecsMessage OnTerminalNotAccessible(SecsMessage request)
    {
        var id = GemItems.TryReadCode(request.Item, out var code) ? code : (byte)0;

        _logger.LogWarning("The equipment says terminal {Terminal} cannot be reached.", id);
        TerminalNotAccessible?.Invoke(this, new GemTerminalUnavailableEventArgs(id));

        return new SecsMessage(10, 8, replyExpected: false) { Name = "TerminalNotAccessibleIgnored" };
    }

    private SecsMessage OnProgramNotice(SecsMessage request, byte replyFunction)
    {
        var id = request.Item is { } item && item.Format.IsText() ? GemItems.ReadText(item) : string.Empty;

        var args = new GemProcessProgramNoticeEventArgs(id, (byte)(replyFunction - 1))
        {
            // NotFound when the machine named nothing, which is the table's nearest word for a
            // notice about no program - and not Accepted, because accepting one would tell the
            // machine its notice landed when there is nothing a host could act on.
            Acknowledgement = id.Length == 0
                ? GemProcessProgramAcknowledgement.NotFound
                : GemProcessProgramAcknowledgement.Accepted,
        };

        _logger.LogInformation(
            "S7F{Function} from the equipment: process program '{Program}'.",
            args.Function,
            id);

        ProcessProgramNoticeReceived?.Invoke(this, args);

        return new SecsMessage(7, replyFunction, replyExpected: false)
        {
            Name = replyFunction == 30 ? "ProcessProgramChangeAcknowledge" : "ProcessProgramSelectedAcknowledge",
            Item = SecsItem.B((byte)args.Acknowledgement).Named("ACKC7"),
        };
    }

    private SecsMessage OnMultiBlockInquire(SecsMessage request)
    {
        var dataId = 0u;
        var length = 0u;
        var readable = false;

        if (request.Item is { IsList: true, Count: 2 } body)
        {
            readable = true;

            if (GemItems.TryReadId(body[0], out var data))
            {
                dataId = (uint)Math.Min(data, uint.MaxValue);
            }

            if (GemItems.TryReadId(body[1], out var declared))
            {
                length = (uint)Math.Min(declared, uint.MaxValue);
            }
        }

        var args = new GemMultiBlockInquiryEventArgs(dataId, length);

        if (!readable)
        {
            // An inquire this end could not read is refused, and NoSpace is the refusal to use:
            // Busy invites the machine to ask again, and the second attempt would be just as
            // unreadable as the first.
            args.Grant = GemMultiBlockGrant.NoSpace;
        }

        // Only the refusal is written here. The grant itself is the property's own initialiser, so
        // the default a host sees documented on GemMultiBlockInquiryEventArgs.Grant is the default
        // that runs - this used to set both, which made the initialiser unreachable and let a
        // control change it to NoSpace with the whole suite still green.

        MultiBlockInquired?.Invoke(this, args);

        return new SecsMessage(6, 6, replyExpected: false)
        {
            Name = "MultiBlockGrant",
            Item = SecsItem.B((byte)args.Grant).Named("GRANT6"),
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

        var args = new GemEventReportEventArgs(dataId, eventId, reports, (byte)(replyFunction - 1))
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
