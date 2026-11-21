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
| `static IReadOnlyList<(byte Stream, byte Function)> HandledMessages` | S15F1, F5, F7, F9, F13, F15, F17, F31 and F53 - nine of stream 15's twenty-seven pairs, and the nine made entirely of defined items. |
| `ValueTask HandleAsync(SecsTransaction transaction, CancellationToken = default)` | Answers one of the nine. The store is asked **before** the reply bit is looked at, so a recipe sent without it is still stored. |

A body this end cannot read is answered `Rejected` **and logged**. The log is the only place the
distinction lives: `RMACK` has one refusal code, so "I could not read that" and "no" come back as
the same byte.

## The store

### `IRecipeStore`

Nine methods, one per pair. Each is awaited inside the transaction the host is waiting on.

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

- **S15F3/F4, the recipe directory**: the reply needs `RCPVER`, `RCPATTRNAME` and `RCPATTRVAL`,
  none defined. A host can list namespaces but not the recipes in one.
- **S15F19 to S15F30**, the rename, copy, lock, unlock and attributes: the rename's `RCPIDNEW` is
  an exact anagram of the defined-and-unused `RCPNEWID`, and the copy's `RCPSPECDST` and `RCPIDDST`
  would need a suffix route the reference contradicts.
- **Eleven further pairs**, each needing at least one name the dictionary does not define.
- **Stream 19 and stream 20**, which are different standards. Stream 19 was refused; stream 20 is a
  system layer above this one and would sit on a package like this rather than replace it.
- A store that outlives the process.
