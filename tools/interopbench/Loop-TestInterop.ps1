<#
.SYNOPSIS
  Loop test: Batch Interop on a set of NON-workshared cloud models, from a cold cache, N times.
.DESCRIPTION
  Each iteration:
    1. refuses to start while any Revit is running (the probe launches its own);
    2. deletes ONLY this set's local cloud cache (CollaborationCache\<acct>\<project>\<model>*) and its
       entries in cloud-model-facts.json, so every open is a first open decided by a fresh Forma lookup;
    3. runs the supervised probe (one child Revit, crash -> plain retry -> skip) via Run-InteropSpike.ps1;
    4. checks: one OK row per model, no ERROR, every open tagged [plain open], no new Revit crash in the
       Windows Application log, and no Revit left running.
  -NegativeControl instead runs ONE model with KHBATCH_OPENPLAN=off (the old workset configuration):
  the expected result is a crash in the child, a plain retry, and an OK row - the protection itself.
  Never kills a process: a Revit still running after the run is reported and left for inspection.
#>
param(
    [int] $Iterations = 5,
    # A saved model set of NON-workshared cloud models, and the Model Checker checkset XML to run.
    # Both are yours to supply; nothing project-specific is defaulted here.
    [Parameter(Mandatory = $true)] [string] $ModelSet,
    [Parameter(Mandatory = $true)] [string] $Checkset,
    [string] $TestRoot = (Join-Path $env:USERPROFILE 'KhBatchTest'),
    [int] $TimeoutSec = 3600,
    [switch] $NegativeControl
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$presetDir = Join-Path $env:APPDATA 'KhBatchRunner\Presets'
$cacheRoot = Join-Path $env:LOCALAPPDATA 'Autodesk\Revit\Autodesk Revit 2026\CollaborationCache'
$factsPath = Join-Path $env:LOCALAPPDATA 'KhBatchRunner\cloud-model-facts.json'
$surface = Join-Path $env:LOCALAPPDATA 'KhBatchRunner\surface.log'

$presetFile = Get-ChildItem $presetDir -Filter *.json | Where-Object {
    (Get-Content $_.FullName -Raw | ConvertFrom-Json).Name -eq $ModelSet } | Select-Object -First 1
if (-not $presetFile) { throw "No model set named '$ModelSet' in $presetDir" }
$preset = Get-Content $presetFile.FullName -Raw | ConvertFrom-Json
$models = @($preset.Models | Where-Object { -not $_.IsLocal })
$tempPreset = $null

if ($NegativeControl) {
    # One model only: the control costs one deliberate crash.
    $one = $models[0]
    $tempPreset = Join-Path $presetDir 'zz-negative-control-delete-me.json'
    $neg = [ordered]@{ Id = [guid]::NewGuid().ToString('N'); Name = 'zz NegControl'; Models = @($one) }
    $neg | ConvertTo-Json -Depth 5 | Out-File $tempPreset -Encoding utf8
    $ModelSet = 'zz NegControl'
    $models = @($one)
    $Iterations = 1
    $env:KHBATCH_OPENPLAN = 'off'
}

function Clear-ColdCache {
    foreach ($m in $models) {
        Get-ChildItem $cacheRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
            $proj = Join-Path $_.FullName $m.ProjectGuid
            if (Test-Path $proj) {
                Get-ChildItem $proj -Force | Where-Object { $_.Name -like "$($m.ModelGuid)*" } |
                    Remove-Item -Recurse -Force -Confirm:$false
            }
        }
    }
    if (Test-Path $factsPath) {
        $facts = Get-Content $factsPath -Raw | ConvertFrom-Json
        $keep = [ordered]@{}
        foreach ($p in $facts.PSObject.Properties) {
            $mine = $models | Where-Object { $p.Name -eq "cloud:$($_.ProjectGuid):$($_.ModelGuid)" }
            if (-not $mine) { $keep[$p.Name] = $p.Value }
        }
        $keep | ConvertTo-Json -Depth 5 | Out-File $factsPath -Encoding utf8
    }
}

$results = @()
try {
    for ($i = 1; $i -le $Iterations; $i++) {
        if (Get-Process Revit -ErrorAction SilentlyContinue) {
            Write-Output "STOP: a Revit is running - close it before the loop test (iteration $i)."
            break
        }
        Clear-ColdCache
        $t0 = Get-Date
        $surfaceStart = if (Test-Path $surface) { (Get-Item $surface).Length } else { 0 }
        Write-Output "===== iteration $i of $Iterations  ($($models.Count) model(s), cold cache) ====="

        $log = & (Join-Path $here 'Run-InteropSpike.ps1') -Checkset "supervised:$ModelSet|$Checkset" `
            -TestRoot $TestRoot -TimeoutSec $TimeoutSec
        $wall = [int]((Get-Date) - $t0).TotalSeconds

        $rows = @($log | Where-Object { $_ -match '^\s{4}\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,' })
        $ok = @($rows | Where-Object { $_ -match ',OK,' }).Count
        $err = @($rows | Where-Object { $_ -match ',ERROR,' }).Count

        # Trail lines written during this iteration only.
        $trail = @()
        if (Test-Path $surface) {
            $fs = [IO.File]::Open($surface, 'Open', 'Read', 'ReadWrite'); $fs.Seek($surfaceStart, 'Begin') | Out-Null
            $sr = New-Object IO.StreamReader($fs); $trail = $sr.ReadToEnd() -split "`r?`n"; $sr.Close()
        }
        $opens = @($trail | Where-Object { $_ -match '\| open \((background|activated)' })
        $plain = @($opens | Where-Object { $_ -match '\[plain open: ' })
        $died = @($trail | Where-Object { $_ -match 'supervisor: pid \d+ died opening' })

        $crashes = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $t0; Id = 1000 } -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -match 'Revit\.exe' })
        # A Revit that has posted its normal close can take minutes to release a large session
        # (measured 2026-11-03: >2 min for the supervising parent). Wait for it rather than call it stuck.
        $exitDeadline = (Get-Date).AddSeconds(600)
        while ((Get-Process Revit -ErrorAction SilentlyContinue) -and (Get-Date) -lt $exitDeadline) { Start-Sleep -Seconds 10 }
        $left = @(Get-Process Revit -ErrorAction SilentlyContinue)

        $pass = if ($NegativeControl) {
            ($ok -eq $models.Count) -and ($died.Count -ge 1) -and ($left.Count -eq 0)
        } else {
            ($ok -eq $models.Count) -and ($err -eq 0) -and ($plain.Count -eq $opens.Count) -and
            ($crashes.Count -eq 0) -and ($left.Count -eq 0)
        }
        $results += [pscustomobject]@{
            Iter = $i; Pass = $pass; WallSec = $wall; OK = $ok; ERROR = $err; Opens = $opens.Count
            PlainOpens = $plain.Count; ChildDiedOpening = $died.Count; RevitCrashEvents = $crashes.Count
            RevitLeft = $left.Count
        }
        $log | Where-Object { $_ -match 'supervised probe|fact (before|after)|open crashes|ERROR' } | ForEach-Object { "  $_" }
        $died | ForEach-Object { "  $_" }
        if ($left.Count -gt 0) {
            Write-Output "STOP: Revit still running after iteration $i (pid $($left.Id -join ', ')) - left open for inspection."
            break
        }
    }
}
finally {
    if ($tempPreset -and (Test-Path $tempPreset)) { Remove-Item $tempPreset -Force -Confirm:$false }
    if ($NegativeControl) { Remove-Item Env:KHBATCH_OPENPLAN -ErrorAction SilentlyContinue }
}

Write-Output ''
$results | Format-Table -AutoSize | Out-String -Width 200
$failed = @($results | Where-Object { -not $_.Pass }).Count
Write-Output ("RESULT: {0} of {1} iteration(s) passed" -f ($results.Count - $failed), $results.Count)
