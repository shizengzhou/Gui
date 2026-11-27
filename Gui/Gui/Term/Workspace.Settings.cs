using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Text;

namespace Gui.Term;

public sealed partial class Workspace
{
    private sealed class SettingsNavEntry
    {
        public string Page = string.Empty;
        public string Label = string.Empty;
        public ChromeButton Button = null!;
        public TextBlock Text = null!;
        public FontIcon Icon = null!;
        public Border Accent = null!;
    }

    /// <summary>The family the renderer falls back to when "font-family" is unset.</summary>
    private const string DefaultFontFamily = "JetBrainsMono Nerd Font";


    private Grid? _settingsLayer;
    private Button? _settingsFocus;
    private Grid? _settingsPages;
    private TextBlock? _settingsTitle;
    private string _settingsPage = "general";

    /// <summary>True while the settings page covers the window body.</summary>
    private bool _settingsActive;

    private readonly List<SettingsNavEntry> _settingsNavEntries = new();
    private readonly List<(string Title, FrameworkElement View)> _settingsPagesList = new();

    /// <summary>
    /// The settings page from the design: a card over the window body with a category rail
    /// on the left and one scrolling page on the right. It is not a tab; opening it covers
    /// the tab strip and the panes, and Esc or the header's close button returns.
    /// </summary>
    private void BuildSettings()
    {
        // The page is about to be replaced, so a row that is still waiting for a key has
        // nowhere to paint its result.
        CancelKeybindCapture(restore: false);

        Grid? previous = _settingsLayer;
        if (previous is not null && previous.Parent is Panel owner) owner.Children.Remove(previous);
        _settingsLayer = null;
        _settingsNavEntries.Clear();
        _settingsPagesList.Clear();

        _settingsLayer = new Grid
        {
            Visibility = Visibility.Collapsed,
            Background = ClearBrush(),
            Padding = CompactDensity ? new Thickness(0) : new Thickness(6),
        };
        // A press on inert chrome (the card, the rail, the header, blank space) has to take
        // focus off the field that had it. Controls that own a press handle it themselves and
        // never bubble here; the presses that do reach this handler are the inert ones.
        _settingsLayer.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            // A press outside the waiting row abandons the capture instead of leaving the
            // next keystroke to change a shortcut the user has moved on from. The repaint
            // is deferred because this handler runs inside the press.
            DeferSettingsChange(() => CancelKeybindCapture());
            if (IsInertPress(args.OriginalSource, _settingsLayer)) FocusSettingsOwner();
        };

        // Stock inputs (text box, combo box, number box) draw their border from Fluent's fixed
        // greys and take the accent for the focused state; neither follows a Ghostty theme. Point
        // those resources at the page's own stroke and accent so every field on the page matches
        // the cards around it. The page root carries them, so each control inherits the set.
        Brush fieldStroke = PanelStrokeBrush();
        Brush fieldStrokeStrong = StrokeStrongBrush();
        Brush fieldAccent = AccentBrush();
        foreach (string key in new[]
                 {
                     "TextControlBorderBrush", "TextControlBorderBrushDisabled",
                     "ComboBoxBorderBrush", "ComboBoxBorderBrushDisabled",
                 })
        {
            _settingsLayer.Resources[key] = fieldStroke;
        }
        foreach (string key in new[]
                 {
                     "TextControlBorderBrushPointerOver", "ComboBoxBorderBrushPointerOver",
                     "ComboBoxBorderBrushPressed",
                 })
        {
            _settingsLayer.Resources[key] = fieldStrokeStrong;
        }
        foreach (string key in new[] { "TextControlBorderBrushFocused" })
        {
            _settingsLayer.Resources[key] = fieldAccent;
        }

        // The fill behind a field: flat on the card until the pointer is on it, then the same
        // subtle tint a row takes, so hovering no longer brings Fluent's white back.
        Brush fieldFill = ClearBrush();
        Brush fieldFillHover = ChipHoverBrush();
        foreach (string key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundFocused",
                     "TextControlBackgroundDisabled", "ComboBoxBackground",
                     "ComboBoxBackgroundDisabled",
                 })
        {
            _settingsLayer.Resources[key] = fieldFill;
        }
        foreach (string key in new[]
                 {
                     "TextControlBackgroundPointerOver", "ComboBoxBackgroundPointerOver",
                     "ComboBoxBackgroundPressed",
                 })
        {
            _settingsLayer.Resources[key] = fieldFillHover;
        }

        // Fluent's own text and surface brushes, for the controls the page still borrows from it
        // (the hyperlink, the field popups): take the theme's values so nothing stays neutral
        // black or white next to our tokens.
        _settingsLayer.Resources["TextFillColorPrimaryBrush"] = TextStrongBrush();
        _settingsLayer.Resources["TextFillColorSecondaryBrush"] = TextBodyBrush();
        _settingsLayer.Resources["TextFillColorTertiaryBrush"] = TextFaintBrush();
        _settingsLayer.Resources["CardBackgroundFillColorDefaultBrush"] = TermBgBrush();
        _settingsLayer.Resources["CardBackgroundFillColorSecondaryBrush"] = TermBgBrush();
        _settingsLayer.Resources["CardStrokeColorDefaultBrush"] = PanelStrokeBrush();
        _settingsLayer.Resources["LayerFillColorDefaultBrush"] = TermBgBrush();

        // The About card's repository link is a HyperlinkButton, which takes the system accent.
        Brush linkAccent = AccentBrush();
        _settingsLayer.Resources["HyperlinkButtonForeground"] = linkAccent;
        _settingsLayer.Resources["HyperlinkButtonForegroundPointerOver"] = linkAccent;
        _settingsLayer.Resources["HyperlinkButtonForegroundPressed"] = linkAccent;

        var card = new Border
        {
            CornerRadius = new CornerRadius(CompactDensity ? 0 : Theme.RadiusSurface),
            Background = TermBgBrush(),
            BorderBrush = PanelStrokeBrush(),
            BorderThickness = new Thickness(CompactDensity ? 0 : 1),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(168) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Child = columns;

        Border nav = BuildSettingsNav();
        Grid.SetColumn(nav, 0);
        columns.Children.Add(nav);

        Grid main = BuildSettingsMain();
        Grid.SetColumn(main, 1);
        columns.Children.Add(main);

        _settingsLayer.Children.Add(card);

        // Every focus move off a field has to land on something that can hold focus. A
        // ContentControl cannot: Focus() on it engages the first focusable control inside
        // it, which is how the command field picked up the focus when a click or a page
        // switch asked for the page owner. A zero-size, hit-test-transparent button is a
        // real focus owner without being a control anyone can click or see.
        _settingsFocus = new Button
        {
            Width = 1,
            Height = 1,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = ClearBrush(),
            Opacity = 0,
            IsTabStop = true,
            IsHitTestVisible = false,
            UseSystemFocusVisuals = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _settingsLayer.Children.Add(_settingsFocus);

        // Cover the whole window, header included: the page replaces the tab strip and the
        // panes instead of becoming a tab of its own. The header paints with z-index 1, so
        // the page has to sit above it.
        Canvas.SetZIndex(_settingsLayer, 2);
        Grid.SetRow(_settingsLayer, 0);
        Grid.SetRowSpan(_settingsLayer, Math.Max(1, _shell.RowDefinitions.Count));
        _shell.Children.Add(_settingsLayer);

        SelectSettingsPage(_settingsPage);
        if (_settingsActive)
        {
            _settingsLayer.Visibility = Visibility.Visible;
            // A chrome rebuild activates the pane afterwards; take the focus back once that
            // pass has run so typing stays on the settings page.
            FocusSettingsOwner();
        }
    }

    /// <summary>
    /// Hands keyboard focus to the page's own owner instead of the first field inside it. A
    /// page built this turn has no template yet and refuses focus, so the move is repeated
    /// once the dispatcher has run layout.
    /// </summary>
    private void FocusSettingsOwner()
    {
        _settingsFocus?.Focus(FocusState.Programmatic);
        _dispatcher?.TryEnqueue(() => _settingsFocus?.Focus(FocusState.Programmatic));
    }

    private Border BuildSettingsNav()
    {
        var nav = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = PanelStrokeBrush(),
            Padding = new Thickness(6, 8, 6, 8),
        };

        var panel = new StackPanel { Spacing = 2 };

        var backRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        backRow.Children.Add(Glyph('\uE72B', 14, SurfaceIconFaintBrush()));
        backRow.Children.Add(Label(Strings.SettingsBack, Theme.FontBody, TextBodyBrush()));

        // The page closes on release, not on press: collapsing it under the pointer would
        // leave the release to hit-test whatever the page covered. A button already works
        // that way, so Click carries the whole rule.
        var back = new ChromeButton(backRow, double.NaN, 32, new CornerRadius(Theme.RadiusField))
        {
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 0, 6),
            // The row owns the whole column, like every other row in the nav: a button
            // sized to its content would leave the hover fill hugging the label.
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        back.SetColors(Theme.PaneIconFaint, hoverFill: Theme.ChipHover);
        back.SetAccessibleName(Strings.SettingsBack);
        back.Click += (_, _) => HideSettings();
        _chromeButtons.Add(back);
        panel.Children.Add(back);

        panel.Children.Add(new TextBlock
        {
            Text = Strings.SettingsTitle,
            FontSize = Theme.FontBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextStrongBrush(),
            Margin = new Thickness(10, 2, 0, 8),
        });

        panel.Children.Add(SettingsNavItem("general", '\uE713', Strings.SettingsNavGeneral));
        panel.Children.Add(SettingsNavItem("appearance", '\uE790', Strings.SettingsNavAppearance));
        panel.Children.Add(SettingsNavItem("terminal", '\uE756', Strings.SettingsNavTerminal));
        panel.Children.Add(SettingsNavItem("notifications", '\uE7ED', Strings.SettingsNavNotifications));
        panel.Children.Add(SettingsNavItem("keys", '\uE765', Strings.SettingsNavKeys));
        panel.Children.Add(SettingsNavItem("about", '\uE946', Strings.SettingsNavAbout));

        var footRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        footRow.Children.Add(Glyph('\uE8E5', 14, SurfaceIconFaintBrush()));
        footRow.Children.Add(Label(Strings.SettingsOpenConfig, Theme.FontBody, TextBodyBrush()));

        var foot = new ChromeButton(footRow, double.NaN, 32, new CornerRadius(Theme.RadiusField))
        {
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        foot.SetColors(Theme.PaneIconFaint, hoverFill: Theme.ChipHover);
        foot.SetAccessibleName(Strings.SettingsOpenConfig);
        foot.Click += (_, _) => OpenConfig();
        _chromeButtons.Add(foot);

        // The foot sits at the bottom of the rail regardless of how many categories exist.
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(panel, 0);
        layout.Children.Add(panel);
        Grid.SetRow(foot, 1);
        layout.Children.Add(foot);
        nav.Child = layout;
        _settingsNav = nav;

        return nav;
    }

    private ChromeButton SettingsNavItem(string page, char glyph, string label)
    {
        // The bar keeps its slot whether or not this row is the active one: collapsing it
        // would pull the icon and label 12 DIP to the left and make every row jump sideways
        // as the selection moves.
        var accent = new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(Theme.RadiusPill),
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        FontIcon icon = Glyph(glyph, 14, SurfaceIconFaintBrush());
        TextBlock text = Label(label, Theme.FontBody, TextBodyBrush());
        text.VerticalAlignment = VerticalAlignment.Center;

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(accent);
        row.Children.Add(icon);
        row.Children.Add(text);

        var button = new ChromeButton(row, double.NaN, 32, new CornerRadius(Theme.RadiusField))
        {
            Padding = new Thickness(7, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        button.SetColors(Theme.PaneIconFaint, hoverFill: Theme.ChipHover);
        button.SetAccessibleName(label);
        button.Click += (_, _) =>
        {
            // A mouse press does not move focus on a chrome button, so hand it to the page
            // owner before the page being left collapses under the field that had it.
            _settingsFocus?.Focus(FocusState.Programmatic);
            SelectSettingsPage(page);
        };
        _chromeButtons.Add(button);

        _settingsNavEntries.Add(new SettingsNavEntry
        {
            Page = page,
            Label = label,
            Button = button,
            Text = text,
            Icon = icon,
            Accent = accent,
        });
        return button;
    }

    private Grid BuildSettingsMain()
    {
        var main = new Grid();
        AddAutoRow(main);
        AddStarRow(main);

        var head = new Border
        {
            Height = 44,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = PanelStrokeBrush(),
            // The window buttons own the card's top-right corner, so there is no right inset.
            Padding = new Thickness(16, 0, 0, 0),
        };

        _settingsTitle = new TextBlock
        {
            FontSize = Theme.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextStrongBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The page covers the window header, so it has to carry the three window buttons
        // itself or a settings-only window could not be minimized, maximized or closed.
        var headGrid = new Grid();
        AddStarColumn(headGrid);
        AddAutoColumn(headGrid);
        headGrid.Children.Add(_settingsTitle);

        // Same width and height as the caption buttons in the window header, and flush with
        // the card's top edge like them.
        double captionHeight = CaptionButtonHeight;
        var captions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
        };

        ChromeButton minimize = CaptionButton('\uE921', CaptionButtonWidth, captionHeight, Theme.ChromeIconBody);
        ChromeButton maximize = CaptionButton('\uE922', CaptionButtonWidth, captionHeight, Theme.ChromeIconBody);
        ChromeButton close = CaptionButton('\uE8BB', CaptionButtonWidth, captionHeight, Theme.ChromeIconBody);
        close.SetColors(Theme.ChromeIconBody, () => Colors.White, hoverFill: Theme.CloseHover);
        // Follow the card's rounded top-right corner, like the header's close button does.
        close.CornerRadius = new CornerRadius(0, CompactDensity ? 0 : Theme.RadiusSurface, 0, 0);

        _settingsCaptionButtons = new[] { minimize, maximize, close };
        _settingsMaximizeIcon = maximize.Content as FontIcon;
        UpdateMaximizeIcon();
        UpdateCaptionTips();

        // Win32 owns the pointer for these too (they are the same three non-client regions);
        // the handlers are the keyboard path.
        minimize.Click += (_, _) => MinimizeWindow();
        maximize.Click += (_, _) => ToggleMaximize();
        close.Click += (_, _) =>
        {
            if (_dispatcher is null) RequestClose();
            else _dispatcher.TryEnqueue(RequestClose);
        };

        captions.Children.Add(minimize);
        captions.Children.Add(maximize);
        captions.Children.Add(close);
        Grid.SetColumn(captions, 1);
        headGrid.Children.Add(captions);

        head.Child = headGrid;
        // The page's title band and nav column are the caption while it is open.
        _settingsHead = head;
        Grid.SetRow(head, 0);
        main.Children.Add(head);

        _settingsPages = new Grid();
        AddStarRow(_settingsPages);

        AddSettingsPage("general", BuildGeneralSettingsPage());
        AddSettingsPage("appearance", BuildAppearanceSettingsPage());
        AddSettingsPage("terminal", BuildTerminalSettingsPage());
        AddSettingsPage("notifications", BuildNotificationsSettingsPage());
        AddSettingsPage("keys", BuildKeysSettingsPage());
        AddSettingsPage("about", BuildAboutSettingsPage());

        Grid.SetRow(_settingsPages, 1);
        main.Children.Add(_settingsPages);
        return main;
    }

    private void AddSettingsPage(string page, FrameworkElement content)
    {
        // A tap on the page's inert chrome makes the scroller hand the keyboard to the first
        // field inside it, which is what dropped focus into the command box. Handle the press
        // here, below the scroller, before that happens. Controls that own the press handle it
        // first and never reach this handler.
        content.PointerPressed += (_, args) =>
        {
            if (!IsInertPress(args.OriginalSource, content)) return;
            FocusSettingsOwner();
            args.Handled = true;
        };

        var scroller = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Visibility = page == _settingsPage ? Visibility.Visible : Visibility.Collapsed,
        };
        _settingsPages!.Children.Add(scroller);
        _settingsPagesList.Add((page, scroller));

        // The page's own surface has to be hit-testable and at least viewport-tall, otherwise
        // a press on empty space falls through to the scroller, whose press handling gives
        // the keyboard to the first field instead of the page owner.
        scroller.SizeChanged += (_, _) => content.MinHeight = Math.Max(0, scroller.ViewportHeight);
    }

    private void SelectSettingsPage(string page)
    {
        _settingsPage = page;

        foreach (SettingsNavEntry entry in _settingsNavEntries)
        {
            bool active = entry.Page == page;
            entry.Button.SetActive(active);
            entry.Text.Foreground = active ? TextStrongBrush() : TextBodyBrush();
            entry.Text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            entry.Icon.Foreground = active ? AccentBrush() : SurfaceIconFaintBrush();
            entry.Accent.Background = active ? AccentBrush() : ClearBrush();
            if (active && _settingsTitle is not null) _settingsTitle.Text = entry.Label;
        }

        foreach ((string title, FrameworkElement view) in _settingsPagesList)
            view.Visibility = title == page ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Opens the settings page over the window body. Esc or the header's close button
    /// returns to the terminal.
    /// </summary>
    private async void ShowSettings()
    {
        if (_body is null) return;
        if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible)
            HideCommandPalette();

        // Font enumeration probes every installed face (~600 ms). BuildSettings reads the
        // list synchronously, so make sure it is ready on a background thread first — a
        // cold/expired cache must never block the UI here. Hot path is await-CompletedTask.
        try
        {
            await FontCache.EnsureMonospaceAsync(DefaultFontFamily);
        }
        catch (Exception error)
        {
            // A failed probe leaves the list empty; Settings still opens with the bundled font.
            Diag.LogException("Workspace.ShowSettings.fonts", error);
        }
        if (_body is null) return;

        // Rebuild on open so the controls start from the live config: the theme may have been
        // changed from the palette, and a reload may have rewritten other values.
        BuildSettings();
        ActivateSettingsTab();
    }

    /// <summary>
    /// True when a press landed on page chrome rather than on an interactive control: every
    /// element on the route from the hit element up to the boundary is a panel or a border.
    /// A press that a control owns either was marked handled or shows up here as a control.
    /// </summary>
    private static bool IsInertPress(object? source, DependencyObject boundary)
    {
        for (DependencyObject? node = source as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (node is Control) return false;
            if (ReferenceEquals(node, boundary)) return true;
        }
        return true;
    }

    private void HideSettings()
    {
        CancelKeybindCapture(restore: false);
        DeactivateSettings();
        if (_tabs.Count > 0) FocusKeyboardAnchor();
    }

    private void ActivateSettingsTab()
    {
        if (_settingsLayer is null) return;
        _settingsActive = true;
        _settingsLayer.Visibility = Visibility.Visible;
        UpdateHeaders();
        // The page takes over the caption: its nav column and title band drag the window and
        // its controls stay clickable.
        InvalidateNonClientRegions();
        FocusSettingsOwner();
    }

    private void DeactivateSettings()
    {
        _settingsActive = false;
        if (_settingsLayer is not null) _settingsLayer.Visibility = Visibility.Collapsed;
        InvalidateNonClientRegions();
    }

    private bool OnSettingsKey(KeyRoutedEventArgs args)
    {
        // Esc stays inside the page: it closes an open drop-down, it never leaves settings.
        // Keys pressed inside a setting belong to that control (text box, combo box, slider);
        // with any other focus the page swallows them so nothing leaks to the shell.
        return !IsSettingsFocused();
    }

    private bool IsSettingsFocused()
    {
        if (_root.XamlRoot is null || _settingsLayer is null) return false;
        object? focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        for (DependencyObject? node = focused as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _settingsLayer)) return true;
        }
        return false;
    }

    // ---------------- general ----------------

    private FrameworkElement BuildGeneralSettingsPage()
    {
        var page = SettingsPageStack();

        int confirmCloseIndex = _config.ConfirmCloseSurface switch
        {
            "always" => 1,
            "false" => 2,
            _ => 0,
        };
        FrameworkElement confirmClose = SettingsSegmented(
            new[]
            {
                Strings.SettingsConfirmCloseRunning, Strings.SettingsConfirmCloseAlways,
                Strings.SettingsConfirmCloseNever,
            },
            confirmCloseIndex, ApplyConfirmCloseSurfaceChoice);

        page.Children.Add(SettingsGroup(Strings.SettingsWindow, SettingsCard(
            SettingsRow(Strings.SettingsWindowSaveState, null,
                SettingsSwitch(_config.WindowSaveState != "never", ApplyWindowSaveStateChoice)),
            SettingsRow(Strings.SettingsConfirmClose, null,
                confirmClose))));

        page.Children.Add(SettingsGroup(Strings.SettingsUpdates, SettingsCard(
            SettingsRow(Strings.SettingsAutoUpdate, null,
                SettingsSwitch(_config.AutoUpdate != "off", ApplyAutoUpdateChoice)))));

        return page;
    }

    // ---------------- appearance ----------------

    private FrameworkElement BuildAppearanceSettingsPage()
    {
        var page = SettingsPageStack();

        (int Index, string Value)[] languages = { (0, "en"), (1, "zh-CN"), (2, string.Empty) };
        // An unset "language" follows the system language; the selector shows that state
        // instead of pretending the resolved language was configured explicitly.
        bool languageConfigured = _config.Values.ContainsKey("language");
        int languageIndex = !languageConfigured ? 2 : _config.Language == Language.Chinese ? 1 : 0;
        ComboBox language = SettingsSelect(
            new[] { "English", "简体中文", Strings.FollowSystem }, languageIndex, 150,
            index => ApplyLanguageChoice(languages[Math.Clamp(index, 0, 2)].Value));

        int windowThemeIndex = _config.WindowThemeMode switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        FrameworkElement windowTheme = SettingsSegmented(
            new[] { Strings.FollowSystem, Strings.Light, Strings.Dark }, windowThemeIndex,
            index => ApplyWindowThemeChoice(index switch
            {
                1 => "light",
                2 => "dark",
                _ => "system",
            }));

        int placementIndex = _placement switch
        {
            TabPlacement.Left => 1,
            TabPlacement.Right => 2,
            _ => 0,
        };
        FrameworkElement placement = SettingsSegmented(
            new[] { Strings.SettingsTabTop, Strings.SettingsTabLeft, Strings.SettingsTabRight },
            placementIndex, index => DeferSettingsChange(() => SetTabPlacement(index switch
            {
                1 => "left",
                2 => "right",
                _ => "top",
            })));

        page.Children.Add(SettingsGroup(null, SettingsCard(
            SettingsRow(Strings.SettingsLanguage, null, language),
            SettingsRow(Strings.SettingsWindowAppearance, null, windowTheme),
            SettingsRow(Strings.SettingsTabPosition, null, placement),
            SettingsRow(Strings.SettingsCompact, Strings.SettingsCompactDescription,
                SettingsSwitch(_config.Compact, ApplyCompactChoice)))));

        List<(string Name, bool Light)> available = TerminalThemes.Available();
        List<string> lightThemes = available.Where(t => t.Light).Select(t => t.Name).ToList();
        List<string> darkThemes = available.Where(t => !t.Light).Select(t => t.Name).ToList();
        // The built-in design themes are not files in Assets\Themes, so they have to stay in
        // the list explicitly. Otherwise picking a bundled theme makes them unreachable.
        InsertDefaultTheme(lightThemes, Config.LightThemeName);
        InsertDefaultTheme(darkThemes, Config.DarkThemeName);
        EnsureCurrentTheme(lightThemes, _config.ThemeLightName);
        EnsureCurrentTheme(darkThemes, _config.ThemeDarkName);

        AutoSuggestBox lightBox = SettingsThemeSearch(
            lightThemes, _config.ThemeLightName, 220, name => ApplyThemeChoice(name, true));
        AutoSuggestBox darkBox = SettingsThemeSearch(
            darkThemes, _config.ThemeDarkName, 220, name => ApplyThemeChoice(name, false));

        page.Children.Add(SettingsGroup(Strings.SettingsThemes, SettingsCard(
            SettingsRow(Strings.SettingsLightTheme, null, lightBox),
            SettingsRow(Strings.SettingsDarkTheme, null, darkBox))));

        FrameworkElement opacityRow = SettingsPercentSlider(
            Math.Clamp(_config.BackgroundOpacity * 100.0, 0.0, 100.0),
            percent => ApplyBackgroundOpacityChoice(percent / 100.0));

        string[] positionValues =
        {
            "top-left", "top-center", "top-right",
            "center-left", "center", "center-right",
            "bottom-left", "bottom-center", "bottom-right",
        };
        string[] positionLabels =
        {
            Strings.SettingsBackgroundImagePositionTopLeft, Strings.SettingsBackgroundImagePositionTop,
            Strings.SettingsBackgroundImagePositionTopRight, Strings.SettingsBackgroundImagePositionLeft,
            Strings.SettingsBackgroundImagePositionCenter, Strings.SettingsBackgroundImagePositionRight,
            Strings.SettingsBackgroundImagePositionBottomLeft, Strings.SettingsBackgroundImagePositionBottom,
            Strings.SettingsBackgroundImagePositionBottomRight,
        };
        int positionIndex = Array.IndexOf(positionValues, _config.BackgroundImagePosition);
        if (positionIndex < 0) positionIndex = 4;
        ComboBox imagePosition = SettingsSelect(
            positionLabels, positionIndex, 150,
            index => ApplyBackgroundImagePositionChoice(positionValues[index]));

        string[] fitValues = { "contain", "cover", "stretch", "none" };
        string[] fitLabels =
        {
            Strings.SettingsBackgroundImageFitContain, Strings.SettingsBackgroundImageFitCover,
            Strings.SettingsBackgroundImageFitStretch, Strings.SettingsBackgroundImageFitNone,
        };
        int fitIndex = Array.IndexOf(fitValues, _config.BackgroundImageFit);
        if (fitIndex < 0) fitIndex = 0;
        ComboBox imageFit = SettingsSelect(
            fitLabels, fitIndex, 150,
            index => ApplyBackgroundImageFitChoice(fitValues[index]));

        FrameworkElement imageOpacityRow = SettingsPercentSlider(
            Math.Clamp(_config.BackgroundImageOpacity * 100.0, 0.0, 100.0),
            percent => ApplyBackgroundImageOpacityChoice(percent / 100.0));

        // The image options only mean anything with a picture chosen, so they ride along with
        // the path row and come and go with it. The block carries its own top separator; the
        // card's separator before it belongs to the path row and stays whether it is shown.
        var imageOptions = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = PanelStrokeBrush(),
            Visibility = _config.BackgroundImage.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            Child = SettingsRowStack(
                SettingsRow(Strings.SettingsBackgroundImageOpacity, null, imageOpacityRow),
                SettingsRow(Strings.SettingsBackgroundImagePosition, null, imagePosition),
                SettingsRow(Strings.SettingsBackgroundImageFit, null, imageFit),
                SettingsRow(Strings.SettingsBackgroundImageRepeat, null,
                    SettingsSwitch(_config.BackgroundImageRepeat, ApplyBackgroundImageRepeatChoice))),
        };

        var imageSection = new StackPanel();
        imageSection.Children.Add(SettingsRow(Strings.SettingsBackgroundImage, null,
            SettingsPathChoice(_config.BackgroundImage, PickBackgroundImageAsync, path =>
            {
                ApplyBackgroundImageChoice(path);
                imageOptions.Visibility = path.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            })));
        imageSection.Children.Add(imageOptions);

        page.Children.Add(SettingsGroup(Strings.SettingsBackground, SettingsCard(
            SettingsRow(Strings.SettingsBackgroundOpacity, null, opacityRow),
            SettingsRow(Strings.SettingsBackgroundBlur, null,
                SettingsSwitch(_config.BackgroundBlur, ApplyBackgroundBlurChoice)),
            imageSection)));

        return page;
    }

    private static void EnsureCurrentTheme(List<string> themes, string current)
    {
        if (current.Length > 0 && !themes.Contains(current)) themes.Insert(0, current);
    }

    private static void InsertDefaultTheme(List<string> themes, string name)
    {
        if (name.Length > 0 && !themes.Contains(name)) themes.Insert(0, name);
    }

    /// <summary>
    /// A theme picker that filters as the user types: 200+ light and 600+ dark themes are too
    /// many for a plain drop-down. Committing a suggestion applies that theme immediately.
    /// </summary>
    private AutoSuggestBox SettingsThemeSearch(
        IReadOnlyList<string> themes, string current, double width, Action<string> changed)
    {
        string selected = current;
        var box = new AutoSuggestBox
        {
            Width = width,
            MinHeight = 32,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            Background = ClearBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            Text = selected,
            PlaceholderText = Strings.SettingsSearchThemes,
            VerticalAlignment = VerticalAlignment.Center,
        };
        CenterSettingsValue(box);

        void Commit(string name)
        {
            if (name.Length == 0) return;
            box.Text = name;
            if (name == selected) return;
            selected = name;
            changed(name);
        }

        box.TextChanged += (_, args) =>
        {
            if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            string query = box.Text.Trim();
            if (query.Length == 0)
            {
                box.ItemsSource = themes;
                box.IsSuggestionListOpen = false;
                return;
            }

            var matches = new List<string>();
            foreach (string name in themes)
            {
                if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(name);
                    if (matches.Count >= 40) break;
                }
            }
            box.ItemsSource = matches;
            box.IsSuggestionListOpen = matches.Count > 0;
        };

        box.SuggestionChosen += (_, args) =>
        {
            if (args.SelectedItem is string name) Commit(name);
        };

        box.QuerySubmitted += (_, args) =>
        {
            string query = (args.QueryText ?? string.Empty).Trim();
            string? match = null;
            foreach (string name in themes)
            {
                if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
                {
                    match = name;
                    break;
                }
            }
            if (match is null)
            {
                foreach (string name in themes)
                {
                    if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        match = name;
                        break;
                    }
                }
            }
            if (match is null) box.Text = selected;
            else Commit(match);
        };

        // Leaving with a half-typed query restores the theme that is actually configured.
        box.LostFocus += (_, _) =>
        {
            if (!box.Text.Equals(selected, StringComparison.OrdinalIgnoreCase)) box.Text = selected;
            // The drop-down does not follow a focus change by itself; close it on the next
            // turn so a suggestion click raised in this one still commits first.
            _dispatcher?.TryEnqueue(() => box.IsSuggestionListOpen = false);
        };
        return box;
    }

    // ---------------- terminal ----------------

    private FrameworkElement BuildTerminalSettingsPage()
    {
        var page = SettingsPageStack();

        // Show what the config file holds, not the resolved value: an absent
        // "working-directory" resolves to %USERPROFILE%, and inherited directories only live
        // in memory, so both would fill a field the user never set.
        Config stored = Config.Load();

        // A missing key is written straight away as "no-cursor", not just shown in the field:
        // the prompt then keeps the configured cursor shape instead of switching to a bar
        // while the shell integration is active, and the page shows what is really in the file.
        string storedShellFeatures = StoredValue(stored, "shell-integration-features");
        if (storedShellFeatures.Length == 0)
        {
            storedShellFeatures = "no-cursor";
            Config.SaveValue("shell-integration-features", storedShellFeatures);
            _config.ShellFeatures = Config.ParseShellFeatures(storedShellFeatures);
        }

        page.Children.Add(SettingsGroup(Strings.SettingsStartup, SettingsCard(
            SettingsRow(Strings.SettingsCommand, null,
                SettingsText(StoredValue(stored, "command"), Strings.SettingsCommandAuto, 260, value =>
                {
                    _config.Command = value;
                    Config.SaveValue("command", value);
                })),
            SettingsRow(Strings.SettingsWorkingDirectory, null,
                SettingsText(StoredValue(stored, "working-directory"), "%USERPROFILE%", 260, value =>
                {
                    _config.WorkingDirectory = value;
                    Config.SaveValue("working-directory", value);
                })),
            SettingsRow(Strings.SettingsShellFeatures, null,
                SettingsText(storedShellFeatures, "no-cursor", 260, ApplyShellFeatureList)),
            SettingsRow(Strings.SettingsNewTabPosition, null,
                SettingsSegmented(
                    new[] { Strings.SettingsNewTabPositionCurrent, Strings.SettingsNewTabPositionEnd },
                    _config.NewTabPosition == "end" ? 1 : 0,
                    index => ApplyNewTabPositionChoice(index == 1 ? "end" : "current"))),
            SettingsRow(Strings.SettingsTabInheritWorkingDirectory, null,
                SettingsSwitch(_config.TabInheritWorkingDirectory, ApplyTabInheritWorkingDirectoryChoice)),
            SettingsRow(Strings.SettingsSplitInheritWorkingDirectory, null,
                SettingsSwitch(_config.SplitInheritWorkingDirectory, ApplySplitInheritWorkingDirectoryChoice)))));

        IReadOnlyList<string> families = MonospaceFontFamilies();
        ComboBox fontFamilies = SettingsEditableSelect(
            families,
            _config.FontFamily.Length > 0 ? _config.FontFamily : DefaultFontFamily,
            240,
            ApplyFontFamilyChoice);

        page.Children.Add(SettingsGroup(Strings.SettingsFont, SettingsCard(
            SettingsRow(Strings.SettingsFontFamily, null, fontFamilies),
            SettingsRow(Strings.SettingsFontSize, null,
                SettingsNumber(_config.FontSize, Config.MinFontSizePoints, Config.MaxFontSizePoints,
                    ApplyFontSizeChoice)),
            SettingsRow(Strings.SettingsFontFeatures, null,
                SettingsText(StoredValue(stored, "font-feature"), "ss01, -calt", 260,
                    ApplyFontFeatureList)))));

        int cursorIndex = _config.CursorStyle switch
        {
            "block_hollow" => 1,
            "bar" => 2,
            "underline" => 3,
            _ => 0,
        };
        FrameworkElement cursorShape = SettingsSegmented(
            new[]
            {
                Strings.SettingsCursorBlock, Strings.SettingsCursorBlockHollow,
                Strings.SettingsCursorBar, Strings.SettingsCursorUnderline,
            },
            cursorIndex, index => ApplyCursorStyle(index switch
            {
                1 => "block_hollow",
                2 => "bar",
                3 => "underline",
                _ => "block",
            }));

        page.Children.Add(SettingsGroup(Strings.SettingsCursor, SettingsCard(
            SettingsRow(Strings.SettingsCursorShape, null, cursorShape),
            SettingsRow(Strings.SettingsCursorBlink, null,
                SettingsSwitch(_config.CursorBlink != false, ApplyCursorBlinkChoice)))));

        string[] rightClickActions =
        {
            Strings.SettingsRightClickContextMenu, Strings.SettingsRightClickPaste,
            Strings.SettingsRightClickCopy, Strings.SettingsRightClickCopyOrPaste,
            Strings.SettingsRightClickIgnore,
        };
        int rightClickIndex = _config.RightClickAction switch
        {
            "paste" => 1,
            "copy" => 2,
            "copy-or-paste" => 3,
            "ignore" => 4,
            _ => 0,
        };

        page.Children.Add(SettingsGroup(Strings.SettingsSelection, SettingsCard(
            SettingsRow(Strings.SettingsCopyOnSelect, null,
                SettingsSwitch(_config.CopyOnSelect, ApplyCopyOnSelectChoice)),
            SettingsRow(Strings.SettingsRightClickAction, null,
                SettingsSelect(rightClickActions, rightClickIndex, 180, ApplyRightClickActionChoice)))));

        page.Children.Add(SettingsGroup(Strings.SettingsScrolling, SettingsCard(
            SettingsRow(Strings.SettingsScrollbar, null,
                SettingsSegmented(
                    new[] { Strings.SettingsScrollbarSystem, Strings.SettingsScrollbarNever },
                    _config.Scrollbar == "never" ? 1 : 0,
                    index => ApplyScrollbarChoice(index == 1 ? "never" : "system"))))));

        // "custom-shader" is repeatable, so the chain is one row per shader plus an add row;
        // the animation switch is the same one Ghostty puts beside it.
        var shaderRows = new List<FrameworkElement>();
        IReadOnlyList<string> shaderPaths = _config.CustomShaders;
        for (int i = 0; i < shaderPaths.Count; i++)
        {
            int index = i;
            shaderRows.Add(SettingsRow(Strings.SettingsCustomShader, null,
                SettingsPathChoice(shaderPaths[i], PickShaderAsync,
                    picked => ApplyCustomShader(index, picked))));
        }
        shaderRows.Add(SettingsRow(Strings.SettingsAddShader, null,
            SettingsTextButton(Strings.SettingsBrowse, async () =>
            {
                string? picked = await PickShaderAsync();
                if (!string.IsNullOrEmpty(picked)) ApplyCustomShader(-1, picked!);
            })));
        shaderRows.Add(SettingsRow(Strings.SettingsShaderAnimation, null,
            SettingsSwitch(_config.CustomShaderAnimation != "false", ApplyShaderAnimationChoice)));

        page.Children.Add(SettingsGroup(Strings.SettingsShaders, SettingsCard(shaderRows.ToArray())));

        return page;
    }

    // ---------------- notifications ----------------

    private FrameworkElement BuildNotificationsSettingsPage()
    {
        var page = SettingsPageStack();

        Config stored = Config.Load();

        int mode = _config.NotifyOnCommandFinish switch
        {
            "always" => 2,
            "never" => 0,
            _ => 1,
        };
        FrameworkElement when = SettingsSegmented(
            new[] { Strings.SettingsNotifyNever, Strings.SettingsNotifyUnfocused, Strings.SettingsNotifyAlways },
            mode, index => ApplyNotifyMode(index switch
            {
                2 => "always",
                0 => "never",
                _ => "unfocused",
            }));

        TextBox after = SettingsText(
            StoredValue(stored, "notify-on-command-finish-after"), "5s", 100,
            ApplyNotifyAfterChoice);

        page.Children.Add(SettingsGroup(Strings.SettingsNotifyCommand, SettingsCard(
            SettingsRow(Strings.SettingsNotifyOnFinish, Strings.SettingsNotifyOnFinishDescription, when),
            SettingsRow(Strings.SettingsNotifyAfter, null, after))));

        page.Children.Add(SettingsGroup(Strings.SettingsNotifyAction, SettingsCard(
            SettingsRow(Strings.SettingsNotifyBell, null,
                SettingsSwitch(_config.NotifyOnCommandFinishBell, on =>
                {
                    _config.NotifyOnCommandFinishBell = on;
                    SaveNotifyAction();
                })),
            SettingsRow(Strings.SettingsNotifyToast, null,
                SettingsSwitch(_config.NotifyOnCommandFinishToast, on =>
                {
                    _config.NotifyOnCommandFinishToast = on;
                    SaveNotifyAction();
                })))));

        page.Children.Add(SettingsGroup(Strings.SettingsNotifyProgram, SettingsCard(
            SettingsRow(Strings.SettingsNotifyProgramBlock, Strings.SettingsNotifyProgramDescription,
                SettingsSwitch(_config.NotifyOnProgramBlock, on =>
                {
                    _config.NotifyOnProgramBlock = on;
                    SaveProgramStatusNotify();
                })),
            SettingsRow(Strings.SettingsNotifyProgramDone, null,
                SettingsSwitch(_config.NotifyOnProgramDone, on =>
                {
                    _config.NotifyOnProgramDone = on;
                    SaveProgramStatusNotify();
                })),
            SettingsRow(Strings.SettingsNotifyProgramFail, null,
                SettingsSwitch(_config.NotifyOnProgramFail, on =>
                {
                    _config.NotifyOnProgramFail = on;
                    SaveProgramStatusNotify();
                })))));

        page.Children.Add(SettingsGroup(Strings.SettingsDesktopNotifications, SettingsCard(
            SettingsRow(Strings.SettingsDesktopNotificationsAllow,
                Strings.SettingsDesktopNotificationsDescription,
                SettingsSwitch(_config.DesktopNotifications, ApplyDesktopNotificationsChoice)))));

        // The path and volume are only meaningful with "bell-features = audio", so the whole
        // group follows that switch (the switch flips its Visibility below). Declared first so
        // the audio switch's handler can capture it.
        FrameworkElement bellAudioGroup = SettingsGroup(Strings.SettingsBellAudioGroup, SettingsCard(
            SettingsRow(Strings.SettingsBellAudioPath, null,
                SettingsPathChoice(_config.BellAudioPath, PickBellAudioAsync, ApplyBellAudioPathChoice)),
            SettingsRow(Strings.SettingsBellVolume, null,
                SettingsUnit(SettingsNumber(
                    Math.Round(_config.BellAudioVolume * 100), 0, 100, ApplyBellVolumeChoice), "%"))));
        bellAudioGroup.Visibility = _config.BellAudio ? Visibility.Visible : Visibility.Collapsed;

        page.Children.Add(SettingsGroup(Strings.SettingsBell, SettingsCard(
            SettingsRow(Strings.SettingsBellSystem, null,
                BellFeatureSwitch(_config.BellSystem, on => _config.BellSystem = on)),
            SettingsRow(Strings.SettingsBellAudio, null,
                BellFeatureSwitch(_config.BellAudio, on =>
                {
                    _config.BellAudio = on;
                    bellAudioGroup.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                })),
            SettingsRow(Strings.SettingsBellAttention, null,
                BellFeatureSwitch(_config.BellAttention, on => _config.BellAttention = on)),
            SettingsRow(Strings.SettingsBellTitle, null,
                BellFeatureSwitch(_config.BellTitle, on => _config.BellTitle = on)),
            SettingsRow(Strings.SettingsBellBorder, null,
                BellFeatureSwitch(_config.BellBorder, on => _config.BellBorder = on)))));

        page.Children.Add(bellAudioGroup);

        return page;
    }

    private FrameworkElement BellFeatureSwitch(bool value, Action<bool> set) =>
        SettingsSwitch(value, on =>
        {
            set(on);
            SaveBellFeatures();
        });

    /// <summary>
    /// A file path with Browse/Remove, shared by the background image and bell audio rows.
    /// The path is a value preview, so it takes the small token and one width on both rows;
    /// clicking it selects the file in Explorer.
    /// </summary>
    private FrameworkElement SettingsPathChoice(string path, Func<Task<string?>> pick, Action<string> apply)
    {
        string current = path;
        TextBlock label = Label(path.Length > 0 ? path : "—", Theme.FontSmall, TextFaintBrush());
        label.MaxWidth = 150;

        Brush faint = TextFaintBrush();
        Brush body = TextBodyBrush();
        var link = new HyperlinkButton
        {
            Content = label,
            // A link-sized hit target, not the 32 DIP field height the Fluent button keeps
            // around its text: the row already carries that height.
            MinHeight = 32,
            Height = 32,
            MinWidth = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = ClearBrush(),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = path.Length > 0,
        };
        // The path reads as quiet metadata, not an accent link; the same faint colour in
        // every state keeps the template's accent off it, and hover only lifts the text.
        foreach (string key in new[]
                 {
                     "HyperlinkButtonForeground", "HyperlinkButtonForegroundPointerOver",
                     "HyperlinkButtonForegroundPressed", "HyperlinkButtonForegroundDisabled",
                     "HyperlinkButtonBorderBrush", "HyperlinkButtonBorderBrushPointerOver",
                     "HyperlinkButtonBorderBrushPressed", "HyperlinkButtonBorderBrushDisabled",
                 })
        {
            link.Resources[key] = key.Contains("Border") ? ClearBrush() : faint;
        }
        foreach (string key in new[]
                 {
                     "HyperlinkButtonBackground", "HyperlinkButtonBackgroundPointerOver",
                     "HyperlinkButtonBackgroundPressed", "HyperlinkButtonBackgroundDisabled",
                 })
        {
            link.Resources[key] = ClearBrush();
        }
        link.PointerEntered += (_, _) =>
        {
            if (current.Length > 0) label.Foreground = body;
        };
        link.PointerExited += (_, _) => label.Foreground = faint;
        link.Click += (_, _) =>
        {
            if (current.Length > 0) RevealPath(current);
        };

        Button remove = null!;
        remove = SettingsTextButton(Strings.SettingsRemove, () =>
        {
            current = string.Empty;
            label.Text = "—";
            label.Foreground = faint;
            link.IsEnabled = false;
            apply(string.Empty);
            // The path is gone, so this action has nothing left to remove.
            remove.Visibility = Visibility.Collapsed;
        });
        remove.Visibility = path.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Button browse = SettingsTextButton(Strings.SettingsBrowse, async () =>
        {
            string? picked = await pick();
            if (picked is null) return;
            current = picked;
            label.Text = picked;
            link.IsEnabled = true;
            remove.Visibility = Visibility.Visible;
            apply(picked);
        });

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(link);
        row.Children.Add(browse);
        row.Children.Add(remove);
        return row;
    }

    /// <summary>Selects the file in Explorer, or opens its folder when the file is gone.</summary>
    private static void RevealPath(string path)
    {
        try
        {
            // Explorer only accepts /select when the quote starts after the comma, which is
            // why the command line is spelled out instead of using ArgumentList.
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = true,
                    Arguments = "/select,\"" + path.Replace("\"", string.Empty) + "\"",
                });
                return;
            }
            string? folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true,
                Arguments = "\"" + folder.Replace("\"", string.Empty) + "\"",
            });
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.RevealPath", error);
        }
    }

    private async System.Threading.Tasks.Task<string?> PickBellAudioAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary,
            };
            picker.FileTypeFilter.Add(".wav");
            picker.FileTypeFilter.Add(".mp3");
            picker.FileTypeFilter.Add(".m4a");
            picker.FileTypeFilter.Add(".ogg");
            picker.FileTypeFilter.Add(".flac");
            if (_hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.PickBellAudio", error);
            return null;
        }
    }

    private void ApplyBellAudioPathChoice(string path)
    {
        _config.BellAudioPath = path;
        Config.SaveValue("bell-audio-path", path);
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.SyncConfig(_config));
    }

    private void ApplyNotifyMode(string mode)
    {
        _config.NotifyOnCommandFinish = mode;
        Config.SaveValue("notify-on-command-finish", mode);
    }

    /// <summary>
    /// "notify-on-command-finish-action" is a list with "no-" negations. Both tokens are
    /// written, otherwise the omitted one keeps its default and a switch would lie.
    /// </summary>
    private void SaveNotifyAction()
    {
        string action = (_config.NotifyOnCommandFinishBell ? "bell" : "no-bell") + "," +
                        (_config.NotifyOnCommandFinishToast ? "notify" : "no-notify");
        Config.SaveValue("notify-on-command-finish-action", action);
    }

    /// <summary>
    /// "notify-on-program-status" is a list with "no-" negations. Every token is written,
    /// otherwise the omitted one keeps its default and a switch would lie.
    /// </summary>
    private void SaveProgramStatusNotify()
    {
        string action = (_config.NotifyOnProgramBlock ? "block" : "no-block") + "," +
                        (_config.NotifyOnProgramDone ? "done" : "no-done") + "," +
                        (_config.NotifyOnProgramFail ? "fail" : "no-fail");
        Config.SaveValue("notify-on-program-status", action);
    }

    private void ApplyNotifyAfterChoice(string value)
    {
        Config.SaveValue("notify-on-command-finish-after", value);
        _config.NotifyOnCommandFinishAfter =
            Config.ParseDuration(value, TimeSpan.FromSeconds(5));
    }

    private void ApplyDesktopNotificationsChoice(bool enabled)
    {
        _config.DesktopNotifications = enabled;
        Config.SaveValue("desktop-notifications", enabled ? "true" : "false");
    }

    /// <summary>
    /// "bell-features" starts from Ghostty's defaults and only names what changes, so every
    /// token is written: the checkboxes then describe the whole state instead of a delta.
    /// </summary>
    private void SaveBellFeatures()
    {
        var parts = new List<string>(5)
        {
            _config.BellSystem ? "system" : "no-system",
            _config.BellAudio ? "audio" : "no-audio",
            _config.BellAttention ? "attention" : "no-attention",
            _config.BellTitle ? "title" : "no-title",
            _config.BellBorder ? "border" : "no-border",
        };
        Config.SaveValue("bell-features", string.Join(",", parts));
    }

    private void ApplyBellVolumeChoice(double percent)
    {
        float volume = Math.Clamp((float)(percent / 100.0), 0f, 1f);
        _config.BellAudioVolume = volume;
        Config.SaveValue("bell-audio-volume",
            volume.ToString("0.##", CultureInfo.InvariantCulture));
    }

    // ---------------- key bindings ----------------

    /// <summary>The action whose shortcut cap is waiting for a key press, if any.</summary>
    private string? _keybindCaptureAction;

    /// <summary>The clicked chord, or null when the press adds one to an empty action.</summary>
    private string? _keybindCaptureChord;

    /// <summary>The row that owns the field being edited, so a cancel can repaint it.</summary>
    private StackPanel? _keybindCaptureKeys;

    /// <summary>The read-only field swapped in for the clicked shortcut while editing.</summary>
    private TextBox? _keybindCaptureBox;

    /// <summary>The "+" cap swapped out of its slot while a chord is being added.</summary>
    private Button? _keybindCaptureAdd;

    /// <summary>The slot that holds the "+" cap and the field replacing it.</summary>
    private Border? _keybindCaptureAddArea;

    /// <summary>Re-runs the keys page search, because an edit can change which rows match.</summary>
    private Action? _keybindFilter;

    private FrameworkElement BuildKeysSettingsPage()
    {
        var page = SettingsPageStack();
        page.Children.Add(Label(Strings.SettingsKeysHint, Theme.FontCaption, TextFaintBrush()));

        var search = new TextBox
        {
            PlaceholderText = Strings.SettingsKeysSearch,
            Height = 32,
            MinHeight = 32,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            Background = ClearBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        page.Children.Add(search);

        var list = new StackPanel { Spacing = 16 };
        page.Children.Add(list);

        TextBlock noMatch = Label(Strings.SettingsKeysNoMatch, Theme.FontBody, TextFaintBrush());
        noMatch.Visibility = Visibility.Collapsed;
        page.Children.Add(noMatch);

        Dictionary<string, string> labels = KeybindActionLabels();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var groups = new List<(FrameworkElement View, List<(FrameworkElement Row, Func<string> Text)> Rows)>();

        // Every action this build can run, grouped the way the commands are used. The action
        // ids are the ones a "keybind" line names; the labels and the chords both come from
        // the live configuration, so the page always shows what is actually bound.
        (string Title, string[] Actions)[] definitions =
        {
            (Strings.SettingsKeysGroupTabs, new[]
            {
                "new_tab", "next_tab", "previous_tab", "last_tab", "move_tab:-1", "move_tab:1",
                "move_tab_to_new_window",
                "goto_tab:1", "goto_tab:2", "goto_tab:3", "goto_tab:4",
                "goto_tab:5", "goto_tab:6", "goto_tab:7", "goto_tab:8",
                "close_tab:this", "close_tab:other", "close_tab:right",
            }),
            (Strings.SettingsKeysGroupPanes, new[]
            {
                "new_split:right", "new_split:down", "new_split:left", "new_split:up",
                "goto_split:previous", "goto_split:next",
                "goto_split:left", "goto_split:right", "goto_split:up", "goto_split:down",
                "resize_split:left", "resize_split:right", "resize_split:up", "resize_split:down",
                "toggle_split_zoom", "toggle_readonly", "equalize_splits", "close_surface",
            }),
            (Strings.SettingsKeysGroupTerminal, new[]
            {
                "increase_font_size:1", "decrease_font_size:1", "reset_font_size",
                "scroll_to_top", "scroll_to_bottom", "scroll_to_selection",
                "scroll_page_up", "scroll_page_down",
                "jump_to_prompt:-1", "jump_to_prompt:1",
                "select_all",
                "adjust_selection:left", "adjust_selection:right",
                "adjust_selection:up", "adjust_selection:down",
                "clear_screen", "reset",
            }),
            (Strings.SettingsKeysGroupClipboard, new[]
            {
                "copy_to_clipboard", "copy_to_clipboard:plain", "copy_to_clipboard:vt",
                "copy_to_clipboard:html", "copy_url_to_clipboard", "copy_title_to_clipboard",
                "paste_from_clipboard", "paste_from_selection",
                "write_screen_file:copy", "write_screen_file:paste", "write_screen_file:open",
            }),
            (Strings.SettingsKeysGroupSearch, new[]
            {
                "start_search", "search_selection", "end_search",
                "navigate_search:next", "navigate_search:previous",
            }),
            (Strings.SettingsKeysGroupWindow, new[]
            {
                "new_window", "toggle_sidebar", "toggle_maximize", "toggle_fullscreen",
                "toggle_window_float_on_top", "reset_window_size", "show_on_screen_keyboard",
                "toggle_mouse_reporting", "toggle_background_opacity",
                "prompt_surface_title", "prompt_tab_title", "prompt_window_title",
                "close_window", "close_all_windows",
            }),
            (Strings.SettingsKeysGroupApp, new[]
            {
                "toggle_command_palette", "open_config", "open_config:new_window",
                "open_config_file", "reload_config", "quit",
            }),
        };

        foreach ((string title, string[] actions) in definitions)
        {
            var rows = new List<(FrameworkElement Row, Func<string> Text)>();
            var elements = new List<FrameworkElement>();
            foreach (string action in actions)
            {
                listed.Add(action);
                FrameworkElement row = KeybindRow(KeybindLabel(labels, action), action);
                elements.Add(row);
                // The chords are read when the query runs, so editing a shortcut is matched
                // by its new keys without rebuilding the page.
                rows.Add((row, () => KeybindSearchText(labels, action)));
            }
            FrameworkElement group = SettingsGroup(title, SettingsCard(elements.ToArray()));
            list.Children.Add(group);
            groups.Add((group, rows));
        }

        // Safety net: a bindable action the groups above forgot still shows up instead of
        // disappearing from the page. Only bound ones are added, so the section stays empty
        // in the normal case.
        var extraActions = new List<string>();
        foreach (KeyValuePair<string, string> entry in labels)
        {
            if (listed.Contains(entry.Key) || _keybinds.Chords(entry.Key).Count == 0) continue;
            extraActions.Add(entry.Key);
        }
        if (extraActions.Count > 0)
        {
            var rows = new List<(FrameworkElement Row, Func<string> Text)>();
            var elements = new List<FrameworkElement>();
            foreach (string action in extraActions)
            {
                FrameworkElement row = KeybindRow(KeybindLabel(labels, action), action);
                elements.Add(row);
                rows.Add((row, () => KeybindSearchText(labels, action)));
            }
            FrameworkElement group = SettingsGroup(Strings.SettingsKeysGroupOther,
                SettingsCard(elements.ToArray()));
            list.Children.Add(group);
            groups.Add((group, rows));
        }

        // Filtering hides rows instead of rebuilding them: the caps keep their capture
        // state, and a page with ~100 rows stays cheap on every keystroke.
        Action applyFilter = () =>
        {
            string query = search.Text.Trim();
            int visible = 0;
            foreach ((FrameworkElement view, List<(FrameworkElement Row, Func<string> Text)> rows) in groups)
            {
                // Rows after the first sit inside a Border that draws the separator line.
                // Hiding only the row would leave that line (and its height) behind, so the
                // wrapper is hidden too and the first visible row drops its top line: the
                // card then looks exactly like one rebuilt from the matches.
                bool firstVisible = true;
                int shown = 0;
                foreach ((FrameworkElement row, Func<string> text) in rows)
                {
                    bool match = query.Length == 0 ||
                        text().Contains(query, StringComparison.OrdinalIgnoreCase);
                    row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    if (row.Parent is Border separator)
                    {
                        separator.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                        // Only the line between two visible rows is kept: the wrapper itself
                        // has to stay visible, or it would take the row down with it.
                        separator.BorderThickness = match && !firstVisible
                            ? new Thickness(0, 1, 0, 0)
                            : new Thickness(0);
                    }
                    if (!match) continue;

                    firstVisible = false;
                    shown++;
                }
                view.Visibility = shown > 0 ? Visibility.Visible : Visibility.Collapsed;
                visible += shown;
            }
            noMatch.Visibility = visible == 0 ? Visibility.Visible : Visibility.Collapsed;
        };

        _keybindFilter = applyFilter;
        search.TextChanged += (_, _) => applyFilter();

        return page;
    }

    /// <summary>The text a shortcut row is matched against: label, action id and current chords.</summary>
    private string KeybindSearchText(Dictionary<string, string> labels, string action) =>
        KeybindLabel(labels, action) + " " + action + " " + string.Join(' ', _keybinds.Chords(action));

    /// <summary>
    /// Action id to the label the palette shows for it. The palette rows are the one place
    /// that pairs a localized label with the action a "keybind" line names, so the shortcut
    /// list is built from them instead of a second label table that could drift.
    /// </summary>
    private Dictionary<string, string> KeybindActionLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (PaletteItem item in _paletteItems)
        {
            string? action = KeybindActionForLabel(item.Label);
            if (action is not null) labels[action] = item.Label;
        }

        // Actions that only ever ran as chords: the palette has no row to take a label from.
        labels["toggle_command_palette"] = Strings.CommandPalette;
        labels["jump_to_prompt:-1"] = Strings.JumpToPreviousPrompt;
        labels["jump_to_prompt:1"] = Strings.JumpToNextPrompt;
        labels["adjust_selection:left"] = Strings.AdjustSelectionLeft;
        labels["adjust_selection:right"] = Strings.AdjustSelectionRight;
        labels["adjust_selection:up"] = Strings.AdjustSelectionUp;
        labels["adjust_selection:down"] = Strings.AdjustSelectionDown;
        for (int slot = 1; slot <= 8; slot++) labels[$"goto_tab:{slot}"] = Strings.GoToTab(slot);
        return labels;
    }

    private static string KeybindLabel(Dictionary<string, string> labels, string action) =>
        labels.TryGetValue(action, out string? label) ? label : action;

    /// <summary>
    /// One shortcut row: the action's label, a leading "+" and every chord the config
    /// leaves bound, each drawn as the usual key cap. Clicking a cap swaps it for a
    /// read-only field; only that focused field records the next press. The "+" only
    /// appears while the pointer is over the row.
    /// </summary>
    private FrameworkElement KeybindRow(string label, string action)
    {
        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        FillKeybindChips(keys, action);

        // Kept in the layout at zero opacity, so revealing it does not shift the caps. The
        // transparent wrapper keeps its slot hoverable while the button itself is invisible.
        Button add = SettingsKbd("+", KeybindAddWidth);
        ShowAddChip(add, false);

        var addArea = new Border
        {
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
            Child = add,
        };
        add.Click += (_, _) => BeginAddKeybindCapture(action, keys, add, addArea);
        // Keep the page's inert-press handler from cancelling a capture the click started.
        add.PointerPressed += (_, args) => args.Handled = true;

        // The add slot, the gap after it and the caps are one hover surface: moving from
        // "+" to a cap must not cross dead space where the button would disappear.
        var host = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Background = ClearBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        host.Children.Add(addArea);
        host.Children.Add(keys);

        // The "+" belongs to its own slot and to the caps, not to the whole row: hovering
        // the label leaves it hidden. Tag remembers the state for the post-capture restore.
        bool overRow = false, addFocused = false;
        void RefreshAdd()
        {
            bool shown = overRow || addFocused;
            // The tag keeps the plain pointer state; the button also stays visible while it
            // is the capture target, but that must not survive into the restore.
            add.Tag = shown;
            ShowAddChip(add, shown || ReferenceEquals(_keybindCaptureAdd, add));
        }

        add.GotFocus += (_, _) =>
        {
            addFocused = true;
            RefreshAdd();
        };
        add.LostFocus += (_, _) =>
        {
            addFocused = false;
            RefreshAdd();
        };

        // A Button marks its pointer events handled, so the panel's ordinary "+=" handler
        // never sees them; listen with handledEventsToo so the caps keep "+" visible too.
        host.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, _) =>
        {
            overRow = true;
            RefreshAdd();
        }), true);
        host.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) =>
        {
            overRow = false;
            RefreshAdd();
        }), true);

        return SettingsRow(label, null, host);
    }

    /// <summary>Paints one row's chords as key caps; a click swaps one for the editor.</summary>
    private void FillKeybindChips(StackPanel keys, string action)
    {
        keys.Children.Clear();
        List<string> chords = _keybinds.CanonicalChords(action);
        if (chords.Count == 0)
        {
            // The cap keeps the column aligned and is the target that adds a shortcut.
            keys.Children.Add(KeybindChip(Strings.SettingsKeysUnbound, action, null, keys));
            return;
        }

        foreach (string chord in chords)
        {
            keys.Children.Add(KeybindChip(Keybinds.Display(chord), action, chord, keys));
        }
    }

    /// <summary>One clickable cap; its chord is what the editor replaces.</summary>
    private Button KeybindChip(string text, string action, string? chord, StackPanel keys)
    {
        Button cap = SettingsKbd(text);
        cap.Click += (_, _) => BeginKeybindCapture(action, chord, text, keys, cap);
        // Keep the page's inert-press handler from cancelling a capture the click started.
        cap.PointerPressed += (_, args) => args.Handled = true;
        return cap;
    }

    /// <summary>
    /// Swaps the clicked cap for a focused read-only field. The field owns the capture:
    /// keys only count while it has focus, and its LostFocus puts the cap back.
    /// </summary>
    private void BeginKeybindCapture(string action, string? chord, string text, StackPanel keys, Button cap)
    {
        int index = keys.Children.IndexOf(cap);
        if (index < 0) return;

        // Swapping inside the cap's own click can fail-fast WinUI, so it lands on the next
        // dispatcher turn like every other chrome rebuild here.
        DeferSettingsChange(() =>
        {
            if (keys.Children.IndexOf(cap) != index) return;
            TextBox field = KeybindEditor(cap.ActualWidth, cap.ActualHeight);
            field.LostFocus += (_, _) => DeferSettingsChange(() =>
            {
                // Restore only this slot: another cap in the same row may already be under
                // the pointer, and repainting the row would destroy it before its Click.
                if (ReferenceEquals(_keybindCaptureBox, field)) CancelKeybindCapture(restore: false);
                RestoreKeybindChip(field, text, action, chord, keys);
            });
            keys.Children.RemoveAt(index);
            keys.Children.Insert(index, field);
            _keybindCaptureAction = action;
            _keybindCaptureChord = chord;
            _keybindCaptureKeys = keys;
            _keybindCaptureBox = field;
            _keybindCaptureAdd = null;
            _keybindCaptureAddArea = null;
            field.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>Starts the capture from the leading "+", which becomes the prompt itself.</summary>
    private void BeginAddKeybindCapture(string action, StackPanel keys, Button add, Border addArea)
    {
        DeferSettingsChange(() =>
        {
            TextBox field = KeybindEditor(KeybindCapWidth, add.ActualHeight);
            field.LostFocus += (_, _) => DeferSettingsChange(() =>
            {
                if (ReferenceEquals(_keybindCaptureBox, field)) CancelKeybindCapture(restore: false);
                else if (ReferenceEquals(addArea.Child, field))
                {
                    addArea.Child = add;
                    RestoreAddChip(add);
                }
            });
            addArea.Child = field;
            _keybindCaptureAction = action;
            _keybindCaptureChord = null;
            _keybindCaptureKeys = keys;
            _keybindCaptureBox = field;
            _keybindCaptureAdd = add;
            _keybindCaptureAddArea = addArea;
            field.Focus(FocusState.Programmatic);
        });
    }

    /// <summary>
    /// The read-only prompt shown only while a shortcut is being recorded. It takes the
    /// clicked cap's width and height so the row does not move when the field appears, and
    /// the text sits in the same centre the cap uses.
    /// </summary>
    private TextBox KeybindEditor(double width, double height)
    {
        TextBox field = KeybindBox(Strings.SettingsKeysPress, width);
        // The template's default minimum would make the field taller than the cap.
        field.MinWidth = 0;
        field.MinHeight = 0;
        if (height > 0) field.Height = height;
        field.Padding = new Thickness(8, 4, 8, 4);
        field.HorizontalContentAlignment = HorizontalAlignment.Center;
        field.VerticalContentAlignment = VerticalAlignment.Center;
        field.TextAlignment = TextAlignment.Center;
        // A press on the field must not reach the page's inert-press handler, which would
        // cancel the capture the press just started.
        field.PointerPressed += (_, args) => args.Handled = true;
        return field;
    }

    private void CancelKeybindCapture(bool restore = true)
    {
        string? action = _keybindCaptureAction;
        StackPanel? keys = _keybindCaptureKeys;
        Button? add = _keybindCaptureAdd;
        Border? addArea = _keybindCaptureAddArea;
        _keybindCaptureAction = null;
        _keybindCaptureChord = null;
        _keybindCaptureKeys = null;
        _keybindCaptureBox = null;
        _keybindCaptureAdd = null;
        _keybindCaptureAddArea = null;

        if (add is not null && addArea is not null)
        {
            addArea.Child = add;
            RestoreAddChip(add);
        }
        if (restore && action is not null && keys is not null) FillKeybindChips(keys, action);
    }

    /// <summary>Puts the caps back when an editor lost focus without a commit.</summary>
    private void RestoreKeybindChip(TextBox field, string text, string action, string? chord, StackPanel keys)
    {
        int index = keys.Children.IndexOf(field);
        if (index < 0) return;
        keys.Children.RemoveAt(index);
        keys.Children.Insert(index, KeybindChip(text, action, chord, keys));
    }

    /// <summary>Returns the "+" to its narrow, hover-only form after a capture ends.</summary>
    private static void RestoreAddChip(Button add)
    {
        if (add.Content is TextBlock label) label.Text = "+";
        add.Width = KeybindAddWidth;
        ShowAddChip(add, add.Tag is true);
    }

    private static void ShowAddChip(FrameworkElement add, bool show)
    {
        add.Opacity = show ? 1.0 : 0.0;
        add.IsHitTestVisible = show;
    }

    /// <summary>
    /// The key press that answers an open shortcut capture: Esc cancels, Delete clears the
    /// focused chord, and any other key replaces it. Tab is left to the page so focus moves
    /// on, which ends the capture through the field's LostFocus.
    /// </summary>
    private bool OnKeybindCaptureKey(KeyRoutedEventArgs args)
    {
        // Tab moves focus out of the field instead of becoming a shortcut. Only the capture
        // is dropped, with no repaint: the field's LostFocus puts its cap back.
        if (args.Key == VirtualKey.Tab)
        {
            CancelKeybindCapture(restore: false);
            return false;
        }

        if (args.Key == VirtualKey.Escape)
        {
            CancelKeybindCapture();
            FocusSettingsOwner();
            return true;
        }

        // A modifier alone never completes a shortcut.
        if (IsModifierKey(args.Key)) return true;

        string action = _keybindCaptureAction!;
        string? oldChord = _keybindCaptureChord;
        if (args.Key == VirtualKey.Back || args.Key == VirtualKey.Delete)
        {
            ApplyKeybind(action, oldChord, null);
            return true;
        }

        string? chord = Keybinds.Format(CurrentMods(), args.Key, KeyMap.UnshiftedCodepoint(args.Key));
        if (chord is not null) ApplyKeybind(action, oldChord, chord);
        return true;
    }

    /// <summary>
    /// Replaces (or clears, when <paramref name="chord"/> is null) one chord of an action;
    /// the action's other chords stay bound.
    /// </summary>
    private void ApplyKeybind(string action, string? oldChord, string? chord)
    {
        StackPanel? keys = _keybindCaptureKeys;
        CancelKeybindCapture(restore: false);

        var lines = new List<string>();
        if (_config.Values.TryGetValue("keybind", out List<string>? current)) lines.AddRange(current);
        lines = KeybindEdit.SetBinding(lines, action, oldChord, chord);

        Config.SaveList("keybind", lines);
        _config.ReplaceValues("keybind", lines);
        RefreshKeybinds();

        // The edited row can now match a different query (the new chord was nowhere on the
        // page before), so the filter runs again instead of leaving stale results.
        DeferSettingsChange(() =>
        {
            if (keys is not null) FillKeybindChips(keys, action);
            _keybindFilter?.Invoke();
            // The field that recorded the press is gone after the repaint; hand the keyboard
            // back to the page rather than leaving focus on a detached element.
            FocusSettingsOwner();
        });
    }

    /// <summary>True while the field that started the capture still owns keyboard focus.</summary>
    private bool IsKeybindCaptureFocused()
    {
        if (_keybindCaptureBox is null || _root.XamlRoot is null) return false;
        object? focused = FocusManager.GetFocusedElement(_root.XamlRoot);
        for (DependencyObject? node = focused as DependencyObject; node is not null;
             node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, _keybindCaptureBox)) return true;
        }
        return false;
    }

    // ---------------- about ----------------

    /// <summary>The public repository, shown (and copyable) in the About card.</summary>
    private const string RepositoryUrl = "https://github.com/shizengzhou/Gui";

    private FrameworkElement BuildAboutSettingsPage()
    {
        var page = SettingsPageStack();

        // Drawn with the page's own primitives rather than the toolkit's expander: that control
        // fills its header from Fluent's card brush (a white sheet on this theme) and keeps its
        // pointer-over colours after the pointer leaves, and neither is reachable through the
        // page's resources.
        var content = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = PanelStrokeBrush(),
            Visibility = Visibility.Collapsed,
        };

        var body = SettingsRowStack(
            SettingsRow(Strings.AboutRepository, null, RepositoryControl()),
            SettingsRow(Strings.AboutLicense, null,
                Label("MIT License", Theme.FontBody, TextFaintBrush())));
        content.Child = body;

        var icon = new Image
        {
            Width = 20,
            Height = 20,
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.scale-200.png"))),
        };

        var titles = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Label("Gui", Theme.FontTitle, TextStrongBrush()));
        titles.Children.Add(Label(Strings.AboutCopyright(DateTime.Now.Year), Theme.FontCaption, TextFaintBrush()));

        // The version sits at the end of the header row, where the toolkit expander put its
        // content, rather than inside the part that opens.
        TextBlock version = Label(AppVersion(), Theme.FontBody, TextFaintBrush());
        version.VerticalAlignment = VerticalAlignment.Center;

        FontIcon chevron = Glyph('\uE76C', 10, TextFaintBrush());
        chevron.VerticalAlignment = VerticalAlignment.Center;

        var headerGrid = new Grid { Padding = new Thickness(13, 11, 13, 11), ColumnSpacing = 10 };
        AddAutoColumn(headerGrid);
        AddStarColumn(headerGrid);
        AddAutoColumn(headerGrid);
        AddAutoColumn(headerGrid);
        headerGrid.Children.Add(icon);
        Grid.SetColumn(titles, 1);
        headerGrid.Children.Add(titles);
        Grid.SetColumn(version, 2);
        headerGrid.Children.Add(version);
        Grid.SetColumn(chevron, 3);
        headerGrid.Children.Add(chevron);

        var header = new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusField),
            Background = ClearBrush(),
            Child = headerGrid,
        };
        header.PointerEntered += (_, _) => header.Background = ChipHoverBrush();
        header.PointerExited += (_, _) => header.Background = ClearBrush();
        header.PointerReleased += (_, args) =>
        {
            if (!IsPrimaryRelease(args, header)) return;
            bool open = content.Visibility != Visibility.Visible;
            content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            // The header is the whole card while closed; with the body open it is only its
            // top edge, so the fill and hover should be square where the separator follows.
            header.CornerRadius = open
                ? new CornerRadius(Theme.RadiusField, Theme.RadiusField, 0, 0)
                : new CornerRadius(Theme.RadiusField);
            chevron.Glyph = open ? "\uE70D" : "\uE76C";
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(content);

        page.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusField),
            BorderThickness = new Thickness(1),
            BorderBrush = PanelStrokeBrush(),
            Child = stack,
        });
        return page;
    }

    /// <summary>The repository link and the copy button the toolkit card used to provide.</summary>
    private FrameworkElement RepositoryControl()
    {
        var link = new HyperlinkButton
        {
            Content = RepositoryUrl,
            NavigateUri = new Uri(RepositoryUrl),
            FontFamily = new FontFamily("Cascadia Mono"),
            FontSize = Theme.FontBody,
            Foreground = AccentBrush(),
            Padding = new Thickness(0),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };

        FontIcon copyIcon = Glyph('\uE8C8', Theme.FontBody, TextFaintBrush());
        var copy = new ChromeButton(copyIcon, 26, 26, new CornerRadius(Theme.RadiusControl))
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        copy.SetColors(Theme.TextFaint, hoverFill: Theme.ChipHover);
        copy.SetAccessibleName(Strings.AboutCopyRepository);
        Tips.Set(copy, Strings.AboutCopyRepository);
        copy.Click += (_, _) => ClipboardAccess.TryWriteText(RepositoryUrl);
        _chromeButtons.Add(copy);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(link);
        row.Children.Add(copy);
        return row;
    }

    /// <summary>The version stamped into the build, pre-release suffix included.</summary>
    private static string AppVersion()
    {
        string? version = typeof(Workspace).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version))
            return typeof(Workspace).Assembly.GetName().Version?.ToString(3) ?? "dev";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }

    // ---------------- live application ----------------

    private void ApplyWindowSaveStateChoice(bool save)
    {
        _config.WindowSaveState = save ? "always" : "never";
        Config.SaveValue("window-save-state", _config.WindowSaveState);
    }

    private void ApplyConfirmCloseSurfaceChoice(int index)
    {
        string value = index switch
        {
            1 => "always",
            2 => "false",
            _ => "true",
        };
        _config.ConfirmCloseSurface = value;
        Config.SaveValue("confirm-close-surface", value);
    }

    private void ApplyAutoUpdateChoice(bool check)
    {
        _config.AutoUpdate = check ? "check" : "off";
        Config.SaveValue("auto-update", _config.AutoUpdate);
    }

    private void ApplyLanguageChoice(string value)
    {
        Config.SaveValue("language", value);
        DeferSettingsChange(ReloadConfigCore);
    }

    private void ApplyWindowThemeChoice(string mode)
    {
        ApplyWindowThemeMode(mode);
        Config.SaveValue("window-theme", mode);
        BroadcastTheme();
    }

    private void ApplyCompactChoice(bool compact)
    {
        SetDensity(compact);

        // The page's own padding, corner radius and border follow the density, so the layer
        // is rebuilt after the click handler unwinds.
        DeferSettingsChange(() =>
        {
            BuildSettings();
            _settingsFocus?.Focus(FocusState.Programmatic);
        });
    }

    private void ApplyBackgroundOpacityChoice(double opacity)
    {
        _config.BackgroundOpacity = (float)opacity;
        Config.SaveValue("background-opacity", opacity.ToString("0.##", CultureInfo.InvariantCulture));
        ApplyBackgroundOpacity();
        RefreshPaneVisuals();
    }

    private void ApplyBackgroundBlurChoice(bool blur)
    {
        _config.BackgroundBlur = blur;
        Config.SaveValue("background-blur", blur ? "true" : "false");
        ApplyBackgroundOpacity();
        RefreshPaneVisuals();
    }

    private async System.Threading.Tasks.Task<string?> PickBackgroundImageAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            if (_hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.PickBackgroundImage", error);
            return null;
        }
    }

    private void ApplyBackgroundImageChoice(string path)
    {
        _config.BackgroundImage = path;
        Config.SaveValue("background-image", path);
        RefreshPaneVisuals();
    }

    private void ApplyBackgroundImageOpacityChoice(double opacity)
    {
        _config.BackgroundImageOpacity = (float)Math.Clamp(opacity, 0.0, 1.0);
        Config.SaveValue("background-image-opacity",
            _config.BackgroundImageOpacity.ToString("0.##", CultureInfo.InvariantCulture));
        RefreshPaneVisuals();
    }

    private void ApplyBackgroundImagePositionChoice(string position)
    {
        _config.BackgroundImagePosition = position;
        Config.SaveValue("background-image-position", position);
        RefreshPaneVisuals();
    }

    private void ApplyBackgroundImageFitChoice(string fit)
    {
        _config.BackgroundImageFit = fit;
        Config.SaveValue("background-image-fit", fit);
        RefreshPaneVisuals();
    }

    private void ApplyBackgroundImageRepeatChoice(bool repeat)
    {
        _config.BackgroundImageRepeat = repeat;
        Config.SaveValue("background-image-repeat", repeat ? "true" : "false");
        RefreshPaneVisuals();
    }

    // ---------------- custom shaders ----------------

    private async System.Threading.Tasks.Task<string?> PickShaderAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            // Shadertoy-style GLSL, the syntax Ghostty's own "custom-shader" takes.
            picker.FileTypeFilter.Add(".glsl");
            if (_hwnd != IntPtr.Zero) WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            return file?.Path;
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.PickShader", error);
            return null;
        }
    }

    /// <summary>
    /// One edit to the shader chain: replace the entry at <paramref name="index"/> (or drop it
    /// when the path is empty), or append when the index is negative.
    /// </summary>
    private void ApplyCustomShader(int index, string picked)
    {
        var paths = new List<string>(_config.CustomShaders);
        if (index >= 0 && index < paths.Count)
        {
            if (picked.Length == 0) paths.RemoveAt(index);
            else paths[index] = picked;
        }
        else if (picked.Length > 0)
        {
            paths.Add(picked);
        }

        Config.SaveList("custom-shader", paths);
        _config.ReplaceValues("custom-shader", paths);
        // Re-resolve the way a reload would, so a dropped entry or a file that has since gone
        // missing leaves the chain before the panes pick it up.
        _config.CustomShaders = Config.Load().CustomShaders;
        RefreshPaneVisuals();

        // Adding or removing an entry changes the rows themselves.
        DeferSettingsChange(BuildSettings);
    }

    private void ApplyShaderAnimationChoice(bool animate)
    {
        _config.CustomShaderAnimation = animate ? "true" : "false";
        Config.SaveValue("custom-shader-animation", _config.CustomShaderAnimation);
        RefreshPaneVisuals();
    }

    private void ApplyFontFamilyChoice(string family)
    {
        string name = family.Length > 0 ? family : DefaultFontFamily;
        _config.FontFamily = name;
        _config.FontFamilies.Clear();
        _config.FontFamilies.Add(name);
        Config.SaveValue("font-family", name);
        RefreshPaneFonts();
    }

    private void ApplyFontSizeChoice(double size)
    {
        _config.FontSize = (float)size;
        Config.SaveValue("font-size", size.ToString(CultureInfo.InvariantCulture));
        RefreshPaneFonts();
    }

    private void ApplyFontFeatureList(string value)
    {
        _config.FontFeatures = Config.ParseFontFeatures(new[] { value });
        Config.SaveValue("font-feature", value);
        RefreshPaneFonts();
    }

    private void ApplyShellFeatureList(string value)
    {
        // Existing panes already started their shell, so this only reaches new tabs.
        _config.ShellFeatures = Config.ParseShellFeatures(value);
        Config.SaveValue("shell-integration-features", value);
    }

    private void ApplyNewTabPositionChoice(string position)
    {
        _config.NewTabPosition = position;
        Config.SaveValue("window-new-tab-position", position);
    }

    private void ApplyTabInheritWorkingDirectoryChoice(bool inherit)
    {
        _config.TabInheritWorkingDirectory = inherit;
        Config.SaveValue("tab-inherit-working-directory", inherit ? "true" : "false");
    }

    private void ApplySplitInheritWorkingDirectoryChoice(bool inherit)
    {
        _config.SplitInheritWorkingDirectory = inherit;
        Config.SaveValue("split-inherit-working-directory", inherit ? "true" : "false");
    }

    private void ApplyCursorStyle(string style)
    {
        _config.CursorStyle = style;
        Config.SaveValue("cursor-style", style);
        RefreshPaneFonts();
    }

    private void ApplyCursorBlinkChoice(bool blink)
    {
        _config.CursorBlink = blink;
        Config.SaveValue("cursor-style-blink", blink ? "true" : "false");
        RefreshPaneFonts();
    }

    private void ApplyCopyOnSelectChoice(bool copy)
    {
        _config.CopyOnSelect = copy;
        Config.SaveValue("copy-on-select", copy ? "true" : "false");

        // Split panes and inherited tabs hold a snapshot of the config; the selection code
        // reads it at copy time, so it just has to be pushed across.
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.SyncConfig(_config));
    }

    private void ApplyScrollbarChoice(string value)
    {
        _config.Scrollbar = value;
        Config.SaveValue("scrollbar", value);
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.ApplyScrollSettings());
    }

    private void ApplyRightClickActionChoice(int index)
    {
        string action = index switch
        {
            1 => "paste",
            2 => "copy",
            3 => "copy-or-paste",
            4 => "ignore",
            _ => "context-menu",
        };
        _config.RightClickAction = action;
        Config.SaveValue("right-click-action", action);

        // Panes read the action from their own config snapshot on right-click.
        foreach (Tab tab in _tabs)
            ForEachPane(tab, pane => pane.SyncConfig(_config));
    }

    private void RefreshPaneFonts()
    {
        foreach (Tab tab in _tabs)
        {
            ForEachPane(tab, pane =>
            {
                pane.SyncConfig(_config);
                pane.ApplyFont();
            });
        }
    }

    private void RefreshPaneVisuals()
    {
        foreach (Tab tab in _tabs)
        {
            ForEachPane(tab, pane =>
            {
                pane.SyncConfig(_config);
                pane.ApplyFont();
                pane.ApplyTheme();
            });
        }
    }

    /// <summary>
    /// Runs a change that rebuilds the chrome on the next dispatcher turn. Rebuilding inside
    /// the pointer handler of a settings control would remove that control mid-event.
    /// </summary>
    private void DeferSettingsChange(Action action)
    {
        if (_dispatcher is null)
        {
            action();
            return;
        }
        _dispatcher.TryEnqueue(() => Diag.Guard("ws.settings", action));
    }

    // ---------------- controls ----------------

    private StackPanel SettingsPageStack()
    {
        return new StackPanel
        {
            Spacing = 16,
            // Transparent but hit-testable: presses on empty page space have to land on the
            // page, not fall through to the scroller behind it.
            Background = ClearBrush(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 14, 16, 18),
        };
    }

    private FrameworkElement SettingsGroup(string? title, FrameworkElement card)
    {
        var group = new StackPanel { Spacing = 7 };
        if (!string.IsNullOrEmpty(title))
        {
            group.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = Theme.FontCaption,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextFaintBrush(),
                Margin = new Thickness(2, 0, 2, 0),
            });
        }
        group.Children.Add(card);
        return group;
    }

    private Border SettingsCard(params FrameworkElement[] rows)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusField),
            BorderThickness = new Thickness(1),
            BorderBrush = PanelStrokeBrush(),
            Child = SettingsRowStack(rows),
        };
    }

    /// <summary>
    /// Stacks rows with the 1 DIP separator the cards use between neighbours, for surfaces
    /// that already own their border (a card, the About page's expanded body).
    /// </summary>
    private StackPanel SettingsRowStack(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i == 0)
            {
                stack.Children.Add(rows[i]);
                continue;
            }
            stack.Children.Add(new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = PanelStrokeBrush(),
                Child = rows[i],
            });
        }
        return stack;
    }

    private Grid SettingsRow(string title, string? description, FrameworkElement? control)
    {
        var grid = new Grid
        {
            Padding = new Thickness(13, 11, 13, 11),
            MinHeight = 46,
            ColumnSpacing = 14,
        };
        AddStarColumn(grid);
        AddAutoColumn(grid);

        var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = Theme.FontBody,
            Foreground = TextStrongBrush(),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(description))
        {
            label.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = Theme.FontCaption,
                Foreground = TextFaintBrush(),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        grid.Children.Add(label);

        if (control is not null)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            control.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(control, 1);
            grid.Children.Add(control);
        }

        return grid;
    }

    private FrameworkElement SettingsSegmented(string[] options, int selected, Action<int> changed)
    {
        var host = new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusField),
            Background = ChipHoverBrush(),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        host.Child = row;

        var buttons = new List<(Border Button, TextBlock Text)>();

        void Apply(int active)
        {
            for (int i = 0; i < buttons.Count; i++)
            {
                bool on = i == active;
                (Border button, TextBlock text) = buttons[i];
                button.Background = on ? TermBgBrush() : ClearBrush();
                button.BorderBrush = on ? PanelStrokeBrush() : ClearBrush();
                button.BorderThickness = new Thickness(on ? 1 : 0);
                text.Foreground = on ? TextStrongBrush() : TextBodyBrush();
                text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var text = new TextBlock
            {
                Text = options[i],
                FontSize = Theme.FontBody,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Border
            {
                // With the host's 2 DIP padding this is the page's 32 DIP control height.
                Height = 28,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(Theme.RadiusControl),
                Background = ClearBrush(),
                Child = text,
            };
            button.PointerPressed += (_, args) =>
            {
                args.Handled = true;
                if (!IsPrimaryPress(args, button)) return;
                Apply(index);
                changed(index);
            };
            row.Children.Add(button);
            buttons.Add((button, text));
        }

        Apply(selected);
        return host;
    }

    /// <summary>
    /// The single-line TextBox inside AutoSuggestBox/NumberBox takes its padding from the theme
    /// style, so setting Padding on the outer control does nothing, and a control-level
    /// Resources override cannot replace a ThemeResource either. Nudge only the inner box's
    /// vertical padding by 2 DIP: that centres the glyphs in the 32 DIP field without changing
    /// its height or its horizontal padding. Pinning the field shorter or rewriting the
    /// horizontal padding clips the value and pushes the spin buttons out of place.
    /// </summary>
    private static void CenterSettingsValue(Control box)
    {
        bool applied = false;
        box.Loaded += (_, _) =>
        {
            if (applied) return;
            box.ApplyTemplate();
            if (FirstDescendant<TextBox>(box) is not { } inner) return;
            applied = true;
            Thickness p = inner.Padding;
            inner.Padding = new Thickness(p.Left, p.Top + 2, p.Right, Math.Max(0, p.Bottom - 2));
        };
    }

    private static T? FirstDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FirstDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private ComboBox SettingsSelect(
        IReadOnlyList<string> items, int selectedIndex, double width, Action<int> changed)
    {
        var box = new ComboBox
        {
            Width = width,
            MinHeight = 32,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (string item in items) box.Items.Add(item);
        if (selectedIndex >= 0 && selectedIndex < items.Count) box.SelectedIndex = selectedIndex;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0) changed(box.SelectedIndex);
        };
        return box;
    }

    private ComboBox SettingsEditableSelect(
        IReadOnlyList<string> items, string current, double width, Action<string> changed)
    {
        var box = new ComboBox
        {
            Width = width,
            MinHeight = 32,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            IsEditable = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (string item in items) box.Items.Add(item);

        // The text has to be set after the items: adding to an empty editable ComboBox
        // clears whatever Text was assigned in the initializer.
        box.Text = current;
        if (items.Contains(current)) box.SelectedItem = current;

        void Commit()
        {
            string text = box.Text.Trim();
            if (text.Length == 0 || text == current) return;
            current = text;
            changed(text);
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            args.Handled = true;
            Commit();
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is string name && name != current)
            {
                current = name;
                changed(name);
            }
        };
        return box;
    }

    private FrameworkElement SettingsSwitch(bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch
        {
            IsOn = value,
            // The card has no room for the On/Off captions the stock switch carries.
            OnContent = string.Empty,
            OffContent = string.Empty,
            // The template keeps a 12 DIP column for that caption; pin the width to the
            // 40 DIP pill so the switch still ends flush with the fields above it.
            Width = 40,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The template puts 10 DIP above and below the 20 DIP track. Trim both to 6 so
        // the control is 32 DIP high like the fields in the rows around it.
        toggle.Resources["ToggleSwitchPreContentMargin"] = 6.0;
        toggle.Resources["ToggleSwitchPostContentMargin"] = 6.0;
        // The off state keeps Fluent's own neutral fill and stroke; only the on state takes
        // the terminal theme's accent, so the switch matches the rest of the chrome. These
        // six keys are brushes in the template, so a brush override is the right type.
        Brush accent = AccentBrush();
        foreach (string key in new[]
                 {
                     "ToggleSwitchFillOn", "ToggleSwitchFillOnPointerOver",
                     "ToggleSwitchFillOnPressed", "ToggleSwitchStrokeOn",
                     "ToggleSwitchStrokeOnPointerOver", "ToggleSwitchStrokeOnPressed",
                 })
        {
            toggle.Resources[key] = accent;
        }
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    private NumberBox SettingsNumber(double value, double min, double max, Action<double> changed)
    {
        var box = new NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 150,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            VerticalAlignment = VerticalAlignment.Center,
        };
        CenterSettingsValue(box);
        box.ValueChanged += (_, args) =>
        {
            // A cleared or half-typed field evaluates to NaN; keep the last good size.
            if (double.IsNaN(args.NewValue)) return;
            if (args.NewValue != args.OldValue) changed(args.NewValue);
        };
        return box;
    }

    /// <summary>A number field with its unit beside it; NumberBox has no suffix of its own.</summary>
    private FrameworkElement SettingsUnit(NumberBox box, string unit)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(box);
        row.Children.Add(Label(unit, Theme.FontSmall, TextFaintBrush()));
        return row;
    }

    /// <summary>
    /// A 0..100 slider paired with a small number field, for percentage settings. The slider
    /// and the field drive the same value; each guards against echoing the other's update.
    /// </summary>
    private FrameworkElement SettingsPercentSlider(double percent, Action<double> changed)
    {
        var value = new NumberBox
        {
            Value = Math.Clamp(Math.Round(percent), 0, 100),
            Minimum = 0,
            Maximum = 100,
            SmallChange = 1,
            LargeChange = 5,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden,
            // Wide enough that the value stays readable while the clear button is showing.
            Width = 80,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            VerticalAlignment = VerticalAlignment.Center,
        };
        CenterSettingsValue(value);
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(percent, 0.0, 100.0),
            StepFrequency = 1,
            Width = 150,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The stock slider paints its value from the system accent, which follows Windows'
        // personalisation, and the plate under its thumb from Fluent's fixed whites; neither
        // follows a Ghostty theme. Take the theme's accent for everything that reads as the
        // value (the same brush the switch takes when it is on) and the card's own surface and
        // stroke for the plate around the thumb and its rim.
        Brush sliderAccent = AccentBrush();
        Brush sliderPlate = TermBgBrush();
        Brush sliderRim = PanelStrokeBrush();
        slider.Foreground = sliderAccent;
        foreach (string key in new[]
                 {
                     "SliderTrackValueFill", "SliderTrackValueFillPointerOver",
                     "SliderTrackValueFillPressed", "SliderThumbBackground",
                     "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                     "SliderThumbBackgroundThemeBrush", "SliderThumbPointerOverBackgroundThemeBrush",
                     "SliderThumbPressedBackgroundThemeBrush",
                 })
        {
            slider.Resources[key] = sliderAccent;
        }
        foreach (string key in new[]
                 {
                     "SliderOuterThumbBackground", "SliderOuterThumbBackgroundPointerOver",
                     "SliderOuterThumbBackgroundPressed",
                 })
        {
            slider.Resources[key] = sliderPlate;
        }
        foreach (string key in new[] { "SliderThumbBorderBrush", "SliderThumbBorderThemeBrush" })
        {
            slider.Resources[key] = sliderRim;
        }

        bool syncing = false;
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true;
            value.Value = Math.Round(e.NewValue);
            syncing = false;
            changed(e.NewValue);
        };
        value.ValueChanged += (_, e) =>
        {
            // A cleared or half-typed field evaluates to NaN; keep the last good value.
            if (syncing || double.IsNaN(e.NewValue)) return;
            double percentValue = Math.Round(e.NewValue);
            if (percentValue == e.OldValue) return;
            syncing = true;
            slider.Value = percentValue;
            syncing = false;
            changed(percentValue);
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(slider);
        row.Children.Add(SettingsUnit(value, "%"));
        return row;
    }

    /// <summary>
    /// Every installed family DirectWrite marks as monospaced, sorted by name, with the bundled
    /// Nerd Font always present. The enumeration is done once in FontCache and shared with the
    /// renderer, so opening Settings only reads that cached list.
    /// </summary>
    private static IReadOnlyList<string> MonospaceFontFamilies() => FontCache.MonospaceFamilies(DefaultFontFamily);

    /// <summary>
    /// The value written in the config file for a key, without any resolved default: an unset
    /// key reads as an empty string so the field shows its placeholder.
    /// </summary>
    private static string StoredValue(Config config, string key) =>
        config.Values.TryGetValue(key, out List<string>? values) && values.Count > 0
            ? values[^1]
            : string.Empty;

    private TextBox SettingsText(string value, string placeholder, double width, Action<string> commit)
    {
        var box = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder,
            Width = width,
            Height = 32,
            MinHeight = 32,
            FontSize = Theme.FontBody,
            BorderBrush = PanelStrokeBrush(),
            Background = ClearBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        // Commit only a real edit: leaving the field untouched must not rewrite the key, or
        // opening the page would persist a stale, inherited value.
        string initial = value;
        void Commit()
        {
            string text = box.Text.Trim();
            if (text == initial) return;
            initial = text;
            commit(text);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter) return;
            args.Handled = true;
            Commit();
        };
        return box;
    }

    /// <summary>
    /// A small text button on the card (Browse/Remove style). It is a Button so it can take
    /// focus and run from the keyboard; the Fluent brushes are overridden to the card's own
    /// colours so it keeps the quiet look.
    /// </summary>
    private Button SettingsTextButton(string text, Action clicked)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = Theme.FontBody,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Brush fill = ChipHoverBrush();
        Brush clear = ClearBrush();
        Brush stroke = PanelStrokeBrush();
        var button = new Button
        {
            Content = label,
            MinWidth = 0,
            Height = 32,
            MinHeight = 32,
            Padding = new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = stroke,
            Background = clear,
            Foreground = TextBodyBrush(),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The template swaps in its own theme brushes for hover and press; point them at
        // the card's colours instead of a stock grey button.
        foreach (string key in new[]
                 {
                     "ButtonBackground", "ButtonBackgroundDisabled",
                 })
        {
            button.Resources[key] = clear;
        }
        foreach (string key in new[]
                 {
                     "ButtonBackgroundPointerOver", "ButtonBackgroundPressed",
                 })
        {
            button.Resources[key] = fill;
        }
        foreach (string key in new[]
                 {
                     "ButtonBorderBrush", "ButtonBorderBrushPointerOver",
                     "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
                 })
        {
            button.Resources[key] = stroke;
        }
        foreach (string key in new[]
                 {
                     "ButtonForeground", "ButtonForegroundDisabled",
                 })
        {
            button.Resources[key] = TextBodyBrush();
        }
        foreach (string key in new[]
                 {
                     "ButtonForegroundPointerOver", "ButtonForegroundPressed",
                 })
        {
            button.Resources[key] = TextStrongBrush();
        }

        button.Click += (_, _) => clicked();
        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>Width shared by every shortcut field, so the column lines up row to row.</summary>
    private const double KeybindCapWidth = 128;

    /// <summary>Width of the narrow "+" field that adds another chord to an action.</summary>
    private const double KeybindAddWidth = 30;

    /// <summary>
    /// One key cap on the card, used whenever a shortcut is not being edited. It is a
    /// Button so it can take focus and be activated from the keyboard; its Fluent brushes
    /// are overridden so the normal state still looks like the original cap.
    /// </summary>
    private Button SettingsKbd(string text, double width = KeybindCapWidth)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = Theme.FontBody,
            Foreground = TextStrongBrush(),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        Brush fill = ChipHoverBrush();
        Brush stroke = PanelStrokeBrush();
        var button = new Button
        {
            Content = label,
            Width = width,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = stroke,
            Background = fill,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // The template swaps in its own theme brushes for hover, press and focus; point
        // them at the cap's colours so it never repaints as a stock grey button.
        foreach (string key in new[]
                 {
                     "ButtonBackground", "ButtonBackgroundPointerOver",
                     "ButtonBackgroundPressed", "ButtonBackgroundDisabled",
                 })
        {
            button.Resources[key] = fill;
        }
        foreach (string key in new[]
                 {
                     "ButtonBorderBrush", "ButtonBorderBrushPointerOver",
                     "ButtonBorderBrushPressed", "ButtonBorderBrushDisabled",
                 })
        {
            button.Resources[key] = stroke;
        }
        foreach (string key in new[]
                 {
                     "ButtonForeground", "ButtonForegroundPointerOver",
                     "ButtonForegroundPressed", "ButtonForegroundDisabled",
                 })
        {
            button.Resources[key] = TextStrongBrush();
        }

        AutomationProperties.SetName(button, text);
        return button;
    }

    /// <summary>The read-only field shown only while a shortcut is being recorded.</summary>
    private TextBox KeybindBox(string text, double width)
    {
        return new TextBox
        {
            Text = text,
            IsReadOnly = true,
            IsTabStop = true,
            Width = width,
            FontSize = Theme.FontBody,
            TextAlignment = TextAlignment.Center,
            Padding = new Thickness(8, 0, 8, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = PanelStrokeBrush(),
            Background = ClearBrush(),
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
