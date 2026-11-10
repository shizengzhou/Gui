# Ghostty 默认快捷键清单（TODO）

来源：`D:\dev\ghostty` 源码 `src/config/Config.zig` 的 `Keybinds.init`（默认绑定在源码里直接注册），核对于 2026-11-10。
参考文档：<https://ghostty.org/docs/config/keybind/reference>

说明：`ctrlOrSuper` 在 macOS 上是 `Cmd`，其它平台是 `Ctrl`；`Super` 在 Windows 上指 Win 键。同一个按键后注册的绑定会覆盖先注册的，因此下表按**最终生效**结果列出。Gui 是 Windows 项目，macOS 段仅作对照，可直接跳过。

状态：`[x]` 当前 Gui 已绑定；`[ ]` 尚未绑定或与 Windows 无关。实现主要在 `Gui/Gui/Term/Workspace.Window.cs` 的 `OnKey`。

用法：完成一项把 `[ ]` 改成 `[x]`，需要时在行尾补实现位置。

## 通用（所有平台）

- [x] `Ctrl/Cmd+Shift+,` — `reload_config` 重新加载配置
- [x] `Ctrl/Cmd+,` — `open_config` 用系统编辑器打开配置（`Workspace.Window.cs`）
- [ ] `Copy` 键（无修饰） — `copy_to_clipboard:mixed`（WinUI 没有对应虚拟键）
- [ ] `Paste` 键（无修饰） — `paste_from_clipboard`（WinUI 没有对应虚拟键）
- [x] `Ctrl/Cmd+=` — `increase_font_size:1`
- [x] `Ctrl/Cmd++` — `increase_font_size:1`
- [x] `Ctrl/Cmd+-` — `decrease_font_size:1`
- [x] `Ctrl/Cmd+0` — `reset_font_size`
- [x] `Ctrl+Shift+Super+J` — `write_screen_file:copy`（Win 组合可能被系统占用）
- [x] `Ctrl/Cmd+Shift+J` — `write_screen_file:paste`
- [x] `Ctrl/Cmd+Shift+Alt+J` — `write_screen_file:open`
- [x] `Shift+←/→/↑/↓` — `adjust_selection` 扩展选区
- [ ] `Shift+PageUp/PageDown` — `adjust_selection:page_up/page_down`（Windows/Linux 下方被滚动绑定覆盖）
- [ ] `Shift+Home/End` — `adjust_selection:home/end`（Windows/Linux 下方被滚动绑定覆盖）
- [x] `Ctrl+Shift+Tab` — `previous_tab`
- [x] `Ctrl+Tab` — `next_tab`
- [x] `Ctrl/Cmd+Enter` — `toggle_fullscreen`
- [x] `Ctrl/Cmd+Shift+Enter` — `toggle_split_zoom`
- [x] `Ctrl/Cmd+Shift+P` — `toggle_command_palette`
- [x] `Alt/Cmd+1`…`8` — `goto_tab:1`…`8`
- [x] `Alt/Cmd+9` — `last_tab`

## Linux / Windows

- [x] `Ctrl+Insert` — `copy_to_clipboard:mixed`
- [x] `Shift+Insert` — `paste_from_selection`（源码先绑定 `paste_from_clipboard`，后被同键覆盖）
- [x] `Ctrl+Shift+C` — `copy_to_clipboard:mixed`
- [x] `Ctrl+Shift+V` — `paste_from_clipboard`
- [x] `Ctrl+Shift+N` — `new_window`
- [x] `Alt+F4` — `close_window`（由系统关闭窗口处理）
- [x] `Ctrl+Shift+Q` — `quit`
- [x] `Ctrl+Shift+T` — `new_tab`
- [x] `Ctrl+Shift+W` — `close_tab:this`（Gui 关闭当前窗格；单窗格时即关标签）
- [x] `Ctrl+Shift+←` — `previous_tab`
- [x] `Ctrl+Shift+→` — `next_tab`
- [x] `Ctrl+PageUp` — `previous_tab`
- [x] `Ctrl+PageDown` — `next_tab`
- [x] `Ctrl+Shift+O` — `new_split:right`（Gui 同时保留 `Alt+Shift+Plus`）
- [x] `Ctrl+Shift+E` — `new_split:down`（Gui 同时保留 `Alt+Shift+-`）
- [x] `Ctrl+Super+[` — `goto_split:previous`
- [x] `Ctrl+Super+]` — `goto_split:next`
- [x] `Ctrl+Alt+←/→/↑/↓` — `goto_split:left/right/up/down`
- [x] `Super+Ctrl+Shift+←/→/↑/↓` — `resize_split` 对应方向 10
- [x] `Shift+Home` — `scroll_to_top`
- [x] `Shift+End` — `scroll_to_bottom`
- [x] `Shift+PageUp` — `scroll_page_up`
- [x] `Shift+PageDown` — `scroll_page_down`
- [x] `Ctrl+Shift+↑` — `jump_to_prompt:-1`
- [x] `Ctrl+Shift+↓` — `jump_to_prompt:1`
- [x] `Ctrl+Shift+PageUp` — `move_tab:-1`
- [x] `Ctrl+Shift+PageDown` — `move_tab:1`
- [x] `Ctrl+Shift+F` — `start_search`
- [x] `Esc` — `end_search`（查找框聚焦时由查找框处理）
- [ ] `Ctrl+Shift+I` — `inspector:toggle`（WinUI 无运行时 Inspector）
- [x] `Ctrl+Shift+A` — `select_all`

## macOS（仅参考，与 Windows 项目无关）

- [ ] `Cmd+C` / `Cmd+V` — 复制 / 粘贴
- [ ] `Cmd+Q` — `quit`
- [ ] `Cmd+K` — `clear_screen`
- [ ] `Cmd+A` — `select_all`
- [ ] `Cmd+Z` / `Cmd+Shift+T` — `undo`
- [ ] `Cmd+Shift+Z` — `redo`
- [ ] `Cmd+Home/End/PageUp/PageDown` — `scroll_to_top/bottom/page_up/page_down`
- [ ] `Cmd+J` — `scroll_to_selection`
- [ ] `Cmd+Shift+↑/↓` — `jump_to_prompt:-1/1`
- [ ] `Cmd+N` — `new_window`
- [ ] `Cmd+W` — `close_surface`
- [ ] `Cmd+Alt+W` — `close_tab:this`
- [ ] `Cmd+Shift+W` — `close_window`
- [ ] `Cmd+Shift+Alt+W` — `close_all_windows`
- [ ] `Cmd+T` — `new_tab`
- [ ] `Cmd+Shift+[` / `Cmd+Shift+]` — `previous_tab` / `next_tab`
- [ ] `Cmd+D` — `new_split:right`
- [ ] `Cmd+Shift+D` — `new_split:down`
- [ ] `Cmd+[` / `Cmd+]` — `goto_split:previous` / `next`
- [ ] `Cmd+Alt+←/→/↑/↓` — `goto_split:left/right/up/down`
- [ ] `Cmd+Ctrl+←/→/↑/↓` — `resize_split` 对应方向 10
- [ ] `Cmd+Ctrl+=` — `equalize_splits`
- [ ] `Cmd+↑/↓` — `jump_to_prompt:-1/1`
- [ ] `Cmd+F` — `start_search`
- [ ] `Cmd+E` — `search_selection`
- [ ] `Cmd+Shift+F` / `Esc` — `end_search`
- [ ] `Cmd+G` / `Cmd+Shift+G` — `navigate_search:next/previous`
- [ ] `Cmd+Alt+I` — `inspector:toggle`
- [ ] `Cmd+Ctrl+F` — `toggle_fullscreen`
- [ ] `Cmd+Shift+V` — `paste_from_selection`
- [ ] `Cmd+→` — `text:\x05`（行尾）
- [ ] `Cmd+←` — `text:\x01`（行首）
- [ ] `Cmd+Backspace` — `text:\x15`（删到行首）
- [ ] `Alt+→` — `esc:f`（按词前进）
- [ ] `Alt+←` — `esc:b`（按词后退）

## 注意

- 非 macOS 的 `Ctrl+Shift+W` 最终是 `close_tab:this`：源码先注册 `close_surface`，同键随后被覆盖。
- 非 macOS 的 `Shift+Insert` 最终是 `paste_from_selection`，不是粘贴系统剪贴板（`Ctrl+Insert` 才是复制）。
- `equalize_splits`、`search_selection`、`navigate_search` 只有 macOS 默认键；Windows/Linux 需自行绑定。
- `toggle_mouse_reporting`、`toggle_maximize`、`toggle_float_on_top`、`toggle_background_opacity`、`toggle_readonly`、非 macOS 的 `undo/redo` 没有默认键。
- `Ctrl+Shift+Super+J` 含 Win 键，部分系统/键盘会拦截该组合。
- `keybind =`（空值）恢复全部默认；`keybind = clear` 清空全部默认。
