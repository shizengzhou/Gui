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

    /// <summary>Selects the language once at startup; changing it needs a restart, like Ghostty.</summary>
    public static void Use(Language language) => _language = language;

    private static string Pick(string english, string chinese) =>
        _language == Language.Chinese ? chinese : english;

    // ---- terminal pane ----------------------------------------------------

    public static string SplitRight => Pick("Split right", "向右拆分窗格");
    public static string SplitDown => Pick("Split down", "向下拆分窗格");
    public static string ToggleZoom => Pick("Toggle pane zoom", "切换窗格缩放");
    public static string ClosePane => Pick("Close pane", "关闭窗格");

    // ---- command palette --------------------------------------------------

    public static string PalettePlaceholder =>
        Pick("Search commands, tabs or settings\u2026", "搜索命令、标签页或设置\u2026");

    public static string NewTab => Pick("New tab", "新建标签页");
    public static string CloseFocusedPane => Pick("Close focused pane", "关闭当前窗格");
    public static string CopySelection => Pick("Copy selection", "复制选中内容");
    public static string Paste => Pick("Paste", "粘贴");
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
