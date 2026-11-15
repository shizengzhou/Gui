using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Gui.Term;

/// <summary>
/// One system-wide key chord, the Windows counterpart of Ghostty's "global:" keybinds
/// (which go through the compositor or macOS Accessibility). Windows delivers a registered
/// chord as WM_HOTKEY to the window that registered it, so this class owns a hidden message
/// window on the UI thread and hands the chord back to the caller.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN = 0x0008;
    private const uint MOD_NOREPEAT = 0x4000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly Dictionary<(IntPtr Hwnd, int Id), GlobalHotkey> Live = new();
    private static WndProc? _windowProc;
    private static string? _className;
    private static int _nextId = 0x4A00;

    private readonly IntPtr _hwnd;
    private readonly int _id;
    private readonly Action _callback;
    private bool _disposed;

    private GlobalHotkey(IntPtr hwnd, int id, Action callback)
    {
        _hwnd = hwnd;
        _id = id;
        _callback = callback;
    }

    /// <summary>
    /// Registers <paramref name="chord"/> in the canonical form Keybinds produces
    /// ("ctrl+backquote"). Null means Windows refused the chord, usually because another
    /// application already owns it; the error says so for the log.
    /// </summary>
    public static GlobalHotkey? TryRegister(string chord, Action callback, out string error)
    {
        error = string.Empty;
        if (!TryParseChord(chord, out uint modifiers, out uint key))
        {
            error = "not a chord RegisterHotKey can bind";
            return null;
        }

        IntPtr hwnd;
        try
        {
            hwnd = EnsureMessageWindow();
        }
        catch (Exception ex)
        {
            error = "message window: " + ex.Message;
            return null;
        }

        int id = _nextId++;
        if (!RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, key))
        {
            error = "already taken by another application (error " + Marshal.GetLastWin32Error() + ")";
            return null;
        }

        var hotkey = new GlobalHotkey(hwnd, id, callback);
        Live[(hwnd, id)] = hotkey;
        return hotkey;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterHotKey(_hwnd, _id);
        Live.Remove((_hwnd, _id));
    }

    /// <summary>
    /// Splits a canonical chord and maps it onto the virtual-key code RegisterHotKey wants.
    /// </summary>
    internal static bool TryParseChord(string chord, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrEmpty(chord)) return false;

        string[] parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].Trim().ToLowerInvariant())
            {
                case "ctrl": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "super": modifiers |= MOD_WIN; break;
                default: return false;
            }
        }

        // Windows insists on at least one modifier, and an unmodified chord would swallow a
        // plain key everywhere, so a bare key is refused instead of registered.
        if (modifiers == 0) return false;

        return TryVirtualKey(parts[^1].Trim().ToLowerInvariant(), out key);
    }

    private static bool TryVirtualKey(string name, out uint key)
    {
        key = 0;
        if (name.Length == 1)
        {
            char c = name[0];
            if (c is >= 'a' and <= 'z')
            {
                key = (uint)('A' + c - 'a');
                return true;
            }
            if (c is >= '0' and <= '9')
            {
                key = (uint)('0' + c - '0');
                return true;
            }
        }

        if (name.Length is 2 or 3 && name[0] == 'f' && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
        {
            key = (uint)(0x70 + f - 1);
            return true;
        }

        if (name.Length == 4 && name.StartsWith("kp_", StringComparison.Ordinal) &&
            name[3] is >= '0' and <= '9')
        {
            key = (uint)(0x60 + name[3] - '0');
            return true;
        }

        key = name switch
        {
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "home" => 0x24,
            "end" => 0x23,
            "page_up" => 0x21,
            "page_down" => 0x22,
            "insert" => 0x2D,
            "delete" => 0x2E,
            "backspace" => 0x08,
            "enter" => 0x0D,
            "tab" => 0x09,
            "escape" => 0x1B,
            "space" => 0x20,
            "plus" => 0xBB,
            "equal" => 0xBB,
            "minus" => 0xBD,
            "comma" => 0xBC,
            "period" => 0xBE,
            "slash" => 0xBF,
            "backslash" => 0xDC,
            "backquote" => 0xC0,
            "bracket_left" => 0xDB,
            "bracket_right" => 0xDD,
            "semicolon" => 0xBA,
            "apostrophe" => 0xDE,
            _ => 0,
        };
        return key != 0;
    }

    // ---------------- message window ----------------

    private static IntPtr EnsureMessageWindow()
    {
        if (_windowProc is null)
        {
            _windowProc = WindowProc;
            _className = "GuiGlobalHotkey_" + Environment.ProcessId;

            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = _windowProc,
                hInstance = GetModuleHandle(null),
                lpszClassName = _className,
            };
            if (RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

            _messageWindow = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (_messageWindow == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
        }

        return _messageWindow;
    }

    private static IntPtr _messageWindow;

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_HOTKEY &&
            Live.TryGetValue((hwnd, (int)wParam.ToInt64()), out GlobalHotkey? hotkey))
        {
            hotkey._callback();
            return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}