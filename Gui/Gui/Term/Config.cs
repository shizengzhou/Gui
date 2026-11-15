using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Gui.Term;

/// <summary>Severity of a configuration problem.</summary>
public enum ConfigSeverity
{
    /// <summary>The option is valid but this build has no equivalent for it.</summary>
    Notice,

    /// <summary>The option or its value could not be used.</summary>
    Warning,
}

public readonly record struct ConfigDiagnostic(int Line, ConfigSeverity Severity, string Key, string Message);

/// <summary>One "clipboard-codepoint-map" entry: a codepoint range and its replacement.</summary>
public readonly record struct CodepointMap(int Start, int End, string Replacement);

/// <summary>One "font-codepoint-map" entry: a codepoint range drawn with a given family.</summary>
public readonly record struct FontCodepointMap(int Start, int End, string Family);

/// <summary>One "font-variation" axis value, e.g. "wght=600".</summary>
public readonly record struct FontVariation(string Axis, float Value);

/// <summary>
/// "shell-integration-features": which pieces the injected script emits. Defaults mirror
/// Ghostty: cursor and title on, the sudo/ssh/path helpers off (and irrelevant on Windows).
/// </summary>
public readonly record struct ShellIntegrationFeatures(
    bool Cursor, bool CursorSteady, bool Title, bool Sudo, bool SshEnv, bool SshTerminfo, bool Path)
{
    public static ShellIntegrationFeatures Default => new(true, false, true, false, false, false, true);
}

/// <summary>Where a search highlight color comes from.</summary>
public enum SearchColorKind
{
    /// <summary>The built-in golden/peach search color.</summary>
    Default,

    /// <summary>A fixed color from the configuration.</summary>
    Explicit,

    /// <summary>Use the matched cell's foreground.</summary>
    CellForeground,

    /// <summary>Use the matched cell's background.</summary>
    CellBackground,
}

/// <summary>A "search-*" color: an explicit value or a per-cell reference.</summary>
public readonly record struct SearchColor(SearchColorKind Kind, Rgb Value);

/// <summary>
/// A pixel or percentage adjustment, the shape Ghostty uses for every "adjust-*" option:
/// a bare number is a pixel delta, a trailing "%" is a percentage of the base value.
/// </summary>
public readonly struct MetricAdjust
{
    public readonly bool IsPercent;
    public readonly float Amount;

    public MetricAdjust(bool isPercent, float amount)
    {
        IsPercent = isPercent;
        Amount = amount;
    }

    public bool IsZero => Amount == 0f;

    /// <summary>
    /// Adjusts a base value in the same unit the adjustment was written in. A pixel amount
    /// is divided by the rasterization scale because the renderer works in DIPs.
    /// </summary>
    public float Apply(float value, float scale) => value + Delta(value, scale);

    public float Apply(float value) => Apply(value, 1f);

    /// <summary>The adjustment as a delta against the given basis.</summary>
    public float Delta(float basis, float scale)
    {
        if (IsPercent) return basis * Amount / 100f;
        return scale > 0f ? Amount / scale : Amount;
    }

    public float Delta(float basis) => Delta(basis, 1f);
}

/// <summary>
/// Ghostty-style configuration: a plain text file with one "key = value" per line and
/// "#" starting a comment (the same shape as ghostty's own config file).
///
/// Every option in <see cref="ConfigSchema"/> is accepted. Options this app can honor are
/// resolved into the typed properties below; the rest are kept as raw values and reported
/// through <see cref="Diagnostics"/> so a user can see what had no effect.
/// </summary>
public sealed class Config
{
    /// <summary>The built-in light theme. Its config value and its name are the same string.</summary>
    public const string LightThemeName = "Gui Light";

    /// <summary>The built-in dark theme. Its config value and its name are the same string.</summary>
    public const string DarkThemeName = "Gui Dark";

    /// <summary>Effective value of "theme" when the key is absent.</summary>
    public const string DefaultTheme = "light:" + LightThemeName + ",dark:" + DarkThemeName;

    public string Command = string.Empty;          // empty: auto-detect pwsh/powershell/cmd
    public Language Language = Language.English;   // "language"; unset follows the system
    public string InitialCommand = string.Empty;   // empty: same as Command
    public string WorkingDirectory = string.Empty; // empty: %USERPROFILE%
    public string WindowTitle = string.Empty;      // empty: follow the active pane title
    public bool Maximize;                          // "maximize": start maximized
    public bool Fullscreen;                        // "fullscreen": start fullscreen
    public string FontFamily = string.Empty;       // empty: JetBrainsMono Nerd Font
    public string FontFamilyBold = string.Empty;   // empty: FontFamily
    public string FontFamilyItalic = string.Empty; // empty: FontFamily
    public string FontFamilyBoldItalic = string.Empty; // empty: FontFamilyBold or FontFamily
    public List<string> FontFamilies = new();       // primary first, then fallbacks
    public List<string> FontFamiliesBold = new();
    public List<string> FontFamiliesItalic = new();
    public List<string> FontFamiliesBoldItalic = new();
    public string FontStyleName = string.Empty;    // empty: the style the family advertises
    public string FontStyleBold = string.Empty;
    public string FontStyleItalic = string.Empty;
    public string FontStyleBoldItalic = string.Empty;
    public float FontSize = 12.0f;                 // points; Ghostty's default off macOS
    public const float MinFontSizePoints = 4.5f;   // 6 DIPs
    public const float MaxFontSizePoints = 72.0f;  // 96 DIPs
    public float UnfocusedSplitOpacity = 0.7f;     // 0.15..1, Ghostty's default
    public ulong ScrollbackLimit = 50_000_000;     // bytes, Ghostty 1.4's default
    public ulong ScrollbackLimitLines = ulong.MaxValue; // rows; unlimited, Ghostty 1.4's default
    public uint ImageStorageLimit = 320_000_000;   // bytes per screen, Ghostty 1.4's default
    public List<string> CustomShaders = new();     // resolved GLSL paths, run in order
    public string CustomShaderAnimation = "true";  // true, false, or always
    public string Scrollbar = "system";            // "system" (default) or "never"
    public bool HasUnfocusedSplitFill;
    public Rgb UnfocusedSplitFill;                 // empty: terminal background
    public bool HasSplitDividerColor;
    public Rgb SplitDividerColor;                  // empty: the theme's pane stroke
    public string TabPosition = string.Empty;      // "top" (default), "left", "right"
    public string NewTabPosition = "current";      // "current" (default) or "end"
    public bool ShowTabBar = true;                 // window-show-tab-bar: false when never
    public bool TabInheritWorkingDirectory = true; // new tabs start in the focused shell's cwd
    public bool WindowInheritWorkingDirectory = true; // new windows do the same
    public bool SplitInheritWorkingDirectory = true; // new splits do the same
    public string DragHandle = "auto";             // drag-handle: "auto" (default), "always", "never"
    public bool InheritFontSize = true;            // new tabs keep the focused pane's font size
    public bool Compact;                           // true: panes run edge to edge
    public double SidebarWidthLeft = 240.0;        // DIPs; restored width of a left rail
    public double SidebarWidthRight = 240.0;       // DIPs; restored width of a right rail
    public string ThemeMode = string.Empty;        // active side's theme name
    public string ThemeLightName = LightThemeName; // "theme" light: side
    public string ThemeDarkName = DarkThemeName;   // "theme" dark: side
    public string ThemeValue = DefaultTheme;       // canonical light:...,dark:... value
    public string WindowThemeMode = string.Empty;  // "system" (default), "light", "dark"
    public string TitleFontFamily = string.Empty;  // window-title-font-family; empty: system default
    public int WindowWidth;                        // window-width in cells; 0: runtime default
    public int WindowHeight;                       // window-height in cells; 0: runtime default
    public bool HasWindowPosition;                 // both window-position-x and -y were set
    public int WindowPositionX;
    public int WindowPositionY;
    public string WindowSaveState = "always";      // "always" or "never"
    public string QuickTerminalPosition = "top";   // "top" (default), "bottom", "left", "right", "center"
    public string QuickTerminalSize = string.Empty; // "50%" or "300px", optionally "primary,secondary"
    public string QuickTerminalScreen = "mouse";   // "mouse" (default) or "main"
    public bool QuickTerminalAutohide = true;      // hide the quick terminal when it loses focus
    public double QuickTerminalAnimationDuration = 0.2; // seconds; 0 shows and hides it instantly
    public bool PaddingBalance;                    // window-padding-balance
    public string ConfirmCloseSurface = "true";    // "true", "false", "always"
    public int ClickRepeatInterval;                // milliseconds; 0 = Ghostty's 500ms default
    public bool PasteProtection = true;            // clipboard-paste-protection
    public bool PasteBracketedSafe = true;         // clipboard-paste-bracketed-safe
    public List<string> ConfigFiles = new();       // config-file entries, resolved
    public bool BellSystem;                        // bell-features: system
    public bool BellAudio;                         // bell-features: audio
    public bool BellAttention = true;              // bell-features: attention
    public bool BellTitle = true;                  // bell-features: title
    public bool BellBorder;                        // bell-features: border
    public string BellAudioPath = string.Empty;    // resolved path; empty: none
    public float BellAudioVolume = 0.5f;           // 0..1
    public bool DebugTap;                         // record the raw VT byte stream
    public bool CrashDumps;                        // ask WER for full crash dumps
    public TerminalTheme TerminalTheme = new();    // resolved terminal palette
    public bool LightTheme;                        // resolved from theme only
    public bool WindowLight;                       // resolved from window-theme

    // ---- typography -------------------------------------------------------

    public List<string> FontFeatures = new();
    public List<FontCodepointMap> FontCodepointMap = new();
    public List<FontVariation> FontVariations = new();
    public List<FontVariation> FontVariationsBold = new();
    public List<FontVariation> FontVariationsItalic = new();
    public List<FontVariation> FontVariationsBoldItalic = new();
    public bool SyntheticBold = true;             // font-synthetic-style: bold
    public bool SyntheticItalic = true;           // font-synthetic-style: italic
    public bool SyntheticBoldItalic = true;       // font-synthetic-style: bold-italic
    public MetricAdjust IconHeightAdjust;
    public bool FontShapingBreak = true;          // false when configured "no-cursor"
    public MetricAdjust CellWidthAdjust;
    public MetricAdjust CellHeightAdjust;
    public MetricAdjust FontBaselineAdjust;
    public MetricAdjust UnderlinePositionAdjust;
    public MetricAdjust UnderlineThicknessAdjust;
    public MetricAdjust StrikethroughPositionAdjust;
    public MetricAdjust StrikethroughThicknessAdjust;
    public MetricAdjust OverlinePositionAdjust;
    public MetricAdjust OverlineThicknessAdjust;
    public MetricAdjust CursorThicknessAdjust;
    public MetricAdjust CursorHeightAdjust;

    // ---- background image ------------------------------------------------

    public float BackgroundOpacity = 1.0f;          // 0..1; window background translucency
    public bool BackgroundOpacityCells;             // translucent explicit cell backgrounds
    public bool BackgroundBlur;                     // DWM acrylic blur behind the window

    /// <summary>
    /// Runtime state, not a config key: true while the window backdrop owns the terminal
    /// background tint, so panes must stay transparent and not apply the opacity again.
    /// </summary>
    public bool SingleLayerTransparency;
    public string BackgroundImage = string.Empty;   // resolved path; empty: none
    public float BackgroundImageOpacity = 1.0f;     // 0..1
    public string BackgroundImagePosition = "center"; // one of the nine anchors
    public string BackgroundImageFit = "contain";   // contain, cover, stretch, none
    public bool BackgroundImageRepeat;

    // ---- colors -----------------------------------------------------------

    public bool HasBoldColor;
    public Rgb BoldColor;
    public float FaintOpacity = 0.5f;             // Ghostty's default
    public bool HasSelectionBackground;
    public Rgb SelectionBackground;
    public bool HasSelectionForeground;
    public Rgb SelectionForeground;
    public SearchColor SearchForeground;
    public SearchColor SearchBackground;
    public SearchColor SearchSelectedForeground;
    public SearchColor SearchSelectedBackground;
    public bool HasCursorText;
    public Rgb CursorText;
    public float CursorOpacity = 1.0f;

    // ---- cursor -----------------------------------------------------------

    public string CursorStyle = string.Empty;      // empty: whatever the program asked for
    public bool? CursorBlink = false;              // cursor-style-blink; null: whatever the program asked for

    // ---- layout -----------------------------------------------------------

    // Ghostty points, not DIPs: PaddingDips converts them at layout time.
    public int PaddingLeft = 2;                    // Ghostty's window-padding defaults
    public int PaddingTop = 2;
    public int PaddingRight = 2;
    public int PaddingBottom = 2;
    public float MouseScrollMultiplier = 3.0f;     // rows per wheel notch; Ghostty's discrete default
    public bool FocusFollowsMouse;

    // ---- clipboard and mouse ---------------------------------------------

    public bool CopyOnSelect;
    public string RightClickAction = "context-menu";
    public string MiddleClickAction = "primary-paste";
    public bool MouseReporting = true;
    public string MouseShiftCapture = "false";
    public bool MouseHideWhileTyping;
    public bool ScrollToBottomKeystroke = true;
    public bool ScrollToBottomOutput;
    public bool LinkUrl = true;
    public bool LinkOsc8 = true;
    public string LinkPreviews = "true";           // parsed; previews are not drawn yet
    public bool ClipboardTrimTrailingSpaces = true;
    public string ClipboardRead = "ask";           // clipboard-read: ask|allow|deny
    public string ClipboardWrite = "allow";        // clipboard-write: ask|allow|deny
    public ulong ClipboardWriteLimitBytes = 64 * 1024 * 1024; // clipboard-write-limit-bytes
    public bool SelectionClearOnCopy;
    public bool SelectionClearOnTyping = true;

    /// <summary>
    /// "selection-word-chars": characters that end a word for double-click selection. The
    /// default is Ghostty's set of common delimiters; whitespace always ends a word.
    /// </summary>
    public string SelectionWordChars = DefaultSelectionWordChars;
    public const string DefaultSelectionWordChars = "\t'\"│`|:;,()[]{}<>$";

    /// <summary>"clipboard-codepoint-map" entries, later entries taking priority.</summary>
    public List<CodepointMap> ClipboardCodepointMap = new();

    // ---- program queries ---------------------------------------------------

    /// <summary>
    /// "title-report": whether CSI 21 t may read the title back. Off by default, like
    /// Ghostty: the reply is written into the shell's own input, so a program could inject
    /// text (or worse) through a crafted title.
    /// </summary>
    public bool TitleReport;

    /// <summary>
    /// "enquiry-response": the bytes sent back when a program asks with ENQ (0x05). Empty,
    /// Ghostty's default, means the terminal stays silent.
    /// </summary>
    public string EnquiryResponse = string.Empty;

    /// <summary>
    /// "osc-color-report-format": the shape of OSC 4/10/11 query replies. The terminal core
    /// always answers with 16-bit components, so "none" and "8-bit" are applied to the reply
    /// bytes before they reach the pty.
    /// </summary>
    public string OscColorReportFormat = ColorReport.SixteenBit;

    /// <summary>
    /// "vt-kam-allowed": whether a program may turn on KAM (ANSI mode 2) to disable
    /// keyboard input. Off by default, like Ghostty; keybindings still run.
    /// </summary>
    public bool VtKamAllowed;

    /// <summary>
    /// "vt-window-resize-allowed": whether a program may resize the window with CSI 8 t.
    /// Off by default, like Ghostty; requests are clamped to 40x10 cells.
    /// </summary>
    public bool VtWindowResizeAllowed;

    /// <summary>
    /// "vt-xt-checksum-extension": xterm's CSBITS for the DECRQCRA checksum, used after a
    /// reset. Bit 1/2/4 turn off negation, video attributes and blank trimming; bit 8
    /// counts undrawn cells as spaces; bit 16 uses full codepoints. Zero is the DEC
    /// calculation, which is Ghostty's default.
    /// </summary>
    public byte XtChecksumExtension;

    /// <summary>
    /// "vt-xt-checksum-report": whether DECRQCRA may read the screen back with a checksum.
    /// Off by default, like Ghostty; while off, XTCHECKSUM (CSI Ps # y) is ignored too.
    /// </summary>
    public bool XtChecksumReport;

    // ---- shell integration and notifications ------------------------------

    public string ShellIntegration = "detect";     // none, detect, or a shell name
    public ShellIntegrationFeatures ShellFeatures = ShellIntegrationFeatures.Default;
    public string NotifyOnCommandFinish = "unfocused"; // never, unfocused, or always
    public bool NotifyOnCommandFinishBell = true;   // notify-on-command-finish-action: bell
    public bool NotifyOnCommandFinishToast = true;  // notify-on-command-finish-action: notify
    public TimeSpan NotifyOnCommandFinishAfter = TimeSpan.FromSeconds(5);

    /// <summary>"progress-style": draw OSC 9;4 progress in the tab bar (default on).</summary>
    public bool ProgressStyle = true;

    /// <summary>
    /// "desktop-notifications": whether the running program may raise a system notification
    /// with OSC 9 or OSC 777. On by default, like Ghostty.
    /// </summary>
    public bool DesktopNotifications = true;

    // ---- process ----------------------------------------------------------

    public List<(string Name, string Value)> ExtraEnvironment = new();
    public string Input = string.Empty;
    public bool WaitAfterCommand;
    public string TermVariable = string.Empty;

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConfigDiagnostic> _diagnostics = new();

    /// <summary>Directory of the file being parsed, used to resolve relative paths.</summary>
    private string _configDirectory = string.Empty;

    /// <summary>
    /// Set when "language" was written with a value this build does not know. The value is
    /// dropped like any other invalid one, but the key was present: resolving it as "absent"
    /// would follow the system language, while an unusable explicit value falls back to
    /// English (with the warning Validate already recorded).
    /// </summary>
    private bool _languageRejected;

    /// <summary>Raw values for every recognized option, in file order.</summary>
    public IReadOnlyDictionary<string, List<string>> Values => _values;

    /// <summary>Options that were unknown, invalid, or had no effect on this platform.</summary>
    public IReadOnlyList<ConfigDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Font size in DIPs for Win2D. "font-size" is in points like Ghostty and WezTerm;
    /// 1pt = 96/72 DIP, independent of the display DPI because both are physical lengths.
    /// </summary>
    public float FontSizeDip => FontSize * (96.0f / 72.0f);

    /// <summary>%APPDATA%\gui\config.</summary>
    public static string ConfigFile()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return string.Empty;
        return Path.Combine(appData, "gui", "config");
    }

    /// <summary>Resolves the path, writes the template when missing and parses it.</summary>
    public static Config Load()
    {
        string file = ConfigFile();
        if (string.IsNullOrEmpty(file)) return new Config();
        EnsureDefault(file);
        return Load(file);
    }

    /// <summary>
    /// Copies freshly parsed values into this instance. The workspace and every pane hold a
    /// reference to this object, so a config reload must update it in place instead of
    /// replacing it.
    /// </summary>
    public void CopyFrom(Config other)
    {
        Command = other.Command;
        Language = other.Language;
        InitialCommand = other.InitialCommand;
        WorkingDirectory = other.WorkingDirectory;
        WindowTitle = other.WindowTitle;
        Maximize = other.Maximize;
        Fullscreen = other.Fullscreen;
        FontFamily = other.FontFamily;
        FontFamilyBold = other.FontFamilyBold;
        FontFamilyItalic = other.FontFamilyItalic;
        FontFamilyBoldItalic = other.FontFamilyBoldItalic;
        FontFamilies = new List<string>(other.FontFamilies);
        FontFamiliesBold = new List<string>(other.FontFamiliesBold);
        FontFamiliesItalic = new List<string>(other.FontFamiliesItalic);
        FontFamiliesBoldItalic = new List<string>(other.FontFamiliesBoldItalic);
        FontStyleName = other.FontStyleName;
        FontStyleBold = other.FontStyleBold;
        FontStyleItalic = other.FontStyleItalic;
        FontStyleBoldItalic = other.FontStyleBoldItalic;
        FontSize = other.FontSize;
        UnfocusedSplitOpacity = other.UnfocusedSplitOpacity;
        ScrollbackLimit = other.ScrollbackLimit;
        ScrollbackLimitLines = other.ScrollbackLimitLines;
        ImageStorageLimit = other.ImageStorageLimit;
        CustomShaders = new List<string>(other.CustomShaders);
        CustomShaderAnimation = other.CustomShaderAnimation;
        Scrollbar = other.Scrollbar;
        HasUnfocusedSplitFill = other.HasUnfocusedSplitFill;
        UnfocusedSplitFill = other.UnfocusedSplitFill;
        HasSplitDividerColor = other.HasSplitDividerColor;
        SplitDividerColor = other.SplitDividerColor;
        TabPosition = other.TabPosition;
        NewTabPosition = other.NewTabPosition;
        ShowTabBar = other.ShowTabBar;
        TabInheritWorkingDirectory = other.TabInheritWorkingDirectory;
        WindowInheritWorkingDirectory = other.WindowInheritWorkingDirectory;
        SplitInheritWorkingDirectory = other.SplitInheritWorkingDirectory;
        DragHandle = other.DragHandle;
        InheritFontSize = other.InheritFontSize;
        Compact = other.Compact;
        SidebarWidthLeft = other.SidebarWidthLeft;
        SidebarWidthRight = other.SidebarWidthRight;
        ThemeMode = other.ThemeMode;
        ThemeLightName = other.ThemeLightName;
        ThemeDarkName = other.ThemeDarkName;
        ThemeValue = other.ThemeValue;
        WindowThemeMode = other.WindowThemeMode;
        TitleFontFamily = other.TitleFontFamily;
        WindowWidth = other.WindowWidth;
        WindowHeight = other.WindowHeight;
        HasWindowPosition = other.HasWindowPosition;
        WindowPositionX = other.WindowPositionX;
        WindowPositionY = other.WindowPositionY;
        WindowSaveState = other.WindowSaveState;
        QuickTerminalPosition = other.QuickTerminalPosition;
        QuickTerminalSize = other.QuickTerminalSize;
        QuickTerminalScreen = other.QuickTerminalScreen;
        QuickTerminalAutohide = other.QuickTerminalAutohide;
        QuickTerminalAnimationDuration = other.QuickTerminalAnimationDuration;
        PaddingBalance = other.PaddingBalance;
        ConfirmCloseSurface = other.ConfirmCloseSurface;
        ClickRepeatInterval = other.ClickRepeatInterval;
        PasteProtection = other.PasteProtection;
        PasteBracketedSafe = other.PasteBracketedSafe;
        ConfigFiles = new List<string>(other.ConfigFiles);
        BellSystem = other.BellSystem;
        BellAudio = other.BellAudio;
        BellAttention = other.BellAttention;
        BellTitle = other.BellTitle;
        BellBorder = other.BellBorder;
        BellAudioPath = other.BellAudioPath;
        BellAudioVolume = other.BellAudioVolume;
        DebugTap = other.DebugTap;
        CrashDumps = other.CrashDumps;
        TerminalTheme = other.TerminalTheme;
        LightTheme = other.LightTheme;
        WindowLight = other.WindowLight;

        FontFeatures = new List<string>(other.FontFeatures);
        FontCodepointMap = new List<FontCodepointMap>(other.FontCodepointMap);
        FontVariations = new List<FontVariation>(other.FontVariations);
        FontVariationsBold = new List<FontVariation>(other.FontVariationsBold);
        FontVariationsItalic = new List<FontVariation>(other.FontVariationsItalic);
        FontVariationsBoldItalic = new List<FontVariation>(other.FontVariationsBoldItalic);
        SyntheticBold = other.SyntheticBold;
        SyntheticItalic = other.SyntheticItalic;
        SyntheticBoldItalic = other.SyntheticBoldItalic;
        IconHeightAdjust = other.IconHeightAdjust;
        FontShapingBreak = other.FontShapingBreak;
        CellWidthAdjust = other.CellWidthAdjust;
        CellHeightAdjust = other.CellHeightAdjust;
        FontBaselineAdjust = other.FontBaselineAdjust;
        UnderlinePositionAdjust = other.UnderlinePositionAdjust;
        UnderlineThicknessAdjust = other.UnderlineThicknessAdjust;
        StrikethroughPositionAdjust = other.StrikethroughPositionAdjust;
        StrikethroughThicknessAdjust = other.StrikethroughThicknessAdjust;
        OverlinePositionAdjust = other.OverlinePositionAdjust;
        OverlineThicknessAdjust = other.OverlineThicknessAdjust;
        CursorThicknessAdjust = other.CursorThicknessAdjust;
        CursorHeightAdjust = other.CursorHeightAdjust;

        BackgroundImage = other.BackgroundImage;
        BackgroundOpacity = other.BackgroundOpacity;
        BackgroundOpacityCells = other.BackgroundOpacityCells;
        BackgroundBlur = other.BackgroundBlur;
        SingleLayerTransparency = other.SingleLayerTransparency;
        BackgroundImageOpacity = other.BackgroundImageOpacity;
        BackgroundImagePosition = other.BackgroundImagePosition;
        BackgroundImageFit = other.BackgroundImageFit;
        BackgroundImageRepeat = other.BackgroundImageRepeat;

        HasBoldColor = other.HasBoldColor;
        BoldColor = other.BoldColor;
        FaintOpacity = other.FaintOpacity;
        HasSelectionBackground = other.HasSelectionBackground;
        SelectionBackground = other.SelectionBackground;
        HasSelectionForeground = other.HasSelectionForeground;
        SelectionForeground = other.SelectionForeground;
        SearchForeground = other.SearchForeground;
        SearchBackground = other.SearchBackground;
        SearchSelectedForeground = other.SearchSelectedForeground;
        SearchSelectedBackground = other.SearchSelectedBackground;
        HasCursorText = other.HasCursorText;
        CursorText = other.CursorText;
        CursorOpacity = other.CursorOpacity;

        CursorStyle = other.CursorStyle;
        CursorBlink = other.CursorBlink;

        PaddingLeft = other.PaddingLeft;
        PaddingTop = other.PaddingTop;
        PaddingRight = other.PaddingRight;
        PaddingBottom = other.PaddingBottom;
        MouseScrollMultiplier = other.MouseScrollMultiplier;
        FocusFollowsMouse = other.FocusFollowsMouse;

        CopyOnSelect = other.CopyOnSelect;
        RightClickAction = other.RightClickAction;
        MiddleClickAction = other.MiddleClickAction;
        MouseReporting = other.MouseReporting;
        MouseShiftCapture = other.MouseShiftCapture;
        MouseHideWhileTyping = other.MouseHideWhileTyping;
        ScrollToBottomKeystroke = other.ScrollToBottomKeystroke;
        ScrollToBottomOutput = other.ScrollToBottomOutput;
        LinkUrl = other.LinkUrl;
        LinkOsc8 = other.LinkOsc8;
        LinkPreviews = other.LinkPreviews;
        ClipboardTrimTrailingSpaces = other.ClipboardTrimTrailingSpaces;
        ClipboardRead = other.ClipboardRead;
        ClipboardWrite = other.ClipboardWrite;
        ClipboardWriteLimitBytes = other.ClipboardWriteLimitBytes;
        SelectionClearOnCopy = other.SelectionClearOnCopy;
        SelectionClearOnTyping = other.SelectionClearOnTyping;
        SelectionWordChars = other.SelectionWordChars;
        ClipboardCodepointMap = new List<CodepointMap>(other.ClipboardCodepointMap);

        TitleReport = other.TitleReport;
        EnquiryResponse = other.EnquiryResponse;
        OscColorReportFormat = other.OscColorReportFormat;
        VtKamAllowed = other.VtKamAllowed;
        VtWindowResizeAllowed = other.VtWindowResizeAllowed;
        XtChecksumExtension = other.XtChecksumExtension;
        XtChecksumReport = other.XtChecksumReport;

        ExtraEnvironment = new List<(string, string)>(other.ExtraEnvironment);
        Input = other.Input;
        WaitAfterCommand = other.WaitAfterCommand;
        TermVariable = other.TermVariable;
        ShellIntegration = other.ShellIntegration;
        ShellFeatures = other.ShellFeatures;
        NotifyOnCommandFinish = other.NotifyOnCommandFinish;
        NotifyOnCommandFinishBell = other.NotifyOnCommandFinishBell;
        NotifyOnCommandFinishToast = other.NotifyOnCommandFinishToast;
        NotifyOnCommandFinishAfter = other.NotifyOnCommandFinishAfter;
        ProgressStyle = other.ProgressStyle;
        DesktopNotifications = other.DesktopNotifications;

        _values.Clear();
        foreach (var pair in other._values)
            _values[pair.Key] = new List<string>(pair.Value);

        _diagnostics.Clear();
        _diagnostics.AddRange(other._diagnostics);
    }

    /// <summary>Parses the file. A missing or unreadable file yields the defaults.</summary>
    public static Config Load(string file)
    {
        var config = new Config();
        bool present;
        try
        {
            present = File.Exists(file);
        }
        catch
        {
            present = false;
        }
        if (!present)
        {
            // Resolve even without a file so the theme and window defaults are populated.
            config.Resolve();
            return config;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        config.LoadFile(file, seen, 0);
        config.Resolve();
        return config;
    }

    /// <summary>
    /// Parses the file and its "config-file" includes without resolving the values into
    /// typed fields. CLI tools that only need the merged raw keys use this, so no theme
    /// lookup, registry access or libghostty-vt call happens outside the app process.
    /// Unlike <see cref="Load()"/> this never writes a template file.
    /// </summary>
    public static Config LoadRaw(string file)
    {
        var config = new Config();
        try
        {
            if (!File.Exists(file)) return config;
        }
        catch
        {
            return config;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        config.LoadFile(file, seen, 0);
        return config;
    }

    /// <summary>
    /// Parses one file, then the "config-file" entries it declared. Ghostty loads the
    /// included files after the entire including file, so their values win. Paths are
    /// relative to the file that names them and a cycle is ignored instead of recursing.
    /// </summary>
    private void LoadFile(string file, HashSet<string> seen, int depth)
    {
        string full;
        try
        {
            full = Path.GetFullPath(file);
        }
        catch
        {
            return;
        }

        if (!File.Exists(full))
        {
            if (depth > 0)
            {
                _diagnostics.Add(new ConfigDiagnostic(0, ConfigSeverity.Warning, "config-file",
                    "file not found: " + full));
            }
            return;
        }

        if (depth > 8 || !seen.Add(full))
        {
            _diagnostics.Add(new ConfigDiagnostic(0, ConfigSeverity.Warning, "config-file",
                "config-file cycle ignored: " + full));
            return;
        }

        string previousDirectory = _configDirectory;
        _configDirectory = Path.GetDirectoryName(full) ?? string.Empty;

        string data;
        try
        {
            data = File.ReadAllText(full, Encoding.UTF8);
        }
        catch
        {
            _configDirectory = previousDirectory;
            seen.Remove(full);
            return;
        }

        var includes = new List<string>();
        Parse(data, includes);
        _configDirectory = previousDirectory;

        foreach (string raw in includes)
        {
            string entry = raw.Trim();
            bool optional = entry.StartsWith("?", StringComparison.Ordinal);
            if (optional) entry = entry[1..];
            if (entry.Length == 0) continue;

            string path = ResolvePath(entry, Path.GetDirectoryName(full) ?? string.Empty);
            if (path.Length == 0) continue;
            if (!File.Exists(path))
            {
                if (!optional)
                {
                    _diagnostics.Add(new ConfigDiagnostic(0, ConfigSeverity.Warning, "config-file",
                        "file not found: " + path));
                }
                continue;
            }

            ConfigFiles.Add(path);
            LoadFile(path, seen, depth + 1);
        }

        seen.Remove(full);
    }

    // ---------------- parsing ----------------

    private void Parse(string data, List<string> includes)
    {
        string[] lines = data.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim(' ', '\t', '\r', '\uFEFF');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            int separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                _diagnostics.Add(new ConfigDiagnostic(i + 1, ConfigSeverity.Warning, string.Empty,
                    "line has no '=' separator"));
                continue;
            }

            string key = trimmed[..separator].Trim(' ', '\t').ToLowerInvariant();
            string value = Unquote(trimmed[(separator + 1)..].Trim(' ', '\t'));
            if (key.Length == 0) continue;

            if (!ConfigSchema.TryGet(key, out ConfigOption option))
            {
                _diagnostics.Add(new ConfigDiagnostic(i + 1, ConfigSeverity.Warning, key,
                    "unknown option"));
                continue;
            }

            // Aliases ("shell", "cwd") resolve to the canonical option name.
            key = option.Name;

            // Path-shaped values are relative to the file that contains them, so they are
            // made absolute while that file's directory is still current.
            if (key is "background-image" or "bell-audio-path")
                value = ResolvePath(value, _configDirectory);
            else if (key == "custom-shader")
                value = ResolveShaderPath(value);

            // "key = """ clears a repeatable list; an empty value for anything else is a no-op.
            if (value.Length == 0)
            {
                _values.Remove(key);
                continue;
            }

            if (!Validate(option, value, i + 1, out string reason))
            {
                _diagnostics.Add(new ConfigDiagnostic(i + 1, ConfigSeverity.Warning, key, reason));
                if (key == "language") _languageRejected = true;
                continue;
            }

            if (key == "config-file") includes.Add(value);

            if (option.Kind == ConfigKind.List)
            {
                if (!_values.TryGetValue(key, out List<string>? list))
                {
                    list = new List<string>();
                    _values[key] = list;
                }
                list.Add(value);
            }
            else
            {
                _values[key] = new List<string> { value };
            }

            if (ConfigSchema.IsForeign(option))
            {
                _diagnostics.Add(new ConfigDiagnostic(i + 1, ConfigSeverity.Notice, key,
                    $"{option.Platform} only; ignored on Windows"));
            }
        }
    }

    private static bool Validate(ConfigOption option, string value, int line, out string reason)
    {
        reason = string.Empty;

        // "language" is the one free-text option with a fixed vocabulary: an empty value
        // falls back to the system language, so anything else has to name English or Chinese.
        if (option.Name == "language")
        {
            if (ParseLanguage(value) is not null) return true;
            reason = "expected en or zh-CN (leave empty to follow the system language)";
            return false;
        }

        switch (option.Kind)
        {
            case ConfigKind.Bool:
                if (TryParseBool(value, out _)) return true;
                reason = "expected true or false";
                return false;

            case ConfigKind.Integer:
                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return true;
                reason = "expected a whole number";
                return false;

            case ConfigKind.Float:
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return true;
                reason = "expected a number";
                return false;

            case ConfigKind.Number:
                if (TryParseAdjust(value, out _)) return true;
                reason = "expected a pixel count or a percentage";
                return false;

            case ConfigKind.Color:
                if (TryParseColor(value, out _)) return true;
                reason = "expected #RRGGBB, #RGB, #RRGGBBAA or a color name";
                return false;

            default:
                return true;
        }
    }

    // ---------------- resolution ----------------

    private void Resolve()
    {
        Command = Last("command") ?? string.Empty;
        // An unset option follows the system language; an explicit value this build does not
        // ship falls back to English (the warning comes from Validate). A rejected value never
        // reaches _values, so it is tracked separately: without that it would look unset and
        // follow the system language instead.
        Language = Last("language") is { } language
            ? ParseLanguage(language) ?? Language.English
            : _languageRejected ? Language.English : DetectSystemLanguage();
        InitialCommand = Last("initial-command") ?? string.Empty;
        WorkingDirectory = Last("working-directory") ?? string.Empty;
        WindowTitle = Last("title") ?? string.Empty;
        Maximize = GetBool("maximize", false);
        Fullscreen = ParseFullscreen(Last("fullscreen"));
        // "font-family" is repeatable: Ghostty uses the first entry as the primary face and
        // every following entry as a fallback for glyphs the earlier ones do not cover.
        FontFamilies = ParseFamilies("font-family");
        FontFamiliesBold = ParseFamilies("font-family-bold");
        FontFamiliesItalic = ParseFamilies("font-family-italic");
        FontFamiliesBoldItalic = ParseFamilies("font-family-bold-italic");
        FontFamily = FontFamilies.Count > 0 ? FontFamilies[0] : string.Empty;
        FontFamilyBold = FontFamiliesBold.Count > 0 ? FontFamiliesBold[0] : string.Empty;
        FontFamilyItalic = FontFamiliesItalic.Count > 0 ? FontFamiliesItalic[0] : string.Empty;
        FontFamilyBoldItalic = FontFamiliesBoldItalic.Count > 0 ? FontFamiliesBoldItalic[0] : string.Empty;
        FontStyleName = Last("font-style") ?? string.Empty;
        FontStyleBold = Last("font-style-bold") ?? string.Empty;
        FontStyleItalic = Last("font-style-italic") ?? string.Empty;
        FontStyleBoldItalic = Last("font-style-bold-italic") ?? string.Empty;
        FontFeatures = ParseFontFeatures(All("font-feature"));
        FontCodepointMap = ParseFontCodepointMaps(All("font-codepoint-map"));
        FontVariations = ParseVariations(All("font-variation"));
        FontVariationsBold = ParseVariations(All("font-variation-bold"));
        FontVariationsItalic = ParseVariations(All("font-variation-italic"));
        FontVariationsBoldItalic = ParseVariations(All("font-variation-bold-italic"));
        ApplySyntheticStyle(All("font-synthetic-style"));

        // Ghostty breaks shaping runs at the cursor by default; "no-cursor" turns that off
        // so ligatures keep forming across the cell the cursor is on.
        string shapingBreak = (Last("font-shaping-break") ?? string.Empty).ToLowerInvariant();
        FontShapingBreak = !shapingBreak.Contains("no-cursor", StringComparison.Ordinal);

        FontSize = Math.Clamp(GetFloat("font-size", FontSize), MinFontSizePoints, MaxFontSizePoints);
        UnfocusedSplitOpacity = Math.Clamp(GetFloat("unfocused-split-opacity", UnfocusedSplitOpacity), 0.15f, 1.0f);
        if (TryGetColor("unfocused-split-fill", out Rgb unfocusedFill))
        {
            HasUnfocusedSplitFill = true;
            UnfocusedSplitFill = unfocusedFill;
        }

        CellWidthAdjust = GetAdjust("adjust-cell-width");
        CellHeightAdjust = GetAdjust("adjust-cell-height");
        FontBaselineAdjust = GetAdjust("adjust-font-baseline");
        UnderlinePositionAdjust = GetAdjust("adjust-underline-position");
        UnderlineThicknessAdjust = GetAdjust("adjust-underline-thickness");
        StrikethroughPositionAdjust = GetAdjust("adjust-strikethrough-position");
        StrikethroughThicknessAdjust = GetAdjust("adjust-strikethrough-thickness");
        OverlinePositionAdjust = GetAdjust("adjust-overline-position");
        OverlineThicknessAdjust = GetAdjust("adjust-overline-thickness");
        CursorThicknessAdjust = GetAdjust("adjust-cursor-thickness");
        CursorHeightAdjust = GetAdjust("adjust-cursor-height");
        IconHeightAdjust = GetAdjust("adjust-icon-height");

        BackgroundImage = ResolveImagePath(Last("background-image") ?? string.Empty);
        BackgroundOpacity = Math.Clamp(GetFloat("background-opacity", BackgroundOpacity), 0.0f, 1.0f);
        BackgroundOpacityCells = GetBool("background-opacity-cells", false);
        BackgroundBlur = ParseBackgroundBlur(Last("background-blur"));
        BackgroundImageOpacity = Math.Clamp(
            GetFloat("background-image-opacity", BackgroundImageOpacity), 0.0f, 1.0f);
        BackgroundImagePosition = NormalizeImagePosition(Last("background-image-position"));
        BackgroundImageFit = NormalizeImageFit(Last("background-image-fit"));
        BackgroundImageRepeat = GetBool("background-image-repeat", false);

        if (TryGetColor("bold-color", out Rgb bold))
        {
            HasBoldColor = true;
            BoldColor = bold;
        }
        FaintOpacity = Math.Clamp(GetFloat("faint-opacity", FaintOpacity), 0.0f, 1.0f);
        if (TryGetColor("selection-background", out Rgb selectionBackground))
        {
            HasSelectionBackground = true;
            SelectionBackground = selectionBackground;
        }
        if (TryGetColor("selection-foreground", out Rgb selectionForeground))
        {
            HasSelectionForeground = true;
            SelectionForeground = selectionForeground;
        }
        SearchForeground = ParseSearchColor(Last("search-foreground"));
        SearchBackground = ParseSearchColor(Last("search-background"));
        SearchSelectedForeground = ParseSearchColor(Last("search-selected-foreground"));
        SearchSelectedBackground = ParseSearchColor(Last("search-selected-background"));
        if (TryGetColor("cursor-text", out Rgb cursorText))
        {
            HasCursorText = true;
            CursorText = cursorText;
        }
        CursorOpacity = Math.Clamp(GetFloat("cursor-opacity", CursorOpacity), 0.0f, 1.0f);

        string cursorStyle = (Last("cursor-style") ?? string.Empty).ToLowerInvariant();
        CursorStyle = cursorStyle switch
        {
            "block" => "block",
            "bar" => "bar",
            "underline" => "underline",
            "block_hollow" => "block_hollow",
            _ => string.Empty,
        };
        if (TryGetBool("cursor-style-blink", out bool blink)) CursorBlink = blink;

        ResolvePadding(Last("window-padding-x"), ref PaddingLeft, ref PaddingRight);
        ResolvePadding(Last("window-padding-y"), ref PaddingTop, ref PaddingBottom);
        MouseScrollMultiplier = Math.Clamp(ParseScrollMultiplier(Last("mouse-scroll-multiplier")), 0.01f, 10000f);
        FocusFollowsMouse = GetBool("focus-follows-mouse", false);

        CopyOnSelect = ParseCopyOnSelect(Last("copy-on-select"));
        RightClickAction = NormalizeRightClick(Last("right-click-action"));
        MiddleClickAction = NormalizeMiddleClick(Last("middle-click-action"));
        MouseReporting = GetBool("mouse-reporting", true);
        MouseShiftCapture = NormalizeShiftCapture(Last("mouse-shift-capture"));
        MouseHideWhileTyping = GetBool("mouse-hide-while-typing", false);
        (ScrollToBottomKeystroke, ScrollToBottomOutput) = ParseScrollToBottom(Last("scroll-to-bottom"));
        LinkUrl = GetBool("link-url", true);
        LinkOsc8 = GetBool("link-osc8", true);
        LinkPreviews = (Last("link-previews") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "false" => "false",
            "osc8" => "osc8",
            _ => "true",
        };
        ClipboardTrimTrailingSpaces = GetBool("clipboard-trim-trailing-spaces", true);
        ClipboardRead = ParseClipboardAccess(Last("clipboard-read"), ClipboardRead);
        ClipboardWrite = ParseClipboardAccess(Last("clipboard-write"), ClipboardWrite);
        ClipboardWriteLimitBytes = ParseLimit(Last("clipboard-write-limit-bytes"), ClipboardWriteLimitBytes);
        SelectionClearOnCopy = GetBool("selection-clear-on-copy", false);
        SelectionClearOnTyping = GetBool("selection-clear-on-typing", true);
        TitleReport = GetBool("title-report", false);
        EnquiryResponse = Last("enquiry-response") ?? string.Empty;
        OscColorReportFormat = ColorReport.Normalize(Last("osc-color-report-format"));
        VtKamAllowed = GetBool("vt-kam-allowed", false);
        VtWindowResizeAllowed = GetBool("vt-window-resize-allowed", false);
        XtChecksumExtension = ParseXtChecksumExtension(Last("vt-xt-checksum-extension"));
        XtChecksumReport = GetBool("vt-xt-checksum-report", false);
        SelectionWordChars = Last("selection-word-chars") ?? DefaultSelectionWordChars;
        ClipboardCodepointMap = ParseCodepointMaps(All("clipboard-codepoint-map"));
        PasteProtection = GetBool("clipboard-paste-protection", true);
        PasteBracketedSafe = GetBool("clipboard-paste-bracketed-safe", true);
        ClickRepeatInterval = Math.Clamp(GetInt("click-repeat-interval", 0), 0, 10000);
        ConfirmCloseSurface = NormalizeConfirmClose(Last("confirm-close-surface"));

        TitleFontFamily = (Last("window-title-font-family") ?? string.Empty).Trim();
        PaddingBalance = GetBool("window-padding-balance", false);

        // "window-width"/"window-height" are grid cells and only apply as a pair.
        int windowWidth = GetInt("window-width", 0);
        int windowHeight = GetInt("window-height", 0);
        if (windowWidth >= 10 && windowHeight >= 4)
        {
            WindowWidth = windowWidth;
            WindowHeight = windowHeight;
        }

        // "window-position-x"/"window-position-y" are pixels and also apply as a pair.
        bool hasX = TryGetInt("window-position-x", out int positionX);
        bool hasY = TryGetInt("window-position-y", out int positionY);
        if (hasX && hasY)
        {
            HasWindowPosition = true;
            WindowPositionX = positionX;
            WindowPositionY = positionY;
        }

        // Windows has no system-provided save policy to defer to, so Ghostty's "default"
        // (and anything unrecognized) behaves like "always": quit and reopen restores the
        // session. Only "never" opts out.
        WindowSaveState =
            (Last("window-save-state") ?? string.Empty).Trim().ToLowerInvariant() == "never"
                ? "never"
                : "always";

        // The quick terminal is a drop-down window toggled by a global hotkey. Its edge and
        // size follow Ghostty's keys; the size string is parsed where the geometry is worked
        // out, so an unusable value quietly keeps the default instead of failing the load.
        QuickTerminalPosition = QuickTerminalGeometry.NormalizePosition(Last("quick-terminal-position"));
        QuickTerminalSize = (Last("quick-terminal-size") ?? string.Empty).Trim();
        QuickTerminalScreen =
            (Last("quick-terminal-screen") ?? string.Empty).Trim().ToLowerInvariant() == "main"
                ? "main"
                : "mouse";
        QuickTerminalAutohide = GetBool("quick-terminal-autohide", true);
        QuickTerminalAnimationDuration = ParseSeconds(Last("quick-terminal-animation-duration"), 0.2);

        ApplyBellFeatures(Last("bell-features"));
        BellAudioPath = ResolveImagePath(Last("bell-audio-path") ?? string.Empty);
        BellAudioVolume = Math.Clamp(GetFloat("bell-audio-volume", 0.5f), 0.0f, 1.0f);

        ExtraEnvironment = ParseEnvironment(All("env"));
        Input = string.Concat(All("input"));
        WaitAfterCommand = GetBool("wait-after-command", false);
        TermVariable = Last("term") ?? string.Empty;
        ShellIntegration = NormalizeShellIntegration(Last("shell-integration"));
        ApplyShellFeatures(Last("shell-integration-features"));
        NotifyOnCommandFinish = NormalizeNotifyMode(Last("notify-on-command-finish"));
        (NotifyOnCommandFinishBell, NotifyOnCommandFinishToast) =
            ParseNotifyAction(Last("notify-on-command-finish-action"));
        NotifyOnCommandFinishAfter =
            ParseDuration(Last("notify-on-command-finish-after"), TimeSpan.FromSeconds(5));
        ProgressStyle = GetBool("progress-style", true);
        DesktopNotifications = GetBool("desktop-notifications", true);

        TabPosition = (Last("tab-position") ?? string.Empty).ToLowerInvariant();
        NewTabPosition =
            (Last("window-new-tab-position") ?? string.Empty).Trim().ToLowerInvariant() == "end"
                ? "end"
                : "current";
        ShowTabBar = (Last("window-show-tab-bar") ?? string.Empty).Trim().ToLowerInvariant() != "never";
        TabInheritWorkingDirectory = GetBool("tab-inherit-working-directory", true);
        WindowInheritWorkingDirectory = GetBool("window-inherit-working-directory", true);
        SplitInheritWorkingDirectory = GetBool("split-inherit-working-directory", true);
        InheritFontSize = GetBool("window-inherit-font-size", true);
        if (TryGetColor("split-divider-color", out Rgb splitDivider))
        {
            HasSplitDividerColor = true;
            SplitDividerColor = splitDivider;
        }
        // Automatic is the default and the only value that is not spelled out; anything
        // unrecognized falls back to it, like Ghostty's enum parsing.
        DragHandle = (Last("drag-handle") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "always" => "always",
            "never" => "never",
            _ => "auto",
        };
        Compact = GetBool("compact", true);
        SidebarWidthLeft = ParseSidebarWidth(Last("sidebar-width-left"), SidebarWidthLeft);
        SidebarWidthRight = ParseSidebarWidth(Last("sidebar-width-right"), SidebarWidthRight);
        ScrollbackLimit = ParseLimit(Last("scrollback-limit-bytes"), ScrollbackLimit);
        ScrollbackLimitLines = ParseLimit(Last("scrollback-limit-lines"), ScrollbackLimitLines);
        ImageStorageLimit = (uint)Math.Min(
            ParseLimit(Last("image-storage-limit"), ImageStorageLimit), uint.MaxValue);
        CustomShaders = ResolveShaders(All("custom-shader"));
        CustomShaderAnimation = ParseShaderAnimation(Last("custom-shader-animation"));
        Scrollbar = (Last("scrollbar") ?? string.Empty).Trim().ToLowerInvariant() == "never"
            ? "never"
            : "system";
        DebugTap = GetBool("debug-tap", false);
        CrashDumps = GetBool("crash-dumps", false);

        if (WorkingDirectory.Length == 0)
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        ResolveWindowTheme();
        ResolveTheme();
    }

    /// <summary>
    /// "theme" is a comma-separated list whose entries may carry a "light:" or "dark:"
    /// prefix; an unprefixed name applies to both appearances. The side selected by
    /// "window-theme" is the one painted. An absent key means
    /// "light:Gui Light,dark:Gui Dark", and an omitted side keeps that side's default.
    /// </summary>
    private void ResolveTheme()
    {
        string raw = (Last("theme") ?? string.Empty).Trim();
        (string? lightName, string? darkName) = ParseThemeValue(raw);

        ThemeLightName = lightName ?? LightThemeName;
        ThemeDarkName = darkName ?? DarkThemeName;
        ThemeValue = "light:" + ThemeLightName + ",dark:" + ThemeDarkName;

        TerminalTheme = ResolveThemeFor(WindowLight ? ThemeLightName : ThemeDarkName,
            out bool light, out string canonical);
        LightTheme = light;
        ThemeMode = canonical;
    }

    /// <summary>Splits "light:X,dark:Y"; an unprefixed entry applies to both sides.</summary>
    public static (string? Light, string? Dark) ParseThemeValue(string raw)
    {
        string? light = null;
        string? dark = null;
        foreach (string part in raw.Split(','))
        {
            string entry = part.Trim();
            if (entry.Length == 0) continue;

            if (entry.StartsWith("light:", StringComparison.OrdinalIgnoreCase))
            {
                string name = entry[6..].Trim();
                if (name.Length > 0) light = name;
            }
            else if (entry.StartsWith("dark:", StringComparison.OrdinalIgnoreCase))
            {
                string name = entry[5..].Trim();
                if (name.Length > 0) dark = name;
            }
            else
            {
                light = entry;
                dark = entry;
            }
        }
        return (light, dark);
    }

    /// <summary>Resolves one appearance's theme; the palette previews use this too.</summary>
    public void ResolveThemeSide(bool light, out TerminalTheme theme, out bool isLight, out string name)
    {
        theme = ResolveThemeFor(light ? ThemeLightName : ThemeDarkName, out isLight, out name);
    }

    /// <summary>The canonical "theme" value with one appearance's theme replaced.</summary>
    public string WithThemeSide(bool light, string name) =>
        "light:" + (light ? name : ThemeLightName) + ",dark:" + (light ? ThemeDarkName : name);

    /// <summary>Replaces the runtime "theme" value and re-resolves both sides.</summary>
    public void SetTheme(string value)
    {
        _values["theme"] = new List<string> { value };
        ResolveTheme();
    }

    /// <summary>
    /// Builds a palette from one theme name and layers the explicit
    /// background/foreground/cursor/palette keys on top, like Ghostty's precedence.
    /// </summary>
    private TerminalTheme ResolveThemeFor(string name, out bool light, out string canonical)
    {
        name = name.Trim();
        canonical = name;

        string key = name.ToLowerInvariant();
        bool designLight = key is "light" or "gui light";
        bool designDark = key is "dark" or "gui dark";
        light = designLight;

        TerminalTheme theme;
        if (name.Length > 0 && !designLight && !designDark)
        {
            if (TerminalThemes.Load(name, out TerminalTheme loaded))
            {
                theme = loaded;
                light = TerminalThemes.IsLight(loaded.Background);
            }
            else
            {
                // Unknown name: the design palette for this appearance is the fallback.
                canonical = WindowLight ? LightThemeName : DarkThemeName;
                light = WindowLight;
                theme = TerminalTheme.CreateDesign(light);
            }
        }
        else
        {
            if (designLight) canonical = LightThemeName;
            else if (designDark) canonical = DarkThemeName;
            theme = TerminalTheme.CreateDesign(light);
        }

        bool haveBackground = TryGetColor("background", out Rgb explicitBackground);
        bool haveForeground = TryGetColor("foreground", out Rgb explicitForeground);
        bool haveCursor = TryGetColor("cursor-color", out Rgb cursor);
        var paletteEntries = All("palette");

        if (!haveBackground && !haveForeground && !haveCursor && paletteEntries.Count == 0)
            return theme;

        var palette = new Rgb[256];
        Array.Copy(theme.Design ? TerminalTheme.DesignPalette(light) : theme.Palette, palette, 256);

        Rgb background = haveBackground ? explicitBackground : theme.Background;
        Rgb foreground = haveForeground ? explicitForeground : theme.Foreground;

        if (haveCursor)
        {
            theme.CursorSet = true;
            theme.Cursor = cursor;
        }

        var paletteMask = new GhosttyNative.ColorPaletteMask();
        foreach (string entry in paletteEntries)
        {
            int split = entry.IndexOf('=');
            if (split < 0) continue;
            if (!int.TryParse(entry[..split].Trim(), out int index) || index < 0 || index > 255) continue;
            if (!TryParseColor(entry[(split + 1)..], out Rgb color)) continue;
            palette[index] = color;
            paletteMask.Set(index);
        }

        // palette-generate fills 16-255 from the base 16; without it the configured
        // entries are used verbatim and every other slot keeps its previous value.
        if (GetBool("palette-generate", false))
        {
            var generated = new Rgb[256];
            GhosttyNative.PaletteGenerate(palette, paletteMask, background, foreground,
                GetBool("palette-harmonious", false), generated);
            Array.Copy(generated, theme.Palette, 256);
        }
        else
        {
            Array.Copy(palette, theme.Palette, 256);
        }

        theme.Background = background;
        theme.Foreground = foreground;
        theme.Custom = true;
        theme.Design = false;
        light = TerminalThemes.IsLight(background);
        return theme;
    }

    /// <summary>
    /// window-theme: window decorations are independent of the terminal theme.
    /// "system" (default) follows the OS appearance; "light" / "dark" force one.
    /// </summary>
    private void ResolveWindowTheme()
    {
        WindowThemeMode = (Last("window-theme") ?? string.Empty).ToLowerInvariant();
        if (WindowThemeMode == "light")
        {
            WindowLight = true;
        }
        else if (WindowThemeMode == "dark")
        {
            WindowLight = false;
        }
        else
        {
            WindowThemeMode = "system";
            WindowLight = SystemUsesLightTheme();
        }
    }

    // ---------------- typed access ----------------

    public string? Last(string key) =>
        _values.TryGetValue(key, out List<string>? list) && list.Count > 0 ? list[^1] : null;

    public List<string> All(string key) =>
        _values.TryGetValue(key, out List<string>? list) ? list : new List<string>();

    public bool GetBool(string key, bool fallback) =>
        TryGetBool(key, out bool value) ? value : fallback;

    public bool TryGetBool(string key, out bool value)
    {
        value = false;
        return _values.TryGetValue(key, out List<string>? list) && list.Count > 0 &&
               TryParseBool(list[^1], out value);
    }

    public int GetInt(string key, int fallback) =>
        _values.TryGetValue(key, out List<string>? list) && list.Count > 0 &&
        int.TryParse(list[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    public bool TryGetInt(string key, out int value)
    {
        value = 0;
        return _values.TryGetValue(key, out List<string>? list) && list.Count > 0 &&
               int.TryParse(list[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    public float GetFloat(string key, float fallback) =>
        _values.TryGetValue(key, out List<string>? list) && list.Count > 0 &&
        float.TryParse(list[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
            ? value
            : fallback;

    public bool TryGetColor(string key, out Rgb color)
    {
        color = default;
        return _values.TryGetValue(key, out List<string>? list) && list.Count > 0 &&
               TryParseColor(list[^1], out color);
    }

    public MetricAdjust GetAdjust(string key)
    {
        if (_values.TryGetValue(key, out List<string>? list) && list.Count > 0 &&
            TryParseAdjust(list[^1], out MetricAdjust adjust))
        {
            return adjust;
        }
        return default;
    }

    // ---------------- value shapes ----------------

    /// <summary>
    /// "language" names the UI language explicitly: "zh-CN" selects the Chinese translation
    /// and "en" selects English. Null means the option was not set or is not one of those
    /// two spellings; <see cref="Resolve"/> decides between the system language and the
    /// English fallback for the two cases.
    /// </summary>
    private static Language? ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string text = value.Trim().ToLowerInvariant();
        if (text == "zh-cn") return Language.Chinese;
        if (text == "en") return Language.English;
        return null;
    }

    /// <summary>
    /// The language used when "language" is not configured: Simplified Chinese gets the
    /// Chinese translation, every other system language stays English.
    /// </summary>
    public static Language DetectSystemLanguage()
    {
        CultureInfo culture;
        try
        {
            culture = CultureInfo.CurrentUICulture;
            if (string.IsNullOrEmpty(culture.Name)) culture = CultureInfo.InstalledUICulture;
        }
        catch
        {
            return Language.English;
        }

        return IsSimplifiedChinese(culture.Name) ? Language.Chinese : Language.English;
    }

    /// <summary>
    /// True for Simplified Chinese cultures (zh, zh-CN, zh-SG, zh-Hans) and false for the
    /// Traditional variants (zh-TW, zh-HK, zh-MO, zh-Hant), which this build does not ship.
    /// </summary>
    public static bool IsSimplifiedChinese(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName)) return false;

        string name = cultureName.Trim().ToLowerInvariant();
        if (!name.StartsWith("zh", StringComparison.Ordinal)) return false;

        if (name.Contains("hant", StringComparison.Ordinal)) return false;
        return !name.Contains("-tw", StringComparison.Ordinal) &&
               !name.Contains("-hk", StringComparison.Ordinal) &&
               !name.Contains("-mo", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a background image path. "~" expands to the user profile and a relative
    /// path is taken from the config file's directory, so a config can sit next to its
    /// images. An unusable path yields an empty string, which means "no image".
    /// </summary>
    private string ResolveImagePath(string value)
    {
        string directory = _configDirectory.Length > 0
            ? _configDirectory
            : Path.GetDirectoryName(ConfigFile()) ?? string.Empty;
        return ResolvePath(value, directory);
    }

    /// <summary>
    /// Expands "~" and makes a relative path absolute against <paramref name="directory"/>.
    /// Returns an empty string when the value cannot be turned into a path.
    /// </summary>
    private static string ResolvePath(string value, string directory)
    {
        string path = value.Trim();
        if (path.Length == 0) return string.Empty;

        try
        {
            if (path.Length > 1 && path[0] == '~' && (path[1] == '\\' || path[1] == '/'))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            }

            if (!Path.IsPathRooted(path) && directory.Length > 0)
                path = Path.Combine(directory, path);

            return Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// "custom-shader" paths are made absolute while the including file's directory is
    /// current. "?" keeps the entry optional without becoming part of the filename.
    /// </summary>
    private string ResolveShaderPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        bool optional = value.StartsWith("?", StringComparison.Ordinal);
        string path = optional ? value[1..] : value;
        string resolved = ResolvePath(path, _configDirectory);
        return optional ? "?" + resolved : resolved;
    }

    /// <summary>
    /// "custom-shader" is repeatable. A leading "?" makes a missing file optional; any other
    /// missing file is reported so a typo is visible in the diagnostics.
    /// </summary>
    private List<string> ResolveShaders(IEnumerable<string> values)
    {
        var paths = new List<string>();
        foreach (string raw in values)
        {
            string entry = raw.Trim();
            bool optional = entry.StartsWith("?", StringComparison.Ordinal);
            if (optional) entry = entry[1..];
            if (entry.Length == 0) continue;

            string path = Path.IsPathRooted(entry) ? entry : ResolveImagePath(entry);
            if (path.Length == 0) continue;
            if (!File.Exists(path))
            {
                if (!optional)
                {
                    // Shader files are compiled after config loading, like Ghostty: a missing
                    // file is a notice here, not a config warning.
                    _diagnostics.Add(new ConfigDiagnostic(0, ConfigSeverity.Notice,
                        "custom-shader", "file not found: " + path));
                }
                continue;
            }

            paths.Add(path);
        }
        return paths;
    }

    private static string ParseShaderAnimation(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "false" or "off" or "never" => "false",
            "always" => "always",
            _ => "true",
        };

    /// <summary>The nine Ghostty position anchors; anything else falls back to center.</summary>
    private static string NormalizeImagePosition(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "top-left" => "top-left",
            "top-center" => "top-center",
            "top-right" => "top-right",
            "center-left" => "center-left",
            "center" => "center",
            "center-right" => "center-right",
            "bottom-left" => "bottom-left",
            "bottom-center" => "bottom-center",
            "bottom-right" => "bottom-right",
            _ => "center",
        };
    }

    private static string NormalizeImageFit(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "contain" => "contain",
            "cover" => "cover",
            "stretch" => "stretch",
            "none" => "none",
            _ => "contain",
        };
    }

    /// <summary>
    /// "background-blur" accepts a boolean or a radius. Windows has no adjustable radius,
    /// so any enabled value maps to the DWM acrylic backdrop; 0 / false turn it off.
    /// </summary>
    private static bool ParseBackgroundBlur(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        string text = value.Trim().ToLowerInvariant();
        if (text is "true" or "on" or "yes") return true;
        if (text is "false" or "off" or "no") return false;

        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
            return radius > 0f;

        return true;
    }

    private static bool TryParseBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "on" or "1":
                result = true;
                return true;
            case "false" or "no" or "off" or "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryParseAdjust(string value, out MetricAdjust adjust)
    {
        adjust = default;
        string text = value.Trim();
        if (text.Length == 0) return false;

        bool percent = text[^1] == '%';
        if (percent) text = text[..^1];

        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float amount))
            return false;

        adjust = new MetricAdjust(percent, amount);
        return true;
    }

    private static readonly Dictionary<string, Rgb> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = new(0, 0, 0),
        ["silver"] = new(192, 192, 192),
        ["gray"] = new(128, 128, 128),
        ["gray50"] = new(127, 127, 127),
        ["white"] = new(255, 255, 255),
        ["maroon"] = new(128, 0, 0),
        ["red"] = new(255, 0, 0),
        ["green"] = new(0, 128, 0),
        ["lime"] = new(0, 255, 0),
        ["olive"] = new(128, 128, 0),
        ["yellow"] = new(255, 255, 0),
        ["navy"] = new(0, 0, 128),
        ["blue"] = new(0, 0, 255),
        ["purple"] = new(128, 0, 128),
        ["magenta"] = new(255, 0, 255),
        ["fuchsia"] = new(255, 0, 255),
        ["teal"] = new(0, 128, 128),
        ["aqua"] = new(0, 255, 255),
        ["cyan"] = new(0, 255, 255),
        ["orange"] = new(255, 165, 0),
        ["pink"] = new(255, 192, 203),
        ["brown"] = new(165, 42, 42),
        ["gold"] = new(255, 215, 0),
        ["indigo"] = new(75, 0, 130),
        ["violet"] = new(238, 130, 238),
        ["darkgray"] = new(169, 169, 169),
        ["lightgray"] = new(211, 211, 211),
        ["whitesmoke"] = new(245, 245, 245),
        ["transparent"] = new(0, 0, 0),
    };

    /// <summary>Parses the #RGB / #RRGGBB / #RRGGBBAA forms used by Ghostty plus X11 names.</summary>
    public static bool TryParseColor(string value, out Rgb color)
    {
        string text = value.Trim();
        if (text.Length > 0 && text[0] == '#') return TerminalThemes.ParseColor(text, out color);
        return NamedColors.TryGetValue(text, out color);
    }

    private static float ParseScrollMultiplier(string? value)
    {
        // Ghostty's shape is { precision = 1, discrete = 3 }; a bare number sets both.
        // The Windows pointer is a discrete wheel, so the discrete value is the one used.
        if (string.IsNullOrWhiteSpace(value)) return 3.0f;

        float discrete = 3.0f;
        foreach (string part in value.Split(','))
        {
            string text = part.Trim();
            if (text.Length == 0) continue;

            int colon = text.IndexOf(':');
            if (colon < 0)
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float all))
                    discrete = all;
                continue;
            }

            string kind = text[..colon].Trim().ToLowerInvariant();
            if (kind != "discrete") continue;
            if (float.TryParse(text[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                discrete = number;
        }

        return discrete;
    }

    private static bool ParseCopyOnSelect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        // Ghostty 1.4: none / primary / clipboard / both, with true/false as aliases.
        // Windows has no selection clipboard, so "both" means the system clipboard.
        string text = value.Trim().ToLowerInvariant();
        return text is "true" or "yes" or "on" or "1" or "clipboard" or "both";
    }

    /// <summary>
    /// "fullscreen" accepts false/true plus the macOS non-native variants; Windows always
    /// uses the native fullscreen presenter, so every non-false value means "on".
    /// </summary>
    private static bool ParseFullscreen(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        string text = value.Trim().ToLowerInvariant();
        if (text is "false" or "no" or "off" or "0") return false;
        return text is "true" or "yes" or "on" or "1" ||
               text.StartsWith("non-native", StringComparison.Ordinal);
    }

    /// <summary>
    /// "search-*" colors accept a color, "cell-foreground" or "cell-background". An
    /// unset or invalid value falls back to Ghostty's golden/peach default.
    /// </summary>
    private static SearchColor ParseSearchColor(string? value)
    {
        string text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return default;

        switch (text.ToLowerInvariant())
        {
            case "cell-foreground":
                return new SearchColor(SearchColorKind.CellForeground, default);
            case "cell-background":
                return new SearchColor(SearchColorKind.CellBackground, default);
        }

        return TryParseColor(text, out Rgb color)
            ? new SearchColor(SearchColorKind.Explicit, color)
            : default;
    }

    private static string NormalizeMiddleClick(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "clipboard-paste" => "clipboard-paste",
            "ignore" => "ignore",
            _ => "primary-paste",
        };
    }

    /// <summary>"confirm-close-surface": true (default), false, or always.</summary>
    private static string NormalizeConfirmClose(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "false" => "false",
            "always" => "always",
            _ => "true",
        };
    }

    /// <summary>
    /// "shell-integration": none, detect, or a shell name. This build injects into
    /// PowerShell and bash; the other Unix shell names are accepted but cannot be honored.
    /// </summary>
    private string NormalizeShellIntegration(string? value)
    {
        string text = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (text)
        {
            case "":
            case "detect":
            case "auto":
            case "true":
                return "detect";
            case "none":
            case "false":
                return "none";
            case "pwsh":
            case "powershell":
                return "powershell";
            case "bash":
                return "bash";
            case "elvish":
            case "fish":
            case "nushell":
            case "zsh":
                _diagnostics.Add(new ConfigDiagnostic(0, ConfigSeverity.Notice, "shell-integration",
                    text + " integration is not available on Windows"));
                return text;
            default:
                return "detect";
        }
    }

    /// <summary>
    /// "shell-integration-features" is a comma/space separated list with "no-" negations.
    /// "true" enables every feature and "false" disables every feature, like Ghostty.
    /// </summary>
    private void ApplyShellFeatures(string? value) => ShellFeatures = ParseShellFeatures(value);

    /// <summary>
    /// The same parse for callers that only hold the value, like the settings page, which
    /// has to turn what the user typed into flags without reloading the file.
    /// </summary>
    public static ShellIntegrationFeatures ParseShellFeatures(string? value)
    {
        ShellIntegrationFeatures features = ShellIntegrationFeatures.Default;
        if (string.IsNullOrWhiteSpace(value)) return features;

        foreach (string raw in value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToLowerInvariant();
            if (token == "true")
            {
                features = features with
                {
                    Cursor = true, Title = true, Sudo = true,
                    SshEnv = true, SshTerminfo = true, Path = true,
                };
                continue;
            }
            if (token == "false")
            {
                features = features with
                {
                    Cursor = false, CursorSteady = false, Title = false,
                    Sudo = false, SshEnv = false, SshTerminfo = false, Path = false,
                };
                continue;
            }

            bool enable = true;
            if (token.StartsWith("no-", StringComparison.Ordinal))
            {
                enable = false;
                token = token[3..];
            }

            // "cursor:steady" asks for a steady bar instead of a blinking one.
            bool steady = token.Contains(":steady", StringComparison.Ordinal);
            int colon = token.IndexOf(':');
            if (colon >= 0) token = token[..colon];

            switch (token)
            {
                case "cursor":
                    features = features with { Cursor = enable, CursorSteady = enable && steady };
                    break;
                case "title": features = features with { Title = enable }; break;
                case "sudo": features = features with { Sudo = enable }; break;
                case "ssh-env": features = features with { SshEnv = enable }; break;
                case "ssh-terminfo": features = features with { SshTerminfo = enable }; break;
                case "path": features = features with { Path = enable }; break;
            }
        }

        return features;
    }

    /// <summary>"notify-on-command-finish": unfocused (default), always, or never.</summary>
    private static string NormalizeNotifyMode(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "always" => "always",
            "never" => "never",
            _ => "unfocused",
        };

    /// <summary>
    /// "notify-on-command-finish-action": a comma separated list of bell/notify with
    /// "no-" negations. Both are on by default.
    /// </summary>
    private static (bool Bell, bool Toast) ParseNotifyAction(string? value)
    {
        bool bell = true;
        bool toast = true;
        if (string.IsNullOrWhiteSpace(value)) return (bell, toast);

        foreach (string raw in value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToLowerInvariant();
            bool enable = true;
            if (token.StartsWith("no-", StringComparison.Ordinal))
            {
                enable = false;
                token = token[3..];
            }

            switch (token)
            {
                case "bell": bell = enable; break;
                case "notify": toast = enable; break;
            }
        }

        return (bell, toast);
    }

    /// <summary>
    /// "notify-on-command-finish-after" durations: numbers with unit suffixes, added
    /// together ("1h30m", "45s", "500ms"). An unparsable value keeps the default.
    /// </summary>
    private static TimeSpan ParseDuration(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;

        double seconds = 0;
        bool any = false;
        int i = 0;
        while (i < value.Length)
        {
            char ch = value[i];
            if (char.IsWhiteSpace(ch) || ch == ',')
            {
                i++;
                continue;
            }
            if (!char.IsDigit(ch) && ch != '.')
            {
                i++;
                continue;
            }

            int numberStart = i;
            while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.')) i++;
            if (!double.TryParse(value[numberStart..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                break;

            while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
            int unitStart = i;
            while (i < value.Length && char.IsLetter(value[i])) i++;
            string unit = value[unitStart..i].ToLowerInvariant();

            double factor = unit switch
            {
                "ns" => 1e-9,
                "us" or "\u00b5s" or "\u03bcs" => 1e-6,
                "ms" => 1e-3,
                "s" => 1,
                "m" => 60,
                "h" => 3600,
                "d" => 86400,
                "y" => 31536000,
                _ => double.NaN,
            };
            if (double.IsNaN(factor)) continue;

            seconds += number * factor;
            any = true;
        }

        if (!any) return fallback;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, TimeSpan.MaxValue.TotalSeconds));
    }

    /// <summary>
    /// "bell-features" is a comma separated list of features with "no-" negations.
    /// Ghostty's defaults are attention and title on, audio/system/border off.
    /// </summary>
    private void ApplyBellFeatures(string? value)
    {
        BellSystem = false;
        BellAudio = false;
        BellAttention = true;
        BellTitle = true;
        BellBorder = false;

        if (string.IsNullOrWhiteSpace(value)) return;

        foreach (string raw in value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string token = raw.Trim().ToLowerInvariant();
            bool enable = true;
            if (token.StartsWith("no-", StringComparison.Ordinal))
            {
                enable = false;
                token = token[3..];
            }

            switch (token)
            {
                case "system": BellSystem = enable; break;
                case "audio": BellAudio = enable; break;
                case "attention": BellAttention = enable; break;
                case "title": BellTitle = enable; break;
                case "border": BellBorder = enable; break;
            }
        }
    }

    /// <summary>
    /// "vt-xt-checksum-extension" is a comma separated list of negate / attributes / trim /
    /// undrawn / full with "no-" negations. Ghostty's default is the DEC calculation:
    /// negate, attributes and trim on, undrawn and full off. The result is xterm's CSBITS,
    /// which turn off the default parts, so the defaults map to zero.
    /// </summary>
    private static byte ParseXtChecksumExtension(string? value)
    {
        bool negate = true;
        bool attributes = true;
        bool trim = true;
        bool undrawn = false;
        bool full = false;

        if (!string.IsNullOrWhiteSpace(value))
        {
            foreach (string raw in value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim().ToLowerInvariant();
                bool enable = true;
                if (token.StartsWith("no-", StringComparison.Ordinal))
                {
                    enable = false;
                    token = token[3..];
                }

                switch (token)
                {
                    case "negate": negate = enable; break;
                    case "attributes": attributes = enable; break;
                    case "trim": trim = enable; break;
                    case "undrawn": undrawn = enable; break;
                    case "full": full = enable; break;
                }
            }
        }

        return (byte)((!negate ? 1 : 0) | (!attributes ? 2 : 0) | (!trim ? 4 : 0) |
                      (undrawn ? 8 : 0) | (full ? 16 : 0));
    }

    private static string NormalizeShiftCapture(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "true" => "true",
            "always" => "always",
            "never" => "never",
            _ => "false",
        };
    }

    /// <summary>
    /// "scroll-to-bottom" is a set of tokens; the default is `keystroke, no-output`.
    /// Accepts keystroke / no-keystroke / output / no-output.
    /// </summary>
    private static (bool Keystroke, bool Output) ParseScrollToBottom(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (true, false);

        bool keystroke = true;
        bool output = false;
        foreach (string raw in value.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "keystroke":
                    keystroke = true;
                    break;
                case "no-keystroke":
                    keystroke = false;
                    break;
                case "output":
                    output = true;
                    break;
                case "no-output":
                    output = false;
                    break;
            }
        }
        return (keystroke, output);
    }

    private static string NormalizeRightClick(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "context-menu" => "context-menu",
            "paste" => "paste",
            "copy" => "copy",
            "copy-or-paste" => "copy-or-paste",
            "ignore" => "ignore",
            _ => "context-menu",
        };
    }

    /// <summary>
    /// Parses "clipboard-codepoint-map" entries: "U+2500=U+002D" replaces one codepoint,
    /// "U+2500-U+257F=U+002D" a range, and the right-hand side may also be literal text.
    /// </summary>
    private static List<CodepointMap> ParseCodepointMaps(List<string> entries)
    {
        var result = new List<CodepointMap>();
        foreach (string entry in entries)
        {
            int equals = entry.IndexOf('=');
            if (equals <= 0) continue;

            string rangeText = entry[..equals].Trim();
            string replacementText = entry[(equals + 1)..].Trim();
            if (rangeText.Length == 0 || replacementText.Length == 0) continue;

            string replacement = TryParseCodepoint(replacementText, out int mapped)
                ? char.ConvertFromUtf32(mapped)
                : replacementText;

            int dash = rangeText.IndexOf('-');
            if (dash < 0)
            {
                if (TryParseCodepoint(rangeText, out int single))
                    result.Add(new CodepointMap(single, single, replacement));
                continue;
            }

            if (TryParseCodepoint(rangeText[..dash], out int start) &&
                TryParseCodepoint(rangeText[(dash + 1)..], out int end))
            {
                result.Add(new CodepointMap(Math.Min(start, end), Math.Max(start, end), replacement));
            }
        }
        return result;
    }

    /// <summary>
    /// Parses "font-family" entries in order: the first is the primary face, the rest are
    /// fallbacks consulted per glyph. Blank entries are dropped so a cleared list stays empty.
    /// </summary>
    private List<string> ParseFamilies(string key)
    {
        var result = new List<string>();
        foreach (string entry in All(key))
        {
            string family = entry.Trim();
            if (family.Length == 0) continue;
            if (result.Exists(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(family);
        }
        return result;
    }

    /// <summary>
    /// Parses "font-codepoint-map" entries: "U+2500=Consolas" forces one codepoint,
    /// "U+E000-U+F8FF=Symbols Nerd Font" a whole range, to be drawn with the named family
    /// instead of the configured monospace face.
    /// </summary>
    private static List<FontCodepointMap> ParseFontCodepointMaps(List<string> entries)
    {
        var result = new List<FontCodepointMap>();
        foreach (string entry in entries)
        {
            int equals = entry.IndexOf('=');
            if (equals <= 0) continue;

            string rangeText = entry[..equals].Trim();
            string family = entry[(equals + 1)..].Trim();
            if (rangeText.Length == 0 || family.Length == 0) continue;

            int dash = rangeText.IndexOf('-');
            if (dash < 0)
            {
                if (TryParseCodepoint(rangeText, out int single))
                    result.Add(new FontCodepointMap(single, single, family));
                continue;
            }

            if (TryParseCodepoint(rangeText[..dash], out int start) &&
                TryParseCodepoint(rangeText[(dash + 1)..], out int end))
            {
                result.Add(new FontCodepointMap(Math.Min(start, end), Math.Max(start, end), family));
            }
        }
        return result;
    }

    /// <summary>
    /// "font-feature" is repeatable, and one line may list several features separated by
    /// commas ("ss01, -calt") — which is the form the settings page writes back when the
    /// field is committed.
    /// </summary>
    public static List<string> ParseFontFeatures(IEnumerable<string> entries)
    {
        var result = new List<string>();
        foreach (string entry in entries)
        {
            foreach (string part in entry.Split(','))
            {
                string feature = part.Trim();
                if (feature.Length > 0) result.Add(feature);
            }
        }
        return result;
    }

    /// <summary>
    /// Parses "font-variation" entries: each one is "axis=value", and a single line may
    /// carry several axes separated by commas ("wght=600,opsz=12").
    /// </summary>
    private static List<FontVariation> ParseVariations(List<string> entries)
    {
        var result = new List<FontVariation>();
        foreach (string entry in entries)
        {
            foreach (string part in entry.Split(','))
            {
                int equals = part.IndexOf('=');
                if (equals <= 0) continue;

                string axis = part[..equals].Trim();
                if (axis.Length == 0) continue;

                if (!float.TryParse(part[(equals + 1)..].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float value))
                {
                    continue;
                }

                result.Add(new FontVariation(axis.ToLowerInvariant(), value));
            }
        }
        return result;
    }

    /// <summary>
    /// The variation axes for one face. A bold/italic face with no axes of its own inherits
    /// "font-variation", which is Ghostty's documented fallback.
    /// </summary>
    public List<FontVariation> VariationsFor(bool bold, bool italic)
    {
        List<FontVariation> specific = (bold, italic) switch
        {
            (true, false) => FontVariationsBold,
            (false, true) => FontVariationsItalic,
            (true, true) => FontVariationsBoldItalic,
            _ => FontVariations,
        };

        if (specific.Count > 0 || !bold && !italic) return specific;
        return FontVariations;
    }

    /// <summary>
    /// "font-synthetic-style" is a set of tokens applied in order: "bold" / "italic" /
    /// "bold-italic" allow synthesized faces, the "no-" forms forbid them. The default is
    /// all three enabled, as in Ghostty.
    /// </summary>
    private void ApplySyntheticStyle(List<string> entries)
    {
        SyntheticBold = true;
        SyntheticItalic = true;
        SyntheticBoldItalic = true;

        foreach (string entry in entries)
        {
            foreach (string raw in entry.Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (raw.Trim().ToLowerInvariant())
                {
                    case "bold":
                        SyntheticBold = true;
                        break;
                    case "no-bold":
                        SyntheticBold = false;
                        break;
                    case "italic":
                        SyntheticItalic = true;
                        break;
                    case "no-italic":
                        SyntheticItalic = false;
                        break;
                    case "bold-italic":
                        SyntheticBoldItalic = true;
                        break;
                    case "no-bold-italic":
                        SyntheticBoldItalic = false;
                        break;
                }
            }
        }
    }

    private static bool TryParseCodepoint(string text, out int codepoint)
    {
        string value = text.Trim();
        if (value.StartsWith("U+", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }
        else
        {
            // A bare hexadecimal number is not a codepoint in Ghostty's syntax, but a
            // literal replacement ("SUM") must not be swallowed either.
            codepoint = 0;
            return false;
        }

        return int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codepoint) &&
               codepoint >= 0 && codepoint <= 0x10FFFF;
    }

    /// <summary>Rewrites copied text through the configured codepoint map.</summary>
    internal string ApplyClipboardCodepointMap(string text)
    {
        if (ClipboardCodepointMap.Count == 0) return text;

        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            int length = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? 2 : 1;
            int codepoint = length == 2 ? char.ConvertToUtf32(text[i], text[i + 1]) : text[i];

            string? replacement = null;
            // Later entries take priority over earlier ones for overlapping ranges.
            for (int index = ClipboardCodepointMap.Count - 1; index >= 0; index--)
            {
                CodepointMap map = ClipboardCodepointMap[index];
                if (codepoint >= map.Start && codepoint <= map.End)
                {
                    replacement = map.Replacement;
                    break;
                }
            }

            if (replacement is null) builder.Append(text, i, length);
            else builder.Append(replacement);
            i += length;
        }
        return builder.ToString();
    }

    private static List<(string Name, string Value)> ParseEnvironment(List<string> entries)
    {
        var result = new List<(string, string)>();
        foreach (string entry in entries)
        {
            int equals = entry.IndexOf('=');
            if (equals <= 0) continue;
            result.Add((entry[..equals].Trim(), entry[(equals + 1)..]));
        }
        return result;
    }

    // ---------------- persistence ----------------

    /// <summary>
    /// Rewrites one "key = value" line in place (appending it when missing) so a
    /// runtime setting change survives the next start.
    /// </summary>
    public static void SaveValue(string key, string value) => SaveValue(ConfigFile(), key, value);

    /// <summary>The same, against an explicit file so tests do not touch the user's config.</summary>
    public static void SaveValue(string file, string key, string value)
    {
        if (string.IsNullOrEmpty(file)) return;
        EnsureDefault(file);

        // The file belongs to the user: comments, blank lines and their order all survive,
        // and only the lines that set this key are rewritten. The newline style is the one
        // the file already uses, so a save never mixes endings.
        var lines = new List<string>();
        string newline = "\n";
        try
        {
            string text = File.ReadAllText(file, Encoding.UTF8);
            if (text.Contains("\r\n", StringComparison.Ordinal)) newline = "\r\n";
            foreach (string raw in text.Split('\n')) lines.Add(raw.TrimEnd('\r'));
        }
        catch
        {
            // Treat an unreadable file as empty; the value is appended below.
        }

        string wanted = key.ToLowerInvariant();
        string replacement = key + " = " + value;
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim(' ', '\t');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            int separator = trimmed.IndexOf('=');
            if (separator < 0) continue;
            if (trimmed[..separator].Trim(' ', '\t').ToLowerInvariant() != wanted) continue;

            lines[i] = replacement;
            replaced = true;
        }
        if (!replaced)
        {
            // Append after the last setting, so a trailing blank line stays trailing.
            int insert = lines.Count;
            while (insert > 0 && lines[insert - 1].Trim().Length == 0) insert--;
            lines.Insert(insert, replacement);
        }

        try
        {
            File.WriteAllText(file, string.Join(newline, lines), new UTF8Encoding(false));
        }
        catch
        {
            // A failed save must not take the app down.
        }
    }

    /// <summary>
    /// Creates an empty config file when one does not exist yet. Every key falls back to its
    /// default until the user sets it, so the file needs no commented template.
    /// </summary>
    public static void EnsureDefault(string file)
    {
        if (File.Exists(file)) return;

        try
        {
            string? directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        }
        catch
        {
            return;
        }

        const string template = "";

        try
        {
            File.WriteAllText(file, template, new UTF8Encoding(false));
        }
        catch
        {
            // Discovery of the config file is best effort.
        }
    }

    /// <summary>Windows' app light/dark setting, used by "window-theme = system".</summary>
    public static bool SystemUsesLightTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            return value is not int number || number != 0;
        }
        catch
        {
            return true;
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            return value;

        // Quoted values support the escapes a shell command or font name needs. An
        // unrecognized escape keeps its backslash so Windows paths survive untouched.
        string inner = value[1..^1];
        var builder = new StringBuilder(inner.Length);
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(inner[i]);
                continue;
            }

            char next = inner[i + 1];
            switch (next)
            {
                case '\\': builder.Append('\\'); i++; break;
                case '"': builder.Append('"'); i++; break;
                case 'n': builder.Append('\n'); i++; break;
                case 't': builder.Append('\t'); i++; break;
                case 'r': builder.Append('\r'); i++; break;
                default: builder.Append('\\'); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// A byte or row budget: a base-0 count or the literal "unlimited". Anything ghostty's
    /// grammar does not accept keeps the default.
    /// </summary>
    private static ulong ParseLimit(string? value, ulong fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (value.Trim().Equals("unlimited", StringComparison.OrdinalIgnoreCase)) return ulong.MaxValue;
        return TryParseCount(value, out ulong count) ? count : fallback;
    }

    /// <summary>
    /// "window-padding-x/y" is one pixel count for both sides or "left,right" /
    /// "top,bottom", matching Ghostty's WindowPadding grammar.
    /// </summary>
    private static void ResolvePadding(string? value, ref int first, ref int second)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        string[] parts = value.Split(',');
        if (!TryParsePadding(parts[0], out int a)) return;

        int b = a;
        if (parts.Length > 1 && !TryParsePadding(parts[1], out b)) return;

        first = a;
        second = b;
    }

    private static bool TryParsePadding(string text, out int value) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
        value >= 0;

    /// <summary>
    /// "clipboard-read" / "clipboard-write": ask (the default for reads), allow, or deny.
    /// Anything unknown keeps the fallback so a typo cannot widen access.
    /// </summary>
    private static string ParseClipboardAccess(string? value, string fallback)
    {
        string text = (value ?? string.Empty).Trim().ToLowerInvariant();
        return text is "allow" or "deny" or "ask" ? text : fallback;
    }

    /// <summary>
    /// Converts a window-padding value from Ghostty's points into the DIPs WinUI lays out in.
    /// Ghostty scales with <c>floor(points * dpi / 72)</c> (src/Surface.zig scaledPadding);
    /// a DIP is 1/96 inch, so <c>dpi = 96 * scale</c> and the resulting physical pixel count
    /// is divided back by the scale. <paramref name="scale"/> is XamlRoot.RasterizationScale.
    /// </summary>
    internal static double PaddingDips(int points, double scale)
    {
        if (scale <= 0.0) scale = 1.0;
        double dpi = 96.0 * scale;
        return Math.Floor(points * dpi / 72.0) / scale;
    }

    /// <summary>A duration in seconds, clamped to something a slide animation can use.</summary>
    private static double ParseSeconds(string? value, double fallback)
    {
        if (!double.TryParse((value ?? string.Empty).Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double seconds))
            return fallback;
        if (double.IsNaN(seconds) || seconds < 0) return fallback;
        return Math.Min(seconds, 5.0);
    }

    /// <summary>Sidebar widths keep a usable range; out-of-range values keep the default.</summary>
    private static double ParseSidebarWidth(string? value, double fallback)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ||
            width < 120.0 || width > 800.0)
        {
            return fallback;
        }
        return width;
    }

    /// <summary>
    /// Parses a count the way ghostty's config reader does: a bare integer with an optional
    /// `0x` / `0o` / `0b` prefix (ghostty parses with base 0). Unit suffixes such as `10MB`
    /// are not part of that grammar and are rejected.
    /// </summary>
    internal static bool TryParseCount(string value, out ulong count)
    {
        count = 0;
        string text = value.Trim();
        if (text.Length == 0) return false;

        int radix = 10;
        if (text.Length > 2 && text[0] == '0')
        {
            radix = char.ToLowerInvariant(text[1]) switch
            {
                'x' => 16,
                'o' => 8,
                'b' => 2,
                _ => 10,
            };
            if (radix != 10) text = text[2..];
        }

        if (text.Length == 0) return false;
        foreach (char c in text)
        {
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix) return false;
            if (count > (ulong.MaxValue - (ulong)digit) / (ulong)radix) return false;
            count = count * (ulong)radix + (ulong)digit;
        }
        return true;
    }
}
