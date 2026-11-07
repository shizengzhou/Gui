using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Which tab stays selected after a close. The bug this pins down: closing a tab that sits
/// *behind* the active one used to select the closed tab's index, so the selection jumped to
/// a neighbour that had nothing to do with what the user was looking at.
/// </summary>
public class TabSelectionTests
{
    [Fact]
    public void ClosingATabBehindTheActiveOneKeepsTheSelection()
    {
        // [0][1 active][2 closed][3] -> [0][1 active][3]
        Assert.Equal(1, TabSelection.AfterClose(active: 1, closed: 2, countAfterClose: 3));
    }

    [Fact]
    public void ClosingATabInFrontOfTheActiveOneShiftsTheIndexDown()
    {
        // [0 closed][1 active][2][3] -> [0 active][1][2]
        Assert.Equal(0, TabSelection.AfterClose(active: 1, closed: 0, countAfterClose: 3));
    }

    [Fact]
    public void ClosingTheActiveTabSelectsTheTabThatTookItsPlace()
    {
        // [0][1 active][2][3], close 1 -> [0][1 (old 2) active][2 (old 3)]
        Assert.Equal(1, TabSelection.AfterClose(active: 1, closed: 1, countAfterClose: 3));
    }

    [Fact]
    public void ClosingTheLastActiveTabFallsBackToTheNewLastTab()
    {
        Assert.Equal(2, TabSelection.AfterClose(active: 3, closed: 3, countAfterClose: 3));
    }

    [Fact]
    public void ClosingTheOnlyTabLeavesIndexZero()
    {
        Assert.Equal(0, TabSelection.AfterClose(active: 0, closed: 0, countAfterClose: 0));
    }

    [Theory]
    [InlineData(0, 1, 0)] // [0 active][1 closed][2] -> [0 active][1]
    [InlineData(0, 2, 0)] // [0 active][1][2 closed] -> [0 active][1]
    [InlineData(2, 0, 1)] // [0 closed][1][2 active] -> [0][1 active]
    public void NeighboursKeepTheirRelationship(int active, int closed, int expected)
    {
        Assert.Equal(expected, TabSelection.AfterClose(active, closed, 2));
    }
}
