using System;
using System.IO;
using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// Configuration parsing and persistence. The tests write to throwaway files, never the
/// user's %APPDATA%\gui\config.
/// </summary>
public class ConfigTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-cfg-" + Guid.NewGuid().ToString("N"));

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

    private string Write(string content)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public void LoadReadsKeysAndIgnoresComments()
    {
        string path = Write(
            "# a comment\n" +
            "font-size = 17\n" +
            "  # indented comment\n" +
            "font-family = Consolas\n" +
            "tab-position = left\n" +
            "garbage line without separator\n");

        Config config = Config.Load(path);

        Assert.Equal(17f, config.FontSize);
        Assert.Equal("Consolas", config.FontFamily);
        Assert.Equal("left", config.TabPosition);
    }

    [Fact]
    public void LoadAcceptsAliasesAndQuotedValues()
    {
        string path = Write("shell = \"C:\\Windows\\System32\\cmd.exe\"\nfont-family = \"Consolas\"\n");

        Config config = Config.Load(path);

        Assert.Equal("C:\\Windows\\System32\\cmd.exe", config.Command);
        Assert.Equal("Consolas", config.FontFamily);
    }

    [Fact]
    public void LoadClampsOutOfRangeNumbers()
    {
        string path = Write("font-size = 999\nunfocused-split-opacity = 5\n");

        Config config = Config.Load(path);

        Assert.InRange(config.FontSize, 6f, 72f);
        Assert.InRange(config.UnfocusedSplitOpacity, 0.15f, 1f);
    }

    [Fact]
    public void LoadIgnoresInvalidNumbers()
    {
        string path = Write("font-size = huge\nunfocused-split-opacity = -3\n");

        Config config = Config.Load(path);

        // An unparsable size keeps the default, a parsable one is clamped into range.
        Assert.Equal(13f, config.FontSize);
        Assert.Equal(0.15f, config.UnfocusedSplitOpacity);
    }

    [Fact]
    public void LoadFallsBackToDefaultsForAMissingFile()
    {
        Config config = Config.Load(Path.Combine(_dir, "does-not-exist"));

        Assert.Equal(13f, config.FontSize);
        Assert.Equal(string.Empty, config.ThemeMode);
    }

    [Fact]
    public void LoadResolvesTheWorkingDirectory()
    {
        string path = Write("theme = light\n");

        Config config = Config.Load(path);

        Assert.False(string.IsNullOrEmpty(config.WorkingDirectory));
    }

    [Fact]
    public void SaveValueReplacesAnActiveKey()
    {
        string path = Write("theme = light\nfont-size = 13\n");

        Config.SaveValue(path, "theme", "dark");

        string text = File.ReadAllText(path);
        Assert.Contains("theme = dark", text);
        Assert.DoesNotContain("theme = light", text);
    }

    [Fact]
    public void SaveValueAppendsAMissingKey()
    {
        string path = Write("theme = light\n");

        Config.SaveValue(path, "font-size", "18");

        string text = File.ReadAllText(path);
        Assert.Contains("theme = light", text);
        Assert.Contains("font-size = 18", text);
    }

    [Fact]
    public void SaveValueLeavesTheCommentedTemplateAlone()
    {
        string path = Write("# theme = dark\ntheme = light\n");

        Config.SaveValue(path, "theme", "dark");

        string text = File.ReadAllText(path);
        Assert.Contains("# theme = dark", text);
        Assert.Contains("theme = dark", text);
    }

    [Fact]
    public void SaveValueCreatesTheFileWhenItIsMissing()
    {
        string path = Path.Combine(_dir, "nested", "config");

        Config.SaveValue(path, "theme", "light");

        Assert.True(File.Exists(path));
        Assert.Contains("theme = light", File.ReadAllText(path));
    }

    [Fact]
    public void CopyFromReplacesEveryValue()
    {
        Config source = new()
        {
            Command = "cmd.exe",
            WorkingDirectory = "C:\\work",
            FontFamily = "Consolas",
            FontSize = 19f,
            UnfocusedSplitOpacity = 0.3f,
            HasUnfocusedSplitFill = true,
            UnfocusedSplitFill = new Rgb(1, 2, 3),
            TabPosition = "left",
            ThemeMode = "light",
            WindowThemeMode = "dark",
            LightTheme = true,
            WindowLight = false,
        };

        Config target = new();
        target.CopyFrom(source);

        Assert.Equal(source.Command, target.Command);
        Assert.Equal(source.WorkingDirectory, target.WorkingDirectory);
        Assert.Equal(source.FontFamily, target.FontFamily);
        Assert.Equal(source.FontSize, target.FontSize);
        Assert.Equal(source.UnfocusedSplitOpacity, target.UnfocusedSplitOpacity);
        Assert.Equal(source.HasUnfocusedSplitFill, target.HasUnfocusedSplitFill);
        Assert.Equal(source.UnfocusedSplitFill, target.UnfocusedSplitFill);
        Assert.Equal(source.TabPosition, target.TabPosition);
        Assert.Equal(source.ThemeMode, target.ThemeMode);
        Assert.Equal(source.WindowThemeMode, target.WindowThemeMode);
        Assert.Equal(source.LightTheme, target.LightTheme);
        Assert.Equal(source.WindowLight, target.WindowLight);
    }

    [Fact]
    public void TheBuiltInThemesUseTheirNameAsValue()
    {
        Config light = Config.Load(Write("theme = Gui Light\nwindow-theme = light\n"));
        Assert.Equal(Config.LightThemeName, light.ThemeMode);
        Assert.True(light.LightTheme);

        Config dark = Config.Load(Write("theme = Gui Dark\nwindow-theme = dark\n"));
        Assert.Equal(Config.DarkThemeName, dark.ThemeMode);
        Assert.False(dark.LightTheme);
    }

    [Fact]
    public void TheOldShortThemeNamesStillWork()
    {
        Config light = Config.Load(Write("theme = light\nwindow-theme = light\n"));
        Assert.Equal(Config.LightThemeName, light.ThemeMode);
        Assert.True(light.LightTheme);

        Config dark = Config.Load(Write("theme = dark\nwindow-theme = dark\n"));
        Assert.Equal(Config.DarkThemeName, dark.ThemeMode);
        Assert.False(dark.LightTheme);
    }

    [Fact]
    public void AMismatchingThemeFallsBackToTheNamedPalette()
    {
        // A light theme in a dark window cannot be used, so the dark design palette takes
        // over -- and it carries its full name.
        Config config = Config.Load(Write("theme = Gui Light\nwindow-theme = dark\n"));

        Assert.Equal(Config.DarkThemeName, config.ThemeMode);
        Assert.False(config.LightTheme);
    }

    [Fact]
    public void ConfigFileLivesUnderAppData()
    {
        string path = Config.ConfigFile();

        Assert.Contains("gui", path);
        Assert.EndsWith("config", path);
    }
}
