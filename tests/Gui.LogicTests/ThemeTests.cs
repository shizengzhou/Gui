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

    [Fact]
    public void OverCompositesATranslucentColourOnAnOpaqueOne()
    {
        Windows.UI.Color opaque = Theme.Over(Theme.Rgba(255, 255, 255, 255), Theme.Rgba(255, 0, 0, 0));
        Assert.Equal(255, opaque.R);

        Windows.UI.Color clear = Theme.Over(Theme.Rgba(0, 255, 255, 255), Theme.Rgba(255, 10, 20, 30));
        Assert.Equal(10, clear.R);
        Assert.Equal(20, clear.G);
        Assert.Equal(30, clear.B);

        Windows.UI.Color half = Theme.Over(Theme.Rgba(128, 255, 255, 255), Theme.Rgba(255, 0, 0, 0));
        Assert.InRange(half.R, 127, 128);
        Assert.Equal(255, half.A);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACustomThemeIslandIsTheCompositedWindowBackdrop(bool light)
    {
        TerminalTheme saved = Theme.Terminal;
        bool savedLight = Theme.Light;
        bool savedWindowLight = Theme.WindowLight;
        try
        {
            // The design's --window-bg is the mica with the scrim on top, so the island and
            // the chrome are one surface; a custom theme has to derive the same composite or
            // the island shows up as a card of its own shade.
            Theme.Light = light;
            Theme.WindowLight = light;
            Theme.Terminal = new TerminalTheme { Custom = true, Background = new Rgb(0, 0, 0) };

            Assert.Equal(Theme.Over(Theme.Scrim(), Theme.WindowMica()), Theme.ContentBg());
            Assert.NotEqual(Theme.TermSurfaceBg(), Theme.ContentBg());
        }
        finally
        {
            Theme.Terminal = saved;
            Theme.Light = savedLight;
            Theme.WindowLight = savedWindowLight;
        }
    }
}
