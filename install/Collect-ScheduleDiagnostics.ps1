<#
.SYNOPSIS
  Collects everything needed to explain what a scheduled run did, into one ZIP on the Desktop.

.DESCRIPTION
  Run it with no arguments. It finds this product's scheduled tasks itself (any task named
  KhBatchRunner_<32-char job id>, which is what the Full edition registers), collects
  the launch chain, task status/XML/history, job JSON, launcher log, run manifests, tracking CSVs,
  surface/broker/controller logs and recent Revit journals, and writes a plain-English REPORT.txt
  at the top of the bundle saying how far the run got.

  Read-only: outside its own output folder it copies and queries, never starts, modifies or deletes
  a scheduled task. Run it while signed in as the Windows user that owns the schedule.

  Task history (the Windows event log) is the one part that usually needs administrator rights.
  Everything else works unelevated, and REPORT.txt says which mode it ran in.

  MUST RUN UNDER CONSTRAINED LANGUAGE. The PCs this exists to diagnose are managed ones, and a
  managed PC puts PowerShell in ConstrainedLanguage mode — where .NET method calls, New-Object on
  most types, [pscustomobject], Add-Type and even Compress-Archive are all refused. Version 1.3.5
  used all of those and died at PARAMETER BINDING on the Desktop lookup below, before a single line
  ran: one error, no bundle, nothing to send. The tool that explains the problem was disabled by
  the problem. So: cmdlets and core types only (string, int, datetime, hashtable, array), external
  programs where a cmdlet will not do, and tar.exe for the ZIP. install\Verify-ConstrainedLanguage-
  Safe.ps1 fails the build if that slips.

.EXAMPLE
  # The normal way — double-click Collect-Diagnostics.cmd, or:
  .\install\Collect-ScheduleDiagnostics.ps1

.EXAMPLE
  # The last three days instead of just today:
  .\install\Collect-ScheduleDiagnostics.ps1 -Days 3

.EXAMPLE
  # Everything, regardless of date - the pre-1.3.8.5 behaviour:
  .\install\Collect-ScheduleDiagnostics.ps1 -All

.EXAMPLE
  # Only one job:
  .\install\Collect-ScheduleDiagnostics.ps1 -TaskName KhBatchRunner_0123456789abcdef0123456789abcdef
#>
[CmdletBinding()]
param(
    # Optional. Omit it and every KhBatchRunner_* task on this PC is collected.
    [ValidatePattern('^(KhBatchRunner_[0-9a-fA-F]{32})?$')]
    [string]$TaskName = '',

    # Empty = "the Desktop", resolved below. NOT defaulted here: a parameter default is evaluated at
    # binding time, so a blocked call here kills the script before any of its own error handling
    # exists — which is exactly how 1.3.5 failed with one line of output and no bundle.
    [string]$OutputDirectory = '',

    # How far back to collect. 1 = today (since midnight), 2 = today and yesterday, and so on.
    # TODAY BY DEFAULT since 1.3.8.5: a PC with twenty saved schedules made the old seven-day
    # collection slow enough that the user stopped running it, and a report nobody runs explains
    # nothing. -All restores the previous breadth.
    [ValidateRange(1, 30)]
    [int]$Days = 1,

    # Collect regardless of date - every task history, every log, the whole surface.log.
    [switch]$All,

    # Don't open Explorer on the finished bundle (for unattended/support-scripted use).
    [switch]$NoOpen
)

$ErrorActionPreference = 'Stop'

# The real Desktop, which is often redirected into OneDrive — so the registry, not a built path.
# Get-ItemPropertyValue is a cmdlet and survives ConstrainedLanguage; [Environment]::GetFolderPath
# does not.
if ($OutputDirectory -eq '') {
    $desktop = ''
    try {
        $desktop = Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders' -Name 'Desktop' -ErrorAction Stop
    } catch { }
    if ($desktop -eq '' -or $null -eq $desktop) { $desktop = Join-Path $env:USERPROFILE 'Desktop' }
    $OutputDirectory = Join-Path $desktop 'KhBatchRunner-Diagnostics'
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmm'
# Deliberately short names throughout. A Desktop redirected into OneDrive, plus a per-task
# subfolder, plus a file name is enough to cross the 260-character path limit, and the collection
# then loses exactly the files a support case needs.
$bundle = Join-Path $OutputDirectory ("{0}_{1}" -f $env:COMPUTERNAME, $stamp)
New-Item -ItemType Directory -Path $bundle -Force | Out-Null

# The collection window. Everything dated - task history, job files, tracking CSVs, run manifests,
# Revit journals, and surface.log's own lines - is filtered against this one value, so a bundle is
# either "today's run" or "everything" and never an unstated mixture of the two.
# DateTime is a core type, so .Date/.AddDays survive ConstrainedLanguage.
if ($All) {
    $since = (Get-Date '1900-01-01')
    $scopeText = 'everything on this PC, regardless of date (-All)'
} else {
    $since = (Get-Date).Date.AddDays(-($Days - 1))
    if ($Days -eq 1) { $scopeText = "TODAY only (since $($since.ToString('yyyy-MM-dd')) 00:00) - re-run with -Days N or -All for more" }
    else { $scopeText = "the last $Days days (since $($since.ToString('yyyy-MM-dd')) 00:00)" }
}
$sinceDates = @()
for ($d = 0; $d -lt 40; $d++) {
    $day = $since.AddDays($d)
    if ($day -gt (Get-Date)) { break }
    $sinceDates += $day.ToString('yyyy-MM-dd')
}

$scheduled   = Join-Path $env:APPDATA 'KhBatchRunner\Scheduled'
$localRunner = Join-Path $env:LOCALAPPDATA 'KhBatchRunner'

# Revit gives each session a PRIVATE temp directory (%TEMP%\<guid>\), and the broker/controller
# logs land in whichever %TEMP% their process had. Collect from every plausible root rather than
# trusting this shell's: a log looked for in the wrong directory reads as "never written", which
# has already cost this project several rounds of chasing the wrong root cause.
$userTemp = ''
try { $userTemp = Get-ItemPropertyValue -Path 'HKCU:\Environment' -Name 'TEMP' -ErrorAction Stop } catch { }

$tempRoots = @()
foreach ($candidate in @($env:TEMP, $userTemp, (Join-Path $env:LOCALAPPDATA 'Temp'))) {
    if ($null -eq $candidate -or $candidate -eq '') { continue }
    # The registry copy is stored unexpanded (%USERPROFILE%\...). Only these two tokens are ever
    # used in a real TEMP value, and a string -replace is allowed where ExpandEnvironmentVariables
    # is not.
    $expanded = $candidate -replace '%USERPROFILE%', $env:USERPROFILE -replace '%LOCALAPPDATA%', $env:LOCALAPPDATA
    if ($tempRoots -notcontains $expanded) { $tempRoots += $expanded }
}

# REPORT.txt is built up as the collection runs, so the headline facts come from the same reads the
# evidence files do rather than from a second, possibly disagreeing, pass. A plain array, appended
# with +=: a generic List cannot even be constructed under ConstrainedLanguage.
$report = @()
function Say([string]$Line) { $script:report += $Line }

# Never fatal: one unreadable source (an event log needing admin, a path over the length limit)
# must not cost the operator every other file in the bundle.
function Write-TextFile([string]$Name, [scriptblock]$Producer) {
    $path = Join-Path $bundle $Name
    try {
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        & $Producer 2>&1 | Out-File -LiteralPath $path -Width 4096 -Encoding utf8
    } catch {
        $message = "Collection failed: $($_.Exception.Message)"
        try { $message | Out-File -LiteralPath $path -Encoding utf8 } catch { Write-Warning "$Name : $message" }
    }
}

function Copy-Evidence([string]$Path, [string]$RelativeName) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $destination = Join-Path $bundle $RelativeName
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    try { Copy-Item -LiteralPath $Path -Destination $destination -Force } catch {}
}

# Files written INSIDE the collection window, newest first, capped at $Count as a size backstop.
# The cap used to be the only rule, and that is how this collector threw away the two Revit journals
# it was run to explain: five parallel Revits started afterwards had newer ones. Window first, cap
# second.
function Copy-RecentFiles([string]$Path, [string]$RelativeDirectory, [string]$Filter, [int]$Count = 10) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Get-ChildItem -LiteralPath $Path -File -Filter $Filter -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTime -ge $since } |
        Sort-Object LastWriteTime -Descending | Select-Object -First $Count |
        ForEach-Object { Copy-Evidence $_.FullName (Join-Path $RelativeDirectory $_.Name) }
}

# A detail CSV, capped. These are per-DAY append files and one of them reached tens of megabytes on
# a Type Mark run, so the whole file cannot go in the bundle - but the header and the first rows are
# what a matrix question actually needs (are the columns the ones the pivot expects, and do the rows
# carry the values). A truncated file says so in a final line, so nobody reads a cut file as a short
# one. $All takes the file whole.
function Copy-CsvHead([string]$Path, [string]$RelativeName, [int]$Lines = 3000) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if ($All) { Copy-Evidence $Path $RelativeName; return }
    $destination = Join-Path $bundle $RelativeName
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    try {
        $total = @(Get-Content -LiteralPath $Path -ErrorAction Stop).Count
        $head = @(Get-Content -LiteralPath $Path -TotalCount $Lines -ErrorAction Stop)
        if ($total -gt $head.Count) {
            $head += ('--- TRUNCATED by the diagnostic collector: ' + $head.Count + ' of ' + $total +
                      ' line(s). Re-run with -All for the whole file. ---')
        }
        Set-Content -LiteralPath $destination -Value $head -Encoding UTF8
    } catch {}
}

# surface.log is the add-in's whole history and reaches several MB. Its lines begin 'yyyy-MM-dd', so
# the window can be applied to the CONTENT: today's run stays readable and the bundle stays small.
# Lines that carry no date (a wrapped PowerShell error, say) follow the line above them, so a kept
# line keeps its continuation.
function Copy-LogWindow([string]$Path, [string]$RelativeName) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $destination = Join-Path $bundle $RelativeName
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    if ($All) { Copy-Evidence $Path $RelativeName; return }
    try {
        $keep = @()
        $keeping = $false
        foreach ($line in (Get-Content -LiteralPath $Path -ErrorAction Stop)) {
            $stampOnLine = ''
            if ($line.Length -ge 10) { $stampOnLine = $line.Substring(0, 10) }
            if ($stampOnLine -match '^\d{4}-\d{2}-\d{2}$') { $keeping = ($sinceDates -contains $stampOnLine) }
            if ($keeping) { $keep += $line }
        }
        $header = "(TRIMMED by Collect-ScheduleDiagnostics to: $scopeText. Run it with -All for the whole file.)"
        @($header, '') + $keep | Out-File -LiteralPath $destination -Width 4096 -Encoding utf8
    } catch {
        Copy-Evidence $Path $RelativeName
    }
}

# whoami rather than WindowsPrincipal: the .NET route needs New-Object on a non-core type. The token
# carries the High Mandatory Level SID (S-1-16-12288) only when the process is actually elevated,
# which is the question being asked — not "is this account an administrator".
function Test-Elevated {
    try {
        $groups = & "$env:SystemRoot\System32\whoami.exe" /groups 2>$null
        foreach ($line in $groups) { if ($line -match 'S-1-16-12288') { return $true } }
        return $false
    } catch { return $false }
}

# Both editions register their tasks as KhBatchRunner_<job id> (TaskSchedulerGateway), so one
# prefix finds every schedule. schtasks is used rather than Get-ScheduledTask because
# it exists on every Windows this add-in supports.
function Get-ProductTaskNames {
    $names = @()
    try {
        foreach ($line in (& schtasks.exe /Query /FO CSV /NH 2>$null)) {
            if ($line -match '^"([^"]+)"') {
                $leaf = Split-Path -Leaf $Matches[1]
                if ($leaf -match '^KhBatchRunner_[0-9a-fA-F]{32}$' -and $names -notcontains $leaf) {
                    $names += $leaf
                }
            }
        }
    } catch {}
    return $names
}

$elevated = Test-Elevated

Say "KH Batch Script Runner - diagnostic report"
Say "==========================================="
Say ""
Say "PC:        $env:COMPUTERNAME"
Say "User:      $env:USERDOMAIN\$env:USERNAME"
Say "Collected: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') local / $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss'))Z"
try { Say "Timezone:  $((Get-TimeZone).Id)" } catch {}
Say "SCOPE:     $scopeText"
if ($elevated) { Say "Rights:    administrator (task history included)" }
else { Say "Rights:    standard user (Windows task history may be missing - re-run as administrator for it)" }
Say ""

# --- Which tasks to collect -------------------------------------------------------------------
if ($TaskName) {
    $tasks = @($TaskName)
    Say "Scope:     one task requested on the command line."
} else {
    $tasks = @(Get-ProductTaskNames)
    if ($tasks.Count -eq 0) {
        Say "Scope:     NO KhBatchRunner_* scheduled task exists on this PC for this user."
        Say "           If a schedule was saved, either it was saved by a different Windows user,"
        Say "           or Save never got as far as registering the task. The rest of this bundle"
        Say "           still shows the install, the launcher self-test and the last run's logs."
    } else {
        Say "Scope:     $($tasks.Count) scheduled task(s) found automatically."
    }
}
Say ""

# --- The launch chain (machine-wide) ------------------------------------------------------------
# A 0x1 with no launcher log means the task's action never got as far as running; on a PC with
# Windows Script Host disabled that is exactly what the pre-1.3.4 wscript launcher did, so these
# facts are the first ones worth having.
# EVERY copy is tested, not just the first one found. The same executable is refused from
# %ProgramData% and permitted from %ProgramFiles% under the default Software Restriction Policy /
# AppLocker rules, so "does a launcher exist" and "will this PC run it" have different answers per
# folder — and only the second question decides whether a schedule can be saved.
$launcherPaths = @(Join-Path $env:ProgramFiles 'Kanghun BIM\KhBatchLauncher.exe')
foreach ($year in '2024', '2025', '2026') {
    $launcherPaths += (Join-Path $env:ProgramData "Autodesk\Revit\Addins\$year\KhBatchLauncher.exe")
    # The PER-USER add-ins folder too. Install-Dev.ps1 deploys there, and TaskSchedulerGateway's
    # OrderedLauncherPaths tries an assembly-adjacent copy FIRST - so on a dev install this is the
    # copy the add-in actually runs, and it was the one copy this report never tested. A collector
    # that says "not installed anywhere it was looked for" while the add-in is failing on a copy it
    # never tried sends the reader looking in the wrong place.
    $launcherPaths += (Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\KhBatchLauncher.exe")
}

$selfTestLog = Join-Path $env:TEMP 'khbatch-diagnostic-selftest.log'
$probe = Join-Path $env:SystemRoot 'System32\whoami.exe'
# Hashtables, not [pscustomobject]: casting to PSCustomObject is refused under ConstrainedLanguage
# ("ConversionSupportedOnlyToCoreTypes"). A hashtable indexes the same way for what is needed here.
$launcherResults = @()
foreach ($path in $launcherPaths) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $exit = $null; $detail = ''
    try {
        Remove-Item -LiteralPath $selfTestLog -ErrorAction SilentlyContinue
        $p = Start-Process -FilePath $path -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList @('--self-test', "`"$probe`"", '--log', "`"$selfTestLog`"")
        $exit = $p.ExitCode
    } catch { $detail = $_.Exception.Message }
    $launcherResults += @{ Path = $path; Exit = $exit; Detail = $detail }
}
$workingLauncher = $launcherResults | Where-Object { $_.Exit -eq 0 } | Select-Object -First 1
$launcherExe = $launcherResults | Select-Object -First 1

Write-TextFile '00-launch-chain.txt' {
    'Launcher copies and whether this PC will RUN each one (0 = yes):'
    if ($launcherResults.Count -eq 0) { '  KhBatchLauncher.exe is not installed anywhere it was looked for' }
    foreach ($r in $launcherResults) {
        "  $($r.Path)"
        Get-Item -LiteralPath $r.Path | Select-Object Length, LastWriteTime,
            @{ Name = 'FileVersion'; Expression = { $_.VersionInfo.FileVersion } } | Format-List
        "    self-test exit: $($r.Exit) $($r.Detail)"
        ''
    }
    'Not-installed candidates:'
    foreach ($path in $launcherPaths) { if (-not (Test-Path -LiteralPath $path)) { "  $path" } }
    ''
    'Software Restriction Policy / AppLocker (why a copy can be refused by folder):'
    foreach ($key in 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers',
                     'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2') {
        if (Test-Path $key) { "  $key"; Get-ItemProperty -Path $key | Format-List }
        else { "  $key : not set" }
    }
    ''
    'Windows Script Host policy (only relevant to XML-v6 and earlier registrations):'
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows Script Host\Settings',
                     'HKCU:\SOFTWARE\Microsoft\Windows Script Host\Settings') {
        if (Test-Path $key) { "  $key"; Get-ItemProperty -Path $key | Select-Object Enabled, TrustPolicy | Format-List }
        else { "  $key : not set (default = allowed)" }
    }
    ''
    'Last self-test log:'
    if (Test-Path -LiteralPath $selfTestLog) { Get-Content -LiteralPath $selfTestLog }
}

if ($launcherResults.Count -eq 0) {
    Say "LAUNCH CHAIN: KhBatchLauncher.exe is NOT installed - no schedule on this PC can start Revit."
} elseif ($null -ne $workingLauncher) {
    Say "LAUNCH CHAIN: OK - this PC runs $($workingLauncher.Path)"
    if ($launcherResults.Count -gt 1) {
        Say "              ($($launcherResults.Count) copies installed; the first that runs is the one used.)"
    }
} else {
    Say "LAUNCH CHAIN: BLOCKED - every installed copy was refused:"
    foreach ($r in $launcherResults) { Say "    $($r.Path) -> exit $($r.Exit) $($r.Detail)" }
    Say "              'blocked by group policy' is a Software Restriction Policy / AppLocker rule,"
    Say "              not a fault in the add-in: the default rules permit Program Files and refuse"
    Say "              ProgramData. Install 1.3.4.3+ (it adds a Program Files copy) or have IT allow"
    Say "              KhBatchLauncher.exe by path or publisher."
}

# --- Machine facts ------------------------------------------------------------------------------
Write-TextFile '01-machine-time-and-region.txt' {
    "Collected local: $((Get-Date).ToString('o'))"
    "Collected UTC:   $((Get-Date).ToUniversalTime().ToString('o'))"
    "Windows user:    $env:USERDOMAIN\$env:USERNAME"
    "Windows session: $env:SESSIONNAME"
    "Elevated:        $elevated"
    ''
    Get-TimeZone | Format-List *
    ''
    'tzutil /g:'
    & tzutil.exe /g
    ''
    'Culture:'
    Get-Culture | Format-List *
    ''
    'UICulture:'
    Get-UICulture | Format-List *
    ''
    if (Get-Command Get-WinSystemLocale -ErrorAction SilentlyContinue) {
        'System locale:'
        Get-WinSystemLocale | Format-List *
    }
}

# --- Notifications ------------------------------------------------------------------------------
# "I get no notifications" has four completely different causes and they need different fixes:
#   1. the run never asked for one          -> a product bug; surface.log shows no attempt
#   2. PowerShell is locked down            -> the preferred (WinRT) path CANNOT work on this PC;
#                                              the add-in falls back to a tray balloon instead
#   3. Windows refused to accept it         -> the app's toast identity is disabled or unregistered
#   4. Windows accepted it and hid it       -> Do Not Disturb / Focus assist: it IS in Notification
#                                              Center, it just never flashed on screen
# Reading settings alone cannot tell these apart, so this SENDS A REAL TOAST and then asks Windows
# whether it kept it. A kept toast the operator never saw is case 4 and nothing else.
#
# Case 2 is why this section exists at all. The add-in raises its preferred notification from a
# Windows PowerShell child process, and an AppLocker/SRP-managed PC puts PowerShell into
# ConstrainedLanguage mode, where the WinRT types below cannot be loaded — so every notification
# failed silently for several versions. THIS SCRIPT RUNS UNDER THE SAME CONSTRAINT, which is what
# makes the probe honest: if the types will not load here, they did not load for the add-in either.
$toastIds = @('Kanghun.KhBatchRunner', 'Kanghun.KhBatchController')

$languageMode = "$($ExecutionContext.SessionState.LanguageMode)"
$lockdownPolicy = $env:__PSLockdownPolicy
$srpConfigured = Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers'
$appLockerConfigured = Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2'
$constrained = ($languageMode -ne 'FullLanguage')

# Under ConstrainedLanguage the answer is already known and the test is not merely pointless but
# harmful: every line below is itself forbidden, so attempting it risks taking the collector down
# with it. Skipped by decision, not by exception — and the verdict states the finding rather than
# hiding it behind "could not test".
#
# Otherwise: load the WinRT types ONCE, on their own, so a failure is attributable. Folded into the
# per-identity loop it would read as "every identity is broken" when the truth is "this PC will not
# load the API at all" — a different problem with a different fix.
$winrtError = ''
if ($constrained) {
    $winrtError = "PowerShell is in $languageMode mode; the Windows notification API cannot be loaded here."
} else {
    try {
        [void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.UI.Notifications.ToastNotification, Windows.UI.Notifications, ContentType = WindowsRuntime]
        [void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime]
    } catch {
        $winrtError = $_.Exception.Message
    }
}

$notifySettings = 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings'
$globalToasts = $null
$pushToasts = $null
try { $globalToasts = (Get-ItemProperty -Path $notifySettings -ErrorAction Stop).NOC_GLOBAL_SETTING_TOASTS_ENABLED } catch { }
try { $pushToasts = (Get-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications' -ErrorAction Stop).ToastEnabled } catch { }

# A retained toast needs a Start-menu shortcut carrying the AppUserModelID; the installers create
# one per Revit year. Without it Windows may show a banner and then drop the item from history.
$shortcutIds = @()
foreach ($programs in @($env:ProgramData + '\Microsoft\Windows\Start Menu\Programs\Kanghun BIM',
                        $env:AppData + '\Microsoft\Windows\Start Menu\Programs\Kanghun BIM')) {
    if (-not (Test-Path -LiteralPath $programs)) { continue }
    foreach ($lnk in (Get-ChildItem -LiteralPath $programs -Filter *.lnk -ErrorAction SilentlyContinue)) {
        $shortcutIds += $lnk.Name
    }
}

$probe = @()
foreach ($appId in $toastIds) {
    $enabled = $null
    try { $enabled = (Get-ItemProperty -Path (Join-Path $notifySettings $appId) -ErrorAction Stop).Enabled } catch { }
    $kept = 'not tested'
    if ($winrtError) {
        $probe += @{ AppId = $appId; Enabled = $enabled; Retained = 'SKIPPED - WinRT unavailable' }
        continue
    }
    # clm-safe-ignore-begin: unreachable unless $constrained is false, i.e. the language mode has
    # already been checked and this API is known to be callable. Probing the blocked API IS the test.
    try {
        $tag = 'diag' + (Get-Random -Maximum 99999)
        $doc = [Windows.Data.Xml.Dom.XmlDocument]::new()
        $doc.LoadXml('<toast scenario="reminder" duration="long"><visual><binding template="ToastGeneric">' +
                     '<text>KH Batch diagnostic test</text>' +
                     '<text>If you can see this, notifications work on this PC. It removes itself in 5 minutes.</text>' +
                     '</binding></visual><actions><action activationType="system" arguments="dismiss" content="Dismiss"/></actions></toast>')
        $n = [Windows.UI.Notifications.ToastNotification]::new($doc)
        $n.Tag = $tag
        $n.Group = 'KhBatchDiag'
        $n.ExpirationTime = [DateTimeOffset]::Now.AddMinutes(5)
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($n)
        Start-Sleep -Milliseconds 900
        $history = @([Windows.UI.Notifications.ToastNotificationManager]::History.GetHistory($appId))
        $kept = 'NO - Windows did not keep it'
        foreach ($item in $history) { if ($item.Tag -eq $tag) { $kept = 'YES'; break } }
    } catch {
        $kept = 'ERROR - ' + $_.Exception.Message
    }
    # clm-safe-ignore-end
    $probe += @{ AppId = $appId; Enabled = $enabled; Retained = $kept }
}

Write-TextFile '02-notifications.txt' {
    'Scripting-host lockdown (decides whether the PREFERRED notification path can work at all):'
    "  PowerShell LanguageMode = $languageMode   (anything but FullLanguage blocks the WinRT API)"
    "  __PSLockdownPolicy      = $lockdownPolicy"
    "  SRP configured          = $srpConfigured"
    "  AppLocker configured    = $appLockerConfigured"
    if ($winrtError) { "  WinRT load ERROR        = $winrtError" } else { '  WinRT load              = OK' }
    ''
    'Global switches (HKCU):'
    "  NOC_GLOBAL_SETTING_TOASTS_ENABLED = $globalToasts   (0 = all notifications off)"
    "  PushNotifications\ToastEnabled    = $pushToasts     (0 = all notifications off)"
    ''
    'Per-identity state and live delivery probe:'
    $probe | Format-Table -AutoSize | Out-String
    ''
    'Start-menu shortcuts found (these carry the AppUserModelID a retained toast needs):'
    if ($shortcutIds.Count -eq 0) { '  (none)' } else { $shortcutIds | ForEach-Object { '  ' + $_ } }
    ''
    'Raw notification settings:'
    try { Get-ChildItem -Path $notifySettings -ErrorAction Stop | Select-Object -ExpandProperty PSChildName } catch { "  unreadable: $($_.Exception.Message)" }
}

$retainedAny = @($probe | Where-Object { $_.Retained -eq 'YES' }).Count
if ($winrtError -or $constrained) {
    Say "NOTIFICATIONS: POWERSHELL LOCKED DOWN - this PC will not let the Windows notification API"
    Say "               load (LanguageMode $languageMode), so the preferred notification cannot work"
    Say "               here and never could. THIS IS THE ANSWER to 'I never get notifications'."
    if ($winrtError) { Say "               Error: $winrtError" }
    Say "               1.3.4.6+ falls back to a notification that needs none of this: a card in the"
    Say "               bottom-right corner that WAITS until you click or dismiss it, plus a tray"
    Say "               icon. Clicking either opens that run's log. It is not kept in Notification"
    Say "               Center - that is the cost of the fallback, not a second fault."
    Say "               Check runner\surface.log for 'delivered via tray balloon' to confirm it ran."
    Say "               Same root cause as a blocked scheduled launcher: AppLocker/SRP on this PC"
    Say "               (SRP configured: $srpConfigured, AppLocker configured: $appLockerConfigured)."
} elseif (($globalToasts -eq 0) -or ($pushToasts -eq 0)) {
    Say "NOTIFICATIONS: OFF - Windows notifications are disabled for this user account."
    Say "               Settings > System > Notifications, turn Notifications on."
} elseif ($retainedAny -eq 0) {
    Say "NOTIFICATIONS: BLOCKED - Windows accepted no test toast under any of the product identities."
    Say "               See 02-notifications.txt for the per-identity error. Usually the add-in was"
    Say "               installed without its Start-menu shortcut, or notifications are turned off"
    Say "               for this app specifically."
} else {
    Say "NOTIFICATIONS: OK - Windows kept a test toast under $retainedAny of $($toastIds.Count) identities."
    Say "               A test toast was just sent. IF NOTHING APPEARED ON SCREEN just now but this"
    Say "               line says OK, the toast went straight to Notification Center without a"
    Say "               banner - that is Do Not Disturb / Focus assist, not a fault in the add-in."
    Say "               Open Notification Center (Win+N) to confirm it is sitting there."
}

$logScheme = $null
foreach ($root in @('HKLM:\Software\Classes\khbatchlog\shell\open\command',
                    'HKCU:\Software\Classes\khbatchlog\shell\open\command')) {
    try { $logScheme = (Get-ItemProperty -Path $root -ErrorAction Stop).'(default)' } catch { }
    if ($logScheme) { break }
}
if ($logScheme) {
    Say "NOTIFY LOG LINK: registered -> $logScheme"
} else {
    Say "NOTIFY LOG LINK: NOT registered - failure notifications will still appear, but without the"
    Say "                 'Open log' button (a toast cannot link to a file: path; Windows drops it)."
}

# --- Per task -----------------------------------------------------------------------------------
$jobFolders = @()

# Task Scheduler's event log is read ONCE, here, and then filtered in memory per task. It used to be
# read inside the per-task loop, so a PC with twenty saved schedules made twenty full passes over
# the same log with a regex over every message - by a wide margin the slowest thing this script did,
# and the reason the collection was described as taking too long to bother with.
$taskEvents = @()
$taskEventError = ''
try {
    $taskEvents = @(Get-WinEvent -FilterHashtable @{
        LogName   = 'Microsoft-Windows-TaskScheduler/Operational'
        StartTime = $since
    } -ErrorAction Stop | Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message)
} catch {
    $taskEventError = $_.Exception.Message
}

foreach ($task in $tasks) {
    $jobId = $task.Substring('KhBatchRunner_'.Length)
    # Folder named by job id alone (the "KhBatchRunner_" prefix is the same for every task and
    # costs 15 characters of a 260-character budget); REPORT.txt carries the full task name.
    $rel = Join-Path 'tasks' $jobId

    Write-TextFile (Join-Path $rel '02-status.txt') {
        & schtasks.exe /Query /TN $task /V /FO LIST
        if (Get-Command Get-ScheduledTask -ErrorAction SilentlyContinue) {
            ''
            'PowerShell task definition:'
            Get-ScheduledTask -TaskName $task | Format-List *
            ''
            'PowerShell task result:'
            Get-ScheduledTaskInfo -TaskName $task | Format-List *
        }
    }
    Write-TextFile (Join-Path $rel '03-task.xml') { & schtasks.exe /Query /TN $task /XML }
    Write-TextFile (Join-Path $rel '04-history.txt') {
        "Window: $scopeText"
        ''
        if ($taskEventError -like '*No events were found*') {
            # Not a failure and not a permissions problem: the log simply holds nothing in this
            # window. Reported as its own case because calling it "could not be read" sent at least
            # one reader looking for administrator rights they did not need.
            'No Task Scheduler events at all in this window.'
            'Either nothing ran in it, or the Operational log is turned off on this PC'
            '(Event Viewer > Applications and Services > Microsoft > Windows > TaskScheduler).'
        } elseif ($taskEventError -ne '') {
            "Task Scheduler history could not be read: $taskEventError"
            '(Windows only lets an administrator read this log - re-run as administrator for it.)'
        } else {
            # -like, not -match + [regex]::Escape: a static call on a non-core type is refused under
            # ConstrainedLanguage, which is the mode the PCs this script exists for run in.
            $mine = @($taskEvents | Where-Object { $_.Message -like "*$task*" })
            if ($mine.Count -eq 0) { "No events for this task in the window." }
            else { $mine | Format-List }
        }
    }

    $jobJson     = Join-Path $scheduled ($jobId + '.json')
    $vbsLauncher = Join-Path $scheduled ('run_' + $jobId + '.vbs')          # XML-v6 and earlier
    $marker      = Join-Path $scheduled ('launcher_' + $jobId + '.marker')  # XML-v7 and later
    $launcherLog = Join-Path $scheduled ('launcher_' + $jobId + '.log')

    Copy-Evidence $jobJson     (Join-Path $rel (Split-Path -Leaf $jobJson))
    Copy-Evidence $vbsLauncher (Join-Path $rel (Split-Path -Leaf $vbsLauncher))
    Copy-Evidence $marker      (Join-Path $rel (Split-Path -Leaf $marker))
    Copy-Evidence $launcherLog (Join-Path $rel (Split-Path -Leaf $launcherLog))

    # Headline facts for this task, straight into REPORT.txt.
    Say ""
    Say "TASK $task"
    $generation = 'unknown - no launcher artefact found (this job may never have been registered)'
    if (Test-Path -LiteralPath $marker) { $generation = 'XML-v7+ (KhBatchLauncher.exe)' }
    elseif (Test-Path -LiteralPath $vbsLauncher) { $generation = 'XML-v6 or earlier (wscript .vbs launcher)' }
    Say "  registration: $generation"

    # WHICH CHAIN THIS PC IS ON. Since XML-v9 the action is not always the launcher: a PC whose
    # policy refuses it gets Revit.exe directly, or cmd.exe where even that is impossible. Reading
    # this line first saves the next half hour - "the launcher never logged" is expected, not a
    # fault, on a job whose action was never the launcher.
    if (Test-Path -LiteralPath $marker) {
        $actionLine = @(Get-Content -LiteralPath $marker) | Where-Object { $_ -like 'action=*' } | Select-Object -First 1
        if ($actionLine) {
            $kind = $actionLine.Substring(7)
            $what = 'unrecognised'
            if ($kind -eq 'launcher')     { $what = 'KhBatchLauncher.exe (preferred; logs before anything can fail)' }
            elseif ($kind -eq 'revit-direct') { $what = 'Revit.exe directly - the launcher was refused by this PC. No launcher log is written and none is missing; the job is named by the task working directory (firedir= below).' }
            elseif ($kind -eq 'cmd-shell')    { $what = 'cmd.exe - both the launcher and a direct Revit start were refused. A console window is visible for the whole run.' }
            Say "  task action:  $kind - $what"
        } else {
            Say "  task action:  not recorded (marker written before XML-v8)"
        }
    }

    $job = $null
    try { if (Test-Path -LiteralPath $jobJson) { $job = Get-Content -LiteralPath $jobJson -Raw | ConvertFrom-Json } } catch {}
    if ($null -eq $job) {
        Say "  job file:     MISSING ($jobJson) - the task exists but the add-in has nothing to run."
    } else {
        Say "  job name:     $($job.Name)"
        if ($job.PSObject.Properties.Name -contains 'Time') {
            # Recurrence serialises as the enum's ordinal (ScheduleRecurrence { Once, Daily, Weekly }).
            $recurrence = "$($job.Recurrence)"
            if ($recurrence -eq '0') { $recurrence = 'Once' }
            elseif ($recurrence -eq '1') { $recurrence = 'Daily' }
            elseif ($recurrence -eq '2') { $recurrence = 'Weekly' }
            Say "  scheduled:    $recurrence at $($job.Time)"
        }
        if (-not [string]::IsNullOrWhiteSpace($job.DestinationFolder)) {
            Say "  logs to:      $($job.DestinationFolder)\Log"
            if ($jobFolders -notcontains $job.DestinationFolder) { $jobFolders += $job.DestinationFolder }
        }
        if ($job.PSObject.Properties.Name -contains 'LastRunAt' -and $job.LastRunAt) {
            Say "  add-in last ran: $($job.LastRunAt)"
        }
    }

    try {
        $info = Get-ScheduledTaskInfo -TaskName $task -ErrorAction Stop
        $code = $info.LastTaskResult
        $hex = '0x{0:X}' -f $code
        $meaning = "see 02-task-status.txt"
        if ($code -eq 0) { $meaning = 'the run completed and Revit exited normally' }
        elseif ($code -eq 1) { $meaning = "the CHILD's exit code - Revit ran and returned 1" }
        elseif ($code -eq 0xFA1) { $meaning = 'launcher: bad arguments' }
        elseif ($code -eq 0xFA2) { $meaning = 'launcher: could not start Revit' }
        elseif ($code -eq 267009) { $meaning = 'currently running' }
        elseif ($code -eq 267011) { $meaning = 'has not run yet' }
        Say "  last result:  $hex ($meaning)"
        Say "  last run:     $($info.LastRunTime)"
        Say "  next run:     $($info.NextRunTime)"
    } catch {
        Say "  last result:  could not be read ($($_.Exception.Message))"
    }

    if (Test-Path -LiteralPath $launcherLog) {
        Say "  launcher log: $((Get-Item -LiteralPath $launcherLog).LastWriteTime) - last lines:"
        foreach ($line in @(Get-Content -LiteralPath $launcherLog -Tail 6 -ErrorAction SilentlyContinue)) {
            Say "      $line"
        }
    } else {
        # Only XML-v7+ registrations write this log, and only when they actually run. Saying
        # "Revit never started" without checking the marker date would misread a task whose last
        # run simply predates the upgrade - the same missing-log-is-not-absence trap as before.
        Say "  launcher log: none."
        if (Test-Path -LiteralPath $marker) {
            Say "                Launcher registered $((Get-Item -LiteralPath $marker).LastWriteTime); if that is AFTER"
            Say "                the last run above, the task simply has not run since. If it is BEFORE, the task's"
            Say "                action never ran - which is what a blocked launch looks like."
        }
    }
}

# --- Everything the job left behind, regardless of task ------------------------------------------
Copy-LogWindow (Join-Path $localRunner 'surface.log')      'runner\surface.log'
Copy-LogWindow (Join-Path $localRunner 'surface.prev.log') 'runner\surface.prev.log'
# Copy-Evidence overwrites, so these are walked OLDEST first and the newest session's log is the
# copy that survives. Revit's per-session directories are searched one level down as well.
foreach ($root in $tempRoots) {
    foreach ($name in 'broker.log', 'controller.log') {
        Copy-Evidence (Join-Path $root (Join-Path 'KhBatchRunner' $name)) "runner\$name"
    }
    Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 8 |
        ForEach-Object {
            foreach ($name in 'broker.log', 'controller.log') {
                Copy-Evidence (Join-Path $_.FullName (Join-Path 'KhBatchRunner' $name)) "runner\$name"
            }
        }
}
Copy-RecentFiles $scheduled 'scheduled' '*.json' 20
Copy-RecentFiles (Join-Path $scheduled 'runs') 'scheduled\runs' '*.json' 10
# Registration evidence: a definition Task Scheduler REFUSED (task_<id>.rejected.xml, kept only on
# failure) and the launcher preflight's own log. These are the two artefacts that explain a schedule
# that will not save at all, as opposed to one that saves and then misbehaves.
Copy-RecentFiles $scheduled 'scheduled' '*.xml' 10
Copy-Evidence (Join-Path $scheduled 'launcher_selftest.log') 'scheduled\launcher_selftest.log'

# A refused task definition is copied regardless of the window: it is tiny, it is rare, and
# REPORT.txt names it by file name below - a verdict pointing at a file the bundle does not contain
# is worse than no verdict.
Get-ChildItem -LiteralPath $scheduled -File -Filter '*.rejected.xml' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 3 |
    ForEach-Object { Copy-Evidence $_.FullName (Join-Path 'scheduled' $_.Name) }

foreach ($folder in $jobFolders) { Copy-RecentFiles (Join-Path $folder 'Log') 'tracking-logs' '*.csv' 20 }

# The tracking log says a task RAN. It cannot say what the task produced - and a report of
# "the matrix has no Workset sheet" could not be answered from a bundle that held neither the
# detail CSV the workbook is pivoted from nor the workbook itself. Both live one level down, in
# <destination>\<Task>\, and neither was ever collected.
#
# A detail CSV can be enormous (one model produced 99,126 Type Mark rows), so it is TRUNCATED
# rather than skipped: the header plus the first lines is what answers "are the rows the shape the
# matrix expects", and that is the whole question being asked of it.
foreach ($folder in $jobFolders) {
    if ([string]::IsNullOrWhiteSpace($folder)) { continue }
    if (-not (Test-Path -LiteralPath $folder)) { continue }
    foreach ($taskDir in @(Get-ChildItem -LiteralPath $folder -Directory -ErrorAction SilentlyContinue)) {
        if ($taskDir.Name -eq 'Log') { continue }
        $rel = Join-Path 'task-output' $taskDir.Name
        Copy-RecentFiles $taskDir.FullName $rel '*.xlsx' 4
        foreach ($csv in @(Get-ChildItem -LiteralPath $taskDir.FullName -File -Filter '*.csv' -ErrorAction SilentlyContinue |
                           Where-Object { $_.LastWriteTime -ge $since } |
                           Sort-Object LastWriteTime -Descending | Select-Object -First 3)) {
            Copy-CsvHead $csv.FullName (Join-Path $rel $csv.Name)
        }
    }
}

# Revit journals are the best evidence when Revit itself exits before the add-in writes surface.log.
$revitRoot = Join-Path $env:LOCALAPPDATA 'Autodesk\Revit'
if (Test-Path -LiteralPath $revitRoot) {
    # 15, not 5: a split run puts EIGHT Revits on this PC at once, and each writes its own journal.
    # The window decides which ones; this is only a size backstop.
    Get-ChildItem -LiteralPath $revitRoot -Directory -Filter 'Autodesk Revit *' -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-RecentFiles (Join-Path $_.FullName 'Journals') (Join-Path 'revit-journals' $_.Name) 'journal.*.txt' 15 }
}

Write-TextFile '05-installed-addin-versions.txt' {
    foreach ($year in '2024', '2025', '2026') {
        foreach ($base in @('KhBatchRunner')) {
            $dll = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$year\$base.dll"
            $addin = Join-Path $env:ProgramData "Autodesk\Revit\Addins\$year\$base.addin"
            "Revit $year / $base"
            if (Test-Path -LiteralPath $dll) { Get-Item -LiteralPath $dll | Select-Object FullName, LastWriteTime, @{ Name = 'FileVersion'; Expression = { $_.VersionInfo.FileVersion } } | Format-List }
            else { '  DLL not installed' }
            if (Test-Path -LiteralPath $addin) { Get-Content -LiteralPath $addin -Raw }
            else { '  Add-in manifest not installed' }
            ''
        }
    }
    'Per-user shadow copies (these OVERRIDE the machine-wide install and are the top "my fix did not'
    'arrive" cause; a scheduled run also cannot answer the modal trust prompt a duplicate manifest causes):'
    $any = $false
    foreach ($year in '2024', '2025', '2026') {
        $dir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
        foreach ($base in @('KhBatchRunner')) {
            $addin = Join-Path $dir "$base.addin"
            if (Test-Path -LiteralPath $addin) {
                $any = $true
                Get-Item -LiteralPath $addin | Select-Object FullName, LastWriteTime | Format-List
            }
        }
    }
    if (-not $any) { '  none - good.' }
}

# The same duplicate-manifest check, but as a verdict line in REPORT.txt.
$shadow = @()
foreach ($year in '2024', '2025', '2026') {
    foreach ($base in @('KhBatchRunner')) {
        $addin = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year\$base.addin"
        if (Test-Path -LiteralPath $addin) { $shadow += $addin }
    }
}
# A schedule that will not SAVE leaves this behind; one that saves and misbehaves does not.
$rejected = @(Get-ChildItem -LiteralPath $scheduled -File -Filter '*.rejected.xml' -ErrorAction SilentlyContinue)
if ($rejected.Count -gt 0) {
    Say ""
    Say "REGISTRATION: Task Scheduler REFUSED $($rejected.Count) definition(s) - the newest is"
    Say "              scheduled\$($rejected[0].Name) ($($rejected[0].LastWriteTime)). The add-in's"
    Say "              message at the time carries the schtasks exit code and text."
}

Say ""
if ($shadow.Count -gt 0) {
    Say "INSTALL: a per-user (dev) manifest is present and shadows the machine-wide install:"
    foreach ($s in $shadow) { Say "    $s" }
    Say "         Remove it before judging a scheduled run - two manifests for one AddInId make Revit"
    Say "         show a modal trust prompt that an unattended run cannot answer."
} else {
    Say "INSTALL: no per-user shadow manifests - the machine-wide install is the one that loads."
}

# --- Did the run actually do work? --------------------------------------------------------------
# The tracking CSV is the product's own answer to "did this run happen"; summarising it here is what
# turns a bundle of evidence into something readable in ten seconds.
$latestCsv = $null
try {
    $csvDir = Join-Path $bundle 'tracking-logs'
    if (Test-Path -LiteralPath $csvDir) {
        $latestCsv = Get-ChildItem -LiteralPath $csvDir -File -Filter '*.csv' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
} catch {}

Say ""
if ($null -eq $latestCsv) {
    Say "RESULT: no tracking CSV inside the collection window ($scopeText), so no model was processed"
    Say "        in it - or the job's log folder is unreachable from this PC. If a run you expected is"
    Say "        older than the window, re-run with -Days N or -All. Otherwise check runner\surface.log"
    Say "        and the Revit journals for how far Revit got."
} else {
    Say "RESULT: newest tracking log $($latestCsv.Name) ($($latestCsv.LastWriteTime))"
    try {
        $rows = @(Import-Csv -LiteralPath $latestCsv.FullName)
        Say "        $($rows.Count) row(s)"
        $rows | Group-Object Status | Sort-Object Count -Descending | ForEach-Object {
            $label = $_.Name
            if ([string]::IsNullOrWhiteSpace($label)) { $label = '(blank)' }
            Say ("        {0,-10} {1}" -f $label, $_.Count)
        }
        # INFO/SUMMARY rows are the run's own narration, not a model's result (TrackingCsvReader
        # makes the same distinction), so they must not be reported as failures.
        $bad = @($rows | Where-Object { $_.Status -and $_.Status -notmatch '^(?i)(OK|SKIPPED|INFO|SUMMARY)$' })
        if ($bad.Count -eq 0) {
            Say "        no failed model rows."
        } else {
            Say "        $($bad.Count) failed row(s); first few:"
            $bad | Select-Object -First 5 | ForEach-Object {
                Say ("          {0} | {1} | {2}" -f $_.Model, $_.Task, $_.Message)
            }
        }
    } catch {
        Say "        (could not be parsed: $($_.Exception.Message))"
    }
}

# Run manifest = the supervisor's own view: which groups ran, what it retried, what it gave up on.
try {
    $manifestDir = Join-Path $bundle 'scheduled\runs'
    if (Test-Path -LiteralPath $manifestDir) {
        $latestManifest = Get-ChildItem -LiteralPath $manifestDir -File -Filter '*.json' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -ne $latestManifest) {
            $m = Get-Content -LiteralPath $latestManifest.FullName -Raw | ConvertFrom-Json
            Say ""
            Say "SUPERVISOR: run $($m.RunId) state=$($m.State) rounds=$(@($m.Rounds).Count) models=$(@($m.AllModels).Count)"
            if (-not [string]::IsNullOrWhiteSpace($m.StopReason)) { Say "            stop reason: $($m.StopReason)" }
            $unrec = @($m.Unrecoverable)
            if ($unrec.Count -gt 0) { Say "            gave up on $($unrec.Count) model(s) after 4 attempts" }
        }
    }
} catch {}

# --- What the ADD-IN did about notifications -----------------------------------------------------
# The probe above answers "will Windows accept a toast from this PC". It cannot answer "did the
# add-in ever raise one", and those two failures look identical from a chair. The add-in writes a
# line to surface.log for every notification it attempts, naming the mechanism that carried it, so
# this counts them and says which of three different problems is actually present.
$surface = Join-Path $bundle 'runner\surface.log'
$notifyLines = @()
if (Test-Path -LiteralPath $surface) {
    $notifyLines = @(Get-Content -LiteralPath $surface -ErrorAction SilentlyContinue |
        Where-Object { $_ -match 'notification|tray balloon|toast' })
}

# 'delivered via tray balloon' is a PREFIX of 'delivered via in-process tray balloon', so the hosted
# count must exclude it or the two mechanisms cannot be told apart - which is the whole point here.
# The bare 'retained notification delivered' form is what versions before 1.3.4.6 wrote; counting it
# as a toast means a log spanning an upgrade still adds up instead of silently under-reporting.
$viaInProc = @($notifyLines | Where-Object { $_ -match 'delivered via in-process tray balloon' }).Count
$viaHosted = @($notifyLines | Where-Object {
    $_ -match 'delivered via tray balloon' -and $_ -notmatch 'in-process' }).Count
$viaToast = @($notifyLines | Where-Object {
    $_ -match 'delivered via retained toast' -or $_ -match 'retained notification delivered' }).Count
$notDelivered = @($notifyLines | Where-Object { $_ -match 'NOT delivered' }).Count
$notifyErrors = @($notifyLines | Where-Object { $_ -match 'failed|refused|timed out' })
$delivered = $viaToast + $viaHosted + $viaInProc

Write-TextFile '03-notification-history.txt' {
    "Window: $scopeText"
    ''
    "Notification lines found in runner\surface.log: $($notifyLines.Count)"
    "  delivered via retained toast (Notification Center): $viaToast"
    "  delivered via tray balloon (hosted, clickable):     $viaHosted"
    "  delivered via in-process tray balloon (brief):      $viaInProc"
    "  NOT delivered by any mechanism:                     $notDelivered"
    ''
    'Full history (each line is one notification the add-in attempted):'
    if ($notifyLines.Count -eq 0) { '  (none - the add-in never attempted a notification)' }
    else { $notifyLines | ForEach-Object { '  ' + $_ } }
}

Say ""
if (-not (Test-Path -LiteralPath $surface)) {
    Say "NOTIFICATION HISTORY: no runner\surface.log on this PC, so nothing can be said about what"
    Say "                      the add-in did. That usually means this add-in has not run here yet."
} elseif ($notifyLines.Count -eq 0) {
    Say "NOTIFICATION HISTORY: within $scopeText - THE ADD-IN NEVER ASKED. surface.log records runs but not one"
    Say "                      notification attempt, so this is a fault in the add-in, NOT in this"
    Say "                      PC's settings - no Windows setting can explain a message never sent."
    Say "                      Send this ZIP; 03-notification-history.txt is the proof."
} elseif ($delivered -eq 0) {
    Say "NOTIFICATION HISTORY: ATTEMPTED $($notifyLines.Count), DELIVERED NONE. Every mechanism was"
    Say "                      refused by this PC. The reason is in 03-notification-history.txt:"
    foreach ($e in @($notifyErrors | Select-Object -Last 3)) { Say "    $e" }
} else {
    Say "NOTIFICATION HISTORY: $delivered delivered (toast $viaToast, tray balloon $viaHosted,"
    Say "                      in-process balloon $viaInProc), $notDelivered not delivered."
    if ($viaToast -gt 0) {
        Say "                      Toasts go to Notification Center - press Win+N to find one you"
        Say "                      missed."
    }
    if ($viaHosted -gt 0 -or $viaInProc -gt 0) {
        Say "                      A tray balloon shows for a few SECONDS (Windows decides), then"
        Say "                      only its tray icon remains - hover it for the run name, click it"
        Say "                      to open that run's log. It is not kept in Notification Center."
    }
    if ($viaInProc -gt 0) {
        Say "                      'in-process' means KhBatchLauncher.exe was not found beside the"
        Say "                      add-in: that message was brief and NOT clickable. Reinstall to fix."
    }
}

# The last thing Revit's own breadcrumb trail said, which is where a hang shows up.
if (Test-Path -LiteralPath $surface) {
    Say ""
    Say "LAST BREADCRUMBS (runner\surface.log):"
    foreach ($line in @(Get-Content -LiteralPath $surface -Tail 12 -ErrorAction SilentlyContinue)) { Say "    $line" }
}

Say ""
Say "WHERE TO LOOK NEXT"
Say "  00-launch-chain.txt        can this PC start the launcher at all"
Say "  02-notifications.txt       whether Windows accepts and keeps this product's toasts"
Say "  03-notification-history.txt what the add-in actually sent, and by which mechanism"
Say "  tasks\<id>\02-status.txt   what Task Scheduler recorded (0x1 = Revit ran and returned 1;"
Say "                             0xFA1/0xFA2 are the launcher's own codes: bad arguments / could"
Say "                             not start Revit; 0x1 with NO launcher log means the action never ran)"
Say "  tasks\<id>\launcher_*.log  the launcher's own trail, written before anything can fail"
Say "  runner\surface.log         the add-in's stage-by-stage breadcrumbs inside Revit"
Say "  revit-journals\            Revit's own log when it exits or hangs before the add-in writes"
Say "  tracking-logs\             the per-model result CSVs"
Say "  task-output\<Task>\        what each task PRODUCED: its matrix workbook(s) and the head of"
Say "                             its detail CSV (truncated - re-run with -All for whole files)"
Say ""
Say "This bundle covers $scopeText."
Say "If the run you are chasing is older than that, re-run Collect-Diagnostics.cmd with -Days 3 (or"
Say "-All). The per-task job files, the launch chain and the installed versions are collected whole"
Say "either way - only dated evidence is windowed."
Say ""
Say "This report names no model or project paths. Those stay in the evidence files, for a trusted"
Say "support recipient only."

$report | Out-File -LiteralPath (Join-Path $bundle 'REPORT.txt') -Encoding utf8

# --- Package ------------------------------------------------------------------------------------
$zip = $bundle + '.zip'
$zipped = $false
# tar.exe, and neither of the two obvious alternatives:
#
#   * Compress-Archive is a PowerShell MODULE, so it is refused under ConstrainedLanguage exactly
#     like the code calling it — verified, it fails with MethodInvocationNotSupportedInConstrained-
#     Language. A managed PC is precisely where this bundle matters most.
#   * [System.IO.Compression.ZipFile] needs Add-Type, also refused there. It was used until 1.3.5
#     because Compress-Archive had stringified Get-ChildItem DirectoryInfo objects to their NAMES
#     ("the path 'revit-journals' is not a valid file system path") and produced no ZIP at all.
#
# bsdtar ships in System32 on Windows 10 1803+, is a real executable so no language mode applies,
# and lives in a folder every SRP/AppLocker default rule permits. "-a" picks the format from the
# .zip extension; "-C $bundle ." packs the contents WITHOUT the parent folder name.
try {
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (Test-Path -LiteralPath $tar) {
        & $tar -a -c -f $zip -C $bundle . 2>$null
        $zipped = (Test-Path -LiteralPath $zip)
    }
    if (-not $zipped) {
        Write-Warning "Could not create a ZIP on this PC. Send the FOLDER instead: $bundle"
    }
} catch {
    Write-Warning "Bundle directory created, but ZIP compression failed ($($_.Exception.Message)). Send the folder instead: $bundle"
}

Write-Host ""
foreach ($line in $report) { Write-Host $line }
Write-Host ""
Write-Host "==================================================================" -ForegroundColor Green
if ($zipped) {
    Write-Host " SEND THIS ONE FILE:" -ForegroundColor Green
    Write-Host "   $zip" -ForegroundColor Green
} else {
    Write-Host " SEND THIS FOLDER:" -ForegroundColor Green
    Write-Host "   $bundle" -ForegroundColor Green
}
Write-Host "==================================================================" -ForegroundColor Green

try { Set-Clipboard -Value $(if ($zipped) { $zip } else { $bundle }) } catch {}

if (-not $NoOpen) {
    try { Start-Process explorer.exe "/select,`"$(if ($zipped) { $zip } else { $bundle })`"" } catch {}
}
