using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gui.Term;

/// <summary>Straight-alpha BGRA pixels of an extracted shell icon.</summary>
internal sealed class IconPixels
{
    public required byte[] Pixels;
    public required int Width;
    public required int Height;
}

/// <summary>
/// Extracts the shell executable's own icon (PowerShell blue icon, cmd icon, ...) for the
/// pane header and the tab chip. The icon is used exactly as Windows supplies it: a theme
/// change must not recolor or re-render it. Icons carry an opaque dark tile, which reads
/// clearly on both the light and dark chrome.
///
/// GDI extraction is free of any UI-thread affinity, but decoding into a XAML image source
/// is not: <see cref="GetAsync"/> must be called on the UI thread and does the encode there.
/// </summary>
internal static class ShellIcon
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, IconPixels?> PixelCache = new();

    // ImageSource instances are UI-thread affine, so this cache has no lock.
    private static readonly Dictionary<string, ImageSource?> SourceCache = new();

    public static async Task<ImageSource?> GetAsync(string executable)
    {
        if (string.IsNullOrEmpty(executable)) return null;

        string key = executable.ToLowerInvariant();
        if (SourceCache.TryGetValue(key, out ImageSource? cached)) return cached;

        IconPixels? pixels;
        lock (Gate)
        {
            if (!PixelCache.TryGetValue(key, out pixels))
            {
                pixels = null;
                PixelCache[key] = null; // placeholder; replaced below
            }
        }

        if (pixels is null)
        {
            pixels = await Task.Run(() => Extract(executable));
            lock (Gate)
            {
                PixelCache[key] = pixels;
            }
        }
        if (pixels is null) return null;

        ImageSource? source = await CreateSourceAsync(pixels);
        SourceCache[key] = source;
        return source;
    }

    /// <summary>Must run on the UI thread.</summary>
    private static async Task<ImageSource?> CreateSourceAsync(IconPixels pixels)
    {
        try
        {
            // SoftwareBitmap with premultiplied alpha, so premultiply the straight-alpha icon.
            var premultiplied = new byte[pixels.Pixels.Length];
            for (int i = 0; i < pixels.Pixels.Length; i += 4)
            {
                int a = pixels.Pixels[i + 3];
                premultiplied[i] = (byte)(pixels.Pixels[i] * a / 255);
                premultiplied[i + 1] = (byte)(pixels.Pixels[i + 1] * a / 255);
                premultiplied[i + 2] = (byte)(pixels.Pixels[i + 2] * a / 255);
                premultiplied[i + 3] = (byte)a;
            }

            using var stream = new InMemoryRandomAccessStream();
            IBuffer buffer;
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(premultiplied);
                buffer = writer.DetachBuffer();
            }

            SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8,
                pixels.Width, pixels.Height, BitmapAlphaMode.Premultiplied);

            var encoded = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            encoded.Seek(0);

            var image = new BitmapImage();
            await image.SetSourceAsync(encoded);
            return image;
        }
        catch (Exception error)
        {
            Diag.LogException("ShellIcon.Encode", error);
            return null;
        }
    }

    private static IconPixels? Extract(string executable)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(executable, FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                SHGFI_ICON | SHGFI_LARGEICON) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        IntPtr icon = info.hIcon;
        IntPtr colorBitmap = IntPtr.Zero;
        IntPtr maskBitmap = IntPtr.Zero;
        IntPtr dc = IntPtr.Zero;
        try
        {
            if (!GetIconInfo(icon, out ICONINFO iconInfo) || iconInfo.hbmColor == IntPtr.Zero)
                return null;
            colorBitmap = iconInfo.hbmColor;
            maskBitmap = iconInfo.hbmMask;

            var bitmap = new BITMAP();
            if (GetObject(colorBitmap, Marshal.SizeOf<BITMAP>(), ref bitmap) == 0)
                return null;

            int width = bitmap.bmWidth;
            int height = bitmap.bmHeight;
            if (width <= 0 || height <= 0 || width > 512 || height > 512)
                return null;

            var pixels = new byte[width * height * 4];
            var header = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                },
            };

            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return null;
            if (GetDIBits(dc, colorBitmap, 0, (uint)height, pixels, ref header, DIB_RGB_COLORS) == 0)
                return null;

            bool anyAlpha = false;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 0) { anyAlpha = true; break; }
            }

            if (!anyAlpha && maskBitmap != IntPtr.Zero)
            {
                // 1bpp mask: transparent where the mask is set.
                var mask = new byte[width * height * 4];
                var maskHeader = header;
                if (GetDIBits(dc, maskBitmap, 0, (uint)height, mask, ref maskHeader, DIB_RGB_COLORS) != 0)
                {
                    for (int i = 0; i < pixels.Length; i += 4)
                    {
                        bool transparent = mask[i] != 0 || mask[i + 1] != 0 || mask[i + 2] != 0;
                        pixels[i + 3] = (byte)(transparent ? 0 : 255);
                    }
                }
            }

            return new IconPixels { Pixels = pixels, Width = width, Height = height };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (colorBitmap != IntPtr.Zero) DeleteObject(colorBitmap);
            if (maskBitmap != IntPtr.Zero) DeleteObject(maskBitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            DestroyIcon(icon);
        }
    }

    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint infoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetObject(IntPtr handle, int size, ref BITMAP bitmap);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BITMAPINFO info, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
