#Requires -Version 7.0
<#
.SYNOPSIS
  Publishes the portable, self-contained win-x64 build and packages it as a ZIP (T-03, ADR 0003).
.DESCRIPTION
  Output: artifacts/MaxPlanner-<Version>-win-x64.zip (root folder MaxPlanner/) plus a .sha256 file.
  The payload carries THIRD-PARTY-NOTICES.txt (checked against the published dependencies), README.txt for end users,
  and SHA256SUMS.txt with the checksum of every other file. The ZIP never contains a data/ folder, so extracting an
  update cannot overwrite user records (T-03.8), and never contains the development tools (fixture generator,
  benchmarks).
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish/MaxPlanner'
$zip = Join-Path $artifacts "MaxPlanner-$Version-win-x64.zip"

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

# Self-contained, WindowsAppSDKSelfContained, and the no-trim/AOT/single-file settings live in the Desktop project.
# Global -p: overrides would also reach the Presentation class library, which rejects WindowsAppSDKSelfContained.
& dotnet publish (Join-Path $root 'src/MaxPlanner.Desktop/MaxPlanner.Desktop.csproj') `
    -c $Configuration -r win-x64 -p:Platform=x64 -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# Unpackaged apps name their resource index <AssemblyName>.pri rather than resources.pri.
$required = 'MaxPlanner.exe', 'MaxPlanner.dll', 'MaxPlanner.pri', 'Microsoft.ui.xaml.dll', 'hostfxr.dll', 'e_sqlite3.dll'
foreach ($file in $required) {
    if (-not (Get-ChildItem $publishDir -Recurse -Filter $file -ErrorAction SilentlyContinue)) {
        throw "Published output is missing $file."
    }
}
if (Test-Path (Join-Path $publishDir 'data')) { throw 'Published output must not contain a data/ folder (T-03.8).' }

# Development tools never ship (Phase 17): the fixture generator and the benchmark suite are separate projects.
$forbidden = 'MaxPlanner.FixtureGenerator*', 'MaxPlanner.Benchmarks*', 'BenchmarkDotNet*', '*.Tests.dll', 'xunit*'
foreach ($pattern in $forbidden) {
    if (Get-ChildItem $publishDir -Recurse -Filter $pattern -ErrorAction SilentlyContinue) {
        throw "Published output must not contain development files ($pattern)."
    }
}
Get-ChildItem $publishDir -Recurse -Include '*.user', '*.pdb' | Remove-Item -Force

# T-03.9: dependency notices. The committed notices must list exactly the packages in this payload.
& (Join-Path $root 'tools/Generate-Notices.ps1') -DepsFile (Join-Path $publishDir 'MaxPlanner.deps.json') -Check
if ($LASTEXITCODE -ne 0) { throw 'THIRD-PARTY-NOTICES.txt is out of date; run tools/Generate-Notices.ps1 and publish again.' }
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $publishDir
Copy-Item (Join-Path $root 'eng/portable/README.txt') $publishDir

$sums = Get-ChildItem $publishDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($publishDir, $_.FullName)
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $relative
}
Set-Content (Join-Path $publishDir 'SHA256SUMS.txt') $sums -Encoding utf8

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $publishDir -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content "$zip.sha256" "$hash  $(Split-Path $zip -Leaf)" -Encoding utf8

Write-Host "Portable ZIP: $zip"
Write-Host "SHA-256:      $hash"
