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

    public static string SplitRight => Pick("Split right", "向右拆分窗格");
    public static string SplitDown => Pick("Split down", "向下拆分窗格");
    public static string SplitLeft => Pick("Split left", "向左拆分窗格");
    public static string SplitUp => Pick("Split up", "向上拆分窗格");
    public static string ToggleZoom => Pick("Toggle Split Zoom", "切换分屏缩放");
    public static string ClosePane => Pick("Close pane", "关闭窗格");
    public static string CloseSplit => Pick("Close split", "关闭拆分");

    // ---- tab context menu -------------------------------------------------

    public static string RenameTab => Pick("Rename tab", "重命名标签页");
    public static string CloseOtherTabs => Pick("Close other tabs", "关闭其他标签页");
    public static string CloseTabsToTheRight => Pick("Close tabs to the right", "关闭右侧标签页");
    public static string MoveTabToNewWindow => Pick("Move tab to new window", "将标签页移到新窗口");

    // ---- search -----------------------------------------------------------

    public static string FindPlaceholder => Pick("Search", "搜索");
    public static string SearchPrevious => Pick("Previous match (Shift+Enter)", "上一个匹配（Shift+Enter）");
    public static string SearchNext => Pick("Next match (Enter)", "下一个匹配（Enter）");
    public static string SearchClose => Pick("Close search (Esc)", "关闭查找（Esc）");

    // ---- command palette --------------------------------------------------

    public static string PalettePlaceholder =>
        Pick("Search commands, tabs or settings\u2026", "搜索命令、标签页或设置\u2026");

    public static string NewTab => Pick("New tab", "新建标签页");
    public static string NewWindow => Pick("New window", "新建窗口");
    public static string CloseFocusedPane => Pick("Close focused pane", "关闭当前窗格");
    public static string CloseTerminal => Pick("Close Terminal", "关闭终端");
    public static string NextTab => Pick("Next tab", "下一个标签页");
    public static string PreviousTab => Pick("Previous tab", "上一个标签页");
    public static string LastTab => Pick("Go to last tab", "切回上次使用的标签页");
    public static string ToggleSidebar => Pick("Toggle tab sidebar", "显示/隐藏标签侧栏");
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

    // ---- search ----
    public static string StartSearch => Pick("Start Search", "开始搜索");
    public static string SearchSelection => Pick("Search Selection", "搜索选中内容");
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
    public static string EqualizeSplits => Pick("Equalize Splits", "均分分屏");
    public static string ResizeSplitLeft => Pick("Resize Split Left", "调整分屏：向左");
    public static string ResizeSplitRight => Pick("Resize Split Right", "调整分屏：向右");
    public static string ResizeSplitUp => Pick("Resize Split Up", "调整分屏：向上");
    public static string ResizeSplitDown => Pick("Resize Split Down", "调整分屏：向下");

    // ---- debug & config ----
    public static string ResetWindowSize => Pick("Reset Window Size", "重置窗口大小");
    public static string ShowOnScreenKeyboard => Pick("Show On-Screen Keyboard", "显示屏幕键盘");
    public static string OpenConfigOs => Pick("Open Config Using OS editor", "用系统编辑器打开配置");
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

    public static string CommandFinishedTitle => Pick("Command finished", "命令已完成");

    public static string CommandFinishedNotification(string title, int exitCode)
    {
        string name = string.IsNullOrEmpty(title) ? Pick("Terminal", "终端") : title;
        return _language == Language.Chinese
            ? $"{name} 中的命令已完成（退出码 {exitCode}）"
            : $"Command finished in {name} (exit {exitCode})";
    }
    public static string ReloadConfig => Pick("Reload configuration", "重新加载配置");
    public static string OpenConfig => Pick("Open configuration file", "打开配置文件");
    public static string OpenDiagnosticsLog => Pick("Open diagnostics log", "打开调试日志");
    public static string CopyDiagnosticsLog => Pick("Copy diagnostics log", "复制调试日志");
    public static string EnableCrashDumps => Pick("Enable crash dumps", "启用崩溃转储");

    public static string ChooseWindowTheme => Pick("Choose window theme\u2026", "选择窗口主题\u2026");
    public static string WindowTheme => Pick("Window theme", "窗口主题");
    public static string ChooseTheme => Pick("Choose theme\u2026", "选择主题\u2026");
    public static string Theme => Pick("Theme", "主题");

    public static string TabBarTop => Pick("Move tab bar to top", "标签栏移到顶部");
    public static string TabBarLeft => Pick("Move tab bar to left", "标签栏移到左侧");
    public static string TabBarRight => Pick("Move tab bar to right", "标签栏移到右侧");

    public static string FollowSystem => Pick("Follow system", "跟随系统");
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
        Pick("Debug tap: on (records VT bytes)", "调试 tap：开（记录 VT 收发字节）");

    public static string TapOff =>
        Pick("Debug tap: off (records VT bytes)", "调试 tap：关（记录 VT 收发字节）");

    // ---- window buttons ----------------------------------------------------

    public static string Minimize => Pick("Minimize", "最小化");
    public static string Maximize => Pick("Maximize", "最大化");
    public static string Restore => Pick("Restore", "还原");
    public static string CloseWindow => Pick("Close", "关闭");

    /// <summary>The palette row that switches the pane layout.</summary>
    public static string CompactOn => Pick("Compact mode: on", "紧凑模式：开");
    public static string CompactOff => Pick("Compact mode: off", "紧凑模式：关");

    // ---- automation names -------------------------------------------------

    public static string CloseTab => Pick("Close tab", "关闭标签页");

    public static string TabName(string title) => Pick("Tab: ", "标签页: ") + title;
}
