using System;
using System.Collections.Generic;

namespace Gui.Term;

/// <summary>A modifier key as "key-remap" names it.</summary>
public enum KeyModifier : byte
{
    Ctrl,
    Alt,
    Shift,
    Super,
}

/// <summary>
/// One "key-remap = from=to" line: the physical modifier <see cref="From"/> acts as
/// <see cref="To"/>. A generic line ("ctrl=super") covers both sides, a sided one
/// ("left_ctrl=right_alt") only that side. The side of the target is only part of its name -
/// a modifier is one bit, so "right_alt" as a target simply means Alt.
/// </summary>
public readonly record struct KeyRemap(KeyModifier From, KeyModifier To, bool Left, bool Right)
{
    /// <summary>True when the line names the modifier without a side.</summary>
    public bool Generic => Left && Right;
}

/// <summary>
/// The "key-remap" table, parsed and applied without any window system: the caller reads the
/// physical keys and asks what each one acts as. Remaps are one-way and never transitive, the
/// way Ghostty describes them - "ctrl=super" plus "alt=ctrl" makes Alt act as Ctrl, not Super.
/// </summary>
public static class KeyRemapTable
{
    /// <summary>Parses "from=to", where each side is a generic or a sided modifier name.</summary>
    public static bool TryParse(string entry, out KeyRemap remap, out string error)
    {
        remap = default;
        error = string.Empty;

        int separator = entry.IndexOf('=');
        if (separator <= 0 || separator == entry.Length - 1)
        {
            error = "expected from=to";
            return false;
        }

        if (!TryParseName(entry[..separator], out KeyModifier from, out bool sided, out bool right,
                out error))
            return false;

        if (!TryParseName(entry[(separator + 1)..], out KeyModifier to, out _, out _, out error))
            return false;

        // A generic source covers both sides; a sided one covers only its own.
        remap = new KeyRemap(from, to,
            Left: !(sided && right),
            Right: !(sided && !right));
        return true;
    }

    /// <summary>
    /// The modifier a physical key acts as. A sided line wins over a generic one (it names the
    /// key more precisely), and the last matching line wins among equals.
    /// </summary>
    public static KeyModifier Match(IReadOnlyList<KeyRemap> remaps, KeyModifier from, bool right)
    {
        KeyModifier mapped = from;
        bool sided = false;

        for (int i = 0; i < remaps.Count; i++)
        {
            KeyRemap remap = remaps[i];
            if (remap.From != from) continue;

            if (remap.Generic)
            {
                if (!sided) mapped = remap.To;
            }
            else if (right ? remap.Right : remap.Left)
            {
                mapped = remap.To;
                sided = true;
            }
        }

        return mapped;
    }

    private static bool TryParseName(string text, out KeyModifier modifier, out bool sided,
        out bool right, out string error)
    {
        modifier = KeyModifier.Ctrl;
        sided = false;
        right = false;
        error = string.Empty;

        string name = text.Trim().ToLowerInvariant();
        if (name.StartsWith("left_", StringComparison.Ordinal))
        {
            sided = true;
            name = name["left_".Length..];
        }
        else if (name.StartsWith("right_", StringComparison.Ordinal))
        {
            sided = true;
            right = true;
            name = name["right_".Length..];
        }

        bool known = true;
        modifier = name switch
        {
            "ctrl" or "control" => KeyModifier.Ctrl,
            "alt" or "option" or "opt" => KeyModifier.Alt,
            "shift" => KeyModifier.Shift,
            // Ghostty calls the Win key "super"; the macOS and Linux spellings come along so
            // a shared config keeps working.
            "super" or "cmd" or "command" or "win" or "windows" or "meta" or "logo"
                => KeyModifier.Super,
            _ => Unknown(),
        };

        if (!known)
        {
            error = $"unknown modifier '{text.Trim()}'";
            return false;
        }

        return true;

        KeyModifier Unknown()
        {
            known = false;
            return KeyModifier.Ctrl;
        }
    }
}
