// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.Material;

/// <summary>
/// What the equipment knows about the material it holds.
/// </summary>
/// <remarks>
/// <para>
/// Five methods, and they are every question a host can ask in the material half of stream 3. The
/// other direction - material found, lost, changed, moved - is the equipment telling the host, and
/// that goes out through <see cref="MaterialEquipment"/> rather than coming in here.
/// </para>
/// <para>
/// Each method is asked inside a transaction the host is waiting on, so none of them should take
/// long. A machine whose inventory lives behind a database query wants that query cached, not
/// awaited while T3 runs.
/// </para>
/// </remarks>
public interface IMaterialSource
{
    /// <summary>Every piece of material this machine holds, and where - S3F1.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The material. An empty list is a legitimate answer: the machine is empty.</returns>
    ValueTask<IReadOnlyList<MaterialStatus>> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>How long each piece of material has left - S3F3.</summary>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The material and its remaining time.</returns>
    ValueTask<IReadOnlyList<MaterialCompletion>> GetTimeToCompletionAsync(CancellationToken cancellationToken);

    /// <summary>Where one piece of material is - S3F9.</summary>
    /// <param name="materialId">What the host is looking for.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Where it is, or <see langword="null"/> when this machine does not have it.</returns>
    /// <remarks><see langword="null"/> is answered with the zero-length list E5 uses for "no data", because S3F10 has no code to carry a refusal.</remarks>
    ValueTask<MaterialLocation?> FindAsync(string materialId, CancellationToken cancellationToken);

    /// <summary>Move material somewhere - S3F11.</summary>
    /// <param name="transfer">What to move and where to.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the machine will do it.</returns>
    /// <remarks>
    /// The acknowledgement says the request was taken, not that the material has arrived. A
    /// transfer takes a robot and some seconds; S3F35 is how the machine says it is done.
    /// </remarks>
    ValueTask<MaterialAcknowledgement> TransferAsync(MaterialTransfer transfer, CancellationToken cancellationToken);

    /// <summary>Whether a large body may be sent - S3F15.</summary>
    /// <param name="dataId">The data identifier the host will use for it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant.</returns>
    ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken cancellationToken);
}

/// <summary>
/// A material source that keeps an inventory in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or a machine whose inventory is small and rebuilt at start-up.
/// </para>
/// <para>
/// A transfer <em>moves</em> the material rather than only acknowledging it, so a host that asks
/// S3F9 afterwards gets the new location. That is what makes it useful in a sample: the two
/// messages agree with each other without anything outside this class arranging it.
/// </para>
/// </remarks>
public sealed class MaterialInMemorySource : IMaterialSource
{
    private readonly ConcurrentDictionary<string, Holding> _material = new(StringComparer.Ordinal);

    private sealed record Holding(uint Location, byte Quantity, uint TimeToCompletion);

    /// <summary>Gets or sets how many pieces of material may be held at once.</summary>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many pieces of material are held.</summary>
    public int Count => _material.Count;

    /// <summary>Gets or sets whether a multi-block inquire is granted.</summary>
    /// <remarks>Granted by default. A simulator that needs the refusing path sets it to <see cref="MaterialGrant.NoSpace"/>.</remarks>
    public MaterialGrant Grant { get; set; } = MaterialGrant.Accepted;

    /// <summary>Gets or sets whether a transfer request is accepted.</summary>
    /// <remarks>
    /// Accepted by default, and a <see cref="MaterialAcknowledgement.Denied"/> here leaves the
    /// material where it is - which is the behaviour a host should see from a machine whose robot
    /// is busy.
    /// </remarks>
    public MaterialAcknowledgement TransferAnswer { get; set; } = MaterialAcknowledgement.Accepted;

    /// <summary>Puts material in the machine.</summary>
    /// <param name="materialId">What it is called.</param>
    /// <param name="location">Where it is.</param>
    /// <param name="quantity">How much of it.</param>
    /// <param name="timeToCompletion">How long it has left.</param>
    /// <returns><see langword="false"/> when the machine is full.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialId"/> is <see langword="null"/>.</exception>
    public bool Load(string materialId, uint location, byte quantity = 1, uint timeToCompletion = 0)
    {
        ArgumentNullException.ThrowIfNull(materialId);

        if (_material.Count >= Capacity && !_material.ContainsKey(materialId))
        {
            return false;
        }

        _material[materialId] = new Holding(location, quantity, timeToCompletion);
        return true;
    }

    /// <summary>Takes material out of the machine.</summary>
    /// <param name="materialId">What to take out.</param>
    /// <returns><see langword="false"/> when it was not there.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="materialId"/> is <see langword="null"/>.</exception>
    public bool Unload(string materialId)
    {
        ArgumentNullException.ThrowIfNull(materialId);

        return _material.TryRemove(materialId, out _);
    }

    /// <summary>Empties the machine.</summary>
    public void Clear() => _material.Clear();

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MaterialStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<MaterialStatus> status = [];

        foreach (var (materialId, holding) in _material)
        {
            status.Add(new MaterialStatus
            {
                MaterialId = materialId,
                Location = holding.Location,
                Quantity = holding.Quantity,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<MaterialStatus>>(status);
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<MaterialCompletion>> GetTimeToCompletionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        List<MaterialCompletion> completion = [];

        foreach (var (materialId, holding) in _material)
        {
            completion.Add(new MaterialCompletion
            {
                MaterialId = materialId,
                Location = holding.Location,
                TimeToCompletion = holding.TimeToCompletion,
            });
        }

        return ValueTask.FromResult<IReadOnlyList<MaterialCompletion>>(completion);
    }

    /// <inheritdoc/>
    public ValueTask<MaterialLocation?> FindAsync(string materialId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(materialId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_material.TryGetValue(materialId, out var holding)
            ? new MaterialLocation { MaterialId = materialId, Location = holding.Location }
            : null);
    }

    /// <inheritdoc/>
    public ValueTask<MaterialAcknowledgement> TransferAsync(MaterialTransfer transfer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_material.TryGetValue(transfer.MaterialId, out var holding))
        {
            // Material this machine does not have cannot be moved by it, whatever TransferAnswer
            // says. A host that gets Accepted here would wait for an S3F35 that never comes.
            return ValueTask.FromResult(MaterialAcknowledgement.Denied);
        }

        if (TransferAnswer is MaterialAcknowledgement.Denied)
        {
            return ValueTask.FromResult(MaterialAcknowledgement.Denied);
        }

        _material[transfer.MaterialId] = holding with { Location = transfer.Destination };

        return ValueTask.FromResult(MaterialAcknowledgement.Accepted);
    }

    /// <inheritdoc/>
    public ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(Grant);
    }
}
