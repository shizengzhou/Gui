using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// "window-save-state" re-encodes a pane only while its snapshot still describes the pane.
/// The cache is what keeps an idle background tab from being encoded and written on every
/// structural change.
/// </summary>
public class SnapshotCacheTests
{
    [Fact]
    public void MatchesOnlyTheRecordedState()
    {
        var cache = new Term.SnapshotCache();

        Assert.False(cache.Matches(1, 80, 24)); // nothing recorded yet

        cache.Record("a.snap", revision: 1, cols: 80, rows: 24);

        Assert.Equal("a.snap", cache.Name);
        Assert.True(cache.Matches(1, 80, 24));
        Assert.False(cache.Matches(2, 80, 24)); // new output
        Assert.False(cache.Matches(1, 81, 24)); // reflowed wider
        Assert.False(cache.Matches(1, 80, 25)); // reflowed taller
    }

    [Fact]
    public void RecordingReplacesThePreviousSnapshot()
    {
        var cache = new Term.SnapshotCache();
        cache.Record("a.snap", revision: 1, cols: 80, rows: 24);
        cache.Record("b.snap", revision: 7, cols: 100, rows: 30);

        Assert.Equal("b.snap", cache.Name);
        Assert.True(cache.Matches(7, 100, 30));
        Assert.False(cache.Matches(1, 80, 24));
    }
}
