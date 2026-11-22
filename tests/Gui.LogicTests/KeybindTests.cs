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
    public void SequenceWaitsForItsSecondChord()
    {
        Keybinds binds = Load("keybind = ctrl+a>n=new_tab");

        Assert.Equal(KeybindStep.Pending,
            binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A, out _, out string? next));
        Assert.Equal("ctrl+a", next);

        // The first chord alone runs nothing, and it is not a plain binding either.
        Assert.Equal(KeybindMatch.None,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out _));

        Assert.Equal(KeybindStep.Action, binds.Step(next, 0, VirtualKey.N, out string? action, out _));
        Assert.Equal("new_tab", action);
    }

    [Fact]
    public void ThreePartSequenceStepsThroughEveryChord()
    {
        Keybinds binds = Load("keybind = ctrl+a>ctrl+b>c=close_window");

        Assert.Equal(KeybindStep.Pending,
            binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A, out _, out string? one));
        Assert.Equal(KeybindStep.Pending,
            binds.Step(one, GhosttyNative.MODS_CTRL, VirtualKey.B, out _, out string? two));
        Assert.Equal("ctrl+a>ctrl+b", two);
        Assert.Equal(KeybindStep.Action, binds.Step(two, 0, VirtualKey.C, out string? action, out _));
        Assert.Equal("close_window", action);
    }

    [Fact]
    public void SequencePrefixBeatsAPlainBindingOfTheSameChord()
    {
        Keybinds binds = Load(
            "keybind = ctrl+a=new_window",
            "keybind = ctrl+a>n=new_tab");

        Assert.Equal(KeybindStep.Pending,
            binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A, out _, out _));
        // The overridden single binding is not advertised by the palette or +list-keybinds.
        Assert.DoesNotContain(("ctrl+a", "new_window"), binds.Effective());
    }

    [Fact]
    public void PlainBindingOfAPrefixDropsTheSequences()
    {
        Keybinds binds = Load(
            "keybind = ctrl+a>n=new_tab",
            "keybind = ctrl+a=new_window");

        // The sequence is gone, so the chord is a plain binding Resolve answers.
        Assert.Equal(KeybindStep.None,
            binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A, out _, out _));
        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out string? action));
        Assert.Equal("new_window", action);
        Assert.Equal(KeybindStep.None, binds.Step(null, 0, VirtualKey.N, out _, out _));
    }

    [Fact]
    public void AKeyThatDoesNotContinueTheSequenceMisses()
    {
        Keybinds binds = Load("keybind = ctrl+a>n=new_tab");

        Assert.Equal(KeybindStep.Miss, binds.Step("ctrl+a", 0, VirtualKey.X, out _, out _));
    }

    [Fact]
    public void AnUnboundSequenceNeverWaits()
    {
        Keybinds binds = Load("keybind = ctrl+a>n=");

        Assert.Equal(KeybindStep.None,
            binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A, out _, out _));
    }

    [Fact]
    public void GlobalLinesAreListedForRegistration()
    {
        Keybinds binds = Load(
            "keybind = global:ctrl+shift+y=new_tab",
            "keybind = ctrl+shift+z=open_directory");

        int count = 0;
        string? chord = null, action = null;
        foreach ((string c, string a) in binds.Global())
        {
            count++;
            chord = c;
            action = a;
        }

        Assert.Equal(1, count);
        Assert.Equal("ctrl+shift+y", chord);
        Assert.Equal("new_tab", action);
    }

    [Fact]
    public void SequenceCannotBeGlobal()
    {
        Keybinds binds = Load("keybind = global:ctrl+a>n=new_tab");

        Assert.Equal(KeybindStep.None,
            binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A, out _, out _));
        Assert.Empty(binds.Global());
    }

    [Fact]
    public void SequenceLabelJoinsTheChords()
    {
        Keybinds binds = Load("keybind = ctrl+a>n=open_directory");

        Assert.Equal("Ctrl+A N", binds.Label("open_directory", "Ctrl+K Ctrl+O"));
        Assert.Contains(("ctrl+a>n", "open_directory"), binds.Effective());
        Assert.Contains("Ctrl+A N", binds.Chords("open_directory"));
    }

    [Fact]
    public void CodepointTriggerIsStoredAndDisplayed()
    {
        Keybinds binds = Load("keybind = ctrl+π=open_directory");

        Assert.Contains(("ctrl+u+03c0", "open_directory"), binds.Effective());
        Assert.Equal("Ctrl+π", binds.Label("open_directory", string.Empty));
    }

    [Fact]
    public void CodepointTriggerFoldsCase()
    {
        Keybinds binds = Load(
            "keybind = Ä=open_directory",
            "keybind = ä=close_window");

        // Both spellings fold to U+00E4, so the later line replaces the earlier one.
        Assert.Contains(("u+00e4", "close_window"), binds.Effective());
        Assert.DoesNotContain(("u+00e4", "open_directory"), binds.Effective());
    }

    [Fact]
    public void EmojiTriggerIsOneCodepoint()
    {
        Keybinds binds = Load("keybind = 😀=open_directory");

        Assert.Contains(("u+1f600", "open_directory"), binds.Effective());
        Assert.Equal("😀", binds.Label("open_directory", string.Empty));
    }

    [Fact]
    public void PunctuationWithoutANameIsACodepoint()
    {
        Keybinds binds = Load("keybind = ctrl+?=open_directory");

        Assert.Contains(("ctrl+u+003f", "open_directory"), binds.Effective());
        Assert.Equal("Ctrl+?", binds.Label("open_directory", string.Empty));
    }

    [Fact]
    public void CodepointTriggerCanBePartOfASequence()
    {
        Keybinds binds = Load("keybind = π>n=open_directory");

        Assert.Contains(("u+03c0>n", "open_directory"), binds.Effective());
        Assert.Equal("π N", binds.Label("open_directory", string.Empty));
    }

    [Fact]
    public void CodepointBindingMatchesTheProbedCharacter()
    {
        Keybinds binds = Load("keybind = ctrl+π=open_directory");

        // Any key that produces π on the active layout resolves to the binding; the probe
        // is handed in so the test does not depend on the machine's keyboard layout.
        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.Y, out string? action, 0x03C0));
        Assert.Equal("open_directory", action);

        // Case folding: Π (U+03A0) folds to π, so it matches the same trigger.
        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.Y, out _, 0x03A0));
        Assert.Equal(KeybindMatch.None,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.Y, out _, 0x03B1));
    }

    [Fact]
    public void PhysicalBindingWinsOverTheSameCodepoint()
    {
        Keybinds binds = Load(
            "keybind = ctrl+a=open_directory",
            "keybind = ctrl+π=close_window");

        // Even when this key produces π, the physical "ctrl+a" binding answers first.
        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out string? action, 0x03C0));
        Assert.Equal("open_directory", action);
    }

    [Fact]
    public void CodepointCompletesASequence()
    {
        Keybinds binds = Load("keybind = ctrl+a>π=open_directory");

        Assert.Equal(KeybindStep.Action, binds.Step("ctrl+a", 0, VirtualKey.Y,
            out string? action, out _, 0x03C0));
        Assert.Equal("open_directory", action);
    }

    [Fact]
    public void AsciiLettersStayPhysicalNames()
    {
        Keybinds binds = Load("keybind = ctrl+a=open_directory");

        Assert.Contains(("ctrl+a", "open_directory"), binds.Effective());
        Assert.DoesNotContain(("ctrl+u+0061", "open_directory"), binds.Effective());
    }

    [Fact]
    public void TableBindingOnlyAnswersWhileActive()
    {
        Keybinds binds = Load("keybind = foo/ctrl+a=new_window");

        Assert.True(binds.HasTable("foo"));
        Assert.Contains(("foo/ctrl+a", "new_window"), binds.Effective());

        // Inactive: the default set has no ctrl+a line, so nothing answers.
        Assert.Equal(KeybindMatch.None,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out _, 0, null, out _));

        // Active: the table answers, and it reports the table the line came from.
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out string? action, 0, new[] { "foo" }, out KeybindMatchInfo info));
        Assert.Equal("new_window", action);
        Assert.Equal("foo", info.Table);
    }

    [Fact]
    public void InnermostTableWinsAndUnbindShadows()
    {
        Keybinds binds = Load(
            "keybind = ctrl+a=open_directory",
            "keybind = outer/ctrl+a=close_window",
            "keybind = inner/ctrl+a=new_tab");

        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out string? outer, 0, new[] { "outer" }, out _));
        Assert.Equal("close_window", outer);

        // The innermost table is last in the activation order.
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out string? inner, 0, new[] { "outer", "inner" }, out _));
        Assert.Equal("new_tab", inner);

        // An unbound line in a table hides the sets below it instead of falling through.
        Keybinds shadow = Load(
            "keybind = ctrl+a=open_directory",
            "keybind = inner/ctrl+a=");
        Assert.Equal(KeybindMatch.Unbound,
            shadow.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out _, 0, new[] { "inner" }, out _));
    }

    [Fact]
    public void TableSequencesOnlyWorkWhileActive()
    {
        Keybinds binds = Load("keybind = foo/ctrl+a>n=new_tab");

        Assert.Equal(KeybindStep.None, binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, out _, 0, null, out _));
        Assert.Equal(KeybindStep.Pending, binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, out string? next, 0, new[] { "foo" }, out _));
        Assert.Equal("ctrl+a", next);
        Assert.Equal(KeybindStep.Action, binds.Step(next, 0, VirtualKey.N,
            out string? action, out _, 0, new[] { "foo" }, out KeybindMatchInfo info));
        Assert.Equal("new_tab", action);
        Assert.Equal("foo", info.Table);
    }

    [Fact]
    public void TableSlashAloneClearsTheTable()
    {
        Keybinds binds = Load("keybind = foo/ctrl+a=new_window", "keybind = foo/");

        Assert.True(binds.HasTable("foo"));
        Assert.Equal(KeybindMatch.None,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out _, 0, new[] { "foo" }, out _));
        Assert.DoesNotContain(("foo/ctrl+a", "new_window"), binds.Effective());
    }

    [Fact]
    public void TableNamesAndGlobalLinesAreRejected()
    {
        Keybinds binds = Load(
            "keybind = a+b/ctrl+a=new_window",
            "keybind = /ctrl+a=new_tab",
            "keybind = foo/global:ctrl+a=close_window");

        Assert.False(binds.HasTable("a+b"));
        Assert.False(binds.HasTable("foo"));
        Assert.Empty(binds.Global());
        Assert.DoesNotContain(("a+b/ctrl+a", "new_window"), binds.Effective());
    }

    [Fact]
    public void PerformableBindingIsSkippedWhenTheActionCannotRun()
    {
        Keybinds binds = Load("keybind = performable:ctrl+a=copy_to_clipboard");

        Assert.True(binds.HasPerformable);
        Assert.Contains(("performable:ctrl+a", "copy_to_clipboard"), binds.Effective());

        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out string? action, 0, null, out _, _ => true));
        Assert.Equal("copy_to_clipboard", action);

        // The action cannot run, so the key is not consumed by this line.
        Assert.Equal(KeybindMatch.None, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out _, _ => false));
    }

    [Fact]
    public void PerformableBindingFallsThroughToTheSetBelow()
    {
        Keybinds binds = Load(
            "keybind = ctrl+a=open_directory",
            "keybind = foo/performable:ctrl+a=copy_to_clipboard");

        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out string? action, 0, new[] { "foo" }, out KeybindMatchInfo info, _ => false));
        Assert.Equal("open_directory", action);
        Assert.Null(info.Table);
    }

    [Fact]
    public void PerformableSequenceResetsWhenTheActionCannotRun()
    {
        Keybinds binds = Load("keybind = performable:ctrl+a>n=copy_to_clipboard");

        Assert.Equal(KeybindStep.Pending, binds.Step(null, GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, out string? next, 0, null, out _, _ => false));
        Assert.Equal(KeybindStep.Miss, binds.Step(next, 0, VirtualKey.N,
            out _, out _, 0, null, out _, _ => false));
        Assert.Equal(KeybindStep.Action, binds.Step(next, 0, VirtualKey.N,
            out string? action, out _, 0, null, out _, _ => true));
        Assert.Equal("copy_to_clipboard", action);
    }

    [Fact]
    public void PerformableChordsAreNotAdvertised()
    {
        Keybinds binds = Load("keybind = performable:ctrl+shift+y=open_directory");

        // Ghostty keeps performable bindings out of the menus.
        Assert.Empty(binds.Label("open_directory", string.Empty));
        // The listing still carries the spelling, so a paste round-trips.
        Assert.Contains(("performable:ctrl+shift+y", "open_directory"), binds.Effective());
    }

    [Fact]
    public void AllPrefixIsCarriedWithTheMatch()
    {
        Keybinds binds = Load("keybind = all:ctrl+a=new_window");

        Assert.Contains(("all:ctrl+a", "new_window"), binds.Effective());
        Assert.True(binds.HasAll);

        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info));
        Assert.True(info.All);

        Keybinds plain = Load("keybind = ctrl+a=new_window");
        Assert.False(plain.HasAll);
        Assert.Equal(KeybindMatch.Action, plain.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo plainInfo));
        Assert.False(plainInfo.All);
    }

    [Fact]
    public void GlobalImpliesAll()
    {
        Keybinds binds = Load("keybind = global:ctrl+a=new_window");

        Assert.Contains(("all:ctrl+a", "new_window"), binds.Effective());
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info));
        Assert.True(info.All);
    }

    [Fact]
    public void AllAndPerformableStack()
    {
        Keybinds binds = Load("keybind = all:performable:ctrl+a=copy_to_clipboard");

        Assert.Contains(("all:performable:ctrl+a", "copy_to_clipboard"), binds.Effective());
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info, _ => true));
        Assert.True(info.All);
        Assert.True(info.Performable);
    }

    [Fact]
    public void PhysicalPrefixKeepsTheSpellingAndStaysPhysical()
    {
        Keybinds binds = Load("keybind = physical:ctrl+a=new_window");

        Assert.Contains(("physical:ctrl+a", "new_window"), binds.Effective());
        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out string? action));
        Assert.Equal("new_window", action);
    }

    [Fact]
    public void PhysicalPrefixRejectsCodepoints()
    {
        Keybinds binds = Load(
            "keybind = physical:ctrl+π=new_window",
            "keybind = physical:ctrl+?=new_window");

        // "physical:" promises a key code, so neither line becomes a codepoint trigger.
        Assert.DoesNotContain(("ctrl+u+03c0", "new_window"), binds.Effective());
        Assert.DoesNotContain(("ctrl+u+003f", "new_window"), binds.Effective());
        Assert.DoesNotContain(("physical:ctrl+a", "new_window"), binds.Effective());
    }

    [Fact]
    public void PhysicalPrefixStacksWithOtherPrefixes()
    {
        Keybinds binds = Load("keybind = all:physical:ctrl+a=new_window");

        Assert.Contains(("all:physical:ctrl+a", "new_window"), binds.Effective());
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info));
        Assert.True(info.All);
    }

    [Fact]
    public void UnconsumedPrefixIsCarriedWithTheMatch()
    {
        Keybinds binds = Load("keybind = unconsumed:ctrl+a=new_window");

        Assert.Contains(("unconsumed:ctrl+a", "new_window"), binds.Effective());
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info));
        Assert.True(info.Unconsumed);
    }

    [Fact]
    public void AllBindingsAlwaysConsume()
    {
        // Ghostty: "all:" and "global:" have nothing to encode into, so they always consume.
        Keybinds binds = Load("keybind = all:unconsumed:ctrl+a=new_window");

        Assert.Contains(("all:ctrl+a", "new_window"), binds.Effective());
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info));
        Assert.False(info.Unconsumed);
    }

    [Fact]
    public void UnconsumedStacksWithOtherPrefixes()
    {
        Keybinds binds = Load("keybind = physical:unconsumed:ctrl+a=new_window");

        Assert.Contains(("physical:unconsumed:ctrl+a", "new_window"), binds.Effective());
        Assert.Equal(KeybindMatch.Action, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, null, out KeybindMatchInfo info));
        Assert.True(info.Unconsumed);
    }

    [Fact]
    public void IgnoreIsAnActionThatDoesNothing()
    {
        Keybinds binds = Load("keybind = ctrl+a=ignore");

        Assert.Contains(("ctrl+a", "ignore"), binds.Effective());
        Assert.Equal(KeybindMatch.Action,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out string? action));
        Assert.Equal("ignore", action);
    }

    [Fact]
    public void UnbindSpellingRemovesTheBinding()
    {
        Keybinds binds = Load("keybind = ctrl+a=new_tab", "keybind = ctrl+a=unbind");

        Assert.Equal(KeybindMatch.Unbound,
            binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A, out _));
        Assert.DoesNotContain(("ctrl+a", "new_tab"), binds.Effective());
    }

    [Fact]
    public void UnbindInATableShadowsTheDefaultSet()
    {
        Keybinds binds = Load(
            "keybind = ctrl+a=open_directory",
            "keybind = foo/ctrl+a=unbind");

        Assert.Equal(KeybindMatch.Unbound, binds.Resolve(GhosttyNative.MODS_CTRL, VirtualKey.A,
            out _, 0, new[] { "foo" }, out _));
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

    [Fact]
    public void FormatSpellsAPressedChordTheConfigWay()
    {
        Assert.Equal("ctrl+shift+t", Keybinds.Format(CtrlShift, VirtualKey.T));
        Assert.Equal("ctrl+alt+left", Keybinds.Format(
            GhosttyNative.MODS_CTRL | GhosttyNative.MODS_ALT, VirtualKey.Left));
    }

    [Fact]
    public void CanonicalChordsListDefaultsAndUserLines()
    {
        Keybinds binds = Load("keybind = ctrl+shift+y=new_tab");

        var chords = binds.CanonicalChords("new_tab");

        Assert.Contains("ctrl+shift+t", chords);
        Assert.Contains("ctrl+shift+y", chords);
    }

    [Fact]
    public void CanonicalChordsSkipWhatTheConfigUnbound()
    {
        Keybinds binds = Load("keybind = ctrl+shift+t=");

        Assert.Empty(binds.CanonicalChords("new_tab"));
    }

    [Fact]
    public void RewrittenLinesResolveToTheNewChordAfterAReload()
    {
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, "keybind = ctrl+shift+y=new_tab\r\n");
        Config config = Config.LoadRaw(path);

        var edited = KeybindEdit.SetBinding(config.Values["keybind"], "new_tab",
            "ctrl+shift+t", "ctrl+alt+n");
        Config.SaveList(path, "keybind", edited);
        Keybinds after = new Keybinds(Config.LoadRaw(path));

        Assert.Equal(KeybindMatch.Action, after.Resolve(
            GhosttyNative.MODS_CTRL | GhosttyNative.MODS_ALT, VirtualKey.N, out string? action));
        Assert.Equal("new_tab", action);
        Assert.Equal(KeybindMatch.Unbound, after.Resolve(CtrlShift, VirtualKey.T, out _));
        // The user's other chord for the same action is still there.
        Assert.Equal(KeybindMatch.Action, after.Resolve(CtrlShift, VirtualKey.Y, out _));
    }
}
