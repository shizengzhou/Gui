using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Feature tags are packed the way Win2D's typography enum stores them. Getting the byte order
/// wrong is invisible: DirectWrite receives a tag no font has and drops the feature, which is
/// how "font-feature = cv01" once did nothing at all.
/// </summary>
public class FontFeatureTagTests
{
    [Fact]
    public void PackMatchesTheWin2DEnumValues()
    {
        // CanvasTypographyFeatureName.ContextualAlternates and .StandardLigatures hold these.
        Assert.Equal(0x746C6163u, FontFeatureTag.Pack("calt"));
        Assert.Equal(0x6167696Cu, FontFeatureTag.Pack("liga"));
        // No enum member: character variants are the reason the packing exists.
        Assert.Equal(0x31307663u, FontFeatureTag.Pack("cv01"));
    }

    [Theory]
    [InlineData("cv01", true)]
    [InlineData("ss20", true)]
    [InlineData("cv0", false)]
    [InlineData("cv011", false)]
    [InlineData("c\u00e901", false)]
    public void IsTagTakesFourPrintableCharacters(string text, bool expected)
    {
        Assert.Equal(expected, FontFeatureTag.IsTag(text));
    }
}
