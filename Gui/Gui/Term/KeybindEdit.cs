using System;
using System.Collections.Generic;
using System.Text;

namespace Gui.Term;

/// <summary>
/// Rewrites the "keybind" lines when the settings page changes one shortcut cap. Only the
/// clicked chord is replaced or cleared; the action's other chords keep working.
/// </summary>
internal static class KeybindEdit
{
    /// <summary>
    /// The lines that bind <paramref name="action"/> to <paramref name="chord"/> in place of
    /// <paramref name="oldChord"/>. A null old chord adds one; a null new chord clears the
    /// clicked one. Other chords of the same action, and every other line, stay as they were.
    /// </summary>
    public static List<string> SetBinding(IReadOnlyList<string> lines, string action,
        string? oldChord, string? chord)
    {
        var result = new List<string>(lines.Count + 2);
        string prefix = string.Empty;
        bool replaced = false;

        foreach (string line in lines)
        {
            if (BindsChord(line, action, oldChord))
            {
                // A "performable:"/"global:" line keeps its prefix when the chord moves.
                if (!replaced) prefix = PrefixOf(line);
                replaced = true;
                continue;
            }
            result.Add(line);
        }

        if (oldChord is not null && !string.Equals(oldChord, chord, StringComparison.Ordinal))
            result.Add(oldChord + "=");

        // Reusing a chord the action already has must not leave a duplicate line behind.
        if (chord is not null && !HasBinding(result, action, chord))
            result.Add(prefix + chord + "=" + action);

        return result;
    }

    /// <summary>True when one default-set "trigger=action" line binds this chord to the action.</summary>
    private static bool BindsChord(string line, string action, string? chord)
    {
        if (chord is null) return false;

        string text = line.Trim();
        int equals = text.IndexOf('=');
        if (equals < 0) return false;
        if (!string.Equals(text[(equals + 1)..].Trim(), action, StringComparison.Ordinal)) return false;

        string trigger = text[..equals].Trim();
        // A table binding ("foo/ctrl+a=...") is not shown on the keys page and stays put.
        if (trigger.Contains('/')) return false;

        return string.Equals(Canonical(trigger), chord, StringComparison.Ordinal);
    }

    private static bool HasBinding(IReadOnlyList<string> lines, string action, string chord)
    {
        foreach (string line in lines)
            if (BindsChord(line, action, chord)) return true;
        return false;
    }

    /// <summary>The prefixes in front of a trigger, so a replacement keeps their behaviour.</summary>
    private static string PrefixOf(string line)
    {
        int equals = line.Trim().IndexOf('=');
        if (equals < 0) return string.Empty;

        string rest = line.Trim()[..equals].Trim();
        var prefix = new StringBuilder();
        while (true)
        {
            int colon = rest.IndexOf(':');
            if (colon <= 0) break;
            string name = rest[..colon].Trim().ToLowerInvariant();
            if (name is not ("global" or "performable" or "all" or "unconsumed" or "physical"))
                break;
            prefix.Append(name).Append(':');
            rest = rest[(colon + 1)..].TrimStart();
        }
        return prefix.ToString();
    }

    /// <summary>The trigger without Ghostty's behaviour prefixes.</summary>
    private static string Canonical(string trigger)
    {
        while (true)
        {
            int colon = trigger.IndexOf(':');
            if (colon <= 0) return trigger;
            string name = trigger[..colon].Trim().ToLowerInvariant();
            if (name is not ("global" or "performable" or "all" or "unconsumed" or "physical"))
                return trigger;
            trigger = trigger[(colon + 1)..].TrimStart();
        }
    }
}
