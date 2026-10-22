# Spike 5.6: pipeline disposal. Runs against a PlatformControls window on the Projection page.
# The mutator keeps updating the store throughout, so any surviving subscription would show up as UI changes.
# Usage: pwsh -File Spike5-Dispose.ps1 -Hwnd <hwnd> -AppPid <pid> -OutDir <folder> [-Cycles 10]
param(
    [Parameter(Mandatory)][string]$Hwnd,
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$OutDir,
    [int]$Cycles = 10
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$log = Join-Path $OutDir '24-spike5-dispose-log.txt'
"Spike 5.6 disposal run $(Get-Date -Format o) hwnd=$Hwnd pid=$AppPid" | Set-Content $log

function Ui {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp ui @Arguments -w $Hwnd 2>&1 | ForEach-Object { "$_" })
    if ($global:LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output
}

function Text { param([string]$Id) ((Ui @('get-value', $Id, '--json')) -join "`n" | ConvertFrom-Json).text }
function Note { param([string]$Message) $line = "$(Get-Date -Format 'HH:mm:ss.fff') $Message"; Add-Content $log $line; $line }
function Store { Text 'Shell_StoreStatusText' }
function WorkingSetMb { [math]::Round((Get-Process -Id $AppPid).WorkingSet64 / 1MB, 1) }

$row = 'Projection_TitleText_00000005000000000000000000000000'
Note "initial pipeline: $(Text 'Projection_PipelineText')"
Note "initial store:    $(Store)"

Ui @('invoke', 'Projection_StartOtherRowsButton', '--action', 'invoke') | Out-Null
Start-Sleep -Seconds 2
Note "running:  changes='$(Text 'Projection_ChangesText')' row5='$(Text $row)'"

Ui @('invoke', 'Projection_DisposeButton', '--action', 'invoke') | Out-Null
Start-Sleep -Milliseconds 300
$changesAtDispose = Text 'Projection_ChangesText'
$rowAtDispose = Text $row
Note "disposed: pipeline='$(Text 'Projection_PipelineText')'"
Note "disposed: store='$(Store)'"
Start-Sleep -Seconds 3
$changesLater = Text 'Projection_ChangesText'
$rowLater = Text $row
Note "3 s after dispose (mutator still running): changes='$changesLater' row5='$rowLater' store='$(Store)'"
Note ("UI frozen after dispose: changes unchanged=" + ($changesAtDispose -eq $changesLater) + " row5 unchanged=" + ($rowAtDispose -eq $rowLater))

Ui @('invoke', 'Projection_RebuildButton', '--action', 'invoke') | Out-Null
Start-Sleep -Seconds 1
Note "rebuilt: pipeline='$(Text 'Projection_PipelineText')'"

$wsBefore = WorkingSetMb
for ($i = 1; $i -le $Cycles; $i++) {
    Ui @('invoke', 'Shell_NavBlankItem', '--action', 'select') | Out-Null
    Start-Sleep -Milliseconds 1500
    $away = Store
    Ui @('invoke', 'Shell_NavProjectionItem', '--action', 'select') | Out-Null
    Start-Sleep -Milliseconds 1500
    $back = Store
    Note "cycle $i away: $away"
    Note "cycle $i back: $back"
}
[GC]::Collect()
$wsAfter = WorkingSetMb
Note "working set before cycles=$wsBefore MB after $Cycles cycles=$wsAfter MB (indicative only; no forced GC in the app)"
Ui @('invoke', 'Projection_StopButton', '--action', 'invoke') | Out-Null
Note "final store: $(Store)"
