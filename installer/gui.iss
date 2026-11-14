; Inno Setup script for the Gui terminal (x64).
;
; Build the payload first, then compile this script:
;   dotnet publish Gui/Gui/Gui.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
;   iscc installer/gui.iss /DAppVersion=1.0.0
;
; Two payloads can be packaged from this one script:
;   (no define)  self-contained: the payload carries the .NET 8 runtime, so a target
;                machine needs nothing installed
;   /DLite=1     framework-dependent: the payload leaves the .NET runtime out, so the
;                setup is smaller but the machine must already have .NET 8
; Both keep the Windows App SDK and ghostty-vt.dll in the payload either way, because an
; unpackaged WinUI app cannot start without them. Both use the same AppId and the same
; install directory: they are one product with two download sizes, so installing one
; over the other is an upgrade rather than a second copy. Only the x64 installer exists
; because native/x64 is the only ghostty-vt.dll checked in.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#define AppName "Gui"
#define AppExe "Gui.exe"

#ifdef Lite
  #define VariantSuffix "-lite"
#else
  #define VariantSuffix ""
#endif
; Where the publish profile dropped the payload. The profile's PublishDir has no platform
; folder, unlike a plain "dotnet publish -p:Platform=x64"; override with /DPublishDir=... if
; you published elsewhere.
#ifndef PublishDir
  #define PublishDir "..\Gui\Gui\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#endif

; The Setup.exe's own resource version: digits and dots only, so a pre-release suffix such as
; "-beta.1" is stripped by build-installer.ps1 and passed as /DVerFile=...
#ifndef VerFile
  #define VerFile AppVersion
#endif

[Setup]
AppId={{5F0C7B2E-3A61-4C9D-8E24-9B7D1A6F0C58}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
AppCopyright={#AppName}
; Without these the setup program's file properties are empty and Explorer's tooltip reports
; "File version: 0.0.0.0"; the app's own exe gets its version from -p:Version in the publish.
VersionInfoVersion={#VerFile}
VersionInfoProductVersion={#VerFile}
VersionInfoTextVersion={#AppVersion}
VersionInfoCompany={#AppName}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
VersionInfoOriginalFileName={#AppName}-{#AppVersion}-x64{#VariantSuffix}-setup.exe
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
OutputBaseFilename={#AppName}-{#AppVersion}-x64{#VariantSuffix}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
; Same mark as the exe, taken from the source tree so a stale payload cannot pick an old one.
SetupIconFile=..\Gui\Gui\Assets\app.ico
CloseApplications=yes
RestartApplications=no
; The PATH entry below is written through [Registry]; this makes Setup broadcast
; WM_SETTINGCHANGE at the end of an install and of an uninstall, so Explorer and new
; shells pick the new PATH up without a sign-out.
ChangesEnvironment=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
; Only the payload's bin folder goes on PATH: it holds the CLI (gui.exe) alone. The root is
; full of WinUI DLLs (DWriteCore, hostfxr, Microsoft.ui.xaml), and a PATH entry pointing
; there made unrelated programs load our private copies.
Name: "addpath"; Description: "Add gui to the PATH"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

#ifdef Lite
[InstallDelete]
; The lite payload carries no .NET runtime. Leftover runtime files from an earlier
; self-contained install would be picked up instead of the shared ones (a private
; hostfxr wins) and would survive an uninstall as tens of MB of orphans, so the lite
; setup clears the app directory and lays its own payload down again a moment later.
Type: filesandordirs; Name: "{app}\*"
#endif


[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\bin"; Tasks: addpath; Check: NeedsAddPath(ExpandConstant('{app}\bin'))

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[Code]
// Read one ";"-separated list entry by entry instead of searching for ";dir;": the entry may
// sit at either end of PATH, and an older version of this script appended "{app}" again on
// every install because its search never matched.
function PathHasEntry(Path, Dir: string): Boolean;
var
  Rest, Item: string;
  P: Integer;
begin
  Result := False;
  Rest := Path;
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    if CompareText(Trim(Item), Dir) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := not PathHasEntry(Path, Dir);
end;

// Drops every occurrence of Dir (and any empty entry left behind) from the user's PATH.
procedure RemovePathEntry(Dir: string);
var
  Path, Rest, Item, NewPath: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then exit;

  NewPath := '';
  Rest := Path;
  while Rest <> '' do
  begin
    P := Pos(';', Rest);
    if P = 0 then
    begin
      Item := Rest;
      Rest := '';
    end
    else
    begin
      Item := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;

    Item := Trim(Item);
    if (Item <> '') and (CompareText(Item, Dir) <> 0) then
    begin
      if NewPath <> '' then NewPath := NewPath + ';';
      NewPath := NewPath + Item;
    end;
  end;

  RegWriteExpandStringValue(HKCU, 'Environment', 'Path', NewPath);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemovePathEntry(ExpandConstant('{app}\bin'));
  end;
end;

#ifdef Lite
// The lite payload leaves the .NET runtime out, so Setup has to make sure it is there
// before it copies the rest. The app resolves Microsoft.NETCore.App 8.0.0, and any 8.x
// patch release satisfies that, so a single 8.* folder is all this looks for - in the
// machine-wide install and in a per-user one, which is where dotnet-install puts it.
function HasDotNet8In(Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Root) then exit;
  if not FindFirst(AddBackslash(Root) + '8.*', FindRec) then exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Exit;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function DotNet8Installed: Boolean;
begin
  Result :=
    HasDotNet8In(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App')) or
    HasDotNet8In(ExpandConstant('{localappdata}\Microsoft\dotnet\shared\Microsoft.NETCore.App'));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  if DotNet8Installed then
  begin
    Result := True;
    Exit;
  end;

  // A silent install must not block on a message or launch a browser; it fails with a
  // non-zero exit code instead, which is what an unattended deployment can react to.
  Result := False;
  if not WizardSilent then
    if MsgBox('Gui needs the .NET 8 runtime, which this package does not carry.' + #13#10 +
              'It is a free download from dotnet.microsoft.com.' + #13#10 + #13#10 +
              'Open the download page now?', mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '',
                SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;
#endif
