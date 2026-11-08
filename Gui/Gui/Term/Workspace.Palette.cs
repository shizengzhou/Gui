﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
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

    // ---------------- theme switching ----------------

    private void ApplyDesignTheme(bool lightMode)
    {
        Theme.Light = lightMode;
        Theme.Terminal = new TerminalTheme();

        _config.ThemeMode = lightMode ? Config.LightThemeName : Config.DarkThemeName;
        _config.LightTheme = lightMode;
        _config.TerminalTheme = new TerminalTheme();
        RefreshTheme();
    }

    private void ApplyNamedTheme(string name)
    {
        if (!TerminalThemes.Load(name, out TerminalTheme loaded)) return;

        Theme.Terminal = loaded;
        Theme.Light = TerminalThemes.IsLight(loaded.Background);

        _config.ThemeMode = name;
        _config.LightTheme = Theme.Light;
        _config.TerminalTheme = loaded;
        RefreshTheme();
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

        if (_config.LightTheme != Theme.WindowLight)
        {
            // Only themes matching the window brightness are usable: fall back to the
            // default Gui Light / Gui Dark palette.
            ApplyDesignTheme(Theme.WindowLight);
            Config.SaveValue("theme", Theme.WindowLight ? Config.LightThemeName : Config.DarkThemeName);
        }
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

        // Acrylic is not a themed brush; rebuild it with the new tint.
        if (_palettePanel is not null)
        {
            _palettePanel.Background = new AcrylicBrush
            {
                TintColor = Theme.PaletteTint(),
                TintOpacity = Theme.PaletteTintOpacity(),
                FallbackColor = Theme.PaletteBg(),
            };
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
        UpdateInputAnchor();
    }

    private void SetSidebarCollapsed(bool collapsed)
    {
        if (_placement == TabPlacement.Top) return;
        _sidebarCollapsed = collapsed;
        if (_sidebar is not null) _sidebar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        // A hidden rail has no border to drag: the pane runs to the window edge.
        if (_sidebarSplitter is not null)
            _sidebarSplitter.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_headerSideTools is not null)
            _headerSideTools.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;

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
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 0, 10, 0),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
    }

    private PaletteItem MakeRow(string text, string shortcut, List<PaletteItem> owner, Action? action)
    {
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
                CornerRadius = new CornerRadius(4),
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
        int index = owner.Count;
        WirePaletteRow(row, title, owner, index);
        _paletteRows.Children.Add(row);
        owner.Add(item);
        return item;
    }

    private void WirePaletteRow(Border row, TextBlock title, List<PaletteItem> owner, int index)
    {
        row.PointerEntered += (_, _) =>
        {
            if (!ReferenceEquals(owner, ActivePaletteItems())) return;
            if (index == _paletteSelected) return;
            row.Background = ChipHoverBrush();
            title.Foreground = TextStrongBrush();
        };
        row.PointerExited += (_, _) =>
        {
            if (!ReferenceEquals(owner, ActivePaletteItems())) return;
            if (index == _paletteSelected) return;
            row.Background = ClearBrush();
            title.Foreground = TextBodyBrush();
        };
        row.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!ReferenceEquals(owner, ActivePaletteItems())) return;
            RunPaletteItem(owner, index);
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
            CornerRadius = new CornerRadius(4),
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
        int index = items.Count;
        WirePaletteRow(row, title, items, index);
        _paletteRows.Children.Add(row);
        items.Add(item);
    }

    private void AddOptionRow(List<PaletteItem> items, string text, string value, bool light, Action action)
    {
        Border row = PaletteRowBorder();
        row.BorderBrush = ClearBrush();
        row.Background = ClearBrush();

        var grid = new Grid();
        AddStarColumn(grid);
        AddAutoColumn(grid);

        TextBlock title = Label(text, 12.5, TextBodyBrush());
        grid.Children.Add(title);

        FontIcon check = Glyph('\uE73E', 12, AccentBrush());
        check.Visibility = Visibility.Collapsed;
        check.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        row.Child = grid;

        var item = new PaletteItem
        {
            Row = row,
            Title = title,
            Label = text,
            Action = action,
            Value = value,
            Light = light,
        };
        int index = items.Count;
        WirePaletteRow(row, title, items, index);
        _paletteRows.Children.Add(row);
        items.Add(item);

        if (ReferenceEquals(items, _paletteWindowItems)) _paletteWindowChecks.Add(check);
        else if (ReferenceEquals(items, _paletteThemeItems)) _paletteThemeChecks.Add(check);
    }

    /// <summary>Drops the rows and the selection a previous chrome build left behind.</summary>
    private void ResetPaletteState()
    {
        _paletteItems.Clear();
        _paletteWindowItems.Clear();
        _paletteThemeItems.Clear();
        _paletteWindowChecks.Clear();
        _paletteThemeChecks.Clear();
        _paletteThemeNameChecks.Clear();

        _paletteThemesBuilt = false;
        _paletteSubmenu = false;
        _paletteActiveSub = null;
        _paletteSelected = 0;

        _paletteWindowValue = null;
        _paletteThemeValue = null;
        _paletteChip = null;
        _paletteChipText = null;
        _paletteBackRow = null;
        _paletteEmpty = null;
        _paletteScroller = null;
    }

    private void BuildPalette()
    {
        // The chrome is rebuilt whenever the tab rail moves or the config is reloaded, and a
        // rebuild lands here with the previous palette still recorded. The rows below are
        // brand new elements, so the bookkeeping has to start from scratch too: otherwise the
        // lists hold the old rows as well and the highlight lands on one that is no longer in
        // the tree, leaving the visible list with no selection.
        ResetPaletteState();

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
            CornerRadius = new CornerRadius(12),
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
            CornerRadius = new CornerRadius(4),
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
            FontSize = 13,
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

        MakeRow(Strings.NewTab, "Ctrl+Shift+T", _paletteItems,
            () => Activate(IndexOf(NewTab())));
        MakeRow(Strings.NewWindow, "Ctrl+Shift+N", _paletteItems, OpenNewWindow);
        MakeRow(Strings.SplitRight, "Alt+Shift+Plus", _paletteItems, () => SplitFocused(false));
        MakeRow(Strings.SplitDown, "Alt+Shift+-", _paletteItems, () => SplitFocused(true));
        MakeRow(Strings.CloseFocusedPane, "Ctrl+Shift+W", _paletteItems, CloseFocusedPane);
        MakeRow(Strings.FindInTerminal, "Ctrl+Shift+F", _paletteItems, () =>
        {
            if (_tabs.Count > 0) _tabs[_active].Focused?.ShowSearch();
        });
        MakeRow(Strings.CopySelection, "Ctrl+Shift+C", _paletteItems, () =>
        {
            if (_tabs.Count > 0) _tabs[_active].Focused?.CopySelection();
        });
        MakeRow(Strings.Paste, "Ctrl+Shift+V", _paletteItems, () =>
        {
            if (_tabs.Count > 0) _tabs[_active].Focused?.PasteFromClipboard();
        });
        MakeRow(Strings.ReloadConfig, "Ctrl+Shift+,", _paletteItems, ReloadConfig);
        MakeRow(Strings.OpenConfig, string.Empty, _paletteItems, OpenConfig);

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

        // Two-level entries: Window Theme and Theme. The parent rows live in the root list;
        // their actions open the corresponding second level.
        AddSubmenuRow(_paletteItems, Strings.ChooseWindowTheme,
            () => EnterPaletteSubmenu(Strings.WindowTheme, Strings.ChooseWindowTheme, _paletteWindowItems), out TextBlock windowValue);
        _paletteWindowValue = windowValue;
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

        (string Label, string Value)[] windowOptions =
        {
            (Strings.FollowSystem, "system"), (Strings.Light, "light"), (Strings.Dark, "dark"),
        };
        foreach ((string label, string value) in windowOptions)
        {
            string optionValue = value;
            AddOptionRow(_paletteWindowItems, label, optionValue, false, () =>
            {
                ApplyWindowThemeMode(optionValue);
                Config.SaveValue("window-theme", optionValue);
                _themePreviewActive = false;
                BroadcastTheme();
            });
        }

        (string Label, bool Light)[] themeOptions =
        {
            (Config.DarkThemeName, false),
            (Config.LightThemeName, true),
        };
        foreach ((string label, bool isLight) in themeOptions)
        {
            bool lightMode = isLight;
            string value = lightMode ? Config.LightThemeName : Config.DarkThemeName;
            AddOptionRow(_paletteThemeItems, label, value, lightMode, () =>
            {
                ApplyDesignTheme(lightMode);
                Config.SaveValue("theme", value);
                _themePreviewActive = false;
                BroadcastTheme();
            });
        }

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
        List<PaletteItem> items = ActivePaletteItems();
        if (items.Count == 0) return;

        var visible = new bool[items.Count];
        for (int index = 0; index < items.Count; index++)
            visible[index] = items[index].Row.Visibility == Visibility.Visible;

        int next = PaletteSelection.Step(visible, _paletteSelected, down);
        if (next != _paletteSelected) SelectPalette(next);
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

        // The keyboard anchor sits above the whole shell at the cursor cell. The card can be
        // drawn over that cell, and the anchor would then swallow presses meant for the card,
        // so it drops behind the shell while the palette is open.
        Canvas.SetZIndex(_inputAnchor, -1);

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
        Canvas.SetZIndex(_inputAnchor, 0);
        if (_tabs.Count > 0) FocusKeyboardAnchor();
    }

    private List<PaletteItem> ActivePaletteItems() =>
        _paletteSubmenu && _paletteActiveSub is not null ? _paletteActiveSub : _paletteItems;

    private void RefreshPaletteValues()
    {
        string windowMode = _config.WindowThemeMode is "light" or "dark" ? _config.WindowThemeMode : "system";
        if (_paletteWindowValue is not null)
        {
            _paletteWindowValue.Text = windowMode switch
            {
                "light" => Strings.Light,
                "dark" => Strings.Dark,
                _ => Strings.FollowSystem,
            };
        }
        if (_paletteThemeValue is not null)
        {
            // Bundled themes show their own name; the design palettes are Gui Light/Dark.
            _paletteThemeValue.Text = _config.TerminalTheme.Custom
                ? _config.ThemeMode
                : _config.LightTheme ? "Gui Light" : "Gui Dark";
        }

        string[] windowValues = { "system", "light", "dark" };
        for (int i = 0; i < _paletteWindowChecks.Count && i < 3; i++)
            _paletteWindowChecks[i].Visibility = windowMode == windowValues[i] ? Visibility.Visible : Visibility.Collapsed;

        bool customTheme = _config.TerminalTheme.Custom;
        for (int i = 0; i < _paletteThemeChecks.Count && i < 2; i++)
        {
            bool optionLight = i == 1;
            _paletteThemeChecks[i].Visibility =
                !customTheme && optionLight == _config.LightTheme ? Visibility.Visible : Visibility.Collapsed;
        }

        string wanted = _config.ThemeMode.ToLowerInvariant();
        foreach ((string name, FontIcon check) in _paletteThemeNameChecks)
        {
            check.Visibility = customTheme && name.ToLowerInvariant() == wanted
                ? Visibility.Visible : Visibility.Collapsed;
        }
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

        if (design) Theme.Terminal = new TerminalTheme();
        Theme.Light = light;

        RefreshTheme();
        _themePreviewActive = true;
    }

    private void PreviewWindowThemeOption(string value)
    {
        Theme.WindowLight = value switch
        {
            "light" => true,
            "dark" => false,
            _ => Config.SystemUsesLightTheme(),
        };

        // Preview the palette that the mode allows: keep the committed theme when its
        // brightness matches, otherwise show the default Gui Light/Dark.
        if (_config.LightTheme == Theme.WindowLight)
        {
            Theme.Light = _config.LightTheme;
            Theme.Terminal = _config.TerminalTheme;
        }
        else
        {
            Theme.Light = Theme.WindowLight;
            Theme.Terminal = new TerminalTheme();
        }
        RefreshTheme();
        _themePreviewActive = true;
    }

    private void PreviewSelectedPaletteItem(int index)
    {
        if (!_paletteSubmenu || _paletteActiveSub is null) return;
        List<PaletteItem> items = _paletteActiveSub;
        if (index < 0 || index >= items.Count) return;

        PaletteItem item = items[index];
        if (item.Value.Length == 0) return;

        if (ReferenceEquals(_paletteActiveSub, _paletteWindowItems)) PreviewWindowThemeOption(item.Value);
        else if (ReferenceEquals(_paletteActiveSub, _paletteThemeItems)) PreviewThemeOption(item.Value);
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
        if (_paletteScroller is null) return;
        List<PaletteItem> items = ActivePaletteItems();
        if (_paletteSelected < 0 || _paletteSelected >= items.Count) return;

        Border row = items[_paletteSelected].Row;
        if (row.Visibility != Visibility.Visible) return;

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
    /// Puts the shared list back on its first row. Both levels live in one ScrollViewer, so
    /// without this a submenu opens at the offset the level above was left at — the theme
    /// list then starts a few entries down instead of at its first theme.
    /// </summary>
    private void ResetPaletteScroll() => _paletteScroller?.ChangeView(null, 0.0, null, true);

    private void EnterPaletteSubmenu(string title, string placeholder, List<PaletteItem> items)
    {
        if (ReferenceEquals(items, _paletteThemeItems) && !_paletteThemesBuilt)
        {
            _paletteThemesBuilt = true;
            foreach ((string name, bool light) in TerminalThemes.Bundled())
            {
                string themeName = name;
                AddOptionRow(_paletteThemeItems, name, name, light, () =>
                {
                    ApplyNamedTheme(themeName);
                    Config.SaveValue("theme", themeName);
                    _themePreviewActive = false;
                    BroadcastTheme();
                });
            }
            // The bundled rows did not exist when the palette opened, so their check marks
            // (including the committed theme's) must be applied now.
            RefreshPaletteValues();
        }

        _paletteSubmenu = true;
        _paletteActiveSub = items;
        if (ReferenceEquals(items, _paletteThemeItems)) _paletteAllowedLight = Theme.WindowLight;

        if (_paletteChipText is not null) _paletteChipText.Text = title;
        if (_paletteChip is not null) _paletteChip.Visibility = Visibility.Visible;
        if (_paletteBackRow is not null) _paletteBackRow.Visibility = Visibility.Visible;
        _paletteInput.PlaceholderText = placeholder;
        _paletteInput.Text = string.Empty;

        // Open with no row focused and no preview; the first arrow key starts the selection
        // (and with it, the live preview).
        _suppressPalettePreview = true;
        FilterPalette();
        _suppressPalettePreview = false;
        SelectPalette(items.Count);

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
        _paletteSelected = 0;
        FilterPalette();
        ResetPaletteScroll();
    }

    private void FilterPalette()
    {
        string query = _paletteInput.Text.ToLowerInvariant();

        // Rows from the other level never participate in the current filter.
        void Collapse(List<PaletteItem> list)
        {
            foreach (PaletteItem item in list) item.Row.Visibility = Visibility.Collapsed;
        }

        if (_paletteSubmenu)
        {
            Collapse(_paletteItems);
            if (!ReferenceEquals(_paletteActiveSub, _paletteWindowItems)) Collapse(_paletteWindowItems);
            if (!ReferenceEquals(_paletteActiveSub, _paletteThemeItems)) Collapse(_paletteThemeItems);
        }
        else
        {
            Collapse(_paletteWindowItems);
            Collapse(_paletteThemeItems);
        }

        List<PaletteItem> items = ActivePaletteItems();
        int first = items.Count;
        int matches = 0;
        for (int index = 0; index < items.Count; index++)
        {
            PaletteItem item = items[index];
            bool match = query.Length == 0 || item.Label.ToLowerInvariant().Contains(query);
            // Only themes matching the window theme's brightness are selectable.
            bool modeMatch = !ReferenceEquals(_paletteActiveSub, _paletteThemeItems) || item.Light == _paletteAllowedLight;
            bool visible = match && modeMatch;

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

    private void SelectPalette(int index)
    {
        _paletteSelected = index;
        List<PaletteItem> items = ActivePaletteItems();
        for (int current = 0; current < items.Count; current++)
        {
            PaletteItem item = items[current];
            bool selected = current == index;
            item.Row.Background = selected ? AccentSoftBrush() : ClearBrush();
            item.Row.BorderBrush = selected ? AccentStrokeBrush() : ClearBrush();
            item.Title.Foreground = selected ? TextStrongBrush() : TextBodyBrush();
        }

        // Moving through a theme menu previews the highlighted option live.
        if (_paletteSubmenu && !_suppressPalettePreview) PreviewSelectedPaletteItem(index);

        // Keep the highlighted row visible even when only the bar moved.
        _dispatcher?.TryEnqueue(ScrollPaletteSelectionIntoView);
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

    private void RunPalette() => RunPaletteItem(ActivePaletteItems(), _paletteSelected);

    private void OpenConfig()
    {
        string file = Config.ConfigFile();
        if (string.IsNullOrEmpty(file)) return;
        Config.EnsureDefault(file);
        try
        {
            Process.Start(new ProcessStartInfo("notepad.exe", '"' + file + '"') { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.OpenConfig", error);
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

        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.CopyDiagnosticsLog", error);
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
