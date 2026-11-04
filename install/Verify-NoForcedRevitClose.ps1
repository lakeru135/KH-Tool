<#
.SYNOPSIS
  Fails packaging if deployable source can forcibly terminate Revit.

.DESCRIPTION
  Version 1.3.4 permits normal WM_CLOSE / CloseMainWindow requests only. A stalled worker must be
  left open and the supervised run stopped.

  A few allowances, all narrow enough that they cannot be widened by accident — each is keyed to one
  file AND to text naming the non-Revit process it acts on, so a Revit-targeting call in the very
  same file still fails the gate:

    1. RunNotification.cs / process.Kill()   - the short-lived hidden PowerShell toast helper.
    1a. RunNotification.cs / toastHelper.Kill() - the direct-WinRT toast helper.
    1b. RunNotification.cs / sweeper.Kill()  - the expired-notification sweeper.
    2. InstallerCommon.iss / KhBatchLauncher.exe - setup closing our own tray-notification helper,
       which since 1.3.4.6 holds a tray icon for hours and so holds its own executable open. Windows
       reports a locked image as ACCESS DENIED, which is what stopped 1.3.5 setups on
       "DeleteFile failed; code 5". That process holds a notification, never a document.

  Neither is Revit, and the rule that matters - never force-close Revit, because a live model may
  hold unsaved work - is untouched.
#>
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$self = $MyInvocation.MyCommand.Path

$files = @(
    Get-ChildItem (Join-Path $root "src\KhBatchRunner") -Recurse -File -Filter *.cs
    Get-ChildItem (Join-Path $root "src\KhBatchController") -Recurse -File -Filter *.cs
    Get-ChildItem $PSScriptRoot -File |
        Where-Object { $_.Extension -in @(".ps1", ".iss", ".vbs") -and $_.FullName -ne $self }
)

$patterns = @(
    '\.Kill\s*\(',
    '\btaskkill(?:\.exe)?\b',
    '\bTerminateProcess\s*\(',
    'AllowHardTerminate[^\r\n]*true'
)

$violations = foreach ($file in $files) {
    foreach ($hit in Select-String -LiteralPath $file.FullName -Pattern $patterns -AllMatches) {
        $isNotificationHelper =
            $file.FullName.EndsWith("\UI\RunNotification.cs", [StringComparison]::OrdinalIgnoreCase) -and
            $hit.Line -match 'process\.Kill\s*\('
        # The direct-WinRT toast helper (1.3.5.3): the same short-lived, hidden, non-Revit process
        # as allowance 1, started as KhBatchLauncher.exe --toast instead of powershell.exe. Keyed
        # to its own variable name rather than to a bare \.Kill\( so that widening it stays a
        # deliberate edit, exactly like the two allowances above.
        $isToastHelper =
            $file.FullName.EndsWith("\UI\RunNotification.cs", [StringComparison]::OrdinalIgnoreCase) -and
            $hit.Line -match 'toastHelper\.Kill\s*\('
        # The toast SWEEPER (1.3.7): KhBatchLauncher.exe --toast-sweep, which removes expired
        # notifications and opens no document. Same short-lived, hidden, non-Revit shape as the two
        # above, and keyed to its own variable name for the same reason — an allowance that covered
        # every .Kill( in this file would eventually cover one aimed at Revit.
        $isToastSweeper =
            $file.FullName.EndsWith("\UI\RunNotification.cs", [StringComparison]::OrdinalIgnoreCase) -and
            $hit.Line -match 'sweeper\.Kill\s*\('
        # Setup closing our own tray-notification helper. The image name must appear on the SAME
        # line as the call, so this cannot quietly come to cover a taskkill aimed at Revit.
        $isLauncherHelper =
            $file.FullName.EndsWith("\InstallerCommon.iss", [StringComparison]::OrdinalIgnoreCase) -and
            $hit.Line -match 'KhBatchLauncher\.exe'
        if (-not ($isNotificationHelper -or $isToastHelper -or $isToastSweeper -or $isLauncherHelper)) {
            [pscustomobject]@{
                File = $file.FullName
                Line = $hit.LineNumber
                Text = $hit.Line.Trim()
            }
        }
    }
}

if ($violations) {
    $detail = $violations | ForEach-Object { "$($_.File):$($_.Line): $($_.Text)" }
    throw "Forced process termination is forbidden in deployed code:" +
          [Environment]::NewLine + ($detail -join [Environment]::NewLine)
}

Write-Host "Forced-close gate passed: no deployed Revit termination path found." -ForegroundColor Green
