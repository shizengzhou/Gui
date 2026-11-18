using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// A grapheme that names its emoji drawing must not be handed to a text fallback family, whose
/// glyph for the same codepoint is monochrome.
/// </summary>
public class EmojiPresentationTests
{
    [Fact]
    public void TheEmojiSelectorAsksForEmoji()
    {
        Assert.True(EmojiPresentation.Asks("\u2764\uFE0F"));   // heavy black heart, emoji
        Assert.True(EmojiPresentation.Asks("\u2708\uFE0F"));   // airplane, emoji
    }

    [Fact]
    public void TheTextSelectorAsksForNothing()
    {
        Assert.False(EmojiPresentation.Asks("\u2764\uFE0E"));
    }

    [Fact]
    public void APlainCodepointAsksForNothing()
    {
        // Without a selector the family keeps its own drawing, which is what the configured
        // fallbacks have always provided.
        Assert.False(EmojiPresentation.Asks("\u2764"));
        Assert.False(EmojiPresentation.Asks("\U0001F680"));
        Assert.False(EmojiPresentation.Asks("a"));
        Assert.False(EmojiPresentation.Asks(string.Empty));
    }
}
