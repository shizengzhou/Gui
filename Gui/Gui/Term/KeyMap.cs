using System;
using System.Runtime.InteropServices;
using Windows.System;

namespace Gui.Term;

/// <summary>
/// Maps WinUI virtual keys onto libghostty-vt's layout-independent physical key codes
/// (W3C UI Events "code" values, in the order declared by key/event.h).
/// </summary>
internal static class KeyMap
{
    internal const int Unidentified = 0;

    internal const int Backquote = 1;
    internal const int Backslash = 2;
    internal const int BracketLeft = 3;
    internal const int BracketRight = 4;
    internal const int Comma = 5;
    internal const int Digit0 = 6;
    internal const int Equal = 16;
    internal const int KeyA = 20;
    internal const int KeyZ = 45;
    internal const int Minus = 46;
    internal const int Period = 47;
    internal const int Quote = 48;
    internal const int Semicolon = 49;
    internal const int Slash = 50;

    internal const int AltLeft = 51;
    internal const int Backspace = 53;
    internal const int CapsLock = 54;
    internal const int ControlLeft = 56;
    internal const int Enter = 58;
    internal const int MetaLeft = 59;
    internal const int MetaRight = 60;
    internal const int ShiftLeft = 61;
    internal const int Space = 63;
    internal const int Tab = 64;

    internal const int Delete = 68;
    internal const int End = 69;
    internal const int Home = 71;
    internal const int Insert = 72;
    internal const int PageDown = 73;
    internal const int PageUp = 74;

    internal const int ArrowDown = 75;
    internal const int ArrowLeft = 76;
    internal const int ArrowRight = 77;
    internal const int ArrowUp = 78;

    internal const int NumLock = 79;
    internal const int Numpad0 = 80;
    internal const int Numpad9 = 89;
    internal const int NumpadAdd = 90;
    internal const int NumpadDecimal = 95;
    internal const int NumpadDivide = 96;
    internal const int NumpadMultiply = 104;
    internal const int NumpadSubtract = 107;
    internal const int NumpadSeparator = 108;
    internal const int Escape = 120;
    internal const int F1 = 121;

    internal const int PrintScreen = 148;
    internal const int ScrollLock = 149;
    internal const int Pause = 150;

    public static int FromVirtualKey(VirtualKey key)
    {
        int vk = (int)key;

        if (vk >= (int)VirtualKey.A && vk <= (int)VirtualKey.Z)
            return KeyA + (vk - (int)VirtualKey.A);
        if (vk >= (int)VirtualKey.Number0 && vk <= (int)VirtualKey.Number9)
            return Digit0 + (vk - (int)VirtualKey.Number0);
        if (vk >= (int)VirtualKey.NumberPad0 && vk <= (int)VirtualKey.NumberPad9)
            return Numpad0 + (vk - (int)VirtualKey.NumberPad0);
        if (vk >= (int)VirtualKey.F1 && vk <= (int)VirtualKey.F24)
            return F1 + (vk - (int)VirtualKey.F1);

        switch (key)
        {
            case VirtualKey.Space: return Space;
            case VirtualKey.Enter: return Enter;
            case VirtualKey.Tab: return Tab;
            case VirtualKey.Escape: return Escape;
            case VirtualKey.Back: return Backspace;
            case VirtualKey.Delete: return Delete;
            case VirtualKey.Insert: return Insert;
            case VirtualKey.Home: return Home;
            case VirtualKey.End: return End;
            case VirtualKey.PageUp: return PageUp;
            case VirtualKey.PageDown: return PageDown;
            case VirtualKey.Left: return ArrowLeft;
            case VirtualKey.Right: return ArrowRight;
            case VirtualKey.Up: return ArrowUp;
            case VirtualKey.Down: return ArrowDown;
            case VirtualKey.NumberKeyLock: return NumLock;
            case VirtualKey.Multiply: return NumpadMultiply;
            case VirtualKey.Add: return NumpadAdd;
            case VirtualKey.Separator: return NumpadSeparator;
            case VirtualKey.Subtract: return NumpadSubtract;
            case VirtualKey.Decimal: return NumpadDecimal;
            case VirtualKey.Divide: return NumpadDivide;
            case VirtualKey.Scroll: return ScrollLock;
            case VirtualKey.Pause: return Pause;
            case VirtualKey.Print: return PrintScreen;
            case VirtualKey.CapitalLock: return CapsLock;
            // Windows exposes one virtual key per modifier, while the kitty protocol names
            // the physical side. The left one is the best available answer.
            case VirtualKey.Shift: return ShiftLeft;
            case VirtualKey.Control: return ControlLeft;
            case VirtualKey.Menu: return AltLeft;
            case VirtualKey.LeftWindows: return MetaLeft;
            case VirtualKey.RightWindows: return MetaRight;
        }

        return vk switch
        {
            0xBA => Semicolon,
            0xBB => Equal,
            0xBC => Comma,
            0xBD => Minus,
            0xBE => Period,
            0xBF => Slash,
            0xC0 => Backquote,
            0xDB => BracketLeft,
            0xDC => Backslash,
            0xDD => BracketRight,
            0xDE => Quote,
            _ => Unidentified,
        };
    }

    /// <summary>Printable keys are typed by the IME sink, not by the key encoder.</summary>
    public static bool IsTextKey(int key)
    {
        if (key >= KeyA && key <= KeyZ) return true;
        if (key >= Digit0 && key <= Digit0 + 9) return true;
        if (key >= Numpad0 && key <= Numpad9) return true;

        return key is Space or Minus or Period or Quote or Semicolon or Slash or Equal
            or Comma or Backquote or Backslash or BracketLeft or BracketRight;
    }

    /// <summary>True for the physical keys that are modifiers themselves.</summary>
    public static bool IsModifierKey(int key) =>
        key is ShiftLeft or ControlLeft or AltLeft or MetaLeft or MetaRight;

    // ---------------- text under the active layout ----------------

    private const uint VK_CONTROL = 0x11;
    private const uint VK_MENU = 0x12;

    /// <summary>
    /// The text a physical key types under the active layout, with Ctrl/Alt transformations
    /// removed (the kitty protocol wants the layout character, not the control code). Empty
    /// for dead keys, which belong to the IME.
    /// </summary>
    internal static string Text(VirtualKey key)
    {
        IntPtr layout = GetKeyboardLayout(0);
        var state = new byte[256];
        GetKeyboardState(state);

        if ((state[VK_CONTROL] & 0x80) != 0 || (state[VK_MENU] & 0x80) != 0)
        {
            // AltGr arrives as Ctrl+Alt and does produce text ("@" on a German layout), so
            // the real state is tried first; a Ctrl or Alt chord yields a control character
            // and falls through to the text without those modifiers.
            string altGr = Printable(Convert(key, state, layout));
            if (altGr.Length > 0) return altGr;

            var plain = (byte[])state.Clone();
            plain[VK_CONTROL] = 0;
            plain[VK_MENU] = 0;
            return Printable(Convert(key, plain, layout));
        }

        return Printable(Convert(key, state, layout));
    }

    /// <summary>
    /// The codepoint a key produces with no modifiers, which is what the kitty protocol
    /// reports as the key code (Shift+A is still reported as "a" plus a Shift modifier).
    /// </summary>
    internal static uint UnshiftedCodepoint(VirtualKey key)
    {
        string text = Printable(Convert(key, new byte[256], GetKeyboardLayout(0)));
        if (text.Length == 0) return 0;
        return (uint)char.ConvertToUtf32(text, 0);
    }

    private static string Convert(VirtualKey key, byte[] state, IntPtr layout)
    {
        uint vk = (uint)key;
        uint scan = MapVirtualKeyEx(vk, 0 /* MAPVK_VK_TO_VSC */, layout);
        var chars = new char[8];
        // Bit 2 leaves the keyboard state alone, so a dead key cannot leak out of a probe.
        int count = ToUnicodeEx(vk, scan, state, chars, chars.Length, 1 << 2, layout);
        if (count <= 0) return string.Empty;
        return new string(chars, 0, Math.Min(count, chars.Length));
    }

    private static string Printable(string text)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c)) return string.Empty;
        }
        return text;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] state,
        [Out] char[] chars, int count, uint flags, IntPtr layout);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);
}
