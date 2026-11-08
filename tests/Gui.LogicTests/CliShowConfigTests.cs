using System;
using System.IO;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Covers the two building blocks of "gui-cli +show-config": the parse-only config load
/// (which must not touch libghostty-vt) and the pure renderer.
/// </summary>
public class CliShowConfigTests
{
    private static string WriteConfig(params (string Name, string Content)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gui-cli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach ((string name, string content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);
        return Path.Combine(dir, "config");
    }

    [Fact]
    public void LoadRawMergesIncludesWithoutResolving()
    {
        string file = WriteConfig(
            ("config", "font-size = 14\nconfig-file = extra\n"),
            ("extra", "font-size = 18\ntheme = Some Theme\n"));
        try
        {
            Config config = Config.LoadRaw(file);
            // Included files load after the including file, so their values win.
            Assert.Equal("18", config.Last("font-size"));
            Assert.Equal("Some Theme", config.Last("theme"));
            Assert.Empty(config.Diagnostics);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Fact]
    public void LoadRawOnAMissingFileReturnsDefaults()
    {
        Config config = Config.LoadRaw(Path.Combine(Path.GetTempPath(), "gui-cli-missing-" + Guid.NewGuid().ToString("N")));
        Assert.Empty(config.Values);
    }

    [Fact]
    public void RenderChangesOnlyListsSetKeys()
    {
        string file = WriteConfig(("config", "font-size = 14\nfont-family = JetBrains Mono\nfont-family = Consolas\n"));
        try
        {
            string text = Gui.Cli.ShowConfig.Render(Config.LoadRaw(file), includeAll: false, docs: false);
            Assert.Contains("font-size = 14\n", text);
            Assert.Contains("font-family = JetBrains Mono\n", text);
            Assert.Contains("font-family = Consolas\n", text);
            // An unset key is omitted in changes-only mode.
            Assert.DoesNotContain("command =\n", text);
            Assert.DoesNotContain("command = ", text);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Fact]
    public void RenderFullIncludesUnsetKeysAsEmptyValues()
    {
        string file = WriteConfig(("config", "font-size = 14\n"));
        try
        {
            string text = Gui.Cli.ShowConfig.Render(Config.LoadRaw(file), includeAll: true, docs: false);
            Assert.Contains("font-size = 14\n", text);
            Assert.Contains("command =\n", text);
            Assert.Contains("tab-position =\n", text); // app-specific key
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, true);
        }
    }

    [Fact]
    public void RenderDefaultWithDocsCoversEveryOption()
    {
        string text = Gui.Cli.ShowConfig.Render(new Config(), includeAll: true, docs: true);
        Assert.StartsWith("# Every recognized option", text);
        Assert.Contains("# font size in points\n", text);
        Assert.Contains("font-size =\n", text);
        Assert.Contains("tab-position =\n", text);
        // Foreign-platform options are annotated, not dropped.
        Assert.Contains("# macOS only; ignored on Windows\n", text);
    }
}
