using System;

namespace Gui;

/// <summary>UI language, driven by the Ghostty "language" option.</summary>
public enum Language
{
    /// <summary>Default: every string is English.</summary>
    English,

    /// <summary>Simplified Chinese.</summary>
    Chinese,
}

/// <summary>
/// Every user-facing string the app draws itself. The standard is English; Chinese is a
/// full translation selected by "language = zh-CN" (or "zh", "chinese").
/// </summary>
public static class Strings
{
    private static Language _language = Language.English;

    public static Language Current => _language;

    /// <summary>
    /// Selects the language. It is applied once at startup and again whenever the config is
    /// reloaded (`Ctrl+Shift+,`), including in the other open windows.
    /// </summary>
    public static void Use(Language language) => _language = language;

    private static string Pick(string english, string chinese) =>
        _language == Language.Chinese ? chinese : english;

    // ---- terminal pane ----------------------------------------------------

    public static string SplitRight => Pick("Split Right", "向右拆分");
    public static string SplitDown => Pick("Split Down", "向下拆分");
    public static string SplitLeft => Pick("Split Left", "向左拆分");
    public static string SplitUp => Pick("Split Up", "向上拆分");
    public static string ToggleZoom => Pick("Toggle Split Zoom", "切换分屏缩放");
    public static string ClosePane => Pick("Close Pane", "关闭窗格");
    public static string CloseSplit => Pick("Close Split", "关闭拆分");

    /// <summary>
    /// Printed into a surface whose process died too quickly ("abnormal-command-exit-runtime").
    /// {0} is the exit code, {1} the runtime.
    /// </summary>
    public static string AbnormalExit => Pick(
        "Process exited abnormally: code {0} after {1}",
        "进程异常退出：退出码 {0}，运行 {1}");

    // ---- tab context menu -------------------------------------------------

    public static string RenameTab => Pick("Rename Tab", "重命名标签页");
    public static string CloseOtherTabs => Pick("Close Other Tabs", "关闭其他标签页");
    public static string CloseTabsToTheRight => Pick("Close Tabs to the Right", "关闭右侧标签页");
    public static string MoveTabToNewWindow => Pick("Move Tab to New Window", "将标签页移到新窗口");

    // ---- search -----------------------------------------------------------

    public static string FindPlaceholder => Pick("Search", "搜索");

    // ---- command palette --------------------------------------------------

    public static string PalettePlaceholder =>
        Pick("Search commands, tabs or settings\u2026", "搜索命令、标签页或设置\u2026");

    public static string NewTab => Pick("New Tab", "新建标签页");
    public static string NewWindow => Pick("New Window", "新建窗口");
    public static string ToggleQuickTerminal => Pick("Quick Terminal", "快速终端");
    public static string CloseFocusedPane => Pick("Close Focused Pane", "关闭当前窗格");
    public static string CloseTerminal => Pick("Close Terminal", "关闭终端");
    public static string NextTab => Pick("Next Tab", "下一个标签页");
    public static string PreviousTab => Pick("Previous Tab", "上一个标签页");
    public static string LastTab => Pick("Go to Last Tab", "切回上次使用的标签页");
    public static string ToggleSidebar => Pick("Toggle Sidebar", "显示/隐藏侧栏");
    public static string IncreaseFontSize => Pick("Increase Font Size", "增大字号");
    public static string DecreaseFontSize => Pick("Decrease Font Size", "减小字号");
    public static string ResetFontSize => Pick("Reset Font Size", "恢复默认字号");

    // ---- clipboard & terminal ----
    public static string ResetTerminal => Pick("Reset Terminal", "重置终端");
    public static string CopyToClipboard => Pick("Copy to Clipboard", "复制到剪贴板");
    public static string CopySelectionPlain =>
        Pick("Copy Selection as Plain Text to Clipboard", "以纯文本复制选中内容");
    public static string CopySelectionAnsi =>
        Pick("Copy Selection as ANSI Sequences to Clipboard", "以 ANSI 序列复制选中内容");
    public static string CopySelectionHtml =>
        Pick("Copy Selection as HTML to Clipboard", "以 HTML 复制选中内容");
    public static string CopyUrl => Pick("Copy URL to Clipboard", "复制 URL 到剪贴板");
    public static string CopyTitle => Pick("Copy Terminal Title to Clipboard", "复制终端标题到剪贴板");
    public static string PasteFromClipboard => Pick("Paste from Clipboard", "从剪贴板粘贴");
    public static string PasteFromSelection => Pick("Paste from Selection", "粘贴选中内容");
    public static string Copy => Pick("Copy", "复制");
    public static string Paste => Pick("Paste", "粘贴");

    // ---- main menu ----
    public static string MainMenu => Pick("Main Menu", "主菜单");
    public static string FileMenu => Pick("File", "文件");
    public static string EditMenu => Pick("Edit", "编辑");
    public static string ViewMenu => Pick("View", "视图");
    public static string WindowMenu => Pick("Window", "窗口");
    public static string ViewLayout => Pick("Tabs Position", "标签栏位置");
    public static string ViewZoomSplit => Pick("Zoom Split", "缩放分屏");
    public static string ViewResizeSplit => Pick("Resize Split", "调整分屏");
    public static string ViewFocusSplit => Pick("Focus Split", "聚焦分屏");
    public static string EditFind => Pick("Find", "查找");
    public static string OpenDirectory => Pick("Open Directory…", "打开目录…");
    public static string CommandPalette => Pick("Command Palette", "命令面板");
    public static string Settings => Pick("Settings", "设置");
    public static string Exit => Pick("Exit", "退出");

    public static string HelpMenu => Pick("Help", "帮助");
    public static string HelpAbout => Pick("About", "关于");
    public static string HelpCheckForUpdates => Pick("Check for Updates", "检查更新");
    public static string UpdateChecking => Pick("Checking for Updates\u2026", "正在检查更新\u2026");
    public static string UpdateUpToDate => Pick("No Updates Available", "没有可用更新");
    public static string UpdateAvailable(string version) =>
        Pick($"Update Available: {version}", $"有可用更新：{version}");
    public static string UpdateFailed =>
        Pick("Update Check Failed, Please Try Again Later", "检查更新失败，请稍后重试");
    public static string HelpReleaseNotes => Pick("Release Notes", "发布说明");
    public static string HelpDocumentation => Pick("Documentation", "文档");
    public static string HelpFeedback => Pick("Feedback", "反馈");

    // ---- settings ----
    public static string SettingsTitle => Pick("Settings", "设置");
    public static string SettingsNavAppearance => Pick("Appearance", "外观");
    public static string SettingsNavTerminal => Pick("Terminal", "终端");
    public static string SettingsNavKeys => Pick("Key bindings", "快捷键");
    public static string SettingsNavAbout => Pick("About", "关于");
    public static string SettingsNavNotifications => Pick("Notifications", "通知");
    public static string SettingsLanguage => Pick("Language", "语言");
    public static string SettingsWindowAppearance => Pick("Window theme", "窗口外观");
    public static string SettingsTabPosition => Pick("Tabs position", "标签栏位置");
    public static string SettingsTabTop => Pick("Top", "顶部");
    public static string SettingsTabLeft => Pick("Left", "左侧");
    public static string SettingsTabRight => Pick("Right", "右侧");
    public static string SettingsCompact => Pick("Compact mode", "紧凑模式");
    public static string ViewCompact => Pick("Compact Mode", "紧凑模式");

    public static string SettingsCompactDescription => Pick(
        "Drop the pane gaps and rounded corners so the terminal fills the window edges.",
        "去掉窗格间距与圆角，终端内容贴满窗口边缘。");

    public static string SettingsThemes => Pick("Themes", "主题");
    public static string SettingsSearchThemes => Pick("Search themes", "搜索主题");
    public static string SettingsLightTheme => Pick("Light theme", "浅色主题");
    public static string SettingsDarkTheme => Pick("Dark theme", "深色主题");
    public static string SettingsBackground => Pick("Background", "背景");
    public static string SettingsBackgroundOpacity => Pick("Background opacity", "背景透明度");
    public static string SettingsBackgroundBlur => Pick("Background blur", "背景模糊");
    public static string SettingsBackgroundImage => Pick("Background image", "背景图片");
    public static string SettingsBrowse => Pick("Browse…", "浏览…");
    public static string SettingsRemove => Pick("Remove", "移除");
    public static string SettingsStartup => Pick("Startup", "启动");
    public static string SettingsCommand => Pick("Command", "启动命令");
    public static string SettingsCommandAuto => "pwsh/powershell/cmd";
    public static string SettingsWorkingDirectory => Pick("Working directory", "工作目录");
    public static string SettingsShellFeatures => Pick("Shell integration features", "Shell 集成特性");
    public static string SettingsNewTabPosition => Pick("New tab position", "新标签页位置");
    public static string SettingsNewTabPositionCurrent => Pick("After current", "当前之后");
    public static string SettingsNewTabPositionEnd => Pick("At end", "末尾");
    public static string SettingsTabInheritWorkingDirectory =>
        Pick("Inherit working directory in new tabs", "新标签页继承工作目录");
    public static string SettingsRightClickAction => Pick("Right-click action", "右键菜单");
    public static string SettingsRightClickContextMenu => Pick("Context menu", "上下文菜单");
    public static string SettingsRightClickPaste => Pick("Paste", "粘贴");
    public static string SettingsRightClickCopy => Pick("Copy", "复制");
    public static string SettingsRightClickCopyOrPaste =>
        Pick("Copy or paste", "有选中则复制，否则粘贴");
    public static string SettingsRightClickIgnore => Pick("Ignore", "忽略");

    public static string SettingsFont => Pick("Font", "字体");
    public static string SettingsFontFamily => Pick("Font family", "字体族");
    public static string SettingsFontSize => Pick("Font size", "字号");
    public static string SettingsFontFeatures => Pick("Font features", "字体特性");
    public static string SettingsCursor => Pick("Cursor", "光标");
    public static string SettingsCursorShape => Pick("Cursor shape", "光标形状");
    public static string SettingsCursorBlock => Pick("Block", "方块");
    public static string SettingsCursorBar => Pick("Bar", "竖线");
    public static string SettingsCursorUnderline => Pick("Underline", "下划线");
    public static string SettingsCursorBlockHollow => Pick("Hollow block", "空心方块");
    public static string SettingsCursorBlink => Pick("Blink cursor", "光标闪烁");

    public static string SettingsSelection => Pick("Selection", "选择");
    public static string SettingsCopyOnSelect => Pick("Copy on select", "选中即复制");

    public static string SettingsNotifyCommand => Pick("Notify", "命令通知");
    public static string SettingsNotifyAction => Pick("Notify on command finish action", "命令结束时通知动作");
    public static string SettingsNotifyOnFinish => Pick("Notify on command finish", "命令结束时通知");
    public static string SettingsNotifyOnFinishDescription => Pick(
        "Reads the OSC 133 D report from the shell integration. \"Unfocused\" only fires while the pane or the window is in the background.",
        "依赖 shell 集成上报的 OSC 133 D。选择“未聚焦时”只在窗格或窗口处于后台时通知。");
    public static string SettingsNotifyNever => Pick("Never", "从不");
    public static string SettingsNotifyUnfocused => Pick("Unfocused", "未聚焦时");
    public static string SettingsNotifyAlways => Pick("Always", "总是");
    public static string SettingsNotifyBell => Pick("Bell", "响铃");
    public static string SettingsNotifyToast => Pick("System", "系统通知");
    public static string SettingsNotifyAfter => Pick("Notify on command finish after", "命令运行多长时间后才通知");
    public static string SettingsDesktopNotifications => Pick("Desktop notifications", "桌面通知");
    public static string SettingsDesktopNotificationsAllow => Pick(
        "Allow programs to send notifications", "允许程序发送通知");
    public static string SettingsDesktopNotificationsDescription => Pick(
        "Allow programs to raise a notification with OSC 9 or OSC 777.",
        "允许程序通过 OSC 9 / OSC 777 发起通知。");
    public static string SettingsBell => Pick("Bell features", "提示音");
    public static string SettingsBellSystem => Pick("System", "系统提示音");
    public static string SettingsBellAudio => Pick("Custom audio file", "自定义音频");
    public static string SettingsBellAttention => Pick("Flash the taskbar", "任务栏闪烁");
    public static string SettingsBellTitle => Pick("Mark the title", "标题标记");
    public static string SettingsBellBorder => Pick("Highlight the pane border", "高亮窗格边框");
    public static string SettingsBellAudioGroup => Pick("Bell audio", "响铃音频");
    public static string SettingsBellAudioPath => Pick("Audio file", "音频文件");
    public static string SettingsBellVolume => Pick("Volume", "音量");

    public static string SettingsKeysUnbound => Pick("None", "未绑定");
    public static string SettingsKeysGroupTabs => Pick("Tabs", "标签页");
    public static string SettingsKeysGroupPanes => Pick("Panes", "窗格");
    public static string SettingsKeysGroupTerminal => Pick("Terminal", "终端");
    public static string SettingsKeysGroupClipboard => Pick("Clipboard", "剪贴板");
    public static string SettingsKeysGroupSearch => Pick("Search", "搜索");
    public static string SettingsKeysGroupWindow => Pick("Window", "窗口");
    public static string SettingsKeysGroupApp => Pick("Application", "应用");
    public static string SettingsKeysGroupOther => Pick("Other", "其他");
    public static string SettingsBack => Pick("Back", "返回");
    public static string SettingsOpenConfig => Pick("Open config file", "打开配置文件");
    public static string AboutRepository => Pick("Repository", "仓库地址");
    public static string AboutCopyRepository => Pick("Copy repository URL", "复制仓库地址");
    public static string AboutLicense => Pick("Open source license", "开源协议");
    public static string AboutCopyright(int year) => Pick(
        $"\u00a9 {year} Gui. All rights reserved.", $"\u00a9 {year} Gui. 保留所有权利。");


    // ---- search ----
    public static string StartSearch => Pick("Start Search", "开始搜索");
    public static string SearchSelection => Pick("Search Selection", "搜索选中内容");
    public static string SearchPrevious => Pick("Previous Match", "上一个匹配");
    public static string SearchNext => Pick("Next Match", "下一个匹配");
    public static string SearchClose => Pick("Close Search", "关闭搜索");
    public static string EndSearch => Pick("End Search", "结束搜索");
    public static string NextSearchResult => Pick("Next Search Result", "下一个搜索结果");
    public static string PreviousSearchResult => Pick("Previous Search Result", "上一个搜索结果");

    // ---- screen & scroll ----
    public static string ClearScreen => Pick("Clear Screen", "清屏");
    public static string SelectAll => Pick("Select All", "全选");
    public static string ScrollToTop => Pick("Scroll to Top", "滚动到顶部");
    public static string ScrollToBottom => Pick("Scroll to Bottom", "滚动到底部");
    public static string ScrollToSelection => Pick("Scroll to Selection", "滚动到选中内容");
    public static string ScrollPageUp => Pick("Scroll Page Up", "向上翻页");
    public static string ScrollPageDown => Pick("Scroll Page Down", "向下翻页");
    public static string JumpToPreviousPrompt => Pick("Jump to Previous Prompt", "跳转到上一个提示符");
    public static string JumpToNextPrompt => Pick("Jump to Next Prompt", "跳转到下一个提示符");
    public static string AdjustSelectionLeft => Pick("Extend Selection Left", "向左扩展选中");
    public static string AdjustSelectionRight => Pick("Extend Selection Right", "向右扩展选中");
    public static string AdjustSelectionUp => Pick("Extend Selection Up", "向上扩展选中");
    public static string AdjustSelectionDown => Pick("Extend Selection Down", "向下扩展选中");
    public static string GoToTab(int slot) => Pick($"Go to Tab {slot}", $"切换到标签页 {slot}");

    // ---- export to a temporary file ----
    public static string WriteScreenFile(string action, string emit) =>
        WriteFileCommand(true, action, emit);

    public static string WriteSelectionFile(string action, string emit) =>
        WriteFileCommand(false, action, emit);

    private static string WriteFileCommand(bool screen, string action, string emit)
    {
        string what = screen ? Pick("Screen", "屏幕") : Pick("Selection", "选中内容");
        if (emit.Length > 0)
        {
            what += emit == "html"
                ? Pick(" as HTML", "（HTML）")
                : Pick(" as ANSI Sequences", "（ANSI 序列）");
        }

        string verb = action switch
        {
            "copy" => Pick("Copy Path", "复制路径"),
            "paste" => Pick("Paste Path", "粘贴路径"),
            _ => Pick("Open", "打开"),
        };
        return Pick("Copy ", "复制") + what + Pick(" to Temporary File and ", "到临时文件并") + verb;
    }

    // ---- window & tabs ----
    public static string MoveTabLeft => Pick("Move Tab Left", "标签页左移");
    public static string MoveTabRight => Pick("Move Tab Right", "标签页右移");
    public static string ChangeTerminalTitle => Pick("Change Terminal Title\u2026", "修改终端标题\u2026");
    public static string ChangeTabTitle => Pick("Change Tab Title\u2026", "修改标签页标题\u2026");
    public static string ChangeWindowTitle => Pick("Change Window Title\u2026", "修改窗口标题\u2026");

    // ---- splits ----
    public static string FocusSplitPrevious => Pick("Focus Split: Previous", "聚焦分屏：上一个");
    public static string FocusSplitNext => Pick("Focus Split: Next", "聚焦分屏：下一个");
    public static string FocusSplitLeft => Pick("Focus Split: Left", "聚焦分屏：向左");
    public static string FocusSplitRight => Pick("Focus Split: Right", "聚焦分屏：向右");
    public static string FocusSplitUp => Pick("Focus Split: Up", "聚焦分屏：向上");
    public static string FocusSplitDown => Pick("Focus Split: Down", "聚焦分屏：向下");
    public static string ToggleReadOnly => Pick("Toggle Read-Only Mode", "切换只读模式");
    public static string ReadOnlyStatus => Pick("Read-Only", "只读");
    public static string EqualizeSplits => Pick("Equalize Splits", "均分分屏");
    public static string ResizeSplitLeft => Pick("Resize Split Left", "调整分屏：向左");
    public static string ResizeSplitRight => Pick("Resize Split Right", "调整分屏：向右");
    public static string ResizeSplitUp => Pick("Resize Split Up", "调整分屏：向上");
    public static string ResizeSplitDown => Pick("Resize Split Down", "调整分屏：向下");

    // ---- debug & config ----
    public static string ResetWindowSize => Pick("Reset Window Size", "重置窗口大小");
    public static string ShowOnScreenKeyboard => Pick("Show On-Screen Keyboard", "显示屏幕键盘");
    public static string OpenConfigOs => Pick("Open Config Using OS Editor", "用系统编辑器打开配置");
    public static string OpenConfigNewWindow =>
        Pick("Open Config in New Terminal Window", "在新终端窗口中打开配置");

    // ---- close ----
    public static string CloseWindowCommand => Pick("Close Window", "关闭窗口");
    public static string CloseAllWindows => Pick("Close All Windows", "关闭所有窗口");
    public static string Quit => Pick("Quit", "退出 Gui");

    // ---- window state ----
    public static string ToggleMaximize => Pick("Toggle Maximize", "切换最大化");
    public static string ToggleFullscreen => Pick("Toggle Fullscreen", "切换全屏");
    public static string ToggleFloatOnTop => Pick("Toggle Float on Top", "切换窗口置顶");
    public static string ToggleMouseReporting => Pick("Toggle Mouse Reporting", "切换鼠标上报");
    public static string ToggleBackgroundOpacity => Pick("Toggle Background Opacity", "切换背景不透明度");

    public static string Ok => Pick("OK", "确定");
    public static string PasteWarningTitle => Pick("Paste multiple lines?", "粘贴多行内容？");

    public static string PasteWarningBody => Pick(
        "The clipboard contains multiple lines or control characters. Pasted text can run commands in the terminal.",
        "剪贴板内容包含多行文本或控制字符，粘贴后可能直接在终端中执行命令。");

    public static string PasteAnyway => Pick("Paste anyway", "仍然粘贴");
    public static string Cancel => Pick("Cancel", "取消");

    public static string CloseWarningTitle => Pick("Close window?", "关闭窗口？");

    public static string CloseWarningBody => Pick(
        "A command is still running in this window. Closing it will stop the command.",
        "此窗口中仍有命令正在运行，关闭窗口会终止该命令。");

    public static string CloseTerminalWarningTitle => Pick("Close terminal?", "关闭终端？");

    public static string CloseTerminalWarningBody => Pick(
        "A command is still running in this terminal. Closing it will stop the command.",
        "此终端中仍有命令正在运行，关闭会终止该命令。");

    public static string CommandFinishedTitle => Pick("Command finished", "命令已完成");

    public static string CommandFinishedNotification(string title, int exitCode)
    {
        string name = string.IsNullOrEmpty(title) ? Pick("Terminal", "终端") : title;
        return _language == Language.Chinese
            ? $"{name} 中的命令已完成（退出码 {exitCode}）"
            : $"Command finished in {name} (exit {exitCode})";
    }
    public static string ReloadConfig => Pick("Reload Configuration", "重新加载配置");
    public static string OpenConfig => Pick("Open Configuration File", "打开配置文件");
    public static string OpenDiagnosticsLog => Pick("Open Diagnostics Log", "打开调试日志");
    public static string CopyDiagnosticsLog => Pick("Copy Diagnostics Log", "复制调试日志");
    public static string EnableCrashDumps => Pick("Enable Crash Dumps", "启用崩溃转储");

    public static string ChooseWindowTheme => Pick("Choose Window Theme\u2026", "选择窗口主题\u2026");
    public static string WindowTheme => Pick("Window Theme", "窗口主题");
    public static string ChooseTheme => Pick("Choose Theme\u2026", "选择主题\u2026");
    public static string Theme => Pick("Theme", "主题");

    public static string TabBarTop => Pick("Tabs on Top", "标签栏在顶部");
    public static string TabBarLeft => Pick("Tabs on Left", "标签栏在左侧");
    public static string TabBarRight => Pick("Tabs on Right", "标签栏在右侧");

    public static string FollowSystem => Pick("System", "系统");
    public static string Light => Pick("Light", "浅色");
    public static string Dark => Pick("Dark", "深色");

    public static string NoMatchingCommands =>
        Pick("No matching commands. Try \u201cnew\u201d, \u201csplit\u201d or \u201cconfig\u201d.",
            "没有匹配的命令，试试\u201c新建\u201d\u201c分屏\u201d或\u201c配置文件\u201d。");

    public static string HintSelect => Pick("\u2191\u2193 select", "\u2191\u2193 选择");
    public static string HintRun => Pick("Enter run", "Enter 执行");
    public static string HintClose => Pick("Esc close", "Esc 关闭");
    public static string HintBack => Pick("Backspace back", "Backspace 返回");

    public static string TapOn =>
        Pick("Debug Tap: On (Records VT Bytes)", "调试 tap：开（记录 VT 收发字节）");

    public static string TapOff =>
        Pick("Debug Tap: Off (Records VT Bytes)", "调试 tap：关（记录 VT 收发字节）");

    // ---- window buttons ----------------------------------------------------

    public static string Minimize => Pick("Minimize", "最小化");
    public static string Maximize => Pick("Maximize", "最大化");
    public static string Restore => Pick("Restore", "还原");
    public static string CloseWindow => Pick("Close", "关闭");

    /// <summary>The palette row that switches the pane layout.</summary>
    public static string CompactOn => Pick("Compact Mode: On", "紧凑模式：开");
    public static string CompactOff => Pick("Compact Mode: Off", "紧凑模式：关");

    // ---- automation names -------------------------------------------------

    public static string CloseTab => Pick("Close Tab", "关闭标签页");

    public static string TabName(string title) => Pick("Tab: ", "标签页: ") + title;
}
