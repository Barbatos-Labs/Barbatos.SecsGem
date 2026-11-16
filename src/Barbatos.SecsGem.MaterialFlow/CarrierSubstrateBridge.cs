// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Carriers;
using Barbatos.SecsGem.Substrates;

namespace Barbatos.SecsGem.MaterialFlow;

/// <summary>
/// Turns a carrier's verified slot map into tracked substrates, and forgets them again.
/// </summary>
/// <remarks>
/// <para>
/// <strong>What this is, and why it is not in either package.</strong> E87 reads a slot map: one
/// byte a slot, the codes the machine's own, <c>SLOTMAP</c> with no dictionary entry anywhere.
/// E90 tracks substrates: one object a wafer, at a location, with a state. The two standards do
/// not refer to each other, and the E87 and E90 packages here do not either - neither depends on
/// the other, which is what lets a machine implement one without the other.
/// </para>
/// <para>
/// So the join lives here, in the one package that depends on both. It is a convention rather than
/// a rule, and the whole of it is in <see cref="CarrierSubstrateOptions"/>: what a slot and its
/// wafer are called, which byte means occupied, whether the wafer goes back where it came from.
/// The E87 and E90 READMEs say correlating the two is the application's business, and that is
/// still true - this package is one application's answer, written down and tested, for the many
/// fabs whose answer is the same.
/// </para>
/// <para>
/// <strong>The rule it enforces.</strong> Substrates are only created from a slot map the host has
/// agreed - <see cref="CarrierSlotMapStatus.SlotMapVerificationOk"/>. A map that is unread,
/// waiting or refused is not a bill of materials, and tracking wafers from one would have the
/// machine believing in wafers nobody confirmed. That is the whole of E87's reason for having a
/// slot map state machine, carried over to this side of the join.
/// </para>
/// <para>
/// <strong>Locks.</strong> This class holds no lock of its own; it calls two managers that each
/// hold theirs. With <see cref="Attach"/> it runs inside the carrier manager's lock - its event
/// handler calls into the substrate tracker - so the order is always carriers, then substrates,
/// and it never calls back the other way. An application that adds its own handlers should keep
/// that order, because two locks taken in two orders is the one deadlock a stack like this can
/// build for itself.
/// </para>
/// </remarks>
public sealed class CarrierSubstrateBridge
{
    private readonly CarrierManager _carriers;
    private readonly SubstrateTracker _substrates;
    private readonly Dictionary<string, List<CarrierSubstrateEntry>> _open = new(StringComparer.Ordinal);
#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif
    private EventHandler<CarrierStateChangedEventArgs>? _handler;

    /// <summary>Joins a carrier manager to a substrate tracker.</summary>
    /// <param name="carriers">E87's state machines.</param>
    /// <param name="substrates">E90's state machines.</param>
    /// <param name="options">The convention. <see langword="null"/> takes the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="carriers"/> or <paramref name="substrates"/> is <see langword="null"/>.</exception>
    public CarrierSubstrateBridge(
        CarrierManager carriers,
        SubstrateTracker substrates,
        CarrierSubstrateOptions? options = null)
    {
        _carriers = carriers ?? throw new ArgumentNullException(nameof(carriers));
        _substrates = substrates ?? throw new ArgumentNullException(nameof(substrates));
        Options = options ?? new CarrierSubstrateOptions();
    }

    /// <summary>Raised after a carrier's substrates are created.</summary>
    /// <remarks>
    /// Raised on whichever thread did the work, and when <see cref="Attach"/> is in use that is
    /// inside the carrier manager's lock. Queue the work.
    /// </remarks>
    public event EventHandler<CarrierSubstrateEventArgs>? Opened;

    /// <summary>Raised after a carrier's substrates are forgotten.</summary>
    public event EventHandler<CarrierSubstrateEventArgs>? Closed;

    /// <summary>Gets the convention this bridge follows.</summary>
    public CarrierSubstrateOptions Options { get; }

    /// <summary>Gets the carriers whose substrates this bridge created and has not forgotten.</summary>
    public IReadOnlyList<string> OpenCarriers
    {
        get
        {
            lock (_gate)
            {
                return [.. _open.Keys];
            }
        }
    }

    /// <summary>Gets a value indicating whether this bridge is listening to the carrier manager.</summary>
    public bool IsAttached => _handler is not null;

    /// <summary>Listens to the carrier manager, so a verified slot map creates substrates by itself.</summary>
    /// <remarks>
    /// Attaching twice does nothing the second time. What it listens for is in
    /// <see cref="CarrierSubstrateOptions.OpensWhenSlotMapVerified"/> and
    /// <see cref="CarrierSubstrateOptions.ClosesWhenCarrierFinished"/>; with both off it listens
    /// and does nothing, which is a legitimate way to use <see cref="Open"/> by hand.
    /// </remarks>
    public void Attach()
    {
        lock (_gate)
        {
            if (_handler is not null)
            {
                return;
            }

            _handler = OnCarrierStateChanged;
            _carriers.CarrierStateChanged += _handler;
        }
    }

    /// <summary>Stops listening.</summary>
    /// <remarks>What it already created stays created: a bridge that forgot a machine's wafers on being detached would be a worse surprise than one that did not.</remarks>
    public void Detach()
    {
        lock (_gate)
        {
            if (_handler is null)
            {
                return;
            }

            _carriers.CarrierStateChanged -= _handler;
            _handler = null;
        }
    }

    /// <summary>Gets the substrates this bridge created for a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <returns>The entries, or an empty list for a carrier it has not opened.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    public IReadOnlyList<CarrierSubstrateEntry> SubstratesOf(string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            return _open.TryGetValue(carrierId, out var entries) ? [.. entries] : [];
        }
    }

    /// <summary>Creates one substrate per occupied slot of a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <returns>What was created, and which slots were refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// Refused outright for a carrier the manager does not have, for a slot map the host has not
    /// agreed, and for a carrier this bridge has already opened - that last one because opening
    /// twice would either collide with its own substrates or quietly track a wafer twice.
    /// </para>
    /// <para>
    /// The slot locations are declared as they are needed, so an application does not have to
    /// know a carrier's capacity up front. A slot the tracker refuses is reported and the rest go
    /// on: a carrier where one slot is already tracked is still twenty-four wafers a machine needs
    /// to know about.
    /// </para>
    /// </remarks>
    public CarrierSubstrateResult Open(string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        if (_carriers.FindCarrier(carrierId) is not { } carrier)
        {
            return CarrierSubstrateResult.Denied(carrierId, $"there is no carrier '{carrierId}'.");
        }

        if (carrier.SlotMapStatus is not CarrierSlotMapStatus.SlotMapVerificationOk)
        {
            return CarrierSubstrateResult.Denied(
                carrierId,
                $"carrier '{carrierId}' is {carrier.SlotMapStatus}; substrates are only created from a "
                + $"{CarrierSlotMapStatus.SlotMapVerificationOk} slot map.");
        }

        lock (_gate)
        {
            if (_open.ContainsKey(carrierId))
            {
                return CarrierSubstrateResult.Denied(
                    carrierId,
                    $"carrier '{carrierId}' already has {_open[carrierId].Count} substrate(s) from this bridge.");
            }

            List<CarrierSubstrateEntry> created = [];
            Dictionary<byte, string> refused = [];
            var map = carrier.SlotMap.Span;

            for (var index = 0; index < map.Length; index++)
            {
                if (!Options.IsOccupied(map[index]))
                {
                    continue;
                }

                // Counting from one, because a slot map's first byte is slot one everywhere in
                // E87 and in every fab that ever printed a carrier label.
                var slot = (byte)Math.Min(index + 1, byte.MaxValue);
                var locationId = Options.SlotLocationName(carrierId, slot);
                var substrateId = Options.SubstrateName(carrierId, slot);

                if (locationId.Length == 0 || substrateId.Length == 0)
                {
                    refused[slot] = "the naming convention gave an empty name.";
                    continue;
                }

                _substrates.AddLocation(locationId);

                var added = _substrates.Add(substrateId, locationId, slot);

                if (!added.IsAccepted)
                {
                    refused[slot] = added.Reason;
                    continue;
                }

                var described = Describe(carrier, substrateId, locationId);

                if (!described.IsAccepted)
                {
                    // The substrate exists and could not be labelled, which is worth saying rather
                    // than hiding: a wafer with no lot identifier is still a wafer the machine is
                    // tracking, and removing it would lose that.
                    refused[slot] = described.Reason;
                }

                created.Add(new CarrierSubstrateEntry
                {
                    Slot = slot,
                    SubstrateId = substrateId,
                    LocationId = locationId,
                });
            }

            if (created.Count > 0)
            {
                _open[carrierId] = created;
            }

            var result = new CarrierSubstrateResult
            {
                IsAccepted = true,
                CarrierId = carrierId,
                Substrates = created,
                Refused = refused,
            };

            Opened?.Invoke(this, new CarrierSubstrateEventArgs { Result = result });

            return result;
        }
    }

    /// <summary>Forgets the substrates this bridge created for a carrier.</summary>
    /// <param name="carrierId">Which carrier.</param>
    /// <returns>What was forgotten, and which slots were refused.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="carrierId"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// E90 only forgets a substrate that is at its destination or lost, so a carrier whose wafers
    /// are still being worked on comes back with every slot refused and nothing changed. Those
    /// stay this bridge's, so the call can be made again when they get there - which is why a
    /// close that forgets nothing is still accepted.
    /// </remarks>
    public CarrierSubstrateResult Close(string carrierId)
    {
        ArgumentNullException.ThrowIfNull(carrierId);

        lock (_gate)
        {
            if (!_open.TryGetValue(carrierId, out var entries))
            {
                return CarrierSubstrateResult.Denied(
                    carrierId,
                    $"this bridge has no substrates for carrier '{carrierId}'.");
            }

            List<CarrierSubstrateEntry> removed = [];
            Dictionary<byte, string> refused = [];

            foreach (var entry in entries)
            {
                var result = _substrates.Remove(entry.SubstrateId);

                if (result.IsAccepted)
                {
                    removed.Add(entry);
                }
                else
                {
                    refused[entry.Slot] = result.Reason;
                }
            }

            foreach (var entry in removed)
            {
                entries.Remove(entry);
            }

            if (entries.Count == 0)
            {
                _open.Remove(carrierId);
            }

            var answer = new CarrierSubstrateResult
            {
                IsAccepted = true,
                CarrierId = carrierId,
                Substrates = removed,
                Refused = refused,
            };

            Closed?.Invoke(this, new CarrierSubstrateEventArgs { Result = answer });

            return answer;
        }
    }

    private SubstrateResult Describe(Carrier carrier, string substrateId, string locationId)
        => _substrates.Describe(
            substrateId,
            lotId: Options.CopiesLotId ? carrier.LotId : null,
            usage: Options.CopiesUsage ? carrier.Usage : null,
            substrateType: Options.SubstrateType.Length > 0 ? Options.SubstrateType : null,

            // Its own slot, which is where a wafer usually goes back to - and a destination is
            // where it is going, so this does not put it there.
            destinationLocationId: Options.ReturnsToItsOwnSlot ? locationId : null);

    private void OnCarrierStateChanged(object? sender, CarrierStateChangedEventArgs arguments)
    {
        switch (arguments.StateMachine)
        {
            // Every slot map transition asks, and Open decides: the rule that only a verified map
            // becomes substrates lives there, in one place. Checking the status here as well
            // looked tidier and was a second copy of the same rule - with the odd property that
            // breaking it changed nothing a test could see, which is how it was noticed.
            case CarrierStateMachine.SlotMapStatus when Options.OpensWhenSlotMapVerified:
                Open(arguments.Carrier.CarrierId);
                break;

            // Both endings, because both mean the machine has let go: a carrier that was stopped
            // part way has wafers somewhere, and whether they can be forgotten is E90's answer
            // rather than this bridge's.
            case CarrierStateMachine.AccessStatus
                when Options.ClosesWhenCarrierFinished
                    && (CarrierAccessStatus)arguments.Current
                        is CarrierAccessStatus.CarrierComplete or CarrierAccessStatus.CarrierStopped:
                Close(arguments.Carrier.CarrierId);
                break;

            default:
                break;
        }
    }
}
