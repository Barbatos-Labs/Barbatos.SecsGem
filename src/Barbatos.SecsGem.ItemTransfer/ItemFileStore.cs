// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using System.Collections.Concurrent;
using Barbatos.SecsGem.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ItemTransfer;

/// <summary>
/// An item store that outlives the process, by keeping each item in a file.
/// </summary>
/// <remarks>
/// <para>
/// Stream 21 is the one stream here both ends call the same registration on, so both ends have a
/// store and either may be the one that was switched off. <see cref="ItemInMemoryStore"/> loses
/// everything; this writes an item to disk before it answers S21F8, and adopts what is still there
/// when it is next constructed.
/// </para>
/// <para>
/// <strong>The mechanism is <c>DurableRecordDirectory</c>'s</strong>, shared as source with the
/// three other transfer stores: one file per item, written through, committed with a rename, and a
/// file name that is a sequence number and nothing else - so <c>ITEMID</c>, an <c>A[n]</c> the
/// other end chooses, never becomes part of a path.
/// </para>
/// <para>
/// <strong>This is the store with the most to write down, and the record shape is what makes it
/// possible.</strong> A control program is bytes and a revision; a data set is a number and a
/// payload; an item is an identifier, a type, a length, <em>any number of attributes</em> and
/// contents that may not exist yet. A record is a list of fields and an optional payload, so the
/// attributes are fields and "listed but never sent" is a record with no payload at all - which is
/// a real state here and not a half-finished one: S21F1 lists an item, S21F3 describes it and
/// S21F7 sends it, and those are three transactions a host may make hours apart.
/// </para>
/// <para>
/// <strong>An attribute is kept as the bytes it arrived in and decoded again for every
/// request</strong>, where the in-memory store keeps the caller's own <c>SecsItem</c>. Necessary
/// here, because a record is bytes - and safer, though the margin is narrower than it looks:
/// <c>ItemTransferCodec.AttributeBody</c> puts an attribute's value <em>into</em> the reply it
/// builds, a reply is disposed once it has been sent, and disposing a list disposes its children.
/// For a small value that costs nothing, since an unpooled payload owns nothing to release; an
/// attribute past <c>SecsCodecOptions.PooledPayloadThreshold</c> would have its buffer handed back
/// to the pool after the first request and handed out again on the second. Keeping bytes also means
/// <see cref="AddAsync"/> copies what it is given, so a caller may dispose the items it built -
/// which <c>ItemInMemoryStore.Add</c> does not allow, because it keeps them.
/// </para>
/// <para>
/// <strong>What a restart keeps is what was sent; the grants are not.</strong> The items, their
/// types, their lengths, their attributes and their contents all come back.
/// <see cref="GrantedCount"/> is zero on every open, because an S21F5 answered and an S21F7 that
/// never arrived is a transaction in flight and ends the way an interruption ends - the other end
/// asks again.
/// </para>
/// <para>
/// No <c>Capture</c>/<c>Restore</c> pair, for the reason the in-memory store gives: an item is not
/// state a machine claims about itself, so it survives a restart through an implementation.
/// </para>
/// <para>
/// One store per directory. Two would fight over the sequence numbers - and since both ends of
/// stream 21 have a store, two peers in one process need two directories.
/// </para>
/// </remarks>
public sealed class ItemFileStore : IItemStore
{
    /// <summary>The extension a finished item record carries.</summary>
    public const string RecordExtension = ".item";

    /// <summary>The extension appended to any file while it is being written.</summary>
    public const string PartialExtension = DurableRecordDirectory.PartialExtension;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Held> _items = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, uint> _granted = new(StringComparer.Ordinal);
    private readonly DurableRecordDirectory _records;
    private readonly ILogger _logger;

    private long _next;

    /// <summary>Opens a store over a directory, adopting whatever is already in it.</summary>
    /// <param name="directory">Where the records live. Created if it does not exist.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="directory"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The directory cannot be created or read.</exception>
    /// <remarks>
    /// The directory is created and scanned here rather than on first use, so a path that is wrong
    /// fails at start-up where an operator is looking. An unfinished write is swept, a file that is
    /// not this store's is left alone, and so is a record this version cannot read.
    /// <see cref="Capacity"/> is not enforced on what is adopted: a directory holding more than this
    /// end is configured for is a fact, and the other end can still ask for every item in it by
    /// name.
    /// </remarks>
    public ItemFileStore(string directory, ILogger<ItemFileStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _logger = logger ?? NullLogger<ItemFileStore>.Instance;
        _records = new DurableRecordDirectory(directory, RecordExtension, _logger);

        var unreadable = 0;

        foreach (var (sequence, path) in _records.Records())
        {
            if (Read(path) is not { } record)
            {
                unreadable++;
                continue;
            }

            if (_items.TryGetValue(record.Held.What.Id, out var held))
            {
                _logger.LogWarning(
                    "Two records hold item '{Id}'; keeping sequence {Kept} and deleting {Dropped}.",
                    record.Held.What.Id,
                    Math.Max(held.Sequence, sequence),
                    Math.Min(held.Sequence, sequence));

                _records.Delete(Math.Min(held.Sequence, sequence));

                if (sequence < held.Sequence)
                {
                    continue;
                }
            }

            _items[record.Held.What.Id] = record.Held with { Sequence = sequence };
            _next = Math.Max(_next, sequence + 1);
        }

        if (_items.Count > 0 || _records.Swept > 0 || unreadable > 0)
        {
            _logger.LogInformation(
                "Item directory {Directory} opened with {Items} item(s), {WithData} of them carrying contents, "
                    + "{Partial} unfinished write(s) removed and {Unreadable} unreadable record(s) left alone.",
                Directory,
                _items.Count,
                _items.Values.Count(held => held.HasData),
                _records.Swept,
                unreadable);
        }
    }

    /// <summary>Gets the directory the records live in, as a full path.</summary>
    public string Directory => _records.Path;

    /// <summary>Gets or sets how many items may be held. Default 64.</summary>
    /// <remarks>A request beyond this is refused with <see cref="ItemTransferGrant.NoSpace"/>, which is what the code is for.</remarks>
    public int Capacity { get; set; } = 64;

    /// <summary>Gets how many items are held.</summary>
    public int Count => _items.Count;

    /// <summary>Gets how many transfers have been granted and not yet sent.</summary>
    /// <remarks>Zero on every open: a grant is a transaction in flight, so a restart ends it.</remarks>
    public int GrantedCount => _granted.Count;

    /// <summary>Gets an item, or <see langword="null"/> when there is none.</summary>
    /// <param name="itemId">Which item.</param>
    /// <returns>It.</returns>
    /// <remarks>
    /// Out of the index and not off the disk, so it costs nothing: an item's identifier, type and
    /// length are what a directory is made of, and <see cref="ListAsync"/> would be a scan of every
    /// record otherwise.
    /// </remarks>
    public Item? Find(string itemId) => _items.TryGetValue(itemId, out var held) ? held.What : null;

    /// <summary>Gets an item's contents, or <see langword="null"/> when it has none.</summary>
    /// <param name="itemId">Which item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them.</returns>
    /// <remarks>
    /// Asynchronous where <c>ItemInMemoryStore.Contents</c> is a property-like call, because the
    /// contents are in a file. Null covers both an item that was only ever listed and an item whose
    /// record will not read - the second is logged, because nothing else tells them apart.
    /// </remarks>
    public async ValueTask<ItemPayload?> ContentsAsync(string itemId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_items.TryGetValue(itemId, out var held) || !held.HasData)
            {
                return null;
            }

            if (Read(_records.PathFor(held.Sequence)) is not { } record)
            {
                _logger.LogWarning("The record for item '{Id}' could not be read, so it has no contents to give.", itemId);

                return null;
            }

            return record.Data;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Puts an item in without a transaction, and on the disk.</summary>
    /// <param name="item">What it is.</param>
    /// <param name="data">Its contents, or null for an item that is only listed.</param>
    /// <param name="attributes">Its attributes. Their values are copied, so they stay the caller's to dispose.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the item is on the disk.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is <see langword="null"/>.</exception>
    /// <exception cref="IOException">The write failed. Nothing was stored.</exception>
    /// <remarks>
    /// How a peer offers an item the other end can go and fetch, which stream 21 has no message
    /// for: there is no "I have a new item" primary, so the other end finds out with S21F1. It is
    /// also the only way an item gets <strong>attributes</strong> - no row of the stream sets one,
    /// S21F3 only asks - so without this a durable store could list nothing it had not been given
    /// by a transfer.
    /// </remarks>
    public async ValueTask AddAsync(
        Item item,
        ItemPayload? data = null,
        IReadOnlyList<ItemAttribute>? attributes = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await WriteAsync(
                item,
                [.. (attributes ?? []).Select(attribute =>
                    (attribute.Name, Value: attribute.Value.ToByteArray()))],
                data,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<Item>> ListAsync(string itemType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemType);
        cancellationToken.ThrowIfCancellationRequested();

        // Ordered by identifier so the other end can diff two directories, and filtered by type
        // only when one was asked for - an empty ITEMTYPE is every kind. Out of the index, which is
        // what the index is for: a directory request must not become a scan of every record.
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
    /// <remarks>
    /// Every call decodes the stored bytes afresh, so the items handed back are the caller's and
    /// may be disposed with the reply they go into. That is the difference this store's remarks
    /// explain: an attribute kept as a live item would be handed out twice and disposed once.
    /// </remarks>
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
        IReadOnlyList<ItemAttribute> answered =
        [
            .. held.Attributes
                .Where(attribute => request.Attributes.Count == 0
                    || request.Attributes.Contains(attribute.Name, StringComparer.Ordinal))
                .Select(attribute => new ItemAttribute
                {
                    Name = attribute.Name,
                    Value = SecsItem.Decode(attribute.Value),
                }),
        ];

        return ValueTask.FromResult(answered);
    }

    /// <inheritdoc />
    /// <remarks>Nothing is written: a grant is a transaction in flight, so a restart ends it.</remarks>
    public async ValueTask<ItemTransferGrant> RequestAsync(
        ItemTransferRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_items.ContainsKey(request.ItemId) && _items.Count >= Capacity)
            {
                return ItemTransferGrant.NoSpace;
            }

            _granted[request.ItemId] = request.DataId;

            return ItemTransferGrant.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The record goes down before the acknowledgement, so an interruption between the two leaves
    /// an item the next open adopts - the right way round, because the other end was not told it
    /// arrived and may send it again, onto the same record. An item that already existed keeps its
    /// type and its attributes, which is the in-memory store's rule: a transfer carries contents,
    /// and nothing in S21F7 says what kind of thing this is.
    /// </remarks>
    public async ValueTask<ItemAcknowledgement> SaveAsync(
        string itemId,
        uint dataId,
        ItemPayload data,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(data);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_granted.TryGetValue(itemId, out var granted) || granted != dataId)
            {
                // Nothing was granted for this item and this transfer, so nothing was offered. The
                // acknowledgement cannot say which of those two it was - none of stream 21's
                // acknowledgement items has a table of values.
                return ItemAcknowledgement.Denied;
            }

            var held = _items.TryGetValue(itemId, out var existing) ? existing : null;

            await WriteAsync(
                new Item
                {
                    Id = itemId,
                    Type = held?.What.Type ?? string.Empty,
                    Length = Measure(data),
                },
                held?.Attributes ?? [],
                data,
                cancellationToken).ConfigureAwait(false);

            _granted.TryRemove(itemId, out _);

            return ItemAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ItemAcknowledgement> AbortAsync(
        string itemId,
        uint dataId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!_granted.TryGetValue(itemId, out var granted) || granted != dataId)
            {
                return ItemAcknowledgement.Denied;
            }

            // Forgets the grant and keeps whatever is stored, and writes nothing: S21F17 is the
            // message that deletes, and the grant was never on the disk.
            _granted.TryRemove(itemId, out _);

            return ItemAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ItemAcknowledgement> DeleteAsync(string itemId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _granted.TryRemove(itemId, out _);

            if (!_items.TryRemove(itemId, out var held))
            {
                return ItemAcknowledgement.Denied;
            }

            _records.Delete(held.Sequence);

            return ItemAcknowledgement.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---------------------------------------------------------------- the files

    /// <summary>Counts an arriving item's values, for the length the directory reports.</summary>
    /// <remarks>
    /// The encoded size is not used, deliberately and for the in-memory store's reason:
    /// <c>LENGTH</c> is a <c>U4</c> and the reference says nothing about what it counts, so this
    /// reports the value count of the item it was sent and says so rather than quietly measuring a
    /// header.
    /// </remarks>
    private static uint Measure(ItemPayload data)
    {
        using var decoded = data.Decode();

        return (uint)(decoded?.Count ?? 0);
    }

    /// <summary>Writes one item's record and moves the index onto it.</summary>
    /// <remarks>
    /// The identifier, the type and the length, then two fields per attribute, then the contents as
    /// a tail - or no tail at all for an item that is only listed, which is a state of its own
    /// here. An existing item keeps its sequence number, so a replacement is one atomic rewrite.
    /// </remarks>
    private async ValueTask WriteAsync(
        Item item,
        IReadOnlyList<(string Name, byte[] Value)> attributes,
        ItemPayload? data,
        CancellationToken cancellationToken)
    {
        List<ReadOnlyMemory<byte>> fields =
        [
            DurableRecordDirectory.Bytes(item.Id),
            DurableRecordDirectory.Bytes(item.Type),
            DurableRecordDirectory.Bytes(item.Length),
        ];

        foreach (var (name, value) in attributes)
        {
            fields.Add(DurableRecordDirectory.Bytes(name));
            fields.Add(value);
        }

        var sequence = _items.TryGetValue(item.Id, out var held) ? held.Sequence : _next;

        await _records.WriteRecordAsync(sequence, fields, data?.Encoded, cancellationToken).ConfigureAwait(false);

        _next = Math.Max(_next, sequence + 1);
        _items[item.Id] = new Held(sequence, item, attributes, data is not null);
    }

    /// <summary>Reads one item's record.</summary>
    /// <remarks>
    /// An odd number of fields is a record this version cannot read, the same answer as a truncated
    /// one: three fixed fields and two per attribute can never come to an even count.
    /// </remarks>
    private (Held Held, ItemPayload? Data)? Read(string path)
    {
        if (_records.TryReadRecord(path) is not { } record
            || record.Fields.Count < 3
            || record.Fields.Count % 2 == 0
            || DurableRecordDirectory.Number(record.Fields[2]) is not { } length)
        {
            return null;
        }

        List<(string Name, byte[] Value)> attributes = [];

        for (var at = 3; at + 1 < record.Fields.Count; at += 2)
        {
            attributes.Add((DurableRecordDirectory.Text(record.Fields[at]), record.Fields[at + 1]));
        }

        var item = new Item
        {
            Id = DurableRecordDirectory.Text(record.Fields[0]),
            Type = DurableRecordDirectory.Text(record.Fields[1]),
            Length = length,
        };

        return (
            new Held(0, item, attributes, record.Tail is not null),
            record.Tail is { } encoded ? ItemPayload.FromEncoded(encoded) : null);
    }

    /// <summary>What the index holds: where the record is, what the item is, and its attributes as bytes.</summary>
    /// <remarks>
    /// The contents are the one part left on the disk - an item may be a megabyte - so what is here
    /// is what a directory request and an attribute request need, which between them are five of
    /// stream 21's six primaries.
    /// </remarks>
    private sealed record Held(
        long Sequence,
        Item What,
        IReadOnlyList<(string Name, byte[] Value)> Attributes,
        bool HasData);
}
