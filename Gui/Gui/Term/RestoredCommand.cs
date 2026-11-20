using System;
using System.IO;

namespace Gui.Term;

/// <summary>
/// The command a restored pane carries. A saved one-off command (a test helper, an
/// installer) can outlive the file it runs, and launching it would make the fresh shell
/// exit immediately, so the command is only reused when its script is still where the
/// pane will look for it.
/// </summary>
internal static class RestoredCommand
{
    /// <summary>
    /// Whether the saved command can still run. Only PowerShell's "-File &lt;script&gt;"
    /// form is checked; every other form is left alone. A relative script path is resolved
    /// the way the shell would see it: against the directory the pane launches in.
    /// </summary>
    public static bool Usable(string command, string directory)
    {
        int file = command.IndexOf("-File ", StringComparison.OrdinalIgnoreCase);
        if (file < 0) return true;

        string path = command[(file + 6)..].Trim();
        if (path.StartsWith('"'))
        {
            int end = path.IndexOf('"', 1);
            if (end < 1) return true;
            path = path[1..end];
        }
        else
        {
            int space = path.IndexOf(' ');
            if (space >= 0) path = path[..space];
        }

        if (path.Length == 0) return true;
        path = Environment.ExpandEnvironmentVariables(path);
        if (!Path.IsPathRooted(path) && directory.Length > 0)
            path = Path.Combine(directory, path);
        return File.Exists(path);
    }
}
