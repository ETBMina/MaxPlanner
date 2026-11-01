#Requires -Version 7.0
<#
.SYNOPSIS
  Switches the data of the Debug (or Release) app between named dataset checkpoints.
.DESCRIPTION
  The portable app keeps all data in "data/" beside MaxPlanner.exe (ADR 0003). This script snapshots
  that data (data.db, data.db-wal, configuration.json, configuration.last-good.json) into named checkpoints
  under ".dev-data/checkpoints/" in the repo (ignored by git, survives rebuilds), and restores them later.
  Backups, logs and temp files are never part of a checkpoint.

  Commands:
    list            List saved checkpoints; "*" marks the one last saved or applied (data may have changed since).
    save <Name>     Snapshot the current data as <Name>. Refuses to overwrite unless -Force.
    use <Name>      Replace the current data with checkpoint <Name>.
    empty           Remove the database and configuration so the app creates a fresh dataset on next start.
    delete <Name>   Remove a checkpoint (asks to confirm unless -Force).

  "use" and "empty" first save the current data as checkpoint "_previous" (replacing the old one), so
  nothing is lost by accident; "use _previous" swaps back. save, use and empty refuse to run while the app
  is running: close it first.
.PARAMETER Command
  list, save, use, empty or delete.
.PARAMETER Name
  Checkpoint name (letters, digits, '-', '_', '.'; max 64). Names starting with '_' are reserved for the script.
.PARAMETER Configuration
  Debug (default) or Release. Selects src/MaxPlanner.Desktop/bin/x64/<Configuration>.
.PARAMETER DataDirectory
  Use this data folder instead of discovering it beside MaxPlanner.exe.
.PARAMETER CheckpointRoot
  Use this folder for checkpoint storage instead of <repo>/.dev-data/checkpoints.
.PARAMETER Force
  Overwrite an existing checkpoint on save; skip the confirmation on delete.
.EXAMPLE
  ./scripts/dev-data.ps1 save BigData
.EXAMPLE
  ./scripts/dev-data.ps1 empty
.EXAMPLE
  ./scripts/dev-data.ps1 use BigData
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('list', 'save', 'use', 'empty', 'delete')]
    [string]$Command,
    [Parameter(Position = 1)]
    [string]$Name,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$DataDirectory,
    [string]$CheckpointRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# What a checkpoint holds. The -shm file is a rebuildable index, so it is never saved, only removed on replace.
$checkpointFiles = @('data.db', 'data.db-wal', 'configuration.json', 'configuration.last-good.json')
$clearedFiles = $checkpointFiles + @('data.db-shm', 'restore.pending.json')
$metaFile = 'checkpoint.json'
$previousName = '_previous'
$reservedDeviceNames = @('CON', 'PRN', 'AUX', 'NUL') + (1..9 | ForEach-Object { "COM$_"; "LPT$_" })

if (-not $CheckpointRoot) { $CheckpointRoot = Join-Path $root '.dev-data/checkpoints' }
$CheckpointRoot = [IO.Path]::GetFullPath($CheckpointRoot)
$currentMarker = Join-Path $CheckpointRoot '.current'

function Resolve-DataDirectory {
    if ($DataDirectory) { return [IO.Path]::GetFullPath($DataDirectory) }
    $binRoot = Join-Path $root "src/MaxPlanner.Desktop/bin/x64/$Configuration"
    $exe = $null
    if (Test-Path -LiteralPath $binRoot) {
        $exe = Get-ChildItem -LiteralPath $binRoot -Filter 'MaxPlanner.exe' -Recurse -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $exe) {
        throw "MaxPlanner.exe not found under $binRoot. Build the $Configuration x64 app first, or pass -DataDirectory."
    }
    return Join-Path $exe.DirectoryName 'data'
}

function Assert-ValidName([string]$Value, [switch]$AllowReserved) {
    if ([string]::IsNullOrEmpty($Value)) { throw "A checkpoint name is required for '$Command'." }
    if ($AllowReserved -and $Value -ieq $previousName) { return }
    if ($Value.Length -gt 64 -or $Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' -or $Value.Contains('..') -or $Value.EndsWith('.')) {
        throw "Invalid checkpoint name '$Value'. Use letters, digits, '-', '_' and '.', start with a letter or digit, no spaces, max 64 characters. Names starting with '_' are reserved."
    }
    if ($reservedDeviceNames -contains $Value.Split('.')[0].ToUpperInvariant()) {
        throw "Invalid checkpoint name '$Value': it is a reserved Windows device name."
    }
}

# Returns the existing checkpoint directory for a name (case-insensitive), or $null.
function Find-Checkpoint([string]$Value) {
    if (-not (Test-Path -LiteralPath $CheckpointRoot)) { return $null }
    Get-ChildItem -LiteralPath $CheckpointRoot -Directory |
        Where-Object { $_.Name -ieq $Value -and -not $_.Name.StartsWith('.') } | Select-Object -First 1
}

function Get-FileSet([string]$Directory) {
    $checkpointFiles | Where-Object { Test-Path -LiteralPath (Join-Path $Directory $_) -PathType Leaf }
}

function Get-Running([string]$DataDir) {
    $appDir = Split-Path -Parent $DataDir
    foreach ($process in Get-Process -Name 'MaxPlanner' -ErrorAction SilentlyContinue) {
        try {
            if ($process.Path -and ((Split-Path -Parent $process.Path) -ieq $appDir)) { return $process }
        }
        catch { } # Path is unreadable for some elevated processes; the lock check still covers them.
    }
}

# The app owns <data>/.lock as an open FileShare.None handle for its whole lifetime (DatasetLock.cs).
# Taking the same lock proves the app is closed and keeps it from starting while the data is replaced.
function Enter-DataLock([string]$DataDir) {
    $process = Get-Running $DataDir
    if ($process) { throw "MaxPlanner is running (PID $($process.Id)). Close the app first." }
    try {
        return [IO.FileStream]::new((Join-Path $DataDir '.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    }
    catch {
        $inner = $_.Exception
        while ($inner.InnerException) { $inner = $inner.InnerException }
        if ($inner -is [IO.IOException] -and (($inner.HResult -band 0xFFFF) -in 32, 33)) {
            throw 'MaxPlanner is running (the dataset lock is held). Close the app first.'
        }
        throw
    }
}

function Remove-Quietly([string]$Path) {
    if ($Path -and (Test-Path -LiteralPath $Path)) { Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue }
}

function Set-Current([string]$Value) {
    if ($Value) {
        New-Item -ItemType Directory -Force -Path $CheckpointRoot | Out-Null
        Set-Content -LiteralPath $currentMarker -Value $Value -Encoding utf8NoBOM
    }
    else { Remove-Quietly $currentMarker }
}

function Get-Current {
    if (Test-Path -LiteralPath $currentMarker) { (Get-Content -LiteralPath $currentMarker -TotalCount 1).Trim() }
}

# Copies the data files into a temp sibling, then swaps it into place so a failure never leaves a half checkpoint.
function Save-Checkpoint([string]$DataDir, [string]$Value, [string]$Origin) {
    $files = @(Get-FileSet $DataDir)
    New-Item -ItemType Directory -Force -Path $CheckpointRoot | Out-Null
    $suffix = [guid]::NewGuid().ToString('N')
    $staging = Join-Path $CheckpointRoot ".tmp-$suffix"
    $displaced = Join-Path $CheckpointRoot ".old-$suffix"
    $target = Join-Path $CheckpointRoot $Value
    $existing = Find-Checkpoint $Value
    try {
        New-Item -ItemType Directory -Path $staging | Out-Null
        foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $DataDir $file) -Destination $staging }
        $meta = [ordered]@{ name = $Value; savedUtc = [DateTime]::UtcNow.ToString('o'); origin = $Origin }
        $meta | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging $metaFile) -Encoding utf8NoBOM

        if ($existing) { Move-Item -LiteralPath $existing.FullName -Destination $displaced }
        try { Move-Item -LiteralPath $staging -Destination $target }
        catch {
            if ($existing) { Move-Item -LiteralPath $displaced -Destination $existing.FullName }
            throw
        }
    }
    finally {
        Remove-Quietly $staging
        Remove-Quietly $displaced
    }
}

# Copies the checkpoint files next to their final names first, then removes the old data and renames,
# so a failed copy leaves the current data untouched.
function Install-Files([string]$SourceDir, [string]$DataDir) {
    $suffix = ".devdata-$([guid]::NewGuid().ToString('N'))"
    $staged = @()
    try {
        foreach ($file in Get-FileSet $SourceDir) {
            $temp = Join-Path $DataDir "$file$suffix"
            $staged += [pscustomobject]@{ Temp = $temp; Final = Join-Path $DataDir $file }
            Copy-Item -LiteralPath (Join-Path $SourceDir $file) -Destination $temp
        }
    }
    catch {
        foreach ($item in $staged) { Remove-Quietly $item.Temp }
        throw
    }
    Clear-Files $DataDir
    foreach ($item in $staged) { Move-Item -LiteralPath $item.Temp -Destination $item.Final }
}

# Removing db, wal and shm together means a stale WAL can never be paired with a different database.
function Clear-Files([string]$DataDir) {
    foreach ($file in $clearedFiles) {
        $path = Join-Path $DataDir $file
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } # not quiet: a stale file must fail loudly
    }
}

function Get-Size([string]$Directory) {
    (Get-ChildItem -LiteralPath $Directory -File | Measure-Object Length -Sum).Sum
}

function Format-Size([long]$Bytes) {
    if ($Bytes -ge 1MB) { '{0:N1} MB' -f ($Bytes / 1MB) } elseif ($Bytes -ge 1KB) { '{0:N0} KB' -f ($Bytes / 1KB) } else { "$Bytes B" }
}

function Show-List {
    $directories = @()
    if (Test-Path -LiteralPath $CheckpointRoot) {
        $directories = @(Get-ChildItem -LiteralPath $CheckpointRoot -Directory | Where-Object { -not $_.Name.StartsWith('.') } | Sort-Object Name)
    }
    if ($directories.Count -eq 0) {
        Write-Host "No checkpoints saved yet in $CheckpointRoot"
        Write-Host 'Save the current data with: ./scripts/dev-data.ps1 save <Name>'
        return
    }
    $current = Get-Current
    $rows = foreach ($directory in $directories) {
        $saved = $directory.LastWriteTime
        $metaPath = Join-Path $directory.FullName $metaFile
        if (Test-Path -LiteralPath $metaPath) {
            try { $saved = ([DateTime](Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json).savedUtc).ToLocalTime() } catch { }
        }
        [pscustomobject]@{
            Current = if ($current -and $directory.Name -ieq $current) { '*' } else { '' }
            Name    = $directory.Name
            Saved   = $saved.ToString('yyyy-MM-dd HH:mm:ss')
            Size    = Format-Size (Get-Size $directory.FullName)
        }
    }
    $rows | Format-Table -AutoSize | Out-Host
    Write-Host '* = last saved or applied (the data may have changed since).'
}

switch ($Command) {
    'list' { Show-List }

    'delete' {
        Assert-ValidName $Name -AllowReserved
        $checkpoint = Find-Checkpoint $Name
        if (-not $checkpoint) { throw "Checkpoint '$Name' does not exist. Run 'list' to see saved checkpoints." }
        if (-not $Force) {
            $answer = Read-Host "Delete checkpoint '$($checkpoint.Name)' ($(Format-Size (Get-Size $checkpoint.FullName)))? [y/N]"
            # "$answer": Read-Host returns $null on closed input, and $null -notmatch is falsy.
            if ("$answer" -notmatch '^(y|yes)$') { Write-Host 'Cancelled. (Use -Force to delete without asking.)'; return }
        }
        Remove-Item -LiteralPath $checkpoint.FullName -Recurse -Force
        if ((Get-Current) -ieq $checkpoint.Name) { Set-Current $null }
        Write-Host "Deleted checkpoint '$($checkpoint.Name)'."
    }

    'save' {
        Assert-ValidName $Name
        $dataDir = Resolve-DataDirectory
        if (-not (Test-Path -LiteralPath (Join-Path $dataDir 'data.db'))) { throw "No data.db in $dataDir; there is nothing to save." }
        $existing = Find-Checkpoint $Name
        if ($existing -and -not $Force) { throw "Checkpoint '$($existing.Name)' already exists. Use -Force to overwrite it." }
        $lock = Enter-DataLock $dataDir
        try { Save-Checkpoint $dataDir $Name $dataDir }
        finally { $lock.Dispose() }
        Set-Current $Name
        $saved = Find-Checkpoint $Name
        Write-Host "Saved checkpoint '$Name' ($(Format-Size (Get-Size $saved.FullName))) from $dataDir"
    }

    'use' {
        Assert-ValidName $Name -AllowReserved
        $checkpoint = Find-Checkpoint $Name
        if (-not $checkpoint) { throw "Checkpoint '$Name' does not exist. Run 'list' to see saved checkpoints." }
        if (-not (Get-FileSet $checkpoint.FullName)) { throw "Checkpoint '$($checkpoint.Name)' contains no data files." }
        $dataDir = Resolve-DataDirectory
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        $lock = Enter-DataLock $dataDir
        $stage = $null
        try {
            $source = $checkpoint.FullName
            if ($checkpoint.Name -ieq $previousName) {
                # Saving the current data replaces _previous, so read from a private copy of it.
                $stage = Join-Path $CheckpointRoot ".tmp-use-$([guid]::NewGuid().ToString('N'))"
                Copy-Item -LiteralPath $source -Destination $stage -Recurse
                $source = $stage
            }
            $hasCurrent = [bool](Get-FileSet $dataDir)
            if ($hasCurrent) { Save-Checkpoint $dataDir $previousName $dataDir }
            Install-Files $source $dataDir
        }
        finally {
            $lock.Dispose()
            Remove-Quietly $stage
        }
        Set-Current $checkpoint.Name
        Write-Host "Now using checkpoint '$($checkpoint.Name)' in $dataDir"
        if ($hasCurrent) { Write-Host "The previous data was saved as '$previousName' ('use $previousName' swaps back)." }
    }

    'empty' {
        $dataDir = Resolve-DataDirectory
        if (-not (Test-Path -LiteralPath $dataDir)) {
            Write-Host "No data directory at $dataDir; the app will create a fresh dataset on first start."
            return
        }
        $lock = Enter-DataLock $dataDir
        try {
            $hasCurrent = [bool](Get-FileSet $dataDir)
            if ($hasCurrent) { Save-Checkpoint $dataDir $previousName $dataDir }
            Clear-Files $dataDir
        }
        finally { $lock.Dispose() }
        Set-Current $null
        Write-Host "Data reset in $dataDir; the app creates a fresh dataset on next start."
        if ($hasCurrent) { Write-Host "The previous data was saved as '$previousName'." }
    }
}
