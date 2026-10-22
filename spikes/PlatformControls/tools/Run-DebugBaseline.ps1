# Spike 4.4 attribution: launch the spike under `winapp run --debug-output`, optionally exercise it through UIA,
# close it through the title-bar Close button (UIA Invoke), and summarize first-chance exception codes.
# Usage: pwsh -File Run-DebugBaseline.ps1 -Label settings-idle [-AppArgs '--projection'] [-Exercise {param($hwnd,$pid) ...}]
param(
    [Parameter(Mandatory)][string]$Label,
    [string]$AppArgs = '',
    [scriptblock]$Exercise,
    [string]$OutDir = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\docs\evidence\phase-0\platform-controls')).Path
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\PlatformControls.csproj'
$runLog = Join-Path $OutDir "12-debug-$Label-run.txt"
$runArgs = @('run', $project, '-c', 'Release', '--arch', 'x64', '-p', 'Platform=x64', '--no-build', '--debug-output')
if ($AppArgs) { $runArgs += @('--args', $AppArgs) }
$job = Start-Job -ScriptBlock { param($a, $log) & winapp @a *> $log } -ArgumentList (, $runArgs), $runLog

$process = $null
for ($i = 0; $i -lt 60 -and -not $process; $i++) {
    Start-Sleep -Seconds 1
    $process = Get-Process PlatformControls -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $process) { throw 'Spike process did not start' }
$window = $null
for ($i = 0; $i -lt 30 -and -not $window; $i++) {
    Start-Sleep -Seconds 1
    $window = winapp ui list-windows -a $process.Id --json 2>$null | ConvertFrom-Json | Where-Object { $_.title -eq 'Platform Controls spike' } | Select-Object -First 1
}
Start-Sleep -Seconds 3
if ($Exercise) { & $Exercise ([string]$window.hwnd) $process.Id }
winapp ui invoke Close -w ([string]$window.hwnd) | Out-Null
Wait-Job $job -Timeout 60 | Out-Null
Remove-Job $job -Force

$debugLog = (Select-String -Path $runLog -Pattern 'winapp-dumps\\debug-.*\.log' -AllMatches | Select-Object -Last 1)
$text = Get-Content $runLog -Raw
$path = [regex]::Match(($text -replace "`r?`n", ''), '[A-Z]:\\[^\s]*winapp-dumps\\debug-[0-9-]+\.log').Value
$codes = Get-Content $path | ForEach-Object {
    if ($_ -match 'First-chance exception: .+? \((0x[0-9A-F]+)\)') { $Matches[1] } elseif ($_ -match '^\[Debug\]') { 'OutputDebugString' }
} | Group-Object | Sort-Object Count -Descending | ForEach-Object { "$($_.Count) x $($_.Name)" }
$summary = "label=$Label args='$AppArgs' pid=$($process.Id) debugLog=$path`n" + ($codes -join "`n")
Add-Content $runLog "`n==== SUMMARY`n$summary"
Copy-Item $path (Join-Path $OutDir "12-debug-$Label-firstchance.log") -Force
$summary
