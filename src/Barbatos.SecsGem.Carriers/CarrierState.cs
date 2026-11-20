// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

namespace Barbatos.SecsGem.Carriers;

/// <summary>
/// One carrier, as something that can be written down and read back.
/// </summary>
/// <remarks>
/// <para>
/// A faithful record: every state a <see cref="Carrier"/> had when it was captured, including the
/// ones <see cref="CarrierManager.Restore"/> will refuse to claim again. That is on purpose. The
/// file says what was true; the library decides what a restarted machine is entitled to say it
/// knows, and keeping those two apart is what makes the second one testable.
/// </para>
/// <para>
/// Plain properties with <c>init</c> accessors and no constructor, so
/// <c>System.Text.Json</c> - or whatever the application already stores things with - round-trips
/// it without this package taking a dependency on a serializer. Persistence is the application's
/// choice, the same way its container is.
/// </para>
/// </remarks>
public sealed class CarrierSnapshot
{
    /// <summary>What the carrier is called.</summary>
    public required string CarrierId { get; init; }

    /// <summary>Whether its identifier had been read and believed.</summary>
    /// <remarks>Recorded, and not restored: see <see cref="CarrierManager.Restore"/>.</remarks>
    public CarrierIdStatus IdStatus { get; init; }

    /// <summary>Whether its slot map had been read and believed.</summary>
    /// <remarks>Recorded, and not restored.</remarks>
    public CarrierSlotMapStatus SlotMapStatus { get; init; }

    /// <summary>How far through it the equipment had got.</summary>
    public CarrierAccessStatus AccessStatus { get; init; }

    /// <summary>Which load port it was at, or <see langword="null"/>.</summary>
    /// <remarks>Recorded, and not restored: where a box is sitting is something a machine senses.</remarks>
    public byte? PortId { get; init; }

    /// <summary>Which storage location it was in, or <see langword="null"/>.</summary>
    /// <remarks>Recorded, and not restored, for the same reason <see cref="PortId"/> is not.</remarks>
    public uint? StorageLocation { get; init; }

    /// <summary>What was in each slot.</summary>
    /// <remarks>
    /// Recorded, and not restored. A list rather than an array, so that reading this property
    /// twice cannot hand two callers the same mutable buffer - the snapshot is a record and
    /// nothing should be able to edit it after the fact.
    /// </remarks>
    public IReadOnlyList<byte> SlotMap { get; init; } = [];

    /// <summary>How many slots it has.</summary>
    public byte Capacity { get; init; }

    /// <summary>How many substrates were in it.</summary>
    /// <remarks>Recorded, and not restored: it is read off the slot map, and the slot map is not.</remarks>
    public byte SubstrateCount { get; init; }

    /// <summary>The lot it belongs to.</summary>
    public string LotId { get; init; } = string.Empty;

    /// <summary>What it is for.</summary>
    public string Usage { get; init; } = string.Empty;
}

/// <summary>One load port, as something that can be written down and read back.</summary>
/// <remarks>As with <see cref="CarrierSnapshot"/>, a faithful record rather than a filtered one.</remarks>
public sealed class LoadPortSnapshot
{
    /// <summary>Which port.</summary>
    public required byte PortId { get; init; }

    /// <summary>What it was ready to do.</summary>
    /// <remarks>
    /// Only the in-service half of this is restored. Whether a port is in service is a host
    /// instruction; whether a transfer may happen this second is not.
    /// </remarks>
    public PortTransferState TransferState { get; init; }

    /// <summary>Who drives it.</summary>
    public PortAccessMode AccessMode { get; init; }

    /// <summary>Whether a carrier belonged to it.</summary>
    /// <remarks>Recorded, and not restored.</remarks>
    public PortAssociationState AssociationState { get; init; }

    /// <summary>Whether it was held for a carrier.</summary>
    public PortReservationState ReservationState { get; init; }

    /// <summary>The carrier that belonged to it.</summary>
    /// <remarks>Recorded, and not restored.</remarks>
    public string? AssociatedCarrierId { get; init; }

    /// <summary>The carrier it was held for.</summary>
    public string? ReservedForCarrierId { get; init; }
}

/// <summary>One storage location, as something that can be written down and read back.</summary>
/// <remarks>
/// A faithful record, as the other two are. One state machine, so one state: see
/// <see cref="CarrierStorageLocation"/> for why a location has fewer of them than a port.
/// </remarks>
public sealed class CarrierStorageSnapshot
{
    /// <summary>Which location - <c>LOC</c>.</summary>
    public required uint Location { get; init; }

    /// <summary>What it was ready to do.</summary>
    /// <remarks>
    /// Only the in-service half of this is restored, exactly as for a port: whether a shelf is
    /// usable at all is a decision somebody made, and whether a transfer may happen this second
    /// is not.
    /// </remarks>
    public PortTransferState TransferState { get; init; }

    /// <summary>The carrier that was in it, or <see langword="null"/>.</summary>
    /// <remarks>Recorded, and not restored: what is in a storage location is sensed.</remarks>
    public string? CarrierId { get; init; }
}

/// <summary>
/// Everything <see cref="CarrierManager"/> holds, as one value.
/// </summary>
/// <remarks>
/// Written by <see cref="CarrierManager.Capture"/> and read by
/// <see cref="CarrierManager.Restore"/>. What an application does with it in between - a file, a
/// row, a key in a cache - is its own business, and this package has no opinion and no
/// dependency on one.
/// </remarks>
public sealed class CarrierManagerSnapshot
{
    /// <summary>The load ports, ordered by number.</summary>
    public IReadOnlyList<LoadPortSnapshot> Ports { get; init; } = [];

    /// <summary>The storage locations, ordered by <c>LOC</c>.</summary>
    public IReadOnlyList<CarrierStorageSnapshot> StorageLocations { get; init; } = [];

    /// <summary>The carriers.</summary>
    public IReadOnlyList<CarrierSnapshot> Carriers { get; init; } = [];
}

/// <summary>
/// What came back from a snapshot, and what a restarted machine was not allowed to claim.
/// </summary>
/// <remarks>
/// The second list is the useful one, and the reason this is a report rather than a
/// <see langword="void"/>. A machine that reboots mid-lot has to tell a host something, and
/// "CAR-001 was in access, so it comes back stopped" is the sentence somebody acts on.
/// </remarks>
public sealed class CarrierRestoreReport
{
    /// <summary>Whether the snapshot was applied at all.</summary>
    /// <remarks>
    /// <see langword="false"/> only when nothing was attempted - a manager that already has
    /// carriers. Per-item adjustments do not make a restore a failure; that is what
    /// <see cref="Adjusted"/> is for.
    /// </remarks>
    public required bool IsAccepted { get; init; }

    /// <summary>Why nothing was attempted. Empty when something was.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The carriers, ports and storage locations that came back, named.</summary>
    public IReadOnlyList<string> Restored { get; init; } = [];

    /// <summary>What was not restored as recorded, and why.</summary>
    /// <remarks>
    /// Keyed by carrier identifier, by <c>port 1</c> or by <c>location 101</c>. Every entry is a rule in
    /// <see cref="CarrierManager.Restore"/> doing its job, not a problem with the snapshot.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Adjusted { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets how many carriers, ports and storage locations came back.</summary>
    public int Count => Restored.Count;

    internal static CarrierRestoreReport Denied(string reason)
        => new() { IsAccepted = false, Reason = reason };
}
