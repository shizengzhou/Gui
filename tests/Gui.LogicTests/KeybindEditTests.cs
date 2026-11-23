using System;
using System.Collections.Generic;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Rewriting "keybind" lines when the settings page changes one shortcut cap: only the
/// clicked chord moves or disappears, and the action's other chords keep working.
/// </summary>
public class KeybindEditTests
{
    [Fact]
    public void ReplacingOneChordKeepsTheActionSiblings()
    {
        var lines = new List<string>
        {
            "ctrl+insert=copy_to_clipboard",
            "ctrl+shift+c=copy_to_clipboard",
            "ctrl+shift+p=toggle_command_palette",
        };

        List<string> result = KeybindEdit.SetBinding(lines, "copy_to_clipboard",
            "ctrl+insert", "ctrl+alt+c");

        Assert.Equal(new[]
        {
            "ctrl+shift+c=copy_to_clipboard",
            "ctrl+shift+p=toggle_command_palette",
            "ctrl+insert=",
            "ctrl+alt+c=copy_to_clipboard",
        }, result);
    }

    [Fact]
    public void ClearingOneChordKeepsTheActionSiblings()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "ctrl+insert=copy_to_clipboard", "ctrl+shift+c=copy_to_clipboard" },
            "copy_to_clipboard", "ctrl+insert", null);

        Assert.Equal(new[] { "ctrl+shift+c=copy_to_clipboard", "ctrl+insert=" }, result);
    }

    [Fact]
    public void AddingAChordLeavesEverythingElseAlone()
    {
        List<string> result = KeybindEdit.SetBinding(
            Array.Empty<string>(), "new_tab", null, "ctrl+alt+n");

        Assert.Equal(new[] { "ctrl+alt+n=new_tab" }, result);
    }

    [Fact]
    public void ReplacingADefaultChordUnbindsTheOldOne()
    {
        List<string> result = KeybindEdit.SetBinding(
            Array.Empty<string>(), "new_tab", "ctrl+shift+t", "ctrl+alt+n");

        Assert.Equal(new[] { "ctrl+shift+t=", "ctrl+alt+n=new_tab" }, result);
    }

    [Fact]
    public void ReusingAChordTheActionAlreadyHasDoesNotDuplicateIt()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "ctrl+shift+c=copy_to_clipboard" }, "copy_to_clipboard",
            "ctrl+insert", "ctrl+shift+c");

        Assert.Equal(new[] { "ctrl+shift+c=copy_to_clipboard", "ctrl+insert=" }, result);
    }

    [Fact]
    public void ReplacingAChordKeepsItsBehaviourPrefix()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "performable:ctrl+insert=copy_to_clipboard" }, "copy_to_clipboard",
            "ctrl+insert", "ctrl+alt+c");

        Assert.Equal(new[]
        {
            "ctrl+insert=",
            "performable:ctrl+alt+c=copy_to_clipboard",
        }, result);
    }

    [Fact]
    public void TableBindingsAreLeftAlone()
    {
        // The keys page only edits the default set; a named table keeps its own binding.
        // "ctrl+a" is not a built-in chord, so no unbind line is needed either.
        List<string> result = KeybindEdit.SetBinding(
            new[] { "foo/ctrl+a=new_tab" }, "new_tab", "ctrl+a", "ctrl+alt+n");

        Assert.Equal(new[] { "foo/ctrl+a=new_tab", "ctrl+alt+n=new_tab" }, result);
    }

    [Fact]
    public void ClearLineSurvives()
    {
        List<string> result = KeybindEdit.SetBinding(new[] { "clear" }, "new_tab",
            null, "ctrl+alt+n");

        Assert.Equal(new[] { "clear", "ctrl+alt+n=new_tab" }, result);
    }

    [Fact]
    public void ReplacingAUserChordLeavesNoUnbindLine()
    {
        // Nothing built in uses "ctrl+alt+c", so dropping its line is enough; an extra
        // "ctrl+alt+c=" would only be noise that grows with every rebind.
        List<string> result = KeybindEdit.SetBinding(
            new[] { "ctrl+alt+c=copy_to_clipboard" }, "copy_to_clipboard",
            "ctrl+alt+c", "ctrl+alt+d");

        Assert.Equal(new[] { "ctrl+alt+d=copy_to_clipboard" }, result);
    }

    [Fact]
    public void ClearingAUserChordLeavesNoUnbindLine()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "ctrl+alt+c=copy_to_clipboard" }, "copy_to_clipboard",
            "ctrl+alt+c", null);

        Assert.Empty(result);
    }

    [Fact]
    public void PressingTheSameChordAgainKeepsASingleLine()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "ctrl+shift+t=new_tab" }, "new_tab", "ctrl+shift+t", "ctrl+shift+t");

        Assert.Equal(new[] { "ctrl+shift+t=new_tab" }, result);
    }

    [Fact]
    public void ClearingAChordThatIsAlreadyUnboundKeepsOneUnbindLine()
    {
        // A previous edit already left "shift+alt+plus=" behind; clearing the same chord
        // again must not append a second copy.
        List<string> result = KeybindEdit.SetBinding(
            new[] { "shift+alt+plus=copy_to_clipboard", "shift+alt+plus=" },
            "copy_to_clipboard", "shift+alt+plus", null);

        Assert.Equal(new[] { "shift+alt+plus=" }, result);
    }

    [Fact]
    public void MovingAChordThatIsAlreadyUnboundAddsNoSecondUnbindLine()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "shift+alt+plus=focus_split_left", "shift+alt+plus=" },
            "focus_split_left", "shift+alt+plus", "ctrl+alt+f");

        Assert.Equal(new[] { "shift+alt+plus=", "ctrl+alt+f=focus_split_left" }, result);
    }

    [Fact]
    public void RepeatedEditsFoldDuplicateUnbindLines()
    {
        List<string> result = KeybindEdit.SetBinding(
            new[] { "shift+alt+plus=", "shift+alt+plus=", "ctrl+t=" },
            "new_tab", null, "ctrl+alt+n");

        Assert.Equal(new[] { "shift+alt+plus=", "ctrl+t=", "ctrl+alt+n=new_tab" }, result);
    }
}
