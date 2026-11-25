; Inno Setup script for the Gui terminal.
;
; Build the payload first, then compile this script:
;   dotnet publish Gui/Gui/Gui.csproj -c Release -p:Platform=x64 -p:PublishProfile=win-x64
;   iscc installer/gui.iss /DAppVersion=1.0.0
;
; Two payloads can be packaged from this one script:
;   (no define)  self-contained: the payload carries the .NET 10 runtime, so a target
;                machine needs nothing installed
;   /DLite=1     framework-dependent: the payload leaves the .NET runtime out, so the setup
;                is smaller; it installs the runtime itself when the machine has none
; Both keep the Windows App SDK and ghostty-vt.dll in the payload either way, because an
; unpackaged WinUI app cannot start without them. Both use the same AppId and the same
; install directory: they are one product with two download sizes, so installing one
; over the other is an upgrade rather than a second copy.
;
; /DPlatform=x64 (default) or arm64 says which architecture that payload was published for;
; it picks the payload folder, the setup file name and the CPUs the setup accepts.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

; The arm64 architecture names and CreateDownloadPage - the progress page the lite setup uses
; for its runtime download - both need Inno Setup 6.2 or newer. Saying so here beats a cryptic
; "Unknown identifier" for whoever compiles this with an old ISCC.
#if VER < 0x06020000
  #error installer/gui.iss needs Inno Setup 6.2 or newer
#endif

#define AppName "Gui"
#define AppExe "Gui.exe"
; Must match Program.AppUserModelId: the notification identity and the shortcuts have to
; agree, or Windows shows the app twice and the taskbar splits the pinned and running icons.
#define AppUserModelId "shizengzhou.Gui"

; Which architecture the payload was published for. The native core is checked in per
; platform (native\x64 and native\ARM64, installed by native\get-ghostty-vt.ps1) and
; Gui.csproj copies the one that matches -p:Platform, so this only has to agree with the
; publish being packaged; build-installer.ps1 passes it. There is no x86 because the
; official libghostty-vt builds have no 32-bit DLL.
#ifndef Platform
  #define Platform "x64"
#endif
; The RID folder and the setup file name use the lower-case spelling: win-arm64,
; Gui-1.0.0-arm64-setup.exe.
#define PlatformLower LowerCase(Platform)
#define Rid "win-" + PlatformLower

#ifdef Lite
  #define VariantSuffix "-lite"
#else
  #define VariantSuffix ""
#endif
; An arm64 payload runs only on Arm64 Windows; "x64compatible" is the x64 spelling that also
; accepts an Arm64 machine running it under emulation. Both are also what tells Setup to use
; the real 64-bit Program Files instead of the emulated one.
#if PlatformLower == "arm64"
  #define ArchAllowed "arm64"
  ; The upper-case architecture tag of DOTNET_ROOT_<ARCH>, which the apphost checks first.
  #define DotNetEnvArch "ARM64"
#else
  #define ArchAllowed "x64compatible"
  #define DotNetEnvArch "X64"
#endif
; Where the publish profile dropped the payload. The profile's PublishDir has no platform
; folder, unlike a plain "dotnet publish -p:Platform=x64"; override with /DPublishDir=... if
; you published elsewhere.
#ifndef PublishDir
  #define PublishDir "..\Gui\Gui\bin\Release\net10.0-windows10.0.19041.0\" + Rid + "\publish"
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
; Without this, Inno builds the uninstall entry's DisplayName from the localized
; "NameAndVersion" message, which reads "Gui version 0.1.0" (or "Gui 版本 0.1.0") in
; Settings > Apps. Show the product name only; the version has its own column there.
AppVerName={#AppName}
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
VersionInfoOriginalFileName={#AppName}-{#AppVersion}-{#PlatformLower}{#VariantSuffix}-setup.exe
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user by default: no elevation, installed under %LOCALAPPDATA%\Programs.
; Passing /ALLUSERS switches to Program Files through the autopf constants.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0.17763
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchAllowed}
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-{#PlatformLower}{#VariantSuffix}-setup
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

; Setup speaks the language of the Windows it runs on: English and 简体中文 are built in,
; and with two languages listed Setup picks the one matching the user's UI language
; instead of asking. Inno Setup itself ships no Chinese, so the translation is bundled
; under installer\languages (MIT, see the licence file next to it).
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "{#SourcePath}languages\ChineseSimplified.isl"

[CustomMessages]
; Setup's own pages, buttons and error dialogs come from the files above; everything this
; script says itself needs a line per language.
en.DesktopIcon=Create a desktop shortcut
zh.DesktopIcon=创建桌面快捷方式
en.AddToPath=Add gui to the PATH
zh.AddToPath=将 gui 添加到 PATH
en.LaunchGui=Launch Gui
zh.LaunchGui=启动 Gui
en.AutoDotNet=Gui needs the .NET 10 runtime, which this package does not carry, and none was found on this machine.%n%nSetup can download the official installer (about 30 MB) and run it now; Windows will ask for permission to install it. Do that now?
zh.AutoDotNet=Gui 需要 .NET 10 运行时，而这个安装包不带它，本机也没有检测到。%n%n安装程序现在可以下载官方安装器（约 30 MB）并自动安装，期间 Windows 会请求管理员权限。要现在安装吗？
en.DotNetFailed=Setup could not install the .NET 10 runtime automatically (%1).%n%nCheck the network connection and run Setup again, or install the runtime yourself from https://dotnet.microsoft.com/download/dotnet/10.0
zh.DotNetFailed=安装程序未能自动完成 .NET 10 运行时的安装（%1）。%n%n你可以检查网络后重新运行安装程序，或自行从 https://dotnet.microsoft.com/download/dotnet/10.0 安装运行时。
en.OpenDotNetDownload=Open the download page now?
zh.OpenDotNetDownload=现在打开下载页面吗？
en.CloseRunningApp=Gui is running. Setup has to close it before it can replace the program files. Close it now and continue?
zh.CloseRunningApp=Gui 正在运行。安装程序需要先关闭它才能替换程序文件。现在关闭并继续吗？
en.CloseRunningAppFailed=Gui is still running. Exit it manually, then run Setup again.
zh.CloseRunningAppFailed=Gui 仍在运行。请手动退出后重新运行安装程序。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
; Only the payload's bin folder goes on PATH: it holds the CLI (gui.exe) alone. The root is
; full of WinUI DLLs (DWriteCore, hostfxr, Microsoft.ui.xaml), and a PATH entry pointing
; there made unrelated programs load our private copies.
Name: "addpath"; Description: "{cm:AddToPath}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb"

#ifdef Lite
[InstallDelete]
; The lite payload carries no .NET runtime. Leftover runtime files from an earlier
; self-contained install would be picked up instead of the shared ones (a private
; hostfxr wins) and would survive an uninstall as tens of MB of orphans, so the lite
; setup removes them first.
;
; Only those runtime files are deleted: clearing "{app}\*" failed with "Access is denied"
; while Setup recreated "bin", because a running instance (or its ConPTY host, or a shell
; whose working directory sits in the install folder) still held a handle in the tree and
; a half-deleted directory refuses to be created again.
Type: files; Name: "{app}\coreclr.dll"
Type: files; Name: "{app}\clr*.dll"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\dbgshim.dll"
Type: files; Name: "{app}\hostfxr.dll"
Type: files; Name: "{app}\hostpolicy.dll"
Type: files; Name: "{app}\mscord*.dll"
Type: files; Name: "{app}\mscorlib.dll"
Type: files; Name: "{app}\msquic.dll"
Type: files; Name: "{app}\sni.dll"
Type: files; Name: "{app}\System.*.dll"
Type: files; Name: "{app}\Microsoft.CSharp.dll"
Type: files; Name: "{app}\Microsoft.VisualBasic*.dll"
Type: files; Name: "{app}\Microsoft.Win32.*.dll"
Type: files; Name: "{app}\netstandard.dll"
#endif


[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\bin"; Tasks: addpath; Check: NeedsAddPath(ExpandConstant('{app}\bin'))

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon; AppUserModelID: "{#AppUserModelId}"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchGui}"; Flags: nowait postinstall skipifsilent

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

// Setup cannot replace Gui.exe, its ConPTY host or the CLI while they are running, and the
// lite variant also has to drop runtime files from the payload directory. Ask to close the
// app (or close it silently in an unattended install) and wait for the handles to go away
// before Setup deletes or writes anything.
function VariantPath(Process: Variant): String;
begin
  try
    Result := Process.ExecutablePath;
  except
    Result := '';
  end;
end;

function VariantPid(Process: Variant): String;
begin
  try
    Result := Process.ProcessId;
  except
    Result := '';
  end;
end;

// Comma-separated PIDs of Gui processes running from the directory being installed into.
function FindRunningAppPids: String;
var
  Locator, Wmi, Processes, Process: Variant;
  I: Integer;
  AppDir, ExePath, Pid: String;
begin
  Result := '';
  AppDir := ExpandConstant('{app}');
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Wmi := Locator.ConnectServer('.', 'root\cimv2');
    Processes := Wmi.ExecQuery(
      'SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''Gui.exe'' OR ' +
      'Name = ''OpenConsole.exe'' OR Name = ''gui-cli.exe''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.Item(I);
      ExePath := VariantPath(Process);
      Pid := VariantPid(Process);
      if (Pid <> '') and (ExePath <> '') and
         (CompareText(Copy(ExePath, 1, Length(AppDir) + 1), AppDir + '\') = 0) then
      begin
        if Result <> '' then
          Result := Result + ',';
        Result := Result + Pid;
      end;
    end;
  except
    // WMI is unavailable on a stripped system; Setup's own Restart Manager check still
    // reports files that are in use.
  end;
end;

procedure KillPids(Pids: String);
var
  Rest, Pid: String;
  P, Code: Integer;
begin
  Rest := Pids;
  while Rest <> '' do
  begin
    P := Pos(',', Rest);
    if P = 0 then
    begin
      Pid := Rest;
      Rest := '';
    end
    else
    begin
      Pid := Copy(Rest, 1, P - 1);
      Rest := Copy(Rest, P + 1, Length(Rest));
    end;
    if Pid <> '' then
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/PID ' + Pid + ' /T /F', '',
           SW_HIDE, ewWaitUntilTerminated, Code);
  end;
end;

function WaitForAppExit(TimeoutMs: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while FindRunningAppPids <> '' do
  begin
    if Waited >= TimeoutMs then
    begin
      Result := False;
      Exit;
    end;
    Sleep(200);
    Waited := Waited + 200;
  end;
  Result := True;
end;

#ifdef Lite
// ---- The .NET 10 dependency of the framework-dependent payload --------------------------
//
// Gui.runtimeconfig.json asks for Microsoft.NETCore.App 10.0.0 and the default roll-forward is
// Minor, so any 10.x folder satisfies it while an installed 8.x or 11.x does not: the app would
// be installed and then refuse to start. Both of the locations an apphost can use are probed:
// %ProgramFiles%\dotnet, where the official installer puts the runtime and what Setup is about
// to fix, and the DOTNET_ROOT_<arch> / DOTNET_ROOT folder for a machine that keeps a private
// 10.x there on purpose. A per-user "dotnet-install" folder without that variable is not on the
// list - the host does not search it since .NET 7 - so counting it let Setup install an app that
// could not start.
//
// When nothing satisfies the check, Setup downloads Microsoft's own runtime installer and runs
// it. That runtime is a per-machine install (Program Files plus HKLM), which is why this one
// step needs admin while Gui itself does not.

const
  // Microsoft's channel link: it redirects to the newest 10.0.x runtime installer for this
  // setup's architecture, so following servicing costs no version to keep in sync here.
  DotNetRuntimeUrl = 'https://aka.ms/dotnet/10.0/dotnet-runtime-win-{#PlatformLower}.exe';
  DotNetRuntimeFile = 'dotnet-runtime.exe';
  // The real installer is ~30 MB, while an unknown aka.ms short link answers 200 with a ~15 KB
  // "link not found" page, so the download is size-gated before any of it is executed.
  DotNetInstallerMinBytes = 4 * 1024 * 1024;

var
  DotNetNeeded: Boolean;
  DotNetPage: TDownloadWizardPage;

function HasDotNet10In(Root: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Root) then exit;
  if not FindFirst(AddBackslash(Root) + '10.*', FindRec) then exit;
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

function DotNet10Installed: Boolean;
var
  Root: String;
begin
  Root := GetEnv('DOTNET_ROOT_{#DotNetEnvArch}');
  if Root = '' then Root := GetEnv('DOTNET_ROOT');
  Result :=
    HasDotNet10In(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.NETCore.App')) or
    ((Root <> '') and HasDotNet10In(AddBackslash(Root) + 'shared\Microsoft.NETCore.App'));
end;

function InitializeSetup: Boolean;
begin
  if DotNet10Installed then
  begin
    Result := True;
    Exit;
  end;

  // A silent run gets no dialog to answer, so it resolves the dependency itself: the
  // alternative is a failure an unattended deployment can only report, not fix. Answering No
  // in a wizard stops here, because installing an app that cannot start helps nobody.
  if not (WizardSilent or
          (MsgBox(CustomMessage('AutoDotNet'), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES)) then
  begin
    Result := False;
    Exit;
  end;

  DotNetNeeded := True;
  Result := True;
end;

procedure InitializeWizard;
begin
  DotNetPage := CreateDownloadPage(SetupMessage(msgWizardPreparing),
                                   SetupMessage(msgPreparingDesc), nil);
  // The page would otherwise name the redirect target's long URL; the file says the same thing
  // in one readable line.
  DotNetPage.ShowBaseNameInsteadOfUrl := True;
end;

// Downloads the runtime installer into {tmp} (which Setup removes again) with Setup's own
// download page - so the user sees bytes, percent and rate instead of a frozen wizard - and
// then runs it silently. Reason carries the technical note for the failure message.
function InstallDotNetRuntime(var Reason: String): Boolean;
var
  Installer: String;
  Code: Integer;
  Size: Int64;
begin
  Result := False;
  Installer := ExpandConstant('{tmp}\' + DotNetRuntimeFile);
  DeleteFile(Installer);

  DotNetPage.Clear;
  // No hash: the channel link follows servicing, so the file changes with every patch release.
  // The size gate below is what stands between a dead short link and executing junk.
  DotNetPage.Add(DotNetRuntimeUrl, DotNetRuntimeFile, '');
  if not WizardSilent then
    DotNetPage.Show;
  try
    try
      Log('Downloading the .NET 10 runtime installer from ' + DotNetRuntimeUrl);
      // Download returns the byte count, which is what the gate below needs.
      Size := DotNetPage.Download;
    except
      if DotNetPage.AbortedByUser then
        Reason := 'cancelled'
      else
        Reason := GetExceptionMessage;
      Exit;
    end;
  finally
    if not WizardSilent then
      DotNetPage.Hide;
  end;

  if Size < DotNetInstallerMinBytes then
  begin
    Reason := 'downloaded ' + IntToStr(Size) + ' bytes, not the installer';
    Exit;
  end;

  // ShellExec rather than Exec: CreateProcess ignores the installer's own
  // requireAdministrator manifest, so only the shell produces the UAC prompt a machine-wide
  // runtime install needs. SW_HIDE because /quiet leaves no window up anyway.
  Log('Running the runtime installer silently (' + IntToStr(Size) + ' bytes)');
  if not ShellExec('open', Installer, '/install /quiet /norestart', '', SW_HIDE,
                   ewWaitUntilTerminated, Code) then
  begin
    // 1223 is the code a declined UAC dialog comes back with.
    Reason := 'installer could not start (' + IntToStr(Code) + ')';
    Exit;
  end;

  // The folder check is the contract, not the exit code: 0 means installed, 3010 means
  // installed and reboot wanted, and 1638 means a different version already owns the install.
  if not DotNet10Installed then
  begin
    Reason := 'installer exit ' + IntToStr(Code);
    Exit;
  end;

  Log('The .NET 10 runtime is available (installer exit ' + IntToStr(Code) + ')');
  Result := True;
end;
#endif

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Pids: String;
#ifdef Lite
  DotNetReason: String;
#endif
begin
  Result := '';
  NeedsRestart := False;

#ifdef Lite
  // The runtime goes in before anything is written: a dependency that cannot be fetched
  // should leave the machine exactly as it was, and a non-empty return here aborts Setup
  // before Setup touches a single file.
  if DotNetNeeded then
    if not InstallDotNetRuntime(DotNetReason) then
    begin
      Log('The automatic .NET 10 install failed: ' + DotNetReason);
      Result := FmtMessage(CustomMessage('DotNetFailed'), [DotNetReason]);
      Exit;
    end;
#endif

  Pids := FindRunningAppPids;
  if Pids = '' then exit;

  if not WizardSilent then
    if MsgBox(CustomMessage('CloseRunningApp'), mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := CustomMessage('CloseRunningAppFailed');
      Exit;
    end;

  Log('Closing the running app before installing: ' + Pids);
  KillPids(Pids);
  if not WaitForAppExit(5000) then
    Result := CustomMessage('CloseRunningAppFailed');
end;
