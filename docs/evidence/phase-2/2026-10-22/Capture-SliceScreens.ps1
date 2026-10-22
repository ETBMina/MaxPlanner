#Requires -Version 7.0
<#
.SYNOPSIS
  Phase 2 visual evidence: screenshots of the slice pages and Grid states in a restored window (never
  maximized or full screen, which captures black), Light and Dark, at 500/640/1008/1600 epx, plus the
  breakpoint and minimum-size measurements. UI Automation on the app's own process only; window sizes are
  set on the app's own window with SetWindowPos from a per-monitor-DPI-aware thread (physical pixels).
#>
[CmdletBinding()]
param([string]$OutDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$here = $PSScriptRoot
$notes = [Collections.Generic.List[string]]::new()

Add-Type -Namespace Native -Name Shots -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@
# Per-monitor DPI aware V2: without it this process sees system-DPI-virtualized sizes.
[Native.Shots]::SetThreadDpiAwarenessContext([IntPtr]-4) | Out-Null

function Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp ui @Arguments -w $script:hwnd 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output -join "`n"
}

function UiJson { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) (Ui @Arguments '--json') | ConvertFrom-Json }

function Shot([string]$Name) {
    Start-Sleep -Milliseconds 600
    $path = Join-Path $OutDirectory $Name
    Remove-Item $path -ErrorAction SilentlyContinue
    Ui screenshot --output $path | Out-Null
    $notes.Add("$Name")
}

function RowId([string]$RowName) {
    $match = (UiJson search $RowName).matches | Where-Object { $_.automationId -like 'Grid_Row_*' -and $_.name -like "$RowName*" } | Select-Object -First 1
    $match.automationId.Substring('Grid_Row_'.Length)
}

function Size([int]$WidthEpx, [int]$HeightEpx) {
    $handle = [IntPtr][long]$script:hwnd
    $scale = [Native.Shots]::GetDpiForWindow($handle) / 96.0
    [Native.Shots]::SetWindowPos($handle, [IntPtr]::Zero, 40, 40, [int]($WidthEpx * $scale), [int]($HeightEpx * $scale), 0x0014) | Out-Null
    Start-Sleep -Milliseconds 1200
    $rect = [Native.Shots+RECT]::new()
    [Native.Shots]::GetWindowRect($handle, [ref]$rect) | Out-Null
    [pscustomobject]@{ Scale = $scale; WidthPx = $rect.Right - $rect.Left; HeightPx = $rect.Bottom - $rect.Top }
}

function Launch([string]$Theme, [string]$Root) {
    $app = & (Join-Path $here 'Start-SliceApp.ps1') -Root $Root -DebugHooks -Theme $Theme -ActivePage grid | Select-Object -Last 1
    $script:appPid = $app.ProcessId
    $script:hwnd = $app.Hwnd
    Ui wait-for Grid_Table -t 15000 | Out-Null
}

function Close-App {
    $process = Get-Process -Id $script:appPid -ErrorAction SilentlyContinue
    if ($process) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(30000)) { $process.Kill() }
    }
}

$base = Join-Path ([IO.Path]::GetTempPath()) ('MaxPlanner Tëst UI\shots-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
try {
    # Dark run: pages, Grid states, live updates, breakpoints, minimum size.
    $root = Join-Path $base 'dark'
    Launch 'Dark' $root
    $triggers = Join-Path $root 'data/debug'
    New-Item -ItemType Directory -Force -Path $triggers | Out-Null
    Size 1200 820 | Out-Null
    $onboarding = RowId 'Task Update onboarding copy'
    $certificate = RowId 'Task Renew the TLS certificate'

    Shot '01-grid-populated-dark.png'

    Ui invoke "Grid_Cell_${onboarding}_Title" | Out-Null
    Ui wait-for Grid_CellEditor -t 3000 | Out-Null
    Ui set-value Grid_CellEditor 'Update onboarding copy v2' | Out-Null
    Shot '02-grid-editing-dark.png'

    Set-Content (Join-Path $triggers 'delay-next-commit') '6000'
    Ui focus "Grid_Cell_${onboarding}_Kind" | Out-Null
    Ui wait-for Shell_SaveStatusText --value 'Saving…' -t 3000 | Out-Null
    Shot '03-grid-saving-dark.png'
    Ui wait-for "Grid_Cell_${onboarding}_Title" --value 'Update onboarding copy v2' -t 10000 | Out-Null
    Ui wait-for Grid_CellEditor --gone -t 5000 | Out-Null

    Set-Content (Join-Path $triggers 'fail-next-commit') 'DiskFull'
    Ui set-value "Grid_Cell_${certificate}_Title" 'Renew and install the TLS certificate' | Out-Null
    Ui wait-for Grid_CellError --value 'drive is full' --contains -t 5000 | Out-Null
    Shot '04-grid-error-disk-full-dark.png'
    Ui set-value "Grid_Cell_${certificate}_Title" 'Renew and install the TLS certificate' | Out-Null
    Ui wait-for Grid_CellEditor --gone -t 5000 | Out-Null

    # Genuine stale revision: the Grid commit is held while the Checklists page commits to the same task.
    Ui invoke "Grid_Cell_${onboarding}_Title" | Out-Null
    Ui wait-for Grid_CellEditor --value 'Update onboarding copy v2' -t 3000 | Out-Null
    Ui set-value Grid_CellEditor 'Onboarding copy, final' | Out-Null
    Set-Content (Join-Path $triggers 'delay-next-commit') '6000'
    Ui focus "Grid_Cell_${onboarding}_Kind" | Out-Null
    Ui invoke Shell_NavChecklistsItem | Out-Null
    Ui set-value "Checklists_AddItemBox_$onboarding" 'Check the German text' | Out-Null
    Ui invoke "Checklists_AddItemButton_$onboarding" | Out-Null
    Ui wait-for "Checklists_Progress_$onboarding" --value '1 of 4 done' -t 5000 | Out-Null
    Ui invoke Shell_NavGridItem | Out-Null
    Ui wait-for Grid_ConflictSavedText -t 10000 | Out-Null
    Shot '05-grid-conflict-dark.png'
    Ui invoke Conflict_UseMineButton | Out-Null
    Ui wait-for "Grid_Cell_${onboarding}_Title" --value 'Onboarding copy, final' -t 5000 | Out-Null

    Ui set-value "Grid_Cell_${onboarding}_Status" 'In Progress' | Out-Null
    Ui wait-for "Grid_Cell_${onboarding}_Status" --value 'In Progress' -t 5000 | Out-Null
    Ui invoke Shell_NavBoardItem | Out-Null
    Ui wait-for "Board_TaskCard_$onboarding" -t 5000 | Out-Null
    Shot '06-board-after-live-status-move-dark.png'
    Ui invoke Shell_NavChecklistsItem | Out-Null
    Ui wait-for "Checklists_GroupTitle_$onboarding" --value 'Onboarding copy, final' -t 5000 | Out-Null
    Shot '07-checklists-after-live-title-change-dark.png'
    Ui invoke Shell_NavDashboardItem | Out-Null
    Ui wait-for Placeholder_Title -t 5000 | Out-Null
    Shot '08-placeholder-dashboard-dark.png'

    # At S the NavigationView is LeftMinimal and its items leave the UIA tree while the pane is closed, so
    # each page is opened at a large width first and then resized through the breakpoints.
    foreach ($page in 'Grid', 'Board', 'Checklists') {
        Size 1200 820 | Out-Null
        Ui invoke "Shell_Nav${page}Item" | Out-Null
        foreach ($width in 500, 640, 1008, 1600) {
            $size = Size $width 900
            Shot ("10-{0}-{1}epx-dark.png" -f $page.ToLowerInvariant(), $width)
            if ($page -eq 'Grid') {
                # In LeftMinimal the closed pane is not in the UIA tree; a missing PaneRoot is reported as 0.
                $pane = try { (UiJson search 'PaneRoot').matches | Select-Object -First 1 } catch { $null }
                $paneWidth = if ($pane) { [int]($pane.width / $size.Scale) } else { 0 }
                $content = (UiJson search 'PageHost').matches | Select-Object -First 1
                $notes.Add("width $width epx: window $($size.WidthPx)x$($size.HeightPx) px (scale $($size.Scale)); nav pane $paneWidth epx; page host x=$($content.x) w=$([int]($content.width / $size.Scale)) epx")
            }
        }
    }

    $minimum = Size 200 200
    $notes.Add("minimum: requested 300x300 px, window kept at $($minimum.WidthPx)x$($minimum.HeightPx) px (scale $($minimum.Scale)); PreferredMinimumWidth/Height set to $([int][math]::Round(500 * $minimum.Scale))")
    Close-App

    # Light run: the three pages at the large breakpoint.
    Launch 'Light' (Join-Path $base 'light')
    Size 1200 820 | Out-Null
    foreach ($page in 'Grid', 'Board', 'Checklists') {
        Ui invoke "Shell_Nav${page}Item" | Out-Null
        Shot ("09-{0}-light.png" -f $page.ToLowerInvariant())
    }
} finally {
    Close-App
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
    $notes | Set-Content (Join-Path $OutDirectory 'screens-notes.txt') -Encoding utf8
}
$notes
