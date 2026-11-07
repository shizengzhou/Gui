namespace Gui.Term;

/// <summary>
/// Where the tab rail draws its thin separators. The design separates two quiet, inactive
/// tabs only: never next to the active tab, never next to a hovered one, and never in front
/// of the first tab, because there is nothing on its left to separate it from.
/// </summary>
internal static class TabSeparators
{
    /// <param name="index">The tab the separator belongs to (it sits in its leading gutter).</param>
    /// <param name="active">Index of the active tab.</param>
    /// <param name="hovered">Whether this tab is hovered.</param>
    /// <param name="previousHovered">Whether the tab on the left is hovered.</param>
    /// <param name="topRail">False for the side rails, which have no separators.</param>
    public static bool Show(int index, int active, bool hovered, bool previousHovered, bool topRail)
    {
        if (!topRail || index <= 0) return false;
        if (index == active || hovered) return false;
        if (index - 1 == active || previousHovered) return false;
        return true;
    }
}
