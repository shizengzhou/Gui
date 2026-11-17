using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gui.Cli;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The theme catalog behind "+list-themes": the light/dark folders, a theme file's colors, and
/// the "theme" value the browser writes (only the matching half changes).
/// </summary>
public class ThemeCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-themes-" + Guid.NewGuid().ToString("N"));

    public ThemeCatalogTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "light"));
        Directory.CreateDirectory(Path.Combine(_dir, "dark"));
    }

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

    private void Write(string folder, string name, string body) =>
        File.WriteAllText(Path.Combine(_dir, folder, name), body);

    [Fact]
    public void LoadsBothFoldersAndSkipsDotFiles()
    {
        Write("light", "Day One", "background = #ffffff\nforeground = #000000\n");
        Write("dark", "Night One", "background = #101010\nforeground = #eeeeee\n");
        Write("dark", ".hidden", "background = #000000\n");

        List<ThemeEntry> themes = ThemeCatalog.Load(_dir, null);

        Assert.Equal(2, themes.Count);
        Assert.Equal("Day One", themes[0].Name);
        Assert.True(themes[0].Light);
        Assert.Equal("Night One", themes[1].Name);
        Assert.False(themes[1].Light);
    }

    [Fact]
    public void UserFolderThemesShadowBundledOnes()
    {
        Write("dark", "Shared", "background = #101010\nforeground = #eeeeee\n");
        string user = Path.Combine(_dir, "user-themes");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "Shared"), "background = #ffffff\nforeground = #000000\n");
        File.WriteAllText(Path.Combine(user, "Mine"), "background = #fefefe\nforeground = #111111\n");

        List<ThemeEntry> themes = ThemeCatalog.Load(_dir, user);

        // One entry per name: the user's file replaces the bundled one.
        Assert.Equal(2, themes.Count);
        ThemeEntry shared = themes.Single(t => t.Name == "Shared");
        Assert.True(shared.Light);
        Assert.Equal(Path.Combine(user, "Shared"), shared.File);

        // A user theme has no folder to classify it, so its background decides.
        ThemeEntry mine = themes.Single(t => t.Name == "Mine");
        Assert.True(mine.Light);
        Assert.Equal(new Gui.Term.Rgb(0x11, 0x11, 0x11), ThemeCatalog.Read(mine, _dir)!.Foreground);
    }

    [Fact]
    public void ReadsTheSurfaceCursorAndPalette()
    {
        Write("dark", "Mine", string.Join("\n", new[]
        {
            "# a comment",
            "background = #101015",
            "foreground = #eeeeee",
            "cursor-color = #ff0000",
            "palette = 1=#aa0000",
            "palette = 15=#ffffff",
            "palette = 99=#123456",
        }) + "\n");

        ThemeColors colors = ThemeCatalog.Read(new ThemeEntry("Mine", false), _dir)!;

        Assert.Equal(new Gui.Term.Rgb(0x10, 0x10, 0x15), colors.Background);
        Assert.Equal(new Gui.Term.Rgb(0xEE, 0xEE, 0xEE), colors.Foreground);
        Assert.True(colors.CursorSet);
        Assert.Equal(new Gui.Term.Rgb(0xFF, 0, 0), colors.Cursor);
        Assert.Equal(new Gui.Term.Rgb(0xAA, 0, 0), colors.Palette[1]);
        Assert.Equal(new Gui.Term.Rgb(0xFF, 0xFF, 0xFF), colors.Palette[15]);
        // A slot the file never sets keeps the xterm fallback.
        Assert.Equal(new Gui.Term.Rgb(0, 0, 0), colors.Palette[0]);
    }

    [Fact]
    public void MissingFilesReadAsNull()
    {
        Assert.Null(ThemeCatalog.Read(new ThemeEntry("nope", true), _dir));
    }

    [Fact]
    public void ApplyOnlyTouchesTheMatchingHalf()
    {
        var light = new ThemeEntry("Day One", true);
        var dark = new ThemeEntry("Night One", false);

        Assert.Equal("light:Day One,dark:" + Gui.Term.Config.DarkThemeName,
            ThemeCatalog.Apply(null, light));
        Assert.Equal("light:Day One,dark:" + Gui.Term.Config.DarkThemeName,
            ThemeCatalog.Apply("", light));
        Assert.Equal("light:Old,dark:Night One",
            ThemeCatalog.Apply("light:Old,dark:Old", dark));
        Assert.Equal("light:Old,dark:Night One",
            ThemeCatalog.Apply("light:Old,dark:Old", dark));
        // An unprefixed value applies to both halves, so each side can still be replaced.
        Assert.Equal("light:Day One,dark:Unprefixed",
            ThemeCatalog.Apply("Unprefixed", light));
    }

    [Fact]
    public void FilterIgnoresCase()
    {
        var themes = new List<ThemeEntry>
        {
            new("Catppuccin Frappe", false),
            new("Aardvark Blue", false),
            new("3024 Day", true),
        };

        Assert.Single(ThemeCatalog.Filter(themes, "cat"));
        Assert.Equal(3, ThemeCatalog.Filter(themes, "a").Count);
        Assert.Empty(ThemeCatalog.Filter(themes, "zzz"));
        Assert.Equal(3, ThemeCatalog.Filter(themes, string.Empty).Count);
    }
}
