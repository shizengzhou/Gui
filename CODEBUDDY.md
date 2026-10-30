# CODEBUDDY.md

This file provides guidance to CodeBuddy Code when working with code in this repository.

## Repository layout

```
Gui.slnx                          # solution (new XML .slnx format), platforms: ARM64 / x64 / x86
Gui/Gui/                          # WinUI 3 application project (Gui.csproj)
  App.xaml(.cs)                   # Application entry point; OnLaunched creates MainWindow
  MainWindow.xaml(.cs)            # main window, hosts Controls/TerminalView
  Controls/TerminalView.cs        # Win2D terminal renderer + input handling
  Terminal/                       # ConPTY host, libghostty-vt interop, session, key encoding
  native/<platform>/ghostty-vt.dll# prebuilt libghostty-vt (see native/build-ghostty-vt.sh)
  app.manifest                    # OS compatibility + PerMonitorV2 DPI
  Properties/PublishProfiles/     # win-x64 / win-x86 / win-arm64 .pubxml
native/build-ghostty-vt.sh        # builds ghostty-vt.dll for Windows via Zig
Gui/Gui (Package)/                # MSIX packaging project (.wapproj) - note the space in the path
  Package.appxmanifest
  Images/                         # tile / splash assets
```

The app project (`Gui/Gui/Gui.csproj`) is the only code project; the packaging project
(`Gui/Gui (Package)/Gui (Package).wapproj`) contains no code — it references the app via
`EntryPointProjectUniqueName` and `ProjectReference`, supplies the MSIX manifest and images, and
picks the per-platform publish profile (`Properties\PublishProfiles\win-$(Platform).pubxml`).

## Commands

Always pass an explicit `Platform` (x64 is the practical default); the solution has no AnyCPU
configuration and the csproj `Platforms` are `x86;x64;ARM64`.

```bash
# Build the app project (works with the dotnet CLI)
dotnet build Gui/Gui/Gui.csproj -p:Platform=x64

# Build the whole solution including the MSIX packaging project (needs VS MSBuild:
# the .wapproj imports the DesktopBridge targets from $(MSBuildExtensionsPath)\Microsoft\DesktopBridge)
msbuild Gui.slnx -p:Configuration=Debug -p:Platform=x64 -restore

# Deploy / run: open Gui.slnx in Visual Studio with the "Gui (Package)" project as startup
# and press F5. There is no CLI run path for the packaged app.
```

There is no test project, no analyzer/lint configuration, and no CI configuration in this repo.

The `Gui` folder names contain spaces and parentheses — quote paths in shell commands
(`cd "Gui/Gui (Package)"`).

## Terminal app pipeline

The app is a terminal emulator: a shell runs inside a Windows pseudoconsole, the byte stream is
parsed by libghostty-vt, and the resulting cell grid is drawn with Win2D.

```
Gui/Terminal/ConPty.cs          CreatePseudoConsole + CreateProcess, pipe reader/writer
Gui/Terminal/GhosttyNative.cs   P/Invoke surface for ghostty-vt.dll (hand-written; mirrors the C headers)
Gui/Terminal/TerminalSession.cs owns the native terminal + render state, feeds PTY bytes, captures the viewport
Gui/Terminal/TerminalInput.cs   key event -> VT escape sequence mapping
Gui/Controls/TerminalView.cs    Win2D render loop, input, resize, scroll, clipboard paste
```

Key invariants (they are easy to break and produce crashes or blank screens):

- Every native terminal call is serialized on `TerminalSession._gate`. `ghostty_terminal_vt_write` and
  `ghostty_render_state_update` must never overlap.
- Callbacks (write-pty, bell, title-changed) fire *inside* `ghostty_terminal_vt_write`, so they must not
  call back into the terminal. Title reads are deferred to the UI thread for this reason.
- `ghostty_terminal_set` takes the value *as typed by the option*: callback options want the function
  pointer itself, color options want a pointer to a `GhosttyColorRgb`, palette wants `GhosttyColorRgb[256]`.
- The render state iterators are reusable handles created once and refilled by `ghostty_render_state_get`
  (`GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR`, `GHOSTTY_RENDER_STATE_ROW_DATA_CELLS`). Cell iteration uses
  `ghostty_render_state_row_cells_select` per column so wide characters (2 cells, trailing spacer) stay
  column-aligned.
- `ConPty.BeginRead()` must be called after subscribing to `OutputReceived`, otherwise the shell banner is
  read and dropped before anyone listens.

The shell is `powershell.exe` when present, else `pwsh.exe`, else `%COMSPEC%`; `GUITERM_SHELL` overrides it.
An MSIX execution alias (e.g. `pwsh.exe` from `WindowsApps`) can lose the pseudoconsole attachment, so real
Win32 paths are preferred.

## Native dependency: libghostty-vt

`Gui/Gui/native/<platform>/ghostty-vt.dll` is built from the upstream `libghostty-vt` source drop and copied
next to `Gui.exe` by an item in `Gui.csproj`. Rebuild it with:

```bash
ZIG=/path/to/zig.exe PLATFORM=x64 ./native/build-ghostty-vt.sh
```

Zig 0.17.0 is required (the version pinned in `build.zig.zon` of the 1.4.1 source drop);
the script runs `zig build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=<arch>-windows`.
Only the x64 binary is checked in; other platforms need the script with `PLATFORM=x86` / `ARM64`.

## Build configuration notes

- Target: `net8.0-windows10.0.19041.0`, min OS `10.0.17763.0`, `Nullable` enabled, `OutputType` WinExe.
- Windows App SDK `Microsoft.WindowsAppSDK` 2.6.4 is referenced directly by **both** the app and the
  packaging project; keep the two versions in sync when upgrading, along with
  `Microsoft.Windows.SDK.BuildTools`.
- `WinUISDKReferences` is `false` in both projects (WinAppSDK supplied via PackageReference, not the
  MSIX framework package references).
- Publish profiles are `SelfContained=true`, `PublishSingleFile=False`; `PublishReadyToRun` and
  `PublishTrimmed` are enabled for every configuration except `Debug`.

## App / packaging specifics

- `Package.appxmanifest` declares `runFullTrust` plus the restricted `systemai:Capability Name="systemAIModels"`
  capability (the `systemai` namespace is listed in `IgnorableNamespaces`). `DefaultLanguage` is `zh-CN`
  and the package is unsigned (`AppxPackageSigningEnabled` false).
- `.gitignore` excludes `bin/`, `obj/`, `.vs/`, MSIX/NuGet artifacts and the local `_ref/` scratch tree
  (upstream source drop, Zig toolchain, ConPTY probes); the prebuilt
  `Gui/Gui/native/*/ghostty-vt.dll` is re-included and must stay tracked.
- The repo has no commits so far.
