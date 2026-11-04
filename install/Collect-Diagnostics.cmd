@echo off
REM One-click diagnostic report for the KH Batch Script Runner scheduled runs.
REM
REM Double-click this file after a scheduled run. It writes a ZIP to your Desktop
REM (KhBatchRunner-Diagnostics\...) and opens Explorer on it - send that one file.
REM
REM Nothing here needs administrator rights, but Windows only lets an administrator read the
REM Task Scheduler event history; run this as administrator if you want that part too.
REM
REM -ExecutionPolicy Bypass applies to THIS invocation only - it does not change any machine policy.

setlocal
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if not exist "%PS%" set "PS=powershell.exe"

echo Collecting diagnostics. This takes a few seconds...
echo.
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Collect-ScheduleDiagnostics.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
REM Never claim success on the strength of having been run. Until 1.3.5.1 this printed the
REM "copied to your clipboard" line unconditionally, so a script that had died on its first
REM statement still looked like it had produced a report - and the operator went looking on the
REM Desktop for a file that was never written.
if "%RC%"=="0" (
    echo (The full path of the report has also been copied to your clipboard.^)
) else (
    echo ============================================================
    echo  THE REPORT WAS NOT COMPLETED. PowerShell exited with %RC%.
    echo.
    echo  Copy the red error text above and send THAT - it is the
    echo  finding. If a folder was created on your Desktop under
    echo  KhBatchRunner-Diagnostics, send the newest one too, even
    echo  though it is incomplete.
    echo ============================================================
)
echo.
pause
endlocal
