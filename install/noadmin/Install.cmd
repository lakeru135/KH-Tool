@echo off
REM No-admin install: copies this add-in into YOUR OWN Revit Addins folder
REM (%APPDATA%\Autodesk\Revit\Addins\<year>), for every Revit year in payload\ that is
REM installed on this PC. Nothing is written outside your user profile, so no administrator
REM rights are needed. It loads only for the Windows user who runs this file.
REM
REM If the all-users setup.exe is also installed, this per-user copy takes precedence (Revit
REM ignores a %ProgramData% manifest when %APPDATA% holds one with the same file name).
REM Running the setup.exe later removes this per-user copy again.

setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
call "%~dp0package.cmd"
title Install %PRODUCT% %VERSION% - current user, no admin

echo.
echo  %PRODUCT% %VERSION%
echo  Per-user install (no administrator rights needed)
echo  ------------------------------------------------------------
echo.

tasklist /FI "IMAGENAME eq Revit.exe" 2>nul | find /I "Revit.exe" >nul
if not errorlevel 1 (
    echo  Revit is running. Close every Revit window, then run this again.
    echo  ^(The add-in files are locked while Revit has them loaded.^)
    echo.
    pause
    exit /b 1
)

set "PF=%ProgramW6432%"
if not defined PF set "PF=%ProgramFiles%"

set "INSTALLED="
set "FAILED="
set "LAUNCHER="
for /D %%Y in ("%~dp0payload\*") do (
    set "YEAR=%%~nxY"
    if exist "!PF!\Autodesk\Revit !YEAR!\Revit.exe" (
        set "DEST=%APPDATA%\Autodesk\Revit\Addins\!YEAR!"
        xcopy "%%~fY\*" "!DEST!\" /E /I /Y /Q /R >nul
        if errorlevel 1 (
            echo  Revit !YEAR!: FAILED to copy into !DEST!
            set "FAILED=!FAILED! !YEAR!"
        ) else (
            echo  Revit !YEAR!: installed into !DEST!
            set "INSTALLED=!INSTALLED! !YEAR!"
            if exist "!DEST!\%PAYLOAD_SUBDIR%KhBatchLauncher.exe" set "LAUNCHER=!DEST!\%PAYLOAD_SUBDIR%KhBatchLauncher.exe"
            if exist "%ProgramData%\Autodesk\Revit\Addins\!YEAR!\%MANIFEST%" (
                echo            ^(the all-users copy in ProgramData is now overridden by this one^)
            )
            REM Files extracted from a downloaded ZIP carry the "from the internet" mark; clear it
            REM so Revit does not refuse the DLLs. Best effort: harmless if PowerShell is locked down.
            set "UNBLOCK_DIR=!DEST!"
            powershell -NoProfile -Command "Get-ChildItem -LiteralPath $env:UNBLOCK_DIR -Recurse -File | Unblock-File" >nul 2>&1
        )
    ) else (
        echo  Revit !YEAR!: not installed on this PC - skipped
    )
)

if not defined INSTALLED (
    echo.
    if defined FAILED (
        echo  Nothing was installed. See the FAILED line above.
    ) else (
        echo  Nothing was installed: none of the supported Revit versions were found
        echo  under "!PF!\Autodesk".
    )
    echo.
    pause
    exit /b 1
)

REM The "Open log" link on a run notification uses this URL scheme. The all-users setup registers
REM it machine-wide; per-user it goes under HKCU, which needs no admin.
if "%REGISTER_PROTOCOL%"=="1" if defined LAUNCHER (
    reg add "HKCU\Software\Classes\khbatchlog" /ve /t REG_SZ /d "URL:KH Batch run log" /f >nul
    reg add "HKCU\Software\Classes\khbatchlog" /v "URL Protocol" /t REG_SZ /d "" /f >nul
    reg add "HKCU\Software\Classes\khbatchlog\shell\open\command" /ve /t REG_SZ /d "\"!LAUNCHER!\" --open-log \"%%1\"" /f >nul
)

echo.
if defined FAILED (
    echo  Installed for Revit%INSTALLED%, but FAILED for Revit%FAILED%.
) else (
    echo  Done. Start Revit%INSTALLED% - the "%TAB%" ribbon tab should appear.
)
echo  If Revit asks whether to load this add-in, choose "Always Load".
echo  To remove it later, run Uninstall.cmd from this folder.
echo.
pause
endlocal
