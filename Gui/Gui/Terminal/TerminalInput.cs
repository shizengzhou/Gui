using System;
using System.Text;
using Windows.System;

namespace Gui.Terminal;

/// <summary>
/// Translates WinUI key events into the escape sequences a VT program expects.
/// </summary>
internal static class TerminalInput
{
    public static byte[]? Encode(VirtualKey key, bool ctrl, bool alt, bool shift)
    {
        // Ctrl+<letter> -> C0 control code.
        if (ctrl && !alt && key >= VirtualKey.A && key <= VirtualKey.Z)
        {
            int code = (int)key - (int)VirtualKey.A + 1;
            return new[] { (byte)code };
        }

        byte[]? seq = key switch
        {
            VirtualKey.Enter => "\r"u8.ToArray(),
            VirtualKey.Back => new byte[] { 0x7F },
            VirtualKey.Escape => new byte[] { 0x1B },
            VirtualKey.Tab => shift ? "\x1b[Z"u8.ToArray() : "\t"u8.ToArray(),
            VirtualKey.Up => Cursor("A", ctrl, alt, shift),
            VirtualKey.Down => Cursor("B", ctrl, alt, shift),
            VirtualKey.Right => Cursor("C", ctrl, alt, shift),
            VirtualKey.Left => Cursor("D", ctrl, alt, shift),
            VirtualKey.Home => Cursor("H", ctrl, alt, shift),
            VirtualKey.End => Cursor("F", ctrl, alt, shift),
            VirtualKey.Insert => Tilde(2, ctrl, alt, shift),
            VirtualKey.Delete => Tilde(3, ctrl, alt, shift),
            VirtualKey.PageUp => Tilde(5, ctrl, alt, shift),
            VirtualKey.PageDown => Tilde(6, ctrl, alt, shift),
            VirtualKey.F1 => FunctionKey("P", ctrl, alt, shift),
            VirtualKey.F2 => FunctionKey("Q", ctrl, alt, shift),
            VirtualKey.F3 => FunctionKey("R", ctrl, alt, shift),
            VirtualKey.F4 => FunctionKey("S", ctrl, alt, shift),
            VirtualKey.F5 => Tilde(15, ctrl, alt, shift),
            VirtualKey.F6 => Tilde(17, ctrl, alt, shift),
            VirtualKey.F7 => Tilde(18, ctrl, alt, shift),
            VirtualKey.F8 => Tilde(19, ctrl, alt, shift),
            VirtualKey.F9 => Tilde(20, ctrl, alt, shift),
            VirtualKey.F10 => Tilde(21, ctrl, alt, shift),
            VirtualKey.F11 => Tilde(23, ctrl, alt, shift),
            VirtualKey.F12 => Tilde(24, ctrl, alt, shift),
            _ => null,
        };

        if (seq is not null)
            return seq;

        // Alt+letter / Alt+digit -> ESC prefix.
        if (alt && !ctrl && !shift && key >= VirtualKey.A && key <= VirtualKey.Z)
            return new byte[] { 0x1B, (byte)('a' + ((int)key - (int)VirtualKey.A)) };

        if (alt && !ctrl && key >= VirtualKey.Number0 && key <= VirtualKey.Number9)
            return new byte[] { 0x1B, (byte)('0' + ((int)key - (int)VirtualKey.Number0)) };

        return null;
    }

    private static byte[] Cursor(string final, bool ctrl, bool alt, bool shift)
    {
        int mod = Modifier(ctrl, alt, shift);
        string s = mod == 1 ? $"\x1b[{final}" : $"\x1b[1;{mod}{final}";
        return Encoding.ASCII.GetBytes(s);
    }

    private static byte[] Tilde(int number, bool ctrl, bool alt, bool shift)
    {
        int mod = Modifier(ctrl, alt, shift);
        string s = mod == 1 ? $"\x1b[{number}~" : $"\x1b[{number};{mod}~";
        return Encoding.ASCII.GetBytes(s);
    }

    private static byte[] FunctionKey(string final, bool ctrl, bool alt, bool shift)
    {
        int mod = Modifier(ctrl, alt, shift);
        string s = mod == 1 ? $"\x1bO{final}" : $"\x1b[1;{mod}{final}";
        return Encoding.ASCII.GetBytes(s);
    }

    private static int Modifier(bool ctrl, bool alt, bool shift)
        => 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

    public static byte[] EncodeText(string text) => Encoding.UTF8.GetBytes(text);
}
