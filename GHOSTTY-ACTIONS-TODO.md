# Ghostty 命令面板动作清单（TODO）

来源：<https://ghostty.org/docs/config/keybind/reference>（抓取于 2026-11-05，共 79 个动作）
说明：命令面板（`toggle_command_palette`）里列出的就是这些动作，每项同时可作为 `keybind` 的 action。除内置动作外，还可用配置项 `command-palette-entry` 追加自定义条目。
用法：完成一项把 `[ ]` 改成 `[x]`，需要时在行尾补实现位置。

## 1. 底层按键注入（7）

- [ ] `ignore` — 吞掉该按键，不传给终端也不传给子进程
- [ ] `unbind` — 解除此前绑定的按键
- [ ] `csi` — 发送 CSI 序列（参数不含 `ESC [`，如 `csi:0m`）
- [ ] `esc` — 发送 ESC 序列
- [ ] `text` — 发送指定文本（Zig 字符串字面量语法）
- [ ] `cursor_key` — 按光标键模式发送对应序列（application/normal）
- [ ] `reset` — 重置终端，等价于执行 `reset`

## 2. 剪贴板（5）

- [ ] `copy_to_clipboard` — 复制选中文本
- [ ] `paste_from_clipboard` — 粘贴默认剪贴板
- [ ] `paste_from_selection` — 粘贴选择剪贴板（X11 primary）
- [ ] `copy_url_to_clipboard` — 复制光标下的 URL
- [ ] `copy_title_to_clipboard` — 复制终端标题

## 3. 字号（4）

- [ ] `increase_font_size` — 增大字号（pt，如 `increase_font_size:1.5`）
- [ ] `decrease_font_size` — 减小字号（pt）
- [ ] `reset_font_size` — 恢复配置的原始字号
- [ ] `set_font_size` — 设为指定字号（pt，如 `set_font_size:14.5`）

## 4. 搜索（5）

- [ ] `search` — 用给定文本开始搜索，空串则取消
- [ ] `search_selection` — 用当前选中文本搜索
- [ ] `navigate_search` — 切换到下一个/上一个搜索结果
- [ ] `start_search` — 只打开搜索 UI，不设关键词
- [ ] `end_search` — 结束搜索并隐藏搜索 UI

## 5. 滚动与选择（12）

- [ ] `clear_screen` — 清屏并清空回滚缓冲
- [ ] `select_all` — 全选屏幕文本
- [ ] `scroll_to_top` — 滚动到顶部
- [ ] `scroll_to_bottom` — 滚动到底部
- [ ] `scroll_to_selection` — 滚动到选中文本位置
- [ ] `scroll_to_row` — 滚动到指定绝对行（0 为第一行）
- [ ] `scroll_page_up` — 上翻一页
- [ ] `scroll_page_down` — 下翻一页
- [ ] `scroll_page_fractional` — 按页比例滚动（正数向下，如 `:0.5`）
- [ ] `scroll_page_lines` — 按行数滚动（正数向下，如 `:3`）
- [ ] `adjust_selection` — 调整选区（left/right/up/down/page_up/page_down/home/end）
- [ ] `jump_to_prompt` — 按提示符个数跳转（需 shell 集成）

## 6. 导出到文件（3）

- [ ] `write_scrollback_file` — 回滚内容写入临时文件（copy/paste/open）
- [ ] `write_screen_file` — 屏幕内容写入临时文件（参数同上）
- [ ] `write_selection_file` — 选中文本写入临时文件（参数同上）

## 7. 窗口与标签（8）

- [ ] `new_window` — 新建窗口
- [ ] `new_tab` — 新建标签页
- [ ] `previous_tab` — 切到上一个标签页
- [ ] `next_tab` — 切到下一个标签页
- [ ] `last_tab` — 切到最后一个标签页
- [ ] `goto_tab` — 跳到指定序号标签页（从 1 起）
- [ ] `move_tab` — 按相对偏移移动标签页（越界循环）
- [ ] `toggle_tab_overview` — 标签页总览（仅 Linux，libadwaita ≥ 1.4）

## 8. 标题（4）

- [ ] `prompt_surface_title` — 弹窗修改当前 surface 标题
- [ ] `prompt_tab_title` — 弹窗修改当前标签标题（覆盖并持久）
- [ ] `set_surface_title` — 直接设置 surface 标题（空串清空）
- [ ] `set_tab_title` — 直接设置标签标题（空串清除覆盖）

## 9. 分屏（7）

- [ ] `new_split` — 新建分屏（right/down/left/up/auto）
- [ ] `goto_split` — 聚焦分屏（方向或 previous/next）
- [ ] `goto_window` — 聚焦上一个/下一个窗口
- [ ] `toggle_split_zoom` — 放大/还原当前分屏
- [ ] `toggle_readonly` — 切换只读模式（不向 PTY 发送输入）
- [ ] `resize_split` — 调整分屏尺寸（`方向,像素`，如 `up,10`）
- [ ] `equalize_splits` — 均分当前窗口的所有分屏

## 10. 调试与配置（6）

- [ ] `reset_window_size` — 恢复默认窗口尺寸（仅 macOS）
- [ ] `inspector` — 终端检查器开关（toggle/show/hide）
- [ ] `show_gtk_inspector` — 打开 GTK 检查器（仅 Linux）
- [ ] `show_on_screen_keyboard` — 显示屏幕键盘（仅 Linux GTK）
- [ ] `open_config` — 用系统默认编辑器打开配置文件
- [ ] `reload_config` — 重新加载配置

## 11. 关闭（4）

- [ ] `close_surface` — 关闭当前 surface（可能弹确认框）
- [ ] `close_tab` — 关闭标签页（this/other/right）
- [ ] `close_window` — 关闭当前窗口及其全部标签与分屏
- [ ] `close_all_windows` — 关闭所有窗口（已废弃，改用 `all:close_window`）

## 12. 窗口状态与全局开关（10）

- [ ] `toggle_maximize` — 最大化/还原（macOS 无此概念，无效果）
- [ ] `toggle_fullscreen` — 进入/退出全屏
- [ ] `toggle_window_decorations` — 切换窗口装饰（仅 Linux）
- [ ] `toggle_window_float_on_top` — 窗口置顶（仅 macOS）
- [ ] `toggle_secure_input` — 切换安全输入模式（仅 macOS）
- [ ] `toggle_mouse_reporting` — 开关鼠标事件上报
- [ ] `toggle_command_palette` — 开关命令面板
- [ ] `toggle_quick_terminal` — 开关快速终端（Quake 式下拉）
- [ ] `toggle_visibility` — 显示/隐藏全部窗口
- [ ] `toggle_background_opacity` — 透明/不透明切换（仅 macOS）

## 13. 其他（4）

- [ ] `check_for_updates` — 检查更新（仅 macOS）
- [ ] `undo` — 撤销上一个可撤销操作（如关闭标签/窗口）
- [ ] `redo` — 重做
- [ ] `end_key_sequence` — 结束当前按键序列并提交已按下的键
