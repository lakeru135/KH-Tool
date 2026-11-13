<#
.SYNOPSIS
  Packages each add-in as a no-admin ZIP: the same Release files as the setup.exe installers,
  plus Install.cmd / Uninstall.cmd that copy them into the CURRENT USER's Revit Addins folder
  (%APPDATA%\Autodesk\Revit\Addins\<year>). For PCs where the user cannot elevate.

  Run AFTER the installer builds (Build-Installer.ps1, Build-Installer-Drw-2026.ps1):
  it reads their bin\Release trees and the AppVersion of each .iss,
  and refuses to package a DLL whose stamped version differs from its .iss.

  Output: install\Output\NoAdmin\<Name>-NoAdmin-<version>.zip

  Why .cmd and not .ps1: a managed PC can run PowerShell in ConstrainedLanguage mode or block
  scripts outright (see Verify-ConstrainedLanguageSafe.ps1); a batch file using xcopy and reg
  works on all of them. The one PowerShell call in Install.cmd (Unblock-File) is best effort.

.EXAMPLE
  .\install\Build-NoAdminPackages.ps1
#>
param(
    [ValidateSet("Full", "DRW")]
    [string[]]$Editions = @("Full", "DRW")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$root    = Split-Path -Parent $PSScriptRoot
$binRoot = Join-Path $root "src\KhBatchRunner\bin\Release"
$tmplDir = Join-Path $PSScriptRoot "noadmin"
$outDir  = Join-Path $PSScriptRoot "Output\NoAdmin"
$stage   = Join-Path $env:TEMP "KhBatchNoAdmin"

# One row per edition. Bins maps a Revit year to its Release tree; Subdir is where the DLLs go
# relative to Addins\<year> (DRW keeps its payload in its own folder - see DrwHelper.addin).
$defs = @{
    Full = @{ Name = "KhBatchRunner"; Product = "KH Batch Script Runner"; Tab = "KH-Tool"
              Iss = "KhBatchRunner.iss"; Asm = "KhBatchRunner"; Subdir = ""; Protocol = 1
              Bins = [ordered]@{ "2024" = "2024"; "2025" = "2025"; "2026" = "2026"; "2027" = "2027" } }
    DRW  = @{ Name = "DrwHelper"; Product = "DRW Helper"; Tab = "DRW Helper"
              Iss = "DrwHelper-2026.iss"; Asm = "DrwHelper"; Subdir = "DrwHelper\"; Protocol = 0
              Bins = [ordered]@{ "2024" = "DRW\2024"; "2026" = "DRW\2026"; "2027" = "DRW\2027" } }
}

# The templates are ASCII batch files; cmd.exe needs CRLF. Written byte-exact (no BOM): this PC's
# codepage is 874, so a Get/Set-Content round trip is exactly what must not touch them.
function Write-Crlf([string]$text, [string]$path) {
    $norm = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
    [IO.File]::WriteAllText($path, $norm, (New-Object Text.ASCIIEncoding))
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

foreach ($ed in $Editions) {
    $d = $defs[$ed]
    $issText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $d.Iss))
    if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in $($d.Iss)" }
    $ver = $Matches[1]

    $pkg = Join-Path $stage "$($d.Name)-NoAdmin-$ver"
    if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
    $files = New-Object System.Collections.Generic.List[string]

    foreach ($year in $d.Bins.Keys) {
        $bin = Join-Path $binRoot $d.Bins[$year]
        $dll = Join-Path $bin "$($d.Asm).dll"
        if (-not (Test-Path $dll)) { throw "Missing $dll - build the $ed installer first." }
        $stamped = (Get-Item $dll).VersionInfo.FileVersion
        if ($stamped -ne $ver) { throw "$dll is $stamped but $($d.Iss) says $ver - rebuild the installer first." }

        $yearDir    = Join-Path $pkg "payload\$year"
        $payloadDir = Join-Path $yearDir $d.Subdir
        New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

        Copy-Item (Join-Path $bin "$($d.Asm).addin") $yearDir
        $files.Add("$year\$($d.Asm).addin")
        foreach ($f in Get-ChildItem $bin -File) {
            if ($f.Extension -eq ".addin") { continue }
            if ($f.Extension -notin ".dll", ".pdb", ".json", ".exe", ".config") { continue }
            Copy-Item $f.FullName $payloadDir
            $files.Add("$year\$($d.Subdir)$($f.Name)")
        }

        # KH-Tool's AI Bridge MCP server, in its KhMcp\ subfolder beside the add-in (net48; the
        # ribbon tooltip's setup line points here when there is no Program Files copy).
        $khMcp = Join-Path $bin "KhMcp"
        if (Test-Path $khMcp) {
            $khMcpDest = Join-Path $payloadDir "KhMcp"
            New-Item -ItemType Directory -Force -Path $khMcpDest | Out-Null
            foreach ($f in Get-ChildItem $khMcp -File) {
                Copy-Item $f.FullName $khMcpDest
                $files.Add("$year\$($d.Subdir)KhMcp\$($f.Name)")
            }
        }

        # Diagnostic collector beside the add-in: the ribbon's Diagnostic Report button resolves it
        # relative to the loaded assembly (same layout as the setup.exe and Install-Dev.ps1).
        if ($d.Protocol -eq 1) {
            $support = Join-Path $payloadDir "Support"
            New-Item -ItemType Directory -Force -Path $support | Out-Null
            foreach ($s in "Collect-Diagnostics.cmd", "Collect-ScheduleDiagnostics.ps1") {
                Copy-Item (Join-Path $PSScriptRoot $s) $support
                $files.Add("$year\$($d.Subdir)Support\$s")
            }
        }
    }

    Write-Crlf ([IO.File]::ReadAllText((Join-Path $tmplDir "Install.cmd")))   (Join-Path $pkg "Install.cmd")
    Write-Crlf ([IO.File]::ReadAllText((Join-Path $tmplDir "Uninstall.cmd"))) (Join-Path $pkg "Uninstall.cmd")
    Write-Crlf (($files -join "`n") + "`n") (Join-Path $pkg "files.txt")
    Write-Crlf (@(
        "@echo off"
        "REM Generated by install\Build-NoAdminPackages.ps1 - read by Install.cmd and Uninstall.cmd."
        "set `"PRODUCT=$($d.Product)`""
        "set `"VERSION=$ver`""
        "set `"TAB=$($d.Tab)`""
        "set `"MANIFEST=$($d.Asm).addin`""
        "set `"PAYLOAD_SUBDIR=$($d.Subdir)`""
        "set `"REGISTER_PROTOCOL=$($d.Protocol)`""
    ) -join "`n") (Join-Path $pkg "package.cmd")
    Write-Crlf (@(
        "$($d.Product) $ver - no-admin install"
        ""
        "1. Close Revit."
        "2. Extract this whole ZIP to a folder (do not run from inside the ZIP)."
        "3. Double-click Install.cmd."
        "4. Start Revit. The `"$($d.Tab)`" ribbon tab appears. If Revit asks, choose `"Always Load`"."
        ""
        "Revit versions in this package: $($d.Bins.Keys -join ', ') (only those installed on this PC are used)."
        "Installs for the current Windows user only, into %APPDATA%\Autodesk\Revit\Addins\<year>."
        "No administrator rights are needed. Other users on the same PC need to run it themselves."
        ""
        "To remove: close Revit, double-click Uninstall.cmd."
        ""
        "Differences from the setup.exe installer: no Start-menu shortcuts, so run notifications may"
        "not stay in Windows Notification Center after they disappear; and no copy of the scheduled-run"
        "launcher under Program Files. Scheduled runs use whichever launch route the add-in's"
        "self-test finds working when you save a schedule."
    ) -join "`n") (Join-Path $pkg "README.txt")

    $zip = Join-Path $outDir "$($d.Name)-NoAdmin-$ver.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Not Compress-Archive: on PowerShell 5.1 it stores entry names with backslashes, which
    # Explorer tolerates but other extractors turn into flat files named "payload\2026\x.dll".
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($f in Get-ChildItem $pkg -Recurse -File) {
            $entry = $f.FullName.Substring($pkg.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry)
        }
    }
    finally { $archive.Dispose() }
    Write-Host ("  {0,-40} {1,6:N0} KB  ({2} files)" -f (Split-Path $zip -Leaf), ((Get-Item $zip).Length / 1KB), $files.Count) -ForegroundColor Green
}

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "No-admin packages in $outDir" -ForegroundColor Green
