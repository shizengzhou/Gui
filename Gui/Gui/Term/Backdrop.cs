using System;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// The window backdrop for a translucent terminal, modelled on Windows Terminal: the
/// translucency is a single layer tinted with the terminal background at
/// "background-opacity". With "background-blur" the layer is DWM acrylic; without it (and
/// on Windows 11, the only version that supports unblurred transparency) it is a plain
/// translucent color brush. Because the tint lives here, the panes stay transparent and the
/// configured opacity is applied exactly once instead of being multiplied by the system
/// acrylic tint.
/// </summary>
internal sealed class TerminalBackdrop : SystemBackdrop
{
    private readonly Color _tint;
    private readonly float _opacity;
    private readonly bool _acrylic;
    private DesktopAcrylicController? _controller;
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionBrush? _brush;

    public TerminalBackdrop(Color tint, float opacity, bool acrylic)
    {
        _tint = tint;
        _opacity = Math.Clamp(opacity, 0f, 1f);
        _acrylic = acrylic;
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);

        // Windows 11 can show the desktop through a plain translucent brush; everything else
        // (or a failure to build that brush) goes through acrylic.
        if (!_acrylic && TryApplyPlain(target)) return;

        ApplyAcrylic(target, xamlRoot);
    }

    private void ApplyAcrylic(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        var controller = new DesktopAcrylicController();
        controller.AddSystemBackdropTarget(target);
        controller.SetSystemBackdropConfiguration(GetDefaultSystemBackdropConfiguration(target, xamlRoot));

        // Customize after the configuration is applied: the controller otherwise restores
        // the default light/dark values when the configuration theme is set. Its defaults
        // carry the material in the luminosity layer (tint opacity 0, luminosity 0.85), so
        // scaling that layer with the configured opacity gives exactly "background-opacity"
        // of the theme background over the desktop, like Windows Terminal.
        controller.TintColor = _tint;
        controller.TintOpacity = 0f;
        controller.LuminosityOpacity = _opacity;
        controller.FallbackColor = _tint;
        _controller = controller;
    }

    private bool TryApplyPlain(ICompositionSupportsSystemBackdrop target)
    {
        try
        {
            var compositor = new Windows.UI.Composition.Compositor();
            _brush = compositor.CreateColorBrush(Color.FromArgb(
                (byte)Math.Round(255 * _opacity), _tint.R, _tint.G, _tint.B));
            _compositor = compositor;
            target.SystemBackdrop = _brush;
            return true;
        }
        catch (Exception error)
        {
            Diag.LogException("backdrop.plain", error);
            _brush = null;
            _compositor = null;
            return false;
        }
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_controller is not null)
        {
            _controller.RemoveSystemBackdropTarget(target);
            _controller.Dispose();
            _controller = null;
        }

        if (_brush is not null)
        {
            target.SystemBackdrop = null;
            _brush.Dispose();
            _brush = null;
        }

        _compositor?.Dispose();
        _compositor = null;

        base.OnTargetDisconnected(target);
    }
}
