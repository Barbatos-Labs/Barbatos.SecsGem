// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Productivity;

/// <summary>The answer to anything that would move a tool between E10's states.</summary>
/// <remarks>
/// A reason in words rather than a number, as in every other behaviour package here: E10 has no
/// messages and so no acknowledgement code. There are only two things this package refuses, and
/// both are a caller saying nothing - see <see cref="ProductivityTracker.MoveTo"/>.
/// </remarks>
public sealed class ProductivityResult
{
    /// <summary>Whether it was allowed.</summary>
    public required bool IsAccepted { get; init; }

    /// <summary>Why not. Empty when accepted.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>An accepted answer.</summary>
    internal static ProductivityResult Ok { get; } = new() { IsAccepted = true };

    /// <summary>A refusal.</summary>
    /// <param name="reason">What was wrong.</param>
    /// <returns>The result.</returns>
    internal static ProductivityResult Refused(string reason) => new() { IsAccepted = false, Reason = reason };
}

/// <summary>
/// A tool's time and what it says about the tool - E10's RAM metrics and E79's OEE.
/// </summary>
/// <remarks>
/// <para>
/// A snapshot taken at the moment it is asked for, so the state the tool is in now has its elapsed
/// time counted in already: the six buckets add up to <see cref="Total"/>. That is an invariant
/// worth leaning on and there is a test that leans on it.
/// </para>
/// <para>
/// <strong>Two denominators, and confusing them is the commonest mistake made against E10.</strong>
/// Every ratio here exists in an <em>operations</em> form and a <em>total</em> form, and they
/// differ by the time nobody scheduled. A tool that ran perfectly through one staffed shift of a
/// three-shift day is at 100% availability on the operations denominator and 33% on the total one,
/// and both numbers are correct answers to different questions. The names say which:
/// <see cref="Availability"/> and <see cref="OperationalUtilisation"/> are over operations time,
/// <see cref="AvailabilityEfficiency"/> and <see cref="TotalUtilisation"/> over total time.
/// </para>
/// <para>
/// Every ratio is zero when its denominator is, rather than a division by zero: a tool asked about
/// the instant it was created has not been unavailable either.
/// </para>
/// </remarks>
public sealed class ProductivityReport
{
    /// <summary>When this accounting started: the tool's creation, or the last reset.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>What the tool is doing now.</summary>
    public required EquipmentState State { get; init; }

    /// <summary>Why, when the state has a reason. Empty otherwise.</summary>
    public string StateReason { get; init; } = string.Empty;

    /// <summary>How long it has been in <see cref="State"/>, counting only time inside this window.</summary>
    public required TimeSpan TimeInState { get; init; }

    // ------------------------------------------------------------ the six buckets

    /// <summary>Time nobody asked the tool to be available.</summary>
    public required TimeSpan NonScheduled { get; init; }

    /// <summary>Time it was broken.</summary>
    /// <remarks>Includes a repair still in progress, which is why <see cref="MeanTimeToRepair"/> does not use this figure.</remarks>
    public required TimeSpan UnscheduledDowntime { get; init; }

    /// <summary>Time it was deliberately unavailable.</summary>
    public required TimeSpan ScheduledDowntime { get; init; }

    /// <summary>Time it could have produced and was learning instead.</summary>
    public required TimeSpan Engineering { get; init; }

    /// <summary>Time it could have produced and had nothing to produce.</summary>
    public required TimeSpan Standby { get; init; }

    /// <summary>Time it produced.</summary>
    public required TimeSpan Productive { get; init; }

    // ------------------------------------------------------------------ the counts

    /// <summary>How many times the tool broke - entries into <see cref="EquipmentState.UnscheduledDowntime"/>.</summary>
    public required int Failures { get; init; }

    /// <summary>How many times it was taken down on purpose - entries into <see cref="EquipmentState.ScheduledDowntime"/>.</summary>
    public required int ScheduledInterrupts { get; init; }

    /// <summary>How many repairs finished - exits from <see cref="EquipmentState.UnscheduledDowntime"/>.</summary>
    /// <remarks>
    /// Fewer than <see cref="Failures"/> exactly while a repair is still going on, which is the
    /// point: a repair that has not finished has no duration yet.
    /// </remarks>
    public required int Repairs { get; init; }

    /// <summary>Whatever the fab counts as a cycle - a wafer, a lot, a robot move.</summary>
    /// <remarks>E10 leaves the unit to the equipment, so this package takes it as a number it is told.</remarks>
    public required long Cycles { get; init; }

    /// <summary>How many units came out.</summary>
    public required long ActualUnits { get; init; }

    /// <summary>How many of those were good.</summary>
    public required long EffectiveUnits { get; init; }

    /// <summary>What the units that came out should have taken - E79's theoretical production time for actual units.</summary>
    public required TimeSpan TheoreticalActualTime { get; init; }

    /// <summary>What the good units should have taken - E79's theoretical production time for effective units.</summary>
    public required TimeSpan TheoreticalEffectiveTime { get; init; }

    // ------------------------------------------------------------- the roll-ups

    /// <summary>Gets every stoppage, planned or not - what E10 calls an interrupt.</summary>
    public int Interrupts => Failures + ScheduledInterrupts;

    /// <summary>Gets the time the tool was unavailable: unscheduled plus scheduled downtime.</summary>
    public TimeSpan Downtime => UnscheduledDowntime + ScheduledDowntime;

    /// <summary>Gets the time the tool was able to produce: engineering, standby and productive.</summary>
    public TimeSpan Uptime => Engineering + Standby + Productive;

    /// <summary>Gets the time the tool was asked to be available: everything but non-scheduled.</summary>
    public TimeSpan Operations => Downtime + Uptime;

    /// <summary>Gets the whole window.</summary>
    public TimeSpan Total => NonScheduled + Operations;

    // -------------------------------------------------------------- E10's metrics

    /// <summary>Gets uptime over operations time, between zero and one.</summary>
    /// <remarks>
    /// The question "when the factory wanted this tool, could it work?". Not the first factor of
    /// E79's OEE, which uses the total denominator instead - that one is
    /// <see cref="AvailabilityEfficiency"/>.
    /// </remarks>
    public double Availability => Share(Uptime, Operations);

    /// <summary>Gets productive time over operations time, between zero and one.</summary>
    public double OperationalUtilisation => Share(Productive, Operations);

    /// <summary>Gets productive time over the whole window, between zero and one.</summary>
    public double TotalUtilisation => Share(Productive, Total);

    /// <summary>Gets the mean time between failures.</summary>
    /// <remarks>
    /// Operations time over <see cref="Failures"/>. <strong>E10 offers more than one denominator
    /// for this</strong> - some fabs divide uptime, some divide operations time, some exclude
    /// scheduled downtime - and this package names one convention rather than pretending there is
    /// only one. Every bucket and every count is on this report, so a fab whose definition differs
    /// computes its own from the same numbers. <see cref="TimeSpan.Zero"/> when nothing has failed,
    /// which is not an infinite mean so much as a window with no evidence in it.
    /// </remarks>
    public TimeSpan MeanTimeBetweenFailures => Per(Operations, Failures);

    /// <summary>Gets the mean time between interrupts of any kind.</summary>
    /// <remarks>Operations time over <see cref="Interrupts"/>, by the same convention as <see cref="MeanTimeBetweenFailures"/>.</remarks>
    public TimeSpan MeanTimeBetweenInterrupts => Per(Operations, Interrupts);

    /// <summary>Gets the mean time a repair took.</summary>
    /// <remarks>
    /// Over <see cref="Repairs"/>, and it does <em>not</em> divide
    /// <see cref="UnscheduledDowntime"/>: that figure includes a repair still in progress, and a
    /// repair that has not finished has no duration to average. Dividing the one by the other would
    /// make a tool that is broken right now look better at being fixed the longer it stays broken.
    /// </remarks>
    public required TimeSpan MeanTimeToRepair { get; init; }

    /// <summary>Gets the mean number of cycles between failures.</summary>
    /// <remarks>Zero when nothing has failed.</remarks>
    public double MeanCyclesBetweenFailures => Failures == 0 ? 0d : (double)Cycles / Failures;

    // -------------------------------------------------------------- E79's OEE

    /// <summary>Gets E79's first factor: uptime over the whole window.</summary>
    /// <remarks>The same ratio E10 would call availability on the total denominator. E79's chain uses this one, not <see cref="Availability"/>.</remarks>
    public double AvailabilityEfficiency => Share(Uptime, Total);

    /// <summary>Gets E79's second factor: productive time over uptime.</summary>
    /// <remarks>Of the time the tool could have produced, how much it did - which is where standby and engineering come out.</remarks>
    public double OperationalEfficiency => Share(Productive, Uptime);

    /// <summary>Gets E79's third factor: what the units should have taken over what they did take.</summary>
    /// <remarks>Below one for a tool running slower than its theoretical rate; above one would mean the theoretical rate is wrong.</remarks>
    public double RateEfficiency => Share(TheoreticalActualTime, Productive);

    /// <summary>Gets E79's fourth factor: the good units over all the units, by theoretical time.</summary>
    public double QualityEfficiency => Share(TheoreticalEffectiveTime, TheoreticalActualTime);

    /// <summary>Gets E79's grouping of the two middle factors.</summary>
    public double PerformanceEfficiency => OperationalEfficiency * RateEfficiency;

    /// <summary>Gets overall equipment efficiency: what the good units should have taken, over the whole window.</summary>
    /// <remarks>
    /// <para>
    /// E79's own direct definition, and it is computed that way rather than by multiplying the four
    /// factors - one division instead of four, so the number does not collect four roundings. The
    /// two agree, and that identity is the best test this package has: the chain telescopes,
    /// <c>(Up/Total) * (Prod/Up) * (Theoretical/Prod) * (Effective/Theoretical)</c> cancelling down
    /// to <c>Effective/Total</c>, so any arithmetic mistake in a factor shows up as the product
    /// disagreeing with this.
    /// </para>
    /// <para>
    /// Zero while nothing has been produced, which is honest: a tool with no units out has no
    /// productivity to report, however available it was.
    /// </para>
    /// </remarks>
    public double OverallEquipmentEfficiency => Share(TheoreticalEffectiveTime, Total);

    private static double Share(TimeSpan part, TimeSpan whole)
        => whole > TimeSpan.Zero ? part / whole : 0d;

    private static TimeSpan Per(TimeSpan total, int count)
        => count <= 0 ? TimeSpan.Zero : total / count;
}

/// <summary>A tool moved between E10's states.</summary>
/// <remarks>
/// <para>
/// E10 defines no collection event for this, because E10 defines nothing on a wire. The event is
/// here because an application wants to know - to publish the new state as a GEM status variable,
/// to write a row to the fab's own history - and raising one costs nothing.
/// </para>
/// <para>
/// Raised after the state has moved and inside the tracker's lock. A handler reading the tracker
/// sees the state the event announces, and must not call back in.
/// </para>
/// </remarks>
public sealed class EquipmentStateChangedEventArgs : EventArgs
{
    /// <summary>What it was.</summary>
    public required EquipmentState Previous { get; init; }

    /// <summary>What it is.</summary>
    public required EquipmentState Current { get; init; }

    /// <summary>Why it is, when the new state has a reason. Empty otherwise.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>How long the tool spent in <see cref="Previous"/> this time.</summary>
    /// <remarks>
    /// The stretch that just ended, not the total: a tool that has been down three times this
    /// shift gets three of these, each with its own duration.
    /// </remarks>
    public required TimeSpan Elapsed { get; init; }
}

/// <summary>A tool is in the same state for a different stated reason.</summary>
/// <remarks>
/// Not a state change, which is the point of having its own event: a tool that goes from "pump
/// failed" to "awaiting parts" never stopped being in unscheduled downtime, and reporting it as a
/// transition would count a second failure and break one stretch of downtime into two. E10 cares
/// about the difference - the first phase is the tool's fault and the second is the stockroom's -
/// so the reason is worth an event of its own.
/// </remarks>
public sealed class ProductivityReasonChangedEventArgs : EventArgs
{
    /// <summary>The state the tool is in, and stays in.</summary>
    public required EquipmentState State { get; init; }

    /// <summary>What it said before.</summary>
    public required string Previous { get; init; }

    /// <summary>What it says now.</summary>
    public required string Current { get; init; }
}
