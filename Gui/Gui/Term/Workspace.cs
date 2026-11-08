﻿using System;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;

namespace Gui.Term;

/// <summary>
/// Window chrome: a header island (tab strip + window buttons) over a rounded content card
/// that hosts the pane grid and the command palette. Owns the shared IME host so the system
/// IME composes into whichever pane has focus.
/// </summary>
public sealed partial class Workspace
{
    private enum TabPlacement { Top, Left, Right }

    private sealed class SplitNode
    {
        public TerminalPane? Pane;
        public bool Vertical;   // true: children stacked top/bottom
        public double Ratio = 0.5; // share of the first child, dragged via the divider
        public SplitNode? First;
        public SplitNode? Second;
    }

    private sealed class Tab
    {
        public string Title = string.Empty;
        public SplitNode? Root;
        public TerminalPane? Focused;
        public bool Zoomed;

        public Grid Surface = null!;
        // Split grids built by the last relayout. A pane visual can still be a child of
        // one of these, so relayout detaches from them before re-parenting.
        public readonly List<Grid> Grids = new();
        public Border Chip = null!;
        public TextBlock ChipText = null!;
        public Image ChipIcon = null!;
        public FontIcon ChipIconFallback = null!;
        public ImageSource? ChipIconSource;
        public Border ChipClose = null!;
        public Border ChipDivider = null!;
        public ContentControl ChipFocus = null!;
        public TextBlock ChipTipText = null!;
        public string ChipTipContent = string.Empty;
        public bool Hovered;

        /// <summary>Freezes the terminal grids while an animation drives their size.</summary>
        public void SuspendPaneLayout()
        {
            if (Root is not null) VisitPanes(Root, pane => pane.SuspendLayout());
        }

        public void ResumePaneLayout()
        {
            if (Root is not null) VisitPanes(Root, pane => pane.ResumeLayout());
        }

        // Strip displacement: render-only translation, so moving a chip never triggers a
        // layout pass. It always holds the value currently on screen.
        public double ShiftX;
        public double ShiftY;
    }

    /// <summary>One chip on its way to a new displacement, eased by the strip itself.</summary>
    private sealed class ChipSpring
    {
        public Tab? Tab;
        public double FromX, FromY, ToX, ToY;
        public DateTime Start;
    }

    private sealed class PaletteItem
    {
        public Border Row = null!;
        public TextBlock Title = null!;
        public string Label = string.Empty;
        public Action? Action;
        public bool Submenu;
        public string Value = string.Empty;
        public bool Light;
    }

    /// <summary>The rail's width before it is dragged; the config carries it afterwards.</summary>
    private const double SidebarWidthDefault = 240;

    /// <summary>How narrow the rail can be dragged when it has its top row to itself.</summary>
    private const double SidebarWidthMin = 160;

    /// <summary>Size of the rail's own buttons (new tab, palette, the merged menu).</summary>
    private const double ToolButtonWidth = 30;
    private const double ToolButtonHeight = 28;

    /// <summary>Size of the caption buttons drawn at the window's right edge.</summary>
    private const double CaptionButtonWidth = 46;
    /// <summary>The top layout's title bar: 6px above the tabs plus their 32px.</summary>
    private const double HeaderHeight = 38;

    /// <summary>The band the caption buttons use in the side layouts: the pane header's 32px.</summary>
    private const double SideHeaderHeight = 32;

    /// <summary>Where that band starts: the island's 6px margin plus the pane frame's 1px border.</summary>
    private const double SideHeaderInset = 7;

    /// <summary>The pane area never shrinks below this while the rail is dragged wider.</summary>
    private const double ContentWidthMin = 320;

    /// <summary>
    /// Width of the strip that turns the pane's own border into the rail's splitter. It
    /// straddles the boundary, so most of it lies in the gap and only a couple of DIP reach
    /// over the pane's edge.
    /// </summary>
    private const double SidebarSplitterWidth = 10;

    /// <summary>How much of the splitter reaches over the pane's edge.</summary>
    private const double SidebarSplitterOverlap = 2;

    private sealed class ExitAnimation
    {
        public TerminalPane? Pane;
        public DispatcherQueueTimer? Timer;
    }

    private readonly Config _config;
    private TabPlacement _placement;
    private readonly DispatcherQueue? _dispatcher;

    private Window? _window;
    private AppWindow? _appWindow;
    private Grid _root = null!;
    private Grid _shell = null!;
    private Grid _body = null!;
    private Border _header = null!;
    private Grid _headerInner = null!;
    private Border? _sidebar;
    private ResizeGrip? _sidebarSplitter;
    private double _sidebarWidth = SidebarWidthDefault;
    private bool _resizingSidebar;
    private double _resizeStartWidth;
    private double _resizeCursorX;
    private Border _contentCard = null!;
    private Grid _contentStack = null!;
    private FontIcon? _maximizeIcon;
    private Border? _closeCaption;
    private TextBlock? _minimizeTip;
    private TextBlock? _maximizeTip;
    private TextBlock? _closeTip;
    private Grid _surfaceHost = null!;
    private StackPanel? _topTabHost;
    private StackPanel? _tabColumn;
    private StackPanel _chipHost = null!;
    private StackPanel? _headerSideTools;
    private ScrollViewer? _tabScroller;
    private StackPanel? _stripHost;
    private TranslateTransform? _stripTransform;
    private double _stripOffset;
    // One-shot: scroll the active chip into view only after the active tab changed (or a tab
    // was added/removed), never on ordinary repaints, otherwise it fights the wheel.
    private bool _ensureActiveVisible;
    private bool _headersQueued;
    private IntPtr _hwnd;

    // "confirm-close-surface": the window close is cancelled once, then a dialog decides.
    private bool _allowClose;
    private bool _closePromptOpen;

    private Grid _paletteLayer = null!;
    private Border _palettePanel = null!;
    private TextBlock? _paletteBackRow;
    private TextBox _paletteInput = null!;
    private StackPanel _paletteRows = null!;
    private TextBlock? _paletteEmpty;
    private readonly List<PaletteItem> _paletteItems = new();
    private readonly List<PaletteItem> _paletteWindowItems = new();
    private readonly List<PaletteItem> _paletteThemeItems = new();
    private List<PaletteItem>? _paletteActiveSub;
    private bool _paletteSubmenu;
    private bool _paletteThemesBuilt;
    private bool _paletteAllowedLight;
    private bool _themePreviewActive;
    private bool _suppressPalettePreview;
    private ScrollViewer? _paletteScroller;
    private readonly List<(string Name, FontIcon Check)> _paletteThemeNameChecks = new();
    private Border? _paletteChip;
    private TextBlock? _paletteChipText;
    private PaletteItem? _densityRow;
    private bool _paletteOpenedByPress;
    private string _paletteRootPlaceholder = string.Empty;
    private int _paletteSelected;
    private TextBlock? _paletteWindowValue;
    private TextBlock? _paletteThemeValue;
    private readonly List<FontIcon> _paletteWindowChecks = new();
    private readonly List<FontIcon> _paletteThemeChecks = new();

    private Control _inputAnchor = null!;
    private ImeHost? _imeHost;
    private DispatcherQueueTimer? _imeTimer;

    // Every themed brush is created once and shared: switching the theme only has to
    // update the Color of these instances, and every element that captured a brush
    // repaints with the new palette.
    private readonly List<(Func<Color> Color, SolidColorBrush Brush)> _themedBrushes = new();
    private FontFamily? _defaultTitleFont;

    private readonly List<ChipSpring> _chipSprings = new();
    private DispatcherQueueTimer? _chipSpringTimer;

    private readonly List<Tab> _tabs = new();
    private int _active;
    private bool _sidebarCollapsed;
    private bool _firstSurface = true;

    private readonly List<ExitAnimation> _exitAnimations = new();
    private ZoomTransition? _zoomTransition;
    private readonly List<TerminalPane> _closingPanes = new();
    private Tab? _relayouting;

    /// <summary>True while this workspace applies a config reload (its own or a peer's).</summary>
    private bool _applyingConfig;

    /// <summary>
    /// Language this window's chrome was last labelled in. It is tracked per window because
    /// Strings.Current is process-wide: the window that reloads switches it first, so the
    /// other windows would otherwise conclude that nothing changed.
    /// </summary>
    private Language _appliedLanguage;

    private bool _dragging;
    private PointInt32 _dragCursor;
    private PointInt32 _dragOrigin;
    private ulong _lastHeaderPress;

    private Tab? _dragTab;
    private bool _dragArmed;
    private bool _dragMoving;
    private bool _hasPendingTabMove;
    private int _pendingTabFrom;
    private int _pendingTabTo;
    private double _dragPressX;
    private double _dragPressY;
    private int _dragTarget;
    private double _dragHomeX, _dragHomeY;
    private double _dragFollowX, _dragFollowY;
    private Point _dragPointerView;
    private Point _dragPointerContent;
    private double _dragScrollOffset;
    private DispatcherQueueTimer? _dragScrollTimer;
    private DispatcherQueueTimer? _dropTimer;

    private bool _inputHandlersInstalled;
    private TerminalPane? _imeCompositionPane;

    public Workspace(Config config)
    {
        _config = config;
        _placement = PlacementFrom(config.TabPosition);
        _sidebarWidth = Math.Max(MinSidebarWidth(), SidebarWidthFromConfig());
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appliedLanguage = Strings.Current;
    }

    /// <summary>The config value for the tab rail; anything unknown means the top.</summary>
    private static TabPlacement PlacementFrom(string value) => value switch
    {
        "left" => TabPlacement.Left,
        "right" => TabPlacement.Right,
        _ => TabPlacement.Top,
    };

    // ---------------- attach ----------------

    public void Attach(Grid root, Window window)
    {
        _root = root;
        _window = window;

        // Borderless-over-its-frame window: the title bar is drawn by this class, so the
        // client area covers the whole window while the frame keeps its shadow, rounding
        // and resize edges.
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                presenter.IsResizable = true;
            }
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.Attach.presenter", error);
        }

        BuildChrome();

        try
        {
            _appWindow = window.AppWindow;
            _hwnd = Win32Interop.GetWindowFromWindowId(_appWindow.Id);
            // The beep for Alt chords comes from WinUI's input-site windows; retried by the
            // IME timer because they may not exist yet.
            InputSite.Install(_hwnd);
            // IMM32 is hooked on the same input-site windows: they are what Windows gives
            // focus and IME messages to.
            _imeHost = new ImeHost(_hwnd, IsTerminalKeyboardFocus, CommitImeText,
                ShowImeComposition, ImeCaretOnScreen);
            _imeHost.Install();
            ApplyWindowTheme();
            _appWindow.Closing += OnAppWindowClosing;
            _appWindow.Changed += (_, args) =>
            {
                if (args.DidSizeChange) UpdateMaximizeIcon();
            };
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.Attach.appWindow", error);
        }

        NewTab();
        Activate(0);
        UpdateStatus();
        ApplyTabBarVisibility();
    }

    /// <summary>
    /// The focused pane's current grid and cell size, used by "window-width" / "window-height"
    /// to grow the window by whole cells without having to model the chrome.
    /// </summary>
    public bool ActiveGridMetrics(out int columns, out int rows, out double cellWidth, out double cellHeight)
    {
        columns = rows = 0;
        cellWidth = cellHeight = 0;
        if (_tabs.Count == 0 || _tabs[_active].Focused is not { } pane) return false;

        CellMetrics metrics = pane.CellMetricsDip;
        if (metrics.Width == 0 || metrics.Height == 0) return false;
        if (!pane.LaidOut) return false;

        columns = pane.Columns;
        rows = pane.Rows;
        cellWidth = metrics.Width;
        cellHeight = metrics.Height;
        return true;
    }

    /// <summary>
    /// True when <paramref name="source"/> sits inside <paramref name="ancestor"/> in the
    /// visual tree. The walk starts at the pressed element, so a press on a palette row
    /// counts as a press inside the palette.
    /// </summary>
    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, ancestor)) return true;
            source = source is UIElement element ? VisualTreeHelper.GetParent(element) : null;
        }
        return false;
    }

    // ---------------- config reload ----------------

    /// <summary>
    /// Re-reads %APPDATA%\gui\config and applies the settings that can change while the app
    /// is running: theme, window theme, palette, font, split dimming and the tab rail.
    /// Settings that need a new shell (command, working-directory) are picked up by new
    /// panes and tabs; existing shells keep running.
    /// </summary>
    public void ReloadConfig()
    {
        if (_applyingConfig) return;
        _applyingConfig = true;
        try
        {
            ReloadConfigCore();
        }
        finally
        {
            _applyingConfig = false;
        }

        // Palette-saved settings (theme, tab rail, density) belong to the whole process: the
        // other windows re-read the file so every window lands on the same state.
        App.ReloadOtherWindows(this);
        Diag.Log("config", "reloaded");
    }

    /// <summary>
    /// Reload requested by another window. It must not bounce the reload back, or the two
    /// windows would keep reloading each other.
    /// </summary>
    internal void ReloadFromPeer()
    {
        if (_applyingConfig) return;
        _applyingConfig = true;
        try
        {
            ReloadConfigCore();
        }
        finally
        {
            _applyingConfig = false;
        }
        Diag.Log("config", "reloaded (peer)");
    }

    private void ReloadConfigCore()
    {
        Config fresh = Config.Load();
        _config.CopyFrom(fresh);

        // Split panes keep their own copy of the config (their session command and working
        // directory are per pane), so the reload has to be pushed into them before anything
        // re-reads it. The pane that shares the live instance returns immediately.
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.SyncConfig(_config));

        // The language is switchable at runtime. It is selected before the theme and the tab
        // rail are refreshed so any chrome rebuild already uses the new strings; the pass at
        // the end then re-labels the elements that survive.
        bool languageChanged = _config.Language != _appliedLanguage;
        if (languageChanged)
        {
            Strings.Use(_config.Language);
            _appliedLanguage = _config.Language;
        }

        Theme.Light = fresh.LightTheme;
        Theme.WindowLight = fresh.WindowLight;
        Theme.Terminal = fresh.TerminalTheme;

        RefreshTheme();

        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane =>
            {
                pane.ApplyFont();
                pane.ApplyScrollSettings();
                pane.ApplyClipboardSettings();
            });

        // The chrome is rebuilt here, so a failure must not take the app down with it.
        SetSidebarWidth(SidebarWidthFromConfig());
        Diag.Guard("ws.placement", () => ApplyTabPlacement(PlacementFrom(fresh.TabPosition)));

        // The density is a live setting like the others: an edit to `compact` has to reach the
        // island margin, the split gaps, the pane frames and the tab chips without a restart.
        Diag.Guard("ws.density", () => SetDensity(_config.Compact));
        Diag.Guard("ws.titleFont", ApplyTitleFont);

        if (languageChanged)
            Diag.Guard("ws.language", ApplyLanguage);
    }

    /// <summary>
    /// Re-labels everything this app draws itself: pane tooltips, tab automation names and
    /// the command palette. Terminal content and the running shells are untouched.
    /// </summary>
    private void ApplyLanguage()
    {
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.ApplyLanguage());

        UpdateHeaders();

        // BuildPalette creates a fresh layer, so the old one has to leave the tree first.
        if (_paletteLayer is not null)
            _contentStack.Children.Remove(_paletteLayer);
        BuildPalette();
    }

    /// <summary>
    /// Moves the tab rail and remembers the choice, so it survives a restart. The value is
    /// the same one the config file uses ("top" / "left" / "right").
    /// </summary>
    private void SetTabPlacement(string value)
    {
        TabPlacement placement = value switch
        {
            "left" => TabPlacement.Left,
            "right" => TabPlacement.Right,
            _ => TabPlacement.Top,
        };

        if (_config.TabPosition != value) Config.SaveValue("tab-position", value);
        _config.TabPosition = value;

        ApplyTabPlacement(placement);

        // The rail is a process-wide setting: the other windows rebuild their chrome too.
        if (!_applyingConfig) App.ReloadOtherWindows(this);
    }

    /// <summary>
    /// Switches between floating islands and the edge-to-edge compact surface. The choice is
    /// persisted like the other chrome settings, and everything that reads it (island margin,
    /// split gaps, pane frames, tab chips, rail padding) is refreshed here.
    /// </summary>
    private void SetDensity(bool compact)
    {
        if (_config.Compact != compact)
        {
            _config.Compact = compact;
            Config.SaveValue("compact", compact ? "true" : "false");
        }

        _contentCard.Margin = ContentCardMargin();
        _contentCard.CornerRadius = new CornerRadius(CompactDensity ? 0 : 12);
        _surfaceHost.Margin = SurfaceMargin();
        if (_sidebar is not null) _sidebar.Padding = SidebarPadding();
        if (_header is not null)
            _header.Margin = HeaderMargin(_placement != TabPlacement.Top);

        // The close button's corner tracks the island, which compact mode squares off.
        UpdateCloseCorner();

        foreach (Tab tab in _tabs)
        {
            tab.Surface.RowSpacing = SurfaceGap;
            tab.Surface.ColumnSpacing = SurfaceGap;
            Relayout(tab);
            StyleChip(tab);
        }

        if (_densityRow is not null) _densityRow.Title.Text = DensityLabel();

        // Density is persisted and belongs to the whole process, like the tab rail.
        if (!_applyingConfig) App.ReloadOtherWindows(this);
    }

    private string DensityLabel() => CompactDensity ? Strings.CompactOn : Strings.CompactOff;

    /// <summary>
    /// Moves the tab rail to the top, the left or the right. The chrome (header, tab rail,
    /// sidebar) is rebuilt for the new placement; the tabs, their chips and the running
    /// shells are carried over to it.
    /// </summary>
    private void ApplyTabPlacement(TabPlacement placement)
    {
        if (placement == _placement) return;
        _placement = placement;

        // Each side keeps its own width and its own floor: a right-hand rail shares its top
        // row with the caption buttons. Read the new side's saved width and clamp it with the
        // *new* placement before the chrome is rebuilt, otherwise a left rail dragged down to
        // 160 lands on the right at 160 and its buttons collide with the caption buttons.
        SetSidebarWidth(SidebarWidthFromConfig());
        if (SidebarWidthFromConfig() != _sidebarWidth) SaveSidebarWidth();

        // The chips belong to the tabs, so they travel with them; they have to leave their
        // old host first, an element can only have one parent.
        var chips = new List<UIElement>();
        foreach (Tab tab in _tabs)
        {
            UIElement chip = tab.ChipFocus;
            if (VisualTreeHelper.GetParent(chip) is Panel owner) owner.Children.Remove(chip);
            chips.Add(chip);
        }

        // The mounted shell has to leave the old chrome as well: an element that is
        // unloaded together with its tree cannot be added to a new one afterwards.
        if (_tabs.Count > 0 &&
            VisualTreeHelper.GetParent(_tabs[_active].Surface) is Panel surfaceOwner)
        {
            surfaceOwner.Children.Remove(_tabs[_active].Surface);
        }

        // The strip scroll offset belongs to the old rail.
        _stripOffset = 0;

        _root.Children.Clear();
        BuildChrome();

        foreach (UIElement chip in chips) _chipHost.Children.Add(chip);
        foreach (Tab tab in _tabs) StyleChip(tab);

        // The running shells move into the new surface host.
        if (_tabs.Count > 0) Activate(_active);

        if (_placement != TabPlacement.Top) SetSidebarCollapsed(_sidebarCollapsed);

        UpdateHeaders();
        UpdateInputAnchor();
        ApplyTabBarVisibility();
        Diag.Log("tab", $"placement {_placement}");
    }

    /// <summary>
    /// "window-show-tab-bar = never" hides the tab chips. The header itself stays so the
    /// window can still be dragged and the caption buttons keep their place.
    /// </summary>
    private void ApplyTabBarVisibility()
    {
        if (_chipHost is not null)
            _chipHost.Visibility = _config.ShowTabBar ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- theme brushes ----------------

    private TerminalBackdrop? _backdrop;
    private uint _backdropTint;
    private float _backdropOpacity;
    private bool _backdropAcrylic;

    /// <summary>
    /// "background-opacity" needs something behind the panes. Like Windows Terminal, the
    /// translucency is a single window backdrop tinted with the terminal background: acrylic
    /// when "background-blur" is on (or on Windows 10, which has no unblurred transparency),
    /// a plain translucent brush on Windows 11 with blur off. Panes then stay transparent, so
    /// the configured opacity is applied once instead of stacking on the system acrylic tint.
    /// </summary>
    private void ApplyBackgroundOpacity()
    {
        bool translucent = _config.BackgroundOpacity < 0.999f;

        bool acrylicSupported = false;
        if (translucent)
        {
            try
            {
                acrylicSupported =
                    Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
            }
            catch
            {
                acrylicSupported = false;
            }
        }

        // "background-blur" selects acrylic; without it Windows 11 can use a plain translucent
        // brush. Windows 10 has no unblurred transparency, so it keeps acrylic there, exactly
        // like Windows Terminal. TerminalBackdrop falls back to acrylic if the plain brush
        // cannot be applied.
        bool windows11 = Environment.OSVersion.Version.Build >= 22000;
        bool acrylic = translucent && (_config.BackgroundBlur || !windows11);
        bool plain = translucent && !acrylic;
        bool wantBackdrop = translucent && _window is not null && (plain || acrylicSupported);

        // Panes read this while they rebuild their surface: the backdrop owns the tint.
        _config.SingleLayerTransparency = wantBackdrop;

        Color tint = Theme.TermSurfaceBg();
        if (wantBackdrop)
        {
            if (_backdrop is null || _backdropAcrylic != acrylic ||
                _backdropOpacity != _config.BackgroundOpacity || _backdropTint != Rgba(tint))
            {
                _backdrop = new TerminalBackdrop(tint, _config.BackgroundOpacity, acrylic);
                _backdropTint = Rgba(tint);
                _backdropOpacity = _config.BackgroundOpacity;
                _backdropAcrylic = acrylic;
                try
                {
                    _window!.SystemBackdrop = _backdrop;
                }
                catch (Exception error)
                {
                    Diag.LogException("ws.backdrop", error);
                    _backdrop = null;
                    _config.SingleLayerTransparency = false;
                }
            }
        }
        else if (_backdrop is not null)
        {
            _backdrop = null;
            try
            {
                if (_window is not null) _window.SystemBackdrop = null;
            }
            catch (Exception error)
            {
                Diag.LogException("ws.backdrop", error);
            }
        }

        // The backdrop is the window background: clear the mica and the scrim so it is not
        // hidden, and let the panes stay transparent for the same reason.
        bool active = _config.SingleLayerTransparency;
        _root.Background = active ? ClearBrush() : WindowMicaBrush();
        if (_shell is not null)
            _shell.Background = active ? ClearBrush() : ScrimBrush();
        if (_contentCard is not null)
            _contentCard.Background = translucent ? ClearBrush() : CardFillBrush();
    }

    private static uint Rgba(Color color) =>
        ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private SolidColorBrush ThemedBrush(Func<Color> color)
    {
        foreach (var entry in _themedBrushes)
        {
            if (ReferenceEquals(entry.Color, color)) return entry.Brush;
        }
        var created = (color, new SolidColorBrush(color()));
        _themedBrushes.Add(created);
        return created.Item2;
    }

    private void RefreshThemedBrushes()
    {
        foreach (var entry in _themedBrushes)
            entry.Brush.Color = entry.Color();
    }

    private SolidColorBrush ClearBrush() => new(Color.FromArgb(0, 0, 0, 0));

    private Brush WindowMicaBrush() => ThemedBrush(Theme.WindowMica);
    private Brush ScrimBrush() => ThemedBrush(Theme.Scrim);
    private Brush PanelStrokeBrush() => ThemedBrush(Theme.Stroke);
    private Brush StrokeStrongBrush() => ThemedBrush(Theme.StrokeStrong);
    private Brush CardFillBrush() => ThemedBrush(Theme.ContentBg);

    /// <summary>The active tab and side row: the design fills them with the terminal background.</summary>
    private Brush TermBgBrush() => ThemedBrush(Theme.TermSurfaceBg);
    private Brush TextStrongBrush() => ThemedBrush(Theme.TextStrong);
    private Brush TextBodyBrush() => ThemedBrush(Theme.TextBody);
    private Brush TextFaintBrush() => ThemedBrush(Theme.TextFaint);
    private Brush AccentBrush() => ThemedBrush(Theme.Accent);
    private Brush AccentSoftBrush() => ThemedBrush(Theme.AccentSoft);
    private Brush AccentStrokeBrush() => ThemedBrush(Theme.AccentStroke);
    private Brush ChipHoverBrush() => ThemedBrush(Theme.ChipHover);
    private Brush ChromeTextStrongBrush() => ThemedBrush(Theme.ChromeTextStrong);
    private Brush ChromeTextBodyBrush() => ThemedBrush(Theme.ChromeTextBody);
    private Brush ChromeTextFaintBrush() => ThemedBrush(Theme.ChromeTextFaint);
    private Brush ChromeChipHoverBrush() => ThemedBrush(Theme.ChromeChipHover);
    private Brush ChromeCloseButtonHoverBrush() => ThemedBrush(Theme.ChromeCloseButtonHover);
    private Brush ChromeStrokeBrush() => ThemedBrush(Theme.ChromeStroke);
    private Brush ChromeHeaderFillBrush() => ThemedBrush(Theme.ChromeHeaderFill);
    private Brush CloseHoverBrush() => ThemedBrush(Theme.CloseHover);

    // ---------------- chrome ----------------

    private static void AddRow(Grid grid, double height, bool star)
    {
        grid.RowDefinitions.Add(new RowDefinition
        {
            Height = star ? new GridLength(1, GridUnitType.Star) : new GridLength(height),
        });
    }

    private static void AddAutoRow(Grid grid) =>
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    private static void AddAutoColumn(Grid grid) =>
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    private static void AddStarColumn(Grid grid) =>
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

    private static TextBlock Label(string text, double size, Brush brush)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// "window-title-font-family" applies to the tab titles this app draws in place of a
    /// native title bar. An unset value keeps the system UI font.
    /// </summary>
    private FontFamily TitleFontFamily()
    {
        _defaultTitleFont ??= new TextBlock().FontFamily;
        string name = _config.TitleFontFamily;
        return string.IsNullOrWhiteSpace(name) ? _defaultTitleFont : new FontFamily(name);
    }

    private void ApplyTitleFont()
    {
        FontFamily font = TitleFontFamily();
        foreach (Tab tab in _tabs)
        {
            tab.ChipText.FontFamily = font;
            tab.ChipTipText.FontFamily = font;
            PinChipTitleWidth(tab);
        }
    }

    private static FontIcon Glyph(char code, double size, Brush brush)
    {
        // MDL2 ships with Windows 10 and 11; "Segoe Fluent Icons" is Windows 11 only and
        // renders as tofu elsewhere.
        return new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Glyph = code.ToString(),
            FontSize = size,
            Foreground = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>
    /// Rounds the close button only where it covers the pane's own top-right corner, which is
    /// the side layouts. Two cases keep a square corner: the top layout (a normal title bar)
    /// and a right-hand rail that is open, because then the corner belongs to the rail and
    /// the captions sit over chrome, not over the pane.
    /// </summary>
    /// <summary>
    /// Hover label for a caption button: the same shape the pane's action buttons use, so the
    /// chrome reads consistently. Returns the label so the maximize button can follow the
    /// window state.
    /// </summary>
    private static TextBlock CaptionTip(Border button, string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(Theme.TextStrong()),
        };
        var tip = new StackPanel { Spacing = 2 };
        tip.Children.Add(label);
        ToolTipService.SetToolTip(button, tip);
        return label;
    }

    /// <summary>
    /// Labels the three window buttons; the middle one says "maximize" or "restore"
    /// depending on the window. Re-run when the window state or the UI language changes.
    /// </summary>
    private void UpdateCaptionTips()
    {
        if (_minimizeTip is not null) _minimizeTip.Text = Strings.Minimize;
        if (_closeTip is not null) _closeTip.Text = Strings.CloseWindow;
        if (_maximizeTip is null) return;

        bool maximized = _window is not null &&
                         _window.AppWindow.Presenter is OverlappedPresenter presenter &&
                         presenter.State == OverlappedPresenterState.Maximized;
        _maximizeTip.Text = maximized ? Strings.Restore : Strings.Maximize;
    }

    private void UpdateCloseCorner()
    {
        if (_closeCaption is null) return;

        // The button's corner follows the island's: 12 in the cozy layouts, and square in
        // compact mode, where the pane runs to the window edge and nothing rounds it.
        bool onPaneCorner = !CompactDensity
            && _placement != TabPlacement.Top
            && !(_placement == TabPlacement.Right && !_sidebarCollapsed);

        _closeCaption.CornerRadius = onPaneCorner
            ? new CornerRadius(0, 12, 0, 0)
            : new CornerRadius(0);
    }

    private Border CaptionButton(char code, double width, double height, Brush glyphBrush)
    {
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(0),
            // A null background is not hit-testable, so hover would only fire on the small
            // glyph in the middle. A transparent brush keeps the whole button hoverable.
            Background = ClearBrush(),
            Child = Glyph(code, 10, glyphBrush),
        };
    }

    private Border ToolButton(char code, Brush foreground)
    {
        return new Border
        {
            Width = ToolButtonWidth,
            Height = ToolButtonHeight,
            CornerRadius = new CornerRadius(7),
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Glyph(code, 13, foreground),
        };
    }

    private Border ToolButton(char code) => ToolButton(code, TextFaintBrush());

    private Border ToolButton(Path icon)
    {
        return new Border
        {
            Width = ToolButtonWidth,
            Height = ToolButtonHeight,
            CornerRadius = new CornerRadius(7),
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Child = icon,
        };
    }

    /// <summary>
    /// The design's panel glyph: a rounded frame split into a narrow rail and a wide pane.
    /// It is drawn as a path instead of a font glyph, so it matches the other hand-drawn
    /// icons and cannot turn into tofu.
    /// </summary>
    private static Path PanelIcon(Brush stroke, double size, bool railOnLeft)
    {
        double scale = size / 16.0;
        double left = 2 * scale, top = 2 * scale, right = 14 * scale, bottom = 14 * scale;
        double radius = 2.5 * scale;
        double split = railOnLeft ? 6 * scale : 10 * scale;

        var geometry = new PathGeometry();

        var frame = new PathFigure { IsClosed = true, StartPoint = new Point(left + radius, top) };
        frame.Segments.Add(new LineSegment { Point = new Point(right - radius, top) });
        frame.Segments.Add(new ArcSegment
        {
            Size = new Size(radius, radius),
            RotationAngle = 0,
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
            Point = new Point(right, top + radius),
        });
        frame.Segments.Add(new LineSegment { Point = new Point(right, bottom - radius) });
        frame.Segments.Add(new ArcSegment
        {
            Size = new Size(radius, radius),
            RotationAngle = 0,
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
            Point = new Point(right - radius, bottom),
        });
        frame.Segments.Add(new LineSegment { Point = new Point(left + radius, bottom) });
        frame.Segments.Add(new ArcSegment
        {
            Size = new Size(radius, radius),
            RotationAngle = 0,
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
            Point = new Point(left, bottom - radius),
        });
        frame.Segments.Add(new LineSegment { Point = new Point(left, top + radius) });
        frame.Segments.Add(new ArcSegment
        {
            Size = new Size(radius, radius),
            RotationAngle = 0,
            IsLargeArc = false,
            SweepDirection = SweepDirection.Clockwise,
            Point = new Point(left + radius, top),
        });
        geometry.Figures.Add(frame);

        // The divider between the rail and the pane.
        var divider = new PathFigure { IsClosed = false, StartPoint = new Point(split, top) };
        divider.Segments.Add(new LineSegment { Point = new Point(split, bottom) });
        geometry.Figures.Add(divider);

        return new Path
        {
            Width = size,
            Height = size,
            Stroke = stroke,
            StrokeThickness = size * 1.4 / 16.0,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Data = geometry,
        };
    }

    /// <summary>Swaps the background on hover; the over-brush is resolved live from the theme.</summary>
    private void Hoverable(Border element, Func<Brush> over)
    {
        element.PointerEntered += (_, _) => element.Background = over();
        element.PointerExited += (_, _) => element.Background = ClearBrush();
    }

    private void CaptionHover(Border button, Func<Brush> overFill, Func<Brush> overIcon)
    {
        var icon = button.Child as FontIcon;
        button.PointerEntered += (_, _) =>
        {
            button.Background = overFill();
            if (icon is not null) icon.Foreground = overIcon();
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = ClearBrush();
            if (icon is not null) icon.Foreground = ChromeTextBodyBrush();
        };
    }

    private void BuildChrome()
    {
        _shell = new Grid { Background = ScrimBrush() };
        AddAutoRow(_shell);
        AddStarRow(_shell);

        BuildHeader();

        _body = new Grid();
        Grid.SetRow(_body, 1);
        _shell.Children.Add(_body);

        BuildSurface();

        if (_placement == TabPlacement.Top)
        {
            // The top layout keeps a real title bar: the strip sits above the body and the
            // tabs dock to the island's top edge.
            Grid.SetRow(_header, 0);
            _shell.Children.Add(_header);
            _body.Children.Add(_contentCard);
        }
        else
        {
            bool leftSide = _placement == TabPlacement.Left;
            _body.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = leftSide ? GridLength.Auto : new GridLength(1, GridUnitType.Star),
            });
            _body.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = leftSide ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });

            BuildSidebar();
            Grid.SetColumn(_sidebar!, leftSide ? 0 : 1);
            Grid.SetColumn(_contentCard, leftSide ? 1 : 0);
            _body.Children.Add(_sidebar!);
            _body.Children.Add(_contentCard);

            // The splitter goes in last so it sits above the pane it borders.
            BuildSidebarSplitter(leftSide);

            // The side layouts have no title bar at all: the island starts at the window's
            // top edge and this strip only carries the caption buttons, laid over the band
            // the pane header occupies so they line up with the pane's action buttons.
            Grid.SetColumnSpan(_header, _body.ColumnDefinitions.Count);
            _body.Children.Add(_header);
        }

        _root.Background = WindowMicaBrush();
        _root.Children.Add(_shell);
        ApplyBackgroundOpacity();

        // Keyboard anchor: invisible, but it is the element XAML routes keys and text to.
        // It is a plain Control, not a TextBox, because text and IME composition are handled
        // by ImeHost through WM_CHAR / WM_IME_*.
        _inputAnchor = new ContentControl
        {
            Opacity = 0.0,
            Width = 8,
            Height = 16,
            IsTabStop = true,
            UseSystemFocusVisuals = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _root.Children.Add(_inputAnchor);

        // The anchor sits above the whole window at the cursor cell. A press on it never
        // reaches the pane's canvas (the canvas is inside the shell, not an ancestor of the
        // anchor), so forward it to the focused pane and let selection start there. A plain
        // Control does not claim the pointer, but the handlers stay for when the anchor is
        // hit-testable.
        _inputAnchor.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible) return;
            if (_tabs.Count == 0) return;
            _tabs[_active].Focused?.BeginSelectionFromAnchor(args);
        }), true);
        _inputAnchor.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (_tabs.Count == 0) return;
            _tabs[_active].Focused?.MoveSelectionFromAnchor(args);
        }), true);
        _inputAnchor.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (_tabs.Count == 0) return;
            _tabs[_active].Focused?.EndSelectionFromAnchor(args);
        }), true);

        EnsureInputHandlers();

        EnsureTimers();

        BuildPalette();
    }

    /// <summary>
    /// The window-level input handlers live on the root, which survives a chrome rebuild;
    /// registering them again would fire every shortcut once per rebuild.
    /// </summary>
    private void EnsureInputHandlers()
    {
        if (_inputHandlersInstalled) return;
        _inputHandlersInstalled = true;

        // Keys are handled at the window root, not on the keyboard anchor: the anchor only
        // gives XAML a focus target, and any other focus target (a tab chip, the palette, a
        // caption button) would otherwise swallow the shortcuts.
        _root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (OnKey(args)) args.Handled = true;
        }), true);

        // Clicking away ends the composition; a terminal discards the raw intermediate
        // string instead of sending it to the shell.
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (_imeHost is { IsComposing: true }) _imeHost.CancelComposition();
        }), true);

        // A press outside the palette dismisses it. The palette layer covers the body only,
        // so the title bar, the caption buttons and the drag strip stay reachable; a press
        // there would otherwise leave the palette open. Presses inside the layer keep their
        // own behaviour (the empty area closes it, the card swallows them).
        // handledEventsToo is required because the chrome and the card mark their presses
        // handled.
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (_paletteLayer is null || _paletteLayer.Visibility != Visibility.Visible) return;
            if (IsInsidePalettePanel(args)) return;

            // The press that opens the palette — the rail's or the header's search button —
            // keeps bubbling to this handler after its own handler ran, and hiding here would
            // close what it just opened.
            if (_paletteOpenedByPress)
            {
                _paletteOpenedByPress = false;
                return;
            }

            HideCommandPalette();
        }), true);
    }

    /// <summary>
    /// True when the press landed on the palette card. The test is geometric rather than a
    /// walk up the visual tree: the card holds a search box, and the box's own template
    /// elements are not children of the card in the visual tree, so a walk from the pressed
    /// element can lose its way and report a press on the search box as an outside press.
    /// </summary>
    private bool IsInsidePalettePanel(PointerRoutedEventArgs args)
    {
        if (_palettePanel is null) return false;
        Point position = args.GetCurrentPoint(_palettePanel).Position;
        return position.X >= 0.0 && position.Y >= 0.0 &&
               position.X <= _palettePanel.ActualWidth && position.Y <= _palettePanel.ActualHeight;
    }

    /// <summary>
    /// The timers outlive the chrome: switching the tab rail rebuilds it, and a second set of
    /// timers would tick against elements that no longer exist.
    /// </summary>
    private void EnsureTimers()
    {
        if (_dispatcher is null || _imeTimer is not null) return;

        _imeTimer = _dispatcher.CreateTimer();
        _imeTimer.Interval = TimeSpan.FromMilliseconds(250);
        _imeTimer.Tick += (_, _) =>
        {
            Diag.Guard("ws.ime", UpdateInputAnchor);
            InputSite.Install(_hwnd);
            _imeHost?.Install();
        };
        _imeTimer.Start();

        // Keeps the strip scrolling while a dragged chip is held against an edge.
        _dragScrollTimer = _dispatcher.CreateTimer();
        _dragScrollTimer.Interval = TimeSpan.FromMilliseconds(16);
        _dragScrollTimer.Tick += (_, _) => Diag.Guard("ws.tabDragScroll", TickTabDragScroll);

        // Reordering while the push-aside springs are still running snaps the other
        // chips forward; hold the collection change until they settle.
        _dropTimer = _dispatcher.CreateTimer();
        _dropTimer.IsRepeating = false;
        _dropTimer.Interval = TimeSpan.FromMilliseconds(80);
        _dropTimer.Tick += (_, _) =>
        {
            _dropTimer?.Stop();
            ApplyPendingTabMove();
        };
    }

    private static void AddStarRow(Grid grid) =>
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

    private void BuildHeader()
    {
        // The top layout draws a real title bar strip; the side layouts have none, so this
        // element only carries the caption buttons there, floating over the pane header's
        // band: no background (a transparent brush would still take the clicks meant for the
        // pane underneath) and a z-index above the body so the buttons stay on top.
        bool sideRail = _placement != TabPlacement.Top;

        // 6px below the window's top edge (the island's own margin) plus the pane frame's 1px
        // border is where the pane header's content starts; the caption buttons use that same
        // band so their glyphs share the action buttons' centre line.
        double headerHeight = sideRail ? SideHeaderHeight : HeaderHeight;
        _header = new Border
        {
            Height = headerHeight,
            // The caption buttons stay inside the island: its 6px right margin is repeated
            // here so the close button's hover no longer spills past the pane's edge.
            // Compact mode has no island inset, so the strip rises with the pane header and
            // runs flush to the window's right edge, where the close button hugs the border.
            Margin = HeaderMargin(sideRail),
            VerticalAlignment = sideRail ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            Background = sideRail ? null : ChromeHeaderFillBrush(),
            BorderThickness = new Thickness(0),
        };
        Canvas.SetZIndex(_header, 1);
        _header.PointerPressed += OnHeaderPointerPressed;
        _header.PointerMoved += (_, args) =>
        {
            if (!_dragging) return;
            args.Handled = true;
            DragTo();
        };
        _header.PointerReleased += (_, args) =>
        {
            if (!_dragging) return;
            args.Handled = true;
            _header.ReleasePointerCapture(args.Pointer);
            EndDrag();
        };
        _header.PointerCaptureLost += (_, _) => EndDrag();

        _headerInner = new Grid();
        AddStarColumn(_headerInner);   // tab strip
        AddAutoColumn(_headerInner);   // header tools
        AddAutoColumn(_headerInner);   // caption buttons
        _header.Child = _headerInner;

        if (_placement == TabPlacement.Top)
        {
            _topTabHost = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // The chip carries its own 2px trailing margin, which is the gutter the
                // divider sits in; a panel spacing on top of that would double it.
                Spacing = 0,
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            var strip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 32,
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            strip.Children.Add(_topTabHost);

            // The strip is scrolled by this class, not by the ScrollViewer: XAML's own
            // horizontal scroll of this content fail-fasts (0xC000027B) as soon as the
            // chips overflow. The ScrollViewer is kept purely as a clipping viewport.
            _stripHost = strip;
            _stripTransform = new TranslateTransform();
            strip.RenderTransform = _stripTransform;

            var scroller = new ScrollViewer
            {
                Content = strip,
                Height = 32,
                // The design lines the first chip up with the content island (both 6px from
                // the window edge), so the strip carries no indent of its own; the chips get
                // their 6px from the host margin below.
                Margin = new Thickness(0),
                // The design docks the strip to the bottom of the 38px header: 6px above the
                // chips and no gap below, so the active tab's bottom edge meets the content
                // island's top edge exactly.
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                VerticalScrollMode = ScrollMode.Disabled,
                IsTabStop = false,
            };
            // handledEventsToo: the ScrollViewer's class handler marks the wheel handled even
            // with scrolling disabled, so a plain subscription would never fire.
            scroller.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnStripWheel), true);

            _chipHost = _topTabHost;
            _tabScroller = scroller;
            Grid.SetColumn(scroller, 0);
            _headerInner.Children.Add(scroller);

            // New-tab / search stay pinned to the right of the header, whatever the tab
            // strip's width or scroll offset.
            var topTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 4, 4),
            };

            Border add = ToolButton('\uE710', ChromeTextFaintBrush());
            add.Width = 28;
            Hoverable(add, ChromeChipHoverBrush);
            add.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                RequestNewTab();
            };
            topTools.Children.Add(add);

            // The magnifier opens the command palette, like the rail's own search command;
            // the per-pane find bar stays on Ctrl+Shift+F and the palette's Find row.
            Border palette = ToolButton('\uE721', ChromeTextFaintBrush());
            palette.Width = 28;
            Hoverable(palette, ChromeChipHoverBrush);
            palette.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                ShowCommandPalette();
            };
            topTools.Children.Add(palette);

            Grid.SetColumn(topTools, 1);
            _headerInner.Children.Add(topTools);

            WireTabDrag();
        }

        if (_placement != TabPlacement.Top)
        {
            // With the rail collapsed only the button that brings it back stays: the rail's
            // other actions went away with it (Ctrl+Shift+T / Ctrl+Shift+P still work). It
            // sits on the rail's side of the window and inside the band the pane header
            // occupies, so the two rows read as one line.
            bool railRight = _placement == TabPlacement.Right;
            _headerSideTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Height = 32,
                // 12 = 6 inside the island's edge (so the button is not flush with the
                // pane's border) plus the pane header's own inset; the strip itself already
                // starts at the pane header's band, so no vertical inset is needed.
                Margin = railRight ? new Thickness(0, 0, 12, 0) : new Thickness(12, 0, 0, 0),
                HorizontalAlignment = railRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };

            // The panel glyph shows the tab rail on the side it lives on.
            Border sideToggle = ToolButton(PanelIcon(ChromeTextFaintBrush(), 14, !railRight));
            Hoverable(sideToggle, ChromeChipHoverBrush);
            sideToggle.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                SetSidebarCollapsed(false);
            };
            _headerSideTools.Children.Add(sideToggle);

            Grid.SetColumn(_headerSideTools, 0);
            _headerInner.Children.Add(_headerSideTools);
        }

        // The caption buttons fill the header edge to edge, like real window controls; their
        // glyphs still sit in the 6..38 band so they share the centre line with the tabs, the
        // rail's tool row and the pane header's buttons.
        var captions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0),
        };

        Border minimize = CaptionButton('\uE921', CaptionButtonWidth, headerHeight, ChromeTextBodyBrush());
        Border maximize = CaptionButton('\uE922', CaptionButtonWidth, headerHeight, ChromeTextBodyBrush());
        Border close = CaptionButton('\uE8BB', CaptionButtonWidth, headerHeight, ChromeTextBodyBrush());
        _maximizeIcon = maximize.Child as FontIcon;
        _closeCaption = close;
        UpdateCloseCorner();
        CaptionHover(minimize, ChromeChipHoverBrush, ChromeTextStrongBrush);
        CaptionHover(maximize, ChromeChipHoverBrush, ChromeTextStrongBrush);
        CaptionHover(close, CloseHoverBrush, () => new SolidColorBrush(Colors.White));

        // The three window buttons carry the same one-line hover label the pane actions use.
        _minimizeTip = CaptionTip(minimize, Strings.Minimize);
        _maximizeTip = CaptionTip(maximize, string.Empty);
        _closeTip = CaptionTip(close, Strings.CloseWindow);
        UpdateCaptionTips();

        minimize.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            MinimizeWindow();
        };
        maximize.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            ToggleMaximize();
        };
        close.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            _window?.Close();
        };

        captions.Children.Add(minimize);
        captions.Children.Add(maximize);
        captions.Children.Add(close);

        Grid.SetColumn(captions, 2);
        _headerInner.Children.Add(captions);
    }

    /// <summary>
    /// 7 = the island's 6px inset + the pane frame's 1px border: the 32px tool row then
    /// shares its band with the pane header and the caption buttons. Compact mode moves the
    /// island to the window edge, so the rail's row has to follow it up.
    /// </summary>
    private Thickness SidebarPadding() => new Thickness(6, CompactDensity ? 0 : 7, 6, 8);

    private void BuildSidebar()
    {
        bool leftSide = _placement == TabPlacement.Left;

        // Flat tab rail: with no title bar in the side layouts it starts at the window's
        // top edge and has no card chrome of its own.
        _sidebar = new Border
        {
            Width = _sidebarWidth,
            // No horizontal margin on either side: the rail's padding is the gap to the
            // window edge and to the island, so both sides read as 6.
            Margin = new Thickness(0, 0, 0, 8),
            Background = ClearBrush(),
            BorderThickness = new Thickness(0),
            Padding = SidebarPadding(),
        };

        // The header strip is click-through in the side layouts (the island rises into it),
        // so the rail's own empty area drags the window instead.
        _sidebar.PointerPressed += OnHeaderPointerPressed;
        _sidebar.PointerMoved += (_, args) =>
        {
            if (!_dragging) return;
            args.Handled = true;
            DragTo();
        };
        _sidebar.PointerReleased += (_, args) =>
        {
            if (!_dragging) return;
            args.Handled = true;
            _sidebar.ReleasePointerCapture(args.Pointer);
            EndDrag();
        };
        _sidebar.PointerCaptureLost += (_, _) => EndDrag();

        var layout = new Grid();
        AddAutoRow(layout);
        AddStarRow(layout);
        _sidebar.Child = layout;

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Height = 32,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The sidebar toggle is always a button of its own: collapsing the rail should never
        // require opening a menu.
        Border collapse = ToolButton(PanelIcon(TextFaintBrush(), 14, _placement != TabPlacement.Right));
        Hoverable(collapse, ChipHoverBrush);
        collapse.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            SetSidebarCollapsed(true);
        };
        tools.Children.Add(collapse);

        Border add = ToolButton('\uE710');
        Hoverable(add, ChipHoverBrush);
        add.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            Activate(IndexOf(NewTab()));
        };
        tools.Children.Add(add);

        Border search = ToolButton('\uE721');
        Hoverable(search, ChipHoverBrush);
        search.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            ShowCommandPalette();
        };
        tools.Children.Add(search);

        // The two extra actions become an overflow button only when the rail's top row cannot
        // hold them. A right-hand rail shares that row with the caption buttons, so it needs
        // noticeably more width than a left-hand one.
        var railMenu = new MenuFlyout
        {
            // Edge-aligned to the button: centered below it would hang the menu off the
            // window when the rail is at its narrowest.
            Placement = _placement == TabPlacement.Right
                ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight
                : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
        };

        var newTabItem = new MenuFlyoutItem
        {
            Text = "新建标签页",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+T",
        };
        newTabItem.Click += (_, _) => Activate(IndexOf(NewTab()));
        railMenu.Items.Add(newTabItem);

        var searchItem = new MenuFlyoutItem
        {
            Text = "搜索命令",
            KeyboardAcceleratorTextOverride = "Ctrl+Shift+P",
        };
        searchItem.Click += (_, _) => ShowCommandPalette();
        railMenu.Items.Add(searchItem);

        Border menu = ToolButton('\uE712');
        Hoverable(menu, ChipHoverBrush);
        // Opening on release: a flyout shown while the button is still down is dismissed by
        // the same press.
        menu.PointerPressed += (_, args) => args.Handled = true;
        menu.PointerReleased += (_, args) =>
        {
            args.Handled = true;
            railMenu.ShowAt(menu);
        };
        tools.Children.Add(menu);

        void UpdateRailTools()
        {
            double leading = leftSide ? 14 : 6;
            double captions = leftSide ? 0 : 3 * CaptionButtonWidth;
            double needed = 3 * ToolButtonWidth + 2 * tools.Spacing + leading + captions;
            bool fits = (_sidebar?.ActualWidth ?? 0.0) >= needed;

            add.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
            search.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
            menu.Visibility = fits ? Visibility.Collapsed : Visibility.Visible;
        }
        _sidebar.SizeChanged += (_, _) => UpdateRailTools();
        UpdateRailTools();

        Grid.SetRow(tools, 0);
        layout.Children.Add(tools);

        _tabColumn = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        _chipHost = _tabColumn;

        var scroller = new ScrollViewer
        {
            Content = _tabColumn,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = ScrollMode.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
        };
        _tabScroller = scroller;
        // The rail is laid out after the tabs exist, so a pending "scroll to the active tab"
        // (a new tab, or a switch into the side layout) has to run again once it has a size.
        scroller.SizeChanged += (_, _) =>
        {
            if (_ensureActiveVisible && EnsureActiveVisible()) _ensureActiveVisible = false;
        };
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        WireTabDrag();
    }

    /// <summary>
    /// Turns the pane's own left (or right) border into the rail's splitter. The strip lies
    /// mostly in the gap between the rail and the pane and reaches a couple of DIP over the
    /// pane's edge, which is the visible border the pointer grabs; the accent line it paints
    /// while dragging lands on that border.
    /// </summary>
    private void BuildSidebarSplitter(bool leftSide)
    {
        _sidebarSplitter = new ResizeGrip
        {
            Width = SidebarSplitterWidth,
            HorizontalAlignment = leftSide ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = leftSide
                ? new Thickness(0, 0, -SidebarSplitterOverlap, 0)
                : new Thickness(-SidebarSplitterOverlap, 0, 0, 0),
        };

        // A UserControl's own Background is not painted by its template, so the hit-testable
        // surface is this child. The splitter paints nothing: the border it covers is already
        // there, and the resize cursor is the only cue.
        _sidebarSplitter.Content = new Grid { Background = ClearBrush() };

        _sidebarSplitter.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (_sidebar is null) return;
            _resizingSidebar = true;
            _resizeStartWidth = _sidebar.Width;
            // The cursor is tracked in screen pixels, like the window drag: the border moves
            // under the pointer, so a relative position would feed the resize back into its
            // own delta.
            GetCursorPos(out POINT cursor);
            _resizeCursorX = cursor.X;
            _sidebarSplitter.CapturePointer(args.Pointer);
        };
        _sidebarSplitter.PointerMoved += (_, args) =>
        {
            if (!_resizingSidebar) return;
            args.Handled = true;
            ResizeSidebarTo();
        };
        _sidebarSplitter.PointerReleased += (_, args) =>
        {
            if (!_resizingSidebar) return;
            args.Handled = true;
            _sidebarSplitter.ReleasePointerCapture(args.Pointer);
            EndSidebarResize();
        };
        _sidebarSplitter.PointerCaptureLost += (_, _) => EndSidebarResize();

        Grid.SetColumn(_sidebarSplitter, leftSide ? 0 : 1);
        _body.Children.Add(_sidebarSplitter);
    }

    /// <summary>
    /// Follows the pointer while the rail's edge is dragged. A right-hand rail grows
    /// leftwards, and the screen-pixel delta is converted to DIPs with the window's scale.
    /// </summary>
    private void ResizeSidebarTo()
    {
        if (_sidebar is null) return;

        GetCursorPos(out POINT cursor);
        double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0.0) scale = 1.0;

        double delta = (cursor.X - _resizeCursorX) / scale;
        if (_placement == TabPlacement.Right) delta = -delta;

        double floor = MinSidebarWidth();
        double max = Math.Max(floor, _body.ActualWidth - ContentWidthMin);
        double width = Math.Clamp(_resizeStartWidth + delta, floor, max);
        if (Math.Abs(width - _sidebar.Width) < 0.5) return;

        _sidebarWidth = width;
        _sidebar.Width = width;
    }

    private void EndSidebarResize()
    {
        if (!_resizingSidebar) return;
        _resizingSidebar = false;
        SaveSidebarWidth();
    }

    /// <summary>The saved width for the side the rail is currently on.</summary>
    private double SidebarWidthFromConfig() =>
        _placement == TabPlacement.Right ? _config.SidebarWidthRight : _config.SidebarWidthLeft;

    /// <summary>
    /// Persists the current width under the current side's key. Left and right rails are
    /// dragged independently, so switching sides never carries one side's width over.
    /// </summary>
    private void SaveSidebarWidth()
    {
        int value = (int)Math.Round(_sidebarWidth);
        if (_placement == TabPlacement.Right)
        {
            _config.SidebarWidthRight = value;
            Config.SaveValue("sidebar-width-right", value.ToString());
        }
        else
        {
            _config.SidebarWidthLeft = value;
            Config.SaveValue("sidebar-width-left", value.ToString());
        }
    }

    private void SetSidebarWidth(double width)
    {
        _sidebarWidth = Math.Max(MinSidebarWidth(), width);
        if (_sidebar is not null) _sidebar.Width = _sidebarWidth;
    }

    /// <summary>
    /// Narrowest the rail may be dragged. A right-hand rail shares its top row with the three
    /// caption buttons, so it has to keep the room of two of its own buttons in front of them;
    /// a left-hand rail has that row to itself.
    /// </summary>
    private double MinSidebarWidth() => _placement == TabPlacement.Right
        ? 2 * ToolButtonWidth + 3 * CaptionButtonWidth
        : SidebarWidthMin;

    /// <summary>
    /// The header strip's own inset. The side layouts float it over the island, so it repeats
    /// the island's 6 DIP inset; compact mode removes that inset from both.
    /// </summary>
    private Thickness HeaderMargin(bool sideRail) => sideRail
        ? new Thickness(0, CompactDensity ? 0 : SideHeaderInset, CompactDensity ? 0 : 6, 0)
        : new Thickness(0);

    /// <summary>
    /// Compact chrome: the panes stop being islands and fill the content area, separated
    /// only by 1 DIP dividers.
    /// </summary>
    private bool CompactDensity => _config.Compact;

    /// <summary>Gap between two split cells; compact mode makes them share an edge.</summary>
    private double SurfaceGap => CompactDensity ? 0.0 : 6.0;

    /// <summary>Thickness of the band that resizes a split; it straddles the boundary.</summary>
    private const double SplitterWidth = 10.0;

    /// <summary>Smallest a pane may be dragged to, so both sides stay usable.</summary>
    private const double SplitterMinExtent = 40.0;

    /// <summary>The gap under the top tab strip; compact mode drops it.</summary>
    private Thickness SurfaceMargin() =>
        _placement == TabPlacement.Top && !CompactDensity ? new Thickness(0, 6, 0, 0) : new Thickness(0);

    /// <summary>
    /// The island's outer margin. It is 6px on every visible edge: the top layout puts the
    /// island under the 38px title bar, the side layouts have no title bar at all and start
    /// the island at the window's own 6px inset.
    /// </summary>
    private Thickness ContentCardMargin()
    {
        // Compact mode merges the panes into one continuous surface: the island inset goes
        // away on every edge.
        if (CompactDensity) return new Thickness(0);

        if (_placement == TabPlacement.Top) return new Thickness(6, 0, 6, 6);

        if (_sidebarCollapsed) return new Thickness(6, 6, 6, 6);
        return _placement == TabPlacement.Left ? new Thickness(0, 6, 6, 6)
            : new Thickness(6, 6, 0, 6);
    }

    private void BuildSurface()
    {
        _contentCard = new Border
        {
            Margin = ContentCardMargin(),
            CornerRadius = new CornerRadius(CompactDensity ? 0 : 12),
            Background = CardFillBrush(),
        };

        // The design's content island carries no outline and lets the panes run to its edges.
        // The top layout keeps a 6px gap under the tab strip; the side layouts pull the
        // island up into the header row instead, so the pane shares the window's top edge
        // with the caption buttons and needs no gap there.
        _surfaceHost = new Grid { Margin = SurfaceMargin() };

        _contentStack = new Grid();
        _contentStack.Children.Add(_surfaceHost);
        _contentCard.Child = _contentStack;
    }

    // ---------------- tabs ----------------

    private static string ExecutableFrom(string command)
    {
        int first = 0;
        while (first < command.Length && (command[first] == ' ' || command[first] == '\t')) first++;
        if (first >= command.Length) return string.Empty;
        command = command[first..];

        if (command[0] == '"')
        {
            int close = command.IndexOf('"', 1);
            return command[1..(close < 0 ? command.Length : close)];
        }
        int end = command.IndexOfAny(new[] { ' ', '\t' });
        return end < 0 ? command : command[..end];
    }

    /// <summary>
    /// Display name for a shell command or executable path. An auto-detected shell arrives
    /// as a full path that may contain spaces, so only tokenize when the whole string is
    /// not an existing executable.
    /// </summary>
    private static string ShellDisplayName(string command)
    {
        string executable;
        if (string.IsNullOrEmpty(command))
        {
            executable = TerminalSession.DefaultShellPath();
        }
        else if (File.Exists(command))
        {
            executable = command;
        }
        else
        {
            executable = ExecutableFrom(command);
        }

        string stem = string.Empty;
        try
        {
            stem = System.IO.Path.GetFileNameWithoutExtension(executable);
        }
        catch
        {
            stem = executable;
        }

        return stem.ToLowerInvariant() switch
        {
            "pwsh" => "PowerShell",
            "powershell" => "Windows PowerShell",
            "cmd" => "Command Prompt",
            _ => stem,
        };
    }

    /// <summary>
    /// The chip's shape depends on the rail it sits in: both variants fill the header's
    /// 8px top gap exactly, and moving the rail restyles the chip.
    /// </summary>
    private void StyleChip(Tab tab)
    {
        bool top = _placement == TabPlacement.Top;
        tab.Chip.Height = 32;
        // Rounded on all four corners: the tab floats on the strip instead of docking into
        // the content island, so there is no square bottom edge and no connector. Compact
        // mode squares the bottom edge of a top-strip chip, where it meets the edge-to-edge
        // surface; a rail row keeps its four corners, it sits beside the surface, not above it.
        tab.Chip.CornerRadius = CompactDensity && top
            ? new CornerRadius(8, 8, 0, 0)
            : new CornerRadius(8);
        tab.Chip.Padding = top ? new Thickness(11, 0, 10, 0) : new Thickness(8, 0, 8, 0);
        tab.Chip.Margin = top ? new Thickness(0, 0, 2, 0) : new Thickness(0);
        tab.Chip.MaxWidth = top ? 220 : double.PositiveInfinity;
        if (tab.ChipClose is not null)
            tab.ChipClose.Margin = top ? new Thickness(0, 0, 8, 0) : new Thickness(0);
    }

    private Tab NewTab()
    {
        Diag.Debug("tab", $"create start #{_tabs.Count + 1}");
        var tab = new Tab { Title = ShellDisplayName(_config.Command) };

        tab.Surface = new Grid { RowSpacing = SurfaceGap, ColumnSpacing = SurfaceGap };

        bool top = _placement == TabPlacement.Top;

        // Both placements use the same flat chip; the top variant is taller and docks into
        // the content card with square bottom corners.
        tab.Chip = new Border
        {
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (top) tab.Chip.MaxWidth = 220;

        Tab self = tab;
        tab.Chip.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            // The strip is about to reorder; starting a drag now would race that
            // collection change.
            int index = IndexOf(self);
            if (_hasPendingTabMove)
            {
                if (index >= 0) Activate(index);
                return;
            }
            if (index < 0) return;
            Activate(index);
            if (_chipHost is null) return;

            // Capture on the strip, not the chip: reordering moves the chip between
            // children and would drop a chip-level capture.
            Point point = args.GetCurrentPoint(_chipHost).Position;
            _dragTab = self;
            _dragArmed = true;
            _dragMoving = false;
            _dragPressX = point.X;
            _dragPressY = point.Y;
            _dragTarget = index;
            _dragHomeX = 0;
            _dragHomeY = 0;
            _dragFollowX = 0;
            _dragFollowY = 0;
            Canvas.SetZIndex(self.ChipFocus, 1);
            _chipHost.CapturePointer(args.Pointer);
        };
        tab.Chip.PointerEntered += (_, _) => Diag.Guard("ws.chipHover", () =>
        {
            self.Hovered = true;
            UpdateHeaders();
        });
        tab.Chip.PointerExited += (_, _) => Diag.Guard("ws.chipHover", () =>
        {
            self.Hovered = false;
            UpdateHeaders();
        });

        var chipGrid = new Grid();
        AddAutoColumn(chipGrid);
        AddStarColumn(chipGrid);
        AddAutoColumn(chipGrid);
        AddAutoColumn(chipGrid);

        // Same shell icon as the pane header: a 16 DIP slot that shows the extracted
        // executable icon and falls back to the terminal glyph.
        var iconSlot = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        tab.ChipIconFallback = Glyph('\uE756', 16, ChromeTextFaintBrush());
        iconSlot.Children.Add(tab.ChipIconFallback);

        tab.ChipIcon = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };
        iconSlot.Children.Add(tab.ChipIcon);
        chipGrid.Children.Add(iconSlot);

        tab.ChipText = Label(tab.Title, 12, ChromeTextBodyBrush());
        tab.ChipText.FontFamily = TitleFontFamily();
        tab.ChipText.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(tab.ChipText, 1);
        chipGrid.Children.Add(tab.ChipText);

        tab.ChipClose = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = Glyph('\uE711', 10, ChromeTextFaintBrush()),
        };
        Hoverable(tab.ChipClose, ChromeCloseButtonHoverBrush);
        tab.ChipClose.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            // Removing the pressed chip from its panel inside this handler crashes WinUI;
            // close after the pointer event unwinds.
            _dispatcher?.TryEnqueue(() =>
            {
                int index = IndexOf(self);
                if (index >= 0) CloseTab(index);
            });
        };
        Grid.SetColumn(tab.ChipClose, 2);
        chipGrid.Children.Add(tab.ChipClose);

        // A thin separator between two inactive tabs. It lives in the 2px gutter *before*
        // the chip (the design's .tab + .tab::before with left:-1px), so it reads as a
        // separator between tabs instead of as part of the chip's own box.
        tab.ChipDivider = new Border
        {
            Width = 1,
            Height = 20,
            Background = ChromeStrokeBrush(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-1, 0, 0, 0),
            IsHitTestVisible = false,
            Opacity = 0.0,
        };

        tab.Chip.Child = chipGrid;
        StyleChip(tab);
        // A brand-new chip has no measured width yet, so the scroll-into-view runs again
        // once it does.
        tab.Chip.SizeChanged += (_, _) =>
        {
            if (_ensureActiveVisible && EnsureActiveVisible()) _ensureActiveVisible = false;
        };

        // Hover tooltip: a single wrapped, centered TextBlock whose inlines are the title
        // followed by the italicized switch-to-tab key chord.
        tab.ChipTipText = new TextBlock
        {
            FontFamily = TitleFontFamily(),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        ToolTipService.SetToolTip(tab.Chip, new ToolTip { Content = tab.ChipTipText });

        // A Border cannot take focus, so the chip sits inside a focusable ContentControl;
        // only the active chip joins the tab order (roving index).
        var chipShell = new Grid();
        chipShell.Children.Add(tab.Chip);
        chipShell.Children.Add(tab.ChipDivider);

        tab.ChipFocus = new ContentControl
        {
            Content = chipShell,
            IsTabStop = false,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        tab.ChipFocus.KeyDown += (_, args) => OnChipKeyDown(self, args);

        AutomationProperties.SetName(tab.ChipClose, Strings.CloseTab);
        int index = TabInsertIndex();
        _chipHost.Children.Insert(index, tab.ChipFocus);

        var pane = new TerminalPane(NewTabConfig(ActivePane()));
        WirePane(pane);

        tab.Focused = pane;
        tab.Root = new SplitNode { Pane = pane };

        _tabs.Insert(index, tab);
        Relayout(tab);
        Diag.Debug("tab", "create done");
        return tab;
    }

    /// <summary>"window-new-tab-position": after the active tab (current) or at the end.</summary>
    private int TabInsertIndex() =>
        _config.NewTabPosition == "current" && _tabs.Count > 0 && _active >= 0 && _active < _tabs.Count
            ? Math.Min(_active + 1, _tabs.Count)
            : _tabs.Count;

    private TerminalPane? ActivePane() =>
        _tabs.Count > 0 && _active >= 0 && _active < _tabs.Count ? _tabs[_active].Focused : null;

    /// <summary>
    /// The config for a new tab. Tabs inherit the focused shell's directory and font size
    /// (tab-inherit-working-directory / window-inherit-font-size), and the very first
    /// surface can use "initial-command".
    /// </summary>
    private Config NewTabConfig(TerminalPane? source)
    {
        bool inheritDirectory = _config.TabInheritWorkingDirectory && source is not null;
        bool inheritFont = _config.InheritFontSize && source is not null;
        bool initialCommand = _firstSurface && _config.InitialCommand.Length > 0;
        _firstSurface = false;

        if (!inheritDirectory && !inheritFont && !initialCommand) return _config;

        var config = new Config();
        config.CopyFrom(_config);
        if (inheritDirectory) config.WorkingDirectory = source!.WorkingDirectory();
        if (inheritFont) config.FontSize = source!.CurrentFontSizePoints;
        if (initialCommand) config.Command = _config.InitialCommand;
        return config;
    }

    private void OnChipKeyDown(Tab self, KeyRoutedEventArgs args)
    {
        int index = IndexOf(self);
        if (index < 0) return;

        bool horizontal = _placement == TabPlacement.Top;
        int count = _tabs.Count;
        int next = -1;
        switch (args.Key)
        {
            case VirtualKey.Left when horizontal: next = index - 1; break;
            case VirtualKey.Right when horizontal: next = index + 1; break;
            case VirtualKey.Up when !horizontal: next = index - 1; break;
            case VirtualKey.Down when !horizontal: next = index + 1; break;
            case VirtualKey.Home: next = 0; break;
            case VirtualKey.End: next = count - 1; break;
        }
        if (next >= 0 && next < count)
        {
            args.Handled = true;
            Activate(next);
            // Activate hands focus back to the terminal; navigation should keep the
            // roving ring on the strip.
            _tabs[_active].ChipFocus.Focus(FocusState.Keyboard);
            return;
        }

        if (args.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            args.Handled = true;
            Activate(index);
            return;
        }
        if (args.Key == VirtualKey.Delete)
        {
            args.Handled = true;
            _dispatcher?.TryEnqueue(() =>
            {
                int current = IndexOf(self);
                if (current >= 0) CloseTab(current);
            });
        }
    }

    /// <summary>
    /// Creates a tab from a pointer handler. The strip's Children collection must not be
    /// mutated while WinUI is still dispatching that pointer event, so the work is posted,
    /// exactly like the tab-close path.
    /// </summary>
    private void RequestNewTab()
    {
        void Create() => Activate(IndexOf(NewTab()));

        if (_dispatcher is null)
        {
            Diag.Guard("ws.newTab", Create);
            return;
        }
        _dispatcher.TryEnqueue(() => Diag.Guard("ws.newTab", Create));
    }

    private int IndexOf(Tab tab)
    {
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (ReferenceEquals(_tabs[i], tab)) return i;
        }
        return -1;
    }

    // ---------------- tab drag ----------------

    private void WireTabDrag()
    {
        if (_chipHost is null) return;

        _chipHost.PointerMoved += (_, args) =>
        {
            if (_dragTab is null || !_dragArmed) return;

            bool horizontal = _placement == TabPlacement.Top;
            Point point = args.GetCurrentPoint(_chipHost).Position;
            double delta = horizontal ? point.X - _dragPressX : point.Y - _dragPressY;
            if (!_dragMoving && Math.Abs(delta) < 6.0) return;

            args.Handled = true;

            if (!_dragMoving)
            {
                _dragMoving = true;
                List<double> origins = TabSlotOrigins(horizontal);
                int current = IndexOf(_dragTab);
                if (current >= 0 && current < origins.Count)
                {
                    _dragHomeX = horizontal ? origins[current] : 0;
                    _dragHomeY = horizontal ? 0 : origins[current];
                    _dragHomeX += _dragTab.ShiftX;
                    _dragHomeY += _dragTab.ShiftY;
                    _dragFollowX = _dragHomeX;
                    _dragFollowY = _dragHomeY;
                }
                _dragScrollTimer?.Start();
            }

            _dragPointerContent = point;
            if (_tabScroller is not null)
            {
                _dragPointerView = args.GetCurrentPoint(_tabScroller).Position;
                _dragScrollOffset = horizontal ? _stripOffset : _tabScroller.VerticalOffset;
            }
            UpdateTabDrag(point.X, point.Y);
        };

        _chipHost.PointerReleased += (_, args) =>
        {
            if (_dragTab is null) return;
            args.Handled = true;
            _chipHost.ReleasePointerCapture(args.Pointer);
            EndTabDrag();
        };
        _chipHost.PointerCaptureLost += (_, _) => EndTabDrag();
    }

    private void UpdateTabDrag(double pointerX, double pointerY)
    {
        if (_dragTab is null || !_dragArmed || !_dragMoving) return;

        bool horizontal = _placement == TabPlacement.Top;
        int current = IndexOf(_dragTab);
        if (current < 0) return;

        // The strip order never changes during the gesture; only the chip translations do.
        List<double> origins = TabSlotOrigins(horizontal);
        if (current >= origins.Count) return;

        double delta = horizontal ? pointerX - _dragPressX : pointerY - _dragPressY;
        if (horizontal) _dragFollowX = _dragHomeX + delta;
        else _dragFollowY = _dragHomeY + delta;

        double baseX = horizontal ? origins[current] : _dragFollowX;
        double baseY = horizontal ? _dragFollowY : origins[current];
        ShiftTabNow(_dragTab, _dragFollowX - baseX, _dragFollowY - baseY);

        // The same midpoint rule browsers use.
        double draggedCentre = (horizontal ? _dragFollowX : _dragFollowY) + TabStripExtent(_dragTab, horizontal) / 2.0;
        int target = 0;
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i == current) continue;
            double centre = origins[i] + TabStripExtent(_tabs[i], horizontal) / 2.0;
            if (draggedCentre > centre) target++;
        }
        if (target == _dragTarget) return;
        _dragTarget = target;

        var order = new List<Tab>(_tabs.Count);
        for (int i = 0; i < _tabs.Count; i++)
        {
            if (i != current) order.Add(_tabs[i]);
        }
        order.Insert(target, _dragTab);
        List<double> finalOrigins = TabSlotOriginsForOrder(order, horizontal);

        for (int i = 0; i < _tabs.Count && i < origins.Count; i++)
        {
            if (i == current) continue;

            int slot = i;
            if (current < target)
            {
                if (i > current && i <= target) slot = i - 1;
            }
            else if (current > target)
            {
                if (i >= target && i < current) slot = i + 1;
            }

            double desiredX = horizontal ? finalOrigins[slot] - origins[i] : 0;
            double desiredY = horizontal ? 0 : finalOrigins[slot] - origins[i];
            Tab tab = _tabs[i];
            double distanceX = desiredX - tab.ShiftX;
            double distanceY = desiredY - tab.ShiftY;
            if (Math.Abs(distanceX) < 0.5 && Math.Abs(distanceY) < 0.5) continue;
            ShiftTab(tab, desiredX, desiredY, true);
        }
    }

    private void TickTabDragScroll()
    {
        if (_dragTab is null || !_dragMoving || _tabScroller is null) return;

        bool horizontal = _placement == TabPlacement.Top;
        double viewport = horizontal ? _tabScroller.ActualWidth : _tabScroller.ActualHeight;
        double view = horizontal ? _dragPointerView.X : _dragPointerView.Y;

        double edge = 40.0;
        if (viewport <= edge * 2.0) edge = viewport / 3.0;
        if (edge <= 0.0) return;

        double speed = 0.0;
        if (view < edge) speed = -(edge - view) / edge;
        else if (view > viewport - edge) speed = (view - (viewport - edge)) / edge;
        if (speed == 0.0) return;

        double maxOffset = horizontal
            ? (_stripHost is not null ? Math.Max(0.0, _stripHost.ActualWidth - _tabScroller.ActualWidth) : 0.0)
            : _tabScroller.ScrollableHeight;
        if (maxOffset <= 0.0) return;

        double target = Math.Clamp(_dragScrollOffset + speed * 18.0, 0.0, maxOffset);
        if (target == _dragScrollOffset) return;
        double step = target - _dragScrollOffset;

        if (horizontal)
        {
            ScrollStrip(step);
            _dragPointerContent.X += step;
        }
        else
        {
            _tabScroller.ChangeView(null, target, 1.0f, true);
            _dragPointerContent.Y += step;
        }
        _dragScrollOffset = target;
        UpdateTabDrag(_dragPointerContent.X, _dragPointerContent.Y);
    }

    private void QueueTabMove(int from, int to)
    {
        _pendingTabFrom = from;
        _pendingTabTo = to;
        _hasPendingTabMove = true;

        if (_dispatcher is null || _dropTimer is null)
        {
            ApplyPendingTabMove();
            return;
        }
        _dropTimer.Stop();
        _dropTimer.Start();
    }

    private void ApplyPendingTabMove()
    {
        if (!_hasPendingTabMove) return;
        _hasPendingTabMove = false;
        MoveTab(_pendingTabFrom, _pendingTabTo);
    }

    private void EndTabDrag()
    {
        Tab? tab = _dragTab;
        bool moved = _dragMoving;
        int target = _dragTarget;
        int from = tab is null ? -1 : IndexOf(tab);

        _dragScrollTimer?.Stop();
        if (tab is not null) Canvas.SetZIndex(tab.ChipFocus, 0);

        if (moved && tab is not null && from >= 0)
        {
            if (target != from)
            {
                bool horizontal = _placement == TabPlacement.Top;
                List<double> oldOrigins = TabSlotOrigins(horizontal);
                var order = new List<Tab>(_tabs.Count);
                for (int i = 0; i < _tabs.Count; i++)
                {
                    if (i != from) order.Add(_tabs[i]);
                }
                order.Insert(target, tab);
                List<double> finalOrigins = TabSlotOriginsForOrder(order, horizontal);

                if (from < oldOrigins.Count && target < finalOrigins.Count)
                {
                    double delta = finalOrigins[target] - oldOrigins[from];
                    ShiftTab(tab, horizontal ? delta : 0, horizontal ? 0 : delta, true);
                }
                QueueTabMove(from, target);
            }
            else
            {
                ShiftTab(tab, 0, 0, true);
            }
        }

        _dragTab = null;
        _dragArmed = false;
        _dragMoving = false;
        _dragTarget = 0;
        UpdateHeaders();
    }

    /// <summary><paramref name="to"/> is the insertion index after removing the tab.</summary>
    private void MoveTab(int from, int to)
    {
        if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count || from == to) return;

        bool horizontal = _placement == TabPlacement.Top;
        List<double> oldOrigins = TabSlotOrigins(horizontal);
        var oldOrder = new List<Tab>(_tabs.Count);
        var oldShifts = new List<(double X, double Y)>(_tabs.Count);
        var oldExtents = new List<double>(_tabs.Count);
        foreach (Tab tab in _tabs)
        {
            oldOrder.Add(tab);
            oldShifts.Add((tab.ShiftX, tab.ShiftY));
            oldExtents.Add(TabStripExtent(tab, horizontal));
        }

        ContentControl chip = _tabs[from].ChipFocus;
        Tab moved = _tabs[from];
        _tabs.RemoveAt(from);
        _tabs.Insert(to, moved);

        if (_chipHost is not null)
        {
            _chipHost.Children.RemoveAt(from);
            _chipHost.Children.Insert(to, chip);
        }

        // Keep the active tab pointing at the same Tab object.
        if (_active == from) _active = to;
        else if (from < _active && to >= _active) _active--;
        else if (from > _active && to <= _active) _active++;

        // A chip that was just re-inserted into the panel reports a zero width until the
        // next layout pass, so the resting slots have to be rebuilt from the widths
        // measured before the swap.
        var previousIndex = new int[_tabs.Count];
        for (int i = 0; i < _tabs.Count; i++)
        {
            for (int probe = 0; probe < oldOrder.Count; probe++)
            {
                if (ReferenceEquals(oldOrder[probe], _tabs[i]))
                {
                    previousIndex[i] = probe;
                    break;
                }
            }
        }

        double spacing = _chipHost?.Spacing ?? 0.0;
        var newOrigins = new double[_tabs.Count];
        double offset = 0.0;
        for (int i = 0; i < _tabs.Count; i++)
        {
            newOrigins[i] = offset;
            if (previousIndex[i] < oldExtents.Count) offset += oldExtents[previousIndex[i]] + spacing;
        }

        // Each chip keeps its on-screen position across the reorder: the slot it lands in
        // differs from the old one, so the difference becomes a translation that springs
        // back to zero.
        for (int i = 0; i < _tabs.Count && i < oldOrigins.Count; i++)
        {
            Tab tab = _tabs[i];
            int oldIndex = previousIndex[i];

            double startX = oldIndex < oldShifts.Count ? oldShifts[oldIndex].X : 0;
            double startY = oldIndex < oldShifts.Count ? oldShifts[oldIndex].Y : 0;
            double slotDelta = oldIndex < oldOrigins.Count ? oldOrigins[oldIndex] - newOrigins[i] : 0;
            if (horizontal) startX += slotDelta;
            else startY += slotDelta;

            ShiftTabNow(tab, startX, startY);
            if (Math.Abs(startX) < 0.5 && Math.Abs(startY) < 0.5) continue;
            ShiftTab(tab, 0, 0, true);
        }

        UpdateHeaders();
    }

    private List<double> TabSlotOrigins(bool horizontal)
    {
        var order = new List<Tab>(_tabs);
        return TabSlotOriginsForOrder(order, horizontal);
    }

    private List<double> TabSlotOriginsForOrder(IReadOnlyList<Tab> order, bool horizontal)
    {
        var origins = new List<double>(order.Count);
        double offset = 0.0;
        double spacing = _chipHost?.Spacing ?? 0.0;
        foreach (Tab tab in order)
        {
            origins.Add(offset);
            offset += TabStripExtent(tab, horizontal) + spacing;
        }
        return origins;
    }

    private static double TabStripExtent(Tab tab, bool horizontal) =>
        horizontal ? tab.ChipFocus.ActualWidth : tab.ChipFocus.ActualHeight;

    /// <summary>
    /// The strip animates its own chips: a composition animation on the chip cannot be
    /// mirrored exactly onto the active tab's connectors, and a connector that lags its
    /// chip shows up as a corner sitting at the destination on its own.
    /// </summary>
    private void ShiftTab(Tab tab, double x, double y, bool animated)
    {
        if (!animated || _dispatcher is null)
        {
            ShiftTabNow(tab, x, y);
            return;
        }

        DateTime now = DateTime.UtcNow;
        foreach (ChipSpring spring in _chipSprings)
        {
            if (!ReferenceEquals(spring.Tab, tab)) continue;
            spring.FromX = tab.ShiftX;
            spring.FromY = tab.ShiftY;
            spring.ToX = x;
            spring.ToY = y;
            spring.Start = now;
            _chipSpringTimer?.Start();
            return;
        }

        _chipSprings.Add(new ChipSpring
        {
            Tab = tab,
            FromX = tab.ShiftX,
            FromY = tab.ShiftY,
            ToX = x,
            ToY = y,
            Start = now,
        });

        if (_chipSpringTimer is null && _dispatcher is not null)
        {
            _chipSpringTimer = _dispatcher.CreateTimer();
            _chipSpringTimer.Interval = TimeSpan.FromMilliseconds(16);
            _chipSpringTimer.Tick += (_, _) => Diag.Guard("ws.chipSprings", TickChipSprings);
        }
        _chipSpringTimer?.Start();
    }

    private void ShiftTabNow(Tab tab, double x, double y)
    {
        StopChipSpring(tab);
        ApplyChipShift(tab, x, y);
    }

    private void ApplyChipShift(Tab tab, double x, double y)
    {
        tab.ShiftX = x;
        tab.ShiftY = y;
        tab.ChipFocus.Translation = new System.Numerics.Vector3((float)x, (float)y, 0);
    }

    private void StopChipSpring(Tab tab)
    {
        _chipSprings.RemoveAll(spring => ReferenceEquals(spring.Tab, tab));
        if (_chipSprings.Count == 0) _chipSpringTimer?.Stop();
    }

    private void TickChipSprings()
    {
        DateTime now = DateTime.UtcNow;
        const double duration = 180.0;

        foreach (ChipSpring spring in _chipSprings)
        {
            if (spring.Tab is null) continue;
            double elapsed = (now - spring.Start).TotalMilliseconds;
            double progress = Math.Clamp(elapsed / duration, 0.0, 1.0);
            // Ease-out cubic, the same curve the pane open/close animations use.
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            ApplyChipShift(spring.Tab,
                spring.FromX + (spring.ToX - spring.FromX) * eased,
                spring.FromY + (spring.ToY - spring.FromY) * eased);
        }

        var finished = new List<ChipSpring>();
        foreach (ChipSpring spring in _chipSprings)
        {
            if ((now - spring.Start).TotalMilliseconds >= duration)
            {
                if (spring.Tab is not null) ApplyChipShift(spring.Tab, spring.ToX, spring.ToY);
                finished.Add(spring);
            }
        }
        foreach (ChipSpring spring in finished) _chipSprings.Remove(spring);
        if (_chipSprings.Count == 0) _chipSpringTimer?.Stop();
    }

    // ---------------- tab connectors ----------------

    /// <summary>
    /// Horizontal wheel scrolling over the tab strip. The strip is moved with a render
    /// transform instead of the ScrollViewer's own scrolling, which fail-fasts as soon as
    /// the chips overflow.
    /// </summary>
    private void OnStripWheel(object sender, PointerRoutedEventArgs args)
    {
        if (_tabScroller is null) return;
        int delta = args.GetCurrentPoint(_tabScroller).Properties.MouseWheelDelta;
        if (delta == 0) return;

        args.Handled = true;
        // The user is steering the strip; do not yank it back to the active chip.
        _ensureActiveVisible = false;
        ScrollStrip(delta > 0 ? -72.0 : 72.0);
    }

    private void ScrollStrip(double delta)
    {
        if (_stripHost is null || _stripTransform is null || _tabScroller is null)
        {
            // Side rail: no transform, the ScrollViewer still scrolls it vertically.
            _tabScroller?.ChangeView(null, _tabScroller.VerticalOffset + delta, null, true);
            return;
        }

        // The strip's own ActualWidth is clamped to the viewport once the ScrollViewer stops
        // scrolling, so the content extent has to come from the chips themselves.
        double contentWidth = 16.0; // host margins
        List<double> origins = TabSlotOrigins(true);
        if (_tabs.Count > 0) contentWidth += origins[^1] + TabStripExtent(_tabs[^1], true);

        double max = Math.Max(0.0, contentWidth - _tabScroller.ActualWidth);
        double next = Math.Clamp(_stripOffset + delta, 0.0, max);
        if (Math.Abs(next - _stripOffset) < 0.01) return;

        _stripOffset = next;
        _stripTransform.X = -next;
    }

    /// <summary>Content extent of the strip, measured from the chips themselves.</summary>
    private double StripContentWidth()
    {
        List<double> origins = TabSlotOrigins(true);
        if (_tabs.Count == 0 || origins.Count == 0) return 16.0;
        return origins[^1] + TabStripExtent(_tabs[^1], true) + 16.0; // + host margins
    }

    /// <summary>
    /// Scrolls the strip so the active chip is fully visible, and pulls the offset back when
    /// closing tabs shrinks the content (otherwise the strip is left scrolled past its end).
    /// </summary>
    /// <summary>
    /// Returns false while the active chip still has no measured width, so the caller can
    /// keep the request pending until layout has run.
    /// </summary>
    private bool EnsureActiveVisible()
    {
        if (_tabScroller is null || _tabs.Count == 0) return false;

        int index = Math.Clamp(_active, 0, _tabs.Count - 1);

        // The side rails are ordinary vertical scroll areas (the chips stack downwards), so
        // the ScrollViewer can bring the chip into view itself; the horizontal strip below is
        // scrolled by hand because XAML's own horizontal scrolling of it fail-fasts.
        if (_stripTransform is null)
        {
            ContentControl chip = _tabs[index].ChipFocus;
            double height = chip.ActualHeight;
            double railViewport = _tabScroller.ViewportHeight;
            // Nothing to measure against yet (a fresh rail has not been laid out): keep the
            // request pending, the size-changed hook below runs it again.
            if (height <= 0.0 || railViewport <= 0.0 || _tabColumn is null) return false;

            double top = chip.TransformToVisual(_tabColumn).TransformPoint(new Point(0, 0)).Y;
            double offset = _tabScroller.VerticalOffset;
            if (top < offset) _tabScroller.ChangeView(null, top, null, true);
            else if (top + height > offset + railViewport)
                _tabScroller.ChangeView(null, top + height - railViewport, null, true);
            return true;
        }

        double viewport = _tabScroller.ActualWidth;
        if (viewport <= 0.0) return false;
        double extent = TabStripExtent(_tabs[index], true);
        if (extent <= 0.0) return false;

        double max = Math.Max(0.0, StripContentWidth() - viewport);
        double left = 8.0 + TabSlotOrigins(true)[index];

        double target = _stripOffset;
        if (left < _stripOffset) target = left;
        else if (left + extent > _stripOffset + viewport) target = left + extent - viewport;
        target = Math.Clamp(target, 0.0, max);

        if (Math.Abs(target - _stripOffset) >= 0.5)
        {
            _stripOffset = target;
            _stripTransform.X = -target;
        }
        return true;
    }

    // ---------------- split tree ----------------

    private void SettleExitAnimations()
    {
        if (_exitAnimations.Count == 0) return;

        var pending = new List<TerminalPane>();
        foreach (ExitAnimation entry in _exitAnimations)
        {
            entry.Timer?.Stop();
            if (entry.Pane is not null) pending.Add(entry.Pane);
        }
        _exitAnimations.Clear();

        foreach (TerminalPane pane in pending)
        {
            // A settled close skips the animation's completion callback, so the panes it
            // suspended would stay frozen without this.
            foreach (Tab tab in _tabs)
            {
                if (!TabContainsPane(tab, pane)) continue;
                tab.ResumePaneLayout();
                break;
            }
            FinishPaneRemoval(pane);
        }
    }

    private void Relayout(Tab tab)
    {
        // A running zoom transition holds a RenderTransform on a pane this pass may detach.
        SettleZoomTransition();

        // Settling a pending close runs FinishPaneRemoval, which relayouts the same tab.
        // That nested pass would drop the grids this one still detaches the panes from.
        if (ReferenceEquals(_relayouting, tab)) return;
        _relayouting = tab;
        try
        {
            RebuildLayout(tab);
        }
        finally
        {
            _relayouting = null;
        }
    }

    private void RebuildLayout(Tab tab)
    {
        SettleExitAnimations();

        var previous = new List<Grid>(tab.Grids);

        tab.Surface.RowDefinitions.Clear();
        tab.Surface.ColumnDefinitions.Clear();
        tab.Surface.Children.Clear();

        // The last pane cannot be zoomed or closed; the buttons stay visible but disabled.
        bool singlePane = tab.Root is not null && tab.Root.Pane is not null;
        ForEachPane(tab, pane => pane.SetSinglePane(singlePane));
        ForEachPane(tab, pane => pane.SetCompact(CompactDensity));

        // Ghostty's split handle is the pane header itself. Dragging is enabled once a tab
        // has two or more splits; "always" keeps it on a lone pane too, and "never" turns
        // split dragging off entirely. A zoomed tab paints one pane and refuses the drag, so
        // it must not advertise the move cursor either.
        bool dragEnabled = !tab.Zoomed && _config.DragHandle != "never" &&
                           (!singlePane || _config.DragHandle == "always");
        ForEachPane(tab, pane => pane.SetDragEnabled(dragEnabled));

        if (tab.Zoomed && tab.Focused is not null)
        {
            tab.Surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            tab.Surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            AddChild(tab.Surface, tab.Focused.Visual, previous);
            TerminalPane focused = tab.Focused;
            ForEachPane(tab, pane => pane.SetZoomed(ReferenceEquals(pane, focused)));
            return;
        }
        if (tab.Root is null) return;

        ForEachPane(tab, pane => pane.SetZoomed(false));

        tab.Grids.Clear();
        tab.Surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tab.Surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddChild(tab.Surface, BuildNode(tab.Root, previous, tab.Grids), previous);
    }

    /// <summary>
    /// A pane visual belongs to its pane, not to the split grid it sits in, and the grid it
    /// was last placed in can outlive the rebuild. Appending an element that still has a
    /// parent throws, so detach from the old grid first.
    /// </summary>
    private static void AddChild(Panel panel, UIElement element, IReadOnlyList<Grid> previous)
    {
        if (VisualTreeHelper.GetParent(element) is Panel owner)
            owner.Children.Remove(element);
        foreach (Grid grid in previous)
            grid.Children.Remove(element);
        panel.Children.Add(element);
    }

    /// <summary>
    /// Each split becomes a nested three-track Grid: first half, the gap (6 DIP, or compact
    /// mode's 1 DIP divider), second half. Keeping the gap as its own track is what lets the
    /// resize grip live exactly on the boundary — a star pair alone would put it in a fixed
    /// middle that drifts away as the ratio changes.
    /// </summary>
    private FrameworkElement BuildNode(SplitNode node, IReadOnlyList<Grid> previous, List<Grid> grids)
    {
        if (node.Pane is not null) return node.Pane.Visual;

        // Cozy keeps a 6 DIP gap between cells. Compact replaces it with a 1 DIP divider, so
        // the split line is the same weight the pane border used to draw. The two stars carry
        // the split's own ratio, which the divider drag edits.
        bool compact = CompactDensity;
        double gap = compact ? 1.0 : SurfaceGap;
        var grid = new Grid();
        grids.Add(grid);
        if (node.Vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(node.Ratio, GridUnitType.Star),
            });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
            grid.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1.0 - node.Ratio, GridUnitType.Star),
            });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(node.Ratio, GridUnitType.Star),
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1.0 - node.Ratio, GridUnitType.Star),
            });
        }

        FrameworkElement first = BuildNode(node.First!, previous, grids);
        FrameworkElement second = BuildNode(node.Second!, previous, grids);
        if (node.Vertical)
        {
            Grid.SetRow(first, 0);
            Grid.SetRow(second, 2);
        }
        else
        {
            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 2);
        }
        AddChild(grid, first, previous);
        if (compact)
        {
            Color dividerColor = _config.HasSplitDividerColor
                ? Color.FromArgb(255, _config.SplitDividerColor.R, _config.SplitDividerColor.G,
                    _config.SplitDividerColor.B)
                : Theme.PaneStroke();
            var divider = new Border { Background = new SolidColorBrush(dividerColor) };
            if (node.Vertical) Grid.SetRow(divider, 1); else Grid.SetColumn(divider, 1);
            grid.Children.Add(divider);
        }
        AddChild(grid, second, previous);
        // The resize grip goes in last so it sits above both halves and the divider.
        grid.Children.Add(BuildSplitter(node, grid, gap));
        return grid;
    }

    /// <summary>
    /// The invisible band that turns a split boundary into a resize handle. It fills the gap
    /// track and reaches a few DIP into both panes with negative margins, so the pointer only
    /// has to graze the line to grab it.
    /// </summary>
    private FrameworkElement BuildSplitter(SplitNode node, Grid grid, double gap)
    {
        var grip = new ResizeGrip(node.Vertical
            ? InputSystemCursorShape.SizeNorthSouth
            : InputSystemCursorShape.SizeWestEast)
        {
            Content = new Grid { Background = ClearBrush() },
        };
        double reach = Math.Max(0.0, (SplitterWidth - gap) / 2.0);
        if (node.Vertical)
        {
            Grid.SetRow(grip, 1);
            grip.HorizontalAlignment = HorizontalAlignment.Stretch;
            grip.VerticalAlignment = VerticalAlignment.Stretch;
            grip.Margin = new Thickness(0, -reach, 0, -reach);
        }
        else
        {
            Grid.SetColumn(grip, 1);
            grip.HorizontalAlignment = HorizontalAlignment.Stretch;
            grip.VerticalAlignment = VerticalAlignment.Stretch;
            grip.Margin = new Thickness(-reach, 0, -reach, 0);
        }

        double grab = 0.0;
        bool dragging = false;
        // The line runs through the middle of the gap track, so the drag maths folds that
        // track in on both sides of the conversion.

        grip.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            Point point = args.GetCurrentPoint(grid).Position;
            double extent = node.Vertical ? grid.ActualHeight : grid.ActualWidth;
            // Keep the pointer's offset to the line, so grabbing the band off-centre does
            // not snap the split under it.
            grab = (node.Vertical ? point.Y : point.X) - (node.Ratio * (extent - gap) + gap / 2.0);
            dragging = true;
            grip.CapturePointer(args.Pointer);
        };
        grip.PointerMoved += (_, args) =>
        {
            if (!dragging) return;
            args.Handled = true;

            double extent = node.Vertical ? grid.ActualHeight : grid.ActualWidth;
            if (extent - gap <= SplitterMinExtent * 2.0) return;

            Point point = args.GetCurrentPoint(grid).Position;
            double edge = (node.Vertical ? point.Y : point.X) - grab - gap / 2.0;
            double margin = SplitterMinExtent / (extent - gap);
            node.Ratio = Math.Clamp(edge / (extent - gap), margin, 1.0 - margin);
            ApplySplitRatio(node, grid);
        };
        grip.PointerReleased += (_, args) =>
        {
            if (!dragging) return;
            args.Handled = true;
            dragging = false;
            grip.ReleasePointerCapture(args.Pointer);
        };
        grip.PointerCaptureLost += (_, _) => dragging = false;
        return grip;
    }

    /// <summary>Writes the node's ratio into the live grid, without rebuilding the tree.</summary>
    private static void ApplySplitRatio(SplitNode node, Grid grid)
    {
        if (node.Vertical)
        {
            if (grid.RowDefinitions.Count < 2) return;
            grid.RowDefinitions[0].Height = new GridLength(node.Ratio, GridUnitType.Star);
            grid.RowDefinitions[^1].Height = new GridLength(1.0 - node.Ratio, GridUnitType.Star);
        }
        else
        {
            if (grid.ColumnDefinitions.Count < 2) return;
            grid.ColumnDefinitions[0].Width = new GridLength(node.Ratio, GridUnitType.Star);
            grid.ColumnDefinitions[^1].Width = new GridLength(1.0 - node.Ratio, GridUnitType.Star);
        }
    }

    private static void VisitPanes(SplitNode node, Action<TerminalPane> fn)
    {
        if (node.Pane is not null)
        {
            fn(node.Pane);
            return;
        }
        if (node.First is not null) VisitPanes(node.First, fn);
        if (node.Second is not null) VisitPanes(node.Second, fn);
    }

    private static void ForEachPane(Tab tab, Action<TerminalPane> fn)
    {
        if (tab.Root is not null) VisitPanes(tab.Root, fn);
    }

    private static TerminalPane? FirstPane(SplitNode? node)
    {
        if (node is null) return null;
        if (node.Pane is not null) return node.Pane;
        return FirstPane(node.First) ?? FirstPane(node.Second);
    }

    private static bool ContainsPane(SplitNode? node, TerminalPane pane)
    {
        if (node is null) return false;
        if (node.Pane is not null) return ReferenceEquals(node.Pane, pane);
        return ContainsPane(node.First, pane) || ContainsPane(node.Second, pane);
    }

    private static bool TabContainsPane(Tab tab, TerminalPane pane) => ContainsPane(tab.Root, pane);

    /// <summary>
    /// Replaces the focused leaf with a two-leaf split; the new pane always lands in the
    /// second half (right or bottom).
    /// </summary>
    private bool SplitLeaf(ref SplitNode? node, TerminalPane target, bool vertical, Config childConfig,
        out TerminalPane? created)
    {
        created = null;
        if (node is null) return false;

        if (node.Pane is not null)
        {
            if (!ReferenceEquals(node.Pane, target)) return false;

            var pane = new TerminalPane(childConfig);
            WirePane(pane);
            created = pane;

            var split = new SplitNode { Vertical = vertical };
            split.First = new SplitNode { Pane = node.Pane };
            split.Second = new SplitNode { Pane = pane };
            node = split;
            return true;
        }

        if (SplitLeaf(ref node.First, target, vertical, childConfig, out created)) return true;
        return SplitLeaf(ref node.Second, target, vertical, childConfig, out created);
    }

    /// <summary>Detaches the leaf and collapses its parent into the surviving sibling subtree.</summary>
    private SplitNode? RemoveLeaf(SplitNode? node, TerminalPane target, ref bool found, ref TerminalPane? nextFocus)
    {
        if (node is null) return null;

        if (node.Pane is not null)
        {
            if (!ReferenceEquals(node.Pane, target)) return node;
            found = true;
            return null;
        }

        node.First = RemoveLeaf(node.First, target, ref found, ref nextFocus);
        if (!found) node.Second = RemoveLeaf(node.Second, target, ref found, ref nextFocus);
        if (!found) return node;

        if (node.First is null)
        {
            nextFocus ??= FirstPane(node.Second);
            return node.Second;
        }
        if (node.Second is null)
        {
            nextFocus ??= FirstPane(node.First);
            return node.First;
        }
        return node;
    }

    // ---------------- pane wiring ----------------

    private void WirePane(TerminalPane pane)
    {
        pane.OnFocus = p =>
        {
            // Hovering must not move the focus while a split is being dragged across panes.
            if (_dragPane is not null) return;
            foreach (Tab tab in _tabs)
            {
                if (!TabContainsPane(tab, p)) continue;
                FocusPane(tab, p);
                return;
            }
        };
        pane.OnTitle = p =>
        {
            if (p.Title.Length == 0) return;
            foreach (Tab tab in _tabs)
            {
                if (!TabContainsPane(tab, p)) continue;
                if (ReferenceEquals(tab.Focused, p) && tab.Title != p.Title)
                {
                    tab.Title = p.Title;
                    UpdateHeaders();
                }
                return;
            }
        };
        pane.OnExit = p =>
        {
            if (PaneIsClosing(p)) return;
            for (int index = 0; index < _tabs.Count; index++)
            {
                Tab tab = _tabs[index];
                if (!TabContainsPane(tab, p)) continue;

                // The tree root is a leaf only while the tab has a single pane.
                if (tab.Root is null || tab.Root.Pane is not null)
                {
                    CloseTab(index);
                    return;
                }

                bool vertical = false, closingFirst = false;
                if (!tab.Zoomed && SplitAxisForPane(tab.Root, p, ref vertical, ref closingFirst))
                {
                    _closingPanes.Add(p);
                    AnimatePaneExit(p, vertical, closingFirst);
                }
                else
                {
                    FinishPaneRemoval(p);
                }
                return;
            }
        };
        pane.OnShellIcon = p =>
        {
            foreach (Tab tab in _tabs)
            {
                if (!TabContainsPane(tab, p)) continue;
                if (ReferenceEquals(tab.Focused, p)) UpdateHeaders();
                return;
            }
        };
        pane.OnSplitRight = _ => SplitFocused(false);
        pane.OnSplitDown = _ => SplitFocused(true);
        pane.OnDragHandleStart = BeginPaneDrag;
        pane.OnDragHandleMove = MovePaneDrag;
        pane.OnDragHandleDrop = p => EndPaneDrag(p, true);
        pane.OnDragHandleCancel = p => EndPaneDrag(p, false);
        pane.OnMaximize = _ => TogglePaneZoom();
        pane.OnClosePane = _ => CloseFocusedPane();
        pane.OnBellAttention = _ => FlashWindow();
        pane.OnCommandFinished = OnPaneCommandFinished;
        pane.OnSearchFocusReleased = p =>
        {
            // Returning focus to the IME sink lets the find bar stay visible while the
            // terminal resumes shell input, matching Ghostty's Esc behavior.
            if (_tabs.Count > 0 && ReferenceEquals(_tabs[_active].Focused, p))
                FocusKeyboardAnchor();
        };
    }

    private void TogglePaneZoom()
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Root is null || tab.Root.Pane is not null || tab.Focused is null) return;

        // The transition needs the pane's current rectangle before the rebuild moves it.
        TerminalPane focused = tab.Focused;
        SettleZoomTransition();
        Rect before = PaneBounds(focused);

        tab.Zoomed = !tab.Zoomed;
        Relayout(tab);
        FocusPane(tab, focused);
        AnimateZoomTransition(tab, focused, before);
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        SettleZoomTransition();

        Diag.Debug("tab", $"activate {index}");
        _active = index;
        _ensureActiveVisible = true;
        _surfaceHost.Children.Clear();
        AddChild(_surfaceHost, _tabs[index].Surface, Array.Empty<Grid>());
        UpdateHeaders();

        TerminalPane? pane = _tabs[index].Focused ?? FirstPane(_tabs[index].Root);
        if (pane is not null) FocusPane(_tabs[index], pane);
    }

    private void FocusPane(Tab tab, TerminalPane pane)
    {
        // A focus change wants to cancel the composition, not hand its raw pinyin to the
        // newly focused shell.
        if (tab.Focused is not null && !ReferenceEquals(tab.Focused, pane))
            _imeHost?.CancelComposition();

        tab.Focused = pane;
        ForEachPane(tab, candidate => candidate.SetFocused(ReferenceEquals(candidate, pane)));

        // A split pane switch follows whichever pane is focused; also refresh the title
        // while the tab is in the background.
        if (pane.Title.Length > 0 && tab.Title != pane.Title) tab.Title = pane.Title;
        UpdateHeaders();
        if (!ReferenceEquals(tab, _tabs[_active])) return;

        // The palette owns the keyboard while it is open; focusing the anchor here would take
        // it away from the search box and leave the palette deaf to typing and arrow keys.
        if (_paletteLayer is null || _paletteLayer.Visibility != Visibility.Visible)
            FocusKeyboardAnchor();
        UpdateInputAnchor();
        UpdateStatus();
    }

    private void SplitFocused(bool vertical)
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Focused is null || tab.Root is null) return;

        // The split inherits the focused session: same shell command and the directory the
        // shell is currently sitting in. Everything else (background opacity/blur, backdrop
        // state, font features, adjustments, ...) comes from the live config, otherwise a
        // new pane would render opaque while the others are translucent.
        var childConfig = new Config();
        childConfig.CopyFrom(_config);

        string command = tab.Focused.ShellCommand();
        if (command.Length > 0) childConfig.Command = command;
        if (_config.SplitInheritWorkingDirectory)
            childConfig.WorkingDirectory = tab.Focused.WorkingDirectory();

        SplitNode? root = tab.Root;
        if (!SplitLeaf(ref root, tab.Focused, vertical, childConfig, out TerminalPane? created) || created is null)
            return;
        tab.Root = root;

        tab.Zoomed = false;
        Relayout(tab);
        SplitFocusedTail(created, vertical);
    }
}
