# Ghostty 命令面板动作清单（TODO）

来源：Ghostty 源码 `src/input/command.zig`（`Command.defaults`，由 `actionCommands()` 在 comptime 展开），核对于 2026-11-09，共 **95 条**。

说明：命令面板（`toggle_command_palette`）列出的就是这些条目，标题即面板上显示的文案。带参数的动作在面板里会被拆成多条（`new_split` 4 条、`goto_split` 6 条、`copy_to_clipboard` 4 条、`write_screen_file` / `write_selection_file` 各 9 条）。只存在于 keybind、不出现在面板里的动作有 `set_font_size`、`search:<text>`、`scroll_to_row`、`resize_split`、`adjust_selection`、`jump_to_prompt`、`toggle_command_palette`、`toggle_quick_terminal`、`goto_tab` 等。

面板运行时还会追加：每个已打开 surface 的动态跳转条目（`collectJumpCommands`，条数随窗口数变化），以及配置项 `command-palette-entry` 追加的自定义条目（GTK 端会过滤掉平台不支持的动作，`command-palette-entry = clear` 清空包括默认在内的全部条目）。

> 2026-11-10：命令面板已按下表补齐（实现见 `Gui/Gui/Term/Workspace.Palette.cs`）。按需求不加入“外观”“窗口聚焦”“Toggle Tab Overview”和“彩蛋”。
> 暂未实现：`Toggle Inspector`、`Show the GTK Inspector`（WinUI 无运行时 Inspector）、`Toggle Window Decorations`、`Toggle Secure Input`（Windows 无对应能力）、`Check for Updates`（无更新器）、`Undo` / `Redo`（需要关闭历史/会话保活）。

用法：完成一项把 `[ ]` 改成 `[x]`，需要时在行尾补实现位置。

## 命令面板条目（95 条）

顺序与分组按源码，标题即面板上显示的文案。

顺序按源码分组，标题即面板上显示的文案。

### 剪贴板与终端（9）

- [x] `Reset Terminal` — `reset`
- [x] `Copy to Clipboard` — `copy_to_clipboard:mixed`
- [x] `Copy Selection as Plain Text to Clipboard` — `copy_to_clipboard:plain`
- [x] `Copy Selection as ANSI Sequences to Clipboard` — `copy_to_clipboard:vt`
- [x] `Copy Selection as HTML to Clipboard` — `copy_to_clipboard:html`
- [x] `Copy URL to Clipboard` — `copy_url_to_clipboard`
- [x] `Copy Terminal Title to Clipboard` — `copy_title_to_clipboard`
- [x] `Paste from Clipboard` — `paste_from_clipboard`
- [x] `Paste from Selection` — `paste_from_selection`

### 搜索（5）

- [x] `Start Search` — `start_search`
- [x] `Search Selection` — `search_selection`
- [x] `End Search` — `end_search`
- [x] `Next Search Result` — `navigate_search:next`
- [x] `Previous Search Result` — `navigate_search:previous`

### 字号（3）

- [x] `Increase Font Size` — `increase_font_size:1`
- [x] `Decrease Font Size` — `decrease_font_size:1`
- [x] `Reset Font Size` — `reset_font_size`

### 屏幕与滚动（7）

- [x] `Clear Screen` — `clear_screen`
- [x] `Select All` — `select_all`
- [x] `Scroll to Top` — `scroll_to_top`
- [x] `Scroll to Bottom` — `scroll_to_bottom`
- [x] `Scroll to Selection` — `scroll_to_selection`
- [x] `Scroll Page Up` — `scroll_page_up`
- [x] `Scroll Page Down` — `scroll_page_down`

### 导出到临时文件（18）

屏幕内容 → `write_screen_file`，选中文本 → `write_selection_file`；每种 3 个动作 × 3 种格式。

- [x] `Copy Screen to Temporary File and Copy Path` — `write_screen_file:copy`
- [x] `Copy Screen to Temporary File and Paste Path` — `write_screen_file:paste`
- [x] `Copy Screen to Temporary File and Open` — `write_screen_file:open`
- [x] `Copy Screen as HTML to Temporary File and Copy Path` — `write_screen_file:{action:copy,emit:html}`
- [x] `Copy Screen as HTML to Temporary File and Paste Path` — `write_screen_file:{action:paste,emit:html}`
- [x] `Copy Screen as HTML to Temporary File and Open` — `write_screen_file:{action:open,emit:html}`
- [x] `Copy Screen as ANSI Sequences to Temporary File and Copy Path` — `write_screen_file:{action:copy,emit:vt}`
- [x] `Copy Screen as ANSI Sequences to Temporary File and Paste Path` — `write_screen_file:{action:paste,emit:vt}`
- [x] `Copy Screen as ANSI Sequences to Temporary File and Open` — `write_screen_file:{action:open,emit:vt}`
- [x] `Copy Selection to Temporary File and Copy Path` — `write_selection_file:copy`
- [x] `Copy Selection to Temporary File and Paste Path` — `write_selection_file:paste`
- [x] `Copy Selection to Temporary File and Open` — `write_selection_file:open`
- [x] `Copy Selection as HTML to Temporary File and Copy Path` — `write_selection_file:{action:copy,emit:html}`
- [x] `Copy Selection as HTML to Temporary File and Paste Path` — `write_selection_file:{action:paste,emit:html}`
- [x] `Copy Selection as HTML to Temporary File and Open` — `write_selection_file:{action:open,emit:html}`
- [x] `Copy Selection as ANSI Sequences to Temporary File and Copy Path` — `write_selection_file:{action:copy,emit:vt}`
- [x] `Copy Selection as ANSI Sequences to Temporary File and Paste Path` — `write_selection_file:{action:paste,emit:vt}`
- [x] `Copy Selection as ANSI Sequences to Temporary File and Open` — `write_selection_file:{action:open,emit:vt}`

### 窗口与标签（6）

- [x] `New Window` — `new_window`
- [x] `New Tab` — `new_tab`
- [x] `Move Tab Left` — `move_tab:-1`
- [x] `Move Tab Right` — `move_tab:1`
- [x] `Move Tab to New Window` — `move_tab_to_new_window`
- [ ] `Toggle Tab Overview` — `toggle_tab_overview`（按需求不加入）

### 标题（3）

- [x] `Change Terminal Title…` — `prompt_surface_title`
- [x] `Change Tab Title…` — `prompt_tab_title`
- [x] `Change Window Title…` — `prompt_window_title`

### 分屏（13）

- [x] `Split Left` — `new_split:left`
- [x] `Split Right` — `new_split:right`
- [x] `Split Up` — `new_split:up`
- [x] `Split Down` — `new_split:down`
- [x] `Focus Split: Previous` — `goto_split:previous`
- [x] `Focus Split: Next` — `goto_split:next`
- [x] `Focus Split: Left` — `goto_split:left`
- [x] `Focus Split: Right` — `goto_split:right`
- [x] `Focus Split: Up` — `goto_split:up`
- [x] `Focus Split: Down` — `goto_split:down`
- [x] `Toggle Split Zoom` — `toggle_split_zoom`
- [x] `Toggle Read-Only Mode` — `toggle_readonly`
- [x] `Equalize Splits` — `equalize_splits`

### 窗口焦点（2）

- [ ] `Focus Window: Previous` — `goto_window:previous`（按需求不加入）
- [ ] `Focus Window: Next` — `goto_window:next`（按需求不加入）

### 调试与配置（7）

- [x] `Reset Window Size` — `reset_window_size`
- [ ] `Toggle Inspector` — `inspector:toggle`
- [ ] `Show the GTK Inspector` — `show_gtk_inspector`
- [x] `Show On-Screen Keyboard` — `show_on_screen_keyboard`
- [x] `Open Config Using OS editor` — `open_config:os_open`
- [x] `Open Config in New Terminal Window` — `open_config:new_window`
- [x] `Reload Config` — `reload_config`

### 外观（4）

- [ ] `Switch to Configured Appearance` — `set_window_theme:config`（按需求不加入）
- [ ] `Switch to System Appearance` — `set_window_theme:system`（按需求不加入）
- [ ] `Switch to Light Appearance` — `set_window_theme:light`（按需求不加入）
- [ ] `Switch to Dark Appearance` — `set_window_theme:dark`（按需求不加入）

### 关闭（6）

- [x] `Close Terminal` — `close_surface`
- [x] `Close Tab` — `close_tab:this`
- [x] `Close Other Tabs` — `close_tab:other`
- [x] `Close Tabs to the Right` — `close_tab:right`
- [x] `Close Window` — `close_window`
- [x] `Close All Windows` — `close_all_windows`

### 窗口状态（7）

- [x] `Toggle Maximize` — `toggle_maximize`
- [x] `Toggle Fullscreen` — `toggle_fullscreen`
- [ ] `Toggle Window Decorations` — `toggle_window_decorations`
- [x] `Toggle Float on Top` — `toggle_window_float_on_top`
- [ ] `Toggle Secure Input` — `toggle_secure_input`
- [x] `Toggle Mouse Reporting` — `toggle_mouse_reporting`
- [x] `Toggle Background Opacity` — `toggle_background_opacity`

### 其他（4）

- [ ] `Check for Updates` — `check_for_updates`
- [ ] `Undo` — `undo`
- [ ] `Redo` — `redo`
- [x] `Quit` — `quit`

### 彩蛋（1）

- [ ] `Ghostty` — `text:"👻"`（按需求不加入）
