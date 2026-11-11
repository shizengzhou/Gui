using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
/// ConPTY session and renderer. The workspace owns focus and the keyboard anchor, so keys
/// and committed text are pushed in for the focused pane only.
/// </summary>
public sealed class TerminalPane
{
    private readonly Config _config;
    private float _fontSizeDip = 13.0f;
    private TerminalSession _session;
    private readonly TerminalRenderer _renderer = new();

    /// <summary>Divider written above the new shell when this pane came from a snapshot.</summary>
    private readonly string? _restoreBanner;

    /// <summary>
    /// Session file this pane was restored from. A pane in a background tab has not started
    /// (and has no terminal content yet), so a save reuses this file instead of losing it.
    /// </summary>
    public string? RestoredSnapshotName { get; }

    private readonly ContentControl _host;
    private readonly Border _frame;
    private readonly CanvasControl _canvas;
    private readonly Border _dim;
    private readonly ScrollBar _scrollBar;
    private readonly TextBlock _pathText;
    private readonly FontIcon _iconFallback;
    private readonly Image _shellIcon;

    /// <summary>OSC 7501 status dot on the header's shell icon, matching the tab chip.</summary>
    private Ellipse? _iconStatusDot;
    private Path? _zoomIcon;
    private Border? _zoomButton;
    private Border? _closeButton;
    private StackPanel? _actions;
    private double _actionsInset = -1;
    private Grid? _info;
    private double _infoInset = -1;
    private Grid? _iconSlot;
    private DragHeader? _headerBar;
    private bool _dragEnabled;
    private bool _handlePressed;
    private bool _handleDragging;
    private Point _handlePressPoint;
    private readonly List<ActionVisual> _actionVisuals = new();
    private Keybinds _keybinds = null!;

    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _blinkTimer;
    private DispatcherQueueTimer? _cwdTimer;

    private string _workingDirectory;
    private string _shellIconPath = string.Empty;
    private ImageSource? _shellIconSource;
    private bool _cursorOn = true;
    private bool _redrawPending;
    private bool _loaded;
    private bool _ready;

    /// <summary>True once the session was handed to another pane (a tab that moved windows).</summary>
    private bool _sessionReleased;

    /// <summary>True once the pane stopped its shell; Close is idempotent.</summary>
    private bool _closed;
    private bool _focused;
    private bool _hovered;
    private bool _selecting;
    private bool _updatingScrollbar;
    private bool _mouseReportedPress;
    private bool _anyMouseButton;
    private bool _cursorHidden;
    private string? _hoverUrl;
    private bool _hoverOsc8;
    private Point _hoverPoint;
    private ushort _hoverX;
    private ushort _hoverY;
    private bool _hoverHadCtrl;
    private ulong _lastClickTime;
    private ushort _lastClickX;
    private ushort _lastClickY;
    private int _clickCount;
    private string _baseTitle = string.Empty;
    private bool _bellTitle;
    private bool _bellBorder;
    private Border? _linkPreview;
    private TextBlock? _linkPreviewText;
    private Border? _searchBar;
    private Border? _searchField;
    private TextBox? _searchBox;
    private TextBlock? _searchCount;
    private SolidColorBrush? _searchGlyphBrush;
    private SolidColorBrush? _searchGlyphHoverBrush;
    private DispatcherQueueTimer? _searchTimer;
    private bool _searchActive;
    private bool _searchSelectPending;
    private bool _searchComposing;
    private string _searchQuery = string.Empty;
    private static Windows.Media.Playback.MediaPlayer? _bellPlayer;

    private static readonly Regex UrlPattern = new(
        @"\b(?:https?://|www\.)[^\s""'<>]+", RegexOptions.Compiled);
    private int _layoutSuspended;
    private bool _drawHooked;
    private XamlRoot? _hookedRoot;
    private double _appliedScale = 1.0;
    private ushort _anchorX;
    private ushort _anchorY;
    private uint _anchorScreenY;
    private DispatcherQueueTimer? _selectScrollTimer;
    private int _selectScrollDirection; // -1: toward older rows, +1: toward newer, 0: idle
    private double _selectPointerX;
    private double _selectPointerY;
    private ushort _cols = 80;
    private ushort _rows = 24;

    // "progress-style": OSC 9;4 progress drawn across the top of the terminal surface.
    private readonly ProgressBar _progressBar;
    private int _progressState;   // 0 none, 1 normal, 2 error, 3 indeterminate, 4 paused
    private int _progressValue;

    // OSC 7501: what the running program says it is doing, shown in the header.
    private StackPanel? _statusBadge;
    private FontIcon? _statusIcon;
    private TextBlock? _statusText;

    public string Title = string.Empty;

    /// <summary>
    /// A title the user pinned by hand ("Change Terminal Title…"). While it is set the
    /// shell's own title reports no longer replace it.
    /// </summary>
    private string? _titleOverride;

    /// <summary>True while Shift+arrows extend a keyboard selection.</summary>
    private bool _keyboardSelecting;
    private ushort _selectAnchorX;
    private uint _selectAnchorY;
    private ushort _selectEndX;
    private uint _selectEndY;

    /// <summary>True blocks keyboard input, IME text and pasting ("Toggle Read-Only Mode").</summary>
    public bool ReadOnly;

    /// <summary>False stops forwarding pointer input to a mouse-tracking program.</summary>
    public bool MouseReportingEnabled = true;

    public Action<TerminalPane>? OnTitle;
    public Action<TerminalPane>? OnExit;
    public Action<TerminalPane>? OnFocus;
    public Action<TerminalPane>? OnSplitRight;
    public Action<TerminalPane>? OnSplitDown;
    public Action<TerminalPane>? OnSplitLeft;
    public Action<TerminalPane>? OnSplitUp;
    public Action<TerminalPane>? OnMaximize;
    public Action<TerminalPane>? OnClosePane;
    public Action<TerminalPane>? OnShellIcon;
    public Action<TerminalPane>? OnBellAttention;
    public Action<TerminalPane, int, TimeSpan>? OnCommandFinished;
    public Action<TerminalPane>? OnSearchFocusReleased;

    /// <summary>CSI 8 t resize request ("vt-window-resize-allowed"): rows, then columns.</summary>
    public Action<TerminalPane, int, int>? OnResizeWindow;

    /// <summary>OSC 9 / OSC 777 desktop notification ("desktop-notifications"): title, body.</summary>
    public Action<TerminalPane, string, string>? OnDesktopNotification;

    // Split drag and drop ("drag-handle"): the pane hands the pointer to the workspace,
    // which owns the split tree and the drop overlay. Points are in this pane's visual.
    public Action<TerminalPane>? OnDragHandleStart;
    public Action<TerminalPane, Point>? OnDragHandleMove;
    public Action<TerminalPane>? OnDragHandleDrop;
    public Action<TerminalPane>? OnDragHandleCancel;

    private sealed class ActionVisual
    {
        public Path Icon = null!;
        public Border Button = null!;
        public TextBlock TipTitle = null!;
        public TextBlock TipKey = null!;
        public Func<string> Tip = () => string.Empty;
        public string? ActionId;
        public string FallbackShortcut = string.Empty;
    }

    /// <summary>
    /// The pane header doubles as the split drag handle. WinUI keeps the cursor behind a
    /// protected member of UIElement, so the band has to be a small subclass to show the
    /// move cursor while (and only while) dragging is enabled.
    /// </summary>
    private sealed class DragHeader : Grid
    {
        private static readonly InputCursor MoveCursor =
            InputSystemCursor.Create(InputSystemCursorShape.SizeAll);

        private static readonly InputCursor ArrowCursor =
            InputSystemCursor.Create(InputSystemCursorShape.Arrow);

        private bool _move;

        public DragHeader() => ProtectedCursor = ArrowCursor;

        public void ShowMoveCursor(bool move)
        {
            if (_move == move) return;
            _move = move;
            ProtectedCursor = move ? MoveCursor : ArrowCursor;
        }
    }

    /// <summary>Keeps the arrow over the header actions instead of the band's move cursor.</summary>
    private sealed class CursorGrid : Grid
    {
        public CursorGrid(InputSystemCursorShape shape) =>
            ProtectedCursor = InputSystemCursor.Create(shape);
    }

    /// <summary>
    /// Refreshes this pane's config snapshot from the live config so a reload also reaches
    /// panes created by a split. The running session keeps its command and directory.
    /// </summary>
    internal void SyncConfig(Config config)
    {
        // The first pane shares the live Config instance; copying it onto itself would clear
        // the raw values. Split panes keep their own session command and directory.
        if (ReferenceEquals(config, _config)) return;

        string command = _config.Command;
        string workingDirectory = _config.WorkingDirectory;
        _config.CopyFrom(config);
        _config.Command = command;
        _config.WorkingDirectory = workingDirectory;
        UpdateProgressBar();
    }

    public TerminalPane(Config config, byte[]? snapshot = null, string? restoreBanner = null,
        string? restoredSnapshot = null)
    {
        _config = config;
        _keybinds = new Keybinds(config);
        _restoreBanner = restoreBanner;
        RestoredSnapshotName = restoredSnapshot;
        if (config.FontSize > 0) _fontSizeDip = config.FontSizeDip;

        _session = new TerminalSession(snapshot);
        _session.SetScrollbackLimit(config.ScrollbackLimit);
        _session.SetScrollbackLimitLines(config.ScrollbackLimitLines);
        _session.SetTitleReport(config.TitleReport);
        _session.SetEnquiryResponse(config.EnquiryResponse);
        _session.SetOscColorReportFormat(config.OscColorReportFormat);
        _session.SetXtChecksumExtension(config.XtChecksumExtension);
        _session.SetXtChecksumReport(config.XtChecksumReport);
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _canvas = new CanvasControl
        {
            UseSharedDevice = true,
            ClearColor = CanvasClearColor(),
        };
        // XamlRoot is not attached yet, so this first pass uses scale 1. Layout recomputes
        // the margins through BalancePadding as soon as the pane is ready.
        _canvas.Margin = new Thickness(PadLeft, PadTop, PadRight, PadBottom);

        _dim = new Border
        {
            Margin = _canvas.Margin,
            Background = new SolidColorBrush(DimFillColor()),
            IsHitTestVisible = false,
            Opacity = 0.0,
        };

        // "link-previews": a compact URL badge that follows the pointer while a link is
        // hovered. It never takes input, so the Ctrl+click underneath keeps working.
        _linkPreviewText = new TextBlock
        {
            FontSize = 12,
            Foreground = new SolidColorBrush(Theme.TextStrong()),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            MaxWidth = 420,
        };
        _linkPreview = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush(Theme.PaletteBg()),
            BorderBrush = new SolidColorBrush(Theme.Stroke()),
            BorderThickness = new Thickness(1),
            Child = _linkPreviewText,
        };

        // "search": a compact find bar over the top-right of the surface.
        _searchBox = new TextBox
        {
            MinWidth = 90,
            Height = 26,
            MinHeight = 26,
            FontSize = 12,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            Foreground = new SolidColorBrush(Theme.TextStrong()),
            PlaceholderForeground = new SolidColorBrush(Theme.TextFaint()),
            PlaceholderText = Strings.FindPlaceholder,
            // The TextBox template draws its line at the top of the content box; symmetric
            // padding keeps the text and caret on the bar's center line. The right inset is
            // widened only while the inline match counter has text.
            Padding = new Thickness(8, 5, 10, 5),
            // Match the 24 DIP navigation buttons on the same row; the TextBox template's
            // default height is taller and would leave the text sitting at the top.
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = false,
        };
        // The stock template paints a white field and an accent underline on focus; the
        // find bar keeps the palette's flat look.
        _searchBox.Resources["TextControlBackground"] = TransparentBrush();
        _searchBox.Resources["TextControlBackgroundFocused"] = TransparentBrush();
        _searchBox.Resources["TextControlBackgroundPointerOver"] = TransparentBrush();
        _searchBox.Resources["TextControlBackgroundPressed"] = TransparentBrush();
        _searchBox.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _searchBox.Resources["TextControlBorderBrushFocused"] = TransparentBrush();
        _searchBox.Loaded += (_, _) => HideSearchClearButton();
        _searchBox.TextChanged += (_, _) =>
        {
            // The counter owns the field's trailing edge; the template's own clear button
            // would sit on top of it and is hidden after every text change.
            HideSearchClearButtonSoon();
            // IME composition text is pinyin, not the query; wait for the commit.
            if (!_searchActive || _searchComposing) return;
            _searchQuery = _searchBox.Text;
            RestartSearch();
            UpdateSearchFieldPadding();
        };
        _searchBox.TextCompositionStarted += (_, _) => _searchComposing = true;
        _searchBox.TextCompositionEnded += (_, _) =>
        {
            _searchComposing = false;
            if (!_searchActive) return;
            _searchQuery = _searchBox.Text;
            RestartSearch();
            UpdateSearchFieldPadding();
        };
        _searchBox.KeyDown += OnSearchKeyDown;
        // Like Ghostty, the overlay is not dismissed just because focus moved: it stays
        // visible with its results until Esc on an empty query or the close button.

        _searchCount = new TextBlock
        {
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Theme.TextFaint()),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false,
        };

        // macOS-style field: the counter is pinned to the trailing edge, overlaying the
        // TextBox's reserved padding.
        var searchFieldGrid = new Grid { Height = 26, VerticalAlignment = VerticalAlignment.Center };
        searchFieldGrid.Children.Add(_searchBox);
        searchFieldGrid.Children.Add(_searchCount);

        // macOS gives the text field a subtle filled surface inside the floating bar.
        _searchField = new Border
        {
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Theme.ChipHover()),
            Child = searchFieldGrid,
        };

        var searchRow = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            ColumnSpacing = 4,
        };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 3; i++)
            searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_searchField, 0);
        searchRow.Children.Add(_searchField);

        Border previous = SearchButton('\uE70E', Strings.SearchPrevious, () => NavigateSearch(false));
        Grid.SetColumn(previous, 1);
        searchRow.Children.Add(previous);

        Border next = SearchButton('\uE70D', Strings.SearchNext, () => NavigateSearch(true));
        Grid.SetColumn(next, 2);
        searchRow.Children.Add(next);

        Border close = SearchButton('\uE711', Strings.SearchClose, HideSearch);
        Grid.SetColumn(close, 3);
        searchRow.Children.Add(close);

        _searchBar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 40, 0),
            Padding = new Thickness(8, 5, 3, 5),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Theme.PaletteTint()),
            BorderBrush = new SolidColorBrush(Theme.Stroke()),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            Child = searchRow,
        };

        // Scrollbar: the stock WinUI control, as an overlay flush with the pane's right edge.
        // With padding this small there is no gutter to sit in, so it must not take layout
        // width: appearing would otherwise shrink the canvas, change the column count and
        // reflow the shell on every scroll. The resting indicator is a thin line that fits
        // inside the right padding; it only widens over the last cells while hovered, like
        // Windows Terminal. It owns the thumb sizing, the drag, the page clicks and the hover
        // treatment; this class only maps its Value to a scrollback row.
        _scrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            // Track the band the canvas draws its rows in; zero on the right so the thin
            // resting indicator sits inside the right padding rather than over a cell.
            Margin = new Thickness(0, PadTop, 0, PadBottom),
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
            if (_searchActive) _searchTimer?.Start();
        };
        // The control does not take the wheel itself, so scrolling over the bar has to reach
        // the terminal the same way it does over the surface.
        _scrollBar.PointerWheelChanged += OnPointerWheel;
        _scrollBar.SizeChanged += (_, _) => UpdateScrollbar();

        // "progress-style": a hairline across the top of the surface, above the first row,
        // inside the pane's padding. Ghostty draws its OSC 9;4 report in the same place.
        _progressBar = new ProgressBar
        {
            Height = 2,
            Minimum = 0,
            Maximum = 100,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = TransparentBrush(),
            Foreground = new SolidColorBrush(Theme.Accent()),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        var faint = new SolidColorBrush(Theme.TextFaint());
        // Action glyphs stay neutral whatever terminal theme is selected; only the surface
        // brightness decides between the dark and light grey.
        var iconFaint = new SolidColorBrush(Theme.PaneIconFaint());
        // Header row: cwd on the left, pane actions on the right. It lives inside the same
        // frame as the surface so focus and rounded corners stay unified.
        var header = new DragHeader { Height = 32 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new Grid { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _info = info;
        _infoInset = 0.0;
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconSlot = new Grid { Width = 16, Height = 16 };
        _iconSlot = iconSlot;
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

        // Same OSC 7501 dot that the tab chip draws on this icon: the pane header keeps the
        // shell icon and cwd, so the program's state belongs on that corner rather than in
        // another badge.
        _iconStatusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -1, -1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        iconSlot.Children.Add(_iconStatusDot);
        info.Children.Add(iconSlot);

        _workingDirectory = DefaultWorkingDirectory(config);
        _pathText = new TextBlock
        {
            Text = _workingDirectory,
            FontSize = 11.5,
            Foreground = faint,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            // Left instead of stretch: the block then measures to the visible text, which is
            // what the split drag area follows (a stretched block would claim the whole band).
            HorizontalAlignment = HorizontalAlignment.Left,
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

        AddAction(SplitIcon(true, iconFaint, 12.0), () => Strings.SplitRight, "new_split:right", "alt+shift+plus",
            () => OnSplitRight?.Invoke(this));
        AddAction(SplitIcon(false, iconFaint, 12.0), () => Strings.SplitDown, "new_split:down", "alt+shift+-",
            () => OnSplitDown?.Invoke(this));

        _zoomIcon = StyledIcon(iconFaint, 12.0);
        _zoomIcon.Data = ZoomGeometry(false, 12.0);
        _zoomButton = AddAction(_zoomIcon, () => Strings.ToggleZoom, "toggle_split_zoom", "ctrl+shift+enter",
            () => OnMaximize?.Invoke(this));
        _closeButton = AddAction(CloseIcon(iconFaint, 12.0), () => Strings.ClosePane, "close_surface", "ctrl+shift+w",
            () => OnClosePane?.Invoke(this));

        // The action buttons keep the arrow cursor: the header's move cursor stops at this
        // host, which covers the whole actions area.
        var actionHost = new CursorGrid(InputSystemCursorShape.Arrow)
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        actionHost.Children.Add(_actions);

        // OSC 7501: the program's own account of what it is doing, left of the actions so
        // it cannot push them off the header. Hidden until a report arrives.
        var statusBadge = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _statusBadge = statusBadge;
        _statusIcon = new FontIcon
        {
            Glyph = "\uE73E",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = faint,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _statusText = new TextBlock
        {
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = faint,
            MaxWidth = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        statusBadge.Children.Add(_statusIcon);
        statusBadge.Children.Add(_statusText);

        var rightHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightHost.Children.Add(statusBadge);
        rightHost.Children.Add(actionHost);
        Grid.SetColumn(rightHost, 1);
        header.Children.Add(rightHost);

        // Ghostty's split handle is the split's own title bar: grabbing the shell icon or
        // the working directory drags the pane. Only that content is the grab area, so it
        // tracks the text instead of spanning the whole band, and the rest of the header
        // keeps the plain arrow.
        _headerBar = header;
        header.Background = TransparentBrush();
        header.PointerPressed += OnHeaderDragPressed;
        header.PointerMoved += OnHeaderDragMoved;
        header.PointerReleased += OnHeaderDragReleased;
        header.PointerExited += (_, _) =>
        {
            if (_handlePressed) return;
            _headerBar?.ShowMoveCursor(false);
        };
        header.PointerCaptureLost += (_, _) =>
        {
            _handlePressed = false;
            if (!_handleDragging) return;
            _handleDragging = false;
            OnDragHandleCancel?.Invoke(this);
        };

        var frameRoot = new Grid();
        frameRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        frameRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        frameRoot.Children.Add(header);
        Grid.SetRow(_canvas, 1);
        frameRoot.Children.Add(_canvas);
        Grid.SetRow(_scrollBar, 1);
        frameRoot.Children.Add(_scrollBar);
        Grid.SetRow(_progressBar, 1);
        frameRoot.Children.Add(_progressBar);
        Grid.SetRow(_dim, 1);
        frameRoot.Children.Add(_dim);
        Grid.SetRow(_linkPreview, 1);
        frameRoot.Children.Add(_linkPreview);
        Grid.SetRow(_searchBar, 1);
        frameRoot.Children.Add(_searchBar);

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
        _canvas.PointerCaptureLost += (_, _) =>
        {
            _selecting = false;
            StopSelectionScroll();
        };
        _canvas.PointerExited += (_, _) =>
        {
            SetHoverUrl(null);
            ShowPointer();
        };
        _canvas.SizeChanged += (_, _) => Diag.Guard("pane.layout", () =>
        {
            // Starting the shell before the canvas has its real size creates the
            // pseudoconsole at the 80x24 seed. The later resize can lose the race with
            // the shell's console initialization, which leaves the session at 80 columns
            // and a blank band on the right.
            if (_loaded && !_ready) TryStart();
            else Layout();
        });
        _canvas.Loaded += (_, _) => Diag.Guard("pane.start", () =>
        {
            HookXamlRoot();
            _loaded = true;
            TryStart();
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

            _searchTimer = _dispatcher.CreateTimer();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(25);
            _searchTimer.Tick += (_, _) => Diag.Guard("pane.search", StepSearch);

            // The injected PowerShell shell integration reports its location over OSC 7
            // (see Term/ShellIntegration.cs); cmd.exe has no such report, so the shell
            // process itself is polled as the fallback and mirrored in the header.
            _cwdTimer = _dispatcher.CreateTimer();
            _cwdTimer.Interval = TimeSpan.FromMilliseconds(300);
            _cwdTimer.Tick += (_, _) => Diag.Guard("pane.cwd", UpdateLocation);

            // "selection auto-scroll": while the left button drags past the top or bottom
            // edge, the viewport follows so the selection can extend into the scrollback.
            _selectScrollTimer = _dispatcher.CreateTimer();
            _selectScrollTimer.Interval = TimeSpan.FromMilliseconds(40);
            _selectScrollTimer.Tick += (_, _) => Diag.Guard("pane.selectscroll", StepSelectionScroll);
        }

        ApplyFocusState();

        // A background tab's shell is started only when the tab is opened, but its chip still
        // needs the shell icon, so resolve it from the configured command right away.
        UpdateShellIcon();
    }

    public FrameworkElement Visual => _host;

    /// <summary>Icon of the shell this pane spawned, shared with the tab chip.</summary>
    public ImageSource? ShellIconSource =>
        _shellIconSource ?? (_shellIconPath.Length > 0 ? ShellIcon.TryGetCached(_shellIconPath) : null);

    public CellMetrics CellMetricsDip => _renderer.CellMetrics;
    public int Columns => _cols;
    public int Rows => _rows;

    /// <summary>True once a real layout pass has produced a grid, not the 80x24 seed.</summary>
    public bool LaidOut { get; private set; }

    private static string DefaultWorkingDirectory(Config config)
    {
        if (!string.IsNullOrEmpty(config.WorkingDirectory)) return config.WorkingDirectory;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? profile : "~";
    }

    // ---------------- pane header ----------------

    /// <summary>
    /// Turns the header into a grab bar (or not). The workspace decides, so the policy lives
    /// with the "drag-handle" option and the tab's split count; with dragging off the header
    /// behaves like ordinary chrome again.
    /// </summary>
    public void SetDragEnabled(bool enabled)
    {
        _dragEnabled = enabled;
        if (!enabled) _headerBar?.ShowMoveCursor(false);
    }

    /// <summary>
    /// The grab area is the icon and the cwd text, not the whole header band: the text
    /// block measures to its visible glyphs, so a longer path widens it and a shorter one
    /// shrinks it. A little padding makes the thin glyph line easier to hit.
    /// </summary>
    private bool IsOverDragContent(Point position)
    {
        if (_headerBar is null || _pathText is null || _iconSlot is null) return false;

        Rect icon = DragBounds(_iconSlot);
        Rect text = DragBounds(_pathText);
        double left = Math.Min(icon.X, text.X) - 4.0;
        double top = Math.Min(icon.Y, text.Y) - 4.0;
        double right = Math.Max(icon.Right, text.Right) + 4.0;
        double bottom = Math.Max(icon.Bottom, text.Bottom) + 4.0;
        return new Rect(left, top, Math.Max(0.0, right - left), Math.Max(0.0, bottom - top))
            .Contains(position);
    }

    private Rect DragBounds(FrameworkElement element)
    {
        Point origin = element.TransformToVisual(_headerBar!).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private void ShowDragCursor(Point position) =>
        _headerBar?.ShowMoveCursor(_dragEnabled && IsOverDragContent(position));

    private void OnHeaderDragPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_dragEnabled || _headerBar is null) return;

        PointerPoint point = args.GetCurrentPoint(_headerBar);
        // Right and middle presses belong to the shell (context menu, paste).
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed) return;
        // Presses on the empty band stay ordinary chrome; only the icon and the path drag.
        if (!IsOverDragContent(point.Position)) return;

        // Swallow the press so it cannot start a selection, then take the focus the way a
        // click on the pane would before the drag can begin.
        args.Handled = true;
        OnFocus?.Invoke(this);
        _headerBar.ShowMoveCursor(true);
        _handlePressed = true;
        _handleDragging = false;
        _handlePressPoint = args.GetCurrentPoint(_host).Position;
        _headerBar.CapturePointer(args.Pointer);
    }

    private void OnHeaderDragMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_handlePressed)
        {
            // Hovering only: the move cursor follows the content, so it changes width with
            // the path under a live pointer (cd, OSC 7, and so on).
            if (_headerBar is not null) ShowDragCursor(args.GetCurrentPoint(_headerBar).Position);
            return;
        }

        args.Handled = true;

        Point position = args.GetCurrentPoint(_host).Position;
        if (!_handleDragging)
        {
            double dx = position.X - _handlePressPoint.X;
            double dy = position.Y - _handlePressPoint.Y;
            // Button-sized slop, so a click on the header does not flick the pane around.
            if (dx * dx + dy * dy < 16.0) return;
            _handleDragging = true;
            OnDragHandleStart?.Invoke(this);
        }
        OnDragHandleMove?.Invoke(this, position);
    }

    private void OnHeaderDragReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!_handlePressed || _headerBar is null) return;
        args.Handled = true;

        bool dragging = _handleDragging;
        _handlePressed = false;
        _handleDragging = false;
        // Release before the drop: the workspace re-parents this pane, and a capture held
        // across that surgery would be lost anyway.
        _headerBar.ReleasePointerCapture(args.Pointer);
        _headerBar.ShowMoveCursor(false);
        if (dragging) OnDragHandleDrop?.Invoke(this);
    }

    /// <summary>The tooltips show the chord the config leaves active for the action.</summary>
    internal void ApplyKeybinds()
    {
        _keybinds = new Keybinds(_config);
        foreach (ActionVisual action in _actionVisuals)
        {
            if (action.ActionId is not null)
                action.TipKey.Text = _keybinds.Label(action.ActionId, action.FallbackShortcut);
        }
    }

    private Border AddAction(Path icon, Func<string> tip, string actionId, string shortcut, Action handler)
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
            Text = _keybinds.Label(actionId, shortcut),
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
            icon.Stroke = new SolidColorBrush(Theme.PaneIconStrong());
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = TransparentBrush();
            icon.Stroke = new SolidColorBrush(Theme.PaneIconFaint());
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
            ActionId = actionId,
            FallbackShortcut = shortcut,
        });
        return button;
    }

    /// <summary>Small icon button used by the find bar (previous, next, close).</summary>
    private Border SearchButton(char glyph, string tip, Action handler)
    {
        // Shared brushes, so ApplyTheme can recolor every find-bar glyph in place: a new
        // brush captured here would keep the old theme's colour after a theme switch and
        // could disappear against the new field.
        _searchGlyphBrush ??= new SolidColorBrush(Theme.TextFaint());
        _searchGlyphHoverBrush ??= new SolidColorBrush(Theme.TextStrong());

        var icon = new FontIcon
        {
            Glyph = glyph.ToString(),
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Foreground = _searchGlyphBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var button = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(4),
            Background = TransparentBrush(),
            Child = icon,
        };
        ToolTipService.SetToolTip(button, new ToolTip { Content = tip });

        button.PointerEntered += (_, _) =>
        {
            button.Background = new SolidColorBrush(Theme.ChipHover());
            icon.Foreground = _searchGlyphHoverBrush;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = TransparentBrush();
            icon.Foreground = _searchGlyphBrush;
        };
        button.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            handler();
        };
        return button;
    }

    // ---------------- search ----------------

    public bool SearchActive => _searchActive;

    /// <summary>True while the find bar itself owns keyboard focus.</summary>
    public bool SearchHasFocus => _searchBox is { FocusState: not FocusState.Unfocused };

    /// <summary>Opens the find bar over this pane and gives it keyboard focus.</summary>
    public void ShowSearch()
    {
        if (_searchBar is null || _searchBox is null) return;

        _searchActive = true;
        _searchComposing = false;
        _searchBar.Visibility = Visibility.Visible;
        _searchBox.Focus(FocusState.Programmatic);
        _searchBox.SelectAll();
        // Focus and SelectAll can race the template; repeat once the dispatcher is idle so
        // typing or pasting replaces the previous query instead of appending to it.
        TextBox box = _searchBox;
        box.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_searchActive) box.SelectAll();
        });
        if (_searchBox.Text.Length > 0) RestartSearch();
        UpdateSearchFieldPadding();
    }

    public void HideSearch()
    {
        if (!_searchActive) return;

        _searchActive = false;
        _searchSelectPending = false;
        _searchTimer?.Stop();
        _searchQuery = string.Empty;
        if (_searchBar is not null) _searchBar.Visibility = Visibility.Collapsed;
        if (_searchCount is not null) _searchCount.Text = string.Empty;
        UpdateSearchFieldPadding();

        _session.SearchSetNeedle(null);
        _renderer.SetSearchSpans(null);
        Redraw();
        OnSearchFocusReleased?.Invoke(this);
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            // Ghostty keeps the find bar when there is a query and only returns focus to
            // the terminal; an empty query closes it.
            if (_searchBox is { Text.Length: > 0 }) ReleaseSearchFocus();
            else HideSearch();
            return;
        }
        if (args.Key == VirtualKey.Enter)
        {
            args.Handled = true;
            NavigateSearch(!IsDown(VirtualKey.Shift));
        }
    }

    /// <summary>
    /// Opens the find bar with the current selection as the query ("search_selection").
    /// A search needle is one line, so a multi-line selection searches its first line.
    /// </summary>
    public void SearchSelection()
    {
        string text = System.Text.Encoding.UTF8.GetString(_session.CopySelection()).Trim();
        if (text.Length == 0) return;

        int lineBreak = text.IndexOfAny(new[] { '\r', '\n' });
        if (lineBreak >= 0) text = text[..lineBreak];

        ShowSearch();
        if (_searchBox is null) return;
        _searchBox.Text = text;
        _searchBox.SelectAll();
    }

    /// <summary>Moves to the next or previous match while the find bar is open.</summary>
    public void SearchNext()
    {
        if (_searchActive) NavigateSearch(true);
    }

    public void SearchPrevious()
    {
        if (_searchActive) NavigateSearch(false);
    }

    /// <summary>Gives the keyboard back to the terminal without dismissing the find bar.</summary>
    private void ReleaseSearchFocus() => OnSearchFocusReleased?.Invoke(this);

    /// <summary>Restarts the scan for the new needle and drops the old selection.</summary>
    private void RestartSearch()
    {
        _searchSelectPending = true;
        _session.SearchSetNeedle(_searchQuery);
        if (_searchCount is not null) _searchCount.Text = _searchQuery.Length == 0 ? string.Empty : "\u2026";
        _renderer.SetSearchSpans(null);
        Redraw();
        _searchTimer?.Start();
        StepSearch();
    }

    /// <summary>Advances the incremental scan; the timer keeps calling it until complete.</summary>
    private void StepSearch()
    {
        if (!_searchActive) return;

        bool complete = _session.SearchStep();

        // The first complete scan picks the newest match, like Ghostty's find bar.
        if (complete && _searchSelectPending)
        {
            _searchSelectPending = false;
            _session.SearchStats(out ulong total, out _);
            if (total > 0 && _session.SearchSelect(true))
            {
                _session.UpdateRenderState();
                UpdateScrollbar();
            }
        }

        UpdateSearchResults(complete);
        Redraw();
        if (!complete) _searchTimer?.Start();
    }

    private void NavigateSearch(bool next)
    {
        if (!_searchActive) return;
        if (!_session.SearchSelect(next))
        {
            UpdateSearchResults(true);
            return;
        }

        _session.UpdateRenderState();
        UpdateScrollbar();
        UpdateSearchResults(true);
        Redraw();
    }

    /// <summary>Pushes the viewport highlights and the "k / n" counter into the UI.</summary>
    private void UpdateSearchResults(bool complete)
    {
        _session.SearchStats(out ulong total, out ulong? selected);
        _renderer.SetSearchSpans(_session.SearchSpans());

        if (_searchCount is null) return;
        if (_searchQuery.Length == 0)
        {
            _searchCount.Text = string.Empty;
        }
        else if (total == 0)
        {
            _searchCount.Text = complete ? "-/0" : "\u2026";
        }
        else if (selected is null)
        {
            _searchCount.Text = complete ? $"-/{total}" : $"{total}+";
        }
        else
        {
            _searchCount.Text = complete ? $"{selected.Value + 1}/{total}" : $"{total}+";
        }
        UpdateSearchFieldPadding();
    }

    /// <summary>
    /// Reserves space inside the field only while the counter actually has text, so an empty
    /// query does not leave a blank strip after the caret.
    /// </summary>
    private void UpdateSearchFieldPadding()
    {
        if (_searchBox is null) return;

        double right = 10;
        if (_searchBox.Text.Length > 0)
        {
            right = 6;
            if (_searchCount is { Visibility: Visibility.Visible } &&
                !string.IsNullOrEmpty(_searchCount.Text))
            {
                // Reserve exactly as much as this counter needs, so a short "1/6" or "-/0"
                // does not leave a wide blank strip between the text and the counter.
                _searchCount.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                right += _searchCount.DesiredSize.Width + 6;
            }
        }
        _searchBox.Padding = new Thickness(8, 5, right, 5);
    }

    /// <summary>
    /// The counter occupies the field's trailing edge, so the TextBox template's built-in
    /// clear button is removed from the visual tree's layout entirely. The template's
    /// "ButtonVisible" state re-shows it on text changes, hence the repeated call.
    /// </summary>
    private void HideSearchClearButton()
    {
        if (_searchBox is null) return;
        if (FindDescendant(_searchBox, "DeleteButton") is not Button deleteButton) return;

        deleteButton.Visibility = Visibility.Collapsed;
        deleteButton.Width = 0;
        deleteButton.MinWidth = 0;
        deleteButton.Padding = new Thickness(0);
        deleteButton.Margin = new Thickness(0);
        deleteButton.Opacity = 0;
        deleteButton.IsHitTestVisible = false;
    }

    private void HideSearchClearButtonSoon()
    {
        DispatcherQueue? queue = _searchBox?.DispatcherQueue;
        if (queue is null)
        {
            HideSearchClearButton();
            return;
        }
        queue.TryEnqueue(HideSearchClearButton);
    }

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name) return element;
            FrameworkElement? found = FindDescendant(child, name);
            if (found is not null) return found;
        }
        return null;
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
        UpdateFrameBorder();
        ApplyFocusState();
    }

    // PointerEntered does not always reach the frame when the pointer first lands on the
    // canvas child, so movement counts as hover too.
    private void SetHover(bool value)
    {
        if (_hovered == value) return;
        _hovered = value;
        UpdateFrameBorder();
        ApplyFocusState();

        if (value && _config.FocusFollowsMouse) OnFocus?.Invoke(this);
    }

    /// <summary>
    /// The frame outline follows hover, except while a bell "border" alert is pending: that
    /// stays visible until the pane is focused or interacted with.
    /// </summary>
    private void UpdateFrameBorder()
    {
        _frame.BorderBrush = new SolidColorBrush(
            _bellBorder ? Theme.Accent()
            : _hovered ? Theme.PaneStrokeHover()
            : Theme.PaneStroke());
    }

    /// <summary>
    /// The terminal bell and "notify-on-command-finish-action = bell" share this path, so
    /// both follow "bell-features".
    /// </summary>
    public void RingBell()
    {
        if (_config.BellSystem) MessageBeep(0xFFFFFFFF);
        if (_config.BellAudio) PlayBellAudio();

        // The window decides whether it is actually in the background; a focused pane
        // can still be behind another application.
        if (_config.BellAttention) OnBellAttention?.Invoke(this);
        if (_focused) return;

        if (_config.BellBorder && !_bellBorder)
        {
            _bellBorder = true;
            ApplyFocusState();
        }
        if (_config.BellTitle && !_bellTitle && _baseTitle.Length > 0)
        {
            _bellTitle = true;
            Title = BellTitlePrefix + _baseTitle;
            OnTitle?.Invoke(this);
        }
    }

    /// <summary>True while the injected shell integration reports a running command.</summary>
    public bool CommandRunning => _session.CommandRunning;

    /// <summary>
    /// OSC 9;4 report from the program: draw it as a bar along the top of the surface.
    /// "progress-style = false" only hides the bar; the state is kept so re-enabling the
    /// option shows the current progress again.
    /// </summary>
    private void OnProgressReported(int state, int value)
    {
        _progressState = state;
        _progressValue = value;
        UpdateProgressBar();
    }

    private void UpdateProgressBar()
    {
        // SyncConfig/ApplyTheme may run before the constructor reached the bar.
        if (_progressBar is null) return;

        bool show = _config.ProgressStyle && _progressState != 0;
        _progressBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;

        // State 2 and 4 have their own colours in the control's template; state 3 pulses.
        _progressBar.IsIndeterminate = _progressState == 3;
        _progressBar.ShowError = _progressState == 2;
        _progressBar.ShowPaused = _progressState == 4;
        _progressBar.Value = _progressValue;
    }

    // ---------------- program status (OSC 7501) ----------------

    /// <summary>The OSC 7501 report to show, or null when the program reported nothing.</summary>
    public ProgramStatusInfo? ProgramStatus => _session?.ProgramStatus;

    /// <summary>Raised when the visible OSC 7501 report changed, so tab chips can follow.</summary>
    public Action<TerminalPane>? OnProgramStatusChanged;

    /// <summary>
    /// Draws the program's own status in the header: its message or title, plus a percentage
    /// while it works. An idle program shows nothing, and the icon alone is enough when the
    /// report carried no text.
    /// </summary>
    private void UpdateStatusBadge()
    {
        if (_statusBadge is null || _statusIcon is null || _statusText is null) return;

        ProgramStatusInfo? reported = _session?.ProgramStatus;
        if (reported is not { } status || status.State == ProgramState.Idle)
        {
            _statusBadge.Visibility = Visibility.Collapsed;
            if (_iconStatusDot is not null) _iconStatusDot.Visibility = Visibility.Collapsed;
            return;
        }

        if (_iconStatusDot is not null)
        {
            _iconStatusDot.Visibility = Visibility.Visible;
            _iconStatusDot.Fill = new SolidColorBrush(Theme.ProgramStatusColor(status.State));
        }

        string text = status.Message.Length > 0 ? status.Message
            : status.Title.Length > 0 ? status.Title
            : status.App;
        if (status.Progress >= 0)
            text = text.Length == 0 ? $"{status.Progress}%" : $"{text} {status.Progress}%";

        _statusBadge.Visibility = Visibility.Visible;
        _statusIcon.Glyph = StatusGlyph(status.State);
        _statusIcon.Foreground = new SolidColorBrush(Theme.ProgramStatusColor(status.State));
        _statusText.Text = text;
        _statusText.Foreground = new SolidColorBrush(Theme.ProgramStatusColor(status.State));
        _statusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string StatusGlyph(ProgramState state) => state switch
    {
        ProgramState.Working => "\uE895",  // Sync
        ProgramState.Blocked => "\uE72E",  // Lock
        ProgramState.Error => "\uE783",    // Error
        ProgramState.Done => "\uE73E",     // CheckMark
        _ => "\uE946",                     // Info
    };

    // ---------------- lifecycle ----------------

    /// <summary>
    /// Starts the shell only once the canvas has a real size. The pseudoconsole is then
    /// created at the final grid size instead of the 80x24 seed, so the shell never
    /// initializes its console at the wrong width.
    /// </summary>
    private void TryStart()
    {
        if (_ready || !_loaded) return;
        if (_canvas.ActualWidth < 2 || _canvas.ActualHeight < 2) return;
        Start();
    }

    public void Start()
    {
        if (_ready) return;

        WireSession();
        BeginSession();

        // Policies have to be in place before the shell can print an OSC 52 sequence.
        ApplyClipboardSettings();

        // A restored pane already holds the saved output; the divider goes below it and the
        // new shell inherits that cursor (TerminalSession.Start), so its prompt lands under
        // the history with nothing replayed from this side.
        if (_session.Restored)
            _session.PrepareRestored(_cols, _rows, PhysicalCellMetrics(), _restoreBanner ?? string.Empty);

        _session.Start(_config.Command, _config.WorkingDirectory, _cols, _rows, PhysicalCellMetrics(),
            _config.ExtraEnvironment, _config.TermVariable, _config.Input,
            _config.ShellIntegration, _config.ShellFeatures);
        _cwdTimer?.Start();
        UpdateShellIcon();
        UpdateLocation();
        Redraw();

        // Loaded can fire before the canvas is arranged (a cold start, or a window that is
        // still hidden), so the Layout above may have no size to work with and the grid
        // stays at the 80x24 seed. One dispatcher turn later the arrange has finished; lay
        // out once more so the shell always gets the real column count.
        _dispatcher?.TryEnqueue(() => Diag.Guard("pane.layout", () => Layout()));
    }

    /// <summary>
    /// Re-attaches a session that is already running: a tab moved to another window keeps
    /// its shell, scrollback and working directory instead of starting a new process.
    /// </summary>
    internal void AdoptSession(TerminalSession session)
    {
        if (_ready) return;

        // The placeholder session made by the constructor is not needed.
        _session.Dispose();
        _session = session;
        _session.SetScrollbackLimit(_config.ScrollbackLimit);
        _session.SetScrollbackLimitLines(_config.ScrollbackLimitLines);
        _session.SetTitleReport(_config.TitleReport);
        _session.SetEnquiryResponse(_config.EnquiryResponse);
        _session.SetOscColorReportFormat(_config.OscColorReportFormat);
        _session.SetXtChecksumExtension(_config.XtChecksumExtension);
        _session.SetXtChecksumReport(_config.XtChecksumReport);

        WireSession();
        BeginSession();

        ApplyClipboardSettings();
        // Output that arrived while the pane was detached is still in the session's buffer.
        _session.FlushPending();
        _cwdTimer?.Start();
        UpdateShellIcon();
        UpdateLocation();
        ApplyTheme();
        _dispatcher?.TryEnqueue(() => Diag.Guard("pane.layout", () => Layout()));
    }

    /// <summary>
    /// Hands the running session over without stopping the shell. Close() afterwards only
    /// tears down this pane's timers and canvas hooks, so the new owner keeps the process.
    /// </summary>
    internal TerminalSession ReleaseSession()
    {
        TerminalSession session = _session;
        _sessionReleased = true;
        session.Post = null;
        session.OnUpdate = null;
        session.OnOutput = null;
        session.OnBell = null;
        session.OnCommandFinished = null;
        session.OnProgress = null;
        session.OnProgramStatus = null;
        session.OnDesktopNotification = null;
        session.OnResizeWindow = null;
        session.OnExit = null;
        session.OnTitle = null;
        return session;
    }

    /// <summary>This pane's config snapshot, carried along when the pane moves windows.</summary>
    internal Config ConfigSnapshot()
    {
        var copy = new Config();
        copy.CopyFrom(_config);
        return copy;
    }

    /// <summary>The dispatcher and draw plumbing both the fresh and the adopted session need.</summary>
    private void BeginSession()
    {
        _ready = true;
        HookDraw();
        Layout();
    }

    private void WireSession()
    {
        _session.Post = action =>
        {
            if (_dispatcher is null) return;
            _dispatcher.TryEnqueue(() => action());
        };
        _session.OnUpdate = ScheduleRedraw;
        _session.OnOutput = () =>
        {
            if (_config.ScrollToBottomOutput) _session.ScrollToBottom();
            // New output can add or remove matches; let the incremental scan catch up.
            if (_searchActive) _searchTimer?.Start();
        };
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
            _baseTitle = value;
            if (_titleOverride is not null) return;
            Title = _bellTitle ? BellTitlePrefix + value : value;
            OnTitle?.Invoke(this);
        };
        _session.OnBell = RingBell;
        _session.OnCommandFinished = (exit, duration) => OnCommandFinished?.Invoke(this, exit, duration);
        _session.OnProgress = OnProgressReported;
        _session.OnDesktopNotification = (title, body) =>
        {
            if (_config.DesktopNotifications) OnDesktopNotification?.Invoke(this, title, body);
        };
        _session.OnResizeWindow = (rows, columns) =>
        {
            // "vt-window-resize-allowed": off by default, like Ghostty. Resizing re-lays out
            // the pane, which resizes the terminal; defer so that work never runs inside the
            // VT write that produced this request.
            if (!_config.VtWindowResizeAllowed) return;
            if (_dispatcher is null) return;
            _dispatcher.TryEnqueue(() => Diag.Guard("pane.resize_window",
                () => OnResizeWindow?.Invoke(this, rows, columns)));
        };
        _session.OnProgramStatus = () =>
        {
            UpdateStatusBadge();
            OnProgramStatusChanged?.Invoke(this);
        };
        _session.OnExit = () =>
        {
            // wait-after-command keeps the surface (and its scrollback) open after the shell exits.
            if (!_config.WaitAfterCommand) OnExit?.Invoke(this);
        };
    }

    public void Close()
    {
        if (!_ready && _session is null) return;
        // Closing a tab and closing the split inside it can both reach the same pane; the
        // shell behind it must go exactly once.
        if (_closed) return;
        _closed = true;
        _ready = false;
        _layoutSuspended = 0;
        ShowPointer();

        _canvas.PointerWheelChanged -= OnPointerWheel;
        _canvas.PointerPressed -= OnPointerPressed;
        _canvas.PointerMoved -= OnPointerMoved;
        _canvas.PointerReleased -= OnPointerReleased;
        UnhookDraw();
        _renderer.NeedsRedraw -= Redraw;

        if (_hookedRoot is not null)
        {
            _hookedRoot.Changed -= OnXamlRootChanged;
            _hookedRoot = null;
        }

        _cwdTimer?.Stop();
        _cwdTimer = null;
        _searchTimer?.Stop();
        _searchTimer = null;
        _searchActive = false;
        _renderer.SetSearchSpans(null);
        _blinkTimer?.Stop();
        _blinkTimer = null;
        StopSelectionScroll();
        _selectScrollTimer = null;

        _session.OnUpdate = null;
        _session.OnBell = null;
        _session.OnCommandFinished = null;
        _session.OnProgress = null;
        _session.OnProgramStatus = null;
        _session.OnDesktopNotification = null;
        _session.OnResizeWindow = null;
        _session.OnExit = null;
        _session.OnTitle = null;
        if (_sessionReleased) return;
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

    /// <summary>Configured padding (Ghostty points) converted to the DIPs this layout uses.</summary>
    private double PadLeft => Config.PaddingDips(_config.PaddingLeft, RasterizationScale);
    private double PadTop => Config.PaddingDips(_config.PaddingTop, RasterizationScale);
    private double PadRight => Config.PaddingDips(_config.PaddingRight, RasterizationScale);
    private double PadBottom => Config.PaddingDips(_config.PaddingBottom, RasterizationScale);

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
        UpdateSearchBarWidth();

        // Padding moves the margins, but ActualWidth still reflects the old ones until the
        // arrange pass that follows. Derive the new content size from the margin delta
        // instead of waiting for SizeChanged: on startup the shell must be created at the
        // real width, not the 80x24 seed.
        Thickness oldMargin = _canvas.Margin;
        BalancePadding();
        double width = _canvas.ActualWidth + oldMargin.Left + oldMargin.Right
                       - _canvas.Margin.Left - _canvas.Margin.Right;
        double height = _canvas.ActualHeight + oldMargin.Top + oldMargin.Bottom
                        - _canvas.Margin.Top - _canvas.Margin.Bottom;
        if (width < 2 || height < 2) return;

        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return;

        // The renderer draws columns at the font's exact advance, while CellMetrics.Width
        // is rounded up. Using the rounded width here would fit fewer columns and leave a
        // multi-cell blank band on the right.
        double cellWidth = _renderer.CellAdvanceX > 0 ? _renderer.CellAdvanceX : metrics.Width;
        ushort cols = (ushort)Math.Max(2, (int)(width / cellWidth));
        ushort rows = (ushort)Math.Max(2, (int)(height / metrics.Height));
        LaidOut = true;
        if (!force && cols == _cols && rows == _rows) return;

        Diag.Debug("pane.layout",
            $"canvas={width:0.#}x{height:0.#} cell={metrics.Width}x{metrics.Height} " +
            $"scale={RasterizationScale:0.##} grid={cols}x{rows} cur={_cols}x{_rows}");

        _cols = cols;
        _rows = rows;
        _session.Resize(cols, rows, PhysicalCellMetrics());
        Redraw();
    }

    /// <summary>
    /// The find bar is right-aligned and must not run past the pane's left edge in a narrow
    /// window or split. Its width follows the surface; the text box takes the leftover.
    /// </summary>
    private void UpdateSearchBarWidth()
    {
        if (_searchBar is null) return;

        double available = _canvas.ActualWidth + _canvas.Margin.Left + _canvas.Margin.Right;
        if (available <= 0) return;

        double rightMargin = available < 280 ? 8 : 40;
        double width = Math.Min(260, Math.Max(200, available - rightMargin - 8));
        // Width starts as NaN for an auto-sized element; NaN comparisons are always false,
        // so test explicitly or the bar stays auto-sized and grows/shrinks while typing.
        if (double.IsNaN(_searchBar.Width) || Math.Abs(_searchBar.Width - width) > 0.5)
            _searchBar.Width = width;
        _searchBar.Margin = new Thickness(0, 8, rightMargin, 0);

        // Very narrow splits keep the input and the navigation buttons; the counter is
        // the first thing to go so the bar never overflows.
        if (_searchCount is not null && _searchBox is not null)
        {
            bool showCount = width >= 230;
            _searchCount.Visibility = showCount ? Visibility.Visible : Visibility.Collapsed;
            UpdateSearchFieldPadding();
        }
    }

    /// <summary>
    /// "window-padding-balance": splits the pixels left over after the last whole cell
    /// between the two sides instead of leaving them all on the right/bottom. The outer
    /// size is reconstructed from the canvas plus its current margins, so the result is
    /// stable across the SizeChanged pass it triggers.
    /// </summary>
    private bool BalancePadding()
    {
        double baseLeft = PadLeft;
        double baseTop = PadTop;
        double baseRight = PadRight;
        double baseBottom = PadBottom;

        if (!_config.PaddingBalance)
            return SetPaddingMargins(baseLeft, baseTop, baseRight, baseBottom);

        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return false;

        double outerWidth = _canvas.ActualWidth + _canvas.Margin.Left + _canvas.Margin.Right;
        double outerHeight = _canvas.ActualHeight + _canvas.Margin.Top + _canvas.Margin.Bottom;

        // Match the column math in Layout: the drawn grid uses the exact advance.
        double cellWidth = _renderer.CellAdvanceX > 0 ? _renderer.CellAdvanceX : metrics.Width;
        double cols = Math.Max(2,
            Math.Floor(Math.Max(0, outerWidth - baseLeft - baseRight) / cellWidth));
        double rows = Math.Max(2,
            Math.Floor(Math.Max(0, outerHeight - baseTop - baseBottom) / metrics.Height));
        double extraWidth = Math.Max(0, outerWidth - baseLeft - baseRight - cols * cellWidth);
        double extraHeight = Math.Max(0, outerHeight - baseTop - baseBottom - rows * metrics.Height);

        double left = baseLeft + Math.Floor(extraWidth / 2);
        double right = baseRight + (extraWidth - Math.Floor(extraWidth / 2));
        double top = baseTop + Math.Floor(extraHeight / 2);
        double bottom = baseBottom + (extraHeight - Math.Floor(extraHeight / 2));
        return SetPaddingMargins(left, top, right, bottom);
    }

    private bool SetPaddingMargins(double left, double top, double right, double bottom)
    {
        Thickness margin = _canvas.Margin;
        if (Math.Abs(margin.Left - left) < 0.01 && Math.Abs(margin.Top - top) < 0.01 &&
            Math.Abs(margin.Right - right) < 0.01 && Math.Abs(margin.Bottom - bottom) < 0.01)
        {
            return false;
        }

        _canvas.Margin = new Thickness(left, top, right, bottom);
        _dim.Margin = _canvas.Margin;
        _scrollBar.Margin = new Thickness(0, top, 0, bottom);
        return true;
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
        if (focused)
        {
            ClearBellState();
            // The user is looking at the surface: a finished or failed program report has
            // served its purpose.
            _session?.AcknowledgeProgramStatus();
        }

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

    /// <summary>Clears "bell-features = title / border" alerts once the pane is used again.</summary>
    private void ClearBellState()
    {
        if (_bellTitle)
        {
            _bellTitle = false;
            Title = _baseTitle;
            OnTitle?.Invoke(this);
        }
        if (_bellBorder)
        {
            _bellBorder = false;
            UpdateFrameBorder();
        }
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

    /// <summary>Re-applies the program-query options after a config reload.</summary>
    public void ApplyProgramQueries()
    {
        _session.SetTitleReport(_config.TitleReport);
        _session.SetEnquiryResponse(_config.EnquiryResponse);
        _session.SetOscColorReportFormat(_config.OscColorReportFormat);
        _session.SetXtChecksumExtension(_config.XtChecksumExtension);
        _session.SetXtChecksumReport(_config.XtChecksumReport);
    }

    /// <summary>Re-applies "clipboard-read/write" and the per-write byte limit.</summary>
    public void ApplyClipboardSettings() =>
        _session.ApplyClipboardSettings(
            _config.ClipboardRead, _config.ClipboardWrite, _config.ClipboardWriteLimitBytes);

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

    /// <summary>The pane's current (possibly zoomed) font size in points.</summary>
    public float CurrentFontSizePoints => _fontSizeDip * 72f / 96f;

    /// <summary>"background-opacity" as a 0..1 alpha for the pane surface.</summary>
    private float SurfaceOpacity => Math.Clamp(_config.BackgroundOpacity, 0f, 1f);

    /// <summary>
    /// The terminal surface. With a translucent window backdrop the backdrop already carries
    /// the tint, so the pane stays transparent; otherwise it applies "background-opacity"
    /// itself. The frame paints it, which also covers the header and the padding around the
    /// canvas.
    /// </summary>
    private Color SurfaceColor()
    {
        // With a window backdrop the tint already lives there; painting the pane again
        // would apply "background-opacity" twice and make the terminal nearly opaque.
        if (_config.SingleLayerTransparency) return Color.FromArgb(0, 0, 0, 0);

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
        UpdateFrameBorder();
        _progressBar.Foreground = new SolidColorBrush(Theme.Accent());
        UpdateProgressBar();
        UpdateStatusBadge();
        _dim.Background = new SolidColorBrush(DimFillColor());
        _canvas.ClearColor = CanvasClearColor();
        if (_linkPreview is not null)
        {
            _linkPreview.Background = new SolidColorBrush(Theme.PaletteBg());
            _linkPreview.BorderBrush = new SolidColorBrush(Theme.Stroke());
        }
        if (_linkPreviewText is not null)
            _linkPreviewText.Foreground = new SolidColorBrush(Theme.TextStrong());
        if (_searchBar is not null)
        {
            _searchBar.Background = new SolidColorBrush(Theme.PaletteTint());
            _searchBar.BorderBrush = new SolidColorBrush(Theme.Stroke());
        }
        if (_searchField is not null)
            _searchField.Background = new SolidColorBrush(Theme.ChipHover());
        if (_searchBox is not null)
        {
            _searchBox.Foreground = new SolidColorBrush(Theme.TextStrong());
            _searchBox.PlaceholderForeground = new SolidColorBrush(Theme.TextFaint());
        }
        if (_searchCount is not null)
            _searchCount.Foreground = new SolidColorBrush(Theme.TextFaint());

        // The find-bar glyphs own shared brushes created when the bar was built; recolor
        // them here or they keep the previous theme's colour and vanish on a theme switch.
        if (_searchGlyphBrush is not null) _searchGlyphBrush.Color = Theme.TextFaint();
        if (_searchGlyphHoverBrush is not null) _searchGlyphHoverBrush.Color = Theme.TextStrong();

        foreach (ActionVisual action in _actionVisuals)
        {
            action.Icon.Stroke = new SolidColorBrush(Theme.PaneIconFaint());
            action.Button.Background = TransparentBrush();
            action.TipTitle.Foreground = new SolidColorBrush(Theme.TextStrong());
            action.TipKey.Foreground = new SolidColorBrush(Theme.TextFaint());
        }

        ApplyFocusState();

        _session.ApplyTheme(Theme.Terminal);
        // The shell icon is theme-independent, so a theme change leaves it untouched.
        Redraw();
    }

    /// <summary>
    /// Resolves the shell's icon. A pane in a background tab has no process yet, so the
    /// configured command answers instead; the icon does not have to wait for the shell to
    /// start (a restored window would otherwise show a placeholder until each tab is opened).
    /// </summary>
    private void UpdateShellIcon()
    {
        string executable = _session.ShellExecutable();
        if (executable.Length == 0) executable = TerminalSession.PreferredShellPath(_config.Command);
        if (executable == _shellIconPath) return;

        _shellIconPath = executable;
        _ = LoadShellIconAsync(executable);
    }

    private async Task LoadShellIconAsync(string executable)
    {
        ImageSource? source = await ShellIcon.GetAsync(executable);
        // Drop a late result: the pane is gone, the shell changed, or extraction failed.
        if (_sessionReleased || source is null || _shellIconPath != executable) return;

        _shellIconSource = source;
        _shellIcon.Source = source;
        _shellIcon.Visibility = Visibility.Visible;
        _iconFallback.Visibility = Visibility.Collapsed;

        // A cached icon resolves synchronously, so by the time it is ready the pane's tab does
        // not exist yet and nobody is listening. Announcing on the next dispatcher turn gives
        // the chip that is built right after this constructor the chance to hear it.
        if (_dispatcher is null || !_dispatcher.TryEnqueue(() => OnShellIcon?.Invoke(this)))
            OnShellIcon?.Invoke(this);
    }

    // ---------------- session inheritance ----------------

    /// <summary>Keep configured arguments intact; only auto-detected sessions fall back.</summary>
    public string ShellCommand()
    {
        if (!string.IsNullOrEmpty(_config.Command)) return _config.Command;
        return _session.ShellExecutable();
    }

    /// <summary>
    /// The command exactly as configured (possibly empty, meaning "use the default shell").
    /// The resolved executable must not be persisted: pinning an absolute packaged path would
    /// change how the shell is launched and which icon it resolves to.
    /// </summary>
    internal string ConfiguredCommand() => _config.Command;

    public string WorkingDirectory() =>
        _session.CurrentDirectory(out string directory) ? directory : _workingDirectory;

    /// <summary>Terminal state for "window-save-state"; null when it cannot be encoded.</summary>
    internal byte[]? Snapshot() => _session.Snapshot();

    /// <summary>True once the shell has started and the pane owns a live terminal.</summary>
    internal bool Started => _ready;

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

        // Cell space starts after the frame outline and the pane head band, then the
        // configured term padding; the host is the frame's root, so those offsets count.
        // Both are measured here instead of hard-coding the old 1 + 34 + 2 stack, which
        // baked the default padding into the constant and double-counted it.
        double inset = _frame.BorderThickness.Top;
        double head = _headerBar?.Height ?? 32.0;
        x = cellX * (double)_renderer.CellAdvanceX + inset + PadLeft;
        y = cellY * (double)metrics.Height + inset + head + PadTop;
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
        if (ReadOnly) return true;
        // Keybindings already ran in the workspace, so KAM only swallows what would have
        // reached the shell, matching Ghostty's order.
        if (KamBlocksKeyboard()) return true;

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

        HidePointer();
        ClearBellState();
        _session.SendKey(key, mods, text.Length > 0 ? System.Text.Encoding.ASCII.GetBytes(text) : null);
        if (_config.ScrollToBottomKeystroke) _session.ScrollToBottom();
        return true;
    }

    public void CommitText(string text)
    {
        if (!_ready || _session is null || text.Length == 0) return;
        if (ReadOnly) return;
        // IME commits are keyboard input too ("vt-kam-allowed").
        if (KamBlocksKeyboard()) return;
        HidePointer();
        ClearBellState();
        ClearSelectionOnTyping();
        _session.WriteInput(System.Text.Encoding.UTF8.GetBytes(text));
        if (_config.ScrollToBottomKeystroke) _session.ScrollToBottom();
    }

    private bool KamBlocksKeyboard() =>
        _config.VtKamAllowed && _session is not null && _session.KeyboardActionMode();

    private void ClearSelectionOnTyping()
    {
        if (!_config.SelectionClearOnTyping) return;
        if (!_session.HasSelection()) return;
        StopSelectionScroll();
        _keyboardSelecting = false;
        _session.ClearSelection();
        Redraw();
    }

    public void CopySelection()
    {
        StopSelectionScroll();
        byte[] text = _session.CopySelection();
        if (text.Length == 0) return;

        string value = System.Text.Encoding.UTF8.GetString(text);
        if (_config.ClipboardCodepointMap.Count > 0) value = _config.ApplyClipboardCodepointMap(value);
        if (_config.ClipboardTrimTrailingSpaces) value = TrimTrailingSpaces(value);
        CopyTextToClipboard(value);
        ClearSelectionAfterCopy();
    }

    /// <summary>
    /// "Copy to Clipboard": the selection when there is one, otherwise the whole screen.
    /// </summary>
    public void CopyToClipboard()
    {
        if (_session.HasSelection())
        {
            CopySelection();
            return;
        }

        StopSelectionScroll();
        byte[] text = _session.Format(false, GhosttyNative.FORMAT_PLAIN);
        if (text.Length == 0) return;
        CopyTextToClipboard(System.Text.Encoding.UTF8.GetString(text));
    }

    /// <summary>Copies the selection with colors as ANSI sequences.</summary>
    public void CopySelectionAnsi()
    {
        StopSelectionScroll();
        byte[] text = _session.Format(true, GhosttyNative.FORMAT_VT);
        if (text.Length == 0) return;
        CopyTextToClipboard(System.Text.Encoding.UTF8.GetString(text));
        ClearSelectionAfterCopy();
    }

    /// <summary>Copies the selection as a minimal HTML document.</summary>
    public void CopySelectionHtml()
    {
        StopSelectionScroll();
        byte[] text = _session.Format(true, GhosttyNative.FORMAT_PLAIN);
        if (text.Length == 0) return;

        string plain = System.Text.Encoding.UTF8.GetString(text);
        try
        {
            var package = new DataPackage();
            package.SetText(plain);
            package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(HtmlFromPlain(plain)));
            Clipboard.SetContent(package);
        }
        catch
        {
            // Clipboard unavailable.
        }
        ClearSelectionAfterCopy();
    }

    private void ClearSelectionAfterCopy()
    {
        if (!_config.SelectionClearOnCopy || !_session.HasSelection()) return;
        _keyboardSelecting = false;
        _session.ClearSelection();
        Redraw();
    }

    /// <summary>Wraps terminal text into a preformatted HTML document.</summary>
    internal static string HtmlFromPlain(string plain)
    {
        string body = System.Net.WebUtility.HtmlEncode(plain);
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head>" +
               "<body><pre style=\"font-family:Consolas,monospace;white-space:pre-wrap\">" +
               body + "</pre></body></html>";
    }

    private static void CopyTextToClipboard(string value)
    {
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
    }

    /// <summary>Scrolls the viewport to the oldest row ("scroll_to_top").</summary>
    public void ScrollToTop() => _session.ScrollTo(0);

    /// <summary>Scrolls the viewport to the newest row ("scroll_to_bottom").</summary>
    public void ScrollToBottom() => _session.ScrollToBottom();

    /// <summary>Scrolls the viewport by one window ("scroll_page_up/down").</summary>
    public void ScrollPage(bool up) => _session.ScrollPage(up);

    /// <summary>Jumps to the previous (-1) or next (+1) shell prompt.</summary>
    public void JumpToPrompt(int delta) => _session.JumpToPrompt(delta);

    /// <summary>Selects the whole viewport ("select_all").</summary>
    public void SelectAll()
    {
        if (_cols == 0 || _rows == 0) return;
        if (!_session.SetSelection(0, 0, (ushort)(_cols - 1), (ushort)(_rows - 1))) return;

        _keyboardSelecting = false;
        _session.UpdateRenderState();
        Redraw();
    }

    /// <summary>
    /// Extends the selection from the text cursor by one cell ("adjust_selection"). The
    /// anchor is kept in screen coordinates so scrolling does not move it.
    /// </summary>
    public void AdjustSelection(int dx, int dy)
    {
        if (!_ready) return;
        if (!_session.CursorCell(out ushort cursorX, out ushort cursorY)) return;
        if (!_session.Scrollbar(out ulong total, out ulong offset, out ulong len)) return;
        if (total == 0 || _cols == 0) return;

        long cursorRow = (long)offset + cursorY;
        if (!_keyboardSelecting || !_session.HasSelection())
        {
            _keyboardSelecting = true;
            _selectAnchorX = cursorX;
            _selectAnchorY = (uint)Math.Clamp(cursorRow, 0, (long)total - 1);
            _selectEndX = cursorX;
            _selectEndY = _selectAnchorY;
        }

        long maxRow = (long)total - 1;
        long endRow = _selectEndY;
        long endX = _selectEndX;

        if (dy != 0) endRow = Math.Clamp(endRow + dy, 0, maxRow);
        if (dx != 0)
        {
            // Move across the row, wrapping so the selection can span line breaks.
            long flat = endRow * _cols + endX + dx;
            flat = Math.Clamp(flat, 0, Math.Max(0, (long)total * _cols - 1));
            endRow = flat / _cols;
            endX = flat % _cols;
        }

        _selectEndX = (ushort)Math.Clamp(endX, 0, _cols - 1);
        _selectEndY = (uint)endRow;

        if (!_session.SetSelectionScreen(_selectAnchorX, _selectAnchorY,
                _selectEndX, _selectEndY))
        {
            return;
        }

        _session.UpdateRenderState();
        UpdateScrollbar();
        Redraw();
    }

    /// <summary>Clears the screen and the scrollback ("clear_screen").</summary>
    public void ClearScreen()
    {
        _session.ClearScreen();
        Redraw();
    }

    /// <summary>Copies the URL under the pointer ("copy_url_to_clipboard").</summary>
    public void CopyUrlAtPointer()
    {
        string? url = _hoverUrl;
        if (url is null && CellAt(_hoverPoint, out ushort x, out ushort y))
        {
            if (_config.LinkOsc8) url = _session.HyperlinkAt(x, y);
            if (url is null && _config.LinkUrl) url = UrlAt(x, y);
        }
        if (string.IsNullOrEmpty(url)) return;
        CopyTextToClipboard(url);
    }

    /// <summary>Pins the pane title; null returns it to the shell's own reports.</summary>
    public void SetTitleOverride(string? title)
    {
        _titleOverride = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        Title = _titleOverride ?? _baseTitle;
        if (Title.Length > 0) OnTitle?.Invoke(this);
    }

    /// <summary>Formats the selection (or the whole terminal) for the file export commands.</summary>
    public byte[] FormatTerminal(bool selectionOnly, int emit) => _session.Format(selectionOnly, emit);

    public bool HasSelection() => _session.HasSelection();

    /// <summary>Pastes a string as terminal input, honouring bracketed paste.</summary>
    public void PasteString(string text)
    {
        if (ReadOnly || text.Length == 0) return;
        _session.PasteText(System.Text.Encoding.UTF8.GetBytes(text));
        if (_config.ScrollToBottomKeystroke) _session.ScrollToBottom();
    }

    /// <summary>Pastes the current selection back into the shell ("paste_from_selection").</summary>
    public void PasteSelection()
    {
        if (ReadOnly) return;
        byte[] text = _session.CopySelection();
        if (text.Length == 0) return;
        _session.PasteText(text);
        if (_config.ScrollToBottomKeystroke) _session.ScrollToBottom();
    }

    /// <summary>Resets the terminal state ("Reset Terminal").</summary>
    public void ResetTerminal()
    {
        _session.Reset();
        Redraw();
    }

    /// <summary>Scrolls the viewport so the selection starts at its top edge.</summary>
    public void ScrollToSelection()
    {
        if (_session.SelectedRow(out ulong row)) _session.ScrollTo(row);
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
        if (ReadOnly) return;
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

            if (!await ConfirmPasteAsync(text, _session.BracketedPaste())) return;

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

    /// <summary>
    /// "clipboard-paste-protection": asks before pasting text that could run commands
    /// (multiple lines or control characters). "clipboard-paste-bracketed-safe" skips the
    /// prompt for bracketed pastes, which the running program has opted into.
    /// </summary>
    private async Task<bool> ConfirmPasteAsync(string text, bool bracketed)
    {
        if (!_config.PasteProtection) return true;
        if (bracketed && _config.PasteBracketedSafe) return true;
        if (!IsUnsafePaste(text)) return true;

        XamlRoot? root = _canvas.XamlRoot;
        if (root is null) return true;

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = Strings.PasteWarningTitle,
            Content = new TextBlock
            {
                Text = Strings.PasteWarningBody,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
            },
            PrimaryButtonText = Strings.PasteAnyway,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static bool IsUnsafePaste(string text)
    {
        foreach (char c in text)
        {
            if (c is '\n' or '\r') return true;
            if (char.IsControl(c) && c != '\t') return true;
        }
        return false;
    }

    private bool CellAt(Point point, out ushort x, out ushort y)
    {
        x = 0;
        y = 0;
        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return false;

        int cx = Math.Clamp((int)(point.X / _renderer.CellAdvanceX), 0, Math.Max(0, _cols - 1));
        int cy = Math.Clamp((int)(point.Y / metrics.Height), 0, Math.Max(0, _rows - 1));
        x = (ushort)cx;
        y = (ushort)cy;
        return true;
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs args)
    {
        if (!_ready) return;
        ShowPointer();

        PointerPoint point = args.GetCurrentPoint(_canvas);
        int delta = point.Properties.MouseWheelDelta;
        if (delta == 0) return;

        // A program in a mouse tracking mode receives the wheel as buttons 4/5.
        int mods = PointerMods();
        int wheelButton = delta > 0 ? GhosttyNative.MOUSE_BUTTON_FOUR : GhosttyNative.MOUSE_BUTTON_FIVE;
        if (TryReportMouse(GhosttyNative.MOUSE_ACTION_PRESS, wheelButton, mods, point))
        {
            TryReportMouse(GhosttyNative.MOUSE_ACTION_RELEASE, wheelButton, mods, point);
            args.Handled = true;
            return;
        }

        // mouse-scroll-multiplier is the discrete-wheel row count (Ghostty's default is 3).
        int step = Math.Max(1, (int)Math.Round(_config.MouseScrollMultiplier));
        _session.Scroll(delta > 0 ? -step : step);
        // The cached viewport match list moves with the rows.
        if (_searchActive) _searchTimer?.Start();
        args.Handled = true;
    }

    /// <summary>
    /// Starts a selection for a press that landed on the keyboard anchor. The anchor is a
    /// sibling of the shell in the window's visual tree, so its press never bubbles to this
    /// canvas. Capturing here makes the normal move/release handlers take over the drag.
    /// </summary>
    public void BeginSelectionFromAnchor(PointerRoutedEventArgs args) => OnPointerPressed(_canvas, args);

    /// <summary>
    /// The anchor keeps the pointer capture (a canvas that never received the press cannot
    /// take it), so move and release keep arriving at the anchor and have to be forwarded too.
    /// The handlers below are no-ops unless a selection is in progress.
    /// </summary>
    public void MoveSelectionFromAnchor(PointerRoutedEventArgs args) => OnPointerMoved(_canvas, args);

    public void EndSelectionFromAnchor(PointerRoutedEventArgs args) => OnPointerReleased(_canvas, args);

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_ready) return;

        _keyboardSelecting = false;
        ShowPointer();
        ClearBellState();
        OnFocus?.Invoke(this);

        PointerPoint point = args.GetCurrentPoint(_canvas);
        int mods = PointerMods();

        // Ctrl+click opens the link under the pointer (OSC 8 or a matched URL).
        if (point.Properties.IsLeftButtonPressed && (mods & GhosttyNative.MODS_CTRL) != 0 &&
            _hoverUrl is not null)
        {
            args.Handled = true;
            OpenLink(_hoverUrl);
            return;
        }

        // A left press goes to the program when it requested mouse tracking.
        if (point.Properties.IsLeftButtonPressed &&
            TryReportMouse(GhosttyNative.MOUSE_ACTION_PRESS, GhosttyNative.MOUSE_BUTTON_LEFT,
                mods, point))
        {
            _mouseReportedPress = true;
            _anyMouseButton = true;
            args.Handled = true;
            return;
        }

        if (!CellAt(point.Position, out ushort x, out ushort y)) return;

        if (point.Properties.IsMiddleButtonPressed)
        {
            args.Handled = true;
            // Windows has no primary selection, so both paste values use the clipboard;
            // "ignore" keeps middle-click inert.
            if (_config.MiddleClickAction != "ignore") PasteFromClipboard();
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            // Acting on press would let the pending right-button release light-dismiss
            // the menu we open (and the IME sink also sees the press). The action runs
            // from RightTapped, which fires after the press/release pair.
            args.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        _anyMouseButton = true;

        // "click-repeat-interval": clicks on the same cell inside the interval count as a
        // repeat; 0 means Ghostty's 500 ms default on non-macOS platforms.
        int interval = _config.ClickRepeatInterval > 0 ? _config.ClickRepeatInterval : 500;
        long now = Environment.TickCount64;
        bool repeat = now - (long)_lastClickTime <= interval &&
                      x == _lastClickX && y == _lastClickY;
        _clickCount = repeat ? Math.Min(3, _clickCount + 1) : 1;
        _lastClickTime = (ulong)now;
        _lastClickX = x;
        _lastClickY = y;

        if (_clickCount >= 2)
        {
            StopSelectionScroll();
            _selecting = false;
            if (_clickCount >= 3) SelectLineAt(y);
            else SelectWordAt(x, y);
            args.Handled = true;
            return;
        }

        StopSelectionScroll();
        _selecting = true;
        _anchorX = x;
        _anchorY = y;
        _anchorScreenY = ScreenRow(y);
        _selectPointerX = point.Position.X;
        _selectPointerY = point.Position.Y;
        _canvas.CapturePointer(args.Pointer);
        _session.ClearSelection();
        Redraw();
        args.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        ShowPointer();

        PointerPoint point = args.GetCurrentPoint(_canvas);
        int mods = PointerMods();

        if (_selecting)
        {
            _selectPointerX = point.Position.X;
            _selectPointerY = point.Position.Y;

            // Dragging past the top or bottom edge scrolls the viewport on a timer; the
            // selection endpoint stays pinned to the visible edge while it runs.
            if (_selectPointerY < 0) SetSelectionScroll(-1);
            else if (_selectPointerY >= _canvas.ActualHeight) SetSelectionScroll(1);
            else SetSelectionScroll(0);

            bool outside = _selectPointerY < 0 || _selectPointerY >= _canvas.ActualHeight;
            if (!CellAt(point.Position, out ushort x, out ushort y)) return;
            if (!outside && x == _anchorX && y == _anchorY) return;

            ExtendSelection(x, y);
            args.Handled = true;
            return;
        }

        // Hover feedback for links (with Ctrl held) and motion for mouse-aware programs.
        UpdateLinkHover(point.Position, (mods & GhosttyNative.MODS_CTRL) != 0);
        TryReportMouse(GhosttyNative.MOUSE_ACTION_MOTION,
            _anyMouseButton ? GhosttyNative.MOUSE_BUTTON_LEFT : 0, mods, point);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        PointerPoint point = args.GetCurrentPoint(_canvas);

        // The right-click action runs on release, not press: a flyout opened during the
        // press is light-dismissed by the release that follows it.
        if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonReleased)
        {
            args.Handled = true;
            ApplyRightClickAction(point.Position);
            return;
        }

        if (_mouseReportedPress)
        {
            _mouseReportedPress = false;
            _anyMouseButton = false;
            TryReportMouse(GhosttyNative.MOUSE_ACTION_RELEASE, GhosttyNative.MOUSE_BUTTON_LEFT,
                PointerMods(), point);
            args.Handled = true;
            return;
        }

        _anyMouseButton = false;
        if (!_selecting) return;
        _selecting = false;
        StopSelectionScroll();
        _canvas.ReleasePointerCapture(args.Pointer);
        args.Handled = true;

        if (_config.CopyOnSelect && _session.HasSelection()) CopySelection();
    }

    // ---------------- selection auto-scroll ----------------

    /// <summary>
    /// The screen (scrollback-absolute) row currently shown at a viewport row. The anchor is
    /// kept in this space so it stays on the same text while auto-scrolling.
    /// </summary>
    private uint ScreenRow(ushort viewportY) =>
        _session.Scrollbar(out _, out ulong offset, out _)
            ? (uint)(offset + viewportY)
            : viewportY;

    private void SetSelectionScroll(int direction)
    {
        if (direction == 0 || _selectScrollTimer is null)
        {
            StopSelectionScroll();
            return;
        }

        _selectScrollDirection = direction;
        if (!_selectScrollTimer.IsRunning) _selectScrollTimer.Start();
    }

    private void StopSelectionScroll()
    {
        _selectScrollDirection = 0;
        _selectScrollTimer?.Stop();
    }

    /// <summary>One timer tick: scroll one row, then move the selection endpoint to the edge.</summary>
    private void StepSelectionScroll()
    {
        if (!_selecting || _selectScrollDirection == 0)
        {
            StopSelectionScroll();
            return;
        }

        if (!_session.Scrollbar(out ulong total, out ulong offset, out ulong len))
        {
            StopSelectionScroll();
            return;
        }

        ulong last = total > len ? total - len : 0;
        if ((_selectScrollDirection < 0 && offset > 0) ||
            (_selectScrollDirection > 0 && offset < last))
        {
            _session.Scroll(_selectScrollDirection);
        }

        ExtendSelectionToPointer();
    }

    /// <summary>Extends the selection to the edge cell under the last pointer position.</summary>
    private void ExtendSelectionToPointer()
    {
        if (!_selecting) return;
        if (!_session.Scrollbar(out _, out ulong offset, out _)) return;

        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return;

        int x = Math.Clamp((int)(_selectPointerX / _renderer.CellAdvanceX), 0, Math.Max(0, _cols - 1));
        int y = _selectPointerY < 0 ? 0
            : _selectPointerY >= _canvas.ActualHeight ? Math.Max(0, _rows - 1)
            : Math.Clamp((int)(_selectPointerY / metrics.Height), 0, Math.Max(0, _rows - 1));

        if (_session.SetSelectionScreen(_anchorX, _anchorScreenY, (ushort)x, (uint)(offset + (ulong)y)))
            Redraw();
    }

    private void ExtendSelection(ushort x, ushort y)
    {
        if (!_session.Scrollbar(out _, out ulong offset, out _))
        {
            if (_session.SetSelection(_anchorX, _anchorY, x, y)) Redraw();
            return;
        }

        if (_session.SetSelectionScreen(_anchorX, _anchorScreenY, x, (uint)(offset + y)))
            Redraw();
    }

    private void ApplyRightClickAction(Point position)
    {
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
            case "copy-or-paste":
                if (_session.HasSelection()) CopySelection();
                else PasteFromClipboard();
                break;
            default:
                // "context-menu" (the default) shows a menu instead of acting
                // immediately, like Ghostty.
                ShowContextMenu(position);
                break;
        }
    }

    /// <summary>
    /// "right-click-action = context-menu": Copy (only with a selection) and Paste.
    /// The menu is positioned at the pointer and never acts on its own.
    /// </summary>
    private void ShowContextMenu(Point position)
    {
        // Opening the flyout inside the pointer event lets the right-tap/context-request
        // that follows light-dismiss it. Post it so those events finish first.
        if (_dispatcher is null)
        {
            ShowContextMenuNow(position);
            return;
        }
        _dispatcher.TryEnqueue(() => Diag.Guard("pane.menu", () => ShowContextMenuNow(position)));
    }

    private void ShowContextMenuNow(Point position)
    {
        var menu = new MenuFlyout();

        void AddItem(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        // The menu intentionally shows no shortcut hints: copy and paste are bound to
        // Ctrl+Shift+C / Ctrl+Shift+V, which the plain Ctrl+C / Ctrl+V labels would mislead.
        var copy = new MenuFlyoutItem
        {
            Text = Strings.Copy,
            IsEnabled = _session.HasSelection(),
        };
        copy.Click += (_, _) => CopySelection();
        menu.Items.Add(copy);

        var paste = new MenuFlyoutItem { Text = Strings.Paste };
        paste.Click += (_, _) => PasteFromClipboard();
        menu.Items.Add(paste);

        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(Strings.SplitRight, () => OnSplitRight?.Invoke(this));
        AddItem(Strings.SplitDown, () => OnSplitDown?.Invoke(this));
        AddItem(Strings.SplitLeft, () => OnSplitLeft?.Invoke(this));
        AddItem(Strings.SplitUp, () => OnSplitUp?.Invoke(this));

        menu.Items.Add(new MenuFlyoutSeparator());
        AddItem(Strings.CloseSplit, () => OnClosePane?.Invoke(this));

        menu.ShowAt(_canvas, new FlyoutShowOptions { Position = position });
    }

    // ---------------- mouse reporting and links ----------------

    private static int PointerMods()
    {
        int mods = 0;
        if (IsDown(VirtualKey.Shift)) mods |= GhosttyNative.MODS_SHIFT;
        if (IsDown(VirtualKey.Control)) mods |= GhosttyNative.MODS_CTRL;
        if (IsDown(VirtualKey.Menu)) mods |= GhosttyNative.MODS_ALT;
        return mods;
    }

    /// <summary>
    /// Sends one mouse event to the running program when it is in a mouse tracking mode.
    /// "mouse-reporting" and "mouse-shift-capture" decide whether the press is reported or
    /// handled locally (shift extends the selection unless the config says otherwise).
    /// </summary>
    private bool TryReportMouse(int action, int button, int mods, PointerPoint point)
    {
        if (!MouseReportingEnabled) return false;
        if (!_config.MouseReporting) return false;

        bool shift = (mods & GhosttyNative.MODS_SHIFT) != 0;
        if (shift && _config.MouseShiftCapture is not ("true" or "always")) return false;

        CellMetrics metrics = _renderer.CellMetrics;
        if (metrics.Width == 0 || metrics.Height == 0) return false;

        double cellX = Math.Max(0, Math.Floor(point.Position.X / _renderer.CellAdvanceX));
        double cellY = Math.Max(0, Math.Floor(point.Position.Y / metrics.Height));
        if (!_session.EncodeMouse(action, button, mods, cellX, cellY, out byte[] bytes))
            return false;

        _session.WriteInput(bytes);
        return true;
    }

    private void UpdateLinkHover(Point position, bool ctrl)
    {
        _hoverPoint = position;

        if (!_config.LinkUrl && !_config.LinkOsc8)
        {
            SetHoverUrl(null);
            return;
        }

        if (!CellAt(position, out ushort x, out ushort y))
        {
            SetHoverUrl(null);
            return;
        }

        if (ctrl && _hoverUrl is not null && _hoverHadCtrl && x == _hoverX && y == _hoverY)
        {
            UpdateLinkPreview();
            return;
        }

        _hoverX = x;
        _hoverY = y;
        _hoverHadCtrl = ctrl;
        if (!ctrl)
        {
            SetHoverUrl(null);
            return;
        }

        string? url = null;
        bool osc8 = false;
        if (_config.LinkOsc8)
        {
            url = _session.HyperlinkAt(x, y);
            osc8 = url is not null;
        }
        if (url is null && _config.LinkUrl) url = UrlAt(x, y);
        _hoverOsc8 = osc8;
        SetHoverUrl(url);
    }

    /// <summary>The http(s) / www URL covering the cell, or null.</summary>
    private string? UrlAt(ushort x, ushort y)
    {
        _session.UpdateRenderState();
        string[]? row = _session.RowCells(y);
        if (row is null || x >= row.Length) return null;

        string text = string.Concat(row);
        foreach (Match match in UrlPattern.Matches(text))
        {
            if (x < match.Index || x >= match.Index + match.Length) continue;

            string value = match.Value.TrimEnd('.', ',', ';', ':', ')', ']', '}', '>', '\'', '"');
            return value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? "https://" + value
                : value;
        }
        return null;
    }

    private void SetHoverUrl(string? url)
    {
        _hoverUrl = url;
        UpdateLinkPreview();
    }

    /// <summary>
    /// "link-previews" true shows a badge for every hovered link, "osc8" only for OSC 8
    /// hyperlinks, and false hides it. The badge follows the pointer and never takes input.
    /// </summary>
    private void UpdateLinkPreview()
    {
        if (_linkPreview is null || _linkPreviewText is null) return;

        bool enabled = _config.LinkPreviews != "false" &&
                       (_config.LinkPreviews != "osc8" || _hoverOsc8);
        if (_hoverUrl is null || !enabled)
        {
            _linkPreview.Visibility = Visibility.Collapsed;
            return;
        }

        _linkPreviewText.Text = _hoverUrl;
        _linkPreview.Visibility = Visibility.Visible;

        // The badge lives in the canvas row, so the point is relative to the canvas; the
        // margin restores the pane padding and keeps the badge inside the surface.
        double left = Math.Clamp(_canvas.Margin.Left + _hoverPoint.X + 14, 4,
            Math.Max(4, _canvas.Margin.Left + _canvas.ActualWidth - 120));
        double top = _canvas.Margin.Top + _hoverPoint.Y + 24;
        if (top + 28 > _canvas.Margin.Top + _canvas.ActualHeight)
            top = Math.Max(4, _canvas.Margin.Top + _hoverPoint.Y - 32);
        _linkPreview.Margin = new Thickness(left, top, 0, 0);
    }

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Diag.LogException("pane.openLink", error);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ShowCursor(bool show);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint type);

    private const string BellTitlePrefix = "\U0001F514 ";

    /// <summary>
    /// "bell-features = audio": plays "bell-audio-path" through the media pipeline so
    /// "bell-audio-volume" is honored. Failures are non-fatal, like Ghostty's.
    /// </summary>
    private void PlayBellAudio()
    {
        string path = _config.BellAudioPath;
        if (path.Length == 0 || !System.IO.File.Exists(path)) return;
        try
        {
            _bellPlayer ??= new Windows.Media.Playback.MediaPlayer();
            _bellPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            _bellPlayer.Volume = _config.BellAudioVolume;
            _bellPlayer.Play();
        }
        catch (Exception error)
        {
            Diag.LogException("pane.bellAudio", error);
        }
    }

    private void HidePointer()
    {
        if (!_config.MouseHideWhileTyping || _cursorHidden) return;
        ShowCursor(false);
        _cursorHidden = true;
    }

    private void ShowPointer()
    {
        if (!_cursorHidden) return;
        ShowCursor(true);
        _cursorHidden = false;
    }

    /// <summary>
    /// Double-click selects the word under the pointer. "selection-word-chars" lists the
    /// characters that end a word; blank cells and whitespace always end one.
    /// </summary>
    private void SelectWordAt(ushort x, ushort y)
    {
        if (!_ready) return;

        // The last frame may have painted before the most recent output arrived.
        _session.UpdateRenderState();
        string[]? row = _session.RowCells(y);
        if (row is null) return;

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

    /// <summary>Triple-click selects the whole line, ignoring trailing blanks.</summary>
    private void SelectLineAt(ushort y)
    {
        if (!_ready) return;

        _session.UpdateRenderState();
        string[]? row = _session.RowCells(y);
        if (row is null) return;

        int end = row.Length - 1;
        while (end >= 0 && string.IsNullOrWhiteSpace(row[end])) end--;
        if (end < 0)
        {
            _session.ClearSelection();
            Redraw();
            return;
        }

        _anchorX = 0;
        _anchorY = y;
        if (!_session.SetSelection(0, y, (ushort)end, y)) return;

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
                // Same bounds and 4.5-unit arrowheads as the expanded state; only the head
                // moves to the inner end so the arrows point at the centre.
                new float[,] { { 14.0f, 2.5f }, { 9.5f, 7.0f } },
                new float[,] { { 13.5f, 7.0f }, { 9.0f, 7.0f }, { 9.0f, 2.5f } },
                new float[,] { { 2.0f, 13.5f }, { 6.5f, 9.0f } },
                new float[,] { { 2.5f, 9.0f }, { 7.0f, 9.0f }, { 7.0f, 13.5f } },
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
            new float[,] { { 4.0f, 4.0f }, { 12.0f, 12.0f } },
            new float[,] { { 12.0f, 4.0f }, { 4.0f, 12.0f } },
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
        // The X is two short diagonals, so the shared 1.5/16 stroke reads thinner than the
        // structural split/zoom glyphs. Give it a little more weight and flat tips so it
        // stays crisp at the small preview size.
        path.StrokeThickness = size * 2.0 / 16.0;
        path.StrokeStartLineCap = PenLineCap.Flat;
        path.StrokeEndLineCap = PenLineCap.Flat;
        path.Data = CloseGeometry(size);
        return path;
    }

}
