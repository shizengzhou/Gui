using Gui.Cli;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Covers "gui --key=value": Ghostty's launch flags, validated against the same schema and
/// the same rules the config file goes through.
/// </summary>
public class CliLaunchTests
{
    [Fact]
    public void ParseKeepsEveryOverrideInOrder()
    {
        Launch.Options options = Launch.Parse(new[] { "--background=282c34", "--font-size=18" });

        Assert.Null(options.Error);
        Assert.Equal(new[] { "background=282c34", "font-size=18" }, options.Overrides);
    }

    [Fact]
    public void ParseAcceptsSpacesAndEqualsInsideAValue()
    {
        Launch.Options options = Launch.Parse(new[]
        {
            "--font-family=JetBrainsMono Nerd Font",
            "--keybind=ctrl+shift+t=new_tab",
        });

        Assert.Null(options.Error);
        Assert.Equal("font-family=JetBrainsMono Nerd Font", options.Overrides[0]);
        Assert.Equal("keybind=ctrl+shift+t=new_tab", options.Overrides[1]);
    }

    [Fact]
    public void ParseRejectsAnUnknownKey()
    {
        Launch.Options options = Launch.Parse(new[] { "--bakcground=282c34" });

        Assert.NotNull(options.Error);
        Assert.Contains("unknown config key 'bakcground'", options.Error);
    }

    [Fact]
    public void ParseRejectsABadValue()
    {
        Launch.Options options = Launch.Parse(new[] { "--font-size=big" });

        Assert.NotNull(options.Error);
        Assert.Contains("expected a number", options.Error);
    }

    [Fact]
    public void ParseRejectsAFlagWithoutAValue()
    {
        Launch.Options options = Launch.Parse(new[] { "--background" });

        Assert.NotNull(options.Error);
        Assert.Contains("expected --key=value", options.Error);
    }

    [Fact]
    public void ParseRejectsConfigFile()
    {
        // The includes were already read, so a launch flag cannot add one.
        Launch.Options options = Launch.Parse(new[] { @"--config-file=C:\other" });

        Assert.NotNull(options.Error);
        Assert.Contains("config-file", options.Error);
    }

    [Fact]
    public void ParseAsksForAtLeastOneOverride()
    {
        Launch.Options options = Launch.Parse(System.Array.Empty<string>());

        Assert.NotNull(options.Error);
    }

    [Fact]
    public void ArgumentsQuoteValuesWithSpaces()
    {
        string text = Launch.Arguments(new[]
        {
            "background=282c34",
            "font-family=JetBrainsMono Nerd Font",
        });

        Assert.Equal("--background=282c34 --font-family=\"JetBrainsMono Nerd Font\"", text);
    }
}
