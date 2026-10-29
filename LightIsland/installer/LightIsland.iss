; Inno Setup script. Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=1.7.0 /DSourceDir=<release folder> installer\LightIsland.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\release"
#endif

[Setup]
AppId={{B5A3D3F2-7C1E-4E58-9A56-4D2F6C8E1A90}
AppName=LightIsland
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Hussain
; installs for the current user only: no admin rights, and the app can update itself in this folder
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\LightIsland
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=out
OutputBaseFilename=LightIsland-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\LightIsland.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{userprograms}\LightIsland"; Filename: "{app}\LightIsland.exe"
Name: "{userdesktop}\LightIsland"; Filename: "{app}\LightIsland.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LightIsland.exe"; Description: "Start LightIsland"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure StopApp();
var R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im LightIsland.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopApp();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then StopApp();
end;
