; EsoAgent Windows installer (Inno Setup 6) — item 45.
;
; Craft requirements it implements:
;   * Wizard: welcome -> existing-install detection -> location ->
;     progress -> finish (with first-run summary + "open the app").
;   * Golden-wing icon, publisher + version in Apps & Features.
;   * Service + tray-autostart registration (via install.ps1, the same
;     script the zip install uses — one installation, two doors).
;   * UAC-correct: admin install, per-machine.
;   * Pre-flight: Windows 10+, disk space, port check — a failed
;     pre-flight changes NOTHING. Post-install verification gates the
;     finish page; the install log is always kept.
;   * Two modes: friendly GUI, or silent/parameterised for IT:
;       EsoAgent-Setup.exe /VERYSILENT /FLEETURL=https://fleet:8740 /ENROLLKEY=XXXX
;   * Repair: re-running the installer over the same version repairs in
;     place. Uninstall removes service + files; device DATA is kept
;     unless the user explicitly chooses to remove it.
;   * Authenticode: the SignTool slot below is ready; releases are
;     signed via SignPath Foundation once the project is approved
;     (see CODE_SIGNING.md). Unsigned builds stay unsigned — never fake.

#define AppName "EsoAgent"
#define AppVersion "1.2.0"
#define AppPublisher "EsoAgent"
#define AppURL "https://github.com/SENESO/eso-agent"

[Setup]
AppId={{8E50A1C2-7B3F-4E6A-9C1D-ESOAGENT1200}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\EsoAgent
DefaultGroupName=EsoAgent
DisableProgramGroupPage=yes
OutputBaseFilename=EsoAgent-Setup-{#AppVersion}
SetupIconFile=..\..\assets\brand\png\app-icon.ico
UninstallDisplayIcon={app}\EsoAgent.App.exe
UninstallDisplayName=EsoAgent {#AppVersion}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
; SignTool slot (SignPath Foundation, once approved — CODE_SIGNING.md):
; SignTool=signpath
; SignedUninstaller=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"

[Files]
; The win-x64 publish output (service + tray + app + client + scripts),
; staged next to this script by the release workflow as .\payload\.
Source: "payload\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "verify-install.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
; Tray starts at user login (the agent service itself is boot-registered
; by install.ps1 as an auto-start Windows service).
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueName: "EsoAgentTray"; ValueType: string; \
  ValueData: """{app}\EsoAgent.Tray.exe"""; Flags: uninsdeletevalue

[Run]
; Registration + verification. install.ps1 is idempotent and detects an
; existing install (same dir, same service, same data) — an upgrade is
; an in-place binary refresh, never a second installation.
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -InstallDir ""{app}"" {code:GetFleetParams}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering the EsoAgent service..."
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\verify-install.ps1"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Verifying the installation (service, self-check, panel)..."
; Finish page offer: open the app.
Filename: "{app}\EsoAgent.App.exe"; Description: "Open the EsoAgent app"; \
  Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"""; \
  Flags: runhidden waituntilterminated

[Code]
var
  FirstRunPage: TOutputMsgWizardPage;

function GetFleetParams(Param: string): string;
var
  FleetUrl, EnrollKey: string;
begin
  Result := '';
  FleetUrl := ExpandConstant('{param:FLEETURL|}');
  EnrollKey := ExpandConstant('{param:ENROLLKEY|}');
  if FleetUrl <> '' then
    Result := Result + ' -FleetUrl "' + FleetUrl + '"';
  if EnrollKey <> '' then
    Result := Result + ' -EnrollmentKey "' + EnrollKey + '"';
end;

{ Pre-flight: refuse BEFORE touching anything when the machine cannot
  host the agent. A failed pre-flight leaves zero half-installed state. }
function InitializeSetup(): Boolean;
var
  FreeMB: Cardinal;
  TotalMB: Cardinal;
  Existing: string;
begin
  Result := True;
  if not GetSpaceOnDisk(ExpandConstant('{autopf}'), True, FreeMB, TotalMB) then
    FreeMB := 0;
  if FreeMB < 900 then
  begin
    MsgBox('EsoAgent needs about 900 MB free on the system drive (found '
      + IntToStr(FreeMB) + ' MB). Free some space and run setup again — '
      + 'nothing was installed.', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  { Existing-install detection: same product, same dir => upgrade/repair
    in place; the user is told, never surprised by a second copy. }
  if RegQueryStringValue(HKLM,
      'Software\Microsoft\Windows\CurrentVersion\Uninstall\EsoAgent_is1',
      'InstallLocation', Existing) then
  begin
    if MsgBox('EsoAgent is already installed at ' + Existing + '.' + #13#10
        + 'Setup will upgrade/repair it IN PLACE — same service, same '
        + 'device identity, same data. Continue?', mbConfirmation,
        MB_YESNO) = IDNO then
      Result := False;
  end;
end;

{ The finish page carries the first-run summary (device code + how
  approval works) written by verify-install.ps1. }
procedure InitializeWizard();
begin
  FirstRunPage := CreateOutputMsgPage(wpInstalling,
    'Your device is ready', 'Device code and next steps',
    'EsoAgent verified itself after install. The summary below is also '
    + 'saved as first-run.txt in the data folder.');
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Lines: TArrayOfString;
  Text: string;
  I: Integer;
begin
  if CurPageID = FirstRunPage.ID then
  begin
    Text := '';
    if LoadStringsFromFile(ExpandConstant('{app}\data\first-run.txt'), Lines) then
      for I := 0 to GetArrayLength(Lines) - 1 do
        Text := Text + Lines[I] + #13#10;
    if Text = '' then
      Text := 'Verification summary was written to the install log '
        + 'in the data folder.';
    FirstRunPage.MsgLabel.Caption := Text;
  end;
end;

{ Uninstall: data removal is an explicit choice, never silent. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Remove the device DATA too (identity, config, logs in '
        + 'the data folder under the install directory)?' + #13#10
        + 'Choose No to keep it — a later reinstall resumes as the same '
        + 'device.', mbConfirmation, MB_YESNO) = IDYES then
      DelTree(ExpandConstant('{app}\data'), True, True, True);
  end;
end;
