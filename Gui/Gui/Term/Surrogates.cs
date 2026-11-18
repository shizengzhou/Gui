using System;

namespace Gui.Term;

/// <summary>
/// Joins the two halves of a UTF-16 surrogate pair when they arrive separately. WinUI's text
/// box takes a character outside the BMP (an emoji, a rare CJK ideograph) as two edits, so a
/// terminal that forwards each edit on its own would encode each half as U+FFFD; holding the
/// high half until the low one shows up keeps the pair whole.
/// </summary>
internal static class Surrogates
{
    /// <summary>
    /// Completes a pair split across two calls and holds a trailing high half back in
    /// <paramref name="pending"/> for the next one. A half that never finds a partner is
    /// dropped, because it has no UTF-8 form.
    /// </summary>
    public static string Join(string text, ref char pending)
    {
        if (pending != '\0')
        {
            if (text.Length > 0 && char.IsLowSurrogate(text[0]))
                text = pending + text;
            pending = '\0';
        }
        else if (text.Length > 0 && char.IsLowSurrogate(text[0]))
        {
            // A low half with nothing to join: it can only come from a broken source.
            text = text[1..];
        }

        if (text.Length > 0 && char.IsHighSurrogate(text[^1]))
        {
            pending = text[^1];
            text = text[..^1];
        }

        return text;
    }
}
