using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The box-drawing table. Every character of the block must be described (otherwise the
/// renderer falls back to the font for that one cell and the box gets a colour and a weight
/// of its own), and the arms must match the names Unicode gives them.
/// </summary>
public class BoxDrawingTests
{
    [Fact]
    public void EveryCharacterOfTheBlockIsDescribed()
    {
        for (char rune = '\u2500'; rune <= '\u257F'; rune++)
        {
            Assert.True(BoxDrawing.TryGet(rune, out BoxSpec spec), $"U+{(int)rune:X4} is missing");

            bool noArms = spec.Left == BoxWeight.None && spec.Right == BoxWeight.None &&
                          spec.Up == BoxWeight.None && spec.Down == BoxWeight.None;
            Assert.True(!noArms || spec.Diagonal != BoxDiagonal.None,
                $"U+{(int)rune:X4} would draw nothing");
        }
    }

    [Fact]
    public void OrdinaryCharactersAreNotBoxDrawing()
    {
        Assert.False(BoxDrawing.TryGet('a', out _));
        Assert.False(BoxDrawing.TryGet(' ', out _));
        Assert.False(BoxDrawing.TryGet('\u24FF', out _));
        Assert.False(BoxDrawing.TryGet('\u2580', out _));
    }

    [Theory]
    [InlineData('\u2500', BoxWeight.Light, BoxWeight.Light, BoxWeight.None, BoxWeight.None)]
    [InlineData('\u2501', BoxWeight.Heavy, BoxWeight.Heavy, BoxWeight.None, BoxWeight.None)]
    [InlineData('\u2502', BoxWeight.None, BoxWeight.None, BoxWeight.Light, BoxWeight.Light)]
    [InlineData('\u253C', BoxWeight.Light, BoxWeight.Light, BoxWeight.Light, BoxWeight.Light)]
    [InlineData('\u254B', BoxWeight.Heavy, BoxWeight.Heavy, BoxWeight.Heavy, BoxWeight.Heavy)]
    [InlineData('\u256C', BoxWeight.Double, BoxWeight.Double, BoxWeight.Double, BoxWeight.Double)]
    // Mixed corners: "down light and right heavy".
    [InlineData('\u250D', BoxWeight.None, BoxWeight.Heavy, BoxWeight.None, BoxWeight.Light)]
    // Double horizontal with a single vertical through it.
    [InlineData('\u256A', BoxWeight.Double, BoxWeight.Double, BoxWeight.Light, BoxWeight.Light)]
    // Half lines.
    [InlineData('\u2574', BoxWeight.Light, BoxWeight.None, BoxWeight.None, BoxWeight.None)]
    [InlineData('\u257F', BoxWeight.None, BoxWeight.None, BoxWeight.Heavy, BoxWeight.Light)]
    public void ArmsMatchTheUnicodeName(char rune, BoxWeight left, BoxWeight right,
        BoxWeight up, BoxWeight down)
    {
        Assert.True(BoxDrawing.TryGet(rune, out BoxSpec spec));

        Assert.Equal(left, spec.Left);
        Assert.Equal(right, spec.Right);
        Assert.Equal(up, spec.Up);
        Assert.Equal(down, spec.Down);
    }

    [Fact]
    public void TheOddShapesAreMarked()
    {
        Assert.True(BoxDrawing.TryGet('\u2504', out BoxSpec dashed));
        Assert.Equal(3, dashed.Dashes);

        Assert.True(BoxDrawing.TryGet('\u254C', out BoxSpec doubleDash));
        Assert.Equal(2, doubleDash.Dashes);

        Assert.True(BoxDrawing.TryGet('\u256D', out BoxSpec rounded));
        Assert.True(rounded.Rounded);

        Assert.True(BoxDrawing.TryGet('\u2571', out BoxSpec forward));
        Assert.Equal(BoxDiagonal.Forward, forward.Diagonal);

        Assert.True(BoxDrawing.TryGet('\u2572', out BoxSpec backward));
        Assert.Equal(BoxDiagonal.Backward, backward.Diagonal);

        Assert.True(BoxDrawing.TryGet('\u2573', out BoxSpec cross));
        Assert.Equal(BoxDiagonal.Cross, cross.Diagonal);
    }

    [Fact]
    public void DoubleLinesAreRecognisedPerAxis()
    {
        Assert.True(BoxDrawing.TryGet('\u2550', out BoxSpec horizontal));
        Assert.True(horizontal.HorizontalDouble);
        Assert.False(horizontal.VerticalDouble);

        Assert.True(BoxDrawing.TryGet('\u2551', out BoxSpec vertical));
        Assert.False(vertical.HorizontalDouble);
        Assert.True(vertical.VerticalDouble);
    }
}
