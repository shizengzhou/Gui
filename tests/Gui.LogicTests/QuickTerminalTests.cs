using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Gui.Term;
using Xunit;

namespace Gui.LogicTests;

/// <summary>
/// The quick terminal: where the drop-down lands on a monitor ("quick-terminal-position"),
/// how big it is ("quick-terminal-size") and the global chord that toggles it.
/// </summary>
public class QuickTerminalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gui-quick-" + Guid.NewGuid().ToString("N"));

    public QuickTerminalTests() => Directory.CreateDirectory(_dir);

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

    private Config Load(params string[] lines)
    {
        string path = Path.Combine(_dir, "config");
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n");
        return Config.Load(path);
    }

    private static readonly QuickTerminalRect Work = new(0, 0, 1920, 1080);

    [Fact]
    public void PositionFallsBackToTheTop()
    {
        Assert.Equal("top", QuickTerminalGeometry.NormalizePosition(null));
        Assert.Equal("top", QuickTerminalGeometry.NormalizePosition("sideways"));
        Assert.Equal("bottom", QuickTerminalGeometry.NormalizePosition(" Bottom "));
        Assert.Equal("center", QuickTerminalGeometry.NormalizePosition("center"));
    }

    [Fact]
    public void TopEdgeSpansTheWholeWidthAndTakesAQuarterOfTheHeight()
    {
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "top", null);

        // Ghostty's default when no size is given: 400 pixels out of the height, full width.
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 400), rect);
    }

    [Fact]
    public void PercentageTakesThatShareOfThePrimaryAxis()
    {
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "top", "50%");
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 540), rect);

        QuickTerminalRect bottom = QuickTerminalGeometry.Calculate(Work, "bottom", "25%");
        Assert.Equal(new QuickTerminalRect(0, 810, 1920, 270), bottom);
    }

    [Fact]
    public void TwoValuesSetBothAxes()
    {
        // Primary first: half the height, then a 300px width centred on the monitor.
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "top", "50%,300px");
        Assert.Equal(new QuickTerminalRect(810, 0, 300, 540), rect);
    }

    [Fact]
    public void SideEdgesRunTheFullHeight()
    {
        QuickTerminalRect left = QuickTerminalGeometry.Calculate(Work, "left", null);
        Assert.Equal(new QuickTerminalRect(0, 0, 400, 1080), left);

        QuickTerminalRect right = QuickTerminalGeometry.Calculate(Work, "right", "25%");
        Assert.Equal(new QuickTerminalRect(1440, 0, 480, 1080), right);
    }

    [Fact]
    public void CenterTakesWidthFromThePrimaryAxisOnALandscapeMonitor()
    {
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "center", null);
        Assert.Equal(new QuickTerminalRect(560, 340, 800, 400), rect);
    }

    [Fact]
    public void CenterTakesHeightFromThePrimaryAxisOnAPortraitMonitor()
    {
        var portrait = new QuickTerminalRect(0, 0, 1080, 1920);
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(portrait, "center", null);
        Assert.Equal(new QuickTerminalRect(340, 560, 400, 800), rect);
    }

    [Fact]
    public void SizesNeverLeaveTheWorkArea()
    {
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "top", "400%,5000px");
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 1080), rect);

        QuickTerminalRect side = QuickTerminalGeometry.Calculate(Work, "right", "5000px");
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 1080), side);
    }

    [Fact]
    public void PixelSizesAreDipsScaledToTheMonitor()
    {
        // 400 DIP on a 200% monitor is 800 physical pixels, the unit the work area uses.
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "top", null, 2.0);
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 800), rect);

        QuickTerminalRect custom = QuickTerminalGeometry.Calculate(Work, "top", "300px", 2.0);
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 600), custom);
    }

    [Fact]
    public void ABareNumberIsNotASize()
    {
        // Ghostty requires a unit, so "500" keeps the default instead of guessing pixels.
        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(Work, "top", "500");
        Assert.Equal(new QuickTerminalRect(0, 0, 1920, 400), rect);
    }

    [Fact]
    public void ConfigReadsTheQuickTerminalKeys()
    {
        Config config = Load(
            "quick-terminal-position = bottom",
            "quick-terminal-size = 40%,600px",
            "quick-terminal-screen = main",
            "quick-terminal-autohide = false",
            "quick-terminal-animation-duration = 0.5");

        Assert.Equal("bottom", config.QuickTerminalPosition);
        Assert.Equal("40%,600px", config.QuickTerminalSize);
        Assert.Equal("main", config.QuickTerminalScreen);
        Assert.False(config.QuickTerminalAutohide);
        Assert.Equal(0.5, config.QuickTerminalAnimationDuration, 3);
    }

    [Fact]
    public void ConfigDefaultsFollowGhostty()
    {
        Config config = Load();

        Assert.Equal("top", config.QuickTerminalPosition);
        Assert.Equal(string.Empty, config.QuickTerminalSize);
        Assert.Equal("mouse", config.QuickTerminalScreen);
        Assert.True(config.QuickTerminalAutohide);
        Assert.Equal(0.2, config.QuickTerminalAnimationDuration, 3);
    }

    [Fact]
    public void TheDefaultToggleIsAGlobalChord()
    {
        Keybinds binds = new(Load("font-size = 12"));

        var globals = binds.Global().ToList();
        Assert.Contains(("ctrl+backquote", "toggle_quick_terminal"), globals);
        Assert.Equal("Ctrl+`", binds.Label("toggle_quick_terminal", string.Empty));
    }

    [Fact]
    public void AGlobalLineOverridesTheDefaultAndUnbindingRemovesIt()
    {
        Keybinds rebound = new(Load("keybind = global:ctrl+alt+grave_accent=toggle_quick_terminal"));
        // Ghostty's defaults are ordinary bindings: a line for the same action adds a
        // chord, it does not replace the default one.
        var globals = rebound.Global().ToList();
        Assert.Contains(("ctrl+backquote", "toggle_quick_terminal"), globals);
        Assert.Contains(("ctrl+alt+backquote", "toggle_quick_terminal"), globals);

        Keybinds unbound = new(Load("keybind = global:ctrl+backquote="));
        Assert.DoesNotContain(("ctrl+backquote", "toggle_quick_terminal"),
            unbound.Global().Select(entry => entry));
    }

    [Fact]
    public void GlobalChordsMapOntoRegisterHotKey()
    {
        Assert.True(GlobalHotkey.TryParseChord("ctrl+backquote", out uint modifiers, out uint key));
        Assert.Equal(2u, modifiers);      // MOD_CONTROL
        Assert.Equal(0xC0u, key);         // VK_OEM_3

        Assert.True(GlobalHotkey.TryParseChord("ctrl+shift+f12", out modifiers, out key));
        Assert.Equal(6u, modifiers);      // MOD_CONTROL | MOD_SHIFT
        Assert.Equal(0x7Bu, key);         // VK_F12

        // A bare letter would swallow typing everywhere, so it is refused.
        Assert.False(GlobalHotkey.TryParseChord("backquote", out _, out _));
    }
}