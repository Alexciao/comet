; Comet installer (Inno Setup 6). Per-user: installs to %LocalAppData%\Programs\Comet, no admin rights.
; Built by installer/build.ps1, which passes the version from Directory.Build.props (the single version source):
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=..\artifacts\publish installer\Comet.iss
;
; Install / update: a running Comet is asked to quit first (Comet.exe --quit), the program folder is replaced
; as a whole (no stale files from older versions), and Comet starts again. User data in %LocalAppData%\Comet is
; never touched. Uninstall: Comet quits, its files and the start-at-login entry are removed; settings, snippets
; and replacements stay (they are the user's).

#ifndef AppVersion
  #error AppVersion must be defined (/DAppVersion=x.y.z)
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish"
#endif

[Setup]
AppId={{B4E7A1C9-3D52-4F6E-9A1B-7C2D8E0F4A65}
AppName=Comet
AppVersion={#AppVersion}
AppVerName=Comet {#AppVersion}
AppPublisher=Alex Corazza
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Comet
DefaultDirName={localappdata}\Programs\Comet
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\installer
OutputBaseFilename=Comet-{#AppVersion}-setup
SetupIconFile=..\src\Comet.App\Assets\Comet.ico
UninstallDisplayIcon={app}\Comet.exe
UninstallDisplayName=Comet
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Comet is closed by PrepareToInstall below (cleanly, through its own --quit), not by Restart Manager.
CloseApplications=no
RestartApplications=no

[InstallDelete]
; Replace the program folder as a whole on updates.
Type: filesandordirs; Name: "{app}\*"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Comet"; Filename: "{app}\Comet.exe"; Comment: "Emoji, snippets and auto-replace as you type"

[Registry]
; Comet writes this value itself when "Start at login" is on; uninstall removes it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Comet"; Flags: uninsdeletevalue dontcreatekey

[Run]
; Interactive install: start Comet and show Settings. Silent update: start it again in the tray.
Filename: "{app}\Comet.exe"; Description: "Start Comet"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Comet.exe"; Parameters: "--background"; Flags: nowait skipifnotsilent

[UninstallRun]
Filename: "{app}\Comet.exe"; Parameters: "--quit"; Flags: runhidden waituntilterminated; RunOnceId: "QuitComet"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\Comet.exe');
  if FileExists(Exe) then
  begin
    if not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
      Result := 'Comet is still running. Quit it from its tray icon, then run Setup again.';
  end;
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
