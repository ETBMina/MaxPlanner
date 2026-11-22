#Requires -Version 7.0
<#
.SYNOPSIS
  Generates THIRD-PARTY-NOTICES.txt (full license texts) and THIRD-PARTY-NOTICES.md (a short inventory with name,
  version, license and a link to the license text) for the portable ZIP (T-03.9) from the packages that are actually in
  the published payload, or checks that the committed files still match them.
.DESCRIPTION
  The package list comes from the published MaxPlanner.deps.json (every runtime package, transitive ones included,
  plus the .NET and Windows SDK projection runtime packs) and the -ExtraPackages that contribute native files outside
  deps.json (the Windows App SDK Base package). For each package the nuspec in the local NuGet cache gives the
  license, copyright and project URL; the license text is the package's own license file, or the SPDX template in
  tools/notices/ (MIT, Apache-2.0, BSD-2-Clause) with the package's copyright line. Notice files a package ships
  (THIRD-PARTY-NOTICES, ThirdPartyNotices, NOTICE) are appended. Output is sorted and deterministic.

  The .md inventory takes its license and link from the same nuspec data: an SPDX expression links to its
  licenses.nuget.org page; a package that ships its own license file points at THIRD-PARTY-NOTICES.txt, which reproduces
  that file; a package that gives only a license URL is listed as unconfirmed with that URL, never guessed.

  -Check compares only the package list (id and version) with both committed files and exits 1 when either differs, so
  publish-portable.ps1 fails when a dependency changes without regenerating the notices.
.EXAMPLE
  ./scripts/publish-portable.ps1   # first publish (fails the notices check after a dependency change), then:
  ./tools/Generate-Notices.ps1     # regenerate from artifacts/publish/MaxPlanner/MaxPlanner.deps.json
#>
[CmdletBinding()]
param(
    [string]$DepsFile,
    [string]$Output,
    [string]$InventoryOutput,
    [string[]]$ExtraPackages = @('Microsoft.WindowsAppSDK.Base'),
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $DepsFile) { $DepsFile = Join-Path $root 'artifacts/publish/MaxPlanner/MaxPlanner.deps.json' }
if (-not $Output) { $Output = Join-Path $root 'THIRD-PARTY-NOTICES.txt' }
if (-not $InventoryOutput) { $InventoryOutput = Join-Path $root 'THIRD-PARTY-NOTICES.md' }
if (-not (Test-Path $DepsFile)) { throw "deps.json not found: $DepsFile. Publish first (scripts/publish-portable.ps1)." }

$globalPackages = ((& dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', '').Trim()
$templates = Join-Path $PSScriptRoot 'notices'

# ----- The package set -----
$deps = Get-Content $DepsFile -Raw | ConvertFrom-Json -AsHashtable
$packages = [System.Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($key in $deps.libraries.Keys) {
    $library = $deps.libraries[$key]
    $id, $version = $key -split '/', 2
    switch ($library.type) {
        'package' { $packages[$id] = $version }
        'runtimepack' { $packages[$id -replace '^runtimepack\.', ''] = $version }
        default { }
    }
}

$lock = Get-Content (Join-Path $root 'src/MaxPlanner.Desktop/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($extra in $ExtraPackages) {
    foreach ($framework in $lock.dependencies.Values) {
        if ($framework.ContainsKey($extra)) { $packages[$extra] = $framework[$extra].resolved }
    }
}

$headers = $packages.GetEnumerator() | ForEach-Object { "Package: $($_.Key) $($_.Value)" }

if ($Check) {
    $failed = $false
    foreach ($file in @(@{ Path = $Output; Pattern = '^Package: (.+)$' }, @{ Path = $InventoryOutput; Pattern = '^\| ([^ |]+) \| ([^ |]+) \|' })) {
        $name = Split-Path $file.Path -Leaf
        if (-not (Test-Path $file.Path)) { Write-Host "Missing $name."; $failed = $true; continue }
        $committed = Select-String -Path $file.Path -Pattern $file.Pattern | ForEach-Object {
            $match = $_.Matches[0]
            if ($match.Groups.Count -gt 2) { "Package: $($match.Groups[1].Value) $($match.Groups[2].Value)" } else { $_.Line.Trim() }
        } | Where-Object { $_ -notmatch '^Package: (Component|-+) ' }
        $missing = $headers | Where-Object { $_ -notin $committed }
        $stale = $committed | Where-Object { $_ -notin $headers }
        if ($missing -or $stale) {
            $missing | ForEach-Object { Write-Host "Not in ${name}: $_" }
            $stale | ForEach-Object { Write-Host "No longer in the payload (${name}): $_" }
            $failed = $true
        }
    }

    if ($failed) {
        Write-Error 'The third-party notices do not match the published payload; run tools/Generate-Notices.ps1.'
        exit 1
    }

    Write-Host "THIRD-PARTY-NOTICES.txt and .md cover all $($headers.Count) packages of the payload."
    exit 0
}

# ----- Text -----
function Read-Text([string]$path) { (Get-Content $path -Raw) -replace "`r`n", "`n" }

$inventory = [System.Collections.Generic.List[string]]::new()
$unconfirmed = [System.Collections.Generic.List[string]]::new()
$builder = [System.Text.StringBuilder]::new()
$rule = '=' * 78
[void]$builder.AppendLine('Max Planner - third-party notices')
[void]$builder.AppendLine()
[void]$builder.AppendLine('Max Planner includes the software components listed below. Each is distributed under its own license,')
[void]$builder.AppendLine('reproduced here together with the notices the component ships. The list is generated by')
[void]$builder.AppendLine('tools/Generate-Notices.ps1 from the published MaxPlanner.deps.json and the NuGet package metadata.')
[void]$builder.AppendLine()
[void]$builder.AppendLine('SQLite (inside e_sqlite3.dll, from SQLitePCLRaw.lib.e_sqlite3) is in the public domain:')
[void]$builder.AppendLine('https://www.sqlite.org/copyright.html')
[void]$builder.AppendLine()
[void]$builder.AppendLine('Components:')
$headers | ForEach-Object { [void]$builder.AppendLine("  $($_ -replace '^Package: ', '')") }

foreach ($entry in $packages.GetEnumerator()) {
    $id = $entry.Key
    $version = $entry.Value
    $folder = Join-Path $globalPackages (Join-Path $id.ToLowerInvariant() $version)
    if (-not (Test-Path $folder)) { throw "Package $id $version is not in the NuGet cache ($folder); restore first." }
    $nuspecPath = Get-ChildItem $folder -Filter '*.nuspec' | Select-Object -First 1
    [xml]$nuspec = Get-Content $nuspecPath.FullName -Raw
    $metadata = $nuspec.package.metadata
    $copyright = if ($metadata.copyright) { $metadata.copyright } else { "Copyright (c) $($metadata.authors)" }
    $license = $metadata.license
    $licenseKind = if ($license) { $license.type } else { 'url' }
    $licenseValue = if ($license) { $license.'#text' } else { $metadata.licenseUrl }

    $inventoryLicense, $inventoryLink = switch ($licenseKind) {
        'expression' { $licenseValue, "[$licenseValue](https://licenses.nuget.org/$licenseValue)" }
        'file' { "Package license file ($licenseValue)", '[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)' }
        default {
            $unconfirmed.Add("$id $version (license URL only: $licenseValue)")
            'Not stated in the package metadata (unconfirmed)', "[$licenseValue]($licenseValue)"
        }
    }
    $inventory.Add("| $id | $version | $inventoryLicense | $inventoryLink |")

    [void]$builder.AppendLine()
    [void]$builder.AppendLine($rule)
    [void]$builder.AppendLine("Package: $id $version")
    $licenseLabel = if ($licenseKind -eq 'file') { "the package's own terms ($licenseValue), reproduced below" } else { $licenseValue }
    [void]$builder.AppendLine("License: $licenseLabel")
    if ($metadata.projectUrl) { [void]$builder.AppendLine("Project: $($metadata.projectUrl)") }
    [void]$builder.AppendLine("Copyright: $copyright")
    [void]$builder.AppendLine($rule)
    [void]$builder.AppendLine()

    switch ($licenseKind) {
        'file' {
            [void]$builder.AppendLine((Read-Text (Join-Path $folder $licenseValue)).TrimEnd())
        }
        'expression' {
            $template = Join-Path $templates "$licenseValue.txt"
            if (-not (Test-Path $template)) { throw "No license template for '$licenseValue' ($id); add tools/notices/$licenseValue.txt." }
            $text = Read-Text $template
            $text = if ($text.Contains('{copyright}')) { $text.Replace('{copyright}', $copyright) } else { "$copyright`n`n$text" }
            [void]$builder.AppendLine($text.TrimEnd())
        }
        default {
            $file = Get-ChildItem $folder -File | Where-Object { $_.Name -match '^(LICENSE|License)(\.txt|\.md)?$' } | Select-Object -First 1
            if ($file) { [void]$builder.AppendLine((Read-Text $file.FullName).TrimEnd()) }
            else { [void]$builder.AppendLine("The license terms are published at $licenseValue") }
        }
    }

    $notices = Get-ChildItem $folder -File | Where-Object { $_.Name -match '^(THIRD-PARTY-NOTICES|ThirdPartyNotices|NOTICE)(\.txt|\.md)?$' } | Sort-Object Name
    foreach ($notice in $notices) {
        [void]$builder.AppendLine()
        [void]$builder.AppendLine("--- $($notice.Name) distributed with $id ---")
        [void]$builder.AppendLine()
        [void]$builder.AppendLine((Read-Text $notice.FullName).TrimEnd())
    }
}

$text = $builder.ToString() -replace "`r`n", "`n"
[IO.File]::WriteAllText($Output, $text, [Text.UTF8Encoding]::new($false))

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine('# Third-party notices')
[void]$md.AppendLine()
[void]$md.AppendLine('Max Planner is licensed under the PolyForm Internal Use License 1.0.0 (see `LICENSE`). It includes the components')
[void]$md.AppendLine('below, each under its own license. `THIRD-PARTY-NOTICES.txt` reproduces the license texts and the notices the')
[void]$md.AppendLine('packages ship. Both files are generated by `tools/Generate-Notices.ps1` from the published `MaxPlanner.deps.json` and')
[void]$md.AppendLine('the license data in each package''s own `.nuspec`.')
[void]$md.AppendLine()
[void]$md.AppendLine('| Component | Version | License | License text |')
[void]$md.AppendLine('| --- | --- | --- | --- |')
foreach ($row in $inventory) { [void]$md.AppendLine($row) }
[void]$md.AppendLine()
[void]$md.AppendLine('## Not a NuGet package')
[void]$md.AppendLine()
[void]$md.AppendLine('- SQLite (inside `e_sqlite3.dll`, from `SQLitePCLRaw.lib.e_sqlite3`) is in the public domain: <https://www.sqlite.org/copyright.html>')
if ($unconfirmed.Count -gt 0) {
    [void]$md.AppendLine()
    [void]$md.AppendLine('## License not confirmed')
    [void]$md.AppendLine()
    [void]$md.AppendLine('The package metadata gives only a license URL and no license text or identifier, so the terms were not verified:')
    [void]$md.AppendLine()
    foreach ($item in $unconfirmed) { [void]$md.AppendLine("- $item") }
}
$mdText = $md.ToString() -replace "`r`n", "`n"
[IO.File]::WriteAllText($InventoryOutput, $mdText, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $Output and $InventoryOutput ($($packages.Count) packages)."
