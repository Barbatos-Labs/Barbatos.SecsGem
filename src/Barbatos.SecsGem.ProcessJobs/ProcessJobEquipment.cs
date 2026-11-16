// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>
/// The equipment side of E40: it answers the host's twelve primaries out of a
/// <see cref="ProcessJobManager"/>, and sends two of its own.
/// </summary>
/// <remarks>
/// <para>
/// E40 is the first of the four material-flow standards in this stack with a message set of its
/// own. E87 borrowed S3F17, E90 and E94 had nothing but stream 14 - here there is a whole stream,
/// and twelve of its fourteen transactions start at the host.
/// </para>
/// <para>
/// This class is a translator and nothing else: every refusal it sends comes from the manager,
/// which has no idea a wire exists. That is what lets the whole of E40's sequencing be tested
/// without one, and it is why the acknowledgement codes are decided here - the manager says what
/// happened and in which state, and this class picks the code the message in question can carry.
/// </para>
/// <para>
/// <strong>What it does not do by itself.</strong> A state change does not send an S16F11 unless
/// something asks it to. The manager raises its event inside its own lock, so starting a send from
/// there would hold that lock across the first stretch of a transaction; the wiring belongs to the
/// application, which knows whether it wants every transition on the wire. The README shows the
/// three lines.
/// </para>
/// </remarks>
public sealed class ProcessJobEquipment
{
    private readonly ProcessJobManager _manager;
    private readonly HsmsConnection _connection;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>Initialises the equipment side over a connection and a manager.</summary>
    /// <param name="manager">The state machines.</param>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="clock">Where the timestamp on an S16F11 comes from. <see langword="null"/> uses the system clock.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="manager"/> or <paramref name="connection"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The clock is a <see cref="TimeProvider"/> because S16F11 carries a <c>TIME</c>, and a test
    /// that asserted sixteen characters against a wall clock would be a test that fails at
    /// midnight.
    /// </remarks>
    public ProcessJobEquipment(
        ProcessJobManager manager,
        HsmsConnection connection,
        TimeProvider? clock = null,
        ILogger<ProcessJobEquipment>? logger = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _clock = clock ?? TimeProvider.System;
        _logger = logger ?? NullLogger<ProcessJobEquipment>.Instance;
    }

    /// <summary>Every primary this end answers: the twelve a host sends.</summary>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (16, 1),    // Process Job Create Request
        (16, 3),    // Process Job Command Send
        (16, 5),    // Process Job Status Request
        (16, 7),    // Process Job List Request
        (16, 9),    // Process Job Deselect Request
        (16, 15),   // Process Job Additional Material Send
        (16, 17),   // Process Job Parameter Change Send
        (16, 19),   // Process Job Queue Position Request
        (16, 21),   // Process Job Queue Reorder Send
        (16, 23),   // Process Job Multi-Command Send
        (16, 25),   // Process Job Material Binding Request
        (16, 27),   // Process Job Parameter Report Request
    ];

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 16 ? Build(transaction) : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{Stream}F{Function} reached the process job equipment, which does not answer it.",
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

    /// <summary>Tells a host a job has moved - S16F11.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="state">What state it is in now.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the host said - <c>PJACK</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// Takes the identifier and the state rather than a <see cref="ProcessJob"/>, because the job
    /// keeps moving: a report is what was true at a moment, and reading the state off a live object
    /// inside a send would put whatever it had become on the wire instead.
    /// </remarks>
    public async ValueTask<ProcessJobAcknowledgement> ReportStateAsync(
        string processJobId,
        ProcessJobState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        using var primary = new SecsMessage(16, 11, replyExpected: true)
        {
            Name = "ProcessJobEventReportSend",
            Item = ProcessJobCodec.EventReportBody(processJobId, state, _clock.GetUtcNow()),
        };

        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadAcknowledge(reply.Item)
            ?? throw new SecsException("S16F12 carried no PJACK.");
    }

    /// <summary>Tells a host what the whole queue looks like - S16F29.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the host said - <c>PJACK</c>.</returns>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// Every job the machine has, not only the queued ones: a host that has just connected wants
    /// the one that is processing most of all, and E40 gives it no other way to ask for all of them
    /// unprompted.
    /// </remarks>
    public async ValueTask<ProcessJobAcknowledgement> ReportQueueAsync(
        CancellationToken cancellationToken = default)
    {
        using var primary = new SecsMessage(16, 29, replyExpected: true)
        {
            Name = "ProcessJobQueueStatusReportSend",
            Item = ProcessJobCodec.QueueReportBody([.. _manager.Jobs]),
        };

        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadAcknowledge(reply.Item)
            ?? throw new SecsException("S16F30 carried no PJACK.");
    }

    // --------------------------------------------------------------- the replies

    private SecsMessage? Build(SecsTransaction transaction) => transaction.F switch
    {
        1 => Created(transaction.Message.Item),
        3 => Commanded(transaction.Message.Item),
        5 => Status(transaction.Message.Item),
        7 => Listed(),
        9 => Deselected(transaction.Message.Item),
        15 => MaterialAdded(transaction.Message.Item),
        17 => ParametersChanged(transaction.Message.Item),
        19 => QueuePosition(transaction.Message.Item),
        21 => Reordered(transaction.Message.Item),
        23 => MultiCommanded(transaction.Message.Item),
        25 => MaterialBound(transaction.Message.Item),
        27 => ParametersReported(transaction.Message.Item),
        _ => null,
    };

    private SecsMessage Created(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadCreate(body, out var processJobId, out var recipeId, out var material))
        {
            return Refuse(2, "S16F1", string.Empty);
        }

        var result = _manager.Create(processJobId, recipeId, material);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("S16F1 refused: {Reason}", result.Reason);
        }

        return Reply(2, "ProcessJobCreateAcknowledge", ProcessJobCodec.AcknowledgeBody(processJobId, result.Acknowledgement));
    }

    private SecsMessage Commanded(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadCommand(body, out var processJobId, out var command))
        {
            return Reply(
                4,
                "ProcessJobCommandAcknowledge",
                ProcessJobCodec.CommandResultBody(string.Empty, ProcessJobCommandAcknowledgement.CannotPerformNow));
        }

        var result = _manager.Command(processJobId, command);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("S16F3 refused: {Reason}", result.Reason);
        }

        return Reply(
            4,
            "ProcessJobCommandAcknowledge",
            ProcessJobCodec.CommandResultBody(
                processJobId,
                result.IsAccepted
                    ? ProcessJobCommandAcknowledgement.Performed
                    : result.CommandAcknowledgement));
    }

    private SecsMessage Status(SecsItem? body)
    {
        var processJobId = ProcessJobCodec.TryReadJobId(body);
        var job = processJobId is null ? null : _manager.Find(processJobId);

        // A job this machine does not have answers with a zero-length list rather than with a
        // state: S16F6 has no acknowledgement code in it, so an empty body is the only way the
        // reply can say "no such job" - the same shape S1F4 and S3F2 use.
        return job is null
            ? Reply(6, "ProcessJobStatusData", SecsItem.L())
            : Reply(6, "ProcessJobStatusData", ProcessJobCodec.StatusBody(job));
    }

    private SecsMessage Listed()
        => Reply(8, "ProcessJobListData", ProcessJobCodec.ListBody([.. _manager.Jobs]));

    private SecsMessage Deselected(SecsItem? body)
    {
        var processJobId = ProcessJobCodec.TryReadJobId(body);

        if (processJobId is null)
        {
            return Refuse(10, "S16F9", string.Empty);
        }

        var result = _manager.Deselect(processJobId);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("S16F9 refused: {Reason}", result.Reason);
        }

        return Reply(10, "ProcessJobDeselectAcknowledge", ProcessJobCodec.AcknowledgeBody(processJobId, result.Acknowledgement));
    }

    private SecsMessage MaterialAdded(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadMaterial(body, out var processJobId, out var material))
        {
            return Refuse(16, "S16F15", processJobId);
        }

        var result = _manager.AddMaterial(processJobId, material);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("S16F15 refused: {Reason}", result.Reason);
        }

        return Reply(
            16,
            "ProcessJobAdditionalMaterialAcknowledge",
            ProcessJobCodec.AcknowledgeBody(processJobId, result.Acknowledgement));
    }

    private SecsMessage MaterialBound(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadMaterial(body, out var processJobId, out var material))
        {
            return Refuse(26, "S16F25", processJobId);
        }

        var result = _manager.BindMaterial(processJobId, material);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("S16F25 refused: {Reason}", result.Reason);
        }

        return Reply(
            26,
            "ProcessJobMaterialBindingAcknowledge",
            ProcessJobCodec.AcknowledgeBody(processJobId, result.Acknowledgement));
    }

    private SecsMessage ParametersChanged(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadParameterChange(body, out var processJobId, out var parameters))
        {
            return Reply(
                18,
                "ProcessJobParameterChangeAcknowledge",
                ProcessJobCodec.ParameterResultBody(processJobId, []));
        }

        List<ProcessJobParameterResult> results = [];

        // One CPACK per parameter, which is what S16F18 carries: a change naming four parameters
        // where one is misspelt tells a host which one, and leaves the other three changed. That is
        // the opposite of the all-or-nothing rule the stream 14 providers follow, and it is the
        // message's own doing - a reply with one code per name is a reply that expects partial
        // success.
        foreach (var parameter in parameters)
        {
            var result = _manager.ChangeParameter(processJobId, parameter.Name, parameter.Value);

            results.Add(new ProcessJobParameterResult
            {
                Name = parameter.Name,
                Acknowledgement = result.IsAccepted
                    ? ProcessJobParameterAcknowledgement.Accepted
                    : result.ParameterAcknowledgement,
            });
        }

        return Reply(
            18,
            "ProcessJobParameterChangeAcknowledge",
            ProcessJobCodec.ParameterResultBody(processJobId, results));
    }

    private SecsMessage ParametersReported(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadParameterRequest(body, out var processJobId, out var names))
        {
            return Reply(
                28,
                "ProcessJobParameterReportData",
                ProcessJobCodec.ParameterReportBody(processJobId, []));
        }

        List<ProcessJobParameter> parameters = [];

        if (_manager.Find(processJobId) is { } job)
        {
            // An empty name list asks for all of them, the same reading stream 14 takes of an
            // empty ATTRID list. A name the job does not have is left out rather than answered
            // with an empty value, because S16F28 has no code to say which it was.
            foreach (var name in names.Count == 0 ? [.. job.Parameters.Keys] : names)
            {
                if (job.Parameters.TryGetValue(name, out var value))
                {
                    parameters.Add(new ProcessJobParameter { Name = name, Value = value });
                }
            }
        }

        return Reply(
            28,
            "ProcessJobParameterReportData",
            ProcessJobCodec.ParameterReportBody(processJobId, parameters));
    }

    private SecsMessage QueuePosition(SecsItem? body)
    {
        var processJobId = ProcessJobCodec.TryReadJobId(body);
        var job = processJobId is null ? null : _manager.Find(processJobId);

        return job is null
            ? Reply(20, "ProcessJobQueuePositionData", SecsItem.L())
            : Reply(
                20,
                "ProcessJobQueuePositionData",
                ProcessJobCodec.QueuePositionBody(job.ProcessJobId, _manager.QueuePosition(job.ProcessJobId), job.State));
    }

    private SecsMessage Reordered(SecsItem? body)
    {
        var processJobIds = ProcessJobCodec.ReadReorder(body);
        var result = _manager.Reorder(processJobIds);

        if (!result.IsAccepted)
        {
            _logger.LogInformation("S16F21 refused: {Reason}", result.Reason);
        }

        // HCACK, which is defined and says more than a yes or a no: a reorder naming a job that is
        // not in the queue is "no such object exists", and one of the wrong length or with a name
        // twice is "at least one parameter is invalid". Neither is "cannot perform now", and a
        // host that is told which has something to fix. The manager picks the code, because a code
        // chosen by looking for words in its reason would change when somebody rewords it.
        var acknowledgement = result.IsAccepted
            ? ProcessJobQueueAcknowledgement.Acknowledged
            : result.QueueAcknowledgement;

        return Reply(
            22,
            "ProcessJobQueueReorderAcknowledge",
            ProcessJobCodec.HostCommandAcknowledgeBody((byte)acknowledgement));
    }

    private SecsMessage MultiCommanded(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadMultiCommand(body, out var processJobIds, out var command))
        {
            return Reply(24, "ProcessJobMultiCommandAcknowledge", ProcessJobCodec.MultiCommandResultBody([]));
        }

        List<ProcessJobCommandResult> results = [];

        // One CMDA per job, so a command that half applies says which half. Nothing is rolled
        // back: a job that started is started, and a reply that pretended otherwise would be
        // worse than the partial success it is hiding.
        foreach (var processJobId in processJobIds)
        {
            var result = _manager.Command(processJobId, command);

            results.Add(new ProcessJobCommandResult
            {
                ProcessJobId = processJobId,
                Acknowledgement = result.IsAccepted
                    ? ProcessJobCommandAcknowledgement.Performed
                    : result.CommandAcknowledgement,
            });
        }

        return Reply(
            24,
            "ProcessJobMultiCommandAcknowledge",
            ProcessJobCodec.MultiCommandResultBody(results));
    }

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(16, function, replyExpected: false) { Name = name, Item = body };

    private SecsMessage Refuse(byte function, string primary, string processJobId)
    {
        _logger.LogWarning("{Primary} arrived with a body this end could not read; answering PJACK 1.", primary);

        return Reply(
            function,
            $"ProcessJobAcknowledge{function}",
            ProcessJobCodec.AcknowledgeBody(processJobId, ProcessJobAcknowledgement.Rejected));
    }
}
