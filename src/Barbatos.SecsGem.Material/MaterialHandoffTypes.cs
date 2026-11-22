// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material;

/// <summary>
/// The answer to an offer of material - <c>RSACK</c>, and the codes are the reference's own.
/// </summary>
/// <remarks>
/// <para>
/// Unusually for this stack, nothing here is inferred. <c>RSACK</c> is a <c>B[1]</c> in the
/// dictionary <em>with a table of values</em>, so these five names are quoted rather than read -
/// and the four refusals are the four reasons E5 thinks a port cannot take a box, which is more
/// than most of this stream says out loud.
/// </para>
/// <para>
/// 5 to 63 belong to the equipment, and they all read as <see cref="Other"/> with the arriving
/// code logged - the same rule <see cref="MaterialGrant"/> keeps for the same reason.
/// </para>
/// </remarks>
public enum MaterialSendAcknowledgement : byte
{
    /// <summary>The receiver will take it.</summary>
    WillAccept = 0,

    /// <summary>There is no such port.</summary>
    InvalidPort = 1,

    /// <summary>The port already has material on it.</summary>
    PortOccupied = 2,

    /// <summary>Busy; offer it again later.</summary>
    Busy = 3,

    /// <summary>The receiver is not in a mode where a host or a handler may drive it.</summary>
    NotInRemoteMode = 4,

    /// <summary>Some other refusal: E5 reserves 5 to 63 for the equipment to define.</summary>
    /// <remarks>Every code from 5 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 5,
}

/// <summary>The answer to a request to be sent material - <c>RRACK</c>.</summary>
/// <remarks>
/// Quoted from the dictionary's own table, as <see cref="MaterialSendAcknowledgement"/> is, and
/// shorter: a request to receive is granted or it is not, and the refusal carries no reason. 2 to
/// 63 belong to the equipment and read as <see cref="Other"/>.
/// </remarks>
public enum MaterialReceiveAcknowledgement : byte
{
    /// <summary>Granted.</summary>
    Granted = 0,

    /// <summary>Denied, with no reason given.</summary>
    Denied = 1,

    /// <summary>Some other refusal: E5 reserves 2 to 63 for the equipment to define.</summary>
    /// <remarks>Every code from 2 upwards reads as this one, and the arriving code is logged.</remarks>
    Other = 2,
}

/// <summary>
/// Which step of the interlocked handoff a message is - the nine primaries of S4F1 to S4F18.
/// </summary>
/// <remarks>
/// <para>
/// One enum rather than nine events, because all nine carry the same body - a port and a piece of
/// material - and what distinguishes them is which step of the handshake they announce. The three
/// faults at the end are the ones worth wiring to something: E5 gives this stream a message for
/// material that got stuck at either end and for a transfer that never finished, which is more
/// than it gives most streams.
/// </para>
/// <para>
/// <strong>The order is E5's, not an inference</strong>, and this package does not enforce it -
/// see <see cref="MaterialHandoff"/> for why a sequence nothing in this repository can check is
/// documented rather than policed.
/// </para>
/// </remarks>
public enum MaterialHandoffStep
{
    /// <summary>S4F1 - the sender offers material. Answered with <see cref="MaterialSendAcknowledgement"/>.</summary>
    ReadyToSend = 1,

    /// <summary>S4F3 - the sender is sending it now. No reply.</summary>
    SendMaterial = 3,

    /// <summary>S4F5 - the transfer finished. No reply.</summary>
    HandshakeComplete = 5,

    /// <summary>S4F7 - the sender has withdrawn the offer. No reply.</summary>
    NotReadyToSend = 7,

    /// <summary>S4F9 - the material is stuck at the sending end. No reply.</summary>
    StuckInSender = 9,

    /// <summary>S4F11 - the material is stuck at the receiving end. No reply.</summary>
    StuckInReceiver = 11,

    /// <summary>S4F13 - the transfer did not complete in time. No reply.</summary>
    SendIncompleteTimeout = 13,

    /// <summary>S4F15 - the receiver has it. No reply.</summary>
    MaterialReceived = 15,

    /// <summary>S4F17 - the receiver asks to be sent material. Answered with <see cref="MaterialReceiveAcknowledgement"/>.</summary>
    RequestToReceive = 17,
}

/// <summary>One step of an interlocked handoff, as it arrived.</summary>
/// <remarks>
/// The body of all nine is <c>L,2 { PTN, MID }</c>, so the arguments are the same whichever step
/// it is. <see cref="Step"/> is the function number it came in on, which is why the enum's values
/// are those function numbers.
/// </remarks>
public sealed class MaterialHandoffEventArgs : EventArgs
{
    /// <summary>Which step of the handshake this is.</summary>
    public required MaterialHandoffStep Step { get; init; }

    /// <summary>Which port it is about - <c>PTN</c>.</summary>
    public required byte PortId { get; init; }

    /// <summary>Which piece of material - <c>MID</c>. Empty when the sender named none.</summary>
    public string MaterialId { get; init; } = string.Empty;

    /// <summary>Returns the step, the port and the material.</summary>
    /// <returns>For example <c>ReadyToSend port 1 (LOT-7)</c>.</returns>
    public override string ToString()
        => MaterialId.Length == 0 ? $"{Step} port {PortId}" : $"{Step} port {PortId} ({MaterialId})";
}

/// <summary>
/// Which role this end plays in a handoff - <c>TRROLE</c>, and the two names are the table's own.
/// </summary>
/// <remarks>
/// <para>
/// <c>TRROLE</c> is an <c>A</c> rather than a code, and it is one of the few text items in this
/// reference that arrives <em>with</em> a table: SOURCE and DESTINATION, spelled out. That matters
/// beyond this enum, because it is what makes the silence around <c>TRACK</c> meaningful - the
/// source does write values down for a text item when it knows them.
/// </para>
/// <para>
/// So the wire carries the word and not a number, and a word this end cannot read is
/// <see cref="Other"/> rather than a guess at which side somebody meant.
/// </para>
/// </remarks>
public enum MaterialHandoffRole
{
    /// <summary>Neither word the table names, or none at all.</summary>
    /// <remarks>
    /// First rather than last, because it is the value a default-constructed descriptor has and
    /// "unknown" is the honest reading of an unset role. Which side is sending material is not
    /// something to assume.
    /// </remarks>
    Other = 0,

    /// <summary>SOURCE - this equipment is the material source.</summary>
    Source = 1,

    /// <summary>DESTINATION - this equipment is the material destination.</summary>
    Destination = 2,
}

/// <summary>
/// The answer carried by the handoff protocol's three acknowledgement items.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One enum for <c>HOACK</c>, <c>HOCANCELACK</c> and <c>HOHALTACK</c></strong>, which are
/// three separate items in the dictionary and identical in every respect that matters here: each
/// is a <c>B[1]</c> and <em>none of the three carries a table of values</em>. There is nothing to
/// quote, so there is nothing for three enums to disagree about, and saying it once is better than
/// saying the same inferred thing three times.
/// </para>
/// <para>
/// <strong>What is inferred is that zero is the favourable answer</strong>, and it is read off this
/// stream's own tabled siblings rather than invented: <c>RSACK</c> 0 is "will accept" and
/// <c>RRACK</c> 0 is "granted". That is the same reading
/// <c>Barbatos.SecsGem.ControlPrograms</c> gives <c>ACKC8</c>, which has no table either - and the
/// consequence is the same, so it is said here too: <strong>every non-zero code is a refusal with
/// no reason</strong>, because the reference names none. A machine that needs to say why sends the
/// reason in <c>ERRCODE</c> and <c>ERRTEXT</c>, which S4F33 carries beside the code for exactly
/// that purpose.
/// </para>
/// </remarks>
public enum MaterialHandoffAcknowledgement : byte
{
    /// <summary>Zero - the favourable answer, read off <c>RSACK</c> and <c>RRACK</c>.</summary>
    Accepted = 0,

    /// <summary>Any non-zero code: a refusal, and the reference names no reasons.</summary>
    /// <remarks>
    /// The arriving code is logged, because this member throws it away and the table offers nowhere
    /// else for it to go. Not called <c>Other</c>, as the catch-alls elsewhere in this package are:
    /// those sit above a table's named codes and this one sits above nothing, so there is no
    /// boundary for it to mark.
    /// </remarks>
    Refused = 1,
}

/// <summary>
/// Which step of the handoff protocol a message is - the seven per-link primaries of S4F29 to
/// S4F41.
/// </summary>
/// <remarks>
/// <para>
/// S4F27 is not here: it announces a link rather than acting on one, and it is the only row of the
/// eight that carries a whole descriptor instead of a <c>TRLINK</c> and a field or two. It has an
/// event of its own for that reason.
/// </para>
/// <para>
/// The values are the function numbers, as <see cref="MaterialHandoffStep"/>'s are, and for the
/// same reason: the function is the only thing that distinguishes rows which otherwise share a
/// shape. Four shapes across seven rows - a command, a command completion, a verification, and the
/// four cancel and halt messages that carry a link with or without an acknowledgement.
/// </para>
/// </remarks>
public enum MaterialHandoffNotice
{
    /// <summary>S4F29 - run this command on the link. Carries an index, a name and parameters.</summary>
    Command = 29,

    /// <summary>S4F31 - that command finished. Carries the index and an acknowledgement.</summary>
    CommandComplete = 31,

    /// <summary>S4F33 - the handoff as a whole is verified, or is not. Carries an acknowledgement and a reason.</summary>
    Verified = 33,

    /// <summary>S4F35 - this end wants to cancel. Carries the link alone.</summary>
    CancelReady = 35,

    /// <summary>S4F37 - the answer to a cancel. Carries an acknowledgement.</summary>
    CancelReadyAcknowledge = 37,

    /// <summary>S4F39 - this end wants to halt. Carries the link alone.</summary>
    Halt = 39,

    /// <summary>S4F41 - the answer to a halt. Carries an acknowledgement.</summary>
    HaltAcknowledge = 41,
}

/// <summary>
/// A handoff link, as S4F27 announces it.
/// </summary>
/// <remarks>
/// <para>
/// Nine items, every one of them defined in the dictionary and eight of the nine named by stream 4
/// alone - which is what made this half of the stream implementable where the transfer jobs above
/// it were not. The <c>TRLINK</c> is the name the rest of the protocol addresses: S4F29 through
/// S4F41 carry it and nothing else by way of identity.
/// </para>
/// <para>
/// <strong>S4F27 is where a link comes from.</strong> The README used to say a <c>TRLINK</c> was
/// minted by S4F19, the transfer job create, and that the handoff half therefore could not be
/// served without the job half - which the dictionary contradicts: this row carries the same
/// descriptor the job does, travels either way, and nothing in the reference says only a job may
/// name a link. Two ends can agree a handoff between themselves.
/// </para>
/// </remarks>
public sealed class MaterialHandoffLink
{
    /// <summary>Gets the link's name - <c>TRLINK</c>. What every later message addresses.</summary>
    public required string Link { get; init; }

    /// <summary>Gets the equipment announcing it - <c>EQNAME</c>.</summary>
    public string EquipmentName { get; init; } = string.Empty;

    /// <summary>Gets the object being handed off - <c>TROBJNAME</c>.</summary>
    public string ObjectName { get; init; } = string.Empty;

    /// <summary>Gets what kind of object it is - <c>TROBJTYPE</c>.</summary>
    /// <remarks>
    /// Free text, and deliberately not an enumeration: the reference gives this item no table, and
    /// E87's object types are that standard's rather than this stream's.
    /// </remarks>
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>Gets this end's port - <c>TRPORT</c>.</summary>
    /// <remarks>
    /// Text, where the interlocked handshake's <c>PTN</c> is a <c>U1</c>. The two halves of stream 4
    /// name a port differently and this package does not reconcile them, because the reference
    /// does not: a <c>TRPORT</c> of "LP1" has no <c>PTN</c>.
    /// </remarks>
    public string Port { get; init; } = string.Empty;

    /// <summary>Gets which side this end is - <c>TRROLE</c>.</summary>
    public MaterialHandoffRole Role { get; init; }

    /// <summary>Gets the other end - <c>TRPTNR</c>.</summary>
    public string PartnerName { get; init; } = string.Empty;

    /// <summary>Gets the other end's port - <c>TRPTPORT</c>.</summary>
    public string PartnerPort { get; init; } = string.Empty;

    /// <summary>Gets where the handoff happens - <c>TRLOCATION</c>.</summary>
    public string Location { get; init; } = string.Empty;

    /// <summary>Returns the link, the role and the two ends.</summary>
    /// <returns>For example <c>LINK-1 Source EQ1:LP1 -&gt; AGV3:ARM</c>.</returns>
    public override string ToString()
        => $"{Link} {Role} {EquipmentName}:{Port} -> {PartnerName}:{PartnerPort}";
}

/// <summary>A link another end has announced - S4F27.</summary>
/// <remarks>
/// Its own event rather than an eighth value on <see cref="MaterialHandoffNotice"/>, because it is
/// the one row of the eight that carries a descriptor instead of acting on one. A handler that
/// keeps links keeps them from here.
/// </remarks>
public sealed class MaterialHandoffAnnouncementEventArgs : EventArgs
{
    /// <summary>Gets the link, as it arrived.</summary>
    public required MaterialHandoffLink Link { get; init; }

    /// <summary>Returns the link.</summary>
    /// <returns>What <see cref="MaterialHandoffLink.ToString"/> returns.</returns>
    public override string ToString() => Link.ToString();
}

/// <summary>One step of the handoff protocol, as it arrived - S4F29 to S4F41.</summary>
/// <remarks>
/// <para>
/// <strong>Which members are populated depends on <see cref="Notice"/></strong>, because the seven
/// rows have four shapes between them. <see cref="Link"/> is always set, since it is the whole of
/// the identity in every one of them:
/// </para>
/// <list type="bullet">
/// <item><description><c>Command</c>: <see cref="Index"/>, <see cref="CommandName"/>, <see cref="Parameters"/>.</description></item>
/// <item><description><c>CommandComplete</c>: <see cref="Index"/> and <see cref="Acknowledgement"/>.</description></item>
/// <item><description><c>Verified</c>: <see cref="Acknowledgement"/>, <see cref="ErrorCode"/>, <see cref="ErrorText"/>.</description></item>
/// <item><description><c>CancelReady</c> and <c>Halt</c>: the link alone.</description></item>
/// <item><description><c>CancelReadyAcknowledge</c> and <c>HaltAcknowledge</c>: <see cref="Acknowledgement"/>.</description></item>
/// </list>
/// <para>
/// One type rather than four, which is the trade this package already made for the interlocked
/// handshake's nine steps - and the reason is the same: four event types and four argument types
/// would be eight public types for one protocol, and a handler written against
/// <see cref="Notice"/> reads the same way either way. What is different here is that the shapes
/// genuinely differ, so the list above is part of the contract rather than a note.
/// </para>
/// </remarks>
public sealed class MaterialHandoffLinkEventArgs : EventArgs
{
    /// <summary>Gets which step this is.</summary>
    public required MaterialHandoffNotice Notice { get; init; }

    /// <summary>Gets the link it is about - <c>TRLINK</c>. Always set.</summary>
    public required string Link { get; init; }

    /// <summary>Gets <c>MCINDEX</c>, for the two command rows. Zero elsewhere.</summary>
    public uint Index { get; init; }

    /// <summary>Gets <c>HOCMDNAME</c>, for a command. Empty elsewhere.</summary>
    public string CommandName { get; init; } = string.Empty;

    /// <summary>Gets the <c>CPNAME</c> and <c>CPVAL</c> pairs, for a command. Empty elsewhere.</summary>
    /// <remarks>
    /// Text rather than items, unlike GEM's remote command parameters: the reference types
    /// <c>CPVAL</c> as an <c>A</c> here, so there is no format to preserve and nothing borrowed
    /// from the arriving message - which means these outlive the handler, where GEM's do not.
    /// </remarks>
    public IReadOnlyList<(string Name, string Value)> Parameters { get; init; } = [];

    /// <summary>Gets the acknowledgement, for the four rows that carry one. <c>Accepted</c> elsewhere.</summary>
    public MaterialHandoffAcknowledgement Acknowledgement { get; init; }

    /// <summary>Gets <c>ERRCODE</c>, for a verification. Zero elsewhere, and zero means no error.</summary>
    /// <remarks>
    /// Untabled in the reference, so nothing here interprets it: a number this end passes through.
    /// The same treatment <c>QUA</c> gets in this package, and for the same reason.
    /// </remarks>
    public uint ErrorCode { get; init; }

    /// <summary>Gets <c>ERRTEXT</c>, for a verification. Empty elsewhere.</summary>
    public string ErrorText { get; init; } = string.Empty;

    /// <summary>Returns the step, the link and whichever field distinguishes it.</summary>
    /// <returns>For example <c>Command LINK-1 #2 LOAD</c>.</returns>
    public override string ToString() => Notice switch
    {
        MaterialHandoffNotice.Command => $"{Notice} {Link} #{Index} {CommandName}",
        MaterialHandoffNotice.CommandComplete => $"{Notice} {Link} #{Index} {Acknowledgement}",
        MaterialHandoffNotice.CancelReady or MaterialHandoffNotice.Halt => $"{Notice} {Link}",
        _ => $"{Notice} {Link} {Acknowledgement}",
    };
}
