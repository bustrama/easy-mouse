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
#define BrokerExe "EasyMouse.Broker.exe"
; The name of the firewall rule that lets the app accept connections from the local network.
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
Name: "autostart"; Description: "{cm:StartWithWindows}"; GroupDescription: "{cm:StartupGroup}"; Flags: unchecked

[CustomMessages]
english.StartWithWindows=Start {#AppName} when I sign in to Windows
english.StartupGroup=Startup:
english.ConfiguringFirewall=Configuring the firewall...
english.RemoveDataPrompt=Also remove your paired devices and settings (%1)?%n%nChoose No to keep them, for example if you plan to install Easy Mouse again.
hebrew.StartWithWindows=להפעיל את {#AppName} בכניסה ל-Windows
hebrew.StartupGroup=הפעלה:
hebrew.ConfiguringFirewall=מגדיר את חומת האש...
hebrew.RemoveDataPrompt=למחוק גם את המכשירים המצומדים וההגדרות (%1)?%n%nבחרו "לא" כדי לשמור אותם, למשל אם אתם מתכננים להתקין את Easy Mouse שוב.

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; "Start with Windows": the same HKCU Run value the app's own switch writes, set as the user who started setup (not the elevated admin account, which can differ).
Filename: "{sys}\reg.exe"; Parameters: "add HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v EasyMouse /t REG_SZ /d ""\""{app}\{#AppExe}\"" --autostart"" /f"; Flags: runhidden runasoriginaluser; Tasks: autostart
; Firewall: this program only, Private profile only, local subnet only. Never a blanket rule. There is no fixed port in it:
; the port is a setting in the app, and the rule follows the program, so changing the port never needs a new rule.
; The old rule is removed first so an update or repair never leaves a duplicate or a stale path behind.
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#RuleName}"""; Flags: runhidden; StatusMsg: "{cm:ConfiguringFirewall}"
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#RuleName}"" dir=in action=allow program=""{app}\{#AppExe}"" protocol=TCP profile=private remoteip=LocalSubnet enable=yes"; Flags: runhidden; StatusMsg: "{cm:ConfiguringFirewall}"
; Start the app: offered at the end of an interactive install; after a silent run only when the updater asked (/RELAUNCH=1).
; runasoriginaluser: the app runs with the user's normal rights, not with the installer's admin token.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#RuleName}"""; Flags: runhidden; RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
; Setup removes exactly the files it installed. The folder itself goes only if it is then empty, so installing into a folder that
; already holds other files (for example D:\Apps) can never delete them. %APPDATA%\EasyMouse (paired devices, settings) is handled in [Code].
Type: dirifempty; Name: "{app}"

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


{ ---- The Easy Mouse Broker service (admin windows and the UAC prompt, docs/security.md). ----
  The service is registered but left OFF (start type "manual", not running): turning the feature on is the user's act, in the app's
  Settings, behind Windows' own consent prompt. An update keeps whatever the user had chosen. It is only registered when the app
  is installed under Program Files, because it runs files from the app folder as SYSTEM and a folder an ordinary user can write to would let
  that user replace them. In another folder the app works as before and the Settings switch says the helper is not installed. }

const
  BrokerService = 'EasyMouseBroker';

var
  PreviousBrokerStart: Cardinal;
  BrokerSetupFailed: Boolean;

function InProtectedFolder: Boolean;
var
  App: String;
begin
  App := Lowercase(ExpandConstant('{app}')) + '\';
  Result := (Pos(Lowercase(ExpandConstant('{commonpf64}')) + '\', App) = 1) or (Pos(Lowercase(ExpandConstant('{commonpf32}')) + '\', App) = 1);
end;

function BrokerProcessRunning: Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq {#BrokerExe}" /NH | find /I "{#BrokerExe}"',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ Stops the service and waits until its process (and the agent it started) has ended, so the files can be replaced or removed. }
procedure StopBrokerService;
var
  Code, Waited: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop ' + BrokerService, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Waited := 0;
  while BrokerProcessRunning and (Waited < 15000) do
  begin
    Sleep(500);
    Waited := Waited + 500;
  end;
end;

{ Runs sc.exe and says whether it succeeded. A failure is logged, and shown unless the install is silent (a silent update must not block). }
function RunSc(const Params: String): Boolean;
var
  Code: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then
  begin
    Log('sc.exe ' + Params + ' failed with code ' + IntToStr(Code));
    BrokerSetupFailed := True;
  end;
end;

{ The folder the broker runs from must be writable only by SYSTEM and Administrators: set it explicitly and without inheritance
  (Users read and run only), instead of trusting that the folder name implies it. If this fails the service is not registered. }
function LockDownAppFolder: Boolean;
var
  Code: Integer;
  Dir: String;
begin
  Dir := ExpandConstant('{app}');
  { 1. The folder itself: no inheritance, SYSTEM and Administrators full, Users read and run, all passed on to what is created inside.
    (Not with /T: an entry meant for folders, put on a file, does not apply to the file and would lock everybody out of it.) }
  Result := Exec(ExpandConstant('{sys}\icacls.exe'),
    '"' + Dir + '" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-32-545:(OI)(CI)RX" /C /Q',
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  { 2. Everything already inside: back to "inherit from the folder", which is exactly that list. }
  if Result then
    Result := Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '\*" /reset /T /C /Q', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  { 3. Owned by Administrators, not by whichever account ran Setup. }
  if Result then
    Result := Exec(ExpandConstant('{sys}\icacls.exe'), '"' + Dir + '" /setowner "*S-1-5-32-544" /T /C /Q', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
  if not Result then Log('icacls on the app folder failed with code ' + IntToStr(Code));
end;
procedure RegisterBrokerService;
var
  Start: Cardinal;
  Exists: Boolean;
  BinPath: String;
begin
  if not InProtectedFolder then Exit;
  if not LockDownAppFolder then
  begin
    BrokerSetupFailed := True;
    Exit;
  end;
  Exists := RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + BrokerService, 'Start', Start);
  BinPath := 'binPath= "\"' + ExpandConstant('{app}\{#BrokerExe}') + '\" --service"';
  if not Exists then
    RunSc('create ' + BrokerService + ' ' + BinPath + ' start= demand DisplayName= "Easy Mouse Broker"')
  else
    RunSc('config ' + BrokerService + ' ' + BinPath); { also covers an app folder that moved }
  RunSc('description ' + BrokerService + ' "Lets the Easy Mouse phone remote control administrator windows and the UAC prompt, only when you turn that on in Easy Mouse Settings. Does nothing otherwise."');
  RunSc('failure ' + BrokerService + ' reset= 86400 actions= restart/5000');
  { An update of an installation where the user had switched it on: start it again (the new files are in place). }
  if Exists and (PreviousBrokerStart = 2) then RunSc('start ' + BrokerService);
end;

{ Whatever happened, a broker the user had switched on is started again at the end (a failed or cancelled update must not leave it stopped). }
procedure DeinitializeSetup;
var
  Code: Integer;
  Start: Cardinal;
begin
  if (PreviousBrokerStart = 2) and RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + BrokerService, 'Start', Start) then
    Exec(ExpandConstant('{sys}\sc.exe'), 'start ' + BrokerService, '', SW_HIDE, ewWaitUntilTerminated, Code);
  if BrokerSetupFailed and (not WizardSilent) then
    MsgBox('The Easy Mouse Broker service could not be set up. Easy Mouse works as before; control of administrator windows stays unavailable until you reinstall.', mbInformation, MB_OK);
end;
procedure RemoveBrokerService;
var
  Code: Integer;
  Start: Cardinal;
begin
  if not RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + BrokerService, 'Start', Start) then Exit;
  StopBrokerService;
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete ' + BrokerService, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then RegisterBrokerService;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningApp;
  { Remember whether the user had turned the broker on, then stop it so its files can be replaced. }
  if not RegQueryDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Services\' + BrokerService, 'Start', PreviousBrokerStart) then PreviousBrokerStart := 0;
  StopBrokerService;
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
  if CurUninstallStep = usUninstall then
  begin
    RemoveAutostartValue;
    RemoveBrokerService; { before the files go: stop it, wait for its process to end, delete the service }
  end;
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{userappdata}\EasyMouse');
  if not DirExists(Data) then Exit;
  { /SILENT and /VERYSILENT never delete user data. Add /REMOVEDATA=1 to a silent uninstall to remove it. }
  if UninstallSilent then
  begin
    if ExpandConstant('{param:REMOVEDATA|0}') = '1' then DelTree(Data, True, True, True);
    Exit;
  end;
  if MsgBox(FmtMessage(CustomMessage('RemoveDataPrompt'), [Data]),
            mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    DelTree(Data, True, True, True);
end;
