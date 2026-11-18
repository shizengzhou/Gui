namespace Gui.Term;

/// <summary>
/// Whether a grapheme asks, through a variation selector, for its emoji drawing rather than its
/// text one. Ghostty decides the rest from the Unicode character database (an emoji-shaped
/// codepoint defaults to emoji); a grapheme with no selector therefore asks for nothing here,
/// which keeps the configured fallbacks in charge exactly as before.
/// </summary>
internal static class EmojiPresentation
{
    /// <summary>The emoji variation selector: "draw this the way the emoji font does".</summary>
    private const char EmojiSelector = '\uFE0F';

    /// <summary>Whether the grapheme spelled out an emoji presentation.</summary>
    public static bool Asks(string grapheme)
    {
        foreach (char ch in grapheme)
        {
            if (ch == EmojiSelector) return true;
        }
        return false;
    }
}
