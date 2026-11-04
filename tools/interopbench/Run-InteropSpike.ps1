<#
.SYNOPSIS
  Launches Revit 2026 with the Batch Interop self-test armed and waits for it to finish.
.DESCRIPTION
  -Checkset is passed straight to KHBATCH_INTEROP_SPIKE, so it takes the SelfTest modes:
    profile:<xml>   one model (-Model, default Bench-Arch-01) - whole checkset x3 + per-check table
    engine:<xml>    the real production path over -Models (';'-separated); 8 models = the bench run
    <xml>           the original Part A/B spike on TestA/TestB
  The self-test closes Revit itself when done; the log lands in <TestRoot>\interopspike\.
  Only a dev build that knows KHBATCH_SELFTEST_V9 arms, so an older installed DRW Helper stays out.
#>
param(
    [Parameter(Mandatory)] [string] $Checkset,
    [string] $TestRoot = (Join-Path $env:USERPROFILE 'KhBatchTest'),
    [string] $Model,
    [string] $Models,
    [int] $TimeoutSec = 1800,
    [string] $RevitExe = 'C:\Program Files\Autodesk\Revit 2026\Revit.exe'
)

$logDir = Join-Path $TestRoot 'interopspike'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("spike-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

$env:KHBATCH_SELFTEST_V9 = $log
$env:KHBATCH_INTEROP_SPIKE = $Checkset
$env:KHBATCH_TEST_ROOT = $TestRoot
if ($Model)  { $env:KHBATCH_INTEROP_MODEL = $Model }
if ($Models) { $env:KHBATCH_INTEROP_MODELS = $Models }

$p = Start-Process -FilePath $RevitExe -ArgumentList '/language ENU' -PassThru
Write-Output "Revit pid $($p.Id); log $log"

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline -and -not $p.HasExited) {
    if ((Test-Path $log) -and (Select-String -Path $log -SimpleMatch '=== Interop spike complete ===' -Quiet)) { break }
    Start-Sleep -Seconds 10
}
if (-not $p.HasExited) {
    # The self-test asks Revit to close after the end marker; give it a moment to do so.
    if (-not $p.WaitForExit(120000)) { Write-Output "Revit still running after the run (pid $($p.Id)) - left open." }
}
if (Test-Path $log) { Get-Content $log } else { Write-Output "No log written: $log" }
