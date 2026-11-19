using System;
using System.Collections.Generic;
using System.Text;
using Windows.System;

namespace Gui.Term;

/// <summary>How a chord from the keyboard relates to the configured keybind lines.</summary>
internal enum KeybindMatch
{
    /// <summary>No "keybind" line mentions this chord.</summary>
    None,

    /// <summary>The chord is bound to an action.</summary>
    Action,

    /// <summary>The chord was explicitly unbound, so it belongs to the shell.</summary>
    Unbound,
}

/// <summary>Where a matched binding lives and how it has to be dispatched.</summary>
internal readonly record struct KeybindMatchInfo(
    string? Table, bool Performable, bool All, bool Unconsumed)
{
    public static readonly KeybindMatchInfo None = new(null, false, false, false);
}

/// <summary>How one key advances a multi-key trigger sequence.</summary>
internal enum KeybindStep
{
    /// <summary>No sequence is open and this chord does not start one.</summary>
    None,

    /// <summary>The chord is a sequence prefix; the next key decides the binding.</summary>
    Pending,

    /// <summary>The chord completed a sequence bound to an action.</summary>
    Action,

    /// <summary>A sequence was open and this chord does not continue it.</summary>
    Miss,
}

/// <summary>
/// Parses Ghostty's "keybind = &lt;trigger&gt;=&lt;action&gt;" lines and answers two questions:
/// which action a chord runs, and which chord the palette should advertise for an action.
///
/// Triggers are normalised to a canonical "ctrl+shift+key" form, so the order the modifiers
/// were written in does not matter. A trigger may be several chords separated by "&gt;"
/// (Ghostty's trigger sequence); the chords are joined the same way and `Step` advances the
/// sequence one key at a time. Defaults stay in the hardcoded key handler; this class only
/// carries the user's additions, removals and the "clear" state.
/// </summary>
internal sealed class Keybinds
{
    /// <summary>
    /// One set of bindings: the default set (Name is empty) or a named key table. A lookup
    /// walks the active tables from the innermost outwards and ends at the default set.
    /// </summary>
    private sealed class BindingSet
    {
        public BindingSet(string name) => Name = name;

        public string Name { get; }
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Unbound { get; } = new(StringComparer.Ordinal);

        /// <summary>Chords written with the "performable:" prefix.</summary>
        public HashSet<string> Performable { get; } = new(StringComparer.Ordinal);

        /// <summary>Chords written with the "all:" (or "global:") prefix.</summary>
        public HashSet<string> All { get; } = new(StringComparer.Ordinal);

        /// <summary>Chords written with the "physical:" prefix.</summary>
        public HashSet<string> Physical { get; } = new(StringComparer.Ordinal);

        /// <summary>Chords written with the "unconsumed:" prefix.</summary>
        public HashSet<string> Unconsumed { get; } = new(StringComparer.Ordinal);
        public List<(string Chord, string Action)> Ordered { get; } = new();

        /// <summary>True when a longer, still-bound sequence starts with this chord.</summary>
        public bool IsPrefix(string chord)
        {
            foreach (KeyValuePair<string, string> entry in Bindings)
            {
                if (entry.Key.Length <= chord.Length) continue;
                if (entry.Key[chord.Length] != '>') continue;
                if (entry.Key.StartsWith(chord, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>Forgets every line that named this chord.</summary>
        public void Remove(string chord)
        {
            Bindings.Remove(chord);
            Unbound.Remove(chord);
            Performable.Remove(chord);
            All.Remove(chord);
            Physical.Remove(chord);
            Unconsumed.Remove(chord);
            Ordered.RemoveAll(entry => entry.Chord == chord);
        }

        /// <summary>
        /// Drops every sequence that starts with the given chord. Ghostty does this when a
        /// non-sequence binding uses a chord that was previously a sequence prefix.
        /// </summary>
        public void RemoveSequencesUnder(string chord)
        {
            string prefix = chord + ">";
            var stale = new List<string>();
            foreach (string key in Bindings.Keys)
                if (key.StartsWith(prefix, StringComparison.Ordinal)) stale.Add(key);
            foreach (string key in stale) Bindings.Remove(key);

            Unbound.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
            Performable.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
            All.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
            Physical.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
            Unconsumed.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
            Ordered.RemoveAll(entry => entry.Chord.StartsWith(prefix, StringComparison.Ordinal));
        }

        public void Clear()
        {
            Bindings.Clear();
            Unbound.Clear();
            Performable.Clear();
            All.Clear();
            Physical.Clear();
            Unconsumed.Clear();
            Ordered.Clear();
        }
    }

    private readonly BindingSet _root = new(string.Empty);

    /// <summary>The named key tables the config defined, keyed by name.</summary>
    private readonly Dictionary<string, BindingSet> _tables = new(StringComparer.Ordinal);

    /// <summary>Chords the user marked with the "global:" prefix, in canonical form.</summary>
    private readonly HashSet<string> _global = new(StringComparer.Ordinal);
    private bool _cleared;

    /// <summary>
    /// True once a binding names a Unicode codepoint: only then does the caller have to
    /// probe the layout for the character a key produces (a user32 call per key press).
    /// </summary>
    private bool _hasCodepoints;

    /// <summary>True when a codepoint binding exists, so a probe is worth doing.</summary>
    public bool HasCodepoints => _hasCodepoints;

    /// <summary>
    /// The default chords of the actions this build implements, in the order the UI prefers
    /// to show them. The first active chord is the one the palette and tooltips advertise.
    /// </summary>
    private static readonly Dictionary<string, string[]> Defaults = new(StringComparer.Ordinal)
    {
        ["new_tab"] = new[] { "ctrl+shift+t" },
        ["new_window"] = new[] { "ctrl+shift+n" },
        ["next_tab"] = new[] { "ctrl+tab", "ctrl+page_down", "ctrl+shift+right" },
        ["previous_tab"] = new[] { "ctrl+shift+tab", "ctrl+page_up", "ctrl+shift+left" },
        ["move_tab:-1"] = new[] { "ctrl+shift+page_up" },
        ["move_tab:1"] = new[] { "ctrl+shift+page_down" },
        ["last_tab"] = new[] { "alt+9" },
        // The Alt+1..8 slots are hardcoded in the key handler; listing them here keeps the
        // shortcut page and "+list-keybinds" complete and lets a config line rebind them.
        ["goto_tab:1"] = new[] { "alt+1" },
        ["goto_tab:2"] = new[] { "alt+2" },
        ["goto_tab:3"] = new[] { "alt+3" },
        ["goto_tab:4"] = new[] { "alt+4" },
        ["goto_tab:5"] = new[] { "alt+5" },
        ["goto_tab:6"] = new[] { "alt+6" },
        ["goto_tab:7"] = new[] { "alt+7" },
        ["goto_tab:8"] = new[] { "alt+8" },
        ["toggle_sidebar"] = new[] { "ctrl+shift+b" },

        ["new_split:right"] = new[] { "ctrl+shift+o" },
        ["new_split:down"] = new[] { "ctrl+shift+e" },
        ["goto_split:previous"] = new[] { "ctrl+super+bracket_left" },
        ["goto_split:next"] = new[] { "ctrl+super+bracket_right" },
        ["goto_split:left"] = new[] { "ctrl+alt+left" },
        ["goto_split:right"] = new[] { "ctrl+alt+right" },
        ["goto_split:up"] = new[] { "ctrl+alt+up" },
        ["goto_split:down"] = new[] { "ctrl+alt+down" },
        ["toggle_split_zoom"] = new[] { "ctrl+shift+enter" },
        ["resize_split:left"] = new[] { "super+ctrl+shift+left" },
        ["resize_split:right"] = new[] { "super+ctrl+shift+right" },
        ["resize_split:up"] = new[] { "super+ctrl+shift+up" },
        ["resize_split:down"] = new[] { "super+ctrl+shift+down" },

        ["increase_font_size:1"] = new[] { "ctrl+plus" },
        ["decrease_font_size:1"] = new[] { "ctrl+minus" },
        ["reset_font_size"] = new[] { "ctrl+0" },

        ["scroll_to_top"] = new[] { "shift+home" },
        ["scroll_to_bottom"] = new[] { "shift+end" },
        ["scroll_page_up"] = new[] { "shift+page_up" },
        ["scroll_page_down"] = new[] { "shift+page_down" },
        ["adjust_selection:left"] = new[] { "shift+left" },
        ["adjust_selection:right"] = new[] { "shift+right" },
        ["adjust_selection:up"] = new[] { "shift+up" },
        ["adjust_selection:down"] = new[] { "shift+down" },
        ["select_all"] = new[] { "ctrl+shift+a" },
        ["jump_to_prompt:-1"] = new[] { "ctrl+shift+up" },
        ["jump_to_prompt:1"] = new[] { "ctrl+shift+down" },

        ["copy_to_clipboard"] = new[] { "ctrl+insert", "ctrl+shift+c" },
        ["paste_from_clipboard"] = new[] { "ctrl+shift+v" },
        ["paste_from_selection"] = new[] { "shift+insert" },
        ["start_search"] = new[] { "ctrl+shift+f" },
        ["toggle_command_palette"] = new[] { "ctrl+shift+p" },

        ["write_screen_file:copy"] = new[] { "super+ctrl+shift+j" },
        ["write_screen_file:paste"] = new[] { "ctrl+shift+j" },
        ["write_screen_file:open"] = new[] { "ctrl+alt+shift+j" },

        ["toggle_fullscreen"] = new[] { "ctrl+enter" },
        ["close_surface"] = new[] { "ctrl+shift+w" },
        ["close_window"] = new[] { "alt+f4" },
        ["quit"] = new[] { "ctrl+shift+q" },
        ["open_config"] = new[] { "ctrl+comma" },
        ["reload_config"] = new[] { "ctrl+shift+comma" },
    };

    public Keybinds(Config config)
    {
        if (!config.Values.TryGetValue("keybind", out List<string>? lines)) return;
        foreach (string line in lines) Apply(line);
    }

    /// <summary>"keybind = clear" removed every default, not just the user's additions.</summary>
    public bool Cleared => _cleared;

    private void Apply(string raw)
    {
        string text = raw.Trim();
        if (text.Length == 0)
        {
            ResetOverrides();
            return;
        }

        if (text.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            ResetOverrides();
            _cleared = true;
            return;
        }

        int equals = text.IndexOf('=');
        if (equals < 0)
        {
            // "table/" defines a table and clears every binding in it.
            if (text.EndsWith('/'))
            {
                DeclareTable(text[..^1].Trim());
                return;
            }

            Diag.Log("keybind", $"ignored '{text}'");
            return;
        }

        string left = text[..equals].Trim();
        string action = text[(equals + 1)..].Trim();

        // A trigger may name a table in front of it: "<table>/ctrl+a=new_window".
        string tableName = string.Empty;
        string triggerText = left;
        int slash = left.IndexOf('/');
        if (slash >= 0)
        {
            tableName = left[..slash].Trim();
            triggerText = left[(slash + 1)..].Trim();
            if (!IsTableName(tableName))
            {
                Diag.Log("keybind", $"invalid key table '{tableName}'");
                return;
            }

            // "table/=" only clears the table.
            if (triggerText.Length == 0)
            {
                DeclareTable(tableName);
                return;
            }
        }

        string trigger = StripPrefixes(triggerText, out bool global, out bool performable, out bool all,
            out bool physical, out bool unconsumed);
        string? sequence = NormalizeSequence(trigger, physical, out int chords);
        if (sequence is null)
        {
            Diag.Log("keybind", $"unsupported trigger '{trigger}'");
            return;
        }

        // A global chord is registered with the OS as one system-wide key, so Ghostty does
        // not allow a sequence behind that prefix, and a table only exists while it is
        // active, so a line inside one cannot be global either.
        if (global && chords > 1)
        {
            Diag.Log("keybind", $"trigger sequence '{trigger}' cannot be global");
            return;
        }
        if (global && tableName.Length > 0)
        {
            Diag.Log("keybind", $"key table '{tableName}' cannot hold a global binding");
            return;
        }

        BindingSet set = tableName.Length == 0 ? _root : Table(tableName);

        set.Remove(sequence);
        _global.Remove(sequence);

        // A direct binding of a chord other lines use as a sequence prefix drops those
        // sequences, so the two cannot both claim the same first key.
        if (chords == 1) set.RemoveSequencesUnder(sequence);

        // An unbind of a codepoint has to probe too, or the key would keep falling through.
        if (sequence.Contains("u+", StringComparison.Ordinal)) _hasCodepoints = true;

        // An empty action — or Ghostty's spelled-out "unbind" — removes the binding so the
        // shell sees the key again.
        if (action.Length == 0 || string.Equals(action, "unbind", StringComparison.Ordinal))
        {
            set.Unbound.Add(sequence);
            return;
        }

        set.Bindings[sequence] = action;
        set.Ordered.Add((sequence, action));
        if (performable) set.Performable.Add(sequence);
        if (all) set.All.Add(sequence);
        if (physical) set.Physical.Add(sequence);

        // Ghostty: "all:" and "global:" always consume, since they are not tied to one
        // surface and have nothing to encode the key into.
        if (unconsumed && !all) set.Unconsumed.Add(sequence);

        if (global) _global.Add(sequence);
    }

    /// <summary>"table/" (or "table/=") defines the table and clears every binding in it.</summary>
    private void DeclareTable(string name)
    {
        if (!IsTableName(name))
        {
            Diag.Log("keybind", $"invalid key table '{name}'");
            return;
        }
        Table(name).Clear();
    }

    /// <summary>Ghostty reserves the characters its trigger syntax uses.</summary>
    private static bool IsTableName(string name)
    {
        if (name.Length == 0) return false;
        foreach (char c in name)
            if (c is '/' or '=' or '+' or '>') return false;
        return true;
    }

    private BindingSet Table(string name)
    {
        if (_tables.TryGetValue(name, out BindingSet? set)) return set;
        set = new BindingSet(name);
        _tables[name] = set;
        return set;
    }

    /// <summary>True when the config defined a table with this name.</summary>
    public bool HasTable(string name) => _tables.ContainsKey(name);

    /// <summary>True when any line carries the "all:" (or "global:") prefix.</summary>
    public bool HasAll
    {
        get
        {
            if (_root.All.Count > 0) return true;
            foreach (BindingSet set in _tables.Values)
                if (set.All.Count > 0) return true;
            return false;
        }
    }

    /// <summary>True when any line carries the "performable:" prefix.</summary>
    public bool HasPerformable
    {
        get
        {
            if (_root.Performable.Count > 0) return true;
            foreach (BindingSet set in _tables.Values)
                if (set.Performable.Count > 0) return true;
            return false;
        }
    }

    private void ResetOverrides()
    {
        _root.Clear();
        _tables.Clear();
        _global.Clear();
        _cleared = false;
        _hasCodepoints = false;
    }

    /// <summary>
    /// Every chord that should be registered with the operating system, so it fires while
    /// another application has the focus: the "global:" defaults still in effect, then the
    /// user's own global lines.
    /// </summary>
    public IEnumerable<(string Chord, string Action)> Global()
    {
        if (!_cleared)
        {
            foreach (KeyValuePair<string, string[]> entry in Defaults)
            {
                foreach (string trigger in entry.Value)
                {
                    if (!trigger.StartsWith("global:", StringComparison.OrdinalIgnoreCase)) continue;
                    string? chord = NormalizeTrigger(trigger);
                    if (chord is null || _root.Unbound.Contains(chord)) continue;
                    // An explicit line for this chord wins; it is listed from Ordered below.
                    if (_root.Bindings.ContainsKey(chord)) continue;
                    // A chord a sequence starts with never fires on its own.
                    if (_root.IsPrefix(chord)) continue;
                    yield return (chord, entry.Key);
                }
            }
        }

        foreach ((string chord, string action) in _root.Ordered)
            if (_global.Contains(chord)) yield return (chord, action);
    }

    /// <summary>Looks up the chord the event describes, including the "ctrl+plus" spelling.</summary>
    public KeybindMatch Resolve(int mods, VirtualKey key, out string? action, uint codepoint = 0)
        => Resolve(mods, key, out action, codepoint, null, out _);

    /// <summary>
    /// The same lookup with the surface's active key tables: the innermost table answers
    /// first, then the ones below it, then the default set. <paramref name="sourceTable"/>
    /// is where the winning line lives (null for the default set), which is what the caller
    /// needs to drop a "once" table after it fires.
    /// </summary>
    public KeybindMatch Resolve(int mods, VirtualKey key, out string? action, uint codepoint,
        IReadOnlyList<string>? tables, out KeybindMatchInfo info, Func<string, bool>? canPerform = null)
    {
        action = null;
        info = KeybindMatchInfo.None;

        foreach (BindingSet set in Sets(tables))
        {
            foreach ((int chordMods, string name) in Candidates(mods, key, codepoint))
            {
                string chord = ChordName(chordMods, name);
                if (set.Bindings.TryGetValue(chord, out string? bound))
                {
                    // "performable:" declines the key when the action cannot run, so the
                    // lookup keeps going: another chord spelling, table or the default set
                    // may still answer.
                    if (canPerform is not null && set.Performable.Contains(chord) && !canPerform(bound))
                        continue;

                    action = bound;
                    info = Info(set, chord);
                    return KeybindMatch.Action;
                }
                if (set.Unbound.Contains(chord)) return KeybindMatch.Unbound;
            }
        }
        return KeybindMatch.None;
    }

    /// <summary>
    /// Advances a trigger sequence one key at a time. <paramref name="pending"/> is the
    /// sequence built so far, or null when none is open; `Pending` returns the longer
    /// sequence in <paramref name="next"/>. A chord that is also a complete binding still
    /// pends when a longer sequence starts with it, which is Ghostty's rule that a prefix
    /// overrides a plain binding of the same chord.
    /// </summary>
    public KeybindStep Step(string? pending, int mods, VirtualKey key,
        out string? action, out string next, uint codepoint = 0)
        => Step(pending, mods, key, out action, out next, codepoint, null, out _);

    /// <summary>The same step with the surface's active key tables.</summary>
    public KeybindStep Step(string? pending, int mods, VirtualKey key,
        out string? action, out string next, uint codepoint,
        IReadOnlyList<string>? tables, out KeybindMatchInfo info, Func<string, bool>? canPerform = null)
    {
        action = null;
        next = string.Empty;
        info = KeybindMatchInfo.None;

        foreach (BindingSet set in Sets(tables))
        {
            foreach ((int chordMods, string name) in Candidates(mods, key, codepoint))
            {
                string chord = ChordName(chordMods, name);
                string sequence = pending is null ? chord : pending + ">" + chord;

                if (set.IsPrefix(sequence))
                {
                    next = sequence;
                    return KeybindStep.Pending;
                }

                // A chord that starts no sequence is Resolve's business; only a continuation
                // can complete a sequence.
                if (pending is null)
                {
                    // An unbind in a table still shadows the sets below it.
                    if (set.Unbound.Contains(chord)) return KeybindStep.None;
                    continue;
                }

                if (set.Unbound.Contains(sequence)) return KeybindStep.Miss;
                if (set.Bindings.TryGetValue(sequence, out string? bound))
                {
                    // A sequence whose action cannot run is reset: the caller sees a miss
                    // and nothing of the sequence fires.
                    if (canPerform is not null && set.Performable.Contains(sequence) && !canPerform(bound))
                        return KeybindStep.Miss;

                    action = bound;
                    info = Info(set, sequence);
                    return KeybindStep.Action;
                }
            }
        }

        return pending is null ? KeybindStep.None : KeybindStep.Miss;
    }

    /// <summary>Where a matched chord lives and how the caller has to dispatch it.</summary>
    private static KeybindMatchInfo Info(BindingSet set, string chord)
        => new(set.Name.Length == 0 ? null : set.Name,
            set.Performable.Contains(chord),
            set.All.Contains(chord),
            set.Unconsumed.Contains(chord));

    /// <summary>The sets a lookup walks: innermost table first, the default set last.</summary>
    private IEnumerable<BindingSet> Sets(IReadOnlyList<string>? tables)
    {
        if (tables is not null)
        {
            for (int i = tables.Count - 1; i >= 0; i--)
                if (_tables.TryGetValue(tables[i], out BindingSet? set)) yield return set;
        }
        yield return _root;
    }

    /// <summary>
    /// Every binding in effect, in Ghostty's config spelling: the defaults the config left
    /// alone, followed by the user's own chords. Declaration order; callers that print a
    /// table sort it themselves.
    /// </summary>
    public IEnumerable<(string Chord, string Action)> Effective()
    {
        foreach ((string chord, string action) in RootEffective())
            yield return (chord, action);

        var names = new List<string>(_tables.Keys);
        names.Sort(StringComparer.Ordinal);
        foreach (string name in names)
            foreach ((string chord, string action) in _tables[name].Ordered)
                yield return (name + "/" + Spell(_tables[name], chord), action);
    }

    /// <summary>The config spelling of a binding, prefixes included.</summary>
    private static string Spell(BindingSet set, string chord)
    {
        bool all = set.All.Contains(chord);
        bool performable = set.Performable.Contains(chord);
        bool physical = set.Physical.Contains(chord);
        bool unconsumed = set.Unconsumed.Contains(chord);
        if (!all && !performable && !physical && !unconsumed) return chord;

        var text = new StringBuilder();
        if (all) text.Append("all:");
        if (performable) text.Append("performable:");
        if (physical) text.Append("physical:");
        if (unconsumed) text.Append("unconsumed:");
        return text.Append(chord).ToString();
    }

    /// <summary>Every binding of the default set, in config spelling.</summary>
    private IEnumerable<(string Chord, string Action)> RootEffective()
    {
        if (!_cleared)
        {
            foreach (KeyValuePair<string, string[]> entry in Defaults)
            {
                foreach (string trigger in entry.Value)
                {
                    string? chord = NormalizeTrigger(trigger);
                    if (chord is null || _root.Unbound.Contains(chord)) continue;
                    // An explicit line for this chord wins (it is listed from Ordered below).
                    if (_root.Bindings.ContainsKey(chord)) continue;
                    // A chord a sequence starts with never fires on its own.
                    if (_root.IsPrefix(chord)) continue;
                    yield return (chord, entry.Key);
                }
            }
        }

        foreach ((string chord, string action) in _root.Ordered)
        {
            // The first chord of a sequence is not a binding of its own.
            if (!chord.Contains('>') && _root.IsPrefix(chord)) continue;
            yield return (Spell(_root, chord), action);
        }
    }

    /// <summary>
    /// The chord the UI should show for an action: the first default that is still active,
    /// otherwise the user's own chord. Actions without defaults keep their contextual hint
    /// (for example "Esc" on the End Search command).
    /// </summary>
    public string Label(string actionId, string fallback)
    {
        string[] defaults = Defaults.TryGetValue(actionId, out string[]? table)
            ? table
            : Array.Empty<string>();

        foreach (string trigger in defaults)
            if (IsDefaultActive(trigger, actionId)) return Pretty(trigger);

        // Ghostty keeps performable bindings out of the menus: the chord only runs while the
        // action can actually do something, so advertising it as "the" shortcut would lie.
        foreach ((string chord, string action) in _root.Ordered)
            if (action == actionId && !_root.Performable.Contains(chord)) return Pretty(chord);

        return defaults.Length > 0 ? string.Empty : fallback;
    }

    /// <summary>
    /// Every chord the config currently leaves active for one action, in display spelling.
    /// Empty when the action was never bound or the config unbound all of it.
    /// </summary>
    public List<string> Chords(string actionId)
    {
        var chords = new List<string>();
        foreach ((string chord, string action) in RootEffective())
            if (action == actionId) chords.Add(Pretty(chord));
        return chords;
    }

    private bool IsDefaultActive(string trigger, string actionId)
    {
        if (_cleared) return false;
        string? chord = NormalizeTrigger(trigger);
        if (chord is null || _root.Unbound.Contains(chord)) return false;
        if (_root.IsPrefix(chord)) return false;
        if (_root.Bindings.TryGetValue(chord, out string? bound)) return bound == actionId;
        return true;
    }

    // ---------------- chord normalisation ----------------

    private static readonly string[] KeyNames =
    {
        "left", "right", "up", "down", "home", "end", "page_up", "page_down",
        "insert", "delete", "backspace", "enter", "tab", "escape", "space",
        "plus", "minus", "equal", "comma", "period", "slash", "backslash",
        "backquote", "bracket_left", "bracket_right", "semicolon", "apostrophe",
    };

    /// <summary>
    /// Removes the prefixes Ghostty puts in front of a trigger. "global:" and "performable:"
    /// change behaviour here (the chord is registered system-wide; the action has to be able
    /// to run); the others are accepted so a real Ghostty config keeps loading.
    /// </summary>
    private static string StripPrefixes(string trigger, out bool global, out bool performable, out bool all,
        out bool physical, out bool unconsumed)
    {
        global = false;
        performable = false;
        all = false;
        physical = false;
        unconsumed = false;

        int colon = trigger.IndexOf(':');
        while (colon > 0)
        {
            string name = trigger[..colon].Trim().ToLowerInvariant();
            if (name is not ("global" or "performable" or "all" or "unconsumed" or "physical")) break;

            global |= name == "global";
            performable |= name == "performable";
            all |= name == "all";
            physical |= name == "physical";
            unconsumed |= name == "unconsumed";

            trigger = trigger[(colon + 1)..].TrimStart();
            colon = trigger.IndexOf(':');
        }

        // Ghostty: "global:" implies "all:" — a system-wide chord has no focused surface.
        all |= global;
        return trigger;
    }

    /// <summary>
    /// Parses a trigger into its canonical chords, joining a sequence with "&gt;".
    /// Returns null for anything this build cannot register: key tables (the "/" form) or
    /// a chord with an unknown key name.
    /// </summary>
    private static string? NormalizeSequence(string trigger, bool physical, out int chords)
    {
        chords = 0;
        if (trigger.Contains('/')) return null;

        string[] parts = trigger.Split('>');
        var names = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string? chord = NormalizeTrigger(parts[i].Trim(), physical);
            if (chord is null) return null;
            names[i] = chord;
        }

        chords = names.Length;
        return string.Join('>', names);
    }

    /// <summary>
    /// Parses "ctrl+shift+t", "alt+shift+plus", "global:ctrl+backquote" or
    /// "physical:ctrl+comma" into the canonical chord.
    /// </summary>
    private static string? NormalizeTrigger(string trigger, bool physical = false)
    {
        trigger = StripPrefixes(trigger.Trim(), out _, out _, out _, out bool prefixPhysical, out _);
        physical |= prefixPhysical;
        if (trigger.Length == 0) return null;

        // A single chord never carries the sequence or key-table separators.
        if (trigger.Contains('/') || trigger.Contains('>')) return null;

        string keyPart;
        string modPart;
        if (trigger == "+")
        {
            keyPart = "+";
            modPart = string.Empty;
        }
        else if (trigger.EndsWith("++", StringComparison.Ordinal))
        {
            // "ctrl++" means the plus key, not an empty key name.
            keyPart = "+";
            modPart = trigger[..^2];
        }
        else
        {
            int plus = trigger.LastIndexOf('+');
            keyPart = plus < 0 ? trigger : trigger[(plus + 1)..];
            modPart = plus < 0 ? string.Empty : trigger[..plus];
        }

        string? key = NormalizeKey(keyPart, physical);
        if (key is null) return null;

        int mods = 0;
        foreach (string raw in modPart.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.Trim().ToLowerInvariant())
            {
                case "ctrl":
                case "control": mods |= GhosttyNative.MODS_CTRL; break;
                case "shift": mods |= GhosttyNative.MODS_SHIFT; break;
                case "alt":
                case "opt":
                case "option": mods |= GhosttyNative.MODS_ALT; break;
                case "super":
                case "cmd":
                case "command":
                case "win":
                case "windows": mods |= GhosttyNative.MODS_SUPER; break;
                case "": break;
                default: return null;
            }
        }

        return ChordName(mods, key);
    }

    private static string? NormalizeKey(string name, bool physical)
    {
        string raw = name.Trim();
        name = raw.ToLowerInvariant();
        switch (name)
        {
            case "esc": return "escape";
            // Ghostty spells the key above Tab this way; the chord names here use backquote.
            case "grave_accent":
            case "grave": return "backquote";
            case "return": return "enter";
            case "pgup": case "pageup": return "page_up";
            case "pgdn": case "pagedown": return "page_down";
            case "del": return "delete";
            case "ins": return "insert";
            case "arrow_left": case "arrowleft": return "left";
            case "arrow_right": case "arrowright": return "right";
            case "arrow_up": case "arrowup": return "up";
            case "arrow_down": case "arrowdown": return "down";
            case "+": return "plus";
            case "-": return "minus";
            case "=": return "equal";
            case ",": return "comma";
            case ".": return "period";
            case "/": return "slash";
            case "\\": return "backslash";
            case "`": return "backquote";
            case "[": return "bracket_left";
            case "]": return "bracket_right";
            case ";": return "semicolon";
            case "'": return "apostrophe";
        }

        foreach (string known in KeyNames)
            if (name == known) return known;

        if (name.Length == 1)
        {
            char c = name[0];
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) return name;
        }

        if (name.Length >= 2 && name[0] == 'f' && int.TryParse(name[1..], out int f) && f is >= 1 and <= 24)
            return name;

        if (name.StartsWith("kp_", StringComparison.Ordinal) && name.Length == 4 &&
            name[3] is >= '0' and <= '9')
            return name;

        // Anything else that is exactly one Unicode codepoint is a codepoint trigger: it
        // matches whatever key produces that character under the active layout. ASCII
        // letters and digits stay physical names (above), which is what "a" and "0" always
        // meant here.
        bool single = raw.Length == 1 ? !char.IsSurrogate(raw[0])
            : raw.Length == 2 && char.IsSurrogatePair(raw, 0);
        if (single)
        {
            // "physical:" promises a physical key code, so a character that is not one of
            // the names above is a config error instead of a codepoint trigger.
            if (physical) return null;

            int codepoint = FoldCodepoint((uint)char.ConvertToUtf32(raw, 0));
            if (codepoint > 0) return CodepointName(codepoint);
        }

        return null;
    }

    /// <summary>
    /// A codepoint key is stored as "u+03c0": the hex value cannot collide with a key name
    /// and survives being read back out of a chord string.
    /// </summary>
    private static string CodepointName(int codepoint) => "u+" + codepoint.ToString("x4");

    private static bool TryCodepoint(string key, out int codepoint)
    {
        codepoint = 0;
        if (key.Length < 4 || key[0] != 'u' || key[1] != '+') return false;

        int value = 0;
        for (int i = 2; i < key.Length; i++)
        {
            char c = key[i];
            int digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            value = value * 16 + digit;
        }

        if (value == 0 || value > 0x10FFFF) return false;
        codepoint = value;
        return true;
    }

    /// <summary>
    /// Case-folds a produced codepoint the way the config spelling is folded (the trigger
    /// text goes through ToLowerInvariant), so "Ä" and "ä" are one trigger, matching
    /// Ghostty's case-insensitive codepoint matching.
    /// </summary>
    private static int FoldCodepoint(uint codepoint)
    {
        if (codepoint == 0 || codepoint > 0x10FFFF || codepoint is >= 0xD800 and <= 0xDFFF) return 0;

        string text = char.ConvertFromUtf32((int)codepoint).ToLowerInvariant();
        return text.Length == 0 ? 0 : char.ConvertToUtf32(text, 0);
    }

    /// <summary>
    /// Splits a canonical chord into its modifier prefix and its key. A codepoint key
    /// ("u+03c0") carries a '+' of its own, so splitting at the last '+' would cut it in half.
    /// </summary>
    private static (string Mods, string Key) SplitChord(string chord)
    {
        int codepoint = chord.LastIndexOf("+u+", StringComparison.Ordinal);
        if (codepoint > 0) return (chord[..codepoint], chord[(codepoint + 1)..]);
        if (TryCodepoint(chord, out _)) return (string.Empty, chord);

        int plus = chord.LastIndexOf('+');
        return plus < 0 ? (string.Empty, chord) : (chord[..plus], chord[(plus + 1)..]);
    }

    private static string ChordName(int mods, string key)
    {
        var text = new StringBuilder();
        if ((mods & GhosttyNative.MODS_CTRL) != 0) text.Append("ctrl+");
        if ((mods & GhosttyNative.MODS_SHIFT) != 0) text.Append("shift+");
        if ((mods & GhosttyNative.MODS_ALT) != 0) text.Append("alt+");
        if ((mods & GhosttyNative.MODS_SUPER) != 0) text.Append("super+");
        text.Append(key);
        return text.ToString();
    }

    /// <summary>Maps the pressed key to the name the config uses, if we understand it.</summary>
    private static string? KeyName(VirtualKey key)
    {
        int code = (int)key;

        if (code >= (int)VirtualKey.A && code <= (int)VirtualKey.Z)
            return ((char)('a' + code - (int)VirtualKey.A)).ToString();
        if (code >= (int)VirtualKey.Number0 && code <= (int)VirtualKey.Number9)
            return ((char)('0' + code - (int)VirtualKey.Number0)).ToString();
        if (code >= (int)VirtualKey.NumberPad0 && code <= (int)VirtualKey.NumberPad9)
            return "kp_" + (char)('0' + code - (int)VirtualKey.NumberPad0);
        if (code >= (int)VirtualKey.F1 && code <= (int)VirtualKey.F24)
            return "f" + (code - (int)VirtualKey.F1 + 1);

        switch (key)
        {
            case VirtualKey.Left: return "left";
            case VirtualKey.Right: return "right";
            case VirtualKey.Up: return "up";
            case VirtualKey.Down: return "down";
            case VirtualKey.Home: return "home";
            case VirtualKey.End: return "end";
            case VirtualKey.PageUp: return "page_up";
            case VirtualKey.PageDown: return "page_down";
            case VirtualKey.Insert: return "insert";
            case VirtualKey.Delete: return "delete";
            case VirtualKey.Back: return "backspace";
            case VirtualKey.Enter: return "enter";
            case VirtualKey.Tab: return "tab";
            case VirtualKey.Escape: return "escape";
            case VirtualKey.Space: return "space";
            case VirtualKey.Add: return "plus";
            case VirtualKey.Subtract: return "minus";
            case VirtualKey.Decimal: return "period";
            case VirtualKey.Divide: return "slash";
        }

        // OEM punctuation is named by the glyph on a US layout, which is how the config
        // spells the same keys.
        return code switch
        {
            0xBB => "plus",
            0xBD => "minus",
            0xBC => "comma",
            0xBE => "period",
            0xBF => "slash",
            0xC0 => "backquote",
            0xDB => "bracket_left",
            0xDC => "backslash",
            0xDD => "bracket_right",
            0xBA => "semicolon",
            0xDE => "apostrophe",
            _ => null,
        };
    }

    /// <summary>
    /// The exact chord first, then the "ctrl+plus"/"ctrl+equal" spellings that share one
    /// physical key. Shift is dropped for those glyph keys because the glyph already implies
    /// it on most layouts. A codepoint binding comes last: Ghostty gives physical keys
    /// priority over codepoints for the same press.
    /// </summary>
    private IEnumerable<(int Mods, string Name)> Candidates(int mods, VirtualKey key, uint codepoint)
    {
        string? name = KeyName(key);
        if (name is not null)
        {
            yield return (mods, name);

            if (name is "plus" or "equal" or "minus")
            {
                int relaxed = mods & ~GhosttyNative.MODS_SHIFT;
                if (relaxed != mods) yield return (relaxed, name);

                string alternate = name switch
                {
                    "plus" => "equal",
                    "equal" => "plus",
                    _ => name,
                };
                if (alternate != name)
                {
                    yield return (mods, alternate);
                    if (relaxed != mods) yield return (relaxed, alternate);
                }
            }
        }

        // The character the key types with no modifiers, matched case-folded. Ghostty's rule
        // is that a codepoint trigger matches any key that produces that codepoint, so the
        // caller probes the active layout and hands the result in.
        if (codepoint == 0) yield break;
        int folded = FoldCodepoint(codepoint);
        if (folded > 0) yield return (mods, CodepointName(folded));
    }

    // ---------------- display ----------------

    /// <summary>
    /// "Control+A N": the chords of a sequence are shown in order, separated by a space.
    /// </summary>
    private static string Pretty(string trigger)
    {
        if (!trigger.Contains('>')) return PrettyChord(trigger);

        var text = new StringBuilder();
        foreach (string part in trigger.Split('>'))
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(PrettyChord(part));
        }
        return text.ToString();
    }

    private static string PrettyChord(string trigger)
    {
        string chord = NormalizeTrigger(trigger) ?? trigger;
        (string mods, string key) = SplitChord(chord);
        var text = new StringBuilder();
        foreach (string part in mods.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            text.Append(part switch
            {
                "ctrl" => "Ctrl",
                "shift" => "Shift",
                "alt" => "Alt",
                "super" => "Win",
                _ => part,
            }).Append('+');
        }
        return text.Append(PrettyKey(key)).ToString();
    }

    private static string PrettyKey(string key)
    {
        if (TryCodepoint(key, out int codepoint)) return char.ConvertFromUtf32(codepoint);

        return key switch
    {
        "left" => "\u2190",
        "right" => "\u2192",
        "up" => "\u2191",
        "down" => "\u2193",
        "page_up" => "PageUp",
        "page_down" => "PageDown",
        "escape" => "Esc",
        "enter" => "Enter",
        "tab" => "Tab",
        "home" => "Home",
        "end" => "End",
        "insert" => "Insert",
        "delete" => "Delete",
        "backspace" => "Backspace",
        "space" => "Space",
        "plus" => "Plus",
        "minus" => "-",
        "equal" => "=",
        "comma" => ",",
        "period" => ".",
        "slash" => "/",
        "backslash" => "\\",
        "backquote" => "`",
        "bracket_left" => "[",
        "bracket_right" => "]",
        "semicolon" => ";",
        "apostrophe" => "'",
        _ => key.Length == 1 ? key.ToUpperInvariant() : key.ToUpperInvariant(),
    };
    }
}