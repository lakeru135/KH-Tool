<#
.SYNOPSIS
  Runs a REAL scheduled batch — Task Scheduler starting Revit, opening models, writing a log —
  over and over, and reports what happened each time.

.DESCRIPTION
  Loop-TestScheduleLaunch.ps1 proves the launch CHAIN works using stubs in seconds. This proves the
  whole thing: launcher -> Revit -> add-in claims KHBATCH_JOB -> models processed -> Revit closes
  -> Task Scheduler records a result. It is slow (a Revit start per iteration) and it takes over the
  desktop, which is exactly why it is a separate script.

  Each iteration reports three independent facts, because any one of them can lie on its own:
    * the task's Last Run Result   - what Windows saw
    * the launcher log             - whether Revit was started, and what it returned
    * the tracking CSV row count   - whether models were actually processed

  A run that "succeeds" with zero CSV rows is the failure this exists to catch: Revit started and
  closed without the add-in ever picking up the job.

  Registers its own throwaway schedule, runs it ON DEMAND (the time trigger is parked far in the
  future so it can never fire on its own), and deletes it afterwards. Existing schedules are never
  touched.

.EXAMPLE
  .\install\Loop-TestScheduledRun.ps1 -Iterations 3 -ModelCount 2
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 50)]
    [int]$Iterations = 3,

    [ValidateRange(1, 50)]
    [int]$ModelCount = 2,

    [ValidateSet('2024', '2025', '2026')]
    [string]$RevitVersion = '2026',

    [ValidateRange(2, 120)]
    [int]$TimeoutMinutes = 20,

    [string]$ModelDirectory = (Join-Path $env:USERPROFILE 'KhBatchTest\models')
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$sim = Join-Path $root 'tools\LivenessSim\LivenessSim.csproj'
$revitExe = "C:\Program Files\Autodesk\Revit $RevitVersion\Revit.exe"

function Head([string]$Text) { Write-Host ""; Write-Host $Text -ForegroundColor Cyan }
function Good([string]$Text) { Write-Host "  ok   $Text" -ForegroundColor Green }
function Bad ([string]$Text) { Write-Host "  FAIL $Text" -ForegroundColor Red }

if (-not (Test-Path -LiteralPath $revitExe)) { throw "Revit not found: $revitExe" }
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw "Revit is already running. Close it first - a scheduled run must start its own instance."
}

$models = Get-ChildItem -LiteralPath $ModelDirectory -Filter 'RT*.rvt' -ErrorAction SilentlyContinue |
    Sort-Object Name | Select-Object -First $ModelCount
if ($models.Count -lt $ModelCount) { throw "Need $ModelCount RT*.rvt models in $ModelDirectory; found $($models.Count)." }

Head "SETUP  registering a throwaway schedule"
$id = 'looprun_' + (Get-Date).ToString('yyyyMMdd_HHmmss')
$createArgs = @('--live-schedule-create', '--id', $id, '--name', "Loop test $id",
                '--owner', 'Full', '--revit', $revitExe, '--delay', '600')
foreach ($m in $models) { $createArgs += @('--model', $m.FullName) }

$created = & dotnet run --project $sim -c Release --no-build -- @createArgs 2>&1
$created | Where-Object { $_ -match '^(LIVE_|Scheduled task)' } | ForEach-Object { Write-Host "  $_" }
if ($LASTEXITCODE -ne 0) { $created | ForEach-Object { Write-Host $_ }; throw "Could not register the test schedule." }

$taskName = ($created | Where-Object { $_ -match '^LIVE_TASK=' }) -replace '^LIVE_TASK=', ''
$destination = ($created | Where-Object { $_ -match '^LIVE_DESTINATION=' }) -replace '^LIVE_DESTINATION=', ''
$logDir = Join-Path $destination 'Log'
$launcherLog = Join-Path $env:APPDATA "KhBatchRunner\Scheduled\launcher_$id.log"
$marker = Join-Path $env:APPDATA "KhBatchRunner\Scheduled\launcher_$id.marker"

if (Test-Path -LiteralPath $marker) {
    $gen = (Get-Content -LiteralPath $marker -TotalCount 1)
    Write-Host "  registration: $gen" -ForegroundColor Green
} else {
    Write-Host "  registration: no marker file - this is a pre-XML-v7 build" -ForegroundColor Yellow
}

$results = @()
try {
    for ($i = 1; $i -le $Iterations; $i++) {
        Head "RUN $i of $Iterations"
        $before = @(Get-ChildItem -LiteralPath $logDir -Filter '*.csv' -ErrorAction SilentlyContinue).Count
        if (Test-Path -LiteralPath $launcherLog) { $logMark = (Get-Item -LiteralPath $launcherLog).Length } else { $logMark = 0 }

        $started = Get-Date
        & schtasks.exe /Run /TN $taskName | Out-Null
        if ($LASTEXITCODE -ne 0) { Bad "schtasks /Run failed"; continue }

        $deadline = $started.AddMinutes($TimeoutMinutes)
        do {
            Start-Sleep -Seconds 5
            $state = (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue).State
            $elapsed = [int]((Get-Date) - $started).TotalSeconds
            Write-Progress -Activity "Run $i of $Iterations" -Status "$state - ${elapsed}s elapsed" `
                -PercentComplete ([Math]::Min(99, $elapsed * 100 / ($TimeoutMinutes * 60)))
        } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)
        Write-Progress -Activity "Run $i of $Iterations" -Completed

        $seconds = [int]((Get-Date) - $started).TotalSeconds
        $timedOut = ($state -eq 'Running')
        $info = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        $code = if ($null -eq $info) { $null } else { [int]$info.LastTaskResult }

        # What the launcher itself recorded for THIS iteration (everything appended since the mark).
        $launcherLines = @()
        if (Test-Path -LiteralPath $launcherLog) {
            $all = Get-Content -LiteralPath $launcherLog
            $launcherLines = $all | Select-Object -Last 3
        }

        # Did any model actually get processed? Newest tracking CSV, data rows only.
        $rows = 0; $okRows = 0; $csvName = '(none)'
        $csv = Get-ChildItem -LiteralPath $logDir -Filter '*.csv' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $csv) {
            $csvName = $csv.Name
            $data = @(Import-Csv -LiteralPath $csv.FullName)
            $rows = $data.Count
            $okRows = @($data | Where-Object { $_.Status -eq 'OK' }).Count
        }

        $codeText = if ($null -eq $code) { 'unknown' } else { '0x{0:X}' -f $code }
        $line = "run $i : result=$codeText  ${seconds}s  csv=$csvName rows=$rows ok=$okRows"
        if ($timedOut)            { Bad "$line  - TIMED OUT, task still running" }
        elseif ($code -ne 0)      { Bad "$line  - task did not report success" }
        elseif ($rows -eq 0)      { Bad "$line  - Revit ran but processed nothing" }
        elseif ($okRows -lt $ModelCount) { Bad "$line  - fewer OK rows than models" }
        else                      { Good $line }
        foreach ($l in $launcherLines) { Write-Host "         $l" -ForegroundColor DarkGray }

        $results += [pscustomobject]@{
            Run = $i; Result = $codeText; Seconds = $seconds; Rows = $rows; Ok = $okRows
            Passed = (-not $timedOut -and $code -eq 0 -and $okRows -ge $ModelCount)
        }

        # A wedged Revit would poison every later iteration; stop rather than report noise.
        if (Get-Process Revit -ErrorAction SilentlyContinue) {
            Bad "Revit is still running after the task finished - stopping the loop for inspection"
            break
        }
    }
} finally {
    Head "CLEANUP"
    & dotnet run --project $sim -c Release --no-build -- --live-schedule-cleanup --id $id 2>&1 |
        Where-Object { $_ -match 'deleted|removed|Scheduled task' } | ForEach-Object { Write-Host "  $_" }
}

Head "RESULT"
$results | Format-Table -AutoSize
$failed = @($results | Where-Object { -not $_.Passed }).Count
if ($failed -eq 0 -and $results.Count -eq $Iterations) {
    Write-Host "$Iterations of $Iterations scheduled runs completed end to end." -ForegroundColor Green
    exit 0
}
Write-Host "$failed of $($results.Count) run(s) did not complete. Evidence:" -ForegroundColor Red
Write-Host "  launcher log : $launcherLog"
Write-Host "  tracking logs: $logDir"
Write-Host "  surface log  : $env:LOCALAPPDATA\KhBatchRunner\surface.log"
exit 1
