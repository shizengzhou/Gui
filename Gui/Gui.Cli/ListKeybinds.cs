using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The "+list-keybinds" action. Mirrors ghostty's +list-keybinds: on a terminal it draws the
/// same colour-coded table (each modifier in its own colour, the action in its own column),
/// and when stdout is redirected it falls back to ghostty's config format
/// `keybind = &lt;trigger&gt;=&lt;action&gt;` so the output can be diffed or pasted into a config file.
///
/// The rows come from Term/Keybinds.cs, the same map the window and the command palette use,
/// so "keybind" lines that add, replace, unbind or "clear" bindings are reflected instead of
/// being listed from a second table here.
/// </summary>
public static class ListKeybinds
{
    /// <summary>One row: chord in ghostty spelling, action id, one-line summary (docs only).</summary>
    public sealed record Entry(string Chord, string Action, string Description);

    public const string Help =
        "Usage: gui-cli +list-keybinds [options]\n" +
        "\n" +
        "Print the key bindings in effect. On a terminal this draws a table; when the output\n" +
        "is redirected (or --plain is given) it prints ghostty \"keybind\" lines instead.\n" +
        "\n" +
        "Options:\n" +
        "  --default          print the built-in bindings, ignoring the config file\n" +
        "  --docs             add the action name and a short description above each line\n" +
        "                     (ignored by the table, like ghostty)\n" +
        "  --plain            never colour the output, even on a terminal\n" +
        "  --no-pager         accepted for parity; this tool never pages\n" +
        "  -h, --help         show this help\n" +
        "\n" +
        "The table is coloured only on a console. NO_COLOR or TERM=dumb turns colour off,\n" +
        "CLICOLOR_FORCE=1 forces it through a pipe.\n";

    /// <summary>
    /// The bindings in effect, sorted the way ghostty sorts its table: most modifiers first,
    /// then modifier value (super, alt, ctrl, shift), then the key name.
    /// </summary>
    internal static IReadOnlyList<Entry> Snapshot(Keybinds keybinds)
    {
        var entries = new List<Entry>();
        foreach ((string chord, string action) in keybinds.Effective())
            entries.Add(new Entry(Canonical(chord), action, Describe(action)));

        entries.Sort(CompareEntries);
        return entries;
    }

    /// <summary>
    /// Renders the ghostty config form. Pure and console-free so the tests can check the
    /// format without spawning anything.
    /// </summary>
    public static string RenderPlain(IReadOnlyList<Entry> entries, bool docs)
    {
        var sb = new StringBuilder();
        foreach (Entry entry in entries)
        {
            if (docs)
            {
                sb.Append('\n');
                sb.Append("# ").Append(entry.Action).Append('\n');
                if (entry.Description.Length > 0)
                    sb.Append("#   ").Append(entry.Description).Append('\n');
            }

            sb.Append("keybind = ").Append(entry.Chord).Append('=').Append(entry.Action).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders ghostty's coloured table: the chord in the left column (each modifier in its
    /// own palette colour), the action three cells past the widest chord, with the ":" dimmed
    /// and an argument coloured. Pure so the tests can assert on the escapes.
    /// </summary>
    public static string RenderTable(IReadOnlyList<Entry> entries, bool color = true)
    {
        int widest = 0;
        var plainChords = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            plainChords[i] = ChordText(entries[i].Chord, null);
            widest = Math.Max(widest, plainChords[i].Length);
        }

        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            sb.Append(color ? ChordText(entries[i].Chord, Palette) : plainChords[i]);
            sb.Append(' ', widest - plainChords[i].Length + 3);
            AppendAction(sb, entries[i].Action, color);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Parses the flags, loads the config and writes the list to stdout.</summary>
    public static int Run(string[] args)
    {
        bool docs = false;
        bool plain = false;
        bool useDefaults = false;

        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--docs": docs = true; break;
                case "--plain": plain = true; break;
                case "--default": useDefaults = true; break;
                // This tool never pages, so the flag is a no-op.
                case "--no-pager": break;
                case "-h":
                case "--help":
                    Console.Out.Write(Help);
                    return 0;
                default:
                    Console.Error.WriteLine($"gui-cli: unknown option '{arg}' for +list-keybinds");
                    Console.Error.Write(Help);
                    return 2;
            }
        }

        // --default ignores the user's config entirely, like ghostty.
        Config config = useDefaults ? new Config() : Config.LoadRaw(Config.ConfigFile());
        IReadOnlyList<Entry> entries = Snapshot(new Keybinds(config));

        bool color = !plain && WantsColor();
        Console.Out.Write(color ? RenderTable(entries) : RenderPlain(entries, docs));
        return 0;
    }

    // ---------------- table ----------------

    // ghostty paints each modifier with a fixed palette index: super 1, ctrl 2, alt 3,
    // shift 4. An action's ":" is dim and its argument is 5.
    private static readonly Dictionary<string, int> Palette = new(StringComparer.Ordinal)
    {
        ["super"] = 1,
        ["ctrl"] = 2,
        ["alt"] = 3,
        ["shift"] = 4,
    };

    private const string Reset = "\u001b[0m";

    /// <summary>
    /// ghostty pads every modifier name to five cells so the '+' signs line up and puts
    /// " + " between them; the key name follows unadorned.
    /// </summary>
    private static string ChordText(string chord, Dictionary<string, int>? palette)
    {
        string[] parts = chord.Split('+');
        var sb = new StringBuilder();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            string name = parts[i];
            string label = name.PadRight(5);
            if (palette is not null && palette.TryGetValue(name, out int index))
                sb.Append("\u001b[38;5;").Append(index).Append('m').Append(label).Append(Reset);
            else
                sb.Append(label);

            sb.Append(" + ");
        }

        sb.Append(parts[^1]);
        return sb.ToString();
    }

    private static void AppendAction(StringBuilder sb, string action, bool color)
    {
        int colon = action.IndexOf(':');
        if (colon < 0)
        {
            sb.Append(action);
            return;
        }

        sb.Append(action, 0, colon);
        if (color)
        {
            sb.Append("\u001b[2m:").Append(Reset);
            sb.Append("\u001b[38;5;5m").Append(action, colon + 1, action.Length - colon - 1).Append(Reset);
        }
        else
        {
            sb.Append(':').Append(action, colon + 1, action.Length - colon - 1);
        }
    }

    /// <summary>
    /// Rewrites a chord into ghostty's display order (super, ctrl, alt, shift), which is the
    /// order its table prints and also a valid config spelling. Keybinds.cs stores chords in
    /// its own canonical order (ctrl, shift, alt, super), so the two differ on chords that
    /// mix modifiers.
    /// </summary>
    private static string Canonical(string chord)
    {
        string[] parts = chord.Split('+');
        var modifiers = new HashSet<string>(parts[..^1], StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (string name in new[] { "super", "ctrl", "alt", "shift" })
        {
            if (!modifiers.Contains(name)) continue;
            sb.Append(name).Append('+');
        }

        sb.Append(parts[^1]);
        return sb.ToString();
    }

    private static int CompareEntries(Entry a, Entry b)
    {
        (int mods, int count, string key) = Split(a.Chord);
        (int otherMods, int otherCount, string otherKey) = Split(b.Chord);

        if (count != otherCount) return otherCount.CompareTo(count);
        if (mods != otherMods) return otherMods.CompareTo(mods);
        int byKey = string.CompareOrdinal(key, otherKey);
        return byKey != 0 ? byKey : string.CompareOrdinal(a.Action, b.Action);
    }

    /// <summary>Modifier bit value, modifier count and key name of a chord.</summary>
    private static (int Mods, int Count, string Key) Split(string chord)
    {
        string[] parts = chord.Split('+');
        int mods = 0;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            mods |= parts[i] switch
            {
                "super" => GhosttyNative.MODS_SUPER,
                "alt" => GhosttyNative.MODS_ALT,
                "ctrl" => GhosttyNative.MODS_CTRL,
                "shift" => GhosttyNative.MODS_SHIFT,
                _ => 0,
            };
        }

        return (mods, parts.Length - 1, parts[^1]);
    }

    /// <summary>
    /// Colour only when something is watching: a real console, no NO_COLOR, and a terminal
    /// that is not dumb. Windows consoles need VT processing switched on first.
    /// </summary>
    private static bool WantsColor()
    {
        // Ghostty colours only on a tty. Here the test is "is stdout a console handle?",
        // which is what Vt.Enable() reports, because Console.IsOutputRedirected lies when a
        // shell re-opens CON for the child (cmd's "> CON").
        string? force = Environment.GetEnvironmentVariable("CLICOLOR_FORCE");
        bool forced = !string.IsNullOrEmpty(force) && force != "0";

        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        if (string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)) return false;

        // A forced run through a pipe has no console mode to flip, so trust the reader.
        return Vt.Enable() || forced;
    }

    private static class Vt
    {
        private const int StdOutputHandle = -11;
        private const int EnableVirtualTerminalProcessing = 0x0004;

        public static bool Enable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return true;

            IntPtr stdout = GetStdHandle(StdOutputHandle);
            if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) return false;
            if (!GetConsoleMode(stdout, out int mode)) return false;
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(stdout, mode | EnableVirtualTerminalProcessing);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr handle, out int mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr handle, int mode);
    }

    // ---------------- docs ----------------

    /// <summary>One-line summary per action id, used by --docs and never by the table.</summary>
    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        ["new_tab"] = "Open a new tab in the current window.",
        ["new_window"] = "Open a new window.",
        ["next_tab"] = "Focus the next tab.",
        ["previous_tab"] = "Focus the previous tab.",
        ["move_tab:1"] = "Move the current tab one slot to the right.",
        ["move_tab:-1"] = "Move the current tab one slot to the left.",
        ["last_tab"] = "Focus the previously active tab.",
        ["toggle_sidebar"] = "Show or hide the tab rail.",
        ["new_split:right"] = "Split the focused pane to the right.",
        ["new_split:down"] = "Split the focused pane downwards.",
        ["goto_split:previous"] = "Focus the previous split.",
        ["goto_split:next"] = "Focus the next split.",
        ["goto_split:left"] = "Focus the split to the left.",
        ["goto_split:right"] = "Focus the split to the right.",
        ["goto_split:up"] = "Focus the split above.",
        ["goto_split:down"] = "Focus the split below.",
        ["toggle_split_zoom"] = "Zoom the focused split, or restore the tab's layout.",
        ["resize_split:left"] = "Move the split boundary to the left.",
        ["resize_split:right"] = "Move the split boundary to the right.",
        ["resize_split:up"] = "Move the split boundary up.",
        ["resize_split:down"] = "Move the split boundary down.",
        ["increase_font_size:1"] = "Increase the font size by one point.",
        ["decrease_font_size:1"] = "Decrease the font size by one point.",
        ["reset_font_size"] = "Reset the font size to the configured size.",
        ["scroll_to_top"] = "Scroll to the top of the scrollback.",
        ["scroll_to_bottom"] = "Scroll to the bottom of the scrollback.",
        ["scroll_page_up"] = "Scroll up by a page.",
        ["scroll_page_down"] = "Scroll down by a page.",
        ["adjust_selection:left"] = "Extend the selection one cell to the left.",
        ["adjust_selection:right"] = "Extend the selection one cell to the right.",
        ["adjust_selection:up"] = "Extend the selection one row up.",
        ["adjust_selection:down"] = "Extend the selection one row down.",
        ["select_all"] = "Select the whole screen and scrollback.",
        ["jump_to_prompt:-1"] = "Jump to the previous shell prompt.",
        ["jump_to_prompt:1"] = "Jump to the next shell prompt.",
        ["copy_to_clipboard"] = "Copy the selection to the clipboard.",
        ["paste_from_clipboard"] = "Paste the clipboard into the focused shell.",
        ["paste_from_selection"] = "Paste the current selection into the shell.",
        ["start_search"] = "Open the find bar in the focused pane.",
        ["toggle_command_palette"] = "Open the command palette.",
        ["write_screen_file:copy"] = "Write the screen to a temp file and copy its path.",
        ["write_screen_file:paste"] = "Write the screen to a temp file and paste its path.",
        ["write_screen_file:open"] = "Write the screen to a temp file and open it.",
        ["toggle_fullscreen"] = "Toggle fullscreen.",
        ["close_surface"] = "Close the focused pane, then the tab.",
        ["close_window"] = "Close the window.",
        ["quit"] = "Quit, closing every window.",
        ["open_config"] = "Open the config file with the OS editor.",
        ["reload_config"] = "Re-read the config file and apply it live.",
    };

    private static string Describe(string action) =>
        Summaries.TryGetValue(action, out string? text) ? text : string.Empty;
}