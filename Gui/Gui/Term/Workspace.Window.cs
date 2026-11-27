using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
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
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ---------------- window · non-client chrome ----------------

    private const uint NonClientSubclass = 2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCMOUSEMOVE = 0x00A0;
    private const int WM_NCDESTROY = 0x0082;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_MOVE = 0x0003;
    private const int WM_SIZE = 0x0005;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const uint TME_LEAVE = 0x00000002;
    private const uint TME_NONCLIENT = 0x00000010;
    private const int HtCaption = 2;
    private const int DragThreshold = 4;
    private const int WmSysCommand = 0x0112;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int VkLeftButton = 0x01;

    /// <summary>The window's caption and button hit areas, registered with WinUI's input source.</summary>
    private InputNonClientPointerSource? _nonClient;

    /// <summary>Kept in a field: the subclass chain holds this delegate for the window's lifetime.</summary>
    private SubclassProc? _nonClientProc;

    private IntPtr[] _dragBars = Array.Empty<IntPtr>();

    /// <summary>The strips the bars were last placed on, in client pixels.</summary>
    private readonly List<RectInt32> _captionRects = new();

    private bool _nonClientDirty;

    /// <summary>True while the system runs a caption move or a frame resize loop.</summary>
    private bool _inMoveLoop;

    /// <summary>
    /// Whether the current move loop is being timed. Off by default: set GUI_DRAG_LOG=1, or drop
    /// an empty "gui-drag-log" file in %TEMP%, and the next drag records every window move with
    /// its gap, so a drag that trails can be attributed to the work between moves.
    /// </summary>
    private bool _dragLogActive;
    private long _dragStartTicks;
    private long _dragLastMoveTicks;
    private long _dragMaxGapTicks;
    private long _dragMaxProcTicks;
    private int _dragMoves;
    private int _dragSizes;
    private int _dragPosChanges;

    /// <summary>
    /// Experimental: while a caption move runs, skip WinUI's window-proc handling of the
    /// position notifications. DWM moves the composed surface either way, and WinUI's handler
    /// costs several milliseconds per step, which starves the move loop at high pointer rates.
    /// <summary>Opt in with GUI_MOVE_SKIP_XAML=1 or an empty %TEMP%\gui-move-skip marker file.</summary>
    private bool _skipXamlMove;
    private int _moveLoopSizeW;
    private int _moveLoopSizeH;


    // Applied sets, compared before every call: SetRegionRects is not free and a layout pass
    // would otherwise re-register the same rects several times a second.
    private RectInt32[] _nonClientMinimize = Array.Empty<RectInt32>();
    private RectInt32[] _nonClientMaximize = Array.Empty<RectInt32>();
    private RectInt32[] _nonClientClose = Array.Empty<RectInt32>();

    /// <summary>
    /// The frame is a borderless one (Attach removes the title bar) and the chrome paints its
    /// own header, so the caption was reimplemented here: GetCursorPos/Move for the drag and
    /// a double-press for maximize. These regions hand that back to Win32. The header band and
    /// the tab rail become the non-client caption, which gives the window the system's drag
    /// loop (snapping to the screen edges, dragging a maximized window back to its size,
    /// double-click maximize, the right-click system menu and the touch gesture), and the
    /// three window buttons become the system's own minimize/maximize/close regions, which is
    /// what also puts the snap-layouts flyout over the maximize button.
    ///
    /// The three window buttons stay ordinary XAML buttons: hover, press and click are the
    /// template's own, only the hit test is answered by the region.
    /// </summary>
    private void InstallNonClientChrome()
    {
        if (_quickTerminal || _appWindow is null || _hwnd == IntPtr.Zero) return;

        try
        {
            _nonClient = InputNonClientPointerSource.GetForWindowId(_appWindow.Id);
        }
        catch (Exception error)
        {
            Diag.LogException("ws.nc.install", error);
            return;
        }

        // Non-client messages are sent to the top-level window, which WinUI has already
        // subclassed; this rides along that chain instead of replacing it.
        _nonClientProc ??= OnNonClientMessage;
        SetWindowSubclass(_hwnd, _nonClientProc, NonClientSubclass, IntPtr.Zero);
        EnsureDragBars(2);
        UpdateNonClientRegions();
    }

    /// <summary>Drops the non-client chrome together with the window.</summary>
    private void ReleaseNonClientChrome()
    {
        if (_nonClientProc is not null && IsWindow(_hwnd))
            RemoveWindowSubclass(_hwnd, _nonClientProc, NonClientSubclass);
        foreach (IntPtr bar in _dragBars) DragBar.Destroy(bar);
        _dragBars = Array.Empty<IntPtr>();
        _nonClient = null;
        _nonClientMinimize = _nonClientMaximize = _nonClientClose = Array.Empty<RectInt32>();
    }

    /// <summary>
    /// Queues a region update for the next dispatcher turn. Every region follows the chrome,
    /// so tab, rail and page changes have to re-register them; a single layout pass can raise
    /// several of these (the tab strip moves chips with a render transform), and one call per
    /// turn is enough.
    /// </summary>
    private void InvalidateNonClientRegions()
    {
        if (_nonClient is null) return;
        // The move loop re-registers everything when it ends; nothing to queue up here.
        if (_inMoveLoop) return;
        if (_nonClientDirty) return;
        _nonClientDirty = true;
        if (_dispatcher?.TryEnqueue(() =>
            {
                _nonClientDirty = false;
                Diag.Guard("ws.nc.regions", UpdateNonClientRegions);
            }) != true)
        {
            _nonClientDirty = false;
        }
    }

    /// <summary>
    /// Registers the regions now, for a change that has to be visible to the next press (a tab
    /// scroll, a strip shift). Deferred while the system runs a move loop, like every other
    /// update: the move loop must not be made to wait on layout work.
    /// </summary>
    private void RefreshNonClientRegions()
    {
        if (_nonClient is null) return;
        if (_inMoveLoop) return;
        UpdateNonClientRegions();
    }

    /// <summary>
    /// Re-registers the three window buttons, which are the only non-client area left: the drag
    /// itself belongs to the native drag bar windows (see DragBar), because a caption region or a
    /// press handed to Win32 as WM_NCLBUTTONDOWN/HTCAPTION is served by WinUI's window procedure,
    /// which drags in DIP-sized steps at the pointer's frame rate and trails the cursor at a high
    /// DPI.
    /// </summary>
    private void UpdateNonClientRegions()
    {
        if (_nonClient is null || _hwnd == IntPtr.Zero) return;

        // Nothing to measure before the tree is rooted: the first layout pass registers again.
        XamlRoot? xamlRoot = _root.XamlRoot;
        if (xamlRoot is null) return;
        double scale = xamlRoot.RasterizationScale;

        ChromeButton[] buttons = VisibleCaptionButtons();
        ApplyRegion(NonClientRegionKind.Minimize, Single(buttons, 0, scale), ref _nonClientMinimize);
        ApplyRegion(NonClientRegionKind.Maximize, Single(buttons, 1, scale), ref _nonClientMaximize);
        ApplyRegion(NonClientRegionKind.Close, Single(buttons, 2, scale), ref _nonClientClose);

        UpdateDragBar(scale);
    }

    /// <summary>
    /// Positions the native drag bar over the empty part of the caption band (see DragBar):
    /// the strip right of the chips in the top layout, the rail's header row and the pane header's
    /// gap in the side ones, and the page's title band while the settings page is open.
    /// </summary>
    private void UpdateDragBar(double scale)
    {
        if (_quickTerminal)
        {
            _captionRects.Clear();
            foreach (IntPtr idle in _dragBars) DragBar.Hide(idle);
            return;
        }

        var rects = new List<RectInt32?>(_dragBars.Length + 1);

        if (_settingsActive)
        {
            // The page covers the header and draws its own title band: that band is the drag
            // strip while it is open, and the window buttons keep their own area.
            ChromeButton[] pageButtons = VisibleCaptionButtons();
            if (Bounds(_settingsHead, scale) is { } head && pageButtons.Length == 3 &&
                Bounds(pageButtons[0], scale) is { } first)
            {
                rects.Add(new RectInt32(head.X, head.Y, first.X - head.X, head.Height));
            }
        }
        else if (_placement == TabPlacement.Top)
        {
            // The strip right of the chips, down to the pane. The bar stops before the chips and
            // the header tools, which is what keeps their own input.
            if (Bounds(_header, scale) is { } band && Bounds(_headerTopTools, scale) is { } tools)
            {
                int left = band.X;
                foreach (Tab tab in _tabs)
                {
                    if (Bounds(tab.Chip, scale) is { } chip) left = Math.Max(left, chip.X + chip.Width);
                }
                int bottom = band.Y + band.Height;
                if (Bounds(_surfaceHost, scale) is { } surface) bottom = Math.Max(bottom, surface.Y);
                rects.Add(new RectInt32(left, band.Y, tools.X - left, bottom - band.Y));
            }
        }
        else
        {
            // The rail's header row right of its buttons.
            if (Bounds(_sidebarTools, scale) is { } row && Bounds(_sidebar, scale) is { } rail)
            {
                int left = row.X;
                foreach (UIElement child in _sidebarTools!.Children)
                {
                    if (Bounds(child as FrameworkElement, scale) is { } button)
                        left = Math.Max(left, button.X + button.Width);
                }
                rects.Add(new RectInt32(left, row.Y, rail.X + rail.Width - left, row.Height));
            }

        }

        // Every visible pane's header gap drags the window, not just the focused one: the
        // unfocused header hides its action buttons, so its gap reaches the header's edge.
        if (!_settingsActive && _tabs.Count > 0 && _active >= 0 && _active < _tabs.Count)
        {
            ForEachPane(_tabs[_active], pane =>
            {
                if (pane.HeaderElement is not { } header) return;
                if (Bounds(header, scale) is not { Height: > 0 } box) return;
                if (pane.HeaderDragGap() is not { Width: > 0 } gap) return;

                rects.Add(new RectInt32(
                    box.X + (int)Math.Round(gap.X * scale),
                    box.Y,
                    (int)Math.Round(gap.Width * scale),
                    (int)Math.Round(gap.Height * scale)));
            });
        }

        EnsureDragBars(rects.Count);
        int frameX = ResizeFrameX;
        int frameY = ResizeFrameY;
        _captionRects.Clear();
        for (int i = 0; i < _dragBars.Length; i++)
        {
            if (i < rects.Count && rects[i] is { } rect)
            {
                RectInt32 strip = KeepFrameFree(rect, frameX, frameY);
                _captionRects.Add(strip);
                DragBar.Place(_dragBars[i], strip);
            }
            else
            {
                DragBar.Hide(_dragBars[i]);
            }
        }
    }

    /// <summary>
    /// Takes the bars down for the move loop, but keeps the one under the pointer: the system
    /// evaluates the cursor as the loop ends, and with no bar there that hit test lands on the
    /// XAML content, whose hover state is stale - the pointer left the sidebar splitter for a
    /// bar, which never gave XAML a pointer-exit, so the splitter's resize arrow flashes over
    /// the caption strip for a frame. The other bars still go, one composited surface less per
    /// move.
    /// </summary>
    private void HideIdleDragBars()
    {
        int keep = -1;
        if (_hwnd != IntPtr.Zero && GetCursorPos(out POINT point) && ScreenToClient(_hwnd, ref point))
        {
            for (int i = 0; i < _dragBars.Length && i < _captionRects.Count; i++)
            {
                if (Covers(_captionRects[i], point)) { keep = i; break; }
            }
        }
        for (int i = 0; i < _dragBars.Length; i++)
        {
            if (i != keep) DragBar.Hide(_dragBars[i]);
        }
    }

    private static bool Covers(RectInt32 rect, POINT point) =>
        point.X >= rect.X && point.X < rect.X + rect.Width &&
        point.Y >= rect.Y && point.Y < rect.Y + rect.Height;

    /// <summary>
    /// Grows the drag bars to hold one strip per caption area. Each bar is a layered child
    /// window, so a split's second pane header gets its bar only once that split exists.
    /// </summary>
    private void EnsureDragBars(int count)
    {
        if (_dragBars.Length >= count) return;

        var grown = new IntPtr[count];
        Array.Copy(_dragBars, grown, _dragBars.Length);
        for (int i = _dragBars.Length; i < grown.Length; i++) grown[i] = DragBar.Create(_hwnd);
        _dragBars = grown;
    }

    /// <summary>
    /// The frame Windows keeps for resizing, in device pixels. A drag bar has to stop short of
    /// it: the bars are layered child windows, so whatever they cover answers the hit test as a
    /// caption, and the resize border underneath never turns the cursor into a resize arrow.
    /// The two axes differ -- measuring the arrows on a 200 % display gives 6 px at the top and
    /// 13 px at the sides -- so each side uses the metrics Windows uses for it.
    /// </summary>
    private static int ResizeFrameX =>
        GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);

    private static int ResizeFrameY =>
        GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CYBORDER);

    /// <summary>
    /// Pulls a drag strip off the frame, on the sides it actually touches. The remainder stays
    /// flush with the border: a gap would be a strip that neither resizes nor drags.
    /// </summary>
    private static RectInt32 KeepFrameFree(RectInt32 rect, int frameX, int frameY)
    {
        int left = Math.Max(rect.X, frameX);
        int top = Math.Max(rect.Y, frameY);
        int right = rect.X + rect.Width;
        int bottom = rect.Y + rect.Height;
        return new RectInt32(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private RectInt32? Bounds(FrameworkElement? element, double scale)
    {
        if (element is null || element.Visibility != Visibility.Visible) return null;
        if (!(element.ActualWidth > 0) || !(element.ActualHeight > 0)) return null;

        try
        {
            Rect box = element.TransformToVisual(null)
                .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
            return new RectInt32(
                (int)Math.Round(box.X * scale),
                (int)Math.Round(box.Y * scale),
                (int)Math.Round(box.Width * scale),
                (int)Math.Round(box.Height * scale));
        }
        catch (Exception error)
        {
            // An element in the middle of a chrome rebuild has no transform to the root yet;
            // it simply contributes no region for this pass.
            Diag.Debug("ws.nc.bounds", error.Message);
            return null;
        }
    }

    /// <summary>One button's region, or an empty set when the index is not on screen.</summary>
    private RectInt32[] Single(ChromeButton[] buttons, int index, double scale)
    {
        if (index >= buttons.Length) return Array.Empty<RectInt32>();
        return Bounds(buttons[index], scale) is { } rect ? new[] { rect } : Array.Empty<RectInt32>();
    }

    private void ApplyRegion(NonClientRegionKind kind, List<RectInt32> rects, ref RectInt32[] applied) =>
        ApplyRegion(kind, rects.ToArray(), ref applied);

    private void ApplyRegion(NonClientRegionKind kind, RectInt32[] rects, ref RectInt32[] applied)
    {
        if (SameRects(rects, applied)) return;
        applied = rects;
        try
        {
            _nonClient?.SetRegionRects(kind, rects);
        }
        catch (Exception error)
        {
            Diag.LogException("ws.nc.region", error);
        }
    }

    private static bool SameRects(RectInt32[] left, RectInt32[] right)
    {
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (left[i].X != right[i].X || left[i].Y != right[i].Y ||
                left[i].Width != right[i].Width || left[i].Height != right[i].Height)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The three window buttons on screen: the settings page draws its own copy.</summary>
    private ChromeButton[] VisibleCaptionButtons() =>
        _settingsActive && _settingsCaptionButtons.Length == 3 ? _settingsCaptionButtons : _headerCaptionButtons;

    private IntPtr OnNonClientMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        IntPtr id, IntPtr data)
    {
        bool destroying = false;
        bool skipChain = false;
        if (_inMoveLoop && _skipXamlMove)
        {
            if (message == WM_MOVE)
            {
                skipChain = true;
            }
            else if (message == WM_WINDOWPOSCHANGED)
            {
                // A resize has to reach WinUI or the panes would not reflow with the frame: only
                // a pure move is safe to swallow, where DWM already moves the composed surface.
                GetWindowRect(hwnd, out RECT now);
                skipChain = now.Right - now.Left == _moveLoopSizeW &&
                            now.Bottom - now.Top == _moveLoopSizeH;
            }
        }
        switch (message)
        {
            case WM_ENTERSIZEMOVE:
                // The system now owns the pointer until WM_EXITSIZEMOVE: registering regions or
                // measuring elements in that window stalls the move loop, which is what a drag
                // that feels like it lags behind the cursor looks like.
                //
                // The drag bars go away for the length of the drag as well: the system has the
                // press already, and a layered child window is one more surface for DWM to
                // composite on every step of the move — which is what makes the desktop window
                // manager spike while a window is dragged. The one under the pointer stays up,
                // see HideIdleDragBars.
                _inMoveLoop = true;
                HideIdleDragBars();
                _skipXamlMove = MoveSkipEnabled();
                if (_skipXamlMove)
                {
                    GetWindowRect(hwnd, out RECT loopRect);
                    _moveLoopSizeW = loopRect.Right - loopRect.Left;
                    _moveLoopSizeH = loopRect.Bottom - loopRect.Top;
                }
                _dragLogActive = DragLogRequested();
                if (_dragLogActive) BeginDragLog(hwnd);
                break;

            case WM_EXITSIZEMOVE:
                _inMoveLoop = false;
                if (_dragLogActive) EndDragLog();
                _dragLogActive = false;
                if (_skipXamlMove)
                {
                    _skipXamlMove = false;
                    ResyncWindowAfterMove(hwnd);
                }
                Diag.Guard("ws.nc.regions", UpdateNonClientRegions);
                break;

            case WM_MOVE:
                if (_dragLogActive && _inMoveLoop) LogDragMove(hwnd);
                break;

            case WM_SIZE:
                if (_dragLogActive && _inMoveLoop) _dragSizes++;
                break;

            case WM_WINDOWPOSCHANGED:
                if (_dragLogActive && _inMoveLoop)
                {
                    _dragPosChanges++;
                    // A skipped pure move never raises WM_MOVE, so time the update here instead.
                    if (skipChain) LogDragMove(hwnd);
                }
                break;

            case WM_NCLBUTTONDOWN:
                // Presses on the caption never reach XAML now, so the layers XAML would have
                // dismissed (the palette, an IME composition) are dismissed from here.
                OnNonClientPress();
                break;

            case WM_NCDESTROY:
                // Unhook after the chain has run: DefSubclassProc needs the registration.
                destroying = true;
                break;
        }

        long procStart = _dragLogActive && _inMoveLoop ? Stopwatch.GetTimestamp() : 0;
        IntPtr result = skipChain ? IntPtr.Zero : DefSubclassProc(hwnd, message, wParam, lParam);
        if (procStart != 0)
        {
            long procTicks = Stopwatch.GetTimestamp() - procStart;
            if (procTicks > _dragMaxProcTicks) _dragMaxProcTicks = procTicks;
            if (TicksToMs(procTicks) >= 2)
                Diag.Log("drag", $"slow msg=0x{message:X4} proc={TicksToMs(procTicks):F1}ms");
        }
        if (destroying) ReleaseNonClientChrome();
        return result;
    }

    /// <summary>Timing is opt-in: GUI_DRAG_LOG=1, or an empty %TEMP%\gui-drag-log marker file.</summary>
    private static bool DragLogRequested()
    {
        if (Environment.GetEnvironmentVariable("GUI_DRAG_LOG") == "1") return true;
        try
        {
            return File.Exists(Path.Combine(Path.GetTempPath(), "gui-drag-log"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether the loop may swallow WinUI's position notifications (GUI_MOVE_SKIP_XAML=0 turns
    /// it off). WinUI's window procedure costs 6-8 ms per position change, which starves the
    /// move loop at a high pointer rate: pure moves are left to DWM instead, and a resize still
    /// goes through because the panes have to reflow.
    /// </summary>
    private static bool MoveSkipEnabled() =>
        Environment.GetEnvironmentVariable("GUI_MOVE_SKIP_XAML") != "0";

    /// <summary>
    /// One forced frame change after a skipped move: WinUI re-reads the window position and
    /// re-syncs its composition, which is the single notification it was denied per step.
    /// </summary>
    private static void ResyncWindowAfterMove(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void BeginDragLog(IntPtr hwnd)
    {
        _dragStartTicks = Stopwatch.GetTimestamp();
        _dragLastMoveTicks = 0;
        _dragMaxGapTicks = 0;
        _dragMaxProcTicks = 0;
        _dragMoves = 0;
        _dragSizes = 0;
        _dragPosChanges = 0;
        GetWindowRect(hwnd, out RECT rect);
        Diag.Log("drag", $"enter rect={rect.Left},{rect.Top} size={rect.Right - rect.Left}x{rect.Bottom - rect.Top}");
    }

    private void LogDragMove(IntPtr hwnd)
    {
        long now = Stopwatch.GetTimestamp();
        long gap = _dragLastMoveTicks == 0 ? 0 : now - _dragLastMoveTicks;
        _dragLastMoveTicks = now;
        if (gap > _dragMaxGapTicks) _dragMaxGapTicks = gap;
        _dragMoves++;

        GetWindowRect(hwnd, out RECT rect);
        GetCursorPos(out POINT cursor);
        Diag.Log("drag", $"move gap={TicksToMs(gap):F1} rect={rect.Left},{rect.Top} cursor={cursor.X},{cursor.Y}");
    }

    private void EndDragLog()
    {
        long total = Stopwatch.GetTimestamp() - _dragStartTicks;
        Diag.Log("drag",
            $"exit dur={TicksToMs(total):F0} moves={_dragMoves} gapMax={TicksToMs(_dragMaxGapTicks):F1} " +
            $"procMax={TicksToMs(_dragMaxProcTicks):F2} size={_dragSizes} poschanged={_dragPosChanges}");
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private void OnNonClientPress()
    {
        // Posted: this runs inside the window procedure, before the system starts the drag or
        // the button's own command, and collapsing a XAML layer there would nest dispatch.
        _dispatcher?.TryEnqueue(() =>
        {
            if (_paletteLayer is { Visibility: Visibility.Visible }) HideCommandPalette();
            if (!_composing) return;
            _imeDiscard = true;
            ShowImeComposition();
        });
    }

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
        if (_window is null) return;
        bool maximized = _window.AppWindow.Presenter is OverlappedPresenter presenter &&
                         presenter.State == OverlappedPresenterState.Maximized;
        string glyph = ((char)(maximized ? 0xE923 : 0xE922)).ToString();
        if (_maximizeIcon is not null) _maximizeIcon.Glyph = glyph;
        // The settings page draws its own copy of the buttons over the covered header.
        if (_settingsMaximizeIcon is not null) _settingsMaximizeIcon.Glyph = glyph;
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

    /// <summary>
    /// Selects the tab and split showing a session, so a notification click lands on the shell
    /// that asked for attention even after the user has switched elsewhere or moved the tab to
    /// another window. False when that session is gone, which is what a toast outliving its
    /// pane looks like.
    /// </summary>
    internal bool FocusSessionById(Guid id)
    {
        for (int index = 0; index < _tabs.Count; index++)
        {
            TerminalPane? found = null;
            ForEachPane(_tabs[index], pane => { if (pane.SessionId == id) found = pane; });
            if (found is null) continue;

            Tab tab = _tabs[index];
            tab.Focused = found;
            Activate(index);
            Diag.Debug("notify", $"focus session={id} tab={index}");

            // A zoomed tab paints only its focused pane, and Activate does not rebuild the
            // layout, so the zoom would otherwise keep showing the pane it was zoomed on.
            if (tab.Zoomed) Relayout(tab);
            return true;
        }
        return false;
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
                .AddArgument("session", pane.SessionId.ToString())
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
            builder.AddArgument("session", pane.SessionId.ToString());
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.notify", error);
            FlashWindow();
        }
    }

    /// <summary>
    /// "notify-on-program-status": OSC 7501 reported the program blocked, done or failed.
    /// Only notify while the pane is in the background; the status badge already shows the
    /// outcome on the pane the user is watching.
    /// </summary>
    private void OnPaneProgramAttention(TerminalPane pane, ProgramStatusInfo status)
    {
        if (IsActivePane(pane)) return;
        Diag.Debug("notify", $"program state={status.State} session={pane.SessionId}");

        try
        {
            EnsureNotificationsRegistered();

            string heading = status.State switch
            {
                ProgramState.Blocked => Strings.ProgramBlockedTitle,
                ProgramState.Error => Strings.ProgramFailedTitle,
                _ => Strings.ProgramDoneTitle,
            };
            string detail = status.Message.Length > 0 ? status.Message
                : status.Title.Length > 0 ? status.Title
                : status.App.Length > 0 ? status.App
                : pane.Title;
            var builder = new AppNotificationBuilder().AddText(heading);
            if (detail.Length > 0) builder.AddText(detail);
            builder.AddArgument("session", pane.SessionId.ToString());
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
        // Alt+F4 on the drop-down hides it: the shell inside is meant to survive a toggle.
        if (_quickTerminal && !_allowClose)
        {
            args.Cancel = true;
            QuickTerminal.Hide();
            return;
        }

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

    /// <summary>Puts the keyboard into the terminal; the quick terminal does this on show.</summary>
    internal void FocusTerminal()
    {
        if (_tabs.Count > 0) FocusKeyboardAnchor();
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

    /// <summary>The chord modifiers, with "key-remap" applied to each physical key.</summary>
    private int CurrentMods() => ModifierKeys.Chord(_config.KeyRemaps);

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

        // A modal text prompt owns the keyboard: Delete/Backspace and the arrow keys must
        // reach its TextBox, not be consumed here as terminal input.
        if (_textPromptOpen) return false;

        // The keys page is waiting for the press that becomes a new shortcut; it must not
        // reach the page's own Esc handling or the shell. Only the focused field records;
        // a capture left behind by a focus change is dropped instead of swallowing the key.
        if (_keybindCaptureAction is not null)
        {
            if (IsKeybindCaptureFocused()) return OnKeybindCaptureKey(args);
            CancelKeybindCapture();
        }

        // Esc aborts a split drag before any other shortcut or the shell sees the key.
        if (_dragPane is not null && args.Key == VirtualKey.Escape)
        {
            EndPaneDrag(_dragPane, false);
            return true;
        }

        // The palette owns its own keys (filter box, arrow navigation, Enter/Esc).
        if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible)
        {
            ClearPendingSequence();
            return OnPaletteKey(args);
        }

        // The settings page closes on Esc; every other key goes to the focused setting instead
        // of the shell or a window shortcut.
        if (_settingsLayer is not null && _settingsLayer.Visibility == Visibility.Visible)
        {
            ClearPendingSequence();
            return OnSettingsKey(args);
        }

        // The inline tab rename editor owns the keyboard. Enter and Esc are decided here,
        // before the text box swallows them; arrows, Home/End and Delete stay with the box,
        // which is why every other key is passed through untouched.
        if (_renamingTab is { } renaming)
        {
            ClearPendingSequence();
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
        {
            ClearPendingSequence();
            return false;
        }

        // A surface held open after its shell exited ("wait-after-command", or the abnormal
        // exit notice) closes on the next key, which is what those options describe.
        if (_tabs.Count > 0 && _tabs[_active].Focused is { HoldingAfterExit: true } held)
        {
            held.CloseFromHold();
            return true;
        }

        // While a composition is open the IME owns the keyboard: the keys that build it, and
        // the keys that commit or cancel it, belong to the input method. Handling one of them
        // here would run a shortcut the IME is about to re-inject once the composition ends.
        if (_composing) return true;

        int mods = CurrentMods();
        bool ctrl = (mods & GhosttyNative.MODS_CTRL) != 0;
        bool shift = (mods & GhosttyNative.MODS_SHIFT) != 0;
        bool alt = (mods & GhosttyNative.MODS_ALT) != 0;

        // A trigger sequence ("keybind = ctrl+a>n=new_tab") holds the keys that built its
        // prefix instead of running them: the next key either completes the sequence or
        // proves it was no sequence, in which case the held keys go to the shell.
        // A codepoint binding is the one case that needs the character this layout types
        // for the key, so the probe only runs when such a line exists. The key tables are
        // per surface, so the focused pane's stack is what the lookup walks.
        TerminalPane? keyPane = _tabs.Count > 0 ? _tabs[_active].Focused : null;
        IReadOnlyList<string>? keyTables = keyPane is { HasKeyTables: true } ? keyPane.KeyTableNames : null;
        uint codepoint = _keybinds.HasCodepoints ? KeyMap.UnshiftedCodepoint(args.Key) : 0;
        Func<string, bool>? canPerform = _keybinds.HasPerformable ? _canPerform ??= CanPerform : null;
        KeybindStep step = _keybinds.Step(_pendingSequence, mods, args.Key,
            out string? sequenceAction, out string sequenceNext, codepoint, keyTables,
            out KeybindMatchInfo sequenceInfo, canPerform);
        switch (step)
        {
            case KeybindStep.Pending:
                _pendingSequence = sequenceNext;
                _pendingKeys.Add((mods, args.Key));
                return true;
            case KeybindStep.Action:
            {
                // "unconsumed:" still sends the keys on to the shell: the held prefix of a
                // sequence first, then the key that completed it.
                List<(int Mods, VirtualKey Key)>? heldKeys = sequenceInfo.Unconsumed
                    ? new List<(int Mods, VirtualKey Key)>(_pendingKeys)
                    : null;

                ClearPendingSequence();
                RunKeybindAction(sequenceAction, sequenceInfo.All);
                // A table activated "once" pops after the binding it provided ran.
                if (sequenceInfo.Table is not null) keyPane?.CompleteKeyTable(sequenceInfo.Table);
                if (heldKeys is not null && keyPane is not null) return SendUnconsumed(keyPane, heldKeys, args);
                return true;
            }
            case KeybindStep.Miss:
                _pendingSequence = null;
                ReplayPendingKeys();
                break;
        }

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
        KeybindMatch match = _keybinds.Resolve(mods, args.Key, out string? boundAction, codepoint,
            keyTables, out KeybindMatchInfo boundInfo, canPerform);
        if (match == KeybindMatch.Action)
        {
            RunKeybindAction(boundAction, boundInfo.All);
            if (boundInfo.Table is not null) keyPane?.CompleteKeyTable(boundInfo.Table);
            if (boundInfo.Unconsumed && keyPane is not null) return keyPane.KeyDown(args);
            return true;
        }
        if (match == KeybindMatch.Unbound)
        {
            // An unbound chord belongs to the shell: it has to be encoded like any other key
            // the app does not handle, which is what Ghostty means by "sent through to the
            // child command if it is printable".
            return keyPane is not null && keyPane.KeyDown(args);
        }

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
    /// Sends a binding's keys to the shell after its action ran: the prefix a sequence was
    /// holding, then the key that completed it. A plain text key returns false so the IME
    /// types it, which is exactly what would have happened without the binding.
    /// </summary>
    private static bool SendUnconsumed(TerminalPane pane,
        List<(int Mods, VirtualKey Key)> held, KeyRoutedEventArgs args)
    {
        foreach ((int mods, VirtualKey key) in held)
            pane.SendBufferedKey(key, mods);
        return pane.KeyDown(args);
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
        // fullscreen here too.
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

    /// <summary>
    /// Drops every field that points at a pane which is leaving. Each of these keeps the
    /// whole pane — its XAML tree, its renderer and its session — alive, so a stale one
    /// outlives the tab it belonged to for the rest of the window's life.
    /// </summary>
    private void ForgetPane(TerminalPane pane)
    {
        if (ReferenceEquals(_imeAnchorPane, pane)) _imeAnchorPane = null;
        if (ReferenceEquals(_imeCompositionPane, pane)) _imeCompositionPane = null;
        if (ReferenceEquals(_dragPane, pane)) _dragPane = null;
        if (ReferenceEquals(_dropPane, pane)) _dropPane = null;
        if (ReferenceEquals(_surfaceTarget, pane)) _surfaceTarget = null;
    }

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

        // Every one of these timers is queued on the UI dispatcher, so a running one keeps
        // this workspace (and through _root the window's whole XAML tree) rooted after the
        // window is gone, and its tick writes into elements that no longer exist. Dragging is
        // the one that never ends by itself: EndTabDrag needs a pointer release a closing
        // window never gets, which would leave the 16 ms scrub timer ticking for the process.
        _dragScrollTimer?.Stop();
        _dropTimer?.Stop();
        _sidebarTimer?.Stop();
        _updatePillTimer?.Stop();
        _updateDelayTimer?.Stop();
        _zoomTransition?.Timer?.Stop();
        _zoomTransition = null;
        _sidebarAnimating = false;
        _dragTab = null;
        _dragArmed = false;
        _dragMoving = false;
        _hasPendingTabMove = false;

        foreach (Tab tab in _tabs) ForEachPane(tab, pane => pane.Close());
        _tabs.Clear();

        _imeAnchorPane = null;
        _imeCompositionPane = null;
        _dragPane = null;
        _dropPane = null;
        _surfaceTarget = null;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        IntPtr id, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CYBORDER = 6;
    private const int SM_CXPADDEDBORDER = 92;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);



    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr reserved);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT info);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, uint id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, uint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
