; Inno Setup script for the KH Batch Controller — the standalone desktop app.
;
; WHY AN INSTALLER AT ALL. The controller shipped as a loose KhBatchController.exe, which is the
; shape managed desktops treat with the most suspicion: SmartScreen warns on it, "downloaded exe"
; policies block it, and there is no Start-menu entry, no version in Add/Remove Programs, and no
; clean uninstall. A proper install fixes all of that.
;
; PER-USER, NO ADMIN. PrivilegesRequired=lowest installs into %LOCALAPPDATA%\Programs, the same
; pattern the VS Code "User Installer" uses, so someone without local admin can install it. That is
; the opposite choice from the Revit add-in installers, which are deliberately all-users because
; they write into the machine-wide Revit Addins folder.
;
; SELF-CONTAINED. The published tree carries the .NET 8 runtime with it (see
; Build-Installer-Controller.ps1), so there is no "install .NET first" step — another thing that
; needs IT on a locked-down machine.
;
; WHAT THIS DOES NOT DO: it is not a signature. If the estate enforces application allowlisting
; (AppLocker/WDAC) or blocks unsigned binaries outright, an installer does not change that — an
; Authenticode certificate does. See SignTool in Build-Installer-Controller.ps1's notes.

#define AppName "KH Batch Controller"
; The CONTROLLER's own version — first release. Not the add-in's: the shared batch engine version
; is reported separately in the About box. See src\KhBatchController\ControllerProduct.cs.
#define AppVersion "0.0.2"
#define AppExe "KhBatchController.exe"
; Self-contained publish output — see Build-Installer-Controller.ps1.
#define Bin "..\src\KhBatchController\bin\Release\publish"

[Setup]
; Own AppId → its own Add/Remove Programs entry, independent of the Revit add-ins.
AppId={{b7a41f52-3c96-4a0e-9d7f-2f1c6e8a45d3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Kanghun BIM
AppPublisherURL=https://kanghun.example
DefaultDirName={autopf}\KhBatchController
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; No admin: {autopf} resolves to %LOCALAPPDATA%\Programs under "lowest", so the whole install
; lands in the user's own profile and Windows never shows a UAC prompt.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=Output
OutputBaseFilename=KhBatchController-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
; The controller holds its own exe while running; offer to close it rather than failing the copy.
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The whole publish tree: the app, its dependencies, and the bundled .NET runtime.
Source: "{#Bin}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; Support-only, read-only scripts. The controller is the one edition someone can install without
; admin, so it carries its own copy rather than relying on a Revit add-in install being present.
Source: "Collect-ScheduleDiagnostics.ps1"; DestDir: "{app}\Support"; Flags: ignoreversion
Source: "Collect-Diagnostics.cmd";         DestDir: "{app}\Support"; Flags: ignoreversion

[Icons]
; The explicit AppUserModelID makes completion/retry reminders real, retained Notification Center
; items. The desktop shortcut carries the same identity so either launch path groups correctly.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Kanghun.KhBatchController"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "Kanghun.KhBatchController"; Tasks: desktopicon
; One click after a run: collects the launch chain, task status, logs and journals into a single ZIP
; on the Desktop. No job id to look up, no elevation needed.
Name: "{autoprograms}\Collect diagnostics report"; Filename: "{app}\Support\Collect-Diagnostics.cmd"; WorkingDir: "{app}\Support"; Comment: "Writes a diagnostic ZIP to your Desktop for the last scheduled or controller run"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// The controller drives Revit: it borrows the Forma token from a hidden Revit and launches Revit
// workers for a batch. Without one installed it can still open, but nothing it does will work —
// so say that up front rather than let the first run fail confusingly.
function RevitIsInstalled(): Boolean;
var
  Year: Integer;
begin
  Result := False;
  for Year := 2024 to 2026 do
    if DirExists(ExpandConstant('{commonpf64}') + '\Autodesk\Revit ' + IntToStr(Year)) then
    begin
      Result := True;
      Exit;
    end;
end;

function InitializeSetup(): Boolean;
var
  Msg, Gap: String;
begin
  Result := True;
  if RevitIsInstalled() then Exit;

  // Built by concatenation, and never with a line STARTING in '#': the Inno preprocessor reads a
  // leading '#' as a directive, so an inline #13#10 at the head of a line fails the compile.
  Gap := Chr(13) + Chr(10) + Chr(13) + Chr(10);
  Msg := 'No Revit 2024-2026 was found on this PC.' + Gap +
         'KH Batch Controller needs Revit installed and signed in to Forma: it borrows the ' +
         'Autodesk sign-in from a hidden Revit, and runs batches in Revit workers.' + Gap +
         'Install anyway?';
  Result := MsgBox(Msg, mbConfirmation, MB_YESNO) = IDYES;
end;
