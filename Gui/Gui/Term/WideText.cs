using System.Text;

namespace Gui.Term;

/// <summary>
/// East Asian width rules for the terminal grid: a wide or fullwidth grapheme takes two
/// cells, everything else takes one. This lives apart from the renderer so the rules can be
/// unit tested without pulling in Win2D.
/// </summary>
internal static class WideText
{
    /// <summary>Whether the grapheme is rendered in two cells.</summary>
    public static bool IsWide(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // ASCII and Latin-1 are never double width; skip the rune decode for them.
        if (text[0] < 0x1100) return false;
        if (!Rune.TryGetRuneAt(text, 0, out Rune rune)) return false;

        int cp = rune.Value;
        return cp is (>= 0x1100 and <= 0x115F)     // Hangul Jamo
            or (>= 0x2E80 and <= 0x303E)           // CJK radicals, Kangxi, CJK symbols
            or (>= 0x3041 and <= 0x33FF)           // Kana, CJK compatibility
            or (>= 0x3400 and <= 0x4DBF)           // CJK extension A
            or (>= 0x4E00 and <= 0x9FFF)           // CJK unified ideographs
            or (>= 0xA000 and <= 0xA4CF)           // Yi
            or (>= 0xA960 and <= 0xA97F)           // Hangul Jamo extended A
            or (>= 0xAC00 and <= 0xD7A3)           // Hangul syllables
            or (>= 0xF900 and <= 0xFAFF)           // CJK compatibility ideographs
            or (>= 0xFE10 and <= 0xFE19)           // Vertical forms
            or (>= 0xFE30 and <= 0xFE6F)           // CJK compatibility forms
            or (>= 0xFF00 and <= 0xFF60)           // Fullwidth forms
            or (>= 0xFFE0 and <= 0xFFE6)           // Fullwidth signs
            or (>= 0x1F300 and <= 0x1F64F)         // Emoji
            or (>= 0x1F900 and <= 0x1F9FF)         // Supplemental emoji
            or (>= 0x20000 and <= 0x3FFFD);        // CJK extensions B and beyond
    }
}
