using System;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>Design token helpers: colour mixing and luminance.</summary>
public class ThemeTests
{
    [Fact]
    public void RgbaKeepsTheChannelOrder()
    {
        Windows.UI.Color color = Theme.Rgba(0x80, 0x11, 0x22, 0x33);

        Assert.Equal(0x80, color.A);
        Assert.Equal(0x11, color.R);
        Assert.Equal(0x22, color.G);
        Assert.Equal(0x33, color.B);
    }

    [Fact]
    public void MixRgbInterpolatesBetweenTwoColors()
    {
        Rgb black = new(0, 0, 0);
        Rgb white = new(255, 255, 255);

        Assert.Equal(new Rgb(0, 0, 0), Theme.MixRgb(black, white, 0.0));
        Assert.Equal(new Rgb(255, 255, 255), Theme.MixRgb(black, white, 1.0));

        Rgb half = Theme.MixRgb(black, white, 0.5);
        Assert.Equal(128, half.R);
        Assert.Equal(128, half.G);
        Assert.Equal(128, half.B);
    }

    [Fact]
    public void PerceivedLuminanceOrdersBlackBelowWhite()
    {
        double black = Theme.PerceivedLuminance(new Rgb(0, 0, 0));
        double white = Theme.PerceivedLuminance(new Rgb(255, 255, 255));
        double grey = Theme.PerceivedLuminance(new Rgb(128, 128, 128));

        Assert.True(black < grey);
        Assert.True(grey < white);
        Assert.Equal(1.0, white, 3);
        Assert.Equal(0.0, black, 3);
    }

    [Fact]
    public void TintKeepsTheColorAndSetsTheAlpha()
    {
        Windows.UI.Color color = Theme.Tint(0x40, new Rgb(0x10, 0x20, 0x30));

        Assert.Equal(0x40, color.A);
        Assert.Equal(0x10, color.R);
        Assert.Equal(0x20, color.G);
        Assert.Equal(0x30, color.B);
    }
}
