// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Material;

/// <summary>
/// The job half of E5's stream 4 - S4F19 to S4F26, which schedule the handoffs
/// <see cref="MaterialHandoff"/> carries out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This half was refused three times and the reason was wrong three times.</strong> The
/// first said a transfer job has no state model anywhere in the reference, where <c>TRJOBMS</c>
/// names six states; the second said only S4F19 can mint a <c>TRLINK</c>, where S4F27 carries one
/// and travels either way; the third said <c>TRACK</c>. All three were argued from the message rows
/// while the answers were in the item dictionary, and the third is the subtlest: <c>TRACK</c> is
/// defined - an <c>A[n]</c> - it simply carries no values. **Untabled is not undefined.** Every one
/// of the twenty item names in these four pairs is in the dictionary, which makes this the only
/// capability in this repository whose rows were clean from the start.
/// </para>
/// <para>
/// <strong>Two declaration lists, unlike <see cref="MaterialHandoff"/>.</strong> That type's
/// seventeen primaries all travel either way, which is why one list serves both ends. These four do
/// not: the host creates a job, commands it and asks to send a long message, and the equipment
/// alerts. So <see cref="EquipmentMessages"/> is what a machine answers and
/// <see cref="HostMessages"/> is what a host answers, and a machine declaring the host's list would
/// be claiming to answer its own alerts.
/// </para>
/// <para>
/// <strong>The verdict is <c>ERRCODE</c>, not <c>TRACK</c></strong> - see
/// <see cref="MaterialTransferJobResult"/>, which is where that reading is argued.
/// </para>
/// </remarks>
public sealed class MaterialTransferJobs
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises one end of the job protocol over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public MaterialTransferJobs(HsmsConnection connection, ILogger<MaterialTransferJobs>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<MaterialTransferJobs>.Instance;
    }

    /// <summary>What a piece of equipment answers: the three primaries a host sends.</summary>
    /// <remarks>
    /// S4F25 is the multi-block inquire, and it is here rather than left to stream 2 because this
    /// package does not depend on <c>Barbatos.SecsGem.Gem</c>: a machine doing material handoff and
    /// nothing else has no S2F39 to fall back on. Where both packages are present a host has two
    /// numbers for one handshake, which is what the reference offers and neither row is wrong.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> EquipmentMessages { get; } =
    [
        (4, 19),   // Transfer Job Create
        (4, 21),   // Transfer Job Command
        (4, 25),   // Multi-block Inquire
    ];

    /// <summary>What a host answers: the one primary the equipment sends.</summary>
    /// <remarks>
    /// S4F24 carries no body at all - the alert is acknowledged and nothing is said back - so the
    /// host end has an event and no answer delegate.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HostMessages { get; } =
    [
        (4, 23),   // Transfer Command Alert
    ];

    /// <summary>Raised when the equipment reports a job's state, or a fault on one - S4F23.</summary>
    public event EventHandler<MaterialTransferJobAlertEventArgs>? JobAlerted;

    /// <summary>Gets or sets what this machine answers a job creation with - S4F20.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Refuses by default</strong>, and the identifier it returns is the job's: S4F20 mints
    /// the <c>TRJOBID</c> that every later message addresses, so a machine that accepted without
    /// minting one would leave a host with a job it could not command.
    /// </para>
    /// <para>
    /// The default returns an empty identifier and <c>ERRCODE</c> 1, because a machine that has not
    /// been told how to schedule a transfer cannot schedule one - the same way round as
    /// <c>GemEquipment.AnswerInitiateProcessing</c>.
    /// </para>
    /// </remarks>
    public Func<MaterialTransferJobRequest, (string JobId, MaterialTransferJobResult Result)> AnswerCreate { get; set; }
        = static _ => (string.Empty, new MaterialTransferJobResult(string.Empty, 1, "this equipment schedules no transfer jobs"));

    /// <summary>Gets or sets what this machine answers a job command with - S4F22.</summary>
    /// <remarks>
    /// Refuses by default, for <see cref="AnswerCreate"/>'s reason: a command on a job this machine
    /// never created cannot be carried out, and a machine with no handler has created none.
    /// </remarks>
    public Func<string, string, IReadOnlyList<(string Name, string Value)>, MaterialTransferJobResult> AnswerCommand { get; set; }
        = static (_, _, _) => new MaterialTransferJobResult(string.Empty, 1, "this equipment takes no transfer job commands");

    /// <summary>Gets or sets what this machine answers a multi-block inquire with - S4F26.</summary>
    /// <remarks>
    /// <strong>Grants by default</strong>, the opposite way round from the two above and for the
    /// reason every other inquire in this repository grants: a handshake whose only purpose is to
    /// let this end refuse should not refuse by silence.
    /// </remarks>
    public Func<uint, uint, MaterialGrant> AnswerMultiBlockInquiry { get; set; }
        = static (_, _) => MaterialGrant.Accepted;

    /// <summary>Asks the other end to create a transfer job - S4F19.</summary>
    /// <param name="request">The job and the link it is for.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The <c>TRJOBID</c> the machine minted, and its verdict.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S4F20 was not the four items E5 defines for it.</exception>
    /// <remarks>
    /// The identifier comes back because this row is where it is minted; everything afterwards
    /// addresses the job by it. A refused creation returns whatever the machine put there, which
    /// may be empty.
    /// </remarks>
    public async ValueTask<(string JobId, MaterialTransferJobResult Result)> CreateAsync(
        MaterialTransferJobRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var primary = new SecsMessage(4, 19, replyExpected: true)
        {
            Name = "TransferJobCreate",
            Item = MaterialCodec.TransferJobBody(request),
        };

        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        if (reply.Item is not { IsList: true, Count: 4 } body)
        {
            throw new SecsException(
                $"S{reply.S}F{reply.F} did not carry a TRJOBID, a TRACK, an ERRCODE and an ERRTEXT.");
        }

        return (MaterialCodec.TextOf(body[0]), MaterialCodec.ReadTransferJobResult(body, 1));
    }

    /// <summary>Commands a transfer job - S4F21.</summary>
    /// <param name="jobId">TRJOBID, as S4F20 minted it.</param>
    /// <param name="command">TRCMDNAME.</param>
    /// <param name="parameters">CPNAME and CPVAL pairs, which may be empty.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The machine's verdict.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">S4F22 was not the three items E5 defines for it.</exception>
    /// <remarks>
    /// <strong>The command names are the application's.</strong> <c>TRCMDNAME</c> is an
    /// <c>A[n]</c> with no table, which is the same position <c>HOCMDNAME</c> is in at S4F29 - so
    /// this package carries the string and takes no view, exactly as the handoff half does.
    /// </remarks>
    public async ValueTask<MaterialTransferJobResult> SendCommandAsync(
        string jobId,
        string command,
        IReadOnlyList<(string Name, string Value)> parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(parameters);

        using var primary = new SecsMessage(4, 21, replyExpected: true)
        {
            Name = "TransferJobCommand",
            Item = MaterialCodec.TransferJobCommandBody(jobId, command, parameters),
        };

        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        if (reply.Item is not { IsList: true, Count: 3 } body)
        {
            throw new SecsException(
                $"S{reply.S}F{reply.F} did not carry a TRACK, an ERRCODE and an ERRTEXT.");
        }

        return MaterialCodec.ReadTransferJobResult(body, 0);
    }

    /// <summary>Asks permission to send a message too big for one block - S4F25.</summary>
    /// <param name="dataId">DATAID.</param>
    /// <param name="length">DATALENGTH, in whatever the two ends have agreed it counts.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>GRANT.</returns>
    /// <exception cref="SecsException">S4F26 carried no GRANT.</exception>
    /// <remarks>
    /// The same two items S2F39 and S6F5 carry, and the same <c>GRANT</c> table S3F16 answers with
    /// - so this is <see cref="MaterialGrant"/> again rather than an enum of its own, which keeps
    /// one item meaning one thing inside this package.
    /// </remarks>
    public async ValueTask<MaterialGrant> InquireMultiBlockAsync(
        uint dataId,
        uint length,
        CancellationToken cancellationToken = default)
    {
        using var primary = new SecsMessage(4, 25, replyExpected: true)
        {
            Name = "MultiBlockInquire",
            Item = SecsItem.L(
                SecsItem.U4(dataId).Named("DATAID"),
                SecsItem.U4(length).Named("DATALENGTH")),
        };

        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        return (MaterialGrant)(MaterialCodec.ReadCode(reply.Item) ?? (byte)MaterialGrant.DuplicateOrInvalid);
    }

    /// <summary>Tells the host a job has changed state, or gone wrong - S4F23.</summary>
    /// <param name="jobId">TRJOBID.</param>
    /// <param name="state">TRJOBMS.</param>
    /// <param name="jobName">TRJOBNAME, or empty.</param>
    /// <param name="errorCode">ERRCODE. Nought for a plain state change.</param>
    /// <param name="errorText">ERRTEXT, or empty.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the host has acknowledged.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="jobId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Answered by an S4F24 with no body, so there is nothing to hand back: the host either
    /// acknowledged or the send threw.
    /// </remarks>
    public async ValueTask AlertAsync(
        string jobId,
        MaterialTransferJobState state,
        string jobName = "",
        uint errorCode = 0,
        string errorText = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        using var primary = new SecsMessage(4, 23, replyExpected: true)
        {
            Name = "TransferCommandAlert",
            Item = MaterialCodec.TransferJobAlertBody(jobId, jobName, state, errorCode, errorText),
        };

        using var reply = await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);

        _ = reply;
    }

    /// <summary>Answers one of this capability's primaries.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes when the reply has been sent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        using var reply = transaction.F switch
        {
            19 => OnCreate(transaction.Message),
            21 => OnCommand(transaction.Message),
            23 => OnAlert(transaction.Message),
            _ => OnMultiBlockInquire(transaction.Message),
        };

        await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private SecsMessage OnCreate(SecsMessage request)
    {
        if (!MaterialCodec.TryReadTransferJob(request.Item, out var job))
        {
            _logger.LogWarning("S4F19 arrived with a body this end could not read as a transfer job.");

            return new SecsMessage(4, 20, replyExpected: false)
            {
                Name = "TransferJobAcknowledge",
                Item = MaterialCodec.TransferJobResultBody(
                    string.Empty,
                    new MaterialTransferJobResult(string.Empty, 1, "the request could not be read")),
            };
        }

        var (jobId, result) = AnswerCreate(job!);

        return new SecsMessage(4, 20, replyExpected: false)
        {
            Name = "TransferJobAcknowledge",
            Item = MaterialCodec.TransferJobResultBody(jobId, result),
        };
    }

    private SecsMessage OnCommand(SecsMessage request)
    {
        var result = new MaterialTransferJobResult(string.Empty, 1, "the command could not be read");

        if (MaterialCodec.TryReadTransferJobCommand(request.Item, out var jobId, out var command, out var parameters))
        {
            result = AnswerCommand(jobId, command, parameters);
        }
        else
        {
            _logger.LogWarning("S4F21 arrived with a body this end could not read as a job command.");
        }

        return new SecsMessage(4, 22, replyExpected: false)
        {
            Name = "TransferJobCommandAcknowledge",
            Item = MaterialCodec.TransferJobCommandResultBody(result),
        };
    }

    private SecsMessage OnAlert(SecsMessage request)
    {
        if (MaterialCodec.TryReadTransferJobAlert(request.Item, out var alert))
        {
            JobAlerted?.Invoke(this, alert!);
        }
        else
        {
            // Nothing raised, because there is nothing to hand anybody - and S4F24 has no body, so
            // the acknowledgement cannot say so either. The log is the only place it is visible.
            _logger.LogWarning(
                "S4F23 arrived with a body this end could not read as a job alert, so nothing was raised.");
        }

        // Acknowledged either way: S4F24 carries no items, so a host that could not read the alert
        // has no way to say so and no reason to leave the transaction open.
        return new SecsMessage(4, 24, replyExpected: false) { Name = "TransferAlertAcknowledge" };
    }

    private SecsMessage OnMultiBlockInquire(SecsMessage request)
    {
        var grant = MaterialGrant.DuplicateOrInvalid;

        if (request.Item is { IsList: true, Count: 2 } body)
        {
            grant = AnswerMultiBlockInquiry(MaterialCodec.UInt32Of(body[0]), MaterialCodec.UInt32Of(body[1]));
        }

        return new SecsMessage(4, 26, replyExpected: false)
        {
            Name = "MultiBlockGrant",
            Item = SecsItem.B((byte)grant).Named("GRANT"),
        };
    }
}
