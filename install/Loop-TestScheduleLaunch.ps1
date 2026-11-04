<#
.SYNOPSIS
  Proves — repeatedly — that this PC can run a scheduled task that starts a program and waits for it.

.DESCRIPTION
  The scheduled-run failure that motivated this script looked like nothing at all: Task Scheduler
  reported "Last Run Result 0x1", Revit never appeared, and no log was written. Everything worked on
  the machine it was built on. The cause is machine POLICY, not the schedule, so the only way to see
  it is to run the real launch chain on the real machine.

  This registers a throwaway task whose action is the product's own KhBatchLauncher.exe, runs it on
  demand N times, and reports the exit code Windows recorded each time. It never touches an existing
  schedule, needs no admin rights, needs no Revit installed, and deletes every task it creates.

  Three targets are exercised, because telling them apart IS the diagnosis:
    Success  whoami.exe  -> expect 0x0
    Failure  reg.exe     -> expect 0x1   (proves 0x1 is the CHILD's exit code, not a launcher fault)
    Missing  a bad path  -> expect 0xFA2 (the launcher could not start the target at all)

  -IncludeLegacyVbs additionally runs the pre-1.3.4 wscript.exe launcher, which is what fails on a
  PC with Windows Script Host disabled. Use it to confirm the diagnosis before/after installing.

.EXAMPLE
  .\install\Loop-TestScheduleLaunch.ps1
.EXAMPLE
  .\install\Loop-TestScheduleLaunch.ps1 -Iterations 50 -IncludeLegacyVbs
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$Iterations = 20,

    # Defaults to the launcher next to this script's installed add-in, then to a dev build.
    [string]$Launcher = "",

    [switch]$IncludeLegacyVbs,

    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$system32 = Join-Path $env:SystemRoot 'System32'
$scratch = Join-Path $env:TEMP ("KhBatchLoopTest_" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null

$script:Failures = 0
function Note([string]$Text)  { Write-Host $Text }
function Good([string]$Text)  { Write-Host "  ok   $Text" -ForegroundColor Green }
function Bad ([string]$Text)  { Write-Host "  FAIL $Text" -ForegroundColor Red; $script:Failures++ }
function Head([string]$Text)  { Write-Host ""; Write-Host $Text -ForegroundColor Cyan }

# ---------------------------------------------------------------------------------------------
# 1. The environment facts that decide whether the OLD launcher could ever have worked here.
# ---------------------------------------------------------------------------------------------
Head "ENVIRONMENT  what this PC allows"
Note "  PC:   $env:COMPUTERNAME    user: $env:USERDOMAIN\$env:USERNAME"
# BuildNumber off the same CIM object rather than [Environment]::OSVersion: this script ships to
# the managed PCs it tests, where PowerShell runs in ConstrainedLanguage and that call is refused.
Note "  OS:   $((Get-CimInstance Win32_OperatingSystem).Caption) build $((Get-CimInstance Win32_OperatingSystem).BuildNumber)"

$wshDisabled = $false
foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows Script Host\Settings',
                 'HKCU:\SOFTWARE\Microsoft\Windows Script Host\Settings') {
    $enabled = $null
    if (Test-Path $key) {
        $prop = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -ne $prop -and $null -ne $prop.Enabled) { $enabled = [int]$prop.Enabled }
    }
    if ($null -eq $enabled) { Note "  WSH policy $key : not set (default = allowed)" }
    elseif ($enabled -eq 0) { Note "  WSH policy $key : Enabled=0  <-- SCRIPT HOST IS DISABLED"; $wshDisabled = $true }
    else                    { Note "  WSH policy $key : Enabled=$enabled" }
}

# Does wscript actually run a script? This is the single fact that explains a 0x1 with no log.
$probeVbs = Join-Path $scratch 'probe.vbs'
Set-Content -LiteralPath $probeVbs -Value 'WScript.Quit 0' -Encoding ASCII
$wscript = Join-Path $system32 'wscript.exe'
$wshWorks = $false
try {
    $p = Start-Process -FilePath $wscript -ArgumentList @('//B', '//NoLogo', "`"$probeVbs`"") -Wait -PassThru -WindowStyle Hidden
    $wshWorks = ($p.ExitCode -eq 0)
    Note "  wscript.exe trivial script -> exit $($p.ExitCode)"
} catch {
    Note "  wscript.exe trivial script -> could not start: $($_.Exception.Message)"
}
if (-not $wshWorks) {
    Write-Host "  => VBScript cannot run here. Any add-in build up to 1.3.4 registered its" -ForegroundColor Yellow
    Write-Host "     scheduled tasks as 'wscript.exe //B //NoLogo run_<id>.vbs', so they returned" -ForegroundColor Yellow
    Write-Host "     0x1 immediately, started no Revit, and wrote no log. That is this bug." -ForegroundColor Yellow
}

# ---------------------------------------------------------------------------------------------
# 2. Locate the launcher under test.
# ---------------------------------------------------------------------------------------------
if (-not $Launcher) {
    $candidates = @()
    foreach ($year in '2026', '2025', '2024') {
        $candidates += (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$year\KhBatchLauncher.exe")
        # And the per-user folder Install-Dev.ps1 writes, which the add-in prefers on a dev install
        # - testing a copy the add-in never runs proves nothing about the PC's real answer.
        $candidates += (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\KhBatchLauncher.exe")
    }
    $candidates += (Join-Path $root 'src\KhBatchLauncher\bin\Release\net48\KhBatchLauncher.exe')
    $candidates += (Join-Path $root 'src\KhBatchLauncher\bin\Debug\net48\KhBatchLauncher.exe')
    $Launcher = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $Launcher -or -not (Test-Path -LiteralPath $Launcher)) {
    throw "KhBatchLauncher.exe not found. Install the add-in, or pass -Launcher <path>."
}
Note "  launcher: $Launcher"

# ---------------------------------------------------------------------------------------------
# 3. One scheduled task, run on demand, measured.
# ---------------------------------------------------------------------------------------------
function New-LoopTask {
    param([string]$TaskName, [string]$Command, [string]$Arguments, [string]$WorkingDirectory = '')

    # A far-future TimeTrigger keeps the task from ever firing by itself; every run here is
    # explicit. Otherwise the settings mirror the product's own registration (see
    # TaskSchedulerGateway.BuildTaskXml) so this measures the real thing, not a simplification.
    # whoami, not WindowsIdentity: this script runs on managed PCs where ConstrainedLanguage
    # refuses .NET calls. "/user /nh /fo csv" prints "domain\user","S-1-5-21-...".
    $sid = ''
    foreach ($row in (& "$env:SystemRoot\System32\whoami.exe" /user /nh /fo csv 2>$null)) {
        if ($row -match '"(S-1-[0-9-]+)"') { $sid = $Matches[1]; break }
    }
    if ($sid -eq '') { throw "Could not determine this account's SID; cannot register a test task." }
    # XML-v9 carries the job id in the action's working directory (TaskSchedulerGateway.FireDirectory),
    # because a task can hand its program a folder but not an environment variable. Emitted only when
    # a target asks for one, so the launcher targets keep the exact XML they always had.
    $workingDirectoryElement = ''
    if ($WorkingDirectory -ne '') {
        $workingDirectoryElement = "`r`n      <WorkingDirectory>$WorkingDirectory</WorkingDirectory>"
    }
    $xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>KH Batch launcher loop test (safe to delete)</Description></RegistrationInfo>
  <Triggers><TimeTrigger><StartBoundary>2099-01-01T03:00:00</StartBoundary><Enabled>true</Enabled></TimeTrigger></Triggers>
  <Principals><Principal id="Author"><UserId>$sid</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <AllowHardTerminate>false</AllowHardTerminate>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>$Command</Command>
      <Arguments>$Arguments</Arguments>$workingDirectoryElement
    </Exec>
  </Actions>
</Task>
"@
    $xmlPath = Join-Path $scratch ($TaskName + '.xml')
    # Task Scheduler requires UTF-16 for XML declaring it. Set-Content -Encoding Unicode writes
    # UTF-16LE with a BOM, which is what schtasks wants, and is a cmdlet so it survives
    # ConstrainedLanguage where [IO.File]::WriteAllText does not.
    Set-Content -LiteralPath $xmlPath -Value $xml -Encoding Unicode
    $out = & schtasks.exe /Create /TN $TaskName /XML $xmlPath /F 2>&1
    if ($LASTEXITCODE -ne 0) { throw "schtasks /Create failed for $TaskName :`n$out" }
}

function Invoke-LoopTask {
    param([string]$TaskName)

    & schtasks.exe /Run /TN $TaskName | Out-Null
    if ($LASTEXITCODE -ne 0) { return @{ Result = $null; Note = 'schtasks /Run failed' } }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 300
        $info = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
        $state = (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue).State
    } while ($state -eq 'Running' -and (Get-Date) -lt $deadline)

    if ($state -eq 'Running') { return @{ Result = $null; Note = 'still running at timeout' } }
    return @{ Result = [int]$info.LastTaskResult; Note = '' }
}

function Test-Target {
    param([string]$Label, [string]$Command, [string]$Arguments, [int]$Expected, [string]$LogPath,
          [string]$WorkingDirectory = '')

    $taskName = 'KhBatchLoopTest_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $results = @{}
    $bad = 0
    try {
        New-LoopTask -TaskName $taskName -Command $Command -Arguments $Arguments -WorkingDirectory $WorkingDirectory
        for ($i = 1; $i -le $Iterations; $i++) {
            $r = Invoke-LoopTask -TaskName $taskName
            $key = if ($null -eq $r.Result) { $r.Note } else { '0x{0:X}' -f $r.Result }
            if ($results.ContainsKey($key)) { $results[$key]++ } else { $results[$key] = 1 }
            if ($null -eq $r.Result -or $r.Result -ne $Expected) { $bad++ }
            Write-Progress -Activity "$Label" -Status "run $i of $Iterations -> $key" -PercentComplete ($i * 100 / $Iterations)
        }
    } finally {
        & schtasks.exe /Delete /TN $taskName /F 2>&1 | Out-Null
        Write-Progress -Activity "$Label" -Completed
    }

    $summary = ($results.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) x$($_.Value)" }) -join ', '
    $expectedText = '0x{0:X}' -f $Expected
    if ($bad -eq 0) { Good "$Label -> $summary  (expected $expectedText)" }
    else            { Bad  "$Label -> $summary  (expected $expectedText every time; $bad run(s) differed)" }

    if ($LogPath -and (Test-Path -LiteralPath $LogPath)) {
        $last = Get-Content -LiteralPath $LogPath -Tail 2
        foreach ($line in $last) { Note "         log: $line" }
    } elseif ($LogPath) {
        Note "         log: NOT WRITTEN ($LogPath) - the launcher never ran"
    }
}

function Quote([string]$Value) { '"' + $Value + '"' }

Head "LOOP  $Iterations run(s) per target, through KhBatchLauncher.exe"

$logOk      = Join-Path $scratch 'ok.log'
$logFail    = Join-Path $scratch 'fail.log'
$logMissing = Join-Path $scratch 'missing.log'

Test-Target -Label 'child succeeds (whoami.exe)' -Command $Launcher -Expected 0 -LogPath $logOk `
    -Arguments ("--job looptest --owner Full --revit {0} --log {1}" -f (Quote (Join-Path $system32 'whoami.exe')), (Quote $logOk))

Test-Target -Label 'child fails    (reg.exe)   ' -Command $Launcher -Expected 1 -LogPath $logFail `
    -Arguments ("--job looptest --owner Full --revit {0} --log {1}" -f (Quote (Join-Path $system32 'reg.exe')), (Quote $logFail))

Test-Target -Label 'target missing             ' -Command $Launcher -Expected 4002 -LogPath $logMissing `
    -Arguments ("--job looptest --owner Full --revit {0} --log {1}" -f (Quote (Join-Path $system32 'no-such-revit.exe')), (Quote $logMissing))

# XML-v9, the action a PC that refuses the launcher gets instead of a console window: Task Scheduler
# starts Revit itself, and the ONLY thing naming the job is the action's working directory. There is
# nothing to run here that is not Revit, so what is proved is the channel - that a folder given to a
# task reaches the program it starts, unaltered. If this fails, a Revit-direct schedule starts Revit
# at 06:00 and it does nothing at all, which is the one failure worth catching before the night.
Head "XML-v9  the working directory that names the job"
$fireDir = Join-Path $scratch 'fire\looptestjob'
if (-not (Test-Path -LiteralPath $fireDir)) { New-Item -ItemType Directory -Force -Path $fireDir | Out-Null }
$logCwd = Join-Path $scratch 'cwd.log'
if (Test-Path -LiteralPath $logCwd) { Remove-Item -LiteralPath $logCwd -Force }
Test-Target -Label 'working dir reaches child  ' -Command (Join-Path $system32 'cmd.exe') -Expected 0 -LogPath $logCwd `
    -WorkingDirectory $fireDir -Arguments ('/c cd>{0}' -f (Quote $logCwd))

$sawCwd = ''
if (Test-Path -LiteralPath $logCwd) { $sawCwd = ((Get-Content -LiteralPath $logCwd -First 1) + '').Trim() }
if ($sawCwd -eq $fireDir) {
    Note "  working directory arrived intact: $sawCwd"
} else {
    Note "  WORKING DIRECTORY DID NOT ARRIVE - expected '$fireDir', child saw '$sawCwd'."
    Note "  A Revit-direct schedule cannot name its job on this PC; it needs the cmd.exe action."
}

if ($IncludeLegacyVbs) {
    Head "LEGACY  the pre-1.3.4 wscript launcher, for comparison"
    $legacy = Join-Path $scratch 'legacy.vbs'
    $legacyLog = Join-Path $scratch 'legacy.log'
    $vbs = @"
Option Explicit
On Error Resume Next
Dim shell, fso, stream, exitCode
Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
Set stream = fso.OpenTextFile("$legacyLog", 8, True)
stream.WriteLine "legacy launcher ran"
stream.Close
exitCode = shell.Run(Chr(34) & "$(Join-Path $system32 'whoami.exe')" & Chr(34), 1, True)
WScript.Quit exitCode
"@
    Set-Content -LiteralPath $legacy -Value $vbs -Encoding ASCII
    Test-Target -Label 'wscript.exe //B launcher   ' -Command $wscript -Expected 0 -LogPath $legacyLog `
        -Arguments ("//B //NoLogo {0}" -f (Quote $legacy))
    if (-not $wshWorks) {
        Note "         (a 0x1 here with no log is the original failure, reproduced)"
    }
}

Head "RESULT"
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
if ($script:Failures -eq 0) {
    Write-Host "All targets behaved as expected over $Iterations run(s) each." -ForegroundColor Green
    exit 0
}
Write-Host "$script:Failures target(s) did not behave as expected." -ForegroundColor Red
Write-Host "If the FIRST target failed, this PC cannot run the scheduled launcher at all -" -ForegroundColor Yellow
Write-Host "check antivirus/AppLocker rules on $Launcher, and send this output." -ForegroundColor Yellow
exit 1
