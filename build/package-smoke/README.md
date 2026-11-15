# Package smoke test

Builds and runs a consumer against the packed `.nupkg` files instead of against the projects.

```
pwsh build/package-smoke/verify-packages.ps1
```

## Why this exists

Every other project here is wired with `ProjectReference`, including the samples and the
benchmarks. That answers *does the code work*. It cannot answer *do the packages work*, and the
two are not the same question:

- `fix(build): the generator package could not be packed` - `dotnet pack` had never been run, and
  failed with NU5017. The test suite was green the whole time.
- `refactor(core)!: the spokes register the serialization hub, not Core` - the foundation package
  shipped a dependency the working agreement forbids. Found by reading a generated `.nuspec` by eye.

Both were invisible to several thousand passing tests, because a `ProjectReference` resolves an
assembly from `bin/`. A consumer resolves it from `lib/net8.0` inside an archive, with the
dependencies the `.nuspec` declares and nothing else.

## What it checks

| Check | What would break it |
| --- | --- |
| A tree survives encode and decode | `lib/` missing a target framework, or a trimmed assembly |
| The packaged Core assembly pulls in no container | `Microsoft.Extensions.*` creeping back into Core |
| One `AddSecsXxx()` per format is the whole wiring | the shared hub registration not compiled into a spoke |
| The analyzer package wrote a contract | the generator not loading from `analyzers/dotnet/cs` |
| The embedded table came out of the package | the 40 KB catalogue resource not embedded |
| The container resolves the equipment | a dependency missing from a `.nuspec`, anywhere in Gem -> Hsms -> Core |

The generator check is the one no other test in the repository can make. `SecsSerializer` falls
back to reflection when there is no generated contract, so serializing would succeed either way -
only `SecsTypeInfoResolver.Generated.Contains(type)` can tell whether the analyzer ran at all.

## How the isolation works

A consumer has to look like somebody else's project, so four files stand between it and this
repository:

| File | Stops |
| --- | --- |
| `Directory.Build.props` | the repository's own props: central package management, `$(BarbatosTargetFrameworks)`, the analyzer set |
| `Directory.Build.targets` | the same walk up the tree, which is separate and would otherwise still find the repository's |
| `Directory.Packages.props` | central package management, so the consumer pins its own versions |
| `nuget.config` | `Barbatos.SecsGem*` arriving from anywhere but the local feed |

The package source mapping is the part worth knowing about: `Barbatos.SecsGem*` is served **only**
by `artifacts/smoke-feed`, so a package missing from the feed fails the restore rather than quietly
arriving from nuget.org and testing a published version instead. Everything else comes from
nuget.org, which is how a consumer gets the declared dependencies.

`Consumer.csproj` also references `Microsoft.Extensions.DependencyInjection` itself. No package
here declares a container - they depend on the abstractions - so a console application that builds
its own `ServiceCollection` brings one. An application on `Microsoft.Extensions.Hosting` already
has it.

## The cache

The script deletes `barbatos.secsgem*` from the NuGet global packages cache before restoring.
Every build stamps the same version, so without that a second run would restore the first run's
packages and prove nothing. Nothing in this repository resolves those from the cache - the solution
uses `ProjectReference` - so removing them cannot break a normal build.

## Proving it can fail

A smoke test that has only ever passed is six lines of text. Drop the generator reference from
`Consumer.csproj` and exactly one check fails:

```
  FAIL  generator: the analyzer package wrote a contract
        InvalidOperationException: no generated contract for ChamberReport: the compiler did not
        load the analyzer from analyzers/dotnet/cs
```

Worth repeating after any change to what the checks assert.
