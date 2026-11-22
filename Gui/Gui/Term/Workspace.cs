using System;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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

        // The grid this split lives in, kept across relayouts so splitting and closing move
        // only the affected subtree instead of rebuilding the whole tab.
        public Grid? Layout;
        public bool LayoutCompact;
        public double LayoutGap = -1;
    }

    private sealed class Tab
    {
        public string Title = string.Empty;

        // "Rename tab": a title the user pinned by hand. While it is set the shell's own
        // title reports are ignored, exactly like Ghostty's tab title override.
        public string? TitleOverride;
        public SplitNode? Root;
        public TerminalPane? Focused;
        public bool Zoomed;

        public Grid Surface = null!;
        public Border Chip = null!;
        public TextBlock ChipText = null!;
        public TextBox ChipEditor = null!;

        /// <summary>True while the chip shows its inline rename editor instead of the label.</summary>
        public bool Renaming;

        /// <summary>The template's clear button, hidden so the chip keeps a single ×.</summary>
        public Button? ChipEditorClear;
        public Image ChipIcon = null!;
        public ImageSource? ChipIconSource;

        /// <summary>OSC 7501 status dot on the shell icon (blocked, failed, done, working).</summary>
        public Ellipse ChipStatusDot = null!;
        public ChromeButton ChipClose = null!;
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

    /// <summary>
    /// A tab on its way to another window. WinUI cannot re-parent visuals across XamlRoots,
    /// so the running sessions (and with them the shells, scrollback and directories) travel
    /// instead of the controls; the destination window builds fresh panes around them.
    /// </summary>
    internal sealed class MovedTab
    {
        public string Title = string.Empty;
        public string? TitleOverride;
        public MovedSplit? Root;
    }

    internal sealed class MovedSplit
    {
        public TerminalSession? Session;
        public Config Config = null!;
        public string Title = string.Empty;
        public bool Vertical;
        public double Ratio = 0.5;
        public MovedSplit? First;
        public MovedSplit? Second;
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

        /// <summary>True while the pointer is over the row; selection styling keeps it in mind.</summary>
        public bool Hovered;
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

    /// <summary>Ghostty-style launch flags, re-applied after every reload of the file.</summary>
    private readonly IReadOnlyList<string>? _launchOverrides;

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

    /// <summary>Collapse/expand animation: a short width slide, eased out.</summary>
    private const double SidebarAnimMs = 180;
    private DispatcherQueueTimer? _sidebarTimer;
    private long _sidebarAnimStart;
    private double _sidebarAnimFrom;
    private double _sidebarAnimTo;
    private bool _sidebarAnimating;

    /// <summary>0 = rail fully open, 1 = fully collapsed; the animation feeds in between.</summary>
    private double _sidebarCollapseFraction;
    private Border _contentCard = null!;
    private Grid _contentStack = null!;
    private FontIcon? _maximizeIcon;
    private ChromeButton? _closeCaption;
    private ChromeButton? _maximizeCaption;
    private TextBlock? _minimizeTip;
    private TextBlock? _maximizeTip;
    private TextBlock? _closeTip;

    // The settings page carries its own copy of the three window buttons.
    private FontIcon? _settingsMaximizeIcon;
    private ChromeButton? _settingsMaximizeCaption;
    private TextBlock? _settingsMaximizeTip;
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
    // The same flag guards the tab and split close prompts, so only one dialog is open.
    private bool _allowClose;
    private bool _closePromptOpen;

    // A modal text prompt (e.g. "Change Window Title") owns the keyboard. The window-level
    // preview handler would otherwise send Delete/Backspace to the terminal and mark them
    // handled, so the dialog's TextBox could never delete anything.
    private bool _textPromptOpen;

    // "Change Window Title" pins the title for this window. Without it, the next title
    // refresh (e.g. a newly focused pane after another window opens) would overwrite it.
    private string? _windowTitleOverride;

    private Grid _paletteLayer = null!;
    private Border _palettePanel = null!;
    private TextBlock? _paletteBackRow;
    private TextBox _paletteInput = null!;
    private StackPanel _paletteRows = null!;
    private TextBlock? _paletteEmpty;
    private readonly List<PaletteItem> _paletteItems = new();
    private readonly List<PaletteItem> _paletteTabItems = new();
    private readonly List<PaletteItem> _paletteThemeItems = new();
    private List<PaletteItem>? _paletteActiveSub;
    private bool _paletteSubmenu;
    private bool _themePreviewActive;
    private bool _suppressPalettePreview;
    private ScrollViewer? _paletteScroller;
    private Border? _paletteChip;
    private TextBlock? _paletteChipText;
    private PaletteItem? _densityRow;
    private bool _paletteOpenedByPress;
    private string _paletteRootPlaceholder = string.Empty;
    private int _paletteSelected;
    private List<PaletteItem>? _paletteSelectedList;
    private TextBlock? _paletteThemeValue;
    private readonly List<(string Value, FontIcon Check)> _paletteWindowThemeChecks = new();

    private TextBox _imeSink = null!;
    private bool _composing;
    private bool _imeCommitted;
    private bool _imeDiscard;
    private DispatcherQueueTimer? _imeTimer;

    // The IME anchor only has to move when the cursor cell, the font or the pane's place on
    // screen changes. The timer still ticks four times a second, but writing the same Margin
    // and Width back costs a layout pass each time, which is what kept an idle window busy.
    private TerminalPane? _imeAnchorPane;
    private double _imeCursorX = double.NaN, _imeCursorY = double.NaN;
    private double _imeSinkX = double.NaN, _imeSinkY = double.NaN;
    private double _imeSinkW = double.NaN, _imeSinkH = double.NaN;
    private bool _imeAnchorDirty = true;

    // Every themed brush is created once and shared: switching the theme only has to
    // update the Color of these instances, and every element that captured a brush
    // repaints with the new palette.
    private readonly List<(Func<Color> Color, SolidColorBrush Brush)> _themedBrushes = new();

    /// <summary>Chrome buttons, so a theme change repaints their hover and pressed fills.</summary>
    private readonly List<ChromeButton> _chromeButtons = new();
    private FontFamily? _defaultTitleFont;

    private readonly List<ChipSpring> _chipSprings = new();
    private DispatcherQueueTimer? _chipSpringTimer;

    private readonly List<Tab> _tabs = new();
    private int _active;

    /// <summary>The tab that was active before the current one ("last_tab").</summary>
    private int _lastActive;
    private bool _sidebarCollapsed;
    private bool _firstSurface = true;

    /// <summary>A tab handed over by another window before this one was attached.</summary>
    private MovedTab? _initialTab;

    /// <summary>A session restored from the previous run ("window-save-state").</summary>
    private readonly WindowState? _restore;

    /// <summary>
    /// The tab whose chip is showing the inline rename editor. While it is set the window's
    /// preview key handler must stay out of the way: the editor owns the keyboard.
    /// </summary>
    private Tab? _renamingTab;

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

    /// <summary>
    /// True for the drop-down window: no caption buttons, no window drag, and Alt+F4 hides
    /// it instead of closing the shell inside.
    /// </summary>
    private readonly bool _quickTerminal;

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

    internal Workspace(Config config, MovedTab? initialTab = null, WindowState? restore = null,
        bool quickTerminal = false, IReadOnlyList<string>? launchOverrides = null)
    {
        _config = config;
        _launchOverrides = launchOverrides;
        _quickTerminal = quickTerminal;
        _initialTab = initialTab;
        _restore = restore;
        _placement = PlacementFrom(config.TabPosition);
        _sidebarWidth = Math.Max(MinSidebarWidth(), SidebarWidthFromConfig());
        _sidebarCollapsed = restore?.SidebarCollapsed == true;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _appliedLanguage = Strings.Current;
        RefreshKeybinds();
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
        // Any layout pass can move the focused pane, so the next tick re-measures.
        _root.LayoutUpdated += (_, _) => _imeAnchorDirty = true;

        // Borderless-over-its-frame window: the title bar is drawn by this class, so the
        // client area covers the whole window while the frame keeps its shadow, rounding
        // and resize edges.
        try
        {
            if (window.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                // The quick terminal has no frame at all: it is anchored to a screen edge and
                // must not show a resize border or a system menu of its own.
                presenter.SetBorderAndTitleBar(!_quickTerminal, false);
                presenter.IsResizable = !_quickTerminal;
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

        if (_initialTab is { } moved)
        {
            _initialTab = null;
            NewTabFrom(moved);
        }
        else if (_restore is { Tabs.Count: > 0 } restore)
        {
            foreach (TabState tab in restore.Tabs) RestoreTab(tab);
            // The saved panes carry their own directories; later tabs must not treat
            // "initial-command" as still pending.
            _firstSurface = false;
            if (_tabs.Count == 0) NewTab();
        }
        else
        {
            NewTab();
        }
        Activate(_restore is { } state && _tabs.Count > 0
            ? Math.Clamp(state.ActiveTab, 0, _tabs.Count - 1)
            : 0);

        // A restored collapsed rail is applied without the slide a toggle would play: the
        // window comes back in the state it was left in.
        if (_sidebarCollapsed && _placement != TabPlacement.Top) ApplySidebarChrome();

        UpdateStatus();
        ApplyTabBarVisibility();
        ScheduleStartupUpdateCheck();

        // The font picker's family list walks every installed face (~300 ms) on first build.
        // Enumerate on a background thread so the ~600 ms walk never blocks the UI; the
        // result is cached in FontCache, which both the renderer and picker share.
        _ = Task.Run(() => _ = MonospaceFontFamilies());
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
    /// "vt-window-resize-allowed": a program asked for a grid size with CSI 8 t. Like
    /// "window-width"/"window-height", the window grows or shrinks by the difference between
    /// the requested and current grid, so the tab rail and title strip need no modelling.
    /// A zero dimension keeps the current one; the size is clamped to Ghostty's 40x10 cells.
    /// </summary>
    private void ResizeWindowFromProgram(TerminalPane pane, int rows, int columns)
    {
        if (_appWindow is null || !pane.LaidOut) return;
        // The request may have been queued just before its pane closed or moved away.
        if (!_tabs.Any(tab => TabContainsPane(tab, pane))) return;

        CellMetrics metrics = pane.CellMetricsDip;
        if (metrics.Width == 0 || metrics.Height == 0) return;

        int targetColumns = columns == 0 ? pane.Columns : Math.Max(columns, 40);
        int targetRows = rows == 0 ? pane.Rows : Math.Max(rows, 10);

        double scale = _root.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0.0) scale = 1.0;

        SizeInt32 current = _appWindow.Size;
        int width = current.Width +
                    (int)Math.Round((targetColumns - pane.Columns) * metrics.Width * scale);
        int height = current.Height +
                     (int)Math.Round((targetRows - pane.Rows) * metrics.Height * scale);

        DisplayArea area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = area.WorkArea;
        width = Math.Clamp(width, 400, Math.Max(400, work.Width));
        height = Math.Clamp(height, 240, Math.Max(240, work.Height));

        if (width != current.Width || height != current.Height)
            _appWindow.Resize(new SizeInt32(width, height));
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
    /// <summary>The pane with keyboard focus, or null when the window shows no terminal.</summary>
    internal TerminalPane? FocusedPane =>
        _tabs.Count > 0 && _active >= 0 && _active < _tabs.Count ? _tabs[_active].Focused : null;

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
        // A reload is how a newly installed font reaches the app: drop the shared font set so
        // the next face lookup and the next Settings open re-enumerate, like WT's FontCache
        // invalidation on a settings change.
        FontCache.Invalidate();
        Config fresh = Config.Load();
        _config.CopyFrom(fresh);

        // Launch flags are an overlay on the file, like ghostty's own command-line options:
        // a reload re-reads the file and puts them back on top instead of dropping them.
        if (_launchOverrides is { Count: > 0 }) _config.ApplyOverrides(_launchOverrides);
        if (_quickTerminal)
        {
            // The drop-down is not a workspace: the file's tab placement, tab bar and pane
            // density do not apply to it, and a reload must not bring them back.
            _config.TabPosition = "top";
            _config.ShowTabBar = false;
            _config.Compact = true;
        }
        RefreshKeybinds();

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
                pane.ApplyImageSettings();
                pane.ApplyClipboardSettings();
                pane.ApplyProgramQueries();
                pane.ApplyKeybinds();
            });

        // The chrome is rebuilt here, so a failure must not take the app down with it.
        SetSidebarWidth(SidebarWidthFromConfig());
        Diag.Guard("ws.placement", () => ApplyTabPlacement(PlacementFrom(fresh.TabPosition)));

        // The quick terminal's hotkeys and its edge come from the same file, so a reload has
        // to re-register them here. Every window does this; registering again replaces the
        // previous chords.
        Diag.Guard("ws.quick", () => QuickTerminal.Install(_config));

        // The density is a live setting like the others: an edit to `compact` has to reach the
        // island margin, the split gaps, the pane frames and the tab chips without a restart.
        Diag.Guard("ws.density", () => SetDensity(_config.Compact));
        Diag.Guard("ws.titleFont", ApplyTitleFont);

        if (languageChanged)
            Diag.Guard("ws.language", ApplyLanguage);

        // The palette chips and pane tooltips mirror the configured keybind lines, so the
        // layer is rebuilt with the fresh map. This is also where a changed language lands,
        // because BuildPalette re-reads every Strings property.
        if (_paletteLayer is not null)
            _body.Children.Remove(_paletteLayer);
        Diag.Guard("ws.palette", BuildPalette);

        // The key list takes its labels from the palette rows, so the settings page can only
        // be rebuilt once those rows exist in the new language; rebuilding it before BuildPalette
        // left every action the palette names untranslated until a restart.
        if (languageChanged && _settingsActive)
            Diag.Guard("ws.settings.language", BuildSettings);
    }

    /// <summary>
    /// Re-labels everything this app draws itself: pane tooltips, tab automation names and
    /// the command palette.
    /// </summary>
    private void ApplyLanguage()
    {
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.ApplyLanguage());

        UpdateHeaders();
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
        _contentCard.CornerRadius = new CornerRadius(CompactDensity ? 0 : Theme.RadiusSurface);
        _surfaceHost.Margin = SurfaceMargin();
        if (_sidebar is not null) _sidebar.Padding = SidebarPadding();
        if (_header is not null)
            _header.Margin = HeaderMargin(_placement != TabPlacement.Top);
        if (_headerSideTools is not null)
            _headerSideTools.Margin = HeaderSideToolsMargin();

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
        bool settingsActive = _settingsActive;

        _root.Children.Clear();
        BuildChrome();

        foreach (UIElement chip in chips) _chipHost.Children.Add(chip);
        foreach (Tab tab in _tabs) StyleChip(tab);

        // The running shells move into the new surface host.
        if (_tabs.Count > 0) Activate(_active);

        // The page survives a rail move; Activate above is for the terminal underneath.
        if (settingsActive) ActivateSettingsTab();

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

        // A rebuilt page leaves its old buttons behind; drop them as they go.
        for (int i = _chromeButtons.Count - 1; i >= 0; i--)
        {
            ChromeButton button = _chromeButtons[i];
            if (button.Parent is null && button.XamlRoot is null)
            {
                _chromeButtons.RemoveAt(i);
                continue;
            }
            button.ApplyTheme();
        }

        // Tooltips own shared brushes too; recolor them with the same pass.
        Tips.RefreshBrushes();
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
    private Brush ControlFillOffBrush() => ThemedBrush(Theme.ControlFillOff);
    private Brush ChromeTextStrongBrush() => ThemedBrush(Theme.ChromeTextStrong);
    private Brush ChromeTextBodyBrush() => ThemedBrush(Theme.ChromeTextBody);
    private Brush ChromeTextFaintBrush() => ThemedBrush(Theme.ChromeTextFaint);
    private Brush ChromeIconStrongBrush() => ThemedBrush(Theme.ChromeIconStrong);
    private Brush ChromeIconBodyBrush() => ThemedBrush(Theme.ChromeIconBody);
    private Brush ChromeIconFaintBrush() => ThemedBrush(Theme.ChromeIconFaint);

    /// <summary>
    /// Neutral icon grey for chrome that sits on the content surface itself (the settings
    /// page): that surface follows "theme", so its icons must not follow the window brightness.
    /// </summary>
    private Brush SurfaceIconFaintBrush() => ThemedBrush(Theme.PaneIconFaint);
    private Brush ChromeChipHoverBrush() => ThemedBrush(Theme.ChromeChipHover);
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

    /// <summary>True only for the primary button, so a right-click never fires an icon button.</summary>
    private static bool IsPrimaryPress(PointerRoutedEventArgs args, UIElement element) =>
        args.GetCurrentPoint(element).Properties.IsLeftButtonPressed;

    /// <summary>True only for a primary-button release; used where a button acts on release.</summary>
    private static bool IsPrimaryRelease(PointerRoutedEventArgs args, UIElement element) =>
        args.GetCurrentPoint(element).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonReleased;

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
    /// Hover label for a caption button: the same shape the pane's action buttons use, so the
    /// chrome reads consistently. Returns the label so the maximize button can follow the
    /// window state.
    /// </summary>
    private static TextBlock CaptionTip(ChromeButton button, string text)
    {
        TextBlock title = Tips.Set(button, text).Title;
        button.SetAccessibleName(text);
        return title;
    }

    /// <summary>
    /// Labels the three window buttons; the middle one says "maximize" or "restore"
    /// depending on the window. Re-run when the window state or the UI language changes.
    /// </summary>
    private void UpdateCaptionTips()
    {
        if (_minimizeTip is not null) _minimizeTip.Text = Strings.Minimize;
        if (_closeTip is not null) _closeTip.Text = Strings.CloseWindow;
        if (_maximizeTip is null && _settingsMaximizeTip is null) return;

        bool maximized = _window is not null &&
                         _window.AppWindow.Presenter is OverlappedPresenter presenter &&
                         presenter.State == OverlappedPresenterState.Maximized;
        string title = maximized ? Strings.Restore : Strings.Maximize;
        if (_maximizeTip is not null) _maximizeTip.Text = title;
        _maximizeCaption?.SetAccessibleName(title);
        if (_settingsMaximizeTip is not null) _settingsMaximizeTip.Text = title;
        _settingsMaximizeCaption?.SetAccessibleName(title);
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

    /// <summary>
    /// The height the main header gives its caption buttons. The settings page covers that
    /// header and draws its own copy, so both use this one value.
    /// </summary>
    private double CaptionButtonHeight =>
        _placement == TabPlacement.Top ? HeaderHeight : SideHeaderHeight;

    private ChromeButton CaptionButton(char code, double width, double height, Func<Color> icon)
    {
        var button = new ChromeButton(Glyph(code, 10, ThemedBrush(icon)), width, height, new CornerRadius(0));
        button.SetColors(icon, Theme.ChromeIconStrong, hoverFill: Theme.CaptionHover);
        _chromeButtons.Add(button);
        return button;
    }

    private ChromeButton ToolButton(char code, Func<Color> icon, Func<Color> hoverFill)
    {
        var button = new ChromeButton(Glyph(code, 13, ThemedBrush(icon)), ToolButtonWidth, ToolButtonHeight,
            new CornerRadius(Theme.RadiusField))
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetColors(icon, hoverFill: hoverFill);
        _chromeButtons.Add(button);
        return button;
    }

    /// <summary>
    /// Hover label for a rail tool button: the action's name above its key chord.
    /// </summary>
    private void RailTip(ChromeButton button, string title, string actionId, string fallback) =>
        Tips.Set(button, title, _keybinds.Label(actionId, fallback));

    private ChromeButton ToolButton(Path icon, Func<Color> iconColor, Func<Color> hoverFill)
    {
        var button = new ChromeButton(icon, ToolButtonWidth, ToolButtonHeight, new CornerRadius(Theme.RadiusField))
        {
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetColors(iconColor, hoverFill: hoverFill);
        _chromeButtons.Add(button);
        return button;
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

        // Invisible IME sink: a 1x1 TextBox at the cursor cell, never visible and never painted.
        // WinUI only feeds text services for a real text control, so this element is the only
        // way an IME can compose into a custom-drawn surface; the terminal still draws the
        // composition itself and the box is emptied as soon as its text is handed over.
        _imeSink = new TextBox
        {
            Opacity = 0.0,
            Width = 8,
            Height = 16,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            AcceptsReturn = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // The terminal draws its own right-click menu; the box's built-in copy/paste
            // flyout (the "Paste / Ctrl+V" bar) must never appear over it. Setting the
            // flyout to null is what actually suppresses it — ContextRequested.Handled
            // alone does not stop TextBox's default menu.
            ContextFlyout = null,
        };
        _root.Children.Add(_imeSink);

        // The sink sits above the whole window at the cursor cell. A press on it never
        // reaches the pane's canvas (the canvas is inside the shell, not an ancestor of the
        // sink), so forward it to the focused pane and let selection start there. The sink
        // keeps its hit-testability, which text and IME input depend on.
        _imeSink.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible) return;
            if (_tabs.Count == 0) return;
            _tabs[_active].Focused?.BeginSelectionFromAnchor(args);
        }), true);
        _imeSink.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            if (_tabs.Count == 0) return;
            _tabs[_active].Focused?.MoveSelectionFromAnchor(args);
        }), true);
        _imeSink.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, args) =>
        {
            if (_tabs.Count == 0) return;
            _tabs[_active].Focused?.EndSelectionFromAnchor(args);
        }), true);

        // The sink is only ever written by the IME: plain typing arrives as TextChanged, and a
        // composition commit lands in Text one dispatcher turn after TextCompositionEnded.
        _imeSink.TextChanged += (_, _) =>
        {
            if (_composing || _imeCommitted) return;

            // A cancelled composition may be finalized through TextChanged rather than
            // TextCompositionEnded, so the discard flag has to cover both routes.
            if (_imeDiscard)
            {
                _imeDiscard = false;
                FlushIme(false);
                return;
            }

            FlushIme(true);
        };
        _imeSink.TextCompositionStarted += (_, _) =>
        {
            _imeDiscard = false;
            _composing = true;
            // The pending text key now belongs to the composition, not to a key event.
            if (_tabs.Count > 0) _tabs[_active].Focused?.CancelPendingTextKey();
            ShowImeComposition();
        };
        _imeSink.TextCompositionChanged += (_, _) => ShowImeComposition();
        _imeSink.TextCompositionEnded += (_, _) =>
        {
            _composing = false;
            _imeCommitted = true;
            ShowImeComposition();

            // At composition end Text still holds the raw composition string ("ni'hao"); the
            // IME overwrites it with the characters the user picked on the next dispatcher
            // turn, so the sink can only be drained then.
            _dispatcher?.TryEnqueue(() =>
            {
                if (_composing) return;
                _imeCommitted = false;
                bool discard = _imeDiscard;
                _imeDiscard = false;

                Diag.Guard("ws.ime.commit", () => FlushIme(!discard));
            });
        };
        _imeSink.LostFocus += (_, _) =>
        {
            // Focus leaving the sink (palette, tab chip, caption button) cancels the
            // composition just like a click, so its raw pinyin must be dropped too.
            if (_composing) _imeDiscard = true;
            _composing = false;
            _imeCommitted = false;
            ShowImeComposition();
        };
        _imeSink.Paste += (_, args) => args.Handled = true;

        EnsureInputHandlers();

        EnsureTimers();

        BuildUpdatePill();

        BuildPalette();

        // The settings page is a layer over the body. It is built on first open (ShowSettings
        // rebuilds it from the live config), so a window that never opens it does not pay for
        // ~400 ms of page construction at start-up; a chrome rebuild while it is open has to
        // carry it over to the new shell.
        if (_settingsActive) BuildSettings();
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

        // Releasing Ctrl (or another modifier) changes which links are highlightable.
        _root.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler((_, args) =>
        {
            if (IsModifierKey(args.Key)) RefreshLinkHovers();
            SendKeyUp(args);
        }), true);

        // Clicking away ends the composition but the IME finalizes it as the raw pinyin; a
        // terminal discards that text instead of sending it to the shell.
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (!_composing) return;
            _imeDiscard = true;
            ShowImeComposition();
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
    /// "report event types": key releases go to the focused pane, but only while the
    /// terminal owns the keyboard. A press consumed by a shortcut or typed into another
    /// control recorded nothing, so its release is dropped inside the pane.
    /// </summary>
    private void SendKeyUp(KeyRoutedEventArgs args)
    {
        if (_textPromptOpen) return;
        if (_composing || _tabs.Count == 0) return;
        if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible) return;
        if (_settingsLayer is not null && _settingsLayer.Visibility == Visibility.Visible) return;
        if (_renamingTab is not null) return;

        TerminalPane? pane = _tabs[_active].Focused;
        if (pane is null || pane.SearchHasFocus) return;
        pane.KeyUp(args);
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
        double headerHeight = CaptionButtonHeight;
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

            // New-tab / main menu stay pinned to the right of the header, whatever the tab
            // strip's width or scroll offset.
            var topTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 4, 4),
            };

            ChromeButton add = ToolButton('\uE710', Theme.ChromeIconFaint, Theme.ChromeChipHover);
            add.Width = 28;
            RailTip(add, Strings.NewTab, "new_tab", "Ctrl+Shift+T");
            add.SetAccessibleName(Strings.NewTab);
            add.Click += (_, _) => RequestNewTab();
            topTools.Children.Add(add);

            // The hamburger holds the window's main menu (File, command palette, settings,
            // exit). A button raises Click on release, which is what a flyout needs: one shown
            // while the button is still down is dismissed by the same press.
            ChromeButton mainMenu = ToolButton('\uE712', Theme.ChromeIconFaint, Theme.ChromeChipHover);
            mainMenu.Width = 28;
            mainMenu.SetAccessibleName(Strings.MainMenu);
            mainMenu.Click += (_, _) => ShowMainMenu(mainMenu);
            topTools.Children.Add(mainMenu);

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
                // Lines up with the rail's own collapse button: 6 DIP from the window edge,
                // which is the rail's padding.
                Margin = HeaderSideToolsMargin(),
                HorizontalAlignment = railRight ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Visibility = Visibility.Collapsed,
            };

            // The panel glyph shows the tab rail on the side it lives on.
            ChromeButton sideToggle = ToolButton(PanelIcon(ChromeIconFaintBrush(), 14, !railRight),
                Theme.ChromeIconFaint, Theme.ChromeChipHover);
            sideToggle.SetAccessibleName(Strings.ToggleSidebar);
            sideToggle.Click += (_, _) => SetSidebarCollapsed(false);
            _headerSideTools.Children.Add(sideToggle);

            Grid.SetColumn(_headerSideTools, 0);
            _headerInner.Children.Add(_headerSideTools);
        }

        // A drop-down has no window controls and no title band: Ghostty's quick terminal is
        // the surface and nothing else. The header is kept for the tab machinery that still
        // runs behind it, but it takes no room. Minimize/maximize/close would either be
        // meaningless or destroy the shell the user expects to find on the next toggle.
        if (_quickTerminal)
        {
            _header.Height = 0;
            _header.Visibility = Visibility.Collapsed;
            return;
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

        ChromeButton minimize = CaptionButton('\uE921', CaptionButtonWidth, headerHeight, Theme.ChromeIconBody);
        ChromeButton maximize = CaptionButton('\uE922', CaptionButtonWidth, headerHeight, Theme.ChromeIconBody);
        ChromeButton close = CaptionButton('\uE8BB', CaptionButtonWidth, headerHeight, Theme.ChromeIconBody);
        // The close button is the one caption that goes white on its own hover fill.
        close.SetColors(Theme.ChromeIconBody, () => Colors.White, hoverFill: Theme.CloseHover);
        _maximizeIcon = maximize.Content as FontIcon;
        _maximizeCaption = maximize;
        _closeCaption = close;
        UpdateCloseCorner();

        // The three window buttons carry the same one-line hover label the pane actions use.
        _minimizeTip = CaptionTip(minimize, Strings.Minimize);
        _maximizeTip = CaptionTip(maximize, string.Empty);
        _closeTip = CaptionTip(close, Strings.CloseWindow);
        UpdateCaptionTips();

        minimize.Click += (_, _) => MinimizeWindow();
        maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) =>
        {
            // Window.Close() does not raise AppWindow.Closing (that is only for system
            // affordances like Alt+F4), so the confirmation is requested from here. Posting
            // keeps the dialog out of the pointer dispatch.
            if (_dispatcher is null) RequestClose();
            else _dispatcher.TryEnqueue(RequestClose);
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
        // A rebuild replaces the rail, so a slide targeting the old one has to stop first.
        StopSidebarAnimation();
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
        ChromeButton collapse = ToolButton(PanelIcon(ChromeIconFaintBrush(), 14, _placement != TabPlacement.Right),
            Theme.ChromeIconFaint, Theme.ChipHover);
        RailTip(collapse, Strings.ToggleSidebar, "toggle_sidebar", "Ctrl+Shift+B");
        collapse.SetAccessibleName(Strings.ToggleSidebar);
        collapse.Click += (_, _) => SetSidebarCollapsed(true);
        tools.Children.Add(collapse);

        ChromeButton add = ToolButton('\uE710', Theme.ChromeIconFaint, Theme.ChipHover);
        RailTip(add, Strings.NewTab, "new_tab", "Ctrl+Shift+T");
        add.SetAccessibleName(Strings.NewTab);
        add.Click += (_, _) => Activate(IndexOf(NewTab()));
        tools.Children.Add(add);

        // The hamburger opens the main menu here too; the rail's own new-tab button stays
        // next to it, so the menu only carries the shared items while the row has room.
        ChromeButton mainMenu = ToolButton('\uE712', Theme.ChromeIconFaint, Theme.ChipHover);
        mainMenu.SetAccessibleName(Strings.MainMenu);
        mainMenu.Click += (_, _) => ShowMainMenu(mainMenu);
        tools.Children.Add(mainMenu);

        // When the rail's top row cannot hold the buttons, the new-tab action folds into the
        // overflow menu, where File > New tab still reaches it.
        ChromeButton menu = ToolButton('\uE712', Theme.ChromeIconFaint, Theme.ChipHover);
        menu.SetAccessibleName(Strings.MainMenu);
        menu.Click += (_, _) => ShowMainMenu(menu);
        tools.Children.Add(menu);

        void UpdateRailTools()
        {
            double leading = leftSide ? 14 : 6;
            double captions = leftSide ? 0 : 3 * CaptionButtonWidth;
            double needed = 3 * ToolButtonWidth + 2 * tools.Spacing + leading + captions;
            bool fits = (_sidebar?.ActualWidth ?? 0.0) >= needed;

            add.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
            mainMenu.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
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
        if (_sidebarAnimating) StopSidebarAnimation();
        // A collapsed rail keeps a zero width so the next open slides in from the edge.
        if (_sidebar is not null)
            _sidebar.Width = _sidebarCollapsed ? 0.0 : _sidebarWidth;
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
    /// Keeps the collapsed rail's expand button the same distance from the window edge as the
    /// rail's own collapse button (6 DIP, the rail's padding). The header already contributes
    /// a right inset, so only the remainder is set here; on the left it contributes nothing.
    /// </summary>
    private Thickness HeaderSideToolsMargin()
    {
        if (_placement != TabPlacement.Right) return new Thickness(6, 0, 0, 0);
        return new Thickness(0, 0, CompactDensity ? 6 : 0, 0);
    }

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
            CornerRadius = new CornerRadius(CompactDensity ? 0 : Theme.RadiusSurface),
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
            : new CornerRadius(Theme.RadiusField);
        // Symmetric insets: the shell icon (16 DIP) and the close button (16 DIP) sit the
        // same distance from their edge, so the chip reads as one balanced row.
        tab.Chip.Padding = top ? new Thickness(11, 0, 11, 0) : new Thickness(8, 0, 8, 0);
        tab.Chip.Margin = top ? new Thickness(0, 0, 2, 0) : new Thickness(0);
        tab.Chip.MaxWidth = top ? 220 : double.PositiveInfinity;
    }

    /// <summary>
    /// The tab's own context menu: the three close modes Ghostty exposes (this / other /
    /// right) and moving the tab to its own window, with rename on its own at the end. Like
    /// Ghostty, it acts on the clicked tab, not the active one.
    /// </summary>
    private void ShowTabMenu(Tab tab, Point position)
    {
        int index = IndexOf(tab);
        if (index < 0) return;

        var menu = new MenuFlyout();

        var close = new MenuFlyoutItem { Text = Strings.CloseTab };
        close.Click += (_, _) =>
        {
            int current = IndexOf(tab);
            if (current >= 0) CloseTab(current);
        };
        menu.Items.Add(close);

        var closeOthers = new MenuFlyoutItem
        {
            Text = Strings.CloseOtherTabs,
            IsEnabled = _tabs.Count > 1,
        };
        closeOthers.Click += (_, _) => CloseOtherTabs(tab);
        menu.Items.Add(closeOthers);

        var closeRight = new MenuFlyoutItem
        {
            Text = Strings.CloseTabsToTheRight,
            IsEnabled = index < _tabs.Count - 1,
        };
        closeRight.Click += (_, _) => CloseTabsToTheRight(tab);
        menu.Items.Add(closeRight);

        var moveToWindow = new MenuFlyoutItem
        {
            Text = Strings.MoveTabToNewWindow,
            // A window with a single tab has nothing to split off; moving it would just
            // recreate the same window.
            IsEnabled = _tabs.Count > 1,
        };
        moveToWindow.Click += (_, _) => MoveTabToNewWindow(tab);
        menu.Items.Add(moveToWindow);

        menu.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem { Text = Strings.RenameTab };
        // The flyout restores focus when it closes, so the editor is opened one turn later
        // or it would lose focus (and commit) immediately.
        rename.Click += (_, _) => _dispatcher?.TryEnqueue(() => BeginRename(tab));
        menu.Items.Add(rename);

        menu.ShowAt(tab.Chip, new FlyoutShowOptions { Position = position });
    }

    private void CloseOtherTabs(Tab tab)
    {
        var targets = new List<Tab>(_tabs.Count);
        foreach (Tab other in _tabs)
        {
            if (!ReferenceEquals(other, tab)) targets.Add(other);
        }
        CloseTabsWithConfirm(targets, tab);
    }

    private void CloseTabsToTheRight(Tab tab)
    {
        int index = IndexOf(tab);
        if (index < 0) return;

        var targets = new List<Tab>(_tabs.Count - index - 1);
        for (int next = index + 1; next < _tabs.Count; next++) targets.Add(_tabs[next]);
        CloseTabsWithConfirm(targets, null);
    }

    /// <summary>
    /// Closes a set of tabs as one action: a single "still running" prompt covers the batch
    /// instead of stacking one dialog per tab. <paramref name="keep"/> is activated after
    /// the batch when given.
    /// </summary>
    private void CloseTabsWithConfirm(List<Tab> targets, Tab? keep)
    {
        if (targets.Count == 0) return;

        bool running = false;
        foreach (Tab target in targets)
        {
            if (!TabHasRunningCommand(target)) continue;
            running = true;
            break;
        }

        if (!NeedsCloseConfirm(running))
        {
            CloseTabsNow(targets, keep);
            return;
        }
        _ = ConfirmAndCloseTabsAsync(targets, keep);
    }

    private async Task ConfirmAndCloseTabsAsync(List<Tab> targets, Tab? keep)
    {
        if (!await ConfirmCloseTerminalAsync()) return;
        CloseTabsNow(targets, keep);
    }

    private void CloseTabsNow(List<Tab> targets, Tab? keep)
    {
        foreach (Tab target in targets)
        {
            int index = IndexOf(target);
            if (index >= 0) CloseTab(index, false);
        }

        int remaining = keep is null ? -1 : IndexOf(keep);
        if (remaining >= 0) Activate(remaining);
    }

    /// <summary>
    /// "Move tab to new window": the tab keeps its splits and running shells. WinUI cannot
    /// re-parent visuals across windows, so the panes hand their sessions to the new window
    /// and it rebuilds the controls around them.
    /// </summary>
    private void MoveTabToNewWindow(Tab tab)
    {
        int index = IndexOf(tab);
        if (index < 0) return;
        SettleZoomTransition();
        if (tab.Renaming) EndRename(tab, false);

        var moved = new MovedTab
        {
            Title = tab.Title,
            TitleOverride = tab.TitleOverride,
            Root = tab.Root is null ? null : CaptureSplit(tab.Root),
        };
        if (moved.Root is null) return;

        ForEachPane(tab, pane =>
        {
            RetireExit(pane);
            _closingPanes.Remove(pane);
            ForgetPane(pane);
            // The session already belongs to the new window; only this pane's timers die.
            pane.Close();
        });

        StopChipSpring(tab);
        _chipHost.Children.RemoveAt(index);
        _tabs.RemoveAt(index);

        App.OpenWindow(null, moved);

        if (_tabs.Count == 0)
        {
            // Like Ghostty, a window that lost its last tab goes away.
            _dispatcher?.TryEnqueue(() =>
            {
                _allowClose = true;
                _window?.Close();
            });
            return;
        }

        Activate(TabSelection.AfterClose(_active, index, _tabs.Count));
        App.MarkSessionDirty();
    }

    /// <summary>
    /// Stops the shells of a tab nobody took over. Once the source window has closed the
    /// panes, a handover that failed leaves its sessions reachable only from this object;
    /// disposing them here is what keeps their ConPTY hosts from running forever.
    /// </summary>
    internal static void DiscardMovedTab(MovedTab moved) => DiscardMovedSplit(moved.Root);

    private static void DiscardMovedSplit(MovedSplit? node)
    {
        if (node is null) return;
        node.Session?.Dispose();
        DiscardMovedSplit(node.First);
        DiscardMovedSplit(node.Second);
    }

    private static MovedSplit CaptureSplit(SplitNode node)
    {
        if (node.Pane is { } pane)
        {
            return new MovedSplit
            {
                Session = pane.ReleaseSession(),
                Config = pane.ConfigSnapshot(),
                Title = pane.Title,
            };
        }

        return new MovedSplit
        {
            Vertical = node.Vertical,
            Ratio = node.Ratio,
            First = node.First is null ? null : CaptureSplit(node.First),
            Second = node.Second is null ? null : CaptureSplit(node.Second),
        };
    }

    /// <summary>
    /// Renames on the tab itself: the chip's label is swapped for an editor. The pinned
    /// title wins over the shell's own reports; an empty title hands it back to the shell.
    /// </summary>
    private void BeginRename(Tab tab)
    {
        if (tab.Renaming || IndexOf(tab) < 0) return;

        tab.Renaming = true;
        _renamingTab = tab;
        // Hiding the label collapses its column, so the editor takes over the column the label
        // sat in and the chip keeps the size it had instead of snapping to the editor's
        // minimum. The column, not the label's own width: a chip whose text is shorter than
        // the space it was given (the star column stretches) would otherwise leave the editor
        // much narrower than the tab it is renaming.
        double columnWidth = tab.Chip.Child is Grid chipGrid &&
            chipGrid.ColumnDefinitions.Count > 1
                ? chipGrid.ColumnDefinitions[1].ActualWidth
                : 0;
        tab.ChipEditor.Width = Math.Clamp(columnWidth > 0 ? columnWidth : 140.0, 60.0, 400.0);
        tab.ChipText.Visibility = Visibility.Collapsed;
        tab.ChipEditor.Text = tab.Title;
        tab.ChipEditor.Foreground = ReferenceEquals(tab, _tabs[_active])
            ? TextStrongBrush()
            : ChromeTextStrongBrush();
        tab.ChipEditor.Visibility = Visibility.Visible;

        tab.ChipEditor.Focus(FocusState.Programmatic);
        tab.ChipEditor.SelectAll();
        HideChipEditorClearButtonSoon(tab);
    }

    private void EndRename(Tab tab, bool commit)
    {
        if (!tab.Renaming) return;
        tab.Renaming = false;
        if (ReferenceEquals(_renamingTab, tab)) _renamingTab = null;
        tab.ChipEditor.Visibility = Visibility.Collapsed;
        tab.ChipText.Visibility = Visibility.Visible;

        if (commit)
        {
            string title = tab.ChipEditor.Text.Trim();
            tab.TitleOverride = title.Length == 0 ? null : title;
            tab.Title = tab.TitleOverride ?? PaneTitle(tab);
            UpdateHeaders();
        }

        // The editor took the keyboard; hand it back unless the palette owns it.
        if (_paletteLayer is null || _paletteLayer.Visibility != Visibility.Visible)
            FocusKeyboardAnchor();
    }

    /// <summary>
    /// The stock TextBox template puts a clear button on its trailing edge, which reads as a
    /// second close button inside the chip. Every text change shows it again, so hide it again.
    /// </summary>
    private void HideChipEditorClearButton(Tab tab)
    {
        tab.ChipEditorClear ??= FindDescendant(tab.ChipEditor, "DeleteButton") as Button;
        if (tab.ChipEditorClear is not { } deleteButton) return;
        if (deleteButton.Visibility == Visibility.Collapsed && deleteButton.Width == 0) return;

        deleteButton.Visibility = Visibility.Collapsed;
        deleteButton.Width = 0;
        deleteButton.MinWidth = 0;
        deleteButton.Padding = new Thickness(0);
        deleteButton.Margin = new Thickness(0);
        deleteButton.Opacity = 0;
        deleteButton.IsHitTestVisible = false;
    }

    private void HideChipEditorClearButtonSoon(Tab tab)
    {
        if (_dispatcher is null)
        {
            HideChipEditorClearButton(tab);
            return;
        }
        _dispatcher.TryEnqueue(() => HideChipEditorClearButton(tab));
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

    /// <summary>The title the chip falls back to when no rename is pinned.</summary>
    private string PaneTitle(Tab tab)
    {
        string live = tab.Focused?.Title ?? string.Empty;
        return live.Length == 0 ? ShellDisplayName(_config.Command) : live;
    }

    /// <summary>
    /// Opens a tab on behalf of the CLI's "+new-tab": the caller may pin the directory, the
    /// command and the title; everything else follows this window's config. The tab becomes
    /// active right away, like the new-tab button: a background tab's surface stays out of
    /// the tree, so its shell would not start until the user clicked the chip.
    /// </summary>
    internal void OpenTab(string? workingDirectory, string? command, string? title)
    {
        Tab tab = OpenTabCore(workingDirectory, command, title);
        Activate(IndexOf(tab));
    }

    private Tab OpenTabCore(string? workingDirectory, string? command, string? title)
    {
        Config config = NewTabConfig(ActivePane());
        if (ReferenceEquals(config, _config))
        {
            // NewTabConfig hands back the shared instance when nothing is inherited; the
            // overrides below must not leak into the window's config.
            config = new Config();
            config.CopyFrom(_config);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory)) config.WorkingDirectory = workingDirectory!;
        if (!string.IsNullOrWhiteSpace(command)) config.Command = command!;

        var pane = new TerminalPane(config);
        WirePane(pane);

        bool named = !string.IsNullOrWhiteSpace(title);
        if (named) pane.SetTitleOverride(title);

        return AddTab(named ? title! : ShellDisplayName(config.Command), named ? title : null, pane,
            new SplitNode { Pane = pane });
    }

    private Tab NewTab()
    {
        var pane = new TerminalPane(NewTabConfig(ActivePane()));
        WirePane(pane);
        return AddTab(ShellDisplayName(_config.Command), null, pane, new SplitNode { Pane = pane });
    }

    /// <summary>Rebuilds a tab another window handed over, keeping its running sessions.</summary>
    private Tab NewTabFrom(MovedTab moved)
    {
        // Panes become reachable from a tab only once AddTab lines them up, so every pane this
        // build creates is collected here: a throw in the middle of the tree would otherwise
        // strand the earlier ones on a recursive local, and their repeating cwd timer (started
        // by AdoptSession) keeps the pane, its session and its shell alive for good.
        var built = new List<TerminalPane>();
        TerminalPane? focused = null;
        try
        {
            SplitNode? root = moved.Root is null ? null : BuildMovedSplit(moved.Root, built);
            focused = root is null ? null : root.Pane ?? FirstPane(root);
            if (root is null || focused is null) return NewTab();

            return AddTab(moved.Title, moved.TitleOverride, focused, root);
        }
        catch
        {
            // Once the panes are reachable from a tab, Workspace.Close() owns them. Otherwise
            // nothing does — the adopted sessions already have a Post, so the orphan fallback
            // for an ownerless session cannot fire — and the panes built so far have to stop
            // their shells here.
            bool adopted = false;
            if (focused is not null)
            {
                foreach (Tab existing in _tabs)
                    if (TabContainsPane(existing, focused)) { adopted = true; break; }
            }
            if (!adopted)
                foreach (TerminalPane pane in built) pane.Close();
            throw;
        }
    }

    private SplitNode BuildMovedSplit(MovedSplit source, List<TerminalPane> built)
    {
        if (source.Session is not { } session)
        {
            return new SplitNode
            {
                Vertical = source.Vertical,
                Ratio = source.Ratio,
                First = source.First is null ? null : BuildMovedSplit(source.First, built),
                Second = source.Second is null ? null : BuildMovedSplit(source.Second, built),
            };
        }

        var pane = new TerminalPane(source.Config);
        built.Add(pane);
        pane.Title = source.Title;
        WirePane(pane);
        pane.AdoptSession(session);
        // The pane owns the session now. Clearing the handover object is what lets a failed
        // build discard only the sessions nobody adopted, and what stops the source tab from
        // keeping every adopted session reachable for the window's whole life.
        source.Session = null;
        return new SplitNode { Pane = pane };
    }

    /// <summary>True unless "window-save-state = never" turns session persistence off.</summary>
    internal bool SavesWindowState => _config.WindowSaveState != "never";

    /// <summary>Whether the tab rail is collapsed; saved with the window state.</summary>
    internal bool SidebarCollapsed => _sidebarCollapsed;

    // ---------------- window-save-state ----------------

    /// <summary>
    /// Snapshots every tab for the session file. Pane directories come from the shell's
    /// OSC 7 report, so a restored session reopens where the shells were sitting.
    /// </summary>
    internal List<TabState> CaptureTabs(out int activeTab)
    {
        activeTab = _tabs.Count == 0 ? 0 : Math.Clamp(_active, 0, _tabs.Count - 1);
        var tabs = new List<TabState>(_tabs.Count);
        foreach (Tab tab in _tabs)
        {
            string? focused = null;
            SplitState? root = CaptureSplit(tab.Root, tab.Focused, string.Empty, ref focused);
            if (root is null) continue;
            tabs.Add(new TabState
            {
                Title = tab.Title,
                TitleOverride = tab.TitleOverride,
                Zoomed = tab.Zoomed,
                Focused = focused ?? string.Empty,
                Root = root,
            });
        }
        return tabs;
    }

    private static SplitState? CaptureSplit(SplitNode? node, TerminalPane? focused,
        string path, ref string? focusedPath)
    {
        if (node is null) return null;

        if (node.Pane is { } pane)
        {
            if (ReferenceEquals(pane, focused)) focusedPath = path;
            string? snapshot = pane.SnapshotForSave();
            return new SplitState
            {
                Pane = new PaneState
                {
                    Directory = pane.WorkingDirectory(),
                    Command = pane.ConfiguredCommand(),
                    AutoCommand = pane.ConfiguredCommand().Length == 0,
                    Title = pane.Title,
                    Snapshot = snapshot,
                },
            };
        }

        if (node.First is null || node.Second is null) return null;
        SplitState? first = CaptureSplit(node.First, focused, path + "L", ref focusedPath);
        SplitState? second = CaptureSplit(node.Second, focused, path + "R", ref focusedPath);
        if (first is null || second is null) return first ?? second;

        return new SplitState
        {
            Vertical = node.Vertical,
            Ratio = node.Ratio,
            First = first,
            Second = second,
        };
    }

    /// <summary>Rebuilds one saved tab: fresh shells in the saved directories, same split tree.</summary>
    private void RestoreTab(TabState state)
    {
        SplitNode? root = state.Root is null ? null : RestoreSplit(state.Root);
        if (root is null) return;

        TerminalPane? focused = PaneAtPath(root, state.Focused) ?? FirstPane(root);
        if (focused is null) return;

        Tab tab = AddTab(
            state.Title.Length > 0 ? state.Title : ShellDisplayName(_config.Command),
            state.TitleOverride, focused, root, append: true);
        if (!state.Zoomed) return;
        tab.Zoomed = true;
        Relayout(tab);
    }

    private SplitNode? RestoreSplit(SplitState source)
    {
        if (source.Pane is { } saved)
        {
            var config = new Config();
            config.CopyFrom(_config);

            // A directory that no longer exists (removed drive, deleted folder) falls back
            // to the configured start directory instead of dropping the pane.
            string directory = saved.Directory;
            if (directory.Length == 0 || !Directory.Exists(directory))
                directory = _config.WorkingDirectory;
            if (directory.Length > 0) config.WorkingDirectory = directory;

            if (!saved.AutoCommand && saved.Command.Length > 0 && RestoredCommand.Usable(saved.Command, directory))
                config.Command = saved.Command;

            byte[]? snapshot = WindowStateStore.ReadSnapshot(saved.Snapshot);
            var pane = new TerminalPane(config, snapshot, snapshot is null ? null : RestoreBanner(),
                saved.Snapshot)
            {
                Restored = true,
            };
            if (saved.Title.Length > 0) pane.Title = saved.Title;
            WirePane(pane);
            return new SplitNode { Pane = pane };
        }

        if (source.First is null || source.Second is null) return null;
        SplitNode? first = RestoreSplit(source.First);
        SplitNode? second = RestoreSplit(source.Second);
        if (first is null || second is null) return first ?? second;

        double ratio = source.Ratio;
        if (double.IsNaN(ratio) || ratio <= 0.0 || ratio >= 1.0) ratio = 0.5;
        return new SplitNode
        {
            Vertical = source.Vertical,
            Ratio = ratio,
            First = first,
            Second = second,
        };
    }

    /// <summary>The pane a saved "L/R" path points at, or null when the path no longer fits.</summary>
    private static TerminalPane? PaneAtPath(SplitNode root, string path)
    {
        SplitNode node = root;
        foreach (char step in path)
        {
            SplitNode? next = step switch
            {
                'L' => node.First,
                'R' => node.Second,
                _ => null,
            };
            if (next is null) return null;
            node = next;
        }
        return node.Pane ?? FirstPane(node);
    }

    /// <summary>The dim divider written above a restored pane's new shell.</summary>
    private string RestoreBanner()
    {
        DateTime saved = _restore?.SavedAt ?? DateTime.Now;
        if (saved == default) saved = DateTime.Now;
        string when = saved.ToString("ddd MMM d HH:mm", CultureInfo.InvariantCulture);
        return $"── restored · session ended {when} ──";
    }

    /// <param name="append">
    /// Restored tabs keep the order they were saved in; "window-new-tab-position" only applies
    /// to tabs the user opens. Otherwise every restored tab is inserted after the active one
    /// and the strip comes back rearranged.
    /// </param>
    private Tab AddTab(string title, string? titleOverride, TerminalPane focused, SplitNode root,
        bool append = false)
    {
        Diag.Debug("tab", $"create start #{_tabs.Count + 1}");
        var tab = new Tab
        {
            Title = title,
            TitleOverride = titleOverride,
            Focused = focused,
            Root = root,
        };

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
            int index = IndexOf(self);
            // Right-click opens the tab menu for the chip that was clicked; like Ghostty, the
            // menu acts on that tab without making it the active one.
            PointerPoint point = args.GetCurrentPoint(self.Chip);
            if (point.Properties.IsRightButtonPressed)
            {
                if (index >= 0) ShowTabMenu(self, point.Position);
                return;
            }
            // The strip is about to reorder; starting a drag now would race that
            // collection change.
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
            Point stripPoint = args.GetCurrentPoint(_chipHost).Position;
            _dragTab = self;
            _dragArmed = true;
            _dragMoving = false;
            _dragPressX = stripPoint.X;
            _dragPressY = stripPoint.Y;
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

        // Same shell icon as the pane header: a 16 DIP slot that stays empty until the
        // extracted executable icon is ready, instead of showing a placeholder glyph.
        var iconSlot = new Grid
        {
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        tab.ChipIcon = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
        };
        iconSlot.Children.Add(tab.ChipIcon);

        // The chip reports the focused pane's OSC 7501 status without widening the chip:
        // a dot on the corner of the shell icon, coloured by the state.
        tab.ChipStatusDot = new Ellipse
        {
            Width = 6,
            Height = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -1, -1),
            Visibility = Visibility.Collapsed,
        };
        iconSlot.Children.Add(tab.ChipStatusDot);

        chipGrid.Children.Add(iconSlot);

        tab.ChipText = Label(tab.Title, 12, ChromeTextBodyBrush());
        tab.ChipText.FontFamily = TitleFontFamily();
        tab.ChipText.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(tab.ChipText, 1);
        chipGrid.Children.Add(tab.ChipText);

        // Inline rename: the label swaps for a TextBox in the same column, so the title is
        // edited on the tab itself. Enter commits, Esc restores, leaving the editor commits.
        tab.ChipEditor = new TextBox
        {
            FontFamily = TitleFontFamily(),
            FontSize = Theme.FontBody,
            MaxLength = 256,
            MinWidth = 60,
            // The stock style's 32 DIP minimum would override the height and leave the text
            // sitting at the top of the box; the vertical padding centers the caret instead.
            MinHeight = 26,
            Height = 26,
            Padding = new Thickness(6, 5, 6, 5),
            BorderThickness = new Thickness(0),
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = false,
            Visibility = Visibility.Collapsed,
        };
        // The stock template paints a field and an accent underline on focus; the chip keeps
        // its own surface, so the editor has to stay flat and transparent.
        tab.ChipEditor.Resources["TextControlBackground"] = ClearBrush();
        tab.ChipEditor.Resources["TextControlBackgroundPointerOver"] = ClearBrush();
        tab.ChipEditor.Resources["TextControlBackgroundFocused"] = ClearBrush();
        tab.ChipEditor.Resources["TextControlBackgroundPressed"] = ClearBrush();
        tab.ChipEditor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        tab.ChipEditor.Resources["TextControlBorderBrushFocused"] = ClearBrush();
        tab.ChipEditor.Loaded += (_, _) => HideChipEditorClearButton(self);
        tab.ChipEditor.TextChanged += (_, _) => HideChipEditorClearButtonSoon(self);
        // The box's own visual states show the clear button again after focus and text
        // changes; the layout pass after that state change hides it before it is painted.
        tab.ChipEditor.LayoutUpdated += (_, _) =>
        {
            if (tab.ChipEditor.Visibility == Visibility.Visible) HideChipEditorClearButton(self);
        };
        tab.ChipEditor.LostFocus += (_, _) => EndRename(self, true);
        // Keep the press inside the editor; the chip would otherwise start a tab drag.
        tab.ChipEditor.PointerPressed += (_, args) => args.Handled = true;
        Grid.SetColumn(tab.ChipEditor, 1);
        chipGrid.Children.Add(tab.ChipEditor);

        tab.ChipClose = new ChromeButton(Glyph('\uE711', 10, ChromeIconFaintBrush()), 16, 16,
            new CornerRadius(Theme.RadiusChip))
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        tab.ChipClose.SetColors(Theme.ChromeIconFaint, hoverFill: Theme.ChromeCloseButtonHover);
        // Only a primary click closes; a right press belongs to the tab menu, which the chip
        // no longer sees once the button has handled the press.
        tab.ChipClose.RightTapped += (_, args) =>
        {
            args.Handled = true;
            ShowTabMenu(self, args.GetPosition(self.Chip));
        };
        tab.ChipClose.Click += (_, _) =>
        {
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

        AutomationProperties.SetName(tab.ChipFocus, tab.Title);
        int index = append ? _tabs.Count : TabInsertIndex();
        _chipHost.Children.Insert(index, tab.ChipFocus);

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

    /// <summary>
    /// The pane an action works on. "all:" bindings run once per pane and point this at each
    /// of them in turn, so every action that goes through ActivePane() follows the prefix
    /// without a second, pane-targeted copy of the action table.
    /// </summary>
    private TerminalPane? ActivePane() =>
        _surfaceTarget ??
        (_tabs.Count > 0 && _active >= 0 && _active < _tabs.Count ? _tabs[_active].Focused : null);

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

        // While the inline editor is up these keys are the text box's: the caret moves and
        // Delete edits text. They reach this handler only because the box leaves them
        // unhandled, so the strip must not treat them as tab navigation or tab close.
        if (self.Renaming) return;

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

        // Every structural change runs through here (tab created, split added or removed,
        // zoom toggled), so the session file stays current even if the app never exits
        // cleanly.
        App.MarkSessionDirty();
    }

    private void RebuildLayout(Tab tab)
    {
        SettleExitAnimations();

        // The last pane cannot be zoomed or closed; the buttons stay visible but disabled.
        bool singlePane = tab.Root is not null && tab.Root.Pane is not null;
        ForEachPane(tab, pane => pane.SetSinglePane(singlePane));
        ForEachPane(tab, pane => pane.SetCompact(CompactDensity));

        // Ghostty's split handle is the pane header itself. Dragging is enabled once a tab
        // has two or more splits; "always" keeps it on a lone pane too, and "never" turns
        // split dragging off entirely. A zoomed tab paints one pane and refuses the drag,
        // so it must not advertise the move cursor either.
        bool dragEnabled = !tab.Zoomed && _config.DragHandle != "never" &&
                           (!singlePane || _config.DragHandle == "always");
        ForEachPane(tab, pane => pane.SetDragEnabled(dragEnabled));

        EnsureSurface(tab.Surface);

        // A background tab's surface is not in the visual tree, where neither Parent nor
        // VisualTreeHelper reports the grid that still holds a pane; WinUI then throws when
        // that pane is added to the new grid. Detach the previous layout structurally — the
        // node tree knows every grid it built — before anything is placed again.
        DetachLayouts(tab);

        if (tab.Zoomed && tab.Focused is not null)
        {
            TerminalPane focused = tab.Focused;
            PlaceElement(tab.Surface, focused.Visual, 0, 0);
            ForEachPane(tab, pane => pane.SetZoomed(ReferenceEquals(pane, focused)));
            return;
        }
        if (tab.Root is null) return;

        ForEachPane(tab, pane => pane.SetZoomed(false));
        PlaceNode(tab.Root, tab.Surface, 0, 0);
    }

    /// <summary>
    /// Empties every grid the last relayout built and unhooks the root one from the surface,
    /// so the placement that follows starts from unparented elements.
    /// </summary>
    private static void DetachLayouts(Tab tab)
    {
        if (tab.Root is null) return;
        if (tab.Root.Layout is { } root && tab.Surface.Children.Contains(root)) tab.Surface.Children.Remove(root);
        ClearLayouts(tab.Root);
    }

    private static void ClearLayouts(SplitNode node)
    {
        if (node.Pane is not null) return;
        node.Layout?.Children.Clear();
        ClearLayouts(node.First!);
        ClearLayouts(node.Second!);
    }

    /// <summary>The surface is one star-sized cell; panes and split grids live inside it.</summary>
    private static void EnsureSurface(Grid surface)
    {
        if (surface.RowDefinitions.Count == 1 && surface.ColumnDefinitions.Count == 1) return;
        surface.RowDefinitions.Clear();
        surface.ColumnDefinitions.Clear();
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    /// <summary>
    /// Places a node in one cell of <paramref name="host"/>. A split keeps the grid it already
    /// lives in (SplitNode.Layout), so a split or a close moves only the affected subtree:
    /// rebuilding every grid on each relayout is what allocated, and leaked, native memory.
    /// </summary>
    private void PlaceNode(SplitNode node, Panel host, int row, int col)
    {
        if (node.Pane is not { } pane)
        {
            Grid grid = SplitGrid(node);
            PlaceElement(host, grid, row, col);
            ApplySplitRatio(node, grid);
            if (node.Vertical)
            {
                PlaceNode(node.First!, grid, 0, 0);
                PlaceNode(node.Second!, grid, 2, 0);
            }
            else
            {
                PlaceNode(node.First!, grid, 0, 0);
                PlaceNode(node.Second!, grid, 0, 2);
            }
            return;
        }

        PlaceElement(host, pane.Visual, row, col);
    }

    /// <summary>Puts an element in a cell, detaching it from whatever grid held it before.</summary>
    private static void PlaceElement(Panel host, FrameworkElement element, int row, int col)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, col);
        // Contains, not Parent: a background tab's surface is outside the visual tree, where
        // both Parent and VisualTreeHelper report nothing while the element is still a child
        // of the grid it was placed in last time; adding it again throws 0x800F1000.
        if (host.Children.Contains(element)) return;
        if (element.Parent is Panel parent) parent.Children.Remove(element);
        if (VisualTreeHelper.GetParent(element) is Panel visual) visual.Children.Remove(element);
        host.Children.Add(element);
    }

    /// <summary>Builds the grid a split lives in, re-creating it only after a density or gap change.</summary>
    private Grid SplitGrid(SplitNode node)
    {
        bool compact = CompactDensity;
        double gap = compact ? 1.0 : SurfaceGap;
        if (node.Layout is { } cached && node.LayoutCompact == compact &&
            Math.Abs(node.LayoutGap - gap) < 0.01)
        {
            return cached;
        }

        // A density or gap change replaces the grid. The old one is still a child of the host,
        // so without this it would stay in the surface beside the new grid, divider included.
        if (node.Layout is { } stale)
        {
            if (stale.Parent is Panel previous) previous.Children.Remove(stale);
            stale.Children.Clear();
        }

        var grid = new Grid();
        if (node.Vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(gap) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.0 - node.Ratio, GridUnitType.Star) });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(node.Ratio, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(gap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0 - node.Ratio, GridUnitType.Star) });
        }

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

        // The grip stays above both halves whatever order the panes are placed in later.
        FrameworkElement grip = BuildSplitter(node, grid, gap);
        Canvas.SetZIndex(grip, 1);
        grid.Children.Add(grip);

        node.Layout = grid;
        node.LayoutCompact = compact;
        node.LayoutGap = gap;
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
    /// Replaces the focused leaf with a two-leaf split; the new pane lands in the second half
    /// (right or bottom), or in the first half when splitting left or up.
    /// </summary>
    private bool SplitLeaf(ref SplitNode? node, TerminalPane target, bool vertical, bool before,
        Config childConfig, out TerminalPane? created)
    {
        created = null;
        if (node is null) return false;

        if (node.Pane is not null)
        {
            if (!ReferenceEquals(node.Pane, target)) return false;

            var pane = new TerminalPane(childConfig);
            WirePane(pane);
            created = pane;

            var current = new SplitNode { Pane = node.Pane };
            var fresh = new SplitNode { Pane = pane };
            node = new SplitNode
            {
                Vertical = vertical,
                First = before ? fresh : current,
                Second = before ? current : fresh,
            };
            return true;
        }

        if (SplitLeaf(ref node.First, target, vertical, before, childConfig, out created)) return true;
        return SplitLeaf(ref node.Second, target, vertical, before, childConfig, out created);
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
            DropLayout(node);
            return node.Second;
        }
        if (node.Second is null)
        {
            nextFocus ??= FirstPane(node.First);
            DropLayout(node);
            return node.First;
        }
        return node;
    }

    /// <summary>
    /// Drops the grid a collapsed split node lived in. Once the node leaves the tree nothing
    /// re-places that grid, so it would stay a child of the surface (or of its parent's grid)
    /// for the rest of the window's life, keeping its divider, its resize grip and the closed
    /// pane's visuals alive and hit-testable. The children are detached rather than thrown
    /// away: the surviving sibling subtree is re-parented by the relayout that follows.
    /// </summary>
    private static void DropLayout(SplitNode node)
    {
        Grid? grid = node.Layout;
        if (grid is null) return;
        node.Layout = null;
        if (grid.Parent is Panel owner) owner.Children.Remove(grid);
        grid.Children.Clear();
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
                if (ReferenceEquals(tab.Focused, p) && tab.TitleOverride is null && tab.Title != p.Title)
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
                    // The shell exited on its own: there is nothing left to confirm.
                    CloseTab(index, false);
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
            // A background tab has no focused pane yet, and its chip still has to pick the
            // icon up as soon as it is read, not when the tab is finally opened.
            foreach (Tab tab in _tabs)
            {
                if (!TabContainsPane(tab, p)) continue;
                UpdateHeaders();
                return;
            }
        };
        pane.OnProgramStatusChanged = p =>
        {
            foreach (Tab tab in _tabs)
            {
                if (!TabContainsPane(tab, p)) continue;
                UpdateHeaders();
                return;
            }
        };
        pane.OnSplitRight = _ => SplitFocused(SplitDirection.Right);
        pane.OnSplitDown = _ => SplitFocused(SplitDirection.Down);
        pane.OnSplitLeft = _ => SplitFocused(SplitDirection.Left);
        pane.OnSplitUp = _ => SplitFocused(SplitDirection.Up);
        pane.OnDragHandleStart = BeginPaneDrag;
        pane.OnDragHandleMove = MovePaneDrag;
        pane.OnDragHandleDrop = p => EndPaneDrag(p, true);
        pane.OnDragHandleCancel = p => EndPaneDrag(p, false);
        pane.OnMaximize = _ => TogglePaneZoom();
        pane.OnClosePane = _ => CloseFocusedPane();
        pane.OnBellAttention = _ => FlashWindow();
        pane.OnCommandFinished = OnPaneCommandFinished;
        pane.OnResizeWindow = ResizeWindowFromProgram;
        pane.OnDesktopNotification = OnPaneDesktopNotification;
        pane.OnProgramAttention = OnPaneProgramAttention;
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

    /// <summary>Steps the active tab by <paramref name="delta"/> and wraps around.</summary>
    private void CycleTab(int delta)
    {
        int count = _tabs.Count;
        if (count == 0) return;
        Activate(((_active + delta) % count + count) % count);
    }

    /// <summary>Returns to the tab that was active before the current one ("last_tab").</summary>
    private void ActivateLastTab()
    {
        if (_lastActive < 0 || _lastActive >= _tabs.Count || _lastActive == _active) return;
        Activate(_lastActive);
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        SettleZoomTransition();

        // Choosing a terminal tab leaves the settings page, the same way the design's
        // setActive() switches away from its settings tab.
        DeactivateSettings();

        Diag.Debug("tab", $"activate {index}");
        if (_active != index) _lastActive = _active;
        _active = index;
        _ensureActiveVisible = true;
        _surfaceHost.Children.Clear();
        PlaceElement(_surfaceHost, _tabs[index].Surface, 0, 0);
        UpdateHeaders();

        // A background tab misses the draws that happen while it is hidden; paint it again
        // now that its surface is back in the tree so it never shows a stale frame.
        ForEachPane(_tabs[index], pane => pane.Redraw());

        TerminalPane? pane = _tabs[index].Focused ?? FirstPane(_tabs[index].Root);
        if (pane is not null) FocusPane(_tabs[index], pane);
    }

    private void FocusPane(Tab tab, TerminalPane pane)
    {
        // A focus change wants to cancel the composition, not hand its raw pinyin to the
        // newly focused shell. The IME finalizes it one dispatcher turn later, so mark it now.
        if (_composing && tab.Focused is not null && !ReferenceEquals(tab.Focused, pane))
        {
            _imeDiscard = true;
            ShowImeComposition();
        }

        tab.Focused = pane;
        ForEachPane(tab, candidate => candidate.SetFocused(ReferenceEquals(candidate, pane)));

        // A split pane switch follows whichever pane is focused; also refresh the title
        // while the tab is in the background. A pinned tab title ignores both.
        if (tab.TitleOverride is null && pane.Title.Length > 0 && tab.Title != pane.Title) tab.Title = pane.Title;
        UpdateHeaders();
        if (!ReferenceEquals(tab, _tabs[_active])) return;

        // The palette owns the keyboard while it is open; focusing the anchor here would take
        // it away from the search box and leave the palette deaf to typing and arrow keys.
        if (_paletteLayer is null || _paletteLayer.Visibility != Visibility.Visible)
            FocusKeyboardAnchor();
        UpdateInputAnchor();
        UpdateStatus();
    }

    private void SplitFocused(SplitDirection direction)
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Focused is null || tab.Root is null) return;

        bool vertical = direction is SplitDirection.Down or SplitDirection.Up;
        bool before = direction is SplitDirection.Left or SplitDirection.Up;

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
        if (!SplitLeaf(ref root, tab.Focused, vertical, before, childConfig, out TerminalPane? created) ||
            created is null)
            return;
        tab.Root = root;

        tab.Zoomed = false;
        Relayout(tab);
        SplitFocusedTail(created, vertical);
    }
}
