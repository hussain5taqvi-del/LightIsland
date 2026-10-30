; Inno Setup script. Built by .github/workflows/release.yml:
;   ISCC /DAppVersion=1.7.0 /DSourceDir=<release folder> installer\LightIsland.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\release"
#endif

[Setup]
AppId={{6E0B0D55-3F0C-4C47-8A32-2B7A1F3C9D14}
AppName=LightIsland Lab
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Hussain
; installs for the current user only: no admin rights, and the app can update itself in this folder
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\LightIslandLab
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=out
OutputBaseFilename=LightIslandLab-Setup-{#AppVersion}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\LightIslandLab.exe
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
Name: "{userprograms}\LightIsland Lab"; Filename: "{app}\LightIslandLab.exe"
Name: "{userdesktop}\LightIsland Lab"; Filename: "{app}\LightIslandLab.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\LightIslandLab.exe"; Description: "Start LightIsland Lab"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure StopApp();
var R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im LightIslandLab.exe', '', SW_HIDE, ewWaitUntilTerminated, R);
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
