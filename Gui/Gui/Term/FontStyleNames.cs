using System;

namespace Gui.Term;

/// <summary>
/// The font face a text style resolves to: a DirectWrite weight plus an italic flag.
/// <see cref="Disabled"/> means the style is unavailable, so the regular face is used.
/// </summary>
public readonly record struct FaceStyle(bool Disabled, ushort Weight, bool Italic);

/// <summary>
/// Maps Ghostty's "font-style" names ("Heavy", "SemiBold Italic", ...) onto the weight and
/// italic pair that CanvasTextFormat takes. Ghostty itself looks the name up in the font's
/// advertised style list; Win2D only exposes weight/slant, so the common family of style
/// names is translated instead.
/// </summary>
public static class FontStyleNames
{
    /// <summary>
    /// Returns the face for a named style, or the style's own default weight when the name
    /// does not describe one. A literal "false" disables the style.
    /// </summary>
    public static FaceStyle Parse(string? value, bool bold, bool italic)
    {
        ushort defaultWeight = bold ? (ushort)700 : (ushort)400;
        if (string.IsNullOrWhiteSpace(value))
            return new FaceStyle(false, defaultWeight, italic);

        string text = value.Trim().ToLowerInvariant();
        if (text == "false")
            return new FaceStyle(true, 400, false);

        // A bare weight number, as kero's "font-weight" and Windows Terminal accept.
        if (ushort.TryParse(text, out ushort numeric) && numeric is >= 1 and <= 999)
            return new FaceStyle(false, numeric, italic);

        ushort? weight = null;
        bool? slant = null;

        foreach (string token in text.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
        {
            switch (token)
            {
                case "thin" or "hairline":
                    weight = 100;
                    break;
                case "extralight" or "ultralight":
                    weight = 200;
                    break;
                case "light":
                    weight = 300;
                    break;
                case "semilight" or "demilight":
                    weight = 350;
                    break;
                case "regular" or "normal" or "book":
                    weight = 400;
                    break;
                case "medium":
                    weight = 500;
                    break;
                case "semibold" or "demibold":
                    weight = 600;
                    break;
                case "bold":
                    weight = 700;
                    break;
                case "extrabold" or "ultrabold":
                    weight = 800;
                    break;
                case "black" or "heavy":
                    weight = 900;
                    break;
                case "extrablack" or "ultrablack":
                    weight = 950;
                    break;
                case "italic" or "oblique":
                    slant = true;
                    break;
            }
        }

        return new FaceStyle(false, weight ?? defaultWeight, slant ?? italic);
    }
}
