# CODEBUDDY.md

This file provides guidance to CodeBuddy Code when working with code in this repository.

## Repository layout

```
Gui.slnx                          # solution (new XML .slnx format), platforms: ARM64 / x64 / x86
Gui/Gui/                          # WinUI 3 application project (Gui.csproj) — the only project
  App.xaml(.cs)                   # Application entry point; resolves config/theme, creates MainWindow
  MainWindow.xaml(.cs)            # thin shell: sizes/centers the window, attaches the Workspace
  Diag.cs                         # startup/crash logging (%TEMP%\gui-startup.log)
  Term/                           # the terminal application (see the table below)
  Assets/Fonts/                   # bundled JetBrainsMono Nerd Font (4 faces)
  Assets/Themes/light|dark/       # ~890 bundled Ghostty themes, split by background brightness
  native/<platform>/ghostty-vt.dll# prebuilt libghostty-vt (see native/build-ghostty-vt.sh)
  app.manifest                    # OS compatibility + PerMonitorV2 DPI
  Properties/PublishProfiles/     # win-x64 / win-x86 / win-arm64 .pubxml
native/build-ghostty-vt.sh        # builds ghostty-vt.dll for Windows via Zig
Gui/Gui (Package)/                # leftover MSIX packaging project — NOT in the solution any more
```

The app is **unpackaged** (`WindowsPackageType=None`) and **self-contained** for the Windows App
SDK (`WindowsAppSDKSelfContained=true`), the same deployment shape as the native project this UI
was ported from. There is no MSIX, no deploy step and no `shell:AppsFolder` activation: building
the project and running the exe is the whole story.

## Commands

Always pass an explicit `Platform` (x64 is the practical default); the solution has no AnyCPU
configuration and the csproj `Platforms` are `x86;x64;ARM64`. The RuntimeIdentifier follows
`Platform` (`win-x64` / `win-x86` / `win-arm64`), so the output lands in a RID subfolder.

```bash
# Build
dotnet build Gui/Gui/Gui.csproj -p:Platform=x64

# Run (no packaging, no deploy)
Gui/Gui/bin/x64/Debug/net8.0-windows10.0.19041.0/win-x64/Gui.exe

# ABI contract test (no WinUI, no NuGet packages)
dotnet run --project tests/Gui.AbiTests -c Release

# Unit tests (no WinUI; includes the repository encoding check)
dotnet test tests/Gui.LogicTests -c Release
```

`tests/Gui.AbiTests` compiles the real `Term/GhosttyNative.cs` and verifies every struct
layout and option constant against `ghostty_type_json()` from
`Gui/Gui/native/x64/ghostty-vt.dll`. It then runs a behavioural smoke check on the same DLL:
a terminal is fed 5,000 lines (CRLF, plus a bare-LF run that must wrap into more rows) and the
scrollback caps are asserted through `ghostty_terminal_set` / `ghostty_terminal_get`. Set `GUI_GHOSTTY_DLL` to test a different
build of the DLL. Exit code 0 = pass, 1 = ABI mismatch.

`tests/Gui.LogicTests` compiles `Term/Config.cs`, `Term/KeyMap.cs`, `Term/Theme.cs`,
`Term/TerminalTheme.cs`, `Term/WideText.cs` and `Diag.cs` and covers the config parser,
the key map, the theme helpers, the cell-width rules, the log level parsing and the
UTF-8 BOM + CRLF rule for every source file in the repo.

There is no analyzer/lint configuration in this repo. `.github/workflows/ci.yml` builds the
solution and runs both test suites on Windows.

The `Gui` folder names contain spaces and parentheses — quote paths in shell commands
(`cd "Gui/Gui (Package)"`).

## Diagnostics

`Diag.cs` writes one record per line to `%TEMP%\gui-startup.log`:

```
2026-11-05 22:05:08.319 INFO  [tap] in 30 | 1B 5B 3F 32 35 6C ... |.[?25l...|
--- session pid=20568 started=2026-11-05T22:08:35 level=Info ---
```

- Verbosity comes from `GUI_LOG`: `debug`, `info` (default), `warn`, `error`, `off`. An
  unknown value keeps the default so a typo cannot silence the log.
- The file is rotated to `gui-startup.log.1` once it passes 2 MB, and each run starts with
  a session header so several sessions can share the file.
- `Diag.Guard` wraps the callbacks XAML invokes; an exception there is a fail-fast
  (0xC000027B) with no managed stack, so it is logged instead of crashing the app.

Two optional debug switches, both also in the command palette:

- **Debug tap** (`debug-tap = true` in the config, `GUI_TAP=1`, or the palette row)
  records the raw byte stream in both directions as hex, which is what Windows Terminal
  shows in its "debug tap" pane.
- **Crash dumps** (`crash-dumps = true`, or the palette row) registers the app under
  `HKCU\Software\Microsoft\Windows\Windows Error Reporting\LocalDumps` with `DumpType=2`
  (full dump), so a fail-fast leaves a dump in `%TEMP%\gui-dumps`.

## Terminal app pipeline

A shell runs inside a Windows pseudoconsole, the byte stream is parsed by libghostty-vt, and the
resulting cell grid is drawn with Win2D.

| Path | Role |
| --- | --- |
| `Term/Config.cs` | Ghostty-shaped text config in `%APPDATA%\gui\config` |
| `Term/Theme.cs` | Design tokens; two app palettes plus a separately resolved window palette |
| `Term/TerminalTheme.cs` | Loads bundled Ghostty themes into a 256-color palette |
| `Term/TerminalSession.cs` | libghostty-vt facade + ConPTY + shell process + reader thread |
| `Term/ConPty.cs` | `CreatePseudoConsole` + `CreateProcess`, pipe reader/writer |
| `Term/GhosttyNative.cs` | P/Invoke surface for `ghostty-vt.dll` |
| `Term/KeyMap.cs` | `VirtualKey` → libghostty-vt physical key codes |
| `Term/ShellIcon.cs` | Extracts the shell's own icon for the pane header / tab chip; used as Windows supplies it — never recolored per theme, and no plate or outline is put behind it |
| `Term/TerminalRenderer.cs` | Win2D renderer for a render state |
| `Term/TerminalPane.cs` | One terminal surface: frame, header, session, selection, focus |
| `Term/Workspace.cs` (+ `.Split`, `.Palette`, `.Window` partials) | Window chrome, tabs, split tree, command palette, IME host |

Key invariants (they are easy to break and produce crashes or blank screens):

- Every native terminal call is serialized: PTY bytes are queued by the reader thread and only
  drained on the UI thread, where `ghostty_terminal_vt_write` and `ghostty_render_state_update`
  never overlap.
- Callbacks (write-pty, title, pwd) fire *inside* `ghostty_terminal_vt_write`, so they must not
  re-enter the terminal through anything but the intended read APIs.
- `ghostty_terminal_set` takes the value *as typed by the option*: callback options want the
  function pointer itself, color options want a pointer to an `Rgb`.
- `GhosttyRenderState` is incremental: `updateRenderState()` consumes the dirty state, and
  `CanvasControl` clears its surface before every `Draw`, so the renderer always paints a frame.
- The row iterator must be bound with `GHOSTTY_RENDER_STATE_DATA_ROW_ITERATOR` before the first
  `row_iterator_next`, or the first call faults.
- Sized structs (`GhosttyStyle`, `GhosttyRenderStateCursor`, `GhosttySelection`, `GhosttyGridRef`,
  …) need their `Size` field set before being passed to a get/set call.
- `ConPty.BeginRead()` must be called after subscribing to `OutputReceived`, otherwise the shell
  banner is read and dropped before anyone listens.
- Shell icons are extracted with GDI (any thread) but decoded into a `BitmapImage` on the UI
  thread; creating the image source on a worker thread makes the icon silently fall back to the
  MDL2 glyph.

## Window chrome and panes

The window has no system title bar: `Workspace.Attach` calls
`OverlappedPresenter.SetBorderAndTitleBar(true, false)` and draws the 38 DIP header itself in the
top layout. The header is plain client area and the window drag region — it records `GetCursorPos`
in screen pixels and calls `AppWindow.Move` from `PointerMoved`; a double press toggles maximize.
The side placements have no title bar at all: the island starts at the window's own 6 DIP inset
and the header element shrinks to a 32 DIP strip at y=7 that carries only the caption buttons, so
they sit in the pane header's band and share the centre line with the pane's action buttons. The
tab rail carries the drag there.

A tab owns a binary split tree; each split becomes a nested two-cell `Grid` (6 DIP spacing) so no
empty cells are laid out. Three invariants keep the rebuild safe:

- `Ctrl+Shift+B` hides and shows the tab rail. The top layout has no rail, so the chord does
  nothing there.

- The pane's own border is the rail's splitter: a `ResizeGrip` strip (a `UserControl`, because
  the cursor lives behind `UIElement.ProtectedCursor`) sits in the body grid over the boundary —
  8 DIP in the gap, 2 DIP over the pane — and its drag becomes the rail's width. It clamps to
  160 DIP and keeps 320 DIP for the panes; the drag end saves the width under the current
  side's key (`sidebar-width-left` / `sidebar-width-right`) so the two sides stay independent.
  The strip paints nothing at all — the border is already drawn and the resize cursor is the
  only cue. Collapsing the rail hides the strip. The floor is 160 DIP for a left rail and
  `2 * tool button + 3 * caption button` for a right one, which shares its top row with the
  caption buttons. Moving the rail to the other side reads that side's own saved width and
  re-clamps it with the new side's floor before the chrome is rebuilt (and saves it), so a
  left rail dragged to 160 does not land on the right at 160.
- XAML keeps a removed subtree alive until it has been laid out, so `AddChild` detaches a pane
  from its live parent *and* from the grids the previous pass built.
- Close animations keep the outgoing pane's element and split grid alive; `Relayout` settles them
  first and refuses to nest (`_relayouting`).
- A pane's visual can outlive its `TerminalPane`, so `Close()` unhooks every canvas handler and
  stops the reader thread before dropping the session.

The rail's top row shows the sidebar toggle, new tab and search as three buttons while they
fit; only when they do not fit do the latter two become one `MenuFlyout` overflow button
(edge-aligned below it), with the toggle still outside the menu. A right-hand rail shares that
row with the caption buttons, so it switches at ~238 DIP and can be dragged down to 198 DIP;
a left-hand rail always has room for all three.

All keyboard input goes through one shared, invisible `TextBox` (the IME sink): `PreviewKeyDown`
runs the shortcut table and the key encoder, and `TextChanged` forwards printable text. The sink
is repositioned every 250 ms onto the focused pane's cursor and must stay hit-testable — clearing
`IsHitTestVisible` silently stops text and IME input. It sits above the shell and is exactly one
cell wide, so a left press on the cursor cell would otherwise be swallowed (no selection could
start there). `AddHandler(..., handledEventsToo: true)` on the sink forwards press, move and
release to the focused pane; the sink keeps the pointer capture, so the whole drag arrives
there rather than at the canvas.

## Configuration and theming

`%APPDATA%\gui\config` is ghostty-shaped: one `key = value` per line, `#` starts a comment.
Recognized keys: `command`/`shell`, `working-directory`/`cwd`, `font-family`, `font-size`,
`unfocused-split-opacity`, `unfocused-split-fill`, `tab-position`, `compact`, `sidebar-width-left`,
`sidebar-width-right` (written by the app when each rail's edge is dragged, 120..800), `theme`,
`window-theme`, `scrollback-limit`, `scrollback-limit-lines`, `scrollbar`, `debug-tap`,
`crash-dumps`.

- `compact` is `false` (default: panes float as rounded islands with a 6 DIP gap) or `true`
  (panes run edge to edge in a square content area, split only by 1 DIP dividers; the tab
  chips square off at the bottom and the rail's tool row lifts to the window edge). The
  command palette row toggles it live and persists the choice.
- `theme` is `Gui Dark` (default), `Gui Light`, or any bundled Ghostty theme name; a custom
  theme also drives the chrome through tonal steps off its background.
- `window-theme` is `system` (default), `light`, or `dark` and constrains `theme`: only themes
  whose background brightness matches the window are listed and usable.
- The command palette (`Ctrl+Shift+P`) switches both live and persists the choice with
  `Config.SaveValue`.
- `Ctrl+Shift+,` re-reads the file and applies it live: theme, window theme, font, split
  dimming, the scroll settings and the tab rail (`tab-position` = `top` / `left` / `right`) all
  take effect without a restart. `command` and `working-directory` only affect new panes and
  tabs.
- `scrollback-limit` is the terminal's scrollback budget in bytes: a bare integer (ghostty
  parses base 0, so `0x…` works), `unlimited`, or `0` to disable scrollback; the default is
  ghostty 1.4's 50 MB. `scrollback-limit-lines` is the row cap with the same grammar, default
  `unlimited`; both caps apply and whichever is reached first trims the history. `scrollbar`
  is `system` (default: visible only while the scrollback does not fit) or `never`.
- Bundled fonts are registered for the process with `AddFontResourceEx(FR_PRIVATE)` so DirectWrite
  resolves `JetBrainsMono Nerd Font` even when it is not installed.

## Native dependency: libghostty-vt

`Gui/Gui/native/<platform>/ghostty-vt.dll` is built from the upstream `libghostty-vt` source drop
and copied next to `Gui.exe` by an item in `Gui.csproj`. Rebuild it with:

```bash
ZIG=/path/to/zig.exe PLATFORM=x64 ./native/build-ghostty-vt.sh
```

Zig 0.17.0 is required (the version pinned in `build.zig.zon` of the 1.4.1 source drop);
the script runs `zig build -Demit-lib-vt -Doptimize=ReleaseFast -Dtarget=<arch>-windows`.
Only the x64 binary is checked in; other platforms need the script with `PLATFORM=x86` / `ARM64`.

## Build configuration notes

- Target: `net8.0-windows10.0.19041.0`, min OS `10.0.17763.0`, `Nullable` enabled, `OutputType` WinExe.
- `Microsoft.WindowsAppSDK` is pinned to **1.8.260921001** and `Microsoft.Windows.SDK.BuildTools` to
  **10.0.28000.2957**, matching the native project's `packages.config`; keep them in sync. Win2D is
  `Microsoft.Graphics.Win2D` 1.4.0.
- Do **not** drop `WindowsAppSDKSelfContained`: without it the DeploymentManager initializer tries to
  install the runtime package and fails with `0x80070005` (ACCESS_DENIED) before `App`'s constructor
  runs, and the process exits with no UI.
- Publish profiles are `SelfContained=true`, `PublishSingleFile=False`; `PublishReadyToRun` and
  `PublishTrimmed` are enabled for every configuration except `Debug`.
- `Assets\**\*` is copied to the output directory; the app loads fonts and themes from
  `AppContext.BaseDirectory\Assets`, not from the project directory.

## Packaging and diagnostics

- `Gui/Gui/Package.appxmanifest` is not consumed by the build (the app is unpackaged); it is kept only
  for the `systemai:Capability Name="systemAIModels"` declaration. `Gui/Gui/app.manifest` is the
  classic Win32 manifest.
- A window that never appears with no log lines means the failure happened before `App`'s constructor —
  check that the Windows App SDK runtime (self-contained copy) sits next to the exe.
- `.gitignore` excludes `bin/`, `obj/`, `.vs/`, MSIX/NuGet artifacts and the local `_ref/` scratch tree
  (upstream source drop, Zig toolchain, ConPTY probes); the prebuilt
  `Gui/Gui/native/*/ghostty-vt.dll` is re-included and must stay tracked.
- The repo has no commits so far.
