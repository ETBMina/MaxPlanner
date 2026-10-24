#Requires -Version 7.0
<#
.SYNOPSIS
  Release gates for the portable ZIP (T-03, T-11.6, AC-01/AC-02; Phase 18, run in Phase 19). Each gate extracts the
  ZIP into its own disposable folder whose path has spaces and non-ASCII characters, launches MaxPlanner.exe from an
  unrelated working directory, and checks the behaviour through the process, its files, and its own log lines.
.DESCRIPTION
  Gates (-Gates, default All):
    Launch            A responsive top-level window appears; data/ with .lock and data.db is created beside the EXE;
                      the first run reaches "Startup ready"; closing exits with code 0.
    Checksums         The ZIP matches its .sha256 file; every file matches SHA256SUMS.txt and every file is listed;
                      THIRD-PARTY-NOTICES.txt and README.txt are present; the ZIP has no data/ folder.
    Relocation        Data created in one folder survives moving the whole folder (to -SecondDrive when given): the
                      moved copy opens the same dataset (no first-run creation, no recovery) and data.db is unchanged.
    SecondLaunch      A second launch of the same folder exits with code 0 (redirect) and leaves one running instance.
    ReadOnlyFolder    A data/ folder the user cannot write to stops the app with its message and exit code 2 (the ACL
                      is denied for the current user on a disposable copy and restored afterwards).
    LockedDataset     data/.lock held open by another process stops the app with its message and exit code 3.
    NewerSchema       A dataset whose schema is newer than the app shows the recovery page; closing exits with code 4
                      (uses the fixture generator's set-schema-version on a disposable copy).
    Upgrade           Extracting the ZIP over a folder that already has data/ (from -PreviousZip, default this ZIP)
                      leaves data/ byte-for-byte unchanged, and the new build opens it without recovery.
    DependencyAbsence The run adds no MaxPlanner AppData folders, HKCU\Software keys, or package registrations, adds
                      no Windows App Runtime package, and loads the runtime DLLs (coreclr, WinUI, Windows App Runtime,
                      e_sqlite3) from the app folder only.
  Results: a table on screen and verify-portable.json in -EvidenceDir (default docs/evidence/phase-18/<date>). The exit
  code is 1 when any gate fails.
.NOTES
  A developer-machine run is local evidence only; T-11.6 still requires the same gates on a clean offline VM without
  developer tools (docs/deployment-guide.md, "Clean machine verification"). Message boxes of the exit-code gates are
  closed by the script (WM_CLOSE), which is what pressing OK does.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Zip,
    [string]$PreviousZip,
    [ValidateSet('All', 'Launch', 'Checksums', 'Relocation', 'SecondLaunch', 'ReadOnlyFolder', 'LockedDataset', 'NewerSchema', 'Upgrade', 'DependencyAbsence')]
    [string[]]$Gates = @('All'),
    [string]$SecondDrive,
    [string]$FixtureTool,
    [string]$EvidenceDir,
    [int]$TimeoutSeconds = 30,
    [switch]$KeepWork
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$Zip = (Resolve-Path $Zip).Path
if (-not $PreviousZip) { $PreviousZip = $Zip }
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $root ('docs/evidence/phase-18/' + (Get-Date -Format 'yyyy-MM-dd')) }
$all = 'Launch', 'Checksums', 'Relocation', 'SecondLaunch', 'ReadOnlyFolder', 'LockedDataset', 'NewerSchema', 'Upgrade', 'DependencyAbsence'
$selected = if ($Gates -contains 'All') { $all } else { $Gates }
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('Max Planner Tëst ' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

# ----- Helpers -----
function New-Extraction([string]$zipPath, [string]$label) {
    $target = Join-Path $workRoot "$label ünïcode"
    Expand-Archive -Path $zipPath -DestinationPath $target
    $app = Join-Path $target 'MaxPlanner'
    if (-not (Test-Path (Join-Path $app 'MaxPlanner.exe'))) { throw "MaxPlanner.exe not found after extraction: $app" }
    return $app
}

function Start-App([string]$app) {
    Start-Process -FilePath (Join-Path $app 'MaxPlanner.exe') -WorkingDirectory ([IO.Path]::GetTempPath()) -PassThru
}

function Wait-Window([System.Diagnostics.Process]$process) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { return $false }
    } until ($process.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    return $process.MainWindowHandle -ne 0
}

function Read-Log([string]$app) {
    $logs = Join-Path $app 'data/logs'
    if (-not (Test-Path $logs)) { return @() }
    Get-ChildItem $logs -Filter 'maxplanner-*.log' | Sort-Object Name | ForEach-Object { Get-Content $_.FullName }
}

function Wait-Log([string]$app, [string]$pattern, [System.Diagnostics.Process]$process) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Read-Log $app | Select-String -Pattern $pattern -Quiet) { return $true }
        if ($process -and $process.HasExited) { return $false }
        Start-Sleep -Milliseconds 250
    } until ((Get-Date) -gt $deadline)
    return $false
}

# Closes the main window (the app's window or its startup message box) and returns the exit code.
function Stop-App([System.Diagnostics.Process]$process) {
    if (-not $process.HasExited) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(30000)) { $process.Kill(); $process.WaitForExit(); return 'killed' }
    }
    return $process.ExitCode
}

# One full session: launch, wait until ready, close. Returns the exit code.
function Invoke-Session([string]$app) {
    $process = Start-App $app
    try {
        if (-not (Wait-Log $app 'Startup ready: usable view' $process)) { throw "No 'Startup ready' line in $app (exit $($process.ExitCode))." }
    }
    finally {
        $code = Stop-App $process
    }
    return $code
}

function Get-DataHashes([string]$app) {
    $data = Join-Path $app 'data'
    Get-ChildItem $data -Recurse -File | Where-Object { $_.FullName -notmatch '\\logs\\' -and $_.Name -ne '.lock' } |
        Sort-Object FullName | ForEach-Object { '{0} {1}' -f [IO.Path]::GetRelativePath($data, $_.FullName), (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
}

function Add-Result([string]$gate, [bool]$passed, [string]$details) {
    $results.Add([pscustomobject]@{ Gate = $gate; Passed = $passed; Details = $details })
}

function Invoke-FixtureTool([string[]]$arguments) {
    if ($FixtureTool) { & $FixtureTool @arguments }
    else { & dotnet run -c Release --project (Join-Path $root 'tools/MaxPlanner.FixtureGenerator/MaxPlanner.FixtureGenerator.csproj') -p:Platform=x64 -- @arguments }
    if ($LASTEXITCODE -ne 0) { throw "Fixture tool failed ($LASTEXITCODE)." }
}

function Get-Footprint {
    $appDataHits = foreach ($folder in @($env:LOCALAPPDATA, $env:APPDATA, (Join-Path $env:LOCALAPPDATA 'Packages'))) {
        if (Test-Path $folder) { Get-ChildItem $folder -Directory -ErrorAction SilentlyContinue | Where-Object Name -match 'MaxPlanner' | ForEach-Object FullName }
    }
    [pscustomobject]@{
        AppData   = @($appDataHits)
        Registry  = @(Get-ChildItem 'HKCU:\Software' -ErrorAction SilentlyContinue | Where-Object PSChildName -match 'MaxPlanner' | ForEach-Object Name)
        Packages  = @(Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'MaxPlanner' -or $_.Name -like 'Microsoft.WindowsAppRuntime*' } | ForEach-Object PackageFullName)
    }
}

# ----- Gates -----
$gateScripts = [ordered]@{
    Launch = {
        $app = New-Extraction $Zip 'launch'
        $process = Start-App $app
        try {
            $window = Wait-Window $process
            $responding = $window -and $process.Responding
            $ready = Wait-Log $app 'Startup ready: usable view' $process
            $lock = Test-Path (Join-Path $app 'data/.lock')
            $db = Test-Path (Join-Path $app 'data/data.db')
        }
        finally { $code = Stop-App $process }
        Add-Result 'Launch' ($window -and $responding -and $ready -and $lock -and $db -and $code -eq 0) "window=$window responding=$responding ready=$ready lock=$lock db=$db exit=$code"
    }
    Checksums = {
        $expected = ((Get-Content "$Zip.sha256" -ErrorAction Stop) -split '\s+')[0]
        $actual = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
        $app = New-Extraction $Zip 'checksums'
        $lines = Get-Content (Join-Path $app 'SHA256SUMS.txt')
        $listed = @{}
        $bad = @()
        foreach ($line in $lines) {
            $hash, $relative = $line -split '  ', 2
            $listed[$relative] = $true
            $file = Join-Path $app $relative
            if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { $bad += $relative }
        }
        $unlisted = Get-ChildItem $app -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($app, $_.FullName) } |
            Where-Object { $_ -ne 'SHA256SUMS.txt' -and -not $listed.ContainsKey($_) }
        $notices = (Test-Path (Join-Path $app 'THIRD-PARTY-NOTICES.txt')) -and (Test-Path (Join-Path $app 'README.txt'))
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
        try { $dataEntries = @($archive.Entries | Where-Object { $_.FullName -match '^MaxPlanner[\\/]data[\\/]' }).Count }
        finally { $archive.Dispose() }
        Add-Result 'Checksums' ($expected -eq $actual -and $bad.Count -eq 0 -and @($unlisted).Count -eq 0 -and $notices -and $dataEntries -eq 0) `
            "zip=$($expected -eq $actual) mismatched=$($bad.Count) unlisted=$(@($unlisted).Count) notices+readme=$notices dataEntries=$dataEntries"
    }
    Relocation = {
        $app = New-Extraction $Zip 'relocate-from'
        $first = Invoke-Session $app
        $before = Get-DataHashes $app
        $destinationParent = if ($SecondDrive) { Join-Path $SecondDrive ('Max Planner Moved ' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) } else { Join-Path $workRoot 'relocated ëlsewhere' }
        New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
        $moved = Join-Path $destinationParent 'MaxPlanner'
        # Move-Item cannot move a folder across volumes; copy then delete, as a user moving to a USB drive does.
        if ($SecondDrive) { Copy-Item $app $moved -Recurse; Remove-Item $app -Recurse -Force }
        else { Move-Item $app $moved }
        $after = Get-DataHashes $moved
        Remove-Item (Join-Path $moved 'data/logs') -Recurse -Force -ErrorAction SilentlyContinue
        $second = Invoke-Session $moved
        $log = Read-Log $moved
        $opened = [bool]($log | Select-String 'Dataset .* opened')
        $created = [bool]($log | Select-String 'First run: no dataset')
        $recovery = [bool]($log | Select-String 'Startup recovery:')
        $same = (Compare-Object $before $after) -eq $null
        $note = if ($SecondDrive) { "drive=$SecondDrive" } else { 'same drive (pass -SecondDrive to cross drives)' }
        Add-Result 'Relocation' ($first -eq 0 -and $second -eq 0 -and $opened -and -not $created -and -not $recovery -and $same -and -not (Test-Path $app)) `
            "dataUnchangedByMove=$same opened=$opened recreated=$created recovery=$recovery exits=$first/$second; $note"
        if ($SecondDrive -and -not $KeepWork) { Remove-Item $destinationParent -Recurse -Force -ErrorAction SilentlyContinue }
    }
    SecondLaunch = {
        $app = New-Extraction $Zip 'second-launch'
        $first = Start-App $app
        try {
            $ready = Wait-Log $app 'Startup ready: usable view' $first
            $second = Start-App $app
            $redirected = $second.WaitForExit(15000)
            $secondCode = if ($redirected) { $second.ExitCode } else { $second.Kill(); 'still running' }
            Start-Sleep -Seconds 1
            $exe = Join-Path $app 'MaxPlanner.exe'
            $instances = @(Get-Process MaxPlanner -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count
            $firstAlive = -not $first.HasExited
        }
        finally { $code = Stop-App $first }
        Add-Result 'SecondLaunch' ($ready -and $redirected -and $secondCode -eq 0 -and $instances -eq 1 -and $firstAlive -and $code -eq 0) `
            "secondExit=$secondCode instances=$instances firstAlive=$firstAlive firstExit=$code"
    }
    ReadOnlyFolder = {
        $app = New-Extraction $Zip 'read-only'
        $data = New-Item -ItemType Directory -Force -Path (Join-Path $app 'data')
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        & icacls $data.FullName /deny "${user}:(OI)(CI)(W)" | Out-Null
        try {
            $process = Start-App $app
            $message = Wait-Window $process
            $code = Stop-App $process
            $noDb = -not (Test-Path (Join-Path $data.FullName 'data.db'))
        }
        finally { & icacls $data.FullName /remove:d $user | Out-Null }
        Add-Result 'ReadOnlyFolder' ($message -and $code -eq 2 -and $noDb) "message=$message exit=$code noDatabase=$noDb"
    }
    LockedDataset = {
        $app = New-Extraction $Zip 'locked'
        $null = Invoke-Session $app
        $stream = [IO.File]::Open((Join-Path $app 'data/.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        try {
            $process = Start-App $app
            $message = Wait-Window $process
            $code = Stop-App $process
        }
        finally { $stream.Dispose() }
        Add-Result 'LockedDataset' ($message -and $code -eq 3) "message=$message exit=$code"
    }
    NewerSchema = {
        $app = New-Extraction $Zip 'newer-schema'
        $null = Invoke-Session $app
        Invoke-FixtureTool @('set-schema-version', $app, '9999')
        Remove-Item (Join-Path $app 'data/logs') -Recurse -Force -ErrorAction SilentlyContinue
        $process = Start-App $app
        try {
            $window = Wait-Window $process
            $recovery = Wait-Log $app 'Startup recovery: UnsupportedSchema' $process
        }
        finally { $code = Stop-App $process }
        Add-Result 'NewerSchema' ($window -and $recovery -and $code -eq 4) "window=$window recoveryLogged=$recovery exit=$code"
    }
    Upgrade = {
        $app = New-Extraction $PreviousZip 'upgrade'
        $first = Invoke-Session $app
        $before = Get-DataHashes $app
        Expand-Archive -Path $Zip -DestinationPath (Split-Path $app -Parent) -Force
        $after = Get-DataHashes $app
        $same = (Compare-Object $before $after) -eq $null
        Remove-Item (Join-Path $app 'data/logs') -Recurse -Force -ErrorAction SilentlyContinue
        $second = Invoke-Session $app
        $log = Read-Log $app
        $recovery = [bool]($log | Select-String 'Startup recovery:')
        $created = [bool]($log | Select-String 'First run: no dataset')
        Add-Result 'Upgrade' ($first -eq 0 -and $second -eq 0 -and $same -and -not $recovery -and -not $created) `
            "dataUnchangedByExtract=$same recovery=$recovery recreated=$created exits=$first/$second previous=$(Split-Path $PreviousZip -Leaf)"
    }
    DependencyAbsence = {
        $before = Get-Footprint
        $app = New-Extraction $Zip 'footprint'
        $process = Start-App $app
        try {
            $null = Wait-Log $app 'Startup ready: usable view' $process
            $modules = @($process.Modules | ForEach-Object FileName)
        }
        finally { $code = Stop-App $process }
        $after = Get-Footprint
        $newAppData = @($after.AppData | Where-Object { $_ -notin $before.AppData })
        $newRegistry = @($after.Registry | Where-Object { $_ -notin $before.Registry })
        $newPackages = @($after.Packages | Where-Object { $_ -notin $before.Packages })
        $outside = @(foreach ($dll in 'coreclr.dll', 'Microsoft.ui.xaml.dll', 'Microsoft.WindowsAppRuntime.dll', 'e_sqlite3.dll') {
                $hit = $modules | Where-Object { (Split-Path $_ -Leaf) -ieq $dll } | Select-Object -First 1
                if (-not $hit -or -not $hit.StartsWith($app, [StringComparison]::OrdinalIgnoreCase)) { "$dll=$hit" }
            })
        Add-Result 'DependencyAbsence' ($newAppData.Count -eq 0 -and $newRegistry.Count -eq 0 -and $newPackages.Count -eq 0 -and $outside.Count -eq 0 -and $code -eq 0) `
            "newAppData=$($newAppData -join ';') newRegistry=$($newRegistry -join ';') newPackages=$($newPackages -join ';') runtimeOutsideApp=$($outside -join ';') exit=$code"
    }
}

try {
    foreach ($gate in $selected) {
        Write-Host "Gate: $gate"
        try { & $gateScripts[$gate] }
        catch { Add-Result $gate $false "error: $($_.Exception.Message)" }
    }
}
finally {
    New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
    $report = [ordered]@{
        Date    = (Get-Date).ToString('o')
        Zip     = $Zip
        ZipHash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLowerInvariant()
        Machine = "$((Get-CimInstance Win32_OperatingSystem).Caption) build $([Environment]::OSVersion.Version.Build); user $([Environment]::UserName)"
        Results = $results
    }
    $report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $EvidenceDir 'verify-portable.json') -Encoding utf8
    $results | Format-Table -AutoSize -Wrap | Out-String | Write-Host
    if (-not $KeepWork) { Remove-Item $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    else { Write-Host "Work folder kept: $workRoot" }
}

if ($results | Where-Object { -not $_.Passed }) { exit 1 }
