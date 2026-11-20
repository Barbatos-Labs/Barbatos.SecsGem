// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.Material;

/// <summary>
/// The interlocked material handoff - stream 4, S4F1 to S4F18 - for either end of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One type, not two, and the catalogue is why.</strong> Every other message pair in this
/// stack has a direction: the dictionary marks it host-to-equipment or equipment-to-host, and this
/// repository ends up with a <c>…Host</c> and an <c>…Equipment</c> that each answer the other's
/// primaries. All eleven rows of S4F1 to S4F18 are marked as travelling <em>either</em> way,
/// because the two parties to a handoff are a sender and a receiver of material - which may be a
/// machine and a handling system, or two machines, and need not involve a host at all. So this is
/// the first type here that is neither end of anything: it sends all nine primaries and answers
/// all nine, and both ends of a link register the same list.
/// </para>
/// <para>
/// <strong>On provenance, and for once it is good news.</strong> Stream 4 is the best-covered
/// stream this repository had not implemented: 27 rows, and <em>every item name in all of them is
/// defined in the dictionary</em> - which is true of no other stream here, including the ones
/// already shipped. <c>PTN</c> is a <c>U1</c>, <c>MID</c> an <c>A</c>, and <c>RSACK</c> and
/// <c>RRACK</c> are <c>B[1]</c> items that arrive with their tables of values, so the
/// acknowledgement enums in this package are quoted and not inferred. What the source does not
/// mark as a definition is any of the 27 rows, exactly as for stream 3 - so the bodies are read
/// off the item lists, the way the rest of this package's are.
/// </para>
/// <para>
/// <strong>The sequence is documented and not enforced.</strong> E5 names the steps - offer,
/// accept, send, complete - and this package will send them in any order it is asked to. That is
/// the same choice <c>MaterialEquipment</c> makes about stream 3 and the opposite of the one
/// <c>Barbatos.SecsGem.Carriers</c> makes about E87, and the difference is what there is to check
/// against: E87 defines state machines, and stream 4 is 27 message rows with no state model
/// anywhere in this reference. A package that invented one would be enforcing its own rules while
/// appearing to enforce E5's.
/// </para>
/// <para>
/// <strong>Seven of the nine primaries have no reply at all</strong> - E5 gives S4F3, F5, F7, F9,
/// F11, F13 and F15 no secondary - so they are posted rather than sent, and a caller that wants to
/// know the other end heard has to wait for the step that follows. Only the offer and the request
/// to receive are answered.
/// </para>
/// </remarks>
public sealed class MaterialHandoff
{
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;

    /// <summary>Initialises one end of a handoff over a connection.</summary>
    /// <param name="connection">The link. This object does not run it; it only uses it.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connection"/> is <see langword="null"/>.</exception>
    public MaterialHandoff(HsmsConnection connection, ILogger<MaterialHandoff>? logger = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<MaterialHandoff>.Instance;
    }

    /// <summary>Every primary this end answers, which is every primary it also sends.</summary>
    /// <remarks>
    /// Nine, and the same nine at both ends - the only list in this repository that is both what a
    /// type sends and what it receives. S4F19 to S4F41 are not here; see
    /// <see cref="MaterialHandoff"/> and the package README for the two reasons.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (4, 1),    // Ready to Send Material
        (4, 3),    // Send Material
        (4, 5),    // Handshake Complete
        (4, 7),    // Not Ready to Send
        (4, 9),    // Stuck in Sender
        (4, 11),   // Stuck in Receiver
        (4, 13),   // Send Incomplete Timeout
        (4, 15),   // Material Received
        (4, 17),   // Request to Receive
    ];

    /// <summary>Raised for every step of a handoff this end is told about.</summary>
    /// <remarks>
    /// <para>
    /// All nine, including the two that are answered, and it carries what arrived rather than what
    /// this end did about it: the event is raised <strong>before</strong>
    /// <see cref="AnswerOffer"/> or <see cref="AnswerRequest"/> is consulted, so a handler sees
    /// every step uniformly whether or not there is an answer to give. A test pins that order.
    /// </para>
    /// <para>
    /// A handler that wants the question and the answer together records them in the answer
    /// delegate, which has both in hand - that is the one place where both exist at once, and it
    /// is why these event arguments do not carry an acknowledgement that would be meaningless for
    /// seven of the nine steps.
    /// </para>
    /// </remarks>
    public event EventHandler<MaterialHandoffEventArgs>? HandoffReported;

    /// <summary>Gets or sets what this end answers an offer of material with - S4F2.</summary>
    /// <remarks>
    /// <para>
    /// A delegate rather than a method on <c>IMaterialSource</c>, deliberately: adding a member to
    /// that interface would break every application that implements it, and an offer of material
    /// is not a question about what material this machine is holding. It accepts by default, as
    /// <see cref="MaterialInMemorySource"/>'s answers do, so a link works before it is configured
    /// and a refusal is something an application chose.
    /// </para>
    /// <para>
    /// E5 gives four named reasons to refuse - no such port, port occupied, busy, not in a mode
    /// anybody may drive - and they are worth using: a bare refusal sends somebody to a log file.
    /// </para>
    /// </remarks>
    public Func<byte, string, MaterialSendAcknowledgement> AnswerOffer { get; set; }
        = static (_, _) => MaterialSendAcknowledgement.WillAccept;

    /// <summary>Gets or sets what this end answers a request to be sent material with - S4F18.</summary>
    /// <remarks>Granted by default, for the reason <see cref="AnswerOffer"/> gives.</remarks>
    public Func<byte, string, MaterialReceiveAcknowledgement> AnswerRequest { get; set; }
        = static (_, _) => MaterialReceiveAcknowledgement.Granted;

    /// <summary>Offers material to the other end - S4F1, answered by S4F2.</summary>
    /// <param name="portId">Which port - <c>PTN</c>.</param>
    /// <param name="materialId">Which piece of material - <c>MID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the other end will take it, and when it will not, which of E5's four reasons.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RSACK</c>.</exception>
    public async ValueTask<MaterialSendAcknowledgement> OfferAsync(
        byte portId,
        string materialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialId);

        using var reply = await RequestAsync(1, "ReadyToSendMaterial", portId, materialId, cancellationToken)
            .ConfigureAwait(false);

        var code = MaterialCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S4F2 carried no RSACK.");

        if (code > (byte)MaterialSendAcknowledgement.Other)
        {
            _logger.LogInformation("S4F2 answered with RSACK {Code}, which E5 leaves to the equipment.", code);

            return MaterialSendAcknowledgement.Other;
        }

        return (MaterialSendAcknowledgement)code;
    }

    /// <summary>Asks the other end to send material - S4F17, answered by S4F18.</summary>
    /// <param name="portId">Which port - <c>PTN</c>.</param>
    /// <param name="materialId">Which piece of material, or empty for whatever is next.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the other end granted it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no <c>RRACK</c>.</exception>
    public async ValueTask<MaterialReceiveAcknowledgement> RequestMaterialAsync(
        byte portId,
        string materialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialId);

        using var reply = await RequestAsync(17, "RequestToReceive", portId, materialId, cancellationToken)
            .ConfigureAwait(false);

        var code = MaterialCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S4F18 carried no RRACK.");

        if (code > (byte)MaterialReceiveAcknowledgement.Other)
        {
            _logger.LogInformation("S4F18 answered with RRACK {Code}, which E5 leaves to the equipment.", code);

            return MaterialReceiveAcknowledgement.Other;
        }

        return (MaterialReceiveAcknowledgement)code;
    }

    /// <summary>Tells the other end one of the seven steps that are not answered.</summary>
    /// <param name="step">Which step. <see cref="MaterialHandoffStep.ReadyToSend"/> and <see cref="MaterialHandoffStep.RequestToReceive"/> are refused here, because both expect a reply.</param>
    /// <param name="portId">Which port - <c>PTN</c>.</param>
    /// <param name="materialId">Which piece of material - <c>MID</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the message is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="step"/> is one of the two that is answered.</exception>
    /// <remarks>
    /// <para>
    /// Posted, not sent: E5 gives these seven no secondary at all, so there is nothing to wait for
    /// and no T3 to elapse. A caller that wants to know the message arrived waits for the step the
    /// other end sends next - which is what makes this handshake interlocked rather than
    /// acknowledged.
    /// </para>
    /// <para>
    /// One method with a step rather than seven methods, because the body of all seven is the same
    /// port and the same material and the only difference is which function number carries it. The
    /// two that are answered have their own methods, because their answers have different types.
    /// </para>
    /// </remarks>
    public async ValueTask ReportAsync(
        MaterialHandoffStep step,
        byte portId,
        string materialId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialId);

        if (step is MaterialHandoffStep.ReadyToSend or MaterialHandoffStep.RequestToReceive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(step),
                step,
                $"S4F{(byte)step} expects a reply; use {(step is MaterialHandoffStep.ReadyToSend ? nameof(OfferAsync) : nameof(RequestMaterialAsync))}.");
        }

        using var message = new SecsMessage(4, (byte)step, replyExpected: false)
        {
            Name = step.ToString(),
            Item = MaterialCodec.HandoffBody(portId, materialId),
        };

        await _connection.PostAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once any reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The first check - that the function is one of the nine - is the one line here no test
    /// reaches, and it stays. A stream 4 function outside the nine can only arrive if an
    /// application registered this handler for one, since the router answers S9F5 for anything not
    /// declared; and without the check such a message would be dropped anyway, by the switch
    /// further down having nothing to answer it with. What the check changes is only the warning
    /// in the log - "which answers nine primaries of stream 4" rather than "E5 gives it none" -
    /// and a wrong explanation in a log is worth one branch.
    /// </remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (transaction.S != 4 || !Enum.IsDefined((MaterialHandoffStep)transaction.F))
        {
            _logger.LogWarning(
                "S{S}F{F} reached the material handoff handler, which answers nine primaries of stream 4.",
                transaction.S,
                transaction.F);

            return;
        }

        var step = (MaterialHandoffStep)transaction.F;

        if (!MaterialCodec.TryReadHandoff(transaction.Message.Item, out var portId, out var materialId))
        {
            // No acknowledgement code exists on seven of the nine, so there is nothing to refuse
            // with: a body this reader cannot make sense of is logged and dropped, and the
            // interlock stalls, which is what a handler would see on a lost message anyway.
            _logger.LogWarning("S4F{F} did not carry a port and a piece of material.", transaction.F);

            return;
        }

        HandoffReported?.Invoke(this, new MaterialHandoffEventArgs
        {
            Step = step,
            PortId = portId,
            MaterialId = materialId,
        });

        if (!transaction.ReplyExpected)
        {
            return;
        }

        var (function, name, code) = step switch
        {
            MaterialHandoffStep.ReadyToSend => (2, "ReadyToSendAcknowledge", (byte)AnswerOffer(portId, materialId)),
            MaterialHandoffStep.RequestToReceive => (18, "RequestToReceiveAcknowledge", (byte)AnswerRequest(portId, materialId)),
            _ => (0, string.Empty, (byte)0),
        };

        if (function == 0)
        {
            // One of the seven, sent with W=1 by a machine that should not have. Answering an
            // S4F3 with an S4F4 would be inventing a message E5 does not have, so the reply is
            // the one E5 does have for this: nothing, and the sender's T3 says so.
            _logger.LogWarning("S4F{F} arrived expecting a reply, and E5 gives it none.", transaction.F);

            return;
        }

        using var reply = new SecsMessage(4, (byte)function, replyExpected: false)
        {
            Name = name,
            Item = MaterialCodec.CodeBody(code, step is MaterialHandoffStep.ReadyToSend ? "RSACK" : "RRACK"),
        };

        await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SecsMessage> RequestAsync(
        byte function,
        string name,
        byte portId,
        string materialId,
        CancellationToken cancellationToken)
    {
        using var primary = new SecsMessage(4, function, replyExpected: true)
        {
            Name = name,
            Item = MaterialCodec.HandoffBody(portId, materialId),
        };

        return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
    }
}
