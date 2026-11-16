; Canvas Countdown for Windows — the installer.
;
; Per-user and without administrator rights: it installs into
; %LOCALAPPDATA%\Programs\Canvas Countdown, which a student's own account can
; always write to, and asks for nothing.
;
; Built by windows/scripts/build-release.ps1, which passes:
;   /DAppVersion=1.2.3   the version, which must match the build inside
;   /DArch=x64|arm64     the architecture of the build inside
;   /DSourceDir=...      the published app folder
;   /DOutputDir=...      where the installer goes
;   /DSimulateFailure    (tests only) fail half-way through copying files
;
; How an update stays safe
; ------------------------
; An update must never leave someone without a working copy. The installed
; folder is therefore never overwritten in place: just before the new files
; are copied, the whole folder is renamed to "<folder>.previous" (a rename on
; the same disk either happens completely or not at all), and the new version
; goes into a fresh folder. Only when every file is in place is the
; "install-complete" marker written and the previous folder deleted. If
; anything fails first, the fresh folder is removed and the previous one is
; renamed back — and relaunched, when the app asked for a relaunch. If the
; machine loses power in between, the next run of any installer sees the
; ".previous" folder, checks the marker, and finishes or undoes the swap.
;
; The folder name itself never changes, so the Start menu shortcut, the
; startup entry and Windows' memory of a pinned tray icon all survive updates.

#ifndef AppVersion
  #error Pass /DAppVersion=<version>
#endif
#ifndef Arch
  #error Pass /DArch=x64 or /DArch=arm64
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<published app folder>
#endif
#ifndef OutputDir
  #define OutputDir "Output"
#endif

#define AppName "Canvas Countdown"
#define AppExe "CanvasCountdown.exe"
#define AppUserModelId "le23967.CanvasCountdown"
#define LinkScheme "canvascountdown"

[Setup]
AppId={{6D2FA53B-6E79-499A-A0AE-9058DCF2E7F7}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=le23967
AppPublisherURL=https://le23967.github.io/CanvasCountdown/
AppSupportURL=https://github.com/le23967/CanvasCountdown/issues
AppUpdatesURL=https://github.com/le23967/CanvasCountdown/releases
AppCopyright=Copyright (c) 2026 le23967
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} installer ({#Arch})
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
PrivilegesRequired=lowest
MinVersion=10.0.19041
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
OutputDir={#OutputDir}
OutputBaseFilename=CanvasCountdown-Windows-{#AppVersion}-{#Arch}-setup
SetupIconFile=..\src\CanvasCountdown.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running copy (with the user's say-so in the wizard; outright when an
; update runs silently) rather than fail on files in use.
CloseApplications=force
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef SimulateFailure
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}\simulated-failure"; Flags: ignoreversion; BeforeInstall: FailOnPurpose
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; Clicking a reminder opens the event it is about: reminders carry a
; canvascountdown: link, and this is what opens it.
Root: HKCU; Subkey: "Software\Classes\{#LinkScheme}"; ValueType: string; ValueName: ""; ValueData: "URL:{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#LinkScheme}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\{#LinkScheme}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"",0"
Root: HKCU; Subkey: "Software\Classes\{#LinkScheme}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
; Reminders show the app's own name and icon.
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppUserModelId}"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\AppUserModelId\{#AppUserModelId}"; ValueType: string; ValueName: "IconUri"; ValueData: "{app}\Assets\AppIcon-64.png"
; Launch at login is switched on from Settings, never by the installer, but
; uninstalling takes it away.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "CanvasCountdown"; Flags: uninsdeletevalue dontcreatekey
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "CanvasCountdown"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open {#AppName}"; Flags: nowait postinstall skipifsilent
; After an update the app asked for, open it again where it left off.
Filename: "{app}\{#AppExe}"; Parameters: "--updated"; Flags: nowait; Check: ShouldRelaunch

[UninstallRun]
; Withdraw scheduled reminders and the startup entry before the files go, so
; nothing fires for an app that is no longer there.
Filename: "{app}\{#AppExe}"; Parameters: "--prepare-for-uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "PrepareForUninstall"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{app}.previous"

[Code]
const
  PreviousSuffix = '.previous';
  CompleteMarker = 'install-complete';

var
  MovedPrevious: Boolean;
  InstallSucceeded: Boolean;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function ShouldRelaunch: Boolean;
begin
  Result := WizardSilent and HasParam('/RELAUNCH');
end;

function CurrentDir: String;
begin
  Result := RemoveBackslashUnlessRoot(ExpandConstant('{app}'));
end;

function PreviousDir: String;
begin
  Result := CurrentDir + PreviousSuffix;
end;

function RenameWithRetry(const Source, Destination: String): Boolean;
var
  Attempt: Integer;
begin
  // A scanner or the shell can hold a file open for a moment after the app
  // closes; give it a few seconds before giving up.
  for Attempt := 1 to 10 do
  begin
    if RenameFile(Source, Destination) then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  Result := False;
end;

// An update that was cut off (power, a crash) leaves a ".previous" folder.
// If the marker says the newer copy finished, the old one is just leftover;
// otherwise the newer copy is incomplete and the old one goes back.
procedure RecoverInterruptedUpdate;
begin
  if not DirExists(PreviousDir) then
    Exit;
  if FileExists(AddBackslash(CurrentDir) + CompleteMarker) then
  begin
    Log('Removing the copy left by an earlier update that finished.');
    DelTree(PreviousDir, True, True, True);
  end
  else
  begin
    Log('An earlier update did not finish; putting the previous copy back.');
    if DirExists(CurrentDir) then
      DelTree(CurrentDir, True, True, True);
    RenameWithRetry(PreviousDir, CurrentDir);
  end;
end;

// Restart Manager finds a running copy during "Preparing to Install", but
// Setup only closes it after ssInstall has been announced — too late for the
// folder to be set aside. So any running copy is closed here, first. When the
// app updates itself it has already quit by this point; this is for an
// installer someone runs by hand while the app is open. Nothing is lost by
// closing it: the app has saved everything before it would ever be closed.
procedure CloseRunningCopies;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if ResultCode = 0 then
  begin
    Log('Closed the running copy before updating it.');
    Sleep(1000);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    CloseRunningCopies;
    RecoverInterruptedUpdate;
    if DirExists(CurrentDir) then
    begin
      if not RenameWithRetry(CurrentDir, PreviousDir) then
      begin
        Log('Could not set the installed copy aside; nothing was changed.');
        SuppressibleMsgBox('Canvas Countdown is still in use, so it was not changed. Close it and run the installer again.', mbError, MB_OK, IDOK);
        Abort;
      end;
      MovedPrevious := True;
      Log('Set the installed copy aside as ' + PreviousDir);
    end;
  end
  else if CurStep = ssPostInstall then
  begin
    SaveStringToFile(AddBackslash(CurrentDir) + CompleteMarker, '{#AppVersion}', False);
    InstallSucceeded := True;
    if MovedPrevious then
    begin
      Log('Update complete; removing the previous copy.');
      DelTree(PreviousDir, True, True, True);
    end;
  end;
end;

procedure DeinitializeSetup;
var
  ResultCode: Integer;
begin
  if not MovedPrevious or InstallSucceeded then
    Exit;
  Log('The update did not finish; restoring the previous copy.');
  if DirExists(CurrentDir) then
    DelTree(CurrentDir, True, True, True);
  if RenameWithRetry(PreviousDir, CurrentDir) and HasParam('/RELAUNCH') then
    Exec(AddBackslash(CurrentDir) + '{#AppExe}', '--update-failed', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

#ifdef SimulateFailure
procedure FailOnPurpose;
begin
  RaiseException('Simulated failure half-way through the update (test build).');
end;
#endif
