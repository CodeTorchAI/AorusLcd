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

{ Fully-qualified System32 path so an elevated install can't be hijacked by a planted
  sc.exe/find.exe earlier on the executable search path. }
function SysExe(const Name: String): String;
begin
  Result := ExpandConstant('{sys}\') + Name;
end;

function ServiceIsInstalled(): Boolean;
var
  ResultCode: Integer;
begin
  { `sc query` exits 0 when the service exists (in any state), 1060 when it does not. }
  Result := Exec(SysExe('sc.exe'), 'query ' + ServiceName, '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function ServiceInState(const State: String): Boolean;
var
  ResultCode: Integer;
begin
  { The pipe needs cmd; both executables are fully qualified. `find` exits 0 when the
    state word appears in `sc query` output, 1 otherwise. }
  Result := Exec(ExpandConstant('{cmd}'),
    '/c ""' + SysExe('sc.exe') + '" query ' + ServiceName +
    ' | "' + SysExe('find.exe') + '" "' + State + '""', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function IcaclsOk(const Args: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(SysExe('icacls.exe'), Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    and (ResultCode = 0);
end;

function IsReparsePoint(const Path: String): Boolean;
var
  ResultCode: Integer;
begin
  { `fsutil reparsepoint query` exits 0 only when Path IS a reparse point (junction/symlink). }
  Result := Exec(SysExe('fsutil.exe'), 'reparsepoint query "' + Path + '"', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function HardenServiceDir(): Boolean;
var
  DataDir, BinDir: String;
begin
  { Mirror ServiceControl.InstallAsync: refuse a pre-planted junction, then rebuild each DACL
    from scratch so the LocalSystem exe is SYSTEM/Administrators-owned with Users limited to
    read/execute, regardless of who created the tree. Repairs installs made before this
    hardening existed. Returns False (fail-closed) if any step fails, so the caller can decline
    to run the service from a directory it couldn't secure. }
  DataDir := ExpandConstant('{commonappdata}\AorusLcd');
  BinDir := DataDir + '\bin';
  if IsReparsePoint(DataDir) or IsReparsePoint(BinDir) then
  begin
    Result := False;
    Exit;
  end;
  ForceDirectories(BinDir);
  Result :=
    IcaclsOk('"' + DataDir + '" /setowner *S-1-5-18 /T /C') and
    IcaclsOk('"' + DataDir + '" /reset') and
    IcaclsOk('"' + DataDir + '" /inheritance:r') and
    IcaclsOk('"' + DataDir + '" /remove:g *S-1-3-0') and
    IcaclsOk('"' + DataDir + '" /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(RX,W)') and
    IcaclsOk('"' + DataDir + '" /grant *S-1-5-32-545:(OI)(NP)(IO)M') and
    IcaclsOk('"' + BinDir + '" /reset') and
    IcaclsOk('"' + BinDir + '" /inheritance:r') and
    IcaclsOk('"' + BinDir + '" /remove:g *S-1-3-0') and
    IcaclsOk('"' + BinDir + '" /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX');
end;

procedure RefreshInstalledService();
var
  ResultCode, Attempt: Integer;
  Source, Target: String;
  WasRunning, Copied: Boolean;
begin
  Source := ExpandConstant('{app}\AorusLcd.Service.exe');
  Target := ExpandConstant('{commonappdata}\AorusLcd\bin\AorusLcd.Service.exe');
  WasRunning := ServiceInState('RUNNING');

  { Stop the service and wait for it to actually reach STOPPED (up to ~15s) so Windows
    releases the lock on its exe before we overwrite it. }
  Exec(SysExe('sc.exe'), 'stop ' + ServiceName, '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
  for Attempt := 1 to 30 do
  begin
    if ServiceInState('STOPPED') then
      Break;
    Sleep(500);
  end;

  { Harden the directory BEFORE refreshing the binary, so the exe can never be copied into a
    user-writable location and an upgrade repairs an install predating this hardening. If it
    fails, don't copy or restart - leaving the service stopped is safer than running it from a
    directory we couldn't secure. }
  if not HardenServiceDir() then
    Exit;

  { Overwrite the registered binary, retrying briefly if the lock lingers. }
  ForceDirectories(ExtractFileDir(Target));
  Copied := False;
  for Attempt := 1 to 10 do
  begin
    if CopyFile(Source, Target, False) then
    begin
      Copied := True;
      Break;
    end;
    Sleep(500);
  end;

  { Only restart if the fresh binary was written. If the copy failed (e.g. a local attacker
    holding the old, possibly-tampered exe open to block replacement), leave the service
    stopped rather than restart stale content as LocalSystem. }
  if WasRunning and Copied then
    for Attempt := 1 to 10 do
    begin
      Exec(SysExe('sc.exe'), 'start ' + ServiceName, '',
        SW_HIDE, ewWaitUntilTerminated, ResultCode);
      if ServiceInState('RUNNING') then
        Break;
      Sleep(500);
    end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and ServiceIsInstalled() then
    RefreshInstalledService();
end;
