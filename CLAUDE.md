# Barbatos.SecsGem - working agreement

A family of NuGet packages implementing SECS-II (SEMI E5), HSMS-SS (E37) and GEM (E30) for
.NET 8/9/10. Everything here is a **library**: the public API is the product, and a careless
signature is a breaking change in somebody's fab.

## Repository map

```
src/          twenty-two shipping packages, one folder each (+ README.md, API-REFERENCE.md)
src/Shared/   compile-linked source, not a package: the serialization hub registration
samples/      equipment (passive), host (active), transcode CLI
tests/        one xUnit project per package + IntegrationTests
benchmarks/   BenchmarkDotNet
build/        package icons, strong-name key (git-ignored), the package smoke test
assets/       SML/JSON conformance fixtures
```

## Layering - do not break this

```
Core  <-  Hsms  <-  Gem
            ^----  WaferMap, Material, Objects   (E5 streams 12 and 3, and E39's two - stream 14
                                                 plus the OBJSPEC half of stream 2, S2F49-S2F64;
                                                 all three deliberately not on Gem, so the
                                                 enhanced commands' control-state gate is the
                                                 application's and says so)
                                 ^-- Carriers, Substrates, ControlJobs, ProcessJobs
                                     (E87, E90, E94, E40, on Objects: their attributes are
                                     stream 14; E90 and E94 have no messages at all, E87
                                     borrows S3F17, and E40 has stream 16 of its own)
                                            ^-- MaterialFlow   (the E87-E90 and E40-E90 joins)
                                 ^-- PerformanceTracking  (E116, on Objects and on nothing else
                                     here: not a material standard, no messages, and no mapping
                                     onto E10 - that one is the factory's to make, not ours)
            ^----  Productivity   (E10 + E79, on Hsms for one thing: the container's TimeProvider.
                                   No messages, no items, no object type - these two standards
                                   define measurements, so there is nothing to put on a wire.
                                   Deliberately NOT on PerformanceTracking: see below.)
                       ^-- PerformanceFlow  (the E116-E10 join, on PerformanceTracking and
                           Productivity)
  ^-- Serialization.{Sml,Json,Xml,Ini}
  ^-- Mapper  <-- SourceGenerators (analyzer only)
```

**Two join packages**, `MaterialFlow` and `PerformanceFlow`, and they are the only packages here
that depend on several standards. The rule both keep: **none of the standards a join package joins
may depend on another.** A join lives in its own package precisely so the standards it connects
stay usable apart, and so the conventions it has to invent are visible in one place instead of
buried in whichever package got there first.

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

After touching a `.csproj`, `Directory.Build.*`, `Directory.Packages.props` or anything else that
changes what gets packed:

```
pwsh build/package-smoke/verify-packages.ps1
```

It packs, then builds a consumer against the `.nupkg` files. A `ProjectReference` build resolves
assemblies from `bin/` and cannot see a packaging mistake - both found so far were invisible to the
whole test suite.

## Documentation is part of the change

A public API change is not done until the package's `README.md` (the nuget.org landing page)
and `API-REFERENCE.md` are both updated, plus the sample that demonstrates it.