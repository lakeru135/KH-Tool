<#
.SYNOPSIS
  Read-only verification that an installed build is CLEAN and at the expected version - the
  scripted acceptance goal for the installer-upgrade fix.

  Checks, per product:
    1. Exactly one uninstall entry across the 64-bit and 32-bit (WOW6432Node) registry views,
       with DisplayVersion == expected (a leftover 32-bit-view entry = the stale-duplicate bug).
    2. For each relevant Revit year, %ProgramData%\...\Addins\<year>\<base>.dll FileVersion ==
       expected (the machine install is actually the new build).
    3. NO per-user %AppData% shadow copy of <base>.addin in ANY profile / relevant year (a shadow
       copy with the same AddInId makes Revit load the wrong one - the "didn't upgrade" cause).

  Prints PASS/FAIL per check and exits non-zero if anything failed.

.PARAMETER Product
  KhBatchRunner (years 2024/2025/2026).

.PARAMETER Version
  Expected version (3-part, e.g. 0.6.1). Defaults to the AppVersion parsed from the product .iss.

.EXAMPLE
  .\install\Verify-Install.ps1
#>
param(
    [ValidateSet("KhBatchRunner")]
    [string]$Product = "KhBatchRunner",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"

# --- Per-product config ---
if ($true) {
    $appId = "{7E2C4D8A-3F51-4B9C-9E1D-2A6F0B4C8D31}"
    $base  = "KhBatchRunner"
    $iss   = Join-Path $PSScriptRoot "KhBatchRunner.iss"
    # Only years whose Revit is actually installed carry files.
    $years = @("2024", "2025", "2026") | Where-Object {
        Test-Path "$env:ProgramFiles\Autodesk\Revit $_"
    }
}

if (-not $Version) {
    $issText = Get-Content $iss -Raw
    if ($issText -notmatch '#define\s+AppVersion\s+"([^"]+)"') { throw "AppVersion not found in $iss" }
    $Version = $Matches[1]
}
# Normalize a version to its first 3 parts for comparison (FileVersion is "0.6.1.0").
function Norm3([string]$v) { ($v -split '\.')[0..2] -join '.' }
$expected3 = Norm3 $Version

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $tag = if ($ok) { "PASS" } else { "FAIL" }
    $col = if ($ok) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1} - {2}" -f $tag, $name, $detail) -ForegroundColor $col
}

Write-Host "== Verify $Product @ $Version ==" -ForegroundColor Cyan
Write-Host "Relevant Revit years: $($years -join ', ')"
Write-Host ""

# --- 1. Registry: exactly one entry (64-bit view), correct version, no 32-bit-view leftover ---
$key64 = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$key32 = "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$v64 = if (Test-Path $key64) { (Get-ItemProperty $key64).DisplayVersion } else { $null }
$v32 = if (Test-Path $key32) { (Get-ItemProperty $key32).DisplayVersion } else { $null }

Check "registry: 64-bit-view entry at expected version" `
    ($v64 -and (Norm3 $v64) -eq $expected3) `
    ("64-bit view DisplayVersion = " + $(if ($v64) { $v64 } else { "(absent)" }))
Check "registry: no stale 32-bit-view (WOW6432Node) entry" `
    ($null -eq $v32) `
    ("32-bit view DisplayVersion = " + $(if ($v32) { $v32 } else { "(absent)" }))

# --- 2. ProgramData DLL FileVersion per year ---
foreach ($y in $years) {
    $dll = "$env:ProgramData\Autodesk\Revit\Addins\$y\$base.dll"
    if (Test-Path $dll) {
        $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion
        Check "machine DLL $y at expected version" ((Norm3 $fv) -eq $expected3) "$dll FileVersion = $fv"
    } else {
        Check "machine DLL $y present" $false "$dll not found"
    }
}

# --- 3. No per-user shadow copies across all profiles ---
$shadows = @()
foreach ($profile in (Get-ChildItem "$env:SystemDrive\Users" -Directory -ErrorAction SilentlyContinue)) {
    foreach ($y in $years) {
        $addin = Join-Path $profile.FullName "AppData\Roaming\Autodesk\Revit\Addins\$y\$base.addin"
        if (Test-Path $addin) { $shadows += $addin }
    }
}
Check "no per-user %AppData% shadow copies" ($shadows.Count -eq 0) `
    $(if ($shadows.Count -eq 0) { "none found" } else { ($shadows -join '; ') })

Write-Host ""
$failed = @($results | Where-Object { -not $_.Ok })
if ($failed.Count -eq 0) {
    Write-Host "ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host ("$($failed.Count) CHECK(S) FAILED") -ForegroundColor Red
    exit 1
}
