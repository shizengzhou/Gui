using System;
using System.Linq;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Double-click word selection: the boundary set comes from "selection-word-chars" and
/// whitespace or blank cells always end a word.
/// </summary>
public class WordSelectionTests
{
    private static string[] Cells(string text) => text.Select(c => c.ToString()).ToArray();

    [Fact]
    public void ExpandsToTheWordUnderTheCursor()
    {
        string[] row = Cells("git commit --amend");

        Assert.True(WordSelection.TryExpand(row, 6, Config.DefaultSelectionWordChars,
            out int start, out int end));
        Assert.Equal(4, start);
        Assert.Equal(9, end);

        // A hyphen is not a default boundary, so the whole flag is one word.
        Assert.True(WordSelection.TryExpand(row, 13, Config.DefaultSelectionWordChars,
            out start, out end));
        Assert.Equal(11, start);
        Assert.Equal(17, end);
    }

    [Fact]
    public void BoundaryAndBlankCellsSelectNothing()
    {
        string[] row = Cells("a: b");

        Assert.False(WordSelection.TryExpand(row, 1, Config.DefaultSelectionWordChars, out _, out _));
        Assert.False(WordSelection.TryExpand(row, 2, Config.DefaultSelectionWordChars, out _, out _));
        Assert.False(WordSelection.TryExpand(row, -1, Config.DefaultSelectionWordChars, out _, out _));
        Assert.False(WordSelection.TryExpand(row, row.Length, Config.DefaultSelectionWordChars, out _, out _));
        Assert.False(WordSelection.TryExpand(Array.Empty<string>(), 0, Config.DefaultSelectionWordChars,
            out _, out _));
    }

    [Fact]
    public void CustomBoundaryCharactersSplitTheWord()
    {
        string[] row = Cells("foo-bar");

        Assert.True(WordSelection.TryExpand(row, 5, Config.DefaultSelectionWordChars,
            out int start, out int end));
        Assert.Equal(0, start);
        Assert.Equal(6, end);

        Assert.True(WordSelection.TryExpand(row, 5, "-", out start, out end));
        Assert.Equal(4, start);
        Assert.Equal(6, end);
    }

    [Fact]
    public void EmptyCellsAndWhitespaceAreWordBoundaries()
    {
        string[] row = { "a", "", "b" };

        Assert.False(WordSelection.TryExpand(row, 1, Config.DefaultSelectionWordChars, out _, out _));
        Assert.True(WordSelection.TryExpand(row, 0, Config.DefaultSelectionWordChars,
            out int start, out int end));
        Assert.Equal(0, start);
        Assert.Equal(0, end);

        Assert.False(WordSelection.IsWord(" ", Config.DefaultSelectionWordChars));
        Assert.False(WordSelection.IsWord("│", Config.DefaultSelectionWordChars));
        Assert.True(WordSelection.IsWord("z", Config.DefaultSelectionWordChars));
    }
}
