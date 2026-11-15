# Barbatos.SecsGem.SourceGenerators - API reference

**There is nothing here to call.** The package ships no `lib/` - `IncludeBuildOutput` is `false`
and the assembly travels in `analyzers/dotnet/cs` - so NuGet loads it into the compiler and never
makes it a reference of your project. None of its types are visible to your code.

```xml
<PackageReference Include="Barbatos.SecsGem.SourceGenerators" Version="0.1.0" PrivateAssets="all" />
```

A plain `PackageReference` is enough; `PrivateAssets="all"` just stops a library you publish from
passing the generator on to its own consumers, who should decide for themselves. Inside this
repository the same thing is done with `OutputItemType="Analyzer"` and
`ReferenceOutputAssembly="false"` on a `ProjectReference`.

Concepts, what it finds and what it declines: [`README.md`](README.md).

## What it contributes instead

| Contribution | Where it shows up |
| --- | --- |
| Generated contracts | Registered into [`SecsTypeInfoResolver.Generated`](../Barbatos.SecsGem.Mapper/API-REFERENCE.md#generatedsecstypeinforesolver) from a `[ModuleInitializer]`, before any of your code runs. |
| Diagnostic `BSG001` | The compiler's warning list. |

The only API surface you interact with is the mapper's, and only to ask whether a type was covered:

```csharp
SecsTypeInfoResolver.Generated.Contains(typeof(Person));   // true with this package, false without
```

That call is worth making in a startup assertion or a test. It is the one way to tell a working
generator from one that silently produced nothing - and a silent no-op generator looks exactly like
a working one, because the mapper simply falls through to reflection.

## `BSG001`

| Field | Value |
| --- | --- |
| Id | `BSG001` |
| Title | Order is set on some mapped members but not all |
| Category | `Usage` |
| Default severity | Warning |
| Help link | [the mapper's README](../Barbatos.SecsGem.Mapper/README.md#bsg001) |

Reported on each mapped member that has no `Order` when at least one sibling has one. Only types
carrying `[SecsMessage]` or holding a `[SecsItem]` member are examined, so a DTO with nothing to do
with SECS-II is never a candidate.

**Suppressing it**

```csharp
#pragma warning disable BSG001
```

or, for the whole project:

```xml
<NoWarn>$(NoWarn);BSG001</NoWarn>
```

Suppress it only for a type you have thought about. The warning exists because the unordered
members fall back to a *different rule* in each resolver - metadata order under reflection, syntax
order under this generator - and the two agree today without being required to.

The rule is tracked in `AnalyzerReleases.Unshipped.md`, which is what makes it impossible to
renumber or quietly drop later.

## Types in this assembly

Public because Roslyn requires it, and of no use to a consumer:

| Type | Role |
| --- | --- |
| `SecsMapperGenerator` | `IIncrementalGenerator`, discovered by the compiler through `[Generator]`. |
| `SecsMapperAnalyzer` | `DiagnosticAnalyzer`, discovered through `[DiagnosticAnalyzer]`. |

Neither is constructed, configured or called by hand. If you find yourself wanting to, the thing you
want is a custom `ISecsTypeInfoResolver` in the mapper instead.

## Seeing what it generated

```
dotnet build -p:EmitCompilerGeneratedFiles=true -p:CompilerGeneratedFilesOutputPath=generated
```

One file per mapped type under `generated/`. The output is short and obvious, and it is the fastest
way to see where a member's position came from.
