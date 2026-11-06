using System;
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

    internal const int Backspace = 53;
    internal const int CapsLock = 54;
    internal const int Enter = 58;
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
            case VirtualKey.Scroll: return ScrollLock;
            case VirtualKey.Pause: return Pause;
            case VirtualKey.Print: return PrintScreen;
            case VirtualKey.CapitalLock: return CapsLock;
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

        return key is Space or Minus or Period or Quote or Semicolon or Slash or Equal
            or Comma or Backquote or Backslash or BracketLeft or BracketRight;
    }
}
