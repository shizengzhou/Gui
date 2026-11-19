using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gui.Term;

/// <summary>
/// One window's restorable state ("window-save-state"). Like Kero it also keeps each pane's
/// terminal snapshot, so the old screen and scrollback come back above the new shell; only
/// running programs are gone, since restore always starts a fresh shell.
/// </summary>
internal sealed class WindowState
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>Monitor scale (DPI / 96) the geometry was captured at, for DPI migration.</summary>
    public double Scale { get; set; } = 1.0;

    public bool Maximized { get; set; }
    public bool FullScreen { get; set; }

    /// <summary>Local time the session was captured, shown in each pane's restore divider.</summary>
    public DateTime SavedAt { get; set; }

    public int ActiveTab { get; set; }
    public List<TabState> Tabs { get; set; } = new();
}

/// <summary>A tab: its title, zoom state, the focused pane's path and the split tree.</summary>
internal sealed class TabState
{
    public string Title { get; set; } = string.Empty;
    public string? TitleOverride { get; set; }
    public bool Zoomed { get; set; }

    /// <summary>Path to the focused pane: one 'L' or 'R' per split node, empty for a lone pane.</summary>
    public string Focused { get; set; } = string.Empty;

    public SplitState? Root { get; set; }
}

/// <summary>A split node: either a pane leaf or two children plus their ratio.</summary>
internal sealed class SplitState
{
    public bool Vertical { get; set; }
    public double Ratio { get; set; } = 0.5;
    public PaneState? Pane { get; set; }
    public SplitState? First { get; set; }
    public SplitState? Second { get; set; }
}

/// <summary>What a restored pane needs to start a fresh shell in the same place.</summary>
internal sealed class PaneState
{
    public string Directory { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// True when the shell came from the default resolution rather than an explicit
    /// "command". Such a pane must not be pinned to the executable it happened to resolve:
    /// that would change how the shell launches (and which icon it shows) after a restore.
    /// Older session files predate the flag, so the default is true.
    /// </summary>
    public bool AutoCommand { get; set; } = true;

    public string Title { get; set; } = string.Empty;

    /// <summary>File name (in the session directory) of this pane's terminal snapshot.</summary>
    public string? Snapshot { get; set; }
}

/// <summary>
/// The on-disk shape: a version plus one entry per window. A source-generated context is
/// used because the shipped app is trimmed, where reflection-based serialization is off.
/// </summary>
internal sealed class SessionFile
{
    public int Version { get; set; } = WindowStateStore.CurrentVersion;
    public List<WindowState> Windows { get; set; } = new();
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionFile))]
internal partial class WindowStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads and writes %APPDATA%\gui\window-state.json. Writes go through a temporary file so a
/// crash mid-save cannot leave a truncated session behind; read failures are treated as "no
/// saved session" rather than failing launch.
/// </summary>
internal static class WindowStateStore
{
    internal const int CurrentVersion = 1;

    public static string StateFile()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return string.Empty;
        return Path.Combine(appData, "gui", "window-state.json");
    }

    /// <summary>%APPDATA%\gui\session: one snapshot file per pane, next to the session file.</summary>
    public static string SessionDirectory()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return string.Empty;
        return Path.Combine(appData, "gui", "session");
    }

    /// <summary>
    /// Writes one pane snapshot through <paramref name="write"/> and returns the file name
    /// stored in the session file. The writer receives the temporary path so the snapshot can
    /// go straight from the library to disk instead of through a large managed array.
    /// </summary>
    public static string? WriteSnapshot(Func<string, bool> write)
    {
        string directory = SessionDirectory();
        if (string.IsNullOrEmpty(directory)) return null;

        try
        {
            Directory.CreateDirectory(directory);
            string name = Guid.NewGuid().ToString("N") + ".snap";
            string path = Path.Combine(directory, name);
            string temp = path + ".tmp";
            if (!write(temp))
            {
                TryDelete(temp);
                return null;
            }
            File.Move(temp, path, true);
            return name;
        }
        catch (Exception error)
        {
            Diag.Warn("window-state.snapshot", error.Message);
            return null;
        }
    }

    /// <summary>True when the named snapshot file still exists in the session directory.</summary>
    public static bool SnapshotExists(string? name)
    {
        string directory = SessionDirectory();
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name)) return false;

        // Only a bare file name is accepted: the session file is user-editable.
        if (Path.GetFileName(name) != name) return false;
        return File.Exists(Path.Combine(directory, name));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error)
        {
            Diag.Warn("window-state.snapshot", error.Message);
        }
    }

    /// <summary>Reads a pane snapshot; null when the file is missing or unreadable.</summary>
    public static byte[]? ReadSnapshot(string? name)
    {
        string directory = SessionDirectory();
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name)) return null;

        // Only a bare file name is accepted: the session file is user-editable.
        if (Path.GetFileName(name) != name) return null;

        try
        {
            string path = Path.Combine(directory, name);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception error)
        {
            Diag.Warn("window-state.readSnapshot", error.Message);
            return null;
        }
    }

    /// <summary>The windows of the last saved session, or null when there is none usable.</summary>
    public static List<WindowState>? Load()
    {
        string file = StateFile();
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
        return Load(file);
    }

    /// <summary>The same, against an explicit file so tests do not touch the user's session.</summary>
    public static List<WindowState>? Load(string file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;

        try
        {
            SessionFile? data = JsonSerializer.Deserialize<SessionFile>(
                File.ReadAllText(file, Encoding.UTF8), WindowStateJsonContext.Default.SessionFile);
            if (data is null || data.Version != CurrentVersion) return null;
            return data.Windows.Count > 0 ? data.Windows : null;
        }
        catch (Exception error)
        {
            // A corrupt or hand-edited file falls back to a normal single-window launch.
            Diag.Warn("window-state.load", error.Message);
            return null;
        }
    }

    public static void Save(IReadOnlyList<WindowState> windows)
    {
        string file = StateFile();
        if (string.IsNullOrEmpty(file) || !Save(file, windows)) return;
        CleanupSnapshots(windows);
    }

    public static bool Save(string file, IReadOnlyList<WindowState> windows)
    {
        if (string.IsNullOrEmpty(file)) return false;

        try
        {
            string? directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var data = new SessionFile { Windows = new List<WindowState>(windows) };
            string temp = file + ".tmp";
            File.WriteAllText(temp,
                JsonSerializer.Serialize(data, WindowStateJsonContext.Default.SessionFile),
                new UTF8Encoding(false));
            File.Move(temp, file, true);
            return true;
        }
        catch (Exception error)
        {
            // Persisting the session must never take the app down.
            Diag.Warn("window-state.save", error.Message);
            return false;
        }
    }

    /// <summary>
    /// Deletes pane snapshots the saved session no longer references. Each save writes new
    /// snapshot files, so without this the directory would grow with every window close.
    /// </summary>
    private static void CleanupSnapshots(IReadOnlyList<WindowState> windows)
    {
        string directory = SessionDirectory();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WindowState window in windows)
        {
            foreach (TabState tab in window.Tabs) CollectSnapshots(tab.Root, keep);
        }

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.snap"))
            {
                if (keep.Contains(Path.GetFileName(path))) continue;
                File.Delete(path);
            }
            foreach (string path in Directory.EnumerateFiles(directory, "*.tmp"))
                File.Delete(path);
        }
        catch (Exception error)
        {
            Diag.Warn("window-state.cleanup", error.Message);
        }
    }

    private static void CollectSnapshots(SplitState? node, HashSet<string> keep)
    {
        if (node is null) return;
        if (node.Pane is { Snapshot: { Length: > 0 } name }) keep.Add(name);
        CollectSnapshots(node.First, keep);
        CollectSnapshots(node.Second, keep);
    }
}
