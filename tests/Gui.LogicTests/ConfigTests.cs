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
    public void LoadParsesXtChecksumExtensionBits()
    {
        // Ghostty's default is the DEC calculation, which is xterm's CSBITS zero.
        Assert.Equal(0, Config.Load(Write(string.Empty)).XtChecksumExtension);

        // esctest's --xterm-checksum spelling, xterm's checksumExtension 23.
        Assert.Equal(23, Config.Load(Write(
            "vt-xt-checksum-extension = no-negate,no-attributes,no-trim,full\n")).XtChecksumExtension);

        // Later tokens win, and "no-" turns an option back off.
        Assert.Equal(8, Config.Load(Write(
            "vt-xt-checksum-extension = undrawn,no-undrawn,undrawn,no-full\n")).XtChecksumExtension);
    }

    [Fact]
    public void LoadParsesXtChecksumReport()
    {
        Assert.False(Config.Load(Write(string.Empty)).XtChecksumReport);
        Assert.True(Config.Load(Write("vt-xt-checksum-report = true\n")).XtChecksumReport);
        Assert.False(Config.Load(Write("vt-xt-checksum-report = false\n")).XtChecksumReport);
    }

    [Fact]
    public void LoadParsesDesktopNotifications()
    {
        Assert.True(Config.Load(Write(string.Empty)).DesktopNotifications);
        Assert.False(Config.Load(Write("desktop-notifications = false\n")).DesktopNotifications);
        Assert.True(Config.Load(Write("desktop-notifications = true\n")).DesktopNotifications);
    }

    [Fact]
    public void LoadResolvesCustomShadersAndAnimation()
    {
        Directory.CreateDirectory(_dir);
        string shader = Path.Combine(_dir, "effect.glsl");
        File.WriteAllText(shader,
            "void mainImage(out vec4 c, in vec2 p) { c = vec4(1.0); }",
            new UTF8Encoding(false));

        Config config = Config.Load(Write(
            "custom-shader = ?missing.glsl\n" +
            "custom-shader = effect.glsl\n" +
            "custom-shader-animation = always\n"));

        Assert.Single(config.CustomShaders);
        Assert.Equal(Path.GetFullPath(shader), config.CustomShaders[0]);
        Assert.Equal("always", config.CustomShaderAnimation);
        Assert.DoesNotContain(config.Diagnostics, d => d.Severity == ConfigSeverity.Warning);

        // A required shader that does not exist is reported; "false" disables the loop.
        Config missing = Config.Load(Write(
            "custom-shader = nope.glsl\ncustom-shader-animation = false\n"));
        Assert.Empty(missing.CustomShaders);
        Assert.Equal("false", missing.CustomShaderAnimation);
        Assert.Contains(missing.Diagnostics,
            d => d.Key == "custom-shader" && d.Severity == ConfigSeverity.Notice);

        Assert.Equal("true", Config.Load(Write(string.Empty)).CustomShaderAnimation);
    }

    [Fact]
    public void LoadKeepsLeftAndRightRailWidthsIndependent()
    {
        Config config = Config.Load(Write("sidebar-width-left = 300\nsidebar-width-right = 420\n"));

        Assert.Equal(300.0, config.SidebarWidthLeft);
        Assert.Equal(420.0, config.SidebarWidthRight);
    }

    [Fact]
    public void LoadFallsBackToTheDefaultRailWidth()
    {
        // A hand-edited value outside the usable range keeps the default. The top of that
        // range is the rail's own ceiling (480), so a wider value is not silently clamped.
        Assert.Equal(240.0, Config.Load(Write("sidebar-width-left = 500\n")).SidebarWidthLeft);
        Assert.Equal(240.0, Config.Load(Write("sidebar-width-left = 5000\n")).SidebarWidthLeft);
        Assert.Equal(240.0, Config.Load(Write("sidebar-width-right = 5000\n")).SidebarWidthRight);

        // The two sides are independent, and a missing key keeps the default.
        Config config = Config.Load(Write("sidebar-width-right = 420\n"));
        Assert.Equal(240.0, config.SidebarWidthLeft);
        Assert.Equal(420.0, config.SidebarWidthRight);
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

        Assert.InRange(config.FontSize, Config.MinFontSizePoints, Config.MaxFontSizePoints);
        Assert.InRange(config.UnfocusedSplitOpacity, 0.15f, 1f);
    }

    [Fact]
    public void LoadIgnoresInvalidNumbers()
    {
        string path = Write("font-size = huge\nunfocused-split-opacity = -3\n");

        Config config = Config.Load(path);

        // An unparsable size keeps the default, a parsable one is clamped into range.
        Assert.Equal(12f, config.FontSize);
        Assert.Equal(0.15f, config.UnfocusedSplitOpacity);
    }

    [Fact]
    public void LoadParsesTheScrollbackBudget()
    {
        // Ghostty 1.4's default, and its grammar: a bare integer (base 0, so 0x works) or
        // "unlimited", which the config stores as the largest value of the type.
        Assert.Equal(50_000_000UL, Config.Load(Write(string.Empty)).ScrollbackLimit);
        Assert.Equal(2_000_000UL, Config.Load(Write("scrollback-limit-bytes = 2000000\n")).ScrollbackLimit);
        Assert.Equal(2_000_000UL, Config.Load(Write("scrollback-limit = 2000000\n")).ScrollbackLimit);
        Assert.Equal(20_000_000UL, Config.Load(Write("scrollback-limit = 0x1312D00\n")).ScrollbackLimit);
        Assert.Equal(0UL, Config.Load(Write("scrollback-limit = 0\n")).ScrollbackLimit);
        Assert.Equal(ulong.MaxValue, Config.Load(Write("scrollback-limit = unlimited\n")).ScrollbackLimit);

        // A unit suffix is not part of ghostty's grammar, so the value keeps the default.
        Assert.Equal(50_000_000UL, Config.Load(Write("scrollback-limit = 2MB\n")).ScrollbackLimit);
        Assert.Equal(50_000_000UL, Config.Load(Write("scrollback-limit = lots\n")).ScrollbackLimit);
    }

    [Fact]
    public void LoadParsesTheScrollbackRowCap()
    {
        // Ghostty 1.4 keeps no row cap unless one is asked for, and the grammar matches the
        // byte cap: a bare integer or "unlimited".
        Assert.Equal(ulong.MaxValue, Config.Load(Write(string.Empty)).ScrollbackLimitLines);
        Assert.Equal(1_000UL, Config.Load(Write("scrollback-limit-lines = 1000\n")).ScrollbackLimitLines);
        Assert.Equal(ulong.MaxValue,
            Config.Load(Write("scrollback-limit-lines = unlimited\n")).ScrollbackLimitLines);
        Assert.Equal(ulong.MaxValue,
            Config.Load(Write("scrollback-limit-lines = 1000 lines\n")).ScrollbackLimitLines);
    }

    [Fact]
    public void LoadReadsTheCompactSwitch()
    {
        // On unless the file turns it off; the usual boolean spellings are accepted.
        Assert.True(Config.Load(Write(string.Empty)).Compact);
        Assert.True(Config.Load(Write("compact = true\n")).Compact);
        Assert.True(Config.Load(Write("compact = 1\n")).Compact);
        Assert.True(Config.Load(Write("compact = on\n")).Compact);
        Assert.False(Config.Load(Write("compact = false\n")).Compact);
        Assert.True(Config.Load(Write("compact = tight\n")).Compact);
    }

    [Fact]
    public void LoadReadsThePaneHeaderSwitch()
    {
        // The pane header is shown unless the file asks for "hidden"; anything else keeps
        // the header, like the enum fallbacks in Ghostty's own options.
        Assert.Equal("show", Config.Load(Write(string.Empty)).PaneHeader);
        Assert.Equal("show", Config.Load(Write("pane-header = show\n")).PaneHeader);
        Assert.Equal("hidden", Config.Load(Write("pane-header = hidden\n")).PaneHeader);
        Assert.Equal("hidden", Config.Load(Write("pane-header = Hidden\n")).PaneHeader);
        Assert.Equal("show", Config.Load(Write("pane-header = collapsed\n")).PaneHeader);

        var target = new Config();
        target.CopyFrom(Config.Load(Write("pane-header = hidden\n")));
        Assert.Equal("hidden", target.PaneHeader);
    }

    [Fact]
    public void LoadKeepsOnlyTheImplementedScrollbarModes()
    {
        Assert.Equal("system", Config.Load(Write(string.Empty)).Scrollbar);
        Assert.Equal("never", Config.Load(Write("scrollbar = never\n")).Scrollbar);
        Assert.Equal("never", Config.Load(Write("scrollbar = NEVER\n")).Scrollbar);

        // Ghostty 1.4 only defines system and never, so anything else falls back to
        // the default.
        Assert.Equal("system", Config.Load(Write("scrollbar = always\n")).Scrollbar);
        Assert.Equal("system", Config.Load(Write("scrollbar = sometimes\n")).Scrollbar);
    }

    [Fact]
    public void LoadFallsBackToDefaultsForAMissingFile()
    {
        Config config = Config.Load(Path.Combine(_dir, "does-not-exist"));

        Assert.Equal(12f, config.FontSize);
        // The default theme is "light:Gui Light,dark:Gui Dark"; the appearance picks a side.
        Assert.Equal(Config.LightThemeName, config.ThemeLightName);
        Assert.Equal(Config.DarkThemeName, config.ThemeDarkName);
        Assert.Equal(config.WindowLight ? Config.LightThemeName : Config.DarkThemeName, config.ThemeMode);
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
    public void SaveValueKeepsCommentsAndBlankLines()
    {
        string path = Write("# theme = dark\n# a note\n\ntheme = light\n");

        Config.SaveValue(path, "theme", "dark");

        // Only the active line changes: commented-out settings, notes and blank lines stay
        // exactly where the user put them.
        Assert.Equal("# theme = dark\n# a note\n\ntheme = dark\n", File.ReadAllText(path));

        Config.SaveValue(path, "font-size", "15");
        Assert.Equal("# theme = dark\n# a note\n\ntheme = dark\nfont-size = 15\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void SaveListReplacesEveryLineOfTheKey()
    {
        string path = Write(
            "# keys\nkeybind = ctrl+shift+t=\nfont-size = 13\nkeybind = ctrl+shift+y=new_tab\n");

        Config.SaveList(path, "keybind",
            new[] { "ctrl+shift+t=", "ctrl+shift+y=", "ctrl+alt+n=new_tab" });

        // The block keeps the position of the first line; the second one is folded into it
        // instead of being left behind with an old binding.
        Assert.Equal(
            "# keys\nkeybind = ctrl+shift+t=\nkeybind = ctrl+shift+y=\nkeybind = ctrl+alt+n=new_tab\nfont-size = 13\n",
            File.ReadAllText(path));
    }

    [Fact]
    public void SaveListAppendsWhenTheKeyIsMissing()
    {
        string path = Write("theme = light\n\n");

        Config.SaveList(path, "keybind", new[] { "ctrl+shift+t=" });

        Assert.Equal("theme = light\nkeybind = ctrl+shift+t=\n\n", File.ReadAllText(path));
    }

    [Fact]
    public void SaveListRemovesTheKeyWhenTheListIsEmpty()
    {
        string path = Write("theme = light\nkeybind = clear\n");

        Config.SaveList(path, "keybind", Array.Empty<string>());

        Assert.Equal("theme = light\n", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureDefaultWritesAnEmptyFile()
    {
        string path = Path.Combine(_dir, "fresh", "config");

        Config.EnsureDefault(path);

        Assert.True(File.Exists(path));
        Assert.Equal(string.Empty, File.ReadAllText(path));
    }

    [Fact]
    public void PaddingDipsMatchesGhosttysPointScaling()
    {
        // Ghostty: floor(points * dpi / 72) physical pixels, with dpi = 96 * scale.
        // WinUI lays out in DIPs, so that pixel count is divided back by the scale.
        Assert.Equal(2.0, Config.PaddingDips(2, 1.0), 6);          // 96 dpi -> 2 px
        Assert.Equal(2.5, Config.PaddingDips(2, 2.0), 6);          // 192 dpi -> 5 px
        Assert.Equal(4.0 / 1.5, Config.PaddingDips(2, 1.5), 6);    // 144 dpi -> 4 px
        Assert.Equal(0.0, Config.PaddingDips(0, 2.0), 6);
        Assert.Equal(2.0, Config.PaddingDips(2, 0.0), 6);          // scale unknown yet
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
            SidebarWidthLeft = 320.0,
            SidebarWidthRight = 410.0,
            ThemeMode = "light",
            ThemeLightName = "Custom Light",
            ThemeDarkName = "Custom Dark",
            ThemeValue = "light:Custom Light,dark:Custom Dark",
            WindowThemeMode = "dark",
            LightTheme = true,
            WindowLight = false,
        };

        Config target = new();
        target.CopyFrom(source);

        Assert.Equal(source.SidebarWidthLeft, target.SidebarWidthLeft);
        Assert.Equal(source.SidebarWidthRight, target.SidebarWidthRight);
        Assert.Equal(source.Command, target.Command);
        Assert.Equal(source.WorkingDirectory, target.WorkingDirectory);
        Assert.Equal(source.FontFamily, target.FontFamily);
        Assert.Equal(source.FontSize, target.FontSize);
        Assert.Equal(source.UnfocusedSplitOpacity, target.UnfocusedSplitOpacity);
        Assert.Equal(source.HasUnfocusedSplitFill, target.HasUnfocusedSplitFill);
        Assert.Equal(source.UnfocusedSplitFill, target.UnfocusedSplitFill);
        Assert.Equal(source.TabPosition, target.TabPosition);
        Assert.Equal(source.ThemeMode, target.ThemeMode);
        Assert.Equal(source.ThemeLightName, target.ThemeLightName);
        Assert.Equal(source.ThemeDarkName, target.ThemeDarkName);
        Assert.Equal(source.ThemeValue, target.ThemeValue);
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
    public void TheBuiltInThemesExposeTheirRealColorsToTheTerminal()
    {
        // Programs query OSC 10/11 to tell a light terminal from a dark one, so the design
        // themes must carry their actual surface instead of an empty palette.
        Config light = Config.Load(Write("theme = Gui Light\nwindow-theme = light\n"));
        Assert.True(light.TerminalTheme.Design);
        Assert.Equal(new Rgb(255, 255, 255), light.TerminalTheme.Background);
        Assert.Equal(new Rgb(36, 41, 47), light.TerminalTheme.Foreground);

        Config dark = Config.Load(Write("theme = Gui Dark\nwindow-theme = dark\n"));
        Assert.True(dark.TerminalTheme.Design);
        Assert.Equal(new Rgb(7, 9, 14), dark.TerminalTheme.Background);
        Assert.Equal(new Rgb(245, 245, 247), dark.TerminalTheme.Foreground);
    }

    [Fact]
    public void ProgressStyleDefaultsOnAndCanBeDisabled()
    {
        Assert.True(Config.Load(Write("")).ProgressStyle);
        Assert.False(Config.Load(Write("progress-style = false\n")).ProgressStyle);
        Assert.True(Config.Load(Write("progress-style = true\n")).ProgressStyle);
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
    public void ThemeSupportsLightAndDarkHalves()
    {
        Config light = Config.Load(Write("theme = light:Gui Light,dark:Gui Dark\nwindow-theme = light\n"));
        Assert.Equal(Config.LightThemeName, light.ThemeLightName);
        Assert.Equal(Config.DarkThemeName, light.ThemeDarkName);
        Assert.Equal(Config.LightThemeName, light.ThemeMode);
        Assert.True(light.LightTheme);
        Assert.Equal("light:Gui Light,dark:Gui Dark", light.ThemeValue);

        Config dark = Config.Load(Write("theme = light:Gui Light,dark:Gui Dark\nwindow-theme = dark\n"));
        Assert.Equal(Config.DarkThemeName, dark.ThemeMode);
        Assert.False(dark.LightTheme);
    }

    [Fact]
    public void AMissingThemeHalfKeepsTheDefault()
    {
        Config config = Config.Load(Write("theme = light:Gui Light\nwindow-theme = dark\n"));

        Assert.Equal(Config.LightThemeName, config.ThemeLightName);
        Assert.Equal(Config.DarkThemeName, config.ThemeDarkName);
        Assert.Equal(Config.DarkThemeName, config.ThemeMode);
    }

    [Fact]
    public void AnUnprefixedThemeAppliesToBothAppearances()
    {
        Config light = Config.Load(Write("theme = Gui Light\nwindow-theme = light\n"));
        Config dark = Config.Load(Write("theme = Gui Light\nwindow-theme = dark\n"));

        Assert.Equal(Config.LightThemeName, light.ThemeMode);
        // The window brightness no longer forces a matching palette.
        Assert.Equal(Config.LightThemeName, dark.ThemeMode);
        Assert.True(dark.LightTheme);
        Assert.False(dark.WindowLight);
    }

    [Fact]
    public void WithThemeSideReplacesOnlyThatHalf()
    {
        Config config = Config.Load(Write("theme = light:Gui Light,dark:Gui Dark\n"));

        Assert.Equal("light:Gui Light,dark:Gui Dark",
            config.WithThemeSide(false, Config.DarkThemeName));
        Assert.Equal("light:Gui Light,dark:Gui Dark",
            config.WithThemeSide(true, Config.LightThemeName));
        Assert.Equal("light:Gui Dark,dark:Gui Dark",
            config.WithThemeSide(true, Config.DarkThemeName));
    }

    [Fact]
    public void ConfigFileLivesUnderAppData()
    {
        string path = Config.ConfigFile();

        Assert.Contains("gui", path);
        Assert.EndsWith("config", path);
    }
}
