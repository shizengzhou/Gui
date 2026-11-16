using System.Collections.Generic;
using Microsoft.UI.Input;
using Windows.System;
using Windows.UI.Core;

namespace Gui.Term;

/// <summary>
/// The physical modifier keys and the bits they produce. "key-remap" is applied here, once per
/// physical key, so the shortcut table, the key encoder and the mouse reports all agree on what
/// a key currently acts as.
/// </summary>
internal static class ModifierKeys
{
    private readonly record struct Physical(VirtualKey Key, KeyModifier Kind, bool Right);

    private static readonly Physical[] PhysicalKeys =
    {
        new(VirtualKey.LeftShift, KeyModifier.Shift, false),
        new(VirtualKey.RightShift, KeyModifier.Shift, true),
        new(VirtualKey.LeftControl, KeyModifier.Ctrl, false),
        new(VirtualKey.RightControl, KeyModifier.Ctrl, true),
        new(VirtualKey.LeftMenu, KeyModifier.Alt, false),
        new(VirtualKey.RightMenu, KeyModifier.Alt, true),
        new(VirtualKey.LeftWindows, KeyModifier.Super, false),
        new(VirtualKey.RightWindows, KeyModifier.Super, true),
    };

    /// <summary>Ctrl, Alt, Shift and Super as the configured remaps leave them.</summary>
    public static int Chord(IReadOnlyList<KeyRemap> remaps)
    {
        // With no "key-remap" line this reads the generic key states, exactly as the app did
        // before remapping existed: a config that uses the option cannot change how an
        // untouched one behaves.
        if (remaps.Count == 0)
        {
            int plain = 0;
            if (IsDown(VirtualKey.Shift)) plain |= GhosttyNative.MODS_SHIFT;
            if (IsDown(VirtualKey.Control)) plain |= GhosttyNative.MODS_CTRL;
            if (IsDown(VirtualKey.Menu)) plain |= GhosttyNative.MODS_ALT;
            if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
                plain |= GhosttyNative.MODS_SUPER;
            return plain;
        }

        int mods = 0;
        foreach (Physical key in PhysicalKeys)
        {
            if (!IsDown(key.Key)) continue;
            mods |= Bit(KeyRemapTable.Match(remaps, key.Kind, key.Right));
        }
        return mods;
    }

    public static bool IsDown(VirtualKey key)
    {
        CoreVirtualKeyStates state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static int Bit(KeyModifier modifier) => modifier switch
    {
        KeyModifier.Alt => GhosttyNative.MODS_ALT,
        KeyModifier.Shift => GhosttyNative.MODS_SHIFT,
        KeyModifier.Super => GhosttyNative.MODS_SUPER,
        _ => GhosttyNative.MODS_CTRL,
    };
}
