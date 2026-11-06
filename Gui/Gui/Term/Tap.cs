using System;
using System.Text;

namespace Gui.Term;

/// <summary>
/// The debug tap mirrors the raw bytes on both sides of the terminal: everything the shell
/// sends in and everything the user sends out. Windows Terminal shows this in a second pane;
/// here it goes to the diagnostics log, where it can be read, copied or attached to a bug
/// report. It is off by default because the output is large and noisy.
/// </summary>
internal static class Tap
{
    /// <summary>Bytes shown per record; the rest is counted but not dumped.</summary>
    private const int MaxDump = 256;

    /// <summary>Whether the tap records anything.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Records one direction of the byte stream.</summary>
    /// <param name="direction">"in" for the shell's output, "out" for what we send.</param>
    public static void Write(string direction, ReadOnlySpan<byte> data)
    {
        if (!Enabled || data.Length == 0) return;

        int shown = Math.Min(data.Length, MaxDump);
        var hex = new StringBuilder(shown * 3);
        var ascii = new StringBuilder(shown);
        for (int i = 0; i < shown; i++)
        {
            byte value = data[i];
            hex.Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            ascii.Append(value >= 0x20 && value < 0x7F ? (char)value : '.');
        }

        string extra = data.Length > shown ? $" (+{data.Length - shown} more)" : string.Empty;
        Diag.Log("tap", $"{direction} {data.Length}{extra} | {hex}|{ascii}|");
    }
}
