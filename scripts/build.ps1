#Requires -Version 7.0
<#
.SYNOPSIS
  Restores and builds the Max Planner solution (x64). Warnings are errors (Directory.Build.props).
.PARAMETER Configuration
  Debug (default) or Release.
.PARAMETER Locked
  Restore strictly from packages.lock.json files (CI / reproducible builds).
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$Locked
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'MaxPlanner.slnx'

$restoreArgs = @('restore', $solution, '-p:Platform=x64')
if ($Locked) { $restoreArgs += '--locked-mode' }
& dotnet @restoreArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed ($LASTEXITCODE)." }

& dotnet build $solution -c $Configuration -p:Platform=x64 --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)." }
