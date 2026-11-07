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
        LogConfigDiagnostics(config);
        Strings.Use(config.Language);
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

    /// <summary>
    /// Every Ghostty option is accepted, so a config written for Ghostty loads without
    /// errors; unknown keys, invalid values and options this build cannot honor are
    /// recorded once at startup instead of failing silently.
    /// </summary>
    private static void LogConfigDiagnostics(Term.Config config)
    {
        foreach (Term.ConfigDiagnostic diagnostic in config.Diagnostics)
        {
            string where = $"config:{diagnostic.Line} {diagnostic.Key}";
            if (diagnostic.Severity == Term.ConfigSeverity.Warning)
                Diag.Warn(where, diagnostic.Message);
            else
                Diag.Debug(where, diagnostic.Message);
        }
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
