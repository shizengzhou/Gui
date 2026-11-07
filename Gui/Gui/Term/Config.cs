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

    public string Command = string.Empty;          // empty: auto-detect pwsh/powershell/cmd
    public Language Language = Language.English;   // "language"; English is the default
    public string InitialCommand = string.Empty;   // empty: same as Command
    public string WorkingDirectory = string.Empty; // empty: %USERPROFILE%
    public string FontFamily = string.Empty;       // empty: JetBrainsMono Nerd Font
    public string FontFamilyBold = string.Empty;   // empty: FontFamily
    public string FontFamilyItalic = string.Empty; // empty: FontFamily
    public string FontFamilyBoldItalic = string.Empty; // empty: FontFamilyBold or FontFamily
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
    public string Scrollbar = "system";            // "system" (default) or "never"
    public bool HasUnfocusedSplitFill;
    public Rgb UnfocusedSplitFill;                 // empty: terminal background
    public string TabPosition = string.Empty;      // "top" (default), "left", "right"
    public bool Compact;                           // true: panes run edge to edge
    public double SidebarWidthLeft = 240.0;        // DIPs; restored width of a left rail
    public double SidebarWidthRight = 240.0;       // DIPs; restored width of a right rail
    public string ThemeMode = string.Empty;        // empty, Gui Light, Gui Dark, or a bundled theme name
    public string WindowThemeMode = string.Empty;  // "system" (default), "light", "dark"
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
    public bool HasCursorText;
    public Rgb CursorText;
    public float CursorOpacity = 1.0f;

    // ---- cursor -----------------------------------------------------------

    public string CursorStyle = string.Empty;      // empty: whatever the program asked for
    public bool? CursorBlink;                      // null: whatever the program asked for

    // ---- layout -----------------------------------------------------------

    public int PaddingLeft = 2;                    // DIPs; Ghostty's window-padding defaults
    public int PaddingTop = 2;
    public int PaddingRight = 2;
    public int PaddingBottom = 2;
    public float MouseScrollMultiplier = 3.0f;     // rows per wheel notch; Ghostty's discrete default
    public bool FocusFollowsMouse;

    // ---- clipboard and mouse ---------------------------------------------

    public bool CopyOnSelect;
    public string RightClickAction = "context-menu";
    public bool ClipboardTrimTrailingSpaces = true;
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

    // ---- process ----------------------------------------------------------

    public List<(string Name, string Value)> ExtraEnvironment = new();
    public string Input = string.Empty;
    public bool WaitAfterCommand;
    public string TermVariable = string.Empty;

    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ConfigDiagnostic> _diagnostics = new();

    /// <summary>Directory of the file being parsed, used to resolve relative paths.</summary>
    private string _configDirectory = string.Empty;

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
        FontFamily = other.FontFamily;
        FontFamilyBold = other.FontFamilyBold;
        FontFamilyItalic = other.FontFamilyItalic;
        FontFamilyBoldItalic = other.FontFamilyBoldItalic;
        FontStyleName = other.FontStyleName;
        FontStyleBold = other.FontStyleBold;
        FontStyleItalic = other.FontStyleItalic;
        FontStyleBoldItalic = other.FontStyleBoldItalic;
        FontSize = other.FontSize;
        UnfocusedSplitOpacity = other.UnfocusedSplitOpacity;
        ScrollbackLimit = other.ScrollbackLimit;
        ScrollbackLimitLines = other.ScrollbackLimitLines;
        Scrollbar = other.Scrollbar;
        HasUnfocusedSplitFill = other.HasUnfocusedSplitFill;
        UnfocusedSplitFill = other.UnfocusedSplitFill;
        TabPosition = other.TabPosition;
        Compact = other.Compact;
        SidebarWidthLeft = other.SidebarWidthLeft;
        SidebarWidthRight = other.SidebarWidthRight;
        ThemeMode = other.ThemeMode;
        WindowThemeMode = other.WindowThemeMode;
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
        ClipboardTrimTrailingSpaces = other.ClipboardTrimTrailingSpaces;
        SelectionClearOnCopy = other.SelectionClearOnCopy;
        SelectionClearOnTyping = other.SelectionClearOnTyping;
        SelectionWordChars = other.SelectionWordChars;
        ClipboardCodepointMap = new List<CodepointMap>(other.ClipboardCodepointMap);

        ExtraEnvironment = new List<(string, string)>(other.ExtraEnvironment);
        Input = other.Input;
        WaitAfterCommand = other.WaitAfterCommand;
        TermVariable = other.TermVariable;

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
        string data;
        try
        {
            config._configDirectory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? string.Empty;
            if (!File.Exists(file)) return config;
            data = File.ReadAllText(file, Encoding.UTF8);
        }
        catch
        {
            return config;
        }

        config.Parse(data);
        config.Resolve();
        return config;
    }

    // ---------------- parsing ----------------

    private void Parse(string data)
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

            // "key = """ clears a repeatable list; an empty value for anything else is a no-op.
            if (value.Length == 0)
            {
                _values.Remove(key);
                continue;
            }

            if (!Validate(option, value, i + 1, out string reason))
            {
                _diagnostics.Add(new ConfigDiagnostic(i + 1, ConfigSeverity.Warning, key, reason));
                continue;
            }

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
        Language = ParseLanguage(Last("language")) ?? DetectSystemLanguage();
        InitialCommand = Last("initial-command") ?? string.Empty;
        WorkingDirectory = Last("working-directory") ?? string.Empty;
        FontFamily = Last("font-family") ?? string.Empty;
        FontFamilyBold = Last("font-family-bold") ?? string.Empty;
        FontFamilyItalic = Last("font-family-italic") ?? string.Empty;
        FontFamilyBoldItalic = Last("font-family-bold-italic") ?? string.Empty;
        FontStyleName = Last("font-style") ?? string.Empty;
        FontStyleBold = Last("font-style-bold") ?? string.Empty;
        FontStyleItalic = Last("font-style-italic") ?? string.Empty;
        FontStyleBoldItalic = Last("font-style-bold-italic") ?? string.Empty;
        FontFeatures = All("font-feature");
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
        ClipboardTrimTrailingSpaces = GetBool("clipboard-trim-trailing-spaces", true);
        SelectionClearOnCopy = GetBool("selection-clear-on-copy", false);
        SelectionClearOnTyping = GetBool("selection-clear-on-typing", true);
        SelectionWordChars = Last("selection-word-chars") ?? DefaultSelectionWordChars;
        ClipboardCodepointMap = ParseCodepointMaps(All("clipboard-codepoint-map"));

        ExtraEnvironment = ParseEnvironment(All("env"));
        Input = string.Concat(All("input"));
        WaitAfterCommand = GetBool("wait-after-command", false);
        TermVariable = Last("term") ?? string.Empty;

        TabPosition = (Last("tab-position") ?? string.Empty).ToLowerInvariant();
        Compact = GetBool("compact", false);
        SidebarWidthLeft = ParseSidebarWidth(Last("sidebar-width-left"), SidebarWidthLeft);
        SidebarWidthRight = ParseSidebarWidth(Last("sidebar-width-right"), SidebarWidthRight);
        ScrollbackLimit = ParseLimit(Last("scrollback-limit-bytes"), ScrollbackLimit);
        ScrollbackLimitLines = ParseLimit(Last("scrollback-limit-lines"), ScrollbackLimitLines);
        Scrollbar = (Last("scrollbar") ?? string.Empty).Trim().ToLowerInvariant() == "never"
            ? "never"
            : "system";
        DebugTap = GetBool("debug-tap", false);
        CrashDumps = GetBool("crash-dumps", false);

        if (WorkingDirectory.Length == 0)
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        ResolveTheme();
        ResolveWindowTheme();
    }

    /// <summary>
    /// "theme" controls the terminal palette only. Empty, "Gui Dark" or "Gui Light" pick the
    /// design's own palettes; anything else is a bundled Ghostty theme name
    /// (Assets/Themes/light|dark). The old short names are still accepted.
    /// </summary>
    private void ResolveTheme()
    {
        string themeName = (Last("theme") ?? string.Empty).Trim();
        ThemeMode = themeName;
        string themeKey = themeName.ToLowerInvariant();
        bool designLight = themeKey is "light" or "gui light";
        bool designDark = themeKey is "dark" or "gui dark";

        LightTheme = designLight;
        if (themeName.Length > 0 && !designLight && !designDark)
        {
            if (TerminalThemes.Load(themeName, out TerminalTheme loaded))
            {
                TerminalTheme = loaded;
                LightTheme = TerminalThemes.IsLight(loaded.Background);
            }
        }

        // Explicit keys layer on top of the theme, exactly like Ghostty's precedence.
        bool custom = TerminalTheme.Custom;
        bool haveBackground = TryGetColor("background", out Rgb explicitBackground);
        bool haveForeground = TryGetColor("foreground", out Rgb explicitForeground);
        bool haveCursor = TryGetColor("cursor-color", out Rgb cursor);
        var paletteEntries = All("palette");

        if (!haveBackground && !haveForeground && !haveCursor && paletteEntries.Count == 0)
        {
            // Nothing overrides the theme: no palette work is needed at all.
            if (designLight) ThemeMode = LightThemeName;
            else if (designDark) ThemeMode = DarkThemeName;
            return;
        }

        var palette = new Rgb[256];
        if (custom) Array.Copy(TerminalTheme.Palette, palette, 256);
        else GhosttyNative.PaletteDefault(palette);

        Rgb background = haveBackground ? explicitBackground
            : custom ? TerminalTheme.Background : new Rgb(7, 9, 14);
        Rgb foreground = haveForeground ? explicitForeground
            : custom ? TerminalTheme.Foreground : new Rgb(245, 245, 247);

        if (haveCursor)
        {
            TerminalTheme.CursorSet = true;
            TerminalTheme.Cursor = cursor;
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
            Array.Copy(generated, TerminalTheme.Palette, 256);
        }
        else
        {
            Array.Copy(palette, TerminalTheme.Palette, 256);
        }

        TerminalTheme.Background = background;
        TerminalTheme.Foreground = foreground;
        TerminalTheme.Custom = true;
        LightTheme = TerminalThemes.IsLight(background);

        // The built-in themes carry their full name, so the value in the config file and the
        // name shown in the UI are the same string.
        if (designLight) ThemeMode = LightThemeName;
        else if (designDark) ThemeMode = DarkThemeName;
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

        // The window brightness constrains the usable themes: a light window only
        // accepts light themes and a dark window only dark ones. A mismatching custom
        // theme falls back to the default Gui Light / Gui Dark palette.
        if (LightTheme != WindowLight)
        {
            ThemeMode = WindowLight ? LightThemeName : DarkThemeName;
            LightTheme = WindowLight;
            TerminalTheme = new TerminalTheme();
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
    /// "language" takes a BCP-47 style tag. Chinese is the only translation this build
    /// ships, so every other tag falls back to English. Null means the option was not set,
    /// in which case the system language decides.
    /// </summary>
    private static Language? ParseLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        string text = value.Trim().ToLowerInvariant();
        if (text.StartsWith("zh", StringComparison.Ordinal) || text is "chinese" or "中文" or "简体中文")
            return Language.Chinese;
        return Language.English;
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
        string path = value.Trim();
        if (path.Length == 0) return string.Empty;

        try
        {
            if (path.Length > 1 && path[0] == '~' && (path[1] == '\\' || path[1] == '/'))
            {
                path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            }

            if (!Path.IsPathRooted(path))
            {
                string directory = _configDirectory.Length > 0
                    ? _configDirectory
                    : Path.GetDirectoryName(ConfigFile()) ?? string.Empty;
                if (directory.Length > 0) path = Path.Combine(directory, path);
            }

            return Path.GetFullPath(path);
        }
        catch
        {
            return string.Empty;
        }
    }

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

        var lines = new List<string>();
        try
        {
            lines.AddRange(File.ReadAllText(file, Encoding.UTF8).Split('\n'));
        }
        catch
        {
            // Treat an unreadable file as empty; the value is appended below.
        }

        string wanted = key.ToLowerInvariant();
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim(' ', '\t', '\r');
            // Commented template lines must not count as the active key.
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            int separator = trimmed.IndexOf('=');
            if (separator < 0) continue;
            if (trimmed[..separator].Trim(' ', '\t').ToLowerInvariant() == wanted)
            {
                lines[i] = key + " = " + value;
                replaced = true;
            }
        }
        if (!replaced) lines.Add(key + " = " + value);

        try
        {
            File.WriteAllText(file, string.Join("\n", lines), new UTF8Encoding(false));
        }
        catch
        {
            // A failed save must not take the app down.
        }
    }

    /// <summary>Writes a commented template when the file does not exist yet.</summary>
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

        const string template =
            "# Gui terminal configuration\n" +
            "#\n" +
            "# One 'key = value' per line; '#' starts a comment - same shape as ghostty.\n" +
            "# Every ghostty.org/docs/config/reference option is accepted; options this\n" +
            "# build cannot honor are reported in gui-startup.log.\n" +
            "#\n" +
            "# command            shell to run; default pwsh.exe, then powershell.exe, then %COMSPEC%\n" +
            "# language           UI language; default follows the system (Simplified\n" +
            "#                    Chinese -> zh-CN, otherwise English); needs a restart\n" +
            "# working-directory  directory the shell starts in; default is your user profile\n" +
            "# font-family        monospace font family; default JetBrainsMono Nerd Font\n" +
            "# font-size          font size in points; default 12 (about 16 DIPs)\n" +
            "# unfocused-split-opacity  default 0.7; 0.15 (very dim) to 1 (off)\n" +
            "# unfocused-split-fill     empty (default; terminal background) or #RRGGBB\n" +
            "# window-padding-x/y  padding in points; one value for both sides or\n" +
            "#                    'left,right' / 'top,bottom'; default 2\n" +
            "# scrollback-limit-bytes  scrollback budget in bytes, or 'unlimited'; default\n" +
            "#                    50000000 (50 MB), 0 disables scrollback; the old\n" +
            "#                    scrollback-limit name is still accepted\n" +
            "# scrollback-limit-lines  row cap, or 'unlimited' (default); the byte cap and\n" +
            "#                    this one both apply, whichever is reached first trims\n" +
            "# scrollbar          system (default; only while there is scrollback) or never\n" +
            "# tab-position       top (default), left or right\n" +
            "# background-image   PNG/JPEG path; relative paths resolve next to this file\n" +
            "# background-opacity window background opacity; 1 (default) is opaque\n" +
            "# background-opacity-cells  true also fades explicit cell backgrounds\n" +
            "# background-blur    false (default) or true for DWM acrylic blur behind the\n" +
            "#                    window when background-opacity is below 1\n" +
            "# compact            false (default; panes float as islands) or true (panes run\n" +
            "#                    edge to edge with 1px split dividers)\n" +
            "# sidebar-width-left   left rail width in DIPs; default 240\n" +
            "# sidebar-width-right  right rail width in DIPs; default 240\n" +
            "# theme              empty (default), Gui Dark, Gui Light, or a built-in theme\n" +
            "#                    name such as Dracula or Catppuccin Mocha\n" +
            "# window-theme       system (default), light or dark; window decorations\n" +
            "#                    independently of the terminal theme\n" +
            "# debug-tap          true to record the raw VT byte stream into the log\n" +
            "# crash-dumps        true to ask Windows for a full crash dump\n" +
            "\n" +
            "# command = pwsh.exe\n" +
            "# working-directory = C:\\Users\\you\n" +
            "# font-family = JetBrainsMono Nerd Font\n" +
            "# font-size = 13\n" +
            "# unfocused-split-opacity = 0.7\n" +
            "# unfocused-split-fill = #21252B\n" +
            "# window-padding-x = 2,4\n" +
            "# window-padding-y = 2\n" +
            "# scrollback-limit-bytes = 50000000\n" +
            "# scrollback-limit-lines = unlimited\n" +
            "# scrollbar = system\n" +
            "# compact = false\n" +
            "# sidebar-width-left = 240\n" +
            "# sidebar-width-right = 240\n" +
            "# theme = Gui Dark\n" +
            "# background-image = C:\\images\\background.png\n" +
            "# background-image-fit = contain\n" +
            "# background-opacity = 0.9\n" +
            "# window-theme = system\n" +
            "# debug-tap = false\n" +
            "# crash-dumps = false\n";

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
