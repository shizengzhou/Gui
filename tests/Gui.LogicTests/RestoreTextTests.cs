using System;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The saved screen and scrollback of a restored pane are written into the terminal once the
/// pane knows its real grid. Keeping that text afterwards would retain the previous
/// scrollback as a UTF-16 string for the pane's entire life, so the buffer must drop it as
/// soon as it has been handed over.
/// </summary>
public class RestoreTextTests
{
    [Fact]
    public void TakeHandsTheTextOverOnceAndForgetsIt()
    {
        var buffer = new Term.RestoreText();
        string scrollback = new string('x', 4 * 1024 * 1024); // 4M chars ≈ 8 MB of UTF-16

        buffer.Set(scrollback);
        Assert.Equal(scrollback.Length, buffer.Length);

        string written = buffer.Take();

        Assert.Same(scrollback, written);
        // The bug: the buffer kept the whole string after the write.
        Assert.Equal(0, buffer.Length);
        Assert.Equal(string.Empty, buffer.Text);
        // Taking again must not resurrect it.
        Assert.Equal(string.Empty, buffer.Take());
    }

    [Fact]
    public void SetReplacesThePendingText()
    {
        var buffer = new Term.RestoreText();
        buffer.Set("first");
        buffer.Set("second");

        Assert.Equal(6, buffer.Length);
        Assert.Equal("second", buffer.Take());
        Assert.Equal(0, buffer.Length);
    }
}
