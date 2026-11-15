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

/// <summary>
/// Parses Ghostty's "keybind = &lt;trigger&gt;=&lt;action&gt;" lines and answers two questions:
/// which action a chord runs, and which chord the palette should advertise for an action.
///
/// Triggers are normalised to a canonical "ctrl+shift+key" form, so the order the modifiers
/// were written in does not matter. Defaults stay in the hardcoded key handler; this class
/// only carries the user's additions, removals and the "clear" state.
/// </summary>
internal sealed class Keybinds
{
    private readonly Dictionary<string, string> _bindings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unbound = new(StringComparer.Ordinal);

    /// <summary>Chords the user marked with the "global:" prefix, in canonical form.</summary>
    private readonly HashSet<string> _global = new(StringComparer.Ordinal);
    private readonly List<(string Chord, string Action)> _ordered = new();
    private bool _cleared;

    /// <summary>
    /// The default chords of the actions this build implements, in the order the UI prefers
    /// to show them. The first active chord is the one the palette and tooltips advertise.
    /// </summary>
    private static readonly Dictionary<string, string[]> Defaults = new(StringComparer.Ordinal)
    {
        ["new_tab"] = new[] { "ctrl+shift+t" },
        ["new_window"] = new[] { "ctrl+shift+n" },
        // Ghostty's quick terminal toggle is a global binding: it works while another
        // application has the focus. Windows gets the same behaviour from RegisterHotKey.
        ["toggle_quick_terminal"] = new[] { "global:ctrl+backquote" },
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

        ["new_split:right"] = new[] { "ctrl+shift+o", "alt+shift+plus" },
        ["new_split:down"] = new[] { "ctrl+shift+e", "alt+shift+minus" },
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
        if (equals <= 0)
        {
            Diag.Log("keybind", $"ignored '{text}'");
            return;
        }

        string trigger = StripPrefixes(text[..equals].Trim(), out bool global);
        string action = text[(equals + 1)..].Trim();
        string? chord = NormalizeTrigger(trigger);
        if (chord is null)
        {
            Diag.Log("keybind", $"unsupported trigger '{trigger}'");
            return;
        }

        _unbound.Remove(chord);
        _bindings.Remove(chord);
        _global.Remove(chord);
        _ordered.RemoveAll(entry => entry.Chord == chord);

        // An empty action removes the binding so the shell sees the key again.
        if (action.Length == 0)
        {
            _unbound.Add(chord);
            return;
        }

        _bindings[chord] = action;
        _ordered.Add((chord, action));
        if (global) _global.Add(chord);
    }

    private void ResetOverrides()
    {
        _bindings.Clear();
        _unbound.Clear();
        _global.Clear();
        _ordered.Clear();
        _cleared = false;
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
                    if (chord is null || _unbound.Contains(chord)) continue;
                    // An explicit line for this chord wins; it is listed from _ordered below.
                    if (_bindings.ContainsKey(chord)) continue;
                    yield return (chord, entry.Key);
                }
            }
        }

        foreach ((string chord, string action) in _ordered)
            if (_global.Contains(chord)) yield return (chord, action);
    }

    /// <summary>Looks up the chord the event describes, including the "ctrl+plus" spelling.</summary>
    public KeybindMatch Resolve(int mods, VirtualKey key, out string? action)
    {
        action = null;
        foreach ((int chordMods, string name) in Candidates(mods, key))
        {
            string chord = ChordName(chordMods, name);
            if (_bindings.TryGetValue(chord, out string? bound))
            {
                action = bound;
                return KeybindMatch.Action;
            }
            if (_unbound.Contains(chord)) return KeybindMatch.Unbound;
        }
        return KeybindMatch.None;
    }

    /// <summary>
    /// Every binding in effect, in Ghostty's config spelling: the defaults the config left
    /// alone, followed by the user's own chords. Declaration order; callers that print a
    /// table sort it themselves.
    /// </summary>
    public IEnumerable<(string Chord, string Action)> Effective()
    {
        if (!_cleared)
        {
            foreach (KeyValuePair<string, string[]> entry in Defaults)
            {
                foreach (string trigger in entry.Value)
                {
                    string? chord = NormalizeTrigger(trigger);
                    if (chord is null || _unbound.Contains(chord)) continue;
                    // An explicit line for this chord wins (it is listed from _ordered below).
                    if (_bindings.ContainsKey(chord)) continue;
                    yield return (chord, entry.Key);
                }
            }
        }

        foreach ((string chord, string action) in _ordered)
            yield return (chord, action);
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

        foreach ((string chord, string action) in _ordered)
            if (action == actionId) return Pretty(chord);

        return defaults.Length > 0 ? string.Empty : fallback;
    }

    /// <summary>
    /// Every chord the config currently leaves active for one action, in display spelling.
    /// Empty when the action was never bound or the config unbound all of it.
    /// </summary>
    public List<string> Chords(string actionId)
    {
        var chords = new List<string>();
        foreach ((string chord, string action) in Effective())
            if (action == actionId) chords.Add(Pretty(chord));
        return chords;
    }

    private bool IsDefaultActive(string trigger, string actionId)
    {
        if (_cleared) return false;
        string? chord = NormalizeTrigger(trigger);
        if (chord is null || _unbound.Contains(chord)) return false;
        if (_bindings.TryGetValue(chord, out string? bound)) return bound == actionId;
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
    /// Removes the prefixes Ghostty puts in front of a trigger. "global:" is the one that
    /// changes behaviour here (the chord is registered system-wide); the others are accepted
    /// so a real Ghostty config keeps loading.
    /// </summary>
    private static string StripPrefixes(string trigger, out bool global)
    {
        global = false;
        while (true)
        {
            int colon = trigger.IndexOf(':');
            if (colon <= 0) return trigger;

            switch (trigger[..colon].Trim().ToLowerInvariant())
            {
                case "global":
                    global = true;
                    break;
                case "all":
                case "unconsumed":
                case "physical":
                    break;
                default:
                    return trigger;
            }
            trigger = trigger[(colon + 1)..].TrimStart();
        }
    }

    /// <summary>
    /// Parses "ctrl+shift+t", "alt+shift+plus", "global:ctrl+backquote" or
    /// "physical:ctrl+comma" into the canonical chord.
    /// </summary>
    private static string? NormalizeTrigger(string trigger)
    {
        trigger = StripPrefixes(trigger.Trim(), out _);
        if (trigger.Length == 0) return null;

        // This build has no key tables or chorded triggers.
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

        string? key = NormalizeKey(keyPart);
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

    private static string? NormalizeKey(string name)
    {
        name = name.Trim().ToLowerInvariant();
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

        return null;
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
    /// it on most layouts.
    /// </summary>
    private static IEnumerable<(int Mods, string Name)> Candidates(int mods, VirtualKey key)
    {
        string? name = KeyName(key);
        if (name is null) yield break;

        yield return (mods, name);

        if (name is not ("plus" or "equal" or "minus")) yield break;

        int relaxed = mods & ~GhosttyNative.MODS_SHIFT;
        if (relaxed != mods) yield return (relaxed, name);

        string alternate = name switch
        {
            "plus" => "equal",
            "equal" => "plus",
            _ => name,
        };
        if (alternate == name) yield break;
        yield return (mods, alternate);
        if (relaxed != mods) yield return (relaxed, alternate);
    }

    // ---------------- display ----------------

    private static string Pretty(string trigger)
    {
        string chord = NormalizeTrigger(trigger) ?? trigger;
        string[] parts = chord.Split('+');
        var text = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (i == parts.Length - 1)
            {
                text.Append(PrettyKey(part));
                break;
            }

            text.Append(part switch
            {
                "ctrl" => "Ctrl",
                "shift" => "Shift",
                "alt" => "Alt",
                "super" => "Win",
                _ => part,
            }).Append('+');
        }
        return text.ToString();
    }

    private static string PrettyKey(string key) => key switch
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