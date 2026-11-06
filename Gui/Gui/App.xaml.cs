using System;
using Microsoft.UI.Xaml;

namespace Gui;

/// <summary>
/// Application object. Resolves the config and theme before the first window exists,
/// because WinUI applies the requested theme to chrome, scrollbars and flyouts at
/// creation time.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        Diag.InstallHandlers();

        Term.Config config = Term.Config.Load();
        Term.Theme.Light = config.LightTheme;
        Term.Theme.WindowLight = config.WindowLight;
        Term.Theme.Terminal = config.TerminalTheme;
        RequestedTheme = config.LightTheme ? ApplicationTheme.Light : ApplicationTheme.Dark;

        // The debug tap records the raw byte stream; the environment variable is a shortcut
        // for "just this once" without touching the config file.
        Term.Tap.Enabled = config.DebugTap || Environment.GetEnvironmentVariable("GUI_TAP") is "1";

        // A XAML fail-fast leaves no managed stack, so ask Windows for a full dump.
        if (config.CrashDumps) Term.CrashDump.Enable();

        UnhandledException += (_, args) =>
        {
            Diag.Error("App.UnhandledException", args.Exception?.ToString() ?? args.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
            Diag.Log("App.OnLaunched", "activated");
        }
        catch (Exception error)
        {
            Diag.LogException("App.OnLaunched", error);
            throw;
        }
    }
}
