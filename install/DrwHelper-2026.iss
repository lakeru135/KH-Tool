; Inno Setup script for DRW Helper (the drawing-production ribbon) — REVIT 2024 + 2026 + 2027.
; (The file keeps its old -2026 name so the build scripts that point at it need not move; 2024 was
; added in 0.4.0.0, 2027 in 2.1.0.2. Revit 2025 is not offered: its bundled families do not exist yet.)
;
; DRW Helper is a standalone product built from the same source as the KH Batch Script Runner via
; -p:Edition=DRW: its own assembly (DrwHelper.dll), manifest (DrwHelper.addin, distinct AddInId)
; and this installer (own AppId). One installer COULD carry several ribbons (one .iss laying down
; several manifests); DRW ships on its own for now so it can be versioned and rolled out separately.
;
; All-users install (requires admin) into the machine-wide Revit Addins folders — same rationale as
; the other installers. Each year is laid down only where that Revit is installed; with neither
; found, 2026 is pre-staged as before (IT installing ahead of Revit).
;
; UNLIKE Full, only the manifest sits in the Addins root. The dll and everything it needs go
; into Addins\<year>\DrwHelper\ (the manifest's <Assembly> is DrwHelper\DrwHelper.dll). Nothing is
; shared with the batch runner's flat layout, so uninstalling DRW can never remove a file Full
; still loads, and Full cannot overwrite DRW's copies. For the same reason DRW does NOT install
; the Program Files launcher copy or the khbatchlog: protocol handler — those belong to the batch
; runner's installers; DRW's launcher copy sits beside its own dll.
;
; Build the DRW Release bin trees first, then compile — see install\Build-Installer-Drw-2026.ps1.

#define AppName "DRW Helper"
#define AppVersion "2.1.0.2"
; AppId (single-brace literal, matches [Setup]) + version, consumed by InstallerCommon.iss.
#define InstAppId "{753E7E24-9663-4DFE-844D-52D0F8648C85}"
#define InstAppVersion AppVersion
; DRW Release build output (src\KhBatchRunner\bin\Release\DRW\<year>\).
#define Bin2024 "..\src\KhBatchRunner\bin\Release\DRW\2024"
#define Addins2024 "{commonappdata}\Autodesk\Revit\Addins\2024"
#define Payload2024 "{commonappdata}\Autodesk\Revit\Addins\2024\DrwHelper"
#define Bin2026 "..\src\KhBatchRunner\bin\Release\DRW\2026"
#define Addins2026 "{commonappdata}\Autodesk\Revit\Addins\2026"
#define Payload2026 "{commonappdata}\Autodesk\Revit\Addins\2026\DrwHelper"
#define Bin2027 "..\src\KhBatchRunner\bin\Release\DRW\2027"
#define Addins2027 "{commonappdata}\Autodesk\Revit\Addins\2027"
#define Payload2027 "{commonappdata}\Autodesk\Revit\Addins\2027\DrwHelper"

[Setup]
; Own AppId (distinct from every other Kanghun installer) → its own Add/Remove entry.
AppId={{753E7E24-9663-4DFE-844D-52D0F8648C85}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Kanghun BIM
CreateAppDir=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=DrwHelper-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
; Never let Restart Manager close Revit — see the other installers.
CloseApplications=no
RestartApplications=no

[Files]
; --- Revit 2024 (net48 — *.dll ships DrwHelper + bundled System.Text.Json & transitives) ---
Source: "{#Bin2024}\DrwHelper.addin";      DestDir: "{#Addins2024}";  Check: DrwYear('2024'); Flags: ignoreversion
Source: "{#Bin2024}\*.dll";                DestDir: "{#Payload2024}"; Check: DrwYear('2024'); Flags: ignoreversion
Source: "{#Bin2024}\DrwHelper.pdb";        DestDir: "{#Payload2024}"; Check: DrwYear('2024'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2024}\KhBatchLauncher.exe";        DestDir: "{#Payload2024}"; Check: DrwYear('2024'); Flags: ignoreversion
Source: "{#Bin2024}\KhBatchLauncher.exe.config"; DestDir: "{#Payload2024}"; Check: DrwYear('2024'); Flags: ignoreversion skipifsourcedoesntexist
; --- Revit 2026 ---
Source: "{#Bin2026}\DrwHelper.addin";      DestDir: "{#Addins2026}";  Check: DrwYear('2026'); Flags: ignoreversion
Source: "{#Bin2026}\DrwHelper.dll";        DestDir: "{#Payload2026}"; Check: DrwYear('2026'); Flags: ignoreversion
Source: "{#Bin2026}\DrwHelper.deps.json";  DestDir: "{#Payload2026}"; Check: DrwYear('2026'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2026}\DrwHelper.pdb";        DestDir: "{#Payload2026}"; Check: DrwYear('2026'); Flags: ignoreversion skipifsourcedoesntexist
; Scheduled-run launcher, resolved relative to the loaded assembly (DRW's Batch Run can save a
; schedule and split a large run into groups).
Source: "{#Bin2026}\KhBatchLauncher.exe";        DestDir: "{#Payload2026}"; Check: DrwYear('2026'); Flags: ignoreversion
Source: "{#Bin2026}\KhBatchLauncher.exe.config"; DestDir: "{#Payload2026}"; Check: DrwYear('2026'); Flags: ignoreversion skipifsourcedoesntexist
; --- Revit 2027 (net10) ---
Source: "{#Bin2027}\DrwHelper.addin";      DestDir: "{#Addins2027}";  Check: DrwYear('2027'); Flags: ignoreversion
Source: "{#Bin2027}\DrwHelper.dll";        DestDir: "{#Payload2027}"; Check: DrwYear('2027'); Flags: ignoreversion
Source: "{#Bin2027}\DrwHelper.deps.json";  DestDir: "{#Payload2027}"; Check: DrwYear('2027'); Flags: ignoreversion skipifsourcedoesntexist
Source: "{#Bin2027}\DrwHelper.pdb";        DestDir: "{#Payload2027}"; Check: DrwYear('2027'); Flags: ignoreversion skipifsourcedoesntexist
; Scheduled-run launcher, resolved relative to the loaded assembly (DRW's Batch Run can save a
; schedule and split a large run into groups).
Source: "{#Bin2027}\KhBatchLauncher.exe";        DestDir: "{#Payload2027}"; Check: DrwYear('2027'); Flags: ignoreversion
Source: "{#Bin2027}\KhBatchLauncher.exe.config"; DestDir: "{#Payload2027}"; Check: DrwYear('2027'); Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; Desktop-toast identity with the Revit-year suffix RunNotification.PreferredToastAppId() asks for.
Name: "{commonprograms}\Kanghun BIM\DRW Helper (Revit 2024)"; Filename: "{commonpf64}\Autodesk\Revit 2024\Revit.exe"; AppUserModelID: "Kanghun.DrwHelper.2024"; Check: DrwYear('2024')
Name: "{commonprograms}\Kanghun BIM\DRW Helper (Revit 2026)"; Filename: "{commonpf64}\Autodesk\Revit 2026\Revit.exe"; AppUserModelID: "Kanghun.DrwHelper.2026"; Check: DrwYear('2026')
Name: "{commonprograms}\Kanghun BIM\DRW Helper (Revit 2027)"; Filename: "{commonpf64}\Autodesk\Revit 2027\Revit.exe"; AppUserModelID: "Kanghun.DrwHelper.2027"; Check: DrwYear('2027')

[UninstallDelete]
Type: files; Name: "{#Addins2024}\DrwHelper.addin"
Type: dirifempty; Name: "{#Payload2024}"
Type: files; Name: "{#Addins2026}\DrwHelper.addin"
Type: dirifempty; Name: "{#Payload2026}"
Type: files; Name: "{#Addins2027}\DrwHelper.addin"
Type: dirifempty; Name: "{#Payload2027}"

[Messages]
WelcomeLabel2=This will install [name] for Autodesk Revit 2024, 2026 and 2027 (the versions found on this PC), into the all-users Addins folder — it installs for every user and requires admin.%n%nClose Revit before continuing so the files are not locked.

[Code]
#include "InstallerCommon.iss"

{ A year gets DRW Helper where its Revit is installed. With none found, 2026 is pre-staged —
  what this installer did when it was 2026-only. }
function AnyDrwRevit(): Boolean;
begin
  Result := RevitInstalled('2024') or RevitInstalled('2026') or RevitInstalled('2027');
end;

function DrwYear(Year: String): Boolean;
begin
  if AnyDrwRevit() then
    Result := RevitInstalled(Year)
  else
    Result := (Year = '2026');
end;

{ Same start-up checks as the batch runner: Revit closed, warn (not block) when no Revit 2024, 2026
  or 2027 is installed so IT can pre-stage, then offer upgrade/repair if already installed. }
function InitializeSetup(): Boolean;
begin
  Result := True;
  if not ConfirmRevitClosed() then
  begin
    Result := False;
    Exit;
  end;
  if not AnyDrwRevit() then
    if WizardSilent() then
      Log('No Revit 2024, 2026 or 2027 detected — continuing silent install for 2026 (pre-staging).')
    else if MsgBox('No Autodesk Revit 2024, 2026 or 2027 was detected on this PC.'#13#10#13#10 +
                   'Install for Revit 2026 anyway?',
                   mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
      Exit;
    end;
  Result := ConfirmExistingInstall();
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := RevitMustBeClosedError();
end;

{ With no components page, the Ready page says which years get the add-in. }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Years: String;
begin
  Years := '';
  if DrwYear('2024') then Years := '2024';
  if DrwYear('2026') then
  begin
    if Years <> '' then Years := Years + ', ';
    Years := Years + '2026';
  end;
  if DrwYear('2027') then
  begin
    if Years <> '' then Years := Years + ', ';
    Years := Years + '2027';
  end;
  Result := 'DRW Helper will be installed for Revit:' + NewLine + Space + Years + NewLine;
end;

{ Clear a same-AppId install stranded in the other registry view, and per-user dev manifests
  (DrwHelper.addin in %AppData%) that would claim the same AddInId as this machine install. }
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    { A waiting notification keeps KhBatchLauncher.exe running, and a running exe cannot be
      replaced. DRW's copy has the same image name as the batch runner's. }
    StopNotificationHelpers();
    RemoveOtherViewInstall();
    RemovePerUserCopies('DrwHelper', '2024');
    RemovePerUserCopies('DrwHelper', '2026');
    RemovePerUserCopies('DrwHelper', '2027');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    StopNotificationHelpers();
end;
