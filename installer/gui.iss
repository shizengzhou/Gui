; Inno Setup script for the Gui terminal (x64).
;
; Build the payload first, then compile this script:
;   dotnet publish Gui/Gui/Gui.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
;   iscc installer/gui.iss /DAppVersion=1.0.0
;
; The app is unpackaged and self-contained, so the payload directory already carries the
; .NET runtime, the Windows App SDK and ghostty-vt.dll; a target machine needs nothing
; installed. Only the x64 installer exists because native/x64 is the only ghostty-vt.dll
; checked in.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "Gui"
#define AppExe "Gui.exe"
; Where the publish profile dropped the payload. The profile's PublishDir has no platform
; folder, unlike a plain "dotnet publish -p:Platform=x64"; override with /DPublishDir=... if
; you published elsewhere.
#ifndef PublishDir
  #define PublishDir "..\Gui\Gui\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#endif

[Setup]
AppId={{5F0C7B2E-3A61-4C9D-8E24-9B7D1A6F0C58}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user by default: no elevation, installed under %LOCALAPPDATA%\Programs.
; Passing /ALLUSERS switches to Program Files through the autopf constants.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-x64-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "addpath"; Description: "Add gui-cli to the PATH"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addpath; Check: NeedsAddPath('{app}')

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;
