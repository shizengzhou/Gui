using System;
using System.IO;
using Gui.Term;
using Windows.System;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The "keybind" option: trigger parsing, the unbind/clear forms, and the chord the palette
/// advertises for an action.
/// </summary>
public class KeybindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-keybinds-" + Guid.NewGuid().ToString("N"));

    public KeybindTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    private Keybinds Load(params string[] lines)
    {
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return new Keybinds(Config.LoadRaw(path));
    }

    private static int CtrlShift => GhosttyNative.MODS_CTRL | GhosttyNative.MODS_SHIFT;

    [Fact]
    public void CustomChordResolvesToItsAction()
    {
        Keybinds binds = Load("keybind = ctrl+shift+y=new_tab");

        Assert.Equal(KeybindMatch.Action, binds.Resolve(CtrlShift, VirtualKey.Y, out string? action));
        Assert.Equal("new_tab", action);
    }

    [Fact]
    public void EmptyActionUnbindsTheChord()
    {
        Keybinds binds = Load("keybind = ctrl+shift+t=");

        Assert.Equal(KeybindMatch.Unbound, binds.Resolve(CtrlShift, VirtualKey.T, out _));
    }

    [Fact]
    public void ClearDropsTheBuiltInChords()
    {
        Keybinds binds = Load("keybind = clear", "keybind = ctrl+shift+t=new_tab");

        Assert.True(binds.Cleared);
        Assert.Equal("Ctrl+Shift+T", binds.Label("new_tab", string.Empty));
        // close_surface still has a default chord, but "clear" removed it and no line
        // re-added it, so the command shows no chord at all.
        Assert.Empty(binds.Label("close_surface", "Ctrl+Shift+W"));
    }

    [Fact]
    public void LabelKeepsTheDefaultUntilItIsRemoved()
    {
        Keybinds defaults = Load();
        Assert.Equal("Ctrl+Shift+T", defaults.Label("new_tab", string.Empty));

        Keybinds extra = Load("keybind = ctrl+shift+y=new_tab");
        Assert.Equal("Ctrl+Shift+T", extra.Label("new_tab", string.Empty));

        Keybinds removed = Load("keybind = ctrl+shift+t=");
        Assert.Empty(removed.Label("new_tab", string.Empty));

        Keybinds rebound = Load("keybind = ctrl+shift+t=", "keybind = ctrl+shift+y=new_tab");
        Assert.Equal("Ctrl+Shift+Y", rebound.Label("new_tab", string.Empty));
    }

    [Fact]
    public void ContextOnlyActionsKeepTheirHint()
    {
        Keybinds binds = Load();

        // End Search has no global chord; the palette's "Esc" hint must stay.
        Assert.Equal("Esc", binds.Label("end_search", "Esc"));
    }

    [Fact]
    public void PlusAndEqualSpellTheSamePhysicalKey()
    {
        Keybinds binds = Load("keybind = ctrl+plus=increase_font_size:1");
        Assert.Equal(KeybindMatch.Action, binds.Resolve(
            GhosttyNative.MODS_CTRL | GhosttyNative.MODS_SHIFT, (VirtualKey)0xBB, out string? action));
        Assert.Equal("increase_font_size:1", action);

        Keybinds equal = Load("keybind = ctrl+equal=increase_font_size:1");
        Assert.Equal(KeybindMatch.Action, equal.Resolve(
            GhosttyNative.MODS_CTRL, (VirtualKey)0xBB, out _));
    }

    [Fact]
    public void ModifierAliasesAndPhysicalPrefixAreAccepted()
    {
        Keybinds binds = Load("keybind = physical:control+shift+t=new_tab");

        Assert.Equal(KeybindMatch.Action, binds.Resolve(CtrlShift, VirtualKey.T, out string? action));
        Assert.Equal("new_tab", action);
    }

    [Fact]
    public void LastLineWins()
    {
        Keybinds binds = Load(
            "keybind = ctrl+shift+t=",
            "keybind = ctrl+shift+y=new_tab",
            "keybind = ctrl+shift+y=close_window",
            "keybind = ctrl+shift+z=new_tab");

        Assert.Equal(KeybindMatch.Action, binds.Resolve(CtrlShift, VirtualKey.Y, out string? action));
        Assert.Equal("close_window", action);
        Assert.Equal("Ctrl+Shift+Z", binds.Label("new_tab", string.Empty));
    }
}
