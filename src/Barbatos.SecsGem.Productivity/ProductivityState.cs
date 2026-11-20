// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Productivity;

/// <summary>
/// Everything <see cref="ProductivityTracker"/> holds, as one value.
/// </summary>
/// <remarks>
/// Almost all of this is a ledger, which is why a restart keeps almost all of it: E10 is an
/// accounting standard, and a tracker that lost its buckets to a reboot would report a day that
/// never happened. Plain properties with <c>init</c> accessors and no constructor, so a serializer
/// the application already uses round-trips it without this package depending on one.
/// </remarks>
public sealed class ProductivityTrackerSnapshot
{
    /// <summary>When this was written.</summary>
    /// <remarks>
    /// The gap between this and the restore is time the tool existed and nobody accounted for,
    /// and unlike E116 this standard has somewhere to put it. See
    /// <see cref="ProductivityTracker.Restore"/>.
    /// </remarks>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>When the accounting window started.</summary>
    public DateTimeOffset Since { get; init; }

    /// <summary>What the tool was doing.</summary>
    public EquipmentState State { get; init; }

    /// <summary>Why, in the fab's own words.</summary>
    public string StateReason { get; init; } = string.Empty;

    /// <summary>When it entered that state.</summary>
    public DateTimeOffset StateSince { get; init; }

    /// <summary>Time nobody scheduled the tool for.</summary>
    public TimeSpan NonScheduled { get; init; }

    /// <summary>Time it was scheduled and could not work.</summary>
    public TimeSpan UnscheduledDowntime { get; init; }

    /// <summary>Time it was deliberately taken out of production.</summary>
    public TimeSpan ScheduledDowntime { get; init; }

    /// <summary>Time it spent on engineering work.</summary>
    public TimeSpan Engineering { get; init; }

    /// <summary>Time it could have worked and had nothing to do.</summary>
    public TimeSpan Standby { get; init; }

    /// <summary>Time it spent producing.</summary>
    public TimeSpan Productive { get; init; }

    /// <summary>How many times it broke.</summary>
    public int Failures { get; init; }

    /// <summary>How many times it was taken down on purpose.</summary>
    public int ScheduledInterrupts { get; init; }

    /// <summary>How many repairs finished.</summary>
    public int Repairs { get; init; }

    /// <summary>How long those finished repairs took in total.</summary>
    public TimeSpan CompletedRepairTime { get; init; }

    /// <summary>How many cycles it has run.</summary>
    public long Cycles { get; init; }

    /// <summary>How many units it has produced.</summary>
    public long ActualUnits { get; init; }

    /// <summary>How many of those were good.</summary>
    public long EffectiveUnits { get; init; }

    /// <summary>The theoretical time those actual units should have taken.</summary>
    public TimeSpan TheoreticalActualTime { get; init; }

    /// <summary>The theoretical time the good ones should have taken.</summary>
    public TimeSpan TheoreticalEffectiveTime { get; init; }
}

/// <summary>What came back from a snapshot, and where the time the tool was off went.</summary>
public sealed class ProductivityRestoreReport
{
    /// <summary>Whether the snapshot was applied at all.</summary>
    /// <remarks><see langword="false"/> only when nothing was attempted - a tracker that has already accounted for something.</remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>How long the tool was off, between the capture and the restore.</summary>
    public TimeSpan Unaccounted { get; init; }

    /// <summary>Which bucket that time went into.</summary>
    /// <remarks>
    /// <para>
    /// Unlike E116, this is not <see langword="null"/> and not dropped: every E10 ratio has a
    /// denominator that is some sum of the six buckets, so an hour missing from all of them is an
    /// hour that quietly flatters whichever ratio it would have reduced. The six buckets have to
    /// add up to the window, and <see cref="ProductivityTracker.Restore"/> keeps them adding up.
    /// </para>
    /// <para>
    /// Which bucket is the fab's to say, because only the fab knows whether a machine that was off
    /// was broken or simply not wanted. The default is
    /// <see cref="EquipmentState.UnscheduledDowntime"/>, which is the reading that cannot flatter.
    /// </para>
    /// </remarks>
    public EquipmentState UnaccountedChargedTo { get; init; }

    internal static ProductivityRestoreReport Denied(string reason)
        => new() { IsAccepted = false, Reason = reason };
}
