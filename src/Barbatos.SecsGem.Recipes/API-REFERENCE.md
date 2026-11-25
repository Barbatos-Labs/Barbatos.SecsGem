# Barbatos.SecsGem.Recipes - API reference

Namespace `Barbatos.SecsGem.Recipes`, except where noted. Concepts and worked examples live in
[`README.md`](README.md); this page is member-by-member.

- [The two ends](#the-two-ends)
  - [`RecipeHost`](#recipehost)
  - [`RecipeEquipment`](#recipeequipment)
- [The store](#the-store)
  - [`IRecipeStore`](#irecipestore)
  - [`RecipeInMemoryStore`](#recipeinmemorystore)
  - [`RecipeFileStore`](#recipefilestore)
- [The recipe](#the-recipe)
  - [`Recipe`](#recipe)
  - [`RecipeDownloadRequest`](#recipedownloadrequest)
  - [`RecipeDirectoryEntry`](#recipedirectoryentry)
  - [`RecipeAttribute`](#recipeattribute)
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
| `ValueTask<IReadOnlyList<RecipeDirectoryEntry>> RequestDirectoryAsync(string recipeNamespace, CancellationToken = default)` | S15F3. One entry per recipe, with its version, its byte count and its attributes. **Empty covers three answers** - a namespace with no recipes, one the machine does not have, and a machine whose store serves no directory - because S15F4 carries no acknowledgement item; the equipment logs which. The attribute values are **copies** decoded out of the reply and are the caller's to dispose, since a payload of `PooledPayloadThreshold` bytes or more arrives in a buffer the codec reclaims when the message is disposed. |
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
| `ValueTask<SecsItem?> RequestAttributeAsync(string recipeNamespace, string recipeId, string attributeName, CancellationToken = default)` | S15F27. **The value comes back as a copy of the item it arrived as** - the caller's to dispose, because a payload of `PooledPayloadThreshold` bytes or more is decoded into a buffer the codec reclaims when the reply is disposed - which is what let this row be served: `RCPATTRVAL` has two candidate readings that disagree - `RCPATTRDATA` is an `A[n]` and E39's `ATTRDATA` an `L[n]` - and a SECS-II item carries its own format, so neither has to be chosen. `null` is "no such attribute", which S15F28 says with a zero-length value because the row carries no `RMACK`; it is reported as `null` rather than as an empty list so a host can tell it from an attribute set to one. |
| `ValueTask<RecipeAcknowledgement> SetAttributeAsync(string recipeNamespace, string recipeId, string attributeName, SecsItem value, CancellationToken = default)` | S15F29. One attribute per message, because the row carries one name and one value. The item is sent as given: a method taking a `string` or a `byte[]` would be choosing one of the two readings on the caller's behalf. |
| `ValueTask<SecsItem?> RequestNamespaceAttributeAsync(string recipeNamespace, string attributeName, CancellationToken = default)` | S15F51 - `RequestAttributeAsync` without the recipe, and a **copy** of the item for the same reason. `null` covers a namespace the machine does not have as well as an attribute nobody set, because S15F52 carries no acknowledgement item. Served although the reference gives a namespace attribute no set row anywhere: that is stream 21's position for an item attribute, where the value reaches the store from the application and the machine mints nothing. |
| `ValueTask<RecipeAcknowledgement> SetOwnerAsync(string recipeNamespace, string recipeId, SecsItem owner, CancellationToken = default)` | S15F49. The owner is a `SecsItem` because `RCPOWNER`'s two candidate readings disagree about every byte - a name, against the `BOOLEAN` its unused rival `RCPOWCODE` suggests - and nothing here chooses: send `SecsItem.A("mes-7")` or `SecsItem.Boolean(true)`. **No row reads an owner back**, so S15F50's single acceptance code is the only confirmation on the wire; `RecipeInMemoryStore.Owner` and `RecipeFileStore.Owner` are the other half. |
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
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the nineteen. The store is asked **before** the reply bit is looked at, so a recipe sent without it is still stored. |

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
| `ValueTask<IReadOnlyList<RecipeDirectoryEntry>?> ListRecipesAsync(string recipeNamespace, CancellationToken)` | S15F3. **Defaulted: answers `null`** - and not a refusal, because S15F4 has no code to refuse with, so a store written against an earlier version of this interface serves no directory and the row still answers. `null` is "no such namespace" and an empty list is "none in it"; a host cannot tell them apart and a direct caller can. Both stores sort by identifier, and each recipe's attributes by name, so two answers can be diffed. |
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
| `ValueTask<SecsItem?> GetAttributeAsync(string recipeNamespace, string recipeId, string attributeName, CancellationToken)` | S15F27. **Defaulted: answers `null`** - and not a refusal, because S15F28 has no code to refuse with. `RecipeInMemoryStore` hands out its own `SecsItem`; `RecipeFileStore` keeps the encoded bytes and decodes fresh per request, so two hosts asking at once never share one item. Either way the codec copies the value into the reply, because an item handed to a message is disposed with it. |
| `ValueTask<RecipeAcknowledgement> SetAttributeAsync(string recipeNamespace, string recipeId, string attributeName, SecsItem value, CancellationToken)` | S15F29. **Defaulted: refuses.** The value is **the store's** from here on: the equipment copies it out of the arriving message first, because a payload of a kilobyte or more borrows a buffer the codec reclaims when that message is disposed. An application calling this directly hands over an item it must not then dispose. |
| `ValueTask<RecipeAcknowledgement> SetAttributeAsync(string recipeNamespace, string recipeId, string attributeName, SecsItem value, CancellationToken)` | S15F29. **Defaulted: refuses.** Refused for a recipe that is not there and for one that is locked, like every other mutation here. In `RecipeFileStore` the attribute goes **inside the recipe's own record**, so setting one is a single atomic rewrite and a restart can never find a recipe without its attributes. |
| `ValueTask<RecipeAcknowledgement> SetOwnerAsync(string recipeNamespace, string recipeId, SecsItem owner, CancellationToken)` | S15F49. **Defaulted: refuses.** The owner is **the store's** from here on, as a value set at S15F29 is, and for the same pooling reason. Refused for a recipe that is not there and for one that is locked. In `RecipeFileStore` it goes in the `owners` file rather than in the record, because a record's fields are two and then a pair per attribute - an **even** count, which is what lets this version read a directory an earlier one wrote. |
| `ValueTask<SecsItem?> GetNamespaceAttributeAsync(string recipeNamespace, string attributeName, CancellationToken)` | S15F51. **Defaulted: answers `null`** - and not a refusal, because S15F52 has no code to refuse with. The value reaches a store through `SetNamespaceAttribute`, which is **the only door**: no row in the reference sets a namespace attribute, so this is `ItemStore.Add`'s position in stream 21. |
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
| `SecsItem? Owner(string recipeNamespace, string recipeId)` | What a host set at S15F49, or `null`. **The read-back the stream does not have**: `RCPOWNER` is named by exactly one row in the reference, so nothing asks who owns a recipe. The item is the store's; copy it before keeping it past the next set. |
| `void SetNamespaceAttribute(string recipeNamespace, string attributeName, SecsItem value)` | Sets what S15F51 reads, **creating the namespace** if it is not there. Not a convenience like `SetAttribute`: no row sets a namespace attribute, so without this a store could answer only with nothing. |
| `bool SetAttribute(string recipeNamespace, string recipeId, string attributeName, SecsItem value)` | Sets a recipe's attribute without a transaction; `false` when the recipe is not there. A convenience, since S15F29 exists. |

Four of its rules are **its own choices, not E5's** - see the README. Each is tested against the
store, because on the wire every refusal looks like every other.

No `Capture`/`Restore` pair, on purpose: a recipe is not state a machine claims about itself.

### `RecipeFileStore`

`sealed class RecipeFileStore : IRecipeStore`

One file per recipe, written through and renamed into place, adopting whatever is in the directory
when it is constructed - `GemFileSpool`'s shape, for `GemFileSpool`'s reasons. **One store per
directory**: two over the same one would fight over the sequence numbers. The mechanism is
`DurableRecordDirectory`, shared as source with `ControlProgramFileStore`, `DataSetFileStore` and
`ItemFileStore`; what is this store's own is the framing (a namespace, an identifier and the body)
and the rules.

| Member | Notes |
| --- | --- |
| `RecipeFileStore(string directory, ILogger<RecipeFileStore>? logger = null)` | Creates and scans the directory **here** rather than on first use, so a wrong path fails at start-up. An unfinished write is swept; a file that is not this store's, and a record this version cannot read, are left alone and counted in the opening log line. |
| `string Directory { get; }` | Where the records live, as a full path. |
| `uint MaximumLength { get; set; }` | As the in-memory store's, and **not durable**: it is this machine's configuration, not anything a host said. |
| `int Count { get; }` | How many recipes are held, across every namespace. |
| `int GrantedCount { get; }` | How many downloads have been granted and not yet sent. **Zero on every open** - a grant is a transaction in flight, so a restart ends it. |
| `int LockedCount { get; }` | How many recipes are locked. Restored, because a lock is what a host took. |
| `(string Namespace, string Id)? SelectedId { get; }` | What is selected, where `RecipeInMemoryStore.Selected` is a whole `Recipe` - the body is in a file, and a property that read a megabyte off a disk would be a trap. |
| `bool HasNamespace(string recipeNamespace)` | Whether it is there. |
| `bool IsLocked(string recipeNamespace, string recipeId)` | Whether it is. |
| `ValueTask<int> ForgetLocksAsync(CancellationToken cancellationToken = default)` | Forgets every lock and says how many. **The door E5 does not give** - no row of stream 15 asks which recipes are locked or forces one open, so a host that crashed holding a token would otherwise leave a recipe locked for as long as the store lasts. Not reachable from the wire, and logged as something nobody asked for. |
| `const string RecordExtension` | `.recipe` - what a finished record carries. |
| `const string PartialExtension` | `.writing` - appended to any file while it is being written. |
| `SecsItem? Owner(string recipeNamespace, string recipeId)` | What a host set at S15F49, **decoded fresh** out of what was written down, so two callers never share one item - `GetAttributeAsync`'s rule, and the one difference from the in-memory store. |
| `void SetNamespaceAttribute(string recipeNamespace, string attributeName, SecsItem value)` | As the in-memory store's, and **not durable**. A namespace attribute is set by no row in the reference, so it is not what a host told the machine: it is configuration, reaching the store the way the directory path does. This is the one place this store answers a host differently after a restart, and the README says so. |
| `const string NamespacesFile`, `LocksFile`, `SelectedFile`, `OwnersFile` | The four small files, each rewritten whole and renamed into place. An owner naming a recipe that is no longer there is dropped on adoption, which is the locks' rule and `Restore`'s. |

A file name is **a sequence number and nothing else**; the namespace and the identifier are inside
the record. So no name a host chose is ever part of a path, and a rename is one atomic rewrite of
one file rather than two operations with a window between them - a recipe never has two records.

No `Capture`/`Restore` pair here either, and for a second reason on top of the first: this is
durable through an implementation, so a snapshot would be a third mechanism over two. The README's
table says what a restart keeps and what it ends.

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

### `RecipeDirectoryEntry`

`sealed class RecipeDirectoryEntry`

One recipe's row in S15F4. **The shape came from a served sibling**: S7F36, the formatted process
program directory, is a list of entries, one per program, each entry being its row's items in order
and ending in a nested list of the group that repeats. This is that with a recipe's columns, which
is what let the row be served after six chapters on a shape - every item of S15F4 was already
carried here, so the nesting was the whole of what was left.

| Member | Notes |
| --- | --- |
| `required string Id { get; init; }` | `RCPID`. An entry that named no recipe is dropped on the way in, which is the reading S15F36 gives an empty `RCPID`. |
| `RecipeVersion? Version { get; init; }` | `RCPVER`, carried untouched. `null` sends a zero-length item, because nothing in stream 15 ever *sets* a version and neither store here has one. |
| `uint Length { get; init; }` | `LENGTH`, in **bytes** - no inference needed, since `PPBODY` is a defined `B`. The same quantity S15F5 declares. |
| `IReadOnlyList<RecipeAttribute> Attributes { get; init; }` | The attribute pairs, sorted by name by both stores. Empty is a zero-length list. |
| `override string ToString()` | The identifier, the size and how many attributes it has. |

### `RecipeAttribute`

`sealed class RecipeAttribute`

| Member | Notes |
| --- | --- |
| `required string Name { get; init; }` | `RCPATTRNAME`, whose format reads off the defined-but-unused `RCPATTRID`. |
| `required SecsItem Value { get; init; }` | `RCPATTRVAL`, **as the item it was set with** - never a chosen reading of it. Whose item it is to dispose depends on where the pair came from: a store hands out its own, `RecipeHost.RequestDirectoryAsync` hands out copies. |
| `override string ToString()` | The name, and the value's format and length - a value can be a list, and this has to be one line either way. |

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
| `ISecsGemBuilder AddRecipeFileEquipment(this ISecsGemBuilder builder, string directory)` | Backed by `RecipeFileStore`, which outlives the process. The directory is a parameter and not an option because this package has no options type - stream 15 has no timer and no limit of its own. |
| `ISecsGemBuilder AddRecipeEquipment<TStore>(this ISecsGemBuilder builder) where TStore : class, IRecipeStore` | Backed by a store of your own. The store goes in with `TryAdd`, so one already in the container wins. |
| `ISecsGemBuilder AddRecipeEquipment(this ISecsGemBuilder builder, IRecipeStore store)` | Backed by a store you have already built - a `RecipeFileStore` with a `MaximumLength` of its own, say. |
| `ISecsGemBuilder AddRecipeHost(this ISecsGemBuilder builder)` | Registers `RecipeHost` and **nothing else** - no route, because every primary is the host's. |

Four ways in and **one declaration** behind them, which is `DeclareSpooling`'s arrangement in the
GEM package: the routes and the end are the same whichever store answered, and a second copy of
`RecipeEquipment.HandledMessages` in a second overload is a list that can fall behind the first.

No deferred factory on the equipment side, because that end takes no `HsmsConnection`: there is no
cycle between the end, the connection and the router to break. The three one-way streams - 8, 13
and 15 - are the only registrations here that can be written that way.

## What is not here

**Eight pairs**, and the README gives them row by row with which of two reasons refuses each - it
was four reasons until the attribute pair retired one and the directory retired another, and three
until the owner and the namespace attribute went in. In brief:

- **S15F33/F34, S15F37/F38, S15F41/F42, S15F43/F44, S15F45/F46**: an undefined field that **is**
  the message, so carrying it untouched leaves the row uninterpretable. `RCPSTATE` and `RCPEXIST`
  are the whole reply; `AUTOSEL` is the flag the row turns on; `VALACK` is a verdict whose
  anagram's table belongs to S2F46, a row `Barbatos.SecsGem.Gem` serves; `CMPRESULT` has no table.
- **S15F11/F12, S15F39/F40 and S15F47/F48**: a value only the machine could produce, and it would
  have to produce it for a host to act on. A checksum is a **computation** nobody specifies - and
  stream 7's three verify rows send the whole body instead, which is why all three are served;
  `SPACEAVAIL` and `SPACETOTAL` have a format off the unused `RMSPACE` and no **unit**, where a
  host does arithmetic with them; and S15F48's third column is a value with **no name beside it**,
  where both served attribute replies carry the pair.
- The owner and the namespace attribute were in that third group until the measure was read one row
  at a time. Both cross from one end to the other and **neither has a reader**: `RCPOWNER` is named
  by exactly one row in the whole reference, and a namespace attribute's value comes from the
  application rather than from the machine, which is stream 21's position for an item attribute.
- **Stream 19 and stream 20**, which are the same capability under other numbers. Seven of stream
  19's ten pairs and thirteen of stream 20's seventeen are rows this package already answers.

And **no row here is refused for its shape**, which is a change: the directory was the last one, and
its nesting came from S7F36 the way every body in this library comes from a served sibling.
