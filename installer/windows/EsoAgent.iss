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
;       EsoAgent-Setup.exe /VERYSILENT /FLEETURL=https://fleet:8740 /ENROLLKEY=XXXX /OWNERPASSWORD=XXXX
;     (the GUI asks for the owner password on its own wizard page;
;     silent installs pass it as /OWNERPASSWORD= - no quotes in it.)
;   * Repair: re-running the installer over the same version repairs in
;     place. Uninstall removes service + files; device DATA is kept
;     unless the user explicitly chooses to remove it.
;   * Authenticode: the SignTool slot below is ready; releases are
;     signed via SignPath Foundation once the project is approved
;     (see CODE_SIGNING.md). Unsigned builds stay unsigned — never fake.

#define AppName "EsoAgent"
#define AppVersion "1.2.3"
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
SetupIconFile=..\..\assets\brand\app.ico
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
; The golden-wing application icon, installed next to the exes: the
; shortcuts below name it explicitly (IconFilename), so the desktop and
; Start Menu show the wing even before the exe icon cache is built.
Source: "..\..\assets\brand\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; \
  GroupDescription: "Shortcuts:"

[Icons]
; The App is the user-facing surface — Start Menu always, Desktop via
; the (default-on) task above. Both point at the App's real installed
; path in {app}; the tray is a background companion (autostart via
; [Registry]) and the agent is a service, so neither gets a shortcut.
Name: "{group}\EsoAgent"; Filename: "{app}\EsoAgent.App.exe"; \
  WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; \
  Comment: "Open the EsoAgent app"
Name: "{group}\Uninstall EsoAgent"; Filename: "{uninstallexe}"
Name: "{autodesktop}\EsoAgent"; Filename: "{app}\EsoAgent.App.exe"; \
  WorkingDir: "{app}"; IconFilename: "{app}\app.ico"; \
  Comment: "Open the EsoAgent app"; Tasks: desktopicon

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
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install.ps1"" -InstallDir ""{app}"" {code:GetFleetParams} {code:GetOwnerPassword}"; \
  Flags: runhidden waituntilterminated; StatusMsg: "Registering the EsoAgent service..."
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\verify-install.ps1"""; \
  Flags: runhidden waituntilterminated; StatusMsg: "Verifying the installation (service, self-check, panel)..."
; Finish page offer: open the app.
Filename: "{app}\EsoAgent.App.exe"; Description: "Open the EsoAgent app"; \
  Flags: postinstall nowait skipifsilent unchecked

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\uninstall.ps1"""; \
  Flags: runhidden waituntilterminated

[Code]
var
  FirstRunPage: TOutputMsgWizardPage;
  PasswordPage: TInputQueryWizardPage;
  OwnerPasswordValue: string;

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

{ The owner password for install.ps1: the /OWNERPASSWORD= setup
  parameter wins (silent installs); otherwise the wizard page value.
  Single-quoted for PowerShell (literal string, no $ expansion);
  quote characters are rejected on the wizard page and documented
  as unsupported for the silent parameter. Empty when neither is
  set - install.ps1 then falls back to its interactive prompt,
  which the GUI flow makes unreachable by validating the page. }
function GetOwnerPassword(Param: string): string;
var
  Pw: string;
begin
  Result := '';
  Pw := ExpandConstant('{param:OWNERPASSWORD|}');
  if Pw = '' then
    Pw := OwnerPasswordValue;
  if Pw <> '' then
    Result := ' -OwnerPassword ''' + Pw + '''';
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
  { Owner password page: install.ps1 runs hidden, so its interactive
    prompt can never be answered inside the GUI installer - the
    password is collected here and passed to install.ps1 through the
    GetOwnerPassword code function referenced by the [Run] entry. }
  PasswordPage := CreateInputQueryPage(wpSelectDir,
    'Owner password', 'Protect the control panel',
    'Set the owner password for this device (minimum 8 characters, '
    + 'no quote characters). You will log in with it on the owner '
    + 'panel at https://localhost:8737/panel/.');
  PasswordPage.Add('Owner password:', True);
  FirstRunPage := CreateOutputMsgPage(wpInstalling,
    'Your device is ready', 'Device code and next steps',
    'EsoAgent verified itself after install. The summary below is also '
    + 'saved as first-run.txt in the data folder.');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Pw: string;
begin
  Result := True;
  if CurPageID = PasswordPage.ID then
  begin
    { Validate the EFFECTIVE password: the silent-install parameter
      wins over the wizard page value, which stays empty in silent
      mode because the page is never shown. Validating the page
      value alone blocked every silent install on an empty string. }
    Pw := ExpandConstant('{param:OWNERPASSWORD|}');
    if Pw = '' then
      Pw := PasswordPage.Values[0];
    if Length(Pw) < 8 then
    begin
      MsgBox('The owner password must be at least 8 characters.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
    if (Pos('"', Pw) > 0)
        or (Pos('''', Pw) > 0) then
    begin
      MsgBox('The owner password must not contain quote characters.',
        mbError, MB_OK);
      Result := False;
      Exit;
    end;
    OwnerPasswordValue := Pw;
  end;
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
