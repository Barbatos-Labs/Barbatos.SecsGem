# This Source Code Form is subject to the terms of the MIT License.
# If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
# Copyright (C) Barbatos Labs | Pham The Hung and Barbatos.SecsGem Contributors.
# All Rights Reserved.

<#
.SYNOPSIS
    Builds and runs a consumer against the packed .nupkg files rather than against the projects.

.DESCRIPTION
    Everything else in this repository is wired with ProjectReference, which cannot tell you
    whether the packages work. This packs the solution into artifacts/smoke-feed, then restores,
    builds and runs build/package-smoke/Consumer against that feed on every target framework.

    The feed path is fixed, because build/package-smoke/nuget.config names it relatively and maps
    Barbatos.SecsGem* to it exclusively: a package missing from the feed fails the restore instead
    of quietly arriving from nuget.org.

    It removes barbatos.secsgem* from the NuGet global packages cache first. Every build stamps the
    same version, so without that a second run would restore the first run's packages and prove
    nothing. Nothing in this repository resolves those from the cache - the solution uses
    ProjectReference - so removing them cannot break a normal build.

.PARAMETER Configuration
    Build configuration for both the pack and the consumer. Release by default, which is what ships.

.PARAMETER Frameworks
    The target frameworks to run the consumer on. All three by default.

.PARAMETER SkipPack
    Use the packages already in artifacts/smoke-feed. For CI, which packs in a step of its own.

.EXAMPLE
    pwsh build/package-smoke/verify-packages.ps1

.EXAMPLE
    pwsh build/package-smoke/verify-packages.ps1 -Frameworks net10.0
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string[]] $Frameworks = @('net8.0', 'net9.0', 'net10.0'),
    [switch] $SkipPack
)

$ErrorActionPreference = 'Stop'

# Forward slashes throughout: PowerShell and the .NET SDK both accept them on Windows, and this
# script runs on the Linux CI leg as well.
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$solution = Join-Path $repository 'Barbatos.SecsGem.slnx'
$feed = Join-Path $repository 'artifacts/smoke-feed'
$consumer = Join-Path $PSScriptRoot 'Consumer/Consumer.csproj'

# The version lives in exactly one file. Reading it here rather than restating it keeps the
# consumer's PackageReference entries correct through every version bump.
[xml] $buildProps = Get-Content (Join-Path $repository 'Directory.Build.props')
$version = $null
foreach ($group in $buildProps.Project.PropertyGroup) {
    if ($group.Version) { $version = $group.Version }
}

if (-not $version) {
    throw 'Could not read <Version> from Directory.Build.props.'
}

Write-Host "repository : $repository"
Write-Host "version    : $version"
Write-Host "frameworks : $($Frameworks -join ', ')"
Write-Host ''

if ($SkipPack) {
    if (-not (Test-Path $feed)) {
        throw "-SkipPack was passed but $feed does not exist."
    }
}
else {
    if (Test-Path $feed) { Remove-Item $feed -Recurse -Force }
    New-Item -ItemType Directory -Path $feed -Force | Out-Null

    Write-Host "packing into $feed"
    & dotnet pack $solution -c $Configuration -o $feed
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE." }
    Write-Host ''
}

$packages = Get-ChildItem (Join-Path $feed '*.nupkg') | Sort-Object Name
if ($packages.Count -eq 0) {
    throw "No .nupkg files in $feed."
}
Write-Host "$($packages.Count) package(s) in the feed"

# The cache, for the reason in the .DESCRIPTION above.
$cache = $null
foreach ($line in (& dotnet nuget locals global-packages --list)) {
    $match = [regex]::Match($line, 'global-packages:\s*(?<path>.+)$')
    if ($match.Success) { $cache = $match.Groups['path'].Value.Trim() }
}

if ($cache -and (Test-Path $cache)) {
    $stale = Get-ChildItem $cache -Directory | Where-Object { $_.Name -like 'barbatos.secsgem*' }
    foreach ($directory in $stale) {
        Remove-Item $directory.FullName -Recurse -Force
    }
    Write-Host "removed $($stale.Count) stale cache entr$(if ($stale.Count -eq 1) { 'y' } else { 'ies' }) from $cache"
}
else {
    Write-Warning 'Could not locate the NuGet global packages cache; a stale restore is possible.'
}

# A consumer starts from nothing, so neither does this one carry anything over.
foreach ($directory in @('obj', 'bin')) {
    $path = Join-Path $PSScriptRoot "Consumer/$directory"
    if (Test-Path $path) { Remove-Item $path -Recurse -Force }
}

Write-Host ''
Write-Host 'restoring the consumer from the feed'
& dotnet restore $consumer "-p:BarbatosVersion=$version"
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

$failed = @()
foreach ($framework in $Frameworks) {
    Write-Host ''
    Write-Host "--- $framework ---"
    & dotnet run --project $consumer -c $Configuration -f $framework --no-restore "-p:BarbatosVersion=$version"
    if ($LASTEXITCODE -ne 0) {
        $failed += $framework
        Write-Host "$framework : exit code $LASTEXITCODE"
    }
}

Write-Host ''
if ($failed.Count -gt 0) {
    throw "The consumer failed on: $($failed -join ', ')."
}

Write-Host "package smoke test passed on $($Frameworks -join ', ')"
