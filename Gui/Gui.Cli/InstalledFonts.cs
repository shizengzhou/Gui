using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The font families a Gui pane can draw with. DirectWrite is the source of truth: it is the
/// engine the terminal renders through, its monospaced answer is the one the settings page shows,
/// and it can see the fonts registered only for this process (the bundled Nerd Font). The
/// registry is still consulted for the file a family came from, which the preview displays, and
/// serves as a fallback when DirectWrite cannot be reached.
/// </summary>
internal static class InstalledFonts
{
    private static List<FontFamilyInfo>? _all;

    public static List<FontFamilyInfo> All()
    {
        if (_all is not null) return _all;

        RegisterBundled();
        Dictionary<string, string> files = RegistryFiles();

        var families = new List<FontFamilyInfo>();
        foreach ((string name, bool monospaced, List<(int Weight, int Style)> faces)
                 in DirectWriteFonts.Families())
        {
            var family = new FontFamilyInfo { Name = name, Monospaced = monospaced };
            files.TryGetValue(name, out string? file);
            foreach ((int weight, int style) in faces)
            {
                bool italic = style != 0;
                string styleName = FontCatalog.StyleName(weight, italic);
                if (family.Faces.Any(f => string.Equals(f.Style, styleName, StringComparison.OrdinalIgnoreCase)))
                    continue;
                family.Faces.Add(new FontFace(styleName, weight, italic, file ?? string.Empty));
            }
            families.Add(family);
        }

        // DirectWrite normally lists the bundled faces once they are registered; if a build is
        // run where it does not, the families are still offered from the registry's view.
        if (families.Count == 0) families = FontCatalog.FromRegistry();

        families.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return _all = families;
    }

    /// <summary>
    /// Loads the fonts Gui ships next to the exe, the way the app does with FR_PRIVATE, so the
    /// bundled Nerd Font appears even though it is not installed system-wide.
    /// </summary>
    private static void RegisterBundled()
    {
        foreach (string root in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "..", "Assets", "Fonts"),
                     Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*.ttf"))
                AddFontResourceExW(file, 0x10 /* FR_PRIVATE */, IntPtr.Zero);
        }
    }

    /// <summary>The file each family was installed from, as the font registry names it.</summary>
    private static Dictionary<string, string> RegistryFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamilyInfo family in FontCatalog.FromRegistry())
        {
            string file = family.Primary().File;
            if (file.Length > 0) files[family.Name] = file;
        }
        return files;
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceExW(string file, uint flags, IntPtr reserved);
}
