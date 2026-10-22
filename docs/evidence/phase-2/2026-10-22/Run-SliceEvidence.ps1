#Requires -Version 7.0
<#
.SYNOPSIS
  Phase 2 vertical-slice UI evidence on the real app (Debug build with MAXPLANNER_DEBUG_HOOKS=1), driven only
  through UI Automation on the app's own process (winapp ui): no keyboard, mouse, or touch input.
.DESCRIPTION
  Grid title/status edits through the cells' Value pattern, live Board and Checklists updates, a storage
  failure (debug hook), a genuine stale-revision conflict (debug delay + a Checklists commit in between),
  Use mine, a genuine relaunch on the same data folder, programmatic window resizes of the app's own window
  (breakpoints and the minimum-size units), and 100 navigation cycles with subscription and working-set
  numbers from the app log. Results: slice-results.json beside this script.
#>
[CmdletBinding()]
param([string]$Root = (Join-Path ([IO.Path]::GetTempPath()) ("MaxPlanner Tëst UI\evidence-" + [guid]::NewGuid().ToString('N').Substring(0, 6))))

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$here = $PSScriptRoot
$results = [Collections.Generic.List[object]]::new()
$env:WINAPP_UI_WORKFLOW_ID = [guid]::NewGuid().ToString('N')

Add-Type -Namespace Native -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
[StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
'@

function Ui {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp ui @Arguments -a $script:appPid 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "winapp ui $($Arguments -join ' ') failed: $($output -join ' ')" }
    $output -join "`n"
}

function UiJson { param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments) (Ui @Arguments '--json') | ConvertFrom-Json }

function Step {
    param([string]$Name, [scriptblock]$Script)
    try {
        $detail = & $Script
        $results.Add([ordered]@{ name = $Name; status = 'PASS'; detail = "$detail" })
    } catch {
        $results.Add([ordered]@{ name = $Name; status = 'FAIL'; detail = "$_" })
    }
}

function Value([string]$Selector) { (UiJson get-value $Selector).text }

function RowId([string]$RowName) {
    $match = (UiJson search $RowName).matches | Where-Object { $_.automationId -like 'Grid_Row_*' -and $_.name -like "$RowName*" } | Select-Object -First 1
    if (-not $match) { throw "No Grid row named '$RowName'." }
    $match.automationId.Substring('Grid_Row_'.Length)
}

function ColumnListId([string]$StatusName) {
    $match = (UiJson search "$StatusName column").matches | Where-Object { $_.automationId -like 'Board_ColumnList_*' -and $_.name -like "$StatusName column*" } | Select-Object -First 1
    if (-not $match) { throw "No Board column '$StatusName'." }
    $match.automationId
}

function LogLines([string]$Pattern) {
    $text = foreach ($log in Get-ChildItem (Join-Path $Root 'data/logs') -Filter '*.log') {
        $stream = [IO.FileStream]::new($log.FullName, 'Open', 'Read', 'ReadWrite,Delete')
        try { [IO.StreamReader]::new($stream).ReadToEnd() } finally { $stream.Dispose() }
    }
    @(($text -join "`n") -split "`n" | Where-Object { $_ -match $Pattern })
}

function Resize([int]$WidthEpx, [int]$HeightEpx) {
    $handle = [IntPtr][long]$script:hwnd
    $scale = [Native.Win]::GetDpiForWindow($handle) / 96.0
    # SWP_NOZORDER | SWP_NOACTIVATE: only this app's own window changes size.
    [Native.Win]::SetWindowPos($handle, [IntPtr]::Zero, 60, 60, [int]($WidthEpx * $scale), [int]($HeightEpx * $scale), 0x0014) | Out-Null
    Start-Sleep -Milliseconds 700
    $rect = [Native.Win+RECT]::new()
    [Native.Win]::GetWindowRect($handle, [ref]$rect) | Out-Null
    [pscustomobject]@{ Scale = $scale; WidthPx = $rect.Right - $rect.Left; HeightPx = $rect.Bottom - $rect.Top }
}

function Close-App {
    $process = Get-Process -Id $script:appPid -ErrorAction SilentlyContinue
    if ($process) {
        $process.CloseMainWindow() | Out-Null
        if (-not $process.WaitForExit(30000)) { $process.Kill() }
    }
}

function Launch {
    $app = & (Join-Path $here 'Start-SliceApp.ps1') -Root $Root -DebugHooks -ActivePage grid | Select-Object -Last 1
    $script:appPid = $app.ProcessId
    $script:hwnd = $app.Hwnd
    Ui wait-for Shell_Nav -t 15000 | Out-Null
}

New-Item -ItemType Directory -Force -Path $Root | Out-Null
$triggers = Join-Path $Root 'data/debug'

try {
    Launch
    $onboarding = RowId 'Task Update onboarding copy'
    $certificate = RowId 'Task Renew the TLS certificate'
    $linked = RowId 'Task Card limits API'

    Step 'Grid: rows for tasks and Subtask cards, linked title locked' {
        $rows = (UiJson search 'Grid_Row_').matches | Where-Object { $_.automationId -like 'Grid_Row_*' }
        if ($rows.Count -ne 8) { throw "Expected 8 rows (5 tasks + 3 Subtask cards), found $($rows.Count)." }
        $cell = (UiJson search "Grid_Cell_${linked}_Title").matches | Select-Object -First 1
        if ($cell.isInvokable) { throw 'The linked task title cell is editable.' }
        (Ui search 'Inherited from CR-1067') | Out-Null
        "8 rows; linked title read-only; status summary '$(Value Shell_StatusSummary)'"
    }

    Step 'Grid: title edit commits through the cell and the save status reports Saved' {
        Ui set-value "Grid_Cell_${onboarding}_Title" 'Update onboarding copy v2' | Out-Null
        Ui wait-for "Grid_Cell_${onboarding}_Title" --value 'Update onboarding copy v2' -t 5000 | Out-Null
        Ui wait-for Shell_SaveStatusText --value 'Saved' -t 3000 | Out-Null
        'title saved; status bar: Saved'
    }

    Step 'Grid: status edit to In Progress sets the start date (B-04)' {
        Ui set-value "Grid_Cell_${onboarding}_Status" 'In Progress' | Out-Null
        Ui wait-for "Grid_Cell_${onboarding}_Status" --value 'In Progress' -t 5000 | Out-Null
        $start = Value "Grid_Cell_${onboarding}_StartDate"
        if (-not $start) { throw 'StartDate stayed empty.' }
        "start date $start"
    }

    Step 'Board: the card is in In Progress with the new title, without reload' {
        Ui invoke Shell_NavBoardItem | Out-Null
        $list = ColumnListId 'In Progress'
        Ui wait-for "Board_TaskCard_$onboarding" --root $list -t 5000 | Out-Null
        $card = (UiJson search "Board_TaskCard_$onboarding").matches | Select-Object -First 1
        if ($card.name -notlike 'Update onboarding copy v2,*') { throw "Card name: $($card.name)" }
        $backlog = ColumnListId 'Backlog'
        Ui wait-for "Board_TaskCard_$onboarding" --root $backlog --gone -t 3000 | Out-Null
        "$($card.name) | $(Value Shell_StatusSummary)"
    }

    Step 'Checklists: the group header shows the new title' {
        Ui invoke Shell_NavChecklistsItem | Out-Null
        Ui wait-for "Checklists_GroupTitle_$onboarding" --value 'Update onboarding copy v2' -t 5000 | Out-Null
        Value "Checklists_Progress_$onboarding"
    }

    Step 'Checklists: Add an item through the box and button; blank shows an inline error' {
        Ui set-value "Checklists_AddItemBox_$certificate" '   ' | Out-Null
        Ui invoke "Checklists_AddItemButton_$certificate" | Out-Null
        Ui wait-for "Checklists_AddItemError_$certificate" -t 3000 | Out-Null
        $blank = Value "Checklists_AddItemError_$certificate"
        Ui set-value "Checklists_AddItemBox_$certificate" 'Install on the gateway' | Out-Null
        Ui invoke "Checklists_AddItemButton_$certificate" | Out-Null
        Ui wait-for "Checklists_Progress_$certificate" --value '0 of 2 done' -t 5000 | Out-Null
        "blank: '$blank'; after add: 0 of 2 done"
    }

    Step 'Checklists: checking an item updates progress (AC-06)' {
        $item = ((UiJson search 'Request the certificate').matches | Where-Object { $_.automationId -like 'Checklists_ItemCheck_*' } | Select-Object -First 1).automationId
        Ui invoke $item | Out-Null
        Ui wait-for "Checklists_Progress_$certificate" --value '1 of 2 done' -t 5000 | Out-Null
        Ui invoke Shell_NavBoardItem | Out-Null
        $card = (UiJson search "Board_TaskCard_$certificate").matches | Select-Object -First 1
        if ($card.name -notlike '*Checklist 1/2') { throw "Board card: $($card.name)" }
        $card.name
    }

    Step 'Failure: a disk-full commit keeps the editor and draft, shows the error and the App InfoBar' {
        Ui invoke Shell_NavGridItem | Out-Null
        New-Item -ItemType Directory -Force -Path $triggers | Out-Null
        Set-Content (Join-Path $triggers 'fail-next-commit') 'DiskFull'
        Ui set-value "Grid_Cell_${certificate}_Title" 'Renew and install the TLS certificate' | Out-Null
        Ui wait-for Grid_CellError --value 'drive is full' --contains -t 5000 | Out-Null
        Ui wait-for Grid_CellEditor --value 'Renew and install the TLS certificate' -t 3000 | Out-Null
        Ui wait-for Shell_SaveStatusDetailsButton -t 3000 | Out-Null
        Ui wait-for Shell_SaveFailedInfoBar -t 3000 | Out-Null
        "error '$(Value Grid_CellError)'; draft kept; InfoBar open"
    }

    Step 'Failure: the other pages still show the committed title' {
        Ui invoke Shell_NavChecklistsItem | Out-Null
        Ui wait-for "Checklists_GroupTitle_$certificate" --value 'Renew the TLS certificate' -t 3000 | Out-Null
        Ui invoke Shell_NavGridItem | Out-Null
        'Checklists header unchanged'
    }

    Step 'Failure: retrying the kept draft saves it' {
        Ui set-value "Grid_Cell_${certificate}_Title" 'Renew and install the TLS certificate' | Out-Null
        Ui wait-for "Grid_Cell_${certificate}_Title" --value 'Renew and install the TLS certificate' -t 5000 | Out-Null
        Ui wait-for Shell_SaveStatusText --value 'Saved' -t 3000 | Out-Null
        'saved after retry'
    }

    Step 'Saving: the editor stays open with progress while the command runs' {
        Set-Content (Join-Path $triggers 'delay-next-commit') '4000'
        Ui set-value "Grid_Cell_${certificate}_Service" 'Platform Security' | Out-Null
        Ui wait-for Shell_SaveStatusText --value 'Saving…' -t 2000 | Out-Null
        Ui wait-for Grid_CellEditor -t 1000 | Out-Null
        Ui wait-for "Grid_Cell_${certificate}_Service" --value 'Platform Security' -t 8000 | Out-Null
        Ui wait-for Grid_CellEditor --gone -t 5000 | Out-Null
        'Saving… with the editor open, then saved and closed'
    }

    Step 'Conflict: a genuine stale revision keeps the draft and shows the saved value; Use mine saves it' {
        Ui invoke "Grid_Cell_${onboarding}_Title" | Out-Null
        Ui wait-for Grid_CellEditor --value 'Update onboarding copy v2' -t 3000 | Out-Null
        Ui set-value Grid_CellEditor 'Onboarding copy, final' | Out-Null
        Set-Content (Join-Path $triggers 'delay-next-commit') '6000'

        # Leaving the cell starts the commit (B-07); the hook holds it before the writer while the
        # Checklists page commits a change to the same task, so the held command arrives with a stale revision.
        Ui focus "Grid_Cell_${onboarding}_Kind" | Out-Null
        Ui invoke Shell_NavChecklistsItem | Out-Null
        Ui set-value "Checklists_AddItemBox_$onboarding" 'Check the German text' | Out-Null
        Ui invoke "Checklists_AddItemButton_$onboarding" | Out-Null
        Ui wait-for "Checklists_Progress_$onboarding" --value '1 of 4 done' -t 5000 | Out-Null
        Ui invoke Shell_NavGridItem | Out-Null
        Ui wait-for Grid_ConflictSavedText -t 10000 | Out-Null
        $saved = Value Grid_ConflictSavedText
        $draft = Value Grid_ConflictDraftText
        $error = Value Grid_CellError
        # While the editor is open the cell's UIA value is the draft; the row name shows the committed title.
        $row = ((UiJson search "Grid_Row_$onboarding").matches | Select-Object -First 1).name
        if ($row -notlike 'Task Update onboarding copy v2,*') { throw "The committed title changed: $row" }
        Ui invoke Conflict_UseMineButton | Out-Null
        Ui wait-for "Grid_Cell_${onboarding}_Title" --value 'Onboarding copy, final' -t 5000 | Out-Null
        "error '$error'; '$saved'; '$draft'; Use mine saved the draft"
    }

    Step 'Responsive: breakpoints follow the window width (500 / 640 / 1008 / 1600 epx)' {
        $notes = foreach ($width in 500, 640, 1008, 1600) {
            $size = Resize $width 800
            $toggle = (UiJson search 'TogglePaneButton').matchCount
            $pane = ((UiJson search 'PaneRoot').matches | Select-Object -First 1)
            "$width epx -> $($size.WidthPx)x$($size.HeightPx) px, pane width $([int]($pane.width / $size.Scale)) epx, nav toggle in pane: $toggle"
        }
        $notes -join ' | '
    }

    Step 'Window minimum: PreferredMinimumWidth/Height units (design §2.1)' {
        $size = Resize 200 200
        "requested 200x200 epx; window kept at $($size.WidthPx)x$($size.HeightPx) physical px at scale $($size.Scale) (750 = units are physical pixels; 1125 = epx)"
    }

    Resize 1280 800 | Out-Null

    Step 'AC-17: 100 navigation cycles keep the live store subscriptions stable' {
        $before = (Get-Process -Id $appPid).WorkingSet64
        for ($i = 0; $i -lt 100; $i++) {
            Ui invoke Shell_NavBoardItem | Out-Null
            Ui invoke Shell_NavGridItem | Out-Null
            Ui invoke Shell_NavChecklistsItem | Out-Null
        }

        Start-Sleep -Seconds 2
        $after = (Get-Process -Id $appPid).WorkingSet64
        $lines = LogLines 'Navigated to'
        $subscriptions = $lines | ForEach-Object { if ($_ -match 'subscriptions (\d+); working set (\d+)') { [pscustomobject]@{ S = [int]$Matches[1]; W = [int]$Matches[2] } } }
        $last300 = $subscriptions | Select-Object -Last 300
        $distinct = ($last300.S | Sort-Object -Unique) -join ','
        if (($last300.S | Sort-Object -Unique).Count -ne 1) { throw "Subscription counts varied: $distinct" }
        $first = $last300 | Select-Object -First 1
        $last = $last300 | Select-Object -Last 1
        "subscriptions $distinct over 300 navigations; log working set $($first.W) -> $($last.W) MiB; process working set $([int]($before / 1MB)) -> $([int]($after / 1MB)) MiB"
    }

    Step 'Relaunch: the edits are shown after closing and reopening the same data folder' {
        Close-App
        $exit = (LogLines 'Exiting with code') | Select-Object -Last 1
        if ($exit -notmatch 'code 0 ') { throw "Unexpected exit: $exit" }
        Launch
        Ui invoke Shell_NavGridItem | Out-Null
        Ui wait-for "Grid_Cell_${onboarding}_Title" --value 'Onboarding copy, final' -t 10000 | Out-Null
        Ui wait-for "Grid_Cell_${onboarding}_Status" --value 'In Progress' -t 3000 | Out-Null
        Ui wait-for "Grid_Cell_${certificate}_Title" --value 'Renew and install the TLS certificate' -t 3000 | Out-Null
        $ready = LogLines 'Startup ready'
        "after relaunch: title and status persisted; $($ready.Count) startups in the log"
    }
} catch {
    $results.Add([ordered]@{ name = 'Run'; status = 'FAIL'; detail = "$_" })
} finally {
    try { winapp ui yield 2>&1 | Out-Null } catch { }
    Close-App
}

$failed = @($results | Where-Object { $_.status -eq 'FAIL' }).Count
$report = [ordered]@{
    date = (Get-Date).ToString('o'); configuration = 'Debug (MAXPLANNER_DEBUG_HOOKS=1)'; root = $Root
    exits = @(LogLines 'Exiting with code')
    passed = $results.Count - $failed; failed = $failed; results = @($results)
}
$report | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $here 'slice-results.json') -Encoding utf8
Write-Host "Passed: $($report.passed) | Failed: $failed"
Copy-Item (Join-Path $Root 'data/logs/*.log') (Join-Path $here 'slice-app.log') -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
if ($failed) { exit 1 }
