# Read-only UI Automation probe (Windows PowerShell 5.1: UIAutomationClient). Reports the element UIA considers
# focused, its supported patterns, and its ancestor chain, so a focus that `winapp ui get-focused -w` cannot
# attribute to the window can be explained. Optionally describes an element by AutomationId in a process.
# Usage: powershell.exe -NoProfile -File Get-UiaFocus.ps1 [-ProcessId <pid> -AutomationId <id>]
param(
    [int]$ProcessId = 0,
    [string]$AutomationId = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

function Describe([string]$label, $element) {
    if (-not $element) { "${label}: <null>"; return }
    $c = $element.Current
    "${label}: ControlType=$($c.ControlType.ProgrammaticName) Name='$($c.Name)' AutomationId='$($c.AutomationId)' Class='$($c.ClassName)' Pid=$($c.ProcessId) HasKeyboardFocus=$($c.HasKeyboardFocus) IsKeyboardFocusable=$($c.IsKeyboardFocusable) Rect=$($c.BoundingRectangle)"
    "  Patterns: " + (($element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'Identifiers.Pattern', '' }) -join ', ')
    $chain = @()
    $parent = $walker.GetParent($element)
    while ($parent -and $chain.Count -lt 12) {
        $chain += "$($parent.Current.ControlType.ProgrammaticName -replace 'ControlType.', '')['$($parent.Current.AutomationId)']"
        $parent = $walker.GetParent($parent)
    }
    "  Ancestors: " + ($chain -join ' < ')
}

Describe 'FocusedElement' $ae::FocusedElement

if ($ProcessId -gt 0 -and $AutomationId) {
    $window = $ae::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,
        (New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $ProcessId)))
    $target = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $AutomationId)))
    Describe "Element[$AutomationId]" $target
}
