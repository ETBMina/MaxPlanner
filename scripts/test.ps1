#Requires -Version 7.0
<#
.SYNOPSIS
  Runs the automated test projects through Microsoft Testing Platform (global.json "test.runner").
.PARAMETER Configuration
  Debug (default) or Release.
.PARAMETER NoBuild
  Skip building; run the existing test binaries.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts/test-results'

$testArgs = @('test', '--solution', (Join-Path $root 'MaxPlanner.slnx'), '-c', $Configuration, '-p:Platform=x64',
    '--results-directory', $results, '--report-xunit-trx')
if ($NoBuild) { $testArgs += '--no-build' }

& dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
