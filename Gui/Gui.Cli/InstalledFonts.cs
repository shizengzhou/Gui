using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The font families this system actually has, as GDI sees them. The registry alone is not
/// enough: one value can name several families, and fonts registered for a process only (the
/// bundled JetBrainsMono Nerd Font) have no registry entry at all. Faces are grouped into
/// families by the same style-word rule the config uses, and a family counts as monospaced when
/// GDI's text metrics say every one of its faces is fixed pitch.
/// </summary>
internal static class InstalledFonts
{
    private static List<FontFamilyInfo>? _all;

    public static List<FontFamilyInfo> All()
    {
        if (_all is not null) return _all;

        List<(string Family, string Style, string File)> bundled = RegisterBundled();
        var families = new Dictionary<string, FontFamilyInfo>(StringComparer.OrdinalIgnoreCase);
        var pitch = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return _all = new List<FontFamilyInfo>();

        IntPtr dc = CreateCompatibleDC(screen);
        try
        {
            var logfont = new LOGFONTW { lfCharSet = 1 }; // DEFAULT_CHARSET: every charset
            FontEnumProc callback = (logfontPtr, metricsPtr, fontType, _) =>
            {
                // RASTER_FONTTYPE: the old .fon bitmaps (Fixedsys, Terminal, Courier). A
                // DirectWrite terminal cannot draw with them, so they are not offered.
                if ((fontType & 1) != 0) return 1;
                var font = Marshal.PtrToStructure<LOGFONTW>(logfontPtr);
                var metrics = metricsPtr == IntPtr.Zero
                    ? default
                    : Marshal.PtrToStructure<TEXTMETRICW>(metricsPtr);
                string face = font.lfFaceName ?? string.Empty;
                if (face.Length == 0 || face[0] == '@') return 1; // vertical variants

                (string name, string style) = FontCatalog.SplitInstalledName(face);
                if (name.Length == 0) return 1;

                if (!families.TryGetValue(name, out FontFamilyInfo? family))
                {
                    family = new FontFamilyInfo { Name = name };
                    families[name] = family;
                    pitch[name] = true;
                }

                bool fixedPitch = (metrics.tmPitchAndFamily & 1) == 0;
                if (!fixedPitch) pitch[name] = false;

                if (!family.Faces.Any(f => string.Equals(f.Style, style, StringComparison.OrdinalIgnoreCase)))
                    family.Faces.Add(new FontFace(style, FontCatalog.WeightOf(style),
                        FontCatalog.IsItalic(style)));
                return 1;
            };

            EnumFontFamiliesExW(dc, ref logfont, callback, IntPtr.Zero, 0);
            GC.KeepAlive(callback);
        }
        finally
        {
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }

        // GDI cannot enumerate private fonts, so the bundled ones are added by the name their
        // own file declares.
        foreach ((string familyName, string style, string file) in bundled)
        {
            if (!families.TryGetValue(familyName, out FontFamilyInfo? bundledFamily))
            {
                bundledFamily = new FontFamilyInfo { Name = familyName };
                families[familyName] = bundledFamily;
                pitch[familyName] = FixedPitch(familyName);
            }

            if (!bundledFamily.Faces.Any(f => string.Equals(f.Style, style, StringComparison.OrdinalIgnoreCase)))
            {
                bundledFamily.Faces.Add(new FontFace(style, FontCatalog.WeightOf(style),
                    FontCatalog.IsItalic(style), file));
            }
        }

        // The registry carries a file per face, which the preview shows; a privately registered
        // font has none.
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamilyInfo family in FontCatalog.FromRegistry())
            files[family.Name] = family.Primary().File;

        var result = new List<FontFamilyInfo>(families.Count);
        foreach (FontFamilyInfo family in families.Values)
        {
            family.Monospaced = pitch[family.Name];
            if (files.TryGetValue(family.Name, out string? file) && file.Length > 0)
            {
                for (int i = 0; i < family.Faces.Count; i++)
                {
                    if (family.Faces[i].File.Length == 0)
                        family.Faces[i] = family.Faces[i] with { File = file };
                }
            }
            result.Add(family);
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return _all = result;
    }

    /// <summary>
    /// Loads the fonts Gui ships next to the exe, the way the app does with FR_PRIVATE. GDI does
    /// not enumerate private fonts, so the family and style come out of the font file's name
    /// table instead; without this the bundled Nerd Font, which is the default font-family,
    /// would be missing from its own picker.
    /// </summary>
    private static List<(string Family, string Style, string File)> RegisterBundled()
    {
        var bundled = new List<(string, string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "..", "Assets", "Fonts"),
                     Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"),
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (string file in Directory.EnumerateFiles(root, "*.ttf"))
            {
                if (!seen.Add(file)) continue;
                AddFontResourceExW(file, 0x10 /* FR_PRIVATE */, IntPtr.Zero);
                if (FontNames(file) is { } names) bundled.Add((names.Family, names.Style, file));
            }
        }
        return bundled;
    }

    /// <summary>
    /// The family and style a TrueType file declares: the "name" table's family name (1) and
    /// subfamily (2) in the Windows Unicode encoding, which is what DirectWrite matches on.
    /// </summary>
    private static (string Family, string Style)? FontNames(string file)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(file);
        }
        catch (Exception)
        {
            return null;
        }

        if (data.Length < 12) return null;
        int tables = ReadUInt16(data, 4);
        for (int i = 0; i < tables; i++)
        {
            int record = 12 + i * 16;
            if (record + 16 > data.Length) return null;
            if (data[record] != 'n' || data[record + 1] != 'a' || data[record + 2] != 'm' ||
                data[record + 3] != 'e') continue;

            int offset = (int)ReadUInt32(data, record + 8);
            int count = ReadUInt16(data, offset + 2);
            int strings = offset + ReadUInt16(data, offset + 4);

            string family = string.Empty;
            string style = string.Empty;
            for (int n = 0; n < count; n++)
            {
                int entry = offset + 6 + n * 12;
                if (entry + 12 > data.Length) break;
                int platform = ReadUInt16(data, entry);
                int nameId = ReadUInt16(data, entry + 6);
                int length = ReadUInt16(data, entry + 8);
                int start = strings + ReadUInt16(data, entry + 10);
                if (platform != 3 || (nameId != 1 && nameId != 2)) continue;
                if (start + length > data.Length) continue;

                string value = System.Text.Encoding.BigEndianUnicode.GetString(data, start, length);
                if (nameId == 1) family = value; else style = value;
            }

            if (family.Length == 0) return null;
            return (family, style);
        }
        return null;
    }

    private static int ReadUInt16(byte[] data, int offset) =>
        (data[offset] << 8) | data[offset + 1];

    private static uint ReadUInt32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
        ((uint)data[offset + 2] << 8) | data[offset + 3];

    /// <summary>Whether a family advances every glyph by the same amount, asked of GDI.</summary>
    private static bool FixedPitch(string family)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return false;
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr font = IntPtr.Zero;
        try
        {
            font = CreateFontW(-40, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
            if (font == IntPtr.Zero) return false;
            IntPtr previous = SelectObject(dc, font);
            bool ok = GetTextMetricsW(dc, out TEXTMETRICW metrics);
            SelectObject(dc, previous);
            return ok && (metrics.tmPitchAndFamily & 1) == 0;
        }
        finally
        {
            if (font != IntPtr.Zero) DeleteObject(font);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct TEXTMETRICW
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    private delegate int FontEnumProc(IntPtr logfont, IntPtr textmetric, uint fontType, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumFontFamiliesExW(IntPtr hdc, ref LOGFONTW logfont, FontEnumProc proc,
        IntPtr lParam, uint flags);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceExW(string file, uint flags, IntPtr reserved);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW metrics);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);
}
