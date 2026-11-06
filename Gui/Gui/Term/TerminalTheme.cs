using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui.Term;

/// <summary>Three-channel RGB color, laid out exactly like GhosttyColorRgb.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Rgb : IEquatable<Rgb>
{
    public byte R;
    public byte G;
    public byte B;

    public Rgb(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public readonly bool Equals(Rgb other) => R == other.R && G == other.G && B == other.B;
    public override readonly bool Equals(object? obj) => obj is Rgb other && Equals(other);
    public override readonly int GetHashCode() => (R << 16) | (G << 8) | B;
    public static bool operator ==(Rgb a, Rgb b) => a.Equals(b);
    public static bool operator !=(Rgb a, Rgb b) => !a.Equals(b);
}

/// <summary>
/// Resolved terminal palette. <see cref="Custom"/> is false for the built-in design
/// dark/light palettes and true when a bundled Ghostty theme supplied the colors.
/// </summary>
public sealed class TerminalTheme
{
    public bool Custom;
    public Rgb Background = new(7, 9, 14);
    public Rgb Foreground = new(245, 245, 247);
    public bool CursorSet;
    public Rgb Cursor;
    public Rgb SelectionBackground;
    public Rgb SelectionForeground;
    public readonly Rgb[] Palette = new Rgb[256];
}

/// <summary>Loads the bundled Ghostty themes shipped in Assets\Themes.</summary>
public static class TerminalThemes
{
    private static readonly (string Name, bool Light)[] Folders = { ("light", true), ("dark", false) };

    public static bool IsLight(Rgb background) => GhosttyNative.PaletteLuminance(background) > 0.5;

    /// <summary>Parses the #RGB / #RRGGBB / #RRGGBBAA colors used in Ghostty configs.</summary>
    public static bool ParseColor(string value, out Rgb color)
    {
        color = default;
        string text = value.Trim();
        if (text.Length != 4 && text.Length != 7 && text.Length != 9)
            return false;
        if (text[0] != '#')
            return false;

        static int Hex(char ch)
        {
            if (ch >= '0' && ch <= '9') return ch - '0';
            if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
            if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
            return -1;
        }

        if (text.Length == 4)
        {
            int r = Hex(text[1]), g = Hex(text[2]), b = Hex(text[3]);
            if (r < 0 || g < 0 || b < 0) return false;
            color = new Rgb((byte)(r * 17), (byte)(g * 17), (byte)(b * 17));
            return true;
        }

        int r1 = Hex(text[1]), r2 = Hex(text[2]);
        int g1 = Hex(text[3]), g2 = Hex(text[4]);
        int b1 = Hex(text[5]), b2 = Hex(text[6]);
        if (r1 < 0 || r2 < 0 || g1 < 0 || g2 < 0 || b1 < 0 || b2 < 0) return false;
        color = new Rgb((byte)(r1 * 16 + r2), (byte)(g1 * 16 + g2), (byte)(b1 * 16 + b2));
        return true; // #RRGGBBAA shares the same first six digits.
    }

    private static bool ValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains("..")) return false;
        return name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) < 0;
    }

    private static string ThemeDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");

    private static string? ThemeFile(string name)
    {
        if (!ValidName(name)) return null;
        foreach (var folder in Folders)
        {
            string file = Path.Combine(ThemeDirectory, folder.Name, name);
            if (File.Exists(file)) return file;
        }
        return null;
    }

    /// <summary>
    /// Bundled Ghostty themes, sorted case-insensitively; the flag is the folder they
    /// live in (light or dark). The folder is the light/dark classification, so
    /// listing the menu never parses 890 files.
    /// </summary>
    public static List<(string Name, bool Light)> Bundled()
    {
        var themes = new List<(string, bool)>();
        foreach (var folder in Folders)
        {
            string directory = Path.Combine(ThemeDirectory, folder.Name);
            if (!Directory.Exists(directory)) continue;

            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string name = Path.GetFileName(file);
                if (!ValidName(name)) continue;
                themes.Add((name, folder.Light));
            }
        }

        themes.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return themes;
    }

    public static bool Load(string name, out TerminalTheme theme)
    {
        theme = new TerminalTheme();
        string? file = ThemeFile(name);
        if (file is null) return false;

        string text;
        try
        {
            text = File.ReadAllText(file, Encoding.UTF8);
        }
        catch
        {
            return false;
        }
        if (text.Length == 0) return false;

        var result = new TerminalTheme { Custom = true };

        var baseColors = new Rgb[256];
        GhosttyNative.PaletteDefault(baseColors);
        var skip = new GhosttyNative.ColorPaletteMask();
        bool haveBackground = false;
        bool haveForeground = false;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int equals = line.IndexOf('=');
            if (equals < 0) continue;

            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();

            switch (key)
            {
                case "background":
                    if (ParseColor(value, out Rgb bg))
                    {
                        result.Background = bg;
                        haveBackground = true;
                    }
                    break;
                case "foreground":
                    if (ParseColor(value, out Rgb fg))
                    {
                        result.Foreground = fg;
                        haveForeground = true;
                    }
                    break;
                case "cursor-color":
                    result.CursorSet = ParseColor(value, out result.Cursor);
                    break;
                case "selection-background":
                    ParseColor(value, out result.SelectionBackground);
                    break;
                case "selection-foreground":
                    ParseColor(value, out result.SelectionForeground);
                    break;
                case "palette":
                {
                    int split = value.IndexOf('=');
                    if (split < 0) continue;
                    string indexText = value[..split].Trim();
                    if (!int.TryParse(indexText, out int index) || index < 0 || index >= 256) continue;
                    if (ParseColor(value[(split + 1)..], out Rgb color))
                    {
                        baseColors[index] = color;
                        skip.Set(index);
                    }
                    break;
                }
            }
        }

        if (!haveBackground || !haveForeground) return false;

        var generated = new Rgb[256];
        GhosttyNative.PaletteGenerate(baseColors, skip, result.Background, result.Foreground, false, generated);
        Array.Copy(generated, result.Palette, 256);

        theme = result;
        return true;
    }
}
