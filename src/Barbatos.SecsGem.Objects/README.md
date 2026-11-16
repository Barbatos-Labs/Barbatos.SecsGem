# Barbatos.SecsGem.Objects

Object services - SECS-II stream 14 - for **.NET 8, 9 and 10**: `GetAttr` and `SetAttr` for one
object or many, and the three namelists that say which object types exist, which instances of a
type exist, and which attributes an object has.

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
through one five-method interface.

A library that shipped its own carrier type would be shipping a guess at somebody else's standard,
and the day you implement E87 properly you would have to delete it.

It also means you can answer stream 14 for objects of your own invention, and that is not a misuse:
E5 does not restrict the object types. A machine that exposes its chambers and its load ports as
objects with attributes is using the stream for exactly what it is.

## One class, because the stream has no sides

E5 marks the attribute messages as travelling either way. A host reads a carrier's attributes from
a machine; a machine reads a control job's attributes from the host. Neither end is the passive
one, so there is no `ObjectEquipment` and no `ObjectHost` - there is `ObjectServices`, which asks
and answers, and an end that only asks is one built without a provider.

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

    // ... four more: the three namelists and the set
}
```

One interface serves both the single-object and the multi-object message - the single one arrives
as a request naming one object - so a provider implements each read once.

`AddObjectProvider()` with no type argument uses `ObjectInMemoryProvider`, which holds attribute
values as encoded bytes (an item belongs to its message, and a store outlives every message it
sees). It is strict by default: a set cannot invent an attribute an object does not already have,
which `AllowNewAttributes` turns off.

## What came from the standard and what did not

Of stream 14's twenty-eight catalogue rows, exactly **one** - S14F1 - is marked as a definition
rather than part of a representative subset. So the bodies here are read off the item lists and the
formats of the items, and they are a careful reading rather than a quotation; the whole table is in
`Internal/ObjectCodec.cs`, which is the only file to change if a machine disagrees.

One thing that subset got right: it touches **no undefined item**. `ENABLE`, the one name in stream
14 with no dictionary entry, appears only in S14F23, which is not implemented here.

Reading is looser than writing. An attribute list is accepted whether its entries are two-element
lists or a flat run of name-and-value pairs; a namelist is accepted whether it is a list or one bare
item; an acknowledgement is accepted in any numeric format, and in a reply that carries only the
code. What is refused is a body that cannot be read without guessing - an odd number of flat
attribute fields, for one, where pairing them would attach a name to the wrong value.

## What is not here yet

- **S14F5 to S14F12** (create and delete an object, object actions, attribute relations) and
  **S14F23 to S14F28** (attribute enable, error reports, multi-actions). All representative in the
  catalogue, and `ENABLE` among them has no definition at all.
- A provider that outlives the process. `ObjectInMemoryProvider` is in memory, as it says.
- E87, E90 and E94 object models. This package is the protocol they would travel over, which is
  the right order to build the two in.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
