// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;

namespace Barbatos.SecsGem.ItemTransfer;

/// <summary>
/// The items this end has, and what the other end may do to them.
/// </summary>
/// <remarks>
/// <para>
/// Six methods, one per primary this end answers: list what there is, hand back an item's
/// attributes, decide whether an item may be sent, take one, abort a transfer and delete an item.
/// </para>
/// <para>
/// <strong>Both ends of stream 21 have one of these</strong>, which is the thing that makes this
/// package unlike the rest: every row of the stream travels either way, so a peer is not a host or
/// an equipment but something with items that can ask and be asked. A process that only ever sends
/// still needs a store, because the other end may ask it for a directory.
/// </para>
/// <para>
/// <strong>Half can refuse and half cannot.</strong> S21F6 carries a <c>GRANT</c>, and S21F8,
/// S21F16 and S21F18 carry an acknowledgement, so the transfer request, the data send, the abort
/// and the delete all have somewhere to put a no. S21F2 and S21F4 carry no code at all - a
/// directory and an attribute list are data and nothing else - so an unknown item comes back as an
/// empty list.
/// </para>
/// <para>
/// Every method is awaited inside the transaction the other end is waiting on, so none should be
/// slow.
/// </para>
/// </remarks>
public interface IItemStore
{
    /// <summary>Lists the items this end has - S21F1.</summary>
    /// <param name="itemType">Which kind - <c>ITEMTYPE</c>. Empty asks for every kind.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them. Empty is a legitimate answer.</returns>
    /// <remarks>
    /// The reference gives <c>ITEMTYPE</c> no vocabulary, so what a type means is the two ends'
    /// own business and nothing here interprets it.
    /// </remarks>
    ValueTask<IReadOnlyList<Item>> ListAsync(string itemType, CancellationToken cancellationToken);

    /// <summary>Hands back an item's attributes - S21F3.</summary>
    /// <param name="request">Which item, and which attributes. An empty attribute list asks for all of them.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them, or an empty list when there is no such item.</returns>
    /// <remarks>
    /// The values are <c>ATTRDATA</c>, an <c>L</c> in the dictionary and E39's own item - so an
    /// attribute here is the same pair <c>Barbatos.SecsGem.Objects</c> serves on stream 14. An
    /// empty list is the only refusal S21F4 has, because it carries no acknowledgement code.
    /// </remarks>
    ValueTask<IReadOnlyList<ItemAttribute>> AttributesAsync(
        ItemAttributeRequest request,
        CancellationToken cancellationToken);

    /// <summary>Decides whether an item may be sent to this end - S21F5.</summary>
    /// <param name="request">Which item, whose correlation number, and how large the sender says it is.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant.</returns>
    /// <remarks>The cheap place to say no: after the grant, the next message is the whole item.</remarks>
    ValueTask<ItemTransferGrant> RequestAsync(
        ItemTransferRequest request,
        CancellationToken cancellationToken);

    /// <summary>Takes an item the other end sent - S21F7.</summary>
    /// <param name="itemId">Which item - <c>ITEMID</c>.</param>
    /// <param name="dataId">The sender's correlation number - <c>DATAID</c>.</param>
    /// <param name="data">The contents, already copied out of the arriving message.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was accepted.</returns>
    ValueTask<ItemAcknowledgement> SaveAsync(
        string itemId,
        uint dataId,
        ItemPayload data,
        CancellationToken cancellationToken);

    /// <summary>Aborts a transfer - S21F15.</summary>
    /// <param name="itemId">Which item's transfer.</param>
    /// <param name="dataId">Which transfer of it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was aborted.</returns>
    /// <remarks>Aborting is not deleting - S21F17 is the message that deletes, and it is the only one in the stream that does.</remarks>
    ValueTask<ItemAcknowledgement> AbortAsync(
        string itemId,
        uint dataId,
        CancellationToken cancellationToken);

    /// <summary>Deletes an item - S21F17.</summary>
    /// <param name="itemId">Which item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was deleted.</returns>
    /// <remarks>
    /// The one destructive message this package implements, and the only one stream 21 has. Streams
    /// 8 and 13 have none at all, which is why neither of those packages can remove anything.
    /// </remarks>
    ValueTask<ItemAcknowledgement> DeleteAsync(string itemId, CancellationToken cancellationToken);
}

/// <summary>
/// An item store that keeps everything in memory.
/// </summary>
/// <remarks>
/// <para>
/// Enough for a simulator, a test, or a peer that passes items straight on. Anything that must
/// survive a restart writes its own store; there is no snapshot pair here, for the reason the other
/// transfer packages give - an item is not state a machine claims about itself.
/// </para>
/// <para>
/// <strong>It enforces the request-before-send sequence</strong>, which is this class's choice and
/// not E5's: an item must be granted before <see cref="SaveAsync"/> will take it. The
/// acknowledgement has one refusal and no way to say which of several things went wrong, because
/// none of stream 21's acknowledgement items has a table at all.
/// </para>
/// </remarks>
public sealed class ItemInMemoryStore : IItemStore
{
    private readonly ConcurrentDictionary<string, Held> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, uint> _granted = new(StringComparer.Ordinal);

    /// <summary>Gets or sets how many items may be held. Default 64.</summary>
    /// <remarks>A request beyond this is refused with <see cref="ItemTransferGrant.NoSpace"/>, which is what the code is for.</remarks>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many items are held.</summary>
    public int Count => _items.Count;

    /// <summary>Gets how many transfers have been granted and not yet sent.</summary>
    public int GrantedCount => _granted.Count;

    /// <summary>Puts an item in without a transaction, for a simulator or a test.</summary>
    /// <param name="item">What it is.</param>
    /// <param name="data">Its contents, or null for an item that is only listed.</param>
    /// <param name="attributes">Its attributes. <strong>Kept, not copied</strong> - see the remarks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>An attribute's value stays this caller's item and is kept by reference</strong>,
    /// which is a lifetime the caller has to honour: do not dispose it while the store holds it.
    /// That matters because <c>ItemTransferCodec.AttributeBody</c> puts the value into the reply it
    /// builds and a disposed reply disposes its children - harmless for a small value, which owns
    /// nothing to release, but an attribute past <c>SecsCodecOptions.PooledPayloadThreshold</c>
    /// would go back to the pool after the first S21F3 and be lent out again on the second.
    /// <see cref="ItemFileStore.AddAsync"/> copies instead, because a record is bytes, so a caller
    /// that wants to be rid of the ownership question can use that store.
    /// </para>
    /// <para>
    /// How a peer offers an item the other end can go and fetch, which stream 21 has no message
    /// for: there is no "I have a new item" primary, so the other end finds out with S21F1. It is
    /// also the only way an item gets attributes at all - S21F3 asks for them and no row sets one.
    /// </para>
    /// </remarks>
    public void Add(Item item, ItemPayload? data = null, IReadOnlyList<ItemAttribute>? attributes = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        _items[item.Id] = new Held(item, data, attributes ?? []);
    }

    /// <summary>Gets an item, or <see langword="null"/> when there is none.</summary>
    /// <param name="itemId">Which item.</param>
    /// <returns>It.</returns>
    public Item? Find(string itemId)
        => _items.TryGetValue(itemId, out var held) ? held.What : null;

    /// <summary>Gets an item's contents, or <see langword="null"/> when there are none.</summary>
    /// <param name="itemId">Which item.</param>
    /// <returns>Them.</returns>
    public ItemPayload? Contents(string itemId)
        => _items.TryGetValue(itemId, out var held) ? held.Data : null;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Item>> ListAsync(string itemType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered by identifier so the other end can diff two directories, and filtered by type
        // only when one was asked for - an empty ITEMTYPE is every kind.
        IReadOnlyList<Item> listed =
        [
            .. _items.Values
                .Select(held => held.What)
                .Where(item => itemType.Length == 0 || string.Equals(item.Type, itemType, StringComparison.Ordinal))
                .OrderBy(item => item.Id, StringComparer.Ordinal),
        ];

        return ValueTask.FromResult(listed);
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ItemAttribute>> AttributesAsync(
        ItemAttributeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_items.TryGetValue(request.ItemId, out var held))
        {
            return ValueTask.FromResult<IReadOnlyList<ItemAttribute>>([]);
        }

        // An empty request asks for all of them, which is the reading stream 14 uses for the same
        // item in S14F1 rather than one this package chose.
        IReadOnlyList<ItemAttribute> answered = request.Attributes.Count == 0
            ? held.Attributes
            : [.. held.Attributes.Where(attribute =>
                request.Attributes.Contains(attribute.Name, StringComparer.Ordinal))];

        return ValueTask.FromResult(answered);
    }

    /// <inheritdoc />
    public ValueTask<ItemTransferGrant> RequestAsync(
        ItemTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_items.ContainsKey(request.ItemId) && _items.Count >= Capacity)
        {
            return ValueTask.FromResult(ItemTransferGrant.NoSpace);
        }

        _granted[request.ItemId] = request.DataId;

        return ValueTask.FromResult(ItemTransferGrant.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<ItemAcknowledgement> SaveAsync(
        string itemId,
        uint dataId,
        ItemPayload data,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_granted.TryGetValue(itemId, out var granted) || granted != dataId)
        {
            // Nothing was granted for this item and this transfer, so nothing was offered. The
            // acknowledgement cannot say which of those two it was - none of stream 21's
            // acknowledgement items has a table of values.
            return ValueTask.FromResult(ItemAcknowledgement.Denied);
        }

        _granted.TryRemove(itemId, out _);

        var length = (uint)(data.Decode() is { } decoded ? Measure(decoded) : 0);

        _items[itemId] = _items.TryGetValue(itemId, out var held)
            ? held with
            {
                Data = data,
                What = new Item { Id = itemId, Type = held.What.Type, Length = length },
            }
            : new Held(new Item { Id = itemId, Length = length }, data, []);

        return ValueTask.FromResult(ItemAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<ItemAcknowledgement> AbortAsync(
        string itemId,
        uint dataId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_granted.TryGetValue(itemId, out var granted) || granted != dataId)
        {
            return ValueTask.FromResult(ItemAcknowledgement.Denied);
        }

        // Aborting forgets the grant and keeps whatever is already stored. S21F17 is the message
        // that deletes, and an abort that deleted would be doing its job.
        _granted.TryRemove(itemId, out _);

        return ValueTask.FromResult(ItemAcknowledgement.Accepted);
    }

    /// <inheritdoc />
    public ValueTask<ItemAcknowledgement> DeleteAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        cancellationToken.ThrowIfCancellationRequested();

        _granted.TryRemove(itemId, out _);

        return ValueTask.FromResult(
            _items.TryRemove(itemId, out _) ? ItemAcknowledgement.Accepted : ItemAcknowledgement.Denied);
    }

    /// <summary>Counts an arriving item's values, for the length the directory reports.</summary>
    /// <remarks>
    /// The encoded size is not used, deliberately: <c>LENGTH</c> is a <c>U4</c> and the reference
    /// says nothing about what it counts, so this store reports the value count of the item it was
    /// sent and says so rather than quietly measuring a header.
    /// </remarks>
    private static int Measure(SecsItem item)
    {
        using (item)
        {
            return item.Count;
        }
    }

    /// <summary>A held item: what it is, its contents and its attributes.</summary>
    private sealed record Held(Item What, ItemPayload? Data, IReadOnlyList<ItemAttribute> Attributes);
}
