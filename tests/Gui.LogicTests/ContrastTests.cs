using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The "minimum-contrast" maths: the ratio has to follow WCAG 2.0, and the raised colour has
/// to actually reach the requested ratio without moving further than necessary.
/// </summary>
public class ContrastTests
{
    [Fact]
    public void RatioSpansOneToTwentyOne()
    {
        Assert.Equal(1.0, Contrast.Ratio(new Rgb(40, 40, 40), new Rgb(40, 40, 40)), 3);
        Assert.Equal(21.0, Contrast.Ratio(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 2);
        // The order of the two colours does not matter.
        Assert.Equal(
            Contrast.Ratio(new Rgb(0, 0, 0), new Rgb(255, 255, 255)),
            Contrast.Ratio(new Rgb(255, 255, 255), new Rgb(0, 0, 0)), 6);
    }

    [Fact]
    public void AColourThatAlreadyQualifiesIsUnchanged()
    {
        var foreground = new Rgb(220, 220, 220);
        var background = new Rgb(20, 20, 20);

        Assert.Equal(foreground, Contrast.Raise(foreground, background, 4f));
    }

    [Fact]
    public void NoMinimumLeavesTheColourAlone()
    {
        var foreground = new Rgb(30, 30, 30);
        var background = new Rgb(20, 20, 20);

        Assert.Equal(foreground, Contrast.Raise(foreground, background, 1f));
        Assert.Equal(foreground, Contrast.Raise(foreground, background, 0f));
    }

    [Theory]
    [InlineData(1.1f)]
    [InlineData(3f)]
    [InlineData(4.5f)]
    [InlineData(7f)]
    public void DarkTextOnADarkBackgroundIsLifted(float minimum)
    {
        var background = new Rgb(32, 32, 32);
        // 1.05:1 against the background, so even the 1.1 case has to move.
        var foreground = new Rgb(36, 36, 36);

        Rgb raised = Contrast.Raise(foreground, background, minimum);

        Assert.True(Contrast.Ratio(raised, background) >= minimum - 0.05,
            $"raised {raised.R},{raised.G},{raised.B} only reached " +
            $"{Contrast.Ratio(raised, background):0.###} of {minimum}");
        Assert.True(Contrast.Luminance(raised) > Contrast.Luminance(foreground));
    }

    [Fact]
    public void LightTextOnALightBackgroundIsDarkened()
    {
        var background = new Rgb(250, 250, 250);
        var foreground = new Rgb(235, 235, 235);

        Rgb raised = Contrast.Raise(foreground, background, 4.5f);

        Assert.True(Contrast.Ratio(raised, background) >= 4.45);
        Assert.True(Contrast.Luminance(raised) < Contrast.Luminance(foreground));
    }

    [Fact]
    public void TheColourMovesNoMoreThanTheRatioNeeds()
    {
        var background = new Rgb(0, 0, 0);
        var foreground = new Rgb(10, 10, 10);

        Rgb lifted = Contrast.Raise(foreground, background, 4.5f);

        // Just past the threshold, not all the way to white.
        Assert.True(lifted.R < 250);
        Assert.True(lifted.R >= Contrast.Raise(foreground, background, 3f).R);
    }

    [Fact]
    public void AnImpossibleRatioFallsBackToTheBetterEnd()
    {
        // Mid grey is 3.9:1 from white but 5.3:1 from black, so 21:1 lands on black.
        var background = new Rgb(128, 128, 128);

        Rgb raised = Contrast.Raise(new Rgb(130, 130, 130), background, 21f);

        Assert.Equal(0, raised.R);
        Assert.Equal(0, raised.G);
        Assert.Equal(0, raised.B);
        Assert.True(Contrast.Ratio(raised, background) >
                    Contrast.Ratio(new Rgb(255, 255, 255), background));
    }
}
