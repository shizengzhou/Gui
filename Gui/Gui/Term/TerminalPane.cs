using System;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;

namespace Gui.Term;

/// <summary>
/// One terminal surface: a focusable host control holding a Win2D canvas, with its own
/// ConPTY session and renderer. The workspace owns focus and the IME sink, so keys and
/// committed text are pushed in for the focused pane only.
/// </summary>
public sealed class TerminalPane
{
    private readonly Config _config;
    private float _fontSizeDip = 13.0f;
    private TerminalSession _session;
    private readonly TerminalRenderer _renderer = new();

    private readonly ContentControl _host;
    private readonly Border _frame;
    private readonly CanvasControl _canvas;
    private readonly Border _dim;
    private readonly ScrollBar _scrollBar;
    private readonly TextBlock _pathText;
    private readonly FontIcon _iconFallback;
    private readonly Image _shellIcon;
    private Path? _zoomIcon;
    private Border? _zoomButton;
    private Border? _closeButton;
    private StackPanel? _actions;
    private double _actionsInset = -1;
    private Grid? _info;
    private double _infoInset = -1;
    private readonly List<ActionVisual> _actionVisuals = new();

    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _blinkTimer;
    private DispatcherQueueTimer? _cwdTimer;

    private string _workingDirectory;
    private string _shellIconPath = string.Empty;
    private ImageSource? _shellIconSource;
    private bool _cursorOn = true;
    private bool _redrawPending;
    private bool _ready;
    private bool _focused;
    private bool _hovered;
    private bool _selecting;
    private bool _updatingScrollbar;
    private int _layoutSuspended;
    private bool _drawHooked;
    private XamlRoot? _hookedRoot;
    private double _appliedScale = 1.0;
    private ushort _anchorX;
    private ushort _anchorY;
    private ushort _cols = 80;
    private ushort _rows = 24;

    public string Title = string.Empty;

    public Action<TerminalPane>? OnTitle;
    public Action<TerminalPane>? OnExit;
    public Action<TerminalPane>? OnFocus;
    public Action<TerminalPane>? OnSplitRight;
    public Action<TerminalPane>? OnSplitDown;
    public Action<TerminalPane>? OnMaximize;
    public Action<TerminalPane>? OnClosePane;
    public Action<TerminalPane>? OnShellIcon;

    private sealed class ActionVisual
    {
        public Path Icon = null!;
        public Border Button = null!;
        public TextBlock TipTitle = null!;
        public TextBlock TipKey = null!;
        public Func<string> Tip = () => string.Empty;
    }

    public TerminalPane(Config config)
    {
        _config = config;
        if (config.FontSize > 0) _fontSizeDip = config.FontSizeDip;

        _session = new TerminalSession();
        _session.SetScrollbackLimit(config.ScrollbackLimit);
        _session.SetScrollbackLimitLines(config.ScrollbackLimitLines);
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _canvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = CanvasClearColor(),
            Margin = new Thickness(config.PaddingX, config.PaddingY, config.PaddingX, config.PaddingY),
        };

        _dim = new Border
        {
            Margin = _canvas.Margin,
            Background = new SolidColorBrush(DimFillColor()),
            IsHitTestVisible = false,
            Opacity = 0.0,
        };

        // Scrollbar: the stock WinUI control, over the terminal's right edge in the 14 DIP
        // the canvas leaves free. It owns the thumb sizing, the drag, the page clicks and the
        // hover treatment; this class only maps its Value to a scrollback row.
        _scrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Same 12 DIP band the canvas keeps above and below its rows.
            Margin = new Thickness(0, 12, 2, 12),
            // The terminal owns the keyboard; a focusable bar would take Tab away from it.
            IsTabStop = false,
            Visibility = Visibility.Collapsed,
        };
        _scrollBar.ValueChanged += (_, _) =>
        {
            // UpdateScrollbar writes the value back from the terminal's state; that write is
            // not a user scroll and must not be echoed into the terminal.
            if (_updatingScrollbar || !_ready) return;
            _session.ScrollTo((ulong)Math.Round(_scrollBar.Value));
        };
        // The control does not take the wheel itself, so scrolling over the bar has to reach
        // the terminal the same way it does over the surface.
        _scrollBar.PointerWheelChanged += OnPointerWheel;
        _scrollBar.SizeChanged += (_, _) => UpdateScrollbar();

        var faint = new SolidColorBrush(Theme.TextFaint());
        var strong = new SolidColorBrush(Theme.TextStrong());

        // Header row: cwd on the left, pane actions on the right. It lives inside the same
        // frame as the surface so focus and rounded corners stay unified.
        var header = new Grid { Height = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new Grid { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _info = info;
        _infoInset = 0.0;
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconSlot = new Grid { Width = 16, Height = 16 };
        _iconFallback = new FontIcon
        {
            Glyph = "\uE756",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = faint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconSlot.Children.Add(_iconFallback);

        _shellIcon = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };
        iconSlot.Children.Add(_shellIcon);
        info.Children.Add(iconSlot);

        _workingDirectory = DefaultWorkingDirectory(config);
        _pathText = new TextBlock
        {
            Text = _workingDirectory,
            FontSize = 11.5,
            Foreground = faint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
        };
        Grid.SetColumn(_pathText, 1);
        info.Children.Add(_pathText);

        header.Children.Add(info);

        _actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _actionsInset = 0.0;

        AddAction(SplitIcon(true, faint, 12.0), () => Strings.SplitRight, "alt+shift+plus",
            () => OnSplitRight?.Invoke(this));
        AddAction(SplitIcon(false, faint, 12.0), () => Strings.SplitDown, "alt+shift+-",
            () => OnSplitDown?.Invoke(this));

        _zoomIcon = StyledIcon(faint, 12.0);
        _zoomIcon.Data = ZoomGeometry(false, 12.0);
        _zoomButton = AddAction(_zoomIcon, () => Strings.ToggleZoom, "ctrl+shift+enter",
            () => OnMaximize?.Invoke(this));
        _closeButton = AddAction(CloseIcon(faint, 11.0), () => Strings.ClosePane, "ctrl+shift+w",
            () => OnClosePane?.Invoke(this));

        Grid.SetColumn(_actions, 1);
        header.Children.Add(_actions);

        var frameRoot = new Grid();
        frameRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frameRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frameRoot.Children.Add(header);
        Grid.SetRow(_canvas, 1);
        frameRoot.Children.Add(_canvas);
        Grid.SetRow(_scrollBar, 1);
        frameRoot.Children.Add(_scrollBar);
        Grid.SetRow(_dim, 1);
        frameRoot.Children.Add(_dim);

        _frame = new Border
        {
            // Matches the content island's radius: the pane runs to the island's edges, and a
            // tighter radius would leave a crescent of island colour at every shared corner.
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(SurfaceColor()),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Theme.PaneStroke()),
            Child = frameRoot,
        };
        _frame.PointerEntered += OnFramePointerEntered;
        _frame.PointerMoved += OnFramePointerMoved;
        _frame.PointerExited += OnFramePointerExited;

        _host = new ContentControl
        {
            IsTabStop = true,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Content = _frame,
        };

        _renderer.ApplyConfig(config);
        // A background image decodes asynchronously; repaint once it is ready.
        _renderer.NeedsRedraw += Redraw;

        _canvas.PointerWheelChanged += OnPointerWheel;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.DoubleTapped += OnCanvasDoubleTapped;
        _canvas.PointerCaptureLost += (_, _) => _selecting = false;
        _canvas.SizeChanged += (_, _) => Diag.Guard("pane.layout", () => Layout());
        _canvas.Loaded += (_, _) => Diag.Guard("pane.start", () =>
        {
            HookXamlRoot();
            if (!_ready) Start();
        });

        if (_dispatcher is not null)
        {
            _blinkTimer = _dispatcher.CreateTimer();
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(530);
            _blinkTimer.Tick += (_, _) => Diag.Guard("pane.blink", () =>
            {
                _cursorOn = !_cursorOn;
                Redraw();
            });
            // Started by SetFocused(true): an unfocused pane keeps a steady caret.

            // PowerShell never emits OSC 7 on its own, so poll the shell process for its
            // real current directory and mirror it in the pane header.
            _cwdTimer = _dispatcher.CreateTimer();
            _cwdTimer.Interval = TimeSpan.FromMilliseconds(300);
            _cwdTimer.Tick += (_, _) => Diag.Guard("pane.cwd", UpdateLocation);
        }

        ApplyFocusState();
    }

    public FrameworkElement Visual => _host;

    /// <summary>Icon of the shell this pane spawned, shared with the tab chip.</summary>
    public ImageSource? ShellIconSource => _shellIconSource;

    public CellMetrics CellMetricsDip => _renderer.CellMetrics;

    private static string DefaultWorkingDirectory(Config config)
    {
        if (!string.IsNullOrEmpty(config.WorkingDirectory)) return config.WorkingDirectory;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? profile : "~";
    }

    // ---------------- pane header ----------------

    private Border AddAction(Path icon, Func<string> tip, string shortcut, Action handler)
    {
        var button = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(6),
            Background = TransparentBrush(),
            Child = icon,
        };

        // Hover tooltip: the action's name and its keybinding, the same two-line shape
        // the tab chips use.
        var tooltip = new StackPanel { Spacing = 2 };
        var tipTitle = new TextBlock
        {
            Text = tip(),
            FontSize = 12,
            Foreground = new SolidColorBrush(Theme.TextStrong()),
        };
        var tipKey = new TextBlock
        {
            Text = shortcut,
            FontSize = 11,
            Foreground = new SolidColorBrush(Theme.TextFaint()),
            FontStyle = FontStyle.Italic,
        };
        tooltip.Children.Add(tipTitle);
        tooltip.Children.Add(tipKey);
        ToolTipService.SetToolTip(button, tooltip);

        // Hover colors are resolved from the current theme instead of captured brushes,
        // so a theme switch cannot leave stale colors behind.
        button.PointerEntered += (_, _) =>
        {
            button.Background = new SolidColorBrush(Theme.ChipHover());
            icon.Stroke = new SolidColorBrush(Theme.TextStrong());
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = TransparentBrush();
            icon.Stroke = new SolidColorBrush(Theme.TextFaint());
        };
        button.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            OnFocus?.Invoke(this);
            handler();
        };

        _actions!.Children.Add(button);
        _actionVisuals.Add(new ActionVisual
        {
            Icon = icon,
            Button = button,
            TipTitle = tipTitle,
            TipKey = tipKey,
            Tip = tip,
        });
        return button;
    }

    /// <summary>Re-applies the localized tooltips after a config reload changed the language.</summary>
    public void ApplyLanguage()
    {
        foreach (ActionVisual action in _actionVisuals)
            action.TipTitle.Text = action.Tip();
    }

    private void OnFramePointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(true);
    private void OnFramePointerMoved(object sender, PointerRoutedEventArgs e) => SetHover(true);

    private void OnFramePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_hovered) return;
        _hovered = false;
        _frame.BorderBrush = new SolidColorBrush(Theme.PaneStroke());
        ApplyFocusState();
    }

    // PointerEntered does not always reach the frame when the pointer first lands on the
    // canvas child, so movement counts as hover too.
    private void SetHover(bool value)
    {
        if (_hovered == value) return;
        _hovered = value;
        _frame.BorderBrush = new SolidColorBrush(value ? Theme.PaneStrokeHover() : Theme.PaneStroke());
        ApplyFocusState();

        if (value && _config.FocusFollowsMouse) OnFocus?.Invoke(this);
    }

    // ---------------- lifecycle ----------------

    public void Start()
    {
        if (_ready) return;

        _session.Post = action =>
        {
            if (_dispatcher is null) return;
            _dispatcher.TryEnqueue(() => action());
        };
        _session.OnUpdate = ScheduleRedraw;
        _session.OnTitle = value =>
        {
            if (string.IsNullOrEmpty(value)) return;
            // The console host sometimes reports its own executable path as the title;
            // that is not a useful tab title.
            string lower = value.ToLowerInvariant();
            if (lower.Length > 4 && lower.EndsWith(".exe", StringComparison.Ordinal) &&
                value.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                return;
            }
            Title = value;
            OnTitle?.Invoke(this);
        };
        _session.OnExit = () =>
        {
            // wait-after-command keeps the surface (and its scrollback) open after the shell exits.
            if (!_config.WaitAfterCommand) OnExit?.Invoke(this);
        };

        _ready = true;
        HookDraw();
        Layout();

        _session.Start(_config.Command, _config.WorkingDirectory, _cols, _rows, PhysicalCellMetrics(),
            _config.ExtraEnvironment, _config.TermVariable, _config.Input);
        _cwdTimer?.Start();
        UpdateShellIcon();
        UpdateLocation();
        Redraw();
    }

    public void Close()
    {
        if (!_ready && _session is null) return;
        _ready = false;
        _layoutSuspended = 0;

        _canvas.PointerWheelChanged -= OnPointerWheel;
        _canvas.PointerPressed -= OnPointerPressed;
        _canvas.PointerMoved -= OnPointerMoved;
        _canvas.PointerReleased -= OnPointerReleased;
        _canvas.DoubleTapped -= OnCanvasDoubleTapped;
        UnhookDraw();
        _renderer.NeedsRedraw -= Redraw;

        if (_hookedRoot is not null)
        {
            _hookedRoot.Changed -= OnXamlRootChanged;
            _hookedRoot = null;
        }

        _cwdTimer?.Stop();
        _cwdTimer = null;
        _blinkTimer?.Stop();
        _blinkTimer = null;

        _session.OnUpdate = null;
        _session.OnExit = null;
        _session.OnTitle = null;
        _session.Stop();
        _session.Dispose();
    }

    private void HookDraw()
    {
        if (_drawHooked) return;
        _drawHooked = true;
        _canvas.Draw += OnDraw;
    }

    private void UnhookDraw()
    {
        if (!_drawHooked) return;
        _drawHooked = false;
        _canvas.Draw -= OnDraw;
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (!_ready) return;
        // A throw here escapes into XAML's draw callback, which fail-fasts the process.
        Diag.Guard("pane.draw", () => _renderer.Render(args.DrawingSession, _session, _cursorOn, true,
            (float)sender.ActualWidth, (float)sender.ActualHeight));
    }

    // ---------------- layout ----------------

    private double RasterizationScale => _canvas.XamlRoot?.RasterizationScale ?? 1.0;

    private CellMetrics PhysicalCellMetrics()
    {
        CellMetrics metrics = _renderer.CellMetrics;
        double scale = RasterizationScale;
        return new CellMetrics(
            (uint)Math.Max(1, Math.Ceiling(metrics.Width * scale)),
            (uint)Math.Max(1, Math.Ceiling(metrics.Height * scale)));
    }

    /// <summary>
    /// Freezes the terminal's grid while an animation drives the pane's size. Each resize
    /// reflows the shell, so doing it once per frame during a 180 ms animation would cost
    /// dozens of PTY resizes and repaints; the layout runs once when the animation ends.
    /// </summary>
    public void SuspendLayout() => _layoutSuspended++;

    public void ResumeLayout()
    {
        if (_layoutSuspended == 0) return;
        if (--_layoutSuspended > 0) return;
        Layout();
        Redraw();
    }

    // ---------------- DPI changes ----------------

    /// <summary>
    /// Follows the display scale when the window moves to a monitor with a different DPI.
    /// XAML scales the visual automatically, but the glyphs and the terminal grid have to be
    /// re-measured for the new pixel density, exactly like Windows Terminal and WezTerm do.
    /// </summary>
    private void HookXamlRoot()
    {
        XamlRoot? root = _canvas.XamlRoot;
        if (ReferenceEquals(root, _hookedRoot)) return;

        if (_hookedRoot is not null) _hookedRoot.Changed -= OnXamlRootChanged;
        _hookedRoot = root;
        if (root is null) return;

        _appliedScale = root.RasterizationScale;
        root.Changed += OnXamlRootChanged;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        double scale = sender.RasterizationScale;
        if (Math.Abs(scale - _appliedScale) < 0.001) return;
        _appliedScale = scale;
        Diag.Guard("pane.dpi", ApplyDisplayScale);
    }

    private void ApplyDisplayScale()
    {
        if (!_ready) return;

        // Win2D reads the DPI from the XAML tree itself (CanvasControl.Dpi is read-only),
        // so only the glyphs and the grid need re-measuring here.
        // The pixel-based adjust-* values change the DIP cell size, so the grid may change
        // even though the window did not; force the resize through to the PTY.
        Layout(force: true);
        Redraw();
        Diag.Log("pane.dpi", $"{_appliedScale:0.##}x, {_cols}x{_rows}");
    }

    // ---------------- layout ----------------

    private void Layout(bool force = false)
    {
        if (!_ready || _layoutSuspended > 0) return;

        _renderer.SetRasterizationScale(RasterizationScale);
        _renderer.SetFontSize(_fontSizeDip);

        double width = _canvas.ActualWidth;
        double height = _canvas.ActualHeight;
        if (width < 2 || height < 2) return;

        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return;

        ushort cols = (ushort)Math.Max(2, (int)(width / metrics.Width));
        ushort rows = (ushort)Math.Max(2, (int)(height / metrics.Height));
        if (!force && cols == _cols && rows == _rows) return;

        _cols = cols;
        _rows = rows;
        _session.Resize(cols, rows, PhysicalCellMetrics());
        Redraw();
    }

    private void ScheduleRedraw()
    {
        if (_redrawPending) return;
        _redrawPending = true;
        if (_dispatcher is null)
        {
            _redrawPending = false;
            Redraw();
            return;
        }
        _dispatcher.TryEnqueue(() =>
        {
            _redrawPending = false;
            Redraw();
        });
    }

    public void Redraw()
    {
        if (!_ready) return;
        // Output, scrolling and resizes all end here, so this is where the thumb follows the
        // viewport.
        UpdateScrollbar();
        _canvas.Invalidate();
    }

    /// <summary>
    /// Keeps the header actions clear of something the window draws over the pane's top-right
    /// corner. The side layouts lift the island under the caption buttons, so the pane that
    /// owns that corner asks for the width they need.
    /// </summary>
    public void SetActionsInset(double inset)
    {
        if (_actions is null || Math.Abs(inset - _actionsInset) < 0.01) return;
        _actionsInset = inset;
        _actions.Margin = new Thickness(0, 0, 6 + inset, 0);
    }

    /// <summary>
    /// Same idea on the left: the side layouts can float the rail's own buttons over the
    /// pane's top-left corner, and the path must not run underneath them.
    /// </summary>
    public void SetInfoInset(double inset)
    {
        if (_info is null || Math.Abs(inset - _infoInset) < 0.01) return;
        _infoInset = inset;
        _info.Margin = new Thickness(10 + inset, 0, 0, 0);
    }

    // ---------------- focus ----------------

    public bool Focused => _focused;

    public void SetFocused(bool focused)
    {
        if (_focused == focused) return;
        _focused = focused;

        // Only the focused pane blinks. The unfocused caret stays solid, so the inactive
        // pane does not animate while the user works elsewhere.
        if (_blinkTimer is not null)
        {
            _cursorOn = true;
            if (focused) _blinkTimer.Start();
            else _blinkTimer.Stop();
        }
        Redraw();
        ApplyFocusState();
    }

    /// <summary>
    /// Focus treatment from the design: the unfocused terminal drops to a dim veil, its
    /// header path and icon dim, and its action buttons disappear.
    /// </summary>
    private void ApplyFocusState()
    {
        _dim.Opacity = _focused ? 0.0 : 1.0;
        _pathText.Foreground = new SolidColorBrush(_focused ? Theme.TextStrong() : Theme.TextFaint());
        _iconFallback.Foreground = new SolidColorBrush(_focused ? Theme.Accent() : Theme.TextFaint());
        _shellIcon.Opacity = _focused ? 1.0 : 0.6;

        if (_actions is not null)
        {
            // Hovering an unfocused pane also reveals its header actions.
            bool show = _focused || _hovered;
            _actions.Opacity = show ? 1.0 : 0.0;
            _actions.IsHitTestVisible = show;
        }
    }

    public void SetZoomed(bool zoomed)
    {
        if (_zoomIcon is null) return;
        _zoomIcon.Data = ZoomGeometry(zoomed, 12.0);
    }

    /// <summary>Zoom and close are disabled while the tab has a single pane.</summary>
    public void SetSinglePane(bool single)
    {
        foreach (Border? button in new[] { _zoomButton, _closeButton })
        {
            if (button is null) continue;
            button.Opacity = single ? 0.28 : 1.0;
            button.IsHitTestVisible = !single;
        }
    }

    // ---------------- font zoom ----------------

    /// <summary>One point per step, clamped to a range that still fits a pane.</summary>
    public void AdjustFontSize(float delta)
    {
        float next = Math.Clamp(_fontSizeDip + delta, 6f, 40f);
        if (next == _fontSizeDip) return;
        _fontSizeDip = next;
        Layout();
        Redraw();
    }

    /// <summary>Re-applies the configured font family and size after a config reload.</summary>
    public void ApplyFont()
    {
        _fontSizeDip = _config.FontSizeDip;
        _renderer.SetRasterizationScale(RasterizationScale);
        _renderer.ApplyConfig(_config);
        Layout();
        Redraw();
    }

    /// <summary>
    /// Re-applies the scroll settings after a config reload: the scrollback budget goes to the
    /// terminal, the visibility policy is read by UpdateScrollbar.
    /// </summary>
    public void ApplyScrollSettings()
    {
        _session.SetScrollbackLimit(_config.ScrollbackLimit);
        _session.SetScrollbackLimitLines(_config.ScrollbackLimitLines);
        UpdateScrollbar();
    }

    /// <summary>
    /// Compact mode drops the pane's own island chrome: no rounded corners and no outline,
    /// because the split dividers are the only lines left between panes.
    /// </summary>
    public void SetCompact(bool compact)
    {
        _frame.CornerRadius = new CornerRadius(compact ? 0 : 12);
        _frame.BorderThickness = new Thickness(compact ? 0 : 1);
    }

    /// <summary>Reset returns to the configured size.</summary>
    public void ResetFontSize()
    {
        float configured = _config.FontSizeDip;
        if (configured == _fontSizeDip) return;
        _fontSizeDip = configured;
        Layout();
        Redraw();
    }

    private Color DimFillColor()
    {
        Color color = Theme.TermSurfaceBg();
        if (_config.HasUnfocusedSplitFill)
        {
            color = Color.FromArgb(255, _config.UnfocusedSplitFill.R, _config.UnfocusedSplitFill.G,
                _config.UnfocusedSplitFill.B);
        }
        color.A = (byte)((1.0f - _config.UnfocusedSplitOpacity) * 255.0f + 0.5f);
        return color;
    }

    /// <summary>"background-opacity" as a 0..1 alpha for the pane surface.</summary>
    private float SurfaceOpacity => Math.Clamp(_config.BackgroundOpacity, 0f, 1f);

    /// <summary>
    /// The terminal surface with "background-opacity" applied. The frame paints it, which
    /// also covers the header and the padding around the canvas, so the whole pane keeps one
    /// uniform translucency.
    /// </summary>
    private Color SurfaceColor()
    {
        Color color = Theme.TermSurfaceBg();
        color.A = (byte)Math.Round(255 * SurfaceOpacity);
        return color;
    }

    /// <summary>
    /// The canvas clears itself to this before Draw. A translucent pane must start fully
    /// transparent so the frame's surface is not painted twice.
    /// </summary>
    private Color CanvasClearColor() =>
        SurfaceOpacity < 0.999f ? Color.FromArgb(0, 0, 0, 0) : Theme.TermSurfaceBg();

    // ---------------- theme ----------------

    /// <summary>
    /// Re-applies the global theme to this pane (frame, header, actions, tool tips) and
    /// pushes the new terminal palette into its session.
    /// </summary>
    public void ApplyTheme()
    {
        _frame.Background = new SolidColorBrush(SurfaceColor());
        _frame.BorderBrush = new SolidColorBrush(_hovered ? Theme.PaneStrokeHover() : Theme.PaneStroke());
        _dim.Background = new SolidColorBrush(DimFillColor());
        _canvas.ClearColor = CanvasClearColor();

        foreach (ActionVisual action in _actionVisuals)
        {
            action.Icon.Stroke = new SolidColorBrush(Theme.TextFaint());
            action.Button.Background = TransparentBrush();
            action.TipTitle.Foreground = new SolidColorBrush(Theme.TextStrong());
            action.TipKey.Foreground = new SolidColorBrush(Theme.TextFaint());
        }

        ApplyFocusState();

        _session.ApplyTheme(Theme.Terminal);
        // The shell icon is theme-independent, so a theme change leaves it untouched.
        Redraw();
    }

    private void UpdateShellIcon()
    {
        string executable = _session.ShellExecutable();
        if (executable == _shellIconPath) return;

        _shellIconPath = executable;
        _ = LoadShellIconAsync(executable);
    }

    private async Task LoadShellIconAsync(string executable)
    {
        ImageSource? source = await ShellIcon.GetAsync(executable);
        // Drop a late result for a shell that has since changed.
        if (!_ready || source is null || _session.ShellExecutable() != executable) return;

        _shellIconSource = source;
        _shellIcon.Source = source;
        _shellIcon.Visibility = Visibility.Visible;
        _iconFallback.Visibility = Visibility.Collapsed;
        OnShellIcon?.Invoke(this);
    }

    // ---------------- session inheritance ----------------

    /// <summary>Keep configured arguments intact; only auto-detected sessions fall back.</summary>
    public string ShellCommand()
    {
        if (!string.IsNullOrEmpty(_config.Command)) return _config.Command;
        return _session.ShellExecutable();
    }

    public string WorkingDirectory() =>
        _session.CurrentDirectory(out string directory) ? directory : _workingDirectory;

    private void UpdateLocation()
    {
        if (!_session.CurrentDirectory(out string directory)) return;
        if (directory == _workingDirectory) return;

        _workingDirectory = directory;
        _pathText.Text = directory;
    }

    /// <summary>
    /// Shows the IME composition at the cursor until it is committed (or cleared by passing
    /// an empty string).
    /// </summary>
    public void SetImeComposition(string text)
    {
        if (!_renderer.SetImeComposition(text)) return;
        _canvas.Invalidate();
    }

    /// <summary>Cursor origin in DIPs, relative to the pane's visual root.</summary>
    public bool CursorOrigin(out double x, out double y)
    {
        x = 0;
        y = 0;
        if (!_renderer.CursorCell(out ushort cellX, out ushort cellY)) return false;

        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return false;

        // Cell space starts after the frame's 1 DIP outline, the 34 DIP pane head and the
        // configured term padding (the host is the frame's root, so those offsets count).
        x = cellX * (double)metrics.Width + 1.0 + _config.PaddingX;
        y = cellY * (double)metrics.Height + 35.0 + _config.PaddingY;
        return true;
    }

    // ---------------- input ----------------

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
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= GhosttyNative.MODS_SUPER;
        return mods;
    }

    public bool KeyDown(KeyRoutedEventArgs args)
    {
        if (!_ready || _session is null) return false;

        int mods = CurrentMods();
        bool ctrl = (mods & GhosttyNative.MODS_CTRL) != 0;
        bool alt = (mods & GhosttyNative.MODS_ALT) != 0;

        int key = KeyMap.FromVirtualKey(args.Key);
        if (key == KeyMap.Unidentified) return false;

        if (KeyMap.IsTextKey(key) && !ctrl && !alt) return false;

        ClearSelectionOnTyping();

        string text = string.Empty;
        if (ctrl && !alt && key >= KeyMap.KeyA && key <= KeyMap.KeyZ)
            text = ((char)('a' + (key - KeyMap.KeyA))).ToString();

        _session.SendKey(key, mods, text.Length > 0 ? System.Text.Encoding.ASCII.GetBytes(text) : null);
        return true;
    }

    public void CommitText(string text)
    {
        if (!_ready || _session is null || text.Length == 0) return;
        ClearSelectionOnTyping();
        _session.WriteInput(System.Text.Encoding.UTF8.GetBytes(text));
    }

    private void ClearSelectionOnTyping()
    {
        if (!_config.SelectionClearOnTyping) return;
        if (!_session.HasSelection()) return;
        _session.ClearSelection();
        Redraw();
    }

    public void CopySelection()
    {
        byte[] text = _session.CopySelection();
        if (text.Length == 0) return;

        string value = System.Text.Encoding.UTF8.GetString(text);
        if (_config.ClipboardCodepointMap.Count > 0) value = _config.ApplyClipboardCodepointMap(value);
        if (_config.ClipboardTrimTrailingSpaces) value = TrimTrailingSpaces(value);

        try
        {
            var package = new DataPackage();
            package.SetText(value);
            Clipboard.SetContent(package);
        }
        catch
        {
            // Clipboard unavailable.
        }

        if (_config.SelectionClearOnCopy)
        {
            _session.ClearSelection();
            Redraw();
        }
    }

    /// <summary>Trims the whitespace that ends every line, leaving leading indentation alone.</summary>
    private static string TrimTrailingSpaces(string value)
    {
        string[] lines = value.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd(' ', '\t', '\r');
        return string.Join("\n", lines);
    }

    public void PasteFromClipboard()
    {
        _ = PasteAsync();
    }

    private async Task PasteAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return;

            string text = await view.GetTextAsync();
            if (text.Length == 0) return;

            // ConPTY expects bare newlines; CRLF would submit twice.
            var normalized = new System.Text.StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;
                normalized.Append(text[i]);
            }
            _session.PasteText(System.Text.Encoding.UTF8.GetBytes(normalized.ToString()));
        }
        catch
        {
            // Clipboard unavailable.
        }
    }

    private bool CellAt(Point point, out ushort x, out ushort y)
    {
        x = 0;
        y = 0;
        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return false;

        int cx = Math.Clamp((int)(point.X / metrics.Width), 0, Math.Max(0, _cols - 1));
        int cy = Math.Clamp((int)(point.Y / metrics.Height), 0, Math.Max(0, _rows - 1));
        x = (ushort)cx;
        y = (ushort)cy;
        return true;
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs args)
    {
        if (!_ready) return;
        int delta = args.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        if (delta == 0) return;

        // Three rows per notch is the app's historical step; mouse-scroll-multiplier scales it.
        int step = Math.Max(1, (int)Math.Round(3.0 * _config.MouseScrollMultiplier));
        _session.Scroll(delta > 0 ? -step : step);
        args.Handled = true;
    }

    /// <summary>
    /// Starts a selection for a press that landed on the IME sink. The sink is a sibling of
    /// the shell in the window's visual tree, so its press never bubbles to this canvas.
    /// Capturing here makes the normal move/release handlers take over the drag.
    /// </summary>
    public void BeginSelectionFromImeSink(PointerRoutedEventArgs args) => OnPointerPressed(_canvas, args);

    /// <summary>
    /// The IME sink keeps the pointer capture (a canvas that never received the press cannot
    /// take it), so move and release keep arriving at the sink and have to be forwarded too.
    /// The handlers below are no-ops unless a selection is in progress.
    /// </summary>
    public void MoveSelectionFromImeSink(PointerRoutedEventArgs args) => OnPointerMoved(_canvas, args);

    public void EndSelectionFromImeSink(PointerRoutedEventArgs args) => OnPointerReleased(_canvas, args);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_ready) return;

        OnFocus?.Invoke(this);

        PointerPoint point = args.GetCurrentPoint(_canvas);
        if (!CellAt(point.Position, out ushort x, out ushort y)) return;

        if (point.Properties.IsRightButtonPressed)
        {
            args.Handled = true;
            switch (_config.RightClickAction)
            {
                case "ignore":
                    break;
                case "copy":
                    CopySelection();
                    break;
                case "paste":
                    PasteFromClipboard();
                    break;
                default:
                    // context-menu has no menu in this app, so it keeps the familiar
                    // copy-or-paste behavior; copy-or-paste asks for it explicitly.
                    if (_session.HasSelection()) CopySelection();
                    else PasteFromClipboard();
                    break;
            }
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _selecting = true;
        _anchorX = x;
        _anchorY = y;
        _canvas.CapturePointer(args.Pointer);
        _session.ClearSelection();
        Redraw();
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_selecting) return;

        if (!CellAt(args.GetCurrentPoint(_canvas).Position, out ushort x, out ushort y)) return;
        if (x == _anchorX && y == _anchorY) return;

        if (_session.SetSelection(_anchorX, _anchorY, x, y)) Redraw();
        args.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_selecting) return;
        _selecting = false;
        _canvas.ReleasePointerCapture(args.Pointer);
        args.Handled = true;

        if (_config.CopyOnSelect && _session.HasSelection()) CopySelection();
    }

    /// <summary>
    /// Double-click selects the word under the pointer. "selection-word-chars" lists the
    /// characters that end a word; blank cells and whitespace always end one.
    /// </summary>
    private void OnCanvasDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (!_ready) return;
        if (!CellAt(args.GetPosition(_canvas), out ushort x, out ushort y)) return;

        // The last frame may have painted before the most recent output arrived.
        _session.UpdateRenderState();
        string[]? row = _session.RowCells(y);
        if (row is null) return;

        args.Handled = true;

        if (!WordSelection.TryExpand(row, x, _config.SelectionWordChars, out int start, out int end))
        {
            _session.ClearSelection();
            Redraw();
            return;
        }

        _anchorX = (ushort)start;
        _anchorY = y;
        if (!_session.SetSelection((ushort)start, y, (ushort)end, y)) return;

        Redraw();
        if (_config.CopyOnSelect) CopySelection();
    }

    // ---------------- scrollbar ----------------

    /// <summary>
    /// Feeds the terminal's scroll state into the bar. The control already sizes the thumb
    /// from ViewportSize, so the terminal's rows map one to one: the value range is the rows
    /// the viewport can still move through, and the page step is one viewport.
    /// </summary>
    private void UpdateScrollbar()
    {
        ulong total = 0, offset = 0, len = 0;
        bool show = false;

        if (_ready && _session.Scrollbar(out total, out offset, out len) && len > 0)
        {
            // "never" hides the bar outright; the default shows it exactly while the
            // scrollback does not fit the viewport.
            if (_config.Scrollbar != "never" && total > len)
            {
                ulong last = total - len;
                _updatingScrollbar = true;
                try
                {
                    _scrollBar.Minimum = 0;
                    _scrollBar.Maximum = last;
                    _scrollBar.ViewportSize = len;
                    _scrollBar.SmallChange = 1;
                    _scrollBar.LargeChange = len;
                    _scrollBar.Value = Math.Min(offset, last);
                }
                finally
                {
                    _updatingScrollbar = false;
                }
                show = true;
            }
        }

        _scrollBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- pane header icons ----------------

    private static SolidColorBrush TransparentBrush() => new(Color.FromArgb(0, 0, 0, 0));

    private static Path StyledIcon(Brush stroke, double size)
    {
        return new Path
        {
            Width = size,
            Height = size,
            Stroke = stroke,
            StrokeThickness = size * 1.5 / 16.0,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };
    }

    private static Point IconPoint(float x, float y, double scale) =>
        new(x * scale, y * scale);

    private static PathGeometry PolylineGeometry(IReadOnlyList<float[,]> figures, double size)
    {
        double scale = size / 16.0;
        var geometry = new PathGeometry();
        foreach (float[,] polyline in figures)
        {
            var figure = new PathFigure
            {
                IsClosed = false,
                StartPoint = IconPoint(polyline[0, 0], polyline[0, 1], scale),
            };
            for (int i = 1; i < polyline.GetLength(0); i++)
                figure.Segments.Add(new LineSegment { Point = IconPoint(polyline[i, 0], polyline[i, 1], scale) });
            geometry.Figures.Add(figure);
        }
        return geometry;
    }

    private static PathGeometry SplitGeometry(bool vertical, double size)
    {
        double scale = size / 16.0;
        var geometry = new PathGeometry();
        var rect = new PathFigure { IsClosed = true, StartPoint = IconPoint(4, 3, scale) };
        void Line(float x, float y) => rect.Segments.Add(new LineSegment { Point = IconPoint(x, y, scale) });
        void Arc(float x, float y)
        {
            rect.Segments.Add(new ArcSegment
            {
                Size = new Size(2 * scale, 2 * scale),
                Point = IconPoint(x, y, scale),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false,
            });
        }
        Line(12, 3);
        Arc(14, 5);
        Line(14, 11);
        Arc(12, 13);
        Line(4, 13);
        Arc(2, 11);
        Line(2, 5);
        Arc(4, 3);
        geometry.Figures.Add(rect);

        var divider = new PathFigure();
        if (vertical)
        {
            divider.StartPoint = IconPoint(8, 3, scale);
            divider.Segments.Add(new LineSegment { Point = IconPoint(8, 13, scale) });
        }
        else
        {
            divider.StartPoint = IconPoint(2, 8, scale);
            divider.Segments.Add(new LineSegment { Point = IconPoint(14, 8, scale) });
        }
        geometry.Figures.Add(divider);
        return geometry;
    }

    private static PathGeometry ZoomGeometry(bool zoomed, double size)
    {
        float[][,] figures = zoomed
            ? new[]
            {
                new float[,] { { 13.5f, 2.5f }, { 9.0f, 7.0f } },
                new float[,] { { 9.0f, 7.0f }, { 12.0f, 7.0f } },
                new float[,] { { 9.0f, 7.0f }, { 9.0f, 4.0f } },
                new float[,] { { 2.5f, 13.5f }, { 7.0f, 9.0f } },
                new float[,] { { 7.0f, 9.0f }, { 4.0f, 9.0f } },
                new float[,] { { 7.0f, 9.0f }, { 7.0f, 12.0f } },
            }
            : new[]
            {
                new float[,] { { 9.5f, 2.5f }, { 14.0f, 2.5f }, { 14.0f, 7.0f } },
                new float[,] { { 13.5f, 2.5f }, { 9.0f, 7.0f } },
                new float[,] { { 6.5f, 13.5f }, { 2.0f, 13.5f }, { 2.0f, 9.0f } },
                new float[,] { { 2.5f, 13.5f }, { 7.0f, 9.0f } },
            };
        return PolylineGeometry(figures, size);
    }

    private static PathGeometry CloseGeometry(double size) =>
        PolylineGeometry(new[]
        {
            new float[,] { { 4.4f, 4.4f }, { 11.6f, 11.6f } },
            new float[,] { { 11.6f, 4.4f }, { 4.4f, 11.6f } },
        }, size);

    private static Path SplitIcon(bool vertical, Brush stroke, double size)
    {
        Path path = StyledIcon(stroke, size);
        path.Data = SplitGeometry(vertical, size);
        return path;
    }

    private static Path CloseIcon(Brush stroke, double size)
    {
        Path path = StyledIcon(stroke, size);
        path.Data = CloseGeometry(size);
        return path;
    }

}
