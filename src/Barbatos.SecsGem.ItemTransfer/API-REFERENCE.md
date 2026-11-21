# Barbatos.SecsGem.ItemTransfer - API reference

Namespace `Barbatos.SecsGem.ItemTransfer`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The end](#the-end)
  - [`ItemTransferPeer`](#itemtransferpeer)
- [The store](#the-store)
  - [`IItemStore`](#iitemstore)
  - [`ItemInMemoryStore`](#iteminmemorystore)
- [The item](#the-item)
  - [`Item`](#item)
  - [`ItemPayload`](#itempayload)
  - [`ItemAttribute`](#itemattribute)
  - [`ItemAttributeRequest`](#itemattributerequest)
  - [`ItemTransferRequest`](#itemtransferrequest)
- [Codes](#codes)
  - [`ItemTransferGrant`](#itemtransfergrant)
  - [`ItemAcknowledgement`](#itemacknowledgement)
- [Registration](#registration)
- [What is not here](#what-is-not-here)

## The end

There is one, because every row of stream 21 travels either way. `Barbatos.SecsGem.Material` for
stream 4 is the only other package here that can say that.

### `ItemTransferPeer`

`sealed class ItemTransferPeer`

| Member | Notes |
| --- | --- |
| `ItemTransferPeer(IItemStore store, HsmsConnection connection, ILogger<ItemTransferPeer>? logger = null)` | Throws `ArgumentNullException` for a null store or connection. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S21F1, F3, F5, F7, F15 and F17 - six of stream 21's ten pairs. **Both what this end sends and what it answers**, which is true of one other list in this stack. |
| `ValueTask<IReadOnlyList<Item>> ListAsync(string itemType = "", CancellationToken = default)` | S21F1. Empty `itemType` asks for every kind. An unreadable reply comes back empty and logged. |
| `ValueTask<T> AttributesAsync<T>(ItemAttributeRequest request, Func<IReadOnlyList<ItemAttribute>, T> read, CancellationToken = default)` | S21F3. **The callback is the contract**: each value is a view into the reply, which is disposed when this returns. `SecsException` when S21F4 carried no attribute list. |
| `ValueTask<(ItemTransferGrant Grant, uint DataId)> RequestAsync(string itemId, uint length, CancellationToken = default)` | S21F5. The correlation number comes back because the send has to quote the same one; it is this end's own, incremented per request. `SecsException` when the reply carried no `GRANT`. |
| `ValueTask<ItemAcknowledgement> SendAsync(string itemId, uint dataId, SecsItem data, CancellationToken = default)` | S21F7. The item is **taken**: it goes into the message and is disposed with it. |
| `ValueTask<ItemAcknowledgement> SendAsync(string itemId, uint dataId, ItemPayload data, CancellationToken = default)` | S21F7. The overload for forwarding - the payload stays the caller's and may be sent again. |
| `ValueTask<ItemAcknowledgement> AbortAsync(string itemId, uint dataId, CancellationToken = default)` | S21F15. Aborting is not deleting. |
| `ValueTask<ItemAcknowledgement> DeleteAsync(string itemId, CancellationToken = default)` | S21F17. **The one destructive message in this package**, and the only one in the stream. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the six. The store is asked **before** the reply bit is looked at, so an item sent without it is still stored. |

A reply that answers for a different `ITEMID` is **logged and still returned** - the same choice
`Barbatos.SecsGem.Objects` makes about an S2F64 echoing the wrong `DATAID`. A caller that wants to
act on the mismatch has the log; one that wanted the attributes has them.

An acknowledgement code above 1 is a refusal and the code goes to the log, because none of this
stream's three acknowledgement items has a table of values to interpret it with. A code of exactly
1 is the refusal this package names, so nothing is logged for it.

## The store

### `IItemStore`

Six methods, one per implemented primary. Each is awaited inside the transaction the other end is
waiting on.

| Member | Notes |
| --- | --- |
| `ValueTask<IReadOnlyList<Item>> ListAsync(string itemType, CancellationToken)` | S21F1. Empty asks for every kind. `ITEMTYPE` has no vocabulary in the reference, so what a type means is the two ends' own business. |
| `ValueTask<IReadOnlyList<ItemAttribute>> AttributesAsync(ItemAttributeRequest request, CancellationToken)` | S21F3. An empty attribute list asks for all of them, which is S14F1's reading of the same item. Empty is the only refusal S21F4 has. |
| `ValueTask<ItemTransferGrant> RequestAsync(ItemTransferRequest request, CancellationToken)` | S21F5. The cheap place to say no: after the grant, the next message is the whole item. |
| `ValueTask<ItemAcknowledgement> SaveAsync(string itemId, uint dataId, ItemPayload data, CancellationToken)` | S21F7. The payload is already copied out of the arriving message. |
| `ValueTask<ItemAcknowledgement> AbortAsync(string itemId, uint dataId, CancellationToken)` | S21F15. |
| `ValueTask<ItemAcknowledgement> DeleteAsync(string itemId, CancellationToken)` | S21F17. |

### `ItemInMemoryStore`

`sealed class ItemInMemoryStore : IItemStore`

| Member | Notes |
| --- | --- |
| `int Capacity { get; set; }` | How many items may be held. Default 64. A request beyond it is refused `NoSpace`. |
| `int Count { get; }` | How many are held. |
| `int GrantedCount { get; }` | How many transfers have been granted and not yet sent. |
| `void Add(Item item, ItemPayload? data = null, IReadOnlyList<ItemAttribute>? attributes = null)` | Puts an item in without a transaction. **How a peer offers one**: stream 21 has no "I have a new item" primary, so the other end finds out with S21F1. |
| `Item? Find(string itemId)` | `null` when there is none. |
| `ItemPayload? Contents(string itemId)` | `null` when there are none. |

Three of its rules are **its own choices, not E5's**, and each is tested where it is made:

- **A transfer must be requested before it is sent**, quoting the correlation number the grant
  carried. Two transfers of one item can be in flight.
- **`LENGTH` counts values, not bytes.** The reference says nothing about what it counts.
- **A received item keeps the type it was listed with**, because nothing in stream 21 carries
  `ITEMTYPE` on a send.

No `Capture`/`Restore` pair, on purpose: an item is not state a machine claims about itself.

## The item

### `Item`

`sealed class Item`

| Member | Notes |
| --- | --- |
| `required string Id { get; init; }` | `ITEMID`, an `A` - stream 21's own vocabulary. |
| `string Type { get; init; }` | `ITEMTYPE`. Empty means unfiled; the reference gives no values. |
| `uint Length { get; init; }` | `LENGTH`. What it counts is the store's statement, not E5's. |

### `ItemPayload`

`sealed class ItemPayload`

The contents of an item, as **the encoded item that carried them**. A deliberate near-copy of
`DataSetPayload` rather than a shared type, so the two packages stay usable apart.

| Member | Notes |
| --- | --- |
| `ReadOnlyMemory<byte> Encoded { get; }` | The bytes, header and all. |
| `SecsFormat? Format { get; }` | What arrived, when it is known. |
| `int? Count { get; }` | How many values arrived. |
| `bool IsEmpty { get; }` | True when there are no bytes **or no values** - an encoded `L,0` is two bytes, so asking about bytes alone would call an empty item present. |
| `static ItemPayload From(SecsItem item)` | **Copies.** The item borrows pooled memory and is released with its message. |
| `static ItemPayload FromEncoded(ReadOnlyMemory<byte> encoded, SecsFormat? format = null, int? count = null)` | For a payload read back out of a store. |
| `SecsItem? Decode()` | A fresh item the caller owns and disposes. `null` when there are no bytes. |

### `ItemAttribute`

`sealed class ItemAttribute`

| Member | Notes |
| --- | --- |
| `required string Name { get; init; }` | `ATTRID`, an `A` - E39's item. |
| `required SecsItem Value { get; init; }` | `ATTRDATA`, an `L`. **A view into the reply** when it came from `AttributesAsync`. |

### `ItemAttributeRequest`

`sealed class ItemAttributeRequest`

| Member | Notes |
| --- | --- |
| `required string ItemId { get; init; }` | Which item. |
| `IReadOnlyList<string> Attributes { get; init; }` | Which attributes. **Empty asks for all of them.** |

### `ItemTransferRequest`

`sealed class ItemTransferRequest`

| Member | Notes |
| --- | --- |
| `required string ItemId { get; init; }` | Which item. |
| `required uint DataId { get; init; }` | The sender's correlation number. |
| `uint Length { get; init; }` | How large the sender says it is. A request that left it off reads as zero. |

## Codes

### `ItemTransferGrant`

`enum ItemTransferGrant : byte` - `GRANT`, quoted from a defined table.

| Member | Value | Notes |
| --- | --- | --- |
| `Accepted` | 0 | |
| `Busy` | 1 | |
| `NoSpace` | 2 | |
| `DuplicateOrInvalid` | 3 | Also what an unreadable S21F5 is refused with - the nearest the table comes to "that made no sense". |
| `Other` | 4 | Everything in the reserved range 4 to 63, logged. |

The same five names and numbers as `MaterialGrant` and `ControlProgramGrant`, and a test asserts
all three agree member for member. One item cannot mean two things in one repository.

### `ItemAcknowledgement`

`enum ItemAcknowledgement : byte` - `ACKC21`, `ABTACK` and `DELACK`, none of which is defined.

| Member | Value | Notes |
| --- | --- | --- |
| `Accepted` | 0 | Read off the family: 41 of the reference's 42 tabled `*ACK` items have 0 = accepted. |
| `Denied` | 1 | And so is everything above it, with the code logged. |

**Deliberately no `Other`**: with one named code, a third member would be inventing a distinction
between kinds of silence. An acknowledgement item carrying *no value* throws instead of reading as
a zero.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`.

| Member | Notes |
| --- | --- |
| `ISecsGemBuilder AddItemTransfer(this ISecsGemBuilder builder)` | Backed by `ItemInMemoryStore`. |
| `ISecsGemBuilder AddItemTransfer<TStore>(this ISecsGemBuilder builder) where TStore : class, IItemStore` | Backed by a store of your own. |

**One method, because there is one kind of end** - both ends of a link call it and both declare the
same six primaries. The store goes in with `TryAdd`, so an `IItemStore` already in the container
wins; the end is resolved inside the handler rather than inside the factory, which is what lets the
cycle between the end, the connection and the router close.

## What is not here

- **S21F9 to S21F14**, the blocked transfer and its status. All seven of the catalogue's
  block-transfer items are undefined, and the status reports a block number.
- **S21F19/F20**, the transfer complete: `CHKSUM` and `XFRACK`, both undefined, and a checksum
  cannot be carried opaquely.
- A store that outlives the process.
