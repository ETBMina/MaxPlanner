#Requires -Version 7.0
<#
.SYNOPSIS
  Runs the automated test projects through Microsoft Testing Platform (global.json "test.runner").
.PARAMETER Configuration
  Debug (default) or Release.
.PARAMETER NoBuild
  Skip building; run the existing test binaries.
.PARAMETER IncludeSmoke
  Also run the Desktop smoke tests (Category=Smoke). They launch the app, so they are skipped by default.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$IncludeSmoke
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$results = Join-Path $root 'artifacts/test-results'

$testArgs = @('test', '--solution', (Join-Path $root 'MaxPlanner.slnx'), '-c', $Configuration, '-p:Platform=x64',
    '--results-directory', $results, '--report-xunit-trx')
if ($NoBuild) { $testArgs += '--no-build' }
# Without -IncludeSmoke the smoke project runs zero tests, which Microsoft Testing Platform reports as exit code 8.
if (-not $IncludeSmoke) { $testArgs += @('--filter-not-trait', 'Category=Smoke', '--ignore-exit-code', '8') }

& dotnet @testArgs
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE)." }
