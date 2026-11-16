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

        RegisterBundled();
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
    /// Loads the fonts Gui ships next to the exe, the way the app does with FR_PRIVATE, so the
    /// bundled Nerd Font appears in the list even though it is not installed system-wide.
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
}
