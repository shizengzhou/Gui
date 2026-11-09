﻿# CODEBUDDY.md

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
| `Term/ShellIntegration.cs` | PowerShell prompt wrapper (OSC 133 + OSC 7) and the OSC 133 mark parser |
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

Splits can be rearranged with the mouse, matching Ghostty 1.4: the pane header itself is the
grab bar once the tab has two or more splits (`drag-handle = auto`; `always` keeps it draggable
on a lone pane too, `never` turns the drag off). Only the shell icon and the cwd text are the
grab area and show the move cursor — the text block is left-aligned so it measures to its
visible glyphs, which makes the area follow the path after a `cd` — while the rest of the band
and the action buttons keep the arrow. `Border` is sealed and `UIElement.ProtectedCursor` can
only be set from a subclass, so the band and the actions host are cursor-only `Grid` subclasses.
Grabbing the header and dragging hands over the whole tile to `Term/Workspace.Drag.cs`, which
translates the pointer into the surface host and paints a floating card plus a soft accent
overlay over the half of the target split the pane will take — the side is whichever offset from
the target's centre dominates. Dropping lifts the leaf out of the split tree and re-inserts it
beside the target, so the pane keeps its live session and the shell, scrollback and working
directory travel with it. Esc or a lost capture cancels, hover focus is suppressed while the
drag runs, and a zoomed tab (which paints only one pane) refuses the drag.

Split boundaries are resize handles too: every non-leaf split is a three-track `Grid` (first
half, the 6 DIP gap or compact's 1 DIP divider, second half) so the boundary is a real track
instead of a spacing, and an invisible 10 DIP `ResizeGrip` fills that track with negative
margins that reach into both panes (the tab rail's splitter control, now parameterized with its
cursor). Dragging writes the node's `Ratio` — the first child's star weight — and re-applies the
grid's row/column stars live, so the shells reflow as the line moves; the ratio lives in the
node, so a rebuild (new split, close, zoom) keeps the size. Each side is clamped to 40 DIP, and
the drag maths subtracts the gap track so the line stays under the pointer.

The rail's top row shows the sidebar toggle, new tab and search as three buttons while they
fit; only when they do not fit do the latter two become one `MenuFlyout` overflow button
(edge-aligned below it), with the toggle still outside the menu. A right-hand rail shares that
row with the caption buttons, so it switches at ~238 DIP and can be dragged down to 198 DIP;
a left-hand rail always has room for all three.

`Ctrl+Shift+N` (or the palette's New window row) opens another `MainWindow`. `App` keeps every
open window in a list — WinUI drops a `Window` that nothing references — and removes it again
when it closes; the new window cascades down-right and starts in the focused shell's directory
when `window-inherit-working-directory` is on. `Theme` is process-wide, so the window that
commits a theme change (the palette, or `Ctrl+Shift+,`) copies the theme fields to the other
windows and repaints them through `App.ApplyThemeToOtherWindows`; previewed themes stay on the
window under the pointer until they are committed. Palette rows that persist a process-wide
layout setting (tab rail, compact density) call `App.ReloadOtherWindows`, which re-reads the
file in every other window; the `_applyingConfig` guard keeps a peer reload from bouncing back.

All keyboard input goes through one shared, invisible `ContentControl` (the keyboard anchor):
`PreviewKeyDown` runs the shortcut table and the key encoder. Text and IME do not go through a
TextBox. `Term/ImeHost.cs` subclasses the window's input-site HWNDs and turns `WM_CHAR` into
shell text, and the IME composes into an **invisible 1x1 `TextBox`** parked on the cursor cell:
WinUI only feeds the text services for a real text control, so a custom-drawn surface has no
other way to receive composition (IMM32 is dead here — XAML calls `ImmDisableLegacyIME`, and
`ITfKeystrokeMgr` never hands a key to a TIP in this process, measured with the command
palette's own search box). The box is `Opacity = 0`, never shows anything, and is emptied the
moment its text is handed over: `TextChanged` flushes plain typing, `TextCompositionChanged`
mirrors the preedit onto the pane, and `TextCompositionEnded` drains the sink one dispatcher
turn later (the IME overwrites the raw pinyin with the picked characters in between). A
cancelled composition sets `_imeDiscard` so its raw intermediate string is dropped instead of
typed. While a composition is open the key handler swallows every key (the IME owns them and
re-injects what it does not use). When a real TextBox (the find bar,
the palette) has focus the host passes every message through to XAML. The anchor is repositioned every 250 ms
onto the focused pane's cursor. It sits above the shell and is exactly one cell wide, so a left
press on the cursor cell would otherwise be swallowed (no selection could start there).
`AddHandler(..., handledEventsToo: true)` on the anchor forwards press, move and release to the
focused pane.

## Configuration and theming

`%APPDATA%\gui\config` is ghostty-shaped: one `key = value` per line, `#` starts a comment.
Every Ghostty 1.4 key is recognized — `GHOSTTY-CONFIG-TODO.md` records which ones this build
actually applies. On top of Ghostty's surface the app adds `tab-position`, `compact`,
`sidebar-width-left`, `sidebar-width-right` (written by the app when each rail's edge is
dragged, 120..800), `debug-tap` and `crash-dumps`.

- Defaults follow Ghostty 1.4: `font-size = 12` points (about 16 DIPs), `faint-opacity = 0.5`,
  `background-blur = false`, `right-click-action = context-menu` (the pane shows a Copy/Paste
  `MenuFlyout`), `clipboard-trim-trailing-spaces = true`, `window-padding-x/y = 2`.
- `background-opacity` below 1 makes the whole window — chrome included — translucent through a
  single tinted backdrop modelled on Windows Terminal: acrylic when `background-blur = true`, a
  plain unblurred color brush on Windows 11 when it is false (Windows 10 keeps acrylic, which is
  what Windows Terminal does too). The root, shell scrim and content card are cleared and the
  panes stay transparent, so the opacity is applied exactly once instead of stacking on the
  system acrylic tint.
- `window-padding-x/y` are in points (default `2`), like Ghostty: one value for both sides
  or `left,right` / `top,bottom`. At layout time they follow Ghostty's
  `floor(points * dpi / 72)` scaling into physical pixels, so the padding tracks screen DPI.
- `mouse-scroll-multiplier` is the number of rows per wheel notch (`discrete`, default 3);
  `precision:` is accepted, but the Windows wheel is a discrete device.
- `copy-on-select` accepts Ghostty 1.4's `none` / `primary` / `clipboard` / `both` (plus
  `true`/`false`); Windows has no selection clipboard, so `primary` does nothing and `both`
  copies to the system clipboard.
- OSC 52 / the kitty clipboard protocol: libghostty-vt's `clipboard_write` / `clipboard_read`
  callbacks are wired in `TerminalSession`, which answers synchronously through the Win32
  clipboard (`Term/ClipboardAccess.cs`) because the VT stream waits for the reply. Only text
  MIME types are served; images are `unsupported`. `clipboard-write` (default `allow`) and
  `clipboard-read` (default `ask`) honor allow/deny; `ask` has no blocking prompt in this
  build, so it behaves like `deny`. `clipboard-write-limit-bytes` goes to
  `OPT_CLIPBOARD_WRITE_MAX_BYTES`, so the native layer rejects oversized writes first.
- `mouse-reporting` (default on) drives the libghostty mouse encoder from the terminal's own
  modes; `mouse-shift-capture` (false/true/always/never) decides whether Shift+click still
  goes to the program; `middle-click-action` pastes on Windows for both paste values;
  `mouse-hide-while-typing` hides the pointer through `ShowCursor` until it moves again.
- `scroll-to-bottom` splits into keystroke/output halves (`no-keystroke` / `no-output`).
- `link-url` and `link-osc8` open links on Ctrl+click: OSC 8 hyperlinks resolve first through
  `ghostty_grid_ref_hyperlink_uri`, then a plain URL scan of the cell's text.
  `link-previews` (`true` / `osc8` / `false`) shows a URL badge next to the pointer.
- `window-padding-balance` splits the pixels left after the last whole cell between both
  sides (off by default, like Ghostty). Column math uses the font's exact advance rather
  than the rounded-up cell width, so the leftover on the right is under one cell;
  `window-title-font-family` applies to the tab titles. `window-width`/`window-height`
  grow the window by whole cells from the current grid, and `window-position-x`/`-y` place
  the initial window in screen pixels.
- `click-repeat-interval` drives double- and triple-click selection (0 means Ghostty's 500 ms
  non-macOS default); `clipboard-paste-protection` asks before pasting multi-line or control
  text, and `clipboard-paste-bracketed-safe` treats bracketed pastes as safe.
- `bell-features` (system / audio / attention / title / border) is wired to libghostty's
  `OPT_BELL`; `bell-audio-path` and `bell-audio-volume` drive a `MediaPlayer`.
- `config-file` includes load after the including file, relative to the file that names them;
  `?path` is optional and cycles are ignored with a warning.
- Search (`Ctrl+Shift+F` or the palette's Find row) is a per-pane find bar backed
  by libghostty's search API: it covers scrollback, scans incrementally on a 25 ms timer, shows
  all candidate matches in `search-background` and the selected one in
  `search-selected-background`, and navigates with Enter / Shift+Enter. Esc or clicking the
  terminal closes it.
- The pane header mirrors the shell's directory: cmd.exe is polled through its process
  environment block, and a bare PowerShell shell is started with a prompt wrapper
  (`-NoExit -EncodedCommand`) that reports the location over OSC 7 — the only cwd sequence
  libghostty-vt feeds into its pwd data, since it treats ConEmu's OSC 9;9 as a notification and
  PowerShell's `Set-Location` does not touch the process working directory. `ExecutableToken`
  keeps the full unquoted path (the auto-detected `pwsh.exe` lives under `C:\Program Files`),
  which the hook's "bare shell only" test needs.
- `shell-integration` (default `detect`) gates that wrapper; `none` leaves the shell alone and
  the Unix shell names are recognized with a Notice. The session only injects into a bare
  PowerShell command. The wrapper also emits OSC 133 A/B/C/D: `Term/ShellIntegration.cs`'s
  mark parser turns C/D into `TerminalSession.CommandRunning` and an `OnCommandFinished`
  callback. `shell-integration-features` drives the `cursor` (bar cursor at the prompt, with
  `cursor:steady`) and `title` (cwd as the title) parts; `sudo`, `ssh-*` and `path` are parsed
  but have no Windows behavior. Command finish feeds `notify-on-command-finish` (bell via
  `bell-features`, notify via the Windows app notification, with a taskbar-flash fallback) and
  the running state feeds `confirm-close-surface` at window close.
- 1.4 additions such as `link` (upstream is itself a TODO),
  `scrollback-compression`, `clipboard-write-limit-bytes`, `vt-window-resize-allowed`,
  `vt-xt-checksum-*` and the new `gtk-*` keys are parsed without warnings, but this build has
  no behavior behind them yet.
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
  take effect without a restart. Split panes hold their own config copy (their session command
  and working directory are per pane), so the reload pushes the new values into every pane
  through `TerminalPane.SyncConfig` before anything re-reads them. `command` and
  `working-directory` only affect new panes and tabs. Each window also tracks its own
  `_appliedLanguage`, because `Strings.Current` is process-wide and the window that reloads
  switches it before the others run, which would otherwise look like "no change".
- `scrollback-limit-bytes` (formerly `scrollback-limit`) is the terminal's scrollback budget in bytes: a bare integer (ghostty
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
