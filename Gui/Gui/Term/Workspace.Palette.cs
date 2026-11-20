using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Text;

namespace Gui.Term;

public sealed partial class Workspace
{
    // ---------------- palette metrics ----------------

    /// <summary>Height of one command row.</summary>
    private const double RowHeight = 36;

    /// <summary>Gap between two command rows.</summary>
    private const double RowSpacing = 1;

    /// <summary>
    /// Commands the list shows at once. The card keeps the height it has with eight rows and
    /// the commands that do not fit are reached by scrolling.
    /// </summary>
    private const int VisibleRows = 8;

    /// <summary>
    /// Height of the command list when it holds <see cref="VisibleRows"/> commands:
    /// 8 * <see cref="RowHeight"/> + 7 * <see cref="RowSpacing"/> + the 6px stack margin above
    /// and below.
    /// </summary>
    private const double ListMaxHeight = 8 * RowHeight + 7 * RowSpacing + 12;

    /// <summary>
    /// One entry in the virtualized theme picker. The ListView only creates rows for the
    /// entries on screen, so the several hundred theme names never become several hundred
    /// controls.
    /// </summary>
    private sealed class PaletteThemeOption : INotifyPropertyChanged
    {
        private double _checkOpacity;

        public PaletteThemeOption(string name, bool light)
        {
            Name = name;
            Light = light;
        }

        public string Name { get; }

        public bool Light { get; }

        /// <summary>1 while this is the committed theme, 0 otherwise.</summary>
        public double CheckOpacity
        {
            get => _checkOpacity;
            set
            {
                if (Math.Abs(_checkOpacity - value) < 0.001) return;
                _checkOpacity = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CheckOpacity)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Every theme the picker can offer; filled in on first entry.</summary>
    private readonly List<PaletteThemeOption> _paletteThemeOptions = new();

    /// <summary>The entries that survived the current filter, bound to the ListView.</summary>
    private readonly ObservableCollection<PaletteThemeOption> _paletteThemeMatches = new();

    /// <summary>Row count the ListView currently has realized, kept on the row element itself.</summary>
    private sealed class ThemeRowTag
    {
        public int Index;
    }

    private Grid? _paletteThemeHost;
    private ListView? _paletteThemeList;
    private TextBlock? _paletteThemeEmpty;
    private int _paletteThemeSelected = -1;
    private int _paletteThemeHovered = -1;

    // ---- help menu links ----
    private const string ReleaseNotesUrl = "https://github.com/shizengzhou/Gui/releases";
    private const string DocumentationUrl = "https://shizengzhou.github.io/Gui/docs/";
    private const string FeedbackUrl = "https://github.com/shizengzhou/Gui/issues";

    // ---------------- theme switching ----------------

    /// <summary>
    /// Commits a theme to the appearance it belongs to: a light theme replaces the "light:"
    /// part of "theme", a dark theme the "dark:" part, leaving the other side alone. The
    /// palette thus edits the configuration exactly the way the file is written.
    /// </summary>
    private void ApplyThemeChoice(string name, bool light)
    {
        string value = _config.WithThemeSide(light, name);
        Config.SaveValue("theme", value);
        _config.SetTheme(value);

        Theme.Light = _config.LightTheme;
        Theme.WindowLight = _config.WindowLight;
        Theme.Terminal = _config.TerminalTheme;
        _themePreviewActive = false;
        RefreshTheme();
        BroadcastTheme();
    }

    private void ApplyWindowThemeMode(string mode)
    {
        _config.WindowThemeMode = mode;
        if (mode == "light") Theme.WindowLight = true;
        else if (mode == "dark") Theme.WindowLight = false;
        else
        {
            _config.WindowThemeMode = "system";
            Theme.WindowLight = Config.SystemUsesLightTheme();
        }
        _config.WindowLight = Theme.WindowLight;

        // Switching the appearance also selects the other half of "theme".
        _config.SetTheme(_config.ThemeValue);
        Theme.Light = _config.LightTheme;
        Theme.Terminal = _config.TerminalTheme;
        RefreshTheme();
    }

    /// <summary>
    /// Repaints this window after another one committed a theme change: the Theme statics
    /// are already updated, so only the per-window brushes and the palette lists need the
    /// new fields.
    /// </summary>
    internal void ApplyThemeFrom(Config source)
    {
        _config.ThemeMode = source.ThemeMode;
        _config.ThemeLightName = source.ThemeLightName;
        _config.ThemeDarkName = source.ThemeDarkName;
        _config.ThemeValue = source.ThemeValue;
        _config.LightTheme = source.LightTheme;
        _config.WindowLight = source.WindowLight;
        _config.WindowThemeMode = source.WindowThemeMode;
        _config.TerminalTheme = source.TerminalTheme;
        RefreshTheme();
    }

    /// <summary>Theme is process-wide, so a committed change has to reach the other windows.</summary>
    private void BroadcastTheme() => App.ApplyThemeToOtherWindows(this, _config);

    private void RefreshTheme()
    {
        // Shared brushes update in place, which also refreshes the colors captured by
        // hover handlers and other one-time assignments.
        RefreshThemedBrushes();

        _root.RequestedTheme = Theme.Light ? ElementTheme.Light : ElementTheme.Dark;

        // Acrylic is not a themed brush; retint it. Reuse the existing brush: a theme preview
        // refreshes on every selection move, and replacing the brush rebuilt its composition
        // surface each time.
        if (_palettePanel is not null)
        {
            if (_palettePanel.Background is AcrylicBrush acrylic)
            {
                acrylic.TintColor = Theme.PaletteTint();
                acrylic.TintOpacity = Theme.PaletteTintOpacity();
                acrylic.FallbackColor = Theme.PaletteBg();
            }
            else
            {
                _palettePanel.Background = new AcrylicBrush
                {
                    TintColor = Theme.PaletteTint(),
                    TintOpacity = Theme.PaletteTintOpacity(),
                    FallbackColor = Theme.PaletteBg(),
                };
            }
        }

        ApplyWindowTheme();
        ApplyBackgroundOpacity();
        ApplyTabBarVisibility();

        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane =>
            {
                pane.SyncConfig(_config);
                pane.ApplyTheme();
            });

        UpdateHeaders();
        RenderUpdatePill();
        UpdateInputAnchor();
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        if (_placement == TabPlacement.Top) return;

        bool changed = _sidebarCollapsed != collapsed;
        _sidebarCollapsed = collapsed;

        // A chrome rebuild calls this with the state the rail is already in: apply it in one
        // step instead of animating from a width the new elements never had.
        if (_sidebar is null || (!changed && !_sidebarAnimating) || _dispatcher is null)
        {
            StopSidebarAnimation();
            ApplySidebarChrome();
            return;
        }

        _sidebarAnimFrom = Math.Max(0.0, _sidebar.Width);
        _sidebarAnimTo = collapsed ? 0.0 : _sidebarWidth;
        _sidebarAnimStart = Environment.TickCount64;

        if (!_sidebarAnimating)
        {
            _sidebarAnimating = true;
            // The rail slides out of the window instead of disappearing: the clip follows the
            // moving edge so the tabs are cut off rather than spilling over the pane.
            _sidebar.Visibility = Visibility.Visible;
            _sidebar.Clip = SidebarClip(_sidebarAnimFrom);
            if (_sidebarSplitter is not null) _sidebarSplitter.Visibility = Visibility.Collapsed;
            // The header's expand button fades with the slide instead of popping in after the
            // rail lands, so the icon row never jumps when the animation ends.
            if (_headerSideTools is not null)
            {
                _headerSideTools.Visibility = Visibility.Visible;
                _headerSideTools.Opacity = _sidebarCollapseFraction;
            }
            if (_sidebarTimer is null)
            {
                _sidebarTimer = _dispatcher.CreateTimer();
                _sidebarTimer.Interval = TimeSpan.FromMilliseconds(16);
                _sidebarTimer.Tick += (_, _) => StepSidebarAnimation();
            }
        }
        _sidebarTimer?.Start();
    }

    private void StepSidebarAnimation()
    {
        if (!_sidebarAnimating || _sidebar is null)
        {
            _sidebarTimer?.Stop();
            return;
        }

        double progress = Math.Clamp(
            (Environment.TickCount64 - _sidebarAnimStart) / SidebarAnimMs, 0.0, 1.0);
        // Ease-out cubic: quick off the edge, settling into the final width.
        double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
        double width = _sidebarAnimFrom + (_sidebarAnimTo - _sidebarAnimFrom) * eased;

        _sidebar.Width = Math.Max(0.0, width);
        _sidebar.Clip = SidebarClip(width);

        // The pane header's icons keep clear of the floating chrome; feeding them the same
        // fraction makes them travel with the rail instead of jumping when it lands.
        _sidebarCollapseFraction = _sidebarWidth > 0.0
            ? 1.0 - Math.Clamp(width / _sidebarWidth, 0.0, 1.0)
            : 1.0;
        if (_headerSideTools is not null) _headerSideTools.Opacity = _sidebarCollapseFraction;
        foreach (Tab tab in _tabs) ApplyPaneInsets(tab);

        if (progress < 1.0) return;

        _sidebarTimer?.Stop();
        _sidebarAnimating = false;
        _sidebar.Clip = null;
        ApplySidebarChrome();
    }

    private static RectangleGeometry SidebarClip(double width) => new()
    {
        Rect = new Rect(0, 0, Math.Max(0.0, width), 4096),
    };

    private void StopSidebarAnimation()
    {
        _sidebarTimer?.Stop();
        _sidebarAnimating = false;
        _sidebarCollapseFraction = _sidebarCollapsed ? 1.0 : 0.0;
        if (_headerSideTools is not null) _headerSideTools.Opacity = 1.0;
        if (_sidebar is not null) _sidebar.Clip = null;
    }

    /// <summary>
    /// The resting state of the rail: its width, the splitter, the header's expand button and
    /// the pane insets that make room for it.
    /// </summary>
    private void ApplySidebarChrome()
    {
        _sidebarCollapseFraction = _sidebarCollapsed ? 1.0 : 0.0;
        if (_sidebar is not null)
        {
            _sidebar.Width = _sidebarCollapsed ? 0.0 : _sidebarWidth;
            _sidebar.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_sidebarSplitter is not null)
            _sidebarSplitter.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_headerSideTools is not null)
        {
            _headerSideTools.Visibility = _sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
            _headerSideTools.Opacity = 1.0;
        }

        if (_contentCard is not null) _contentCard.Margin = ContentCardMargin();

        // Opening a right-hand rail takes the pane's top-right corner away from the captions,
        // so the close button returns to a square corner (and back again when it collapses).
        UpdateCloseCorner();

        // The rail's buttons moved into (or out of) the strip, so the corner panes have to
        // give them room or take it back.
        foreach (Tab tab in _tabs) ApplyPaneInsets(tab);
    }

    /// <summary>
    /// Shows or hides the tab rail. The top layout has no rail, so the toggle does nothing
    /// there.
    /// </summary>
    private void ToggleSidebar() => SetSidebarCollapsed(!_sidebarCollapsed);

    // ---------------- command palette ----------------

    private static Border PaletteRowBorder()
    {
        return new Border
        {
            Height = RowHeight,
            CornerRadius = new CornerRadius(Theme.RadiusField),
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
    }

    private PaletteItem MakeRow(string text, string shortcut, List<PaletteItem> owner, Action? action)
    {
        // The row doubles as the keybind handler for its action. The chip shows the chord
        // the config leaves active for that action instead of a copy of the built-in one.
        string? actionId = KeybindActionForLabel(text);
        if (actionId is not null && action is not null)
            _keybindActions[actionId] = action;
        if (actionId is not null)
            shortcut = _keybinds.Label(actionId, shortcut);

        Border row = PaletteRowBorder();
        row.BorderBrush = ClearBrush();
        row.Background = ClearBrush();

        var grid = new Grid();
        AddStarColumn(grid);
        AddAutoColumn(grid);

        TextBlock title = Label(text, 12.5, TextBodyBrush());
        grid.Children.Add(title);

        if (shortcut.Length > 0)
        {
            var keyChip = new Border
            {
                CornerRadius = new CornerRadius(Theme.RadiusChip),
                Padding = new Thickness(5, 3, 5, 3),
                Background = ChipHoverBrush(),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            TextBlock key = Label(shortcut, 9.5, TextFaintBrush());
            key.FontFamily = new FontFamily("Cascadia Mono");
            keyChip.Child = key;
            Grid.SetColumn(keyChip, 1);
            grid.Children.Add(keyChip);
        }
        row.Child = grid;

        var item = new PaletteItem { Row = row, Title = title, Label = text, Action = action };
        WirePaletteRow(row, title, owner, item);
        _paletteRows.Children.Add(row);
        owner.Add(item);
        return item;
    }

    private void WirePaletteRow(Border row, TextBlock title, List<PaletteItem> owner, PaletteItem item)
    {
        // The index is resolved from the list rather than captured: the root level is sorted
        // after every row exists, so a captured position would go stale.
        row.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(owner, ActivePaletteItems())) return;
            item.Hovered = true;
            StylePaletteRow(item, selected: owner.IndexOf(item) == _paletteSelected);
        };
        row.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(owner, ActivePaletteItems())) return;
            item.Hovered = false;
            StylePaletteRow(item, selected: owner.IndexOf(item) == _paletteSelected);
        };
        row.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, row)) return;
            if (!ReferenceEquals(owner, ActivePaletteItems())) return;
            int index = owner.IndexOf(item);
            if (index >= 0) RunPaletteItem(owner, index);
        };
    }

    private void AddSubmenuRow(List<PaletteItem> items, string text, Action action, out TextBlock valueOut)
    {
        Border row = PaletteRowBorder();
        row.BorderBrush = ClearBrush();
        row.Background = ClearBrush();

        var grid = new Grid();
        AddStarColumn(grid);
        AddAutoColumn(grid);
        AddAutoColumn(grid);

        TextBlock title = Label(text, 12.5, TextBodyBrush());
        grid.Children.Add(title);

        var valueChip = new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusChip),
            Padding = new Thickness(5, 3, 5, 3),
            Background = ChipHoverBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 180,
        };
        TextBlock value = Label(string.Empty, 9.5, TextFaintBrush());
        valueChip.Child = value;
        Grid.SetColumn(valueChip, 1);
        grid.Children.Add(valueChip);
        valueOut = value;

        FontIcon chevron = Glyph('\uE76C', 10, TextFaintBrush());
        chevron.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);
        row.Child = grid;

        var item = new PaletteItem { Row = row, Title = title, Label = text, Action = action, Submenu = true };
        WirePaletteRow(row, title, items, item);
        _paletteRows.Children.Add(row);
        items.Add(item);
    }

    /// <summary>Drops the rows and the selection a previous chrome build left behind.</summary>
    private void ResetPaletteState()
    {
        _paletteItems.Clear();
        _paletteTabItems.Clear();
        _paletteThemeItems.Clear();
        _paletteThemeOptions.Clear();
        _paletteThemeMatches.Clear();
        _paletteThemeHost = null;
        _paletteThemeList = null;
        _paletteThemeEmpty = null;
        _paletteThemeSelected = -1;
        _paletteThemeHovered = -1;
        _paletteWindowThemeChecks.Clear();

        _paletteSubmenu = false;
        _paletteActiveSub = null;
        _paletteSelected = 0;
        _paletteSelectedList = null;

        _paletteThemeValue = null;
        _paletteChip = null;
        _paletteChipText = null;
        _paletteBackRow = null;
        _paletteEmpty = null;
        _paletteScroller = null;
    }

    /// <summary>
    /// Sorts one palette level the way Ghostty does: title ascending, ASCII case-insensitive,
    /// with ':' compared as '\t' so "Foo:" sorts before "Foo Bar:", and a shorter title first
    /// when it is a prefix of the other. Equal titles keep their declaration order.
    /// </summary>
    private void SortPaletteLevel(List<PaletteItem> items)
    {
        if (items.Count < 2) return;

        var order = new int[items.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (left, right) =>
        {
            int result = ComparePaletteLabels(items[left].Label, items[right].Label);
            return result != 0 ? result : left.CompareTo(right);
        });

        var sorted = new List<PaletteItem>(items.Count);
        foreach (int index in order) sorted.Add(items[index]);

        // Only the rows of this level move; the other levels keep the positions they were
        // created in, which is what building the submenus later relies on.
        int firstRow = _paletteRows.Children.IndexOf(items[0].Row);
        if (firstRow >= 0)
        {
            foreach (PaletteItem item in items) _paletteRows.Children.Remove(item.Row);
            for (int i = 0; i < sorted.Count; i++) _paletteRows.Children.Insert(firstRow + i, sorted[i].Row);
        }

        items.Clear();
        items.AddRange(sorted);
    }

    private static int ComparePaletteLabels(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        for (int i = 0; i < length; i++)
        {
            char a = AsciiLower(left[i] == ':' ? '\t' : left[i]);
            char b = AsciiLower(right[i] == ':' ? '\t' : right[i]);
            if (a != b) return a < b ? -1 : 1;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static char AsciiLower(char value) =>
        value is >= 'A' and <= 'Z' ? (char)(value + ('a' - 'A')) : value;

    private void BuildPalette()
    {
        // The chrome is rebuilt whenever the tab rail moves or the config is reloaded, and a
        // rebuild lands here with the previous palette still recorded. The rows below are
        // brand new elements, so the bookkeeping has to start from scratch too: otherwise the
        // lists hold the old rows as well and the highlight lands on one that is no longer in
        // the tree, leaving the visible list with no selection.
        ResetPaletteState();
        RegisterBuiltInKeybindActions();

        _paletteLayer = new Grid
        {
            Visibility = Visibility.Collapsed,
            // The design's layer is transparent: no dim, no backdrop blur. The transparent
            // brush still hit-tests the empty area so clicking outside closes it.
            Background = ClearBrush(),
            // The layer spans the whole body, so the card is centered on the window rather
            // than on the pane area the tab rail leaves over.
            Padding = new Thickness(16),
        };
        _paletteLayer.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            HideCommandPalette();
        };

        _palettePanel = new Border
        {
            Width = 520,
            MaxWidth = 520,
            MaxHeight = 520,
            CornerRadius = new CornerRadius(Theme.RadiusSurface),
            Background = new AcrylicBrush
            {
                TintColor = Theme.PaletteTint(),
                TintOpacity = Theme.PaletteTintOpacity(),
                FallbackColor = Theme.PaletteBg(),
            },
            BorderBrush = StrokeStrongBrush(),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32),
        };
        _paletteLayer.SizeChanged += (_, _) =>
        {
            double availableWidth = Math.Max(0.0, _paletteLayer.ActualWidth - 32.0);
            _palettePanel.Width = Math.Min(520.0, availableWidth);
            _palettePanel.MaxHeight = Math.Max(0.0, _paletteLayer.ActualHeight - 32.0);
        };
        _palettePanel.PointerPressed += (_, args) => args.Handled = true;

        var layout = new Grid();
        AddAutoRow(layout);
        AddStarRow(layout);
        AddAutoRow(layout);
        _palettePanel.Child = layout;

        var searchBar = new Border
        {
            Height = 48,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = PanelStrokeBrush(),
        };

        var searchGrid = new Grid { Margin = new Thickness(14, 0, 14, 0) };
        AddAutoColumn(searchGrid);
        AddAutoColumn(searchGrid);
        AddStarColumn(searchGrid);

        FontIcon searchIcon = Glyph('\uE721', 14, TextFaintBrush());
        searchIcon.Margin = new Thickness(0, 0, 10, 0);
        searchGrid.Children.Add(searchIcon);

        // Breadcrumb chip: shows the submenu's name while a second level is open.
        _paletteChip = new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusChip),
            Padding = new Thickness(6, 2, 6, 2),
            Background = ChipHoverBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        _paletteChipText = Label(string.Empty, 10.5, TextBodyBrush());
        _paletteChip.Child = _paletteChipText;
        Grid.SetColumn(_paletteChip, 1);
        searchGrid.Children.Add(_paletteChip);

        _paletteRootPlaceholder = Strings.PalettePlaceholder;
        _paletteInput = new TextBox
        {
            Background = ClearBrush(),
            Padding = new Thickness(0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            FocusVisualPrimaryThickness = new Thickness(0),
            FocusVisualSecondaryThickness = new Thickness(0),
            FontSize = Theme.FontTitle,
            Foreground = TextStrongBrush(),
            PlaceholderText = _paletteRootPlaceholder,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The default TextBox template paints a white rounded field and an accent underline
        // on focus; the design keeps the palette bar flat.
        _paletteInput.Resources["TextControlBackground"] = ClearBrush();
        _paletteInput.Resources["TextControlBackgroundFocused"] = ClearBrush();
        _paletteInput.Resources["TextControlBackgroundPointerOver"] = ClearBrush();
        _paletteInput.Resources["TextControlBackgroundPressed"] = ClearBrush();
        _paletteInput.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        _paletteInput.Resources["TextControlBorderBrushFocused"] = ClearBrush();

        _paletteInput.TextChanged += (_, _) => FilterPalette();
        Grid.SetColumn(_paletteInput, 2);
        searchGrid.Children.Add(_paletteInput);

        searchBar.Child = searchGrid;
        Grid.SetRow(searchBar, 0);
        layout.Children.Add(searchBar);

        _paletteRows = new StackPanel { Margin = new Thickness(6), Spacing = RowSpacing };

        // ---- clipboard & terminal ----
        MakeRow(Strings.ResetTerminal, string.Empty, _paletteItems, () => ActivePane()?.ResetTerminal());
        MakeRow(Strings.CopyToClipboard, "Ctrl+Insert", _paletteItems, () => ActivePane()?.CopyToClipboard());
        MakeRow(Strings.CopySelectionPlain, "Ctrl+Shift+C", _paletteItems, () => ActivePane()?.CopySelection());
        MakeRow(Strings.CopySelectionAnsi, string.Empty, _paletteItems, () => ActivePane()?.CopySelectionAnsi());
        MakeRow(Strings.CopySelectionHtml, string.Empty, _paletteItems, () => ActivePane()?.CopySelectionHtml());
        MakeRow(Strings.CopyUrl, string.Empty, _paletteItems, () => ActivePane()?.CopyUrlAtPointer());
        MakeRow(Strings.CopyTitle, string.Empty, _paletteItems, CopyActiveTitle);
        MakeRow(Strings.PasteFromClipboard, "Ctrl+Shift+V", _paletteItems,
            () => ActivePane()?.PasteFromClipboard());
        MakeRow(Strings.PasteFromSelection, "Shift+Insert", _paletteItems, () => ActivePane()?.PasteSelection());

        // ---- search ----
        MakeRow(Strings.StartSearch, "Ctrl+Shift+F", _paletteItems, () => ActivePane()?.ShowSearch());
        MakeRow(Strings.SearchSelection, string.Empty, _paletteItems, () => ActivePane()?.SearchSelection());
        MakeRow(Strings.EndSearch, "Esc", _paletteItems, () => ActivePane()?.HideSearch());
        MakeRow(Strings.NextSearchResult, "Enter", _paletteItems, () => ActivePane()?.SearchNext());
        MakeRow(Strings.PreviousSearchResult, "Shift+Enter", _paletteItems, () => ActivePane()?.SearchPrevious());

        // ---- font size ----
        MakeRow(Strings.IncreaseFontSize, "Ctrl+Plus", _paletteItems, () => ActivePane()?.AdjustFontSize(1.0f));
        MakeRow(Strings.DecreaseFontSize, "Ctrl+-", _paletteItems, () => ActivePane()?.AdjustFontSize(-1.0f));
        MakeRow(Strings.ResetFontSize, "Ctrl+0", _paletteItems, () => ActivePane()?.ResetFontSize());

        // ---- screen & scroll ----
        MakeRow(Strings.ClearScreen, string.Empty, _paletteItems, () => ActivePane()?.ClearScreen());
        MakeRow(Strings.SelectAll, "Ctrl+Shift+A", _paletteItems, () => ActivePane()?.SelectAll());
        MakeRow(Strings.ScrollToTop, "Shift+Home", _paletteItems, () => ActivePane()?.ScrollToTop());
        MakeRow(Strings.ScrollToBottom, "Shift+End", _paletteItems, () => ActivePane()?.ScrollToBottom());
        MakeRow(Strings.ScrollToSelection, string.Empty, _paletteItems, () => ActivePane()?.ScrollToSelection());
        MakeRow(Strings.ScrollPageUp, "Shift+PageUp", _paletteItems, () => ActivePane()?.ScrollPage(true));
        MakeRow(Strings.ScrollPageDown, "Shift+PageDown", _paletteItems, () => ActivePane()?.ScrollPage(false));

        // ---- export to temporary files (screen / selection x plain / HTML / VT x copy / paste / open) ----
        foreach (string emit in new[] { string.Empty, "html", "vt" })
        {
            foreach (string action in new[] { "copy", "paste", "open" })
            {
                string emitValue = emit;
                string actionValue = action;
                MakeRow(Strings.WriteScreenFile(actionValue, emitValue), string.Empty, _paletteItems,
                    () => WriteTerminalFile(false, emitValue, actionValue));
            }
        }
        foreach (string emit in new[] { string.Empty, "html", "vt" })
        {
            foreach (string action in new[] { "copy", "paste", "open" })
            {
                string emitValue = emit;
                string actionValue = action;
                MakeRow(Strings.WriteSelectionFile(actionValue, emitValue), string.Empty, _paletteItems,
                    () => WriteTerminalFile(true, emitValue, actionValue));
            }
        }

        // ---- window & tabs ----
        MakeRow(Strings.NewWindow, "Ctrl+Shift+N", _paletteItems, OpenNewWindow);
        MakeRow(Strings.ToggleQuickTerminal, _keybinds.Label("toggle_quick_terminal", string.Empty),
            _paletteItems, QuickTerminal.Toggle);
        MakeRow(Strings.NewTab, "Ctrl+Shift+T", _paletteItems, () => Activate(IndexOf(NewTab())));
        MakeRow(Strings.HelpCheckForUpdates, string.Empty, _paletteItems,
            () => _ = RunUpdateCheckAsync(userInitiated: true));
        MakeRow(Strings.Settings, "Ctrl+,", _paletteItems, ShowSettings);
        MakeRow(Strings.MoveTabLeft, "Ctrl+Shift+PageUp", _paletteItems, () => MoveTab(_active, _active - 1));
        MakeRow(Strings.MoveTabRight, "Ctrl+Shift+PageDown", _paletteItems, () => MoveTab(_active, _active + 1));
        MakeRow(Strings.MoveTabToNewWindow, string.Empty, _paletteItems, () =>
        {
            // Moving the only tab would just recreate the window; the tab menu disables it.
            if (_tabs.Count > 1) MoveTabToNewWindow(_tabs[_active]);
        });
        MakeRow(Strings.NextTab, "Ctrl+Tab", _paletteItems, () => CycleTab(1));
        MakeRow(Strings.PreviousTab, "Ctrl+Shift+Tab", _paletteItems, () => CycleTab(-1));
        MakeRow(Strings.LastTab, "Alt+9", _paletteItems, ActivateLastTab);
        MakeRow(Strings.ToggleSidebar, "Ctrl+Shift+B", _paletteItems, ToggleSidebar);

        // ---- titles ----
        MakeRow(Strings.ChangeTerminalTitle, string.Empty, _paletteItems, PromptSurfaceTitle);
        MakeRow(Strings.ChangeTabTitle, string.Empty, _paletteItems, () =>
        {
            if (_tabs.Count == 0) return;
            // The palette restores keyboard focus as it closes; open the editor one turn
            // later or it would lose focus (and commit) immediately, like the tab menu does.
            Tab tab = _tabs[_active];
            _dispatcher?.TryEnqueue(() => BeginRename(tab));
        });
        MakeRow(Strings.ChangeWindowTitle, string.Empty, _paletteItems, PromptWindowTitle);

        // ---- splits ----
        MakeRow(Strings.SplitRight, "Ctrl+Shift+O", _paletteItems, () => SplitFocused(SplitDirection.Right));
        MakeRow(Strings.SplitDown, "Ctrl+Shift+E", _paletteItems, () => SplitFocused(SplitDirection.Down));
        MakeRow(Strings.SplitLeft, string.Empty, _paletteItems, () => SplitFocused(SplitDirection.Left));
        MakeRow(Strings.SplitUp, string.Empty, _paletteItems, () => SplitFocused(SplitDirection.Up));
        MakeRow(Strings.FocusSplitPrevious, "Ctrl+Win+[", _paletteItems, () => CyclePane(-1));
        MakeRow(Strings.FocusSplitNext, "Ctrl+Win+]", _paletteItems, () => CyclePane(1));
        MakeRow(Strings.FocusSplitLeft, "Ctrl+Alt+\u2190", _paletteItems, () => FocusPaneTowards(-1, 0));
        MakeRow(Strings.FocusSplitRight, "Ctrl+Alt+\u2192", _paletteItems, () => FocusPaneTowards(1, 0));
        MakeRow(Strings.FocusSplitUp, "Ctrl+Alt+\u2191", _paletteItems, () => FocusPaneTowards(0, -1));
        MakeRow(Strings.FocusSplitDown, "Ctrl+Alt+\u2193", _paletteItems, () => FocusPaneTowards(0, 1));
        MakeRow(Strings.ToggleZoom, "Ctrl+Shift+Enter", _paletteItems, TogglePaneZoom);
        MakeRow(Strings.ToggleReadOnly, string.Empty, _paletteItems, ToggleReadOnlyMode);
        MakeRow(Strings.EqualizeSplits, string.Empty, _paletteItems, EqualizeSplits);
        MakeRow(Strings.ResizeSplitLeft, "Win+Ctrl+Shift+\u2190", _paletteItems, () => ResizeSplit(-1, 0));
        MakeRow(Strings.ResizeSplitRight, "Win+Ctrl+Shift+\u2192", _paletteItems, () => ResizeSplit(1, 0));
        MakeRow(Strings.ResizeSplitUp, "Win+Ctrl+Shift+\u2191", _paletteItems, () => ResizeSplit(0, -1));
        MakeRow(Strings.ResizeSplitDown, "Win+Ctrl+Shift+\u2193", _paletteItems, () => ResizeSplit(0, 1));

        // ---- debug & config ----
        MakeRow(Strings.ResetWindowSize, string.Empty, _paletteItems, ResetWindowSize);
        MakeRow(Strings.ShowOnScreenKeyboard, string.Empty, _paletteItems, ShowOnScreenKeyboard);
        MakeRow(Strings.OpenConfigOs, string.Empty, _paletteItems, OpenConfig);
        MakeRow(Strings.OpenConfigNewWindow, string.Empty, _paletteItems, OpenConfigInNewWindow);
        MakeRow(Strings.ReloadConfig, "Ctrl+Shift+,", _paletteItems, ReloadConfig);

        // ---- close ----
        MakeRow(Strings.CloseTerminal, "Ctrl+Shift+W", _paletteItems, CloseFocusedPane);
        MakeRow(Strings.CloseTab, string.Empty, _paletteItems, () =>
        {
            if (_tabs.Count > 0) CloseTab(_active);
        });
        MakeRow(Strings.CloseOtherTabs, string.Empty, _paletteItems, () =>
        {
            if (_tabs.Count > 0) CloseOtherTabs(_tabs[_active]);
        });
        MakeRow(Strings.CloseTabsToTheRight, string.Empty, _paletteItems, () =>
        {
            if (_tabs.Count > 0) CloseTabsToTheRight(_tabs[_active]);
        });
        MakeRow(Strings.CloseWindowCommand, "Alt+F4", _paletteItems, CloseWindow);
        MakeRow(Strings.CloseAllWindows, string.Empty, _paletteItems, QuitApplication);

        // ---- window state ----
        MakeRow(Strings.ToggleMaximize, string.Empty, _paletteItems, ToggleMaximize);
        MakeRow(Strings.ToggleFullscreen, "Ctrl+Enter", _paletteItems, ToggleFullscreen);
        MakeRow(Strings.ToggleFloatOnTop, string.Empty, _paletteItems, ToggleFloatOnTop);
        MakeRow(Strings.ToggleMouseReporting, string.Empty, _paletteItems, ToggleMouseReportingMode);
        MakeRow(Strings.ToggleBackgroundOpacity, string.Empty, _paletteItems, ToggleBackgroundOpacityMode);

        // ---- other ----
        MakeRow(Strings.Quit, "Ctrl+Shift+Q", _paletteItems, QuitApplication);

        // The debug tap mirrors the raw VT byte stream into the log; the row shows its state.
        PaletteItem? tapRow = null;
        tapRow = MakeRow(TapLabel(), string.Empty, _paletteItems, () =>
        {
            Tap.Enabled = !Tap.Enabled;
            if (tapRow is not null) tapRow.Title.Text = TapLabel();
        });

        MakeRow(Strings.OpenDiagnosticsLog, string.Empty, _paletteItems, OpenDiagnosticsLog);
        MakeRow(Strings.CopyDiagnosticsLog, string.Empty, _paletteItems, CopyDiagnosticsLog);
        MakeRow(Strings.EnableCrashDumps, string.Empty, _paletteItems, EnableCrashDumps);

        // Window theme is three flat commands: like every other command, they only take
        // effect on Enter, with no live preview while the selection moves over them.
        (string Label, string Value)[] windowOptions =
        {
            (Strings.FollowSystem, "system"), (Strings.Light, "light"), (Strings.Dark, "dark"),
        };
        foreach ((string label, string value) in windowOptions)
        {
            string optionValue = value;
            PaletteItem item = MakeRow(Strings.WindowThemeOption(label), string.Empty, _paletteItems, () =>
            {
                ApplyWindowThemeMode(optionValue);
                Config.SaveValue("window-theme", optionValue);
                _themePreviewActive = false;
                BroadcastTheme();
            });

            // The row that matches "window-theme" carries a check at its end, like the
            // active entry in a picker.
            FontIcon check = Glyph('\uE73E', 12, AccentBrush());
            check.Visibility = Visibility.Collapsed;
            check.HorizontalAlignment = HorizontalAlignment.Right;
            check.VerticalAlignment = VerticalAlignment.Center;
            check.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(check, 1);
            if (item.Row.Child is Grid grid) grid.Children.Add(check);
            _paletteWindowThemeChecks.Add((optionValue, check));
        }

        // Theme is a second level drawn by a virtualized ListView (see BuildThemeList); the
        // parent row opens it.
        AddSubmenuRow(_paletteItems, Strings.ChooseTheme,
            () => EnterPaletteSubmenu(Strings.Theme, Strings.ChooseTheme, _paletteThemeItems), out TextBlock themeValue);
        _paletteThemeValue = themeValue;

        // Three flat commands for the tab rail.
        MakeRow(Strings.TabBarTop, string.Empty, _paletteItems, () => SetTabPlacement("top"));
        MakeRow(Strings.TabBarLeft, string.Empty, _paletteItems, () => SetTabPlacement("left"));
        MakeRow(Strings.TabBarRight, string.Empty, _paletteItems, () => SetTabPlacement("right"));

        // The design's compact mode: panes stop being islands and fill the content area.
        _densityRow = MakeRow(DensityLabel(), string.Empty, _paletteItems,
            () => SetDensity(!CompactDensity));

        // Ghostty sorts the root command list alphabetically.
        SortPaletteLevel(_paletteItems);

        // Every row has registered its handler by now; aliases need the final table.
        LinkKeybindAliases();

        _paletteEmpty = Label(Strings.NoMatchingCommands, 12, TextFaintBrush());
        _paletteEmpty.Margin = new Thickness(12, 22, 12, 22);
        _paletteEmpty.TextWrapping = TextWrapping.Wrap;
        _paletteEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _paletteEmpty.Visibility = Visibility.Collapsed;
        _paletteRows.Children.Add(_paletteEmpty);

        var listScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            // The card keeps the height it had before the diagnostics commands were added;
            // the commands that do not fit are reached by scrolling.
            MaxHeight = ListMaxHeight,
            Content = _paletteRows,
        };
        _paletteScroller = listScroll;
        Grid.SetRow(listScroll, 1);
        layout.Children.Add(listScroll);

        Grid themeHost = BuildThemeList();
        Grid.SetRow(themeHost, 1);
        layout.Children.Add(themeHost);

        var footer = new Border
        {
            Height = 30,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = PanelStrokeBrush(),
        };

        var hints = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        hints.Children.Add(Label(Strings.HintSelect, 10.5, TextFaintBrush()));
        hints.Children.Add(Label(Strings.HintRun, 10.5, TextFaintBrush()));
        hints.Children.Add(Label(Strings.HintClose, 10.5, TextFaintBrush()));

        // "Backspace back" follows "Esc close" on the same hint line. It is not part of the
        // list and is only visible while a second level is open.
        _paletteBackRow = Label(Strings.HintBack, 10.5, TextFaintBrush());
        _paletteBackRow.Visibility = Visibility.Collapsed;
        _paletteBackRow.PointerEntered += (_, _) => _paletteBackRow.Foreground = TextBodyBrush();
        _paletteBackRow.PointerExited += (_, _) => _paletteBackRow.Foreground = TextFaintBrush();
        _paletteBackRow.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, _paletteBackRow)) return;
            LeavePaletteSubmenu();
        };
        hints.Children.Add(_paletteBackRow);

        footer.Child = hints;
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);

        _paletteLayer.Children.Add(_palettePanel);

        // The layer belongs to the body, not to the content island: the island starts to the
        // right of the tab rail, so a card centered inside it lands off-center in the window.
        // Spanning both body columns keeps the centering on the window and still leaves the
        // header (the drag strip and the caption buttons) reachable.
        Grid.SetColumn(_paletteLayer, 0);
        Grid.SetColumnSpan(_paletteLayer, Math.Max(1, _body.ColumnDefinitions.Count));
        _body.Children.Add(_paletteLayer);
    }

    /// <summary>
    /// The theme level of the palette. It is a ListView over <see cref="_paletteThemeMatches"/>,
    /// so only the visible rows exist: a few hundred themes cost no more to scroll or filter
    /// than the short command list. The StackPanel level stays for the commands, which are
    /// few enough that hand-built rows keep the exact styling.
    /// </summary>
    private Grid BuildThemeList()
    {
        _paletteThemeMatches.Clear();

        _paletteThemeList = new ListView
        {
            ItemsSource = _paletteThemeMatches,
            ItemTemplate = ThemeRowTemplate(),
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Background = ClearBrush(),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(6, 6, 6, 6),
            VerticalAlignment = VerticalAlignment.Top,
            MaxHeight = ListMaxHeight,
        };
        // The item template draws the highlight itself, exactly like a command row; the
        // stock ListViewItem chrome (state fills, focus ring, indicator) is switched off so
        // nothing can differ from the hand-built rows.
        Brush clear = ClearBrush();
        foreach (string key in new[]
                 {
                     "ListViewItemBackground", "ListViewItemBackgroundPointerOver",
                     "ListViewItemBackgroundPressed", "ListViewItemBackgroundSelected",
                     "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed",
                     "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush",
                     "ListViewItemSelectionIndicatorPressedBrush",
                 })
        {
            _paletteThemeList.Resources[key] = clear;
        }
        _paletteThemeList.Resources["ListViewItemMinHeight"] = RowHeight;
        _paletteThemeList.Resources["ListViewItemPadding"] = new Thickness(0);

        _paletteThemeList.SelectionChanged += (_, _) => OnThemeSelectionChanged();
        _paletteThemeList.ItemClick += (_, args) =>
        {
            if (args.ClickedItem is not PaletteThemeOption option) return;
            _paletteThemeList.SelectedItem = option;
            CommitPaletteTheme();
        };
        _paletteThemeList.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue) return;
            // Recycled containers keep whatever the stock style measured them with; take the
            // same row metrics as the hand-built command rows.
            args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            args.ItemContainer.Padding = new Thickness(0);
            args.ItemContainer.Margin = new Thickness(0, 0, 0, RowSpacing);
            args.ItemContainer.UseSystemFocusVisuals = false;
            args.ItemContainer.FocusVisualPrimaryThickness = new Thickness(0);
            args.ItemContainer.FocusVisualSecondaryThickness = new Thickness(0);
            if (args.ItemContainer.ContentTemplateRoot is not Border row) return;

            // The row element is recycled with its container: hook the pointer once and let
            // the tag carry the index it currently shows.
            if (row.Tag is not ThemeRowTag tag)
            {
                tag = new ThemeRowTag();
                row.Tag = tag;
                row.PointerEntered += (_, _) =>
                {
                    if (row.Tag is ThemeRowTag current) SetThemeRowHover(current.Index, true);
                };
                row.PointerExited += (_, _) =>
                {
                    if (row.Tag is ThemeRowTag current) SetThemeRowHover(current.Index, false);
                };
            }
            tag.Index = args.ItemIndex;
            ApplyThemeRowStyle(row, args.ItemIndex);
        };

        _paletteThemeEmpty = Label(Strings.NoMatchingCommands, 12, TextFaintBrush());
        _paletteThemeEmpty.Margin = new Thickness(12, 22, 12, 22);
        _paletteThemeEmpty.TextWrapping = TextWrapping.Wrap;
        _paletteThemeEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _paletteThemeEmpty.Visibility = Visibility.Collapsed;

        var host = new Grid
        {
            Visibility = Visibility.Collapsed,
            MaxHeight = ListMaxHeight,
            VerticalAlignment = VerticalAlignment.Top,
        };
        host.Children.Add(_paletteThemeList);
        host.Children.Add(_paletteThemeEmpty);
        _paletteThemeHost = host;
        return host;
    }

    /// <summary>The one row shape the virtualized theme list recycles.</summary>
    private static DataTemplate ThemeRowTemplate()
    {
        const string markup =
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            "<Border Padding=\"10,0\">" +
            "<Grid ColumnSpacing=\"8\">" +
            "<Grid.ColumnDefinitions>" +
            "<ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/>" +
            "</Grid.ColumnDefinitions>" +
            "<TextBlock Text=\"{Binding Name}\" FontSize=\"12.5\" VerticalAlignment=\"Center\"/>" +
            "<FontIcon Grid.Column=\"1\" Glyph=\"&#xE73E;\" FontSize=\"12\" VerticalAlignment=\"Center\"" +
            " Opacity=\"{Binding CheckOpacity}\"/>" +
            "</Grid></Border></DataTemplate>";
        return (DataTemplate)XamlReader.Load(markup);
    }

    /// <summary>Styles one realized theme row; the highlight lives on the template's border.</summary>
    private void ApplyThemeRowStyle(Border row, int index)
    {
        bool selected = index == _paletteThemeSelected;
        bool hovered = index == _paletteThemeHovered;
        row.Height = RowHeight;
        row.CornerRadius = new CornerRadius(Theme.RadiusField);
        // Same fill rules as StylePaletteRow so the two levels read as one list.
        row.Background = selected
            ? (hovered ? AccentStrokeBrush() : AccentSoftBrush())
            : (hovered ? ChipHoverBrush() : ClearBrush());
        if (FirstDescendant<TextBlock>(row) is { } label)
            label.Foreground = selected || hovered ? TextStrongBrush() : TextBodyBrush();
        if (FirstDescendant<FontIcon>(row) is { } check) check.Foreground = AccentBrush();
    }

    private void SetThemeRowHover(int index, bool hovered)
    {
        if (hovered) _paletteThemeHovered = index;
        else if (_paletteThemeHovered == index) _paletteThemeHovered = -1;
        StyleThemeRowAt(index);
    }

    private void StyleThemeRowAt(int index)
    {
        if (index < 0) return;
        if (_paletteThemeList?.ContainerFromIndex(index) is not ListViewItem container) return;
        if (container.ContentTemplateRoot is Border row) ApplyThemeRowStyle(row, index);
    }

    /// <summary>
    /// Handles the keys the palette owns. It runs from the window-level preview handler, not
    /// from the search box, because the search box is not the only element that can hold
    /// focus: clicking a tab chip or letting the IME sink take it used to leave the palette
    /// visible but deaf to the arrow keys.
    /// </summary>
    private bool OnPaletteKey(KeyRoutedEventArgs args)
    {
        // Typing and the IME belong to the search box; take the focus back before the key is
        // routed to whatever stole it.
        if (!IsPaletteInputFocused()) _paletteInput.Focus(FocusState.Programmatic);

        switch (args.Key)
        {
            case VirtualKey.Escape:
                HideCommandPalette();
                return true;
            case VirtualKey.Enter:
                RunPalette();
                return true;
            case VirtualKey.Back when _paletteSubmenu && _paletteInput.Text.Length == 0:
                // Backspace returns to the command list once the filter is cleared.
                LeavePaletteSubmenu();
                return true;
            case VirtualKey.Up:
                MovePaletteSelection(false);
                return true;
            case VirtualKey.Down:
                MovePaletteSelection(true);
                return true;
            default:
                return false;
        }
    }

    private bool IsPaletteInputFocused() =>
        _root.XamlRoot is not null &&
        ReferenceEquals(FocusManager.GetFocusedElement(_root.XamlRoot), _paletteInput);

    /// <summary>Steps the highlight to the next or previous row that survived the filter.</summary>
    private void MovePaletteSelection(bool down)
    {
        if (IsPaletteThemeLevel)
        {
            StepThemeSelection(down);
            return;
        }

        List<PaletteItem> items = ActivePaletteItems();
        if (items.Count == 0) return;

        var visible = new bool[items.Count];
        for (int index = 0; index < items.Count; index++)
            visible[index] = items[index].Row.Visibility == Visibility.Visible;

        int next = PaletteSelection.Step(visible, _paletteSelected, down);
        if (next != _paletteSelected) SelectPalette(next);
    }

    /// <summary>
    /// Rebuilds the "switch to tab" rows from the tabs that are open when the palette opens.
    /// Titles and the tab list both change between opens, so a stale row would switch to the
    /// wrong tab; the rows are dropped and recreated instead of refreshed.
    /// </summary>
    private void RebuildPaletteTabRows()
    {
        foreach (PaletteItem item in _paletteTabItems)
        {
            _paletteRows.Children.Remove(item.Row);
            _paletteItems.Remove(item);
        }
        _paletteTabItems.Clear();
        if (_tabs.Count == 0) return;

        for (int index = 0; index < _tabs.Count; index++)
        {
            Tab target = _tabs[index];
            string shortcut = index < 8 ? _keybinds.Label($"goto_tab:{index + 1}", string.Empty) : string.Empty;
            // Appended after the sorted commands instead of being sorted in with them, so the
            // tab rows always sit at the end of the list.
            PaletteItem item = MakeRow(Strings.TabName(target.Title), shortcut, _paletteItems, () =>
            {
                int live = IndexOf(target);
                if (live >= 0) Activate(live);
            });
            _paletteTabItems.Add(item);
        }
    }

    private void ShowCommandPalette()
    {
        if (_palettePanel is null) return;

        // Always open on the command list, not on the level used last time.
        _paletteSubmenu = false;
        _paletteActiveSub = null;
        if (_paletteChip is not null) _paletteChip.Visibility = Visibility.Collapsed;
        if (_paletteBackRow is not null) _paletteBackRow.Visibility = Visibility.Collapsed;
        _paletteInput.PlaceholderText = _paletteRootPlaceholder;
        _paletteInput.Text = string.Empty;
        RefreshPaletteValues();
        RebuildPaletteTabRows();
        FilterPalette();

        // The opening press keeps bubbling to the dismiss handler; mark it so that press is
        // not mistaken for a click outside. A keyboard open clears the mark on the next
        // dispatcher turn, when no press can still be in flight.
        _paletteOpenedByPress = true;
        _dispatcher?.TryEnqueue(() => _paletteOpenedByPress = false);

        _paletteLayer.Opacity = 0.0;
        _palettePanel.Opacity = 0.0;
        _palettePanel.Translation = new System.Numerics.Vector3(0, -6, 32);
        _paletteLayer.Visibility = Visibility.Visible;

        // The IME sink sits above the whole shell at the cursor cell. The card can be drawn
        // over that cell, and the sink would then swallow presses meant for the card, so it
        // drops behind the shell while the palette is open.
        Canvas.SetZIndex(_imeSink, -1);

        _dispatcher?.TryEnqueue(() =>
        {
            _paletteLayer.Opacity = 1.0;
            _palettePanel.Opacity = 1.0;
            _palettePanel.Translation = new System.Numerics.Vector3(0, 0, 32);
            _paletteInput.Focus(FocusState.Programmatic);
        });
    }

    private void HideCommandPalette()
    {
        // A selection preview is never committed by closing the palette.
        RestoreCommittedTheme();
        _paletteLayer.Visibility = Visibility.Collapsed;
        Canvas.SetZIndex(_imeSink, 0);
        if (_tabs.Count > 0) FocusKeyboardAnchor();
    }

    private List<PaletteItem> ActivePaletteItems() =>
        _paletteSubmenu && _paletteActiveSub is not null ? _paletteActiveSub : _paletteItems;

    private void RefreshPaletteValues()
    {
        if (_paletteThemeValue is not null)
        {
            // The value is the theme selected for the current appearance.
            _paletteThemeValue.Text = _config.ThemeMode;
        }

        // Mark the window theme in use: "system" covers the resolved light/dark cases.
        string windowMode = _config.WindowThemeMode is "light" or "dark" ? _config.WindowThemeMode : "system";
        foreach ((string value, FontIcon check) in _paletteWindowThemeChecks)
            check.Visibility = value == windowMode ? Visibility.Visible : Visibility.Collapsed;

        // Only the theme used by the current appearance is marked; the other half stays in
        // the config but is not offered by the list.
        foreach (PaletteThemeOption option in _paletteThemeOptions)
            option.CheckOpacity = ThemeNameEquals(_config.ThemeMode, option.Name) ? 1 : 0;
    }

    /// <summary>Compares the legacy "light"/"dark" spellings with their full names.</summary>
    private static bool ThemeNameEquals(string configured, string name)
    {
        static string Canonical(string value)
        {
            string key = value.Trim();
            if (key.Equals("light", StringComparison.OrdinalIgnoreCase)) return Config.LightThemeName;
            if (key.Equals("dark", StringComparison.OrdinalIgnoreCase)) return Config.DarkThemeName;
            return key;
        }
        return string.Equals(Canonical(configured), Canonical(name), StringComparison.OrdinalIgnoreCase);
    }

    private void PreviewThemeOption(string value)
    {
        bool light;
        string key = value.Trim().ToLowerInvariant();
        bool design = key is "light" or "dark" or "gui light" or "gui dark";

        if (key is "dark" or "gui dark")
        {
            light = false;
        }
        else if (key is "light" or "gui light")
        {
            light = true;
        }
        else if (TerminalThemes.Load(value, out TerminalTheme loaded))
        {
            light = TerminalThemes.IsLight(loaded.Background);
            Theme.Terminal = loaded;
        }
        else
        {
            return;
        }

        if (design) Theme.Terminal = TerminalTheme.CreateDesign(light);
        Theme.Light = light;

        RefreshTheme();
        _themePreviewActive = true;
    }

    private void RestoreCommittedTheme()
    {
        if (!_themePreviewActive) return;
        Theme.Light = _config.LightTheme;
        Theme.WindowLight = _config.WindowLight;
        Theme.Terminal = _config.TerminalTheme;
        RefreshTheme();
        _themePreviewActive = false;
    }

    private void ScrollPaletteSelectionIntoView()
    {
        // The theme level is a ListView and scrolls its selection by itself.
        if (_paletteScroller is null || IsPaletteThemeLevel) return;
        List<PaletteItem> items = ActivePaletteItems();
        if (_paletteSelected < 0 || _paletteSelected >= items.Count) return;

        Border row = items[_paletteSelected].Row;
        if (row.Visibility != Visibility.Visible) return;

        // Filtering hides rows and scrolls in the same turn, so the panel can still be laid out
        // from before the filter: the measured row offsets would be stale and the selection
        // would end up half hidden under the search bar. Settle the layout first.
        _paletteRows.UpdateLayout();

        Point origin = row.TransformToVisual(_paletteRows).TransformPoint(new Point(0, 0));
        double height = row.ActualHeight;
        if (height <= 0.0) height = 36.0;
        double offset = _paletteScroller.VerticalOffset;
        double viewport = _paletteScroller.ActualHeight;

        double target;
        if (origin.Y < offset) target = origin.Y;
        else if (origin.Y + height > offset + viewport) target = origin.Y + height - viewport;
        else return;

        // Snap the viewport to a row boundary so scrolling moves whole rows.
        double pitch = height + _paletteRows.Spacing;
        if (pitch > 0.0) target = Math.Ceiling(target / pitch - 1e-6) * pitch;
        if (target < 0.0) target = 0.0;

        _paletteScroller.ChangeView(null, target, null, true);
    }

    /// <summary>
    /// Puts the shared list back on its first row. Both stack levels live in one ScrollViewer,
    /// so without this a submenu opens at the offset the level above was left at.
    /// </summary>
    private void ResetPaletteScroll() => _paletteScroller?.ChangeView(null, 0.0, null, true);

    private void EnterPaletteSubmenu(string title, string placeholder, List<PaletteItem> items)
    {
        _paletteSubmenu = true;
        _paletteActiveSub = items;

        if (ReferenceEquals(items, _paletteThemeItems))
        {
            // The theme level is virtualized: its source is built once and filtered by
            // swapping the bound collection, so there are no rows to add here.
            EnsurePaletteThemeOptions();
            RefreshPaletteValues();
        }

        if (_paletteChipText is not null) _paletteChipText.Text = title;
        if (_paletteChip is not null) _paletteChip.Visibility = Visibility.Visible;
        if (_paletteBackRow is not null) _paletteBackRow.Visibility = Visibility.Visible;
        _paletteInput.PlaceholderText = placeholder;

        // Clearing the text re-filters; suppress the preview across that pass so opening the
        // level never repaints the window before the user moves.
        _suppressPalettePreview = true;
        _paletteInput.Text = string.Empty;
        FilterPalette();
        _suppressPalettePreview = false;

        // Clear the command level's highlight even when the theme ListView owns the
        // selection now; otherwise its old row stays filled after coming back.
        SelectPalette(items.Count);
        if (IsPaletteThemeLevel) SelectFirstTheme();

        // No row is selected yet, so the queued scroll-into-view above would skip; the list
        // still carries the offset of the level we came from.
        ResetPaletteScroll();
    }

    private void LeavePaletteSubmenu()
    {
        RestoreCommittedTheme();
        _paletteSubmenu = false;
        _paletteActiveSub = null;
        if (_paletteChip is not null) _paletteChip.Visibility = Visibility.Collapsed;
        if (_paletteBackRow is not null) _paletteBackRow.Visibility = Visibility.Collapsed;
        _paletteInput.PlaceholderText = _paletteRootPlaceholder;
        _paletteInput.Text = string.Empty;
        FilterPalette();
        ResetPaletteScroll();
    }

    private bool IsPaletteThemeLevel =>
        _paletteSubmenu && ReferenceEquals(_paletteActiveSub, _paletteThemeItems);

    private void FilterPalette()
    {
        string query = _paletteInput.Text;

        // Rows from the other levels never participate in the current filter.
        foreach (PaletteItem item in _paletteItems) item.Row.Visibility = Visibility.Collapsed;

        if (IsPaletteThemeLevel)
        {
            // The theme level owns its own scroller: a virtualized ListView over the matches.
            if (_paletteScroller is not null) _paletteScroller.Visibility = Visibility.Collapsed;
            if (_paletteThemeHost is not null) _paletteThemeHost.Visibility = Visibility.Visible;
            if (_paletteEmpty is not null) _paletteEmpty.Visibility = Visibility.Collapsed;
            FilterPaletteThemes(query);
            return;
        }

        if (_paletteThemeHost is not null) _paletteThemeHost.Visibility = Visibility.Collapsed;
        if (_paletteScroller is not null) _paletteScroller.Visibility = Visibility.Visible;

        List<PaletteItem> items = ActivePaletteItems();
        int first = items.Count;
        int matches = 0;
        for (int index = 0; index < items.Count; index++)
        {
            PaletteItem item = items[index];
            bool visible = query.Length == 0 ||
                item.Label.Contains(query, StringComparison.OrdinalIgnoreCase);
            item.Row.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                matches++;
                if (first == items.Count) first = index;
            }
        }

        if (_paletteEmpty is not null)
            _paletteEmpty.Visibility = matches == 0 && query.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        SelectPalette(first);
    }

    /// <summary>
    /// Rebuilds the theme matches. Only the ListView's visible containers exist, so this
    /// replaces hiding hundreds of hand-built rows one by one on every keystroke.
    /// </summary>
    private void FilterPaletteThemes(string query)
    {
        string trimmed = query.Trim();
        _paletteThemeSelected = -1;
        _paletteThemeHovered = -1;
        _paletteThemeMatches.Clear();
        foreach (PaletteThemeOption option in _paletteThemeOptions)
        {
            // Only themes matching the window brightness are offered: a light window edits
            // the "light:" half of "theme", a dark window the "dark:" half.
            if (option.Light != Theme.WindowLight) continue;
            if (trimmed.Length > 0 &&
                !option.Name.Contains(trimmed, StringComparison.OrdinalIgnoreCase)) continue;
            _paletteThemeMatches.Add(option);
        }

        bool any = _paletteThemeMatches.Count > 0;
        if (_paletteThemeEmpty is not null)
            _paletteThemeEmpty.Visibility =
                !any && trimmed.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_paletteThemeList is null) return;

        if (!any)
        {
            _paletteThemeList.SelectedIndex = -1;
            return;
        }

        int previous = _paletteThemeList.SelectedIndex;
        _paletteThemeList.SelectedIndex = 0;
        _paletteThemeList.ScrollIntoView(_paletteThemeMatches[0]);
        // Typing again can leave the index on zero; no SelectionChanged fires then, so the
        // newly filtered first match still has to be previewed here.
        if (!_suppressPalettePreview && previous == 0)
            PreviewThemeOption(_paletteThemeMatches[0].Name);
    }

    /// <summary>Adds the built-in themes and every theme file, in the picker's display order.</summary>
    private void EnsurePaletteThemeOptions()
    {
        if (_paletteThemeOptions.Count > 0) return;
        _paletteThemeOptions.Add(new PaletteThemeOption(Config.DarkThemeName, false));
        _paletteThemeOptions.Add(new PaletteThemeOption(Config.LightThemeName, true));
        foreach ((string name, bool light) in TerminalThemes.Available())
            _paletteThemeOptions.Add(new PaletteThemeOption(name, light));
    }

    private void OnThemeSelectionChanged()
    {
        if (_paletteThemeList is null) return;
        int index = _paletteThemeList.SelectedIndex;
        if (index == _paletteThemeSelected) return;

        int previous = _paletteThemeSelected;
        _paletteThemeSelected = index;
        StyleThemeRowAt(previous);
        StyleThemeRowAt(index);

        if (_suppressPalettePreview || index < 0) return;
        if (_paletteThemeList.SelectedItem is PaletteThemeOption option)
            PreviewThemeOption(option.Name);
    }

    /// <summary>
    /// Opens the theme level on its first row no matter how the level was entered, so typing
    /// a search and arrowing to "Choose Theme" land on the same state.
    /// </summary>
    private void SelectFirstTheme()
    {
        if (_paletteThemeList is null || _paletteThemeMatches.Count == 0) return;
        _paletteThemeList.SelectedIndex = 0;
        // Already-zero selections raise no SelectionChanged; keep the bookkeeping in step.
        _paletteThemeSelected = 0;
        StyleThemeRowAt(0);
        _paletteThemeList.ScrollIntoView(_paletteThemeMatches[0]);
        // A selected row is a previewed theme here, so entering the level applies the first
        // theme exactly as arrowing onto it would.
        PreviewThemeOption(_paletteThemeMatches[0].Name);
    }

    private void StepThemeSelection(bool down)
    {
        if (_paletteThemeList is null) return;
        int count = _paletteThemeMatches.Count;
        if (count == 0) return;

        int current = _paletteThemeList.SelectedIndex;
        int next = current < 0 || current >= count
            ? (down ? 0 : count - 1)
            : (down ? (current + 1) % count : (current + count - 1) % count);

        _paletteThemeList.SelectedIndex = next;
        _paletteThemeList.ScrollIntoView(_paletteThemeMatches[next]);
    }

    private void CommitPaletteTheme()
    {
        if (_paletteThemeList?.SelectedItem is not PaletteThemeOption option) return;
        HideCommandPalette();
        ApplyThemeChoice(option.Name, option.Light);
    }

    private void SelectPalette(int index)
    {
        List<PaletteItem> items = ActivePaletteItems();

        // Restyle only the row that loses the highlight and the one that gains it, so a
        // level with many rows does not repaint every one of them on each arrow key.
        if (_paletteSelectedList is not null)
        {
            if (ReferenceEquals(_paletteSelectedList, items))
            {
                if (_paletteSelected >= 0 && _paletteSelected < items.Count)
                    StylePaletteRow(items[_paletteSelected], selected: false);
            }
            else
            {
                // The index belongs to the level being left. Clear every highlight there so
                // none stays behind when that level is shown again.
                foreach (PaletteItem item in _paletteSelectedList)
                    StylePaletteRow(item, selected: false);
            }
        }

        _paletteSelectedList = items;
        _paletteSelected = index;
        if (index >= 0 && index < items.Count) StylePaletteRow(items[index], selected: true);

        // Keep the highlighted row visible even when only the bar moved.
        _dispatcher?.TryEnqueue(ScrollPaletteSelectionIntoView);
    }

    private void StylePaletteRow(PaletteItem item, bool selected)
    {
        // Selection and hover combine: a hovered row keeps its stronger fill even when the
        // keyboard moves the selection onto it, so hover is never swallowed by selection.
        item.Row.Background = selected
            ? (item.Hovered ? AccentStrokeBrush() : AccentSoftBrush())
            : (item.Hovered ? ChipHoverBrush() : ClearBrush());
        item.Title.Foreground = selected || item.Hovered ? TextStrongBrush() : TextBodyBrush();
    }

    private void RunPaletteItem(List<PaletteItem> items, int index)
    {
        if (index < 0 || index >= items.Count) return;
        PaletteItem item = items[index];
        if (item.Row.Visibility != Visibility.Visible) return;

        // Parent rows open the next level and keep the palette open.
        if (item.Submenu)
        {
            item.Action?.Invoke();
            return;
        }

        Action? action = item.Action;
        HideCommandPalette();
        action?.Invoke();
    }

    private void RunPalette()
    {
        if (IsPaletteThemeLevel)
        {
            CommitPaletteTheme();
            return;
        }
        RunPaletteItem(ActivePaletteItems(), _paletteSelected);
    }

    /// <summary>
    /// The hamburger menu shared by the title bar and the rail.
    /// </summary>
    private MenuFlyout BuildMainMenu()
    {
        var menu = new MenuFlyout
        {
            // Edge-aligned to the button: centered below it would hang the menu off the
            // window when the rail is at its narrowest.
            Placement = _placement == TabPlacement.Left
                ? Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft
                : Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
        };

        var file = new MenuFlyoutSubItem { Text = Strings.FileMenu };
        var newTab = new MenuFlyoutItem
        {
            Text = Strings.NewTab,
            KeyboardAcceleratorTextOverride = _keybinds.Label("new_tab", "Ctrl+Shift+T"),
        };
        newTab.Click += (_, _) => Activate(IndexOf(NewTab()));
        file.Items.Add(newTab);

        var newWindow = new MenuFlyoutItem
        {
            Text = Strings.NewWindow,
            KeyboardAcceleratorTextOverride = _keybinds.Label("new_window", "Ctrl+Shift+N"),
        };
        newWindow.Click += (_, _) => OpenNewWindow();
        file.Items.Add(newWindow);
        file.Items.Add(new MenuFlyoutSeparator());

        var openDirectory = new MenuFlyoutItem
        {
            Text = Strings.OpenDirectory,
            KeyboardAcceleratorTextOverride = _keybinds.Label("open_directory", "Ctrl+K Ctrl+O"),
        };
        openDirectory.Click += (_, _) => _ = OpenDirectoryAsync();
        file.Items.Add(openDirectory);
        file.Items.Add(new MenuFlyoutSeparator());

        var fileCloseTab = new MenuFlyoutItem
        {
            Text = Strings.CloseTab,
            KeyboardAcceleratorTextOverride = _keybinds.Label("close_tab:this", string.Empty),
        };
        fileCloseTab.Click += (_, _) =>
        {
            if (_tabs.Count > 0) CloseTab(_active);
        };
        file.Items.Add(fileCloseTab);

        var fileCloseWindow = new MenuFlyoutItem
        {
            Text = Strings.CloseWindowCommand,
            KeyboardAcceleratorTextOverride = _keybinds.Label("close_window", "Alt+F4"),
        };
        fileCloseWindow.Click += (_, _) => CloseWindow();
        file.Items.Add(fileCloseWindow);
        file.Items.Add(new MenuFlyoutSeparator());

        var fileSettings = new MenuFlyoutItem
        {
            Text = Strings.Settings,
            KeyboardAcceleratorTextOverride = _keybinds.Label("open_config", "Ctrl+,"),
        };
        fileSettings.Click += (_, _) => ShowSettings();
        file.Items.Add(fileSettings);
        file.Items.Add(new MenuFlyoutSeparator());

        var exit = new MenuFlyoutItem { Text = Strings.Exit };
        exit.Click += (_, _) => QuitApplication();
        file.Items.Add(exit);
        menu.Items.Add(file);

        var edit = new MenuFlyoutSubItem { Text = Strings.EditMenu };

        var copy = new MenuFlyoutItem
        {
            Text = Strings.Copy,
            KeyboardAcceleratorTextOverride = _keybinds.Label("copy_to_clipboard:plain", "Ctrl+Shift+C"),
        };
        copy.Click += (_, _) => ActivePane()?.CopySelection();
        edit.Items.Add(copy);

        var paste = new MenuFlyoutItem
        {
            Text = Strings.Paste,
            KeyboardAcceleratorTextOverride = _keybinds.Label("paste_from_clipboard", "Ctrl+Shift+V"),
        };
        paste.Click += (_, _) => ActivePane()?.PasteFromClipboard();
        edit.Items.Add(paste);

        var selectAll = new MenuFlyoutItem
        {
            Text = Strings.SelectAll,
            KeyboardAcceleratorTextOverride = _keybinds.Label("select_all", "Ctrl+Shift+A"),
        };
        selectAll.Click += (_, _) => ActivePane()?.SelectAll();
        edit.Items.Add(selectAll);
        edit.Items.Add(new MenuFlyoutSeparator());

        var find = new MenuFlyoutItem
        {
            Text = Strings.EditFind,
            KeyboardAcceleratorTextOverride = _keybinds.Label("start_search", "Ctrl+Shift+F"),
        };
        find.Click += (_, _) => ActivePane()?.ShowSearch();
        edit.Items.Add(find);

        menu.Items.Add(edit);

        var view = new MenuFlyoutSubItem { Text = Strings.ViewMenu };

        // Split commands lead the menu, in the order of the reference layout.
        var splitRight = new MenuFlyoutItem
        {
            Text = Strings.SplitRight,
            KeyboardAcceleratorTextOverride = _keybinds.Label("new_split:right", "Ctrl+Shift+O"),
        };
        splitRight.Click += (_, _) => SplitFocused(SplitDirection.Right);
        view.Items.Add(splitRight);

        var splitLeft = new MenuFlyoutItem
        {
            Text = Strings.SplitLeft,
            KeyboardAcceleratorTextOverride = _keybinds.Label("new_split:left", string.Empty),
        };
        splitLeft.Click += (_, _) => SplitFocused(SplitDirection.Left);
        view.Items.Add(splitLeft);

        var splitDown = new MenuFlyoutItem
        {
            Text = Strings.SplitDown,
            KeyboardAcceleratorTextOverride = _keybinds.Label("new_split:down", "Ctrl+Shift+E"),
        };
        splitDown.Click += (_, _) => SplitFocused(SplitDirection.Down);
        view.Items.Add(splitDown);

        var splitUp = new MenuFlyoutItem
        {
            Text = Strings.SplitUp,
            KeyboardAcceleratorTextOverride = _keybinds.Label("new_split:up", string.Empty),
        };
        splitUp.Click += (_, _) => SplitFocused(SplitDirection.Up);
        view.Items.Add(splitUp);

        var zoomSplit = new MenuFlyoutItem
        {
            Text = Strings.ViewZoomSplit,
            KeyboardAcceleratorTextOverride = _keybinds.Label("toggle_split_zoom", "Ctrl+Shift+Enter"),
        };
        zoomSplit.Click += (_, _) => TogglePaneZoom();
        view.Items.Add(zoomSplit);

        var resizeSplit = new MenuFlyoutSubItem { Text = Strings.ViewResizeSplit };
        var resizeLeft = new MenuFlyoutItem
        {
            Text = Strings.ResizeSplitLeft,
            KeyboardAcceleratorTextOverride = _keybinds.Label("resize_split:left", "Win+Ctrl+Shift+\u2190"),
        };
        resizeLeft.Click += (_, _) => ResizeSplit(-1, 0);
        resizeSplit.Items.Add(resizeLeft);
        var resizeRight = new MenuFlyoutItem
        {
            Text = Strings.ResizeSplitRight,
            KeyboardAcceleratorTextOverride = _keybinds.Label("resize_split:right", "Win+Ctrl+Shift+\u2192"),
        };
        resizeRight.Click += (_, _) => ResizeSplit(1, 0);
        resizeSplit.Items.Add(resizeRight);
        var resizeUp = new MenuFlyoutItem
        {
            Text = Strings.ResizeSplitUp,
            KeyboardAcceleratorTextOverride = _keybinds.Label("resize_split:up", "Win+Ctrl+Shift+\u2191"),
        };
        resizeUp.Click += (_, _) => ResizeSplit(0, -1);
        resizeSplit.Items.Add(resizeUp);
        var resizeDown = new MenuFlyoutItem
        {
            Text = Strings.ResizeSplitDown,
            KeyboardAcceleratorTextOverride = _keybinds.Label("resize_split:down", "Win+Ctrl+Shift+\u2193"),
        };
        resizeDown.Click += (_, _) => ResizeSplit(0, 1);
        resizeSplit.Items.Add(resizeDown);
        view.Items.Add(resizeSplit);

        var focusToPane = new MenuFlyoutSubItem { Text = Strings.ViewFocusSplit };
        var focusPrevious = new MenuFlyoutItem
        {
            Text = Strings.FocusSplitPrevious,
            KeyboardAcceleratorTextOverride = _keybinds.Label("goto_split:previous", "Ctrl+Win+["),
        };
        focusPrevious.Click += (_, _) => CyclePane(-1);
        focusToPane.Items.Add(focusPrevious);
        var focusNext = new MenuFlyoutItem
        {
            Text = Strings.FocusSplitNext,
            KeyboardAcceleratorTextOverride = _keybinds.Label("goto_split:next", "Ctrl+Win+]"),
        };
        focusNext.Click += (_, _) => CyclePane(1);
        focusToPane.Items.Add(focusNext);
        var focusLeft = new MenuFlyoutItem
        {
            Text = Strings.FocusSplitLeft,
            KeyboardAcceleratorTextOverride = _keybinds.Label("goto_split:left", "Ctrl+Alt+\u2190"),
        };
        focusLeft.Click += (_, _) => FocusPaneTowards(-1, 0);
        focusToPane.Items.Add(focusLeft);
        var focusRight = new MenuFlyoutItem
        {
            Text = Strings.FocusSplitRight,
            KeyboardAcceleratorTextOverride = _keybinds.Label("goto_split:right", "Ctrl+Alt+\u2192"),
        };
        focusRight.Click += (_, _) => FocusPaneTowards(1, 0);
        focusToPane.Items.Add(focusRight);
        var focusUp = new MenuFlyoutItem
        {
            Text = Strings.FocusSplitUp,
            KeyboardAcceleratorTextOverride = _keybinds.Label("goto_split:up", "Ctrl+Alt+\u2191"),
        };
        focusUp.Click += (_, _) => FocusPaneTowards(0, -1);
        focusToPane.Items.Add(focusUp);
        var focusDown = new MenuFlyoutItem
        {
            Text = Strings.FocusSplitDown,
            KeyboardAcceleratorTextOverride = _keybinds.Label("goto_split:down", "Ctrl+Alt+\u2193"),
        };
        focusDown.Click += (_, _) => FocusPaneTowards(0, 1);
        focusToPane.Items.Add(focusDown);
        view.Items.Add(focusToPane);
        view.Items.Add(new MenuFlyoutSeparator());

        var increaseFont = new MenuFlyoutItem
        {
            Text = Strings.IncreaseFontSize,
            KeyboardAcceleratorTextOverride = _keybinds.Label("increase_font_size:1", "Ctrl+Plus"),
        };
        increaseFont.Click += (_, _) => ActivePane()?.AdjustFontSize(1.0f);
        view.Items.Add(increaseFont);

        var decreaseFont = new MenuFlyoutItem
        {
            Text = Strings.DecreaseFontSize,
            KeyboardAcceleratorTextOverride = _keybinds.Label("decrease_font_size:1", "Ctrl+-"),
        };
        decreaseFont.Click += (_, _) => ActivePane()?.AdjustFontSize(-1.0f);
        view.Items.Add(decreaseFont);

        var resetFont = new MenuFlyoutItem
        {
            Text = Strings.ResetFontSize,
            KeyboardAcceleratorTextOverride = _keybinds.Label("reset_font_size", "Ctrl+0"),
        };
        resetFont.Click += (_, _) => ActivePane()?.ResetFontSize();
        view.Items.Add(resetFont);
        view.Items.Add(new MenuFlyoutSeparator());

        var layout = new MenuFlyoutSubItem { Text = Strings.ViewLayout };
        var tabsTop = new ToggleMenuFlyoutItem
        {
            Text = Strings.TabBarTop,
            IsChecked = _placement == TabPlacement.Top,
        };
        tabsTop.Click += (_, _) => SetTabPlacement("top");
        layout.Items.Add(tabsTop);
        var tabsLeft = new ToggleMenuFlyoutItem
        {
            Text = Strings.TabBarLeft,
            IsChecked = _placement == TabPlacement.Left,
        };
        tabsLeft.Click += (_, _) => SetTabPlacement("left");
        layout.Items.Add(tabsLeft);
        var tabsRight = new ToggleMenuFlyoutItem
        {
            Text = Strings.TabBarRight,
            IsChecked = _placement == TabPlacement.Right,
        };
        tabsRight.Click += (_, _) => SetTabPlacement("right");
        layout.Items.Add(tabsRight);
        view.Items.Add(layout);

        var viewSidebar = new MenuFlyoutItem
        {
            Text = Strings.ToggleSidebar,
            KeyboardAcceleratorTextOverride = _keybinds.Label("toggle_sidebar", "Ctrl+Shift+B"),
        };
        viewSidebar.Click += (_, _) => ToggleSidebar();
        view.Items.Add(viewSidebar);

        var compact = new ToggleMenuFlyoutItem
        {
            Text = Strings.ViewCompact,
            IsChecked = CompactDensity,
        };
        compact.Click += (_, _) => SetDensity(!CompactDensity);
        view.Items.Add(compact);

        var quickTerminal = new MenuFlyoutItem
        {
            Text = Strings.ToggleQuickTerminal,
            KeyboardAcceleratorTextOverride = _keybinds.Label("toggle_quick_terminal", string.Empty),
        };
        // The flyout is still dismissing on Click; toggling now makes the drop-down show and
        // lose focus in the same dispatch, and autohide hides it again. Defer past the close.
        quickTerminal.Click += (_, _) => _dispatcher?.TryEnqueue(QuickTerminal.Toggle);
        view.Items.Add(quickTerminal);

        var viewCommandPalette = new MenuFlyoutItem
        {
            Text = Strings.CommandPalette,
            KeyboardAcceleratorTextOverride = _keybinds.Label("toggle_command_palette", "Ctrl+Shift+P"),
        };
        viewCommandPalette.Click += (_, _) => ShowCommandPalette();
        view.Items.Add(viewCommandPalette);

        menu.Items.Add(view);

        var window = new MenuFlyoutSubItem { Text = Strings.WindowMenu };

        var previousTab = new MenuFlyoutItem
        {
            Text = Strings.PreviousTab,
            KeyboardAcceleratorTextOverride = _keybinds.Label("previous_tab", "Ctrl+Shift+Tab"),
        };
        previousTab.Click += (_, _) => CycleTab(-1);
        window.Items.Add(previousTab);

        var nextTab = new MenuFlyoutItem
        {
            Text = Strings.NextTab,
            KeyboardAcceleratorTextOverride = _keybinds.Label("next_tab", "Ctrl+Tab"),
        };
        nextTab.Click += (_, _) => CycleTab(1);
        window.Items.Add(nextTab);

        menu.Items.Add(window);

        var help = new MenuFlyoutSubItem { Text = Strings.HelpMenu };

        var documentation = new MenuFlyoutItem { Text = Strings.HelpDocumentation };
        documentation.Click += (_, _) => LaunchUrl(DocumentationUrl);
        help.Items.Add(documentation);

        var feedback = new MenuFlyoutItem { Text = Strings.HelpFeedback };
        feedback.Click += (_, _) => LaunchUrl(FeedbackUrl);
        help.Items.Add(feedback);
        help.Items.Add(new MenuFlyoutSeparator());

        var releaseNotes = new MenuFlyoutItem { Text = Strings.HelpReleaseNotes };
        releaseNotes.Click += (_, _) => LaunchUrl(ReleaseNotesUrl);
        help.Items.Add(releaseNotes);

        var checkUpdates = new MenuFlyoutItem { Text = Strings.HelpCheckForUpdates };
        checkUpdates.Click += (_, _) => _ = RunUpdateCheckAsync(userInitiated: true);
        help.Items.Add(checkUpdates);
        help.Items.Add(new MenuFlyoutSeparator());

        var about = new MenuFlyoutItem { Text = Strings.HelpAbout };
        about.Click += (_, _) => ShowAbout();
        help.Items.Add(about);

        menu.Items.Add(help);
        menu.Items.Add(new MenuFlyoutSeparator());

        var commandPalette = new MenuFlyoutItem
        {
            Text = Strings.CommandPalette,
            KeyboardAcceleratorTextOverride = _keybinds.Label("toggle_command_palette", "Ctrl+Shift+P"),
        };
        commandPalette.Click += (_, _) => ShowCommandPalette();
        menu.Items.Add(commandPalette);

        var settings = new MenuFlyoutItem
        {
            Text = Strings.Settings,
            KeyboardAcceleratorTextOverride = _keybinds.Label("open_config", "Ctrl+,"),
        };
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        return menu;
    }

    /// <summary>Shows the app's identity and links in a standalone dialog.</summary>
    private async void ShowAbout()
    {
        XamlRoot? root = _root.XamlRoot;
        if (root is null) return;

        try
        {
            var icon = new Image
            {
                Width = 48,
                Height = 48,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(Path.Combine(AppContext.BaseDirectory, "Assets",
                        "Square150x150Logo.scale-200.png"))),
            };

            var identity = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            identity.Children.Add(new TextBlock
            {
                Text = "Gui",
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            identity.Children.Add(new TextBlock { Text = AppVersion(), FontSize = 12 });
            identity.Children.Add(new TextBlock
            {
                Text = Strings.AboutCopyright(DateTime.Now.Year),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            header.Children.Add(icon);
            header.Children.Add(identity);

            var body = new StackPanel { Spacing = 14, Width = 380 };
            body.Children.Add(header);
            body.Children.Add(new HyperlinkButton
            {
                Content = RepositoryUrl,
                NavigateUri = new Uri(RepositoryUrl),
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono"),
                FontSize = 12,
                Padding = new Thickness(0),
                MinWidth = 0,
            });
            body.Children.Add(new TextBlock
            {
                Text = Strings.AboutLicense + ": MIT License",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });

            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = Strings.HelpAbout,
                Content = body,
                PrimaryButtonText = Strings.Ok,
                DefaultButton = ContentDialogButton.Primary,
            };
            await dialog.ShowAsync();
        }
        catch (Exception error)
        {
            Diag.LogException("workspace.showAbout", error);
        }
    }

    // ---- update check (Ghostty-style pill) ---------------------------------

    // Ghostty checks in the background and reports through a small pill instead of a modal
    // dialog: "Checking for Updates…", "Update Available: X", "No Updates Available". The pill
    // appears only when there is something to say; an available update stays until clicked.
    private Border? _updatePill;
    private TextBlock? _updatePillText;
    private FontIcon? _updatePillIcon;
    private FontIcon? _updatePillCloseIcon;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updatePillTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateDelayTimer;
    private bool _updateCheckInFlight;
    private bool _updatePillClickable;
    private bool _updatePillVisible;
    private string _updatePillUrl = string.Empty;
    private string _updatePillMessage = string.Empty;
    private char _updatePillGlyph;
    private bool _updatePillWarning;
    private static bool _startupUpdateChecked;

    /// <summary>Builds the floating update pill. Called from BuildChrome, so a chrome rebuild recreates it.</summary>
    private void BuildUpdatePill()
    {
        if (_quickTerminal) return;

        _updatePillText = new TextBlock
        {
            FontSize = Theme.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420,
        };
        _updatePillIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = Theme.FontSmall,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The close button stays available in every state, including a persistent
        // "Update Available" pill, so the notice can be dismissed without downloading.
        _updatePillCloseIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Glyph = "\uE711",
            FontSize = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var close = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, -2, 0),
            Child = _updatePillCloseIcon,
        };
        close.PointerEntered += (_, _) => close.Background = new SolidColorBrush(Theme.ChipHover());
        close.PointerExited += (_, _) => close.Background = ClearBrush();
        close.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, close)) return;
            HideUpdatePill();
        };

        row.Children.Add(_updatePillIcon);
        row.Children.Add(_updatePillText);
        row.Children.Add(close);

        _updatePill = new Border
        {
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 4, 10, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 16, 16),
            Visibility = Visibility.Collapsed,
            Child = row,
            Shadow = new ThemeShadow(),
            Translation = new System.Numerics.Vector3(0, 0, 32),
        };
        _updatePill.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, _updatePill!)) return;
            if (_updatePillClickable && !_updateCheckInFlight)
            {
                LaunchUrl(_updatePillUrl.Length > 0 ? _updatePillUrl : ReleaseNotesUrl);
                HideUpdatePill();
            }
            else
            {
                HideUpdatePill();
            }
        };

        Grid.SetColumn(_updatePill, 0);
        Grid.SetColumnSpan(_updatePill, Math.Max(1, _body.ColumnDefinitions.Count));
        _body.Children.Add(_updatePill);

        // Re-apply the last state so a chrome rebuild (tab rail move, config reload) does not
        // silently drop an "update available" notice.
        RenderUpdatePill();
    }

    private void RenderUpdatePill()
    {
        if (_updatePill is null || _updatePillText is null || _updatePillIcon is null) return;

        _updatePill.Visibility = _updatePillVisible ? Visibility.Visible : Visibility.Collapsed;
        if (!_updatePillVisible) return;

        Windows.UI.Color ink = _updatePillWarning ? Theme.Warning() : Theme.Accent();
        _updatePillText.Text = _updatePillMessage;
        _updatePillText.Foreground = new SolidColorBrush(ink);
        _updatePillIcon.Glyph = _updatePillGlyph.ToString();
        _updatePillIcon.Foreground = new SolidColorBrush(ink);
        if (_updatePillCloseIcon is not null)
            _updatePillCloseIcon.Foreground = new SolidColorBrush(Theme.TextFaint());
        _updatePill.Background = new SolidColorBrush(Theme.PaletteBg());
        _updatePill.BorderBrush = new SolidColorBrush(Theme.Stroke());
        _updatePill.BorderThickness = new Thickness(1);
    }

    /// <summary>Shows the pill. A clickable pill (an available update) stays until clicked.</summary>
    private void ShowUpdatePill(string message, char glyph, bool warning, bool clickable, bool autoHide)
    {
        _updatePillMessage = message;
        _updatePillGlyph = glyph;
        _updatePillWarning = warning;
        _updatePillClickable = clickable;
        _updatePillVisible = true;
        RenderUpdatePill();

        _updatePillTimer?.Stop();
        if (!autoHide || _dispatcher is null) return;

        if (_updatePillTimer is null)
        {
            _updatePillTimer = _dispatcher.CreateTimer();
            _updatePillTimer.IsRepeating = false;
            _updatePillTimer.Interval = TimeSpan.FromSeconds(5);
            _updatePillTimer.Tick += OnUpdatePillTimerTick;
        }
        _updatePillTimer.Start();
    }

    private void OnUpdateDelayTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        _ = RunUpdateCheckAsync(userInitiated: false);
    }

    private void OnUpdatePillTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (!_updatePillClickable) HideUpdatePill();
    }

    private void HideUpdatePill()
    {
        _updatePillVisible = false;
        _updatePillClickable = false;
        _updatePillTimer?.Stop();
        RenderUpdatePill();
    }

    /// <summary>
    /// "Check for Updates" and the launch auto-check. The background check is silent unless a
    /// newer release exists, like Ghostty's auto-update = check.
    /// </summary>
    private async Task RunUpdateCheckAsync(bool userInitiated)
    {
        if (_updateCheckInFlight) return;
        _updateCheckInFlight = true;

        if (userInitiated)
            ShowUpdatePill(Strings.UpdateChecking, '\uE895', warning: false, clickable: false, autoHide: false);

        try
        {
            UpdateCheck check = await Updater.CheckAsync(AppVersion(), _config.AutoUpdateChannel);
            // A definitive answer resets the 24h clock; a failed attempt does not, so the
            // next launch can retry when the network comes back.
            if (check.Status != UpdateResult.Failed) Updater.RecordCheck();
            _dispatcher?.TryEnqueue(() => ApplyUpdateCheck(check, userInitiated));
        }
        catch (Exception error)
        {
            Diag.LogException("workspace.checkUpdates", error);
            if (userInitiated)
                _dispatcher?.TryEnqueue(() => ShowUpdatePill(
                    Strings.UpdateFailed, '\uE711', warning: true, clickable: false, autoHide: true));
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private void ApplyUpdateCheck(UpdateCheck check, bool userInitiated)
    {
        switch (check.Status)
        {
            case UpdateResult.UpdateAvailable:
                _updatePillUrl = check.Url;
                ShowUpdatePill(Strings.UpdateAvailable(check.Version), '\uE896',
                    warning: true, clickable: true, autoHide: false);
                break;
            case UpdateResult.UpToDate:
                if (userInitiated)
                    ShowUpdatePill(Strings.UpdateUpToDate, '\uE73E',
                        warning: false, clickable: false, autoHide: true);
                else
                    HideUpdatePill();
                break;
            default:
                if (userInitiated)
                    ShowUpdatePill(Strings.UpdateFailed, '\uE711',
                        warning: true, clickable: false, autoHide: true);
                break;
        }
    }

    /// <summary>Schedules the launch check once per process, a few seconds after the window settles.</summary>
    private void ScheduleStartupUpdateCheck()
    {
        if (_quickTerminal || _startupUpdateChecked) return;
        if (_config.AutoUpdate == "off") return;
        if (!Updater.AutoCheckDue()) return;
        _startupUpdateChecked = true;
        if (_dispatcher is null) return;

        // Delay on the UI dispatcher rather than Task.Run: the pill is XAML, so the check
        // has to start on the UI thread and its own continuation stays there as well.
        _updateDelayTimer ??= _dispatcher.CreateTimer();
        _updateDelayTimer.IsRepeating = false;
        _updateDelayTimer.Interval = TimeSpan.FromSeconds(4);
        _updateDelayTimer.Tick += OnUpdateDelayTimerTick;
        _updateDelayTimer.Start();
    }

    /// <summary>Opens a public link in the default browser.</summary>
    private static void LaunchUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Diag.LogException("workspace.launchUrl", error);
        }
    }

    /// <summary>Lets the user pick a folder and opens a new tab started in it.</summary>
    private async Task OpenDirectoryAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
            };
            // The picker needs a file-type filter even though a folder is selected.
            picker.FileTypeFilter.Add("*");
            if (_hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            Windows.Storage.StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;

            // Pin the directory onto a fresh shell instead of cd-ing the focused pane: the
            // existing session keeps its own cwd and scrollback.
            Tab tab = OpenTabCore(folder.Path, null, null);
            Activate(IndexOf(tab));
            FocusKeyboardAnchor();
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.OpenDirectory", error);
        }
    }

    private void OpenConfig()
    {
        string file = Config.ConfigFile();
        if (string.IsNullOrEmpty(file)) return;
        Config.EnsureDefault(file);
        try
        {
            // Hand the file to whatever the OS has associated with it.
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.OpenConfig", error);
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", '"' + file + '"') { UseShellExecute = true });
            }
            catch (Exception fallback)
            {
                Diag.LogException("Workspace.OpenConfigNotepad", fallback);
            }
        }
    }

    /// <summary>Opens the config in a new window whose shell runs the OS editor on it.</summary>
    private void OpenConfigInNewWindow()
    {
        string file = Config.ConfigFile();
        if (string.IsNullOrEmpty(file)) return;
        Config.EnsureDefault(file);
        App.OpenWindow(null, null, "notepad.exe \"" + file + "\"");
    }

    /// <summary>Blocks keyboard, IME and paste input for the focused pane.</summary>
    private void ToggleReadOnlyMode()
    {
        if (ActivePane() is not { } pane) return;
        pane.ReadOnly = !pane.ReadOnly;
    }

    /// <summary>Stops forwarding pointer input to a mouse-tracking program.</summary>
    private void ToggleMouseReportingMode()
    {
        if (ActivePane() is not { } pane) return;
        pane.MouseReportingEnabled = !pane.MouseReportingEnabled;
    }

    /// <summary>The configured background opacity, remembered while the window is opaque.</summary>
    private float _savedBackgroundOpacity = 1.0f;

    /// <summary>Swaps the configured opacity for fully opaque, and back.</summary>
    private void ToggleBackgroundOpacityMode()
    {
        if (_config.BackgroundOpacity < 0.999f)
        {
            _savedBackgroundOpacity = _config.BackgroundOpacity;
            _config.BackgroundOpacity = 1.0f;
        }
        else if (_savedBackgroundOpacity < 0.999f)
        {
            _config.BackgroundOpacity = _savedBackgroundOpacity;
        }
        else
        {
            return;
        }

        ApplyBackgroundOpacity();
        foreach (Tab tab in _tabs)
        {
            ForEachPane(tab, pane =>
            {
                pane.SyncConfig(_config);
                pane.ApplyTheme();
            });
        }
    }

    /// <summary>"Change Terminal Title…"</summary>
    private async void PromptSurfaceTitle()
    {
        try
        {
            if (ActivePane() is not { } pane) return;
            string? value = await PromptTextAsync(Strings.ChangeTerminalTitle, pane.Title);
            if (value is not null) pane.SetTitleOverride(value);
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.PromptSurfaceTitle", error);
        }
    }

    /// <summary>"Change Window Title…"</summary>
    private async void PromptWindowTitle()
    {
        try
        {
            if (_appWindow is null) return;
            string? value = await PromptTextAsync(Strings.ChangeWindowTitle, _appWindow.Title);
            if (value is not null)
            {
                _windowTitleOverride = value.Length > 0 ? value : null;
                _appWindow.Title = value;
                if (_windowTitleOverride is null) UpdateStatus();
            }
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.PromptWindowTitle", error);
        }
    }

    private async Task<string?> PromptTextAsync(string title, string initial)
    {
        XamlRoot? root = _root.XamlRoot;
        if (root is null) return null;

        var box = new TextBox { Text = initial, SelectionStart = initial.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = Strings.Ok,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        _textPromptOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
        }
        finally
        {
            _textPromptOpen = false;
        }
    }

    /// <summary>
    /// "write_screen_file" / "write_selection_file": formats the screen or the selection
    /// into a temporary file, then copies its path, pastes it, or opens it.
    /// </summary>
    private void WriteTerminalFile(bool selectionOnly, string emit, string action)
    {
        if (ActivePane() is not { } pane) return;

        int format = emit == "vt" ? GhosttyNative.FORMAT_VT : GhosttyNative.FORMAT_PLAIN;
        byte[] bytes = pane.FormatTerminal(selectionOnly, format);
        if (bytes.Length == 0) return;

        string text = System.Text.Encoding.UTF8.GetString(bytes);
        if (emit == "html") text = TerminalPane.HtmlFromPlain(text);

        string extension = emit switch { "html" => ".html", "vt" => ".vt", _ => ".txt" };
        string path = Path.Combine(Path.GetTempPath(),
            "gui-" + (selectionOnly ? "selection" : "screen") + "-" +
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + extension);

        try
        {
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(false));
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.WriteTerminalFile", error);
            return;
        }

        switch (action)
        {
            case "copy":
                CopyTextToClipboard(path);
                break;
            case "paste":
                pane.PasteString(path);
                break;
            default:
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception error)
                {
                    Diag.LogException("Workspace.OpenTerminalFile", error);
                }
                break;
        }
    }

    private static string TapLabel() => Tap.Enabled ? Strings.TapOn : Strings.TapOff;

    private void OpenDiagnosticsLog()
    {
        string path = Diag.LogPath;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", '"' + path + '"') { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.OpenDiagnosticsLog", error);
        }
    }

    private void CopyDiagnosticsLog()
    {
        string text = Diag.ReadAll();
        if (text.Length == 0) return;
        CopyTextToClipboard(text);
    }

    /// <summary>Copies the focused tab's title ("copy_title_to_clipboard").</summary>
    private void CopyActiveTitle()
    {
        if (_tabs.Count == 0) return;
        string title = _tabs[_active].Focused?.Title ?? _tabs[_active].Title;
        if (title.Length == 0) return;
        CopyTextToClipboard(title);
    }

    private static void CopyTextToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.CopyText", error);
        }
    }

    private void EnableCrashDumps()
    {
        bool enabled = CrashDump.Enable();
        Diag.Log("crashdump", enabled
            ? $"full dumps enabled in {CrashDump.DumpFolder}"
            : "could not enable crash dumps");
    }
}
