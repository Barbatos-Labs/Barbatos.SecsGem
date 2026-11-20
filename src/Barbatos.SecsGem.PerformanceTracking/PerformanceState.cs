// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.PerformanceTracking;

/// <summary>One module, as something that can be written down and read back.</summary>
/// <remarks>
/// Two different kinds of thing in one record, and the difference is everything on restore: the
/// accumulated times and counts are a <em>ledger</em>, which a restart keeps, and the current
/// state with its task and its reason is a <em>claim about now</em>, which a restart does not.
/// </remarks>
public sealed class PerformanceModuleSnapshot
{
    /// <summary>What the module is called.</summary>
    public required string ModuleId { get; init; }

    /// <summary>What it was doing.</summary>
    /// <remarks>Recorded, and not restored: a restarted machine is not executing anything.</remarks>
    public PerformanceState State { get; init; }

    /// <summary>What it was doing before that.</summary>
    public PerformanceState PreviousState { get; init; }

    /// <summary>When it entered that state.</summary>
    /// <remarks>Recorded, and not restored.</remarks>
    public DateTimeOffset StateSince { get; init; }

    /// <summary>When the accounting below started.</summary>
    public DateTimeOffset AccountingSince { get; init; }

    /// <summary>The task it was running, when it had one.</summary>
    /// <remarks>Recorded, and not restored - a task interrupted by a restart was abandoned, and the count says so.</remarks>
    public string TaskName { get; init; } = string.Empty;

    /// <summary>What kind of task that was.</summary>
    public string TaskType { get; init; } = string.Empty;

    /// <summary>Why it was blocked, when it was.</summary>
    /// <remarks>Recorded, and not restored.</remarks>
    public string BlockedReasonCode { get; init; } = string.Empty;

    /// <summary>What that reason said.</summary>
    public string BlockedReasonText { get; init; } = string.Empty;

    /// <summary>How long it has spent waiting for work.</summary>
    public TimeSpan IdleTotal { get; init; }

    /// <summary>How long it has spent working.</summary>
    public TimeSpan ExecutingTotal { get; init; }

    /// <summary>How long it has spent stopped by something.</summary>
    public TimeSpan BlockedTotal { get; init; }

    /// <summary>How many tasks it has finished.</summary>
    public int TasksCompleted { get; init; }

    /// <summary>How many it has not.</summary>
    public int TasksAbandoned { get; init; }
}

/// <summary>Everything <see cref="PerformanceTracker"/> holds, as one value.</summary>
public sealed class PerformanceTrackerSnapshot
{
    /// <summary>When this was written.</summary>
    /// <remarks>
    /// Needed, and not decoration: the gap between this and the restore is time no module
    /// accounted for, and <see cref="PerformanceRestoreReport.Unaccounted"/> is how a restart
    /// says so instead of charging it to a state that would be a lie.
    /// </remarks>
    public DateTimeOffset CapturedAt { get; init; }

    /// <summary>The modules, ordered by name.</summary>
    public IReadOnlyList<PerformanceModuleSnapshot> Modules { get; init; } = [];
}

/// <summary>What came back from a snapshot, and what a restarted machine was not allowed to claim.</summary>
public sealed class PerformanceRestoreReport
{
    /// <summary>Whether the snapshot was applied at all.</summary>
    /// <remarks><see langword="false"/> only when nothing was attempted - a tracker that has already accounted for something.</remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The modules whose ledgers came back.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>What was not restored as recorded, and why. Keyed by module name.</summary>
    public IReadOnlyDictionary<string, string> Adjusted { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>How long the machine was not accounting for itself.</summary>
    /// <remarks>
    /// <para>
    /// The interval between <see cref="PerformanceTrackerSnapshot.CapturedAt"/> and the restore,
    /// and <strong>it is charged to nothing</strong>. E116 has three states - idle, executing,
    /// blocked - and none of them means "switched off", so putting this time in any of them would
    /// be a figure somebody could act on and should not. It is handed back here instead, for the
    /// application to record wherever it records downtime.
    /// </para>
    /// <para>
    /// A consequence worth knowing rather than discovering: after a restore the three accumulated
    /// times will not add up to <c>now - AccountingSince</c>, by exactly this much. The E116
    /// package already says those two need not agree, and this is the honest reason they do not.
    /// <c>Barbatos.SecsGem.Productivity</c> answers the same question the other way, because E10
    /// does have a state for a tool that was off.
    /// </para>
    /// </remarks>
    public TimeSpan Unaccounted { get; init; }

    /// <summary>Gets how many modules came back.</summary>
    public int Count => Restored.Count;

    internal static PerformanceRestoreReport Denied(string reason)
        => new() { IsAccepted = false, Reason = reason };
}
