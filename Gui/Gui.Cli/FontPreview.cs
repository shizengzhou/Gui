using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Gui.Cli;

/// <summary>
/// Rasterizes a font sample with GDI into a PNG, which the browser then shows through the
/// terminal's image protocol. GDI draws the family's real outlines, so the preview is the same
/// text a pane would render, not the terminal's current font dressed up.
/// </summary>
internal static class FontPreview
{
    private const int PngHeader = 8;

    /// <summary>
    /// Renders <paramref name="text"/> in <paramref name="family"/> at the given pixel height.
    /// The card is a dark surface with light text so the sample stays readable whatever theme
    /// the terminal uses; the image carries its own background instead of blending with one.
    /// </summary>
    public static byte[]? Render(string family, string text, int pixelHeight, int maxWidth,
        out int width, out int height)
    {
        width = 0;
        height = 0;

        var background = (r: 24, g: 25, b: 32);
        var foreground = (r: 232, g: 233, b: 238);
        const int pad = 10;

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        IntPtr dc = CreateCompatibleDC(screen);
        IntPtr font = IntPtr.Zero;
        IntPtr dib = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            // A font height is in logical units, so the screen's DPI would scale the sample on
            // a high-density display. Ask for device pixels instead, then shrink the height
            // until the line fits the space the caller gives it.
            int dpi = GetDeviceCaps(dc, 90); // LOGPIXELSY
            if (dpi <= 0) dpi = 96;
            int heightLogical = -(int)Math.Round(pixelHeight * 96.0 / dpi);

            font = CreateFontW(heightLogical, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
            if (font == IntPtr.Zero) return null;
            previous = SelectObject(dc, font);
            if (!GetTextExtentPoint32W(dc, text, text.Length, out SIZE extent)) return null;

            if (extent.cx + pad * 2 > maxWidth && extent.cx > 0)
            {
                // Too wide for the card: scale the height down by the same ratio.
                SelectObject(dc, previous);
                DeleteObject(font);
                font = IntPtr.Zero;
                heightLogical = Math.Max(-8, (int)(heightLogical * (double)maxWidth / (extent.cx + pad * 2)));
                font = CreateFontW(heightLogical, 0, 0, 0, 400, 0, 0, 0, 1, 5, 4, 4, 0, family);
                if (font == IntPtr.Zero) return null;
                SelectObject(dc, font);
                if (!GetTextExtentPoint32W(dc, text, text.Length, out extent)) return null;
            }

            width = Math.Min(maxWidth, extent.cx + pad * 2);
            height = extent.cy + pad * 2;
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

            // Paint the card, then draw the sample. GDI writes BGRA into the DIB.
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = (byte)background.b;
                pixels[i + 1] = (byte)background.g;
                pixels[i + 2] = (byte)background.r;
                pixels[i + 3] = 255;
            }
            Marshal.Copy(pixels, 0, bits, pixels.Length);

            SetBkMode(dc, 1); // TRANSPARENT
            SetTextColor(dc, foreground.r | (foreground.g << 8) | (foreground.b << 16));
            TextOutW(dc, pad, pad, text, text.Length);
            GdiFlush();

            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return EncodePng(pixels, width, height);
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
