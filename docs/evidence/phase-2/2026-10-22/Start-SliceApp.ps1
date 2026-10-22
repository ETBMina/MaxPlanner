#Requires -Version 7.0
<#
.SYNOPSIS
  Phase 2 evidence helper: copies the built app to a disposable temp folder (path with a space and "ë"),
  creates the fixture dataset there through the real use cases, and launches the EXE from that copy.
.OUTPUTS
  An object with Root, ProcessId and Hwnd. The caller closes the app with CloseMainWindow (WM_CLOSE to its
  own window) and deletes Root.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Debug',
    [string]$Root,
    [switch]$NoFixture,
    [switch]$DebugHooks,
    [ValidateSet('System', 'Light', 'Dark')][string]$Theme = 'System',
    [string]$ActivePage = 'grid',
    # Physical pixels, "width,height" (a restored window; a full-screen window captures black, see the evidence md).
    [string[]]$WindowSize = @('1280', '800')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$build = Join-Path $repo "src/MaxPlanner.Desktop/bin/$Configuration/net10.0-windows10.0.26100.0/win-x64"
if (-not $Root) { $Root = Join-Path ([IO.Path]::GetTempPath()) ("MaxPlanner Tëst UI\slice-" + [guid]::NewGuid().ToString('N').Substring(0, 8)) }

if (-not (Test-Path (Join-Path $Root 'MaxPlanner.exe'))) {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Copy-Item -Path (Join-Path $build '*') -Destination $Root -Recurse -Exclude 'data'
    if (-not $NoFixture) {
        $fixture = Get-ChildItem (Join-Path $repo 'tools/MaxPlanner.FixtureGenerator/bin') -Recurse -Filter 'MaxPlanner.FixtureGenerator.exe' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        & $fixture.FullName $Root | Write-Host
        if ($LASTEXITCODE -ne 0) { throw "Fixture generator failed ($LASTEXITCODE)." }
    }

    # Preferences only beside a fixture: a configuration file in a folder without a dataset means "used
    # before" and would open the recovery page instead of the first run.
    if (-not $NoFixture) {
        $config = [ordered]@{ configurationVersion = 1; theme = $Theme; activePage = $ActivePage }
        $size = @($WindowSize -join ',' -split ',' | ForEach-Object { [int]$_ })
        $config.window = [ordered]@{ bounds = [ordered]@{ x = 40; y = 40; width = $size[0]; height = $size[1] }; state = 'Normal' }
        $config | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Root 'data/configuration.json') -Encoding utf8
    }
}

function Get-ReadyCount {
    $count = 0
    foreach ($log in Get-ChildItem (Join-Path $Root 'data/logs') -Filter '*.log' -ErrorAction SilentlyContinue) {
        # The app keeps its log open for writing; read it with full sharing.
        $stream = [IO.FileStream]::new($log.FullName, 'Open', 'Read', 'ReadWrite,Delete')
        try { $text = [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
        $count += ([regex]::Matches($text, 'Startup (ready|recovery):')).Count
    }
    $count
}

$logStart = Get-ReadyCount

# Start-Process (shell execute) so the app does not inherit this script's console handles; the caller's
# shell would otherwise wait for the app to exit. The hook variable is inherited from this process only.
if ($DebugHooks) { $env:MAXPLANNER_DEBUG_HOOKS = '1' } else { Remove-Item Env:MAXPLANNER_DEBUG_HOOKS -ErrorAction SilentlyContinue }
$process = Start-Process -FilePath (Join-Path $Root 'MaxPlanner.exe') -WorkingDirectory ([IO.Path]::GetTempPath()) -PassThru

$deadline = (Get-Date).AddSeconds(60)
do {
    Start-Sleep -Milliseconds 300
    $process.Refresh()
    if ($process.HasExited) { throw "MaxPlanner exited early with code $($process.ExitCode)." }
    $ready = (Get-ReadyCount) -gt $logStart
} until (($ready -and $process.MainWindowHandle -ne 0) -or (Get-Date) -gt $deadline)
if (-not $ready) { throw 'The app did not reach Ready within 60 s.' }

[pscustomobject]@{ Root = $Root; ProcessId = $process.Id; Hwnd = [string]$process.MainWindowHandle }
