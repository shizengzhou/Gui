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

    /// <summary>The workspace, so app-wide broadcasts can reach this window.</summary>
    internal Term.Workspace? Workspace => _workspace;

    /// <summary>Cascade step so a new window does not land exactly on the previous one.</summary>
    private const int CascadeStep = 28;
    private static int _windowCount;

    internal MainWindow(string? workingDirectory = null, Term.Workspace.MovedTab? movedTab = null)
    {
        InitializeComponent();

        _inheritedDirectory = workingDirectory;
        _movedTab = movedTab;

        // Size the window before it is shown. Doing this after Loaded (or on Activated)
        // means WinUI first displays the default-sized window and the resize lands a frame
        // later, which reads as the window growing on startup.
        SizeAndCenter();
    }

    private void OnTerminalLoaded(object sender, RoutedEventArgs args)
    {
        if (_workspace is not null) return;

        try
        {
            var config = Term.Config.Load();
            if (_inheritedDirectory is not null) config.WorkingDirectory = _inheritedDirectory;
            _workspace = new Term.Workspace(config, _movedTab);
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

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
