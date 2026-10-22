# Spike 4 UI Automation run against a running PlatformControls window (Settings page visible).
# Non-intrusive input only: UIA patterns (invoke/select/expand/toggle), focus, get-*, screenshots, and
# post-message keys sent to the spike's own HWND. No mouse, no send-input.
# Usage: pwsh -File Spike4-Uia.ps1 -Hwnd <hwnd> -OutDir <evidence folder>
param(
    [Parameter(Mandatory)][string]$Hwnd,
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$log = Join-Path $OutDir '06-uia-settings-log.txt'
$results = [Collections.Generic.List[string]]::new()
"Spike 4 UIA run $(Get-Date -Format o) hwnd=$Hwnd" | Set-Content $log

function Ui {
    param([string[]]$Arguments)
    $global:LASTEXITCODE = $null
    $output = @(& winapp ui @Arguments -w $Hwnd 2>&1 | ForEach-Object { "$_" })
    $code = $global:LASTEXITCODE
    Add-Content $log "> winapp ui $($Arguments -join ' ') -w $Hwnd   (exit $code)"
    Add-Content $log ($output -join "`n")
    if ($code -ne 0) { throw "winapp ui $($Arguments -join ' ') failed (exit $code): $($output -join ' ')" }
    $output
}

function Json { param([string[]]$Arguments) (Ui ($Arguments + '--json')) -join "`n" | ConvertFrom-Json }

function Check {
    param([string]$Name, [scriptblock]$Script)
    try {
        $detail = & $Script
        $results.Add("PASS  $Name  $detail")
    } catch {
        $results.Add("FAIL  $Name  $_")
    }
    Add-Content $log "### $($results[-1])"
}

function Get-Popup {
    $windows = & winapp ui list-windows -a $script:AppPid --json 2>$null | ConvertFrom-Json
    $popup = @($windows | Where-Object { [string]$_.ownerHwnd -eq $Hwnd -and $_.title -eq 'PopupHost' }) | Select-Object -First 1
    if (-not $popup) { throw 'No PopupHost window owned by the spike window' }
    [string]$popup.hwnd
}

function PopupJson {
    param([string]$PopupHwnd, [string[]]$Arguments)
    $output = @(& winapp ui @Arguments -w $PopupHwnd --json 2>&1 | ForEach-Object { "$_" })
    Add-Content $log "> winapp ui $($Arguments -join ' ') -w $PopupHwnd --json`n$($output -join "`n")"
    ($output -join "`n") | ConvertFrom-Json
}

$script:AppPid = (& winapp ui list-windows --json | ConvertFrom-Json | Where-Object { [string]$_.hwnd -eq $Hwnd }).processId
$script:ComboSlug = (Json @('get-property', 'Settings_ThemeCombo')).elementId
function Focused { $f = Json @('get-focused'); "$($f.element.automationId)|$($f.element.type)|$($f.element.name)" }

Check 'Cards expose names/roles' {
    $ids = 'Settings_ThemeCard', 'Settings_TaskStatusesExpander', 'Settings_ShowCompletedCard', 'Settings_DataLocationCard', 'Settings_DataPageCard'
    ($ids | ForEach-Object { $p = (Json @('get-property', $_)).properties; "$_=[$($p.ControlType) '$($p.Name)']" }) -join ' '
}

Check 'Inner controls named by Toolkit header' {
    $ids = 'Settings_ThemeCombo', 'Settings_ShowCompletedToggle', 'Settings_OpenDataFolderButton'
    ($ids | ForEach-Object { $p = (Json @('get-property', $_)).properties; "$_=[$($p.ControlType) '$($p.Name)']" }) -join ' '
}

Check 'Expander header exposes ExpandCollapse and expands via UIA' {
    $header = (Json @('search', 'Task statuses', '--type', 'Button', '--root', 'Settings_TaskStatusesExpander')).matches | Select-Object -First 1
    if (-not $header) { throw 'Expander header button not found' }
    $before = (Json @('get-property', $header.selector, '-p', 'ExpandCollapseState')).properties.ExpandCollapseState
    Ui @('invoke', $header.selector, '--action', 'expand') | Out-Null
    Start-Sleep -Milliseconds 600
    $after = (Json @('get-property', $header.selector, '-p', 'ExpandCollapseState')).properties.ExpandCollapseState
    if ($after -ne 'Expanded') { throw "state after expand: $after" }
    "header=$($header.selector) before=$before after=$after"
}

Check 'Expanded rows and inner controls reachable with custom AutomationIds' {
    $rows = 'backlog', 'todo', 'inprogress', 'blocked', 'review', 'qa', 'done', 'cancelled'
    $found = foreach ($key in $rows) {
        $row = (Json @('get-property', "Settings_TaskStatusesRow_$key")).properties
        $menu = (Json @('get-property', "Settings_TaskStatusesRowMenu_$key")).properties
        "$key=[$($row.ControlType) '$($row.Name)'; menu '$($menu.Name)']"
    }
    $add = (Json @('get-property', 'Settings_TaskStatusesAddBox')).properties
    ($found -join ' ') + " addBox=[$($add.ControlType) '$($add.Name)']"
}

Check 'Expanded screenshot' {
    Ui @('scroll-into-view', 'Settings_TaskStatusesRowMenu_cancelled') | Out-Null
    Ui @('screenshot', '-o', (Join-Path $OutDir '07-expander-expanded.png')) | Out-Null
    'saved 07-expander-expanded.png'
}

Check 'Row menu button opens MenuFlyout via UIA invoke' {
    Ui @('invoke', 'Settings_TaskStatusesRowMenu_inprogress') | Out-Null
    Start-Sleep -Milliseconds 700
    $popup = Get-Popup
    $items = (PopupJson $popup @('search', 'Set as default')).matches
    Ui @('screenshot', '-o', (Join-Path $OutDir '08-row-menu-open.png'), '--capture-screen') | Out-Null
    Ui @('send-keys', 'esc') | Out-Null
    Start-Sleep -Milliseconds 400
    if (@($items).Count -eq 0) { throw "menu item not found in popup $popup" }
    "popup hwnd=$popup; 'Set as default' -> $($items[0].type) $($items[0].selector)"
}
Check 'ToggleSwitch in card toggles via UIA' {
    Ui @('invoke', 'Settings_ShowCompletedToggle', '--action', 'toggle') | Out-Null
    $value = (Json @('get-property', 'Settings_ShowCompletedToggle', '-p', 'ToggleState')).properties.ToggleState
    "ToggleState after toggle=$value"
}

Check 'Clickable card (IsClickEnabled) exposes UIA Invoke' {
    $p = (Json @('get-property', 'Settings_DataPageCard')).properties
    try { Ui @('invoke', 'Settings_DataPageCard') | Out-Null } catch { throw "ControlType=$($p.ControlType) Name='$($p.Name)' but UIA invoke failed: $_" }
    "invoked"
}

Check 'Clickable card activates from keyboard (focus + space/enter, post-message)' {
    Ui @('focus', 'Settings_DataPageCard') | Out-Null
    Ui @('send-keys', 'space') | Out-Null
    Start-Sleep -Milliseconds 400
    $afterSpace = (Json @('get-value', 'Settings_ClickStatusText')).text
    Ui @('send-keys', 'enter') | Out-Null
    Start-Sleep -Milliseconds 400
    $afterEnter = (Json @('get-value', 'Settings_ClickStatusText')).text
    if ($afterEnter -notmatch 'card clicked') { throw "space='$afterSpace' enter='$afterEnter'" }
    "after space='$afterSpace' after enter='$afterEnter'"
}
Check 'Keyboard Tab traversal (post-message to spike HWND)' {
    Ui @('focus', 'Settings_ThemeCombo') | Out-Null
    $sequence = [Collections.Generic.List[string]]::new()
    $sequence.Add((Focused))
    for ($i = 0; $i -lt 16; $i++) {
        Ui @('send-keys', 'tab') | Out-Null
        Start-Sleep -Milliseconds 250
        $sequence.Add((Focused))
    }
    $distinct = @($sequence | Select-Object -Unique)
    if ($distinct.Count -lt 2) { throw "focus did not move: $($sequence -join ' -> ')" }
    $sequence -join ' -> '
}

foreach ($theme in @(@{ Name = 'Light'; File = '09-theme-light.png' }, @{ Name = 'Dark'; File = '10-theme-dark.png' })) {
    Check "Theme $($theme.Name) via ComboBox selection (root RequestedTheme)" {
        Ui @('invoke', $script:ComboSlug, '--action', 'expand') | Out-Null
        Start-Sleep -Milliseconds 600
        $popup = Get-Popup
        $item = (PopupJson $popup @('search', $theme.Name, '--type', 'ListItem')).matches | Select-Object -First 1
        if (-not $item) { throw "ListItem $($theme.Name) not found in popup $popup" }
        & winapp ui invoke $item.selector -w $popup --action select 2>&1 | Out-Null
        Start-Sleep -Milliseconds 900
        $state = (Json @('get-property', $script:ComboSlug, '-p', 'ExpandCollapseState')).properties.ExpandCollapseState
        if ($state -eq 'Expanded') { Ui @('invoke', $script:ComboSlug, '--action', 'collapse') | Out-Null; Start-Sleep -Milliseconds 400 }
        Ui @('scroll-into-view', 'Settings_ActualThemeText') | Out-Null
        Start-Sleep -Milliseconds 300
        $text = (Json @('get-value', 'Settings_ActualThemeText')).text
        Ui @('screenshot', '-o', (Join-Path $OutDir $theme.File)) | Out-Null
        if ($text -notmatch "actual theme: $($theme.Name)") { throw $text }
        "$text; combo value=$((Json @('get-value', $script:ComboSlug)).text)"
    }
}
Add-Content $log "`n==== SUMMARY"
Add-Content $log ($results -join "`n")
$results
