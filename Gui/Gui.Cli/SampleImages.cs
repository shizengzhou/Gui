using System;
using System.Collections.Generic;
using System.IO;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// Asks the running Gui to render a font sample with Win2D and returns the PNG. That is the only
/// way to show the pane's own rasterization: Win2D and DirectWrite live in the app, and the
/// window also knows the cell size it is drawing with right now, so a zoom is reflected without
/// the CLI tracking it.
/// </summary>
internal static class SampleImages
{
    /// <summary>A rendered sample and the grid it occupies.</summary>
    public readonly record struct Sample(byte[] Png, int Columns, int Rows);

    private static readonly Dictionary<string, Sample> Cache = new(StringComparer.Ordinal);

    /// <summary>The PNG for one family at this width, or null when no window answered.</summary>
    public static Sample? Render(string family, int columns,
        (byte r, byte g, byte b) foreground, IReadOnlyList<string> lines)
    {
        string key = $"{family}|{columns}|{foreground.r},{foreground.g},{foreground.b}";
        if (Cache.TryGetValue(key, out Sample cached)) return cached;

        string path = Path.Combine(Path.GetTempPath(), "gui", "samples", $"{Hash(key):x16}.png");
        var request = new IpcProtocol.Request
        {
            Action = "render-sample",
            Family = family,
            Columns = columns,
            Foreground = $"#{foreground.r:x2}{foreground.g:x2}{foreground.b:x2}",
            Output = path,
        };

        if (AppChannel.Send(IpcProtocol.PipeName(), request) is not { Ok: true }) return null;
        if (!File.Exists(path)) return null;

        byte[] png = File.ReadAllBytes(path);
        if (png.Length < 24) return null;

        // The image is columns cells wide plus the 2px border on each side, so its IHDR width
        // gives the cell the pane is using right now.
        var sample = new Sample(png, columns, Rows(columns, lines));
        if (Cache.Count > 64) Cache.Clear();
        Cache[key] = sample;
        return sample;
    }

    /// <summary>How many rows the samples take once wrapped to this many columns.</summary>
    public static int Rows(int columns, IReadOnlyList<string> lines)
    {
        if (columns <= 0) return lines.Count;
        int rows = 0;
        foreach (string line in lines) rows += Math.Max(1, (line.Length + columns - 1) / columns);
        return rows;
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
