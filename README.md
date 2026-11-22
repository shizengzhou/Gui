# Gui

Gui says: "Step by step, step after step after step, I climb to the very top. I want to be Ghostty on Windows."

https://github.com/user-attachments/assets/b5013ff2-d1bd-45a0-a6ec-2fb226c59656

**English** | [简体中文](README.zh-CN.md)

## Configuration

The config is Ghostty's own format, at `%APPDATA%\gui\config`.

### Quick terminal

**Quick terminal** in the command palette (or a `global:` keybind, see below) drops a
terminal out of a screen edge over whatever you are doing, even while another application has
the focus, and takes it away again on the next press or as soon as it loses focus. It is one surface of its own: hiding it keeps its shell
and scrollback, and it is never part of the saved window session.

| Setting | Values | Default |
| --- | --- | --- |
| `quick-terminal-position` | `top`, `bottom`, `left`, `right`, `center` | `top` |
| `quick-terminal-size` | `50%`, `300px`, `40%,500px` (primary,secondary) | 400px on the primary axis, full on the other |
| `quick-terminal-screen` | `mouse` (monitor under the pointer), `main` | `mouse` |
| `quick-terminal-autohide` | `true`, `false` | `true` |
| `quick-terminal-animation-duration` | seconds; `0` shows and hides it instantly | `0.2` |

There is no default chord, so the palette row is the only way in until one is bound. A
`global:` binding is registered with Windows, so it also works while another application has
the focus:

```
keybind = global:ctrl+grave_accent=toggle_quick_terminal
```

## Requirements

- Windows 10 1809 (build 17763) or later, x64
- The installer comes in two sizes: the plain one carries the .NET 10 runtime, the `-lite` one
  leaves it out and needs .NET 10 on the machine (the setup checks for it and links to the
  download when it is missing)
- The checked-in native core is x64 only; x86 and ARM64 need
  `native/build-ghostty-vt.sh` with the matching `PLATFORM` (Zig 0.17)

## Build

```bash
# App (add -c Release for a release build)
dotnet build Gui/Gui/Gui.csproj -p:Platform=x64
Gui/Gui/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/Gui.exe

# Installers: tests, publish, Inno Setup -> dist/Gui-<version>-x64-setup.exe (self-contained)
# and dist/Gui-<version>-x64-lite-setup.exe (needs .NET 10); -Variant full|lite builds one
./build-installer.ps1 0.1.0
```

## Tests

```bash
dotnet test tests/Gui.LogicTests -c Release         # config, key map, themes, cell width, encoding
dotnet run --project tests/Gui.AbiTests -c Release  # struct layouts against ghostty-vt.dll
dotnet run --project tests/Gui.PerfTests -c Release # parse / per-frame / scrollback
./tests/perf-gui.ps1                                # start-up, shell hand-off, idle cost
./tests/perf-terminals.ps1 -Mode startup            # against Windows Terminal and otty
./tests/osc7501.ps1                                 # program-status badge through a real pane
```

## Thanks

**[Ghostty](https://github.com/ghostty-org/ghostty)**

Gui is not affiliated with or endorsed by the Ghostty project.

## Third-party components

| Component | Licence |
| --- | --- |
| libghostty-vt (`Gui/Gui/native/*/ghostty-vt.dll`) | MIT, from the Ghostty source |
| ConPTY (`native/*/conpty/`) | MIT, see `ConPTY-LICENSE.txt` |
| JetBrains Mono Nerd Font | SIL OFL 1.1, see `Assets/Fonts/OFL.txt` |
| Bundled themes | Ghostty's theme collection, MIT, see `Assets/Themes/CREDITS.md` |
| Installer Chinese messages (`installer/languages/`) | MIT, see the licence file there |
| Windows App SDK, Win2D, Silk.NET, Vortice | their own licences, see `Gui.csproj` |

Gui is released under the MIT licence; see [LICENSE](LICENSE).
