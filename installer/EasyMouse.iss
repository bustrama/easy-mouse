; Easy Mouse installer (Inno Setup 6). Per-machine, installed as administrator.
;
; Build (from the repository root, after `dotnet publish`, see docs/releasing.md):
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=publish\win-x64 installer\EasyMouse.iss
;
; Silent update (what the in-app updater runs after it has verified this file):
;   EasyMouse-Setup-1.2.3.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH=1

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
; Windows version resources need four numbers: strip any "-prerelease" and append ".0".
#define NumericVersion Copy(AppVersion, 1, Pos("-", AppVersion + "-") - 1) + ".0"

#define AppName "Easy Mouse"
#define AppExe "EasyMouse.exe"
; The one fixed port the app listens on by default, and the name of the firewall rule that allows it.
#define Port "8765"
#define RuleName "Easy Mouse (phone remote)"

[Setup]
; Never change this GUID: it is how Windows (and an update) recognises an existing installation.
AppId={{7F3C2E1A-5B84-4D9E-A1C6-2E9B0D4F7A35}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Easy Mouse contributors
AppPublisherURL=https://github.com/bustrama/easy-mouse
AppSupportURL=https://github.com/bustrama/easy-mouse/issues
VersionInfoVersion={#NumericVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoDescription={#AppName} setup
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
SetupIconFile=..\assets\icon\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=EasyMouse-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; The running app is asked to exit by [Code] below (it ignores window-close, it lives in the tray).
; This is the safety net for anything still holding files.
CloseApplications=yes
RestartApplications=no
; Re-use the choices from the previous install on update (desktop icon, start with Windows).
UsePreviousTasks=yes
UsePreviousAppDir=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "hebrew"; MessagesFile: "compiler:Languages\Hebrew.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; "Start with Windows": the same HKCU Run value the app's own switch writes, set as the user who started setup (not the elevated admin account, which can differ).
Filename: "{sys}\reg.exe"; Parameters: "add HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v EasyMouse /t REG_SZ /d ""\""{app}\{#AppExe}\"" --autostart"" /f"; Flags: runhidden runasoriginaluser; Tasks: autostart
; Firewall: this program only, TCP {#Port} only, Private profile only, local subnet only. Never a blanket rule.
; The old rule is removed first so an update or repair never leaves a duplicate or a stale path behind.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#RuleName}"""; Flags: runhidden; StatusMsg: "Configuring the firewall..."
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#RuleName}"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP localport={#Port} profile=private remoteip=LocalSubnet enable=yes"; Flags: runhidden; StatusMsg: "Configuring the firewall..."
; Start the app: offered at the end of an interactive install; after a silent run only when the updater asked (/RELAUNCH=1).
; runasoriginaluser: the app runs with the user's normal rights, not with the installer's admin token.
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#RuleName}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
; Anything the app wrote next to its files. %APPDATA%\EasyMouse (paired devices, settings) is handled in [Code].
Type: filesandordirs; Name: "{app}"

[Code]
function RelaunchRequested: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

{ True while an EasyMouse.exe process exists. }
function AppIsRunning: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq {#AppExe}" /NH | find /I "{#AppExe}"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ Ask a running copy to exit on its own (it lives in the tray and ignores window-close), then wait for it. }
procedure QuitRunningApp;
var
  Code, Waited: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if not FileExists(Exe) then Exit;
  if not AppIsRunning then Exit;
  Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while AppIsRunning and (Waited < 20000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  QuitRunningApp;
  Result := True;
end;

{ Removes the "Start with Windows" value. Uninstall cannot run code as the original user, so this is the elevated account's
  HKCU: the same person on a normal single-user PC. A leftover value for another account is harmless (it points to a missing file). }
procedure RemoveAutostartValue;
begin
  RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'EasyMouse');
end;

{ Uninstall keeps %APPDATA%\EasyMouse (paired devices and settings) unless the user asks to remove it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usUninstall then RemoveAutostartValue;
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{userappdata}\EasyMouse');
  if not DirExists(Data) then Exit;
  { /SILENT and /VERYSILENT never delete user data. Add /REMOVEDATA=1 to a silent uninstall to remove it. }
  if UninstallSilent then
  begin
    if ExpandConstant('{param:REMOVEDATA|0}') = '1' then DelTree(Data, True, True, True);
    Exit;
  end;
  if MsgBox('Also remove your paired devices and settings (' + Data + ')?' + #13#10#13#10 +
            'Choose No to keep them, for example if you plan to install Easy Mouse again.',
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;
