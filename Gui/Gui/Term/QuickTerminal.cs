using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Gui.Term;

/// <summary>
/// Ghostty's quick terminal: a single drop-down surface that comes out of a screen edge on a
/// global hotkey and goes away again when it loses focus. It is its own window rather than a
/// tab so it can sit above whatever the user is working in, and it is never part of
/// "window-save-state": a drop-down is a scratch surface, not a workspace.
///
/// The window is created on first use and then hidden and shown again, so the shell inside
/// keeps running (and keeps its scrollback) between toggles, which is what Ghostty does.
/// </summary>
internal sealed class QuickTerminal
{
    private static QuickTerminal? _instance;

    private readonly List<GlobalHotkey> _hotkeys = new();

    private Window? _window;
    private Grid? _root;
    private Workspace? _workspace;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _slide;

    private Config _config = new();
    private RectInt32 _target;
    private RectInt32 _offscreen;
    private bool _visible;
    private bool _closing;
    private IntPtr _previousForeground;

    private QuickTerminal()
    {
    }

    /// <summary>
    /// Reads the keybinds and registers the system-wide chords. Called at start-up and after
    /// every config reload; registering again replaces the previous set.
    /// </summary>
    public static void Install(Config config)
    {
        _instance ??= new QuickTerminal();
        _instance.Apply(config);
    }

    /// <summary>The action behind "toggle_quick_terminal" and the global hotkey.</summary>
    public static void Toggle()
    {
        if (_instance is null) Install(Config.Load());
        _instance!.ToggleCore();
    }

    /// <summary>Hides the quick terminal without tearing its shell down (autohide, Esc).</summary>
    public static void Hide()
    {
        if (_instance is { _visible: true } quick) quick.HideCore();
    }

    /// <summary>Closes the quick terminal for good; used by "quit" and app shutdown.</summary>
    public static void CloseIfOpen()
    {
        if (_instance is not { _window: { } window } quick) return;
        quick._closing = true;
        try
        {
            quick._slide?.Stop();
            if (quick._workspace is { } workspace) workspace.CloseWithoutConfirmation();
            else window.Close();
        }
        catch (Exception error)
        {
            Diag.LogException("quick.close", error);
        }
    }

    /// <summary>The theme is process-wide; the drop-down repaints with the other windows.</summary>
    public static void ApplyThemeFrom(Config config) => _instance?._workspace?.ApplyThemeFrom(config);

    /// <summary>A peer window changed a process-wide setting; re-read the file here too.</summary>
    public static void ReloadFromPeer() => _instance?._workspace?.ReloadFromPeer();

    // ---------------- installation ----------------

    private void Apply(Config config)
    {
        _config = config;

        foreach (GlobalHotkey hotkey in _hotkeys) hotkey.Dispose();
        _hotkeys.Clear();

        foreach ((string chord, string action) in new Keybinds(config).Global())
        {
            if (!string.Equals(action, "toggle_quick_terminal", StringComparison.Ordinal))
            {
                Diag.Debug("quick.hotkey", $"'{chord}' is global for '{action}', which is not registered");
                continue;
            }

            GlobalHotkey? hotkey = GlobalHotkey.TryRegister(chord, Toggle, out string error);
            if (hotkey is null)
            {
                Diag.Warn("quick.hotkey", $"{chord}: {error}");
                continue;
            }

            _hotkeys.Add(hotkey);
            Diag.Log("quick.hotkey", chord);
        }

        if (_hotkeys.Count == 0)
            Diag.Log("quick.hotkey", "no global chord is registered; the palette row still works");
    }

    // ---------------- show and hide ----------------

    private void ToggleCore()
    {
        if (_visible || _closing) HideCore();
        else ShowCore();
    }

    private void ShowCore()
    {
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;
        _dispatcher = queue;

        try
        {
            EnsureWindow();
            if (_window is null) return;

            // Re-read the file so an edited position or size takes effect on the next drop-down
            // without a restart, the way Ghostty's quick terminal follows a config reload.
            Config config = Config.Load();
            _config = config;
            Place(config);

            _previousForeground = GetForegroundWindow();
            _visible = true;

            bool animate = config.QuickTerminalAnimationDuration > 0.0 && !SameRect(_offscreen, _target);
            _window.AppWindow.MoveAndResize(animate ? _offscreen : _target);
            _window.Activate();

            if (animate) Slide(_offscreen, _target, config.QuickTerminalAnimationDuration, null);
            else _window.AppWindow.MoveAndResize(_target);

            FocusTerminal();
            Diag.Log("quick", $"show {_target.X},{_target.Y} {_target.Width}x{_target.Height}");
        }
        catch (Exception error)
        {
            _visible = false;
            Diag.LogException("quick.show", error);
        }
    }

    private void HideCore()
    {
        if (_window is null)
        {
            _visible = false;
            return;
        }

        try
        {
            _visible = false;
            RectInt32 from = new(_window.AppWindow.Position.X, _window.AppWindow.Position.Y,
                _window.AppWindow.Size.Width, _window.AppWindow.Size.Height);

            if (_config.QuickTerminalAnimationDuration > 0.0 && !SameRect(from, _offscreen))
            {
                Window window = _window;
                Slide(from, _offscreen, _config.QuickTerminalAnimationDuration, () =>
                {
                    if (ReferenceEquals(_window, window)) window.AppWindow.Hide();
                });
            }
            else
            {
                _window.AppWindow.Hide();
            }

            Diag.Log("quick", "hide");
        }
        catch (Exception error)
        {
            Diag.LogException("quick.hide", error);
        }

        RestoreForeground();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated) return;
        if (!_visible || _closing) return;
        if (!_config.QuickTerminalAutohide) return;
        HideCore();
    }

    /// <summary>
    /// Gives the focus back to whatever the user was looking at. Windows only lets a process
    /// take the foreground when it owns it or when the target answers, so a refusal is fine:
    /// the window still goes away.
    /// </summary>
    private void RestoreForeground()
    {
        IntPtr previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (previous == IntPtr.Zero || !IsWindow(previous)) return;
        if (GetForegroundWindow() == previous) return;
        SetForegroundWindow(previous);
    }

    private void FocusTerminal()
    {
        _workspace?.FocusTerminal();
        // The IME sink only takes focus once the window has been activated, so ask again on
        // the next dispatcher turn.
        _dispatcher?.TryEnqueue(() => _workspace?.FocusTerminal());
    }

    // ---------------- window ----------------

    private void EnsureWindow()
    {
        if (_window is not null) return;
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        if (_dispatcher is null) return;

        var config = Config.Load();
        // The drop-down is one surface: no tab strip, and the rail's placement does not
        // apply to it. Ghostty's quick terminal has no tab bar at all, and a rail dragged to
        // the side would otherwise decide where this window's chrome goes.
        config.TabPosition = "top";
        config.ShowTabBar = false;
        // Compact density: no island inset and no rounded card, so the drop-down is one
        // flush terminal surface instead of a floating card with a gap at the screen edge.
        config.Compact = true;

        _window = new Window();
        _root = new Grid();
        _window.Content = _root;

        _workspace = new Workspace(config, null, null, quickTerminal: true);
        _workspace.Attach(_root, _window);

        try
        {
            _window.AppWindow.Title = "Gui";

            // A drop-down is not a task: keep it out of the taskbar and Alt+Tab, where it
            // would be a second entry for the same app.
            _window.AppWindow.IsShownInSwitchers = false;
            if (_window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
            }
        }
        catch (Exception error)
        {
            Diag.LogException("quick.window", error);
        }

        _window.Activated += OnActivated;
        _window.Closed += (_, _) => OnClosed();
    }

    private void OnClosed()
    {
        _closing = true;
        _slide?.Stop();
        _slide = null;
        _visible = false;
        try
        {
            _workspace?.Close();
        }
        catch (Exception error)
        {
            Diag.LogException("quick.closed", error);
        }
        _workspace = null;
        _root = null;
        _window = null;
        _closing = false;
    }

    /// <summary>Works out the shown and off-screen rectangles for the current config.</summary>
    private void Place(Config config)
    {
        DisplayArea area = config.QuickTerminalScreen == "main"
            ? DisplayArea.Primary
            : DisplayArea.GetFromPoint(CursorPosition(), DisplayAreaFallback.Nearest);

        RectInt32 work = area.WorkArea;

        QuickTerminalRect rect = QuickTerminalGeometry.Calculate(
            new QuickTerminalRect(work.X, work.Y, work.Width, work.Height),
            config.QuickTerminalPosition, config.QuickTerminalSize, WindowScale());
        _target = new RectInt32(rect.X, rect.Y, rect.Width, rect.Height);

        // The slide starts just outside the edge it is anchored to, so the window appears to
        // come out of the screen rather than fade in over it.
        _offscreen = QuickTerminalGeometry.NormalizePosition(config.QuickTerminalPosition) switch
        {
            "bottom" => new RectInt32(_target.X, work.Y + work.Height, _target.Width, _target.Height),
            "left" => new RectInt32(work.X - _target.Width, _target.Y, _target.Width, _target.Height),
            "right" => new RectInt32(work.X + work.Width, _target.Y, _target.Width, _target.Height),
            "center" => _target,
            _ => new RectInt32(_target.X, work.Y - _target.Height, _target.Width, _target.Height),
        };
    }

    private void Slide(RectInt32 from, RectInt32 to, double seconds, Action? done)
    {
        if (_dispatcher is null || _window is null) return;

        _slide?.Stop();
        Window window = _window;
        DispatcherQueueTimer timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(15);
        timer.IsRepeating = true;

        long start = Environment.TickCount64;
        double total = Math.Max(1.0, seconds * 1000.0);

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, (Environment.TickCount64 - start) / total);
            // Ease-out cubic: quick off the edge, settling at the end.
            double eased = 1.0 - Math.Pow(1.0 - t, 3.0);

            if (!ReferenceEquals(_window, window))
            {
                timer.Stop();
                return;
            }

            window.AppWindow.Move(new PointInt32(
                (int)Math.Round(from.X + (to.X - from.X) * eased),
                (int)Math.Round(from.Y + (to.Y - from.Y) * eased)));

            if (t < 1.0) return;
            timer.Stop();
            if (ReferenceEquals(_slide, timer)) _slide = null;
            done?.Invoke();
        };

        _slide = timer;
        timer.Start();
    }

    /// <summary>DPI scale of the monitor the drop-down is about to appear on.</summary>
    private double WindowScale()
    {
        IntPtr hwnd = _window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(_window);
        if (hwnd == IntPtr.Zero) return 1.0;
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private static bool SameRect(RectInt32 a, RectInt32 b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    private static PointInt32 CursorPosition()
    {
        return GetCursorPos(out POINT point) ? new PointInt32(point.X, point.Y) : new PointInt32(0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
}