#Requires -Version 7.0
<#
.SYNOPSIS
  Runs the T-11 performance gate (Phase 17, run in Phase 19): generates the benchmark fixture, runs the non-UI
  BenchmarkDotNet suite, then launches the published portable EXE on the fixture and checks the app-level budgets
  from its own log lines. Results go to docs/evidence/phase-17/<date>/.
.DESCRIPTION
  1. Fixture: tools/MaxPlanner.FixtureGenerator "benchmark" mode creates 10,000 tasks, 5,000 CRs, 50,000 checklist
     items, 2,000 Subtask cards, 2,000 reviews, 5,000 General to-dos and 20% archived tasks through the real use cases.
  2. Benchmarks: tests/MaxPlanner.Benchmarks (Release, in-process) measures snapshot load, projection store initial
     load, save-to-publication latency, Dashboard recompute, filter apply, and fixture export/import.
  3. App runs: the published ZIP is extracted to a disposable folder, the fixture is copied into its data/ folder, and
     the EXE is launched -Launches times. Each run reads, from data/logs:
       - "Startup ready" (event 5003) and "Startup rendered" (event 5016): startup to a usable view, budget <= 3 s on
         every launch;
       - "Page shown" (event 5605) for -NavigationCycles rounds of Ctrl+1 .. Ctrl+5 sent with "winapp ui send-keys":
         page-switch p95 <= 250 ms;
       - "Navigated to ...; live store subscriptions N; working set M MiB" (event 5010): subscriptions must not grow
         over the cycles (AC-17), and the idle working set after the cycles must stay <= 500 MiB.
  The script never touches real user data: everything runs in a new temp folder that it deletes unless -KeepWork.
.NOTES
  Requires the .NET SDK (global.json), a published ZIP (scripts/publish-portable.ps1), and WinApp CLI 0.7+ for the
  navigation cycles (skipped with a recorded limitation when winapp is missing). Run on the documented benchmark
  machine (T-11: Windows 11 x64, SSD, >= 4 logical cores, >= 8 GiB RAM) with no other heavy work running.
  "Cold" launches here are new processes on a warm OS file cache; a true cold-cache run needs a restart before each
  launch and is recorded separately if required.
#>
[CmdletBinding()]
param(
    [string]$Zip,
    [ValidateRange(0.01, 1.0)][double]$Scale = 1.0,
    [ValidateRange(1, 50)][int]$Launches = 5,
    [ValidateRange(0, 500)][int]$NavigationCycles = 20,
    [string]$EvidenceDir,
    [switch]$SkipBenchmarks,
    [switch]$SkipAppRuns,
    [switch]$KeepWork,
    [int]$StartupTimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Zip) {
    $Zip = Get-ChildItem (Join-Path $root 'artifacts') -Filter 'MaxPlanner-*-win-x64.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $EvidenceDir) {
    $EvidenceDir = Join-Path $root ("docs/evidence/phase-17/" + (Get-Date -Format 'yyyy-MM-dd'))
}
New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ("MaxPlanner Perf " + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$fixture = Join-Path $work 'fixture'
New-Item -ItemType Directory -Force -Path $fixture | Out-Null

$budgets = [ordered]@{
    StartupMs        = 3000
    PageSwitchP95Ms  = 250
    IdleWorkingSetMiB = 500
}
$results = [ordered]@{
    Date        = (Get-Date).ToString('o')
    Machine     = $null
    Fixture     = $null
    Benchmarks  = $null
    Launches    = @()
    Navigation  = $null
    Verdicts    = [ordered]@{}
    Limitations = @()
}

function Get-Percentile([double[]]$values, [double]$p) {
    if (-not $values -or $values.Count -eq 0) { return $null }
    $sorted = $values | Sort-Object
    $rank = [Math]::Ceiling($p * $sorted.Count) - 1
    return $sorted[[Math]::Max(0, [Math]::Min($rank, $sorted.Count - 1))]
}

function Read-AppLog([string]$dataDir) {
    $logs = Join-Path $dataDir 'logs'
    if (-not (Test-Path $logs)) { return @() }
    return Get-ChildItem $logs -Filter 'maxplanner-*.log' | Sort-Object Name | ForEach-Object { Get-Content $_.FullName }
}

function Wait-LogLine([string]$dataDir, [string]$pattern, [System.Diagnostics.Process]$process, [int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    do {
        $match = Read-AppLog $dataDir | Select-String -Pattern $pattern | Select-Object -Last 1
        if ($match) { return $match }
        if ($process.HasExited) { throw "MaxPlanner exited with code $($process.ExitCode) before logging '$pattern'." }
        Start-Sleep -Milliseconds 250
    } until ((Get-Date) -gt $deadline)
    throw "Timed out after $timeoutSeconds s waiting for '$pattern'."
}

function Stop-App([System.Diagnostics.Process]$process) {
    if ($process.HasExited) { return }
    $process.CloseMainWindow() | Out-Null
    if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit() }
}

# ----- Machine record (T-11: record hardware/software details) -----
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$disk = Get-PhysicalDisk -ErrorAction SilentlyContinue | Select-Object -First 1
$results.Machine = [ordered]@{
    Os             = "$($os.Caption) $($os.Version) build $($os.BuildNumber)"
    Cpu            = $cpu.Name
    LogicalCores   = [Environment]::ProcessorCount
    MemoryGiB      = [Math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    Disk           = if ($disk) { "$($disk.FriendlyName) ($($disk.MediaType))" } else { 'unknown' }
    DotNetSdk      = (& dotnet --version)
    Zip            = $Zip
}

try {
    # ----- 1. Fixture -----
    Write-Host "Generating the benchmark fixture (scale $Scale) in $fixture ..."
    $generator = Join-Path $root 'tools/MaxPlanner.FixtureGenerator/MaxPlanner.FixtureGenerator.csproj'
    $scaleText = $Scale.ToString([Globalization.CultureInfo]::InvariantCulture)
    $output = & dotnet run -c Release --project $generator -p:Platform=x64 -- benchmark $fixture --scale $scaleText 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Fixture generation failed ($LASTEXITCODE):`n$output" }
    $results.Fixture = ($output | Select-String -Pattern '^Created ' | Select-Object -Last 1).Line
    Write-Host $results.Fixture

    # ----- 2. Benchmarks -----
    if (-not $SkipBenchmarks) {
        $benchArtifacts = Join-Path $EvidenceDir 'benchmarks'
        $env:MAXPLANNER_FIXTURE = $fixture
        $env:MAXPLANNER_BENCHMARK_ARTIFACTS = $benchArtifacts
        try {
            & dotnet run -c Release --project (Join-Path $root 'tests/MaxPlanner.Benchmarks/MaxPlanner.Benchmarks.csproj') -p:Platform=x64 -- --filter '*'
            if ($LASTEXITCODE -ne 0) { throw "The benchmarks failed ($LASTEXITCODE)." }
        }
        finally {
            Remove-Item Env:MAXPLANNER_FIXTURE, Env:MAXPLANNER_BENCHMARK_ARTIFACTS -ErrorAction SilentlyContinue
        }
        $results.Benchmarks = (Get-ChildItem (Join-Path $benchArtifacts 'results') -Filter '*-report-github.md' -ErrorAction SilentlyContinue |
            ForEach-Object { [IO.Path]::GetRelativePath($EvidenceDir, $_.FullName) })
    }
    else {
        $results.Limitations += 'Benchmarks skipped (-SkipBenchmarks).'
    }

    # ----- 3. App runs on the published ZIP -----
    if (-not $SkipAppRuns) {
        if (-not $Zip -or -not (Test-Path $Zip)) { throw "Published ZIP not found; run scripts/publish-portable.ps1 or pass -Zip." }
        $appRoot = Join-Path $work 'app'
        Expand-Archive -Path $Zip -DestinationPath $appRoot
        $exe = Join-Path $appRoot 'MaxPlanner/MaxPlanner.exe'
        $data = Join-Path $appRoot 'MaxPlanner/data'
        New-Item -ItemType Directory -Force -Path $data | Out-Null
        Get-ChildItem (Join-Path $fixture 'data') -Filter 'data.db*' | Copy-Item -Destination $data
        $winapp = Get-Command winapp -ErrorAction SilentlyContinue

        for ($run = 1; $run -le $Launches; $run++) {
            Remove-Item (Join-Path $data 'logs') -Recurse -Force -ErrorAction SilentlyContinue
            $process = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetTempPath()) -PassThru
            try {
                $ready = Wait-LogLine $data 'Startup ready: usable view at (\d+) ms' $process $StartupTimeoutSeconds
                $rendered = Wait-LogLine $data 'Startup rendered: first frame with the ready view at (\d+) ms' $process $StartupTimeoutSeconds
                $launch = [ordered]@{
                    Run        = $run
                    ReadyMs    = [int]$ready.Matches[0].Groups[1].Value
                    RenderedMs = [int]$rendered.Matches[0].Groups[1].Value
                }

                # Navigation cycles and the idle working set are measured on the first launch only.
                if ($run -eq 1 -and $NavigationCycles -gt 0) {
                    if ($winapp) {
                        Start-Sleep -Seconds 2
                        for ($cycle = 0; $cycle -lt $NavigationCycles; $cycle++) {
                            foreach ($key in 'ctrl+2', 'ctrl+3', 'ctrl+4', 'ctrl+5', 'ctrl+1') {
                                & winapp ui send-keys $key -a $process.Id --via send-input --quiet | Out-Null
                                Start-Sleep -Milliseconds 400
                            }
                        }

                        Start-Sleep -Seconds 5
                        $log = Read-AppLog $data
                        $pages = $log | Select-String -Pattern 'Page shown: (\w+) rendered (\d+) ms' | ForEach-Object { [double]$_.Matches[0].Groups[2].Value }
                        $navigated = $log | Select-String -Pattern 'live store subscriptions (\d+); working set (\d+) MiB'
                        $subscriptions = $navigated | ForEach-Object { [int]$_.Matches[0].Groups[1].Value }
                        $process.Refresh()
                        $results.Navigation = [ordered]@{
                            Switches             = $pages.Count
                            P50Ms                = Get-Percentile $pages 0.50
                            P95Ms                = Get-Percentile $pages 0.95
                            MaxMs                = ($pages | Measure-Object -Maximum).Maximum
                            SubscriptionsFirst   = $subscriptions | Select-Object -First 5
                            SubscriptionsLast    = $subscriptions | Select-Object -Last 5
                            IdleWorkingSetMiB    = [Math]::Round($process.WorkingSet64 / 1MB, 1)
                        }
                    }
                    else {
                        $results.Limitations += 'WinApp CLI not found: navigation cycles, page-switch p95, and idle working set were not measured.'
                    }
                }

                $results.Launches += $launch
            }
            finally {
                Stop-App $process
            }
        }
    }
    else {
        $results.Limitations += 'App runs skipped (-SkipAppRuns).'
    }

    # ----- Verdicts against T-11 -----
    if ($results.Launches.Count -gt 0) {
        $worst = ($results.Launches | ForEach-Object { $_.RenderedMs } | Measure-Object -Maximum).Maximum
        $results.Verdicts.Startup = [ordered]@{ WorstRenderedMs = $worst; BudgetMs = $budgets.StartupMs; Pass = $worst -le $budgets.StartupMs }
    }
    if ($results.Navigation) {
        $nav = $results.Navigation
        $results.Verdicts.PageSwitch = [ordered]@{ P95Ms = $nav.P95Ms; BudgetMs = $budgets.PageSwitchP95Ms; Pass = $nav.P95Ms -le $budgets.PageSwitchP95Ms }
        $results.Verdicts.IdleWorkingSet = [ordered]@{ MiB = $nav.IdleWorkingSetMiB; BudgetMiB = $budgets.IdleWorkingSetMiB; Pass = $nav.IdleWorkingSetMiB -le $budgets.IdleWorkingSetMiB }
        $first = ($nav.SubscriptionsFirst | Measure-Object -Maximum).Maximum
        $last = ($nav.SubscriptionsLast | Measure-Object -Maximum).Maximum
        $results.Verdicts.Subscriptions = [ordered]@{ FirstCyclesMax = $first; LastCyclesMax = $last; Pass = $last -le $first }
    }
    $results.Limitations += 'Save-to-visible-UI, search-to-visible-UI, Dashboard-after-commit in the UI, and UI-thread stalls during import are not scripted here; the benchmarks measure their non-UI part and the Phase 19 UI run records the rest.'
}
finally {
    $json = Join-Path $EvidenceDir 'performance-results.json'
    $results | ConvertTo-Json -Depth 6 | Set-Content $json -Encoding utf8
    Write-Host "Results: $json"
    if (-not $KeepWork) { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Work folder kept: $work" }
}

$failed = $results.Verdicts.Values | Where-Object { -not $_.Pass }
if ($failed) { Write-Warning 'At least one T-11 budget was missed; see performance-results.json.'; exit 1 }
