// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Material;

/// <summary>
/// <c>TRJOBMS</c> - where a transfer job is in its life, at S4F23.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Six states, and the reference names every one of them.</strong> That matters because an
/// earlier chapter refused this half of stream 4 on the ground that "a transfer job has no state
/// model anywhere in the reference" - which was argued from the message rows while the answer was
/// in the item dictionary. <c>TRJOBMS</c> is a <c>U1</c> with a table, so this enum is quoted
/// rather than invented, and <c>MaterialTransferJobTests</c> checks it against that table.
/// </para>
/// <para>
/// No catch-all member, because the table reserves nothing: six codes, nought to five, and nothing
/// above. So a machine answering six is reporting a state the reference does not name, and the
/// reader refuses the alert rather than folding it onto a state the machine never claimed - the
/// same position <c>COMMACK</c> is in.
/// </para>
/// </remarks>
public enum MaterialTransferJobState : byte
{
    /// <summary>Queued, and not yet chosen to run.</summary>
    Queued = 0,

    /// <summary>Selected to run next.</summary>
    Selected = 1,

    /// <summary>Executing.</summary>
    Executing = 2,

    /// <summary>Paused part way through.</summary>
    Paused = 3,

    /// <summary>Finished.</summary>
    Completed = 4,

    /// <summary>Cancelled before it finished.</summary>
    Cancelled = 5,
}

/// <summary>
/// What S4F20 and S4F22 answer with: a <c>TRACK</c>, and a reason when there is one.
/// </summary>
/// <remarks>
/// <para>
/// <strong><c>TRACK</c> is defined and carries no values.</strong> It is an <c>A[n]</c> - free text
/// - so there is no code this library could compare against and no table to read it from. This half
/// of stream 4 was refused on that item, and the refusal was wrong in the way a refusal goes wrong:
/// <c>TRACK</c> being untabled is not <c>TRACK</c> being undefined, and the row carries something
/// that <em>is</em> machine-readable beside it.
/// </para>
/// <para>
/// <strong>And this type offers no verdict</strong>, which is the part worth reading twice. The
/// first draft of it had an <c>IsAccepted</c> reading nought from <c>ERRCODE</c> - and that
/// contradicted what this very package says about the same item at S4F33: "untabled in the
/// reference, so nothing here interprets it". <c>ERRCODE</c> is a <c>U4</c> with no table either,
/// so the row has <em>no</em> tabled item in it. Where a row does have one this stack reads that
/// and uses <c>ERRCODE</c> as the reason beside it - <c>GemExceptionAbortResult</c> does exactly
/// that with S5F18's <c>ACKA</c>. This row has nothing to play that part, so all three fields come
/// back as they arrived and the fab decides.
/// </para>
/// <para>
/// <strong>That is not the refusal the row used to carry.</strong> The refusal said a host cannot
/// tell whether its job was created, which is true of the items and is not a reason to leave the
/// row unserved: carrying a field nobody has defined is what this repository does for every own
/// gap, and a row is refused for an uninterpretable field only when that field is the one the
/// message turns on. What S4F19 turns on is the job and the link, and both are defined.
/// </para>
/// </remarks>
/// <param name="Acknowledgement">TRACK, exactly as it arrived. Untabled, so uninterpreted.</param>
/// <param name="ErrorCode">ERRCODE, exactly as it arrived. Untabled, so uninterpreted.</param>
/// <param name="ErrorText">ERRTEXT, for a person to read. May be empty.</param>
public readonly record struct MaterialTransferJobResult(
    string Acknowledgement,
    uint ErrorCode,
    string ErrorText)
{
    /// <summary>Returns all three fields, because none of them is a verdict on its own.</summary>
    /// <returns>For example <c>TRACK=OK ERRCODE=0</c>, or <c>TRACK= ERRCODE=9: no such port</c>.</returns>
    public override string ToString()
        => $"TRACK={Acknowledgement} ERRCODE={ErrorCode}{(ErrorText.Length == 0 ? string.Empty : $": {ErrorText}")}";
}

/// <summary>What S4F19 asks for: a transfer job over one handoff link.</summary>
/// <remarks>
/// <para>
/// <strong>The link block is S4F27's, item for item.</strong> S4F19 carries a <c>DATAID</c> and a
/// <c>TRJOBNAME</c> and then the eight items <c>MaterialHandoff.AnnounceLinkAsync</c> already
/// writes, in the same order - so the body is read off a row this package serves rather than
/// guessed at, which is what the item list on its own could never settle.
/// </para>
/// <para>
/// One link per job. The reference gives the row ten item names and no nesting, and the eight
/// link items appear once each in S4F27's order; the only served sibling is flat, so this is flat.
/// </para>
/// </remarks>
public sealed class MaterialTransferJobRequest
{
    /// <summary>Gets the job's name - TRJOBNAME.</summary>
    public required string JobName { get; init; }

    /// <summary>Gets the link the job is for - every item of it but the equipment name.</summary>
    /// <remarks>
    /// <see cref="MaterialHandoffLink.EquipmentName"/> is ignored: S4F27 carries an <c>EQNAME</c>
    /// and S4F19 does not, which is the one difference between the two blocks.
    /// </remarks>
    public required MaterialHandoffLink Link { get; init; }

    /// <summary>Gets DATAID, the host's own transaction number for this request.</summary>
    public uint DataId { get; init; }

    /// <summary>Returns the job name and the link.</summary>
    /// <returns>For example <c>JOB-7 over LINK-1</c>.</returns>
    public override string ToString() => $"{JobName} over {Link.Link}";
}

/// <summary>What arrived in an S4F23: a job has changed state, or something went wrong with it.</summary>
/// <remarks>
/// The only primary of this half that the equipment sends, which is why the capability declares
/// different messages at the two ends - unlike <c>MaterialHandoff</c>, whose seventeen travel
/// either way.
/// </remarks>
public sealed class MaterialTransferJobAlertEventArgs : EventArgs
{
    /// <summary>Gets TRJOBID.</summary>
    public required string JobId { get; init; }

    /// <summary>Gets TRJOBNAME.</summary>
    public string JobName { get; init; } = string.Empty;

    /// <summary>Gets TRJOBMS.</summary>
    public required MaterialTransferJobState State { get; init; }

    /// <summary>Gets ERRCODE. Nought is no error, which a plain state change has.</summary>
    public uint ErrorCode { get; init; }

    /// <summary>Gets ERRTEXT, or empty.</summary>
    public string ErrorText { get; init; } = string.Empty;

    /// <summary>Returns the job and its state.</summary>
    /// <returns>For example <c>JOB-7 Executing</c>.</returns>
    public override string ToString() => $"{JobId} {State}";
}
