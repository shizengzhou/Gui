using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The tab rail's thin separators: only between two quiet, inactive tabs. The separator sits
/// in the leading gutter of the tab it belongs to, so "the tab on the left" is the one the
/// rule has to look at — that is what went wrong when the line lived at the end of the
/// previous chip and drifted with its content width.
/// </summary>
public class TabSeparatorsTests
{
    [Fact]
    public void TheFirstTabHasNoSeparator()
    {
        Assert.False(TabSeparators.Show(0, active: 3, hovered: false, previousHovered: false, topRail: true));
    }

    [Fact]
    public void TwoQuietInactiveTabsAreSeparated()
    {
        Assert.True(TabSeparators.Show(1, active: 3, hovered: false, previousHovered: false, topRail: true));
        Assert.True(TabSeparators.Show(2, active: 3, hovered: false, previousHovered: false, topRail: true));
    }

    [Fact]
    public void TheActiveTabShowsNoSeparatorInFrontOfItself()
    {
        Assert.False(TabSeparators.Show(2, active: 2, hovered: false, previousHovered: false, topRail: true));
    }

    [Fact]
    public void TheTabRightOfTheActiveOneShowsNoSeparator()
    {
        Assert.False(TabSeparators.Show(3, active: 2, hovered: false, previousHovered: false, topRail: true));
    }

    [Fact]
    public void AHoveredTabHidesItsOwnSeparator()
    {
        Assert.False(TabSeparators.Show(2, active: 0, hovered: true, previousHovered: false, topRail: true));
    }

    [Fact]
    public void AHoveredNeighbourHidesTheSeparator()
    {
        Assert.False(TabSeparators.Show(2, active: 0, hovered: false, previousHovered: true, topRail: true));
    }

    [Fact]
    public void TheSideRailsHaveNoSeparators()
    {
        Assert.False(TabSeparators.Show(2, active: 0, hovered: false, previousHovered: false, topRail: false));
    }

    [Fact]
    public void OnlyTheQuietInactivePairsInARailAreSeparated()
    {
        // [0][1][2 active][3]
        Assert.False(TabSeparators.Show(0, 2, false, false, true));
        Assert.True(TabSeparators.Show(1, 2, false, false, true));
        Assert.False(TabSeparators.Show(2, 2, false, false, true));
        Assert.False(TabSeparators.Show(3, 2, false, false, true));
    }
}
