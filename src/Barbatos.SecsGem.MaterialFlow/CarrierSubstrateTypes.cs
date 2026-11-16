// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Globalization;

namespace Barbatos.SecsGem.MaterialFlow;

/// <summary>
/// How a carrier's slot map becomes substrates: one convention, written down where it can be
/// replaced.
/// </summary>
/// <remarks>
/// <para>
/// E87 says what the machine read off a carrier. E90 says what the machine is tracking. Neither
/// says how one becomes the other - not the names, not which byte means an occupied slot, not
/// whether a wafer goes back where it came from. The E87 and E90 packages say so and leave the
/// join to the application, which is correct and is also a loop every application then writes.
/// </para>
/// <para>
/// This class is that loop's decisions, each one a property rather than a line of code somebody
/// has to read: the defaults are what this repository's own sample used, and a fab that names its
/// slots differently sets two delegates instead of rewriting the loop.
/// </para>
/// </remarks>
public sealed class CarrierSubstrateOptions
{
    /// <summary>What a slot of a carrier is called as a substrate location.</summary>
    /// <remarks>
    /// Default: <c>CAR-001-SLOT-03</c>. E90 leaves location naming to the equipment, so this is a
    /// convention and not a rule - and the locations are declared as they are needed, because
    /// declaring a location twice is harmless.
    /// </remarks>
    public Func<string, byte, string> SlotLocationName { get; set; }
        = (carrierId, slot) => $"{carrierId}-SLOT-{slot.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>What the substrate in a slot is called.</summary>
    /// <remarks>
    /// Default: <c>CAR-001-03</c>. A fab that scribes its wafers reads the identifier off the
    /// wafer instead and sets it with <c>SubstrateTracker.Describe</c> as an acquired identifier -
    /// E90 keeps those two apart for a reason.
    /// </remarks>
    public Func<string, byte, string> SubstrateName { get; set; }
        = (carrierId, slot) => $"{carrierId}-{slot.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>Whether a slot map byte means there is a substrate in that slot.</summary>
    /// <remarks>
    /// Default: anything but zero, which is what the E87 package itself counts when it works out
    /// <c>SubstrateCount</c>, and the only reading this stack takes. <c>SLOTMAP</c> has no
    /// dictionary entry at all, so the codes belong to the equipment - a fab whose mapper reports
    /// zero for a present wafer changes this one line rather than its mapper.
    /// </remarks>
    public Func<byte, bool> IsOccupied { get; set; } = code => code != 0;

    /// <summary>Whether a substrate's destination is set to the slot it came out of.</summary>
    /// <remarks>
    /// <para>
    /// Default <see langword="true"/>, because a carrier in is usually the carrier out: the wafer
    /// goes back where it came from, and saying so up front is what lets E90 refuse a move after
    /// it gets there. A machine that sorts wafers into other carriers leaves this off and sets
    /// each destination itself.
    /// </para>
    /// <para>
    /// It does not make the substrate <see cref="Barbatos.SecsGem.Substrates.SubstrateTransportState.AtDestination"/>
    /// on the spot: a destination is where it is going, and the transport state follows a move
    /// rather than a description.
    /// </para>
    /// </remarks>
    public bool ReturnsToItsOwnSlot { get; set; } = true;

    /// <summary>Whether the carrier's lot identifier is copied onto each substrate.</summary>
    /// <remarks>Default <see langword="true"/>: E90 has a <c>LotID</c> per substrate and a carrier usually is one lot.</remarks>
    public bool CopiesLotId { get; set; } = true;

    /// <summary>Whether the carrier's usage is copied onto each substrate.</summary>
    /// <remarks>Default <see langword="true"/>. Both standards spell it <c>Usage</c> and mean the same thing by it: production, engineering, a dummy.</remarks>
    public bool CopiesUsage { get; set; } = true;

    /// <summary>What kind of substrate these are, in the fab's own words.</summary>
    /// <remarks>
    /// Empty by default, and deliberately not <c>"Wafer"</c>: E90 does not define the vocabulary,
    /// and a package that guessed would put its guess in a host's reports. A fab that has a word
    /// sets it here.
    /// </remarks>
    public string SubstrateType { get; set; } = string.Empty;

    /// <summary>Whether a verified slot map creates the substrates by itself.</summary>
    /// <remarks>
    /// Default <see langword="true"/>, and the one trigger in E87 that is unambiguous: a map the
    /// host has agreed is a bill of materials, and nothing before that is.
    /// </remarks>
    public bool OpensWhenSlotMapVerified { get; set; } = true;

    /// <summary>Whether a carrier reaching an ending forgets its substrates.</summary>
    /// <remarks>
    /// Default <see langword="false"/>, which is the cautious way round. A finished carrier still
    /// has its records, and plenty of applications want them - a history, a report, a lot
    /// genealogy. Turn this on when nothing downstream needs them, and the substrates still have
    /// to be somewhere E90 allows forgetting from: at their destination, or lost.
    /// </remarks>
    public bool ClosesWhenCarrierFinished { get; set; }
}

/// <summary>What happened to one slot.</summary>
public sealed class CarrierSubstrateEntry
{
    /// <summary>Which slot, counting from one.</summary>
    public required byte Slot { get; init; }

    /// <summary>What the substrate is called.</summary>
    public required string SubstrateId { get; init; }

    /// <summary>Where it is, or was.</summary>
    public required string LocationId { get; init; }
}

/// <summary>What happened to a carrier's worth of substrates.</summary>
/// <remarks>
/// A list of what was done and a list of what was not, rather than one boolean: a slot the tracker
/// refused is a real event with a reason, and a carrier where twenty-four slots worked and one did
/// not is the case this type exists for.
/// </remarks>
public sealed class CarrierSubstrateResult
{
    /// <summary>Whether the carrier itself could be dealt with.</summary>
    /// <remarks>
    /// <see langword="false"/> only when nothing was attempted - no such carrier, a slot map the
    /// host has not agreed, a carrier already open. A carrier where some slots were refused is
    /// still accepted, and <see cref="Refused"/> says which.
    /// </remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when it was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Which carrier.</summary>
    public required string CarrierId { get; init; }

    /// <summary>The substrates created, or removed.</summary>
    public IReadOnlyList<CarrierSubstrateEntry> Substrates { get; init; } = [];

    /// <summary>The slots that were refused, and why.</summary>
    /// <remarks>
    /// Keyed by slot. The reason is the tracker's own - a location already occupied, a substrate
    /// already tracked under that name, a substrate not yet at its destination - and nothing is
    /// rolled back to tidy it up: a wafer already tracked under that name is somebody else's
    /// wafer, and removing it to make room would be worse than saying so.
    /// </remarks>
    public IReadOnlyDictionary<byte, string> Refused { get; init; }
        = new Dictionary<byte, string>();

    /// <summary>Gets how many substrates were created, or removed.</summary>
    public int Count => Substrates.Count;

    internal static CarrierSubstrateResult Denied(string carrierId, string reason)
        => new() { IsAccepted = false, CarrierId = carrierId, Reason = reason };
}

/// <summary>A carrier's substrates were created, or forgotten.</summary>
public sealed class CarrierSubstrateEventArgs : EventArgs
{
    /// <summary>What happened.</summary>
    public required CarrierSubstrateResult Result { get; init; }
}
