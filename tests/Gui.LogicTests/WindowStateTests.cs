using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// "window-save-state" persistence: the session file must survive a round trip unchanged and
/// must degrade to "no session" instead of failing launch when it is empty, corrupt, or from
/// an unknown version.
/// </summary>
public class WindowStateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "gui-window-state-" + Guid.NewGuid().ToString("N"));

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

    private string FilePath()
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, "window-state.json");
    }

    [Fact]
    public void RoundTripsWindowsTabsAndSplits()
    {
        string path = FilePath();
        var windows = new List<WindowState>
        {
            new()
            {
                X = 120,
                Y = 80,
                Width = 1400,
                Height = 900,
                Scale = 1.5,
                Maximized = true,
                SavedAt = new DateTime(2026, 11, 9, 0, 31, 0),
                ActiveTab = 1,
                Tabs =
                {
                    new TabState
                    {
                        Title = "PowerShell",
                        TitleOverride = "build",
                        Zoomed = true,
                        Focused = "R",
                        Root = new SplitState
                        {
                            Vertical = false,
                            Ratio = 0.35,
                            First = new SplitState
                            {
                                Pane = new PaneState
                                {
                                    Directory = @"C:\src\Gui",
                                    Command = "pwsh",
                                    Title = "Gui",
                                    Snapshot = "first.snap",
                                },
                            },
                            Second = new SplitState
                            {
                                Pane = new PaneState
                                {
                                    Directory = @"C:\src\Gui\tests",
                                    Command = "pwsh",
                                    Title = "tests",
                                },
                            },
                        },
                    },
                    new TabState
                    {
                        Title = "second",
                        Root = new SplitState
                        {
                            Pane = new PaneState { Directory = @"C:\", Command = "cmd", Title = "cmd" },
                        },
                    },
                },
            },
        };

        WindowStateStore.Save(path, windows);
        List<WindowState>? loaded = WindowStateStore.Load(path);

        Assert.NotNull(loaded);
        Assert.Single(loaded);
        WindowState window = loaded[0];
        Assert.Equal(120, window.X);
        Assert.Equal(1400, window.Width);
        Assert.Equal(1.5, window.Scale);
        Assert.True(window.Maximized);
        Assert.Equal(new DateTime(2026, 11, 9, 0, 31, 0), window.SavedAt);
        Assert.Equal(1, window.ActiveTab);
        Assert.Equal(2, window.Tabs.Count);

        TabState first = window.Tabs[0];
        Assert.Equal("build", first.TitleOverride);
        Assert.True(first.Zoomed);
        Assert.Equal("R", first.Focused);
        Assert.Equal(0.35, first.Root!.Ratio);
        Assert.Equal(@"C:\src\Gui\tests", first.Root.Second!.Pane!.Directory);
        Assert.Equal("pwsh", first.Root.First!.Pane!.Command);
        Assert.Equal("first.snap", first.Root.First!.Pane!.Snapshot);
    }

    [Fact]
    public void EmptySessionIsNotRestored()
    {
        string path = FilePath();
        WindowStateStore.Save(path, Array.Empty<WindowState>());
        Assert.Null(WindowStateStore.Load(path));
    }

    [Fact]
    public void MissingFileIsNotRestored()
    {
        Assert.Null(WindowStateStore.Load(Path.Combine(_dir, "absent.json")));
    }

    [Fact]
    public void UnknownVersionIsNotRestored()
    {
        string path = FilePath();
        File.WriteAllText(path, "{\"Version\":99,\"Windows\":[{\"Width\":800}]}", new UTF8Encoding(false));
        Assert.Null(WindowStateStore.Load(path));
    }

    [Fact]
    public void CorruptFileIsNotRestored()
    {
        string path = FilePath();
        File.WriteAllText(path, "{not json", new UTF8Encoding(false));
        Assert.Null(WindowStateStore.Load(path));
    }

    [Theory]
    [InlineData("window-save-state = always", "always")]
    [InlineData("window-save-state = default", "always")]
    [InlineData("window-save-state = never", "never")]
    public void WindowSaveStateOnlyHasAlwaysAndNever(string content, string expected)
    {
        string path = FilePath();
        File.WriteAllText(path, content, new UTF8Encoding(false));
        Assert.Equal(expected, Config.Load(path).WindowSaveState);
    }
}
