using System;
using System.IO;
using Xunit;

namespace Gui.LogicTests;

/// <summary>The GUI_LOG environment variable decides how much reaches the log file.</summary>
public class DiagTests
{
    // The level values mirror DiagLevel; the enum stays internal to the app.
    private const int Debug = 0;
    private const int Info = 1;
    private const int Warn = 2;
    private const int Error = 3;
    private const int Off = 4;

    [Theory]
    [InlineData("debug", Debug)]
    [InlineData("DEBUG", Debug)]
    [InlineData(" trace ", Debug)]
    [InlineData("info", Info)]
    [InlineData("warn", Warn)]
    [InlineData("warning", Warn)]
    [InlineData("error", Error)]
    [InlineData("off", Off)]
    [InlineData("none", Off)]
    public void KnownValuesMapToTheirLevel(string value, int expected)
    {
        Assert.Equal((DiagLevel)expected, Diag.ParseLevel(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    public void UnknownValuesKeepTheDefault(string? value)
    {
        Assert.Equal(Info, (int)Diag.ParseLevel(value));
    }

    [Fact]
    public void OffIsTheMostSevereLevel()
    {
        Assert.True(Error < Off);
        Assert.True(Debug < Info);
        Assert.True(Info < Warn);
        Assert.True(Warn < Error);
    }

    [Fact]
    public void TheLogPathLivesInTheTempDirectory()
    {
        string path = Diag.LogPath;

        Assert.Contains("gui-startup.log", path);
        Assert.True(Path.IsPathRooted(path));
    }
}
