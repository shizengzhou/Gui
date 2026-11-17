using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Gui.Cli;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Covers "+list-keybinds": the effective set it reads out of Term/Keybinds.cs (defaults plus
/// "keybind" overrides, unbinds and "clear") and both output shapes, the ghostty config lines
/// and the coloured table.
/// </summary>
public class CliListKeybindsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-list-keybinds-" + Guid.NewGuid().ToString("N"));

    public CliListKeybindsTests() => Directory.CreateDirectory(_dir);

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

    private static Keybinds Load(params string[] lines)
    {
        if (lines.Length == 0) return new Keybinds(new Config());

        string path = Path.Combine(Path.GetTempPath(), "gui-list-keybinds-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        try
        {
            return new Keybinds(Config.LoadRaw(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<ListKeybinds.Entry> Snapshot(params string[] lines) =>
        ListKeybinds.Snapshot(Load(lines));

    private static bool Has(IReadOnlyList<ListKeybinds.Entry> entries, string chord, string action) =>
        entries.Any(e => e.Chord == chord && e.Action == action);

    /// <summary>Width of a chord in the table: modifiers pad to five cells plus " + ".</summary>
    private static int ChordWidth(string chord)
    {
        string[] parts = chord.Split('+');
        return (parts.Length - 1) * 8 + parts[^1].Length;
    }

    [Fact]
    public void DefaultsCoverTheCoreBindings()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot();

        Assert.True(Has(entries, "ctrl+shift+t", "new_tab"));
        Assert.True(Has(entries, "ctrl+shift+p", "toggle_command_palette"));
        Assert.True(Has(entries, "ctrl+alt+down", "goto_split:down"));
        Assert.True(Has(entries, "ctrl+0", "reset_font_size"));
        // new_split:right has two defaults and both are listed.
        Assert.True(Has(entries, "ctrl+shift+o", "new_split:right"));
        Assert.True(Has(entries, "alt+shift+plus", "new_split:right"));
    }

    [Fact]
    public void UserChordsAreAdditiveAndRebindingReplacesTheAction()
    {
        // Adding a chord keeps the default one, like ghostty.
        IReadOnlyList<ListKeybinds.Entry> added = Snapshot("keybind = ctrl+shift+y=new_tab");
        Assert.True(Has(added, "ctrl+shift+y", "new_tab"));
        Assert.True(Has(added, "ctrl+shift+t", "new_tab"));
        Assert.Equal(2, added.Count(e => e.Action == "new_tab"));

        // Binding a taken chord to another action takes it away from the old one.
        IReadOnlyList<ListKeybinds.Entry> rebound = Snapshot("keybind = ctrl+shift+t=new_window");
        Assert.True(Has(rebound, "ctrl+shift+t", "new_window"));
        Assert.False(Has(rebound, "ctrl+shift+t", "new_tab"));
    }

    [Fact]
    public void CodepointChordKeepsItsHexNameAndPrintsTheGlyph()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = ctrl+π=open_directory");

        Assert.True(Has(entries, "ctrl+u+03c0", "open_directory"));
        Assert.Contains("keybind = ctrl+u+03c0=open_directory",
            ListKeybinds.RenderPlain(entries, docs: false));
        Assert.Contains("π", ListKeybinds.RenderTable(entries, color: false));
    }

    [Fact]
    public void TableBindingIsListedWithItsPrefix()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = foo/ctrl+a=new_window");

        Assert.True(Has(entries, "foo/ctrl+a", "new_window"));
        Assert.Contains("keybind = foo/ctrl+a=new_window",
            ListKeybinds.RenderPlain(entries, docs: false));
    }

    [Fact]
    public void PerformableBindingKeepsItsPrefixInTheListing()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = performable:ctrl+a=new_window");

        Assert.True(Has(entries, "performable:ctrl+a", "new_window"));
        Assert.Contains("keybind = performable:ctrl+a=new_window",
            ListKeybinds.RenderPlain(entries, docs: false));
        Assert.Contains("a", ListKeybinds.RenderTable(entries, color: false));
    }

    [Fact]
    public void PhysicalBindingKeepsItsPrefixInTheListing()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = physical:ctrl+a=new_window");

        Assert.True(Has(entries, "physical:ctrl+a", "new_window"));
        Assert.Contains("keybind = physical:ctrl+a=new_window",
            ListKeybinds.RenderPlain(entries, docs: false));
    }

    [Fact]
    public void UnconsumedBindingKeepsItsPrefixInTheListing()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = unconsumed:ctrl+a=new_window");

        Assert.True(Has(entries, "unconsumed:ctrl+a", "new_window"));
        Assert.Contains("keybind = unconsumed:ctrl+a=new_window",
            ListKeybinds.RenderPlain(entries, docs: false));
    }

    [Fact]
    public void UnbindDropsTheDefaultButKeepsTheSiblings()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = ctrl+tab=");

        Assert.False(Has(entries, "ctrl+tab", "next_tab"));
        Assert.True(Has(entries, "ctrl+page_down", "next_tab"));
        Assert.True(Has(entries, "ctrl+shift+right", "next_tab"));
    }

    [Fact]
    public void ClearLeavesOnlyTheUsersOwnBindings()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = clear", "keybind = ctrl+shift+y=new_tab");

        Assert.Equal(new[] { "ctrl+shift+y" }, entries.Select(e => e.Chord).ToArray());
        Assert.Equal("new_tab", entries[0].Action);
    }

    [Fact]
    public void RenderPlainWritesGhosttyKeybindLines()
    {
        string text = ListKeybinds.RenderPlain(Snapshot(), docs: false);

        Assert.StartsWith("keybind = ", text);
        Assert.Contains("keybind = ctrl+shift+t=new_tab\n", text);
        Assert.DoesNotContain("#", text);
        Assert.Equal(Snapshot().Count, text.Split('\n').Count(line => line.Length > 0));
    }

    [Fact]
    public void RenderPlainWithDocsAnnotatesEachEntry()
    {
        string text = ListKeybinds.RenderPlain(Snapshot(), docs: true);

        Assert.Contains("\n# new_tab\n#   Open a new tab in the current window.\nkeybind = ctrl+shift+t=new_tab\n", text);
    }

    [Fact]
    public void TableAlignsActionsPastTheWidestChord()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot();
        string plain = ListKeybinds.RenderTable(entries, color: false);

        string[] lines = plain.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(entries.Count, lines.Length);

        int column = entries.Max(e => ChordWidth(e.Chord)) + 3;
        for (int i = 0; i < entries.Count; i++)
            Assert.Equal(entries[i].Action, lines[i][column..]);
    }

    [Fact]
    public void TableColoursModifiersAndArguments()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot("keybind = ctrl+shift+y=new_tab");
        string colored = ListKeybinds.RenderTable(entries, color: true);

        // ctrl is palette index 2 and shift index 4, ghostty's own choices.
        Assert.Contains("\u001b[38;5;2mctrl ", colored);
        Assert.Contains("\u001b[38;5;4mshift", colored);
        // The argument of an action is magenta (5) and the ":" is dim.
        Assert.Contains("\u001b[2m:", colored);
        Assert.Contains("\u001b[38;5;5m", colored);

        // Without colour the same rows carry no escapes at all.
        string plain = ListKeybinds.RenderTable(entries, color: false);
        Assert.DoesNotContain("\u001b[", plain);

        // Stripping the escapes leaves exactly the plain render.
        Assert.Equal(plain, Regex.Replace(colored, "\u001b\\[[0-9;]*m", string.Empty));
    }

    [Fact]
    public void TableSortsByModifierCountThenValue()
    {
        IReadOnlyList<ListKeybinds.Entry> entries = Snapshot();

        int previous = int.MaxValue;
        foreach (ListKeybinds.Entry entry in entries)
        {
            int count = entry.Chord.Split('+').Length - 1;
            Assert.True(count <= previous, $"{entry.Chord} is out of order");
            previous = count;
        }

        // super is the heaviest modifier in ghostty's ordering, so it heads the list.
        Assert.StartsWith("super+", entries[0].Chord);
    }
}