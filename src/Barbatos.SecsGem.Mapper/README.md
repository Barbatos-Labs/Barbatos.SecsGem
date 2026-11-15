# Barbatos.SecsGem.Mapper

C# objects to SECS-II item trees and back, for **.NET 8, 9 and 10** - convention over
configuration, with the source generator reading declaration order straight from the syntax tree.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.Mapper
dotnet add package Barbatos.SecsGem.SourceGenerators
```

The second one is optional and changes no behaviour. See [The source generator](#the-source-generator).

## No attributes required

```csharp
public sealed record Person(string Name, int Age, string[] Emails);

using var item = SecsSerializer.Serialize(new Person("Hung", 36, ["a@x.com"]));
// <L [3] <A [4] 'Hung'> <I4 [1] 36> <L [1] <A [7] 'a@x.com'> > >
```

An object is a list; its members are the items of that list, in declaration order. Each member's
`SecsFormat` comes from its C# type. That is the whole convention, and it is the same idea
`System.Text.Json` works on - which is the point: nobody should have to learn a second mental model
to put a DTO on a wire.

| C# | SECS-II |
| --- | --- |
| `string` | `A`, or `J` via `DefaultTextFormat` |
| `bool` | `Boolean` |
| `sbyte` `short` `int` `long` | `I1` `I2` `I4` `I8` |
| `byte` `ushort` `uint` `ulong` | `U1` `U2` `U4` `U8` |
| `float` `double` | `F4` `F8` |
| `byte[]` | `B` |
| `enum` | its underlying integer type |
| `int[]`, `List<int>` | one `I4` item holding every element |
| `string[]`, `List<string>` | a list of `A` items - a text item's count is characters, so it cannot hold a sequence |
| a mapped object | a nested list |
| `SecsItem` | itself, passed straight through |

## Where the attributes earn their place

Nothing is required. Reach for `[SecsItem]` where the equipment disagrees with the convention, and
equipment often does:

```csharp
[SecsMessage(2, 41, Name = "HostCommandSend")]
public sealed class HostCommand
{
    // The equipment wants U4 even though the natural C# type is int.
    [SecsItem(SecsFormat.U4, Name = "ECID")]
    public int Id { get; set; }

    // GEM is full of fixed-width fields, and equipment that rejects anything else.
    [SecsItem(Length = 16, Name = "TIMESTAMP")]
    public string Timestamp { get; set; } = string.Empty;

    // A list of single-element items, not one array item.
    [SecsItem(AsList = true)]
    public int[] Limits { get; set; } = [];

    [SecsIgnore]
    public DateTime ReceivedAt { get; set; }
}

using var message = SecsSerializer.ToMessage(command);   // S2F41 W, with the body built for you
```

## Where member order comes from

A SECS-II item list is positional, so this is the question that matters most. In priority:

1. **`[SecsItem(Order = n)]`**, when any member has one.
2. **Constructor parameter order**, when every parameter names a mapped member. That is what makes
   a positional `record` exact - its parameter list *is* its declaration order, and the runtime
   does guarantee that order.
3. **Declaration order** - from the syntax tree when the source generator has seen the type, and
   from reflection metadata otherwise.

Option 3 is where the source generator changes something real. See [BSG001](#bsg001) for the one
shape that is ambiguous, and why.

## The source generator

Add `Barbatos.SecsGem.SourceGenerators` and nothing in your code changes.
`SecsTypeInfoResolver.Default` asks the generated contracts first and falls back to reflection, so
a type the generator has seen simply stops using reflection:

```csharp
// true once the package is referenced, false without it. Same item tree either way.
SecsTypeInfoResolver.Generated.Contains(typeof(Person));
```

What it buys:

- **Exact order.** Reflection reads metadata order, which matches declaration order on every
  runtime this library targets and is not promised by the CLI specification. The generator reads
  the syntax, which is the thing you wrote.
- **No reflection.** Accessors are direct delegates and constructors are direct `new` calls, so a
  mapped type survives trimming and Native AOT with no `DynamicallyAccessedMembers` annotation
  anywhere.

What it finds: anything with `[SecsMessage]`; anything with a `[SecsItem]` or `[SecsIgnore]` member;
every `T` in a direct `SecsSerializer.Serialize<T>`, `Deserialize<T>`, `ToMessage<T>` or
`FromMessage<T>` call; and everything reachable from those through mapped members, unwrapping
arrays, nullables and sequences.

What it leaves to reflection, on purpose:

| Shape | Why |
| --- | --- |
| A `struct` | Its members would be written through a boxed copy. Whether that copy makes it back out is a question the two resolvers could answer differently, and two resolvers that differ in behaviour is the worst bug this design could have. |
| A generic type | A contract is registered against one closed `Type`, and the generator cannot know which closures a program will use. |
| A type from another assembly | Declaration order comes from syntax this compilation does not have. |
| `{ get; init; }` with no matching constructor parameter | Generated code cannot call an init accessor from outside an object initialiser. Reflection can. |

Every one of those still maps, exactly as it did before the generator existed. That is what makes
the generator an optimisation rather than a requirement - and what the parity tests in
`tests/Barbatos.SecsGem.Mapper.UnitTests` check, shape by shape.

## BSG001

**Order is set on some mapped members but not all.**

```csharp
public sealed class PartiallyOrdered
{
    [SecsItem(Order = 0)]
    public string First { get; set; } = string.Empty;

    public string Second { get; set; } = string.Empty;   // BSG001
}
```

Set `Order` on every mapped member and the positions are exactly what you wrote. Set it on none and
they are declaration order, which is also exact. Set it on *some* and the unordered members fall
back to a different rule in each resolver - metadata order under reflection, syntax order under the
generator.

Those agree today, on the runtimes this library targets. They are two different rules, so the day
they stop agreeing the symptom is a message that was correct until somebody added a package, with
nothing in either codebase to point at. That is worth a warning.

**The fix** is either direction: give every member an `Order`, or take the one `Order` away.

To turn it off for a type you have thought about:

```csharp
#pragma warning disable BSG001
```

## Converters

Seven ship, for the types SECS-II has no format for:

| Type | Wire form |
| --- | --- |
| `DateTime` | GEM's 16-character `yyyyMMddHHmmssff`, or 12, 14, ISO 8601 or ticks via `DateTimeFormat` |
| `DateTimeOffset` | the same, with the offset |
| `decimal` | ASCII, so nothing is rounded |
| `TimeSpan` | ASCII |
| `Guid` | 16 binary bytes |
| `DateOnly`, `TimeOnly` | ASCII |

Reading is lenient and writing is not: a `DateTime` comes back from any of the recognised shapes and
goes out in exactly one. Equipment is inconsistent about which it sends and strict about what it
accepts.

Your own:

```csharp
public sealed class CelsiusConverter : SecsConverter<Temperature>
{
    public override SecsItem Write(Temperature value, SecsSerializerOptions options)
        => SecsItem.F8(value.Celsius);

    public override Temperature Read(SecsItem item, SecsSerializerOptions options)
        => new(item.FirstValueOrDefault<double>());
}

services.AddSecsMapper().AddSecsConverter<CelsiusConverter>();
// or per member:
[SecsItem(Converter = typeof(CelsiusConverter))]
public Temperature Chamber { get; set; }
```

## Options

```csharp
services.AddSecsMapper(mapper =>
{
    mapper.DefaultTextFormat = SecsFormat.ASCII;
    mapper.DateTimeFormat = SecsDateTimeFormat.Timestamp16;
    mapper.NullHandling = SecsNullHandling.EmptyItem;
    mapper.PropertyOrdering = SecsPropertyOrdering.Convention;
    mapper.WritePropertyLabels = true;
    mapper.Strict = true;
});
```

`PropertyOrdering = Explicit` refuses to map a member that has no `Order`, which is the setting to
reach for when a message's field order is dictated by a document rather than by your class.

`WritePropertyLabels` puts each member's name on its item as a label, which SML, JSON, XML and INI
all carry and the binary wire format does not. It costs nothing on the wire and makes a capture
readable.

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [`API-REFERENCE.md`](API-REFERENCE.md).
- Repository overview: [Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem).

A runnable demonstration, including the generator:

```
dotnet run --project samples/Barbatos.SecsGem.Samples.Transcode
```

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
