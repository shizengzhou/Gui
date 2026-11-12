using System;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// The window `gui-cli +new-tab` targets: the last one that was activated. Null until the
    /// first window appears, at which point the newest window is used instead.
    /// </summary>
    private MainWindow? _active;

    /// <summary>The gui-cli control channel, started with the first window.</summary>
    private Term.IpcServer? _ipc;

    /// <summary>The config read at startup; "window-save-state" and language live here.</summary>
    private readonly Term.Config _config;

    /// <summary>
    /// Windows are laid out (and so marked dirty) while a restored pane is still printing its
    /// saved output. Saving during that window would overwrite the session with a blank one,
    /// so the first seconds after launch are ignored. Closing a window always saves.
    /// </summary>
    private const long LaunchGraceMilliseconds = 6000;
    private readonly long _launchTicks = Environment.TickCount64;

    public App()
    {
        InitializeComponent();

        Diag.InstallHandlers();

        Term.Config config = Term.Config.Load();
        _config = config;
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
            // Read the shell's icon while the first window is still being built: the chip of a
            // restored background tab is drawn long before its shell starts, and a cold icon
            // extraction would land after it. Warming the cache here lets the chip take the
            // finished icon straight out of it.
            _ = Term.ShellIcon.GetAsync(Term.TerminalSession.PreferredShellPath(_config.Command));

            // "window-save-state": reopen exactly what was on screen when the last window
            // closed. A missing or unreadable file falls back to one fresh window.
            List<Term.WindowState>? restored =
                _config.WindowSaveState == "never" ? null : Term.WindowStateStore.Load();
            if (restored is { Count: > 0 })
            {
                Diag.Log("window-state", $"launch: restoring {restored.Count} window(s)");
                foreach (Term.WindowState state in restored) ShowWindow(null, null, state);
            }
            else
            {
                ShowWindow(null);
            }
            Microsoft.UI.Dispatching.DispatcherQueue? queue =
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is not null)
                _ipc = Term.IpcServer.Start(Term.IpcProtocol.PipeName(), queue, HandleIpcRequest);

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
        Term.Workspace.MovedTab? movedTab = null, string? command = null)
    {
        if (Current is App app) app.ShowWindow(workingDirectory, movedTab, null, command);
    }

    /// <summary>
    /// Closes every window ("quit"). A copy is walked because each close removes its window
    /// from the list; the last close is what lets the process exit.
    /// </summary>
    internal static void CloseAllWindows()
    {
        if (Current is not App app) return;

        var windows = new Window[app._windows.Count];
        app._windows.CopyTo(windows);

        // "quit" is one action: ask once, in the window that can show the dialog, then
        // close the rest without their own prompts.
        foreach (Window window in windows)
        {
            if (window is MainWindow { Workspace: { } workspace } && workspace.WantsCloseConfirmation())
            {
                _ = workspace.ConfirmQuitAsync(windows);
                return;
            }
        }

        foreach (Window window in windows)
        {
            if (window is MainWindow { Workspace: { } workspace })
                workspace.CloseWithoutConfirmation();
            else
                window.Close();
        }
    }

    /// <summary>
    /// Runs one gui-cli request on the UI thread. The returned string is what the CLI prints
    /// on stderr; null means the request succeeded.
    /// </summary>
    private string? HandleIpcRequest(Term.IpcProtocol.Request request)
    {
        if (!string.Equals(request.Action, "new-tab", StringComparison.Ordinal))
            return $"unknown action '{request.Action}'";

        MainWindow? target = _active ?? _windows.OfType<MainWindow>().LastOrDefault();
        if (target?.Workspace is not { } workspace) return "no window is open";

        workspace.OpenTab(request.WorkingDirectory, request.Command, request.Title);
        target.Activate();
        Diag.Log("ipc",
            $"new-tab cwd={request.WorkingDirectory ?? "-"} command={request.Command ?? "-"} title={request.Title ?? "-"}");
        return null;
    }

    private void ShowWindow(string? workingDirectory, Term.Workspace.MovedTab? movedTab = null,
        Term.WindowState? restore = null, string? command = null)
    {
        var window = new MainWindow(workingDirectory, movedTab, restore, command);
        _windows.Add(window);
        window.Closed += (_, _) => OnWindowClosed(window);
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) _active = window;
        };

        window.Activate();
        MarkSessionDirty();
    }

    /// <summary>
    /// Persists "window-save-state" after a window goes away. Closing the last window is how
    /// the app exits, so that window's own snapshot is what the next launch reopens; closing
    /// one of several windows instead saves the remaining ones.
    /// </summary>
    private void OnWindowClosed(Window window)
    {
        var closed = window as MainWindow;
        _windows.Remove(window);

        // Re-read the config: "window-save-state" may have been changed and reloaded while
        // the app was running (Ctrl+Shift+,), and the startup snapshot would be stale.
        if (Term.Config.Load().WindowSaveState == "never")
        {
            Diag.Log("window-state", "close: skipped, window-save-state = never");
            return;
        }

        var states = new List<Term.WindowState>(_windows.Count + 1);
        foreach (Window other in _windows)
        {
            if (other is MainWindow main && main.CaptureState() is { } state) states.Add(state);
        }

        // The closing window is normally captured from AppWindow.Closing; a close that never
        // raised it (a forced shutdown, or the workspace already torn down) still gets one
        // chance here while the workspace is alive.
        if (states.Count == 0)
        {
            Term.WindowState? last = closed?.Snapshot ?? closed?.CaptureState();
            if (last is not null) states.Add(last);
        }

        // Nothing usable (window never finished loading): keep the previous session file.
        if (states.Count == 0)
        {
            Diag.Log("window-state", "close: no capturable window, previous session kept");
            return;
        }

        Term.WindowStateStore.Save(states);
        Diag.Log("window-state", $"close: saved {states.Count} window(s)");
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sessionTimer;

    /// <summary>
    /// Debounced session save for anything that changes the layout while the app stays open:
    /// opening or closing a tab or split, zooming, moving a tab to another window. Without it a
    /// crash, a forced kill or an unusual exit path would lose the session, because the last
    /// write only happens when a window closes.
    /// </summary>
    internal static void MarkSessionDirty()
    {
        if (Current is App app &&
            Environment.TickCount64 - app._launchTicks >= LaunchGraceMilliseconds)
            app.ScheduleSessionSave();
    }

    private void ScheduleSessionSave()
    {
        if (_sessionTimer is null)
        {
            Microsoft.UI.Dispatching.DispatcherQueue? queue =
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (queue is null) return;

            Microsoft.UI.Dispatching.DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(2);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => SaveSessionNow();
            _sessionTimer = timer;
        }

        _sessionTimer.Stop();
        _sessionTimer.Start();
    }

    private void SaveSessionNow()
    {
        if (_windows.Count == 0) return;
        if (Term.Config.Load().WindowSaveState == "never") return;

        var states = new List<Term.WindowState>(_windows.Count);
        foreach (Window window in _windows)
        {
            if (window is MainWindow main && main.CaptureState() is { } state) states.Add(state);
        }
        if (states.Count == 0) return;

        Term.WindowStateStore.Save(states);
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
