using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Gui;

/// <summary>
/// Thin shell: the workspace builds every control in code under this window's root grid.
/// </summary>
public sealed partial class MainWindow : Window
{
    private const int DefaultWidthDip = 1100;
    private const int DefaultHeightDip = 700;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private Term.Workspace? _workspace;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _geometryTimer;

    /// <summary>Directory a second window starts in, from "window-inherit-working-directory".</summary>
    private readonly string? _inheritedDirectory;

    /// <summary>A tab handed over by "move tab to new window" before the workspace exists.</summary>
    private readonly Term.Workspace.MovedTab? _movedTab;

    /// <summary>Session this window rebuilds instead of starting a fresh shell.</summary>
    private readonly Term.WindowState? _restore;

    /// <summary>Command this window runs instead of the configured shell ("Open Config in New Terminal Window").</summary>
    private readonly string? _commandOverride;

    /// <summary>
    /// State captured while the window is closing, for "window-save-state": AppWindow's
    /// geometry cannot be read once the window is gone, and the app persists this snapshot
    /// after the Closed event.
    /// </summary>
    internal Term.WindowState? Snapshot { get; private set; }

    /// <summary>The workspace, so app-wide broadcasts can reach this window.</summary>
    internal Term.Workspace? Workspace => _workspace;

    /// <summary>Cascade step so a new window does not land exactly on the previous one.</summary>
    private const int CascadeStep = 28;
    private static int _windowCount;

    internal MainWindow(string? workingDirectory = null, Term.Workspace.MovedTab? movedTab = null,
        Term.WindowState? restore = null, string? command = null)
    {
        InitializeComponent();

        _inheritedDirectory = workingDirectory;
        _movedTab = movedTab;
        _restore = restore;
        _commandOverride = command;

        // Size the window before it is shown. Doing this after Loaded (or on Activated)
        // means WinUI first displays the default-sized window and the resize lands a frame
        // later, which reads as the window growing on startup.
        if (_restore is null) SizeAndCenter();
        else RestoreGeometry(_restore);

        AppWindow.Closing += (_, _) => Snapshot = CaptureState();
    }

    private void OnTerminalLoaded(object sender, RoutedEventArgs args)
    {
        if (_workspace is not null) return;

        try
        {
            var config = Term.Config.Load();
            if (_inheritedDirectory is not null) config.WorkingDirectory = _inheritedDirectory;
            if (_commandOverride is not null) config.Command = _commandOverride;
            _workspace = new Term.Workspace(config, _movedTab, _restore);
            _workspace.Attach(RootGrid, this);
            ApplyStartupWindow(config, _workspace);
            Closed += (_, _) => _workspace?.Close();
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.OnTerminalLoaded", error);
        }
    }

    /// <summary>
    /// Startup-only window state from "maximize", "fullscreen", "title", "window-width",
    /// "window-height", "window-position-x" and "window-position-y".
    /// </summary>
    private void ApplyStartupWindow(Term.Config config, Term.Workspace workspace)
    {
        try
        {
            // A restored window keeps the geometry saved when it closed; the startup-only
            // positions and sizes in the config apply to fresh windows only.
            if (_restore is null)
            {
                if (config.Fullscreen)
                {
                    AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                }
                else if (config.Maximize && AppWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.Maximize();
                }
                else
                {
                    ApplyInitialGeometry(config, workspace);
                }
            }

            if (config.WindowTitle.Length > 0) AppWindow.Title = config.WindowTitle;
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.StartupWindow", error);
        }
    }

    /// <summary>
    /// "window-width" / "window-height" are grid cells. Rather than modelling the tab rail
    /// and title strip, the window grows by the difference between the requested and the
    /// current grid, so the configured cell count is reached without knowing the chrome.
    /// </summary>
    private void ApplyInitialGeometry(Term.Config config, Term.Workspace workspace)
    {
        if (config.HasWindowPosition)
            AppWindow.Move(new PointInt32(config.WindowPositionX, config.WindowPositionY));

        if (config.WindowWidth <= 0) return;

        // The first layout pass has not run when Attach returns, so wait for real cell
        // metrics instead of comparing against the pane's 80x24 seed.
        if (!workspace.ActiveGridMetrics(out int columns, out int rows, out double cellWidth, out double cellHeight))
        {
            ScheduleInitialGeometry(config, workspace);
            return;
        }

        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = hwnd == IntPtr.Zero ? 1.0 : GetDpiForWindow(hwnd) / 96.0;

        SizeInt32 current = AppWindow.Size;
        int width = current.Width +
                    (int)Math.Round((config.WindowWidth - columns) * cellWidth * scale);
        int height = current.Height +
                     (int)Math.Round((config.WindowHeight - rows) * cellHeight * scale);

        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = area.WorkArea;
        width = Math.Clamp(width, 400, Math.Max(400, work.Width));
        height = Math.Clamp(height, 240, Math.Max(240, work.Height));

        if (width != current.Width || height != current.Height)
            AppWindow.Resize(new SizeInt32(width, height));
    }

    private void ScheduleInitialGeometry(Term.Config config, Term.Workspace workspace)
    {
        Microsoft.UI.Dispatching.DispatcherQueue? queue = RootGrid.DispatcherQueue;
        if (queue is null) return;

        _geometryTimer?.Stop();
        var timer = queue.CreateTimer();
        _geometryTimer = timer;
        timer.Interval = TimeSpan.FromMilliseconds(40);
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!workspace.ActiveGridMetrics(out _, out _, out _, out _))
            {
                if (attempts >= 25) timer.Stop();
                return;
            }
            timer.Stop();
            ApplyInitialGeometry(config, workspace);
        };
        timer.Start();
    }

    /// <summary>
    /// Sizes the window to the default size in DIPs and centers it on the work area.
    /// AppWindow takes physical pixels, so DIPs are scaled by the monitor DPI first.
    /// </summary>
    private void SizeAndCenter()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = hwnd == IntPtr.Zero ? 1.0 : GetDpiForWindow(hwnd) / 96.0;

            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;

            // The reported work area can extend past the physical screen, which would push
            // the window off the bottom edge, so clip it to the screen bounds.
            int right = Math.Min(work.X + work.Width, GetSystemMetrics(SM_CXSCREEN));
            int bottom = Math.Min(work.Y + work.Height, GetSystemMetrics(SM_CYSCREEN));
            int usableWidth = Math.Max(1, right - work.X);
            int usableHeight = Math.Max(1, bottom - work.Y);

            int width = (int)Math.Min(DefaultWidthDip * scale, usableWidth * 0.95);
            int height = (int)Math.Min(DefaultHeightDip * scale, usableHeight * 0.95);

            // Each new window steps down-right; clamp so the cascade never leaves the work area.
            int cascade = CascadeStep * _windowCount++ * (int)Math.Round(scale);
            int x = work.X + (usableWidth - width) / 2 + cascade;
            int y = work.Y + (usableHeight - height) / 2 + cascade;
            x = Math.Min(x, Math.Max(work.X, right - width));
            y = Math.Min(y, Math.Max(work.Y, bottom - height));

            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(x, y));
            Diag.Log("MainWindow.SizeAndCenter", $"{width}x{height} at scale {scale:0.##}");
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.SizeAndCenter", error);
        }
    }

    /// <summary>"Reset Window Size": leaves maximize/fullscreen and re-centers at the default size.</summary>
    internal void ResetWindowSize()
    {
        try
        {
            if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
                AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            if (AppWindow.Presenter is OverlappedPresenter
                { State: OverlappedPresenterState.Maximized } presenter)
            {
                presenter.Restore();
            }
            SizeAndCenter();
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.ResetWindowSize", error);
        }
    }

    /// <summary>"window-save-state": the window's geometry plus every tab and split.</summary>
    internal Term.WindowState? CaptureState()
    {
        // "never" also skips capturing: encoding a snapshot writes a file, and nothing would
        // reference it afterwards.
        if (_workspace is null || !_workspace.SavesWindowState) return null;

        try
        {
            var tabs = _workspace.CaptureTabs(out int activeTab);
            if (tabs.Count == 0) return null;

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double scale = hwnd == IntPtr.Zero ? 1.0 : GetDpiForWindow(hwnd) / 96.0;
            SizeInt32 size = AppWindow.Size;
            PointInt32 position = AppWindow.Position;
            bool maximized = AppWindow.Presenter is OverlappedPresenter presenter &&
                             presenter.State == OverlappedPresenterState.Maximized;
            bool fullScreen = AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

            return new Term.WindowState
            {
                X = position.X,
                Y = position.Y,
                Width = size.Width,
                Height = size.Height,
                Scale = scale,
                Maximized = maximized,
                FullScreen = fullScreen,
                SavedAt = DateTime.Now,
                ActiveTab = activeTab,
                Tabs = tabs,
            };
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.CaptureState", error);
            return null;
        }
    }

    /// <summary>
    /// Reapplies a saved frame before the window is shown. Size is rescaled when the monitor
    /// DPI changed; the position is clamped into the nearest work area so a window saved on a
    /// monitor that no longer exists still opens on screen.
    /// </summary>
    private void RestoreGeometry(Term.WindowState state)
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double currentScale = hwnd == IntPtr.Zero ? 1.0 : GetDpiForWindow(hwnd) / 96.0;
            double savedScale = state.Scale > 0.01 ? state.Scale : 1.0;

            int width = (int)Math.Round(state.Width * currentScale / savedScale);
            int height = (int)Math.Round(state.Height * currentScale / savedScale);
            if (width <= 0 || height <= 0)
            {
                SizeAndCenter();
                return;
            }

            DisplayArea area = DisplayArea.GetFromPoint(
                new PointInt32(state.X, state.Y), DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;

            width = Math.Clamp(width, 400, Math.Max(400, work.Width));
            height = Math.Clamp(height, 240, Math.Max(240, work.Height));
            int x = Math.Clamp(state.X, work.X, Math.Max(work.X, work.X + work.Width - width));
            int y = Math.Clamp(state.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));

            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(x, y));

            if (state.FullScreen) AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            else if (state.Maximized && AppWindow.Presenter is OverlappedPresenter presenter)
                presenter.Maximize();

            Diag.Log("MainWindow.RestoreGeometry", $"{width}x{height} at {x},{y}");
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.RestoreGeometry", error);
            SizeAndCenter();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
