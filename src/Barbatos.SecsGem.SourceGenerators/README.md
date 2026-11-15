# Barbatos.SecsGem.SourceGenerators

The Roslyn incremental generator and analyzer behind `Barbatos.SecsGem.Mapper`. Reads property
declaration order straight from the syntax tree, so SECS-II mapping is exact, reflection-free and
Native AOT friendly.

> Part of **[Barbatos.SecsGem](https://github.com/Barbatos-Labs/Barbatos.SecsGem)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

```
dotnet add package Barbatos.SecsGem.SourceGenerators
```

That is the entire setup. **Nothing in your code changes** - no attribute to add, no partial class
to write, no context type to declare. `SecsTypeInfoResolver.Default` asks the generated contracts
first and falls back to reflection, so adding this package changes how a type's shape is
discovered and nothing about the result.

```csharp
// true with this package referenced, false without it. Same item tree either way.
SecsTypeInfoResolver.Generated.Contains(typeof(Person));
```

## Why it exists

A SECS-II item list is positional, and the mapper takes its positions from declaration order.

Reflection reads *metadata* order. That matches declaration order on every runtime this library
targets, and it is not something the CLI specification promises. A type split across partial files
is where it stops being dependable - and a wire format whose field order depends on which file the
compiler read first is not a wire format.

This generator reads the syntax instead, which is the thing the programmer actually wrote.

The second benefit falls out of the first: accessors become direct delegates and constructors
become direct `new` calls, so a mapped type survives trimming and Native AOT with no
`DynamicallyAccessedMembers` annotation anywhere.

```csharp
// What it emits, for `record Person(string Name, int Age, string[] Emails)`:
Get = static instance => ((global::Demo.Person)instance).Name,
CreateWithArguments = static args => new global::Demo.Person((string)args[0]!, (int)args[1]!, (string[])args[2]!),
```

## What it finds

1. Anything carrying `[SecsMessage]`.
2. Anything with a member carrying `[SecsItem]` or `[SecsIgnore]`.
3. Every `T` in a direct `SecsSerializer.Serialize<T>`, `Deserialize<T>`, `ToMessage<T>` or
   `FromMessage<T>` call. This is what keeps the promise that a plain record with no attributes at
   all is still mapped from its syntax.
4. Everything reachable from those through mapped members, unwrapping arrays, nullables and
   sequences. A message type is rarely alone - an S6F11 holds a list of reports, and each report
   holds a list of values - and covering the message while leaving its parts to reflection would
   cover the shallowest layer of exactly the types that need it most.

## What it leaves alone

| Shape | Why |
| --- | --- |
| A `struct` | Its members would be written through a boxed copy, and whether that copy makes it back out is a question the two resolvers could answer differently. |
| A generic type | A contract is registered against one closed `Type`, and the generator cannot know which closures a program will use. |
| A type from another assembly | Declaration order comes from syntax this compilation does not have. |
| `{ get; init; }` with no matching constructor parameter | Generated code cannot call an init accessor from outside an object initialiser. Reflection can. |

Every one of those still maps, through reflection, exactly as it did before this package existed.
Two resolvers that differ in *behaviour* would be the worst bug this design could have - so where
the generator cannot match reflection exactly, it declines rather than approximates.

## BSG001

The one diagnostic. **Order is set on some mapped members but not all.**

```csharp
public sealed class PartiallyOrdered
{
    [SecsItem(Order = 0)]
    public string First { get; set; } = string.Empty;

    public string Second { get; set; } = string.Empty;   // BSG001
}
```

Set `Order` on every mapped member or on none; both are exact. Set it on some and the unordered
members fall back to a different rule in each resolver - metadata order under reflection, syntax
order here. They agree today, and they are two different rules, so the day they stop agreeing the
symptom is a message that was correct until somebody added this package.

The analyzer only looks at types that carry `[SecsMessage]` or have a `[SecsItem]` member. Without
that guard every DTO in a solution would be a candidate, and a warning about SECS-II ordering on a
type that has nothing to do with SECS-II is the kind of noise that teaches people to turn warnings
off.

Full reasoning and the fix: [the mapper's README](../Barbatos.SecsGem.Mapper/README.md#bsg001).

## Looking at what it generated

```
dotnet build -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=generated
```

One file per mapped type, under `generated/`. Worth doing once: the output is short, obvious, and
the fastest way to see where a member's position came from.

## Status

Version 0.1.0 - the public surface is still settling. See the roadmap in the
[repository README](https://github.com/Barbatos-Labs/Barbatos.SecsGem#roadmap).
