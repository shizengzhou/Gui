using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Gui.Term;

/// <summary>
/// Process-wide PNG decoder for the Kitty graphics protocol. libghostty-vt stores images
/// itself but delegates PNG decoding to the embedder; the callback has to hand back RGBA
/// pixels allocated through the allocator the library passes in.
/// </summary>
internal static class KittyGraphics
{
    private static readonly object Gate = new();
    private static GhosttyNative.DecodePngFn? _decodePng;
    private static bool _installed;

    /// <summary>
    /// Installs the PNG decoder once per process. The library reads the callback during
    /// <c>ghostty_terminal_vt_write</c>, so it must be set before any terminal is created.
    /// </summary>
    public static unsafe void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;

            byte kitty = 0;
            if (GhosttyNative.ghostty_build_info(GhosttyNative.BUILD_INFO_KITTY_GRAPHICS, &kitty)
                != GhosttyNative.SUCCESS || kitty == 0)
            {
                return;
            }

            _decodePng = DecodePng;
            IntPtr function = Marshal.GetFunctionPointerForDelegate(_decodePng);
            if (GhosttyNative.ghostty_sys_set(GhosttyNative.SYS_OPT_DECODE_PNG, (void*)function)
                != GhosttyNative.SUCCESS)
            {
                _decodePng = null;
            }
        }
    }

    /// <summary>
    /// Decodes a PNG payload for the native library. The output struct starts zeroed; on
    /// success its pixel buffer is owned by the library and freed with the same allocator.
    /// </summary>
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe bool DecodePng(IntPtr userdata, IntPtr allocator, IntPtr data, nuint dataLen,
        IntPtr output)
    {
        if (data == IntPtr.Zero || dataLen == 0 || dataLen > int.MaxValue || output == IntPtr.Zero)
            return false;

        byte[] png = new byte[(int)dataLen];
        Marshal.Copy(data, png, 0, png.Length);

        try
        {
            (byte[] Rgba, int Width, int Height)? decoded = Task
                .Run(() => DecodeAsync(png))
                .GetAwaiter()
                .GetResult();
            if (decoded is not { } image || image.Width <= 0 || image.Height <= 0) return false;

            // ghostty_alloc, not managed memory: the library frees this buffer itself.
            byte* pixels = (byte*)GhosttyNative.ghostty_alloc(allocator, (nuint)image.Rgba.Length);
            if (pixels == null) return false;
            Marshal.Copy(image.Rgba, 0, (IntPtr)pixels, image.Rgba.Length);

            var result = (GhosttyNative.GhosttySysImage*)output;
            result->Width = (uint)image.Width;
            result->Height = (uint)image.Height;
            result->Data = (IntPtr)pixels;
            result->DataLen = (nuint)image.Rgba.Length;
            return true;
        }
        catch (Exception error)
        {
            Diag.LogException("kitty.png", error);
            return false;
        }
    }

    /// <summary>
    /// Runs on the thread pool so the UI thread that is inside <c>vt_write</c> never has to
    /// pump the WinRT operation. The result is straight RGBA, row-major, four bytes per pixel.
    /// </summary>
    private static async Task<(byte[] Rgba, int Width, int Height)?> DecodeAsync(byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (IOutputStream output = stream.GetOutputStreamAt(0))
            using (var writer = new DataWriter(output))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Rgba8,
                BitmapAlphaMode.Straight,
                new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            long length = (long)width * height * 4;
            if (length <= 0 || length > int.MaxValue) return null;

            var buffer = new Windows.Storage.Streams.Buffer((uint)length);
            bitmap.CopyToBuffer(buffer);
            using var reader = DataReader.FromBuffer(buffer);
            var rgba = new byte[(int)length];
            reader.ReadBytes(rgba);
            return (rgba, width, height);
        }
        catch (Exception error)
        {
            Diag.Warn("kitty.png", $"{error.GetType().Name}: {error.Message}");
            return null;
        }
    }
}
