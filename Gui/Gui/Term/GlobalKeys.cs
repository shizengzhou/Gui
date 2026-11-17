using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>
/// Process-wide registration of the "global:" chords: they have to fire while another
/// application has the focus, so the set is registered once with the OS and rebuilt only
/// when the config's global lines actually change.
///
/// The quick terminal used to own this because it was the only global action; a global
/// binding now dispatches through the same action table as every other chord, against the
/// last activated window.
/// </summary>
internal static class GlobalKeys
{
    private static readonly List<GlobalHotkey> _hotkeys = new();
    private static List<(string Chord, string Action)> _registered = new();

    /// <summary>
    /// Registers every global chord of <paramref name="config"/>. Called on startup and
    /// whenever a window re-reads the config; an unchanged set is left alone, so opening a
    /// second window does not drop and retake the hotkeys.
    /// </summary>
    public static void Apply(Config config)
    {
        var wanted = new List<(string Chord, string Action)>();
        foreach ((string chord, string action) in new Keybinds(config).Global())
            wanted.Add((chord, action));

        if (Same(wanted)) return;

        foreach (GlobalHotkey hotkey in _hotkeys) hotkey.Dispose();
        _hotkeys.Clear();

        foreach ((string chord, string action) in wanted)
        {
            GlobalHotkey? hotkey = GlobalHotkey.TryRegister(chord, () => Fire(action), out string error);
            if (hotkey is null)
            {
                // Another application owns the chord; the binding still works inside the app.
                Diag.Warn("hotkey", $"{chord}: {error}");
                continue;
            }

            _hotkeys.Add(hotkey);
            Diag.Log("hotkey", $"{chord} -> {action}");
        }

        _registered = wanted;
        if (wanted.Count == 0) Diag.Log("hotkey", "no global chord is registered");
    }

    private static bool Same(List<(string Chord, string Action)> wanted)
    {
        if (wanted.Count != _registered.Count) return false;
        for (int i = 0; i < wanted.Count; i++)
            if (wanted[i] != _registered[i]) return false;
        return true;
    }

    /// <summary>
    /// Runs a global chord's action on the last activated window. "global:" implies "all:",
    /// so the action is applied to the whole application (see Workspace.RunGlobalAction).
    /// The quick terminal is the one action that works without a window.
    /// </summary>
    private static void Fire(string action)
    {
        // RegisterHotKey takes the chord away from the focused window, so this is the only
        // place a global binding runs; the local key path never sees it.
        if (App.ActiveWorkspace is { } workspace)
        {
            workspace.RunGlobalAction(action);
            return;
        }

        if (string.Equals(action, "toggle_quick_terminal", StringComparison.Ordinal))
        {
            QuickTerminal.Toggle();
            return;
        }

        Diag.Debug("hotkey", $"'{action}' has no window to run on");
    }
}