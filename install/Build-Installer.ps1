<#
.SYNOPSIS
  Builds the one combined KH Batch Script Runner installer covering Revit 2024/2025/2026/2027.

  Runs one Release build per year (-p:RevitVersion=2024|2025|2026|2027; 2027 needs the .NET 10 SDK), verifies each per-year bin tree
  exists, then compiles install\KhBatchRunner.iss with ISCC.exe into install\Output\.
  The single setup.exe installs the add-in only for the Revit versions detected on the target
  machine (see the .iss per-file RevitInstalled check; there is no component/version question).

.PARAMETER Unlimited
  Build the UNRESTRICTED variant (-p:Edition=FullUnlimited, ISCC /DUnlimited): the same product
  with the build-validity gate compiled out, for our own PC only. Same AppId and AddInId, so it
  replaces a gated install and is replaced by one; it differs only in the setup filename
  (KhBatchRunner-Setup-Unlimited.exe) and the Add/Remove name. Never hand this one out - anyone
  who is meant to keep updating must get the gated build.

.PARAMETER ExpiryUtc
  Override Product.BuiltInExpiryUtc for this build (yyyy-MM-dd). Omit for a release: the date in
  Product.cs is the real one. Its purpose is producing an ALREADY-EXPIRED build to test the gate
  with, without committing a past date to source.

.EXAMPLE
  .\install\Build-Installer.ps1

.EXAMPLE
  # An already-expired gated build, and the unrestricted build that ignores the same date.
  .\install\Build-Installer.ps1            -ExpiryUtc 2026-08-01
  .\install\Build-Installer.ps1 -Unlimited -ExpiryUtc 2026-08-01

.NOTES
  Keep Product.Version (src\KhBatchRunner\Product.cs) and AppVersion (KhBatchRunner.iss) in
  step - currently 2.1.0.2 - and set Product.BuiltInExpiryUtc to 90 days from the build date.
  Requires Inno Setup 6 (ISCC.exe); pass -Iscc to override its path.
#>
param(
    [string]$Iscc = "",
    [ValidateSet("2024", "2025", "2026", "2027")]
    [string[]]$Versions = @("2024", "2025", "2026", "2027"),
    [switch]$Unlimited,
    [ValidatePattern('^$|^\d{4}-\d{2}-\d{2}$')]
    [string]$ExpiryUtc = ""
)

$ErrorActionPreference = "Stop"
$root    = Split-Path -Parent $PSScriptRoot
$csproj  = Join-Path $root "src\KhBatchRunner\KhBatchRunner.csproj"
$iss     = Join-Path $PSScriptRoot "KhBatchRunner.iss"

# Single source of truth for the version: parse AppVersion out of the .iss and stamp the DLL to
# match (-p:Version below). Without this the assembly is FileVersion 1.0.0.0, so there's no way to
# verify on disk which build is installed (and Verify-Install.ps1 can't check it).
$issText = Get-Content $iss -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in $iss" }
$ver = $Matches[1]
Write-Host "AppVersion (from .iss): $ver" -ForegroundColor Cyan

# Packaging is not allowed to reintroduce a forced Revit termination path.
& (Join-Path $PSScriptRoot "Verify-NoForcedRevitClose.ps1")

# Shipped support scripts must survive a managed PC (ConstrainedLanguage). This cannot be caught
# by testing here, where PowerShell is unrestricted - only by the lint.
& (Join-Path $PSScriptRoot "Verify-ConstrainedLanguageSafe.ps1")

# The repository is public: no client, project or site names may ship in it.
& (Join-Path $PSScriptRoot "Verify-NoClientNames.ps1")

# --- 1. Build each per-year Release tree ---
# The unrestricted variant is the same source under a different Edition, so it lands in its own
# bin\Release\FullUnlimited\<year> tree and can never be confused with the gated artifacts.
$binRoot  = Join-Path $root "src\KhBatchRunner\bin\Release"
$extraArgs = @()
if ($Unlimited) {
    $extraArgs += "-p:Edition=FullUnlimited"
    $binRoot = Join-Path $binRoot "FullUnlimited"
    Write-Host "UNRESTRICTED build: expiry gate compiled out (internal use only)" -ForegroundColor Yellow
}
if ($ExpiryUtc) {
    $extraArgs += "-p:ExpiryUtc=$ExpiryUtc"
    Write-Host "Expiry overridden for this build: $ExpiryUtc" -ForegroundColor Yellow
}

foreach ($v in $Versions) {
    Write-Host "== dotnet build Release RevitVersion=$v Version=$ver $extraArgs ==" -ForegroundColor Cyan
    dotnet build $csproj -c Release -p:RevitVersion=$v -p:Version=$ver @extraArgs --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed for RevitVersion=$v" }

    $bin = Join-Path $binRoot $v
    foreach ($f in @("KhBatchRunner.dll", "KhBatchRunner.addin", "KhMcp\KhMcp.exe")) {
        if (-not (Test-Path (Join-Path $bin $f))) { throw "Missing $f in $bin" }
    }
    # The AI Bridge's MCP server: every tool schema must parse, or Claude Code drops the server.
    & (Join-Path $bin "KhMcp\KhMcp.exe") --schemas
    if ($LASTEXITCODE -ne 0) { throw "KhMcp.exe --schemas failed for $bin" }
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

# --- 3. Compile the combined installer ---
# /DUnlimited switches the .iss to the FullUnlimited bin tree, its own setup filename and its own
# Add/Remove name. Everything else (AppId, files, registry) is identical by design.
$isccArgs = @()
if ($Unlimited) { $isccArgs += "/DUnlimited" }
Write-Host "== ISCC $iss $isccArgs ==" -ForegroundColor Cyan
& $Iscc @isccArgs $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$outName = if ($Unlimited) { "KhBatchRunner-Setup-Unlimited.exe" } else { "KhBatchRunner-Setup.exe" }
$out = Join-Path $PSScriptRoot "Output\$outName"
Write-Host ""
if (Test-Path $out) { Write-Host "Installer built: $out" -ForegroundColor Green }
else { Write-Warning "ISCC reported success but $out not found - check OutputDir in the .iss." }
