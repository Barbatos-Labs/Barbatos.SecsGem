# Barbatos.SecsGem.Recipes

Recipe management - SECS-II stream 15 - for **.NET 8, 9 and 10**: the namespace directory, create
and delete, the recipe download in its two transactions, the upload, the delete, the select and the
operation abort.

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

Two of this package's nine pairs - the select and the operation abort, F31 and F53 - lie outside
the range stream 19 reaches at all, because stream 19 stops at F20.

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
nine lean on five items:

```
RCPSPEC   A[n]  defined, the namespace - streams 15 and 20
RCPID     A[n]  defined, the recipe    - streams 15, 19 and 20
LENGTH    U4    defined, named by eight streams, three of its carriers definitions
PPBODY    B     defined, STREAM 7'S OWN - four of its fifteen carriers are definitions
RMACK     B[1]  defined, WITH A TABLE: 0 accepted, 1 rejected, 2-63 reserved
```

Every figure on this page is asserted in `SecsCatalogStream15Tests`, so a refresh of the catalogue
data has to walk past them rather than quietly make the page wrong.

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

## Why the other eighteen pairs are out - the chapter's finding

The rename and the copy were meant to be in. They are not, and the measurement is the reason.

**The reference defines twenty-four items whose names begin `RCP`. Its messages use two of them.**
The other twenty-two are defined, with formats and in two cases with code tables, and no message in
the whole reference names them. Every row this package leaves out reaches for a name the dictionary
does **not** define while a near-twin of it sits there unused:

| the row uses | the dictionary defines | relation |
| --- | --- | --- |
| `RCPIDNEW` (S15F19, rename) | `RCPNEWID` `A[n]` | **exact anagram** |
| `NEWRCPID` (stream 20) | `RCPNEWID` `A[n]` | **exact anagram** |
| `RCPSTATE` (S15F33) | `RCPSTAT` `U1[1]` | prefix |
| `RCPVER` (S15F4, F36, F45) | `RCPVERS` `A[n]` | prefix |
| `VALACK` (S15F44) | `VLAACK` `B[1]` | **exact anagram**, and `VLAACK` is in use |

Across the whole reference, **74** names are used by a message and defined nowhere, and exactly
**three** of those are an anagram of something the dictionary defines. **Two of the three are
recipe names.** The same concept is spelled `RCPIDNEW` by stream 15, `NEWRCPID` by stream 20, and
`RCPNEWID` by the definition neither of them uses.

That is not a gap in a vocabulary. It is one vocabulary spelled several ways, and a library that
picked a spelling would be correcting the source. Get it backwards on a rename and a recipe is
written under a name nobody asked for.

A suffix route would be weak even without a rival: of the twenty-five pairs where the dictionary
defines both a name and that name plus a suffix, only **eleven** keep the format - worse than a coin
flip. And the counter-example is inside this very vocabulary, where `RCPBODYA` is an `A[n]` and
`RCPBODY` is a `B[n]`.

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

`IRecipeStore` is nine methods, one per pair: `ListNamespacesAsync`, `CreateNamespaceAsync`,
`DeleteNamespaceAsync`, `RequestDownloadAsync`, `SaveAsync`, `LoadAsync`, `DeleteAsync`,
`SelectAsync`, `AbortAsync`. Each is awaited inside the transaction the host is waiting on, so none
should be slow - writing a recipe to flash while a host holds its T3 open is how a download times
out.

A recipe read off the wire is **copied** before it reaches the store, because the item it arrived in
borrows pooled memory and is released with its message.

### Four rules `RecipeInMemoryStore` makes up

Seven of the nine replies carry one `RMACK` and no way to say why, so every refusal looks like every
other on the wire. Each of these is therefore tested against the store, where the *reason* can be
pinned:

- **A download must be granted before it is sent**, and the grant is keyed on the namespace *and*
  the identifier - a recipe granted for `DEV` must not land in `PROD`.
- **`LENGTH` is compared with what arrives**, as an equality: a recipe shorter than declared is as
  wrong as one longer.
- **A namespace must exist**, checked at the grant *and again* at the send. Two transactions mean
  the machine's state can change in between, and without the second check a recipe would land in a
  namespace S15F2 does not list.
- **A namespace that still holds recipes is not deleted.** S15F17 names no recipe, so one message
  could otherwise take a whole directory with it. The host has to delete the recipes first.

The last two have no counterpart in streams 8, 13 or 21, because stream 15 is the only one of the
four with a **two-level space**: it can create and delete the container as well as the contents.

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
among the eighteen left out.

An `RMACK` item carrying **no value** throws rather than falling through to a zero. Zero means
accepted, so a machine that sent `B,0` said nothing, and nothing is not yes.

## What is not here yet

- **S15F3/F4, the recipe directory.** The request is clean; the reply needs `RCPVER`,
  `RCPATTRNAME` and `RCPATTRVAL`, none defined. So a host can list namespaces but not the recipes
  inside one - the gap that will matter most to anybody using this package, and the one that cannot
  be closed without choosing a spelling.
- **S15F19 to S15F30, the rename, copy, lock, unlock and attributes.** See the finding above.
- **S15F11/F12 the verify, S15F43/F44 the validate, S15F33/F34 the execution status, S15F35/F36 the
  current recipe, S15F37/F38 the existence check, S15F39/F40 the namespace space, S15F41/F42 the
  auto-select, S15F45 to S15F52 the revision compare, history, owner and namespace attributes.**
  Each needs at least one name the dictionary does not define.
- A store that outlives the process. Nine methods, and a file behind them is an afternoon.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
