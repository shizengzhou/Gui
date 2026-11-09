using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;

namespace Gui;

/// <summary>
/// Application object. Resolves the config and theme before the first window exists,
/// because WinUI applies the requested theme to chrome, scrollbars and flyouts at
/// creation time.
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Every open window is referenced here: WinUI drops a Window that nothing holds, and the
    /// Closed handler removes it again so closing the last one lets the process exit.
    /// </summary>
    private readonly List<Window> _windows = new();

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
            ShowWindow(null);
            Diag.Log("App.OnLaunched", "activated");
        }
        catch (Exception error)
        {
            Diag.LogException("App.OnLaunched", error);
            throw;
        }
    }

    /// <summary>
    /// Opens another window, optionally started in <paramref name="workingDirectory"/>
    /// (Ctrl+Shift+N, the command palette and "window-inherit-working-directory"), or
    /// adopting a tab that "move tab to new window" handed over.
    /// </summary>
    internal static void OpenWindow(string? workingDirectory = null,
        Term.Workspace.MovedTab? movedTab = null)
    {
        if (Current is App app) app.ShowWindow(workingDirectory, movedTab);
    }

    private void ShowWindow(string? workingDirectory, Term.Workspace.MovedTab? movedTab = null)
    {
        var window = new MainWindow(workingDirectory, movedTab);
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Activate();
    }

    /// <summary>
    /// The theme lives in process-wide statics, so the window that commits a change has to
    /// repaint the others: they copy the theme fields and refresh their own brushes.
    /// </summary>
    internal static void ApplyThemeToOtherWindows(Term.Workspace source, Term.Config config)
    {
        if (Current is not App app) return;
        foreach (Window window in app._windows)
        {
            if (window is MainWindow main && !ReferenceEquals(main.Workspace, source))
                main.Workspace?.ApplyThemeFrom(config);
        }
    }

    /// <summary>
    /// Re-reads the config in the other windows after one of them persisted a setting that
    /// belongs to the whole process (tab rail placement, compact density, theme, ...).
    /// </summary>
    internal static void ReloadOtherWindows(Term.Workspace source)
    {
        if (Current is not App app) return;
        foreach (Window window in app._windows)
        {
            if (window is MainWindow main && !ReferenceEquals(main.Workspace, source))
                main.Workspace?.ReloadFromPeer();
        }
    }
}
