using System;
using System.Runtime.InteropServices;

namespace Gui.Term;

/// <summary>
/// Win32 clipboard access for OSC 52 / the kitty clipboard protocol. The WinRT DataPackage
/// API is asynchronous, but the terminal's clipboard callbacks must answer before they
/// return (the VT stream waits), so this uses the synchronous user32 clipboard instead.
/// </summary>
internal static class ClipboardAccess
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr handle);

    public static bool TryReadText(out string text)
    {
        text = string.Empty;
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            IntPtr handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return false;

            IntPtr pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return false;
            try
            {
                text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                return true;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    public static bool TryWriteText(string text)
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            if (!EmptyClipboard()) return false;

            char[] characters = text.ToCharArray();
            nuint bytes = (nuint)((characters.Length + 1) * sizeof(char));
            IntPtr memory = GlobalAlloc(GMEM_MOVEABLE, bytes);
            if (memory == IntPtr.Zero) return false;

            IntPtr target = GlobalLock(memory);
            if (target == IntPtr.Zero)
            {
                GlobalFree(memory);
                return false;
            }
            try
            {
                Marshal.Copy(characters, 0, target, characters.Length);
                Marshal.WriteInt16(target, characters.Length * sizeof(char), 0);
            }
            finally
            {
                GlobalUnlock(memory);
            }

            if (SetClipboardData(CF_UNICODETEXT, memory) != IntPtr.Zero) return true;
            GlobalFree(memory);
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>An empty write clears the clipboard, like Ghostty does for an empty payload.</summary>
    public static bool TryClear()
    {
        if (!OpenClipboard(IntPtr.Zero)) return false;
        try
        {
            return EmptyClipboard();
        }
        finally
        {
            CloseClipboard();
        }
    }
}
