#define AppName "Kanka Git Sync"
#define AppVersion GetEnv("KANKA_VERSION")
#define AppArchitecture GetEnv("KANKA_ARCHITECTURE")
#define PublishDirectory GetEnv("KANKA_PUBLISH_DIRECTORY")

[Setup]
AppId={{A950E5E7-55E5-43D5-8B20-7165446D0C63}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Kirtanei
DefaultDirName={localappdata}\Programs\KankaGitSync
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed={#AppArchitecture}
ArchitecturesInstallIn64BitMode={#AppArchitecture}
OutputDir=..\artifacts\installers
OutputBaseFilename=KankaGitSync-{#AppVersion}-win-{#AppArchitecture}-Setup
Compression=lzma2
SolidCompression=yes
ChangesEnvironment=yes
UninstallDisplayIcon={app}\git-kanka.exe

[Files]
Source: "{#PublishDirectory}\git-kanka.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDirectory}\KankaGitSync.Setup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "kanka-installer.marker"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}\Kanka Git Sync setup"; Filename: "{app}\KankaGitSync.Setup.exe"
Name: "{autoprograms}\{#AppName}\Uninstall {#AppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\KankaGitSync.Setup.exe"; Description: "Set up a Kanka world"; Flags: postinstall nowait skipifsilent

[Code]
function NormalizePath(Value: String): String;
begin
  Result := RemoveBackslashUnlessRoot(Value);
end;

function PathContains(PathValue, Directory: String): Boolean;
begin
  Result := Pos(';' + Lowercase(NormalizePath(Directory)) + ';', ';' + Lowercase(PathValue) + ';') > 0;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PathValue: String;
  Directory: String;
begin
  if CurStep <> ssPostInstall then Exit;
  Directory := NormalizePath(ExpandConstant('{app}'));
  if RegQueryStringValue(HKCU, 'Environment', 'Path', PathValue) then begin
    if PathContains(PathValue, Directory) then begin
      StringChangeEx(PathValue, ';' + Directory, '', True);
      StringChangeEx(PathValue, Directory + ';', '', True);
      if CompareText(PathValue, Directory) = 0 then PathValue := '';
    end;
    if PathValue = '' then
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Directory)
    else
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Directory + ';' + PathValue);
  end else
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Directory);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  PathValue: String;
  Directory: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Directory := NormalizePath(ExpandConstant('{app}'));
  if RegQueryStringValue(HKCU, 'Environment', 'Path', PathValue) then begin
    StringChangeEx(PathValue, ';' + Directory, '', True);
    StringChangeEx(PathValue, Directory + ';', '', True);
    if CompareText(PathValue, Directory) = 0 then PathValue := '';
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', PathValue);
  end;
end;
