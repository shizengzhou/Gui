using System;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>Virtual key to libghostty-vt key code mapping.</summary>
public class KeyMapTests
{
    [Fact]
    public void LettersMapInOrder()
    {
        Assert.Equal(KeyMap.KeyA, KeyMap.FromVirtualKey(Windows.System.VirtualKey.A));
        Assert.Equal(KeyMap.KeyA + 25, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Z));
    }

    [Fact]
    public void DigitsMapInOrder()
    {
        Assert.Equal(KeyMap.Digit0, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Number0));
        Assert.Equal(KeyMap.Digit0 + 9, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Number9));
    }

    [Fact]
    public void NamedKeysMapToTheirOwnCodes()
    {
        Assert.Equal(KeyMap.Space, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Space));
        Assert.Equal(KeyMap.Enter, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Enter));
        Assert.Equal(KeyMap.Escape, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Escape));
        Assert.Equal(KeyMap.Backspace, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Back));
        Assert.Equal(KeyMap.ArrowLeft, KeyMap.FromVirtualKey(Windows.System.VirtualKey.Left));
    }

    [Fact]
    public void OemPunctuationMapsToTheUsLayoutCodes()
    {
        Assert.Equal(KeyMap.Semicolon, KeyMap.FromVirtualKey((Windows.System.VirtualKey)0xBA));
        Assert.Equal(KeyMap.Equal, KeyMap.FromVirtualKey((Windows.System.VirtualKey)0xBB));
        Assert.Equal(KeyMap.Comma, KeyMap.FromVirtualKey((Windows.System.VirtualKey)0xBC));
        Assert.Equal(KeyMap.Minus, KeyMap.FromVirtualKey((Windows.System.VirtualKey)0xBD));
        Assert.Equal(KeyMap.Period, KeyMap.FromVirtualKey((Windows.System.VirtualKey)0xBE));
        Assert.Equal(KeyMap.Slash, KeyMap.FromVirtualKey((Windows.System.VirtualKey)0xBF));
    }

    [Fact]
    public void UnknownKeysAreUnidentified()
    {
        Assert.Equal(KeyMap.Unidentified, KeyMap.FromVirtualKey(Windows.System.VirtualKey.None));
    }

    [Fact]
    public void PrintableKeysAreTextKeys()
    {
        Assert.True(KeyMap.IsTextKey(KeyMap.KeyA));
        Assert.True(KeyMap.IsTextKey(KeyMap.KeyZ));
        Assert.True(KeyMap.IsTextKey(KeyMap.Digit0));
        Assert.True(KeyMap.IsTextKey(KeyMap.Comma));
        Assert.True(KeyMap.IsTextKey(KeyMap.Space));
    }

    [Fact]
    public void ControlKeysAreNotTextKeys()
    {
        Assert.False(KeyMap.IsTextKey(KeyMap.Enter));
        Assert.False(KeyMap.IsTextKey(KeyMap.Escape));
        Assert.False(KeyMap.IsTextKey(KeyMap.ArrowLeft));
        Assert.False(KeyMap.IsTextKey(KeyMap.F1));
    }
}
