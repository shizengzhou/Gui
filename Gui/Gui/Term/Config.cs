using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Gui.Term;

/// <summary>
/// Ghostty-style configuration: a plain text file with one "key = value" per line
/// and "#" starting a comment (same shape as ghostty).
/// </summary>
public sealed class Config
{
    /// <summary>The built-in light theme. Its config value and its name are the same string.</summary>
    public const string LightThemeName = "Gui Light";

    /// <summary>The built-in dark theme. Its config value and its name are the same string.</summary>
    public const string DarkThemeName = "Gui Dark";

    public string Command = string.Empty;          // empty: auto-detect pwsh/powershell/cmd
    public string WorkingDirectory = string.Empty; // empty: %USERPROFILE%
    public string FontFamily = string.Empty;       // empty: JetBrainsMono Nerd Font
    public float FontSize = 13.0f;                 // DIPs
    public float UnfocusedSplitOpacity = 0.7f;     // 0.15..1, Ghostty's default
    public bool HasUnfocusedSplitFill;
    public Rgb UnfocusedSplitFill;                 // empty: terminal background
    public string TabPosition = string.Empty;      // "top" (default), "left", "right"
    public string ThemeMode = string.Empty;        // empty, Gui Light, Gui Dark, or a bundled theme name
    public string WindowThemeMode = string.Empty;  // "system" (default), "light", "dark"
    public bool DebugTap;                         // record the raw VT byte stream
    public bool CrashDumps;                        // ask WER for full crash dumps
    public TerminalTheme TerminalTheme = new();    // resolved terminal palette
    public bool LightTheme;                        // resolved from theme only
    public bool WindowLight;                       // resolved from window-theme

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
        WorkingDirectory = other.WorkingDirectory;
        FontFamily = other.FontFamily;
        FontSize = other.FontSize;
        UnfocusedSplitOpacity = other.UnfocusedSplitOpacity;
        HasUnfocusedSplitFill = other.HasUnfocusedSplitFill;
        UnfocusedSplitFill = other.UnfocusedSplitFill;
        TabPosition = other.TabPosition;
        ThemeMode = other.ThemeMode;
        WindowThemeMode = other.WindowThemeMode;
        DebugTap = other.DebugTap;
        CrashDumps = other.CrashDumps;
        TerminalTheme = other.TerminalTheme;
        LightTheme = other.LightTheme;
        WindowLight = other.WindowLight;
    }

    /// <summary>Parses the file. A missing or unreadable file yields the defaults.</summary>
    public static Config Load(string file)
    {
        var config = new Config();
        string data;
        try
        {
            if (!File.Exists(file)) return config;
            data = File.ReadAllText(file, Encoding.UTF8);
        }
        catch
        {
            return config;
        }
        if (data.Length == 0) return config;

        foreach (string rawLine in data.Split('\n'))
        {
            string trimmed = rawLine.Trim(' ', '\t', '\r');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;

            int separator = trimmed.IndexOf('=');
            if (separator < 0) continue;

            string key = trimmed[..separator].Trim(' ', '\t').ToLowerInvariant();
            string value = Unquote(trimmed[(separator + 1)..].Trim(' ', '\t'));
            if (value.Length == 0) continue;

            switch (key)
            {
                case "command":
                case "shell":
                    config.Command = value;
                    break;
                case "working-directory":
                case "cwd":
                    config.WorkingDirectory = value;
                    break;
                case "font-family":
                    config.FontFamily = value;
                    break;
                case "tab-position":
                    config.TabPosition = value.ToLowerInvariant();
                    break;
                case "theme":
                    config.ThemeMode = value;
                    break;
                case "window-theme":
                    config.WindowThemeMode = value.ToLowerInvariant();
                    break;
                case "font-size":
                    if (double.TryParse(value, out double size) && size >= 6.0 && size <= 72.0)
                        config.FontSize = (float)size;
                    break;
                case "unfocused-split-opacity":
                    if (double.TryParse(value, out double opacity))
                        config.UnfocusedSplitOpacity = Math.Clamp((float)opacity, 0.15f, 1.0f);
                    break;
                case "unfocused-split-fill":
                    if (TerminalThemes.ParseColor(value, out Rgb color))
                    {
                        config.HasUnfocusedSplitFill = true;
                        config.UnfocusedSplitFill = color;
                    }
                    break;
                case "debug-tap":
                    config.DebugTap = value is "true" or "1" or "yes" or "on";
                    break;
                case "crash-dumps":
                    config.CrashDumps = value is "true" or "1" or "yes" or "on";
                    break;
            }
        }

        if (config.WorkingDirectory.Length == 0)
            config.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // "theme" controls the terminal palette only. Empty, "Gui Dark" or "Gui Light" pick the
        // design's own palettes; anything else is a bundled Ghostty theme name
        // (Assets/Themes/light|dark). The old short names are still accepted.
        string themeName = config.ThemeMode.Trim();
        string themeKey = themeName.ToLowerInvariant();
        bool designLight = themeKey is "light" or "gui light";
        bool designDark = themeKey is "dark" or "gui dark";

        config.LightTheme = designLight;
        if (themeName.Length > 0 && !designLight && !designDark)
        {
            if (TerminalThemes.Load(themeName, out TerminalTheme loaded))
            {
                config.TerminalTheme = loaded;
                config.LightTheme = TerminalThemes.IsLight(loaded.Background);
            }
        }

        // The built-in themes carry their full name, so the value in the config file and the
        // name shown in the UI are the same string.
        if (designLight) config.ThemeMode = LightThemeName;
        else if (designDark) config.ThemeMode = DarkThemeName;

        // window-theme: window decorations are independent of the terminal theme.
        // "system" (default) follows the OS appearance; "light" / "dark" force one.
        if (config.WindowThemeMode == "light")
        {
            config.WindowLight = true;
        }
        else if (config.WindowThemeMode == "dark")
        {
            config.WindowLight = false;
        }
        else
        {
            config.WindowThemeMode = "system";
            config.WindowLight = SystemUsesLightTheme();
        }

        // The window brightness constrains the usable themes: a light window only
        // accepts light themes and a dark window only dark ones. A mismatching custom
        // theme falls back to the default Gui Light / Gui Dark palette.
        if (config.LightTheme != config.WindowLight)
        {
            config.ThemeMode = config.WindowLight ? LightThemeName : DarkThemeName;
            config.LightTheme = config.WindowLight;
            config.TerminalTheme = new TerminalTheme();
        }

        return config;
    }

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
            "#\n" +
            "# command            shell to run; default pwsh.exe, then powershell.exe, then %COMSPEC%\n" +
            "# working-directory  directory the shell starts in; default is your user profile\n" +
            "# font-family        monospace font family; default JetBrainsMono Nerd Font\n" +
            "# font-size          font size in DIPs; default 13\n" +
            "# unfocused-split-opacity  default 0.7; 0.15 (very dim) to 1 (off)\n" +
            "# unfocused-split-fill     empty (default; terminal background) or #RRGGBB\n" +
            "# tab-position       top (default), left or right\n" +
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
            "# theme = Gui Dark\n" +
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
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1];
        return value;
    }
}
