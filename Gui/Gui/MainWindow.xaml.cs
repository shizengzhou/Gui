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

    public MainWindow()
    {
        InitializeComponent();

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
            _workspace = new Term.Workspace(config);
            _workspace.Attach(RootGrid, this);
            Closed += (_, _) => _workspace?.Close();
        }
        catch (Exception error)
        {
            Diag.LogException("MainWindow.OnTerminalLoaded", error);
        }
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

            AppWindow.Resize(new SizeInt32(width, height));
            AppWindow.Move(new PointInt32(
                work.X + (usableWidth - width) / 2,
                work.Y + (usableHeight - height) / 2));
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
