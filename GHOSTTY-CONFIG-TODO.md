# Ghostty 配置项清单（TODO）

来源：<https://ghostty.org/docs/config/reference> 与 `D:\dev\ghostty` 的 1.4.1-dev 源码
（`src/config/Config.zig`，共 213 个配置键；比文档多出 1.4 新增项）
说明：按功能分组，一项对应一个 Ghostty 配置键；标注「仅 macOS」「仅 Linux/GTK」的项与 Windows 项目无关，可直接跳过。
状态：`[x]` 已在本分支接入并生效；`[ ]` 已能正常解析（不再报未知选项），但本构建尚无对应实现，或该选项只对 macOS / Linux 有效。
默认值已按 1.4 源码对齐（`font-size` 12、`faint-opacity` 0.5、`background-blur` 关闭、
`right-click-action` context-menu、`clipboard-trim-trailing-spaces` 开启、`window-padding` 2pt）。

## 1. 应用与语言（1）

- [x] `language` — 界面语言：留空（默认）跟随系统语言（系统语言不受支持时回退英文）；可设为 `zh-CN`（中文）或 `en`（英文），其他值告警后回退英文；重新加载配置后即时生效

## 2. 字体家族与样式（21）

- [x] `font-family` — 主字体族，可重复配置追加回退字体（首项为主字体，缺字时按后续项逐字形回退）
- [x] `font-family-bold` — 粗体字体族
- [x] `font-family-italic` — 斜体字体族
- [x] `font-family-bold-italic` — 粗斜体字体族
- [x] `font-style` — 按字体自带的样式名选择常规字形
- [x] `font-style-bold` — 粗体样式名
- [x] `font-style-italic` — 斜体样式名
- [x] `font-style-bold-italic` — 粗斜体样式名
- [x] `font-synthetic-style` — 是否合成缺失的粗体/斜体（no-bold / no-italic / no-bold-italic 关闭；Windows 下通过检测真实字面近似实现）
- [x] `font-feature` — 开启或关闭 OpenType 特性（如 `-calt` 关连字）
- [x] `font-size` — 字号（pt，可为小数）；默认 12（与 1.4 的非 macOS 默认一致）
- [x] `font-variation` — 可变字体轴值（如 `wght=600`；Windows 下 wght / wdth / ital / slnt 生效）
- [x] `font-variation-bold` — 粗体使用的可变字体轴值（未设置时继承 `font-variation`）
- [x] `font-variation-italic` — 斜体使用的可变字体轴值（未设置时继承 `font-variation`）
- [x] `font-variation-bold-italic` — 粗斜体使用的可变字体轴值（未设置时继承 `font-variation`）
- [x] `font-codepoint-map` — 把指定码点/区间强制映射到某字体
- [x] `clipboard-codepoint-map` — 复制时把指定码点替换成其他字符
- [ ] `font-thicken` — 加粗描边绘制字体（仅 macOS）
- [ ] `font-thicken-strength` — 描边加粗强度 0–255（仅 macOS）
- [x] `font-shaping-break` — 在光标等位置断开 shaping run（影响连字）
- [ ] `alpha-blending` — 文本 alpha 混合使用的色彩空间（native/linear/linear-corrected）

## 3. 字形度量微调（15）

- [x] `adjust-cell-width` — 单元格宽度微调（像素或百分比）
- [x] `adjust-cell-height` — 单元格高度微调（文字垂直居中）
- [x] `adjust-font-baseline` — 基线位置微调
- [x] `adjust-underline-position` — 下划线位置微调
- [x] `adjust-underline-thickness` — 下划线粗细微调
- [x] `adjust-strikethrough-position` — 删除线位置微调
- [x] `adjust-strikethrough-thickness` — 删除线粗细微调
- [x] `adjust-overline-position` — 上划线位置微调
- [x] `adjust-overline-thickness` — 上划线粗细微调
- [x] `adjust-cursor-thickness` — 光标粗细微调
- [x] `adjust-cursor-height` — 光标高度微调
- [ ] `adjust-box-thickness` — 制表符线框粗细微调
- [x] `adjust-icon-height` — Nerd Font 图标最大高度微调
- [ ] `grapheme-width-method` — 字素簇占列数的计算方式（unicode/legacy）
- [ ] `freetype-load-flags` — FreeType 加载标志（仅 Linux）

## 4. 主题与配色（25）

- [x] `theme` — 使用内置或自定义主题（名称/文件路径）
- [x] `background` — 窗口背景色
- [x] `foreground` — 窗口前景色
- [x] `background-image` — 背景图片路径（PNG/JPEG；相对路径按配置文件所在目录解析）
- [x] `background-image-opacity` — 背景图不透明度（相对 background-opacity）
- [x] `background-image-position` — 背景图位置（九宫格取值）
- [x] `background-image-fit` — 背景图适配方式（contain/cover/stretch/none）
- [x] `background-image-repeat` — 背景图是否平铺
- [x] `selection-foreground` — 选中文本前景色
- [x] `selection-background` — 选中文本背景色
- [x] `selection-clear-on-typing` — 输入时清除选区（默认 true）
- [x] `selection-clear-on-copy` — 复制后清除选区（默认 false）
- [x] `selection-word-chars` — 双击选词的分词边界字符（默认 `\t'"│\`|:;,()[]{}<>$`）
- [ ] `minimum-contrast` — 前景/背景最小对比度（1–21）
- [x] `palette` — 自定义 256 色板（`N=COLOR`）
- [x] `palette-generate` — 由基础 16 色自动生成 16–255 色
- [x] `palette-harmonious` — 反转自动生成色板的顺序
- [x] `bold-color` — 覆盖粗体文字颜色
- [x] `faint-opacity` — 暗色（faint）文字不透明度；默认 0.5（与 1.4 一致）
- [x] `cursor-color` — 光标颜色
- [x] `cursor-opacity` — 光标不透明度
- [x] `cursor-style` — 默认光标形状（block/bar/underline 等）
- [x] `cursor-style-blink` — 光标默认是否闪烁
- [x] `cursor-text` — 光标下文字颜色
- [ ] `cursor-click-to-move` — 点击提示符移动光标（依赖 shell 集成）

## 5. 背景、分屏与搜索（12）

- [x] `background-opacity` — 整个窗口的不透明度（含标题栏/侧栏/窗格）；低于 1 时由一层带色调的窗口背景提供透明，窗格本身保持透明，避免和系统亚克力的色调叠加两次
- [x] `background-opacity-cells` — 显式设置背景色的单元格也套用透明度
- [x] `background-blur` — true 使用 DWM 亚克力（模糊）；false 在 Windows 11 使用普通半透明画刷（不模糊，失败时回退亚克力），Windows 10 没有无模糊透明，和 Windows Terminal 一样仍走亚克力；默认关闭，与 1.4 一致
- [x] `unfocused-split-opacity` — 未聚焦分屏的不透明度
- [x] `unfocused-split-fill` — 未聚焦分屏的叠加色
- [x] `split-divider-color` — 分屏分隔线颜色；作用于 compact 模式下 1 DIP 的分隔线
- [ ] `split-preserve-zoom` — 何时保留分屏缩放状态
- [x] `drag-handle` — 分屏拖拽开关：`auto`（默认，标签页内有两个及以上分屏时可拖）、`always`（始终可拖）、`never`（禁用拖动）；把手是标题栏里的 shell 图标 + 工作目录文字（宽度随文字变化，悬停显示移动光标），拖到另一个分屏的左/右/上/下侧即可重排
- [x] `search-foreground` — 搜索命中项前景色；`Term/Config.cs` 解析（支持颜色、`cell-foreground`、`cell-background`），`Term/TerminalRenderer.cs` 逐单元格应用
- [x] `search-background` — 搜索命中项背景色；默认 `#FFE082`（Ghostty 的金色）
- [x] `search-selected-foreground` — 当前搜索命中项前景色；默认黑色
- [x] `search-selected-background` — 当前搜索命中项背景色；默认 `#F2A57E`（Ghostty 的柔桃色）

## 6. 进程与启动（13）

- [x] `command` — 启动的命令/Shell
- [x] `initial-command` — 仅首个终端使用的启动命令；后续标签页仍用 `command`
- [x] `notify-on-command-finish` — 命令结束时是否通知（never/unfocused/always，本应用默认 `unfocused`，Ghostty 默认 never）；依赖 shell 集成上报的 OSC 133 D，`unfocused` 只在窗格未聚焦或窗口不在前台时通知
- [x] `notify-on-command-finish-action` — 通知方式（bell/notify，可用 `no-` 关闭，本应用默认 `bell,notify`，Ghostty 默认只有 bell）；bell 走 `bell-features`，notify 走 Windows 应用通知（失败回退任务栏闪烁）
- [x] `notify-on-command-finish-after` — 命令运行超过多久才触发通知；支持 `1h30m`、`45s`、`500ms` 这类时长，默认 5s
- [x] `env` — 追加环境变量（`KEY=VALUE`，可重复）
- [x] `input` — 启动时向子进程写入的输入数据
- [x] `wait-after-command` — 命令退出后保持终端不关闭
- [ ] `abnormal-command-exit-runtime` — 判定异常退出所需的最短运行毫秒数
- [x] `scrollback-limit-bytes` — 回滚缓冲区字节上限（含活动屏）；`Term/Config.cs`，`Term/TerminalSession.cs` 经 `OPT_SCROLLBACK_MAX_BYTES` 下发，构造与 `Ctrl+Shift+,` 重载时各应用一次。按 Ghostty 1.4 对齐：默认 `50000000`（50 MB），取值只接受整数（base 0，`0x` 可用）或 `unlimited`（内部存 `ulong.MaxValue`），不接受 `10MB` 这类后缀；`0` 关闭回滚。1.4 的新键名，旧名 `scrollback-limit` 作为别名仍可用
- [x] `scrollback-limit-lines` — 回滚行数上限（1.4 新增）；`Term/Config.cs` + `Term/TerminalSession.cs` 经 `OPT_SCROLLBACK_MAX_LINES` 下发。语法同 `scrollback-limit-bytes`：整数（base 0）或 `unlimited`，默认 `unlimited`；字节上限与行数上限同时生效，谁先到谁裁
- [x] `scrollback-compression` — 空闲时压缩回滚页（1.4 新增）；本构建仅识别该键，压缩由 libghostty-vt 决定
- [x] `scrollbar` — 滚动条显示策略；`Term/Config.cs` 归一化取值，`Term/TerminalPane.cs` 的 `UpdateScrollbar()` 决定可见性。**只实现 system / never**：`system` = 有回滚内容才显示（1.4 的取值也只有 system / never，没有 `always`）

## 7. 链接、鼠标与选择（13）

- [ ] `link` — 正则匹配终端文本并绑定点击动作（Ghostty 1.4 自身仍标记 TODO，无实际语义）
- [x] `link-url` — 是否识别 URL 并可点击打开；`Term/Config.cs`，`Term/TerminalPane.cs` 的 `UpdateLinkHover()` 用 `UrlPattern`（`http(s)://` 与 `www.`）识别鼠标下的单元格，Ctrl+点击经 `OpenLink()` 打开
- [x] `link-osc8` — 是否识别 OSC 8 超链接；`Term/TerminalSession.cs` 的 `HyperlinkAt()` 走 `ghostty_terminal_grid_ref` + `ghostty_grid_ref_hyperlink_uri`，优先于 URL 正则，Ctrl+点击同样打开
- [x] `link-previews` — 悬停链接时显示 URL 徽标；`Term/TerminalPane.cs` 的 `UpdateLinkPreview()`，`true` 显示所有链接、`osc8` 仅 OSC 8、`false` 关闭
- [x] `mouse-hide-while-typing` — 打字时隐藏鼠标指针；`Term/TerminalPane.cs` 的 `HidePointer()`/`ShowPointer()` 经 `ShowCursor(bool)` 下发，键盘/文本输入时隐藏、指针移动时恢复
- [x] `scroll-to-bottom` — 何时自动滚动到底部；`Term/Config.cs` 拆成 keystroke/output 两个布尔（`no-keystroke`/`no-output`），`Term/TerminalPane.cs` 在按键/文本输入与 `_session.OnOutput` 时调用 `ScrollToBottom()`
- [x] `mouse-shift-capture` — Shift+点击是否交给运行中的程序识别；`Term/Config.cs` 归一化为 false/true/always/never，`Term/TerminalPane.cs` 的 `TryReportMouse()` 控制 Shift 是否放行
- [x] `mouse-reporting` — 是否向程序上报鼠标事件；`Term/TerminalPane.cs` 的 `TryReportMouse()` 组合按键/移动/滚轮，`Term/TerminalSession.cs` 的 `EncodeMouse()` 经 `ghostty_mouse_encoder_setopt_from_terminal` + `ghostty_mouse_encoder_encode` 生成 SGR 序列（按单元格坐标）
- [x] `mouse-scroll-multiplier` — 滚轮滚动倍数（可加 precision:/discrete:）；Windows 使用 discrete，默认 3 行/格
- [x] `copy-on-select` — 选中即复制（1.4：none/primary/clipboard/both；Windows 无选区剪贴板，both 走系统剪贴板）
- [x] `right-click-action` — 右键行为（context-menu/paste/copy/copy-or-paste/ignore）；默认 `context-menu` 会在指针位置弹出 Copy/Paste 菜单（无选区时 Copy 禁用），同时屏蔽 IME 输入框自带的文本框菜单，避免“弹菜单 + 立即粘贴”同时发生
- [x] `middle-click-action` — 中键行为（primary-paste / clipboard-paste / ignore）；Windows 没有主选区，两个粘贴值都走系统剪贴板
- [x] `click-repeat-interval` — 判定连击的间隔毫秒数；`Term/TerminalPane.cs` 的 `OnPointerPressed()` 用 `Environment.TickCount64` 手动判定双击/三击（双击选词、三击选行），0 表示 Ghostty 在非 macOS 的 500ms 默认值

## 8. 窗口与布局（36）

- [x] `maximize` — 新窗口启动即最大化
- [x] `fullscreen` — 新窗口启动即全屏；non-native* 在 Windows 上等同 true
- [x] `title` — 固定窗口标题（不再跟随终端标题）
- [ ] `class` — 应用窗口类名（X11 WM_CLASS）
- [ ] `x11-instance-name` — X11 WM_CLASS 实例名（仅 X11）
- [x] `working-directory` — 启动后切换到的目录
- [x] `keybind` — 键绑定（`trigger=action`，支持 `clear` 与 `trigger=` 解绑；见 `Gui/Gui/Term/Keybinds.cs`）
- [ ] `key-remap` — 应用层修饰键重映射
- [x] `window-padding-x` — 水平内边距；**单位 pt**（与 Ghostty 一致），单个值或 `left,right`；`Term/Config.cs` 的 `PaddingDips` 按 `floor(pt × dpi / 72)` 折算成物理像素，再除以缩放比例得到布局用的 DIP
- [x] `window-padding-y` — 垂直内边距；**单位 pt**，单个值或 `top,bottom`，换算方式同上
- [x] `window-padding-balance` — 余量像素在两侧均衡分配（默认关闭，与 Ghostty 一致）；`Term/TerminalPane.cs` 的 `BalancePadding()` 在扣除整格后把余量平分到左右/上下，`SizeChanged` 后重新布局。列数改用字体的精确 advance 而不是向上取整的单元格宽度，右侧余量本身已小于一格
- [ ] `window-padding-color` — 内边距区域颜色；`background`（默认）与当前绘制一致；`extend`/`extend-always` 需要把边缘单元格颜色画进内边距，渲染器暂未实现
- [ ] `window-vsync` — 垂直同步；Win2D 的 `CanvasControl` 走合成器同步，无法关闭，等同于始终开启
- [x] `window-inherit-working-directory` — 新建窗口（`Ctrl+Shift+N` 或命令面板的 “New window”）以当前窗格的工作目录启动；关闭时用默认目录
- [x] `tab-inherit-working-directory` — 新标签页继承当前 shell 的工作目录（默认开启）
- [x] `split-inherit-working-directory` — 新分屏继承当前 shell 的工作目录（默认开启）
- [x] `window-inherit-font-size` — 新标签页继承当前窗格的字号（含缩放，默认开启）
- [ ] `window-decoration` — 窗口装饰（标题栏/边框）偏好；本构建始终使用无原生标题栏的自绘窗口，true/false/auto 无差异
- [x] `window-title-font-family` — 窗口与标签标题字体；`Term/Workspace.cs` 的 `TitleFontFamily()`/`ApplyTitleFont()` 作用于标签标题与提示，重载配置即时生效
- [ ] `window-subtitle` — 窗口副标题内容
- [x] `window-theme` — 窗口主题（auto/light/dark/ghostty）
- [ ] `window-colorspace` — 解释终端颜色所用的色彩空间
- [x] `window-height` — 初始窗口高度（单元格）；与 `window-width` 成对生效，`MainWindow.ApplyInitialGeometry()` 按当前网格差值调整窗口大小
- [x] `window-width` — 初始窗口宽度（单元格）；同上，10×4 以下的取值被忽略
- [x] `window-position-x` — 初始窗口 X 位置（像素）；与 `window-position-y` 成对生效，见 `MainWindow.ApplyInitialGeometry()`
- [x] `window-position-y` — 初始窗口 Y 位置（像素）；同上
- [x] `window-save-state` — 保存并恢复窗口状态；Windows 上只有 `always` / `never`（默认 `always`，`default` 及其他取值按 `always`）。关闭窗口时把窗口位置/大小/DPI/最大化/全屏、标签页、分屏树与比例、焦点窗格、zoom 以及每个窗格的工作目录、命令、标题写入 `%APPDATA%\gui\window-state.json`；每个窗格的终端快照（libghostty-vt snapshot：屏幕与回滚缓冲）单独写到 `%APPDATA%\gui\session\*.snap`，JSON 与快照都用临时文件原子替换，未引用的旧快照在保存后清理。恢复时按原样重建窗口/标签/分屏，再把快照转成 VT 文本写回新终端：先按真实网格尺寸调整，再写旧输出、`── restored · session ended <时间> ──` 分隔线，然后用 `CreatePseudoConsole(..., PSEUDOCONSOLE_INHERIT_CURSOR)` 启动 shell——conhost 会以 DSR 询问光标位置，终端按历史之后的光标回答，新提示符因此画在分隔线下方，PSReadLine 的绝对定位也保持正确（与 Kero 的 kerod 同一做法）；之前写下的分隔线行会在写入前剔除，避免层层嵌套。后台标签页的会话在重开时不会丢失（未启动的窗格沿用其快照文件）。运行中的程序无法恢复，目录失效时回退 `working-directory`
- [ ] `window-step-resize` — 按单元格尺寸步进调整窗口
- [x] `window-new-tab-position` — 新标签页插入位置（current 紧跟当前页 / end 末尾）
- [x] `window-show-tab-bar` — never 隐藏标签条；always / auto 保持现有显示（标题栏仍保留拖拽区）
- [ ] `window-titlebar-background` — 标题栏背景色（window-theme=ghostty）
- [ ] `window-titlebar-foreground` — 标题栏前景色（window-theme=ghostty）
- [ ] `resize-overlay` — 调整大小时浮层的显示策略
- [ ] `resize-overlay-position` — 浮层位置
- [ ] `resize-overlay-duration` — 浮层显示时长
- [x] `focus-follows-mouse` — 鼠标移动即切换聚焦分屏

## 9. 剪贴板（6）

- [x] `clipboard-read` — `allow` / `deny` / `ask`（默认 ask）；libghostty-vt 的 OSC 52/kitty 读取回调接到 `TerminalSession`，只提供文本 MIME；本构建没有阻塞确认框，`ask` 按 `deny` 处理
- [x] `clipboard-write` — `allow` / `deny` / `ask`（默认 allow）；OSC 52 与 kitty 的文本写入落到 Win32 剪贴板（`CF_UNICODETEXT`），空写入清空剪贴板，非文本 MIME 返回 unsupported，`ask` 同样按 `deny`
- [x] `clipboard-trim-trailing-spaces` — 复制时去掉行尾空白；默认开启（与 1.4 一致）
- [x] `clipboard-paste-protection` — 粘贴可疑内容前二次确认；`Term/TerminalPane.cs` 的 `ConfirmPasteAsync()` 在内容含换行或控制字符且非括号粘贴时弹出确认框
- [x] `clipboard-paste-bracketed-safe` — 括号粘贴视为安全；开启时（默认）程序已启用 bracketed paste 就不再确认
- [x] `clipboard-write-limit-bytes` — OSC 52/kitty 写入的字节上限（1.4，默认 64 MiB，`unlimited` 解除）；下发到 `OPT_CLIPBOARD_WRITE_MAX_BYTES`，由原生层在回调前限流

## 10. 配置加载与生命周期（20）

- [x] `config-file` — 追加读取的配置文件（可重复）；`Term/Config.cs` 的 `LoadFile()` 按 Ghostty 语义在被包含文件之后加载（后者覆盖前者），路径相对声明它的文件，`?` 前缀表示缺失可忽略，循环引用只记警告
- [ ] `config-default-files` — 是否加载默认路径的配置文件；Ghostty 本身仅限 CLI 参数，写入配置文件无效果
- [x] `confirm-close-surface` — 关闭窗口前是否需要确认（默认 true）；`true` 只在 shell 集成报告仍有命令运行时询问，`false` 从不询问，`always` 每次关闭都询问；确认框为应用内的 ContentDialog
- [ ] `quit-after-last-window-closed` — 最后一个窗口关闭后是否退出；本构建为单窗口应用，关闭窗口即退出
- [ ] `quit-after-last-window-closed-delay` — 退出前的延迟时长
- [ ] `initial-window` — 启动时是否创建初始窗口
- [ ] `undo-timeout` — 撤销操作的有效期
- [x] `title-report` — 是否允许程序用 CSI 21 t 查询标题（默认 `false`；开启后核心以 `ESC ] l <title> ESC \` 回复，见 `TerminalSession.SetTitleReport`）
- [ ] `image-storage-limit` — 单屏图像数据（Kitty 协议）字节上限
- [x] `term` — TERM 环境变量值
- [x] `enquiry-response` — 收到 ENQ(0x05) 时回复的字符串（默认空，即不回复；见 `TerminalSession.SetEnquiryResponse`）
- [x] `vt-kam-allowed` — 是否允许 KAM 模式禁用键盘输入；默认 `false`（与 Ghostty 一致）。开启后 `Term/TerminalPane.cs` 的 `KeyDown()` / `CommitText()` 在写入 pty 前查询 ANSI 模式 2（`Term/TerminalSession.cs` 的 `KeyboardActionMode()` 经 `TDATA_MODE` 读取 `0x8002`），KAM 打开时丢弃按键与 IME 文本；键绑定已在工作区层先行处理，不受影响
- [x] `vt-window-resize-allowed` — 是否允许程序通过 CSI 8 t 调整窗口大小；默认 `false`（与 Ghostty 一致）。`TerminalSession` 接 `OPT_RESIZE_WINDOW` 回调（`Term/TerminalSession.cs` 的 `OnResizeWindowReport()` 解析 `GhosttyTerminalResizeWindow`），`Term/TerminalPane.cs` 按配置过滤并延迟到 UI 队列，`Term/Workspace.cs` 的 `ResizeWindowFromProgram()` 按请求窗格的单元格尺寸用差值调整窗口；0 表示保持该维度，最小 40×10 格（与 Ghostty 一致）
- [x] `vt-xt-checksum-extension` — 重置后 DECRQCRA 校验和的算法（xterm 的 CSBITS）；默认 `negate,attributes,trim`（位掩码 0，即 DEC 算法），可用 `no-negate` / `no-attributes` / `no-trim` / `undrawn` / `full` 组合，后出现的项覆盖前面的。`Term/Config.cs` 的 `ParseXtChecksumExtension()` 把名称换算成位掩码，`Term/TerminalSession.cs` 的 `SetXtChecksumExtension()` 经 `OPT_XT_CHECKSUM_EXTENSION` 下发；运行中程序的 XTCHECKSUM（`CSI Ps # y`）仍可在下次重置前覆盖。只有开启 `vt-xt-checksum-report` 后，DECRQCRA 才会真正返回校验和
- [x] `vt-xt-checksum-report` — 是否回报 XT 校验和；默认 `false`（与 Ghostty 一致，避免程序逐格读取屏幕内容）。开启后 DECRQCRA（`CSI Pi ; Pg ; Pt ; Pl ; Pb ; Pr * y`）以 `DCS Pi ! ~ XXXX ST` 回复，算法由 `vt-xt-checksum-extension` 决定；关闭时 DECRQCRA 与 XTCHECKSUM（`CSI Ps # y`）都被忽略。见 `Term/TerminalSession.cs` 的 `SetXtChecksumReport()`（`OPT_XT_CHECKSUM_REPORT`），配置重载时经 `ApplyProgramQueries()` 重新下发
- [x] `osc-color-report-format` — OSC 颜色查询的回报格式（`none` / `8-bit` / `16-bit`，默认 `16-bit`；核心只会发 16 位，另外两种在写回 pty 前改写，见 `Term/ColorReport.cs`）
- [ ] `async-backend` — 异步 IO 后端（auto/epoll/io_uring 等）；仅 Linux（macOS 固定 kqueue），Windows 无对应实现，跳过
- [ ] `app-notifications` — 应用内通知（toast）的控制项
- [x] `desktop-notifications` — 是否允许程序通过 OSC 9 / OSC 777 发起桌面通知；默认 `true`（与 Ghostty 一致）。`TerminalSession` 接 `OPT_DESKTOP_NOTIFICATION` 回调（`OnDesktopNotificationReport()` 解析 `GhosttyTerminalDesktopNotification`），`Term/TerminalPane.cs` 按配置过滤，`Term/Workspace.Window.cs` 的 `OnPaneDesktopNotification()` 用 Windows 应用通知显示：OSC 9 无标题时回退到窗格标题，空正文只显示标题；注册复用命令完成通知的 `AppNotificationManager`，失败时闪烁任务栏
- [x] `progress-style` — 是否支持 OSC 9;4 图形进度条；默认开启。解析 `ESC ] 9 ; 4 ; state ; value ST`（0 清除 / 1 进行中 / 2 失败 / 3 不确定 / 4 暂停，百分比夹到 0–100），像 Ghostty 一样在终端内容顶部画一条 2 DIP 的细条（普通态用主题强调色，失败/暂停用控件的红/黄色，不确定态脉冲）；命令结束（OSC 133 D）或新提示符（OSC 133 A）时自动清除遗留的进度条

## 11. 快速终端（9）

- [ ] `quick-terminal-position` — 呼出位置（top/bottom/left/right/center）
- [ ] `quick-terminal-size` — 尺寸（屏幕百分比或像素）
- [ ] `quick-terminal-screen` — 显示在哪个屏幕
- [ ] `quick-terminal-animation-duration` — 进出动画时长（0 关闭）
- [ ] `quick-terminal-autohide` — 失焦时自动隐藏
- [ ] `quick-terminal-space-behavior` — macOS 切换 Space 时的行为（仅 macOS）
- [ ] `quick-terminal-keyboard-interactivity` — 何时接管键盘输入（仅 Wayland）
- [ ] `gtk-quick-terminal-layer` — 快速终端所在图层（仅 GTK）
- [ ] `gtk-quick-terminal-namespace` — 快速终端的 Wayland 命名空间（仅 GTK）

## 12. Shell 集成与命令面板（3）

- [x] `shell-integration` — 是否自动注入 shell 集成（none/detect/powershell，默认 detect）；仅对裸 PowerShell 命令注入，脚本在提示符前后发 OSC 133 A/B/C/D 与 OSC 7；Unix shell 名会被识别但仅记 Notice
- [x] `shell-integration-features` — 启用哪些 shell 集成特性（逗号分隔，可用 `no-` 关闭，`true`/`false` 全开/全关）；Windows 生效的是 `cursor`（提示符处竖线光标，支持 `cursor:steady`）与 `title`（用工作目录设置标题），`sudo`/`ssh-*`/`path` 只解析不生效
- [ ] `command-palette-entry` — 自定义命令面板条目

## 13. 自定义着色器（2）

- [ ] `custom-shader` — 在默认着色器之后追加 GLSL 着色器
- [ ] `custom-shader-animation` — 是否为客户着色器运行动画循环

## 14. 提示音与通知（3）

- [x] `bell-features` — 铃音特性；`Term/TerminalSession.cs` 接 `OPT_BELL`，`Term/TerminalPane.cs` 实现 system（`MessageBeep`）、audio、attention（任务栏闪烁）、title（标题前加 🔔）、border（窗格边框高亮）
- [x] `bell-audio-path` — 提示音音频文件路径；解析时相对配置文件目录展开，`bell-features` 含 `audio` 时经 `MediaPlayer` 播放
- [x] `bell-audio-volume` — 提示音音量（相对系统音量，默认 0.5）；传给 `MediaPlayer.Volume`

## 15. macOS 专属（19）

- [ ] `macos-non-native-fullscreen` — 使用非原生全屏
- [ ] `macos-window-buttons` — 是否显示标题栏红绿灯按钮
- [ ] `macos-titlebar-style` — 标题栏样式（native/transparent/tabs/hidden）
- [ ] `macos-titlebar-proxy-icon` — 是否显示代理图标
- [ ] `macos-dock-drop-behavior` — 拖拽文件到 Dock 图标时的行为
- [ ] `macos-option-as-alt` — Option 键是否作为 Alt
- [ ] `macos-window-shadow` — 是否启用窗口阴影
- [ ] `macos-hidden` — 是否在 Dock 与切换器中隐藏图标
- [ ] `macos-auto-secure-input` — 检测到密码提示时自动开启 Secure Input
- [ ] `macos-secure-input-indication` — 是否显示 Secure Input 指示
- [ ] `macos-applescript` — 是否启用内置 AppleScript 字典
- [ ] `macos-icon` — 自定义应用图标样式
- [ ] `macos-custom-icon` — 自定义图标文件绝对路径
- [ ] `macos-icon-frame` — 图标外框材质（aluminum 等）
- [ ] `macos-icon-ghost-color` — 图标中幽灵的颜色
- [ ] `macos-icon-screen-color` — 图标中屏幕渐变色
- [ ] `macos-shortcuts` — 是否允许 macOS 快捷指令控制 Ghostty
- [ ] `auto-update` — 自动更新开关（仅 macOS）
- [ ] `auto-update-channel` — 自动更新频道

## 16. Linux / GTK 专属（15）

- [ ] `linux-cgroup` — 每个终端放入独立 systemd scope
- [ ] `linux-cgroup-memory-limit` — 单终端进程内存上限（字节）
- [ ] `linux-cgroup-processes-limit` — 单终端进程数上限
- [ ] `linux-cgroup-hard-fail` — cgroup 创建失败时是否直接失败
- [ ] `gtk-opengl-debug` — 输出 GTK OpenGL 调试日志
- [ ] `gtk-single-instance` — 是否单实例模式
- [ ] `gtk-titlebar` — 是否使用完整 GTK 标题栏
- [ ] `gtk-tabs-location` — 标签栏位置（top/bottom/hidden）
- [ ] `gtk-horizontal-tab-scroll` — 标签过多时横向滚动（1.4）
- [ ] `gtk-tabs-inverted` — 反转标签栏配色（1.4）
- [ ] `gtk-titlebar-hide-when-maximized` — 最大化时隐藏标题栏
- [ ] `gtk-toolbar-style` — 上下工具栏外观（flat 等）
- [ ] `gtk-titlebar-style` — GTK 标题栏样式（native/tabs）
- [ ] `gtk-wide-tabs` — 是否使用宽标签
- [ ] `gtk-custom-css` — 加载自定义 GTK CSS 文件
