using System.Collections.Generic;

namespace Gui.Term;

/// <summary>
/// Arrow-key stepping for the command palette. Rows the filter hid are skipped, the selection
/// wraps at both ends, and a list where nothing survived the filter is left alone so the
/// highlight never parks on a row the user can neither see nor reach.
/// </summary>
internal static class PaletteSelection
{
    /// <summary>
    /// Returns the row to highlight after one arrow key. <paramref name="current"/> is the row
    /// count when nothing is selected yet (a freshly opened submenu); the first arrow then
    /// picks the first or last visible row depending on its direction.
    /// </summary>
    /// <param name="visible">One entry per row, in list order: whether the filter shows it.</param>
    /// <param name="current">The highlighted row, or the row count when there is none.</param>
    /// <param name="down">True for Down, false for Up.</param>
    public static int Step(IReadOnlyList<bool> visible, int current, bool down)
    {
        int count = visible.Count;
        if (count == 0) return current;

        // "Nothing selected yet" and a stale index both start one step outside the list, so
        // the first iteration lands on its first or last row.
        int next = current < 0 || current >= count ? (down ? -1 : count) : current;

        for (int step = 0; step < count; step++)
        {
            next = down ? (next + 1) % count : (next + count - 1) % count;
            if (visible[next]) return next;
        }

        return current;
    }
}
