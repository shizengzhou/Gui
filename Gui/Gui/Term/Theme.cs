using System;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// Design tokens. The whole app renders from one of the design's two token sets; a
/// bundled Ghostty theme derives the chrome from its own terminal colors instead.
/// </summary>
public static class Theme
{
    /// <summary>Design palette in use (config "theme", or the theme's background brightness).</summary>
    public static bool Light;

    /// <summary>Window decorations resolve separately (config "window-theme").</summary>
    public static bool WindowLight;

    /// <summary>Active terminal palette; set once at startup before any session exists.</summary>
    public static TerminalTheme Terminal = new();

    public static bool WindowChromeDiffers => WindowLight != Light;

    public static Color Rgba(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);

    public static bool CustomTheme => Terminal.Custom;

    public static double PerceivedLuminance(Rgb color) =>
        0.299 * (color.R / 255.0) + 0.587 * (color.G / 255.0) + 0.114 * (color.B / 255.0);

    public static Rgb MixRgb(Rgb from, Rgb to, double amount)
    {
        static byte Blend(byte a, byte b, double t)
        {
            double value = a + (b - (double)a) * t;
            return (byte)(value + 0.5);
        }
        return new Rgb(Blend(from.R, to.R, amount), Blend(from.G, to.G, amount), Blend(from.B, to.B, amount));
    }

    /// <summary>
    /// Tonal step away from the terminal background: dark themes lift toward white,
    /// light themes sink toward black. 0 is the background itself.
    /// </summary>
    public static Rgb Tone(double amount)
    {
        Rgb pole = Light ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
        return MixRgb(Terminal.Background, pole, amount);
    }

    public static Color Tint(byte alpha, Rgb color) => Rgba(alpha, color.R, color.G, color.B);

    /// <summary>
    /// Source-over compositing of a translucent colour on an opaque one: what the window
    /// paints where the scrim sits on the mica, and the only way a custom theme can derive
    /// the island colour the design hard-codes.
    /// </summary>
    public static Color Over(Color top, Color bottom)
    {
        double alpha = top.A / 255.0;

        static byte Blend(byte over, byte under, double a) => (byte)(under + (over - under) * a + 0.5);

        return Rgba(255, Blend(top.R, bottom.R, alpha), Blend(top.G, bottom.G, alpha),
            Blend(top.B, bottom.B, alpha));
    }

    // --- window backdrop ---------------------------------------------------
    public static Color WindowMica() =>
        CustomTheme ? Tint(255, Tone(Light ? 0.06 : 0.18))
                    : Light ? Rgba(255, 241, 244, 250) : Rgba(255, 45, 50, 67);

    public static Color Scrim()
    {
        if (CustomTheme)
        {
            Rgb wash = Light ? MixRgb(Terminal.Background, new Rgb(255, 255, 255), 0.04) : Tone(0.06);
            return Tint(Light ? (byte)0x8C : (byte)0x4D, wash);
        }
        return Light ? Rgba(0x8C, 249, 250, 253) : Rgba(0x4D, 20, 23, 31);
    }

    // --- content island and pane surfaces ----------------------------------
    /// <summary>
    /// Content island. The design's --window-bg is exactly the mica with the scrim on top, so
    /// the island reads as the same surface as the chrome and only the panes stand out. A
    /// custom theme derives that same composite instead of its own tonal step, which used to
    /// leave the island a shade off the backdrop and visible as a card.
    /// </summary>
    public static Color ContentBg() =>
        CustomTheme ? Over(Scrim(), WindowMica())
                    : Light ? Rgba(255, 245, 247, 252) : Rgba(255, 37, 42, 56);

    public static Color TermBg() => Light ? Rgba(255, 255, 255, 255) : Rgba(255, 7, 9, 14);

    public static Color TermFg() => Rgba(255, 36, 41, 47);

    /// <summary>
    /// Terminal surface behind the renderer: a bundled Ghostty theme replaces the
    /// design's terminal background independently of the UI palette.
    /// </summary>
    public static Rgb TermSurfaceRgb()
    {
        if (Terminal.Custom) return Terminal.Background;
        return Light ? new Rgb(255, 255, 255) : new Rgb(7, 9, 14);
    }

    public static Color TermSurfaceBg()
    {
        Rgb rgb = TermSurfaceRgb();
        return Rgba(255, rgb.R, rgb.G, rgb.B);
    }

    // --- outlines and chrome ------------------------------------------------
    public static Color Stroke() =>
        CustomTheme ? Tint(Light ? (byte)0x17 : (byte)0x1A, Terminal.Foreground)
                    : Light ? Rgba(0x17, 0, 0, 0) : Rgba(0x1A, 255, 255, 255);

    public static Color StrokeStrong() =>
        CustomTheme ? Tint(Light ? (byte)0x29 : (byte)0x2C, Terminal.Foreground)
                    : Light ? Rgba(0x29, 0, 0, 0) : Rgba(0x2C, 255, 255, 255);

    public static Color PaneStroke() =>
        CustomTheme ? Tint(Light ? (byte)0x17 : (byte)0x12, Terminal.Foreground)
                    : Light ? Rgba(0x17, 0, 0, 0) : Rgba(0x12, 255, 255, 255);

    public static Color PaneStrokeHover() =>
        CustomTheme ? Tint(Light ? (byte)0x29 : (byte)0x2C, Terminal.Foreground)
                    : Light ? Rgba(0x29, 0, 0, 0) : Rgba(0x2C, 255, 255, 255);

    // --- text ----------------------------------------------------------------
    public static Color TextStrongFor(bool lightTheme) =>
        CustomTheme ? Tint(255, Terminal.Foreground)
                    : lightTheme ? Rgba(255, 27, 28, 32) : Rgba(255, 245, 245, 247);

    public static Color TextBodyFor(bool lightTheme) =>
        CustomTheme ? Tint(lightTheme ? (byte)0xA8 : (byte)0xAD, Terminal.Foreground)
                    : lightTheme ? Rgba(0xA8, 0, 0, 0) : Rgba(0xAD, 255, 255, 255);

    public static Color TextFaintFor(bool lightTheme) =>
        CustomTheme ? Tint(lightTheme ? (byte)0x94 : (byte)0x80, Terminal.Foreground)
                    : lightTheme ? Rgba(0x94, 0, 0, 0) : Rgba(0x80, 255, 255, 255);

    public static Color ChipHoverFor(bool lightTheme) =>
        CustomTheme ? Tint(lightTheme ? (byte)0x14 : (byte)0x12, Terminal.Foreground)
                    : lightTheme ? Rgba(0x14, 0, 0, 0) : Rgba(0x12, 255, 255, 255);

    public static Color TextStrong() => TextStrongFor(Light);
    public static Color TextBody() => TextBodyFor(Light);
    public static Color TextFaint() => TextFaintFor(Light);

    // --- window chrome --------------------------------------------------------
    public static Color ChromeTextStrong() => TextStrongFor(WindowLight);
    public static Color ChromeTextBody() => TextBodyFor(WindowLight);
    public static Color ChromeTextFaint() => TextFaintFor(WindowLight);
    public static Color ChromeChipHover() => ChipHoverFor(WindowLight);

    public static Color ChromeCloseButtonHover() =>
        CustomTheme ? Tint(WindowLight ? (byte)0x1A : (byte)0x24, Terminal.Foreground)
                    : WindowLight ? Rgba(0x1A, 0, 0, 0) : Rgba(0x24, 255, 255, 255);

    public static Color ChromeStroke() =>
        CustomTheme ? Tint(WindowLight ? (byte)0x17 : (byte)0x1A, Terminal.Foreground)
                    : WindowLight ? Rgba(0x17, 0, 0, 0) : Rgba(0x1A, 255, 255, 255);

    // --- icon-button glyphs ---------------------------------------------------
    /// <summary>
    /// Icon buttons keep the design's neutral greys: a bundled terminal theme must not tint
    /// them through its foreground. Only the surface they sit on decides between the dark and
    /// light greys, so changing "theme" leaves every icon button the same colour. The chrome
    /// follows the window brightness; pane actions follow the terminal surface brightness.
    /// </summary>
    public static Color ChromeIconStrong() => NeutralStrong(WindowLight);
    public static Color ChromeIconBody() => NeutralBody(WindowLight);
    public static Color ChromeIconFaint() => NeutralFaint(WindowLight);
    public static Color PaneIconStrong() => NeutralStrong(Light);
    public static Color PaneIconFaint() => NeutralFaint(Light);

    private static Color NeutralStrong(bool lightTheme) =>
        lightTheme ? Rgba(255, 27, 28, 32) : Rgba(255, 245, 245, 247);

    private static Color NeutralBody(bool lightTheme) =>
        lightTheme ? Rgba(0xA8, 0, 0, 0) : Rgba(0xAD, 255, 255, 255);

    private static Color NeutralFaint(bool lightTheme) =>
        lightTheme ? Rgba(0x94, 0, 0, 0) : Rgba(0x80, 255, 255, 255);

    /// <summary>
    /// The strip stays transparent (the scrim shows through) whenever the window theme
    /// matches the app theme, preserving the design's original look.
    /// </summary>
    public static Color ChromeHeaderFill()
    {
        if (!WindowChromeDiffers) return Rgba(0, 0, 0, 0);
        return WindowLight ? Rgba(255, 245, 247, 252) : Rgba(255, 37, 41, 52);
    }

    /// <summary>
    /// Accent: the theme's ANSI blue. Ghostty themes fill both the normal (4) and
    /// bright (12) slots, so pick whichever has more contrast against the background.
    /// </summary>
    public static Rgb AccentRgb()
    {
        Rgb normal = Terminal.Palette[4];
        Rgb bright = Terminal.Palette[12];
        double background = PerceivedLuminance(Terminal.Background);
        double normalDistance = Math.Abs(PerceivedLuminance(normal) - background);
        double brightDistance = Math.Abs(PerceivedLuminance(bright) - background);
        return normalDistance >= brightDistance ? normal : bright;
    }

    public static Color Accent() =>
        CustomTheme ? Tint(255, AccentRgb())
                    : Light ? Rgba(255, 0, 95, 184) : Rgba(255, 96, 205, 255);

    public static Color AccentSoft() =>
        CustomTheme ? Tint(Light ? (byte)0x1A : (byte)0x21, AccentRgb())
                    : Light ? Rgba(0x1A, 0, 95, 184) : Rgba(0x21, 96, 205, 255);

    public static Color AccentStroke() =>
        CustomTheme ? Tint(Light ? (byte)0x3D : (byte)0x4D, AccentRgb())
                    : Light ? Rgba(0x3D, 0, 95, 184) : Rgba(0x4D, 96, 205, 255);

    // --- hover fills -----------------------------------------------------------
    public static Color ChipHover() => ChipHoverFor(Light);

    public static Color CloseButtonHover() =>
        CustomTheme ? Tint(Light ? (byte)0x1A : (byte)0x24, Terminal.Foreground)
                    : Light ? Rgba(0x1A, 0, 0, 0) : Rgba(0x24, 255, 255, 255);

    /// <summary>Window caption close: the design uses a brighter red on light chrome.</summary>
    public static Color CloseHover() =>
        WindowLight ? Rgba(255, 0xE8, 0x11, 0x23) : Rgba(255, 0xC4, 0x2B, 0x1C);

    // --- command palette --------------------------------------------------------
    public static Color PaletteBg() =>
        CustomTheme ? Tint(Light ? (byte)247 : (byte)245, Tone(Light ? 0.02 : 0.11))
                    : Light ? Rgba(247, 252, 252, 254) : Rgba(245, 32, 33, 38);

    /// <summary>
    /// Command palette acrylic: TintColor stays opaque and TintOpacity carries the
    /// design's alpha, so the blur reads through the panel.
    /// </summary>
    public static Color PaletteTint() =>
        CustomTheme ? Tint(255, Tone(Light ? 0.02 : 0.11))
                    : Light ? Rgba(255, 252, 252, 254) : Rgba(255, 32, 33, 38);

    public static double PaletteTintOpacity() => Light ? 0.97 : 0.96;
}
