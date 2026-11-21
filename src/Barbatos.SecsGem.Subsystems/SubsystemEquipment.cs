// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Subsystems;

/// <summary>
/// The equipment side of stream 18: the end the subsystems are on.
/// </summary>
/// <remarks>
/// <para>
/// It answers the six primaries a host sends - command, status, read, write, list, code read - out
/// of an <see cref="ISubsystemStore"/>, and sends the two of its own that E5 gives it: an event
/// report and an alarm report, each about one subsystem.
/// </para>
/// <para>
/// So stream 18 is the second stream in this stack where <strong>both ends send primaries</strong>,
/// after stream 3. The two declarations are disjoint - six here and two at the host - which means
/// the routing table cannot catch the two being mixed up, and a test asserts the split instead.
/// </para>
/// <para>
/// <strong>What can be refused and what cannot is E5's doing.</strong> S18F2 carries an
/// <c>HCACK</c>, so an unreadable command is refused in the transaction the host is waiting on.
/// S18F8 carries an <c>SSACK</c>, so a bad write is too. S18F4, S18F6 and S18F10 carry
/// <strong>no code at all</strong> - a status, a variable and a listing are data and nothing else
/// - so an unreadable request is answered with an empty value or an empty list. S18F16 carries an
/// <c>SSACK</c> whose named codes are all about writing a variable, so a failed code read is
/// <see cref="SubsystemAcknowledgement.Other"/>: the table has nothing better and inventing a
/// meaning for 1, 2 or 3 would put a write's vocabulary on a reader.
/// </para>
/// </remarks>
public sealed class SubsystemEquipment
{
    private readonly ISubsystemStore _store;
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a connection and a store.</summary>
    /// <param name="store">The subsystems.</param>
    /// <param name="connection">The link, for the two primaries this end sends.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="connection"/> is <see langword="null"/>.</exception>
    public SubsystemEquipment(
        ISubsystemStore store,
        HsmsConnection connection,
        ILogger<SubsystemEquipment>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<SubsystemEquipment>.Instance;
    }

    /// <summary>Every primary this end answers: the six a host sends.</summary>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (18, 1),   // Subsystem Command Send
        (18, 3),   // Subsystem Status Request
        (18, 5),   // Subsystem Data Read Request
        (18, 7),   // Subsystem Data Write Request
        (18, 9),   // Subsystem List Request
        (18, 15),  // Read 2D Code Condition Request
    ];

    /// <summary>Reports an event one subsystem raised - S18F11, answered by S18F12.</summary>
    /// <param name="report">Which subsystem, which event, and when.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The host's acknowledgement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <para>
    /// This is how a command answered
    /// <see cref="SubsystemCommandAcknowledgement.CompletionLater"/> says it has finished.
    /// </para>
    /// <para>
    /// <strong>It carries no report data.</strong> GEM's S6F11 is <c>DATAID, CEID</c> and then the
    /// linked reports; stream 18's event is <c>SSID, CEID, TIME</c> and stops. A host that wants
    /// values with its events reads them with S18F5, or uses GEM. That is the row as the catalogue
    /// has it and not a reduction this package made.
    /// </para>
    /// </remarks>
    public async ValueTask<SubsystemAcknowledgement> ReportEventAsync(
        SubsystemEvent report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var reply = await RequestAsync(
            new SecsMessage(18, 11, replyExpected: true)
            {
                Name = "SubsystemEventReportSend",
                Item = SubsystemCodec.EventBody(report),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S18F12");
    }

    /// <summary>Reports an alarm one subsystem raised, or cleared - S18F13, answered by S18F14.</summary>
    /// <param name="alarm">Which subsystem, which alarm, whether it is set, and the text.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The host's acknowledgement.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="alarm"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// The same three items GEM's S5F1 carries, with an <c>SSID</c> in front - so a machine that
    /// implements both says alarms about itself in stream 5 and alarms about a sub-part here, and
    /// the difference is which one a host can act on per subsystem.
    /// </remarks>
    public async ValueTask<SubsystemAcknowledgement> ReportAlarmAsync(
        SubsystemAlarm alarm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(alarm);

        using var reply = await RequestAsync(
            new SecsMessage(18, 13, replyExpected: true)
            {
                Name = "SubsystemAlarmReportSend",
                Item = SubsystemCodec.AlarmBody(alarm),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S18F14");
    }

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>The store is asked before the reply bit is looked at, so a write sent without it still happens.</remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 18
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the subsystem equipment handler with nothing to answer it.",
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

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
        => transaction.F switch
        {
            1 => await OnCommandAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            3 => await OnStatusAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            5 => await OnReadAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            7 => await OnWriteAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            9 => await OnListAsync(cancellationToken).ConfigureAwait(false),
            15 => await OnCodeConditionAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    // ----------------------------------------------------------------- commanding

    private async ValueTask<SecsMessage> OnCommandAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!SubsystemCodec.TryReadCommand(message.Item, out var command) || command is null)
        {
            _logger.LogWarning("S18F1 arrived with a body this end could not read; answering HCACK InvalidParameter.");

            // HCACK 3 rather than 1: the command may well exist, and what went wrong was the body.
            return Reply(2, "SubsystemCommandAcknowledge", SubsystemCodec.CommandResultBody(
                new SubsystemCommandResult
                {
                    Subsystem = SubsystemId.Named(string.Empty),
                    Acknowledgement = SubsystemCommandAcknowledgement.InvalidParameter,
                }));
        }

        var result = await _store.CommandAsync(command, cancellationToken).ConfigureAwait(false);

        return Reply(2, "SubsystemCommandAcknowledge", SubsystemCodec.CommandResultBody(result));
    }

    // -------------------------------------------------------------------- status

    private async ValueTask<SecsMessage> OnStatusAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!SubsystemCodec.TryReadSubsystemId(message.Item, out var id))
        {
            return Nothing(4, "SubsystemStatusData", "S18F3");
        }

        var state = await _store.StatusAsync(id, cancellationToken).ConfigureAwait(false);

        // An empty state is "no such subsystem": S18F4 has no code to say it with, so the
        // identifier comes back and the state does not.
        return Reply(4, "SubsystemStatusData", SubsystemCodec.StatusBody(id, state ?? string.Empty));
    }

    // ----------------------------------------------------------------- variables

    private async ValueTask<SecsMessage> OnReadAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!SubsystemCodec.TryReadVariableRequest(message.Item, out var id, out var variableId))
        {
            return Nothing(6, "SubsystemDataReadData", "S18F5");
        }

        var value = await _store.ReadAsync(id, variableId, cancellationToken).ConfigureAwait(false);

        return Reply(6, "SubsystemDataReadData", SubsystemCodec.VariableBody(new SubsystemVariable
        {
            Subsystem = id,
            VariableId = variableId,
            Value = value ?? string.Empty,
        }));
    }

    private async ValueTask<SecsMessage> OnWriteAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!SubsystemCodec.TryReadVariable(message.Item, out var variable) || variable is null)
        {
            _logger.LogWarning("S18F7 arrived with a body this end could not read; answering SSACK UnknownVariable.");

            return Reply(
                8,
                "SubsystemDataWriteAcknowledge",
                SubsystemCodec.WriteResultBody(
                    SubsystemId.Named(string.Empty),
                    (byte)SubsystemAcknowledgement.UnknownVariable));
        }

        var acknowledgement = await _store.WriteAsync(variable, cancellationToken).ConfigureAwait(false);

        return Reply(
            8,
            "SubsystemDataWriteAcknowledge",
            SubsystemCodec.WriteResultBody(variable.Subsystem, (byte)acknowledgement));
    }

    // ---------------------------------------------------------------- the listing

    private async ValueTask<SecsMessage> OnListAsync(CancellationToken cancellationToken)
    {
        var subsystems = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        return Reply(10, "SubsystemListData", SubsystemCodec.ListBody(subsystems));
    }

    // ------------------------------------------------------------- the 2D code read

    private async ValueTask<SecsMessage> OnCodeConditionAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!SubsystemCodec.TryReadTarget(message.Item, out var targetId))
        {
            return Nothing(16, "Read2DCodeConditionData", "S18F15");
        }

        var condition = await _store.ReadCodeAsync(targetId, cancellationToken).ConfigureAwait(false);

        return Reply(16, "Read2DCodeConditionData", SubsystemCodec.CodeConditionBody(
            condition ?? new SubsystemCodeCondition
            {
                TargetId = targetId,

                // SSACK's three refusals are all about writing a variable, so a target this
                // machine does not have gets the only code left.
                Acknowledgement = SubsystemAcknowledgement.Other,
            }));
    }

    // ---------------------------------------------------------------- the pieces

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(18, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Nothing(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering an empty one.",
            primary);

        return Reply(function, name, SecsItem.L());
    }

    private SubsystemAcknowledgement ReadAcknowledgement(SecsMessage reply, string what)
    {
        var code = SubsystemCodec.ReadCode(reply.Item)
            ?? throw new SecsException($"{what} carried no SSACK.");

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
        // The primary is built by the caller and of no use afterwards, so it is disposed here. The
        // reply belongs to whoever reads it, which is this class in every case.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
