using System;
using System.IO;
using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Command-line overrides on top of the parsed config: "gui --font-size=18" has to behave
/// exactly like that line in the file, including repeatable keys and validation.
/// </summary>
public class ConfigOverrideTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-ovr-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not fail the test run.
        }
    }

    private Config Load(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return Config.Load(path);
    }

    [Fact]
    public void OverrideReplacesTheFileValue()
    {
        Config config = Load("font-size = 12\n");

        config.ApplyOverrides(new[] { "font-size=18" });

        Assert.Equal(18f, config.FontSize);
    }

    [Fact]
    public void OverrideBeatsAnEarlierDuplicateFlag()
    {
        Config config = Load(string.Empty);

        config.ApplyOverrides(new[] { "font-size=12", "font-size=14" });

        Assert.Equal(14f, config.FontSize);
        Assert.Equal("14", config.Values["font-size"][0]);
    }

    [Fact]
    public void RepeatableKeysAppendToTheFile()
    {
        Config config = Load("font-family = Consolas\n");

        config.ApplyOverrides(new[] { "font-family=JetBrainsMono Nerd Font" });

        Assert.Equal(new[] { "Consolas", "JetBrainsMono Nerd Font" }, config.FontFamilies);
    }

    [Fact]
    public void AnEmptyValueClearsTheKey()
    {
        Config config = Load("font-family = Consolas\n");

        config.ApplyOverrides(new[] { "font-family=" });

        Assert.Empty(config.FontFamilies);
        Assert.False(config.Values.ContainsKey("font-family"));
    }

    [Fact]
    public void AnUnknownKeyIsReportedAndIgnored()
    {
        Config config = Load("font-size = 12\n");

        var problems = config.ApplyOverrides(new[] { "no-such-option=1" });

        Assert.Single(problems);
        Assert.Equal(ConfigSeverity.Warning, problems[0].Severity);
        Assert.Equal(12f, config.FontSize);
    }

    [Fact]
    public void ABadValueIsReportedAndLeavesTheFileValue()
    {
        Config config = Load("font-size = 12\n");

        var problems = config.ApplyOverrides(new[] { "font-size=big" });

        Assert.Single(problems);
        Assert.Contains("expected a number", problems[0].Message);
        Assert.Equal(12f, config.FontSize);
    }

    [Fact]
    public void AnAliasResolvesToTheCanonicalKey()
    {
        Config config = Load(string.Empty);

        config.ApplyOverrides(new[] { "shell=cmd.exe" });

        Assert.Equal("cmd.exe", config.Command);
    }

    [Fact]
    public void AForeignKeyIsReportedAsANotice()
    {
        Config config = Load(string.Empty);

        var problems = config.ApplyOverrides(new[] { "macos-window-shadow=false" });

        Assert.Single(problems);
        Assert.Equal(ConfigSeverity.Notice, problems[0].Severity);
    }

    [Fact]
    public void ValidateOverrideChecksTheSameRulesAsTheFile()
    {
        Assert.True(Config.ValidateOverride("font-size=18", out _));
        Assert.True(Config.ValidateOverride("font-family=", out _));

        // Ghostty writes colours without the '#', in theme files and in its own examples.
        Assert.True(Config.ValidateOverride("background=282c34", out _));
        Assert.True(Config.ValidateOverride("background=#282c34", out _));

        Assert.False(Config.ValidateOverride("font-size=big", out string reason));
        Assert.Contains("expected a number", reason);

        Assert.False(Config.ValidateOverride("nope=1", out reason));
        Assert.Contains("unknown config key", reason);

        Assert.False(Config.ValidateOverride(@"config-file=C:\x", out reason));
        Assert.Contains("config-file", reason);
    }
}
