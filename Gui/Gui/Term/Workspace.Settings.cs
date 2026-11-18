using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
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
        public Border Button = null!;
        public TextBlock Text = null!;
        public FontIcon Icon = null!;
        public Border Accent = null!;
    }

    /// <summary>The family the renderer falls back to when "font-family" is unset.</summary>
    private const string DefaultFontFamily = "JetBrainsMono Nerd Font";

    /// <summary>Installed monospaced families, resolved once per process.</summary>
    private static List<string>? _monospaceFamilies;

    private Grid? _settingsLayer;
    private Button? _settingsFocus;
    private Grid? _settingsPages;
    private TextBlock? _settingsTitle;
    private string _settingsPage = "appearance";

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
        backRow.Children.Add(Label(Strings.SettingsBack, 12, TextBodyBrush()));

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
        footRow.Children.Add(Label(Strings.SettingsOpenConfig, 12, TextBodyBrush()));

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

        return nav;
    }

    private Border SettingsNavItem(string page, char glyph, string label)
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
        TextBlock text = Label(label, 12, TextBodyBrush());
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

        var button = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(Theme.RadiusField),
            Background = ClearBrush(),
            Padding = new Thickness(7, 0, 10, 0),
            Child = row,
        };
        button.PointerEntered += (_, _) =>
        {
            if (page != _settingsPage) button.Background = ChipHoverBrush();
        };
        button.PointerExited += (_, _) =>
        {
            if (page != _settingsPage) button.Background = ClearBrush();
        };
        button.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, button)) return;
            // This press is swallowed here, so the layer handler cannot move focus; a field
            // on the page being left would otherwise keep it while its page goes collapsed.
            _settingsFocus?.Focus(FocusState.Programmatic);
            SelectSettingsPage(page);
        };

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
            Padding = new Thickness(16, 0, 16, 0),
        };

        _settingsTitle = new TextBlock
        {
            FontSize = Theme.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextStrongBrush(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        head.Child = _settingsTitle;
        Grid.SetRow(head, 0);
        main.Children.Add(head);

        _settingsPages = new Grid();
        AddStarRow(_settingsPages);

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
            entry.Button.Background = active ? ChipHoverBrush() : ClearBrush();
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
    private void ShowSettings()
    {
        if (_body is null) return;
        if (_paletteLayer is not null && _paletteLayer.Visibility == Visibility.Visible)
            HideCommandPalette();

        // Rebuild on open so the controls start from the live config: the theme may have been
        // changed from the palette, and a reload may have rewritten other values.
        BuildSettings();
        ActivateSettingsTab();
    }

    private void HideSettings()
    {
        DeactivateSettings();
        if (_tabs.Count > 0) FocusKeyboardAnchor();
    }

    private void ActivateSettingsTab()
    {
        if (_settingsLayer is null) return;
        _settingsActive = true;
        _settingsLayer.Visibility = Visibility.Visible;
        UpdateHeaders();
        FocusSettingsOwner();
    }

    private void DeactivateSettings()
    {
        _settingsActive = false;
        if (_settingsLayer is not null) _settingsLayer.Visibility = Visibility.Collapsed;
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

        var opacityValue = new TextBlock
        {
            Text = Math.Round(_config.BackgroundOpacity * 100).ToString(CultureInfo.InvariantCulture) + "%",
            FontSize = Theme.FontCaption,
            Foreground = TextFaintBrush(),
            Width = 38,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var opacitySlider = new Slider
        {
            Minimum = 30,
            Maximum = 100,
            Value = Math.Clamp(_config.BackgroundOpacity * 100.0, 30.0, 100.0),
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
        opacitySlider.Foreground = sliderAccent;
        foreach (string key in new[]
                 {
                     "SliderTrackValueFill", "SliderTrackValueFillPointerOver",
                     "SliderTrackValueFillPressed", "SliderThumbBackground",
                     "SliderThumbBackgroundPointerOver", "SliderThumbBackgroundPressed",
                     "SliderThumbBackgroundThemeBrush", "SliderThumbPointerOverBackgroundThemeBrush",
                     "SliderThumbPressedBackgroundThemeBrush",
                 })
        {
            opacitySlider.Resources[key] = sliderAccent;
        }
        foreach (string key in new[]
                 {
                     "SliderOuterThumbBackground", "SliderOuterThumbBackgroundPointerOver",
                     "SliderOuterThumbBackgroundPressed",
                 })
        {
            opacitySlider.Resources[key] = sliderPlate;
        }
        foreach (string key in new[] { "SliderThumbBorderBrush", "SliderThumbBorderThemeBrush" })
        {
            opacitySlider.Resources[key] = sliderRim;
        }
        opacitySlider.ValueChanged += (_, e) =>
        {
            opacityValue.Text = Math.Round(e.NewValue).ToString(CultureInfo.InvariantCulture) + "%";
            ApplyBackgroundOpacityChoice(e.NewValue / 100.0);
        };
        var opacityRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        opacityRow.Children.Add(opacitySlider);
        opacityRow.Children.Add(opacityValue);

        TextBlock imagePath = Label(
            _config.BackgroundImage.Length > 0 ? _config.BackgroundImage : "—", 10.5, TextFaintBrush());
        imagePath.MaxWidth = 130;
        imagePath.TextTrimming = TextTrimming.CharacterEllipsis;

        Border remove = SettingsTextButton(Strings.SettingsRemove, () =>
        {
            imagePath.Text = "—";
            ApplyBackgroundImageChoice(string.Empty);
        });
        remove.Visibility = _config.BackgroundImage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        Border browse = SettingsTextButton(Strings.SettingsBrowse, async () =>
        {
            string? path = await PickBackgroundImageAsync();
            if (path is null) return;
            imagePath.Text = path;
            remove.Visibility = Visibility.Visible;
            ApplyBackgroundImageChoice(path);
        });

        var imageActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        imageActions.Children.Add(imagePath);
        imageActions.Children.Add(browse);
        imageActions.Children.Add(remove);

        page.Children.Add(SettingsGroup(Strings.SettingsBackground, SettingsCard(
            SettingsRow(Strings.SettingsBackgroundOpacity, null, opacityRow),
            SettingsRow(Strings.SettingsBackgroundBlur, null,
                SettingsSwitch(_config.BackgroundBlur, ApplyBackgroundBlurChoice)),
            SettingsRow(Strings.SettingsBackgroundImage, null, imageActions))));

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
            MinHeight = 26,
            FontSize = Theme.FontSmall,
            Padding = new Thickness(9, 0, 9, 0),
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
                SettingsSwitch(_config.TabInheritWorkingDirectory, ApplyTabInheritWorkingDirectoryChoice)))));

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

        return page;
    }

    // ---------------- notifications ----------------

    private FrameworkElement BuildNotificationsSettingsPage()
    {
        var page = SettingsPageStack();

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

        NumberBox after = SettingsNumber(
            Math.Round(_config.NotifyOnCommandFinishAfter.TotalSeconds), 0, 3600,
            ApplyNotifyAfterChoice);

        page.Children.Add(SettingsGroup(Strings.SettingsNotifyCommand, SettingsCard(
            SettingsRow(Strings.SettingsNotifyOnFinish, Strings.SettingsNotifyOnFinishDescription, when),
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
                })),
            SettingsRow(Strings.SettingsNotifyAfter, null, SettingsUnit(after, "s")))));

        page.Children.Add(SettingsGroup(Strings.SettingsDesktopNotifications, SettingsCard(
            SettingsRow(Strings.SettingsDesktopNotificationsAllow,
                Strings.SettingsDesktopNotificationsDescription,
                SettingsSwitch(_config.DesktopNotifications, ApplyDesktopNotificationsChoice)))));

        NumberBox volume = SettingsNumber(
            Math.Round(_config.BellAudioVolume * 100), 0, 100, ApplyBellVolumeChoice);

        page.Children.Add(SettingsGroup(Strings.SettingsBell, SettingsCard(
            SettingsRow(Strings.SettingsBellSystem, null,
                BellFeatureSwitch(_config.BellSystem, on => _config.BellSystem = on)),
            SettingsRow(Strings.SettingsBellAudio, null,
                BellFeatureSwitch(_config.BellAudio, on => _config.BellAudio = on)),
            SettingsRow(Strings.SettingsBellAttention, null,
                BellFeatureSwitch(_config.BellAttention, on => _config.BellAttention = on)),
            SettingsRow(Strings.SettingsBellTitle, null,
                BellFeatureSwitch(_config.BellTitle, on => _config.BellTitle = on)),
            SettingsRow(Strings.SettingsBellBorder, null,
                BellFeatureSwitch(_config.BellBorder, on => _config.BellBorder = on)),
            SettingsRow(Strings.SettingsBellVolume, null, SettingsUnit(volume, "%")))));

        return page;
    }

    private FrameworkElement BellFeatureSwitch(bool value, Action<bool> set) =>
        SettingsSwitch(value, on =>
        {
            set(on);
            SaveBellFeatures();
        });

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

    private void ApplyNotifyAfterChoice(double seconds)
    {
        _config.NotifyOnCommandFinishAfter = TimeSpan.FromSeconds(seconds);
        Config.SaveValue("notify-on-command-finish-after",
            seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s");
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

    private FrameworkElement BuildKeysSettingsPage()
    {
        var page = SettingsPageStack();

        Dictionary<string, string> labels = KeybindActionLabels();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        // Every action this build can run, grouped the way the commands are used. The action
        // ids are the ones a "keybind" line names; the labels and the chords both come from
        // the live configuration, so the page always shows what is actually bound.
        (string Title, string[] Actions)[] groups =
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

        foreach ((string title, string[] actions) in groups)
        {
            var rows = new List<FrameworkElement>();
            foreach (string action in actions)
            {
                listed.Add(action);
                rows.Add(KeybindRow(KeybindLabel(labels, action), action));
            }
            page.Children.Add(SettingsGroup(title, SettingsCard(rows.ToArray())));
        }

        // Safety net: a bindable action the groups above forgot still shows up instead of
        // disappearing from the page. Only bound ones are added, so the section stays empty
        // in the normal case.
        var extra = new List<FrameworkElement>();
        foreach (KeyValuePair<string, string> entry in labels)
        {
            if (listed.Contains(entry.Key) || _keybinds.Chords(entry.Key).Count == 0) continue;
            extra.Add(KeybindRow(entry.Value, entry.Key));
        }
        if (extra.Count > 0)
            page.Children.Add(SettingsGroup(Strings.SettingsKeysGroupOther, SettingsCard(extra.ToArray())));

        return page;
    }

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

    /// <summary>One shortcut row: the action's label and every chord the config leaves bound.</summary>
    private FrameworkElement KeybindRow(string label, string action)
    {
        List<string> chords = _keybinds.Chords(action);
        if (chords.Count == 0)
        {
            TextBlock none = Label(Strings.SettingsKeysUnbound, 10.5, TextFaintBrush());
            none.VerticalAlignment = VerticalAlignment.Center;
            return SettingsRow(label, null, none);
        }

        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (string chord in chords) keys.Children.Add(SettingsKbd(chord));
        return SettingsRow(label, null, keys);
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

        var body = new StackPanel();
        body.Children.Add(SettingsRow(Strings.AboutRepository, null, RepositoryControl()));
        body.Children.Add(SettingsRow(Strings.AboutLicense, null,
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
        titles.Children.Add(Label("Gui", Theme.FontBody, TextStrongBrush()));
        titles.Children.Add(Label(Strings.AboutCopyright(DateTime.Now.Year), Theme.FontCaption, TextFaintBrush()));

        // The version sits at the end of the header row, where the toolkit expander put its
        // content, rather than inside the part that opens.
        TextBlock version = Label(AppVersion(), Theme.FontBody, TextFaintBrush());
        version.VerticalAlignment = VerticalAlignment.Center;

        FontIcon chevron = Glyph('\uE76C', 10, TextFaintBrush());
        chevron.VerticalAlignment = VerticalAlignment.Center;

        var headerGrid = new Grid { Padding = new Thickness(13, 10, 13, 10), ColumnSpacing = 10 };
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
        header.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, header)) return;
            bool open = content.Visibility != Visibility.Visible;
            content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
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
        var copy = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            Background = ClearBrush(),
            Child = copyIcon,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Tips.Set(copy, Strings.AboutCopyRepository);
        copy.PointerEntered += (_, _) => copy.Background = ChipHoverBrush();
        copy.PointerExited += (_, _) => copy.Background = ClearBrush();
        copy.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, copy)) return;
            ClipboardAccess.TryWriteText(RepositoryUrl);
        };

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

        return new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusField),
            BorderThickness = new Thickness(1),
            BorderBrush = PanelStrokeBrush(),
            Child = stack,
        };
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
                FontSize = Theme.FontSmall,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var button = new Border
            {
                Height = 22,
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
            MinHeight = 26,
            FontSize = Theme.FontSmall,
            Padding = new Thickness(9, 0, 26, 0),
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
            MinHeight = 26,
            FontSize = Theme.FontSmall,
            Padding = new Thickness(9, 0, 26, 0),
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
        bool on = value;
        var knob = new Border
        {
            Width = 14,
            Height = 14,
            // Half the side: the knob is a circle, so it is not on the radius scale.
            CornerRadius = new CornerRadius(7),
            // White in both palettes: it sits on the accent when on and on ControlFillOff
            // when off, and both keep 3:1 against it.
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(on ? 0 : 3, 0, on ? 3 : 0, 0),
        };
        var track = new Border
        {
            Width = 36,
            Height = 20,
            // Half the height: the track is a pill, so it is not on the radius scale.
            CornerRadius = new CornerRadius(10),
            Background = on ? AccentBrush() : ControlFillOffBrush(),
            Child = knob,
            VerticalAlignment = VerticalAlignment.Center,
        };
        track.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, track)) return;
            on = !on;
            track.Background = on ? AccentBrush() : ControlFillOffBrush();
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = new Thickness(on ? 0 : 3, 0, on ? 3 : 0, 0);
            changed(on);
        };
        return track;
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
            FontSize = Theme.FontSmall,
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
    /// Every installed family DirectWrite marks as monospaced, sorted by name. The bundled
    /// Nerd Font is always listed so the default stays selectable even before it is loaded.
    /// </summary>
    private static List<string> MonospaceFontFamilies()
    {
        if (_monospaceFamilies is not null) return _monospaceFamilies;

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using CanvasFontSet set = CanvasFontSet.GetSystemFontSet();
            foreach (CanvasFontFace face in set.Fonts)
            {
                if (!face.IsMonospaced) continue;
                foreach (KeyValuePair<string, string> pair in face.FamilyNames)
                {
                    names.Add(pair.Value);
                    break;
                }
            }
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.MonospaceFontFamilies", error);
        }

        names.Add(DefaultFontFamily);
        _monospaceFamilies = names.ToList();
        return _monospaceFamilies;
    }

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
            Height = 26,
            MinHeight = 26,
            FontSize = Theme.FontSmall,
            // The stock control lays single-line text out from the top of its content box;
            // a 5 DIP top inset is what centres an 11 px line in the 26 DIP field.
            Padding = new Thickness(9, 5, 9, 0),
            BorderBrush = PanelStrokeBrush(),
            Background = ClearBrush(),
            CornerRadius = new CornerRadius(Theme.RadiusField),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // The stock template reserves a 32 DIP content box, which would push the text up
        // again in a shorter field; drop that floor.
        box.Resources["TextControlThemeMinHeight"] = 0.0;
        box.Resources["TextControlThemePadding"] = new Thickness(9, 5, 9, 0);

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

    private Border SettingsTextButton(string text, Action clicked)
    {
        var label = Label(text, 11, TextBodyBrush());
        label.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Padding = new Thickness(9, 4, 9, 4),
            CornerRadius = new CornerRadius(Theme.RadiusControl),
            BorderThickness = new Thickness(1),
            BorderBrush = PanelStrokeBrush(),
            Background = ClearBrush(),
            Child = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.PointerEntered += (_, _) =>
        {
            button.Background = ChipHoverBrush();
            label.Foreground = TextStrongBrush();
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = ClearBrush();
            label.Foreground = TextBodyBrush();
        };
        button.PointerPressed += (_, args) =>
        {
            args.Handled = true;
            if (!IsPrimaryPress(args, button)) return;
            clicked();
        };
        return button;
    }

    private Border SettingsKbd(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = Theme.FontCaption,
            FontFamily = new FontFamily("Cascadia Mono"),
            Foreground = TextFaintBrush(),
        };
        return new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusChip),
            Padding = new Thickness(5, 3, 5, 3),
            Background = ChipHoverBrush(),
            Child = label,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }
}
