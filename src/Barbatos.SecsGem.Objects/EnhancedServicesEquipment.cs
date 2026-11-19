// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// The answering end of E39's enhanced services - the eight stream 2 primaries that carry an
/// <c>OBJSPEC</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two classes here rather than the one <see cref="ObjectServices"/> is, and the catalogue is why:
/// every primary in this range is marked host to equipment, so unlike stream 14 there is a passive
/// end and an active one. This is the passive end, and it has no connection of its own - it only
/// answers transactions somebody hands it, which is the same shape <c>CarrierEquipment</c> has.
/// </para>
/// <para>
/// <strong>What these messages are for.</strong> GEM can ask a machine for <c>SVID</c> 1234 and
/// the machine has one answer. A cluster tool has four chambers and therefore four answers, and
/// S1F3 has nowhere to put the question "which one". These eight transactions are GEM's own
/// requests with an object in front: the same identifiers, the same codes, the same reply shapes,
/// scoped. The catalogue's <c>HCACK</c> even reserves a code - 6, no such object exists - that
/// nothing in GEM can produce.
/// </para>
/// <para>
/// The data is <see cref="IEnhancedServiceProvider"/>'s, for the reason set out there: GEM's own
/// stores are not addressed by object, so nothing but the application can be.
/// </para>
/// </remarks>
public sealed class EnhancedServicesEquipment
{
    private readonly IEnhancedServiceProvider _provider;
    private readonly ILogger _logger;

    /// <summary>Initialises the answering end.</summary>
    /// <param name="provider">The variables, constants, alarms and diagnostics this machine has, per object.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
    public EnhancedServicesEquipment(
        IEnhancedServiceProvider provider,
        ILogger<EnhancedServicesEquipment>? logger = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger ?? NullLogger<EnhancedServicesEquipment>.Instance;
    }

    /// <summary>Every primary this class answers - the eight that carry an <c>OBJSPEC</c>.</summary>
    /// <remarks>
    /// None of them overlaps GEM's own list, which is worth stating because the two sets share a
    /// stream: <c>GemEquipment</c> declares S2F13, F15, F17, F29, F31, F33, F35, F37 and F41, and
    /// an equipment that calls both registrations has one routing table with no collision in it.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (2, 49),   // Enhanced Remote Command
        (2, 51),   // Bind Report Request
        (2, 53),   // Unbind Report Request
        (2, 55),   // Enhanced Equipment Constant Request
        (2, 57),   // Enhanced Status Variable Request
        (2, 59),   // Enhanced Data Variable Request
        (2, 61),   // Enhanced Alarm Status Request
        (2, 63),   // Enhanced Diagnostic Data Request
    ];

    /// <summary>Gets the data this end answers out of.</summary>
    public IEnhancedServiceProvider Provider => _provider;

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 2
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the enhanced services handler with nothing to answer it.",
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
    {
        switch (transaction.F)
        {
            case 49:
            {
                if (!EnhancedCodec.TryReadCommand(transaction.Message.Item, out var request) || request is null)
                {
                    // S2F50 has a code, unlike the namelist replies in stream 14, so an unreadable
                    // body is said rather than answered with silence. Three is the only defined
                    // code for "what you sent was wrong" that does not also claim the command does
                    // not exist, which this end cannot know.
                    _logger.LogWarning("S2F49 arrived with a body this end could not read.");

                    return Reply(
                        50,
                        "EnhancedRemoteCommandAcknowledge",
                        EnhancedCodec.CommandResultBody(
                            EnhancedCommandResult.From(EnhancedCommandAcknowledgement.InvalidParameter)));
                }

                var result = await CommandedAsync(request, cancellationToken).ConfigureAwait(false);

                return Reply(50, "EnhancedRemoteCommandAcknowledge", EnhancedCodec.CommandResultBody(result));
            }

            case 51:
            case 53:
            {
                var bind = transaction.F == 51;

                if (!EnhancedCodec.TryReadBinding(transaction.Message.Item, out var binding) || binding is null)
                {
                    _logger.LogWarning("S2F{F} arrived with a body this end could not read.", transaction.F);

                    return BindReply(bind, EnhancedReportAcknowledgement.Denied);
                }

                var acknowledgement = bind
                    ? await _provider.BindReportAsync(binding, cancellationToken).ConfigureAwait(false)
                    : await _provider.UnbindReportAsync(binding, cancellationToken).ConfigureAwait(false);

                return BindReply(bind, acknowledgement);
            }

            case 55:
            case 57:
            case 59:
            {
                var (reply, name) = transaction.F switch
                {
                    55 => ((byte)56, "EnhancedEquipmentConstantData"),
                    57 => ((byte)58, "EnhancedStatusVariableData"),
                    _ => ((byte)60, "EnhancedDataVariableData"),
                };

                if (!EnhancedCodec.TryReadValueRequest(transaction.Message.Item, out var specification, out var ids))
                {
                    return Empty(reply, name, $"S2F{transaction.F}");
                }

                var request = new EnhancedValueRequest { ObjectSpecification = specification, Ids = ids };

                var values = transaction.F switch
                {
                    55 => await _provider.GetEquipmentConstantsAsync(request, cancellationToken).ConfigureAwait(false),
                    57 => await _provider.GetStatusVariablesAsync(request, cancellationToken).ConfigureAwait(false),
                    _ => await _provider.GetDataVariablesAsync(request, cancellationToken).ConfigureAwait(false),
                };

                return Reply(reply, name, EnhancedCodec.ValueDataBody(EnhancedCodec.Aligned(values ?? [], ids.Count)));
            }

            case 61:
            {
                if (!EnhancedCodec.TryReadValueRequest(transaction.Message.Item, out var specification, out var ids))
                {
                    return Empty(62, "EnhancedAlarmStatusData", "S2F61");
                }

                var alarms = await _provider.GetAlarmsAsync(
                    new EnhancedAlarmRequest { ObjectSpecification = specification, AlarmIds = ids },
                    cancellationToken).ConfigureAwait(false);

                return Reply(62, "EnhancedAlarmStatusData", EnhancedCodec.AlarmDataBody(alarms ?? []));
            }

            case 63:
            {
                if (!EnhancedCodec.TryReadDiagnosticRequest(transaction.Message.Item, out var request)
                    || request is null)
                {
                    return Reply(
                        64,
                        "EnhancedDiagnosticDataReport",
                        EnhancedCodec.DiagnosticDataBody(0, null));
                }

                var data = await _provider.GetDiagnosticsAsync(request, cancellationToken).ConfigureAwait(false);

                // DATAID is echoed from the request, which is the only thing S2F64 can correlate
                // with: a host that asked for two diagnostics at once has nothing else to tell the
                // answers apart by.
                return Reply(
                    64,
                    "EnhancedDiagnosticDataReport",
                    EnhancedCodec.DiagnosticDataBody(request.DataId, data));
            }

            default:
                return null;
        }
    }

    /// <summary>Performs a command, and does not let a throwing handler take the link down.</summary>
    /// <remarks>
    /// The one guarded call in this class, and the same guard GEM puts on a remote command handler:
    /// a handler that throws is a bug in the application rather than a reason to drop a link that
    /// is serving everything else. The read methods are not guarded, because a read that throws
    /// has nothing it could honestly answer - there is no code in S2F56, S2F58 or S2F60 to carry a
    /// failure, and inventing a zero-length answer would report every variable as absent.
    /// </remarks>
    private async ValueTask<EnhancedCommandResult> CommandedAsync(
        EnhancedCommandRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _provider.CommandAsync(request, cancellationToken).ConfigureAwait(false)
                ?? EnhancedCommandResult.From(EnhancedCommandAcknowledgement.CannotPerformNow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "The handler for enhanced command '{Command}' on '{Object}' threw.",
                request.Command,
                request.ObjectSpecification);

            return EnhancedCommandResult.From(EnhancedCommandAcknowledgement.CannotPerformNow);
        }
    }

    private static SecsMessage BindReply(bool bind, EnhancedReportAcknowledgement acknowledgement)
        => Reply(
            bind ? (byte)52 : (byte)54,
            bind ? "BindReportAcknowledge" : "UnbindReportAcknowledge",
            EnhancedCodec.BindResultBody(acknowledgement, bind ? "RPACK" : "URACK"));

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(2, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Empty(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering a zero-length list.",
            primary);

        return Reply(function, name, SecsItem.L());
    }
}
