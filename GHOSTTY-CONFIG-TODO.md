# Ghostty 配置项清单（TODO）

来源：<https://ghostty.org/docs/config/reference>（抓取于 2026-11-05，共 202 个配置键）
说明：按功能分组，一项对应一个 Ghostty 配置键；标注「仅 macOS」「仅 Linux/GTK」的项与 Windows 项目无关，可直接跳过。
用法：完成一项把 `[ ]` 改成 `[x]`，需要时在行尾补实现位置。

## 1. 应用与语言（1）

- [ ] `language` — 强制指定 GUI 界面语言（仅 Linux/GTK，需重启）

## 2. 字体家族与样式（21）

- [ ] `font-family` — 主字体族，可重复配置追加回退字体
- [ ] `font-family-bold` — 粗体字体族
- [ ] `font-family-italic` — 斜体字体族
- [ ] `font-family-bold-italic` — 粗斜体字体族
- [ ] `font-style` — 按字体自带的样式名选择常规字形
- [ ] `font-style-bold` — 粗体样式名
- [ ] `font-style-italic` — 斜体样式名
- [ ] `font-style-bold-italic` — 粗斜体样式名
- [ ] `font-synthetic-style` — 是否合成缺失的粗体/斜体（可用 no-bold 等关闭）
- [ ] `font-feature` — 开启或关闭 OpenType 特性（如 `-calt` 关连字）
- [ ] `font-size` — 字号（pt，可为小数）
- [ ] `font-variation` — 可变字体轴值（如 `wght=600`）
- [ ] `font-variation-bold` — 粗体使用的可变字体轴值
- [ ] `font-variation-italic` — 斜体使用的可变字体轴值
- [ ] `font-variation-bold-italic` — 粗斜体使用的可变字体轴值
- [ ] `font-codepoint-map` — 把指定码点/区间强制映射到某字体
- [ ] `clipboard-codepoint-map` — 复制时把指定码点替换成其他字符
- [ ] `font-thicken` — 加粗描边绘制字体（仅 macOS）
- [ ] `font-thicken-strength` — 描边加粗强度 0–255（仅 macOS）
- [ ] `font-shaping-break` — 在光标等位置断开 shaping run（影响连字）
- [ ] `alpha-blending` — 文本 alpha 混合使用的色彩空间（native/linear/linear-corrected）

## 3. 字形度量微调（15）

- [ ] `adjust-cell-width` — 单元格宽度微调（像素或百分比）
- [ ] `adjust-cell-height` — 单元格高度微调（文字垂直居中）
- [ ] `adjust-font-baseline` — 基线位置微调
- [ ] `adjust-underline-position` — 下划线位置微调
- [ ] `adjust-underline-thickness` — 下划线粗细微调
- [ ] `adjust-strikethrough-position` — 删除线位置微调
- [ ] `adjust-strikethrough-thickness` — 删除线粗细微调
- [ ] `adjust-overline-position` — 上划线位置微调
- [ ] `adjust-overline-thickness` — 上划线粗细微调
- [ ] `adjust-cursor-thickness` — 光标粗细微调
- [ ] `adjust-cursor-height` — 光标高度微调
- [ ] `adjust-box-thickness` — 制表符线框粗细微调
- [ ] `adjust-icon-height` — Nerd Font 图标最大高度微调
- [ ] `grapheme-width-method` — 字素簇占列数的计算方式（unicode/legacy）
- [ ] `freetype-load-flags` — FreeType 加载标志（仅 Linux）

## 4. 主题与配色（25）

- [ ] `theme` — 使用内置或自定义主题（名称/文件路径）
- [ ] `background` — 窗口背景色
- [ ] `foreground` — 窗口前景色
- [ ] `background-image` — 背景图片路径（PNG/JPEG）
- [ ] `background-image-opacity` — 背景图不透明度（相对 background-opacity）
- [ ] `background-image-position` — 背景图位置（九宫格取值）
- [ ] `background-image-fit` — 背景图适配方式（contain/cover/stretch/none）
- [ ] `background-image-repeat` — 背景图是否平铺
- [ ] `selection-foreground` — 选中文本前景色
- [ ] `selection-background` — 选中文本背景色
- [ ] `selection-clear-on-typing` — 输入时清除选区（默认 true）
- [ ] `selection-clear-on-copy` — 复制后清除选区（默认 false）
- [ ] `selection-word-chars` — 双击选词的分词边界字符
- [ ] `minimum-contrast` — 前景/背景最小对比度（1–21）
- [ ] `palette` — 自定义 256 色板（`N=COLOR`）
- [ ] `palette-generate` — 由基础 16 色自动生成 16–255 色
- [ ] `palette-harmonious` — 反转自动生成色板的顺序
- [ ] `bold-color` — 覆盖粗体文字颜色
- [ ] `faint-opacity` — 暗色（faint）文字不透明度
- [ ] `cursor-color` — 光标颜色
- [ ] `cursor-opacity` — 光标不透明度
- [ ] `cursor-style` — 默认光标形状（block/bar/underline 等）
- [ ] `cursor-style-blink` — 光标默认是否闪烁
- [ ] `cursor-text` — 光标下文字颜色
- [ ] `cursor-click-to-move` — 点击提示符移动光标（依赖 shell 集成）

## 5. 背景、分屏与搜索（11）

- [ ] `background-opacity` — 窗口背景不透明度
- [ ] `background-opacity-cells` — 显式设置背景色的单元格也套用透明度
- [ ] `background-blur` — 背景模糊开关或半径
- [ ] `unfocused-split-opacity` — 未聚焦分屏的不透明度
- [ ] `unfocused-split-fill` — 未聚焦分屏的叠加色
- [ ] `split-divider-color` — 分屏分隔线颜色
- [ ] `split-preserve-zoom` — 何时保留分屏缩放状态
- [ ] `search-foreground` — 搜索命中项前景色
- [ ] `search-background` — 搜索命中项背景色
- [ ] `search-selected-foreground` — 当前搜索命中项前景色
- [ ] `search-selected-background` — 当前搜索命中项背景色

## 6. 进程与启动（11）

- [ ] `command` — 启动的命令/Shell
- [ ] `initial-command` — 仅首个终端使用的启动命令
- [ ] `notify-on-command-finish` — 命令结束时是否通知（never/always/unfocused）
- [ ] `notify-on-command-finish-action` — 通知方式（bell/notification）
- [ ] `notify-on-command-finish-after` — 命令运行超过多久才触发通知
- [ ] `env` — 追加环境变量（`KEY=VALUE`，可重复）
- [ ] `input` — 启动时向子进程写入的输入数据
- [ ] `wait-after-command` — 命令退出后保持终端不关闭
- [ ] `abnormal-command-exit-runtime` — 判定异常退出所需的最短运行毫秒数
- [ ] `scrollback-limit` — 回滚缓冲区字节上限（含活动屏）
- [ ] `scrollbar` — 滚动条显示策略（system/never/always）

## 7. 链接、鼠标与选择（11）

- [ ] `link` — 正则匹配终端文本并绑定点击动作
- [ ] `link-url` — 是否识别 URL 并可点击打开
- [ ] `link-previews` — 是否显示链接预览
- [ ] `mouse-hide-while-typing` — 打字时隐藏鼠标指针
- [ ] `scroll-to-bottom` — 何时自动滚动到底部
- [ ] `mouse-shift-capture` — Shift+点击是否交给运行中的程序识别
- [ ] `mouse-reporting` — 是否向程序上报鼠标事件
- [ ] `mouse-scroll-multiplier` — 滚轮滚动倍数（可加 precision:/discrete:）
- [ ] `copy-on-select` — 选中即复制
- [ ] `right-click-action` — 右键行为（context-menu/paste/copy/paste_or_copy）
- [ ] `click-repeat-interval` — 判定连击的间隔毫秒数

## 8. 窗口与布局（36）

- [ ] `maximize` — 新窗口启动即最大化
- [ ] `fullscreen` — 新窗口启动即全屏
- [ ] `title` — 固定窗口标题
- [ ] `class` — 应用窗口类名（X11 WM_CLASS）
- [ ] `x11-instance-name` — X11 WM_CLASS 实例名（仅 X11）
- [ ] `working-directory` — 启动后切换到的目录
- [ ] `keybind` — 键绑定（`trigger=action`）
- [ ] `key-remap` — 应用层修饰键重映射
- [ ] `window-padding-x` — 水平内边距
- [ ] `window-padding-y` — 垂直内边距
- [ ] `window-padding-balance` — 余量像素在两侧均衡分配
- [ ] `window-padding-color` — 内边距区域颜色
- [ ] `window-vsync` — 垂直同步
- [ ] `window-inherit-working-directory` — 新窗口继承工作目录
- [ ] `tab-inherit-working-directory` — 新标签页继承工作目录
- [ ] `split-inherit-working-directory` — 新分屏继承工作目录
- [ ] `window-inherit-font-size` — 新窗口/标签继承字号
- [ ] `window-decoration` — 窗口装饰（标题栏/边框）偏好
- [ ] `window-title-font-family` — 窗口与标签标题字体
- [ ] `window-subtitle` — 窗口副标题内容
- [ ] `window-theme` — 窗口主题（auto/light/dark/ghostty）
- [ ] `window-colorspace` — 解释终端颜色所用的色彩空间
- [ ] `window-height` — 初始窗口高度（单元格）
- [ ] `window-width` — 初始窗口宽度（单元格）
- [ ] `window-position-x` — 初始窗口 X 位置（像素）
- [ ] `window-position-y` — 初始窗口 Y 位置（像素）
- [ ] `window-save-state` — 保存并恢复窗口状态
- [ ] `window-step-resize` — 按单元格尺寸步进调整窗口
- [ ] `window-new-tab-position` — 新标签页插入位置
- [ ] `window-show-tab-bar` — 标签栏显示策略
- [ ] `window-titlebar-background` — 标题栏背景色（window-theme=ghostty）
- [ ] `window-titlebar-foreground` — 标题栏前景色（window-theme=ghostty）
- [ ] `resize-overlay` — 调整大小时浮层的显示策略
- [ ] `resize-overlay-position` — 浮层位置
- [ ] `resize-overlay-duration` — 浮层显示时长
- [ ] `focus-follows-mouse` — 鼠标移动即切换聚焦分屏

## 9. 剪贴板（5）

- [ ] `clipboard-read` — 允许程序读取系统剪贴板（OSC 52）
- [ ] `clipboard-write` — 允许程序写入系统剪贴板（OSC 52）
- [ ] `clipboard-trim-trailing-spaces` — 复制时去掉行尾空白
- [ ] `clipboard-paste-protection` — 粘贴可疑内容前二次确认
- [ ] `clipboard-paste-bracketed-safe` — 括号粘贴视为安全

## 10. 配置加载与生命周期（17）

- [ ] `config-file` — 追加读取的配置文件（可重复）
- [ ] `config-default-files` — 是否加载默认路径的配置文件
- [ ] `confirm-close-surface` — 关闭终端前是否需要确认（默认 true）
- [ ] `quit-after-last-window-closed` — 最后一个窗口关闭后是否退出
- [ ] `quit-after-last-window-closed-delay` — 退出前的延迟时长
- [ ] `initial-window` — 启动时是否创建初始窗口
- [ ] `undo-timeout` — 撤销操作的有效期
- [ ] `title-report` — 是否允许程序用 CSI 21 t 查询标题
- [ ] `image-storage-limit` — 单屏图像数据（Kitty 协议）字节上限
- [ ] `term` — TERM 环境变量值
- [ ] `enquiry-response` — 收到 ENQ(0x05) 时回复的字符串
- [ ] `vt-kam-allowed` — 是否允许 KAM 模式禁用键盘输入
- [ ] `osc-color-report-format` — OSC 颜色查询的回报格式
- [ ] `async-backend` — 异步 IO 后端（auto/epoll/io_uring 等）
- [ ] `app-notifications` — 应用内通知（toast）的控制项
- [ ] `desktop-notifications` — 是否允许 OSC 桌面通知
- [ ] `progress-style` — 是否支持 OSC 9;4 图形进度条

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

- [ ] `shell-integration` — 是否自动注入 shell 集成
- [ ] `shell-integration-features` — 启用哪些 shell 集成特性
- [ ] `command-palette-entry` — 自定义命令面板条目

## 13. 自定义着色器（2）

- [ ] `custom-shader` — 在默认着色器之后追加 GLSL 着色器
- [ ] `custom-shader-animation` — 是否为客户着色器运行动画循环

## 14. 提示音与通知（3）

- [ ] `bell-features` — 铃音特性（audio/attention 等，逗号分隔）
- [ ] `bell-audio-path` — 提示音音频文件路径
- [ ] `bell-audio-volume` — 提示音音量（相对系统音量）

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

## 16. Linux / GTK 专属（13）

- [ ] `linux-cgroup` — 每个终端放入独立 systemd scope
- [ ] `linux-cgroup-memory-limit` — 单终端进程内存上限（字节）
- [ ] `linux-cgroup-processes-limit` — 单终端进程数上限
- [ ] `linux-cgroup-hard-fail` — cgroup 创建失败时是否直接失败
- [ ] `gtk-opengl-debug` — 输出 GTK OpenGL 调试日志
- [ ] `gtk-single-instance` — 是否单实例模式
- [ ] `gtk-titlebar` — 是否使用完整 GTK 标题栏
- [ ] `gtk-tabs-location` — 标签栏位置（top/bottom/hidden）
- [ ] `gtk-titlebar-hide-when-maximized` — 最大化时隐藏标题栏
- [ ] `gtk-toolbar-style` — 上下工具栏外观（flat 等）
- [ ] `gtk-titlebar-style` — GTK 标题栏样式（native/tabs）
- [ ] `gtk-wide-tabs` — 是否使用宽标签
- [ ] `gtk-custom-css` — 加载自定义 GTK CSS 文件
