# Barbatos.SecsGem.Recipes

Recipe management - SECS-II stream 15 - for **.NET 8, 9 and 10**: the namespace directory, create
and delete, the recipe download in its two transactions, the upload, the delete, the rename, the
copy, the lock and unlock, the select and the operation abort.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Recipes
```

It depends on `Barbatos.SecsGem.Hsms` and **not** on `Barbatos.SecsGem.Gem`. Stream 15 is E5 and no
part of E30, so a controller that manages recipes over a SECS link without implementing GEM can use
this on its own. A machine that does both adds it beside `AddGemEquipment`, which answers stream 7 -
and that pairing is worth knowing about, because stream 7 is where this stream's recipe body comes
from.

## **Read this first: which "Recipe Management" this is**

The reference contains **two streams called Recipe Management**, 15 and 19 - the only name collision
among its twenty streams. They use **all twenty of the same function numbers** and they mean
different transactions:

| | stream 15 | stream 19 |
| --- | --- | --- |
| F11 | Recipe **Verify** Request | Recipe **Delete** Request |
| F13 | Recipe **Delete** Request | Recipe Parameter Request |

**This package implements stream 15 and nothing else.** It declares no stream 19 route at either
end, and a test asserts that rather than leaving it to the eye. If a tool's manual says "recipe
management", check the stream number before wiring anything up: the hazard is picking the wrong
standard from a document, not an ambiguity in flight - S15F11 and S19F11 are different messages on
the wire and nothing can confuse them there.

Five of this package's thirteen pairs - the copy, the lock, the unlock, the select and the operation
abort, F21, F23, F25, F31 and F53 - lie outside the range stream 19 reaches at all, because stream
19 stops at F20. It was two of nine until the rename and the copy went in.

## Why stream 15 is here when stream 19 was refused

The previous chapter of this repository refused stream 19 and swept stream 15 in with it on a figure
the two share: **108 rows across streams 15, 19 and 20, not one of them a definition.** That figure
is provenance, and the working agreement says provenance is not a veto - stream 21 has the same zero
and ships.

The measure that decides is the item dictionary, and there the two streams are nothing alike:

| | rows | pairs needing **no** inference | rows with every field defined |
| --- | --- | --- | --- |
| **stream 15** | 54 | **9** | 31 |
| stream 20 | 34 | 2 | 15 |
| stream 17 | 14 | 2 | 4 |
| stream 19 | 20 | **0** | 3 |

A pair needs no inference when every field of the primary **and** every field of the reply is a
defined data item. Nine is three times what `Barbatos.SecsGem.ItemTransfer` shipped on, and those
nine lean on five items. **Four more pairs are served on top of them** - the rename, the copy, the
lock and the unlock - each carrying exactly one undefined name; the section below gives the grounds
for each. The five the nine lean on:

```
RCPSPEC   A[n]  defined, the namespace - streams 15 and 20
RCPID     A[n]  defined, the recipe    - streams 15, 19 and 20
LENGTH    U4    defined, named by eight streams, three of its carriers definitions
PPBODY    B     defined, STREAM 7'S OWN - four of its fifteen carriers are definitions
RMACK     B[1]  defined, WITH A TABLE: 0 accepted, 1 rejected, 2-63 reserved
```

Every figure on this page is asserted in `SecsCatalogStream15Tests`, so a refresh of the catalogue
data has to walk past them rather than quietly make the page wrong.

### What stream 20's two pairs turned out to be

The table above has said "stream 20: 2" since this page was written, and nobody had asked **which
two**. A later chapter did, and the answer is the reason stream 20 is still refused:

| stream 20 | items | the stream 15 row it is | already served |
| --- | --- | --- | --- |
| S20F17/F18 Recipe Delete | `RCPSPEC, RCPID` → `RMACK` | S15F13/F14 Recipe Delete | yes |
| S20F33/F34 Directory Create | `RCPSPEC` → `RMACK` | S15F15/F16 Namespace Create | yes |

**Item for item, in order, with the same names.** They are not two rows this package is missing;
they are two rows it already has, under different numbers. Which puts stream 20 in exactly the
position `Barbatos.SecsGem.Subsystems` describes for stream 18 - one capability, two stream
numbers, **serve one or the other** - and stream 15's is the firmer because it is whole:
thirteen pairs here against two of seventeen there.

And there is a second reason, which is about what a routing table says. Declaring two rows of
stream 20 would make the connection answer **S9F5** for the other fifteen functions - "I do stream
20, but not that function" - where leaving the stream alone answers **S9F3**, "I do not do stream
20". The first invites a host to keep asking; the second is true.
`RecipeHostingTests.Nothing_in_this_package_claims_stream_19_or_stream_20` is that decision as a
routing fact, and `SecsCatalogRecipeRegionTests` holds the measurement.

### Stream 19's refusal, restated

Refused as *rootless*, which was true and was not the reason. The reason is one name:
**`RCPNS`, the recipe namespace, which the reference never defines and which nothing else in the
reference names.** Nine of stream 19's ten primaries carry it; the tenth is S19F1, which has no
body because it is the request that asks for the *list* of namespaces, and its reply is a list of
`RCPNS`. So every transaction in the stream is addressed by a field nobody wrote down, in any
spelling - it has no rival, which is the follow-up question that would otherwise change the answer.

Stream 19 is not a worse-documented stream 15. It is a stream whose addressing is missing.

### The recipe body needs no inference at all

This has not been true in this repository before. Stream 15 carries its recipe in **`PPBODY`** -
stream 7's own process-program body - in a row whose stream 7 siblings S7F3, S7F6, S7F23 and S7F26
the reference **documented**. So:

```csharp
// stream 15 and stream 8: bytes that are the recipe, or the program
Recipe? recipe = (await host.UploadAsync("PROD", "ETCH-7", token)).Recipe;
await File.WriteAllBytesAsync("etch-7.rcp", recipe!.Body.ToArray(), token);

// streams 13 and 21: bytes that are an encoded SecsItem, header and all
using var item = payload.Decode();
```

Stream 8 had to read `CPBODY` off `PPBODY`'s **slot** because stream 8 is stream 7's design for
firmware; streams 13 and 21 had no slot to read and carry the encoded item that arrived. Here the
item **is** `PPBODY`. Four packages, three different answers, and the difference is how much the
reference wrote down.

### And `LENGTH` can be checked against what arrives

Because `PPBODY` is a `B` and therefore has a byte count. `RecipeInMemoryStore` compares the
declared length with the recipe that turns up and refuses a mismatch -
`Barbatos.SecsGem.ControlPrograms` does the same for the same reason, and
`Barbatos.SecsGem.DataSets` and `Barbatos.SecsGem.ItemTransfer` document that they cannot.

## The vocabulary the reference wrote down and never used

**The reference defines twenty-four items whose names begin `RCP`. Its messages use two of them.**
The other twenty-two are defined, with formats and in two cases with code tables, and no message in
the whole reference names them. Every row this package once left out reaches for a name the
dictionary does **not** define while a near-twin of it sits there unused:

| the row uses | the dictionary defines | relation | the definition is |
| --- | --- | --- | --- |
| `RCPIDNEW` (S15F19, rename) | `RCPNEWID` `A[n]` | **exact anagram** | used by nothing |
| `RCPIDNEW` (S15F19, rename) | `RCPID` `A[n]` | extends it | **in the same body** |
| `RCPSPECDST` (S15F21, copy) | `RCPSPEC` `A[n]` | extends it | **in the same body** |
| `RCPIDDST` (S15F21, copy) | `RCPID` `A[n]` | extends it | **in the same body** |
| `NEWRCPID` (stream 20) | `RCPNEWID` `A[n]` | **exact anagram** | used by nothing |
| `RCPSTATE` (S15F33) | `RCPSTAT` `U1[1]` | extends it | used by nothing |
| `RCPVER` (S15F4, F36, F45) | `RCPVERS` `A[n]` | its stem | used by nothing |
| `VALACK` (S15F44) | `VLAACK` `B[1]` | **exact anagram** | used by **S2F46** |

Across the whole reference, **74** names are used by a message and defined nowhere, and exactly
**three** of those are an anagram of something the dictionary defines. **Two of the three are
recipe names.** The same concept is spelled `RCPIDNEW` by stream 15, `NEWRCPID` by stream 20, and
`RCPNEWID` by the definition neither of them uses.

## Why the rename, the copy and the lock are in after all

**That table's last column is the chapter nobody had read.** For six chapters this package served
nine pairs, and the four above it were refused on the ground that a library picking a spelling would
be correcting the source. The premise is true. What does not follow is that anything changes on the
wire:

- **A SECS-II body is positional.** Item names are documentation; they never travel. The worst a
  wrong spelling can do to a rename is mislabel a comment.
- **Both readings of `RCPIDNEW` are an `A[n]`** - the live `RCPID` it extends and the unused
  `RCPNEWID` it is an anagram of. There is nothing to get backwards.
- **`RCPSPECDST` and `RCPIDDST` have no competing definition at all.** Each extends a name that is
  in the same body two positions earlier, carrying the same kind of value: source namespace, source
  identifier, destination namespace, destination identifier. That makes the copy the **firmest**
  of this package's three inferences, firmer than the rename.
- **A rename carries one `RCPSPEC` and a copy carries two.** So the rows themselves say a rename
  stays inside its namespace and a copy may cross - and a reading where `RCPSPECDST` were anything
  else would leave the copy unable to do the one thing the rename cannot, which is backwards.

**The statistic the refusal leaned on is measured over a different population.** Of the thirty pairs
where the dictionary defines both a name and that name plus a suffix, only **thirteen** keep the
format - worse than a coin flip, and the counter-example is inside this vocabulary, where `RCPBODYA`
is an `A[n]` and `RCPBODY` is a `B[n]`. But of those thirty pairs, **exactly one is ever named by a
single message** - `SV` and `SVNAME`, in S1F8 - **and it keeps its format.** The coin flip is about
names that never stand beside each other, where a format has to be guessed from the dictionary
rather than read off the row. All three of stream 15's cases stand beside their base.

### And the lock, which is a different argument

`RCPLOCKID` is **a spelling of nothing**: no anagram, no stem, no base in the body, and the longest
prefix it shares with any definition in the reference is `RCP` itself, which all twenty-four share.
So nothing is inferred and it travels as the **encoded item that arrived** - stream 13's answer
rather than stream 8's, through a `RecipeLockToken`.

That loses nothing *here*, and the reason is worth stating because it is what keeps S15F49 out:

> **An opaque field is safe when the end that must read it is the end that produced it.**

A lock identifier is minted by the machine in S15F24 and handed straight back to it in S15F25, so
nothing in between needs to know what it is - the same shape as `DSDATA` and `ITEMDATA`, which are
the sender's own data stored whole. An **owner** travels the other way: the host would choose the
format and the machine would have to read it, and the nearest definition, `RCPOWCODE`, is a
`BOOLEAN` where an owner's name would be text.

The two bodies need no guessing either, because **served siblings give them**: S15F24 is S15F10's
body item for item - a code and then a payload the dictionary does not define - and S15F25 is
S15F7's. Both of those this package already reads and writes, including the case the catalogue's
item list cannot show, which is what a refusal carries.

## Stream 15 runs one way

All twenty-seven primaries are the host's and all twenty-seven replies the equipment's. So
`RecipeHost.HandledMessages` is **empty** and `AddRecipeHost` adds no route; `RecipeEquipment`
accordingly takes **no connection**. Streams 8 and 13 are the other two like this, and those three
packages are the only ones here whose host half answers nothing at all.

Nothing in this stream ever arrives at a host unasked - there is no message saying a machine's
recipes changed.

## The host

```csharp
services
    .AddSecsGem(configure: link =>
    {
        link.DeviceId = 0;
        link.Mode = HsmsConnectionMode.Active;
    })
    .UseTcpTransport("192.168.0.30", 5000)
    .AddRecipeHost();
```

What is there, and what is in it:

```csharp
var host = provider.GetRequiredService<RecipeHost>();

foreach (var space in await host.ListNamespacesAsync(token))      // S15F1
{
    Console.WriteLine(space);
}

await host.CreateNamespaceAsync("DEV", token);                     // S15F15
```

Sending is two transactions, and this package keeps them two:

```csharp
var recipe = await File.ReadAllBytesAsync("etch-7.rcp", token);

var grant = await host.RequestDownloadAsync(                       // S15F5
    new RecipeDownloadRequest { Namespace = "PROD", Id = "ETCH-7", Length = (uint)recipe.Length },
    token);

if (grant == RecipeAcknowledgement.Accepted)
{
    var acknowledgement = await host.SendAsync("PROD", "ETCH-7", recipe, token);   // S15F7
}
```

`RequestDownloadAsync` and `SendAsync` are separate because a host that wants to see the grant
before committing megabytes is the entire reason the request exists. `DownloadAsync` does both for
a caller with nothing to decide in between:

```csharp
var (grant, acknowledgement) = await host.DownloadAsync("PROD", "ETCH-7", recipe, token);

// acknowledgement is null when the grant was refused: there was no S15F7, so there is no second RMACK.
```

**Unlike stream 8's load inquire, this request names what is coming.** S8F1 carries only a length,
because a machine has one boot program and it is anonymous; a recipe has a namespace and an
identifier.

Fetching is one transaction, and **the machine can say no**:

```csharp
var (acknowledgement, recipe) = await host.UploadAsync("PROD", "ETCH-7", token);   // S15F9

// recipe is null when the machine refused - and it CAN refuse, because S15F10 carries RMACK
// and PPBODY together. Stream 8's S8F6 has no code at all, which is why
// Barbatos.SecsGem.ControlPrograms has to advise asking the revision first.
```

Moving one about, which needs no transfer at all:

```csharp
await host.RenameAsync("PROD", "ETCH-7", "ETCH-8", token);            // S15F19 - stays in PROD
await host.CopyAsync("PROD", "ETCH-8", "DEV", "ETCH-8", token);       // S15F21 - may cross
```

**A rename stays inside its namespace and a copy may cross**, because S15F19 carries one `RCPSPEC`
and S15F21 carries two. Moving a recipe between namespaces is a copy and then a delete; nothing in
stream 15 does it in one transaction.

And holding one still while you work on it:

```csharp
var (locked, token2) = await host.LockAsync("PROD", "ETCH-8", token);  // S15F23

if (locked == RecipeAcknowledgement.Accepted)
{
    // ... and S15F25 is the only way back. Keep the token: stream 15 has no message that asks
    // what locks a machine is holding.
    await host.UnlockAsync("PROD", "ETCH-8", token2!, token);          // S15F25
}
```

**What a lock prevents is the machine's business.** E5 defines the transaction and says nothing
about what it stops - `RecipeInMemoryStore` refuses a download, a delete and a rename while a recipe
is locked, and another machine may not. Nothing on the wire says which.

And the rest:

```csharp
await host.SelectAsync("PROD", "ETCH-7", token);         // S15F31 - selecting is not running
await host.AbortAsync("PROD", "ETCH-7", token);          // S15F53 - aborting is not deleting
await host.DeleteAsync("PROD", "ETCH-7", token);         // S15F13
await host.DeleteNamespaceAsync("PROD", token);          // S15F17 - the dangerous one
```

## The equipment

```csharp
services
    .AddSecsGem(configure: link => link.Mode = HsmsConnectionMode.Passive)
    .UseTcpTransport(listenPort: 5000)
    .AddRecipeEquipment();          // or AddRecipeEquipment<MyStore>()
```

`IRecipeStore` is thirteen methods, one per pair: `ListNamespacesAsync`, `CreateNamespaceAsync`,
`DeleteNamespaceAsync`, `RequestDownloadAsync`, `SaveAsync`, `LoadAsync`, `DeleteAsync`,
`SelectAsync`, `AbortAsync`, `RenameAsync`, `CopyAsync`, `LockAsync`, `UnlockAsync`. Each is awaited
inside the transaction the host is waiting on, so none should be slow - writing a recipe to flash
while a host holds its T3 open is how a download times out.

**Nine of the thirteen have no body and four do**, and the line between them is when they arrived
rather than what they do. The nine shipped with this interface; the rename, the copy, the lock and
the unlock came later, so they are default implementations that refuse and **a store written against
the nine still compiles**. The default is `Rejected`, which is also the honest answer: a machine that
will not rename a recipe says so with the one refusal code S15F20 has, and E5 has no code for "I do
not implement this".

A recipe read off the wire is **copied** before it reaches the store, because the item it arrived in
borrows pooled memory and is released with its message.

### Seven rules `RecipeInMemoryStore` makes up

Eleven of the thirteen replies carry one `RMACK` and no way to say why, so every refusal looks like
every other on the wire. Each of these is therefore tested against the store, where the *reason* can
be pinned:

- **A download must be granted before it is sent**, and the grant is keyed on the namespace *and*
  the identifier - a recipe granted for `DEV` must not land in `PROD`.
- **`LENGTH` is compared with what arrives**, as an equality: a recipe shorter than declared is as
  wrong as one longer.
- **A namespace must exist**, checked at the grant *and again* at the send. Two transactions mean
  the machine's state can change in between, and without the second check a recipe would land in a
  namespace S15F2 does not list.
- **A namespace that still holds recipes is not deleted.** S15F17 names no recipe, so one message
  could otherwise take a whole directory with it. The host has to delete the recipes first.
- **A name that is taken is not written over** - not by a rename, not by a copy, not by a namespace
  create. A host that got an acceptance would believe both recipes still existed.
- **A locked recipe is not changed:** not sent over, not deleted, not renamed. The download is
  refused at the **grant** and again at the send, for the same reason the namespace is checked
  twice.
- **A lock is released only by the token it was taken with**, compared byte for byte - there is no
  canonical form to compare, because `RCPLOCKID` has no defined format.

The third and fourth have no counterpart in streams 8, 13 or 21, because stream 15 is the only one
of the four with a **two-level space**: it can create and delete the container as well as the
contents. The last two have no counterpart anywhere in this repository, because stream 15 is the
only stream here with a lock - and since E5 defines the transaction without saying what a lock
prevents, **everything the lock actually stops is this class's invention**. A rename also carries
one rule of its own that a delete does not: **the selection follows the recipe**, where a delete
clears it, because the machine is still set up to run the same bytes.

There is no snapshot pair, for the reason the other transfer packages give: a recipe is not state a
machine claims about itself, so it survives a restart through an implementation.

## What can be refused, and what cannot

Every reply in this package carries an `RMACK`, which is **defined** - and that is a change from the
last three chapters, where `ACKC8`, `ACKC21`, `ABTACK` and `DELACK` had no tables at all and a zero
had to be read off a family of siblings. Here the table is quoted: 0 accepted, 1 rejected, 2 to 63
reserved.

So `RecipeAcknowledgement` has **three members** and the third is honest, where
`ItemAcknowledgement` deliberately has two: a named reserved range means the reference anticipated
codes it did not define, which is the same shape `GRANT` has. A code in that range reads as `Other`
and goes to the log.

What the acknowledgement still cannot do is say **why**. There is one refusal code, and the rows
that would have carried a reason - the verify data's `VFACK`, the validate data's `VALACK` - are
among the fourteen left out.

An `RMACK` item carrying **no value** throws rather than falling through to a zero. Zero means
accepted, so a machine that sent `B,0` said nothing, and nothing is not yes.

## The fourteen pairs that are not here, row by row

Four reasons, and each row is refused by one of them. **Not one of the fourteen is refused for being
undefined**, which was the shape of the old list and was never the argument: three packages here
carry an undefined field untouched, and four rows of this one now do.

| | the reason | and what it is |
| --- | --- | --- |
| **S15F3/F4** directory | twin disagrees, **and** no sibling for the shape | `RCPATTRNAME` and `RCPATTRVAL` below, plus `RCPVER`; and a directory is a list of entries whose nesting no served row here settles |
| **S15F11/F12** verify | nothing to read | `CHECKSUM` is defined nowhere under any spelling - see the line below |
| **S15F27/F28, S15F29/F30, S15F51/F52** attributes | **twin disagrees** | `RCPATTRNAME`/`RCPATTRVAL` against the unused `RCPATTRID`/`RCPATTRDATA`, which say an attribute value is an `A[n]` - where E39's `ATTRDATA`, which this repository *does* serve, is a list, meaning any format at all. Two readings, different bytes |
| **S15F33/F34** execution status | **the field is the message** | `RCPSTATE` is the whole reply, and its only twin `RCPSTAT` is a `U1[1]` **with no code table**, so even taking the spelling as a misprint there are no states to report |
| **S15F35/F36** current recipe | **no sibling for the shape** | the gap is only `RCPVER`, which could be carried untouched - but nothing this package serves answers a request with a multi-item body carrying no acknowledgement, so there is no way to read "nothing is selected" and no row to read a nesting off |
| **S15F37/F38** existence | **the field is the message** | `RCPEXIST` is the whole answer, so carrying it untouched leaves the library unable to tell yes from no |
| **S15F39/F40** namespace space | **the field is the message** | `SPACEAVAIL` and `SPACETOTAL` are the whole reply, two numbers with no format and no units |
| **S15F41/F42** auto-select | **the field is the message** | `AUTOSEL` is the flag the row turns on - `ENABLE`'s position in S14F23, which stream 14 refuses for the same reason |
| **S15F43/F44** validate | **twin is live elsewhere** | `VALACK` is an anagram of `VLAACK`, whose table is real and whose row is **S2F46**; reading it would import another row's vocabulary. `RCPATTRNAME` besides |
| **S15F45/F46** revision compare | **twin is shared** | `RCPVER` and `RCPVER2` both reach for the one `RCPVERS`, and there is only one of it; `CMPRESULT` is the verdict and has no table |
| **S15F47/F48** revision history | no sibling for the shape | `RCPVER` and `RCPATTRVAL`, and a history is a list whose nesting no served row settles |
| **S15F49/F50** owner | **twin disagrees** | `RCPOWNER` against the unused `RCPOWCODE`, a `BOOLEAN` where an owner's name would be text - and the **host** builds this primary, so the end that would choose the format is not the end that parses it |

The shape reasons are the stream 6 chapter's rule: a row's nesting comes from a served sibling and
never from the catalogue's item list, which records the names a row's table mentions and is not a
description of the message. **A body guessed wrong is unparseable rather than merely
uninterpretable**, which ranks it below an item gap and above nothing.

**The verify is one refusal wearing three names**, which is worth a line of its own: S15F11 carries
`CHECKSUM`, S19F10 carries `CKSUM` and S21F19 carries `CHKSUM`, and the reference defines **none of
the three**. No spelling is a misspelling of something written down elsewhere - it simply never says
what a recipe checksum is - so there is nothing to carry and nothing to compare.
`SecsCatalogRecipeRegionTests` asserts all three in one breath rather than once per package.

## What is not here yet

- A store that outlives the process. Thirteen methods, and a file behind them is an afternoon.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
