# Barbatos.SecsGem.ControlPrograms - API reference

Namespace `Barbatos.SecsGem.ControlPrograms`, except where noted. Concepts and worked examples live
in [`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`ControlProgramHost`](#controlprogramhost)
  - [`ControlProgramEquipment`](#controlprogramequipment)
- [The store](#the-store)
  - [`IControlProgramStore`](#icontrolprogramstore)
  - [`ControlProgramInMemoryStore`](#controlprograminmemorystore)
- [The program](#the-program)
  - [`ControlProgram`](#controlprogram)
- [Codes](#codes)
  - [`ControlProgramGrant`](#controlprogramgrant)
  - [`ControlProgramAcknowledgement`](#controlprogramacknowledgement)
- [Registration](#registration)
- [What is not here](#what-is-not-here)

## The two ends

Stream 8 runs one way: the host sends all eight primaries and the equipment sends all eight
replies.

### `ControlProgramHost`

`sealed class ControlProgramHost`

| Member | Notes |
| --- | --- |
| `ControlProgramHost(HsmsConnection connection, ILogger<ControlProgramHost>? logger = null)` | Throws `ArgumentNullException` for a null connection. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | **Empty.** Nothing in stream 8 arrives at a host unasked; public so the claim can be asserted. |
| `ValueTask<ControlProgramGrant> InquireAsync(uint length, CancellationToken = default)` | S8F1. Names nothing - the boot program is anonymous. `SecsException` when the reply carries no `GRANT`. |
| `ValueTask<ControlProgramAcknowledgement> SendAsync(ReadOnlyMemory<byte> body, CancellationToken = default)` | S8F3. Written as a `B`. **Plain bytes, not an encoded item.** |
| `ValueTask<(ControlProgramGrant Grant, ControlProgramAcknowledgement? Acknowledgement)> SendProgramAsync(ReadOnlyMemory<byte> body, CancellationToken = default)` | S8F1 then S8F3. The acknowledgement is `null` when the inquire was refused - there was no S8F3, so there is no `ACKC8`. |
| `ValueTask<ControlProgram?> RequestAsync(string controlProgramId, CancellationToken = default)` | S8F5. `null` when the machine has none - S8F6 has no code to refuse with. |
| `ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken = default)` | S8F11. An unreadable reply comes back empty and logged. |
| `ValueTask<ControlProgramAcknowledgement> AbortAsync(string controlProgramId, CancellationToken = default)` | S8F13. Aborting is not deleting. |
| `ValueTask<string?> RevisionAsync(string controlProgramId, CancellationToken = default)` | S8F15. The one transaction stream 8 has that stream 7 does not, and the cheap way to find out whether a program is there. |

A reply that echoes a different `CPID` is **logged and still returned** - the same choice
`Barbatos.SecsGem.Objects` makes about an S2F64 echoing the wrong `DATAID`, though the mistake here
is worse than most: it would flash one program under another's name.

### `ControlProgramEquipment`

`sealed class ControlProgramEquipment`

Takes **no connection**: it has no primary to send.

| Member | Notes |
| --- | --- |
| `ControlProgramEquipment(IControlProgramStore store, ILogger<ControlProgramEquipment>? logger = null)` | Throws `ArgumentNullException` for a null store. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S8F1, F3, F5, F11, F13 and F15 - six of stream 8's eight pairs. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the six. The store is asked before the reply bit is looked at. |

## The store

### `IControlProgramStore`

`interface IControlProgramStore`

| Member | Message |
| --- | --- |
| `ValueTask<ControlProgramGrant> InquireAsync(uint length, CancellationToken)` | S8F1 |
| `ValueTask<ControlProgramAcknowledgement> SaveAsync(ReadOnlyMemory<byte> body, CancellationToken)` | S8F3 - **no identifier**, because S8F3 carries none |
| `ValueTask<ReadOnlyMemory<byte>?> LoadAsync(string controlProgramId, CancellationToken)` | S8F5 - `null` goes out as a zero-length `CPBODY` |
| `ValueTask<IReadOnlyList<string>> ListAsync(CancellationToken)` | S8F11 |
| `ValueTask<ControlProgramAcknowledgement> AbortAsync(string controlProgramId, CancellationToken)` | S8F13 |
| `ValueTask<string?> RevisionAsync(string controlProgramId, CancellationToken)` | S8F15 - `null` goes out as an empty `SOFTREV` |

Half can refuse and half cannot: S8F2, S8F4 and S8F14 carry a code, and S8F6, S8F12 and S8F16 carry
none.

### `ControlProgramInMemoryStore`

`sealed class ControlProgramInMemoryStore : IControlProgramStore`

| Member | Notes |
| --- | --- |
| `const string BootProgramId` | The empty string. S8F3 sends a program with no name and something has to be done with it; this class's choice, not E5's. |
| `int Capacity { get; set; }` | Default 16, counting the named programs only. |
| `int MaxLength { get; set; }` | Default 4 MiB; zero means no limit. Checked against the declared `LENGTH`. |
| `int Count { get; }` | Programs held, the boot program included. |
| `uint? GrantedLength { get; }` | What the last granted inquire declared, so `SaveAsync` can compare it with what arrives. |
| `void Add(string controlProgramId, ReadOnlyMemory<byte> body, string revision = "")` | Puts a named program in without a transaction. |
| `ControlProgram? Find(string controlProgramId)` | What is held. |

It enforces inquire-before-send, **checks the declared length against the bytes that arrive** - the
thing stream 13 cannot do - keeps what is stored when a transfer is aborted, and leaves the boot
program out of the directory because it has no name a host could ask with.

### `ControlProgramFileStore`

`sealed class ControlProgramFileStore : IControlProgramStore`

One file per program, written through and renamed into place, adopting whatever is in the directory
when it is constructed. The mechanism is `DurableRecordDirectory`, shared as source with the recipe,
data set and item stores. **One store per directory.**

| Member | Notes |
| --- | --- |
| `ControlProgramFileStore(string directory, ILogger<ControlProgramFileStore>? logger = null)` | Creates and scans the directory **here** rather than on first use, so a wrong path fails at start-up. `Capacity` is not enforced on what is adopted. |
| `string Directory { get; }` | Where the records live, as a full path. |
| `int Capacity { get; set; }`, `int MaxLength { get; set; }` | As the in-memory store's, and **not durable**: this machine's configuration, not anything a host said. |
| `int Count { get; }` | How many programs are held, the boot program included. |
| `uint? GrantedLength { get; }` | What the last inquire declared. **Null on every open** - a grant is a transaction in flight. |
| `bool Has(string controlProgramId)` | Whether a program is held, where `ControlProgramInMemoryStore.Find` hands back the program: the body is in a file, so existence and bytes are two costs and two members. |
| `ValueTask AddAsync(string controlProgramId, ReadOnlyMemory<byte> body, string revision = "", CancellationToken cancellationToken = default)` | **The only way a named program gets in**, because S8F3 is anonymous and no row of stream 8 names a program a host can create. Replaces one of the same name by rewriting its own record. |
| `const string RecordExtension`, `PartialExtension` | `.program` and `.writing`. |
| `const string BootProgramId` | Defined from `ControlProgramInMemoryStore.BootProgramId`, so a machine swapping one store for the other finds its boot program in the same place. |

No `Capture`/`Restore` pair, and for two reasons now: a control program is not state a machine
claims about itself, and this is durable through an implementation, so a snapshot would be a third
mechanism over two.

## The program

### `ControlProgram`

| Member | Item |
| --- | --- |
| `required string Id` | `CPID` - undefined, written `A`, read off `PPID`'s slot |
| `required ReadOnlyMemory<byte> Body` | `CPBODY` - undefined, written `B`, read off `PPBODY`'s slot |
| `bool IsEmpty` | How S8F6 says the machine has no such program |
| `override string ToString()` | `BOOT-A, 8 byte(s)`, or `the boot program, 8 byte(s)` |

**`Body` is the program, not an encoded item.** `Barbatos.SecsGem.DataSets` uses the same C# type
for the opposite thing, because its payload has no format to read off.

## Codes

### `ControlProgramGrant`

`GRANT`, quoted - and from S8F2, the only one of the seven messages that name `GRANT` the reference
documented. Agrees with `MaterialGrant` member for member, which a test asserts.

| Member | Value |
| --- | --- |
| `Accepted` | 0 |
| `Busy` | 1 |
| `NoSpace` | 2 |
| `DuplicateOrInvalid` | 3 - and the nearest the table comes to "that made no sense" |
| `Other` | 4 - the table's own reserved range, 4 to 63 |

### `ControlProgramAcknowledgement`

`ACKC8`, which has **no dictionary entry and no table of values**.

| Member | Value |
| --- | --- |
| `Accepted` | 0 - read off eight defined `ACKCn` siblings, all of which have 0 = accepted |
| `Denied` | 1 - and every code above it, logged, because nothing names any of them |

**No `Other` member, deliberately.** With one named code, a third member would be inventing a
distinction between kinds of silence.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`.

| Member | Notes |
| --- | --- |
| `ISecsGemBuilder AddControlProgramEquipment(this ISecsGemBuilder)` | The equipment side with `ControlProgramInMemoryStore`. |
| `ISecsGemBuilder AddControlProgramFileEquipment(this ISecsGemBuilder, string directory)` | With `ControlProgramFileStore`, which outlives the process. The directory is a parameter because this package has no options type. |
| `ISecsGemBuilder AddControlProgramEquipment<TStore>(this ISecsGemBuilder)` | With a store of your own. `TryAdd`, so one already in the container wins. Declares the six. |
| `ISecsGemBuilder AddControlProgramEquipment(this ISecsGemBuilder, IControlProgramStore store)` | With a store you have already built - one with limits of its own, or one a machine has loaded a named program into. |
| `ISecsGemBuilder AddControlProgramHost(this ISecsGemBuilder)` | Registers `ControlProgramHost` and **nothing else** - one of two registrations here that add no route, the other being `AddDataSetHost`. |

No deferred factory on the equipment side: it takes no connection, so there is no cycle to break.

## What is not here

- **S8F7 to S8F10**, the blocked send and request. Their `BLKNUM` and `BLKEND` have no dictionary
  entries, and neither does any other block-transfer item in the catalogue - seven for seven.
- Any message that deletes a control program. Stream 8 has none; stream 7 has S7F17 and stream 8 has
  no equivalent.
- A durable store. `IControlProgramStore` is the mechanism, and there is no snapshot pair on
  purpose.
