# Gui

Gui 说：“我就是要一步一步一步一步一步一步地追到最高，我要做 Windows 上的 Ghostty”。

https://github.com/user-attachments/assets/b5013ff2-d1bd-45a0-a6ec-2fb226c59656

[English](README.md) | **简体中文**

## 配置

配置就是 Ghostty 的格式，位于 `%APPDATA%\gui\config`

## 运行要求

- Windows 10 1809（17763）及以上，x64 或 ARM64
- 安装包分两种：普通的那种把 .NET 10 运行时一起带上，机器上什么都不用装；`-lite` 那种不带，
  如果机器上没有 .NET 10，安装程序会先下载微软官方安装器并装上（会请求管理员权限）
- 两种体积都按架构各出一个，所以一次发布会产出四个安装包；本机验证的是 x64，ARM64 的原生
  内核来自同一个官方 libghostty-vt 预编译发布，而上游只在 x86_64 上运行过它

## 构建

```bash
# 应用（发布版加 -c Release）
dotnet build Gui/Gui/Gui.csproj -p:Platform=x64
Gui/Gui/bin/x64/Debug/net10.0-windows10.0.19041.0/win-x64/Gui.exe

# 原生内核：官方预编译的 libghostty-vt DLL，入库在 Gui/Gui/native/{x64,ARM64}；
# 换版本时重新安装
./native/get-ghostty-vt.ps1

# 安装包：测试 + 发布 + Inno Setup -> dist/Gui-<version>-<arch>-setup.exe（自包含）
# 与 dist/Gui-<version>-<arch>-lite-setup.exe（需要 .NET 10）；-Variant full|lite 只做一种，
# -Platform x64,ARM64 两个架构一起打包
./build-installer.ps1 0.1.0
```

## 测试

```bash
dotnet test tests/Gui.LogicTests -c Release         # 配置、按键映射、主题、字符宽度、编码
dotnet run --project tests/Gui.AbiTests -c Release  # 结构体布局对 ghostty-vt.dll 校验
dotnet run --project tests/Gui.PerfTests -c Release # 解析 / 每帧读路径 / 滚动裁剪
./tests/perf-gui.ps1                                # 启动、shell 交接、空闲开销
./tests/perf-terminals.ps1 -Mode startup            # 与 Windows Terminal、otty 对比
./tests/osc7501.ps1                                 # 用真实窗格验证程序状态徽标
```

## 致谢

**[Ghostty](https://github.com/ghostty-org/ghostty)**

Gui 与 Ghostty 项目没有隶属关系，也未获其背书。

## 第三方组件

| 组件 | 许可 |
| --- | --- |
| libghostty-vt（`Gui/Gui/native/*/ghostty-vt.dll`） | MIT，官方 Windows 预编译包 |
| ConPTY（`native/x64/conpty/`） | MIT，见 `ConPTY-LICENSE.txt`；ARM64 使用系统自带的 ConPTY |
| JetBrains Mono Nerd Font | SIL OFL 1.1，见 `Assets/Fonts/OFL.txt` |
| 内置主题 | Ghostty 主题集，MIT，见 `Assets/Themes/CREDITS.md` |
| 安装程序简体中文消息（`installer/languages/`） | MIT，见目录中的许可文件 |
| Windows App SDK、Win2D、Silk.NET、Vortice | 各自的许可，见 `Gui.csproj` |

Gui 以 MIT 许可发布，见 [LICENSE](LICENSE)。
