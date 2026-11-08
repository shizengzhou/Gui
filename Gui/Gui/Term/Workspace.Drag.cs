using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Gui.Term;

/// <summary>
/// Ghostty's split drag and drop. The handle in a pane header grabs that pane, a small card
/// follows the pointer, an overlay paints the half of the target split it will land on, and
/// dropping rewrites the tab's split tree. The pane itself never leaves its session, so the
/// shell, its scrollback and its working directory move with it.
/// </summary>
public sealed partial class Workspace
{
    /// <summary>Where the dragged pane lands relative to the split it is dropped on.</summary>
    private enum DropSide { Left, Right, Top, Bottom }

    private TerminalPane? _dragPane;
    private TerminalPane? _dropPane;
    private DropSide _dropSide;
    private Grid? _dragLayer;
    private Border? _dropHighlight;
    private Border? _dragGhost;
    private TranslateTransform? _dragGhostShift;

    // ---------------- drag ----------------

    private void BeginPaneDrag(TerminalPane pane)
    {
        if (_dragPane is not null) EndPaneDrag(_dragPane, false);
        if (_tabs.Count == 0) return;
        if (!TabContainsPane(_tabs[_active], pane)) return;

        // A zoomed tab only paints the focused pane, so there is nothing to drop onto.
        if (_tabs[_active].Zoomed) return;

        _dragPane = pane;
        _dropPane = null;

        // The source keeps painting but recedes, so the eye follows the floating card.
        pane.Visual.Opacity = 0.45;
        BuildDragLayer(pane);
    }

    private void MovePaneDrag(TerminalPane pane, Point point)
    {
        if (!ReferenceEquals(pane, _dragPane) || _surfaceHost is null) return;

        // The pane reports points in its own visual; the overlay lives over the surface host.
        Point local = pane.Visual.TransformToVisual(_surfaceHost).TransformPoint(point);
        UpdateDropTarget(local);
        MoveDragGhost(local);
    }

    private void EndPaneDrag(TerminalPane pane, bool commit)
    {
        if (!ReferenceEquals(pane, _dragPane)) return;

        TerminalPane? target = _dropPane;
        DropSide side = _dropSide;
        _dragPane = null;
        _dropPane = null;

        pane.Visual.Opacity = 1.0;
        RemoveDragLayer();

        if (!commit || target is null || ReferenceEquals(target, pane)) return;
        if (_tabs.Count == 0) return;

        Tab tab = _tabs[_active];
        if (!TabContainsPane(tab, pane) || !TabContainsPane(tab, target)) return;
        MovePaneTo(tab, pane, target, side);
    }

    // ---------------- drop target ----------------

    /// <summary>
    /// Picks the split under the pointer and which of its halves the pane will take. The
    /// split is diagonal: the dominant offset from the target's centre wins, so the drop
    /// reads as "the side the pointer is on".
    /// </summary>
    private void UpdateDropTarget(Point local)
    {
        if (_tabs.Count == 0) return;

        TerminalPane? target = null;
        DropSide side = DropSide.Left;
        Rect zone = default;

        ForEachPane(_tabs[_active], candidate =>
        {
            if (target is not null || ReferenceEquals(candidate, _dragPane)) return;

            Rect bounds = PaneBounds(candidate);
            if (bounds.Width <= 0.0 || bounds.Height <= 0.0) return;
            if (!bounds.Contains(local)) return;

            double x = local.X - (bounds.X + bounds.Width / 2.0);
            double y = local.Y - (bounds.Y + bounds.Height / 2.0);
            if (Math.Abs(x) * bounds.Height >= Math.Abs(y) * bounds.Width)
                side = x < 0.0 ? DropSide.Left : DropSide.Right;
            else
                side = y < 0.0 ? DropSide.Top : DropSide.Bottom;

            target = candidate;
            zone = DropZone(bounds, side);
        });

        if (ReferenceEquals(target, _dropPane) && (target is null || side == _dropSide)) return;

        _dropPane = target;
        _dropSide = side;
        if (_dropHighlight is null) return;

        if (target is null)
        {
            _dropHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        _dropHighlight.Margin = new Thickness(zone.X, zone.Y, 0, 0);
        _dropHighlight.Width = zone.Width;
        _dropHighlight.Height = zone.Height;
        _dropHighlight.Visibility = Visibility.Visible;
    }

    /// <summary>The half of the target split the dragged pane will occupy, inset a little.</summary>
    private static Rect DropZone(Rect bounds, DropSide side)
    {
        const double gap = 2.0;
        double width = bounds.Width;
        double height = bounds.Height;
        double halfWidth = Math.Max(0.0, width / 2.0 - gap * 2.0);
        double halfHeight = Math.Max(0.0, height / 2.0 - gap * 2.0);
        double innerWidth = Math.Max(0.0, width - gap * 2.0);
        double innerHeight = Math.Max(0.0, height - gap * 2.0);

        return side switch
        {
            DropSide.Left => new Rect(bounds.X + gap, bounds.Y + gap, halfWidth, innerHeight),
            DropSide.Right => new Rect(bounds.X + width / 2.0 + gap, bounds.Y + gap, halfWidth, innerHeight),
            DropSide.Top => new Rect(bounds.X + gap, bounds.Y + gap, innerWidth, halfHeight),
            _ => new Rect(bounds.X + gap, bounds.Y + height / 2.0 + gap, innerWidth, halfHeight),
        };
    }

    // ---------------- tree surgery ----------------

    /// <summary>
    /// Lifts the pane out of its tab and puts it back beside the target: left/right create a
    /// column split, top/bottom a row split, and the pane keeps its live session throughout.
    /// </summary>
    private void MovePaneTo(Tab tab, TerminalPane pane, TerminalPane target, DropSide side)
    {
        bool vertical = side is DropSide.Top or DropSide.Bottom;
        bool first = side is DropSide.Left or DropSide.Top;

        bool found = false;
        TerminalPane? nextFocus = null;
        SplitNode? root = RemoveLeaf(tab.Root, pane, ref found, ref nextFocus);
        if (!found || root is null) return;

        // The target is looked up in the tree after the removal, so a target that moved up
        // with its collapsing parent is still found.
        if (!InsertLeaf(ref root, target, pane, vertical, first)) return;

        tab.Root = root;
        tab.Zoomed = false;
        Relayout(tab);
        FocusPane(tab, pane);
    }

    /// <summary>Replaces the target leaf with a new split that holds the dragged pane.</summary>
    private static bool InsertLeaf(ref SplitNode? node, TerminalPane target, TerminalPane pane,
        bool vertical, bool first)
    {
        if (node is null) return false;

        if (node.Pane is not null)
        {
            if (!ReferenceEquals(node.Pane, target)) return false;

            var split = new SplitNode { Vertical = vertical };
            var moved = new SplitNode { Pane = pane };
            var kept = new SplitNode { Pane = node.Pane };
            split.First = first ? moved : kept;
            split.Second = first ? kept : moved;
            node = split;
            return true;
        }

        if (InsertLeaf(ref node.First, target, pane, vertical, first)) return true;
        return InsertLeaf(ref node.Second, target, pane, vertical, first);
    }

    // ---------------- overlay ----------------

    private void BuildDragLayer(TerminalPane pane)
    {
        RemoveDragLayer();

        _dragLayer = new Grid { IsHitTestVisible = false };
        Canvas.SetZIndex(_dragLayer, 10);

        _dropHighlight = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Theme.AccentSoft()),
            BorderBrush = new SolidColorBrush(Theme.Accent()),
            BorderThickness = new Thickness(1.5),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _dragLayer.Children.Add(_dropHighlight);

        _dragGhost = BuildDragGhost(pane);
        _dragGhostShift = new TranslateTransform();
        _dragGhost.RenderTransform = _dragGhostShift;
        _dragLayer.Children.Add(_dragGhost);

        // Over the mounted tab surface; Activate() clears this host, and a drag never
        // survives a tab switch.
        _surfaceHost.Children.Add(_dragLayer);
    }

    /// <summary>A small card with the pane's shell icon and title, like the dragged split's tag.</summary>
    private static Border BuildDragGhost(TerminalPane pane)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        ImageSource? icon = pane.ShellIconSource;
        if (icon is not null)
        {
            content.Children.Add(new Image
            {
                Source = icon,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        else
        {
            content.Children.Add(new FontIcon
            {
                Glyph = "\uE756",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = new SolidColorBrush(Theme.TextFaint()),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = pane.Title.Length > 0 ? pane.Title : ShellDisplayName(pane.ShellCommand()),
            FontSize = 12,
            MaxWidth = 260,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Theme.TextStrong()),
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Height = 32,
            MinWidth = 72,
            Padding = new Thickness(10, 0, 12, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Theme.TermSurfaceBg()),
            BorderBrush = new SolidColorBrush(Theme.Accent()),
            BorderThickness = new Thickness(1),
            Opacity = 0.95,
            IsHitTestVisible = false,
            Child = content,
        };
    }

    private void MoveDragGhost(Point local)
    {
        if (_dragGhostShift is null) return;
        _dragGhostShift.X = local.X + 14.0;
        _dragGhostShift.Y = local.Y + 14.0;
    }

    private void RemoveDragLayer()
    {
        if (_dragLayer is not null &&
            VisualTreeHelper.GetParent(_dragLayer) is Panel owner)
        {
            owner.Children.Remove(_dragLayer);
        }
        _dragLayer = null;
        _dropHighlight = null;
        _dragGhost = null;
        _dragGhostShift = null;
    }
}
