<#
.SYNOPSIS
  Builds the standalone DRW Helper (drawing-production ribbon) installer for Revit 2024 + 2026 + 2027.

  Runs the DRW Release builds (-p:RevitVersion=2024|2026|2027 -p:Edition=DRW), verifies the bin trees
  exist, then compiles install\DrwHelper-2026.iss with ISCC.exe into install\Output\
  (DrwHelper-Setup.exe; 2024 added in 0.4.0.0, the script keeps its old name). DRW Helper is built from the same source as the full add-in via the
  Edition flag; see install\Build-Installer.ps1 for the full KH Batch Script Runner (all years).

.PARAMETER ExpiryUtc
  Override Product.BuiltInExpiryUtc for this build (yyyy-MM-dd). Omit for a release: the date in
  Product.cs is the real one. Its purpose is producing an ALREADY-EXPIRED build to test the gate
  with, without committing a past date to source.

.EXAMPLE
  .\install\Build-Installer-Drw-2026.ps1

.EXAMPLE
  .\install\Build-Installer-Drw-2026.ps1 -ExpiryUtc 2026-08-01   # already expired, for testing

.NOTES
  Keep Product.Version (src\KhBatchRunner\Product.cs, EDITION_DRW branch) and AppVersion (DrwHelper-2026.iss) in step —
  currently 2.1.0.2. DRW compiles the expiry gate out (NO_EXPIRY_GATE) until it has its own update manifest.
  Requires Inno Setup 6 (ISCC.exe); pass -Iscc to override its path.
#>
param(
    [string]$Iscc = "",
    [ValidatePattern('^$|^\d{4}-\d{2}-\d{2}$')]
    [string]$ExpiryUtc = ""
)

$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\KhBatchRunner\KhBatchRunner.csproj"
$iss    = Join-Path $PSScriptRoot "DrwHelper-2026.iss"

# Version = AppVersion from the .iss, stamped onto the DLL (see Build-Installer.ps1 for the why).
$issText = Get-Content $iss -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in $iss" }
$ver = $Matches[1]
Write-Host "AppVersion (from .iss): $ver" -ForegroundColor Cyan

# Packaging is not allowed to reintroduce a forced Revit termination path.
& (Join-Path $PSScriptRoot "Verify-NoForcedRevitClose.ps1")

# Shipped support scripts must survive a managed PC (ConstrainedLanguage). This cannot be caught
# by testing here, where PowerShell is unrestricted - only by the lint.
& (Join-Path $PSScriptRoot "Verify-ConstrainedLanguageSafe.ps1")

# --- 1. Build the DRW Release trees (2024 net48, 2026 net8, 2027 net10) ---
$extraArgs = @()
if ($ExpiryUtc) {
    $extraArgs += "-p:ExpiryUtc=$ExpiryUtc"
    Write-Host "Expiry overridden for this build: $ExpiryUtc" -ForegroundColor Yellow
}
foreach ($v in @("2024", "2026", "2027")) {
    Write-Host "== dotnet build Release RevitVersion=$v Edition=DRW Version=$ver $extraArgs ==" -ForegroundColor Cyan
    dotnet build $csproj -c Release -p:RevitVersion=$v -p:Edition=DRW -p:Version=$ver @extraArgs --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed for RevitVersion=$v Edition=DRW" }

    $bin = Join-Path $root "src\KhBatchRunner\bin\Release\DRW\$v"
    foreach ($f in @("DrwHelper.dll", "DrwHelper.addin")) {
        if (-not (Test-Path (Join-Path $bin $f))) { throw "Missing $f in $bin" }
    }
    Write-Host "  ok: $bin"
}

# --- 2. Locate ISCC.exe ---
if (-not $Iscc) {
    foreach ($c in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $c) { $Iscc = $c; break }
    }
}
if (-not $Iscc -or -not (Test-Path $Iscc)) {
    throw "ISCC.exe (Inno Setup 6) not found. Install Inno Setup or pass -Iscc <path>."
}

# --- 3. Compile the DRW installer ---
Write-Host "== ISCC $iss ==" -ForegroundColor Cyan
& $Iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$out = Join-Path $PSScriptRoot "Output\DrwHelper-Setup.exe"
Write-Host ""
if (Test-Path $out) { Write-Host "Installer built: $out" -ForegroundColor Green }
else { Write-Warning "ISCC reported success but $out not found - check OutputDir in the .iss." }
