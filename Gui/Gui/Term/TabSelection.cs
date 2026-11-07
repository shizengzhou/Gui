using System;

namespace Gui.Term;

/// <summary>
/// Which tab ends up selected after one is closed. Closing a tab in front of the active one
/// only shifts its index; closing a tab behind it changes nothing; closing the active tab
/// hands the selection to the tab that took its place, or to the new last tab.
/// </summary>
internal static class TabSelection
{
    /// <param name="active">Index of the active tab before the close.</param>
    /// <param name="closed">Index of the tab being closed.</param>
    /// <param name="countAfterClose">Tabs left once the close has happened (at least one).</param>
    public static int AfterClose(int active, int closed, int countAfterClose)
    {
        if (countAfterClose <= 0) return 0;
        if (closed < active) return active - 1;
        if (closed == active) return Math.Min(active, countAfterClose - 1);
        return active;
    }
}
