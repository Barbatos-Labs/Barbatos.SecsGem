# Barbatos.SecsGem.Recipes - API reference

Namespace `Barbatos.SecsGem.Recipes`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`RecipeHost`](#recipehost)
  - [`RecipeEquipment`](#recipeequipment)
- [The store](#the-store)
  - [`IRecipeStore`](#irecipestore)
  - [`RecipeInMemoryStore`](#recipeinmemorystore)
- [The recipe](#the-recipe)
  - [`Recipe`](#recipe)
  - [`RecipeDownloadRequest`](#recipedownloadrequest)
  - [`RecipeLockToken`](#recipelocktoken)
- [Codes](#codes)
  - [`RecipeAcknowledgement`](#recipeacknowledgement)
- [Registration](#registration)
- [What is not here](#what-is-not-here)

## The two ends

Stream 15 runs one way: the host sends all twenty-seven primaries and the equipment sends all
twenty-seven replies. **This package implements stream 15 and declares no stream 19 route** - see
the README on the name collision between the two.

### `RecipeHost`

`sealed class RecipeHost`

| Member | Notes |
| --- | --- |
| `RecipeHost(HsmsConnection connection, ILogger<RecipeHost>? logger = null)` | Throws `ArgumentNullException` for a null connection. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | **Empty.** Nothing in stream 15 arrives at a host unasked; public so the claim can be asserted. |
| `ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken = default)` | S15F1. The one primary here with no body at all. An unreadable reply comes back empty and logged. |
| `ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(string recipeNamespace, CancellationToken = default)` | S15F15. |
| `ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(string recipeNamespace, CancellationToken = default)` | S15F17. **The most destructive message here**: it names no recipe. |
| `ValueTask<RecipeAcknowledgement> RequestDownloadAsync(RecipeDownloadRequest request, CancellationToken = default)` | S15F5. Unlike stream 8's inquire, it **names** what is coming. |
| `ValueTask<RecipeAcknowledgement> SendAsync(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body, CancellationToken = default)` | S15F7. Written as a `B` - **plain bytes, not an encoded item.** |
| `ValueTask<(RecipeAcknowledgement Grant, RecipeAcknowledgement? Acknowledgement)> DownloadAsync(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body, CancellationToken = default)` | S15F5 then S15F7. The acknowledgement is `null` when the grant was refused - there was no S15F7, so there is no second `RMACK`. |
| `ValueTask<(RecipeAcknowledgement Acknowledgement, Recipe? Recipe)> UploadAsync(string recipeNamespace, string recipeId, CancellationToken = default)` | S15F9. The recipe is `null` when the machine refused, **which it can say**: S15F10 carries a code and a body together. |
| `ValueTask<RecipeAcknowledgement> DeleteAsync(string recipeNamespace, string recipeId, CancellationToken = default)` | S15F13. |
| `ValueTask<RecipeAcknowledgement> SelectAsync(string recipeNamespace, string recipeId, CancellationToken = default)` | S15F31. Selecting is not running - stream 15 has no message that starts a process. |
| `ValueTask<RecipeAcknowledgement> AbortAsync(string recipeNamespace, string recipeId, CancellationToken = default)` | S15F53. Aborting is not deleting. |
| `ValueTask<RecipeAcknowledgement> RenameAsync(string recipeNamespace, string recipeId, string newRecipeId, CancellationToken = default)` | S15F19. **One namespace, two identifiers**, because the row carries one `RCPSPEC` where the copy carries two - so a rename stays where the recipe is. `RCPIDNEW` is undefined and carried anyway: both of its readings are an `A[n]`, the live `RCPID` beside it in the body and the unused `RCPNEWID` it is an anagram of, so the rivalry puts no byte in doubt. |
| `ValueTask<RecipeAcknowledgement> CopyAsync(string sourceNamespace, string sourceId, string destinationNamespace, string destinationId, CancellationToken = default)` | S15F21. Never leaves the machine, which is the point of having it. `RCPSPECDST` and `RCPIDDST` each extend a defined name two positions earlier in the same body and **neither has a competing definition anywhere**, which makes this the firmest of the package's three inferences. |
| `ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(string recipeNamespace, string recipeId, CancellationToken = default)` | S15F23. The token is `null` when the machine refused, and - **with a warning in the log** - when it accepted and sent none, which leaves a host holding a lock it cannot release. **Keep the token:** S15F25 is the only way back and stream 15 has no message that asks what locks a machine holds. |
| `ValueTask<(string RecipeId, RecipeVersion? Version)?> RequestCurrentRecipeAsync(string recipeNamespace, CancellationToken = default)` | S15F35. **The read-back of `SelectAsync`, and the only one this stream has**: a host that restarts has no record of what the machine is set up to run, and sending S15F31 again would change the selection rather than report it. `null` is nothing selected, which S15F36 says with a zero-length body because it carries no `RMACK` - so this is the one method here that throws no `SecsException` for a body with nothing in it. A `Version` of `null` means the machine sent none, which is a different fact and a different body. |
| `ValueTask<RecipeAcknowledgement> UnlockAsync(string recipeNamespace, string recipeId, RecipeLockToken token, CancellationToken = default)` | S15F25. The token goes back out as the item it came in, which is what makes an undefined name safe here: the machine that minted it is the only end that has to understand it. No code for "wrong token" - S15F26 carries one `RMACK`. |

Every method throws `SecsException` when the reply carried no `RMACK`, including when the item
arrived with no value in it - zero means accepted, so silence must not read as a yes.

An `RMACK` in the reserved range 2 to 63 reads as `Other` and the code goes to the log. A code of
exactly 1 is the refusal the table names, so nothing is logged for it.

### `RecipeEquipment`

`sealed class RecipeEquipment`

Takes **no connection**: it has no primary to send.

| Member | Notes |
| --- | --- |
| `RecipeEquipment(IRecipeStore store, ILogger<RecipeEquipment>? logger = null)` | Throws `ArgumentNullException` for a null store. |
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S15F1, F5, F7, F9, F13, F15, F17, F19, F21, F23, F25, F31, F35 and F53 - **fourteen** of stream 15's twenty-seven pairs. Nine are made entirely of defined items; the other five carry one undefined name each. **Six of the fourteen lie outside the function range stream 19 reaches at all**, where it was two of nine before the rename and the copy went in. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the thirteen. The store is asked **before** the reply bit is looked at, so a recipe sent without it is still stored. |

A body this end cannot read is answered `Rejected` **and logged**. The log is the only place the
distinction lives: `RMACK` has one refusal code, so "I could not read that" and "no" come back as
the same byte.

## The store

### `IRecipeStore`

**Fourteen** methods, one per pair. Each is awaited inside the transaction the host is waiting on.

**Nine have no body and four do**, and the line between them is when they arrived rather than what
they do: the nine shipped with this interface, and the rename, the copy, the lock and the unlock are
default implementations that refuse, so a store written against the nine still compiles.
`IObjectProvider` in `Barbatos.SecsGem.Objects` is the same arrangement for the same reason.

| Member | Notes |
| --- | --- |
| `ValueTask<IReadOnlyList<string>> ListNamespacesAsync(CancellationToken)` | S15F1. `RCPSPEC` has no vocabulary in the reference, so what a namespace is called is the two ends' own business. |
| `ValueTask<RecipeAcknowledgement> CreateNamespaceAsync(string recipeNamespace, CancellationToken)` | S15F15. |
| `ValueTask<RecipeAcknowledgement> DeleteNamespaceAsync(string recipeNamespace, CancellationToken)` | S15F17. |
| `ValueTask<RecipeAcknowledgement> RequestDownloadAsync(RecipeDownloadRequest request, CancellationToken)` | S15F5. The cheap place to say no. |
| `ValueTask<RecipeAcknowledgement> SaveAsync(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body, CancellationToken)` | S15F7. The body is already copied out of the arriving message. |
| `ValueTask<(RecipeAcknowledgement Acknowledgement, ReadOnlyMemory<byte> Body)> LoadAsync(string recipeNamespace, string recipeId, CancellationToken)` | S15F9. **The one reply here carrying a code and data together.** |
| `ValueTask<RecipeAcknowledgement> DeleteAsync(string recipeNamespace, string recipeId, CancellationToken)` | S15F13. |
| `ValueTask<RecipeAcknowledgement> SelectAsync(string recipeNamespace, string recipeId, CancellationToken)` | S15F31. |
| `ValueTask<RecipeAcknowledgement> RenameAsync(string recipeNamespace, string recipeId, string newRecipeId, CancellationToken)` | S15F19. **Defaulted: refuses.** The rename stays inside `recipeNamespace`, because the row carries one `RCPSPEC`. |
| `ValueTask<RecipeAcknowledgement> CopyAsync(string sourceNamespace, string sourceId, string destinationNamespace, string destinationId, CancellationToken)` | S15F21. **Defaulted: refuses.** |
| `ValueTask<(RecipeAcknowledgement Acknowledgement, RecipeLockToken? Token)> LockAsync(string recipeNamespace, string recipeId, CancellationToken)` | S15F23. **Defaulted: refuses.** A store that accepts and returns no token leaves S15F24 carrying a code and an empty item, which a host reads as a lock it cannot release - return one or refuse. What a lock *means* is the store's too: nothing in stream 15 says a locked recipe cannot be deleted. |
| `ValueTask<(string RecipeId, RecipeVersion? Version)?> CurrentAsync(string recipeNamespace, CancellationToken)` | S15F35. **Defaulted: answers `null`** - and not a refusal, because S15F36 has no code to refuse with. Per namespace, since S15F35 carries one `RCPSPEC` and asks about it: a selection somewhere else is not this namespace's answer. `RecipeInMemoryStore` reports no version, which is honest rather than incomplete - nothing in stream 15 *sets* a recipe's version, so a store holding only what the wire gave it has none. |
| `ValueTask<RecipeAcknowledgement> UnlockAsync(string recipeNamespace, string recipeId, RecipeLockToken token, CancellationToken)` | S15F25. **Defaulted: refuses.** The token comes back as the item it went out as, so compare it with `RecipeLockToken.Matches` or against whatever the store keeps beside its own locks. |
| `ValueTask<RecipeAcknowledgement> AbortAsync(string recipeNamespace, string recipeId, CancellationToken)` | S15F53. |

### `RecipeInMemoryStore`

`sealed class RecipeInMemoryStore : IRecipeStore`

| Member | Notes |
| --- | --- |
| `uint MaximumLength { get; set; }` | The largest recipe it will take. Default one mebibyte. A declared length beyond it is refused at S15F5. |
| `int Count { get; }` | How many recipes are held, across every namespace. |
| `int GrantedCount { get; }` | How many downloads have been granted and not yet sent. |
| `Recipe? Selected { get; }` | The recipe most recently selected, or `null`. Observable because selecting is the one operation here whose whole effect is a change of state. |
| `void AddNamespace(string recipeNamespace)` | Adds a namespace without a transaction. |
| `void Add(string recipeNamespace, string recipeId, ReadOnlyMemory<byte> body)` | Puts a recipe in without a transaction, **creating the namespace** if it is not there - how a simulator offers a recipe a host can fetch, since stream 15 has no message announcing one. |
| `Recipe? Find(string recipeNamespace, string recipeId)` | `null` when there is none. |
| `bool HasNamespace(string recipeNamespace)` | Whether it is there. |
| `int LockedCount { get; }` | How many recipes are locked. Observable for the reason `GrantedCount` is: a lock is state kept between two transactions, and S15F24 and S15F26 each carry one code, so a test reading only the wire could not tell a lock that was taken from one that was refused. |
| `bool IsLocked(string recipeNamespace, string recipeId)` | Whether it is. Says nothing about **which** lock, deliberately: handing the token to whoever asks would make it worthless, and S15F23 has no way to ask for a lock somebody else holds. |

Four of its rules are **its own choices, not E5's** - see the README. Each is tested against the
store, because on the wire every refusal looks like every other.

No `Capture`/`Restore` pair, on purpose: a recipe is not state a machine claims about itself.

## The recipe

### `Recipe`

`sealed class Recipe`

| Member | Notes |
| --- | --- |
| `required string Namespace { get; init; }` | `RCPSPEC`, an `A[n]` - stream 15's own vocabulary, named by one other stream. |
| `required string Id { get; init; }` | `RCPID`, an `A[n]`. |
| `ReadOnlyMemory<byte> Body { get; init; }` | `PPBODY`, **stream 7's own defined `B`**. Plain bytes, not an encoded item. |
| `bool IsEmpty { get; }` | True when there are no bytes. An honest question here, unlike in `ItemPayload`: the bytes *are* the recipe. |

### `RecipeDownloadRequest`

`sealed class RecipeDownloadRequest`

| Member | Notes |
| --- | --- |
| `required string Namespace { get; init; }` | Which namespace it is going into. |
| `required string Id { get; init; }` | What it will be called. |
| `uint Length { get; init; }` | How large the host says it is - **and this one can be checked against what arrives**, because `PPBODY` is a `B`. A request that left it off reads as zero. |

### `RecipeLockToken`

`sealed class RecipeLockToken`

A lock on a recipe - `RCPLOCKID` - as the **encoded item that carried it**. The same shape
`DataSetPayload` and `ItemPayload` have, and for a stronger reason than either: `RCPLOCKID` is a
spelling of nothing at all. The longest prefix it shares with any definition in the reference is
`RCP`, which all twenty-four recipe definitions share, so there is no sibling, no anagram and no
stem to read a format off.

**Carrying it opaquely loses nothing, which is the whole argument for serving the lock.** The
machine mints the token in S15F24 and reads it back in S15F25 - *the end that must read it is the
end that wrote it* - so nothing in between needs to know what it is. S15F49's `RCPOWNER` fails that
test, which is why the owner stays out: the host would choose the format and the machine would parse
it, and the nearest definition is a `BOOLEAN` where a name would be text.

| Member | Notes |
| --- | --- |
| `ReadOnlyMemory<byte> Encoded { get; }` | The encoded item: header and payload, as it crossed the wire. Empty for a lock reply that carried no token. |
| `SecsFormat? Format { get; }` | The format that arrived, when it is known. `null` only for a token built by `FromEncoded` without one being given. |
| `int? Count { get; }` | How many values the item carried, when known. |
| `bool IsEmpty { get; }` | No bytes, or an item carrying no values. **Not the same as having no bytes**: an encoded empty item still has a format byte and a length byte in front of it. |
| `static RecipeLockToken From(SecsItem item)` | Keeps an item by encoding it - one copy, because the item borrows pooled memory and is released with its message. The item stays the caller's to dispose. |
| `static RecipeLockToken FromEncoded(ReadOnlyMemory<byte> encoded, SecsFormat? format = null, int? count = null)` | Wraps bytes that are already an encoded item, without copying. The way back in for a store that wrote its locks to disk. |
| `static RecipeLockToken FromText(string token)` | Mints one as an `A[n]`. **Text is the store's choice and not the reference's**; nothing in this package depends on the format. |
| `SecsItem? Decode()` | A fresh item, the caller's to dispose, or `null` when there are no bytes. |
| `bool Matches(RecipeLockToken? other)` | Whether the encoded bytes are identical. **The bytes are the whole comparison**, because there is no defined format to canonicalise: a host that hands back the item it was given matches, and one that re-encoded the value in another format does not - which is the right answer, since nothing says the two mean the same thing. |

### `RecipeVersion`

`sealed class RecipeVersion`

A recipe's version - `RCPVER` - as the **encoded item that carried it**, which is
`RecipeLockToken`'s shape and deliberately a second type rather than a shared one: a lock and a
version are different things to a host, and a type called a lock carrying a version would be worse
than two that each say what they hold.

**`RCPVER` is undefined and its only rival is shared.** The dictionary has one version item,
`RCPVERS`, an `A[n]` that no message names - and S15F45 needs *two* distinct version formats from
it, which is why the revision compare stays out. Here there is one version, so a format could be
read off that rival; the item is carried untouched anyway, because a version is the machine's own
string and nothing in this library has a use for its value.

**Carried opaquely although it travels the way an owner does**, which is worth stating because that
direction is what keeps S15F49 out. An owner is a field the far end must *parse* to act on, and its
two readings put different bytes out. A version is a label: a host prints it, or compares it with
`Matches`, and neither needs a format. That is `V`'s position in stream 6, which
`Barbatos.SecsGem.Gem` carries untouched in six rows.

| Member | Notes |
| --- | --- |
| `ReadOnlyMemory<byte> Encoded { get; }` | The encoded item, as it crossed the wire. |
| `SecsFormat? Format { get; }` | The format that arrived, when known. |
| `int? Count { get; }` | How many values the item carried, when known. |
| `bool IsEmpty { get; }` | No bytes, or an item carrying no values - the second of which is what a machine keeping no version puts on the wire. |
| `static RecipeVersion From(SecsItem item)` | Keeps an item by encoding it. One copy; the item stays the caller's to dispose. |
| `static RecipeVersion FromEncoded(ReadOnlyMemory<byte> encoded, SecsFormat? format = null, int? count = null)` | Wraps bytes that are already an encoded item, without copying. |
| `static RecipeVersion FromText(string version)` | Keeps one as an `A[n]` - the reading `RCPVERS` would give, which makes it a small choice rather than a free one. |
| `SecsItem? Decode()` | A fresh item, the caller's to dispose, or `null` when there are no bytes. |
| `bool Matches(RecipeVersion? other)` | Whether the encoded bytes are identical. |
| `string ToString()` | **The text, when it arrived as text** - unlike a lock token, a version is something a person reads. Otherwise the format and the encoded length. |

## Codes

### `RecipeAcknowledgement`

`enum RecipeAcknowledgement : byte` - `RMACK`, **quoted from a defined table**.

| Member | Value | Notes |
| --- | --- | --- |
| `Accepted` | 0 | |
| `Rejected` | 1 | The only refusal, with no way to say why. |
| `Other` | 2 | Everything in the reserved range 2 to 63, logged. |

**Three members, and the third is honest here** where `ItemAcknowledgement` deliberately has two:
`RMACK` has a table and that table names a reserved range, so a code this library cannot interpret
is a distinction the source makes. The value is 2 because that is where the reserved range starts.

## Registration

Namespace `Microsoft.Extensions.DependencyInjection`.

| Member | Notes |
| --- | --- |
| `ISecsGemBuilder AddRecipeEquipment(this ISecsGemBuilder builder)` | Backed by `RecipeInMemoryStore`. |
| `ISecsGemBuilder AddRecipeEquipment<TStore>(this ISecsGemBuilder builder) where TStore : class, IRecipeStore` | Backed by a store of your own. The store goes in with `TryAdd`, so one already in the container wins. |
| `ISecsGemBuilder AddRecipeHost(this ISecsGemBuilder builder)` | Registers `RecipeHost` and **nothing else** - no route, because every primary is the host's. |

No deferred factory on the equipment side, because that end takes no `HsmsConnection`: there is no
cycle between the end, the connection and the router to break. The three one-way streams - 8, 13
and 15 - are the only registrations here that can be written that way.

## What is not here

Fourteen pairs, and the README gives them row by row with which of four reasons refuses each. In
brief:

- **S15F3/F4, the recipe directory**: `RCPATTRNAME` and `RCPATTRVAL` against the unused
  `RCPATTRID`/`RCPATTRDATA`, which say an attribute value is an `A[n]` where E39's `ATTRDATA` - which
  this repository does serve - is a list. Two readings, different bytes. A host can list namespaces
  but not the recipes in one, and this is the gap that matters most to anybody using this package.
- **S15F27 to S15F30 and S15F51/F52**, the attributes: the same two names.
- **S15F49/F50**, the owner: `RCPOWNER` against the unused `RCPOWCODE`, a `BOOLEAN` where a name
  would be text - and the host is the end that would choose.
- **S15F33/F34, S15F37/F38, S15F39/F40, S15F41/F42, S15F45/F46**: an undefined field that **is** the
  message, so carrying it untouched leaves the row uninterpretable.
- **S15F35/F36 and S15F47/F48**: no served sibling settles the nesting, and a body guessed wrong is
  unparseable rather than merely uninterpretable.
- **S15F11/F12 and S15F43/F44**: `CHECKSUM`, which the reference defines under no spelling in any of
  the three streams that ask for it, and `VALACK`, whose table is real and whose row is S2F46.
- **Stream 19 and stream 20**, which are different standards. Stream 19 was refused; stream 20 is a
  system layer above this one and would sit on a package like this rather than replace it.
- A store that outlives the process.
