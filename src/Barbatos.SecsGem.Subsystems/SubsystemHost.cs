// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Subsystems;

/// <summary>
/// The host side of stream 18: the end that asks, and that is told.
/// </summary>
/// <remarks>
/// <para>
/// Six methods for the six primaries a host sends, and two handlers for the two the equipment
/// sends - an event report and an alarm report, each raised here as an event rather than handed to
/// a store, because a host has nowhere to put them and every host has somewhere to log them.
/// </para>
/// <para>
/// <strong>Both ends of stream 18 send primaries</strong>, which makes this the second stream here
/// to work that way after stream 3. The two sets are disjoint, so declaring the wrong one is not
/// something the routing table can catch: a host that registered the equipment's six would answer
/// questions it is the one that asks.
/// </para>
/// <para>
/// One instance sits on one <see cref="HsmsConnection"/>. It holds nothing that needs releasing and
/// is not disposable.
/// </para>
/// </remarks>
public sealed class SubsystemHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host side over a connection.</summary>
    /// <param name="connection">The link.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public SubsystemHost(HsmsConnection connection, ILogger<SubsystemHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<SubsystemHost>.Instance;
    }

    /// <summary>Every primary this end answers: the two the equipment sends.</summary>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (18, 11),  // Subsystem Event Report Send
        (18, 13),  // Subsystem Alarm Report Send
    ];

    /// <summary>Raised when a subsystem reports an event - S18F11.</summary>
    /// <remarks>Raised before the acknowledgement goes out, so a handler that throws is a handler that leaves the machine waiting on its T3.</remarks>
    public event EventHandler<SubsystemEventArgs>? EventReported;

    /// <summary>Raised when a subsystem raises or clears an alarm - S18F13.</summary>
    /// <remarks>Including a clear: <see cref="SubsystemAlarm.IsSet"/> is the top bit of <c>ALCD</c>, and the same message carries both.</remarks>
    public event EventHandler<SubsystemAlarmEventArgs>? AlarmReported;

    /// <summary>Gets or sets what this end answers an event or an alarm report with.</summary>
    /// <remarks>
    /// <see cref="SubsystemAcknowledgement.Accepted"/> by default, which is the only code in
    /// <c>SSACK</c>'s table that means anything here - the other three were written for a variable
    /// write. A host that wants to refuse a report has <see cref="SubsystemAcknowledgement.Other"/>
    /// and nothing more specific, and that is the table's limit rather than this package's.
    /// </remarks>
    public SubsystemAcknowledgement ReportAcknowledgement { get; set; } = SubsystemAcknowledgement.Accepted;

    /// <summary>Commands one subsystem - S18F1, answered by S18F2.</summary>
    /// <param name="command">Which subsystem, which command, and its parameters.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The answer, and which parameters were wrong if the machine said.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <strong>Two codes are successes</strong> - see
    /// <see cref="SubsystemCommandResult.IsAccepted"/>. The body is S2F41's with an <c>SSID</c> in
    /// front, which is also why <c>HCACK</c> and <c>CPACK</c> mean here exactly what they mean in
    /// GEM.
    /// </remarks>
    public async ValueTask<SubsystemCommandResult> CommandAsync(
        SubsystemCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        using var reply = await RequestAsync(
            new SecsMessage(18, 1, replyExpected: true)
            {
                Name = "SubsystemCommandSend",
                Item = SubsystemCodec.CommandBody(command),
            },
            cancellationToken).ConfigureAwait(false);

        if (!SubsystemCodec.TryReadCommandResult(reply.Item, out var echoed, out var code, out var faults))
        {
            throw new SecsException("S18F2 carried no HCACK.");
        }

        if (echoed != command.Subsystem)
        {
            _logger.LogWarning(
                "S18F2 answered for subsystem {Echoed} a command addressed to {Asked}.",
                echoed,
                command.Subsystem);
        }

        return new SubsystemCommandResult
        {
            Subsystem = echoed,
            Acknowledgement = ReadCommandAcknowledgement(code),
            Faults = [.. faults.Select(fault => new SubsystemParameterFault(
                fault.Name,
                ReadParameterAcknowledgement(fault.Code)))],
        };
    }

    /// <summary>Asks what state a subsystem is in - S18F3, answered by S18F4.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its state, or <see langword="null"/> when the machine has no such subsystem.</returns>
    /// <exception cref="SecsException">The reply was not a subsystem status.</exception>
    /// <remarks>
    /// <see langword="null"/> is what an empty <c>SSSTATE</c> means: S18F4 has no acknowledgement
    /// code, so "no such subsystem" and "here is its state" arrive in the same shape and are told
    /// apart by the state being empty. <see cref="ListAsync"/> is the way to find out what there
    /// is.
    /// </remarks>
    public async ValueTask<string?> StatusAsync(
        SubsystemId id,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(18, 3, replyExpected: true)
            {
                Name = "SubsystemStatusRequest",
                Item = SubsystemCodec.SubsystemIdBody(id),
            },
            cancellationToken).ConfigureAwait(false);

        if (!SubsystemCodec.TryReadStatus(reply.Item, out var echoed, out var state))
        {
            throw new SecsException("S18F4 did not carry a subsystem status.");
        }

        if (echoed != id)
        {
            _logger.LogWarning(
                "S18F4 reported on subsystem {Echoed} for a request about {Asked}.",
                echoed,
                id);
        }

        return state.Length == 0 ? null : state;
    }

    /// <summary>Reads one of a subsystem's variables - S18F5, answered by S18F6.</summary>
    /// <param name="id">Which subsystem.</param>
    /// <param name="variableId">Which variable - <c>VID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its value, or <see langword="null"/> when there is no such subsystem or variable.</returns>
    /// <exception cref="SecsException">The reply was not a subsystem variable.</exception>
    /// <remarks>
    /// <strong>A read cannot be refused and a write can.</strong> S18F6 carries no acknowledgement
    /// code, so an unknown variable comes back as an empty value - while
    /// <see cref="WriteAsync"/> gets <see cref="SubsystemAcknowledgement.UnknownVariable"/> for the
    /// same variable. That asymmetry is E5's.
    /// </remarks>
    public async ValueTask<string?> ReadAsync(
        SubsystemId id,
        uint variableId,
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(18, 5, replyExpected: true)
            {
                Name = "SubsystemDataReadRequest",
                Item = SubsystemCodec.VariableRequestBody(id, variableId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!SubsystemCodec.TryReadVariable(reply.Item, out var variable) || variable is null)
        {
            throw new SecsException("S18F6 did not carry a subsystem variable.");
        }

        if (variable.Subsystem != id || variable.VariableId != variableId)
        {
            _logger.LogWarning(
                "S18F6 answered with {Echoed} for a request that asked for subsystem {Asked} VID {VariableId}.",
                variable,
                id,
                variableId);
        }

        return variable.Value.Length == 0 ? null : variable.Value;
    }

    /// <summary>Writes one of a subsystem's variables - S18F7, answered by S18F8.</summary>
    /// <param name="variable">Which subsystem, which variable, and the value.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="variable"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>The transaction <c>SSACK</c>'s table was written for, and the only one in stream 18 whose refusals carry a reason.</remarks>
    public async ValueTask<SubsystemAcknowledgement> WriteAsync(
        SubsystemVariable variable,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(variable);

        using var reply = await RequestAsync(
            new SecsMessage(18, 7, replyExpected: true)
            {
                Name = "SubsystemDataWriteRequest",
                Item = SubsystemCodec.VariableBody(variable),
            },
            cancellationToken).ConfigureAwait(false);

        if (!SubsystemCodec.TryReadWriteResult(reply.Item, out var code))
        {
            throw new SecsException("S18F8 carried no SSACK.");
        }

        return ReadAcknowledgement(code, "S18F8");
    }

    /// <summary>Asks what subsystems the machine has - S18F9, answered by S18F10.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them, with their names and states. Empty when the machine declares none.</returns>
    /// <remarks>
    /// The only primary in stream 18 with no body. An unreadable reply comes back empty rather
    /// than throwing, with the log saying which happened - a listing that could not be parsed and
    /// a machine with no subsystems are not worth an exception between them.
    /// </remarks>
    public async ValueTask<IReadOnlyList<Subsystem>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(18, 9, replyExpected: true) { Name = "SubsystemListRequest" },
            cancellationToken).ConfigureAwait(false);

        var subsystems = SubsystemCodec.ReadList(reply.Item);

        if (subsystems.Count == 0 && reply.Item is { Count: > 0 })
        {
            _logger.LogWarning("S18F10 carried a body with no readable subsystem in it.");
        }

        return subsystems;
    }

    /// <summary>Asks a reader what a 2D code says - S18F15, answered by S18F16.</summary>
    /// <param name="targetId">What to read - <c>TARGETID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the reader reports.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="targetId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a code condition.</exception>
    /// <remarks>
    /// The one pair in stream 18 that is not GEM's message set re-addressed - and the only pair
    /// whose every item name the dictionary defines.
    /// </remarks>
    public async ValueTask<SubsystemCodeCondition> ReadCodeAsync(
        string targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetId);

        using var reply = await RequestAsync(
            new SecsMessage(18, 15, replyExpected: true)
            {
                Name = "Read2DCodeConditionRequest",
                Item = SubsystemCodec.TargetBody(targetId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!SubsystemCodec.TryReadCodeCondition(
            reply.Item,
            out var echoed,
            out var code,
            out var materialId,
            out var status,
            out var condition))
        {
            throw new SecsException("S18F16 did not carry a code condition.");
        }

        if (!string.Equals(echoed, targetId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "S18F16 reported on target '{Echoed}' for a request about '{Asked}'.",
                echoed,
                targetId);
        }

        return new SubsystemCodeCondition
        {
            TargetId = echoed,
            Acknowledgement = ReadAcknowledgement(code, "S18F16"),
            MaterialId = materialId,
            Status = status,
            Condition = condition,
        };
    }

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The report is raised before the acknowledgement is sent, and the acknowledgement is
    /// <see cref="ReportAcknowledgement"/> either way: a host that could not make sense of a report
    /// still heard one, and <c>SSACK</c> has no code for "that made no sense".
    /// </remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var answered = transaction.S == 18 && transaction.F is 11 or 13;

        if (!answered)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the subsystem host handler with nothing to answer it.",
                transaction.S,
                transaction.F);

            return;
        }

        if (transaction.F == 11)
        {
            RaiseEvent(transaction.Message);
        }
        else
        {
            RaiseAlarm(transaction.Message);
        }

        using var reply = new SecsMessage(18, (byte)(transaction.F + 1), replyExpected: false)
        {
            Name = transaction.F == 11 ? "SubsystemEventReportAcknowledge" : "SubsystemAlarmReportAcknowledge",
            Item = SubsystemCodec.CodeBody((byte)ReportAcknowledgement, "SSACK"),
        };

        if (transaction.ReplyExpected)
        {
            await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- the pieces

    private void RaiseEvent(SecsMessage message)
    {
        if (!SubsystemCodec.TryReadEvent(message.Item, out var report) || report is null)
        {
            _logger.LogWarning("S18F11 arrived with a body this end could not read.");
            return;
        }

        EventReported?.Invoke(this, new SubsystemEventArgs(report));
    }

    private void RaiseAlarm(SecsMessage message)
    {
        if (!SubsystemCodec.TryReadAlarm(message.Item, out var alarm, out var category) || alarm is null)
        {
            _logger.LogWarning("S18F13 arrived with a body this end could not read.");
            return;
        }

        var named = category <= (byte)SubsystemAlarmCategory.DataIntegrity
            ? (SubsystemAlarmCategory)category
            : SubsystemAlarmCategory.Other;

        if (named == SubsystemAlarmCategory.Other)
        {
            _logger.LogInformation(
                "S18F13 carried ALCD category {Category}, which the table leaves to the equipment.",
                category);
        }

        AlarmReported?.Invoke(this, new SubsystemAlarmEventArgs(new SubsystemAlarm
        {
            Subsystem = alarm.Subsystem,
            AlarmId = alarm.AlarmId,
            IsSet = alarm.IsSet,
            Category = named,
            Text = alarm.Text,
        }));
    }

    private SubsystemCommandAcknowledgement ReadCommandAcknowledgement(byte code)
    {
        if (code < (byte)SubsystemCommandAcknowledgement.Other)
        {
            return (SubsystemCommandAcknowledgement)code;
        }

        _logger.LogInformation(
            "S18F2 carried HCACK {Code}, which the table reserves and does not name.",
            code);

        return SubsystemCommandAcknowledgement.Other;
    }

    private SubsystemParameterAcknowledgement ReadParameterAcknowledgement(byte code)
    {
        if (code is >= (byte)SubsystemParameterAcknowledgement.UnknownName
            and < (byte)SubsystemParameterAcknowledgement.Other)
        {
            return (SubsystemParameterAcknowledgement)code;
        }

        // Zero included, and deliberately: CPACK's table starts at 1, so a machine that sent a
        // zero has said something the item has no meaning for.
        _logger.LogInformation(
            "S18F2 carried CPACK {Code}, which that table does not name - it starts at 1 and reserves 4 to 63.",
            code);

        return SubsystemParameterAcknowledgement.Other;
    }

    private SubsystemAcknowledgement ReadAcknowledgement(byte code, string what)
    {
        if (code < (byte)SubsystemAcknowledgement.Other)
        {
            return (SubsystemAcknowledgement)code;
        }

        _logger.LogInformation(
            "{What} carried SSACK {Code}, which the table reserves and does not name.",
            what,
            code);

        return SubsystemAcknowledgement.Other;
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>An event one subsystem raised.</summary>
/// <param name="report">What it reported.</param>
public sealed class SubsystemEventArgs(SubsystemEvent report) : EventArgs
{
    /// <summary>Gets what was reported.</summary>
    public SubsystemEvent Report { get; } = report;
}

/// <summary>An alarm one subsystem raised, or cleared.</summary>
/// <param name="alarm">What it reported.</param>
public sealed class SubsystemAlarmEventArgs(SubsystemAlarm alarm) : EventArgs
{
    /// <summary>Gets what was reported.</summary>
    public SubsystemAlarm Alarm { get; } = alarm;
}
