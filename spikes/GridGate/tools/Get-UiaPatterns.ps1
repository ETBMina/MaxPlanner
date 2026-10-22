# Read-only UI Automation probe for the Grid gate spike (Windows PowerShell 5.1: UIAutomationClient).
# Reports supported patterns and Grid/Table/GridItem/TableItem data for the table, a row, and a cell.
# Usage: powershell.exe -NoProfile -File Get-UiaPatterns.ps1 -ProcessId <pid> -RowKey <32-hex row id>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][string]$RowKey,
    [string]$CellColumn = 'Title'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$tree = [System.Windows.Automation.TreeScope]

$pidCondition = New-Object System.Windows.Automation.PropertyCondition($ae::ProcessIdProperty, $ProcessId)
$window = $ae::RootElement.FindFirst($tree::Children, $pidCondition)
if (-not $window) { throw "No top-level window for PID $ProcessId" }

function Find-ById([string]$id) {
    $condition = New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, $id)
    $window.FindFirst($tree::Descendants, $condition)
}

function Describe([string]$label, $element) {
    if (-not $element) { "${label}: NOT FOUND"; return }
    $current = $element.Current
    "${label}: ControlType=$($current.ControlType.ProgrammaticName) Name='$($current.Name)' AutomationId='$($current.AutomationId)' Class='$($current.ClassName)'"
    "  Patterns: " + (($element.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'Identifiers.Pattern', '' }) -join ', ')
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.GridPattern]::Pattern, [ref]$pattern)) {
        "  GridPattern: RowCount=$($pattern.Current.RowCount) ColumnCount=$($pattern.Current.ColumnCount)"
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TablePattern]::Pattern, [ref]$pattern)) {
        $headers = $pattern.Current.GetColumnHeaders() | ForEach-Object { "$($_.Current.Name)[$($_.Current.AutomationId)]" }
        "  TablePattern: RowOrColumnMajor=$($pattern.Current.RowOrColumnMajor) ColumnHeaders=" + ($headers -join ', ')
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.GridItemPattern]::Pattern, [ref]$pattern)) {
        "  GridItemPattern: Row=$($pattern.Current.Row) Column=$($pattern.Current.Column) ContainingGrid='$($pattern.Current.ContainingGrid.Current.AutomationId)'"
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.TableItemPattern]::Pattern, [ref]$pattern)) {
        $columnHeaders = $pattern.Current.GetColumnHeaderItems() | ForEach-Object { $_.Current.Name }
        $rowHeaders = $pattern.Current.GetRowHeaderItems() | ForEach-Object { $_.Current.Name }
        "  TableItemPattern: ColumnHeaderItems=" + ($columnHeaders -join ', ') + " RowHeaderItems=" + ($rowHeaders -join ', ')
    }
    if ($element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        "  ValuePattern: '$($pattern.Current.Value)' IsReadOnly=$($pattern.Current.IsReadOnly)"
    }
    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($element)
    if ($child) { "  First child: $($child.Current.ControlType.ProgrammaticName) '$($child.Current.Name)'" }
}

Describe 'Table' (Find-ById 'Grid_Table')
Describe 'Row' (Find-ById "Grid_Row_$RowKey")
Describe 'Cell' (Find-ById "Grid_Cell_${RowKey}_$CellColumn")

# Column headers: report how they are exposed (control type, name, AutomationId) independent of TablePattern.
$headerCondition = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::HeaderItem)
$headers = $window.FindAll($tree::Descendants, $headerCondition) | Where-Object { $_.Current.Name -notlike 'Row *' } | Select-Object -First 15
"Column header items: " + (($headers | ForEach-Object { "$($_.Current.Name)[$($_.Current.AutomationId)]" }) -join ', ')
