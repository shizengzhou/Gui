using System.Collections.Generic;
using System.Text;

namespace Gui.Term;

/// <summary>
/// Turns files on the clipboard into text to paste, the way Ghostty does: each file
/// becomes one shell-safe path token and the tokens are joined with spaces. Kept free
/// of WinRT and UI so the quoting can be tested on its own.
/// </summary>
internal static class ClipboardPaths
{
    /// <summary>
    /// Quotes a path so a shell reads it as a single argument. Double quotes work in
    /// cmd, PowerShell and Git Bash on Windows; a double quote cannot occur in a
    /// Windows file name, so nothing inside needs escaping.
    /// </summary>
    internal static string Quote(string path)
    {
        var result = new StringBuilder(path.Length + 2);
        result.Append('"');
        result.Append(path);
        result.Append('"');
        return result.ToString();
    }

    /// <summary>Joins paths into one pasteable line, one quoted token per file.</summary>
    internal static string Join(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return string.Empty;

        var result = new StringBuilder();
        for (int i = 0; i < paths.Count; i++)
        {
            if (i > 0) result.Append(' ');
            result.Append(Quote(paths[i]));
        }
        return result.ToString();
    }
}
