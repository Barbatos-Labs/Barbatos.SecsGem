#Requires -Version 5.1
<#
.SYNOPSIS
    Bootstraps the Barbatos.SecsGem repository: solution, build infrastructure and all 22 projects.

.DESCRIPTION
    Writes every file deterministically from this script - no `dotnet new` templates, so the
    result never drifts with the installed SDK. The script is idempotent: a file that already
    exists is left alone unless -Force is passed.

    Layering enforced by the generated ProjectReference graph:

        Barbatos.SecsGem.Core            AST + binary codec + serialization contracts (no sockets)
        Barbatos.SecsGem.Hsms            -> Core          HSMS-SS engine, timers, DI
        Barbatos.SecsGem                 -> Core, Hsms, Serialization.Sml   (metapackage, no assembly)
        Barbatos.SecsGem.Serialization.* -> Core          format spokes
        Barbatos.SecsGem.Mapper          -> Core          SecsSerializer
        Barbatos.SecsGem.SourceGenerators                 netstandard2.0 Roslyn component
        Barbatos.SecsGem.Gem             -> Core, Hsms    GEM layer (placeholder, Phase 7)

.PARAMETER Root
    Repository root. Defaults to the folder this script sits in.

.PARAMETER Force
    Overwrite files that already exist.

.PARAMETER NoGit
    Skip `git init`.

.EXAMPLE
    .\init-barbatos-secsgem.ps1
    .\init-barbatos-secsgem.ps1 -Force
#>
[CmdletBinding()]
param(
    [string] $Root,
    [switch] $Force,
    [switch] $NoGit
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Root)) {
    if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) { $Root = (Get-Location).Path } else { $Root = $PSScriptRoot }
}
$Root = [System.IO.Path]::GetFullPath($Root)

# ---------------------------------------------------------------------------------------------
# Repository-wide constants. Changing a version here changes it everywhere.
# ---------------------------------------------------------------------------------------------
$RepoName    = 'Barbatos.SecsGem'
$RepoUrl     = 'https://github.com/Barbatos-Labs/Barbatos.SecsGem'
$Author      = 'Pham The Hung'
$PkgVersion  = '0.1.0'

# Single switch for every multi-targeted project. Adding net11.0 later is a one-line change:
#   $Tfms = 'net8.0;net9.0;net10.0;net11.0'
$Tfms        = 'net8.0;net9.0;net10.0'
$SampleTfm   = 'net10.0'

$script:Created = New-Object System.Collections.Generic.List[string]
$script:Skipped = New-Object System.Collections.Generic.List[string]

function New-Dir {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { New-Item -ItemType Directory -Path $Path -Force | Out-Null }
}

function Set-RepoFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][AllowEmptyString()][string] $Content
    )
    $full = Join-Path $Root $Path
    New-Dir (Split-Path -Parent $full)
    if ((Test-Path -LiteralPath $full) -and (-not $Force)) { $script:Skipped.Add($Path); return }
    # Normalise to CRLF once, so the file is stable no matter how this script was edited.
    $text = ($Content -replace "`r`n", "`n") -replace "`n", "`r`n"
    [System.IO.File]::WriteAllText($full, $text, (New-Object System.Text.UTF8Encoding $false))
    $script:Created.Add($Path)
}

# MIT header required on every .cs file under src/ and tests/ (see .editorconfig).
$CsHeader = @'
// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
// All Rights Reserved.
'@

# =============================================================================================
# 1. Project table - the single source of truth for the solution graph
# =============================================================================================
# Kind: lib | meta | generator | test | sample | benchmark
$Projects = @(
    @{  Name    = 'Barbatos.SecsGem.Core'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem'
        Browser = $true
        Desc    = 'SECS-II object model for .NET: the SecsItem/SecsMessage abstract syntax tree, a zero-copy binary codec over ReadOnlySequence and IBufferWriter, and the Hub-and-Spoke serialization contracts (ISecsFormatReader / ISecsFormatWriter / SecsTranscoder). Contains no transport and no sockets, so it is safe to use in tooling, analyzers and WebAssembly.'
        Tags    = 'secs;secs-ii;ast;item;codec;transcoder;serialization;semi;e5'
        Folders = @('Items', 'Messages', 'Codec', 'Serialization', 'Exceptions', 'Internal')
        # System.IO.Pipelines is a buffer abstraction, not a transport: the serialization
        # contracts stream over PipeReader/PipeWriter so a 500 MB SML file never needs to be
        # materialised. It brings no socket and no platform dependency with it.
        Pkgs    = @('CommunityToolkit.HighPerformance', 'System.IO.Pipelines')
        Refs    = @()
    },
    @{  Name    = 'Barbatos.SecsGem.Hsms'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Hsms'
        Desc    = 'HSMS-SS (SEMI E37) engine for .NET: an actor-driven connection state machine, a dual-path System.IO.Pipelines decoder with a correctly scoped T8 inter-character timeout, a transaction manager covering T3/T5/T6/T7/T8 over TimeProvider, automatic S9Fx error reporting, and first-class Microsoft.Extensions hosting and dependency injection.'
        Tags    = 'hsms;hsms-ss;e37;state-machine;pipelines;channels;t3;t8;s9;transaction'
        Folders = @('Connection', 'Decoding', 'Transactions', 'Timers', 'Errors', 'Routing', 'Hosting', 'Diagnostics')
        Pkgs    = @(
            'System.IO.Pipelines',
            'Microsoft.Extensions.DependencyInjection.Abstractions',
            'Microsoft.Extensions.Options.ConfigurationExtensions',
            'Microsoft.Extensions.Logging.Abstractions',
            'Microsoft.Extensions.Hosting.Abstractions',
            'Microsoft.Extensions.Diagnostics.Abstractions')
        Refs    = @('Barbatos.SecsGem.Core')
    },
    @{  Name    = 'Barbatos.SecsGem'
        Group   = 'src'
        Kind    = 'meta'
        Ns      = 'Barbatos.SecsGem'
        Desc    = 'Metapackage for Barbatos.SecsGem - installs Barbatos.SecsGem.Core, Barbatos.SecsGem.Hsms and Barbatos.SecsGem.Serialization.Sml in one reference. Ships no assembly of its own.'
        Tags    = 'secs;secsgem;hsms;gem;metapackage'
        Folders = @()
        Pkgs    = @()
        Refs    = @('Barbatos.SecsGem.Core', 'Barbatos.SecsGem.Hsms', 'Barbatos.SecsGem.Serialization.Sml')
    },
    @{  Name    = 'Barbatos.SecsGem.Serialization.Sml'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Serialization.Sml'
        Browser = $true
        Desc    = 'SML (SECS Message Language) reader and writer for Barbatos.SecsGem - a Hub-and-Spoke format spoke that streams over PipeReader/PipeWriter and plugs into SecsTranscoder through AddSecsSml().'
        Tags    = 'sml;secs;text;reader;writer;spoke'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.DependencyInjection.Abstractions')
        Refs    = @('Barbatos.SecsGem.Core')
    },
    @{  Name    = 'Barbatos.SecsGem.Serialization.Json'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Serialization.Json'
        Browser = $true
        Desc    = 'JSON reader and writer for Barbatos.SecsGem, built directly on Utf8JsonReader/Utf8JsonWriter - a Hub-and-Spoke format spoke that plugs into SecsTranscoder through AddSecsJson().'
        Tags    = 'json;secs;utf8json;reader;writer;spoke'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.DependencyInjection.Abstractions')
        Refs    = @('Barbatos.SecsGem.Core')
    },
    @{  Name    = 'Barbatos.SecsGem.Serialization.Xml'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Serialization.Xml'
        Browser = $true
        Desc    = 'XML reader and writer for Barbatos.SecsGem with DTD processing and external entity resolution disabled - a Hub-and-Spoke format spoke that plugs into SecsTranscoder through AddSecsXml().'
        Tags    = 'xml;secs;reader;writer;spoke'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.DependencyInjection.Abstractions')
        Refs    = @('Barbatos.SecsGem.Core')
    },
    @{  Name    = 'Barbatos.SecsGem.Serialization.Ini'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Serialization.Ini'
        Browser = $true
        Desc    = 'INI reader and writer for Barbatos.SecsGem using path-addressed keys (1.2.3 = U4 1234) - a Hub-and-Spoke format spoke that plugs into SecsTranscoder through AddSecsIni().'
        Tags    = 'ini;secs;reader;writer;spoke;configuration'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.DependencyInjection.Abstractions')
        Refs    = @('Barbatos.SecsGem.Core')
    },
    @{  Name    = 'Barbatos.SecsGem.Mapper'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Mapping'
        Browser = $true
        Desc    = 'Convention-over-configuration object mapper for SECS-II: maps C# classes and records to SecsItem trees in both directions using declaration order, with automatic C# to SecsFormat inference, nested lists, custom converters and the optional [SecsMessage] / [SecsItem] / [SecsIgnore] attributes.'
        Tags    = 'mapper;serializer;poco;record;convention;reflection;sourcegenerator'
        Folders = @('Attributes', 'Contracts', 'Converters', 'Reflection', 'Generated')
        Pkgs    = @('Microsoft.Extensions.DependencyInjection.Abstractions', 'Microsoft.Extensions.Options')
        Refs    = @('Barbatos.SecsGem.Core')
        Analyzers = @('Barbatos.SecsGem.SourceGenerators')
    },
    @{  Name    = 'Barbatos.SecsGem.SourceGenerators'
        Group   = 'src'
        Kind    = 'generator'
        Ns      = 'Barbatos.SecsGem.SourceGenerators'
        Desc    = 'Roslyn incremental source generator and analyzer for Barbatos.SecsGem.Mapper. Reads property declaration order straight from the syntax tree, so SECS-II mapping is exact, reflection-free and Native AOT friendly.'
        Tags    = 'sourcegenerator;roslyn;analyzer;aot;mapper'
        Folders = @()
        Pkgs    = @()
        Refs    = @()
    },
    @{  Name    = 'Barbatos.SecsGem.Gem'
        Group   = 'src'
        Kind    = 'lib'
        Ns      = 'Barbatos.SecsGem.Gem'
        Desc    = 'GEM (SEMI E30) layer for Barbatos.SecsGem: communication and control state models, S1F13/S1F14 establish-communications, collection events, status and equipment constants, alarms and remote commands. Placeholder - see the roadmap in README.md.'
        Tags    = 'gem;e30;e5;ceid;svid;ecid;alid;collectionevent;alarm'
        Folders = @('StateModels', 'Collection', 'Alarms', 'Remote', 'Recipes')
        Pkgs    = @()
        Refs    = @('Barbatos.SecsGem.Core', 'Barbatos.SecsGem.Hsms')
    },

    # ----------------------------------------- samples -----------------------------------------
    @{  Name    = 'Barbatos.SecsGem.Samples.Equipment'
        Group   = 'samples'
        Kind    = 'sample'
        Ns      = 'Barbatos.SecsGem.Samples.Equipment'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.Hosting', 'Microsoft.Extensions.Logging.Console')
        Refs    = @('Barbatos.SecsGem', 'Barbatos.SecsGem.Mapper', 'Barbatos.SecsGem.Samples.Shared')
    },
    @{  Name    = 'Barbatos.SecsGem.Samples.Host'
        Group   = 'samples'
        Kind    = 'sample'
        Ns      = 'Barbatos.SecsGem.Samples.Host'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.Hosting', 'Microsoft.Extensions.Logging.Console')
        Refs    = @('Barbatos.SecsGem', 'Barbatos.SecsGem.Mapper', 'Barbatos.SecsGem.Samples.Shared')
    },
    @{  Name    = 'Barbatos.SecsGem.Samples.Transcode'
        Group   = 'samples'
        Kind    = 'sample'
        Ns      = 'Barbatos.SecsGem.Samples.Transcode'
        Folders = @()
        Pkgs    = @('Microsoft.Extensions.DependencyInjection')
        Refs    = @(
            'Barbatos.SecsGem.Core',
            'Barbatos.SecsGem.Serialization.Sml',
            'Barbatos.SecsGem.Serialization.Json',
            'Barbatos.SecsGem.Serialization.Xml',
            'Barbatos.SecsGem.Serialization.Ini',
            'Barbatos.SecsGem.Mapper')
    },
    @{  Name    = 'Barbatos.SecsGem.Samples.Shared'
        Group   = 'samples'
        Kind    = 'sample'
        Library = $true
        Ns      = 'Barbatos.SecsGem.Samples.Shared'
        Folders = @()
        Pkgs    = @()
        Refs    = @('Barbatos.SecsGem.Core', 'Barbatos.SecsGem.Mapper')
    },

    # ------------------------------------------ tests ------------------------------------------
    @{  Name  = 'Barbatos.SecsGem.Core.UnitTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.Core.UnitTests'
        Folders = @(); Pkgs = @(); Refs = @('Barbatos.SecsGem.Core')
    },
    @{  Name  = 'Barbatos.SecsGem.Hsms.UnitTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.Hsms.UnitTests'
        Folders = @(); Pkgs = @('Microsoft.Extensions.TimeProvider.Testing'); Refs = @('Barbatos.SecsGem.Hsms')
    },
    @{  Name  = 'Barbatos.SecsGem.Serialization.UnitTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.Serialization.UnitTests'
        Folders = @()
        Pkgs  = @()
        Refs  = @(
            'Barbatos.SecsGem.Serialization.Sml',
            'Barbatos.SecsGem.Serialization.Json',
            'Barbatos.SecsGem.Serialization.Xml',
            'Barbatos.SecsGem.Serialization.Ini')
    },
    @{  Name  = 'Barbatos.SecsGem.Mapper.UnitTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.Mapper.UnitTests'
        Folders = @(); Pkgs = @(); Refs = @('Barbatos.SecsGem.Mapper', 'Barbatos.SecsGem.Serialization.Sml')
    },
    @{  Name  = 'Barbatos.SecsGem.SourceGenerators.UnitTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.SourceGenerators.UnitTests'
        Folders = @(); Pkgs = @('Microsoft.CodeAnalysis.CSharp'); Refs = @('Barbatos.SecsGem.SourceGenerators')
    },
    @{  Name  = 'Barbatos.SecsGem.Gem.UnitTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.Gem.UnitTests'
        Folders = @(); Pkgs = @(); Refs = @('Barbatos.SecsGem.Gem')
    },
    @{  Name  = 'Barbatos.SecsGem.IntegrationTests'
        Group = 'tests'; Kind = 'test'; Ns = 'Barbatos.SecsGem.IntegrationTests'
        Folders = @()
        Pkgs  = @('Microsoft.Extensions.TimeProvider.Testing', 'Microsoft.Extensions.DependencyInjection')
        Refs  = @('Barbatos.SecsGem', 'Barbatos.SecsGem.Mapper', 'Barbatos.SecsGem.Gem')
    },

    # --------------------------------------- benchmarks ----------------------------------------
    @{  Name  = 'Barbatos.SecsGem.Benchmarks'
        Group = 'benchmarks'; Kind = 'benchmark'; Ns = 'Barbatos.SecsGem.Benchmarks'
        Folders = @(); Pkgs = @('BenchmarkDotNet')
        Refs  = @(
            'Barbatos.SecsGem.Core',
            'Barbatos.SecsGem.Hsms',
            'Barbatos.SecsGem.Serialization.Sml',
            'Barbatos.SecsGem.Serialization.Json',
            'Barbatos.SecsGem.Mapper')
    }
)

function Get-ProjectPath {
    param([Parameter(Mandatory)][string] $Name)
    $p = $Projects | Where-Object { $_.Name -eq $Name }
    if ($null -eq $p) { throw "Unknown project '$Name' referenced in the project table." }
    return "$($p.Group)/$Name/$Name.csproj"
}

# =============================================================================================
# 2. Root infrastructure
# =============================================================================================

Write-Host "Barbatos.SecsGem bootstrap" -ForegroundColor Cyan
Write-Host "  root : $Root"
Write-Host "  tfms : $Tfms"
Write-Host ""

New-Dir $Root

# ----- .gitignore ----------------------------------------------------------------------------
Set-RepoFile '.gitignore' @'
## Build
[Bb]in/
[Oo]bj/
[Ll]og/
[Ll]ogs/
artifacts/
*.binlog
*.user
*.suo
*.userprefs

## IDE
.vs/
.vscode/
.idea/
*.DotSettings.user

## Test / coverage
[Tt]est[Rr]esult*/
coverage*.json
coverage*.xml
coverage*.info
*.coverage
*.coveragexml
BenchmarkDotNet.Artifacts/

## NuGet
*.nupkg
*.snupkg
.nuget/packages/
packages/

## Secrets - the strong-name key never belongs in the repository
*.snk
*.pfx

## OS
.DS_Store
Thumbs.db
'@

# ----- .gitattributes ------------------------------------------------------------------------
Set-RepoFile '.gitattributes' @'
* text=auto

*.cs       text diff=csharp
*.csproj   text eol=crlf
*.props    text eol=crlf
*.targets  text eol=crlf
*.slnx     text eol=crlf
*.ps1      text eol=crlf
*.sml      text
*.md       text
*.json     text
*.yml      text eol=lf

*.png      binary
*.snk      binary
'@

# ----- global.json ---------------------------------------------------------------------------
Set-RepoFile 'global.json' @'
{
  "sdk": {
    "version": "8.0.0",
    "rollForward": "latestMajor",
    "allowPrerelease": false
  }
}
'@

# ----- nuget.config --------------------------------------------------------------------------
Set-RepoFile 'nuget.config' @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
'@

# ----- Directory.Build.props -----------------------------------------------------------------
Set-RepoFile 'Directory.Build.props' @"
<Project>

  <PropertyGroup>
    <Version>$PkgVersion</Version>
    <AssemblyVersion>$PkgVersion</AssemblyVersion>
    <FileVersion>$PkgVersion</FileVersion>
  </PropertyGroup>

  <!-- ===========================================================================
       Target frameworks for the whole repository live here and nowhere else.
       Every multi-targeted project writes <TargetFrameworks>`$(BarbatosTargetFrameworks)</TargetFrameworks>.

       net11.0 is deliberately NOT in this list yet: the .NET 11 SDK is not installed on the
       development machine, and a TFM without its SDK fails the build with NETSDK1045.
       When the SDK is installed, append it here - one line, 22 projects follow.
       =========================================================================== -->
  <PropertyGroup>
    <BarbatosTargetFrameworks>$Tfms</BarbatosTargetFrameworks>
    <BarbatosSampleTargetFramework>$SampleTfm</BarbatosSampleTargetFramework>
  </PropertyGroup>

  <!-- Package identity shared by every shipping project. -->
  <PropertyGroup>
    <Authors>$Author</Authors>
    <Company>Barbatos Labs</Company>
    <Product>$RepoName</Product>
    <Copyright>(c) 2026 Barbatos Labs | $Author and $RepoName Contributors. All rights reserved.</Copyright>
    <PackageProjectUrl>$RepoUrl</PackageProjectUrl>
    <RepositoryUrl>$RepoUrl</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageReleaseNotes>$RepoUrl/releases</PackageReleaseNotes>
    <PackageLicenseFile>LICENSE.md</PackageLicenseFile>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <!-- Words every package shares. A project appends its own through <PackageTags>;
         Directory.Build.targets joins the two. -->
    <CommonTags>dotnet;secs;secsgem;hsms;gem;semi;semiconductor;equipment;automation;fab;factory</CommonTags>
    <!-- Each package may ship its own icon; falls back to the shared repository logo.
         Both the icon and the <PackageIcon> property below are gated on the file existing,
         so `dotnet pack` works on a fresh clone that has no art yet. -->
    <PackageIconSourceFile Condition="'`$(PackageIconSourceFile)' == ''">nuget.png</PackageIconSourceFile>
  </PropertyGroup>

  <!-- Language and analysis. C# 14 needs the .NET 10 SDK, which global.json rolls forward to;
       compiler-only features are fine on every runtime here, but anything whose TYPE ships with
       the runtime (System.Threading.Lock, for one) needs a #if NET9_0_OR_GREATER guard. -->
  <PropertyGroup>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <Deterministic>true</Deterministic>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <AnalysisLevel>latest</AnalysisLevel>
    <EnableNETAnalyzers>true</EnableNETAnalyzers>
    <!-- Suppress ref-safety warnings in unsafe contexts (dotnet/csharplang#6476): used to replace
         Unsafe.SizeOf<T>() with sizeof(T). Reaching these APIs already requires an unsafe context. -->
    <NoWarn>`$(NoWarn);CS8500</NoWarn>
  </PropertyGroup>

  <PropertyGroup>
    <RepositoryDirectory>`$(MSBuildThisFileDirectory)</RepositoryDirectory>
    <BuildToolsDirectory>`$(RepositoryDirectory)build\</BuildToolsDirectory>
  </PropertyGroup>

  <!-- Project classification drives packing, analyzers and AOT settings below. -->
  <PropertyGroup>
    <IsTestProject>`$(MSBuildProjectName.Contains('Test'))</IsTestProject>
    <IsSampleProject>`$(MSBuildProjectName.Contains('Samples'))</IsSampleProject>
    <IsBenchmarkProject>`$(MSBuildProjectName.Contains('Benchmark'))</IsBenchmarkProject>
    <IsShippingProject Condition="`$(IsTestProject) or `$(IsSampleProject) or `$(IsBenchmarkProject)">false</IsShippingProject>
    <IsShippingProject Condition="'`$(IsShippingProject)' == ''">true</IsShippingProject>
  </PropertyGroup>

  <Choose>
    <When Condition="`$(IsShippingProject)">
      <PropertyGroup>
        <IsPackable>true</IsPackable>
        <PackageReadmeFile>README.md</PackageReadmeFile>
        <GenerateDocumentationFile>true</GenerateDocumentationFile>
        <!-- Deliberate deviation from Barbatos.Wpf: packing 10 projects x 3 TFMs on every build
             is a lot of friction during development. Pass -p:PackOnBuild=true (or run
             `dotnet pack`) when you want the .nupkg files. -->
        <GeneratePackageOnBuild Condition="'`$(PackOnBuild)' == 'true'">true</GeneratePackageOnBuild>
      </PropertyGroup>
    </When>
    <Otherwise>
      <PropertyGroup>
        <IsPackable>false</IsPackable>
        <GeneratePackageOnBuild>false</GeneratePackageOnBuild>
        <GenerateDocumentationFile>false</GenerateDocumentationFile>
        <NoWarn>`$(NoWarn);CS1591;CS8002</NoWarn>
      </PropertyGroup>
    </Otherwise>
  </Choose>

  <Choose>
    <When Condition="'`$(SourceLinkEnabled)' != 'false' and `$(IsShippingProject)">
      <PropertyGroup>
        <PublishRepositoryUrl>true</PublishRepositoryUrl>
        <EmbedUntrackedSources>true</EmbedUntrackedSources>
        <ContinuousIntegrationBuild Condition="'`$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
      </PropertyGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.SourceLink.GitHub" PrivateAssets="All" />
      </ItemGroup>
    </When>
  </Choose>

</Project>
"@

# ----- Directory.Build.targets ---------------------------------------------------------------
Set-RepoFile 'Directory.Build.targets' @'
<Project>

  <!-- PackageTags = shared words + whatever the project added. -->
  <PropertyGroup>
    <PackageTags Condition="'$(PackageTags)' != ''">$(CommonTags);$(PackageTags)</PackageTags>
    <PackageTags Condition="'$(PackageTags)' == ''">$(CommonTags)</PackageTags>
  </PropertyGroup>

  <!-- Trimming and AOT. Evaluated per inner build, so netstandard2.0 (the Roslyn component)
       is skipped: IsAotCompatible is only meaningful on .NETCoreApp. -->
  <PropertyGroup Condition="'$(TargetFrameworkIdentifier)' == '.NETCoreApp' and $(IsShippingProject)">
    <IsAotCompatible Condition="'$(IsAotCompatible)' == ''">true</IsAotCompatible>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <EnableAotAnalyzer>true</EnableAotAnalyzer>
    <EnableSingleFileAnalyzer>true</EnableSingleFileAnalyzer>
  </PropertyGroup>

  <!-- Symbols travel as .snupkg; PDBs are never embedded in the main .nupkg as well, or
       NuGet.org rejects the snupkg for duplicating them. -->
  <PropertyGroup Condition="'$(IsPackable)' == 'true'">
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <DeterministicSourcePaths Condition="'$(ContinuousIntegrationBuild)' == 'true'">true</DeterministicSourcePaths>
  </PropertyGroup>

  <!-- <PackageIcon> is only declared when the art actually exists, so a fresh clone can pack. -->
  <PropertyGroup Condition="'$(IsPackable)' == 'true' and Exists('$(BuildToolsDirectory)$(PackageIconSourceFile)')">
    <PackageIcon>Icon.png</PackageIcon>
  </PropertyGroup>

  <ItemGroup Condition="'$(IsPackable)' == 'true'">
    <None Include="$(BuildToolsDirectory)$(PackageIconSourceFile)"
          Condition="Exists('$(BuildToolsDirectory)$(PackageIconSourceFile)')"
          Pack="true" PackagePath="\Icon.png" Visible="false" />
    <None Include="$(RepositoryDirectory)LICENSE.md" Pack="true" PackagePath="\LICENSE.md" Visible="false" />
    <None Include="$(RepositoryDirectory)ThirdPartyNotices.txt" Pack="true" PackagePath="\" Visible="false" />
    <!-- Each package ships the README.md sitting beside its own .csproj: that file is the
         nuget.org landing page, so it is written for a reader who has never seen the repository. -->
    <None Include="$(MSBuildProjectDirectory)\README.md" Condition="Exists('$(MSBuildProjectDirectory)\README.md')"
          Pack="true" PackagePath="\README.md" />
  </ItemGroup>

  <!-- Strong naming is gated on the key being present: it is git-ignored, so only release
       builds that supply build\barbatos.snk produce signed assemblies. -->
  <PropertyGroup Condition="'$(IsPackable)' == 'true' and Exists('$(RepositoryDirectory)build\barbatos.snk')">
    <SignAssembly>true</SignAssembly>
    <AssemblyOriginatorKeyFile>$(RepositoryDirectory)build\barbatos.snk</AssemblyOriginatorKeyFile>
  </PropertyGroup>

  <Target Name="AddCommitHashToAssemblyAttributes" BeforeTargets="GetAssemblyAttributes">
    <ItemGroup>
      <AssemblyAttribute Include="System.Reflection.AssemblyMetadataAttribute" Condition="'$(SourceRevisionId)' != ''">
        <_Parameter1>CommitHash</_Parameter1>
        <_Parameter2>$(SourceRevisionId)</_Parameter2>
      </AssemblyAttribute>
    </ItemGroup>
  </Target>

</Project>
'@

# ----- Directory.Packages.props --------------------------------------------------------------
Set-RepoFile 'Directory.Packages.props' @'
<Project>

  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>false</CentralPackageTransitivePinningEnabled>
    <NuGetAudit>true</NuGetAudit>
    <NuGetAuditLevel>moderate</NuGetAuditLevel>
  </PropertyGroup>

  <ItemGroup Label="Runtime">
    <!-- Pooling primitives: ArrayPoolBufferWriter<T>, MemoryOwner<T>, SpanOwner<T>, StringPool.
         The only third-party dependency in Barbatos.SecsGem.Core, and the only one allowed there. -->
    <PackageVersion Include="CommunityToolkit.HighPerformance" Version="8.4.2" />
    <PackageVersion Include="System.IO.Pipelines" Version="10.0.15" />
  </ItemGroup>

  <ItemGroup Label="Microsoft.Extensions - keep the whole block in lockstep">
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Diagnostics.Abstractions" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Hosting" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Logging.Console" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Options" Version="10.0.15" />
    <PackageVersion Include="Microsoft.Extensions.Options.ConfigurationExtensions" Version="10.0.15" />
  </ItemGroup>

  <ItemGroup Label="Roslyn - pinned LOW on purpose">
    <!-- 4.8.0 is the floor the generator compiles against so it loads in every SDK from
         .NET 8 onwards. Raising this raises the minimum SDK of every consumer. -->
    <PackageVersion Include="Microsoft.CodeAnalysis.CSharp" Version="4.8.0" />
    <PackageVersion Include="Microsoft.CodeAnalysis.Analyzers" Version="3.11.0" />
  </ItemGroup>

  <ItemGroup Label="Build">
    <PackageVersion Include="Microsoft.SourceLink.GitHub" Version="10.0.404" />
  </ItemGroup>

  <ItemGroup Label="Test">
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.12.0" />
    <PackageVersion Include="xunit.v3.mtp-off" Version="4.0.3" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.1" />
    <PackageVersion Include="coverlet.collector" Version="10.1.0" />
    <!-- FakeTimeProvider: how T3/T5/T6/T7/T8 are tested without waiting on a wall clock. -->
    <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.11.0" />
  </ItemGroup>

  <ItemGroup Label="Benchmarks">
    <PackageVersion Include="BenchmarkDotNet" Version="0.15.8" />
  </ItemGroup>

</Project>
'@

# ----- .editorconfig -------------------------------------------------------------------------
Set-RepoFile '.editorconfig' @'
# Barbatos.SecsGem - the style authority for this repository.
root = true

[*]
charset = utf-8
end_of_line = crlf
insert_final_newline = true
trim_trailing_whitespace = true
indent_style = space
indent_size = 4

[*.{csproj,props,targets,slnx,xml,config}]
indent_size = 2

[*.{json,yml,yaml}]
indent_size = 2

[*.md]
trim_trailing_whitespace = false

[*.cs]
indent_size = 4
csharp_using_directive_placement = outside_namespace:warning
csharp_style_namespace_declarations = file_scoped:warning
csharp_prefer_braces = true:silent
csharp_style_prefer_primary_constructors = true:suggestion
csharp_style_expression_bodied_methods = when_on_single_line:silent
csharp_style_expression_bodied_properties = true:silent
csharp_new_line_before_open_brace = all
dotnet_sort_system_directives_first = true
dotnet_separate_import_directive_groups = false

# Every new .cs file under src/ and tests/ carries this header.
file_header_template = This Source Code Form is subject to the terms of the MIT License.\nIf a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.\nCopyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.\nAll Rights Reserved.
dotnet_diagnostic.IDE0073.severity = warning

# Naming
dotnet_naming_rule.private_fields_underscore.severity = warning
dotnet_naming_rule.private_fields_underscore.symbols = private_fields
dotnet_naming_rule.private_fields_underscore.style = underscore_camel
dotnet_naming_symbols.private_fields.applicable_kinds = field
dotnet_naming_symbols.private_fields.applicable_accessibilities = private
dotnet_naming_style.underscore_camel.required_prefix = _
dotnet_naming_style.underscore_camel.capitalization = camel_case

# Public API discipline: a missing XML doc on a shipping member is a build warning.
dotnet_diagnostic.CS1591.severity = warning

# Performance analyzers worth failing a review over in a library this hot.
dotnet_diagnostic.CA1819.severity = warning   # properties should not return arrays
dotnet_diagnostic.CA1825.severity = warning   # avoid zero-length array allocations
dotnet_diagnostic.CA1852.severity = warning   # seal internal types
dotnet_diagnostic.CA2016.severity = warning   # forward CancellationToken

[tests/**/*.cs]
dotnet_diagnostic.CS1591.severity = none
dotnet_diagnostic.CA1819.severity = none

[samples/**/*.cs]
dotnet_diagnostic.CS1591.severity = none
'@

# ----- LICENSE.md ----------------------------------------------------------------------------
Set-RepoFile 'LICENSE.md' @"
MIT License

Copyright (c) 2026 Barbatos Labs | $Author and $RepoName Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
"@

# ----- ThirdPartyNotices.txt -----------------------------------------------------------------
Set-RepoFile 'ThirdPartyNotices.txt' @'
Barbatos.SecsGem - Third Party Notices
======================================

This product bundles no third-party source code. It takes a dependency on the
following packages, each under its own licence:

CommunityToolkit.HighPerformance   MIT   https://github.com/CommunityToolkit/dotnet
System.IO.Pipelines                MIT   https://github.com/dotnet/runtime
Microsoft.Extensions.*             MIT   https://github.com/dotnet/runtime
Microsoft.CodeAnalysis.*           MIT   https://github.com/dotnet/roslyn

Design acknowledgement
----------------------
The zero-allocation approach to the SECS-II item model and the pipelined decoder
were informed by secs4net (MIT, https://github.com/mkjeff/secs4net). No code was
copied; the message-level SML and JSON shapes are kept wire-compatible on purpose,
so existing secs4net fixtures remain readable.
'@

# ----- build/README.md -----------------------------------------------------------------------
Set-RepoFile 'build/README.md' @'
# build

Packaging assets. Everything here is optional - the build and `dotnet pack` both work without it.

| File | Purpose |
| --- | --- |
| `nuget.png` | Shared fallback package icon (128x128 PNG). |
| `secsgem-core-logo.png` | Per-package icon, selected with `<PackageIconSourceFile>`. |
| `barbatos.snk` | Strong-name key. **Git-ignored** - signing turns itself on only when this file is present. |

Generate a key with:

```
dotnet tool install -g dotnet-strong-name   # or use sn.exe from a VS developer prompt
sn -k build\barbatos.snk
```
'@

# ----- assets/README.md ----------------------------------------------------------------------
Set-RepoFile 'assets/README.md' @'
# assets

Conformance fixtures shared by the serialization tests and the benchmarks.

| File | Purpose |
| --- | --- |
| `common.sml` | Large SML corpus - the round-trip baseline. |
| `common.json` | The same corpus in the JSON shape. |
| `gem/*.sml` | Hand-written GEM messages (S1F3, S2F33, S6F11, ...). |

The round-trip matrix in `tests/Barbatos.SecsGem.Serialization.UnitTests` reads every file here
and asserts `sml -> X -> sml` is byte-identical for X in { json, xml, ini, binary }.
'@

# ----- CI ------------------------------------------------------------------------------------
Set-RepoFile '.github/workflows/ci.yml' @'
name: ci

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    strategy:
      fail-fast: false
      matrix:
        os: [ windows-latest, ubuntu-latest ]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            8.0.x
            9.0.x
            10.0.x

      - name: Restore
        run: dotnet restore Barbatos.SecsGem.slnx

      - name: Build
        run: dotnet build Barbatos.SecsGem.slnx -c Release --no-restore

      # Not `-f net8.0`: passing a framework at the solution level forces it onto every project,
      # including the netstandard2.0 Roslyn component, which then fails with NETSDK1005. The
      # test projects multi-target, so a plain run already covers all three runtimes.
      # To narrow locally, pass -f to a single test project instead.
      - name: Test
        run: dotnet test Barbatos.SecsGem.slnx -c Release --no-build

      - name: Pack
        if: matrix.os == 'windows-latest'
        run: dotnet pack Barbatos.SecsGem.slnx -c Release --no-build -o artifacts
'@

# =============================================================================================
# 3. Project files
# =============================================================================================

function Convert-XmlText {
    param([Parameter(Mandatory)][AllowEmptyString()][string] $Text)
    return $Text.Replace('&', '&amp;').Replace('<', '&lt;').Replace('>', '&gt;')
}

function New-CsprojXml {
    param([Parameter(Mandatory)][hashtable] $P)

    $sb = New-Object System.Text.StringBuilder
    $nl = [Environment]::NewLine
    function Add([string] $line) { [void] $sb.Append($line).Append($nl) }

    Add '<Project Sdk="Microsoft.NET.Sdk">'
    Add ''
    Add '  <PropertyGroup>'

    switch ($P.Kind) {
        'generator' {
            Add "    <PackageId>$($P.Name)</PackageId>"
            Add '    <TargetFramework>netstandard2.0</TargetFramework>'
            Add "    <RootNamespace>$($P.Ns)</RootNamespace>"
            Add '    <ImplicitUsings>false</ImplicitUsings>'
            Add '    <IsRoslynComponent>true</IsRoslynComponent>'
            Add '    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>'
            Add '    <!-- The generator assembly travels in analyzers/, never in lib/. -->'
            Add '    <IncludeBuildOutput>false</IncludeBuildOutput>'
            Add '    <NoWarn>$(NoWarn);NU5128</NoWarn>'
        }
        'meta' {
            Add "    <PackageId>$($P.Name)</PackageId>"
            Add '    <TargetFrameworks>$(BarbatosTargetFrameworks)</TargetFrameworks>'
            Add '    <!-- Metapackage: dependencies only, no assembly of its own. -->'
            Add '    <IncludeBuildOutput>false</IncludeBuildOutput>'
            Add '    <GenerateDocumentationFile>false</GenerateDocumentationFile>'
            Add '    <NoWarn>$(NoWarn);NU5128</NoWarn>'
        }
        'test' {
            Add '    <TargetFrameworks>$(BarbatosTargetFrameworks)</TargetFrameworks>'
            Add "    <RootNamespace>$($P.Ns)</RootNamespace>"
            Add '    <IsPackable>false</IsPackable>'
            Add '    <IsTestProject>true</IsTestProject>'
            Add '    <OutputType>Exe</OutputType>'
        }
        'sample' {
            Add '    <TargetFramework>$(BarbatosSampleTargetFramework)</TargetFramework>'
            Add "    <RootNamespace>$($P.Ns)</RootNamespace>"
            if (-not $P.Library) { Add '    <OutputType>Exe</OutputType>' }
            Add '    <IsPackable>false</IsPackable>'
        }
        'benchmark' {
            Add '    <TargetFramework>$(BarbatosSampleTargetFramework)</TargetFramework>'
            Add "    <RootNamespace>$($P.Ns)</RootNamespace>"
            Add '    <OutputType>Exe</OutputType>'
            Add '    <IsPackable>false</IsPackable>'
            Add '    <Optimize>true</Optimize>'
            Add '    <ServerGarbageCollection>true</ServerGarbageCollection>'
        }
        default {
            Add "    <PackageId>$($P.Name)</PackageId>"
            Add '    <TargetFrameworks>$(BarbatosTargetFrameworks)</TargetFrameworks>'
            Add "    <RootNamespace>$($P.Ns)</RootNamespace>"
            if ($P.ContainsKey('Tags')) { Add "    <PackageTags>$(Convert-XmlText $P.Tags)</PackageTags>" }
        }
    }

    if ($P.ContainsKey('Desc') -and $P.Kind -in @('lib', 'meta', 'generator')) {
        Add "    <Description>$(Convert-XmlText $P.Desc)</Description>"
    }
    if ($P.ContainsKey('Tags') -and $P.Kind -in @('meta', 'generator')) {
        Add "    <PackageTags>$(Convert-XmlText $P.Tags)</PackageTags>"
    }
    Add '  </PropertyGroup>'

    if ($P.Browser) {
        Add ''
        Add '  <!-- No platform-specific API: usable from WebAssembly tooling. -->'
        Add '  <ItemGroup>'
        Add '    <SupportedPlatform Include="browser" />'
        Add '  </ItemGroup>'
    }

    if ($P.Kind -eq 'generator') {
        Add ''
        Add '  <ItemGroup>'
        Add '    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" PrivateAssets="all" ExcludeAssets="runtime" />'
        Add '    <PackageReference Include="Microsoft.CodeAnalysis.Analyzers" PrivateAssets="all" />'
        Add '  </ItemGroup>'
        Add ''
        Add '  <ItemGroup>'
        Add '    <None Include="$(OutputPath)\$(AssemblyName).dll" Pack="true" PackagePath="analyzers/dotnet/cs" Visible="false" />'
        Add '  </ItemGroup>'
    }
    elseif ($P.Pkgs -and $P.Pkgs.Count -gt 0) {
        Add ''
        Add '  <ItemGroup>'
        foreach ($pkg in $P.Pkgs) { Add "    <PackageReference Include=`"$pkg`" />" }
        Add '  </ItemGroup>'
    }

    if ($P.Kind -eq 'test') {
        Add ''
        Add '  <ItemGroup>'
        Add '    <PackageReference Include="Microsoft.NET.Test.Sdk" />'
        Add '    <PackageReference Include="xunit.v3.mtp-off" />'
        Add '    <PackageReference Include="xunit.runner.visualstudio" />'
        Add '    <PackageReference Include="coverlet.collector" />'
        Add '  </ItemGroup>'
    }

    if ($P.Refs -and $P.Refs.Count -gt 0) {
        Add ''
        Add '  <ItemGroup>'
        foreach ($r in $P.Refs) {
            $rel = (Get-ProjectPath $r) -replace '/', '\'
            Add "    <ProjectReference Include=`"..\..\$rel`" />"
        }
        Add '  </ItemGroup>'
    }

    if ($P.ContainsKey('Analyzers')) {
        Add ''
        Add '  <!-- The Roslyn component runs over this project at compile time; it is not a'
        Add '       library dependency, so no assembly reference flows to consumers.'
        Add '       TODO(Phase 5): also pack it into this package under analyzers/dotnet/cs. -->'
        Add '  <ItemGroup>'
        foreach ($a in $P.Analyzers) {
            $rel = (Get-ProjectPath $a) -replace '/', '\'
            Add "    <ProjectReference Include=`"..\..\$rel`""
            Add '                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" PrivateAssets="all" />'
        }
        Add '  </ItemGroup>'
    }

    Add ''
    Add '</Project>'
    return $sb.ToString()
}

foreach ($p in $Projects) {
    $dir = "$($p.Group)/$($p.Name)"
    Set-RepoFile "$dir/$($p.Name).csproj" (New-CsprojXml $p)

    foreach ($f in $p.Folders) {
        Set-RepoFile "$dir/$f/.gitkeep" ''
    }

    # Every library shares one set of global usings. The Roslyn component is excluded: it sets
    # ImplicitUsings=false on purpose and wants its own, narrower set.
    if ($p.Kind -eq 'lib') {
        Set-RepoFile "$dir/Internal/Usings.cs" @"
$CsHeader

global using System.Buffers;
global using System.Buffers.Binary;
global using System.Diagnostics;
global using System.Diagnostics.CodeAnalysis;
global using System.Runtime.CompilerServices;
global using System.Runtime.InteropServices;
"@
    }

    # A per-package README is the nuget.org landing page, so only shipping projects get one.
    if ($p.Kind -in @('lib', 'meta', 'generator')) {
        $readme = @"
# $($p.Name)

$($p.Desc)

> Part of **[$RepoName]($RepoUrl)** - a modern, allocation-conscious SECS-II / HSMS-SS / GEM stack
> for .NET 8, 9 and 10.

## Install

``````
dotnet add package $($p.Name)
``````

## Documentation

- Concepts and worked examples: this file.
- Member-by-member reference: [``API-REFERENCE.md``](API-REFERENCE.md).
- Repository overview: [$RepoName]($RepoUrl).

## Status

Version $PkgVersion - the public surface is still settling. See the roadmap in the
[repository README]($RepoUrl#roadmap).
"@
        Set-RepoFile "$dir/README.md" $readme
        Set-RepoFile "$dir/API-REFERENCE.md" "# $($p.Name) - API reference`r`n`r`n_Generated per release. Populated as the public surface lands._`r`n"
    }
}

# ----- placeholders so every project compiles from the first build ---------------------------
Set-RepoFile 'src/Barbatos.SecsGem.Gem/GemRoadmap.cs' @"
$CsHeader

namespace Barbatos.SecsGem.Gem;

/// <summary>
/// Placeholder marking the GEM (SEMI E30) layer, delivered in Phase 7.
/// </summary>
/// <remarks>
/// The project exists from day one so the dependency direction
/// (<c>Gem</c> -&gt; <c>Hsms</c> -&gt; <c>Core</c>) is fixed before any code depends on it.
/// Planned contents: communication and control state models, S1F13/S1F14
/// establish-communications, collection events (CEID/RPTID), status and equipment
/// constants (SVID/ECID), alarms (ALID) and remote commands.
/// </remarks>
internal static class GemRoadmap
{
    internal const string Phase = "Phase 7";
}
"@

Set-RepoFile 'src/Barbatos.SecsGem/_Metapackage.cs' @"
$CsHeader

// Intentionally empty: this project exists only to aggregate package dependencies.
// <IncludeBuildOutput>false</IncludeBuildOutput> keeps the compiled assembly out of the .nupkg.
"@

foreach ($p in $Projects | Where-Object { $_.Kind -eq 'test' }) {
    Set-RepoFile "tests/$($p.Name)/PlaceholderTests.cs" @"
$CsHeader

namespace $($p.Ns);

public sealed class PlaceholderTests
{
    [Fact]
    public void Scaffold_builds_and_the_test_host_runs()
    {
        Assert.True(true);
    }
}
"@
    Set-RepoFile "tests/$($p.Name)/Usings.cs" @"
$CsHeader

global using Xunit;
"@
}

foreach ($p in $Projects | Where-Object { $_.Kind -eq 'sample' -and -not $_.Library }) {
    Set-RepoFile "samples/$($p.Name)/Program.cs" @"
$CsHeader

namespace $($p.Ns);

internal static class Program
{
    private static Task Main(string[] args)
    {
        Console.WriteLine("$($p.Name) - scaffold. See the repository README for the roadmap.");
        return Task.CompletedTask;
    }
}
"@
}

Set-RepoFile 'samples/Barbatos.SecsGem.Samples.Shared/SampleMessages.cs' @"
$CsHeader

namespace Barbatos.SecsGem.Samples.Shared;

/// <summary>Message shapes shared by the equipment and host samples.</summary>
public static class SampleMessages
{
}
"@

Set-RepoFile 'benchmarks/Barbatos.SecsGem.Benchmarks/Program.cs' @"
$CsHeader

using BenchmarkDotNet.Running;

namespace Barbatos.SecsGem.Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
"@

# =============================================================================================
# 4. Solution file
# =============================================================================================

$slnSb = New-Object System.Text.StringBuilder
[void] $slnSb.AppendLine('<Solution>')
[void] $slnSb.AppendLine('  <Configurations>')
[void] $slnSb.AppendLine('    <Platform Name="Any CPU" />')
[void] $slnSb.AppendLine('  </Configurations>')

foreach ($group in @('src', 'samples', 'tests', 'benchmarks')) {
    [void] $slnSb.AppendLine("  <Folder Name=`"/$group/`">")
    foreach ($p in $Projects | Where-Object { $_.Group -eq $group } | Sort-Object Name) {
        [void] $slnSb.AppendLine("    <Project Path=`"$group/$($p.Name)/$($p.Name).csproj`" />")
    }
    [void] $slnSb.AppendLine('  </Folder>')
}

[void] $slnSb.AppendLine('  <Folder Name="/Solution Items/">')
foreach ($f in @('.editorconfig', '.gitattributes', '.gitignore', 'Directory.Build.props',
                 'Directory.Build.targets', 'Directory.Packages.props', 'global.json',
                 'nuget.config', 'README.md', 'API-REFERENCE.md', 'CLAUDE.md',
                 'LICENSE.md', 'ThirdPartyNotices.txt')) {
    [void] $slnSb.AppendLine("    <File Path=`"$f`" />")
}
[void] $slnSb.AppendLine('  </Folder>')
[void] $slnSb.AppendLine('  <Folder Name="/.github/">')
[void] $slnSb.AppendLine('    <File Path=".github/workflows/ci.yml" />')
[void] $slnSb.AppendLine('  </Folder>')
[void] $slnSb.AppendLine('</Solution>')

Set-RepoFile "$RepoName.slnx" $slnSb.ToString()

# =============================================================================================
# 5. Root documentation
# =============================================================================================

Set-RepoFile 'README.md' @"
# $RepoName

A modern SECS-II / HSMS-SS / GEM stack for **.NET 8, 9 and 10** - allocation-conscious,
Native AOT friendly, and built around ``System.IO.Pipelines``, ``System.Threading.Channels``,
``Span<T>`` and ``ReadOnlySequence<byte>``.

Two things set it apart from what came before:

1. **A protocol layer that finishes the job.** Every HSMS timer is real and tested, T8
   included - scoped to the gap *between chunks of one message*, never to an idle link.
   The connection state machine is a single-threaded actor, so no transition can re-enter
   another. S9F1/F3/F5/F7/F9/F11/F13 are emitted automatically.
2. **Universal serialization.** One hub (``SecsMessage``/``SecsItem``), many spokes:
   SECS-II binary, ``.sml``, ``.json``, ``.xml``, ``.ini`` and plain C# objects, each
   convertible to any other. Adding a format never touches ``Barbatos.SecsGem.Core``.

## Packages

| Package | What it owns |
| --- | --- |
| ``Barbatos.SecsGem.Core`` | ``SecsItem``/``SecsMessage`` AST, binary codec, serialization contracts. No sockets. |
| ``Barbatos.SecsGem.Hsms`` | HSMS-SS state machine, dual-path pipe decoder, T3-T8, S9Fx, hosting and DI. |
| ``Barbatos.SecsGem`` | Metapackage: Core + Hsms + SML. |
| ``Barbatos.SecsGem.Serialization.Sml`` | ``.sml`` reader/writer. |
| ``Barbatos.SecsGem.Serialization.Json`` | ``.json`` reader/writer on ``Utf8JsonReader``. |
| ``Barbatos.SecsGem.Serialization.Xml`` | ``.xml`` reader/writer. |
| ``Barbatos.SecsGem.Serialization.Ini`` | ``.ini`` reader/writer, path-addressed keys. |
| ``Barbatos.SecsGem.Mapper`` | ``SecsSerializer``: C# objects <-> ``SecsItem``, convention over configuration. |
| ``Barbatos.SecsGem.SourceGenerators`` | Roslyn component behind the mapper's exact property order. |
| ``Barbatos.SecsGem.Gem`` | GEM (E30) layer. Phase 7. |

## Quick start

``````csharp
// The engine and its transport. Binds "Barbatos:SecsGem" and validates on start, so a T8
// longer than T3 fails the host rather than the first stalled message.
services.AddSecsGem(builder.Configuration).UseTcp();

// The format spokes and the object mapper. Each lives in its own package and extends
// IServiceCollection rather than the builder - which is exactly why adding a format never
// needs a change to Barbatos.SecsGem.Core.
services.AddSecsSml().AddSecsJson().AddSecsXml().AddSecsIni().AddSecsMapper();
``````

Converting files needs none of the protocol stack:

``````csharp
services.AddSecsSerialization().AddSecsSml().AddSecsJson();
await provider.GetRequiredService<SecsTranscoder>().TranscodeFileAsync("recipe.sml", "recipe.json");
``````

## Build

``````
dotnet build $RepoName.slnx
dotnet test  $RepoName.slnx
dotnet pack  $RepoName.slnx -c Release -o artifacts
``````

Do not pass ``-f`` at the solution level: it forces that framework onto every project, including
the ``netstandard2.0`` Roslyn component, which then fails with ``NETSDK1005``. To narrow to one
runtime, pass ``-f`` to a single test project instead.

## Roadmap

| Phase | Scope |
| --- | --- |
| 0 | Repository, build infrastructure, 22 projects. |
| 1 | Core AST, serialization contracts + ``SecsTranscoder``, ``SecsSerializer``, ``AddSecsGem``. |
| 2 | Binary codec, fast/chunked decoder, chunk-boundary tests. |
| 3 | HSMS state machine, T3-T8, S9Fx, loopback integration tests. |
| 4 | Four format spokes, 25-pair round-trip matrix. |
| 5 | Source generator + analyzer BSG001. |
| 6 | Benchmarks, samples, per-package documentation. |
| 7 | GEM layer. |

## Licence

MIT. See [LICENSE.md](LICENSE.md).
"@

Set-RepoFile 'API-REFERENCE.md' @"
# $RepoName - API reference

One page per package, generated per release:

- [``Barbatos.SecsGem.Core``](src/Barbatos.SecsGem.Core/API-REFERENCE.md)
- [``Barbatos.SecsGem.Hsms``](src/Barbatos.SecsGem.Hsms/API-REFERENCE.md)
- [``Barbatos.SecsGem.Serialization.Sml``](src/Barbatos.SecsGem.Serialization.Sml/API-REFERENCE.md)
- [``Barbatos.SecsGem.Serialization.Json``](src/Barbatos.SecsGem.Serialization.Json/API-REFERENCE.md)
- [``Barbatos.SecsGem.Serialization.Xml``](src/Barbatos.SecsGem.Serialization.Xml/API-REFERENCE.md)
- [``Barbatos.SecsGem.Serialization.Ini``](src/Barbatos.SecsGem.Serialization.Ini/API-REFERENCE.md)
- [``Barbatos.SecsGem.Mapper``](src/Barbatos.SecsGem.Mapper/API-REFERENCE.md)
- [``Barbatos.SecsGem.Gem``](src/Barbatos.SecsGem.Gem/API-REFERENCE.md)

## Namespaces

| Namespace | Assembly | Contents |
| --- | --- | --- |
| ``Barbatos.SecsGem`` | Core | ``SecsItem``, ``SecsMessage``, ``SecsFormat``, ``MessageHeader``, exceptions |
| ``Barbatos.SecsGem.Serialization`` | Core | ``ISecsFormatReader``, ``ISecsFormatWriter``, ``SecsTranscoder`` |
| ``Barbatos.SecsGem.Hsms`` | Hsms | connection, decoder, transactions, timers, routing |
| ``Barbatos.SecsGem.Mapping`` | Mapper | ``SecsSerializer``, attributes, converters |
| ``Barbatos.SecsGem.Gem`` | Gem | GEM state models and services |
| ``Microsoft.Extensions.DependencyInjection`` | Hsms, Mapper, spokes | ``AddSecsGem``, ``AddSecsSml``, ... |
"@

Set-RepoFile 'CLAUDE.md' @"
# $RepoName - working agreement

A family of NuGet packages implementing SECS-II (SEMI E5), HSMS-SS (E37) and GEM (E30) for
.NET 8/9/10. Everything here is a **library**: the public API is the product, and a careless
signature is a breaking change in somebody's fab.

## Repository map

``````
src/          nine shipping packages, one folder each (+ README.md, API-REFERENCE.md)
samples/      equipment (passive), host (active), transcode CLI, shared
tests/        one xUnit project per package + IntegrationTests
benchmarks/   BenchmarkDotNet
build/        package icons, strong-name key (git-ignored)
assets/       SML/JSON conformance fixtures
``````

## Layering - do not break this

``````
Core  <-  Hsms  <-  Gem
  ^-- Serialization.{Sml,Json,Xml,Ini}
  ^-- Mapper  <-- SourceGenerators (analyzer only)
``````

- ``Core`` must never reference a socket, a pipe transport or ``Microsoft.Extensions.*``.
  Its only third-party dependency is ``CommunityToolkit.HighPerformance``.
- A new format is a new assembly implementing ``ISecsFormatReader``/``ISecsFormatWriter`` plus an
  ``AddSecsXxx()`` method. **If adding a format edits ``Core``, the design is wrong.**

## Non-negotiables

- **.NET 8, 9, 10 only.** Never add ``netstandard`` or a ``net6``/``net7`` shim. TFMs come from
  ```$(BarbatosTargetFrameworks)`` in ``Directory.Build.props`` - never hard-coded in a ``.csproj``.
  ``net11.0`` lands there once the SDK is installed.
- **No obsolete I/O.** No ``BinaryFormatter``, no blocking ``NetworkStream``, no ``byte[]``
  allocation that can land on the Large Object Heap. Socket async, ``System.IO.Pipelines``,
  ``System.Threading.Channels``, ``Span<T>``, ``ReadOnlySequence<byte>``, ``ArrayPool``.
- **Timers go through ``TimeProvider``.** A test that waits on a wall clock is a flaky test.
- **C# 14, three runtimes.** Compiler-only features are fine everywhere; a type that ships with
  the runtime (``System.Threading.Lock``) needs ``#if NET9_0_OR_GREATER``.
- **No ``InternalsVisibleTo``.** If a test needs internal behaviour, that behaviour becomes
  documented public API, or the test goes through the public surface.
- **Public API discipline.** XML docs on every public member, ``<remarks>`` for the *why*; new
  types are ``sealed``; a rename keeps the old member as ``[Obsolete]``.
- **Central Package Management.** Every version lives in ``Directory.Packages.props``.
- ``.editorconfig`` is the style authority, including the MIT header on every new ``.cs`` file.

## Build and test

``````
dotnet build $RepoName.slnx
dotnet test tests/Barbatos.SecsGem.Core.UnitTests -f net10.0
``````

Run one target framework at a time.

## Documentation is part of the change

A public API change is not done until the package's ``README.md`` (the nuget.org landing page)
and ``API-REFERENCE.md`` are both updated, plus the sample that demonstrates it.
"@

# =============================================================================================
# 6. git
# =============================================================================================

if (-not $NoGit) {
    if (Test-Path -LiteralPath (Join-Path $Root '.git')) {
        Write-Host 'git  : repository already initialised, left alone.' -ForegroundColor DarkGray
    }
    else {
        Push-Location $Root
        try {
            & git init --initial-branch=main 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Host 'git  : initialised on branch main (nothing committed).' -ForegroundColor DarkGray }
            else { Write-Warning 'git init failed - is git on PATH?' }
        }
        finally { Pop-Location }
    }
}

# =============================================================================================
# 7. Report
# =============================================================================================

Write-Host ''
Write-Host ("Created {0} file(s)." -f $script:Created.Count) -ForegroundColor Green
if ($script:Skipped.Count -gt 0) {
    Write-Host ("Skipped {0} existing file(s). Re-run with -Force to overwrite." -f $script:Skipped.Count) -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Next:' -ForegroundColor Cyan
Write-Host "  dotnet restore $RepoName.slnx"
Write-Host "  dotnet build   $RepoName.slnx"
