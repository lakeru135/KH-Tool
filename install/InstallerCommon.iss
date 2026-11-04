// Shared [Code] helpers for the KH Batch Script Runner / DRW Helper installers.
// A pure-Pascal fragment: #include this INSIDE a [Code] section, after defining:
//   #define InstAppId      "{THE-APPID-GUID}"   (single-brace literal, matches [Setup] AppId)
//   #define InstAppVersion AppVersion           (the version string being installed)
//
// Provides Revit detection (64-bit-safe), existing-install / version helpers, and pre-install
// cleanup of stale duplicates (other registry view + per-user %AppData% shadow copies). The
// consuming .iss supplies its own InitializeSetup / CurStepChanged / UpdateReadyMemo event
// functions and calls into these.
//
// NOTE: comments here use // deliberately — a Pascal { } comment ends at the first }, which would
// be swallowed by the {commonpf64} constant in the detection helper below.

// True if "C:\Program Files\Autodesk\Revit <year>" exists. Uses {commonpf64} (the real 64-bit
// Program Files) — Revit is 64-bit only, and the setup runs in 64-bit mode
// (ArchitecturesInstallIn64BitMode), so plain {commonpf} would otherwise resolve to the empty
// "Program Files (x86)" path and wrongly report Revit as absent.
function RevitInstalled(Year: String): Boolean;
begin
  Result := DirExists(ExpandConstant('{commonpf64}\Autodesk\Revit ' + Year));
end;

// Append a year to a comma-separated list, inserting ", " between entries.
function AppendYear(List, Year: String): String;
begin
  if List = '' then
    Result := Year
  else
    Result := List + ', ' + Year;
end;

// Comma-joined list of detected supported Revit years (e.g. "2024, 2026"); '' when none.
function DetectedRevitYears(): String;
begin
  Result := '';
  if RevitInstalled('2024') then Result := AppendYear(Result, '2024');
  if RevitInstalled('2025') then Result := AppendYear(Result, '2025');
  if RevitInstalled('2026') then Result := AppendYear(Result, '2026');
end;

// True if any process with this image name is running. WMI rather than a window search, because it
// sees processes with no window at all — a Revit hidden by the standalone controller, and the
// notification helper below, which has only a tray icon.
function ProcessIsRunning(ExeName: String): Boolean;
var
  Locator, Services, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery(
      'SELECT ProcessId FROM Win32_Process WHERE Name = ''' + ExeName + '''');
    Result := Processes.Count > 0;
  except
    Log('Could not query running processes for ' + ExeName + '; file-lock checks stay active.');
  end;
end;

// Detect any running Revit process without trying to close it. The setup must not use Restart
// Manager to terminate Revit because a live model may contain unsaved work.
function RevitIsRunning(): Boolean;
begin
  Result := ProcessIsRunning('Revit.exe');
end;

// Close our own tray-notification helpers before replacing files.
//
// WHY THIS IS NEEDED. KhBatchLauncher.exe gained a --notify mode in 1.3.4.6 that holds a tray icon
// for the whole retention window — an hour for a clean run, up to eight for a failed one. Windows
// will not let anything delete a file that is currently mapped as a running image, and it reports
// that as ACCESS DENIED (code 5), not "file in use" — so setup stopped on an unexplained
// "DeleteFile failed; code 5" for C:\Program Files\Kanghun BIM\KhBatchLauncher.exe whenever a
// notification from an earlier run was still waiting. Before that mode existed the launcher only
// lived as long as the Revit it started, so closing Revit was enough.
//
// SAFE TO TERMINATE, unlike Revit. This process holds no document and no unsaved work — it holds a
// notification. Killing it dismisses a message that has already been delivered. It cannot be the
// launcher supervising a scheduled run either: that one has a Revit child, and setup has already
// refused to proceed while any Revit is running. A graceful close is not an option worth trying
// because the helper has no window for WM_CLOSE to reach.
procedure StopNotificationHelpers();
var
  ResultCode, Waited: Integer;
begin
  if not ProcessIsRunning('KhBatchLauncher.exe') then Exit;

  Log('Closing KhBatchLauncher.exe (tray notification helper) so its file can be replaced.');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM KhBatchLauncher.exe', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);

  // Termination is asynchronous: the command returns before the kernel has torn the process down
  // and released the image, and copying into that gap fails exactly as before.
  Waited := 0;
  while ProcessIsRunning('KhBatchLauncher.exe') and (Waited < 5000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

// Initial guard. Interactive setup explains the block; unattended setup logs and exits non-zero.
function ConfirmRevitClosed(): Boolean;
begin
  Result := not RevitIsRunning();
  if Result then Exit;

  if WizardSilent() then
    Log('Autodesk Revit is running - refusing to install without closing it normally first.')
  else
    MsgBox('Autodesk Revit is currently running.'#13#10#13#10 +
           'This installer will not close it. Save your work, close every Revit window normally, ' +
           'then run this setup again.', mbError, MB_OK);
end;

// Last-moment guard used by PrepareToInstall. Returning text prevents file changes and leaves an
// interactive setup on the Ready page so the user can close Revit and retry safely.
function RevitMustBeClosedError(): String;
begin
  if RevitIsRunning() then
  begin
    Result := 'Autodesk Revit is running. This installer will not close it. Close Revit normally, then click Install again.';
    Log(Result);
  end
  else
    Result := '';
end;

// Pull the leading integer off a dotted version string (mutates S to the remainder).
function VerNextPart(var S: String): Integer;
var
  p: Integer;
begin
  p := Pos('.', S);
  if p > 0 then
  begin
    Result := StrToIntDef(Copy(S, 1, p - 1), 0);
    Delete(S, 1, p);
  end
  else
  begin
    Result := StrToIntDef(S, 0);
    S := '';
  end;
end;

// Numeric dotted-version compare: -1 when A<B, 0 equal, 1 when A>B.
function CompareVersion(A, B: String): Integer;
var
  pa, pb: Integer;
begin
  Result := 0;
  while ((A <> '') or (B <> '')) and (Result = 0) do
  begin
    pa := VerNextPart(A);
    pb := VerNextPart(B);
    if pa < pb then Result := -1
    else if pa > pb then Result := 1;
  end;
end;

// The Inno-written uninstall key for THIS AppId (Inno appends "_is1" to the [Setup] AppId).
function AppIdUninstallKey(): String;
begin
  Result := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{#InstAppId}_is1';
end;

// DisplayVersion of an existing install of THIS AppId in one specific registry view, '' if absent.
function InstalledVersionInView(RootKey: Integer): String;
begin
  Result := '';
  if not RegQueryStringValue(RootKey, AppIdUninstallKey(), 'DisplayVersion', Result) then
    Result := '';
end;

// Highest DisplayVersion of an existing install of THIS AppId across BOTH HKLM registry views
// (64-bit and 32-bit / WOW6432Node), '' if not installed in either.
//
// Why both views: pre-0.6.0 setups ran in 32-bit mode, so their uninstall key lives in the
// WOW6432Node (HKLM32) view. A 64-bit-mode setup's plain HKLM/HKLM64 lookups never see it — which
// is exactly why the upgrade prompt never fired over a 0.5.x install. Checking HKLM32 explicitly
// surfaces the legacy install so ConfirmExistingInstall can offer the upgrade.
function InstalledVersion(): String;
var
  V64, V32: String;
begin
  V64 := InstalledVersionInView(HKLM64);
  V32 := InstalledVersionInView(HKLM32);
  if (V64 <> '') and (V32 <> '') then
  begin
    if CompareVersion(V64, V32) >= 0 then Result := V64 else Result := V32;
  end
  else if V64 <> '' then
    Result := V64
  else
    Result := V32;
end;

// UninstallString (unquoted) of THIS AppId in a given registry view; '' if not present there.
function UninstallStringInView(RootKey: Integer): String;
begin
  Result := '';
  if not RegQueryStringValue(RootKey, AppIdUninstallKey(), 'UninstallString', Result) then
    Result := '';
  Result := RemoveQuotes(Result);
end;

// Run an Inno uninstaller silently, then POLL until KeyName is gone from both HKLM views (or a
// ~30s timeout). The wait is essential: Inno's unins000.exe copies itself to %TEMP% and re-execs,
// so Exec returns BEFORE the uninstall (and its file deletes) actually finish. Without the wait,
// this setup would copy its files and the still-running old uninstaller would then delete them
// (same install paths) — the classic "installed but nothing there" outcome.
procedure RunUninstallerAndWait(UninstPath, KeyName: String);
var
  ResultCode, Waited: Integer;
begin
  if (UninstPath = '') or (not FileExists(UninstPath)) then Exit;
  Exec(UninstPath, '/VERYSILENT /NORESTART /SUPPRESSMSGBOXES', '', SW_HIDE,
       ewWaitUntilTerminated, ResultCode);
  Waited := 0;
  while (RegKeyExists(HKLM32, KeyName) or RegKeyExists(HKLM64, KeyName)) and (Waited < 30000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
end;

// Before laying down files, remove a same-AppId install sitting in the OTHER (32-bit / WOW6432Node)
// registry view — a legacy pre-0.6.0 setup that ran in 32-bit mode. Our current setup always runs
// 64-bit and updates the 64-bit-view entry in place, so the 32-bit-view entry is a stale duplicate:
// left alone it clutters Add/Remove and its uninstaller (same install paths as ours) could later
// delete our files. Removing it first makes the upgrade clean.
procedure RemoveOtherViewInstall();
begin
  RunUninstallerAndWait(UninstallStringInView(HKLM32), AppIdUninstallKey());
end;

// Delete OUR add-in files (only) from a single profile's per-user Addins\<year> folder. Never a
// wildcard — that folder is shared with other Autodesk add-ins, so each file is named explicitly.
procedure RemovePerUserCopiesInProfile(ProfileDir, BaseName, Year: String);
var
  Dir: String;
  Exts: array[0..3] of String;
  I: Integer;
  F: String;
begin
  Dir := ProfileDir + '\AppData\Roaming\Autodesk\Revit\Addins\' + Year + '\';
  Exts[0] := '.addin';
  Exts[1] := '.dll';
  Exts[2] := '.pdb';
  Exts[3] := '.deps.json';
  for I := 0 to 3 do
  begin
    F := Dir + BaseName + Exts[I];
    if FileExists(F) then
      DeleteFile(F);
  end;
end;

// Remove per-user (%AppData%) shadow copies of this add-in across ALL user profiles, for one Revit
// year. A per-user copy with the SAME AddInId as the machine (ProgramData) install makes Revit load
// only one and reject the other as a duplicate — typically the older per-user (dev) copy wins, which
// looks exactly like "the installer didn't upgrade" (stale version in About, missing new features).
// Setup is elevated, so every profile under <systemdrive>\Users is reachable.
procedure RemovePerUserCopies(BaseName, Year: String);
var
  UsersDir: String;
  FR: TFindRec;
begin
  UsersDir := ExpandConstant('{sd}') + '\Users';
  if FindFirst(UsersDir + '\*', FR) then
  try
    repeat
      // $10 = FILE_ATTRIBUTE_DIRECTORY; skip the "." / ".." pseudo-entries.
      if ((FR.Attributes and $10) <> 0) and (FR.Name <> '.') and (FR.Name <> '..') then
        RemovePerUserCopiesInProfile(UsersDir + '\' + FR.Name, BaseName, Year);
    until not FindNext(FR);
  finally
    FindClose(FR);
  end;
end;

// If an install of this AppId already exists, ask whether to upgrade / repair / downgrade.
// Returns True to proceed (a same-AppId Inno install overwrites in place, so every branch is
// just proceed-or-cancel), False to abort. Not installed -> True (proceed silently).
function ConfirmExistingInstall(): Boolean;
var
  Cur: String;
  Cmp: Integer;
begin
  Result := True;
  Cur := InstalledVersion();
  if Cur = '' then Exit;

  Cmp := CompareVersion('{#InstAppVersion}', Cur);

  { A silent install has nobody to answer a message box. Inno's /SUPPRESSMSGBOXES only covers
    Setup's OWN prompts, not a MsgBox raised from [Code] — so without this the process sits
    forever on an invisible dialog, which is exactly what an unattended or IT-pushed rollout
    hits. Running silently is itself the operator's answer: proceed, and record which branch. }
  if WizardSilent() then
  begin
    if Cmp > 0 then Log('Silent install: upgrading ' + Cur + ' -> {#InstAppVersion}.')
    else if Cmp = 0 then Log('Silent install: reinstalling (repair) ' + Cur + '.')
    else Log('Silent install: DOWNGRADING ' + Cur + ' -> {#InstAppVersion} as instructed.');
    Exit;
  end;

  if Cmp > 0 then
    Result := MsgBox('Version ' + Cur + ' is already installed.'#13#10#13#10 +
                     'Upgrade to {#InstAppVersion}?', mbConfirmation, MB_YESNO) = IDYES
  else if Cmp = 0 then
    Result := MsgBox('Version ' + Cur + ' is already installed.'#13#10#13#10 +
                     'Reinstall (repair) it?', mbConfirmation, MB_YESNO) = IDYES
  else
    Result := MsgBox('A NEWER version (' + Cur + ') is already installed.'#13#10#13#10 +
                     'Downgrading to {#InstAppVersion} may lose fixes. Downgrade anyway?',
                     mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;
