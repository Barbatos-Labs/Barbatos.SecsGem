// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.WaferMap;

/// <summary>
/// Where the host side keeps wafer maps, and where arriving ones are handed to.
/// </summary>
/// <remarks>
/// <para>
/// Nine methods, and between them they are the whole of what a host does in stream 12: three save
/// what the equipment sent, three hand back what the equipment asked for, one takes setup data, one
/// hands setup data out, and one decides whether a map may be sent at all.
/// </para>
/// <para>
/// <strong>This is the arrival point, not a notification.</strong> There is no event for "a map
/// came in" beside these methods, deliberately: an application that cares about a map needs the
/// map, and a store method already has it, can refuse it, and is awaited before the
/// acknowledgement goes back - none of which an event raised beside the transaction could do.
/// </para>
/// <para>
/// Every method is asked inside a transaction the equipment is waiting on, so none of them should
/// take long. Writing a quarter of a million bins to a database while a prober holds its T3 open
/// is how a map transfer times out; queue the work and acknowledge.
/// </para>
/// </remarks>
public interface IWaferMapStore
{
    /// <summary>Takes map setup data the equipment sent - S12F1.</summary>
    /// <param name="setup">The setup.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    ValueTask<WaferMapSetupAcknowledgement> SaveSetupAsync(WaferMapSetup setup, CancellationToken cancellationToken);

    /// <summary>Hands out map setup data the equipment asked for - S12F3.</summary>
    /// <param name="request">What is wanted, and the conventions the equipment wants it in.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The setup, or <see langword="null"/> when there is none for that material.</returns>
    /// <remarks><see langword="null"/> is answered with the zero-length list E5 uses for "no data", not with a refusal: S12F4 has no code to carry one.</remarks>
    ValueTask<WaferMapSetup?> LoadSetupAsync(WaferMapSetupRequest request, CancellationToken cancellationToken);

    /// <summary>Decides whether the equipment may send a map - S12F5.</summary>
    /// <param name="inquiry">What is about to be sent, and how large it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant.</returns>
    /// <remarks>This is the cheap place to say no. After the grant, the next message is the whole map.</remarks>
    ValueTask<WaferMapTransmitGrant> InquireTransmitAsync(WaferMapTransmitInquiry inquiry, CancellationToken cancellationToken);

    /// <summary>Takes a map sent as rows - S12F7.</summary>
    /// <param name="data">The map.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    ValueTask<WaferMapDataAcknowledgement> SaveRowDataAsync(WaferMapRowData data, CancellationToken cancellationToken);

    /// <summary>Takes a map sent as one run of bins - S12F9.</summary>
    /// <param name="data">The map.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    ValueTask<WaferMapDataAcknowledgement> SaveArrayDataAsync(WaferMapArrayData data, CancellationToken cancellationToken);

    /// <summary>Takes a map sent as coordinates - S12F11.</summary>
    /// <param name="data">The map.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    ValueTask<WaferMapDataAcknowledgement> SavePointDataAsync(WaferMapPointData data, CancellationToken cancellationToken);

    /// <summary>Hands back a map as rows - S12F13.</summary>
    /// <param name="request">What is wanted.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The map, or <see langword="null"/> when there is none for that material.</returns>
    ValueTask<WaferMapRowData?> LoadRowDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken);

    /// <summary>Hands back a map as one run of bins - S12F15.</summary>
    /// <param name="request">What is wanted.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The map, or <see langword="null"/> when there is none for that material.</returns>
    ValueTask<WaferMapArrayData?> LoadArrayDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken);

    /// <summary>Hands back a map as coordinates - S12F17.</summary>
    /// <param name="request">What is wanted, including whether null locations are wanted with it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The map, or <see langword="null"/> when there is none for that material.</returns>
    /// <remarks>
    /// The only load that is asked a question beyond which material:
    /// <see cref="WaferMapBinSelection.NonNullOnly"/> asks for the locations that hold a die and
    /// nothing else.
    /// </remarks>
    ValueTask<WaferMapPointData?> LoadPointDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// A wafer map store that keeps everything in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or a host that passes maps straight on to something else.
/// Anything that must survive a restart wants its own store.
/// </para>
/// <para>
/// <strong>It does not convert between the three formats.</strong> A map saved as rows is handed
/// back by <see cref="LoadRowDataAsync"/> and not by <see cref="LoadArrayDataAsync"/>, which
/// answers <see langword="null"/> until an array map is saved for that material. Converting would
/// mean inventing the row and column counts for locations nobody sent, and a store is the wrong
/// place to guess at the shape of a wafer.
/// </para>
/// <para>
/// Maps are keyed by material identifier, compared exactly: <c>W01</c> and <c>w01</c> are two
/// wafers, because to a fab they are.
/// </para>
/// </remarks>
public sealed class WaferMapInMemoryStore : IWaferMapStore
{
    private readonly ConcurrentDictionary<string, WaferMapSetup> _setups = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WaferMapRowData> _rows = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WaferMapArrayData> _arrays = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WaferMapPointData> _points = new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many materials may be held at once.</summary>
    /// <remarks>
    /// A refused save is a refusal the equipment can see - <see cref="WaferMapDataAcknowledgement.Rejected"/> -
    /// rather than a host that quietly grows until the process dies.
    /// </remarks>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many materials have a map or a setup held for them.</summary>
    public int Count => _setups.Count + _rows.Count + _arrays.Count + _points.Count;

    /// <summary>Gets or sets whether a transmit inquire is granted.</summary>
    /// <remarks>
    /// Granted by default. A simulator that needs to exercise the other end of the handshake sets
    /// this to <see cref="WaferMapTransmitGrant.Busy"/> and gets a prober that has to wait, which
    /// is the one path a map transfer never takes until the day it does.
    /// </remarks>
    public WaferMapTransmitGrant Grant { get; set; } = WaferMapTransmitGrant.Granted;

    /// <inheritdoc/>
    public ValueTask<WaferMapSetupAcknowledgement> SaveSetupAsync(WaferMapSetup setup, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(setup);
        cancellationToken.ThrowIfCancellationRequested();

        if (!HasRoomFor(_setups, setup.MaterialId))
        {
            return ValueTask.FromResult(WaferMapSetupAcknowledgement.Rejected);
        }

        _setups[setup.MaterialId] = setup;

        return ValueTask.FromResult(WaferMapSetupAcknowledgement.Accepted);
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapSetup?> LoadSetupAsync(WaferMapSetupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_setups.GetValueOrDefault(request.MaterialId));
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapTransmitGrant> InquireTransmitAsync(WaferMapTransmitInquiry inquiry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inquiry);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Grant);
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapDataAcknowledgement> SaveRowDataAsync(WaferMapRowData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Save(_rows, data.MaterialId, data));
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapDataAcknowledgement> SaveArrayDataAsync(WaferMapArrayData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Save(_arrays, data.MaterialId, data));
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapDataAcknowledgement> SavePointDataAsync(WaferMapPointData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Save(_points, data.MaterialId, data));
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapRowData?> LoadRowDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_rows.GetValueOrDefault(request.MaterialId));
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapArrayData?> LoadArrayDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_arrays.GetValueOrDefault(request.MaterialId));
    }

    /// <inheritdoc/>
    public ValueTask<WaferMapPointData?> LoadPointDataAsync(WaferMapDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var data = _points.GetValueOrDefault(request.MaterialId);

        if (data is null || request.BinSelection != WaferMapBinSelection.NonNullOnly)
        {
            return ValueTask.FromResult(data);
        }

        // The null bin code lives on the setup data, so a map held without one cannot be filtered:
        // the whole map goes back rather than a map this store has guessed the holes in.
        if (!_setups.TryGetValue(request.MaterialId, out var setup))
        {
            return ValueTask.FromResult<WaferMapPointData?>(data);
        }

        var nullBin = (byte)setup.NullBinCode;
        List<WaferMapPoint> kept = [];

        foreach (var point in data.Points)
        {
            var bins = point.Bins.ToArray();

            if (Array.IndexOf(bins, nullBin) < 0)
            {
                kept.Add(point);
                continue;
            }

            // A run that is partly null is split rather than dropped whole: the bins that hold a
            // die keep their own coordinates, which is what coordinate data is for.
            for (var i = 0; i < bins.Length; i++)
            {
                if (bins[i] != nullBin)
                {
                    kept.Add(new WaferMapPoint
                    {
                        Position = new WaferMapPosition(point.Position.X + i, point.Position.Y),
                        Bins = new[] { bins[i] },
                    });
                }
            }
        }

        return ValueTask.FromResult<WaferMapPointData?>(new WaferMapPointData
        {
            MaterialId = data.MaterialId,
            IdType = data.IdType,
            Points = kept,
        });
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        _setups.Clear();
        _rows.Clear();
        _arrays.Clear();
        _points.Clear();
    }

    private WaferMapDataAcknowledgement Save<T>(ConcurrentDictionary<string, T> store, string materialId, T data)
        where T : class
    {
        if (!HasRoomFor(store, materialId))
        {
            return WaferMapDataAcknowledgement.Rejected;
        }

        store[materialId] = data;

        return WaferMapDataAcknowledgement.Accepted;
    }

    private bool HasRoomFor<T>(ConcurrentDictionary<string, T> store, string materialId)
        where T : class
        => store.Count < Capacity || store.ContainsKey(materialId);
}
