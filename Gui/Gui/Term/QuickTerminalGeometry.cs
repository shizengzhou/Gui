using System;
using System.Globalization;

namespace Gui.Term;

/// <summary>A size along one axis of the quick terminal: a share of the monitor or a pixel count.</summary>
internal readonly record struct QuickTerminalSize(bool Percent, double Value)
{
    /// <summary>
    /// The pixel count for <paramref name="extent"/> physical pixels. A pixel size is a DIP
    /// figure, the unit Ghostty's 400-pixel default is written in, so it is scaled by the
    /// monitor's DPI before it is compared with a work area measured in physical pixels.
    /// </summary>
    public int Resolve(int extent, double pixelScale) =>
        Math.Max(1, (int)Math.Round(Percent ? extent * Value / 100.0 : Value * pixelScale));

    /// <summary>
    /// Ghostty's grammar: "50%" or "300px". A bare number has no unit and is a config error
    /// there, so it is not accepted here either.
    /// </summary>
    public static bool TryParse(string? text, out QuickTerminalSize size)
    {
        size = default;
        string value = (text ?? string.Empty).Trim();
        if (value.Length == 0) return false;

        bool percent = value.EndsWith("%", StringComparison.Ordinal);
        bool pixels = value.EndsWith("px", StringComparison.OrdinalIgnoreCase);
        if (!percent && !pixels) return false;
        value = percent ? value[..^1].Trim() : value[..^2].Trim();

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return false;
        if (number <= 0 || double.IsNaN(number) || double.IsInfinity(number)) return false;

        size = new QuickTerminalSize(percent, number);
        return true;
    }
}

/// <summary>A rectangle in physical pixels; kept free of WinRT so it can be unit tested.</summary>
internal readonly record struct QuickTerminalRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>
/// Ghostty's quick terminal geometry: where the drop-down window sits on a monitor and how
/// big it is. This is a direct port of Ghostty's <c>QuickTerminalSize.calculate</c> plus the
/// edge placement, so "quick-terminal-position" and "quick-terminal-size" behave the same.
/// </summary>
internal static class QuickTerminalGeometry
{
    /// <summary>Accepted "quick-terminal-position" values; anything else means the top.</summary>
    public static string NormalizePosition(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "bottom" => "bottom",
        "left" => "left",
        "right" => "right",
        "center" => "center",
        _ => "top",
    };

    /// <summary>
    /// The window rectangle for one monitor's work area. The work area is already in physical
    /// pixels, so pixel sizes need no scaling and percentages are shares of what the user can
    /// actually see.
    /// </summary>
    public static QuickTerminalRect Calculate(QuickTerminalRect work, string? position, string? size,
        double pixelScale = 1.0)
    {
        if (pixelScale <= 0.0) pixelScale = 1.0;
        string place = NormalizePosition(position);
        ParseSizes(size, out QuickTerminalSize? primary, out QuickTerminalSize? secondary);

        int width;
        int height;
        switch (place)
        {
            case "left":
            case "right":
                width = primary?.Resolve(work.Width, pixelScale) ?? Scaled(400, work.Width, pixelScale);
                height = secondary?.Resolve(work.Height, pixelScale) ?? work.Height;
                break;

            case "center":
                // Landscape takes the width from the primary axis, portrait the height, which
                // is what Ghostty does so one number keeps its meaning when the monitor turns.
                bool landscape = work.Width >= work.Height;
                width = (landscape ? primary : secondary)?.Resolve(work.Width, pixelScale) ??
                        Scaled(landscape ? 800 : 400, work.Width, pixelScale);
                height = (landscape ? secondary : primary)?.Resolve(work.Height, pixelScale) ??
                         Scaled(landscape ? 400 : 800, work.Height, pixelScale);
                break;

            default:
                width = secondary?.Resolve(work.Width, pixelScale) ?? work.Width;
                height = primary?.Resolve(work.Height, pixelScale) ?? Scaled(400, work.Height, pixelScale);
                break;
        }

        width = Math.Clamp(width, 1, Math.Max(1, work.Width));
        height = Math.Clamp(height, 1, Math.Max(1, work.Height));

        int x = place switch
        {
            "left" => work.X,
            "right" => work.Right - width,
            _ => work.X + (work.Width - width) / 2,
        };
        int y = place switch
        {
            "top" => work.Y,
            "bottom" => work.Bottom - height,
            _ => work.Y + (work.Height - height) / 2,
        };

        return new QuickTerminalRect(x, y, width, height);
    }

    /// <summary>
    /// Splits "primary" or "primary,secondary". The first value is the primary axis (height
    /// for top/bottom, width for left/right); a missing value keeps that axis at its default.
    /// </summary>
    private static int Scaled(int dip, int extent, double scale) => Math.Min((int)Math.Round(dip * scale), extent);

    private static void ParseSizes(string? text, out QuickTerminalSize? primary, out QuickTerminalSize? secondary)
    {
        primary = null;
        secondary = null;

        string raw = (text ?? string.Empty).Trim();
        if (raw.Length == 0) return;

        int comma = raw.IndexOf(',');
        string first = comma < 0 ? raw : raw[..comma];
        string second = comma < 0 ? string.Empty : raw[(comma + 1)..];

        if (QuickTerminalSize.TryParse(first, out QuickTerminalSize parsed)) primary = parsed;
        if (QuickTerminalSize.TryParse(second, out parsed)) secondary = parsed;
    }
}