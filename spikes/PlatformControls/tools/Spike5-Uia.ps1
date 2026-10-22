# Spike 5 UI Automation scenario against a running PlatformControls window showing the Projection page.
# Non-intrusive input only: UIA select/invoke/toggle/focus/set-value/scroll-into-view, get-*, screenshots, and
# post-message keys to the spike's own HWND. The in-app log (spike-diagnostics.log) samples every mutator tick.
# Variants: A = plain (Update, Move), B = Update->Refresh, C = A with moves as Remove+Insert, D = B with moves as Remove+Insert,
# R = A plus the in-app selection restore after each applied change set.
# Usage: pwsh -File Spike5-Uia.ps1 -Hwnd <hwnd> -OutDir <folder> -Variant A|B|C|D [-Target 30]
param(
    [Parameter(Mandatory)][string]$Hwnd,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][ValidateSet('A', 'B', 'C', 'D', 'R')][string]$Variant,
    [int]$Target = 30
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$log = Join-Path $OutDir "20-spike5-variant$Variant-uia-log.txt"
"Spike 5 UIA run variant=$Variant target=$Target $(Get-Date -Format o) hwnd=$Hwnd" | Set-Content $log
$key = '{0:x8}000000000000000000000000' -f $Target
$draftId = "Projection_DraftBox_$key"
$titleId = "Projection_TitleText_$key"
$rowId = "Projection_Row_$key"

function Ui {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp ui @Arguments -w $Hwnd 2>&1 | ForEach-Object { "$_" })
    Add-Content $log "> winapp ui $($Arguments -join ' ')   (exit $global:LASTEXITCODE)`n$($output -join "`n")"
    if ($global:LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output
}

function Json { param([string[]]$Arguments) (Ui ($Arguments + '--json')) -join "`n" | ConvertFrom-Json }
function Text { param([string]$Id) try { (Json @('get-value', $Id)).text } catch { '<not in UIA tree>' } }

function Focus-Draft {
    # Prefer UIA focus (needs the spike window in the foreground); otherwise use the in-app harness button.
    try { Ui @('focus', $draftId) | Out-Null; 'uia-focus' }
    catch {
        Ui @('set-value', 'Projection_FocusTargetBox', "$Target") | Out-Null
        Ui @('invoke', 'Projection_FocusTargetButton', '--action', 'invoke') | Out-Null
        Start-Sleep -Milliseconds 300
        'in-app-harness (UIA focus refused: window not foreground)'
    }
}
function Snap {
    param([string]$Label)
    $focused = Json @('get-focused')
    $state = [ordered]@{
        label        = $Label
        time         = (Get-Date -Format 'HH:mm:ss.fff')
        focusedId    = $focused.element.automationId
        focusedSlug  = $focused.element.selector
        focusedValue = $focused.element.value
        draft        = Text $draftId
        title        = Text $titleId
        rowSelected  = $(try { (Json @('get-property', $rowId, '-p', 'IsSelected')).properties.IsSelected } catch { '<not in UIA tree>' })
        scrollPct    = (Json @('get-property', 'Projection_List', '-p', 'ScrollVerticalPercent')).properties.ScrollVerticalPercent
        scrollText   = Text 'Projection_ScrollText'
        focusText    = Text 'Projection_FocusText'
        selection    = Text 'Projection_SelectionText'
        changes      = Text 'Projection_ChangesText'
    }
    Add-Content $log ("SNAP " + ($state | ConvertTo-Json -Compress))
    [pscustomobject]$state
}

# 0. Pipeline variant toggles (each toggle rebuilds the pipeline).
function Set-Toggle([string]$Id, [bool]$On) {
    $state = (Json @('get-property', $Id, '-p', 'ToggleState')).properties.ToggleState
    if (($state -eq 'On') -ne $On) { Ui @('invoke', $Id, '--action', $(if ($On) { 'toggle-on' } else { 'toggle-off' })) | Out-Null; Start-Sleep 2 }
}
Set-Toggle 'Projection_UpdatesAsRefreshToggle' ($Variant -in 'B', 'D')
Set-Toggle 'Projection_MovesAsRemoveInsertToggle' ($Variant -in 'C', 'D')
Set-Toggle 'Projection_RestoreSelectionToggle' ($Variant -eq 'R')
$pipeline = Text 'Projection_PipelineText'

# 1. Scroll to a non-zero offset and select the target row.
Ui @('scroll-into-view', ("Projection_Row_{0:x8}000000000000000000000000" -f ($Target + 8))) | Out-Null
Start-Sleep -Milliseconds 500
Ui @('invoke', $rowId, '--action', 'select') | Out-Null

# 2. Start the other-rows mutator first: UIA Invoke on a WinUI Button moves keyboard focus to that button
#    (seen in the first run), so the row is focused only after the mutator is running.
Ui @('invoke', 'Projection_StartOtherRowsButton', '--action', 'invoke') | Out-Null
$focusPath = Focus-Draft
Ui @('set-value', $draftId, "user draft $Target") | Out-Null
Ui @('send-keys', 'abc') | Out-Null
Start-Sleep -Milliseconds 300
$before = Snap 'before'
$samples = @()
for ($i = 1; $i -le 10; $i++) { Start-Sleep -Milliseconds 400; $samples += Snap "other-rows-$i" }
$after = Snap 'after-other-rows'
Ui @('screenshot', '-o', (Join-Path $OutDir "21-variant$Variant-after-other-rows.png")) | Out-Null
$latency = Text 'Projection_LatencyText'
Ui @('invoke', 'Projection_StopButton', '--action', 'invoke') | Out-Null
Start-Sleep -Milliseconds 400
$store = Text 'Shell_StoreStatusText'

# 3. Re-sort the focused row itself (mode ticks 15, 25, 35: down 3 rows, up 3 rows, to the end of the list).
Ui @('invoke', 'Projection_StartMoveFocusedButton', '--action', 'invoke') | Out-Null
$focusPath2 = Focus-Draft
$beforeMove = Snap 'before-move-focused'
$moves = @()
for ($i = 1; $i -le 9; $i++) { Start-Sleep -Milliseconds 500; $moves += Snap "move-focused-$i" }
$afterMove = Snap 'after-move-focused'
Ui @('screenshot', '-o', (Join-Path $OutDir "22-variant$Variant-after-move-focused.png")) | Out-Null
Ui @('invoke', 'Projection_StopButton', '--action', 'invoke') | Out-Null
$all = @($before) + $samples + @($after)
$summary = @(
    "pipeline: $pipeline", "focus setup: other-rows=$focusPath move-focused=$focusPath2",
    "store: $store",
    "latency: $latency",
    "OTHER-ROWS: focus ids seen: " + (($all | ForEach-Object { $_.focusedId } | Select-Object -Unique) -join ', '),
    "OTHER-ROWS: focus slugs seen: " + (($all | ForEach-Object { $_.focusedSlug } | Select-Object -Unique) -join ', '),
    "OTHER-ROWS: drafts seen: " + (($all | ForEach-Object { $_.draft } | Select-Object -Unique) -join ' | '),
    "OTHER-ROWS: row IsSelected seen: " + (($all | ForEach-Object { $_.rowSelected } | Select-Object -Unique) -join ', '),
    "OTHER-ROWS: ScrollVerticalPercent seen: " + (($all | ForEach-Object { $_.scrollPct } | Select-Object -Unique) -join ', '),
    "OTHER-ROWS: scroll text seen: " + (($all | ForEach-Object { ($_.scrollText -replace 'ViewportHeight=\S+ ', '') } | Select-Object -Unique) -join ' || '),
    "OTHER-ROWS: committed titles seen: " + (($all | ForEach-Object { $_.title } | Select-Object -Unique) -join ' | '),
    "OTHER-ROWS: before focusText: $($before.focusText)",
    "OTHER-ROWS: after  focusText: $($after.focusText)",
    "OTHER-ROWS: after  changes:   $($after.changes)",
    "MOVE-FOCUSED: before focusText: $($beforeMove.focusText)"
) + ($moves | ForEach-Object { "MOVE-FOCUSED: $($_.label) focused=$($_.focusedId) draft='$($_.draft)' | $($_.focusText) | $($_.scrollText)" }) + @(
    "MOVE-FOCUSED: after focused=$($afterMove.focusedId) | $($afterMove.focusText) | $($afterMove.changes)"
)
Add-Content $log "`n==== SUMMARY`n$($summary -join "`n")"
$summary
