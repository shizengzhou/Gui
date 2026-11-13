using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Gui.Term;

public sealed partial class Workspace
{
    /// <summary>True while "Toggle Float on Top" keeps the window above the others.</summary>
    private bool _floatOnTop;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    // ---------------- window ----------------

    private void OnHeaderPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true;
        ulong now = GetTickCount64();
        if (now - _lastHeaderPress < GetDoubleClickTime())
        {
            _lastHeaderPress = 0;
            ToggleMaximize();
            return;
        }
        _lastHeaderPress = now;
        // The side layouts lift the island over the header strip, which stays click-through
        // there; the drag then starts on the sidebar instead.
        (sender as UIElement)?.CapturePointer(args.Pointer);
        BeginDrag();
    }

    /// <summary>
    /// The island lives in the client area, so moving the window is done by hand. The
    /// cursor is tracked in screen pixels rather than element-relative DIPs: the island
    /// moves with the window, so a relative position would feed the window's own movement
    /// back into the delta.
    /// </summary>
    private void BeginDrag()
    {
        if (_window is null) return;
        try
        {
            GetCursorPos(out POINT cursor);
            _dragCursor = new PointInt32(cursor.X, cursor.Y);
            _dragOrigin = _window.AppWindow.Position;
            _dragging = true;
        }
        catch
        {
            _dragging = false;
        }
    }

    private void DragTo()
    {
        if (!_dragging || _window is null) return;
        try
        {
            GetCursorPos(out POINT cursor);
            _window.AppWindow.Move(new PointInt32(
                _dragOrigin.X + (cursor.X - _dragCursor.X),
                _dragOrigin.Y + (cursor.Y - _dragCursor.Y)));
        }
        catch
        {
            _dragging = false;
        }
    }

    private void EndDrag() => _dragging = false;

    private void ToggleMaximize()
    {
        if (_window?.AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
        UpdateMaximizeIcon();
    }

    /// <summary>
    /// Windows uses a stacked-squares glyph for "restore" and a single square for
    /// "maximize"; the caption button must follow the window state.
    /// </summary>
    private void UpdateMaximizeIcon()
    {
        if (_maximizeIcon is null || _window is null) return;
        bool maximized = _window.AppWindow.Presenter is OverlappedPresenter presenter &&
                         presenter.State == OverlappedPresenterState.Maximized;
        _maximizeIcon.Glyph = ((char)(maximized ? 0xE923 : 0xE922)).ToString();
        UpdateCaptionTips();
    }

    /// <summary>
    /// window-theme drives the native frame too: the immersive dark-mode attribute makes
    /// DWM paint the border, shadow and resize edges light or dark.
    /// </summary>
    private void ApplyWindowTheme()
    {
        if (_appWindow is null) return;
        IntPtr hwnd = Win32Interop.GetWindowFromWindowId(_appWindow.Id);
        if (hwnd == IntPtr.Zero) return;
        int dark = Theme.WindowLight ? 0 : 1;
        DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int));
    }

    private void MinimizeWindow()
    {
        if (_window?.AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    /// <summary>
    /// "Toggle Float on Top": keeps this window above every other window until it is toggled
    /// back.
    /// </summary>
    private void ToggleFloatOnTop()
    {
        if (_appWindow is null) return;
        IntPtr hwnd = Win32Interop.GetWindowFromWindowId(_appWindow.Id);
        if (hwnd == IntPtr.Zero) return;

        _floatOnTop = !_floatOnTop;
        SetWindowPos(hwnd, _floatOnTop ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>"Reset Window Size": back to the default size, centered on the work area.</summary>
    private void ResetWindowSize()
    {
        if (_window is MainWindow main) main.ResetWindowSize();
    }

    /// <summary>"Show On-Screen Keyboard": starts the Windows touch keyboard.</summary>
    private void ShowOnScreenKeyboard()
    {
        try
        {
            Process.Start(new ProcessStartInfo("osk.exe") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.OnScreenKeyboard", error);
        }
    }

    /// <summary>Ghostty's "close_window": closes this window (still honours confirm-close-surface).</summary>
    private void CloseWindow() => RequestClose();

    /// <summary>
    /// Closes the window after "confirm-close-surface" has been honoured. The custom caption
    /// button and the "close_window" action call Window.Close() programmatically, and
    /// AppWindow.Closing only fires for a system affordance (Alt+F4, the taskbar menu), so
    /// the policy has to be applied here as well.
    /// </summary>
    private void RequestClose()
    {
        if (!_allowClose)
        {
            if (NeedsCloseConfirm(AnyCommandRunning()))
            {
                _ = ConfirmWindowCloseAsync();
                return;
            }
            _allowClose = true;
        }
        _window?.Close();
    }

    /// <summary>True when a window close would have to ask first.</summary>
    internal bool WantsCloseConfirmation()
    {
        return !_allowClose && NeedsCloseConfirm(AnyCommandRunning());
    }

    /// <summary>Closes without asking; used by "quit" after its one shared prompt.</summary>
    internal void CloseWithoutConfirmation()
    {
        _allowClose = true;
        _window?.Close();
    }

    /// <summary>Ghostty's "quit": closes every window, which is how the app exits.</summary>
    private void QuitApplication() => App.CloseAllWindows();

    /// <summary>Ghostty's "toggle_fullscreen".</summary>
    private void ToggleFullscreen()
    {
        if (_appWindow is null) return;
        _appWindow.SetPresenter(_appWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen
            ? AppWindowPresenterKind.Default
            : AppWindowPresenterKind.FullScreen);
    }

    /// <summary>
    /// "bell-features = attention": asks the taskbar to flash while the window is in the
    /// background. Windows stops the flash as soon as the window is activated.
    /// </summary>
    private void FlashWindow()
    {
        if (_hwnd == IntPtr.Zero || GetForegroundWindow() == _hwnd) return;
        var info = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = _hwnd,
            dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount = 1,
        };
        FlashWindowEx(ref info);
    }

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    // ---------------- command notifications and close confirmation ----------------

    /// <summary>
    /// "notify-on-command-finish": the shell integration reports every finished command;
    /// notify when it ran for at least "notify-on-command-finish-after" and the pane is
    /// unfocused (or the mode is always).
    /// </summary>
    private void OnPaneCommandFinished(TerminalPane pane, int exitCode, TimeSpan duration)
    {
        string mode = _config.NotifyOnCommandFinish;
        if (mode == "never") return;
        if (duration < _config.NotifyOnCommandFinishAfter) return;
        if (mode == "unfocused" && IsActivePane(pane)) return;

        if (_config.NotifyOnCommandFinishBell) pane.RingBell();
        if (_config.NotifyOnCommandFinishToast) ShowCommandNotification(pane, exitCode);
    }

    /// <summary>True when this pane is focused and its window is in the foreground.</summary>
    private bool IsActivePane(TerminalPane pane)
    {
        if (_hwnd == IntPtr.Zero || GetForegroundWindow() != _hwnd) return false;
        if (_active < 0 || _active >= _tabs.Count) return false;
        return ReferenceEquals(_tabs[_active].Focused, pane);
    }

    private static bool _notificationsRegistered;

    private static void EnsureNotificationsRegistered()
    {
        if (_notificationsRegistered) return;
        AppNotificationManager.Default.Register();
        _notificationsRegistered = true;
    }

    private void ShowCommandNotification(TerminalPane pane, int exitCode)
    {
        try
        {
            EnsureNotificationsRegistered();

            var notification = new AppNotificationBuilder()
                .AddText(Strings.CommandFinishedTitle)
                .AddText(Strings.CommandFinishedNotification(pane.Title, exitCode))
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
        }
        catch (Exception error)
        {
            // Unpackaged hosts can refuse toasts; the taskbar flash is the fallback.
            Diag.LogException("Workspace.notify", error);
            FlashWindow();
        }
    }

    /// <summary>
    /// "desktop-notifications": the running program asked for a desktop notification with
    /// OSC 9 or OSC 777. OSC 9 carries no title, so fall back to the pane's title.
    /// </summary>
    private void OnPaneDesktopNotification(TerminalPane pane, string title, string body)
    {
        try
        {
            EnsureNotificationsRegistered();

            string heading = title.Length > 0 ? title : pane.Title.Length > 0 ? pane.Title : "Gui";
            var builder = new AppNotificationBuilder().AddText(heading);
            if (body.Length > 0) builder.AddText(body);
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.notify", error);
            FlashWindow();
        }
    }

    /// <summary>
    /// "confirm-close-surface": true asks only while a command is still running, false
    /// never asks, and always asks on every window close.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (!NeedsCloseConfirm(AnyCommandRunning())) return;

        // The confirmation is asynchronous, so hold the close and ask.
        args.Cancel = true;
        _ = ConfirmWindowCloseAsync();
    }

    /// <summary>
    /// "confirm-close-surface": false never asks, true asks only while a command is still
    /// running, and always asks on every close of a surface or window.
    /// </summary>
    private bool NeedsCloseConfirm(bool commandRunning)
    {
        return _config.ConfirmCloseSurface switch
        {
            "false" => false,
            "always" => true,
            _ => commandRunning,
        };
    }

    private bool AnyCommandRunning()
    {
        foreach (Tab tab in _tabs)
        {
            bool running = false;
            ForEachPane(tab, pane =>
            {
                if (pane.CommandRunning) running = true;
            });
            if (running) return true;
        }
        return false;
    }

    private async Task ConfirmWindowCloseAsync()
    {
        if (!await AskCloseWindowAsync()) return;

        _allowClose = true;
        _window?.Close();
    }

    /// <summary>
    /// "quit" is one action: the question is asked once, then every window closes without
    /// asking again. Quitting must not leave some windows alive behind the dialog, and
    /// WinUI refuses a second ContentDialog anyway.
    /// </summary>
    internal async Task ConfirmQuitAsync(IReadOnlyList<Window> windows)
    {
        if (!await AskCloseWindowAsync()) return;

        foreach (Window window in windows)
        {
            if (window is MainWindow { Workspace: { } workspace })
                workspace._allowClose = true;
            window.Close();
        }
    }

    /// <summary>Shows the window-close question; true when the close may go ahead.</summary>
    private async Task<bool> AskCloseWindowAsync()
    {
        // One dialog at a time: a second close request while the prompt is open is dropped
        // instead of closing without asking.
        if (_closePromptOpen) return false;
        _closePromptOpen = true;
        try
        {
            XamlRoot? root = _root.XamlRoot;
            if (root is null) return true;

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = Strings.CloseWarningTitle,
                Content = new TextBlock
                {
                    Text = Strings.CloseWarningBody,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                PrimaryButtonText = Strings.CloseWindow,
                CloseButtonText = Strings.Cancel,
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            // Never fall back to closing: that would stop the running command without an
            // answer.
            Diag.LogException("Workspace.confirmClose", error);
            return false;
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    /// <summary>
    /// Asks before a tab or split with a running command goes away. The prompt shares the
    /// one-dialog guard with the window close, so it never stacks a second ContentDialog.
    /// </summary>
    private async Task<bool> ConfirmCloseTerminalAsync()
    {
        if (_closePromptOpen) return false;

        XamlRoot? root = _root.XamlRoot;
        if (root is null) return true;

        _closePromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = Strings.CloseTerminalWarningTitle,
                Content = new TextBlock
                {
                    Text = Strings.CloseTerminalWarningBody,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                },
                PrimaryButtonText = Strings.CloseTerminal,
                CloseButtonText = Strings.Cancel,
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.confirmCloseTerminal", error);
            return true;
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    // ---------------- windows ----------------

    /// <summary>
    /// Opens another window (Ctrl+Shift+N / the palette). It starts in the focused shell's
    /// directory when "window-inherit-working-directory" allows it, like Ghostty.
    /// </summary>
    private void OpenNewWindow()
    {
        string? directory = _config.WindowInheritWorkingDirectory && _tabs.Count > 0
            ? _tabs[_active].Focused?.WorkingDirectory()
            : null;
        App.OpenWindow(directory);
    }

    // ---------------- IME ----------------

    /// <summary>
    /// The sink is the element XAML routes keys and text to, so it has to stay over the
    /// focused cursor cell; it is also what a press there hits.
    /// </summary>
    private void UpdateInputAnchor()
    {
        if (_tabs.Count == 0) return;

        Tab tab = _tabs[_active];
        if (tab.Focused is null)
        {
            if (_imeSink.Visibility != Visibility.Collapsed) _imeSink.Visibility = Visibility.Collapsed;
            _imeAnchorPane = null;
            return;
        }
        if (_imeSink.Visibility != Visibility.Visible)
        {
            _imeSink.Visibility = Visibility.Visible;
            _imeAnchorDirty = true;
        }

        TerminalPane pane = tab.Focused;
        CellMetrics metrics = pane.CellMetricsDip;
        double width = Math.Max(1, metrics.Width);
        double height = Math.Max(1, metrics.Height);
        if (!pane.CursorOrigin(out double cx, out double cy)) return;

        bool sameCell = ReferenceEquals(pane, _imeAnchorPane) && cx == _imeCursorX &&
            cy == _imeCursorY && width == _imeSinkW && height == _imeSinkH;
        if (sameCell && !_imeAnchorDirty) return;

        Point origin = pane.Visual.TransformToVisual(_root).TransformPoint(new Point(0, 0));
        double x = origin.X + cx;
        double y = origin.Y + cy;
        if (x != _imeSinkX || y != _imeSinkY) _imeSink.Margin = new Thickness(x, y, 0, 0);
        if (width != _imeSinkW) _imeSink.Width = width;
        if (height != _imeSinkH) _imeSink.Height = height;

        _imeAnchorPane = pane;
        _imeCursorX = cx;
        _imeCursorY = cy;
        _imeSinkX = x;
        _imeSinkY = y;
        _imeSinkW = width;
        _imeSinkH = height;
        _imeAnchorDirty = false;
    }

    /// <summary>
    /// Focuses the invisible IME sink. WinUI only feeds the text services while a text
    /// control has the focus, so this is what makes composition possible at all.
    /// </summary>
    private void FocusKeyboardAnchor()
    {
        _imeSink.Focus(FocusState.Programmatic);
    }

    /// <summary>True when the terminal, not a real text control, owns the keyboard.</summary>
    private bool IsTerminalKeyboardFocus()
    {
        XamlRoot? root = _root.XamlRoot;
        if (root is null) return false;
        return ReferenceEquals(FocusManager.GetFocusedElement(root), _imeSink);
    }

    private void CommitImeText(string text)
    {
        if (text.Length == 0 || _tabs.Count == 0) return;
        Diag.Guard("ws.ime.commit", () => _tabs[_active].Focused?.CommitText(text));
    }

    /// <summary>
    /// Hands the sink's text to the shell and empties it, so the box never accumulates state.
    /// A discarded text is the raw pinyin of a cancelled composition and never reaches the
    /// shell.
    /// </summary>
    private void FlushIme(bool send)
    {
        string text = _imeSink.Text;
        if (text.Length > 0) _imeSink.Text = string.Empty;
        ShowImeComposition();

        if (!send || text.Length == 0) return;
        CommitImeText(text);
    }

    /// <summary>Mirrors the sink's in-flight composition onto the focused pane.</summary>
    private void ShowImeComposition() => ShowImeComposition(_imeSink?.Text ?? string.Empty);

    /// <summary>Throws away a composition in flight instead of committing its raw pinyin.</summary>
    private void CancelImeComposition()
    {
        if (!_composing) return;
        _imeDiscard = true;
        ShowImeComposition();
    }

    /// <summary>
    /// Mirrors the IME composition onto the focused pane so the letters being typed appear at
    /// the cursor. The pane paints the string over its own cells and clears it when the
    /// composition ends; the shell only ever sees the committed result.
    /// </summary>
    private void ShowImeComposition(string text)
    {
        TerminalPane? pane = _tabs.Count > 0 ? _tabs[_active].Focused : null;
        text = text.TrimEnd(' ');

        if (_imeCompositionPane is not null && !ReferenceEquals(_imeCompositionPane, pane))
            _imeCompositionPane.SetImeComposition(string.Empty);

        _imeCompositionPane = text.Length > 0 ? pane : null;
        pane?.SetImeComposition(text);    }

    private static bool IsDown(VirtualKey key)
    {
        CoreVirtualKeyStates state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static int CurrentMods()
    {
        int mods = 0;
        if (IsDown(VirtualKey.Shift)) mods |= GhosttyNative.MODS_SHIFT;
        if (IsDown(VirtualKey.Control)) mods |= GhosttyNative.MODS_CTRL;
        if (IsDown(VirtualKey.Menu)) mods |= GhosttyNative.MODS_ALT;
        // Ghostty calls the Win key "super"; a few default bindings use it.
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
            mods |= GhosttyNative.MODS_SUPER;
        return mods;
    }

    private static bool IsModifierKey(VirtualKey key) =>
        key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or
            VirtualKey.LeftWindows or VirtualKey.RightWindows;

    /// <summary>
    /// Ghostty refreshes link hover when the modifiers change, even if the pointer never
    /// moves, so a Ctrl press or release takes effect over the cell already under the cursor.
    /// </summary>
    private void RefreshLinkHovers()
    {
        foreach (Tab tab in _tabs)
        {
            if (tab.Root is not null) VisitPanes(tab.Root, pane => pane.RefreshLinkHover());
        }
    }

    private bool OnKey(KeyRoutedEventArgs args)
    {
        // Modifier changes affect the link under a stationary pointer.
        if (IsModifierKey(args.Key)) RefreshLinkHovers();

        // Esc aborts a split drag before any other shortcut or the shell sees the key.
        if (_dragPane is not null && args.Key == VirtualKey.Escape)
        {
            EndPaneDrag(_dragPane, false);
            return true;
        }

        // The palette owns its own keys (filter box, arrow navigation, Enter/Esc).
        if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible)
            return OnPaletteKey(args);

        // The settings page closes on Esc; every other key goes to the focused setting instead
        // of the shell or a window shortcut.
        if (_settingsLayer is not null && _settingsLayer.Visibility == Visibility.Visible)
            return OnSettingsKey(args);

        // The inline tab rename editor owns the keyboard. Enter and Esc are decided here,
        // before the text box swallows them; arrows, Home/End and Delete stay with the box,
        // which is why every other key is passed through untouched.
        if (_renamingTab is { } renaming)
        {
            if (args.Key == VirtualKey.Enter)
            {
                EndRename(renaming, true);
                return true;
            }
            if (args.Key == VirtualKey.Escape)
            {
                EndRename(renaming, false);
                return true;
            }
            return false;
        }

        // The find bar owns typing, Enter and Esc only while focus is in it; after Esc with
        // a query the bar stays visible and the terminal gets the keyboard back.
        if (_tabs.Count > 0 && _tabs[_active].Focused is { SearchHasFocus: true })
            return false;

        // While a composition is open the IME owns the keyboard: the keys that build it, and
        // the keys that commit or cancel it, belong to the input method. Handling one of them
        // here would run a shortcut the IME is about to re-inject once the composition ends.
        if (_composing) return true;

        int mods = CurrentMods();
        bool ctrl = (mods & GhosttyNative.MODS_CTRL) != 0;
        bool shift = (mods & GhosttyNative.MODS_SHIFT) != 0;
        bool alt = (mods & GhosttyNative.MODS_ALT) != 0;

        // Ctrl+K starts the "Open Directory" chord. The second key decides: Ctrl+O opens the
        // picker, Escape cancels silently, and anything else goes on to the normal handlers
        // (only the prefix itself is consumed).
        if (_openDirectoryChord != 0)
        {
            bool armed = Environment.TickCount64 - _openDirectoryChord < 2000;
            _openDirectoryChord = 0;
            if (armed)
            {
                if (ctrl && !shift && !alt && args.Key == VirtualKey.O)
                {
                    _ = OpenDirectoryAsync();
                    return true;
                }
                if (args.Key == VirtualKey.Escape) return true;
            }
        }

        // A "keybind" line wins over the built-in chord, and an explicit unbind hands the
        // key back to the shell instead of running the default action.
        KeybindMatch match = _keybinds.Resolve(mods, args.Key, out string? boundAction);
        if (match == KeybindMatch.Action)
        {
            RunKeybindAction(boundAction);
            return true;
        }
        if (match == KeybindMatch.Unbound) return false;

        // "keybind = clear" drops the built-in chords; only configured lines remain.
        if (!_keybinds.Cleared && DefaultShortcut(mods, args))
            return true;

        // Plain Ctrl+V is swallowed so it cannot paste into any focused text box; it does
        // not reach the shell either.
        if (ctrl && !shift && !alt && args.Key == VirtualKey.V) return true;

        // A focused tab chip keeps its roving navigation (arrows, Enter, Delete).
        if (IsChipFocused()) return false;

        if (_tabs.Count > 0 && _tabs[_active].Focused is { } focused)
            return focused.KeyDown(args);
        return false;
    }

    /// <summary>
    /// The chords this build ships with. They are checked after the configured bindings, so
    /// a user line can both add a chord and remove one (with "trigger=").
    /// </summary>
    private bool DefaultShortcut(int mods, KeyRoutedEventArgs args)
    {
        bool ctrl = (mods & GhosttyNative.MODS_CTRL) != 0;
        bool shift = (mods & GhosttyNative.MODS_SHIFT) != 0;
        bool alt = (mods & GhosttyNative.MODS_ALT) != 0;
        bool super = (mods & GhosttyNative.MODS_SUPER) != 0;

        // Ghostty's Alt+1..8 jump straight to the tab in that slot; Alt+9 returns to the
        // previous tab. An out-of-range slot is still consumed (Ghostty binds the chord
        // regardless of how many tabs exist).
        if (alt && !ctrl && !shift)
        {
            int slot = (int)args.Key - (int)VirtualKey.Number1;
            if (slot >= 0 && slot < 8)
            {
                if (slot < _tabs.Count) Activate(slot);
                return true;
            }
            if (args.Key == VirtualKey.Number9)
            {
                ActivateLastTab();
                return true;
            }
        }

        // Ghostty's Ctrl+Alt+arrows move the focus between splits.
        if (ctrl && alt && !shift)
        {
            switch (args.Key)
            {
                case VirtualKey.Left:
                    FocusPaneTowards(-1, 0);
                    return true;
                case VirtualKey.Right:
                    FocusPaneTowards(1, 0);
                    return true;
                case VirtualKey.Up:
                    FocusPaneTowards(0, -1);
                    return true;
                case VirtualKey.Down:
                    FocusPaneTowards(0, 1);
                    return true;
            }
        }

        // Ghostty's viewport scrolling: Shift+Home/End/PageUp/PageDown. Shift+Insert pastes
        // the current selection on non-macOS.
        if (shift && !ctrl && !alt)
        {
            TerminalPane? scrollPane = ActivePane();
            switch (args.Key)
            {
                case VirtualKey.Home:
                    scrollPane?.ScrollToTop();
                    return true;
                case VirtualKey.End:
                    scrollPane?.ScrollToBottom();
                    return true;
                case VirtualKey.PageUp:
                    scrollPane?.ScrollPage(true);
                    return true;
                case VirtualKey.PageDown:
                    scrollPane?.ScrollPage(false);
                    return true;
                case VirtualKey.Left:
                    scrollPane?.AdjustSelection(-1, 0);
                    return true;
                case VirtualKey.Right:
                    scrollPane?.AdjustSelection(1, 0);
                    return true;
                case VirtualKey.Up:
                    scrollPane?.AdjustSelection(0, -1);
                    return true;
                case VirtualKey.Down:
                    scrollPane?.AdjustSelection(0, 1);
                    return true;
                case VirtualKey.Insert:
                    scrollPane?.PasteSelection();
                    return true;
            }
        }

        // Windows Terminal font zoom: Ctrl+Plus / Ctrl+Minus resize the focused pane's font
        // by one point; Ctrl+0 restores the configured size. Ghostty's Ctrl+Enter toggles
        // fullscreen and Ctrl+PageUp/PageDown switch tabs here too.
        if (ctrl && !alt)
        {
            int keyCode = (int)args.Key;
            TerminalPane? pane = ActivePane();
            if (keyCode == 0xBB || args.Key == VirtualKey.Add)
            {
                pane?.AdjustFontSize(1.0f);
                return true;
            }
            if (keyCode == 0xBD || args.Key == VirtualKey.Subtract)
            {
                pane?.AdjustFontSize(-1.0f);
                return true;
            }
            if (args.Key == VirtualKey.Number0 || keyCode == 0x60)
            {
                pane?.ResetFontSize();
                return true;
            }

            if (!shift)
            {
                switch (args.Key)
                {
                    case VirtualKey.Enter:
                        ToggleFullscreen();
                        return true;
                    case VirtualKey.Insert:
                        pane?.CopyToClipboard();
                        return true;
                    case VirtualKey.K:
                        // Prefix of the Ctrl+K Ctrl+O chord that opens a directory.
                        _openDirectoryChord = Environment.TickCount64;
                        return true;
                    case VirtualKey.PageUp:
                        CycleTab(-1);
                        return true;
                    case VirtualKey.PageDown:
                        CycleTab(1);
                        return true;
                }
            }
        }

        // Ghostty's open_config: Ctrl+, opens the settings page, which offers the raw config
        // file from its sidebar. Ctrl+Shift+, reloads the file (handled below).
        if (ctrl && !alt && !shift && (int)args.Key == 0xBC)
        {
            ShowSettings();
            return true;
        }

        // Ghostty's write_screen_file:open.
        if (ctrl && alt && shift && args.Key == VirtualKey.J)
        {
            WriteTerminalFile(false, string.Empty, "open");
            return true;
        }

        // Ghostty's super bindings: resize splits with Win+Ctrl+Shift+arrows and
        // write_screen_file:copy with Ctrl+Shift+Win+J. The Win key combinations may be
        // claimed by the shell before they reach the app.
        if (super && ctrl && shift && !alt)
        {
            switch (args.Key)
            {
                case VirtualKey.Left:
                    ResizeSplit(-1, 0);
                    return true;
                case VirtualKey.Right:
                    ResizeSplit(1, 0);
                    return true;
                case VirtualKey.Up:
                    ResizeSplit(0, -1);
                    return true;
                case VirtualKey.Down:
                    ResizeSplit(0, 1);
                    return true;
                case VirtualKey.J:
                    WriteTerminalFile(false, string.Empty, "copy");
                    return true;
            }
        }

        // Ghostty's Ctrl+Super+[ / ] cycle the split focus.
        if (ctrl && super && !alt && !shift)
        {
            int keyCode = (int)args.Key;
            if (keyCode == 0xDB)
            {
                CyclePane(-1);
                return true;
            }
            if (keyCode == 0xDD)
            {
                CyclePane(1);
                return true;
            }
        }

        // Windows Terminal's split bindings: Alt+Shift+Plus splits right, Alt+Shift+- splits
        // down. WinUI may report the OEM key or the numpad key depending on the layout.
        if (alt && shift && !ctrl)
        {
            int keyCode = (int)args.Key;
            if (keyCode == 0xBB || args.Key == VirtualKey.Add)
            {
                SplitFocused(SplitDirection.Right);
                return true;
            }
            if (keyCode == 0xBD || args.Key == VirtualKey.Subtract)
            {
                SplitFocused(SplitDirection.Down);
                return true;
            }
        }

        if (ctrl && shift && !alt)
        {
            // VK_OEM_COMMA has no VirtualKey member; WinUI reports the raw OEM code.
            if ((int)args.Key == 0xBC)
            {
                ReloadConfig();
                return true;
            }

            switch (args.Key)
            {
                case VirtualKey.T:
                    Activate(IndexOf(NewTab()));
                    return true;
                case VirtualKey.N:
                    OpenNewWindow();
                    return true;
                case VirtualKey.W:
                    CloseFocusedPane();
                    return true;
                case VirtualKey.Q:
                    QuitApplication();
                    return true;
                case VirtualKey.O:
                    SplitFocused(SplitDirection.Right);
                    return true;
                case VirtualKey.E:
                    SplitFocused(SplitDirection.Down);
                    return true;
                case VirtualKey.A:
                    ActivePane()?.SelectAll();
                    return true;
                case VirtualKey.J:
                    WriteTerminalFile(false, string.Empty, "paste");
                    return true;
                case VirtualKey.Enter:
                    TogglePaneZoom();
                    return true;
                case VirtualKey.P:
                    ShowCommandPalette();
                    return true;
                case VirtualKey.F:
                    ActivePane()?.ShowSearch();
                    return true;
                case VirtualKey.C:
                    ActivePane()?.CopySelection();
                    return true;
                case VirtualKey.V:
                    ActivePane()?.PasteFromClipboard();
                    return true;
                case VirtualKey.B:
                    ToggleSidebar();
                    return true;
                case VirtualKey.Left:
                    CycleTab(-1);
                    return true;
                case VirtualKey.Right:
                    CycleTab(1);
                    return true;
                case VirtualKey.PageUp:
                    MoveTab(_active, _active - 1);
                    return true;
                case VirtualKey.PageDown:
                    MoveTab(_active, _active + 1);
                    return true;
                case VirtualKey.Up:
                    ActivePane()?.JumpToPrompt(-1);
                    return true;
                case VirtualKey.Down:
                    ActivePane()?.JumpToPrompt(1);
                    return true;
            }
        }

        if (ctrl && args.Key == VirtualKey.Tab && _tabs.Count > 0)
        {
            CycleTab(shift ? -1 : 1);
            return true;
        }

        return false;
    }

    private bool IsChipFocused()
    {
        if (_root.XamlRoot is null) return false;
        object? focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        if (focused is not ContentControl control) return false;
        foreach (Tab tab in _tabs)
        {
            if (ReferenceEquals(tab.ChipFocus, control)) return true;
        }
        return false;
    }

    // ---------------- shutdown ----------------

    public void Close()
    {
        InputSite.Release();
        _imeTimer?.Stop();
        _imeTimer = null;

        // Drop pending close animations before their panes go away.
        foreach (ExitAnimation entry in _exitAnimations) entry.Timer?.Stop();
        _exitAnimations.Clear();
        _closingPanes.Clear();
        _chipSprings.Clear();
        _chipSpringTimer?.Stop();
        _chipSpringTimer = null;

        foreach (Tab tab in _tabs) ForEachPane(tab, pane => pane.Close());
        _tabs.Clear();
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
