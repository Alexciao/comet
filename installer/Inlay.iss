; Inlay installer (Inno Setup 6). Per-user: installs to %LocalAppData%\Programs\Inlay, no admin rights.
; Built by installer/build.ps1, which passes the version from Directory.Build.props (the single version source):
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish installer\Inlay.iss
;
; Install / update: a running Inlay is asked to quit first (Inlay.exe --quit), the program folder is replaced
; as a whole (no stale files from older versions), and Inlay starts again. User data in %LocalAppData%\Inlay is
; never touched. Uninstall: Inlay quits, its files and the start-at-login entry are removed; settings, snippets
; and shorthands stay (they are the user's).
;
; Inlay was called Comet in 1.0. The AppId is the same, so installing Inlay over Comet upgrades it in place: a
; running Comet.exe is asked to quit, Comet's program folder, Start menu shortcut and start-at-login entry are
; removed, and the app moves its data from %LocalAppData%\Comet to %LocalAppData%\Inlay on first run.

#ifndef AppVersion
  #error AppVersion must be defined (/DAppVersion=x.y.z)
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{B4E7A1C9-3D52-4F6E-9A1B-7C2D8E0F4A65}
AppName=Inlay
AppVersion={#AppVersion}
AppVerName=Inlay {#AppVersion}
AppPublisher=Alex Corazza
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Inlay
DefaultDirName={localappdata}\Programs\Inlay
DisableDirPage=yes
; Comet 1.0 lived in Programs\Comet under the same AppId: install to the new folder, not the remembered one.
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Inlay-Setup-{#Arch}
SetupIconFile=..\src\Inlay.App\Assets\Inlay.ico
UninstallDisplayIcon={app}\Inlay.exe
UninstallDisplayName=Inlay
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Inlay is closed by PrepareToInstall below (cleanly, through its own --quit), not by Restart Manager.
CloseApplications=no
RestartApplications=no

[InstallDelete]
; Replace the program folder as a whole on updates.
Type: filesandordirs; Name: "{app}\*"
; Comet 1.0's Start menu shortcut.
Type: files; Name: "{autoprograms}\Comet.lnk"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Inlay"; Filename: "{app}\Inlay.exe"; Comment: "Emoji, snippets and shorthands as you type"

[Registry]
; Inlay writes this value itself when "Start at login" is on; uninstall removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Inlay"; Flags: uninsdeletevalue dontcreatekey
; Comet 1.0's entry (Inlay writes its own when it starts).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Comet"; Flags: deletevalue dontcreatekey

[Run]
; Interactive install: start Inlay and show Settings. Silent update: start it again in the tray.
Filename: "{app}\Inlay.exe"; Description: "Start Inlay"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Inlay.exe"; Parameters: "--background"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{app}\Inlay.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitInlay"

[Code]
// Comet 1.0's program folder (same AppId, older name).
function CometDir(): String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Comet');
end;

function QuitRunning(Exe, Name: String): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      Result := Name + ' is still running. Quit it from its tray icon, then run Setup again.';
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := QuitRunning(ExpandConstant('{app}\Inlay.exe'), 'Inlay');
  if Result = '' then
    Result := QuitRunning(CometDir() + '\Comet.exe', 'Comet');
end;

// After an upgrade from Comet 1.0: its program folder goes (its data stays and moves when Inlay starts).
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and DirExists(CometDir()) then
    DelTree(CometDir(), True, True, True);
end;

// Inno retries deleting the program folder once; right after an update something (typically a scan of the
// freshly written files) can still hold it for a moment, which left an empty folder behind. Retry for up to 5 s.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Attempt: Integer;
begin
  if CurUninstallStep = usPostUninstall then
    for Attempt := 1 to 25 do
    begin
      if not DirExists(ExpandConstant('{app}')) then
        Break;
      RemoveDir(ExpandConstant('{app}'));
      Sleep(200);
    end;
end;
