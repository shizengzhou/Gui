using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>
/// Expands a double-click on one row of cell text to the word around it. The boundary
/// characters come from "selection-word-chars"; blank cells and whitespace always stop a
/// word, as does U+0000, mirroring Ghostty.
/// </summary>
public static class WordSelection
{
    /// <summary>
    /// Returns the inclusive cell range of the word at <paramref name="column"/>. False
    /// when the cell is blank or is itself a boundary character, in which case there is no
    /// word to select.
    /// </summary>
    public static bool TryExpand(IReadOnlyList<string> cells, int column, string? boundaryChars,
        out int start, out int end)
    {
        start = -1;
        end = -1;
        if (cells.Count == 0 || column < 0 || column >= cells.Count) return false;
        if (!IsWord(cells[column], boundaryChars)) return false;

        start = column;
        while (start - 1 >= 0 && IsWord(cells[start - 1], boundaryChars)) start--;

        end = column;
        while (end + 1 < cells.Count && IsWord(cells[end + 1], boundaryChars)) end++;

        return true;
    }

    /// <summary>True when the grapheme can be part of a word under the given boundary set.</summary>
    public static bool IsWord(string grapheme, string? boundaryChars)
    {
        if (string.IsNullOrEmpty(grapheme)) return false;
        if (char.IsWhiteSpace(grapheme[0])) return false;

        int codepoint = CodePointAt(grapheme);
        if (codepoint == 0) return false;

        if (string.IsNullOrEmpty(boundaryChars)) return true;

        string text = char.ConvertFromUtf32(codepoint);
        return boundaryChars.IndexOf(text, StringComparison.Ordinal) < 0;
    }

    private static int CodePointAt(string grapheme) =>
        char.IsHighSurrogate(grapheme[0]) && grapheme.Length > 1
            ? char.ConvertToUtf32(grapheme[0], grapheme[1])
            : grapheme[0];
}
