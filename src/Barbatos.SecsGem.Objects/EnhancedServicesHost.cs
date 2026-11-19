// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Objects;

/// <summary>
/// The asking end of E39's enhanced services: GEM's own questions, addressed to one object.
/// </summary>
/// <remarks>
/// <para>
/// Eight transactions, all of them host to equipment, and every one of them a GEM message with an
/// <c>OBJSPEC</c> in front. A host that knows how to read S1F3 and send S2F41 already knows this
/// class; what it gains is the ability to say <em>which chamber</em>.
/// </para>
/// <para>
/// <strong>The values come back through a callback</strong>, as they do on
/// <see cref="ObjectServices"/>, and for the same reason: a value is a view into the reply and the
/// reply is disposed when the method returns, so handing the items back would be handing back
/// memory that has gone. Copy inside the callback anything kept past it. The alarms are not like
/// that - <see cref="EnhancedAlarm"/> holds numbers and a string - so they are simply returned.
/// </para>
/// <para>
/// This class declares no handlers, because a host answers nothing in this range. An equipment
/// uses <see cref="EnhancedServicesEquipment"/>.
/// </para>
/// </remarks>
public sealed class EnhancedServicesHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the asking end over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public EnhancedServicesHost(HsmsConnection connection, ILogger<EnhancedServicesHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<EnhancedServicesHost>.Instance;
    }

    /// <summary>Tells one object to do something - S2F49, answered by S2F50.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="command">What to do - <c>RCMD</c>.</param>
    /// <param name="parameters">What to do it with. <see langword="null"/> sends an empty list.</param>
    /// <param name="dataId">The host's own correlation number - <c>DATAID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The code, and anything wrong with a parameter.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> or <paramref name="command"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F50 did not carry an acknowledgement.</exception>
    /// <remarks>
    /// Read <see cref="EnhancedCommandResult.IsAccepted"/> rather than comparing the code to
    /// nought: <see cref="EnhancedCommandAcknowledgement.CompletionLater"/> is also a success, and
    /// treating it as a failure is the commonest mistake a host makes against this message.
    /// </remarks>
    public async ValueTask<EnhancedCommandResult> CommandAsync(
        string objectSpecification,
        string command,
        IReadOnlyList<ObjectParameter>? parameters = null,
        uint dataId = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(command);

        var request = new EnhancedCommandRequest
        {
            ObjectSpecification = objectSpecification,
            Command = command,
            DataId = dataId,
            Parameters = parameters ?? [],
        };

        using var reply = await RequestAsync(
            new SecsMessage(2, 49, replyExpected: true)
            {
                Name = "EnhancedRemoteCommandSend",
                Item = EnhancedCodec.CommandBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        return EnhancedCodec.TryReadCommandResult(reply.Item)
            ?? throw new SecsException("S2F50 did not carry an acknowledgement.");
    }

    /// <summary>Ties a report to an object - S2F51, answered by S2F52.</summary>
    /// <param name="reportId">Which report - <c>RPTID</c>. Defined with GEM's own S2F33.</param>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><c>RPACK</c>, and the byte it arrived as.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F52 did not carry an acknowledgement.</exception>
    public ValueTask<EnhancedReportBindResult> BindReportAsync(
        uint reportId,
        string objectSpecification,
        CancellationToken cancellationToken = default)
        => BoundAsync(reportId, objectSpecification, bind: true, cancellationToken);

    /// <summary>Unties a report from an object - S2F53, answered by S2F54.</summary>
    /// <param name="reportId">Which report - <c>RPTID</c>.</param>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><c>URACK</c>, and the byte it arrived as.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F54 did not carry an acknowledgement.</exception>
    public ValueTask<EnhancedReportBindResult> UnbindReportAsync(
        uint reportId,
        string objectSpecification,
        CancellationToken cancellationToken = default)
        => BoundAsync(reportId, objectSpecification, bind: false, cancellationToken);

    /// <summary>Reads an object's equipment constants - S2F55, answered by S2F56.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="ids">Which <c>ECID</c>s, or an empty list for all of them.</param>
    /// <param name="read">Called with the values while the reply is alive. Copy anything kept past it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many values came back.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F56 did not carry a list of values.</exception>
    public ValueTask<int> GetEquipmentConstantsAsync(
        string objectSpecification,
        IReadOnlyList<uint> ids,
        Action<IReadOnlyList<SecsItem>> read,
        CancellationToken cancellationToken = default)
        => ValuesAsync(objectSpecification, ids, read, 55, "ECID", cancellationToken);

    /// <summary>Reads an object's status variables - S2F57, answered by S2F58.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="ids">Which <c>SVID</c>s, or an empty list for all of them.</param>
    /// <param name="read">Called with the values while the reply is alive.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many values came back.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F58 did not carry a list of values.</exception>
    /// <remarks>
    /// The n-th value answers the n-th identifier, and a variable the machine does not have is a
    /// zero-length item in its place rather than a gap - which is why the count coming back is
    /// worth comparing against the count asked for.
    /// </remarks>
    public ValueTask<int> GetStatusVariablesAsync(
        string objectSpecification,
        IReadOnlyList<uint> ids,
        Action<IReadOnlyList<SecsItem>> read,
        CancellationToken cancellationToken = default)
        => ValuesAsync(objectSpecification, ids, read, 57, "SVID", cancellationToken);

    /// <summary>Reads an object's data variables - S2F59, answered by S2F60.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="ids">Which <c>VID</c>s, or an empty list for all of them.</param>
    /// <param name="read">Called with the values while the reply is alive.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>How many values came back.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F60 did not carry a list of values.</exception>
    public ValueTask<int> GetDataVariablesAsync(
        string objectSpecification,
        IReadOnlyList<uint> ids,
        Action<IReadOnlyList<SecsItem>> read,
        CancellationToken cancellationToken = default)
        => ValuesAsync(objectSpecification, ids, read, 59, "VID", cancellationToken);

    /// <summary>Lists an object's alarms - S2F61, answered by S2F62.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="alarmIds">Which <c>ALID</c>s, or an empty list for all of them.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The alarms. Not positional: an alarm the machine does not have is simply absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="objectSpecification"/> or <paramref name="alarmIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F62 did not carry alarm data.</exception>
    public async ValueTask<IReadOnlyList<EnhancedAlarm>> GetAlarmsAsync(
        string objectSpecification,
        IReadOnlyList<uint> alarmIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(alarmIds);

        using var reply = await RequestAsync(
            new SecsMessage(2, 61, replyExpected: true)
            {
                Name = "EnhancedAlarmStatusRequest",
                Item = EnhancedCodec.ValueRequestBody(objectSpecification, alarmIds, "ALID"),
            },
            cancellationToken).ConfigureAwait(false);

        if (!EnhancedCodec.TryReadAlarmData(reply.Item, out var alarms))
        {
            throw new SecsException("S2F62 did not carry alarm data.");
        }

        return alarms;
    }

    /// <summary>Asks an object for diagnostic data - S2F63, answered by S2F64.</summary>
    /// <param name="objectSpecification">Which object.</param>
    /// <param name="dataId">Which diagnostic - <c>DATAID</c>.</param>
    /// <param name="read">Called with the data while the reply is alive. Copy anything kept past it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns><see langword="false"/> when the machine has no such diagnostic, which it says with a zero-length item.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S2F64 did not carry diagnostic data.</exception>
    public async ValueTask<bool> GetDiagnosticsAsync(
        string objectSpecification,
        uint dataId,
        Action<SecsItem> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(read);

        var request = new EnhancedDiagnosticRequest
        {
            ObjectSpecification = objectSpecification,
            DataId = dataId,
        };

        using var reply = await RequestAsync(
            new SecsMessage(2, 63, replyExpected: true)
            {
                Name = "EnhancedDiagnosticDataRequest",
                Item = EnhancedCodec.DiagnosticRequestBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!EnhancedCodec.TryReadDiagnosticData(reply.Item, out var echoed, out var data))
        {
            throw new SecsException("S2F64 did not carry diagnostic data.");
        }

        if (echoed != dataId)
        {
            // Worth saying and not worth throwing: the only thing S2F64 can be correlated by is
            // the DATAID it echoes, so a machine that echoes a different one has answered a
            // question nobody asked.
            _logger.LogWarning(
                "S2F64 echoed DATAID {Echoed} for a request that asked for {Asked}.",
                echoed,
                dataId);
        }

        if (data is null)
        {
            return false;
        }

        read(data);
        return true;
    }

    private async ValueTask<EnhancedReportBindResult> BoundAsync(
        uint reportId,
        string objectSpecification,
        bool bind,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);

        var binding = new EnhancedReportBinding
        {
            ReportId = reportId,
            ObjectSpecification = objectSpecification,
        };

        using var reply = await RequestAsync(
            new SecsMessage(2, bind ? (byte)51 : (byte)53, replyExpected: true)
            {
                Name = bind ? "BindReportRequest" : "UnbindReportRequest",
                Item = EnhancedCodec.BindingBody(binding),
            },
            cancellationToken).ConfigureAwait(false);

        return EnhancedCodec.TryReadBindResult(reply.Item)
            ?? throw new SecsException($"S2F{(bind ? 52 : 54)} did not carry an acknowledgement.");
    }

    private async ValueTask<int> ValuesAsync(
        string objectSpecification,
        IReadOnlyList<uint> ids,
        Action<IReadOnlyList<SecsItem>> read,
        byte function,
        string item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(objectSpecification);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(read);

        var name = function switch
        {
            55 => "EnhancedEquipmentConstantRequest",
            57 => "EnhancedStatusVariableRequest",
            _ => "EnhancedDataVariableRequest",
        };

        using var reply = await RequestAsync(
            new SecsMessage(2, function, replyExpected: true)
            {
                Name = name,
                Item = EnhancedCodec.ValueRequestBody(objectSpecification, ids, item),
            },
            cancellationToken).ConfigureAwait(false);

        if (!EnhancedCodec.TryReadValueData(reply.Item, out var values))
        {
            throw new SecsException($"S2F{function + 1} did not carry a list of values.");
        }

        if (ids.Count > 0 && values.Count != ids.Count)
        {
            // The reply is positional and has no identifiers in it, so a count that does not match
            // means every value after the first gap is against the wrong variable. This end
            // cannot repair that - it does not know where the gap is - so it says so and hands
            // over what arrived.
            _logger.LogWarning(
                "S2F{Function} asked for {Asked} value(s) and {Got} came back, so the reply cannot be lined up.",
                function,
                ids.Count,
                values.Count);
        }

        read(values);
        return values.Count;
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
