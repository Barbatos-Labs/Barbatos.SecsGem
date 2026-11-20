// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.ControlJobs;

/// <summary>One control job, as something that can be written down and read back.</summary>
/// <remarks>
/// A faithful record: the state the job really had when it was captured, including the ones
/// <see cref="ControlJobManager.Restore"/> will not claim again. Plain properties with <c>init</c>
/// accessors and no constructor, so a serializer the application already uses round-trips it
/// without this package depending on one.
/// </remarks>
public sealed class ControlJobSnapshot
{
    /// <summary>What the job is called.</summary>
    public required string ControlJobId { get; init; }

    /// <summary>Where it was in its life.</summary>
    public ControlJobState State { get; init; }

    /// <summary>How it ended, or <see langword="null"/> while it had not.</summary>
    public ControlJobCompletion? Completion { get; init; }

    /// <summary>The material it is for.</summary>
    public IReadOnlyList<string> Carriers { get; init; } = [];

    /// <summary>Who starts it once its material is ready.</summary>
    public ControlJobStartMethod StartMethod { get; init; }

    /// <summary>How the material is to be worked through.</summary>
    public string ProcessOrder { get; init; } = string.Empty;

    /// <summary>What recipe it carries.</summary>
    public string ProcessingSpecification { get; init; } = string.Empty;
}

/// <summary>Everything <see cref="ControlJobManager"/> holds, as one value.</summary>
public sealed class ControlJobManagerSnapshot
{
    /// <summary>The jobs.</summary>
    public IReadOnlyList<ControlJobSnapshot> Jobs { get; init; } = [];

    /// <summary>The queue, in order.</summary>
    /// <remarks>
    /// The order a host asked for, which is the whole reason a queue is worth writing down: a
    /// machine that came back with its lots in a different order would run the wrong one next.
    /// </remarks>
    public IReadOnlyList<string> Queue { get; init; } = [];
}

/// <summary>What came back from a snapshot, and what a restarted machine was not allowed to claim.</summary>
public sealed class ControlJobRestoreReport
{
    /// <summary>Whether the snapshot was applied at all.</summary>
    /// <remarks><see langword="false"/> only when nothing was attempted - a manager that already has jobs.</remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The jobs that came back.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>What was not restored as recorded, and why. Keyed by control job identifier.</summary>
    public IReadOnlyDictionary<string, string> Adjusted { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets how many jobs came back.</summary>
    public int Count => Restored.Count;

    internal static ControlJobRestoreReport Denied(string reason)
        => new() { IsAccepted = false, Reason = reason };
}
