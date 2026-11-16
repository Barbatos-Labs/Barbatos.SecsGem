// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>
/// The host side of E40: twelve questions and commands out, two reports in.
/// </summary>
/// <remarks>
/// <para>
/// A host has no process jobs of its own, so this class holds no state - it is the twelve primaries
/// a host sends, each returning the acknowledgement code its reply actually carries rather than a
/// boolean, and the two the equipment sends as events with a settable code. A host that subscribes
/// to neither still answers correctly.
/// </para>
/// <para>
/// Why the returns differ: a create answers with <c>PJACK</c>, which can only accept or reject; a
/// command answers with <c>CMDA</c>, which can also say the command does not exist; a parameter
/// change answers with one <c>CPACK</c> per parameter; a reorder answers with <c>HCACK</c>, which
/// says which part was wrong. Collapsing all four into one type would throw away the only thing
/// E40 has that E94 does not.
/// </para>
/// </remarks>
public sealed class ProcessJobHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host side over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public ProcessJobHost(HsmsConnection connection, ILogger<ProcessJobHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<ProcessJobHost>.Instance;
    }

    /// <summary>Every primary this end answers: the two the equipment sends unprompted.</summary>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (16, 11),   // Process Job Event Report Send
        (16, 29),   // Process Job Queue Status Report Send
    ];

    /// <summary>Raised when a job changes state - S16F11.</summary>
    /// <remarks>
    /// E40's own state-change report, which is the one thing it has that E90 and E94 do not: no
    /// collection event, no <c>CEID</c>, and nothing for an application to define. The report is
    /// valid for the life of the handler.
    /// </remarks>
    public event EventHandler<ProcessJobStateReportEventArgs>? JobStateReported;

    /// <summary>Raised when the equipment reports its whole queue - S16F29.</summary>
    public event EventHandler<ProcessJobQueueReportEventArgs>? QueueReported;

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
                "S{Stream}F{Function} reached the process job host, which does not answer it.",
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

    /// <summary>Asks for a process job - S16F1, answered by S16F2.</summary>
    /// <param name="processJobId">What to call it.</param>
    /// <param name="recipeId">The recipe it runs.</param>
    /// <param name="material">The material it is for.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine took it - <c>PJACK</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// Three fields, because S16F1 carries three. Everything else a job has is an attribute: set
    /// those over stream 14 - <c>PRRECIPEMETHOD</c>, <c>PRPROCESSSTART</c>, <c>PRMTRLORDER</c>,
    /// <c>PRPAUSEEVENTID</c> - while the job is still queued, which is what
    /// <see cref="ProcessJobObjectProvider"/> is for.
    /// </remarks>
    public async ValueTask<ProcessJobAcknowledgement> CreateAsync(
        string processJobId,
        string recipeId,
        IReadOnlyList<string>? material = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(recipeId);

        using var reply = await RequestAsync(
            new SecsMessage(16, 1, replyExpected: true)
            {
                Name = "ProcessJobCreateRequest",
                Item = ProcessJobCodec.CreateBody(processJobId, recipeId, material ?? []),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadAcknowledge(reply.Item)
            ?? throw new SecsException("S16F2 carried no PJACK.");
    }

    /// <summary>Tells a job to do something - S16F3, answered by S16F4.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="command">What to do. See <see cref="ProcessJobCommands"/>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What happened - <c>CMDA</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<ProcessJobCommandAcknowledgement> CommandAsync(
        string processJobId,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(command);

        using var reply = await RequestAsync(
            new SecsMessage(16, 3, replyExpected: true)
            {
                Name = "ProcessJobCommandSend",
                Item = ProcessJobCodec.CommandBody(processJobId, command),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadCommandResult(reply.Item)
            ?? throw new SecsException("S16F4 carried no CMDA.");
    }

    /// <summary>Tells several jobs to do the same thing - S16F23, answered by S16F24.</summary>
    /// <param name="processJobIds">Which jobs.</param>
    /// <param name="command">What to do.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One result per job, in the order the machine answered.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply could not be read.</exception>
    /// <remarks>
    /// Partial success is the normal outcome and not an error: stopping four jobs where one has
    /// already finished gives three <c>Performed</c> and one <c>CannotPerformNow</c>, and nothing
    /// is rolled back.
    /// </remarks>
    public async ValueTask<IReadOnlyList<ProcessJobCommandResult>> CommandAsync(
        IReadOnlyList<string> processJobIds,
        string command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobIds);
        ArgumentNullException.ThrowIfNull(command);

        using var reply = await RequestAsync(
            new SecsMessage(16, 23, replyExpected: true)
            {
                Name = "ProcessJobMultiCommandSend",
                Item = ProcessJobCodec.MultiCommandBody(processJobIds, command),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadMultiCommandResult(reply.Item, out var results)
            ? results
            : throw new SecsException("S16F24 could not be read.");
    }

    /// <summary>Asks after one job - S16F5, answered by S16F6.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its status, or <see langword="null"/> when the machine has no such job.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <see langword="null"/> rather than an exception: S16F6 has no acknowledgement code, so an
    /// empty body is how the reply says there is no such job, and that is an answer rather than a
    /// failure.
    /// </remarks>
    public async ValueTask<ProcessJobStatus?> GetStatusAsync(
        string processJobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        using var reply = await RequestAsync(
            new SecsMessage(16, 5, replyExpected: true)
            {
                Name = "ProcessJobStatusRequest",
                Item = ProcessJobCodec.JobIdBody(processJobId),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadStatus(reply.Item);
    }

    /// <summary>Asks for every job the machine has - S16F7, answered by S16F8.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One status per job. Empty for a machine with none.</returns>
    /// <exception cref="SecsException">The reply could not be read.</exception>
    public async ValueTask<IReadOnlyList<ProcessJobStatus>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        using var reply = await RequestAsync(
            new SecsMessage(16, 7, replyExpected: true) { Name = "ProcessJobListRequest" },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadList(reply.Item, out var jobs)
            ? jobs
            : throw new SecsException("S16F8 could not be read.");
    }

    /// <summary>Asks for a job to be taken off the machine - S16F9, answered by S16F10.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it is gone - <c>PJACK</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<ProcessJobAcknowledgement> DeselectAsync(
        string processJobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        using var reply = await RequestAsync(
            new SecsMessage(16, 9, replyExpected: true)
            {
                Name = "ProcessJobDeselectRequest",
                Item = ProcessJobCodec.JobIdBody(processJobId),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadAcknowledge(reply.Item)
            ?? throw new SecsException("S16F10 carried no PJACK.");
    }

    /// <summary>Adds material to a job - S16F15, answered by S16F16.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="material">What to add.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was added - <c>PJACK</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<ProcessJobAcknowledgement> AddMaterialAsync(
        string processJobId,
        IReadOnlyList<string> material,
        CancellationToken cancellationToken = default)
        => MaterialAsync(15, 16, "ProcessJobAdditionalMaterialSend", processJobId, material, cancellationToken);

    /// <summary>Binds material to a job - S16F25, answered by S16F26.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="material">What it is for. Replaces whatever was there.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was bound - <c>PJACK</c>.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public ValueTask<ProcessJobAcknowledgement> BindMaterialAsync(
        string processJobId,
        IReadOnlyList<string> material,
        CancellationToken cancellationToken = default)
        => MaterialAsync(25, 26, "ProcessJobMaterialBindingRequest", processJobId, material, cancellationToken);

    /// <summary>Changes a job's recipe parameters - S16F17, answered by S16F18.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="parameters">What to change.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>One <c>CPACK</c> per parameter.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply could not be read.</exception>
    /// <remarks>
    /// Partial success again, and here it is the message's own doing: S16F18 carries one code per
    /// name, so a change naming four parameters where one is misspelt tells a host which one and
    /// leaves the other three changed.
    /// </remarks>
    public async ValueTask<IReadOnlyList<ProcessJobParameterResult>> ChangeParametersAsync(
        string processJobId,
        IReadOnlyList<ProcessJobParameter> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(parameters);

        using var reply = await RequestAsync(
            new SecsMessage(16, 17, replyExpected: true)
            {
                Name = "ProcessJobParameterChangeSend",
                Item = ProcessJobCodec.ParameterChangeBody(processJobId, parameters),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadParameterResult(reply.Item, out var results)
            ? results
            : throw new SecsException("S16F18 could not be read.");
    }

    /// <summary>Reads a job's recipe parameters - S16F27, answered by S16F28.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="names">Which parameters. Empty asks for all of them.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The parameters the job has. A name it does not have is absent from the answer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply could not be read.</exception>
    public async ValueTask<IReadOnlyList<ProcessJobParameter>> GetParametersAsync(
        string processJobId,
        IReadOnlyList<string>? names = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        using var reply = await RequestAsync(
            new SecsMessage(16, 27, replyExpected: true)
            {
                Name = "ProcessJobParameterReportRequest",
                Item = ProcessJobCodec.ParameterRequestBody(processJobId, names ?? []),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadParameterChange(reply.Item, out _, out var parameters)
            ? parameters
            : throw new SecsException("S16F28 could not be read.");
    }

    /// <summary>Asks where a job is in the queue - S16F19, answered by S16F20.</summary>
    /// <param name="processJobId">Which job.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its place, or <see langword="null"/> when the machine has no such job.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobId"/> is <see langword="null"/>.</exception>
    public async ValueTask<ProcessJobQueuePosition?> GetQueuePositionAsync(
        string processJobId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobId);

        using var reply = await RequestAsync(
            new SecsMessage(16, 19, replyExpected: true)
            {
                Name = "ProcessJobQueuePositionRequest",
                Item = ProcessJobCodec.JobIdBody(processJobId),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadQueuePosition(reply.Item);
    }

    /// <summary>Puts the queue in a different order - S16F21, answered by S16F22.</summary>
    /// <param name="processJobIds">The queued jobs, in the order they should be taken up.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>What the machine said - <c>HCACK</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="processJobIds"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<ProcessJobQueueAcknowledgement> ReorderAsync(
        IReadOnlyList<string> processJobIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(processJobIds);

        using var reply = await RequestAsync(
            new SecsMessage(16, 21, replyExpected: true)
            {
                Name = "ProcessJobQueueReorderSend",
                Item = ProcessJobCodec.ReorderBody(processJobIds),
            },
            cancellationToken).ConfigureAwait(false);

        var code = ProcessJobCodec.TryReadHostCommandAcknowledge(reply.Item)
            ?? throw new SecsException("S16F22 carried no HCACK.");

        if (code <= (byte)ProcessJobQueueAcknowledgement.NoSuchObject)
        {
            return (ProcessJobQueueAcknowledgement)code;
        }

        _logger.LogInformation(
            "S16F22 answered HCACK {Code}, which E5 reserves and does not name; reading it as a refusal.",
            code);

        return ProcessJobQueueAcknowledgement.CannotPerformNow;
    }

    // --------------------------------------------------------------- the replies

    private SecsMessage? Build(SecsTransaction transaction) => transaction.F switch
    {
        11 => StateReported(transaction.Message.Item),
        29 => QueueReportArrived(transaction.Message.Item),
        _ => null,
    };

    private SecsMessage StateReported(SecsItem? body)
    {
        var report = ProcessJobCodec.TryReadEventReport(body);

        if (report is null)
        {
            _logger.LogWarning("S16F11 arrived with a body this end could not read; answering PJACK 1.");

            return Reply(12, ProcessJobAcknowledgement.Rejected);
        }

        var arguments = new ProcessJobStateReportEventArgs { Report = report };

        JobStateReported?.Invoke(this, arguments);

        return Reply(12, arguments.Acknowledgement);
    }

    private SecsMessage QueueReportArrived(SecsItem? body)
    {
        if (!ProcessJobCodec.TryReadQueueReport(body, out var jobs))
        {
            _logger.LogWarning("S16F29 arrived with a body this end could not read; answering PJACK 1.");

            return Reply(30, ProcessJobAcknowledgement.Rejected);
        }

        var arguments = new ProcessJobQueueReportEventArgs { Jobs = jobs };

        QueueReported?.Invoke(this, arguments);

        return Reply(30, arguments.Acknowledgement);
    }

    private async ValueTask<ProcessJobAcknowledgement> MaterialAsync(
        byte function,
        byte replyFunction,
        string name,
        string processJobId,
        IReadOnlyList<string> material,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processJobId);
        ArgumentNullException.ThrowIfNull(material);

        using var reply = await RequestAsync(
            new SecsMessage(16, function, replyExpected: true)
            {
                Name = name,
                Item = ProcessJobCodec.MaterialBody(processJobId, material),
            },
            cancellationToken).ConfigureAwait(false);

        return ProcessJobCodec.TryReadAcknowledge(reply.Item)
            ?? throw new SecsException($"S16F{replyFunction} carried no PJACK.");
    }

    private async ValueTask<SecsMessage> RequestAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SecsMessage Reply(byte function, ProcessJobAcknowledgement acknowledgement)
        => new(16, function, replyExpected: false)
        {
            Name = $"ProcessJobAcknowledge{function}",
            Item = ProcessJobCodec.AcknowledgeOnlyBody(acknowledgement),
        };
}
