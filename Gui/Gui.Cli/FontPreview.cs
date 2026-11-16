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
    /// <summary>
    /// Renders the sample the way a pane draws it: one glyph per cell, advanced by the pane's
    /// cell width, one line per cell height. The font is sized until its advance matches the
    /// cell, so the image shows the text at the size and spacing the terminal itself uses.
    /// </summary>
    public static byte[]? Render(string family, IReadOnlyList<string> lines, int cellWidth,
        int cellHeight, (byte r, byte g, byte b) foreground, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (lines.Count == 0 || cellWidth <= 0 || cellHeight <= 0) return null;

        const int pad = 2;
        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr font = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            // MM_TEXT maps one logical unit to one device pixel, so the cell size can be used
            // for the font height directly.
            int heightLogical = -(int)Math.Round(cellHeight * 0.8);
            font = CreateFontW(heightLogical, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
            if (font == IntPtr.Zero) return null;
            previous = SelectObject(dc, font);

            if (!GetTextExtentPoint32W(dc, "M", 1, out SIZE cell)) return null;
            if (cell.cx > 0 && cell.cx != cellWidth)
            {
                // Scale the height so one glyph advances exactly one cell.
                SelectObject(dc, previous);
                DeleteObject(font);
                heightLogical = Math.Min(-6, (int)Math.Round(heightLogical * (double)cellWidth / cell.cx));
                font = CreateFontW(heightLogical, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
                if (font == IntPtr.Zero) return null;
                SelectObject(dc, font);
                if (!GetTextExtentPoint32W(dc, "M", 1, out cell)) return null;
            }

            // Vertically centre the glyphs in their cell, the way a terminal does, and use the
            // measured height so the offset follows the font rather than the nominal size.
            if (!GetTextExtentPoint32W(dc, "Mg", 2, out SIZE glyph)) return null;
            int offsetY = Math.Max(0, (cellHeight - glyph.cy) / 2);

            int longest = 0;
            foreach (string line in lines) longest = Math.Max(longest, line.Length);
            if (longest == 0) return null;

            width = longest * cellWidth + pad * 2;
            height = lines.Count * cellHeight + pad * 2;
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
            // Selecting the bitmap replaces whatever was selected, so the font goes back on
            // after it or the sample would be drawn in the DC's default font.
            SelectObject(dc, dib);
            SelectObject(dc, font);

            var pixels = new byte[width * height * 4];
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            SetBkMode(dc, 1); // TRANSPARENT
            SetTextColor(dc, 0x00FFFFFF);
            for (int row = 0; row < lines.Count; row++)
            {
                string line = lines[row];
                for (int col = 0; col < line.Length; col++)
                {
                    if (line[col] == ' ') continue;
                    TextOutW(dc, pad + col * cellWidth, pad + row * cellHeight + offsetY,
                        line.Substring(col, 1), 1);
                }
            }
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
            // GDI's coverage is lighter than the terminal's own rasterization; a mild gamma
            // keeps the sample from looking washed out next to the pane's text.
            if (alpha > 0) alpha = (byte)Math.Min(255, 255.0 * Math.Pow(alpha / 255.0, 0.75));

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
