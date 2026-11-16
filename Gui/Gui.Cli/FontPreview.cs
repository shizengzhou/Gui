using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Gui.Cli;

/// <summary>
/// Rasterizes a font sample with GDI into a PNG that the browser shows through the terminal's
/// image protocol. GDI draws the family's real outlines, so the preview is the text a pane would
/// render. The card is transparent and the glyphs are coverage, so the sample sits on the
/// terminal's own background the way tsp's and kitty's font browsers do.
/// </summary>
internal static class FontPreview
{
    private static readonly Dictionary<string, bool> _monospace = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the family exists and draws every glyph at the same advance. The picker offers
    /// monospaced families only, and the registry does not say which ones are, so GDI is asked:
    /// enumerating the family proves the name resolves (a made-up name silently falls back to
    /// another font), and its text metrics carry the fixed-pitch flag.
    /// </summary>
    public static bool IsMonospaced(string family)
    {
        if (_monospace.TryGetValue(family, out bool known)) return known;

        bool found = false;
        bool fixedPitch = false;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen != IntPtr.Zero)
        {
            IntPtr dc = CreateCompatibleDC(screen);
            try
            {
                var logfont = new LOGFONTW
                {
                    lfCharSet = 1,           // DEFAULT_CHARSET: match the name in any charset
                    lfPitchAndFamily = 0,
                    lfFaceName = family,
                };

                FontEnumProc callback = (_, metrics, _, _) =>
                {
                    found = true;
                    if (metrics != IntPtr.Zero)
                    {
                        var textMetric = Marshal.PtrToStructure<TEXTMETRICW>(metrics);
                        // Bit 0 of tmPitchAndFamily set means variable pitch.
                        fixedPitch = (textMetric.tmPitchAndFamily & 1) == 0;
                    }
                    return 0; // one face is enough
                };

                EnumFontFamiliesExW(dc, ref logfont, callback, IntPtr.Zero, 0);
                GC.KeepAlive(callback);
            }
            finally
            {
                DeleteDC(dc);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        bool answer = found && fixedPitch;
        _monospace[family] = answer;
        return answer;
    }

    /// <summary>
    /// Renders the sample lines in <paramref name="family"/> at the given pixel height, in the
    /// terminal's foreground colour with alpha coverage around the glyphs.
    /// </summary>
    public static byte[]? Render(string family, IReadOnlyList<string> lines, int pixelHeight,
        int maxWidth, (byte r, byte g, byte b) foreground, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (lines.Count == 0) return null;

        const int pad = 6;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr font = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            // A font height is in logical units, so a high-density screen would scale the
            // sample. Ask for device pixels, then shrink until the widest line fits.
            int dpi = GetDeviceCaps(dc, 90); // LOGPIXELSY
            if (dpi <= 0) dpi = 96;
            int heightLogical = -(int)Math.Round(pixelHeight * 96.0 / dpi);

            font = CreateFontW(heightLogical, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
            if (font == IntPtr.Zero) return null;
            previous = SelectObject(dc, font);

            if (!MeasureLines(dc, lines, out int widest, out int lineHeight)) return null;

            if (widest + pad * 2 > maxWidth && widest > 0)
            {
                SelectObject(dc, previous);
                DeleteObject(font);
                font = IntPtr.Zero;
                heightLogical = Math.Max(-8, (int)(heightLogical * (double)maxWidth / (widest + pad * 2)));
                font = CreateFontW(heightLogical, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
                if (font == IntPtr.Zero) return null;
                SelectObject(dc, font);
                if (!MeasureLines(dc, lines, out widest, out lineHeight)) return null;
            }

            width = widest + pad * 2;
            height = lineHeight * lines.Count + pad * 2;
            if (width < 8 || height < 8) return null;

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height,      // top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,
                },
            };

            dib = CreateDIBSection(dc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            SelectObject(dc, dib);

            // Black everywhere; the glyph coverage becomes the alpha channel below.
            var pixels = new byte[width * height * 4];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            SetBkMode(dc, 1); // TRANSPARENT
            SetTextColor(dc, 0x00FFFFFF);
            for (int i = 0; i < lines.Count; i++)
                TextOutW(dc, pad, pad + i * lineHeight, lines[i], lines[i].Length);
            GdiFlush();

            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return EncodePng(ToCoverage(pixels, foreground), width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (font != IntPtr.Zero) DeleteObject(font);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static bool MeasureLines(IntPtr dc, IReadOnlyList<string> lines, out int widest,
        out int lineHeight)
    {
        widest = 0;
        lineHeight = 0;
        foreach (string line in lines)
        {
            if (!GetTextExtentPoint32W(dc, line, line.Length, out SIZE size)) return false;
            if (size.cx > widest) widest = size.cx;
            if (size.cy > lineHeight) lineHeight = size.cy;
        }
        return true;
    }

    /// <summary>
    /// Turns "white glyphs on black" into the foreground colour with coverage as alpha, so the
    /// terminal composites the sample over whatever background the theme paints.
    /// </summary>
    private static byte[] ToCoverage(byte[] bgra, (byte r, byte g, byte b) foreground)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte b = bgra[i];
            byte g = bgra[i + 1];
            byte r = bgra[i + 2];
            byte alpha = Math.Max(r, Math.Max(g, b));

            bgra[i] = foreground.b;
            bgra[i + 1] = foreground.g;
            bgra[i + 2] = foreground.r;
            bgra[i + 3] = alpha;
        }
        return bgra;
    }

    /// <summary>A minimal RGBA PNG: the pixels already are the rows the terminal decodes.</summary>
    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = 8;   // bit depth
        header[9] = 6;   // colour type RGBA
        output.Write(Chunk("IHDR", header));

        // Each scanline is prefixed with its filter type (0 = none).
        var raw = new byte[(width * 4 + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int to = y * (width * 4 + 1);
            raw[to] = 0;
            Buffer.BlockCopy(rgba, y * width * 4, raw, to + 1, width * 4);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);
        output.Write(Chunk("IDAT", compressed.ToArray()));
        output.Write(Chunk("IEND", Array.Empty<byte>()));
        return output.ToArray();
    }

    private static byte[] Chunk(string type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        WriteBigEndian(chunk, 0, data.Length);
        for (int i = 0; i < 4; i++) chunk[4 + i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, chunk, 8, data.Length);
        WriteBigEndian(chunk, 8 + data.Length, (int)Crc32(chunk, 4, 4 + data.Length));
        return chunk;
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static uint[]? _crcTable;

    private static uint Crc32(byte[] data, int offset, int count)
    {
        if (_crcTable is null)
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            _crcTable = table;
        }

        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < count; i++) crc = _crcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public int[] bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation,
        int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr hdc, int x, int y, string text, int count);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int count, out SIZE size);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hdc, int color);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
}
