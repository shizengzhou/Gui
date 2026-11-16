using System;
using System.Collections.Generic;
using System.IO;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// Asks the running Gui to render a font sample with Win2D and returns the PNG. That is the only
/// way to show the pane's own rasterization: Win2D and DirectWrite live in the app, and the
/// sample then has the same outlines, hinting and antialiasing as the terminal's cells.
/// </summary>
internal static class SampleImages
{
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>The PNG for one family at one cell size, or null when no window answered.</summary>
    public static byte[]? Render(string family, int cellWidth, int cellHeight,
        (byte r, byte g, byte b) foreground)
    {
        string key = $"{family}|{cellWidth}x{cellHeight}|{foreground.r},{foreground.g},{foreground.b}";
        if (Cache.TryGetValue(key, out byte[]? cached)) return cached;

        string path = Path.Combine(Path.GetTempPath(), "gui", "samples", $"{Hash(key):x16}.png");
        var request = new IpcProtocol.Request
        {
            Action = "render-sample",
            Family = family,
            CellWidth = cellWidth,
            CellHeight = cellHeight,
            Foreground = $"#{foreground.r:x2}{foreground.g:x2}{foreground.b:x2}",
            Output = path,
        };

        if (AppChannel.Send(IpcProtocol.PipeName(), request) is not { Ok: true }) return null;
        if (!File.Exists(path)) return null;

        byte[] png = File.ReadAllBytes(path);
        if (Cache.Count > 64) Cache.Clear();
        Cache[key] = png;
        return png;
    }

    /// <summary>A stable 64-bit hash, since string.GetHashCode is randomized per process.</summary>
    private static ulong Hash(string text)
    {
        ulong hash = 14695981039346656037;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 1099511628211;
        }
        return hash;
    }
}
