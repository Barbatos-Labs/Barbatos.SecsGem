// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Substrates;

/// <summary>One substrate, as something that can be written down and read back.</summary>
/// <remarks>
/// A faithful record: every state a <see cref="Substrate"/> had when it was captured, including
/// the one <see cref="SubstrateTracker.Restore"/> will refuse to claim again. Plain properties
/// with <c>init</c> accessors and no constructor, so a serializer the application already uses
/// round-trips it without this package depending on one.
/// </remarks>
public sealed class SubstrateSnapshot
{
    /// <summary>What the substrate is called.</summary>
    public required string SubstrateId { get; init; }

    /// <summary>Where it was.</summary>
    public string LocationId { get; init; } = string.Empty;

    /// <summary>Where it started.</summary>
    public string SourceLocationId { get; init; } = string.Empty;

    /// <summary>Where it is going.</summary>
    public string DestinationLocationId { get; init; } = string.Empty;

    /// <summary>Where it is on its way through the machine.</summary>
    public SubstrateTransportState TransportState { get; init; }

    /// <summary>How far through being worked on it was.</summary>
    /// <remarks><see cref="SubstrateProcessingState.InProcess"/> is the one state a restart will not claim again.</remarks>
    public SubstrateProcessingState ProcessingState { get; init; }

    /// <summary>Which slot of its carrier it came from.</summary>
    public byte SlotId { get; init; }

    /// <summary>The lot it belongs to.</summary>
    public string LotId { get; init; } = string.Empty;

    /// <summary>What it is for.</summary>
    public string Usage { get; init; } = string.Empty;

    /// <summary>What kind of substrate it is.</summary>
    public string SubstrateType { get; init; } = string.Empty;

    /// <summary>The identifier read off the substrate itself.</summary>
    public string AcquiredId { get; init; } = string.Empty;
}

/// <summary>Everything <see cref="SubstrateTracker"/> holds, as one value.</summary>
public sealed class SubstrateTrackerSnapshot
{
    /// <summary>The locations the machine had declared, ordered.</summary>
    /// <remarks>Names only. Which of them is occupied is worked out again from <see cref="Substrates"/>.</remarks>
    public IReadOnlyList<string> Locations { get; init; } = [];

    /// <summary>The substrates.</summary>
    public IReadOnlyList<SubstrateSnapshot> Substrates { get; init; } = [];
}

/// <summary>What came back from a snapshot, and what a restarted machine was not allowed to claim.</summary>
public sealed class SubstrateRestoreReport
{
    /// <summary>Whether the snapshot was applied at all.</summary>
    /// <remarks><see langword="false"/> only when nothing was attempted - a tracker that already has substrates.</remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The substrates that came back.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>What was not restored as recorded, and why. Keyed by substrate identifier.</summary>
    public IReadOnlyDictionary<string, string> Adjusted { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets how many substrates came back.</summary>
    public int Count => Restored.Count;

    internal static SubstrateRestoreReport Denied(string reason)
        => new() { IsAccepted = false, Reason = reason };
}
