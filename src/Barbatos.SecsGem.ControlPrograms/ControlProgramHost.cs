// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ControlPrograms;

/// <summary>
/// The host side of control program transfer: the end that drives every transaction.
/// </summary>
/// <remarks>
/// <para>
/// Six methods for the six primaries this package implements. Every message in stream 8 is a
/// primary the host sends or a reply to one, so <see cref="HandledMessages"/> is empty and
/// <c>AddControlProgramHost</c> adds no route - which <c>Barbatos.SecsGem.DataSets</c> also does,
/// those two being the one-way streams in this stack.
/// </para>
/// <para>
/// <strong>Sending is two transactions and this class does not run them for you.</strong> E5 says
/// inquire, then send; <see cref="InquireAsync"/> and <see cref="SendAsync"/> are separate because a
/// host that wants to check the grant before committing megabytes is the whole reason the inquire
/// exists, and a method that did both would throw the grant away. <see cref="SendProgramAsync"/> is
/// there for a caller that wants the two in one call and says so.
/// </para>
/// <para>
/// One instance sits on one <see cref="HsmsConnection"/>. It holds nothing that needs releasing and
/// is not disposable.
/// </para>
/// </remarks>
public sealed class ControlProgramHost
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises the host side over a connection.</summary>
    /// <param name="connection">The link.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public ControlProgramHost(HsmsConnection connection, ILogger<ControlProgramHost>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<ControlProgramHost>.Instance;
    }

    /// <summary>Every primary this end answers: none.</summary>
    /// <remarks>
    /// Empty, and that is the fact rather than an omission - every one of stream 8's eight
    /// primaries is marked host to equipment. There is no message that announces a control
    /// program, which is what <see cref="ListAsync"/> is for. It is still public so the claim can
    /// be asserted in a test.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } = [];

    /// <summary>Asks whether a control program may be sent - S8F1, answered by S8F2.</summary>
    /// <param name="length">How long it is - <c>LENGTH</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant.</returns>
    /// <exception cref="SecsException">The reply carried no grant code.</exception>
    /// <remarks>
    /// <strong>It names nothing.</strong> S8F1's whole body is a length, and so is this method's
    /// whole argument: the boot program is anonymous, because a machine has one of it. The named
    /// programs are the ones a host <em>requests</em>, never the ones it sends.
    /// </remarks>
    public async ValueTask<ControlProgramGrant> InquireAsync(
        uint length,
        CancellationToken cancellationToken = default)
    {
        using var reply = await ExchangeAsync(
            new SecsMessage(8, 1, replyExpected: true)
            {
                Name = "BootControlProgramLoadInquire",
                Item = ControlProgramCodec.InquireBody(length),
            },
            cancellationToken).ConfigureAwait(false);

        var code = ControlProgramCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S8F2 carried no GRANT.");

        if (code < (byte)ControlProgramGrant.Other)
        {
            return (ControlProgramGrant)code;
        }

        _logger.LogInformation(
            "S8F2 refused the load with GRANT {Code}, which the table reserves and does not name.",
            code);

        return ControlProgramGrant.Other;
    }

    /// <summary>Sends the control program - S8F3, answered by S8F4.</summary>
    /// <param name="body">The program - <c>CPBODY</c>. Plain bytes, not an encoded item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the equipment took it.</returns>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// Written as a <c>B</c>, because <c>CPBODY</c> sits where <c>PPBODY</c> sits in stream 7's
    /// design and <c>PPBODY</c> is a <c>B</c>. This is where a reader coming from
    /// <c>Barbatos.SecsGem.DataSets</c> has to be careful: there the bytes are an encoded item
    /// because that stream's payload has no format, and here they are the program itself.
    /// </remarks>
    public async ValueTask<ControlProgramAcknowledgement> SendAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        using var reply = await ExchangeAsync(
            new SecsMessage(8, 3, replyExpected: true)
            {
                Name = "BootControlProgramSend",
                Item = ControlProgramCodec.BodyItem(body),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S8F4");
    }

    /// <summary>Inquires and then sends, if the inquire was granted - S8F1 and S8F3.</summary>
    /// <param name="body">The program - <c>CPBODY</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant, and the acknowledgement when there was one to get.</returns>
    /// <exception cref="SecsException">Either reply carried no code.</exception>
    /// <remarks>
    /// The two-step E5 describes, in one call, for a caller who has the whole program in hand and
    /// nothing to decide between the steps. The length declared is <c>body.Length</c>, which is a
    /// number this package can know because <c>CPBODY</c> has a format - the thing stream 13 could
    /// not do for its own payload.
    /// </remarks>
    public async ValueTask<(ControlProgramGrant Grant, ControlProgramAcknowledgement? Acknowledgement)> SendProgramAsync(
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        var grant = await InquireAsync((uint)body.Length, cancellationToken).ConfigureAwait(false);

        return grant == ControlProgramGrant.Accepted
            ? (grant, await SendAsync(body, cancellationToken).ConfigureAwait(false))
            : (grant, null);
    }

    /// <summary>Asks for a named control program - S8F5, answered by S8F6.</summary>
    /// <param name="controlProgramId">Which program - <c>CPID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The program, or <see langword="null"/> when the equipment has none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlProgramId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a control program.</exception>
    /// <remarks>
    /// <see langword="null"/> is what a zero-length <c>CPBODY</c> means: S8F6 carries no
    /// acknowledgement code, so "I do not have it" and "here it is" arrive in the same shape and
    /// are told apart by the body being empty. <see cref="RevisionAsync"/> is the transaction that
    /// can say which, because S8F16's reply carries a <c>SOFTREV</c> that is either there or not.
    /// </remarks>
    public async ValueTask<ControlProgram?> RequestAsync(
        string controlProgramId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);

        using var reply = await ExchangeAsync(
            new SecsMessage(8, 5, replyExpected: true)
            {
                Name = "ControlProgramRequest",
                Item = ControlProgramCodec.IdBody(controlProgramId),
            },
            cancellationToken).ConfigureAwait(false);

        if (reply.Item is { IsList: true, Count: 0 })
        {
            // The shape this library's own equipment end sends for a request it could not read.
            _logger.LogWarning("S8F6 for '{ControlProgramId}' carried a zero-length body.", controlProgramId);
            return null;
        }

        if (!ControlProgramCodec.TryReadProgram(reply.Item, out var echoed, out var body))
        {
            throw new SecsException("S8F6 did not carry a control program.");
        }

        if (!string.Equals(echoed, controlProgramId, StringComparison.Ordinal))
        {
            // The only correlation stream 8 offers, and the same check Objects keeps on S2F64: a
            // machine that echoed something else has answered a different question, and acting on
            // it would flash one program under another's name.
            _logger.LogWarning(
                "S8F6 answered with '{Echoed}' for a request that asked for '{Asked}'.",
                echoed,
                controlProgramId);
        }

        return body.IsEmpty
            ? null
            : new ControlProgram { Id = echoed, Body = body };
    }

    /// <summary>Asks what control programs the equipment has - S8F11, answered by S8F12.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Their identifiers. Empty when the equipment has none to offer by name.</returns>
    /// <remarks>
    /// The only primary in stream 8 with no body. An unreadable reply comes back empty rather than
    /// throwing, with the log saying which happened.
    /// </remarks>
    public async ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var reply = await ExchangeAsync(
            new SecsMessage(8, 11, replyExpected: true) { Name = "ControlProgramDirectoryRequest" },
            cancellationToken).ConfigureAwait(false);

        var controlProgramIds = ControlProgramCodec.ReadDirectory(reply.Item);

        if (controlProgramIds.Count == 0 && reply.Item is { Count: > 0 })
        {
            _logger.LogWarning("S8F12 carried a body with no readable CPID in it.");
        }

        return controlProgramIds;
    }

    /// <summary>Aborts a transfer - S8F13, answered by S8F14.</summary>
    /// <param name="controlProgramId">Which program's transfer - <c>CPID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was aborted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlProgramId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>Aborting is not deleting: stream 8 has no message that removes a control program from a machine.</remarks>
    public async ValueTask<ControlProgramAcknowledgement> AbortAsync(
        string controlProgramId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);

        using var reply = await ExchangeAsync(
            new SecsMessage(8, 13, replyExpected: true)
            {
                Name = "ControlProgramTransferAbort",
                Item = ControlProgramCodec.IdBody(controlProgramId),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S8F14");
    }

    /// <summary>Asks what revision a control program is - S8F15, answered by S8F16.</summary>
    /// <param name="controlProgramId">Which program - <c>CPID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Its revision - <c>SOFTREV</c> - or <see langword="null"/> when the equipment has no such program.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="controlProgramId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply was not a control program status.</exception>
    /// <remarks>
    /// The one transaction stream 8 has that stream 7 does not, and the cheap way to find out
    /// whether a program is there before asking for all of it.
    /// </remarks>
    public async ValueTask<string?> RevisionAsync(
        string controlProgramId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(controlProgramId);

        using var reply = await ExchangeAsync(
            new SecsMessage(8, 15, replyExpected: true)
            {
                Name = "ControlProgramStatusRequest",
                Item = ControlProgramCodec.IdBody(controlProgramId),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ControlProgramCodec.TryReadRevision(reply.Item, out var echoed, out var revision))
        {
            throw new SecsException("S8F16 did not carry a control program status.");
        }

        if (!string.Equals(echoed, controlProgramId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "S8F16 reported on '{Echoed}' for a request about '{Asked}'.",
                echoed,
                controlProgramId);
        }

        return revision.Length == 0 ? null : revision;
    }

    // ---------------------------------------------------------------- the pieces

    private ControlProgramAcknowledgement ReadAcknowledgement(SecsMessage reply, string what)
    {
        var code = ControlProgramCodec.ReadCode(reply.Item)
            ?? throw new SecsException($"{what} carried no ACKC8.");

        if (code == (byte)ControlProgramAcknowledgement.Accepted)
        {
            return ControlProgramAcknowledgement.Accepted;
        }

        if (code > (byte)ControlProgramAcknowledgement.Denied)
        {
            // ACKC8 has no table at all, so there is nothing to call this but a refusal. The code
            // is logged because it is the only place it can go.
            _logger.LogInformation(
                "{What} carried ACKC8 {Code}. That item has no table of values, so anything but zero is a refusal with no reason.",
                what,
                code);
        }

        return ControlProgramAcknowledgement.Denied;
    }

    private async ValueTask<SecsMessage> ExchangeAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        // The primary is built by the caller and of no use afterwards, so it is disposed here. The
        // reply belongs to whoever reads it, which is this class in every case.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
