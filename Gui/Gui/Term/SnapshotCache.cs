namespace Gui.Term;

/// <summary>
/// Remembers the snapshot file a pane produced and the terminal state it captured, so a
/// "window-save-state" save can reuse that file while nothing changed. A structural change
/// saves every pane; re-encoding an idle background tab would cost a native encode and a
/// multi-megabyte write for nothing.
/// </summary>
internal sealed class SnapshotCache
{
    /// <summary>The file holding the snapshot of the state below, or null when unknown.</summary>
    public string? Name { get; private set; }

    private long _revision = long.MinValue;
    private ushort _cols;
    private ushort _rows;

    /// <summary>True when <see cref="Name"/> still describes exactly this state.</summary>
    public bool Matches(long revision, ushort cols, ushort rows) =>
        Name is not null && _revision == revision && _cols == cols && _rows == rows;

    public void Record(string name, long revision, ushort cols, ushort rows)
    {
        Name = name;
        _revision = revision;
        _cols = cols;
        _rows = rows;
    }
}
