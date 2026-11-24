# Barbatos.SecsGem.Material - API reference

Namespace `Barbatos.SecsGem.Material`, except where noted. Concepts, worked examples and the
provenance warning live in [`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`MaterialEquipment`](#materialequipment)
  - [`MaterialHost`](#materialhost)
- [Either end](#either-end)
  - [`MaterialHandoff`](#materialhandoff)
- [The inventory](#the-inventory)
  - [`IMaterialSource`](#imaterialsource)
  - [`MaterialInMemorySource`](#materialinmemorysource)
- [The model](#the-model)
  - [`MaterialLocation`](#materiallocation)
  - [`MaterialStatus`](#materialstatus)
  - [`MaterialCompletion`](#materialcompletion)
  - [`MaterialTransfer`](#materialtransfer)
  - [`MaterialStatusChange`](#materialstatuschange)
  - [`MaterialMovement`](#materialmovement)
- [Codes and events](#codes-and-events)
  - [`MaterialAcknowledgement`](#materialacknowledgement)
  - [`MaterialGrant`](#materialgrant)
  - [`MaterialSendAcknowledgement`](#materialsendacknowledgement)
  - [`MaterialReceiveAcknowledgement`](#materialreceiveacknowledgement)
  - [`MaterialHandoffStep`](#materialhandoffstep)
  - [`MaterialHandoffEventArgs`](#materialhandoffeventargs)
  - [`MaterialLocationEventArgs`](#materiallocationeventargs)
  - [`MaterialStatusChangeEventArgs`](#materialstatuschangeeventargs)
  - [`MaterialMovementEventArgs`](#materialmovementeventargs)
- [Registration](#registration)

## The two ends

### `MaterialEquipment`

`sealed class`. Answers the host's five questions out of an [`IMaterialSource`](#imaterialsource),
and sends the four reports the host does not ask for. Not disposable - no state, no timer, nothing
to release.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `MaterialEquipment(IMaterialSource source, HsmsConnection connection, ILogger<MaterialEquipment>? logger = null)` | Does not run the connection; uses it. |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S3F1, S3F3, S3F9, S3F11, S3F15. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Asks the source, then replies. |
| `ReportFoundAsync` | `ValueTask<MaterialAcknowledgement> ReportFoundAsync(MaterialLocation, CancellationToken = default)` | S3F5. |
| `ReportLostAsync` | `ValueTask<MaterialAcknowledgement> ReportLostAsync(MaterialLocation, CancellationToken = default)` | S3F7. The location is where it was last seen. |
| `ReportStatusChangeAsync` | `ValueTask<MaterialAcknowledgement> ReportStatusChangeAsync(MaterialStatusChange, CancellationToken = default)` | S3F13. |
| `ReportMovementAsync` | `ValueTask<MaterialAcknowledgement> ReportMovementAsync(MaterialMovement, CancellationToken = default)` | S3F35. The arrival an accepted S3F11 promised. |

A reply with no acknowledgement code throws `SecsException`, never `Accepted`. A body this end
cannot read is answered `Denied` on S3F12, `DuplicateOrInvalid` on S3F16, and a zero-length list on
S3F10 - which carries material and a location and has no code in it to refuse with.

### `MaterialHost`

`sealed class`. Asks the five questions, and is told the four things. Not disposable.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `MaterialHost(HsmsConnection connection, ILogger<MaterialHost>? logger = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | S3F5, S3F7, S3F13, S3F35. |
| `MaterialFound` | `event EventHandler<MaterialLocationEventArgs>?` | S3F5. |
| `MaterialLost` | `event EventHandler<MaterialLocationEventArgs>?` | S3F7. |
| `MaterialStatusChanged` | `event EventHandler<MaterialStatusChangeEventArgs>?` | S3F13. |
| `MaterialMoved` | `event EventHandler<MaterialMovementEventArgs>?` | S3F35. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | Raises the event, then sends whatever the handler left on it. |
| `RequestStatusAsync` | `ValueTask<IReadOnlyList<MaterialStatus>> RequestStatusAsync(CancellationToken = default)` | S3F1. Empty means the machine is empty. |
| `RequestTimeToCompletionAsync` | `ValueTask<IReadOnlyList<MaterialCompletion>> RequestTimeToCompletionAsync(CancellationToken = default)` | S3F3. |
| `FindMaterialAsync` | `ValueTask<MaterialLocation?> FindMaterialAsync(string materialId, CancellationToken = default)` | S3F9. `null` means the machine does not have it. |
| `RequestTransferAsync` | `ValueTask<MaterialAcknowledgement> RequestTransferAsync(MaterialTransfer, CancellationToken = default)` | S3F11. Accepted means taken, not arrived. |
| `InquireMultiBlockAsync` | `ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken = default)` | S3F15. |

A report this end cannot read is answered `Denied` inside the transaction the equipment is already
waiting on. A host that subscribes to nothing still answers every report with `Accepted`.

## Either end

### `MaterialHandoff`

`public sealed class`. Stream 4's two protocols: the interlocked handoff of S4F1 to S4F18 and the
handoff protocol of S4F27 to S4F41. **Not an end of anything**: the catalogue marks every row of
both as travelling either way, because the two parties are a sender and a receiver of material
rather than a host and a machine. Both ends of a link construct one and declare the same seventeen
primaries.

| Member | Signature | Notes |
| --- | --- | --- |
| constructor | `MaterialHandoff(HsmsConnection, ILogger<MaterialHandoff>? = null)` | |
| `HandledMessages` | `static IReadOnlyList<(byte Stream, byte Function)>` | Seventeen: the handshake's S4F1, F3, F5, F7, F9, F11, F13, F15, F17 and the protocol's S4F27, F29, F31, F33, F35, F37, F39, F41. Disjoint function numbers, so the two protocols cannot be confused for each other. The only list here that is both what a type sends and what it answers. |
| `HandoffReported` | `event EventHandler<MaterialHandoffEventArgs>?` | Every step this end is told about, including the two it answers. Raised **before** `AnswerOffer` or `AnswerRequest` is consulted, and a test pins that order - so it carries what arrived, not what this end did about it. A handler that wants both records them in the answer delegate, the one place both exist at once. |
| `AnswerOffer` | `Func<byte, string, MaterialSendAcknowledgement> { get; set; }` | What S4F2 says. Accepts by default. A delegate rather than a member on `IMaterialSource`, which would break every implementation of it - and an offer is not a question about what material this machine holds. |
| `AnswerRequest` | `Func<byte, string, MaterialReceiveAcknowledgement> { get; set; }` | What S4F18 says. Grants by default. |
| `OfferAsync` | `ValueTask<MaterialSendAcknowledgement> OfferAsync(byte portId, string materialId, CancellationToken = default)` | S4F1, answered by S4F2. Throws `SecsException` when the reply carries no `RSACK`. |
| `RequestMaterialAsync` | `ValueTask<MaterialReceiveAcknowledgement> RequestMaterialAsync(byte portId, string materialId, CancellationToken = default)` | S4F17, answered by S4F18. |
| `ReportAsync` | `ValueTask ReportAsync(MaterialHandoffStep step, byte portId, string materialId, CancellationToken = default)` | The other seven, **posted**: E5 gives them no secondary. `ArgumentOutOfRangeException` for the two that are answered. |
| `HandoffLinkAnnounced` | `event EventHandler<MaterialHandoffAnnouncementEventArgs>?` | S4F27. Its own event because it is the one row of the eight that carries a descriptor rather than acting on a link already named. A descriptor with no `TRLINK` is dropped with a logged reason rather than raised under an empty name. |
| `HandoffLinkReported` | `event EventHandler<MaterialHandoffLinkEventArgs>?` | S4F29 to S4F41. Seven rows and four shapes, so which members are populated depends on `Notice` - that type lists which. No acknowledgement to set: none of the seven is answered. |
| `AnnounceLinkAsync` | `ValueTask AnnounceLinkAsync(MaterialHandoffLink, CancellationToken = default)` | S4F27, **posted**. Where a `TRLINK` comes from. `ArgumentException` when the link names nothing. |
| `SendCommandAsync` | `ValueTask SendCommandAsync(string link, uint index, string command, IReadOnlyList<(string, string)> parameters, CancellationToken = default)` | S4F29, posted. No command set is defined here: `HOCMDNAME` is tabled nowhere, so what a handoff understands is between the two ends. |
| `ReportCommandCompleteAsync` | `ValueTask ReportCommandCompleteAsync(string link, uint index, MaterialHandoffAcknowledgement, CancellationToken = default)` | S4F31, posted. Carries the index back. |
| `ReportVerifiedAsync` | `ValueTask ReportVerifiedAsync(string link, MaterialHandoffAcknowledgement, uint errorCode = 0, string errorText = "", CancellationToken = default)` | S4F33, posted. The one row that can say why, since `HOACK` cannot. |
| `RequestCancelAsync` | `ValueTask RequestCancelAsync(string link, CancellationToken = default)` | S4F35, posted. Answered by the other end's `AcknowledgeCancelAsync`. |
| `AcknowledgeCancelAsync` | `ValueTask AcknowledgeCancelAsync(string link, MaterialHandoffAcknowledgement, CancellationToken = default)` | S4F37, posted - a primary of its own, because S4F35 has no secondary. |
| `RequestHaltAsync` | `ValueTask RequestHaltAsync(string link, CancellationToken = default)` | S4F39, posted. The reference gives this stream two ways to stop, names them differently and says nothing about the difference, so both are carried and neither is defined. |
| `AcknowledgeHaltAsync` | `ValueTask AcknowledgeHaltAsync(string link, MaterialHandoffAcknowledgement, CancellationToken = default)` | S4F41, posted. |
| `HandleAsync` | `ValueTask HandleAsync(SecsTransaction, CancellationToken = default)` | |

Every one of the eight new methods throws `ArgumentException` for an empty link: the link is the
whole of the identity in all of them, so a message without it could never be followed up.

## The inventory

### `IMaterialSource`

`interface`. Five methods, one per question the host can ask. Each is awaited inside a transaction,
so none should take long.

| Member | Signature | Message |
| --- | --- | --- |
| `GetStatusAsync` | `ValueTask<IReadOnlyList<MaterialStatus>> GetStatusAsync(CancellationToken)` | S3F1 |
| `GetTimeToCompletionAsync` | `ValueTask<IReadOnlyList<MaterialCompletion>> GetTimeToCompletionAsync(CancellationToken)` | S3F3 |
| `FindAsync` | `ValueTask<MaterialLocation?> FindAsync(string materialId, CancellationToken)` | S3F9; `null` answers a zero-length list |
| `TransferAsync` | `ValueTask<MaterialAcknowledgement> TransferAsync(MaterialTransfer, CancellationToken)` | S3F11 |
| `InquireMultiBlockAsync` | `ValueTask<MaterialGrant> InquireMultiBlockAsync(uint dataId, CancellationToken)` | S3F15 |

### `MaterialInMemorySource`

`sealed class : IMaterialSource`. Keyed by material identifier, compared with
`StringComparer.Ordinal`.

| Member | Signature | Notes |
| --- | --- | --- |
| `Load` | `bool Load(string materialId, uint location, byte quantity = 1, uint timeToCompletion = 0)` | `false` when full. |
| `Unload` | `bool Unload(string materialId)` | `false` when it was not there. |
| `Clear` | `void Clear()` | |
| `Capacity` | `int { get; set; }` | 64 by default. |
| `Count` | `int { get; }` | |
| `Grant` | `MaterialGrant { get; set; }` | `Accepted` by default. |
| `TransferAnswer` | `MaterialAcknowledgement { get; set; }` | `Accepted` by default; a denial leaves the material where it is. |

A transfer it accepts **moves** the material, so a later S3F9 reports the new location. Material it
does not hold is denied whatever `TransferAnswer` says - accepting would leave the host waiting for
an S3F35 that cannot come.

## The model

### `MaterialLocation`

`sealed class`. `MaterialId` (`required string`, `MID`) and `Location` (`uint`, `LOC`).

### `MaterialStatus`

`sealed class`. `MaterialId`, `Location`, and `Quantity` (`byte`, `QUA` - one byte, no code table
in the catalogue, so nothing here interprets it).

### `MaterialCompletion`

`sealed class`. `MaterialId`, `Location`, and `TimeToCompletion` (`uint`, `TTC`) - a number in
whatever unit the machine counts in, deliberately not a `TimeSpan`.

### `MaterialTransfer`

`sealed class`. `MaterialId` and `Destination` (`uint`, `DESTLOC` - **inferred as `U4`**; see the
README).

### `MaterialStatusChange`

`sealed class`. `MaterialId`, `Location`, and `Status` (`byte`, `MATLSTATUS` - **inferred as
`B[1]`**, codes undefined by the catalogue and so unnamed here).

### `MaterialMovement`

`sealed class`. `MaterialId`, `Location` (where it came from) and `Destination` (where it went).

## Codes and events

### `MaterialAcknowledgement`

`enum : byte` - `ACKC3`. `Accepted = 0`, `Denied = 1`. E5 defines both and no others, and no error
text anywhere in this half of the stream, so a denial cannot say why.

### `MaterialGrant`

`enum : byte` - `GRANT`. `Accepted = 0`, `Busy = 1`, `NoSpace = 2`, `DuplicateOrInvalid = 3`,
`Other = 4`. Codes 4 to 63 are the equipment's to define; all of them read as `Other` and are
logged.

### `MaterialSendAcknowledgement`

`enum : byte` - `RSACK`. `WillAccept = 0`, `InvalidPort = 1`, `PortOccupied = 2`, `Busy = 3`,
`NotInRemoteMode = 4`, `Other = 5`. **Quoted, not inferred**: the dictionary gives this item its own
table of values. Codes 5 to 63 are the equipment's and all read as `Other`, logged - never as
`WillAccept`, which is what the zero of the enum would make somebody else's refusal.

### `MaterialReceiveAcknowledgement`

`enum : byte` - `RRACK`. `Granted = 0`, `Denied = 1`, `Other = 2`. Also quoted. The refusal carries
no reason, as most of this stream's do.

### `MaterialTransferJobs`

`public sealed class`, built from an `HsmsConnection` and an optional
`ILogger<MaterialTransferJobs>`. The job half of stream 4 - S4F19 to S4F26 - and the only part of
the stream where **the two ends declare different messages**.

| Member | Signature | Notes |
| --- | --- | --- |
| `EquipmentMessages` | `static IReadOnlyList<(byte, byte)>` | S4F19, S4F21, S4F25 - what a machine answers |
| `HostMessages` | `static IReadOnlyList<(byte, byte)>` | S4F23 alone - what a host answers |
| `CreateAsync(request)` | `ValueTask<(string JobId, MaterialTransferJobResult)>` | S4F19. **The identifier is minted here** and comes back from this call; everything later addresses the job by it. Throws `SecsException` when S4F20 was not four items |
| `SendCommandAsync(jobId, command, parameters)` | `ValueTask<MaterialTransferJobResult>` | S4F21. `TRCMDNAME` is an `A` with no table, the same position `HOCMDNAME` is in - the string is carried and nothing here takes a view. Throws `SecsException` when S4F22 was not three items |
| `InquireMultiBlockAsync(dataId, length)` | `ValueTask<MaterialGrant>` | S4F25. Here rather than left to S2F39 because this package does not depend on `Barbatos.SecsGem.Gem`: a machine doing handoff and nothing else has no S2F39 to fall back on |
| `AlertAsync(jobId, state, jobName, errorCode, errorText)` | `ValueTask` | S4F23. Answered by an S4F24 with no body, so there is nothing to hand back |
| `JobAlerted` | `event EventHandler<MaterialTransferJobAlertEventArgs>?` | S4F23 at the host end |
| `AnswerCreate` | `Func<MaterialTransferJobRequest, (string, MaterialTransferJobResult)>` | **Refuses by default**, and the identifier it returns is the job's: a machine that accepted without minting one would leave a host with a job it could not command |
| `AnswerCommand` | `Func<string, string, IReadOnlyList<(string, string)>, MaterialTransferJobResult>` | Refuses by default, for the same reason |
| `AnswerMultiBlockInquiry` | `Func<uint, uint, MaterialGrant>` | **Grants by default** - a handshake whose only purpose is to let this end refuse should not refuse by silence |

---

### `MaterialTransferJobState`

`enum : byte` - `TRJOBMS`, quoted from its table: `Queued = 0`, `Selected = 1`, `Executing = 2`,
`Paused = 3`, `Completed = 4`, `Cancelled = 5`.

**No catch-all member**, because the table reserves nothing - six codes and nothing above. So a
machine reporting six is naming a state the reference does not, and the reader **refuses the alert
rather than folding it** onto a state the machine never claimed. The same position `COMMACK` is in.

This enum is also the first refusal's undoing: that one said a transfer job has no state model
anywhere in the reference, where this table had six states in it all along.

---

### `MaterialTransferJobResult`

`public readonly record struct` - `Acknowledgement` (`string`, `TRACK`), `ErrorCode` (`uint`) and
`ErrorText` (`string`).

**No verdict on it, deliberately.** `TRACK` is an `A` with no values and `ERRCODE` is a `U4` with
none, so S4F20 and S4F22 carry no tabled item at all - and this package already says of the same
`ERRCODE` at S4F33 that "nothing here interprets it". The first draft had an `IsAccepted` reading
nought from `ERRCODE` and contradicted that; all three fields now come back as they arrived.

Where a row *does* carry a tabled item this stack reads it and uses `ERRCODE` as the reason beside
it - `GemExceptionAbortResult` with S5F18's `ACKA`. This row has nothing to play that part.

---

### `MaterialTransferJobRequest`

`public sealed class` - `JobName` (`required string`), `Link` (`required MaterialHandoffLink`) and
`DataId` (`uint`).

**The link block is S4F27's, item for item**, which is how the body was settled: S4F19 carries a
`DATAID` and a `TRJOBNAME` and then the eight items `AnnounceLinkAsync` already writes, in the same
order. `MaterialHandoffLink.EquipmentName` is ignored - S4F27 has an `EQNAME` and S4F19 does not,
which is the one difference between the two blocks. One link per job.

---

### `MaterialTransferJobAlertEventArgs`

`sealed class : EventArgs` - `JobId` (`required string`), `JobName` (`string`), `State`
(`required MaterialTransferJobState`), `ErrorCode` (`uint`), `ErrorText` (`string`), and a
`ToString()` of `TJ-1 Executing`.

A plain state change carries `ErrorCode` nought; the row can also report a fault on a job, which is
what the `ERRCODE` and `ERRTEXT` beside the state are for.

---

### `MaterialHandoffStep`

`enum` - which step of the handshake, valued by its own function number: `ReadyToSend = 1`,
`SendMaterial = 3`, `HandshakeComplete = 5`, `NotReadyToSend = 7`, `StuckInSender = 9`,
`StuckInReceiver = 11`, `SendIncompleteTimeout = 13`, `MaterialReceived = 15`,
`RequestToReceive = 17`. One enum rather than nine events, because all nine carry the same body.

### `MaterialHandoffEventArgs`

`sealed class : EventArgs`. `Step` (`required MaterialHandoffStep`), `PortId` (`required byte`),
`MaterialId` (`string`, empty when the sender named none), and `ToString()` -
`ReadyToSend port 1 (LOT-7)`.

### `MaterialHandoffRole`

`enum` - `TRROLE`, and the two names are the table's own: `Other = 0`, `Source = 1`,
`Destination = 2`. The item is an `A` that arrives **with** a table, so the wire carries the word
SOURCE or DESTINATION and this package compares it ordinally. `Other` is first so that a descriptor
nobody set a role on reads as unknown: which end is holding the material is not something to
default.

### `MaterialHandoffAcknowledgement`

`enum : byte` - `Accepted = 0`, `Refused = 1`. **One enum for three items** - `HOACK`,
`HOCANCELACK` and `HOHALTACK` - which are `B[1]` with no table of values between them, so there is
nothing for three enums to disagree about. Zero is read off this stream's own `RSACK` and `RRACK`;
every non-zero code is a refusal the reference names no reason for, and the arriving code is logged.
Not called `Other`, as this package's catch-alls are: those sit above a table's named codes and
this sits above nothing.

### `MaterialHandoffNotice`

`enum` - which step of the handoff protocol, valued by its own function number: `Command = 29`,
`CommandComplete = 31`, `Verified = 33`, `CancelReady = 35`, `CancelReadyAcknowledge = 37`,
`Halt = 39`, `HaltAcknowledge = 41`. S4F27 is not here and has an event of its own. Disjoint from
`MaterialHandoffStep` by construction, and a test says so.

### `MaterialHandoffLink`

`sealed class`. The nine items S4F27 carries: `Link` (`required string`, the `TRLINK` every later
message addresses), `EquipmentName`, `ObjectName`, `ObjectType`, `Port`, `Role`
(`MaterialHandoffRole`), `PartnerName`, `PartnerPort`, `Location`, and `ToString()` -
`LINK-1 Source EQ1:LP1 -> AGV3:ARM`.

`Port` is text where the interlocked handshake's `PTN` is a `U1`. The two halves of stream 4 name a
port differently and this package does not reconcile them, because the reference does not: a
`TRPORT` of "LP1" has no `PTN`.

### `MaterialHandoffAnnouncementEventArgs`

`sealed class : EventArgs`. `Link` (`required MaterialHandoffLink`) and `ToString()`.

### `MaterialHandoffLinkEventArgs`

`sealed class : EventArgs`. `Notice` (`required MaterialHandoffNotice`), `Link`
(`required string`, always set), and then whichever of `Index`, `CommandName`, `Parameters`,
`Acknowledgement`, `ErrorCode` and `ErrorText` the row carries - the type's own documentation lists
which per notice. `Parameters` is `IReadOnlyList<(string Name, string Value)>` and **outlives the
handler**, unlike GEM's remote command parameters: `CPVAL` is an `A` here, so there is no format to
preserve and nothing borrowed from the arriving message.

### `MaterialLocationEventArgs`

`sealed class : EventArgs`. `Material` (`required MaterialLocation`) and `Acknowledgement`
(`MaterialAcknowledgement`, settable, `Accepted` by default). Used by both `MaterialFound` and
`MaterialLost`.

### `MaterialStatusChangeEventArgs`

`sealed class : EventArgs`. `Change` (`required MaterialStatusChange`) and `Acknowledgement`.

### `MaterialMovementEventArgs`

`sealed class : EventArgs`. `Movement` (`required MaterialMovement`) and `Acknowledgement`.

The payload on all three is valid for the life of the handler.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`. Extensions on `ISecsGemBuilder`.

| Method | What it adds |
| --- | --- |
| `AddMaterialEquipment()` | `MaterialEquipment`, `MaterialInMemorySource` as `IMaterialSource`, and handlers for S3F1, F3, F9, F11, F15. |
| `AddMaterialEquipment<TSource>()` | The same with an inventory of your own. `TryAdd`, so an `IMaterialSource` already registered wins. |
| `AddMaterialHost()` | `MaterialHost` and handlers for S3F5, F7, F13, F35. |
| `AddMaterialHandoff()` | `MaterialHandoff` and handlers for all seventeen - S4F1 to S4F17 and S4F27 to S4F41. **Both ends call this one**, and it composes with either of the two above: stream 3 and stream 4 share no declaration. |
| `AddMaterialTransferJobs()` | `MaterialTransferJobs` and handlers for `EquipmentMessages` - S4F19, S4F21 and S4F25. The **machine's** end. |
| `AddMaterialTransferJobHost()` | The same type and handlers for `HostMessages` - S4F23 alone. The **host's** end. A host calling the other one would be declaring that it answers its own requests. |

Either called twice is harmless: the messages go in through the builder's `DeclareOnce`, which
declares a capability's set once however many times it is asked for.

Do not call the equipment and the host on one builder, though. They declare *different* messages,
so neither the routing table nor `DeclareOnce` can catch the mistake - and one process answering as
both ends of one link is not a thing E5 describes.
