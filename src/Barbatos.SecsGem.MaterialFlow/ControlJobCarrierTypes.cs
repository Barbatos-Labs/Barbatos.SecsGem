// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.ControlJobs;

namespace Barbatos.SecsGem.MaterialFlow;

/// <summary>
/// What it takes for a control job's material to count as ready, and what its ending does to the
/// carriers it named.
/// </summary>
/// <remarks>
/// <para>
/// E94 gives a control job one transition the machine makes rather than the host - its material is
/// ready - and one ending the machine reaches rather than the host asking for it: its material is
/// all dealt with. Neither has a trigger inside the E94 package, because E94 does not say what
/// the material is. E87 does: a carrier at a port, its identifier agreed, its slot map agreed.
/// </para>
/// <para>
/// So this is the two questions E94 leaves open, as properties: when is a job's material ready,
/// and when is it finished with. Every default is the one that cannot flatter - a job that does
/// not start is a lot that waits, and a job that starts on material nobody confirmed is a lot
/// processed against the wrong recipe.
/// </para>
/// </remarks>
public sealed class ControlJobCarrierOptions
{
    /// <summary>Whether an identifier in a job's carrier list names an E87 carrier at all.</summary>
    /// <remarks>
    /// <para>
    /// Default: every one of them does. A control job's material is a list of identifiers -
    /// <see cref="ControlJob.Carriers"/> is deliberately strings and not E87 carriers, so that a
    /// machine can run control jobs over whatever material model it has - and a machine whose
    /// jobs name more than one kind of material says here which of them are carriers, usually by
    /// how the fab spells them.
    /// </para>
    /// <para>
    /// <strong>This is a predicate and not a flag, and that is the point.</strong> The carrier
    /// manager cannot tell an identifier it is not responsible for from a carrier that has not
    /// arrived yet: both are simply absent from it. A flag saying "tolerate the ones you do not
    /// know" would therefore also tolerate a lot whose second carrier is still on its way, and
    /// start it - which is the one mistake a bridge like this must not make. So the fab names its
    /// own material and everything else holds the job up until it arrives.
    /// </para>
    /// </remarks>
    public Func<string, bool> IsCarrier { get; set; } = static _ => true;

    /// <summary>Whether a carrier has to be at a load port before it counts as ready.</summary>
    /// <remarks>
    /// Default <see langword="true"/>. A carrier can be verified without ever being at a port -
    /// <c>CarrierNotification</c> creates one the host says is coming, and E87's identifier state
    /// machine will settle it - so verified alone does not mean the material has arrived. A
    /// machine whose carriers live somewhere other than a load port, an internal stocker say,
    /// turns this off.
    /// </remarks>
    public bool RequiresCarrierAtPort { get; set; } = true;

    /// <summary>Whether a job whose carriers all become ready is told so by itself.</summary>
    /// <remarks>
    /// Default <see langword="true"/>, and the reason this bridge listens to anything: the moment
    /// a job's material is ready is the moment the <em>last</em> of its carriers is verified, and
    /// nothing outside can see that without watching every transition.
    /// </remarks>
    public bool ReadiesWhenCarriersVerified { get; set; } = true;

    /// <summary>Whether a job whose carriers are all finished with is told so by itself.</summary>
    /// <remarks>Default <see langword="true"/>, the same quorum at the other end.</remarks>
    public bool FinishesWhenCarriersComplete { get; set; } = true;

    /// <summary>Whether a carrier that was stopped counts as dealt with.</summary>
    /// <remarks>
    /// <para>
    /// Default <see langword="false"/>. E94's machine-side ending is
    /// <see cref="ControlJobManager.MaterialFinished"/>, which records
    /// <see cref="ControlJobCompletion.Completed"/> - the lot is done. A carrier in
    /// <see cref="Barbatos.SecsGem.Carriers.CarrierAccessStatus.CarrierStopped"/> says the
    /// opposite, and calling the job complete over it would tell a host something it has no way
    /// of checking.
    /// </para>
    /// <para>
    /// So by default a stopped carrier does not finish the job: the job stays where it is and
    /// waits for the host to stop or abort it, which is the ending that tells the truth. The
    /// bridge does not keep that to itself - see
    /// <see cref="ControlJobCarrierBridge.Stalled"/>.
    /// </para>
    /// </remarks>
    public bool CountsStoppedCarriers { get; set; }

    /// <summary>Whether an ending means the carriers were stopped rather than finished.</summary>
    /// <remarks>
    /// <para>
    /// Used by <see cref="ControlJobCarrierBridge.ReleaseCarriers"/> to pick between E87's two
    /// endings. The default is the only mapping that needs no choosing: anything but
    /// <see cref="ControlJobCompletion.Completed"/> ended early, so the carrier is
    /// <see cref="Barbatos.SecsGem.Carriers.CarrierAccessStatus.CarrierStopped"/> and the one
    /// completion that did finish leaves it
    /// <see cref="Barbatos.SecsGem.Carriers.CarrierAccessStatus.CarrierComplete"/>.
    /// </para>
    /// <para>
    /// <see cref="ControlJobCompletion.Cancelled"/> is in the early half and reaches E87 as
    /// nothing at all: a cancelled job never ran, so its carriers were never accessed and E87
    /// refuses to end an access that never began. That refusal is reported rather than smoothed
    /// over, because a cancelled job with a carrier in access is a machine telling two stories.
    /// </para>
    /// </remarks>
    public Func<ControlJobCompletion, bool> EndsCarriersEarly { get; set; }
        = completion => completion is not ControlJobCompletion.Completed;
}

/// <summary>What happened between a control job and the carriers it names.</summary>
/// <remarks>
/// A list of the carriers that satisfied whatever was asked and a dictionary of the ones that did
/// not, rather than one boolean: a job held up by one carrier out of four is the case this type
/// exists for, and which carrier it is, is the only thing anybody can act on.
/// </remarks>
public sealed class ControlJobCarrierResult
{
    /// <summary>Whether what was asked for happened.</summary>
    /// <remarks>
    /// For <see cref="ControlJobCarrierBridge.Check"/>, whether the material is ready. For
    /// <see cref="ControlJobCarrierBridge.Ready"/> and
    /// <see cref="ControlJobCarrierBridge.Finish"/>, whether the job accepted the transition -
    /// which it can refuse on its own grounds after the carriers have agreed, and then this is
    /// <see langword="false"/> and <see cref="Reason"/> is E94's own words. For
    /// <see cref="ControlJobCarrierBridge.ClaimCarriers"/> and
    /// <see cref="ControlJobCarrierBridge.ReleaseCarriers"/>, whether anything was attempted at
    /// all; the per-carrier outcomes are in <see cref="Carriers"/> and <see cref="Refused"/>.
    /// </remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not. Empty when it was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Which job.</summary>
    public required string ControlJobId { get; init; }

    /// <summary>The carriers that satisfied what was asked.</summary>
    public IReadOnlyList<string> Carriers { get; init; } = [];

    /// <summary>The carriers that did not, and why.</summary>
    /// <remarks>
    /// Keyed by carrier identifier. It holds the tolerated ones too - an identifier that
    /// <see cref="ControlJobCarrierOptions.IsCarrier"/> says is not this manager's material is
    /// listed here and does not stop the job. A caller that wants to know why a job started
    /// anyway reads this; one that only wants to know whether it did reads
    /// <see cref="IsAccepted"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Refused { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets how many carriers satisfied what was asked.</summary>
    public int Count => Carriers.Count;

    internal static ControlJobCarrierResult Denied(string controlJobId, string reason)
        => new() { IsAccepted = false, ControlJobId = controlJobId, Reason = reason };
}

/// <summary>A control job and its carriers met.</summary>
public sealed class ControlJobCarrierEventArgs : EventArgs
{
    /// <summary>What happened.</summary>
    public required ControlJobCarrierResult Result { get; init; }
}
