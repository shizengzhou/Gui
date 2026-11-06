using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Arrow-key stepping in the command palette: hidden rows are skipped, both ends wrap, and a
/// filter that hides everything leaves the highlight where it is. This is the pure half of
/// the palette's key handling — the other half (routing the keys at the window root so a
/// focus change cannot leave the palette deaf) needs a running window and is checked by hand.
/// </summary>
public class PaletteSelectionTests
{
    private static readonly bool[] AllVisible = { true, true, true, true };

    [Fact]
    public void DownMovesToTheNextRow()
    {
        Assert.Equal(1, PaletteSelection.Step(AllVisible, 0, down: true));
        Assert.Equal(2, PaletteSelection.Step(AllVisible, 1, down: true));
    }

    [Fact]
    public void UpMovesToThePreviousRow()
    {
        Assert.Equal(1, PaletteSelection.Step(AllVisible, 2, down: false));
        Assert.Equal(2, PaletteSelection.Step(AllVisible, 3, down: false));
    }

    [Fact]
    public void DownWrapsFromTheLastRowToTheFirst()
    {
        Assert.Equal(0, PaletteSelection.Step(AllVisible, 3, down: true));
    }

    [Fact]
    public void UpWrapsFromTheFirstRowToTheLast()
    {
        Assert.Equal(3, PaletteSelection.Step(AllVisible, 0, down: false));
    }

    [Fact]
    public void DownSkipsRowsTheFilterHid()
    {
        var visible = new[] { true, false, false, true };

        Assert.Equal(3, PaletteSelection.Step(visible, 0, down: true));
        Assert.Equal(0, PaletteSelection.Step(visible, 3, down: true));
    }

    [Fact]
    public void UpSkipsRowsTheFilterHid()
    {
        var visible = new[] { true, false, false, true };

        Assert.Equal(3, PaletteSelection.Step(visible, 0, down: false));
        Assert.Equal(0, PaletteSelection.Step(visible, 3, down: false));
    }

    [Fact]
    public void ASingleVisibleRowStaysSelected()
    {
        var visible = new[] { false, true, false, false };

        Assert.Equal(1, PaletteSelection.Step(visible, 1, down: true));
        Assert.Equal(1, PaletteSelection.Step(visible, 1, down: false));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 3)]
    public void TheFirstArrowOnAFreshSubmenuPicksAnEnd(bool down, int expected)
    {
        // A submenu opens with the highlight past the last row, which means "no selection".
        Assert.Equal(expected, PaletteSelection.Step(AllVisible, AllVisible.Length, down));
    }

    [Fact]
    public void TheFirstArrowOnAFreshSubmenuSkipsHiddenRows()
    {
        var visible = new[] { false, true, true, false };

        Assert.Equal(1, PaletteSelection.Step(visible, visible.Length, down: true));
        Assert.Equal(2, PaletteSelection.Step(visible, visible.Length, down: false));
    }

    [Fact]
    public void AFilterThatHidesEverythingKeepsTheSelection()
    {
        var visible = new[] { false, false, false, false };

        Assert.Equal(1, PaletteSelection.Step(visible, 1, down: true));
        Assert.Equal(1, PaletteSelection.Step(visible, 1, down: false));
    }

    [Fact]
    public void AFilterThatHidesEverythingDoesNotCreateASelection()
    {
        var visible = new[] { false, false };

        Assert.Equal(visible.Length, PaletteSelection.Step(visible, visible.Length, down: true));
    }

    [Fact]
    public void AnEmptyListKeepsTheSelection()
    {
        Assert.Equal(0, PaletteSelection.Step(new bool[0], 0, down: true));
    }

    [Fact]
    public void AStaleSelectionStartsOverFromTheTop()
    {
        Assert.Equal(0, PaletteSelection.Step(AllVisible, 99, down: true));
        Assert.Equal(3, PaletteSelection.Step(AllVisible, 99, down: false));
    }
}
