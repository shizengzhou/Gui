using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Gui.Term;

/// <summary>One face of an installed family: what the style slots of a terminal can pick.</summary>
public readonly record struct FontFace(string Style, int Weight, bool Italic, string File = "");

/// <summary>An installed font family and the faces it provides.</summary>
public sealed class FontFamilyInfo
{
    public string Name = string.Empty;
    public List<FontFace> Faces = new();
    /// <summary>True or false when it is known; null when the source cannot tell.</summary>
    public bool? Monospaced;

    /// <summary>The face a preview should show: the regular one when the family has it.</summary>
    public FontFace Primary()
    {
        foreach (FontFace face in Faces)
        {
            if (face.Style.Length == 0 || face.Style.Equals("Regular", StringComparison.OrdinalIgnoreCase))
                return face;
        }
        return Faces.Count > 0 ? Faces[0] : default;
    }

    /// <summary>The style names the family provides, in weight order, without duplicates.</summary>
    public IReadOnlyList<string> Styles()
    {
        var styles = new List<string>(Faces.Count);
        foreach (FontFace face in Faces.OrderBy(f => f.Weight).ThenBy(f => f.Italic))
        {
            string style = face.Style.Length == 0 ? "Regular" : face.Style;
            if (!styles.Contains(style, StringComparer.OrdinalIgnoreCase)) styles.Add(style);
        }
        return styles;
    }
}

/// <summary>
/// The installed fonts, described without a window system so the CLI can share the names and
/// the style rules with the font picker. The picker itself enumerates with DirectWrite (it
/// needs the monospaced flag and the real style names); the CLI reads the font registry, which
/// carries the face names Windows installed.
/// </summary>
public static class FontCatalog
{
    private static readonly string[] StyleWords =
    {
        "thin", "extralight", "ultralight", "semilight", "demilight", "light", "book",
        "regular", "roman", "medium", "semibold", "demibold", "extrabold", "ultrabold",
        "bold", "heavy", "black", "italic", "oblique", "condensed", "narrow", "expanded",
    };

    /// <summary>
    /// Splits a font registry value name ("Arial Bold Italic (TrueType)") into the family and
    /// the style words that follow it. A name without style words is the family's regular face.
    /// </summary>
    public static (string Family, string Style) SplitInstalledName(string valueName)
    {
        string name = CleanName(valueName);

        string[] words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int first = words.Length;
        while (first > 0 && Array.IndexOf(StyleWords, words[first - 1].ToLowerInvariant()) >= 0)
            first--;

        // "Bold" on its own is not a family: keep the whole name in that case.
        if (first == 0) return (name, string.Empty);

        string family = string.Join(' ', words.Take(first));
        string style = string.Join(' ', words.Skip(first));
        return (family, style);
    }

    /// <summary>
    /// Every family a registry value registers. One value can name several families ("SimSun &
    /// NSimSun", "MS Gothic & MS UI Gothic & MS PGothic"): they share the face files, and only
    /// the split-out names are usable as a font family.
    /// </summary>
    public static List<(string Family, string Style)> SplitInstalledNames(string valueName)
    {
        var names = new List<(string, string)>(1);
        foreach (string part in CleanName(valueName).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            (string family, string style) = SplitInstalledName(part.Trim());
            if (family.Length > 0) names.Add((family, style));
        }
        return names;
    }

    /// <summary>A registry name without its trailing "(TrueType)" and surrounding space.</summary>
    private static string CleanName(string valueName)
    {
        string name = valueName.Trim();
        foreach (string suffix in new[] { " (TrueType)", " (OpenType)", " (All res)" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^suffix.Length].Trim();
                break;
            }
        }
        return name;
    }

    /// <summary>The style name for a weight and slant, the way font menus spell it.</summary>
    public static string StyleName(int weight, bool italic)
    {
        string style = weight switch
        {
            <= 150 => "Thin",
            <= 250 => "ExtraLight",
            <= 350 => "Light",
            <= 450 => "Regular",
            <= 550 => "Medium",
            <= 650 => "SemiBold",
            <= 750 => "Bold",
            <= 850 => "ExtraBold",
            _ => "Black",
        };
        return italic ? (style == "Regular" ? "Italic" : style + " Italic") : style;
    }

    /// <summary>
    /// The families Windows has installed, from the machine and the per-user font keys. Only
    /// scalable faces are kept: the bitmap "Modern"/"Roman"/"Script" entries are not fonts a
    /// terminal can draw with.
    /// </summary>
    public static List<FontFamilyInfo> FromRegistry()
    {
        var families = new Dictionary<string, FontFamilyInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
                 })
        {
            ReadKey(Registry.LocalMachine, key, families);
            ReadKey(Registry.CurrentUser, key, families);
        }

        return families.Values
            .Where(family => family.Faces.Count > 0)
            .OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ReadKey(RegistryKey root, string path, Dictionary<string, FontFamilyInfo> into)
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(path);
            if (key is null) return;

            foreach (string valueName in key.GetValueNames())
            {
                string file = key.GetValue(valueName) as string ?? string.Empty;
                string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
                // "Modern (All res)" and friends resolve to .fon/.fnt system bitmaps.
                if (extension is ".fon" or ".fnt" or "") continue;

                foreach ((string familyName, string style) in SplitInstalledNames(valueName))
                {
                    if (!into.TryGetValue(familyName, out FontFamilyInfo? family))
                    {
                        family = new FontFamilyInfo { Name = familyName };
                        into[familyName] = family;
                    }

                    // "Arial Bold" and "Arial Bold (TrueType)" can both be listed; the face only
                    // has to exist once.
                    if (family.Faces.Any(f => string.Equals(f.Style, style, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    family.Faces.Add(new FontFace(style, WeightOf(style), IsItalic(style), ResolveFile(file)));
                }
            }
        }
        catch (Exception error)
        {
            // An unreadable key is not worth failing a listing over.
            Diag.Warn("fonts.registry", $"{error.GetType().Name}: {error.Message} (path={path})");
        }
    }

    /// <summary>
    /// The registry names a file without a directory for machine-wide fonts ("consola.ttf") and
    /// with one for fonts installed per user; both are turned into a path the preview can show.
    /// </summary>
    private static string ResolveFile(string file)
    {
        if (file.Length == 0) return string.Empty;
        if (System.IO.Path.IsPathRooted(file)) return file;

        string windir = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
        return System.IO.Path.Combine(windir, "Fonts", file);
    }

    /// <summary>The weight a style name stands for; the faces Windows installs are one of these.</summary>
    public static int WeightOf(string style)
    {
        string lower = style.ToLowerInvariant();
        if (lower.Contains("thin")) return 100;
        if (lower.Contains("extralight") || lower.Contains("ultralight")) return 200;
        if (lower.Contains("semilight") || lower.Contains("demilight") || lower.Contains("light")) return 300;
        if (lower.Contains("medium")) return 500;
        if (lower.Contains("semibold") || lower.Contains("demibold")) return 600;
        if (lower.Contains("extrabold") || lower.Contains("ultrabold")) return 800;
        if (lower.Contains("black") || lower.Contains("heavy")) return 900;
        if (lower.Contains("bold")) return 700;
        return 400;
    }

    /// <summary>True when a style name says the face is slanted.</summary>
    public static bool IsItalic(string style)
    {
        string lower = style.ToLowerInvariant();
        return lower.Contains("italic") || lower.Contains("oblique");
    }

    /// <summary>The families whose name contains the query, case-insensitively.</summary>
    public static List<FontFamilyInfo> Filter(IReadOnlyList<FontFamilyInfo> families, string query)
    {
        string text = query.Trim();
        if (text.Length == 0) return new List<FontFamilyInfo>(families);

        var result = new List<FontFamilyInfo>(families.Count);
        foreach (FontFamilyInfo family in families)
        {
            if (family.Name.Contains(text, StringComparison.OrdinalIgnoreCase)) result.Add(family);
        }
        return result;
    }
}
