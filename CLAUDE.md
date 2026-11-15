# Barbatos.SecsGem - working agreement

A family of NuGet packages implementing SECS-II (SEMI E5), HSMS-SS (E37) and GEM (E30) for
.NET 8/9/10. Everything here is a **library**: the public API is the product, and a careless
signature is a breaking change in somebody's fab.

## Repository map

```
src/          nine shipping packages, one folder each (+ README.md, API-REFERENCE.md)
src/Shared/   compile-linked source, not a package: the serialization hub registration
samples/      equipment (passive), host (active), transcode CLI, shared
tests/        one xUnit project per package + IntegrationTests
benchmarks/   BenchmarkDotNet
build/        package icons, strong-name key (git-ignored)
assets/       SML/JSON conformance fixtures
```

## Layering - do not break this

```
Core  <-  Hsms  <-  Gem
  ^-- Serialization.{Sml,Json,Xml,Ini}
  ^-- Mapper  <-- SourceGenerators (analyzer only)
```

- `Core` must never reference a socket, a pipe transport or `Microsoft.Extensions.*`.
  Its only third-party dependency is `CommunityToolkit.HighPerformance`.
- A new format is a new assembly implementing `ISecsFormatReader`/`ISecsFormatWriter` plus an
  `AddSecsXxx()` method. **If adding a format edits `Core`, the design is wrong.**

## Non-negotiables

- **.NET 8, 9, 10 only.** Never add `netstandard` or a `net6`/`net7` shim. TFMs come from
  `$(BarbatosTargetFrameworks)` in `Directory.Build.props` - never hard-coded in a `.csproj`.
  `net11.0` lands there once the SDK is installed.
- **No obsolete I/O.** No `BinaryFormatter`, no blocking `NetworkStream`, no `byte[]`
  allocation that can land on the Large Object Heap. Socket async, `System.IO.Pipelines`,
  `System.Threading.Channels`, `Span<T>`, `ReadOnlySequence<byte>`, `ArrayPool`.
- **Timers go through `TimeProvider`.** A test that waits on a wall clock is a flaky test.
- **C# 14, three runtimes.** Compiler-only features are fine everywhere; a type that ships with
  the runtime (`System.Threading.Lock`) needs `#if NET9_0_OR_GREATER`.
- **No `InternalsVisibleTo`.** If a test needs internal behaviour, that behaviour becomes
  documented public API, or the test goes through the public surface.
- **Public API discipline.** XML docs on every public member, `<remarks>` for the *why*; new
  types are `sealed`; a rename keeps the old member as `[Obsolete]`.
- **Central Package Management.** Every version lives in `Directory.Packages.props`.
- `.editorconfig` is the style authority, including the MIT header on every new `.cs` file.

## Build and test

```
dotnet build Barbatos.SecsGem.slnx
dotnet test tests/Barbatos.SecsGem.Core.UnitTests -f net10.0
```

Run one target framework at a time.

## Documentation is part of the change

A public API change is not done until the package's `README.md` (the nuget.org landing page)
and `API-REFERENCE.md` are both updated, plus the sample that demonstrates it.