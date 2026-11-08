﻿using System;
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

    private void ShowCommandNotification(TerminalPane pane, int exitCode)
    {
        try
        {
            if (!_notificationsRegistered)
            {
                AppNotificationManager.Default.Register();
                _notificationsRegistered = true;
            }

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
    /// "confirm-close-surface": true asks only while a command is still running, false
    /// never asks, and always asks on every window close.
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        string policy = _config.ConfirmCloseSurface;
        if (policy == "false") return;
        if (policy == "true" && !AnyCommandRunning()) return;

        // The confirmation is asynchronous, so hold the close and ask.
        args.Cancel = true;
        if (_closePromptOpen) return;
        _closePromptOpen = true;
        _ = ConfirmWindowCloseAsync();
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
        try
        {
            XamlRoot? root = _root.XamlRoot;
            if (root is null)
            {
                _allowClose = true;
                _window?.Close();
                return;
            }

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
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            _allowClose = true;
            _window?.Close();
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.confirmClose", error);
            _allowClose = true;
            _window?.Close();
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
    private void UpdateImeSink()
    {
        if (_tabs.Count == 0) return;

        Tab tab = _tabs[_active];
        if (tab.Focused is null)
        {
            _imeSink.Visibility = Visibility.Collapsed;
            return;
        }
        _imeSink.Visibility = Visibility.Visible;

        if (!tab.Focused.CursorOrigin(out double cx, out double cy)) return;

        Point origin = tab.Focused.Visual.TransformToVisual(_root).TransformPoint(new Point(0, 0));
        _imeSink.Margin = new Thickness(origin.X + cx, origin.Y + cy, 0, 0);
        CellMetrics metrics = tab.Focused.CellMetricsDip;
        _imeSink.Width = Math.Max(1, metrics.Width);
        _imeSink.Height = Math.Max(1, metrics.Height);
    }

    /// <summary>
    /// Mirrors the IME composition onto the focused pane so the letters being typed appear at
    /// the cursor. The sink stays invisible; the pane paints the string over its own cells and
    /// throws it away when the composition ends, since the buffer only ever sees the commit.
    /// </summary>
    private void ShowImeComposition()
    {
        TerminalPane? pane = _tabs.Count > 0 ? _tabs[_active].Focused : null;

        // At TextCompositionEnded the sink still holds the raw composition, so the flag -- not
        // the text -- decides whether anything should be drawn.
        string text = _composing && !_imeDiscard ? _imeSink.Text.TrimEnd(' ') : string.Empty;

        if (_imeCompositionPane is not null && !ReferenceEquals(_imeCompositionPane, pane))
            _imeCompositionPane.SetImeComposition(string.Empty);

        _imeCompositionPane = text.Length > 0 ? pane : null;
        pane?.SetImeComposition(text);
    }

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
        return mods;
    }

    private bool OnKey(KeyRoutedEventArgs args)
    {
        // Esc aborts a split drag before any other shortcut or the shell sees the key.
        if (_dragPane is not null && args.Key == VirtualKey.Escape)
        {
            EndPaneDrag(_dragPane, false);
            return true;
        }

        // The palette owns its own keys (filter box, arrow navigation, Enter/Esc).
        if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible)
            return OnPaletteKey(args);

        // The find bar owns typing, Enter and Esc only while focus is in it; after Esc with
        // a query the bar stays visible and the terminal gets the keyboard back.
        if (_tabs.Count > 0 && _tabs[_active].Focused is { SearchHasFocus: true })
            return false;

        _keySeen = true;
        _swallowText = false;

        // The IME consumes the keystrokes that build a composition, so any other key that
        // reaches the window while composing is a shortcut (or a cancel). WinUI ends the
        // composition and finalizes it as the raw pinyin; a terminal drops that, it does not
        // type it. Enter/Esc/Space are the IME's own commit/cancel keys and stay on the
        // normal path, so an IME that commits Latin text with Enter still works.
        if (_composing && args.Key is not (VirtualKey.Enter or VirtualKey.Escape or VirtualKey.Space))
            _imeDiscard = true;

        if (_composing && args.Key is VirtualKey.Escape or VirtualKey.Enter) _composing = false;

        int mods = CurrentMods();
        bool ctrl = (mods & GhosttyNative.MODS_CTRL) != 0;
        bool shift = (mods & GhosttyNative.MODS_SHIFT) != 0;
        bool alt = (mods & GhosttyNative.MODS_ALT) != 0;

        // Windows Terminal's tab hotkeys: Ctrl+Alt+1..9 jump straight to the tab in that
        // slot. Out-of-range digits fall through to the shell.
        if (ctrl && alt && !shift)
        {
            int slot = (int)args.Key - (int)VirtualKey.Number1;
            if (slot >= 0 && slot < 9 && slot < _tabs.Count)
            {
                Activate(slot);
                return true;
            }
        }

        // Windows Terminal font zoom: Ctrl+Plus / Ctrl+Minus resize the focused pane's font
        // by one point; Ctrl+0 restores the configured size.
        if (ctrl && !alt)
        {
            int keyCode = (int)args.Key;
            TerminalPane? pane = _tabs.Count > 0 ? _tabs[_active].Focused : null;
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
        }

        // Windows Terminal's split bindings: Alt+Shift+Plus splits right, Alt+Shift+- splits
        // down. WinUI may report the OEM key or the numpad key depending on the layout.
        if (alt && shift && !ctrl)
        {
            int keyCode = (int)args.Key;
            if (keyCode == 0xBB || args.Key == VirtualKey.Add)
            {
                SplitFocused(false);
                return true;
            }
            if (keyCode == 0xBD || args.Key == VirtualKey.Subtract)
            {
                SplitFocused(true);
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
                case VirtualKey.Enter:
                    TogglePaneZoom();
                    return true;
                case VirtualKey.P:
                    ShowCommandPalette();
                    return true;
                case VirtualKey.F:
                    if (_tabs.Count > 0) _tabs[_active].Focused?.ShowSearch();
                    return true;
                case VirtualKey.C:
                    if (_tabs.Count > 0) _tabs[_active].Focused?.CopySelection();
                    return true;
                case VirtualKey.V:
                    if (_tabs.Count > 0) _tabs[_active].Focused?.PasteFromClipboard();
                    return true;
                case VirtualKey.B:
                    ToggleSidebar();
                    return true;
            }
        }

        if (ctrl && args.Key == VirtualKey.Tab && _tabs.Count > 0)
        {
            int count = _tabs.Count;
            int next = shift ? (_active + count - 1) % count : (_active + 1) % count;
            Activate(next);
            return true;
        }

        // Plain Ctrl+V is swallowed so the focused text box cannot paste into itself; it
        // does not reach the shell either.
        if (ctrl && !shift && !alt && args.Key == VirtualKey.V)
        {
            _swallowText = true;
            return true;
        }

        // A focused tab chip keeps its roving navigation (arrows, Enter, Delete).
        if (IsChipFocused()) return false;

        if (_tabs.Count > 0 && _tabs[_active].Focused is { } focused)
            return focused.KeyDown(args);
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

    private void FlushIme(bool fromKeyboard)
    {
        string text = _imeSink.Text;
        if (text.Length == 0) return;

        _keySeen = false;
        _swallowText = false;

        _imeSink.Text = string.Empty;

        if (!fromKeyboard) return;
        if (_tabs.Count == 0) return;
        _tabs[_active].Focused?.CommitText(text);
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
