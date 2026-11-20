// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ProcessJobs;

/// <summary>One process job, as something that can be written down and read back.</summary>
/// <remarks>
/// A faithful record: the state the job really had when it was captured, including the ones
/// <see cref="ProcessJobManager.Restore"/> will not claim again. Plain properties with <c>init</c>
/// accessors and no constructor, so a serializer the application already uses round-trips it
/// without this package depending on one.
/// </remarks>
public sealed class ProcessJobSnapshot
{
    /// <summary>What the job is called.</summary>
    public required string ProcessJobId { get; init; }

    /// <summary>Where it was in its life.</summary>
    public ProcessJobState State { get; init; }

    /// <summary>How it ended, or <see langword="null"/> while it had not.</summary>
    public ProcessJobCompletion? Completion { get; init; }

    /// <summary>The recipe it runs.</summary>
    public string RecipeId { get; init; } = string.Empty;

    /// <summary>How the recipe is specified.</summary>
    public ProcessJobRecipeMethod RecipeMethod { get; init; } = ProcessJobRecipeMethod.WithoutVariableTuning;

    /// <summary>Whether it starts itself once its material is ready.</summary>
    public bool StartsAutomatically { get; init; } = true;

    /// <summary>The order its material is to be worked through in.</summary>
    public ProcessJobMaterialOrder MaterialOrder { get; init; } = ProcessJobMaterialOrder.ArrivalSequence;

    /// <summary>The event that pauses it, when a host named one.</summary>
    public uint? PauseEventId { get; init; }

    /// <summary>The material it is for.</summary>
    public IReadOnlyList<string> Material { get; init; } = [];

    /// <summary>The recipe parameters a host set.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Everything <see cref="ProcessJobManager"/> holds, as one value.</summary>
public sealed class ProcessJobManagerSnapshot
{
    /// <summary>The jobs.</summary>
    public IReadOnlyList<ProcessJobSnapshot> Jobs { get; init; } = [];

    /// <summary>The queue, in order.</summary>
    /// <remarks>E40 lets a host reorder this, so the order is a host instruction and comes back.</remarks>
    public IReadOnlyList<string> Queue { get; init; } = [];
}

/// <summary>What came back from a snapshot, and what a restarted machine was not allowed to claim.</summary>
public sealed class ProcessJobRestoreReport
{
    /// <summary>Whether the snapshot was applied at all.</summary>
    /// <remarks><see langword="false"/> only when nothing was attempted - a manager that already has jobs.</remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The jobs that came back.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>What was not restored as recorded, and why. Keyed by process job identifier.</summary>
    public IReadOnlyDictionary<string, string> Adjusted { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets how many jobs came back.</summary>
    public int Count => Restored.Count;

    internal static ProcessJobRestoreReport Denied(string reason)
        => new() { IsAccepted = false, Reason = reason };
}
