using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>
/// The locale a text format hands DirectWrite. A terminal font rarely carries CJK glyphs, so
/// those come from DirectWrite's own system fallback, and that fallback is chosen by locale:
/// asking with the user's own locale (en-US on an English install) answers with a Japanese
/// face, and 门 is then drawn the Japanese way, with the stroke crossing the top bar. Asking
/// with the first CJK language the user has installed keeps Han text in that language's face.
/// </summary>
internal static class FontLocale
{
    /// <summary>
    /// The first Chinese, Japanese or Korean language in <paramref name="languages"/>, else
    /// <paramref name="otherwise"/> — the locale the process would use anyway.
    /// </summary>
    public static string ForGlyphFallback(IEnumerable<string> languages, string otherwise)
    {
        if (languages is not null)
        {
            foreach (string language in languages)
            {
                if (IsCjk(language)) return language;
            }
        }
        return otherwise;
    }

    /// <summary>Whether the language's primary subtag is Chinese, Japanese or Korean.</summary>
    private static bool IsCjk(string language)
    {
        if (string.IsNullOrEmpty(language)) return false;

        int dash = language.IndexOf('-');
        string primary = dash < 0 ? language : language[..dash];
        return primary.Equals("zh", StringComparison.OrdinalIgnoreCase)
            || primary.Equals("ja", StringComparison.OrdinalIgnoreCase)
            || primary.Equals("ko", StringComparison.OrdinalIgnoreCase);
    }
}
