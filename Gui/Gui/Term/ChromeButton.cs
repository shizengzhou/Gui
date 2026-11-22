using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// An icon button for the window chrome: a real <see cref="Button"/>, so Tab reaches it, Enter
/// and Space press it, and accessibility tools see a button rather than a painted rectangle.
///
/// The design's chrome colours are handed to it as colour producers and pinned into the theme
/// resources its template reads for hover and pressed. The brushes are created once and only
/// their colour changes, because a ThemeResource lookup keeps the instance it resolved: mutating
/// the brush repaints the states, replacing it would not.
/// </summary>
internal sealed class ChromeButton : Button
{
    private readonly SolidColorBrush _fill = new();
    private readonly SolidColorBrush _hoverFill = new();
    private readonly SolidColorBrush _pressedFill = new();
    private readonly SolidColorBrush _iconBrush = new();
    private readonly SolidColorBrush _hoverIconBrush = new();

    private readonly FontIcon? _glyph;
    private readonly Path? _path;

    private Func<Color> _icon = Theme.ChromeIconFaint;
    private Func<Color>? _hoverIcon;
    private Func<Color>? _fillColor;
    private Func<Color>? _hoverFillColor;
    private Func<Color>? _pressedFillColor;
    private bool _hovered;
    private bool _active;

    public ChromeButton(UIElement icon, double width, double height, CornerRadius radius)
    {
        Width = width;
        Height = height;
        // The template's padding and minimums would make a 24px row button taller than the row.
        Padding = new Thickness(0);
        MinWidth = 0;
        MinHeight = 0;
        BorderThickness = new Thickness(0);
        CornerRadius = radius;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        // A press on the chrome must not move the keyboard out of the terminal; Tab can
        // still reach the button, and Space or Enter then press it.
        AllowFocusOnInteraction = false;
        Content = icon;

        _glyph = icon as FontIcon;
        _path = icon as Path;

        // The template paints the normal state from Background and the others from these
        // resources. Handing it the same brush instances keeps hover on the chrome palette
        // instead of the app theme's, and keeps a colour change visible without a reload.
        Resources["ButtonBackground"] = _fill;
        Resources["ButtonBackgroundPointerOver"] = _hoverFill;
        Resources["ButtonBackgroundPressed"] = _pressedFill;
        Resources["ButtonBackgroundDisabled"] = _fill;
        Resources["ButtonBorderBrush"] = _fill;
        Resources["ButtonBorderBrushPointerOver"] = _hoverFill;
        Resources["ButtonBorderBrushPressed"] = _pressedFill;
        Resources["ButtonBorderBrushDisabled"] = _fill;
        Resources["ButtonForeground"] = _iconBrush;
        Resources["ButtonForegroundPointerOver"] = _hoverIconBrush;
        Resources["ButtonForegroundPressed"] = _hoverIconBrush;
        Background = _fill;

        PointerEntered += (_, _) =>
        {
            _hovered = true;
            PaintIcon(_hoverIcon is null ? _iconBrush : _hoverIconBrush);
        };
        PointerExited += (_, _) =>
        {
            _hovered = false;
            PaintIcon(_iconBrush);
        };

        ApplyTheme();
    }

    /// <summary>
    /// The colours this button draws its four states with. <paramref name="hoverIcon"/> null
    /// keeps the icon colour on hover, which is what a rail tool button does; the fills default
    /// to no fill and to the hover fill for the pressed state.
    /// </summary>
    public void SetColors(Func<Color> icon, Func<Color>? hoverIcon = null,
        Func<Color>? fill = null, Func<Color>? hoverFill = null, Func<Color>? pressedFill = null)
    {
        _icon = icon;
        _hoverIcon = hoverIcon;
        _fillColor = fill;
        _hoverFillColor = hoverFill;
        _pressedFillColor = pressedFill;
        ApplyTheme();
    }

    /// <summary>The name accessibility tools read out; the tooltip shows the same words.</summary>
    public void SetAccessibleName(string name) => AutomationProperties.SetName(this, name);

    /// <summary>
    /// Keeps the button in its hover fill while what it opened (a menu) is still on screen,
    /// the way a pressed caption or menu button stays lit until the flyout closes.
    /// </summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        ApplyTheme();
    }

    /// <summary>Re-reads every colour. Called from the chrome's theme pass.</summary>
    public void ApplyTheme()
    {
        _fill.Color = _fillColor?.Invoke() ?? Colors.Transparent;
        _hoverFill.Color = _hoverFillColor?.Invoke() ?? _fill.Color;
        _pressedFill.Color = _pressedFillColor?.Invoke() ?? _hoverFill.Color;
        // An active button owns the open menu: its resting state is the hover state.
        if (_active) _fill.Color = _hoverFill.Color;
        _iconBrush.Color = _icon();
        _hoverIconBrush.Color = _hoverIcon?.Invoke() ?? _iconBrush.Color;
        PaintIcon((_active || _hovered) && _hoverIcon is not null ? _hoverIconBrush : _iconBrush);
    }

    private void PaintIcon(Brush brush)
    {
        if (_glyph is not null) _glyph.Foreground = brush;
        else if (_path is not null) _path.Stroke = brush;
    }
}
