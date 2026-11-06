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
}
