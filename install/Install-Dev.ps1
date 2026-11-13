<#
.SYNOPSIS
  Dev install for the KH Batch Script Runner Revit add-in.
  Copies the build output (dll + .addin + pdb) into the per-user Revit Addins
  folder so Revit loads it on next launch.

.EXAMPLE
  # Build first (default target Revit 2026), then install:
  .\install\Install-Dev.ps1 -RevitVersion 2026

.EXAMPLE
  # Put the machine-wide installed build back in charge and remove the dev one:
  .\install\Install-Dev.ps1 -RevitVersion 2026 -RestoreInstalled

.EXAMPLE
  # Deploy DRW Helper (build with -p:Edition=DRW first) beside the batch runner:
  .\install\Install-Dev.ps1 -RevitVersion 2026 -Edition DRW -Configuration Release

.NOTES
  Close Revit before running if the add-in is already loaded (the dll will be
  locked). Re-open Revit afterwards; the "KH-Tool" (or, with -Edition DRW, "DRW Helper") ribbon tab appears.

  Revit ignores an all-users manifest when a per-user manifest has the same file
  name. The normal KhBatchRunner.addin deployment therefore needs no elevation.
  A differently named machine-wide manifest that claims the same AddInId is still
  parked when permissions allow - see Disable-ConflictingManifests below.
#>
param(
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string]$RevitVersion = "2026",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    # Which product to deploy. DRW = DRW Helper (DrwHelper.dll, own AddInId), which lives in an
    # Addins\<ver>\DrwHelper\ subfolder beside its manifest so it never shares dlls with Full.
    [ValidateSet("Full", "DRW")]
    [string]$Edition = "Full",
    # Undo: re-enable the machine-wide manifest and delete the dev one.
    [switch]$RestoreInstalled
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
# Output is per Revit version (bin\<Config>\<Version>\) so net48 (2024) deps never mix with net8.
$bin  = Join-Path $root "src\KhBatchRunner\bin\$Configuration\$RevitVersion"
$asmName = "KhBatchRunner"
$tabName = "KH-Tool"
if ($Edition -eq "DRW") {
    # A non-Full edition builds to bin\<Config>\<Edition>\<Version>\ (see KhBatchRunner.csproj).
    $bin     = Join-Path $root "src\KhBatchRunner\bin\$Configuration\DRW\$RevitVersion"
    $asmName = "DrwHelper"
    $tabName = "DRW Helper"
}

$dest       = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
# Where the dll and its companions go. Full keeps its historical flat layout; DRW's manifest names
# DrwHelper\DrwHelper.dll, so everything but the manifest lands in that subfolder.
$payload    = if ($Edition -eq "DRW") { Join-Path $dest "DrwHelper" } else { $dest }
$machineDir = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$RevitVersion"
# Only ever restore manifests THIS script parked; a manifest the user disabled by
# hand keeps whatever name they gave it.
$parkedSuffix = ".disabled-by-dev-install"

function Get-AddInIds([string]$manifest) {
    try { ([xml](Get-Content -Raw $manifest)).RevitAddIns.AddIn.AddInId } catch { @() }
}

<#
  Revit keys its "always load this add-in" trust (HKCU\...\CodeSigning) by AddInId. Two
  manifests carrying the SAME AddInId - the dev build in %AppData% and the installed
  build in %ProgramData% - therefore point one trust record at two different, unsigned
  assemblies, and Revit falls back to prompting. It prompts ONCE PER MANIFEST, per Revit
  process, and the prompt is modal.

  That is cheap to click away in an interactive session and ruinous in a batch: a split
  run starting four background Revits produces eight modal dialogs at once, the
  UI-Automation handler clears them one at a time, and any worker whose prompt is never
  reached does no work at all and is eventually killed for making no progress. Measured
  on 2026-07-26: a 14-model split run returned 27 of 42 results, with one worker
  contributing nothing and the rest running strictly one after another instead of in
  parallel.

  Autodesk documents one useful precedence rule: when a manifest in %AppData% has
  the same FILE NAME as one in %ProgramData%, Revit ignores the ProgramData copy.
  That is the normal case for this project (KhBatchRunner.addin), needs no admin
  access, and leaves exactly one manifest live. Only a differently named machine
  manifest with the same AddInId has to be parked.
#>
function Disable-ConflictingManifests {
    if (-not (Test-Path $machineDir)) { return }

    $devIds = @(Get-ChildItem $dest -Filter *.addin -ErrorAction SilentlyContinue |
                ForEach-Object { Get-AddInIds $_.FullName })
    if ($devIds.Count -eq 0) { return }

    foreach ($m in Get-ChildItem $machineDir -Filter *.addin -ErrorAction SilentlyContinue) {
        $clash = @(Get-AddInIds $m.FullName | Where-Object { $devIds -contains $_ })
        if ($clash.Count -eq 0) { continue }

        $sameNameOverride = Get-ChildItem $dest -Filter *.addin -ErrorAction SilentlyContinue |
            Where-Object { $_.Name.Equals($m.Name, [StringComparison]::OrdinalIgnoreCase) } |
            Select-Object -First 1
        if ($null -ne $sameNameOverride) {
            Write-Host ("  per-user $($sameNameOverride.Name) overrides the identically named " +
                        "machine-wide manifest (Revit manifest precedence; no elevation needed)")
            continue
        }

        # %ProgramData% is admin-only. Renaming is a convenience, not the point of this
        # script, so an unelevated run reports the clash and carries on rather than
        # failing a deploy that has already succeeded.
        # Catch broadly, not just UnauthorizedAccessException: PowerShell surfaces an admin-denied
        # Remove-Item as a wrapped ArgumentError, which slipped past a typed catch and aborted a
        # deploy that had already succeeded.
        try {
            $parked = $m.FullName + $parkedSuffix
            if (Test-Path $parked) { Remove-Item $parked -Force -ErrorAction Stop }
            Rename-Item $m.FullName -NewName ($m.Name + $parkedSuffix) -Force -ErrorAction Stop
            Write-Warning ("Parked $($m.FullName) - it claims AddInId $($clash[0]), which the dev " +
                           "build also uses. Two manifests for one AddInId make Revit prompt on every " +
                           "launch, which breaks split batch runs. Restore with -RestoreInstalled.")
        }
        catch {
            Write-Warning ("$($m.FullName) claims AddInId $($clash[0]), the same one the dev build " +
                           "uses. Revit will prompt on every launch and split batch runs will lose " +
                           "workers. Run this from an ELEVATED prompt to park it, or rename it by hand:" +
                           "`n    ren `"$($m.FullName)`" `"$($m.Name)$parkedSuffix`"")
        }
    }
}

if ($RestoreInstalled) {
    $restored = 0
    Get-ChildItem $machineDir -Filter ("*.addin" + $parkedSuffix) -ErrorAction SilentlyContinue |
        ForEach-Object {
            Rename-Item $_.FullName -NewName ($_.Name -replace ([regex]::Escape($parkedSuffix) + '$'), '') -Force
            Write-Host "  restored $($_.Name -replace ([regex]::Escape($parkedSuffix) + '$'), '')"
            $restored++
        }
    # Only THIS edition's dev manifest: Full and DRW dev installs sit side by side in $dest, and
    # restoring one product must not take the other off the ribbon.
    $devManifest = Join-Path $dest "$asmName.addin"
    if (Test-Path $devManifest) { Remove-Item $devManifest -Force; Write-Host "  removed dev $asmName.addin" }
    if ($payload -ne $dest -and (Test-Path $payload)) {
        Remove-Item $payload -Recurse -Force; Write-Host "  removed dev $payload"
    }

    Write-Host ""
    if ($restored -eq 0) { Write-Warning "Nothing parked by this script was found in $machineDir." }
    Write-Host "The machine-wide install is back in charge. Restart Revit." -ForegroundColor Green
    return
}

if (-not (Test-Path (Join-Path $bin "$asmName.dll"))) {
    throw "Build output not found in $bin. Build first: " +
          "dotnet build src\KhBatchRunner\KhBatchRunner.csproj -c $Configuration -p:RevitVersion=$RevitVersion -p:Edition=$Edition"
}

# Warn (don't hard-fail) if Revit is running - a loaded dll can't be overwritten.
if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    Write-Warning "Revit is running. If the add-in is already loaded the copy will fail - close Revit and re-run."
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
New-Item -ItemType Directory -Force -Path $payload | Out-Null

# Copy the manifest, symbols, and every DLL the build emitted. The net48 (Revit 2024) build
# drops System.Text.Json + its transitives here; net8 (2025/2026) and net10 (2027) drop just our dll (+ STJ).
# RevitAPI/RevitAPIUI are never in bin (Private=false), so a blanket *.dll copy is safe.
Copy-Item (Join-Path $bin "$asmName.addin") $dest -Force; Write-Host "  copied $asmName.addin"
$pdb = Join-Path $bin "$asmName.pdb"
if (Test-Path $pdb) { Copy-Item $pdb $payload -Force; Write-Host "  copied $asmName.pdb" }
foreach ($dll in Get-ChildItem -Path $bin -Filter *.dll) {
    Copy-Item $dll.FullName $payload -Force; Write-Host "  copied $($dll.Name)"
}
# The scheduled-run launcher must sit beside the dll: TaskSchedulerGateway resolves it relative to
# the loaded assembly, and a schedule now refuses to register when it cannot be launched. Without
# this a dev install could save no schedules at all.
foreach ($name in "KhBatchLauncher.exe", "KhBatchLauncher.exe.config") {
    $launcher = Join-Path $bin $name
    if (Test-Path $launcher) { Copy-Item $launcher $payload -Force; Write-Host "  copied $name" }
}

# KH-Tool's AI Bridge: the MCP server Claude Code starts lives in a KhMcp\ subfolder (net48, kept
# apart from the net8 dll). AiBridgeCommand shows the setup line pointing at this path.
$khMcp = Join-Path $bin "KhMcp"
if (Test-Path $khMcp) {
    New-Item -ItemType Directory -Force -Path (Join-Path $payload "KhMcp") | Out-Null
    Copy-Item (Join-Path $khMcp "*") (Join-Path $payload "KhMcp") -Force
    Write-Host "  copied KhMcp\"
}

# The ribbon's Diagnostic Report button resolves the collector relative to the loaded assembly
# (Support\Collect-Diagnostics.cmd), the same way it resolves the scheduled-run launcher. Mirror
# what the installers lay down so the button is not dead on a dev deploy.
$support = Join-Path $payload "Support"
New-Item -ItemType Directory -Force -Path $support | Out-Null
foreach ($name in "Collect-Diagnostics.cmd", "Collect-ScheduleDiagnostics.ps1") {
    Copy-Item (Join-Path $PSScriptRoot $name) $support -Force; Write-Host "  copied Support\$name"
}

# Export IFC + Data's converter (node.exe + bundled script + web-ifc wasm) lives in IfcToFrag\, which
# the action resolves relative to the loaded dll. Present only when tools\ifc2frag was built.
$ifcToFrag = Join-Path $bin "IfcToFrag"
if (Test-Path $ifcToFrag) {
    # Contents into an existing folder: copying the folder itself nests it (IfcToFrag\IfcToFrag)
    # on every re-install after the first.
    $ifcDest = Join-Path $payload "IfcToFrag"
    New-Item -ItemType Directory -Force -Path $ifcDest | Out-Null
    Copy-Item (Join-Path $ifcToFrag "*") $ifcDest -Recurse -Force
    Write-Host "  copied IfcToFrag\"
}

# Debug builds ship test fixtures (e.g. Fixtures\seed.dxf for the DEV seed actions) next
# to the dll; the actions resolve them relative to the loaded assembly, so mirror them.
$fixtures = Join-Path $bin "Fixtures"
if (Test-Path $fixtures) {
    Copy-Item $fixtures (Join-Path $payload "Fixtures") -Recurse -Force
    Write-Host "  copied Fixtures\"
}

# Builds on F:\ (a network share) carry a Zone.Identifier mark, and Copy-Item keeps it. .NET
# Framework (Revit 2024) refuses to load a marked dll: "Operation is not supported (0x80131515)".
# .NET 8 (2025/2026) ignores the mark, so only 2024 shows it.
Get-ChildItem -Path $payload -Recurse -File | Unblock-File
Unblock-File (Join-Path $dest "$asmName.addin")

# After the manifest is in place, so the AddInId comparison reads what was just deployed.
Disable-ConflictingManifests

Write-Host ""
Write-Host "Installed to: $dest (dll in $payload)" -ForegroundColor Green
Write-Host "Start Revit $RevitVersion - the '$tabName' ribbon tab should appear."
