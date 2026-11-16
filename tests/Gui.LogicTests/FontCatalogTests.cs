using System.Collections.Generic;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The font list behind "+list-fonts" and the picker: how Windows' font registry names split
/// into a family and a style, and how the picker spells a weight.
/// </summary>
public class FontCatalogTests
{
    [Theory]
    [InlineData("Arial (TrueType)", "Arial", "")]
    [InlineData("Arial Bold (TrueType)", "Arial", "Bold")]
    [InlineData("Arial Bold Italic (TrueType)", "Arial", "Bold Italic")]
    [InlineData("Cascadia Mono ExtraLight Italic (TrueType)", "Cascadia Mono", "ExtraLight Italic")]
    [InlineData("Segoe UI Semibold (TrueType)", "Segoe UI", "Semibold")]
    [InlineData("Consolas", "Consolas", "")]
    [InlineData("Microsoft YaHei & Microsoft YaHei UI (TrueType)", "Microsoft YaHei & Microsoft YaHei UI", "")]
    public void RegistryNamesSplitIntoFamilyAndStyle(string value, string family, string style)
    {
        (string actualFamily, string actualStyle) = FontCatalog.SplitInstalledName(value);
        Assert.Equal(family, actualFamily);
        Assert.Equal(style, actualStyle);
    }

    [Fact]
    public void AStyleOnItsOwnStaysAFamily()
    {
        Assert.Equal(("Bold", ""), FontCatalog.SplitInstalledName("Bold (TrueType)"));
    }

    [Theory]
    [InlineData(100, false, "Thin")]
    [InlineData(300, false, "Light")]
    [InlineData(400, false, "Regular")]
    [InlineData(400, true, "Italic")]
    [InlineData(500, false, "Medium")]
    [InlineData(600, false, "SemiBold")]
    [InlineData(700, false, "Bold")]
    [InlineData(700, true, "Bold Italic")]
    [InlineData(800, false, "ExtraBold")]
    [InlineData(900, false, "Black")]
    public void StyleNamesFollowTheWeight(int weight, bool italic, string expected)
    {
        Assert.Equal(expected, FontCatalog.StyleName(weight, italic));
    }

    [Fact]
    public void RegistryStylesMapBackToWeights()
    {
        Assert.Equal(700, FontCatalog.WeightOf("Bold"));
        Assert.Equal(700, FontCatalog.WeightOf("Bold Italic"));
        Assert.Equal(600, FontCatalog.WeightOf("SemiBold"));
        Assert.Equal(300, FontCatalog.WeightOf("Light"));
        Assert.Equal(400, FontCatalog.WeightOf(""));
        Assert.True(FontCatalog.IsItalic("Bold Italic"));
        Assert.True(FontCatalog.IsItalic("Oblique"));
        Assert.False(FontCatalog.IsItalic("Bold"));
    }

    [Fact]
    public void FilterMatchesPartOfTheName()
    {
        var families = new List<FontFamilyInfo>
        {
            new() { Name = "Cascadia Mono" },
            new() { Name = "Consolas" },
            new() { Name = "JetBrainsMono Nerd Font" },
        };

        Assert.Equal(3, FontCatalog.Filter(families, "").Count);
        Assert.Equal("Consolas", Assert.Single(FontCatalog.Filter(families, "cons")).Name);
        Assert.Equal("Cascadia Mono", Assert.Single(FontCatalog.Filter(families, "casc")).Name);
        // The query is trimmed, and both monospaced entries match "mono".
        Assert.Equal(2, FontCatalog.Filter(families, "  mono ").Count);
        Assert.Equal("Consolas", Assert.Single(FontCatalog.Filter(families, "  cons  ")).Name);
    }

    [Fact]
    public void StylesAreListedOnceAndInWeightOrder()
    {
        var family = new FontFamilyInfo
        {
            Name = "Example",
            Faces =
            {
                new FontFace("Bold", 700, false),
                new FontFace("", 400, false),
                new FontFace("Bold Italic", 700, true),
                new FontFace("Italic", 400, true),
                new FontFace("Bold", 700, false),
            },
        };

        Assert.Equal(new[] { "Regular", "Italic", "Bold", "Bold Italic" }, family.Styles());
    }
}
