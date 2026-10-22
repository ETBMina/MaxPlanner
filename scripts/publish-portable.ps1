#Requires -Version 7.0
<#
.SYNOPSIS
  Publishes the portable, self-contained win-x64 build and packages it as a ZIP (T-03, ADR 0003).
.DESCRIPTION
  Output: artifacts/MaxPlanner-<Version>-win-x64.zip (root folder MaxPlanner/) plus a .sha256 file.
  The ZIP never contains a data/ folder, so extracting an update cannot overwrite user records.
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

# Self-contained, WindowsAppSDKSelfContained, and the no-trim/AOT/single-file settings live in the
# Desktop project. Global -p: overrides would also reach the Presentation class library, which
# rejects WindowsAppSDKSelfContained.
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
Get-ChildItem $publishDir -Recurse -Include '*.user', '*.pdb' | Remove-Item -Force

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
