; Inno Setup script for AorusLcd - builds a standard Windows installer (setup.exe)
; with a Program Files install, Start Menu shortcut, and Add/Remove Programs entry.
;
; Build:  iscc /DMyAppVersion=1.2.3 installer\AorusLcd.iss
; Expects the self-contained GUI publish (GUI exe + bundled service exe) at
; publish\self-contained\ (see release workflow / README).

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define MyAppName "AorusLcd"
#define MyAppPublisher "CodeTorch.ai"
#define MyAppPublisherURL "https://codetorch.ai"
#define MyAppURL "https://github.com/JustinMDotNet/AorusLcd"
#define MyAppExeName "AorusLcd.Gui.exe"
#define MyServiceName "AorusLcdFeed"

[Setup]
; A stable AppId ties upgrades and the Add/Remove Programs entry together across versions.
AppId={{0BE940C9-EACC-4BBC-8C28-4631D8B4296D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppPublisherURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName} {#MyAppVersion}
OutputBaseFilename=AorusLcd-{#MyAppVersion}-setup
OutputDir=output
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Installing into Program Files and cleaning up the service on uninstall need admin.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\src\AorusLcd.Gui\Assets\aoruslcd.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Package the entire self-contained publish output (GUI exe + bundled service exe).
; PDBs shipped by native packages (SkiaSharp/HarfBuzz) are excluded to keep the installer lean.
Source: "..\publish\self-contained\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop and remove the background service if present. Run via cmd with `& exit 0`
; so a missing service (non-zero sc exit) never triggers an Inno error prompt.
Filename: "{cmd}"; Parameters: "/c ""sc stop {#MyServiceName} & exit 0"""; Flags: runhidden; RunOnceId: "StopAorusLcdFeed"
Filename: "{cmd}"; Parameters: "/c ""sc delete {#MyServiceName} & exit 0"""; Flags: runhidden; RunOnceId: "DeleteAorusLcdFeed"

[UninstallDelete]
; Remove the machine-wide files the app created outside {app}: the installed
; service binary plus its config/log under %ProgramData%\AorusLcd.
Type: filesandordirs; Name: "{commonappdata}\AorusLcd"

[Code]
{ The background service runs from a copy under %ProgramData%\AorusLcd\bin (placed
  there by the app's "Install service" step), which the installer's [Files] section
  does not touch. On an upgrade, refresh that copy from the freshly installed binary
  so the running service - and every update path, including the in-app updater that
  invokes this installer - picks up the new build. Fresh installs skip this: the
  service is not registered until the user installs it from the Device tab. }

const
  ServiceName = '{#MyServiceName}';

function ServiceIsInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  { `sc query` exits 0 when the service exists (in any state), 1060 when it does not. }
  Result := Exec(ExpandConstant('{cmd}'), '/c sc query ' + ServiceName, '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ServiceInState(const State: String): Boolean;
var
  ResultCode: Integer;
begin
  { `find` exits 0 when the state word appears in `sc query` output, 1 otherwise. }
  Result := Exec(ExpandConstant('{cmd}'),
    '/c sc query ' + ServiceName + ' | find "' + State + '"', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

procedure RefreshInstalledService();
var
  ResultCode, Attempt: Integer;
  Source, Target: String;
  WasRunning: Boolean;
begin
  Source := ExpandConstant('{app}\AorusLcd.Service.exe');
  Target := ExpandConstant('{commonappdata}\AorusLcd\bin\AorusLcd.Service.exe');
  WasRunning := ServiceInState('RUNNING');

  { Stop the service and wait for it to actually reach STOPPED (up to ~15s) so Windows
    releases the lock on its exe before we overwrite it. }
  Exec(ExpandConstant('{cmd}'), '/c sc stop ' + ServiceName, '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for Attempt := 1 to 30 do
  begin
    if ServiceInState('STOPPED') then
      Break;
    Sleep(500);
  end;

  { Overwrite the registered binary, retrying briefly if the lock lingers. A persistent
    lock leaves the old exe in place, which is no worse than before this refresh existed. }
  ForceDirectories(ExtractFileDir(Target));
  for Attempt := 1 to 10 do
  begin
    if CopyFile(Source, Target, False) then
      Break;
    Sleep(500);
  end;

  { Only restart if it was running before the upgrade, so a deliberately stopped service
    stays stopped. }
  if WasRunning then
    Exec(ExpandConstant('{cmd}'), '/c sc start ' + ServiceName, '',
      SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and ServiceIsInstalled() then
    RefreshInstalledService();
end;
