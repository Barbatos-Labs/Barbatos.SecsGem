# Barbatos.SecsGem.DataSets - API reference

Namespace `Barbatos.SecsGem.DataSets`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`DataSetHost`](#datasethost)
  - [`DataSetEquipment`](#datasetequipment)
- [The store](#the-store)
  - [`IDataSetStore`](#idatasetstore)
  - [`DataSetInMemoryStore`](#datasetinmemorystore)
- [The payload](#the-payload)
  - [`DataSetPayload`](#datasetpayload)
- [Bodies](#bodies)
  - [`DataSetOpenRequest`](#datasetopenrequest)
  - [`DataSetOpening`](#datasetopening)
  - [`DataSetContent`](#datasetcontent)
  - [`DataSetRequest`](#datasetrequest)
  - [`DataSetStatus`](#datasetstatus)
- [Codes](#codes)
  - [`DataSetGrant`](#datasetgrant)
  - [`DataSetAcknowledgement`](#datasetacknowledgement)
  - [`DataSetOpenMode`](#datasetopenmode)
- [Registration](#registration)
  - [`DataSetServiceCollectionExtensions`](#datasetservicecollectionextensions)
- [What is not here](#what-is-not-here)

## The two ends

Stream 13 is the only stream in this stack that runs one way: the host sends all seven primaries
and the equipment sends all seven replies.

### `DataSetHost`

`sealed class DataSetHost`

The end that drives every transaction.

| Member | Notes |
| --- | --- |
| `DataSetHost(HsmsConnection connection, ILogger<DataSetHost>? logger = null)` | Throws `ArgumentNullException` for a null connection. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | **Empty.** Nothing in stream 13 arrives at a host unasked; public so the claim can be asserted in a test. |
| `ValueTask<DataSetOpening> OpenAsync(uint dataSetId, DataSetOpenMode mode, uint length = 0, CancellationToken = default)` | S13F1 or S13F5. `ArgumentOutOfRangeException` for a mode that is neither; `SecsException` when the reply carries no `GRANT6`. |
| `ValueTask<DataSetAcknowledgement> SendAsync(uint dataSetId, SecsItem data, CancellationToken = default)` | S13F3. **Takes** the item: it becomes part of the message and is released with it. |
| `ValueTask<DataSetAcknowledgement> SendAsync(uint dataSetId, DataSetPayload data, CancellationToken = default)` | S13F3 from bytes already held. Leaves the payload alone, so it may be sent again - the overload to use when forwarding. |
| `ValueTask<DataSetPayload?> GetAsync(uint dataSetId, CancellationToken = default)` | S13F7. `null` when the equipment has none, which is what an empty `DSDATA` means. `SecsException` when the reply is not a data set. |
| `ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken = default)` | S13F11. An unreadable reply comes back empty and logged rather than throwing. |
| `ValueTask<DataSetAcknowledgement> CloseAsync(uint dataSetId, CancellationToken = default)` | S13F13. Closing is not deleting. |
| `ValueTask<DataSetStatus> StatusAsync(uint dataSetId, CancellationToken = default)` | S13F15. The transaction to send first; `SecsException` when the reply is not a status. |

`DATAID` is this end's own, incremented per send or get, and not a parameter - see
[`DataSetRequest.DataId`](#datasetrequest) for why. S13F8 and S13F16 are checked for echoing the
identifiers they were asked with, and a mismatch is logged rather than thrown.

### `DataSetEquipment`

`sealed class DataSetEquipment`

The end that answers. Takes **no connection**: it has no primary to send.

| Member | Notes |
| --- | --- |
| `DataSetEquipment(IDataSetStore store, ILogger<DataSetEquipment>? logger = null)` | Throws `ArgumentNullException` for a null store. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S13F1, F3, F5, F7, F11, F13 and F15. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the seven. The store is asked before the reply bit is looked at. |

No events: the store's own methods are the arrival point.

## The store

### `IDataSetStore`

`interface IDataSetStore`

| Member | Message |
| --- | --- |
| `ValueTask<DataSetOpening> OpenAsync(DataSetOpenRequest request, CancellationToken)` | S13F1 and S13F5 |
| `ValueTask<DataSetAcknowledgement> SaveAsync(DataSetContent content, CancellationToken)` | S13F3 |
| `ValueTask<DataSetPayload?> LoadAsync(DataSetRequest request, CancellationToken)` | S13F7 - `null` is answered with an empty `DSDATA` |
| `ValueTask<IReadOnlyList<uint>> ListAsync(CancellationToken)` | S13F11 |
| `ValueTask<DataSetAcknowledgement> CloseAsync(uint dataSetId, CancellationToken)` | S13F13 |
| `ValueTask<DataSetStatus> StatusAsync(uint dataSetId, CancellationToken)` | S13F15 |

Every method is awaited inside the transaction the host is waiting on. Enforcing the open-then-send
sequence is the store's decision, not the package's.

### `DataSetInMemoryStore`

`sealed class DataSetInMemoryStore : IDataSetStore`

| Member | Notes |
| --- | --- |
| `int Capacity { get; set; }` | Default 64. An open for send beyond it is refused `NoSpace`. |
| `int Count { get; }` | Data sets held. |
| `int OpenCount { get; }` | Data sets open. |
| `void Add(uint dataSetId, DataSetPayload data, uint length = 0)` | Puts one in without a transaction - how an equipment offers a data set, since stream 13 has no primary that announces one. |
| `DataSetPayload? Get(uint dataSetId)` | What is held. |
| `bool IsOpen(uint dataSetId, out DataSetOpenMode mode)` | Whether, and how. |

It enforces the sequence, reports the length it was given rather than one computed from the
payload, and treats an open for send with nothing behind it as not a data set - neither the
directory nor the status admits to one. No `Capture` / `Restore`: see the README.

## The payload

### `DataSetPayload`

`sealed class DataSetPayload`

A data set's contents - `DSDATA` - as the encoded item that carried them. This type exists because
`DSDATA` is the one item stream 13 needs that the catalogue never defines, and because a
`SecsItem` from an arriving message is released with that message and has no clone.

| Member | Notes |
| --- | --- |
| `ReadOnlyMemory<byte> Encoded { get; }` | The encoded item: format header, length header and payload. |
| `SecsFormat? Format { get; }` | The arriving item's format. `null` for a payload built by `FromEncoded` without one. |
| `int? Count { get; }` | How many values the item carried. `null` under the same condition. |
| `bool IsEmpty { get; }` | **No bytes, or no values.** Not the same as having no bytes - a zero-length list encodes to two. |
| `static DataSetPayload From(SecsItem item)` | One copy. The item stays the caller's to dispose. |
| `static DataSetPayload FromEncoded(ReadOnlyMemory<byte> encoded, SecsFormat? format = null, int? count = null)` | No copy, for a store that already owns its bytes. |
| `SecsItem? Decode()` | A fresh item the caller disposes. `null` only when there are no bytes - an item carrying no values still decodes. |
| `override string ToString()` | `ASCII[4], 6 encoded bytes`. |

It is **not** a process program body. `Barbatos.SecsGem.Gem` hands one of those about as plain
bytes because `PPBODY` is defined as a `B`; these bytes are an item tree.

## Bodies

### `DataSetOpenRequest`

| Member | Item |
| --- | --- |
| `required uint DataSetId` | `DSID` |
| `required DataSetOpenMode Mode` | which open |
| `uint Length` | `LENGTH`, S13F1 only - handed to the store and **not checked**; see the README |

### `DataSetOpening`

| Member | Item |
| --- | --- |
| `required DataSetGrant Grant` | `GRANT6` |
| `uint Length` | `LENGTH`, carried by S13F6 only |
| `bool IsGranted` | `Grant == Ok` |

### `DataSetContent`

| Member | Item |
| --- | --- |
| `required uint DataSetId` | `DSID` |
| `required uint DataId` | `DATAID` |
| `required DataSetPayload Data` | `DSDATA` - the store's to keep |

### `DataSetRequest`

| Member | Item |
| --- | --- |
| `required uint DataSetId` | `DSID` |
| `required uint DataId` | `DATAID`, echoed in S13F8 |

`DataId` is taken to be a correlation number rather than a name for part of a data set, which is
the one inference in this package's reading of the rows: stream 13 has a *separate* item for which
part, and three other packages here already serve `DATAID` the same way.

### `DataSetStatus`

| Member | Item |
| --- | --- |
| `required uint DataSetId` | `DSID` |
| `uint Length` | `LENGTH` |
| `required DataSetAcknowledgement Acknowledgement` | `ACKC13` |
| `bool Exists` | `Acknowledgement == Accepted` |

## Codes

### `DataSetGrant`

`GRANT6`, quoted from the dictionary's own table.

| Member | Value |
| --- | --- |
| `Ok` | 0 |
| `Busy` | 1 |
| `NoSpace` | 2 - also the only refusal available for a data set the equipment does not have |
| `Other` | 3 - the table's own reserved range, 3 to 63 |

### `DataSetAcknowledgement`

`ACKC13`, whose four codes `ACKC15` corroborates exactly.

| Member | Value |
| --- | --- |
| `Accepted` | 0 |
| `NotAvailable` | 1 |
| `InvalidDataSet` | 2 - no such data set, and the code a store refuses an unopened one with |
| `InvalidData` | 3 - including a length mismatch under the store's own definition of length |
| `Other` | 4 - **not** a reserved range: this table declares none, so this is the library covering for it |

### `DataSetOpenMode`

Valued by function number.

| Member | Value |
| --- | --- |
| `Send` | 1 - S13F1 |
| `Request` | 5 - S13F5 |

## Registration

### `DataSetServiceCollectionExtensions`

Namespace `Microsoft.Extensions.DependencyInjection`.

| Member | Notes |
| --- | --- |
| `ISecsGemBuilder AddDataSetEquipment(this ISecsGemBuilder)` | The equipment side with `DataSetInMemoryStore`. |
| `ISecsGemBuilder AddDataSetEquipment<TStore>(this ISecsGemBuilder)` | With a store of your own. `TryAdd`, so one already in the container wins. Declares all seven primaries. |
| `ISecsGemBuilder AddDataSetHost(this ISecsGemBuilder)` | Registers `DataSetHost` and **nothing else** - one of two registrations in this repository that add no route, the other being `AddControlProgramHost`. |

No deferred factory on the equipment side, unlike every other end here: it takes no connection, so
there is no cycle to break.

## What is not here

- **S13F9 / F10**, the blocked send. Its `BLKNUM` has no dictionary entry, and neither does any
  other block-transfer item in the catalogue - `BLKNUM`, `BLKSIZE`, `BLKACK`, `BLKEND`,
  `CHECKSUM`, `CHKSUM`, `CKSUM`, seven for seven.
- Any message that announces a data set. Stream 13 has none; `ListAsync` and `StatusAsync` are how
  a host finds out.
- A durable store. `IDataSetStore` is the mechanism, and there is no snapshot pair here on purpose.
