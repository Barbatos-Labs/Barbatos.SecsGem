# Barbatos.SecsGem.Objects

Object services - **SEMI E39**, across both the streams it uses - for **.NET 8, 9 and 10**.

**Stream 14:** `GetAttr` and `SetAttr` for one object or many, the three namelists that say which
object types exist, which instances of a type exist and which attributes an object has, and the
actionable half - create an object, delete one, tell one to do something.

**The enhanced half of stream 2:** the same questions GEM asks a machine, addressed to one object
instead. A command, a report binding, equipment constants, status variables, data variables, alarms
and diagnostics - each with an `OBJSPEC` in front, because a cluster tool has four chambers and
S1F3 has nowhere to put the question "which one".

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Objects
```

Depends on `Barbatos.SecsGem.Hsms` and not on `Barbatos.SecsGem.Gem`.

## This package does not define any objects, and that is the point

Stream 14 is a protocol over an object model it does not own. The objects belong to other
standards - E87 names a carrier, E90 a substrate, E94 a control job - and to the application that
implements them. So the library carries the messages and **your application supplies the objects**,
through one interface: five methods to implement and three with defaults.

A library that shipped its own carrier type would be shipping a guess at somebody else's standard,
and the day you implement E87 properly you would have to delete it.

It also means you can answer stream 14 for objects of your own invention, and that is not a misuse:
E5 does not restrict the object types. A machine that exposes its chambers and its load ports as
objects with attributes is using the stream for exactly what it is.

## Stream 14: one class, because that stream has no sides

E5 marks the attribute messages as travelling either way. A host reads a carrier's attributes from
a machine; a machine reads a control job's attributes from the host. Neither end is the passive
one, so there is no `ObjectEquipment` and no `ObjectHost` - there is `ObjectServices`, which asks
and answers, and an end that only asks is one built without a provider.

The other half of this package is the opposite, and the catalogue is why: every primary from S2F49
to S2F64 is marked host to equipment, so there *is* a passive end and an active one, and they get
`EnhancedServicesHost` and `EnhancedServicesEquipment`. Same standard, same package, different
shape - because the two streams have different directions and a library that hid that would be
tidier than the thing it describes.

```csharp
// An end that asks and answers: it has objects.
services.AddSecsGem(builder.Configuration).UseTcp().AddObjectProvider<MyObjects>();

// An end that only asks: no provider, and nothing declared in the routing table. An entity with
// no objects should answer S9F5 rather than answer every GetAttr with an empty list.
services.AddSecsGem(builder.Configuration).UseTcp().AddObjectServices();
```

The three namelists are the exception: E5 has those travel host to equipment, so an end answering
S14F13 is playing the equipment. Nothing here enforces it - the stream is generic and so is this -
but it is worth knowing which way round the standard expects them.

## Asking

```csharp
var objects = provider.GetRequiredService<ObjectServices>();

foreach (var type in await objects.GetObjectTypesAsync())              // S14F13
{
    foreach (var instance in await objects.GetInstancesAsync(type))     // S14F15
    {
        foreach (var name in await objects.GetAttributeNamesAsync(instance, type))   // S14F17
        {
            Console.WriteLine($"{type} {instance} has {name}");
        }
    }
}
```

Reading attributes takes a callback, and that is deliberate:

```csharp
string? lot = null;

var found = await objects.GetAttributesAsync(                           // S14F1
    "CAR-001",
    "Carrier",
    ["LotId", "SlotCount"],
    values =>
    {
        // Copy here. An attribute value is a view into the reply, and the reply is disposed when
        // this method returns - so handing the items back would be handing back memory that has
        // gone.
        lot = values.Attributes.First(a => a.Name == "LotId").Value.GetString();
    });

if (!found)
{
    // Not a failure. S14F2 carries attributes and no acknowledgement code - the item list has
    // none - so an object the other end does not have is simply an empty list.
}
```

An empty attribute list asks for all of them. That is this library reading a standard that gives no
explicit marker for "everything": a zero-length `ATTRID` list is the only thing such a request can
look like, and a provider is free to treat it as an error instead.

Several objects at once:

```csharp
var count = await objects.GetAttributesAsync(                           // S14F19
    ["CAR-001", "CAR-002", "CAR-003"],
    ["SlotCount"],
    values => slots[values.ObjectSpecification] = values.Attributes[0].Value.FirstValueOrDefault<byte>());

// An object left out of the answer is one the other end does not have. Compare count against what
// you asked for.
```

## Writing

```csharp
var acknowledgement = await objects.SetAttributesAsync(                 // S14F3
    "CAR-001",
    "Carrier",
    [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-2026-1115") }]);
```

`OBJACK` is unusual among E5 acknowledgements: three of its five codes name which part of the
request was wrong - `InvalidObjectType`, `InvalidObjectInstance`, `InvalidAttribute` - so a caller
can tell a typo in an attribute name from an object that is not there.

The same values onto several objects in one transaction:

```csharp
foreach (var result in await objects.SetAttributesAsync(                // S14F21
    ["CAR-001", "CAR-002"],
    [new ObjectAttribute { Name = "LotId", Value = SecsItem.A("LOT-SHARED") }]))
{
    Console.WriteLine($"{result.ObjectSpecification}: {result.Acknowledgement}");
}
```

## Acting, creating, deleting

The half that makes stream 14 more than a property bag. An object with a state machine - a control
job, a carrier - is driven by *actions* rather than by having its state assigned:

```csharp
var acknowledgement = await objects.ActAsync(                           // S14F9
    "CJ-1",
    "ControlJob",
    "Start");

// Several objects, one command. S14F27 carries no object type at all.
foreach (var result in await objects.ActAsync(["CJ-1", "CJ-2"], "Stop"))  // S14F27
{
    Console.WriteLine($"{result.ObjectSpecification}: {result.Acknowledgement}");
}
```

`RCMD` is an `A` item, so a command is a string and the vocabulary belongs to whatever standard
defines the object. A provider refuses what it does not know; nothing here keeps a fab from asking.

An object can also be brought into being and taken out of it:

```csharp
var created = await objects.CreateAsync("ControlJob",                   // S14F5
[
    new ObjectAttribute { Name = "ObjID", Value = SecsItem.A("CJ-1") },
    new ObjectAttribute { Name = "CarrierInputSpec", Value = SecsItem.L(SecsItem.A("CAR-001")) },
]);

Console.WriteLine(created.ObjectId);                                    // the machine names it

await objects.DeleteAsync("CJ-1", "ControlJob");                        // S14F7
```

**The equipment names the object**, not the asker: S14F6 carries an `OBJID` back. A host that wants
a particular name sets it as an attribute and leaves the machine to agree or refuse - which is also
how it learns the name of a job it did not name.

These three are **default interface methods** on `IObjectProvider`: acting answers an empty list,
and creating and deleting refuse with `InvalidObjectType`. That is the honest answer from a provider
whose objects are the machine's own hardware - a host cannot create a load port by asking - and it
means a provider written before this half existed still compiles.

## Who owns an attribute value

`ATTRDATA` is the one item in E5 with no format of its own - an attribute is whatever the object
model says it is, including a nested structure - so a value is a whole `SecsItem`. Two rules cover
every direction:

- **A value you hand this library becomes the library's**, and is disposed with the message it
  travels in. That covers a value returned from a provider and one passed to a set method, so build
  a fresh item for each.
- **A value this library hands you is a live view** into the message it arrived in, and is gone
  when the call returns. Copy anything you keep.

Both rules are already used elsewhere in this stack: the first is what a GEM status variable's read
delegate does, and the second is what every event argument here carries.

## Answering

```csharp
public sealed class MyObjects : IObjectProvider
{
    public ValueTask<IReadOnlyList<ObjectAttributeValues>> GetAttributesAsync(
        ObjectAttributeRequest request,
        CancellationToken cancellationToken)
    {
        List<ObjectAttributeValues> values = [];

        foreach (var specification in request.Objects)
        {
            if (_carriers.TryGetValue(specification, out var carrier))
            {
                values.Add(new ObjectAttributeValues
                {
                    ObjectSpecification = specification,

                    // Fresh items: the reply owns them and disposes them.
                    Attributes = [new ObjectAttribute { Name = "LotId", Value = SecsItem.A(carrier.LotId) }],
                });
            }

            // An object left out is an object this end does not have. There is no code in S14F2
            // to say anything else.
        }

        return ValueTask.FromResult<IReadOnlyList<ObjectAttributeValues>>(values);
    }

    // ... four more: the three namelists and the set. Acting, creating and deleting have
    // defaults, so a provider with no behaviour to drive writes nothing for them.
}
```

One interface serves both the single-object and the multi-object message - the single one arrives
as a request naming one object - so a provider implements each read once.

`AddObjectProvider()` with no type argument uses `ObjectInMemoryProvider`, which holds attribute
values as encoded bytes (an item belongs to its message, and a store outlives every message it
sees). It is strict by default: a set cannot invent an attribute an object does not already have,
which `AllowNewAttributes` turns off.

**Providers compose.** A machine implementing E87, E90 and E94 calls three registrations, one per
package, and each of them adds a provider; `CompositeObjectProvider` makes them one. Reads and sets
concatenate, so every provider answers for its own objects; a create or a delete goes to the first
one that does not say "not mine". The eleven primaries are declared once however many providers
there are, and an object nobody owns comes back as `InvalidObjectInstance` rather than as silence.

This was the one real fault these chapters turned up: the registration used to keep the first
provider and drop the rest, so a machine doing E87 and E90 answered carrier attributes and no
substrate attributes at all, with nothing anywhere saying so.

## The enhanced half of stream 2: asking *which chamber*

GEM can ask a machine for `SVID` 1234 and the machine has one answer. A cluster tool has four
chambers and therefore four answers, and S1F3 has nowhere to put the question. These eight
transactions are GEM's own requests with an object in front - the same identifiers, the same
codes, the same reply shapes, scoped:

| Transaction | What it scopes | GEM's unscoped version |
| --- | --- | --- |
| S2F49/F50 | a remote command, `RCMD` with `CPNAME`/`CPVAL` | S2F41/F42 |
| S2F51/F52, S2F53/F54 | tying a report to an object, and untying it | nothing: GEM's reports are the machine's |
| S2F55/F56 | equipment constants, `ECID` | S2F13/F14 |
| S2F57/F58 | status variables, `SVID` | S1F3/F4 |
| S2F59/F60 | data variables, `VID` | nothing of its own |
| S2F61/F62 | alarms, `ALID` with `ALCD` and `ALTX` | S5F5/F6 |
| S2F63/F64 | diagnostic data, `DATAID` with `DGDATA` | nothing of its own |

The catalogue settles the question of whether that reading is a guess. `HCACK`, the code S2F50
carries, is defined there with **six** values - and the sixth is *no such object exists*, which
nothing in GEM can ever return, because S2F41 commands the equipment and there is only one of
those. A code for "no such object" is the reference data saying out loud that a command is
expected to be addressed to something.

```csharp
// A host, which declares nothing: every primary in this range is host to equipment.
services.AddSecsGem(builder.Configuration).UseTcp().AddEnhancedServices();

// ...
var result = await enhanced.CommandAsync("PM1", "PP-SELECT",
    [new ObjectParameter { Name = "PPID", Value = "RECIPE-7" }]);

// HCACK 4 is a success. Read IsAccepted rather than comparing to nought.
Console.WriteLine(result.IsAccepted ? "on its way" : $"refused: {result.Acknowledgement}");

await enhanced.GetStatusVariablesAsync("PM1", [1234, 1235], values =>
{
    foreach (var value in values)
    {
        // A zero-length item means the machine does not have that one.
        Console.WriteLine(value.Count == 0 ? "(absent)" : value.ToString());
    }
});
```

**The replies are positional, and that is the one trap worth knowing.** S2F56, S2F58 and S2F60
carry values and no identifiers, so the n-th value answers the n-th identifier and a variable the
machine does not have is a **zero-length item in its place** rather than a gap - the rule GEM
follows for S1F4 and S2F14, where dropping it instead would silently shift every later value by
one. This library pads a short answer from a provider and trims a long one, so a malformed
provider cannot put a host's readings out of step, and it says so in the log when a *peer* sends a
count that does not match. The alarm reply is not positional: its entries carry their own `ALID`,
so an alarm the machine does not have is simply absent.

An empty identifier list asks for everything, which is also GEM's rule for S1F3 and S2F13.

### Answering it

```csharp
services.AddSecsGem(builder.Configuration)
    .UseTcp()
    .AddEnhancedServiceProvider<MyChambers>()
    .RunAsHostedService();
```

`IEnhancedServiceProvider` has eight methods and **all of them have defaults**, so an equipment
implements the requests its machine actually serves. `EnhancedInMemoryProvider` is enough for a
simulator, as `ObjectInMemoryProvider` is for stream 14.

Two differences from stream 14 are deliberate and worth stating, because both would otherwise look
like oversights:

- **One provider, not several.** Object providers compose because stream 14's object types belong
  to several standards at once - E87 brings carriers, E90 substrates, from packages that know
  nothing about each other. These replies are positional, so two providers each answering half a
  request cannot be merged into one list. The data here is one application's.
- **The control state is yours to check.** This package does not depend on GEM and cannot see
  whether the equipment is on-line remote, so an implementation of `CommandAsync` that performs
  work must gate it the way `GemRemoteCommand.RequiresRemoteControl` does. Otherwise a host can
  drive a machine an operator has taken offline. The E87 package says the same about its carrier
  actions, for the same reason.

The eight primaries collide with none of GEM's nine in the same stream - S2F13, F15, F17, F29, F31,
F33, F35, F37 and F41 - so an equipment calling `AddGemEquipment` and
`AddEnhancedServiceProvider` has one routing table with nothing lost in it, and a test asserts that
rather than a comment claiming it.

## What came from the standard and what did not

Of stream 14's twenty-eight catalogue rows, exactly **one** - S14F1 - is marked as a definition
rather than part of a representative subset. So the bodies here are read off the item lists and the
formats of the items, and they are a careful reading rather than a quotation; the whole table is in
`Internal/ObjectCodec.cs`, which is the only file to change if a machine disagrees.

One thing that subset got right: it touches **no undefined item**. `ENABLE`, the one name in stream
14 with no dictionary entry, appears only in S14F23, which is not implemented here. That holds for
the actionable half as well: `OBJSPEC`, `OBJTYPE`, `RCMD`, `CPNAME`, `CPVAL`, `OBJID`, `OBJACK`,
`ATTRID` and `ATTRDATA` are all defined, which is why the E94 package on top of this one can say
its *wire* is checked even though its state machine is not.

Reading is looser than writing. An attribute list is accepted whether its entries are two-element
lists or a flat run of name-and-value pairs; a namelist is accepted whether it is a list or one bare
item; an acknowledgement is accepted in any numeric format, and in a reply that carries only the
code. What is refused is a body that cannot be read without guessing - an odd number of flat
attribute fields, for one, where pairing them would attach a name to the wrong value.

The enhanced stream 2 does **better** on provenance, and it is the only part of this repository
that can be said of: **two** of its sixteen rows, S2F49 and S2F50, are definitions rather than a
representative subset. Better still, the fourteen representative ones have a pattern to check
against rather than prose - every one of them is the GEM message it scopes with `OBJSPEC` in front,
and that was checked against this repository's own GEM package rather than against a document.
Where a reading disagreed with GEM's, GEM's won.

Three names in that range have **no dictionary entry at all**, and each is inferred from a defined
item doing the same job, which the codec says out loud:

- `RPACK` and `URACK`, the two bind acknowledgements, read as `B[1]` with nought meaning accepted -
  from `DRACK`, `LRACK` and `ERACK`, the acknowledgements of the three report messages E30 *does*
  define, all three of which are shaped exactly that way. They share one enum here, because
  declaring two identical enums to mirror two undefined names would be pretending to knowledge
  nobody has - and `EnhancedReportBindResult` carries the raw byte so a fab whose host uses another
  code still has it.
- `DGDATA` is a whole item, for the same reason `ATTRDATA` is: an undefined item has no format of
  its own, and diagnostic data is whatever the machine produced.

`CPACK` is defined, but only from 1 upwards - E5 expects that list to carry the parameters that
*failed* - so nought and four are this library's, named as the E40 package named the same item.

## What is not here yet

- **S14F11 and S14F12** (attribute relations) and **S14F23 to S14F26** (attribute enable, object
  error reports). All representative in the catalogue, and `ENABLE` among them has no definition at
  all - which is reason enough to leave S14F23 alone until a machine needs it.
- **An `OBJSPEC` parser.** E39 gives the object specifier a syntax - a type and an identifier, and
  a path when objects nest - and everything here treats it as an opaque string, which is what the
  catalogue's `OBJSPEC|A|` licenses and no more. A fab whose hierarchy needs parsing does it in its
  own provider, and when one convention is clearly the usual one it belongs here.
- **A bridge from these requests to GEM's own stores.** `AddEnhancedServiceProvider` wants a
  provider because GEM's variables are not addressed by object; turning a `GemEquipment`'s
  constants and status variables into per-object ones is a mapping with more than one sensible
  answer, so it is not made here quietly. It is the shape of a join - see
  [`Barbatos.SecsGem.MaterialFlow`](../Barbatos.SecsGem.MaterialFlow/README.md) for how this
  repository writes those down.
- A provider that outlives the process. `ObjectInMemoryProvider` and `EnhancedInMemoryProvider` are
  in memory, as they say.

The object models themselves live in their own packages, which is the right order to have built
them in: [`Barbatos.SecsGem.Carriers`](../Barbatos.SecsGem.Carriers/README.md) for E87,
[`Barbatos.SecsGem.Substrates`](../Barbatos.SecsGem.Substrates/README.md) for E90 and
[`Barbatos.SecsGem.ControlJobs`](../Barbatos.SecsGem.ControlJobs/README.md) for E94. The first two
only read; E94 is the one that needed create, delete and act, and it is worth knowing that the
actionable half of this package exists because a standard asked for it rather than for completeness.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
