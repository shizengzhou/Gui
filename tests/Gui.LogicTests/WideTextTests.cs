using System;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// East Asian width: a wide grapheme must take two cells or the grid drifts.
/// </summary>
public class WideTextTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("Z")]
    [InlineData("0")]
    [InlineData(" ")]
    [InlineData("\u00e9")] // precomposed e-acute
    [InlineData("\u00df")] // sharp s
    public void NarrowTextIsOneCell(string text)
    {
        Assert.False(WideText.IsWide(text));
    }

    [Theory]
    [InlineData("\u4f60")] // ni
    [InlineData("\u597d")] // hao
    [InlineData("\u4e2d")] // zhong
    [InlineData("\u3042")] // hiragana a
    [InlineData("\ud55c")] // hangul
    public void CjkTextIsTwoCells(string text)
    {
        Assert.True(WideText.IsWide(text));
    }

    [Fact]
    public void FullWidthFormsAreTwoCells()
    {
        Assert.True(WideText.IsWide("\uff21")); // fullwidth A
        Assert.True(WideText.IsWide("\u3000")); // ideographic space
    }

    [Fact]
    public void EmojiAreTwoCells()
    {
        Assert.True(WideText.IsWide("\ud83d\ude00"));
    }

    [Fact]
    public void EmptyTextIsNotWide()
    {
        Assert.False(WideText.IsWide(string.Empty));
    }

    [Fact]
    public void ACombiningMarkKeepsTheBaseWidth()
    {
        // "e" plus a combining acute is one cell, not two.
        Assert.False(WideText.IsWide("e\u0301"));
    }

    [Theory]
    [InlineData(GhosttyNative.WIDE_NARROW, 3, 3, 3)]
    [InlineData(GhosttyNative.WIDE_WIDE, 3, 3, 4)]
    [InlineData(GhosttyNative.WIDE_SPACER_TAIL, 3, 2, 3)]
    [InlineData(GhosttyNative.WIDE_SPACER_HEAD, 3, 3, 4)]
    public void SpanOfFollowsTheCellsWidth(int kind, int x, int start, int end)
    {
        // Only a wide cell and its spacer take two cells; narrow cells never move, so a
        // selection over plain text keeps one cell per character.
        Assert.Equal((start, end), WideText.SpanOf(kind, x));
    }

    [Fact]
    public void ASpacerTailInTheFirstColumnStaysOneCell()
    {
        Assert.Equal((0, 0), WideText.SpanOf(GhosttyNative.WIDE_SPACER_TAIL, 0));
    }
}
