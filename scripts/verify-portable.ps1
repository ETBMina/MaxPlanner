#Requires -Version 7.0
<#
.SYNOPSIS
  Extracts a portable ZIP to a path with spaces and non-ASCII characters, launches it from an
  unrelated working directory, and checks that a responsive top-level window appears and that
  data/ (with its lock file) is created beside the EXE (T-03, AC-01).
.NOTES
  A developer-machine run is local evidence only; T-11.6 still requires a clean offline VM.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Zip,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$target = Join-Path ([IO.Path]::GetTempPath()) ("Max Planner Tëst " + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Expand-Archive -Path $Zip -DestinationPath $target
$exe = Join-Path $target 'MaxPlanner/MaxPlanner.exe'
if (-not (Test-Path $exe)) { throw "MaxPlanner.exe not found after extraction: $exe" }

$process = Start-Process -FilePath $exe -WorkingDirectory ([IO.Path]::GetTempPath()) -PassThru
try {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw "MaxPlanner exited early with code $($process.ExitCode)." }
    } until ($process.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)

    if ($process.MainWindowHandle -eq 0) { throw "No top-level window within $TimeoutSeconds s." }
    if (-not $process.Responding) { throw 'The window is not responding.' }
    $data = Join-Path $target 'MaxPlanner/data'
    if (-not (Test-Path (Join-Path $data '.lock'))) { throw "data/.lock was not created beside the EXE: $data" }

    [pscustomobject]@{
        ExtractedTo = $target
        ProcessId   = $process.Id
        WindowTitle = $process.MainWindowTitle
        Responding  = $process.Responding
    }
}
finally {
    if (-not $process.HasExited) { $process.CloseMainWindow() | Out-Null; $process.WaitForExit(10000) | Out-Null }
    if (-not $process.HasExited) { $process.Kill() }
}
