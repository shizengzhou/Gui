using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// One theme file. The name is the file name the "theme" option takes; <see cref="File"/> is
/// where it was found, which is empty for an entry built by name (the tests do that).
/// </summary>
internal readonly record struct ThemeEntry(string Name, bool Light, string File = "");

/// <summary>The colors a theme file defines, with the 16 palette slots filled in.</summary>
internal sealed class ThemeColors
{
    public Rgb Background;
    public Rgb Foreground;
    public bool CursorSet;
    public Rgb Cursor;
    public bool SelectionBackgroundSet;
    public Rgb SelectionBackground;
    public bool SelectionForegroundSet;
    public Rgb SelectionForeground;
    public readonly Rgb[] Palette = new Rgb[16];
    public string File = string.Empty;
}

/// <summary>
/// The bundled Ghostty themes, in the three pieces "+list-themes" needs: the catalog, the colors
/// of one theme file, and the "theme" value to write into the config. Plain BCL, so the logic
/// tests can drive it against a scratch directory.
/// </summary>
internal static class ThemeCatalog
{
    /// <summary>The asset folders, split by background brightness.</summary>
    private static readonly (string Folder, bool Light)[] Folders =
    {
        ("light", true),
        ("dark", false),
    };

    /// <summary>Xterm's colors, used for a palette slot the theme file leaves out.</summary>
    private static readonly Rgb[] FallbackPalette =
    {
        new(0, 0, 0), new(205, 49, 49), new(13, 188, 121), new(229, 229, 16),
        new(36, 114, 200), new(188, 63, 188), new(17, 168, 205), new(229, 229, 229),
        new(102, 102, 102), new(241, 76, 76), new(35, 209, 139), new(245, 245, 67),
        new(59, 142, 234), new(214, 112, 214), new(41, 184, 219), new(255, 255, 255),
    };

    /// <summary>
    /// Finds the bundled "Assets/Themes" folder. The install keeps it next to Gui.exe (and
    /// gui-cli.exe); a development build runs from bin/&lt;platform&gt;/&lt;config&gt;/&lt;tfm&gt;/&lt;rid&gt;,
    /// so a few parents are searched as well.
    /// </summary>
    public static string? FindDirectory()
    {
        string? directory = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(directory); i++)
        {
            foreach (string relative in new[] { "Assets/Themes", "Gui/Gui/Assets/Themes" })
            {
                string candidate = Path.Combine(directory,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(candidate)) return candidate;
            }

            directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    /// <summary>
    /// Every theme file, sorted by name: the user's own folder first — its entries shadow a
    /// bundled theme of the same name, the way Ghostty searches the config directory before its
    /// resources — then the bundled light and dark together. A user theme is classified by the
    /// background it sets, since its folder says nothing.
    /// </summary>
    public static List<ThemeEntry> Load(string directory, string? userDirectory)
    {
        var themes = new List<ThemeEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(userDirectory) && Directory.Exists(userDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(userDirectory))
            {
                string name = Path.GetFileName(file);
                if (name.Length == 0 || name[0] == '.' || !seen.Add(name)) continue;
                themes.Add(new ThemeEntry(name, TerminalThemes.IsLightFile(file), file));
            }
        }

        foreach ((string folder, bool light) in Folders)
        {
            string path = Path.Combine(directory, folder);
            if (!Directory.Exists(path)) continue;

            foreach (string file in Directory.EnumerateFiles(path))
            {
                string name = Path.GetFileName(file);
                if (name.Length == 0 || name[0] == '.' || !seen.Add(name)) continue;
                themes.Add(new ThemeEntry(name, light, file));
            }
        }

        themes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return themes;
    }

    /// <summary>Reads one theme file; null when it cannot be read.</summary>
    public static ThemeColors? Read(ThemeEntry entry, string directory)
    {
        string path = entry.File.Length > 0
            ? entry.File
            : Path.Combine(directory, entry.Light ? "light" : "dark", entry.Name);
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception)
        {
            return null;
        }

        var colors = new ThemeColors
        {
            File = path,
            Background = entry.Light ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0),
            Foreground = entry.Light ? new Rgb(36, 41, 47) : new Rgb(229, 229, 229),
        };
        Array.Copy(FallbackPalette, colors.Palette, colors.Palette.Length);

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int equals = line.IndexOf('=');
            if (equals <= 0) continue;

            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();
            switch (key)
            {
                case "background":
                    if (Config.TryParseColor(value, out Rgb background)) colors.Background = background;
                    break;
                case "foreground":
                    if (Config.TryParseColor(value, out Rgb foreground)) colors.Foreground = foreground;
                    break;
                case "cursor-color":
                    colors.CursorSet = Config.TryParseColor(value, out colors.Cursor);
                    break;
                case "selection-background":
                    colors.SelectionBackgroundSet =
                        Config.TryParseColor(value, out colors.SelectionBackground);
                    break;
                case "selection-foreground":
                    colors.SelectionForegroundSet =
                        Config.TryParseColor(value, out colors.SelectionForeground);
                    break;
                case "palette":
                {
                    int split = value.IndexOf('=');
                    if (split <= 0) break;
                    if (!int.TryParse(value[..split].Trim(), out int index)) break;
                    if (index < 0 || index > 15) break;
                    if (Config.TryParseColor(value[(split + 1)..], out Rgb color)) colors.Palette[index] = color;
                    break;
                }
            }
        }

        // A theme that leaves the selection out is shown the way the terminal draws it: a veil
        // of white over a dark background, of black over a light one.
        if (!colors.SelectionBackgroundSet)
            colors.SelectionBackground = Veil(colors.Background, entry.Light);
        if (!colors.SelectionForegroundSet)
            colors.SelectionForeground = colors.Foreground;

        return colors;
    }

    /// <summary>Blends the theme's background towards white (dark) or black (light).</summary>
    private static Rgb Veil(Rgb background, bool light)
    {
        int Blend(byte channel, int target, int weight) =>
            (channel * (100 - weight) + target * weight) / 100;
        int toward = light ? 0 : 255;
        int weight = light ? 12 : 25;
        return new Rgb((byte)Blend(background.R, toward, weight),
            (byte)Blend(background.G, toward, weight),
            (byte)Blend(background.B, toward, weight));
    }

    /// <summary>
    /// The "theme" value after choosing <paramref name="entry"/>: only the half that matches the
    /// theme's brightness changes, so the other side keeps whatever the file already had.
    /// </summary>
    public static string Apply(string? current, ThemeEntry entry)
    {
        (string? light, string? dark) = Config.ParseThemeValue(current ?? string.Empty);
        if (entry.Light) light = entry.Name;
        else dark = entry.Name;

        return "light:" + (light ?? Config.LightThemeName) +
               ",dark:" + (dark ?? Config.DarkThemeName);
    }

    /// <summary>Case-insensitive substring match, the same filter the font browser uses.</summary>
    public static List<ThemeEntry> Filter(List<ThemeEntry> themes, string query)
    {
        if (query.Length == 0) return new List<ThemeEntry>(themes);

        var matches = new List<ThemeEntry>();
        foreach (ThemeEntry theme in themes)
            if (theme.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) matches.Add(theme);
        return matches;
    }
}
