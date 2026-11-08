using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>Value shape of a configuration option.</summary>
public enum ConfigKind
{
    Bool,
    Integer,
    Float,

    /// <summary>A pixel count or a percentage, e.g. "1" or "-15%".</summary>
    Number,
    Color,
    Text,
    Enum,

    /// <summary>Repeatable: every occurrence adds an entry instead of replacing the previous one.</summary>
    List,
}

/// <summary>Runtime that owns an option upstream. A Windows build ignores macOS and Linux only options.</summary>
public enum ConfigPlatform
{
    All,
    MacOS,
    Linux,
}

public readonly record struct ConfigOption(string Name, ConfigKind Kind, ConfigPlatform Platform, string Summary);

/// <summary>
/// The Ghostty configuration surface, mirroring ghostty.org/docs/config/reference. Parsing
/// accepts every option listed here; <see cref="Config"/> then applies the ones this app has
/// an equivalent for and records the rest as "recognized, not applied".
/// </summary>
public static class ConfigSchema
{
    public static readonly ConfigOption[] Ghostty =
    {
        new("language", ConfigKind.Text, ConfigPlatform.All, "GUI language"),
        new("font-family", ConfigKind.List, ConfigPlatform.All, "font families, repeated for fallbacks"),
        new("font-family-bold", ConfigKind.List, ConfigPlatform.All, "bold font families"),
        new("font-family-italic", ConfigKind.List, ConfigPlatform.All, "italic font families"),
        new("font-family-bold-italic", ConfigKind.List, ConfigPlatform.All, "bold italic font families"),
        new("font-style", ConfigKind.Text, ConfigPlatform.All, "font face style name"),
        new("font-style-bold", ConfigKind.Text, ConfigPlatform.All, "bold face style name"),
        new("font-style-italic", ConfigKind.Text, ConfigPlatform.All, "italic face style name"),
        new("font-style-bold-italic", ConfigKind.Text, ConfigPlatform.All, "bold italic face style name"),
        new("font-synthetic-style", ConfigKind.List, ConfigPlatform.All, "synthesize missing bold/italic"),
        new("font-feature", ConfigKind.List, ConfigPlatform.All, "OpenType features"),
        new("font-size", ConfigKind.Float, ConfigPlatform.All, "font size in points"),
        new("font-variation", ConfigKind.List, ConfigPlatform.All, "variable font axes"),
        new("font-variation-bold", ConfigKind.List, ConfigPlatform.All, "bold variable font axes"),
        new("font-variation-italic", ConfigKind.List, ConfigPlatform.All, "italic variable font axes"),
        new("font-variation-bold-italic", ConfigKind.List, ConfigPlatform.All, "bold italic variable font axes"),
        new("font-codepoint-map", ConfigKind.List, ConfigPlatform.All, "map codepoints to a font"),
        new("clipboard-codepoint-map", ConfigKind.List, ConfigPlatform.All, "rewrite codepoints on copy"),
        new("clipboard-write-limit-bytes", ConfigKind.Text, ConfigPlatform.All, "OSC 52 write size limit"),
        new("font-thicken", ConfigKind.Bool, ConfigPlatform.MacOS, "thicken font strokes"),
        new("font-thicken-strength", ConfigKind.Integer, ConfigPlatform.MacOS, "thickening strength 0-255"),
        new("font-shaping-break", ConfigKind.Text, ConfigPlatform.All, "where to break shaping runs"),
        new("alpha-blending", ConfigKind.Enum, ConfigPlatform.All, "alpha blending color space"),
        new("adjust-cell-width", ConfigKind.Number, ConfigPlatform.All, "cell width adjustment"),
        new("adjust-cell-height", ConfigKind.Number, ConfigPlatform.All, "cell height adjustment"),
        new("adjust-font-baseline", ConfigKind.Number, ConfigPlatform.All, "baseline adjustment"),
        new("adjust-underline-position", ConfigKind.Number, ConfigPlatform.All, "underline position"),
        new("adjust-underline-thickness", ConfigKind.Number, ConfigPlatform.All, "underline thickness"),
        new("adjust-strikethrough-position", ConfigKind.Number, ConfigPlatform.All, "strikethrough position"),
        new("adjust-strikethrough-thickness", ConfigKind.Number, ConfigPlatform.All, "strikethrough thickness"),
        new("adjust-overline-position", ConfigKind.Number, ConfigPlatform.All, "overline position"),
        new("adjust-overline-thickness", ConfigKind.Number, ConfigPlatform.All, "overline thickness"),
        new("adjust-cursor-thickness", ConfigKind.Number, ConfigPlatform.All, "bar cursor thickness"),
        new("adjust-cursor-height", ConfigKind.Number, ConfigPlatform.All, "cursor height"),
        new("adjust-box-thickness", ConfigKind.Number, ConfigPlatform.All, "box drawing thickness"),
        new("adjust-icon-height", ConfigKind.Number, ConfigPlatform.All, "nerd font icon height"),
        new("grapheme-width-method", ConfigKind.Enum, ConfigPlatform.All, "grapheme width method"),
        new("freetype-load-flags", ConfigKind.Text, ConfigPlatform.Linux, "FreeType load flags"),
        new("theme", ConfigKind.Text, ConfigPlatform.All, "theme name or path"),
        new("background", ConfigKind.Color, ConfigPlatform.All, "background color"),
        new("foreground", ConfigKind.Color, ConfigPlatform.All, "foreground color"),
        new("background-image", ConfigKind.Text, ConfigPlatform.All, "background image path"),
        new("background-image-opacity", ConfigKind.Float, ConfigPlatform.All, "background image opacity"),
        new("background-image-position", ConfigKind.Enum, ConfigPlatform.All, "background image position"),
        new("background-image-fit", ConfigKind.Enum, ConfigPlatform.All, "background image fit"),
        new("background-image-repeat", ConfigKind.Bool, ConfigPlatform.All, "repeat background image"),
        new("selection-foreground", ConfigKind.Color, ConfigPlatform.All, "selection foreground"),
        new("selection-background", ConfigKind.Color, ConfigPlatform.All, "selection background"),
        new("selection-clear-on-typing", ConfigKind.Bool, ConfigPlatform.All, "clear selection when typing"),
        new("selection-clear-on-copy", ConfigKind.Bool, ConfigPlatform.All, "clear selection after copy"),
        new("selection-word-chars", ConfigKind.Text, ConfigPlatform.All, "word boundary characters"),
        new("minimum-contrast", ConfigKind.Float, ConfigPlatform.All, "minimum contrast ratio 1-21"),
        new("palette", ConfigKind.List, ConfigPlatform.All, "256 color palette entries"),
        new("palette-generate", ConfigKind.Bool, ConfigPlatform.All, "generate extended palette"),
        new("palette-harmonious", ConfigKind.Bool, ConfigPlatform.All, "reverse generated palette"),
        new("cursor-color", ConfigKind.Color, ConfigPlatform.All, "cursor color"),
        new("cursor-opacity", ConfigKind.Float, ConfigPlatform.All, "cursor opacity"),
        new("cursor-style", ConfigKind.Enum, ConfigPlatform.All, "default cursor style"),
        new("cursor-style-blink", ConfigKind.Bool, ConfigPlatform.All, "cursor blinks by default"),
        new("cursor-text", ConfigKind.Color, ConfigPlatform.All, "text color under the cursor"),
        new("cursor-click-to-move", ConfigKind.Bool, ConfigPlatform.All, "click prompt to move cursor"),
        new("mouse-hide-while-typing", ConfigKind.Bool, ConfigPlatform.All, "hide pointer while typing"),
        new("scroll-to-bottom", ConfigKind.Text, ConfigPlatform.All, "when to scroll to bottom"),
        new("mouse-shift-capture", ConfigKind.Enum, ConfigPlatform.All, "shift key with mouse reporting"),
        new("mouse-reporting", ConfigKind.Bool, ConfigPlatform.All, "report mouse events"),
        new("mouse-scroll-multiplier", ConfigKind.Text, ConfigPlatform.All, "wheel scroll multiplier"),
        new("background-opacity", ConfigKind.Float, ConfigPlatform.All, "background opacity"),
        new("background-opacity-cells", ConfigKind.Bool, ConfigPlatform.All, "opacity on colored cells"),
        new("background-blur", ConfigKind.Text, ConfigPlatform.All, "background blur"),
        new("unfocused-split-opacity", ConfigKind.Float, ConfigPlatform.All, "unfocused split opacity"),
        new("unfocused-split-fill", ConfigKind.Color, ConfigPlatform.All, "unfocused split fill"),
        new("split-divider-color", ConfigKind.Color, ConfigPlatform.All, "split divider color"),
        new("split-preserve-zoom", ConfigKind.Text, ConfigPlatform.All, "preserve split zoom"),
        new("drag-handle", ConfigKind.Enum, ConfigPlatform.All, "split drag handle visibility"),
        new("search-foreground", ConfigKind.Text, ConfigPlatform.All, "search match foreground"),
        new("search-background", ConfigKind.Text, ConfigPlatform.All, "search match background"),
        new("search-selected-foreground", ConfigKind.Text, ConfigPlatform.All, "current match foreground"),
        new("search-selected-background", ConfigKind.Text, ConfigPlatform.All, "current match background"),
        new("command", ConfigKind.Text, ConfigPlatform.All, "command to run"),
        new("initial-command", ConfigKind.Text, ConfigPlatform.All, "command for the first surface"),
        new("notify-on-command-finish", ConfigKind.Enum, ConfigPlatform.All, "command finished notifications"),
        new("notify-on-command-finish-action", ConfigKind.Text, ConfigPlatform.All, "notification action"),
        new("notify-on-command-finish-after", ConfigKind.Text, ConfigPlatform.All, "notification delay"),
        new("env", ConfigKind.List, ConfigPlatform.All, "extra environment variables"),
        new("input", ConfigKind.List, ConfigPlatform.All, "input sent on startup"),
        new("wait-after-command", ConfigKind.Bool, ConfigPlatform.All, "keep the surface after exit"),
        new("abnormal-command-exit-runtime", ConfigKind.Integer, ConfigPlatform.All, "abnormal exit threshold"),
        new("scrollback-limit-bytes", ConfigKind.Text, ConfigPlatform.All, "scrollback size in bytes"),
        new("scrollback-limit-lines", ConfigKind.Text, ConfigPlatform.All, "scrollback row cap"),
        new("scrollback-compression", ConfigKind.Bool, ConfigPlatform.All, "compress idle scrollback"),
        new("scrollbar", ConfigKind.Enum, ConfigPlatform.All, "scrollbar visibility"),
        new("link", ConfigKind.List, ConfigPlatform.All, "regex link matcher"),
        new("link-url", ConfigKind.Bool, ConfigPlatform.All, "match URLs"),
        new("link-osc8", ConfigKind.Bool, ConfigPlatform.All, "OSC 8 hyperlinks"),
        new("link-previews", ConfigKind.Enum, ConfigPlatform.All, "show link previews (true/false/osc8)"),
        new("maximize", ConfigKind.Bool, ConfigPlatform.All, "start maximized"),
        new("fullscreen", ConfigKind.Text, ConfigPlatform.All, "start fullscreen"),
        new("title", ConfigKind.Text, ConfigPlatform.All, "window title"),
        new("class", ConfigKind.Text, ConfigPlatform.Linux, "X11 window class"),
        new("x11-instance-name", ConfigKind.Text, ConfigPlatform.Linux, "X11 instance name"),
        new("working-directory", ConfigKind.Text, ConfigPlatform.All, "startup directory"),
        new("keybind", ConfigKind.List, ConfigPlatform.All, "key bindings"),
        new("key-remap", ConfigKind.List, ConfigPlatform.All, "modifier remapping"),
        new("window-padding-x", ConfigKind.Text, ConfigPlatform.All, "horizontal padding (left,right)"),
        new("window-padding-y", ConfigKind.Text, ConfigPlatform.All, "vertical padding (top,bottom)"),
        new("window-padding-balance", ConfigKind.Bool, ConfigPlatform.All, "balance leftover padding"),
        new("window-padding-color", ConfigKind.Enum, ConfigPlatform.All, "padding area color"),
        new("window-vsync", ConfigKind.Bool, ConfigPlatform.All, "synchronize with the display"),
        new("window-inherit-working-directory", ConfigKind.Bool, ConfigPlatform.All, "inherit cwd in new windows"),
        new("tab-inherit-working-directory", ConfigKind.Bool, ConfigPlatform.All, "inherit cwd in new tabs"),
        new("split-inherit-working-directory", ConfigKind.Bool, ConfigPlatform.All, "inherit cwd in new splits"),
        new("window-inherit-font-size", ConfigKind.Bool, ConfigPlatform.All, "inherit font size"),
        new("window-decoration", ConfigKind.Enum, ConfigPlatform.All, "window decorations"),
        new("window-title-font-family", ConfigKind.Text, ConfigPlatform.All, "title bar font"),
        new("window-subtitle", ConfigKind.Enum, ConfigPlatform.All, "window subtitle"),
        new("window-theme", ConfigKind.Enum, ConfigPlatform.All, "window theme"),
        new("window-colorspace", ConfigKind.Enum, ConfigPlatform.All, "color space"),
        new("window-height", ConfigKind.Integer, ConfigPlatform.All, "initial height in cells"),
        new("window-width", ConfigKind.Integer, ConfigPlatform.All, "initial width in cells"),
        new("window-position-x", ConfigKind.Integer, ConfigPlatform.All, "initial X position"),
        new("window-position-y", ConfigKind.Integer, ConfigPlatform.All, "initial Y position"),
        new("window-save-state", ConfigKind.Enum, ConfigPlatform.All, "save and restore window state"),
        new("window-step-resize", ConfigKind.Bool, ConfigPlatform.All, "resize in cell steps"),
        new("window-new-tab-position", ConfigKind.Enum, ConfigPlatform.All, "new tab position"),
        new("window-show-tab-bar", ConfigKind.Enum, ConfigPlatform.All, "tab bar visibility"),
        new("window-titlebar-background", ConfigKind.Color, ConfigPlatform.Linux, "titlebar background"),
        new("window-titlebar-foreground", ConfigKind.Color, ConfigPlatform.Linux, "titlebar foreground"),
        new("resize-overlay", ConfigKind.Enum, ConfigPlatform.All, "resize overlay visibility"),
        new("resize-overlay-position", ConfigKind.Enum, ConfigPlatform.All, "resize overlay position"),
        new("resize-overlay-duration", ConfigKind.Text, ConfigPlatform.All, "resize overlay duration"),
        new("focus-follows-mouse", ConfigKind.Bool, ConfigPlatform.All, "focus follows the pointer"),
        new("clipboard-read", ConfigKind.Enum, ConfigPlatform.All, "OSC 52 clipboard reads"),
        new("clipboard-write", ConfigKind.Enum, ConfigPlatform.All, "OSC 52 clipboard writes"),
        new("clipboard-trim-trailing-spaces", ConfigKind.Bool, ConfigPlatform.All, "trim on copy"),
        new("clipboard-paste-protection", ConfigKind.Bool, ConfigPlatform.All, "confirm unsafe pastes"),
        new("clipboard-paste-bracketed-safe", ConfigKind.Bool, ConfigPlatform.All, "bracketed paste is safe"),
        new("title-report", ConfigKind.Bool, ConfigPlatform.All, "allow CSI 21 t title queries"),
        new("image-storage-limit", ConfigKind.Integer, ConfigPlatform.All, "image memory limit"),
        new("copy-on-select", ConfigKind.Text, ConfigPlatform.All, "copy when selecting"),
        new("right-click-action", ConfigKind.Enum, ConfigPlatform.All, "right click action"),
        new("middle-click-action", ConfigKind.Enum, ConfigPlatform.All, "middle click action"),
        new("click-repeat-interval", ConfigKind.Integer, ConfigPlatform.All, "multi-click interval"),
        new("config-file", ConfigKind.List, ConfigPlatform.All, "extra config files"),
        new("config-default-files", ConfigKind.Bool, ConfigPlatform.All, "load default config files"),
        new("confirm-close-surface", ConfigKind.Text, ConfigPlatform.All, "confirm before closing"),
        new("quit-after-last-window-closed", ConfigKind.Bool, ConfigPlatform.All, "quit after the last window"),
        new("quit-after-last-window-closed-delay", ConfigKind.Text, ConfigPlatform.Linux, "quit delay"),
        new("initial-window", ConfigKind.Bool, ConfigPlatform.Linux, "create an initial window"),
        new("undo-timeout", ConfigKind.Text, ConfigPlatform.All, "undo availability"),
        new("quick-terminal-position", ConfigKind.Enum, ConfigPlatform.All, "quick terminal position"),
        new("quick-terminal-size", ConfigKind.Text, ConfigPlatform.All, "quick terminal size"),
        new("gtk-quick-terminal-layer", ConfigKind.Enum, ConfigPlatform.Linux, "quick terminal layer"),
        new("gtk-quick-terminal-namespace", ConfigKind.Text, ConfigPlatform.Linux, "quick terminal namespace"),
        new("quick-terminal-screen", ConfigKind.Enum, ConfigPlatform.MacOS, "quick terminal screen"),
        new("quick-terminal-animation-duration", ConfigKind.Float, ConfigPlatform.MacOS, "quick terminal animation"),
        new("quick-terminal-autohide", ConfigKind.Bool, ConfigPlatform.All, "hide quick terminal on blur"),
        new("quick-terminal-space-behavior", ConfigKind.Enum, ConfigPlatform.MacOS, "quick terminal spaces"),
        new("quick-terminal-keyboard-interactivity", ConfigKind.Enum, ConfigPlatform.Linux, "quick terminal keyboard"),
        new("shell-integration", ConfigKind.Enum, ConfigPlatform.All, "shell integration injection"),
        new("shell-integration-features", ConfigKind.Text, ConfigPlatform.All, "shell integration features"),
        new("command-palette-entry", ConfigKind.List, ConfigPlatform.All, "custom palette entries"),
        new("osc-color-report-format", ConfigKind.Enum, ConfigPlatform.All, "OSC color report format"),
        new("vt-kam-allowed", ConfigKind.Bool, ConfigPlatform.All, "allow KAM mode"),
        new("custom-shader", ConfigKind.List, ConfigPlatform.All, "custom GLSL shaders"),
        new("custom-shader-animation", ConfigKind.Bool, ConfigPlatform.All, "shader animation loop"),
        new("bell-features", ConfigKind.Text, ConfigPlatform.All, "bell features"),
        new("bell-audio-path", ConfigKind.Text, ConfigPlatform.All, "bell audio file"),
        new("bell-audio-volume", ConfigKind.Float, ConfigPlatform.All, "bell volume"),
        new("app-notifications", ConfigKind.Text, ConfigPlatform.All, "in-app notifications"),
        new("macos-non-native-fullscreen", ConfigKind.Text, ConfigPlatform.MacOS, "non-native fullscreen"),
        new("macos-window-buttons", ConfigKind.Enum, ConfigPlatform.MacOS, "traffic light buttons"),
        new("macos-titlebar-style", ConfigKind.Enum, ConfigPlatform.MacOS, "titlebar style"),
        new("macos-titlebar-proxy-icon", ConfigKind.Enum, ConfigPlatform.MacOS, "proxy icon"),
        new("macos-dock-drop-behavior", ConfigKind.Enum, ConfigPlatform.MacOS, "dock drop behavior"),
        new("macos-option-as-alt", ConfigKind.Enum, ConfigPlatform.MacOS, "option key as alt"),
        new("macos-window-shadow", ConfigKind.Bool, ConfigPlatform.MacOS, "window shadow"),
        new("macos-hidden", ConfigKind.Bool, ConfigPlatform.MacOS, "hide dock icon"),
        new("macos-auto-secure-input", ConfigKind.Bool, ConfigPlatform.MacOS, "automatic secure input"),
        new("macos-secure-input-indication", ConfigKind.Bool, ConfigPlatform.MacOS, "secure input indicator"),
        new("macos-applescript", ConfigKind.Bool, ConfigPlatform.MacOS, "AppleScript dictionary"),
        new("macos-icon", ConfigKind.Enum, ConfigPlatform.MacOS, "app icon style"),
        new("macos-custom-icon", ConfigKind.Text, ConfigPlatform.MacOS, "custom icon path"),
        new("macos-icon-frame", ConfigKind.Enum, ConfigPlatform.MacOS, "icon frame material"),
        new("macos-icon-ghost-color", ConfigKind.Color, ConfigPlatform.MacOS, "icon ghost color"),
        new("macos-icon-screen-color", ConfigKind.List, ConfigPlatform.MacOS, "icon screen colors"),
        new("macos-shortcuts", ConfigKind.Bool, ConfigPlatform.MacOS, "Shortcuts control"),
        new("linux-cgroup", ConfigKind.Bool, ConfigPlatform.Linux, "per-surface systemd scope"),
        new("linux-cgroup-memory-limit", ConfigKind.Integer, ConfigPlatform.Linux, "memory limit"),
        new("linux-cgroup-processes-limit", ConfigKind.Integer, ConfigPlatform.Linux, "process limit"),
        new("linux-cgroup-hard-fail", ConfigKind.Bool, ConfigPlatform.Linux, "fail on cgroup error"),
        new("gtk-opengl-debug", ConfigKind.Bool, ConfigPlatform.Linux, "GTK OpenGL debug log"),
        new("gtk-single-instance", ConfigKind.Bool, ConfigPlatform.Linux, "single instance mode"),
        new("gtk-titlebar", ConfigKind.Bool, ConfigPlatform.Linux, "full GTK titlebar"),
        new("gtk-tabs-location", ConfigKind.Enum, ConfigPlatform.Linux, "tab bar location"),
        new("gtk-horizontal-tab-scroll", ConfigKind.Bool, ConfigPlatform.Linux, "scroll tabs horizontally"),
        new("gtk-tabs-inverted", ConfigKind.Bool, ConfigPlatform.Linux, "invert tab bar colors"),
        new("gtk-titlebar-hide-when-maximized", ConfigKind.Bool, ConfigPlatform.Linux, "hide titlebar when maximized"),
        new("gtk-toolbar-style", ConfigKind.Enum, ConfigPlatform.Linux, "toolbar style"),
        new("gtk-titlebar-style", ConfigKind.Enum, ConfigPlatform.Linux, "GTK titlebar style"),
        new("gtk-wide-tabs", ConfigKind.Bool, ConfigPlatform.Linux, "wide tabs"),
        new("gtk-custom-css", ConfigKind.Text, ConfigPlatform.Linux, "custom GTK CSS"),
        new("desktop-notifications", ConfigKind.Bool, ConfigPlatform.All, "OSC desktop notifications"),
        new("progress-style", ConfigKind.Bool, ConfigPlatform.All, "OSC 9;4 progress bars"),
        new("bold-color", ConfigKind.Color, ConfigPlatform.All, "bold text color"),
        new("faint-opacity", ConfigKind.Float, ConfigPlatform.All, "faint text opacity"),
        new("term", ConfigKind.Text, ConfigPlatform.All, "TERM environment value"),
        new("enquiry-response", ConfigKind.Text, ConfigPlatform.All, "reply to ENQ"),
        new("vt-window-resize-allowed", ConfigKind.Bool, ConfigPlatform.All, "allow window resize sequences"),
        new("vt-xt-checksum-extension", ConfigKind.Text, ConfigPlatform.All, "XT checksum extension"),
        new("vt-xt-checksum-report", ConfigKind.Bool, ConfigPlatform.All, "report XT checksums"),
        new("async-backend", ConfigKind.Enum, ConfigPlatform.All, "async IO backend"),
        new("auto-update", ConfigKind.Enum, ConfigPlatform.MacOS, "auto update"),
        new("auto-update-channel", ConfigKind.Enum, ConfigPlatform.MacOS, "auto update channel"),
    };

    /// <summary>Options this app adds on top of Ghostty's surface.</summary>
    public static readonly ConfigOption[] Application =
    {
        new("tab-position", ConfigKind.Enum, ConfigPlatform.All, "tab strip position: top, left, right"),
        new("compact", ConfigKind.Bool, ConfigPlatform.All, "panes run edge to edge"),
        new("sidebar-width-left", ConfigKind.Float, ConfigPlatform.All, "left rail width in DIPs"),
        new("sidebar-width-right", ConfigKind.Float, ConfigPlatform.All, "right rail width in DIPs"),
        new("debug-tap", ConfigKind.Bool, ConfigPlatform.All, "record the raw VT byte stream"),
        new("crash-dumps", ConfigKind.Bool, ConfigPlatform.All, "ask Windows for a full crash dump"),
    };

    private static readonly Dictionary<string, ConfigOption> Index = BuildIndex();

    private static Dictionary<string, ConfigOption> BuildIndex()
    {
        var index = new Dictionary<string, ConfigOption>(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigOption option in Ghostty) index[option.Name] = option;
        foreach (ConfigOption option in Application) index[option.Name] = option;

        // Ghostty aliases kept for older configs.
        index["shell"] = index["command"];
        index["cwd"] = index["working-directory"];
        index["scrollback-limit"] = index["scrollback-limit-bytes"];
        index["background-blur-radius"] = index["background-blur"];
        index["adw-toolbar-style"] = index["gtk-toolbar-style"];
        return index;
    }

    public static int Count => Ghostty.Length;

    public static bool TryGet(string name, out ConfigOption option) => Index.TryGetValue(name, out option);

    public static bool IsKnown(string name) => Index.ContainsKey(name);

    /// <summary>True when the option is macOS or Linux only and has no Windows counterpart.</summary>
    public static bool IsForeign(ConfigOption option) => option.Platform != ConfigPlatform.All;
}
