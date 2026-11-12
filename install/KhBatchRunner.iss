; Inno Setup script for the KH Batch Script Runner Revit add-in.
;
; ONE installer covering Revit 2024 + 2025 + 2026 (replaces the old per-year /DRevitYear
; builds). All-users install (requires admin) into the machine-wide Revit Addins folder
; (%ProgramData%). Chosen so that on managed laptops where install runs elevated under a
; *different* admin account, the add-in still loads for the logged-in user (a per-user
; install would land in the admin's profile instead).
;
; No "which Revit?" question: each year's files copy only when that Revit is detected under
; "C:\Program Files\Autodesk\Revit <year>" (the Check: on every [Files] entry), so one
; setup.exe adapts to whatever the machine has. Detection runs in 64-bit mode
; (ArchitecturesInstallIn64BitMode below) so {commonpf64} points at the real Program Files —
; without that the 32-bit installer looked under "Program Files (x86)" and wrongly reported
; every Revit as missing. A single un-suffixed AppId means one Add/Remove entry; the legacy
; year-suffixed installs (0.1.x) are quietly uninstalled first so years aren't double-registered.
;
; Build the three Release bin trees first, then compile — see install\Build-Installer.ps1,
; which runs the builds and then ISCC.exe on this script.

#define AppName "KH Batch Script Runner for Autodesk Forma"
#define AppVersion "2.1.0.0"
; AppId (single-brace literal, matches [Setup]) + version, consumed by InstallerCommon.iss.
#define InstAppId "{7E2C4D8A-3F51-4B9C-9E1D-2A6F0B4C8D31}"
#define InstAppVersion AppVersion

; ISCC /DUnlimited builds the UNRESTRICTED variant: the same product from the FullUnlimited bin
; tree, whose add-in has the build-validity gate compiled out. Same AppId, same AddInId, same
; files and registry keys — so it deliberately REPLACES a gated install and is replaced by one.
; Only the source tree, the setup filename and the Add/Remove name differ, the last of those
; being the only way to tell from outside Revit which of the two a PC is running.
; One script rather than a second .iss: the ~200 lines of [Code] below would otherwise be
; duplicated and drift apart.
#ifdef Unlimited
  #define Bin2024 "..\src\KhBatchRunner\bin\Release\FullUnlimited\2024"
  #define Bin2025 "..\src\KhBatchRunner\bin\Release\FullUnlimited\2025"
  #define Bin2026 "..\src\KhBatchRunner\bin\Release\FullUnlimited\2026"
  #define SetupBaseName "KhBatchRunner-Setup-Unlimited"
  #define UninstallName AppName + " (Unlimited)"
#else
; Per-year Release build output (src\KhBatchRunner\bin\Release\<year>\). The 2024 tree is
; net48 and also carries System.Text.Json + transitives; 2025/2026 are net8 (dll + deps.json).
  #define Bin2024 "..\src\KhBatchRunner\bin\Release\2024"
  #define Bin2025 "..\src\KhBatchRunner\bin\Release\2025"
  #define Bin2026 "..\src\KhBatchRunner\bin\Release\2026"
  #define SetupBaseName "KhBatchRunner-Setup"
  #define UninstallName AppName
#endif

[Setup]
; Single, un-suffixed AppId → one Add/Remove entry for all installed years.
AppId={{7E2C4D8A-3F51-4B9C-9E1D-2A6F0B4C8D31}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Kanghun BIM
; No install directory: every file goes to a per-year Addins folder (see [Files] DestDir), so
; there's no {app} dir to create or let the user choose.
CreateAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; Revit is 64-bit only — run the setup in 64-bit mode so {commonpf64} and Revit detection are
; correct, and refuse to run on a 32-bit-only OS (where no supported Revit could exist anyway).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename={#SetupBaseName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#UninstallName}
; Never let Restart Manager close Revit. InitializeSetup/PrepareToInstall below require the user
; (or deployment system) to close Revit normally before any add-in files are changed.
CloseApplications=no
RestartApplications=no

[Files]
; Every line is guarded by Check: RevitInstalled('<year>') — files for a not-installed year
; never copy, so the installer auto-adapts with no component/version question.
; --- Revit 2024 (net48 — *.dll ships KhBatchRunner + bundled System.Text.Json & transitives) ---
Source: "{#Bin2024}\*.dll";                DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; Check: RevitInstalled('2024'); Flags: ignoreversion
Source: "{#Bin2024}\KhBatchRunner.addin"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; Check: RevitInstalled('2024'); Flags: ignoreversion
Source: "{#Bin2024}\KhBatchRunner.pdb";   DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; Check: RevitInstalled('2024'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2024}\KhBatchLauncher.exe"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; Check: RevitInstalled('2024'); Flags: ignoreversion
Source: "{#Bin2024}\KhBatchLauncher.exe.config"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024"; Check: RevitInstalled('2024'); Flags: ignoreversion skipifsourcedoesntexist
; KH-Tool AI Bridge: the MCP server Claude Code starts (net48), in its own subfolder
Source: "{#Bin2024}\KhMcp\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024\KhMcp"; Check: RevitInstalled('2024'); Flags: ignoreversion

; --- Revit 2025 (net8 — dll + deps.json) ---
Source: "{#Bin2025}\*.dll";                     DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; Check: RevitInstalled('2025'); Flags: ignoreversion
Source: "{#Bin2025}\KhBatchRunner.deps.json";  DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; Check: RevitInstalled('2025'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2025}\KhBatchRunner.addin";      DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; Check: RevitInstalled('2025'); Flags: ignoreversion
Source: "{#Bin2025}\KhBatchRunner.pdb";        DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; Check: RevitInstalled('2025'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2025}\KhBatchLauncher.exe";      DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; Check: RevitInstalled('2025'); Flags: ignoreversion
Source: "{#Bin2025}\KhBatchLauncher.exe.config"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025"; Check: RevitInstalled('2025'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2025}\KhMcp\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025\KhMcp"; Check: RevitInstalled('2025'); Flags: ignoreversion

; --- Revit 2026 (net8 — dll + deps.json) ---
Source: "{#Bin2026}\*.dll";                     DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Check: RevitInstalled('2026'); Flags: ignoreversion
Source: "{#Bin2026}\KhBatchRunner.deps.json";  DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Check: RevitInstalled('2026'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2026}\KhBatchRunner.addin";      DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Check: RevitInstalled('2026'); Flags: ignoreversion
Source: "{#Bin2026}\KhBatchRunner.pdb";        DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Check: RevitInstalled('2026'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2026}\KhBatchLauncher.exe";      DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Check: RevitInstalled('2026'); Flags: ignoreversion
Source: "{#Bin2026}\KhBatchLauncher.exe.config"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026"; Check: RevitInstalled('2026'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2026}\KhMcp\*"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\KhMcp"; Check: RevitInstalled('2026'); Flags: ignoreversion

; POLICY-SAFE LAUNCHER COPY. A managed PC refused the ProgramData copy outright — "This program is
; blocked by group policy" — because the default Software Restriction Policy / AppLocker rule sets
; permit %ProgramFiles% and %WinDir% and deny everywhere else, and %ProgramData% is the classic
; denied location. The identical executable installed here is normally permitted, and the add-in
; self-tests each copy at Save and registers the task against whichever one this PC will run.
; Not year-guarded: the launcher is version-agnostic, so one copy serves every installed Revit.
Source: "{#Bin2026}\KhBatchLauncher.exe";        DestDir: "{commonpf64}\Kanghun BIM"; Flags: ignoreversion
Source: "{#Bin2026}\KhBatchLauncher.exe.config"; DestDir: "{commonpf64}\Kanghun BIM"; Flags: ignoreversion skipifsourcedoesntexist
; The AI Bridge's MCP server, for the same reason: Claude Code starts it, and a policy that denies
; %ProgramData% would refuse the per-year copy. Version-agnostic too; the ribbon tooltip names this copy.
Source: "{#Bin2026}\KhMcp\*"; DestDir: "{commonpf64}\Kanghun BIM\KhMcp"; Flags: ignoreversion

; Support-only, read-only scripts. Installing them machine-wide makes the same diagnostic commands
; available on a user's PC even when they do not have this source checkout. Loop-TestScheduleLaunch
; is the one to run when a scheduled task reports 0x1: it proves, on that PC, whether the launch
; chain works at all — which is exactly what could not be answered remotely before it existed.
Source: "Collect-ScheduleDiagnostics.ps1";       DestDir: "{commonappdata}\Kanghun BIM\KH Batch Script Runner\Support"; Flags: ignoreversion
Source: "Collect-Diagnostics.cmd";               DestDir: "{commonappdata}\Kanghun BIM\KH Batch Script Runner\Support"; Flags: ignoreversion
Source: "Loop-TestScheduleLaunch.ps1";           DestDir: "{commonappdata}\Kanghun BIM\KH Batch Script Runner\Support"; Flags: ignoreversion

; A second copy beside the add-in itself, in a Support\ subfolder of each year's Addins directory.
; The ribbon's Diagnostic Report button resolves the collector RELATIVE TO THE LOADED ASSEMBLY —
; the same rule the scheduled-run launcher uses — so the button keeps working whichever edition is
; installed, and a dev deploy can mirror it. Revit only reads .addin manifests at the top level of
; an Addins folder, so a subfolder here is inert as far as Revit is concerned.
Source: "Collect-ScheduleDiagnostics.ps1"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024\Support"; Check: RevitInstalled('2024'); Flags: ignoreversion
Source: "Collect-Diagnostics.cmd";         DestDir: "{commonappdata}\Autodesk\Revit\Addins\2024\Support"; Check: RevitInstalled('2024'); Flags: ignoreversion
Source: "Collect-ScheduleDiagnostics.ps1"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025\Support"; Check: RevitInstalled('2025'); Flags: ignoreversion
Source: "Collect-Diagnostics.cmd";         DestDir: "{commonappdata}\Autodesk\Revit\Addins\2025\Support"; Check: RevitInstalled('2025'); Flags: ignoreversion
Source: "Collect-ScheduleDiagnostics.ps1"; DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\Support"; Check: RevitInstalled('2026'); Flags: ignoreversion
Source: "Collect-Diagnostics.cmd";         DestDir: "{commonappdata}\Autodesk\Revit\Addins\2026\Support"; Check: RevitInstalled('2026'); Flags: ignoreversion

[Registry]
; The "Open log" link on a failure notification. A toast CANNOT activate a file: URI — Windows
; accepts such a toast, shows it, then silently drops it from Notification Center — so the link
; goes through this registered scheme, handled by the launcher's narrow --open-log mode (existing
; file, report/text extension only, opened with the shell's normal handler for that type).
Root: HKLM; Subkey: "Software\Classes\khbatchlog"; ValueType: string; ValueName: ""; ValueData: "URL:KH Batch run log"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\khbatchlog"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\khbatchlog\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{commonpf64}\Kanghun BIM\KhBatchLauncher.exe"" --open-log ""%1"""
; LEGACY (1.x) - allow-listed by Verify-NoClientNames. Links written by 1.x use accbatchlog:; keep
; them opening, now through the 2.0.0 launcher (it accepts both schemes).
Root: HKLM; Subkey: "Software\Classes\accbatchlog"; ValueType: string; ValueName: ""; ValueData: "URL:KH Batch run log (1.x links)"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\accbatchlog"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKLM; Subkey: "Software\Classes\accbatchlog\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{commonpf64}\Kanghun BIM\KhBatchLauncher.exe"" --open-log ""%1"""

[Icons]
; A desktop toast needs a Start-menu shortcut with an explicit AppUserModelID to be retained in
; Windows Notification Center. These are also useful launch shortcuts.
;
; ONE ID PER YEAR since 1.3.5.4. Windows takes a notification's HEADING from the shortcut carrying
; the identity, so when all three shared "Kanghun.KhBatchRunner" it picked whichever it found
; first: a batch run from Revit 2026 announced itself as "(Revit 2024)" on a PC with both installed.
; Reported from a real run, and the wrong kind of wrong - which Revit ran a batch is exactly what
; the message is for. History now groups per year, which is the honest grouping.
Name: "{commonprograms}\Kanghun BIM\KH Batch Script Runner (Revit 2024)"; Filename: "{commonpf64}\Autodesk\Revit 2024\Revit.exe"; AppUserModelID: "Kanghun.KhBatchRunner.2024"; Check: RevitInstalled('2024')
Name: "{commonprograms}\Kanghun BIM\KH Batch Script Runner (Revit 2025)"; Filename: "{commonpf64}\Autodesk\Revit 2025\Revit.exe"; AppUserModelID: "Kanghun.KhBatchRunner.2025"; Check: RevitInstalled('2025')
Name: "{commonprograms}\Kanghun BIM\KH Batch Script Runner (Revit 2026)"; Filename: "{commonpf64}\Autodesk\Revit 2026\Revit.exe"; AppUserModelID: "Kanghun.KhBatchRunner.2026"; Check: RevitInstalled('2026')
; Reporting a failed scheduled run used to mean finding a support script under %ProgramData% and
; typing a 32-character job id. This shortcut needs neither: it collects every job on the PC and
; leaves one ZIP on the Desktop. It is the only thing a tester has to be told to click.
Name: "{commonprograms}\Kanghun BIM\Collect diagnostics report"; Filename: "{commonappdata}\Kanghun BIM\KH Batch Script Runner\Support\Collect-Diagnostics.cmd"; WorkingDir: "{commonappdata}\Kanghun BIM\KH Batch Script Runner\Support"; Comment: "Writes a diagnostic ZIP to your Desktop for the last scheduled run"

[InstallDelete]
; LEGACY (1.x) - allow-listed by Verify-NoClientNames. 2.0.0 renamed every file. The 1.x manifest
; carries the SAME AddInId as KhBatchRunner.addin, so leaving it beside the new one makes Revit
; reject one of the two as a duplicate. Remove the 1.x files before the new ones are copied.
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\AccBatchRunner.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\AccBatchRunner.dll"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\AccBatchRunner.pdb"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\AccBatchLauncher.exe"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\AccBatchLauncher.exe.config"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\AccBatchRunner.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\AccBatchRunner.dll"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\AccBatchRunner.pdb"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\AccBatchRunner.deps.json"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\AccBatchLauncher.exe"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\AccBatchLauncher.exe.config"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\AccBatchRunner.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\AccBatchRunner.dll"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\AccBatchRunner.pdb"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\AccBatchRunner.deps.json"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\AccBatchLauncher.exe"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\AccBatchLauncher.exe.config"
Type: files; Name: "{commonpf64}\Kanghun BIM\AccBatchLauncher.exe"
Type: files; Name: "{commonpf64}\Kanghun BIM\AccBatchLauncher.exe.config"
Type: files; Name: "{commonprograms}\Kanghun BIM\ACC Batch Script Runner (Revit 2024).lnk"
Type: files; Name: "{commonprograms}\Kanghun BIM\ACC Batch Script Runner (Revit 2025).lnk"
Type: files; Name: "{commonprograms}\Kanghun BIM\ACC Batch Script Runner (Revit 2026).lnk"
Type: filesandordirs; Name: "{commonappdata}\Kanghun BIM\ACC Batch Script Runner"

[UninstallDelete]
; The .addin is the same file in each year folder; remove all three on uninstall. Individual
; DLLs are removed by Inno's normal file tracking; these lines also clean the year folders'
; manifest even if the folder pre-existed.
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2024\KhBatchRunner.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2025\KhBatchRunner.addin"
Type: files; Name: "{commonappdata}\Autodesk\Revit\Addins\2026\KhBatchRunner.addin"

[Messages]
WelcomeLabel2=This will install [name] for the Autodesk Revit versions detected on this PC (2024/2025/2026 — the detected list is shown on the Ready page), into the all-users Addins folder. It installs for every user and requires admin.%n%nClose Revit before continuing so the files are not locked.

[Code]
#include "InstallerCommon.iss"

{ Abort with a clear message if no supported Revit is present (nothing would ever load the
  add-in); otherwise offer upgrade/repair/downgrade if a prior version is already installed. }
function InitializeSetup(): Boolean;
begin
  if not ConfirmRevitClosed() then
  begin
    Result := False;
    Exit;
  end;
  if DetectedRevitYears() = '' then
  begin
    { Same reasoning as ConfirmExistingInstall: a [Code] MsgBox is not covered by
      /SUPPRESSMSGBOXES, so showing one during a silent install would hang the process on an
      invisible dialog instead of failing. Log and abort with a non-zero exit code instead. }
    if WizardSilent() then
      Log('No supported Revit (2024/2025/2026) detected — aborting silent install.')
    else
      MsgBox('No supported Autodesk Revit version (2024, 2025 or 2026) was detected under ' +
             ExpandConstant('{commonpf64}\Autodesk') + '.'#13#10#13#10 +
             'Install Revit first, then run this setup again.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  Result := ConfirmExistingInstall();
end;

{ Recheck immediately before file changes in case Revit was opened after the welcome page. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RevitMustBeClosedError();
end;

{ With no components page, give the Ready page a meaningful summary: which years get the add-in. }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := 'Detected Revit versions (the add-in will be installed for these):' + NewLine +
            Space + DetectedRevitYears() + NewLine;
end;

{ Quietly uninstall the legacy per-year installs (0.1.x used a year-suffixed AppId) so the
  same add-in isn't registered twice. Reads each year's uninstall string from the registry
  and runs it silently before this install lays its files down. }
procedure RemoveLegacyPerYearInstall(Year: String);
var
  KeyName, UninstPath: String;
begin
  { Inno writes uninstall info under this key; the legacy AppId was "<GUID>-<year>" and Inno
    appends "_is1". Both HKLM views are checked since the arch may differ. }
  KeyName := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' +
             '{7E2C4D8A-3F51-4B9C-9E1D-2A6F0B4C8D31}-' + Year + '_is1';
  UninstPath := '';
  if not RegQueryStringValue(HKLM32, KeyName, 'UninstallString', UninstPath) then
    RegQueryStringValue(HKLM64, KeyName, 'UninstallString', UninstPath);
  { UninstallString is the (quoted) path to that install's unins000.exe — strip the quotes and
    run it silently, then wait for its registry key to disappear so its file deletes finish
    before this setup copies the new files (RunUninstallerAndWait — see InstallerCommon.iss). }
  RunUninstallerAndWait(RemoveQuotes(UninstPath), KeyName);
end;

{ LEGACY (1.x) - allow-listed by Verify-NoClientNames. The retired second edition was its own Inno
  install (own AppId) whose add-in shared the 2026 Addins folder and the launcher copy in Program
  Files. Its features are part of this product now, so uninstall it silently first; otherwise
  Revit would load two copies of the same commands. }
procedure RemoveRetiredEditionInstall();
var
  KeyName, UninstPath: String;
begin
  KeyName := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{4e22ffc8-8507-4a18-bd02-d6035af59cc2}_is1';
  UninstPath := '';
  if not RegQueryStringValue(HKLM64, KeyName, 'UninstallString', UninstPath) then
    RegQueryStringValue(HKLM32, KeyName, 'UninstallString', UninstPath);
  RunUninstallerAndWait(RemoveQuotes(UninstPath), KeyName);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { 0. Our tray-notification helper, if a message from an earlier run is still waiting. A running
         executable cannot be replaced, and Windows reports that as "Access is denied". }
    StopNotificationHelpers();
    { 1. Legacy per-year (0.1.x) installs of this add-in. }
    RemoveLegacyPerYearInstall('2024');
    RemoveLegacyPerYearInstall('2025');
    RemoveLegacyPerYearInstall('2026');
    { 2. A same-AppId install stranded in the other (32-bit) registry view (pre-0.6.0). }
    RemoveOtherViewInstall();
    { 3. Per-user %AppData% shadow copies (e.g. a dev deploy) that would shadow this machine
         install via the duplicate-AddInId rule — the top "installed but didn't upgrade" cause. }
    RemovePerUserCopies('KhBatchRunner', '2024');
    RemovePerUserCopies('KhBatchRunner', '2025');
    RemovePerUserCopies('KhBatchRunner', '2026');
    { 4. LEGACY (1.x): the retired second edition, and per-user copies under the 1.x file name. }
    RemoveRetiredEditionInstall();
    RemovePerUserCopies('AccBatchRunner', '2024');
    RemovePerUserCopies('AccBatchRunner', '2025');
    RemovePerUserCopies('AccBatchRunner', '2026');
    RemovePerUserCopies('AccBatchLauncher', '2024');
    RemovePerUserCopies('AccBatchLauncher', '2025');
    RemovePerUserCopies('AccBatchLauncher', '2026');
  end;
end;

{ Uninstalling hits the same wall as installing: a waiting notification holds the launcher open, and
  its file cannot be deleted while it runs. Left alone, uninstall would leave the executable behind
  and report success — and uninstall/reinstall is the first thing anyone tries when a setup fails. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopNotificationHelpers();
end;
