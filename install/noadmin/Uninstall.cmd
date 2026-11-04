@echo off
REM Removes what Install.cmd copied into %APPDATA%\Autodesk\Revit\Addins\<year>. Only the files
REM listed in files.txt are deleted. Shared runtime DLLs (System.*, Microsoft.*) are left in
REM place because another add-in in the same folder may rely on them. An all-users install
REM (setup.exe) is not touched - remove that one from Windows Settings > Apps.

setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
call "%~dp0package.cmd"
title Uninstall %PRODUCT% - current user

tasklist /FI "IMAGENAME eq Revit.exe" 2>nul | find /I "Revit.exe" >nul
if not errorlevel 1 (
    echo  Revit is running. Close every Revit window, then run this again.
    echo.
    pause
    exit /b 1
)

set "ROOT=%APPDATA%\Autodesk\Revit\Addins"
set "REMOVED=0"
for /f "usebackq delims=" %%F in ("%~dp0files.txt") do (
    set "NAME=%%~nxF"
    set "SKIP="
    if /I "!NAME:~0,7!"=="System." set "SKIP=1"
    if /I "!NAME:~0,10!"=="Microsoft." set "SKIP=1"
    if not defined SKIP if exist "%ROOT%\%%F" (
        del /F /Q "%ROOT%\%%F" >nul 2>&1
        if exist "%ROOT%\%%F" (
            echo  could not delete %%F
        ) else (
            set /a REMOVED+=1
        )
    )
)

REM Folders that hold nothing but this add-in's files; rd without /s only removes them if empty.
for /D %%Y in ("%~dp0payload\*") do (
    rd "%ROOT%\%%~nxY\%PAYLOAD_SUBDIR%Fixtures" >nul 2>&1
    rd "%ROOT%\%%~nxY\%PAYLOAD_SUBDIR%Support" >nul 2>&1
    rd "%ROOT%\%%~nxY\%PAYLOAD_SUBDIR%KhMcp" >nul 2>&1
    if not "%PAYLOAD_SUBDIR%"=="" rd "%ROOT%\%%~nxY\%PAYLOAD_SUBDIR%" >nul 2>&1
)

REM The per-user "Open log" scheme, only if it still points into this per-user install.
if "%REGISTER_PROTOCOL%"=="1" (
    reg query "HKCU\Software\Classes\khbatchlog\shell\open\command" /ve 2>nul | find /I "%ROOT%" >nul
    if not errorlevel 1 reg delete "HKCU\Software\Classes\khbatchlog" /f >nul 2>&1
)

echo.
echo  Removed %REMOVED% file^(s^) of %PRODUCT% from %ROOT%.
echo.
pause
endlocal
