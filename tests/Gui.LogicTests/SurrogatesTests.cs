using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// A surrogate pair that arrives as two edits has to reach the shell as one character, not as
/// two U+FFFD.
/// </summary>
public class SurrogatesTests
{
    [Fact]
    public void SplitPairIsJoined()
    {
        char pending = '\0';
        Assert.Equal(string.Empty, Surrogates.Join("\uD83D", ref pending));
        Assert.Equal('\uD83D', pending);

        string text = Surrogates.Join("\uDE80", ref pending);
        Assert.Equal("\uD83D\uDE80", text);
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void WholePairPassesThrough()
    {
        char pending = '\0';
        Assert.Equal("\uD83D\uDE80", Surrogates.Join("\uD83D\uDE80", ref pending));
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void TextBeforeTheHighHalfIsKept()
    {
        char pending = '\0';
        Assert.Equal("ab", Surrogates.Join("ab\uD83D", ref pending));
        Assert.Equal('\uD83D', pending);
    }

    [Fact]
    public void AnOrphanHalfIsDropped()
    {
        // The low half never arrived: the next commit must not carry the high half along.
        char pending = '\0';
        Surrogates.Join("\uD83D", ref pending);
        Assert.Equal("x", Surrogates.Join("x", ref pending));
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void ALowHalfWithoutAHighOneIsDropped()
    {
        char pending = '\0';
        Assert.Equal("y", Surrogates.Join("\uDE80y", ref pending));
        Assert.Equal('\0', pending);
    }

    [Fact]
    public void OrdinaryTextIsUntouched()
    {
        char pending = '\0';
        Assert.Equal("hello", Surrogates.Join("hello", ref pending));
        Assert.Equal("\u4e2d", Surrogates.Join("\u4e2d", ref pending));
        Assert.Equal(string.Empty, Surrogates.Join(string.Empty, ref pending));
        Assert.Equal('\0', pending);
    }
}
