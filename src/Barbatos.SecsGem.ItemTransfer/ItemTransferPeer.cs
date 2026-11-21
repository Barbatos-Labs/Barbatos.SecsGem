// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.

using Barbatos.SecsGem.Hsms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Barbatos.SecsGem.ItemTransfer;

/// <summary>
/// One end of stream 21, and there is only one kind of end.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every row of stream 21 is marked as travelling either way</strong>, so this is the
/// second type in this repository that is neither a host nor an equipment - <c>MaterialHandoff</c>
/// for stream 4 is the other. Both ends have items; either may ask the other for its directory,
/// read its attributes, or send it one. So both ends call the same registration, both declare the
/// same six primaries, and both need an <see cref="IItemStore"/>: a process that only ever sends
/// still has to answer a directory request.
/// </para>
/// <para>
/// <strong>Six pairs of the stream's ten, and the measurement for the other four.</strong> The
/// blocked transfer and its status - S21F9 to S21F14 - need <c>BLKSIZE</c>, <c>BLKNUM</c>,
/// <c>BLKEND</c>, <c>BLKACK</c> and <c>XFRSTAT</c>, and every one of the catalogue's seven
/// block-transfer items is undefined; that is the same measurement that kept the blocked halves of
/// streams 8 and 13 out. The status pair is not separable from them, because what it reports is a
/// block number. The transfer complete - S21F19/F20 - needs <c>CHKSUM</c> and <c>XFRACK</c>, both
/// undefined, and a checksum is the one value that carrying opaquely does not rescue: what two
/// ends must agree on is the algorithm and the span, and the reference gives neither.
/// </para>
/// <para>
/// <strong>What can be refused and what cannot is E5's doing.</strong> S21F6 carries a
/// <c>GRANT</c> and S21F8, S21F16 and S21F18 carry an acknowledgement, so four of the six have
/// somewhere to put a no. S21F2 and S21F4 carry no code at all, so an unreadable request is
/// answered with an empty list - a directory and an attribute list are data and nothing else.
/// </para>
/// </remarks>
public sealed class ItemTransferPeer
{
    private readonly IItemStore _store;
    private readonly HsmsConnection _connection;
    private readonly ILogger _logger;
    private int _dataId;

    /// <summary>Initialises one end over a connection and a store.</summary>
    /// <param name="store">The items this end has.</param>
    /// <param name="connection">The link.</param>
    /// <param name="logger">Diagnostics. <see langword="null"/> discards them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> or <paramref name="connection"/> is <see langword="null"/>.</exception>
    public ItemTransferPeer(
        IItemStore store,
        HsmsConnection connection,
        ILogger<ItemTransferPeer>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? NullLogger<ItemTransferPeer>.Instance;
    }

    /// <summary>Every primary this end answers - and sends, because they are the same six.</summary>
    /// <remarks>
    /// The one list in this stack besides stream 4's that is both what a type sends and what it
    /// answers. Six of stream 21's ten pairs; see the class remarks for the four that are not here.
    /// </remarks>
    public static IReadOnlyList<(byte Stream, byte Function)> HandledMessages { get; } =
    [
        (21, 1),    // Item Directory Request
        (21, 3),    // Item Attributes Request
        (21, 5),    // Item Transfer Request
        (21, 7),    // Item Data Send
        (21, 15),   // Transfer Abort Request
        (21, 17),   // Item Delete Request
    ];

    /// <summary>Asks the other end what items it has - S21F1, answered by S21F2.</summary>
    /// <param name="itemType">Which kind - <c>ITEMTYPE</c>. Empty asks for every kind.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Them. Empty when the other end has none of that kind.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemType"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// There is no message that announces an item, so this is how a peer finds out what the other
    /// one has. An unreadable reply comes back empty and logged rather than throwing.
    /// </remarks>
    public async ValueTask<IReadOnlyList<Item>> ListAsync(
        string itemType = "",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemType);

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 1, replyExpected: true)
            {
                Name = "ItemDirectoryRequest",
                Item = ItemTransferCodec.DirectoryRequestBody(itemType),
            },
            cancellationToken).ConfigureAwait(false);

        var items = ItemTransferCodec.ReadDirectory(reply.Item);

        if (items.Count == 0 && reply.Item is { Count: > 0 })
        {
            _logger.LogWarning("S21F2 carried a body with no readable item in it.");
        }

        return items;
    }

    /// <summary>Asks for an item's attributes - S21F3, answered by S21F4.</summary>
    /// <typeparam name="T">What <paramref name="read"/> returns.</typeparam>
    /// <param name="request">Which item, and which attributes. An empty attribute list asks for all of them.</param>
    /// <param name="read">
    /// Reads the attributes while the reply is still alive, and returns whatever is worth keeping.
    /// It is called once, before this method returns, and the values are gone afterwards.
    /// </param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whatever <paramref name="read"/> returned.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> or <paramref name="read"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// <strong>The values live only as long as this call's results.</strong> Each
    /// <see cref="ItemAttribute.Value"/> is a view into the reply, which is disposed when this
    /// method returns - so copy anything kept. That is the same contract
    /// <c>Barbatos.SecsGem.Objects</c> states for the same two items, and it is why this method
    /// hands the attributes to a callback rather than returning them.
    /// </para>
    /// <para>
    /// Empty is the only refusal S21F4 has, since it carries no acknowledgement code.
    /// </para>
    /// </remarks>
    public async ValueTask<T> AttributesAsync<T>(
        ItemAttributeRequest request,
        Func<IReadOnlyList<ItemAttribute>, T> read,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(read);

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 3, replyExpected: true)
            {
                Name = "ItemAttributesRequest",
                Item = ItemTransferCodec.AttributeRequestBody(request),
            },
            cancellationToken).ConfigureAwait(false);

        if (!ItemTransferCodec.TryReadAttributes(reply.Item, out var echoed, out var attributes))
        {
            throw new SecsException("S21F4 did not carry an attribute list.");
        }

        if (!string.Equals(echoed, request.ItemId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "S21F4 answered for item '{Echoed}' a request about '{Asked}'.",
                echoed,
                request.ItemId);
        }

        return read([.. attributes.Select(pair => new ItemAttribute { Name = pair.Name, Value = pair.Value })]);
    }

    /// <summary>Asks permission to send an item - S21F5, answered by S21F6.</summary>
    /// <param name="itemId">Which item - <c>ITEMID</c>.</param>
    /// <param name="length">How large it is - <c>LENGTH</c>.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The grant, and the correlation number the send must use.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no grant code.</exception>
    /// <remarks>
    /// Worth doing before a large item rather than after. The correlation number comes back with
    /// the grant because
    /// <see cref="SendAsync(string, uint, SecsItem, CancellationToken)"/> has to quote the same
    /// one - it is this end's own, incremented per request, for the reason every other package
    /// here gives.
    /// </remarks>
    public async ValueTask<(ItemTransferGrant Grant, uint DataId)> RequestAsync(
        string itemId,
        uint length,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        var dataId = NextDataId();

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 5, replyExpected: true)
            {
                Name = "ItemTransferRequest",
                Item = ItemTransferCodec.TransferRequestBody(new ItemTransferRequest
                {
                    ItemId = itemId,
                    DataId = dataId,
                    Length = length,
                }),
            },
            cancellationToken).ConfigureAwait(false);

        var code = ItemTransferCodec.ReadCode(reply.Item)
            ?? throw new SecsException("S21F6 carried no GRANT.");

        if (code < (byte)ItemTransferGrant.Other)
        {
            return ((ItemTransferGrant)code, dataId);
        }

        _logger.LogInformation(
            "S21F6 refused the transfer with GRANT {Code}, which the table reserves and does not name.",
            code);

        return (ItemTransferGrant.Other, dataId);
    }

    /// <summary>Sends an item - S21F7, answered by S21F8.</summary>
    /// <param name="itemId">Which item - <c>ITEMID</c>.</param>
    /// <param name="dataId">The correlation number <see cref="RequestAsync"/> returned.</param>
    /// <param name="data">The contents - <c>ITEMDATA</c>. <strong>Taken</strong>: it goes into the message and is disposed with it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the other end took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemId"/> or <paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// The item goes out as it is given: <c>ITEMDATA</c> has no dictionary entry and is named by
    /// stream 21 alone, so this library has no opinion about what an item is. A caller that wants
    /// to send the same one twice sends an <see cref="ItemPayload"/> instead.
    /// </remarks>
    public async ValueTask<ItemAcknowledgement> SendAsync(
        string itemId,
        uint dataId,
        SecsItem data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(data);

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 7, replyExpected: true)
            {
                Name = "ItemDataSend",
                Item = ItemTransferCodec.DataBody(itemId, dataId, data),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S21F8");
    }

    /// <summary>Sends an item already held as bytes - S21F7, answered by S21F8.</summary>
    /// <param name="itemId">Which item.</param>
    /// <param name="dataId">The correlation number <see cref="RequestAsync"/> returned.</param>
    /// <param name="data">The contents. Stays the caller's, and may be sent again.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether the other end took it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemId"/> or <paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no code, or the payload is not a well-formed item.</exception>
    /// <remarks>The overload to use when forwarding: an item that came out of a store crosses the link as the same bytes.</remarks>
    public async ValueTask<ItemAcknowledgement> SendAsync(
        string itemId,
        uint dataId,
        ItemPayload data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(data);

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 7, replyExpected: true)
            {
                Name = "ItemDataSend",
                Item = ItemTransferCodec.DataBody(itemId, dataId, data.Decode()),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S21F8");
    }

    /// <summary>Aborts a transfer - S21F15, answered by S21F16.</summary>
    /// <param name="itemId">Which item's transfer.</param>
    /// <param name="dataId">Which transfer of it.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was aborted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    public async ValueTask<ItemAcknowledgement> AbortAsync(
        string itemId,
        uint dataId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 15, replyExpected: true)
            {
                Name = "TransferAbortRequest",
                Item = ItemTransferCodec.AbortBody(itemId, dataId),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S21F16");
    }

    /// <summary>Deletes an item at the other end - S21F17, answered by S21F18.</summary>
    /// <param name="itemId">Which item.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>Whether it was deleted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="itemId"/> is <see langword="null"/>.</exception>
    /// <exception cref="SecsException">The reply carried no acknowledgement code.</exception>
    /// <remarks>
    /// <strong>The one destructive message in this package</strong>, and the only one stream 21
    /// has. Streams 8 and 13 have none at all, which is why neither of those packages can remove
    /// anything and this one can.
    /// </remarks>
    public async ValueTask<ItemAcknowledgement> DeleteAsync(
        string itemId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);

        using var reply = await ExchangeAsync(
            new SecsMessage(21, 17, replyExpected: true)
            {
                Name = "ItemDeleteRequest",
                Item = ItemTransferCodec.ItemIdBody(itemId),
            },
            cancellationToken).ConfigureAwait(false);

        return ReadAcknowledgement(reply, "S21F18");
    }

    /// <summary>Answers one of the primaries in <see cref="HandledMessages"/>.</summary>
    /// <param name="transaction">The transaction.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>A task that completes once the reply is on the wire.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="transaction"/> is <see langword="null"/>.</exception>
    /// <remarks>The store is asked before the reply bit is looked at, so an item sent without it is still stored.</remarks>
    public async ValueTask HandleAsync(SecsTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        var reply = transaction.S == 21
            ? await BuildReplyAsync(transaction, cancellationToken).ConfigureAwait(false)
            : null;

        if (reply is null)
        {
            _logger.LogWarning(
                "S{S}F{F} reached the item transfer handler with nothing to answer it.",
                transaction.S,
                transaction.F);

            return;
        }

        using (reply)
        {
            if (transaction.ReplyExpected)
            {
                await transaction.ReplyAsync(reply, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<SecsMessage?> BuildReplyAsync(
        SecsTransaction transaction,
        CancellationToken cancellationToken)
        => transaction.F switch
        {
            1 => await OnDirectoryAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            3 => await OnAttributesAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            5 => await OnRequestAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            7 => await OnDataAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            15 => await OnAbortAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            17 => await OnDeleteAsync(transaction.Message, cancellationToken).ConfigureAwait(false),
            _ => null,
        };

    private async ValueTask<SecsMessage> OnDirectoryAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ItemTransferCodec.TryReadDirectoryRequest(message.Item, out var itemType))
        {
            return Nothing(2, "ItemDirectoryData", "S21F1");
        }

        var items = await _store.ListAsync(itemType, cancellationToken).ConfigureAwait(false);

        return Reply(2, "ItemDirectoryData", ItemTransferCodec.DirectoryBody(items));
    }

    private async ValueTask<SecsMessage> OnAttributesAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ItemTransferCodec.TryReadAttributeRequest(message.Item, out var request) || request is null)
        {
            return Nothing(4, "ItemAttributesData", "S21F3");
        }

        var attributes = await _store.AttributesAsync(request, cancellationToken).ConfigureAwait(false);

        // The identifier is echoed so the asking end can tell which item answered, which is the
        // only correlation this pair has - S21F4 carries no acknowledgement code.
        return Reply(
            4,
            "ItemAttributesData",
            ItemTransferCodec.AttributeBody(request.ItemId, attributes));
    }

    private async ValueTask<SecsMessage> OnRequestAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ItemTransferCodec.TryReadTransferRequest(message.Item, out var request) || request is null)
        {
            _logger.LogWarning(
                "S21F5 arrived with a body this end could not read; answering GRANT {Code}.",
                ItemTransferGrant.DuplicateOrInvalid);

            return Reply(
                6,
                "ItemTransferGrant",
                ItemTransferCodec.CodeBody((byte)ItemTransferGrant.DuplicateOrInvalid, "GRANT"));
        }

        var grant = await _store.RequestAsync(request, cancellationToken).ConfigureAwait(false);

        return Reply(6, "ItemTransferGrant", ItemTransferCodec.CodeBody((byte)grant, "GRANT"));
    }

    private async ValueTask<SecsMessage> OnDataAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ItemTransferCodec.TryReadData(message.Item, out var itemId, out var dataId, out var data)
            || data is null)
        {
            _logger.LogWarning("S21F7 arrived with a body this end could not read; answering ACKC21 Denied.");

            return Reply(
                8,
                "ItemDataAcknowledge",
                ItemTransferCodec.CodeBody((byte)ItemAcknowledgement.Denied, "ACKC21"));
        }

        var acknowledgement = await _store.SaveAsync(itemId, dataId, data, cancellationToken).ConfigureAwait(false);

        return Reply(8, "ItemDataAcknowledge", ItemTransferCodec.CodeBody((byte)acknowledgement, "ACKC21"));
    }

    private async ValueTask<SecsMessage> OnAbortAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ItemTransferCodec.TryReadAbort(message.Item, out var itemId, out var dataId))
        {
            return Refuse(16, "TransferAbortAcknowledge", "ABTACK", "S21F15");
        }

        var acknowledgement = await _store.AbortAsync(itemId, dataId, cancellationToken).ConfigureAwait(false);

        return Reply(16, "TransferAbortAcknowledge", ItemTransferCodec.CodeBody((byte)acknowledgement, "ABTACK"));
    }

    private async ValueTask<SecsMessage> OnDeleteAsync(SecsMessage message, CancellationToken cancellationToken)
    {
        if (!ItemTransferCodec.TryReadItemId(message.Item, out var itemId))
        {
            return Refuse(18, "ItemDeleteAcknowledge", "DELACK", "S21F17");
        }

        var acknowledgement = await _store.DeleteAsync(itemId, cancellationToken).ConfigureAwait(false);

        return Reply(18, "ItemDeleteAcknowledge", ItemTransferCodec.CodeBody((byte)acknowledgement, "DELACK"));
    }

    // ---------------------------------------------------------------- the pieces

    private static SecsMessage Reply(byte function, string name, SecsItem body)
        => new(21, function, replyExpected: false) { Name = name, Item = body };

    private uint NextDataId() => (uint)Interlocked.Increment(ref _dataId);

    private SecsMessage Refuse(byte function, string name, string item, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read; answering {Item} Denied.",
            primary,
            item);

        return Reply(function, name, ItemTransferCodec.CodeBody((byte)ItemAcknowledgement.Denied, item));
    }

    private SecsMessage Nothing(byte function, string name, string primary)
    {
        _logger.LogWarning(
            "{Primary} arrived with a body this end could not read, and its reply has no code to say so; answering a zero-length list.",
            primary);

        return Reply(function, name, SecsItem.L());
    }

    private ItemAcknowledgement ReadAcknowledgement(SecsMessage reply, string what)
    {
        var code = ItemTransferCodec.ReadCode(reply.Item)
            ?? throw new SecsException($"{what} carried no acknowledgement code.");

        if (code == (byte)ItemAcknowledgement.Accepted)
        {
            return ItemAcknowledgement.Accepted;
        }

        if (code > (byte)ItemAcknowledgement.Denied)
        {
            // None of stream 21's acknowledgement items has a table of values, so there is nothing
            // to call this but a refusal and nowhere for the code to go but the log.
            _logger.LogInformation(
                "{What} carried acknowledgement code {Code}. That item has no table of values, so anything but zero is a refusal with no reason.",
                what,
                code);
        }

        return ItemAcknowledgement.Denied;
    }

    private async ValueTask<SecsMessage> ExchangeAsync(SecsMessage primary, CancellationToken cancellationToken)
    {
        // The primary is built by the caller and of no use afterwards, so it is disposed here. The
        // reply belongs to whoever reads it, which is this class in every case.
        using (primary)
        {
            return await _connection.SendAsync(primary, cancellationToken).ConfigureAwait(false);
        }
    }
}
