using System;

namespace Gui.Term;

/// <summary>
/// The WCAG 2.0 contrast maths behind "minimum-contrast", kept apart from the renderer so it
/// can be tested without a canvas. The ratio is derived from relative luminance and the text
/// colour is blended toward white or black until the ratio is met, which is what Ghostty does.
/// </summary>
public static class Contrast
{
    /// <summary>Relative luminance of a colour, per WCAG 2.0 (sRGB, linearised).</summary>
    public static double Luminance(Rgb color) =>
        0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    /// <summary>The contrast ratio of two colours, 1 (identical) to 21 (black on white).</summary>
    public static double Ratio(Rgb first, Rgb second)
    {
        double a = Luminance(first);
        double b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// Returns the colour to draw text in so that it reaches <paramref name="minimum"/>
    /// against <paramref name="background"/>. A colour that already qualifies is returned
    /// unchanged; otherwise it is moved the smallest distance toward white or black that
    /// reaches the ratio. Values at or below 1 mean "no minimum".
    /// </summary>
    public static Rgb Raise(Rgb foreground, Rgb background, float minimum)
    {
        if (minimum <= 1f) return foreground;

        double backgroundLuminance = Luminance(background);
        double foregroundLuminance = Luminance(foreground);
        if (RatioOf(foregroundLuminance, backgroundLuminance) >= minimum) return foreground;

        // The luminance the text needs if it moves lighter or darker than the background.
        double lighter = minimum * (backgroundLuminance + 0.05) - 0.05;
        double darker = (backgroundLuminance + 0.05) / minimum - 0.05;

        bool canLighten = lighter <= 1.0;
        bool canDarken = darker >= 0.0;

        bool lighten;
        double target;
        if (canLighten && canDarken)
        {
            // Both directions can reach the ratio; take the shorter move so the colour drifts
            // as little as possible from the one the theme asked for.
            lighten = lighter - foregroundLuminance <= foregroundLuminance - darker;
            target = lighten ? lighter : darker;
        }
        else if (canLighten)
        {
            lighten = true;
            target = lighter;
        }
        else if (canDarken)
        {
            lighten = false;
            target = darker;
        }
        else
        {
            // The background is too far from both ends for this ratio: take whichever of
            // white and black reads better, which is what the option is for.
            return RatioOf(1.0, backgroundLuminance) >= RatioOf(0.0, backgroundLuminance)
                ? new Rgb(255, 255, 255)
                : new Rgb(0, 0, 0);
        }

        // Luminance rises (or falls) monotonically with the blend factor, so bisection finds
        // the smallest blend that reaches the target.
        Rgb result = lighten ? new Rgb(255, 255, 255) : new Rgb(0, 0, 0);
        double low = 0.0;
        double high = 1.0;
        for (int i = 0; i < 12; i++)
        {
            double amount = (low + high) / 2.0;
            Rgb candidate = Blend(foreground, lighten, amount);
            bool reached = lighten
                ? Luminance(candidate) >= target
                : Luminance(candidate) <= target;

            if (reached)
            {
                result = candidate;
                high = amount;
            }
            else
            {
                low = amount;
            }
        }

        return result;
    }

    private static double RatioOf(double first, double second) =>
        (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);

    private static double Channel(byte value)
    {
        double channel = value / 255.0;
        return channel <= 0.03928
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static Rgb Blend(Rgb color, bool toWhite, double amount)
    {
        byte Mix(byte value)
        {
            double target = toWhite ? 255.0 : 0.0;
            return (byte)Math.Clamp(Math.Round(value + (target - value) * amount), 0.0, 255.0);
        }

        return new Rgb(Mix(color.R), Mix(color.G), Mix(color.B));
    }
}
