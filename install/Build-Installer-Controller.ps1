<#
.SYNOPSIS
  Builds the installer for the KH Batch Controller (the standalone desktop app).

  Publishes SELF-CONTAINED (the .NET 8 runtime travels with the app, so there is no separate
  runtime prerequisite on a locked-down machine), then compiles install\KhBatchController.iss
  with ISCC.exe into install\Output\KhBatchController-Setup.exe.

  The result is a per-user install: it lands in %LOCALAPPDATA%\Programs\KhBatchController and
  needs no administrator. See the .iss header for why.

.EXAMPLE
  .\install\Build-Installer-Controller.ps1

.NOTES
  Requires Inno Setup 6 (ISCC.exe); pass -Iscc to override its path.

  SIGNING. An installer stops SmartScreen calling the app "unrecognised publisher" only once it is
  Authenticode-signed. If IT enforces allowlisting (AppLocker/WDAC), packaging alone will not get
  past it — a certificate will. With one available:
      signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /a <file>
  applied to KhBatchController.exe BEFORE compiling, and to the finished Setup.exe after.
#>
param(
    [string]$Iscc = "",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root   = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $root "src\KhBatchController\KhBatchController.csproj"
$iss    = Join-Path $PSScriptRoot "KhBatchController.iss"
$publish = Join-Path $root "src\KhBatchController\bin\Release\publish"

# Single source of truth for the version, as in the add-in installers: parse it out of the .iss
# and stamp the assembly to match, so the installed build is identifiable on disk.
$issText = Get-Content $iss -Raw
if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in $iss" }
$ver = $Matches[1]
Write-Host "AppVersion (from .iss): $ver" -ForegroundColor Cyan

# The controller ships the same supervisor sources, so apply the same no-forced-Revit-close gate.
& (Join-Path $PSScriptRoot "Verify-NoForcedRevitClose.ps1")

# Shipped support scripts must survive a managed PC (ConstrainedLanguage). This cannot be caught
# by testing here, where PowerShell is unrestricted - only by the lint.
& (Join-Path $PSScriptRoot "Verify-ConstrainedLanguageSafe.ps1")

# The app locks its own exe while running, and a stale publish tree would ship silently.
Get-Process -Name KhBatchController -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

Write-Host "== dotnet publish (self-contained, win-x64) ==" -ForegroundColor Cyan
dotnet publish $csproj -c $Configuration -r win-x64 --self-contained true `
    -p:Version=$ver -p:PublishSingleFile=false -o $publish --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $publish "KhBatchController.exe"
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
$size = [math]::Round((Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "  published $size MB (runtime included)" -ForegroundColor Green

# --- Locate ISCC.exe ---
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

Write-Host "== ISCC $iss ==" -ForegroundColor Cyan
& $Iscc $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$out = Join-Path $PSScriptRoot "Output\KhBatchController-Setup.exe"
Write-Host ""
if (Test-Path $out) {
    $mb = [math]::Round((Get-Item $out).Length / 1MB, 2)
    Write-Host "Installer built: $out ($mb MB)" -ForegroundColor Green
    Write-Host "Installs per-user into %LOCALAPPDATA%\Programs\KhBatchController - no admin needed."
} else {
    Write-Warning "ISCC reported success but $out not found - check OutputDir in the .iss."
}
