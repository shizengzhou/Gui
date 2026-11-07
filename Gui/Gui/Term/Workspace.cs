using System;
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
    private Border _contentCard = null!;
    private Grid _contentStack = null!;
    private FontIcon? _maximizeIcon;
    private Grid _surfaceHost = null!;
    private StackPanel? _topTabHost;
    private StackPanel? _tabColumn;
    private StackPanel _chipHost = null!;
    private StackPanel? _headerSideTools;
    private ScrollViewer? _tabScroller;
    private Canvas? _arcLayer;
    private Path? _tabArcLeft;
    private Path? _tabArcRight;
    private bool _arcUpdateQueued;
    private StackPanel? _stripHost;
    private TranslateTransform? _stripTransform;
    private double _stripOffset;
    // One-shot: scroll the active chip into view only after the active tab changed (or a tab
    // was added/removed), never on ordinary repaints, otherwise it fights the wheel.
    private bool _ensureActiveVisible;
    private double _arcLeft;
    private double _arcRight;
    private double _arcBottom;
    private bool _arcPlaceable;
    private bool _headersQueued;
    private IntPtr _hwnd;

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
    private string _paletteRootPlaceholder = string.Empty;
    private int _paletteSelected;
    private TextBlock? _paletteWindowValue;
    private TextBlock? _paletteThemeValue;
    private readonly List<FontIcon> _paletteWindowChecks = new();
    private readonly List<FontIcon> _paletteThemeChecks = new();

    private TextBox _imeSink = null!;
    private DispatcherQueueTimer? _imeTimer;

    // Every themed brush is created once and shared: switching the theme only has to
    // update the Color of these instances, and every element that captured a brush
    // repaints with the new palette.
    private readonly List<(Func<Color> Color, SolidColorBrush Brush)> _themedBrushes = new();

    private readonly List<ChipSpring> _chipSprings = new();
    private DispatcherQueueTimer? _chipSpringTimer;

    private readonly List<Tab> _tabs = new();
    private int _active;
    private bool _sidebarCollapsed;

    private readonly List<ExitAnimation> _exitAnimations = new();
    private readonly List<TerminalPane> _closingPanes = new();
    private Tab? _relayouting;

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
    private bool _composing;
    private bool _imeCommitted;
    private bool _imeDiscard;
    private TerminalPane? _imeCompositionPane;
    private bool _keySeen;
    private bool _swallowText;

    public Workspace(Config config)
    {
        _config = config;
        _placement = PlacementFrom(config.TabPosition);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
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
            ApplyWindowTheme();
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
        Config fresh = Config.Load();
        _config.CopyFrom(fresh);

        // The language is switchable at runtime. It is selected before the theme and the tab
        // rail are refreshed so any chrome rebuild already uses the new strings; the pass at
        // the end then re-labels the elements that survive.
        bool languageChanged = _config.Language != Strings.Current;
        if (languageChanged) Strings.Use(_config.Language);

        Theme.Light = fresh.LightTheme;
        Theme.WindowLight = fresh.WindowLight;
        Theme.Terminal = fresh.TerminalTheme;

        RefreshTheme();

        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.ApplyFont());

        // The chrome is rebuilt here, so a failure must not take the app down with it.
        Diag.Guard("ws.placement", () => ApplyTabPlacement(PlacementFrom(fresh.TabPosition)));

        if (languageChanged)
            Diag.Guard("ws.language", ApplyLanguage);

        Diag.Log("config", "reloaded");
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
    }

    /// <summary>
    /// Moves the tab rail to the top, the left or the right. The chrome (header, tab rail,
    /// sidebar) is rebuilt for the new placement; the tabs, their chips and the running
    /// shells are carried over to it.
    /// </summary>
    private void ApplyTabPlacement(TabPlacement placement)
    {
        if (placement == _placement) return;
        _placement = placement;

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
        UpdateImeSink();
        Diag.Log("tab", $"placement {_placement}");
    }

    // ---------------- theme brushes ----------------

    private bool _backdropApplied;

    /// <summary>
    /// "background-opacity" needs something behind the panes. Like Windows Terminal, a
    /// translucent background with "background-blur" on gets the DWM acrylic backdrop and
    /// the layers between it and the terminal go transparent, so the wallpaper shows
    /// through. With blur off (or without acrylic support) the simulated mica stays as the
    /// app background and only the card becomes transparent.
    /// </summary>
    private void ApplyBackgroundOpacity()
    {
        bool translucent = _config.BackgroundOpacity < 0.999f;
        bool blur = translucent && _config.BackgroundBlur;

        bool acrylic = false;
        if (blur)
        {
            try
            {
                acrylic = Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported();
            }
            catch
            {
                acrylic = false;
            }
        }

        bool wantBackdrop = blur && acrylic;
        if (wantBackdrop != _backdropApplied)
        {
            _backdropApplied = wantBackdrop;
            try
            {
                if (_window is not null)
                {
                    _window.SystemBackdrop = wantBackdrop ? new DesktopAcrylicBackdrop() : null;
                }
            }
            catch (Exception error)
            {
                Diag.LogException("ws.backdrop", error);
            }
        }

        _root.Background = wantBackdrop ? ClearBrush() : WindowMicaBrush();
        if (_contentCard is not null)
            _contentCard.Background = translucent ? ClearBrush() : CardFillBrush();
    }

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
            Width = 30,
            Height = 28,
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
            Width = 30,
            Height = 28,
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

    /// <summary>
    /// Mirrors the design's radial-gradient corner: an 8x8 square whose only filled part is
    /// the concave sliver between a quarter circle and the outer corner.
    /// </summary>
    private Path CornerArc(bool left)
    {
        var path = new Path
        {
            Width = 8,
            Height = 8,
            Fill = CardFillBrush(),
            IsHitTestVisible = false,
        };

        var figure = new PathFigure { IsClosed = true };
        var arc = new ArcSegment
        {
            Size = new Size(8, 8),
            IsLargeArc = false,
            SweepDirection = left ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
        };
        LineSegment tail = new();

        if (left)
        {
            figure.StartPoint = new Point(8, 0);
            arc.Point = new Point(0, 8);
            tail.Point = new Point(8, 8);
        }
        else
        {
            figure.StartPoint = new Point(0, 0);
            arc.Point = new Point(8, 8);
            tail.Point = new Point(0, 8);
        }
        figure.Segments.Add(arc);
        figure.Segments.Add(tail);

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        path.Data = geometry;
        return path;
    }

    private void BuildChrome()
    {
        _shell = new Grid { Background = ScrimBrush() };
        AddAutoRow(_shell);
        AddStarRow(_shell);

        BuildHeader();
        Grid.SetRow(_header, 0);
        _shell.Children.Add(_header);

        _body = new Grid();
        Grid.SetRow(_body, 1);
        _shell.Children.Add(_body);

        BuildSurface();

        if (_placement == TabPlacement.Top)
        {
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
        }

        _root.Background = WindowMicaBrush();
        _root.Children.Add(_shell);
        ApplyBackgroundOpacity();

        // Shared IME host: invisible, but it must stay hit-testable to receive text.
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
        };
        _root.Children.Add(_imeSink);

        EnsureInputHandlers();

        // The sink is only ever written by the IME: plain typing arrives as TextChanged and
        // must have been preceded by a key this window saw, while a composition commit lands
        // in Text one dispatcher turn after TextCompositionEnded, and the keystrokes that
        // produced it never reach PreviewKeyDown (the IME consumes them).
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

            FlushIme(_keySeen && !_swallowText);
        };
        _imeSink.TextCompositionStarted += (_, _) =>
        {
            _imeDiscard = false;
            _composing = true;
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

        // Keys are handled at the window root, not on the IME sink: the sink only owns text
        // composition, and any other focus target (a tab chip, the palette, a caption
        // button) would otherwise swallow the shortcuts.
        _root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (OnKey(args)) args.Handled = true;
        }), true);

        // Clicking away ends the composition but the IME finalizes it as the raw pinyin; a
        // terminal discards that text instead of sending it to the shell.
        _root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            if (!_composing) return;
            _imeDiscard = true;
            ShowImeComposition();
        }), true);
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
            Diag.Guard("ws.ime", UpdateImeSink);
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
        _header = new Border
        {
            Height = 44,
            Margin = new Thickness(0),
            Background = ChromeHeaderFillBrush(),
            BorderThickness = new Thickness(0),
        };
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
                Spacing = 2,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
            };

            var strip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = 36,
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
                Height = 36,
                Margin = new Thickness(20, 0, 0, 0),
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

            // Connectors live above the strip (and above the scroller's clip) so they can
            // spill into the 8px gutters on either side of the active chip.
            _arcLayer = new Canvas
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Grid.SetColumn(_arcLayer, 0);

            _tabArcLeft = CornerArc(true);
            _tabArcRight = CornerArc(false);
            _tabArcLeft.Visibility = Visibility.Collapsed;
            _tabArcRight.Visibility = Visibility.Collapsed;
            _arcLayer.Children.Add(_tabArcLeft);
            _arcLayer.Children.Add(_tabArcRight);
            // No Clip here: clipping this overlay makes WinUI fail-fast (0xC000027B) as soon
            // as the tab strip underneath scrolls. The connectors are hidden whenever the
            // active chip leaves the viewport, so nothing can spill over the chrome.
            _arcLayer.SizeChanged += (_, _) => ScheduleTabArcs();
            _headerInner.Children.Add(_arcLayer);

            scroller.ViewChanged += (_, _) => ScheduleTabArcs();
            scroller.SizeChanged += (_, _) => ScheduleTabArcs();

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

            Border search = ToolButton('\uE721', ChromeTextFaintBrush());
            search.Width = 28;
            Hoverable(search, ChromeChipHoverBrush);
            search.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                ShowCommandPalette();
            };
            topTools.Children.Add(search);

            Grid.SetColumn(topTools, 1);
            _headerInner.Children.Add(topTools);

            WireTabDrag();
        }

        if (_placement != TabPlacement.Top)
        {
            _headerSideTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };

            // The panel glyph shows the tab rail on the side it lives on.
            Border sideToggle = ToolButton(PanelIcon(ChromeTextFaintBrush(), 14, _placement != TabPlacement.Right));
            Hoverable(sideToggle, ChromeChipHoverBrush);
            sideToggle.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                SetSidebarCollapsed(false);
            };
            _headerSideTools.Children.Add(sideToggle);

            Border add = ToolButton('\uE710', ChromeTextFaintBrush());
            Hoverable(add, ChromeChipHoverBrush);
            add.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                RequestNewTab();
            };
            _headerSideTools.Children.Add(add);

            Border search = ToolButton('\uE721', ChromeTextFaintBrush());
            Hoverable(search, ChromeChipHoverBrush);
            search.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                ShowCommandPalette();
            };
            _headerSideTools.Children.Add(search);

            Grid.SetColumn(_headerSideTools, 0);
            _headerInner.Children.Add(_headerSideTools);
        }

        var captions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0),
        };

        Border minimize = CaptionButton('\uE921', 46, 44, ChromeTextBodyBrush());
        Border maximize = CaptionButton('\uE922', 46, 44, ChromeTextBodyBrush());
        Border close = CaptionButton('\uE8BB', 46, 44, ChromeTextBodyBrush());
        _maximizeIcon = maximize.Child as FontIcon;
        CaptionHover(minimize, ChromeChipHoverBrush, ChromeTextStrongBrush);
        CaptionHover(maximize, ChromeChipHoverBrush, ChromeTextStrongBrush);
        CaptionHover(close, CloseHoverBrush, () => new SolidColorBrush(Colors.White));

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

    private void BuildSidebar()
    {
        bool leftSide = _placement == TabPlacement.Left;

        // Flat tab rail: it starts at the window's top edge (negative margin lifts it over
        // the header row) and has no card chrome of its own.
        _sidebar = new Border
        {
            Width = 240,
            Margin = leftSide ? new Thickness(0, -44, 8, 8) : new Thickness(8, -44, 0, 8),
            Background = ClearBrush(),
            BorderThickness = new Thickness(0),
            Padding = leftSide ? new Thickness(14, 8, 6, 8) : new Thickness(6, 8, 14, 8),
        };

        var layout = new Grid();
        AddAutoRow(layout);
        AddStarRow(layout);
        _sidebar.Child = layout;

        var tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Height = 34,
            VerticalAlignment = VerticalAlignment.Center,
        };

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
            NewTab();
            Activate(_tabs.Count - 1);
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
        Grid.SetRow(scroller, 1);
        layout.Children.Add(scroller);

        WireTabDrag();
    }

    private void BuildSurface()
    {
        _contentCard = new Border
        {
            Margin = _placement == TabPlacement.Left ? new Thickness(0, 0, 8, 8)
                : _placement == TabPlacement.Right ? new Thickness(8, 0, 0, 8)
                : new Thickness(8, 0, 8, 8),
            CornerRadius = new CornerRadius(12),
            Background = CardFillBrush(),
            BorderBrush = PanelStrokeBrush(),
            BorderThickness = _placement == TabPlacement.Top ? new Thickness(1, 0, 1, 1) : new Thickness(1),
        };

        _surfaceHost = new Grid { Margin = new Thickness(6) };

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
    /// The chip's shape depends on the rail it sits in: the top variant is taller and docks
    /// into the content card with square bottom corners. Moving the rail restyles it.
    /// </summary>
    private void StyleChip(Tab tab)
    {
        bool top = _placement == TabPlacement.Top;
        tab.Chip.Height = top ? 36 : 34;
        tab.Chip.CornerRadius = top ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8);
        tab.Chip.Padding = top ? new Thickness(12, 0, 0, 0) : new Thickness(8, 0, 8, 0);
        tab.Chip.Margin = top ? new Thickness(0, 0, 2, 0) : new Thickness(0);
        tab.Chip.MaxWidth = top ? 220 : double.PositiveInfinity;
        if (tab.ChipClose is not null)
            tab.ChipClose.Margin = top ? new Thickness(0, 0, 8, 0) : new Thickness(0);
    }

    private Tab NewTab()
    {
        Diag.Debug("tab", $"create start #{_tabs.Count + 1}");
        var tab = new Tab { Title = ShellDisplayName(_config.Command) };

        tab.Surface = new Grid { RowSpacing = 6, ColumnSpacing = 6 };

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

        // A thin separator between inactive tabs, as in Windows Terminal. It stays in the
        // layout so showing it never changes the tab width.
        tab.ChipDivider = new Border
        {
            Width = 1,
            Height = 16,
            Background = ChromeStrokeBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.0,
        };
        Grid.SetColumn(tab.ChipDivider, 3);
        chipGrid.Children.Add(tab.ChipDivider);

        tab.Chip.Child = chipGrid;
        StyleChip(tab);
        // A brand-new chip has no measured width yet, so the scroll-into-view runs again
        // once it does.
        tab.Chip.SizeChanged += (_, _) =>
        {
            ScheduleTabArcs();
            if (_ensureActiveVisible && EnsureActiveVisible()) _ensureActiveVisible = false;
        };

        // Hover tooltip: a single wrapped, centered TextBlock whose inlines are the title
        // followed by the italicized switch-to-tab key chord.
        tab.ChipTipText = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        ToolTipService.SetToolTip(tab.Chip, new ToolTip { Content = tab.ChipTipText });

        // A Border cannot take focus, so the chip sits inside a focusable ContentControl;
        // only the active chip joins the tab order (roving index).
        tab.ChipFocus = new ContentControl
        {
            Content = tab.Chip,
            IsTabStop = false,
            UseSystemFocusVisuals = true,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        tab.ChipFocus.KeyDown += (_, args) => OnChipKeyDown(self, args);

        AutomationProperties.SetName(tab.ChipClose, Strings.CloseTab);
        _chipHost.Children.Add(tab.ChipFocus);

        var pane = new TerminalPane(_config);
        WirePane(pane);

        tab.Focused = pane;
        tab.Root = new SplitNode { Pane = pane };

        _tabs.Add(tab);
        Relayout(tab);
        Diag.Debug("tab", "create done");
        return tab;
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
        void Create()
        {
            NewTab();
            Activate(_tabs.Count - 1);
        }

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
        // The active-tab connectors follow the chip while it is lifted.
        ScheduleTabArcs();

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
        if (_tabs.Count > 0 && ReferenceEquals(_tabs[_active], tab)) PlaceArcs(x, y);
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
        ScheduleTabArcs();
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
        if (_stripTransform is null || _tabScroller is null || _tabs.Count == 0) return false;

        double viewport = _tabScroller.ActualWidth;
        if (viewport <= 0.0) return false;

        int index = Math.Clamp(_active, 0, _tabs.Count - 1);
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
            ScheduleTabArcs();
        }
        return true;
    }

    private void ScheduleTabArcs()
    {
        if (_arcLayer is null || _arcUpdateQueued) return;
        if (_dispatcher is null)
        {
            UpdateTabArcs();
            return;
        }
        _arcUpdateQueued = true;
        _dispatcher.TryEnqueue(() =>
        {
            _arcUpdateQueued = false;
            Diag.Guard("ws.arcs", UpdateTabArcs);
        });
    }

    private void UpdateTabArcs()
    {
        if (_arcLayer is null || _tabArcLeft is null || _tabArcRight is null) return;

        void Hide()
        {
            _arcPlaceable = false;
            _tabArcLeft.Visibility = Visibility.Collapsed;
            _tabArcRight.Visibility = Visibility.Collapsed;
        }

        if (_placement != TabPlacement.Top || _tabs.Count == 0 || _tabScroller is null || _chipHost is null)
        {
            Hide();
            return;
        }

        Border chip = _tabs[_active].Chip;
        double width = chip.ActualWidth;
        double height = chip.ActualHeight;
        if (width <= 0.0 || height <= 0.0)
        {
            Hide();
            return;
        }

        // Measure from the strip host (never render-translated) so the chip's own
        // displacement can be added on top.
        List<double> origins = TabSlotOrigins(true);
        if (_active >= origins.Count)
        {
            Hide();
            return;
        }

        Point hostOrigin = _chipHost.TransformToVisual(_arcLayer).TransformPoint(new Point(0, 0));
        Tab activeTab = _tabs[_active];
        double left = hostOrigin.X + origins[_active];
        double right = left + width;
        double bottom = hostOrigin.Y + height;

        Point view = _tabScroller.TransformToVisual(_arcLayer).TransformPoint(new Point(0, 0));
        double viewLeft = view.X;
        double viewRight = view.X + _tabScroller.ActualWidth;

        // Hide while the chip is scrolled out of view; the connectors are not inside the
        // scroller's clip and would otherwise float over the chrome.
        if (left + activeTab.ShiftX < viewLeft - 0.5 || right + activeTab.ShiftX > viewRight + 0.5)
        {
            Hide();
            return;
        }

        _arcLeft = left - 8.0;
        _arcRight = right;
        _arcBottom = bottom - 8.0;
        _arcPlaceable = true;
        PlaceArcs(activeTab.ShiftX, activeTab.ShiftY);
        _tabArcLeft.Visibility = Visibility.Visible;
        _tabArcRight.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Places the active-tab connectors, folding the chip's displacement into the Canvas
    /// coordinates. UIElement.Translation must not be used here: a translated child inside
    /// this clipped overlay fail-fasts XAML (0xC000027B) while the strip scrolls.
    /// </summary>
    private void PlaceArcs(double shiftX, double shiftY)
    {
        if (!_arcPlaceable || _tabArcLeft is null || _tabArcRight is null) return;

        Canvas.SetLeft(_tabArcLeft, _arcLeft + shiftX);
        Canvas.SetTop(_tabArcLeft, _arcBottom + shiftY);
        Canvas.SetLeft(_tabArcRight, _arcRight + shiftX);
        Canvas.SetTop(_tabArcRight, _arcBottom + shiftY);
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
    /// Each split becomes a nested two-cell Grid, so the two halves share the parent cell
    /// exactly and no empty cells are ever laid out.
    /// </summary>
    private FrameworkElement BuildNode(SplitNode node, IReadOnlyList<Grid> previous, List<Grid> grids)
    {
        if (node.Pane is not null) return node.Pane.Visual;

        var grid = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        grids.Add(grid);
        if (node.Vertical)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        }
        else
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        FrameworkElement first = BuildNode(node.First!, previous, grids);
        FrameworkElement second = BuildNode(node.Second!, previous, grids);
        if (node.Vertical)
        {
            Grid.SetRow(first, 0);
            Grid.SetRow(second, 1);
        }
        else
        {
            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 1);
        }
        AddChild(grid, first, previous);
        AddChild(grid, second, previous);
        return grid;
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
        pane.OnMaximize = _ => TogglePaneZoom();
        pane.OnClosePane = _ => CloseFocusedPane();
    }

    private void TogglePaneZoom()
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Root is null || tab.Root.Pane is not null || tab.Focused is null) return;
        tab.Zoomed = !tab.Zoomed;
        Relayout(tab);
        FocusPane(tab, tab.Focused);
    }

    private void Activate(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

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
        // newly focused shell. The IME finalizes it one dispatcher turn later, so mark it now.
        if (_composing && tab.Focused is not null && !ReferenceEquals(tab.Focused, pane))
        {
            _imeDiscard = true;
            ShowImeComposition();
        }

        tab.Focused = pane;
        ForEachPane(tab, candidate => candidate.SetFocused(ReferenceEquals(candidate, pane)));

        // A split pane switch follows whichever pane is focused; also refresh the title
        // while the tab is in the background.
        if (pane.Title.Length > 0 && tab.Title != pane.Title) tab.Title = pane.Title;
        UpdateHeaders();
        if (!ReferenceEquals(tab, _tabs[_active])) return;

        // The palette owns the keyboard while it is open; focusing the sink here would take
        // it away from the search box and leave the palette deaf to typing and arrow keys.
        if (_paletteLayer is null || _paletteLayer.Visibility != Visibility.Visible)
            _imeSink.Focus(FocusState.Programmatic);
        UpdateImeSink();
        ShowImeComposition();
        UpdateStatus();
    }

    private void SplitFocused(bool vertical)
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Focused is null || tab.Root is null) return;

        // The split inherits the focused session: same shell command and the directory the
        // shell is currently sitting in.
        var childConfig = new Config
        {
            Command = _config.Command,
            WorkingDirectory = _config.WorkingDirectory,
            FontFamily = _config.FontFamily,
            FontSize = _config.FontSize,
            UnfocusedSplitOpacity = _config.UnfocusedSplitOpacity,
            HasUnfocusedSplitFill = _config.HasUnfocusedSplitFill,
            UnfocusedSplitFill = _config.UnfocusedSplitFill,
            TabPosition = _config.TabPosition,
            ThemeMode = _config.ThemeMode,
            WindowThemeMode = _config.WindowThemeMode,
        };

        string command = tab.Focused.ShellCommand();
        if (command.Length > 0) childConfig.Command = command;
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
