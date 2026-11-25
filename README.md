# Gui

Gui says: "Step by step, step after step after step, I climb to the very top. I want to be Ghostty on Windows."

https://github.com/user-attachments/assets/b5013ff2-d1bd-45a0-a6ec-2fb226c59656

**English** | [简体中文](README.zh-CN.md)

## Configuration

The config is Ghostty's own format, at `%APPDATA%\gui\config`.

## Requirements

- Windows 10 1809 (build 17763) or later, x64 or ARM64
- The installer comes in two sizes: the plain one carries the .NET 10 runtime, the `-lite` one
  leaves it out; when the machine has no .NET 10 the lite setup downloads Microsoft's runtime
  installer and runs it first (Windows asks for permission)
- Each size is built per architecture, so a release publishes four setups; `x64` is the one
  tested on this machine, and the ARM64 core comes from the same official libghostty-vt
  release that upstream only runs on x86_64

## Build

```bash
# App (add -c Release for a release build)
dotnet build Gui/Gui/Gui.csproj -p:Platform=x64
Gui/Gui/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/Gui.exe

# Native core: the official prebuilt libghostty-vt DLLs, checked in under
# Gui/Gui/native/{x64,ARM64}; re-install them after changing the version
./native/get-ghostty-vt.ps1

# Installers: tests, publish, Inno Setup -> dist/Gui-<version>-<arch>-setup.exe
# (self-contained) and dist/Gui-<version>-<arch>-lite-setup.exe (needs .NET 10);
# -Variant full|lite builds one, -Platform x64,ARM64 packages both
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
| libghostty-vt (`Gui/Gui/native/*/ghostty-vt.dll`) | MIT, official prebuilt Windows build |
| ConPTY (`native/x64/conpty/`) | MIT, see `ConPTY-LICENSE.txt`; ARM64 uses the system ConPTY |
| JetBrains Mono Nerd Font | SIL OFL 1.1, see `Assets/Fonts/OFL.txt` |
| Bundled themes | Ghostty's theme collection, MIT, see `Assets/Themes/CREDITS.md` |
| Installer Chinese messages (`installer/languages/`) | MIT, see the licence file there |
| Windows App SDK, Win2D, Silk.NET, Vortice | their own licences, see `Gui.csproj` |

Gui is released under the MIT licence; see [LICENSE](LICENSE).
