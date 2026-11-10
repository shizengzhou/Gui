using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Text;

namespace Gui.Term;

public sealed partial class Workspace
{
    /// <summary>Which side of the focused pane the new split lands on.</summary>
    private enum SplitDirection
    {
        Right,
        Down,
        Left,
        Up,
    }

    private void SplitFocusedTail(TerminalPane created, bool vertical)
    {
        FocusPane(_tabs[_active], created);
        AnimatePaneEntrance(created, vertical);
    }

    /// <summary>Every divider in the active tab returns to the middle ("equalize_splits").</summary>
    private void EqualizeSplits()
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Root is null || tab.Root.Pane is not null) return;

        ResetSplitRatios(tab.Root);
        Relayout(tab);
    }

    private static void ResetSplitRatios(SplitNode? node)
    {
        if (node is null || node.Pane is not null) return;
        node.Ratio = 0.5;
        ResetSplitRatios(node.First);
        ResetSplitRatios(node.Second);
    }

    /// <summary>The 5% step one "resize_split" command applies.</summary>
    private const double SplitResizeStep = 0.05;

    /// <summary>
    /// Moves the divider that separates the focused pane along one axis ("resize_split").
    /// <paramref name="dx"/> is -1 / +1 for left / right and <paramref name="dy"/> for
    /// up / down.
    /// </summary>
    private void ResizeSplit(int dx, int dy)
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Zoomed || tab.Root is null || tab.Focused is null) return;

        bool sideBySide = dx != 0;
        int direction = sideBySide ? dx : dy;
        if (direction == 0) return;
        if (!FindDivider(tab.Root, tab.Focused, sideBySide, out SplitNode? divider, out bool focusedIsFirst) ||
            divider is null)
        {
            return;
        }

        // Moving the divider towards the focused side makes that pane bigger.
        bool growFirst = focusedIsFirst == (direction > 0);
        divider.Ratio = Math.Clamp(
            divider.Ratio + (growFirst ? SplitResizeStep : -SplitResizeStep), 0.05, 0.95);
        Relayout(tab);
    }

    /// <summary>
    /// Finds the nearest ancestor split that divides the focused pane along the requested
    /// axis. Side-by-side splits carry Vertical = false and divide along X.
    /// </summary>
    private static bool FindDivider(SplitNode? node, TerminalPane pane, bool sideBySide,
        out SplitNode? divider, out bool focusedIsFirst)
    {
        divider = null;
        focusedIsFirst = false;
        if (node is null || node.Pane is not null) return false;

        if (node.Vertical != sideBySide && node.First is not null && node.Second is not null)
        {
            if (ContainsPane(node.First, pane))
            {
                divider = node;
                focusedIsFirst = true;
                return true;
            }
            if (ContainsPane(node.Second, pane))
            {
                divider = node;
                focusedIsFirst = false;
                return true;
            }
        }

        return FindDivider(node.First, pane, sideBySide, out divider, out focusedIsFirst) ||
               FindDivider(node.Second, pane, sideBySide, out divider, out focusedIsFirst);
    }

    /// <summary>
    /// Cycles the focused pane in layout order ("Focus Split: Previous / Next").
    /// </summary>
    private void CyclePane(int delta)
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Zoomed || tab.Root is null || tab.Focused is null) return;

        var panes = new List<TerminalPane>();
        VisitPanes(tab.Root, panes.Add);
        if (panes.Count < 2) return;

        int index = panes.IndexOf(tab.Focused);
        if (index < 0) return;
        FocusPane(tab, panes[((index + delta) % panes.Count + panes.Count) % panes.Count]);
    }

    /// <summary>
    /// Moves the focus to the nearest pane in the requested direction ("Focus Split: Left"
    /// and friends). <paramref name="dx"/> is -1 / +1 for left / right and
    /// <paramref name="dy"/> for up / down.
    /// </summary>
    private void FocusPaneTowards(int dx, int dy)
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Zoomed || tab.Root is null || tab.Focused is null) return;

        Rect origin = PaneBounds(tab.Focused);
        double ox = origin.X + origin.Width / 2.0;
        double oy = origin.Y + origin.Height / 2.0;

        TerminalPane? best = null;
        double bestDistance = double.MaxValue;
        VisitPanes(tab.Root, candidate =>
        {
            if (ReferenceEquals(candidate, tab.Focused)) return;

            Rect bounds = PaneBounds(candidate);
            double cx = bounds.X + bounds.Width / 2.0;
            double cy = bounds.Y + bounds.Height / 2.0;

            // The pane has to sit on the requested side; distance then prefers the nearest
            // neighbour and penalises panes that are far off the axis.
            double along = dx != 0 ? (cx - ox) * dx : (cy - oy) * dy;
            if (along <= 0.5) return;
            double across = dx != 0 ? Math.Abs(cy - oy) : Math.Abs(cx - ox);
            double distance = along + across;
            if (distance >= bestDistance) return;
            bestDistance = distance;
            best = candidate;
        });

        if (best is not null) FocusPane(tab, best);
    }

    private void CloseFocusedPane()
    {
        if (_tabs.Count == 0) return;
        Tab tab = _tabs[_active];
        if (tab.Focused is null || tab.Root is null) return;
        if (PaneIsClosing(tab.Focused)) return;

        // A single-pane tab closes like a tab, as in Windows Terminal.
        if (tab.Root.Pane is not null)
        {
            CloseTab(_active);
            return;
        }

        bool vertical = false, closingFirst = false;
        if (tab.Zoomed || !SplitAxisForPane(tab.Root, tab.Focused, ref vertical, ref closingFirst))
        {
            FinishPaneRemoval(tab.Focused);
            return;
        }

        TerminalPane target = tab.Focused;
        _closingPanes.Add(target);
        AnimatePaneExit(target, vertical, closingFirst);
    }

    /// <summary>
    /// The surviving side of the split grows to the full parent extent while the closing
    /// pane keeps its size and is pushed out past the boundary; the parent clip hides it.
    /// </summary>
    private void AnimatePaneExit(TerminalPane pane, bool vertical, bool closingIsFirst)
    {
        FrameworkElement element = pane.Visual;
        if (VisualTreeHelper.GetParent(element) is not Grid parent || parent.Children.Count < 2 || _dispatcher is null)
        {
            FinishPaneRemoval(pane);
            return;
        }

        double closingExtent = vertical ? element.ActualHeight : element.ActualWidth;
        if (closingExtent <= 0.0)
        {
            FinishPaneRemoval(pane);
            return;
        }

        // Freeze every terminal in this tab: the split grid resizes the surviving pane on
        // every frame, and each resize would otherwise reflow a live shell. The panes get
        // one layout pass when the animation settles.
        Tab? owner = null;
        foreach (Tab candidate in _tabs)
        {
            if (!TabContainsPane(candidate, pane)) continue;
            owner = candidate;
            break;
        }
        owner?.SuspendPaneLayout();

        // Clip the split at its own bounds so the pushed pane vanishes exactly at the edge
        // instead of painting over the neighbours.
        parent.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, parent.ActualWidth, parent.ActualHeight),
        };

        // Keep the closing pane at its current size so its terminal does not reflow, and
        // pin it to the outer edge. Its grid column (or row) is then collapsed frame by
        // frame, which grows the surviving star-sized side.
        RowDefinition? rowDefinition = null;
        ColumnDefinition? column = null;
        if (vertical)
        {
            element.Height = closingExtent;
            element.VerticalAlignment = closingIsFirst ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            rowDefinition = parent.RowDefinitions[closingIsFirst ? 0 : 2];
            rowDefinition.Height = new GridLength(closingExtent);
            // The gap track would otherwise stay as a 6 DIP strip at the edge while the
            // survivor grows; the grid is thrown away once the animation settles.
            if (parent.RowDefinitions.Count > 2)
                parent.RowDefinitions[1].Height = new GridLength(0);
        }
        else
        {
            element.Width = closingExtent;
            element.HorizontalAlignment = closingIsFirst ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            column = parent.ColumnDefinitions[closingIsFirst ? 0 : 2];
            column.Width = new GridLength(closingExtent);
            if (parent.ColumnDefinitions.Count > 2)
                parent.ColumnDefinitions[1].Width = new GridLength(0);
        }

        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);

        DateTime start = DateTime.UtcNow;
        float sign = closingIsFirst ? -1.0f : 1.0f;
        const double duration = 110.0;
        // Collapse the 6 DIP gutter once instead of interpolating it on every frame: each
        // spacing change re-measures the whole split grid.
        parent.ColumnSpacing = 0;
        parent.RowSpacing = 0;

        timer.Tick += (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
            double progress = Math.Clamp(elapsed / duration, 0.0, 1.0);
            // Ease-out cubic: fast start, gentle settle.
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
            double remaining = closingExtent * (1.0 - eased);
            float pushed = sign * (float)(closingExtent * eased);

            if (vertical)
            {
                if (rowDefinition is not null) rowDefinition.Height = new GridLength(remaining);
                element.Translation = new System.Numerics.Vector3(0, pushed, 0);
            }
            else
            {
                if (column is not null) column.Width = new GridLength(remaining);
                element.Translation = new System.Numerics.Vector3(pushed, 0, 0);
            }

            if (progress >= 1.0)
            {
                timer.Stop();
                // The tree surgery is posted instead of run here: it releases this timer
                // and the pane's split grid while the tick is still on the stack.
                _dispatcher.TryEnqueue(() =>
                {
                    owner?.ResumePaneLayout();
                    if (PaneIsClosing(pane)) FinishPaneRemoval(pane);
                });
            }
        };

        _exitAnimations.Add(new ExitAnimation { Pane = pane, Timer = timer });
        timer.Start();
    }

    private void RetireExit(TerminalPane pane)
    {
        for (int i = _exitAnimations.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_exitAnimations[i].Pane, pane)) continue;
            _exitAnimations[i].Timer?.Stop();
            _exitAnimations.RemoveAt(i);
        }
    }

    private void FinishPaneRemoval(TerminalPane pane)
    {
        RetireExit(pane);
        _closingPanes.Remove(pane);

        for (int index = 0; index < _tabs.Count; index++)
        {
            Tab tab = _tabs[index];
            if (!TabContainsPane(tab, pane)) continue;

            // The pane is leaving the tab whichever way the tree ends up, so its shell stops
            // here; a pane that is dropped without this keeps its ConPTY host for good.
            pane.Close();

            if (tab.Root is null || tab.Root.Pane is not null)
            {
                CloseTab(index);
                return;
            }

            bool found = false;
            TerminalPane? next = null;
            tab.Root = RemoveLeaf(tab.Root, pane, ref found, ref next);
            if (!found || tab.Root is null) return;
            tab.Zoomed = false;

            Relayout(tab);
            next ??= FirstPane(tab.Root);
            if (next is not null) FocusPane(tab, next);
            return;
        }
    }

    /// <summary>
    /// Finds the split that directly owns the pane so the exit animation grows the
    /// surviving side and pushes the pane out along the same axis.
    /// </summary>
    private static bool SplitAxisForPane(SplitNode? node, TerminalPane pane, ref bool vertical, ref bool closingFirst)
    {
        if (node is null || node.Pane is not null) return false;

        if (node.First is { Pane: not null } && ReferenceEquals(node.First.Pane, pane))
        {
            vertical = node.Vertical;
            closingFirst = true;
            return true;
        }
        if (node.Second is { Pane: not null } && ReferenceEquals(node.Second.Pane, pane))
        {
            vertical = node.Vertical;
            closingFirst = false;
            return true;
        }
        return SplitAxisForPane(node.First, pane, ref vertical, ref closingFirst) ||
               SplitAxisForPane(node.Second, pane, ref vertical, ref closingFirst);
    }

    private bool PaneIsClosing(TerminalPane pane) => _closingPanes.Contains(pane);

    /// <summary>
    /// The layout reflows instantly; the new pane fades and slides in from the split
    /// direction so the resize reads as motion.
    /// </summary>
    private void AnimatePaneEntrance(TerminalPane pane, bool vertical)
    {
        // Reference recording: the new pane cross-fades in at its final slot (the layout
        // snaps within ~2 frames) — no slide.
        FrameworkElement element = pane.Visual;

        try
        {
            var compositor = CompositionTarget.GetCompositorForCurrentThread();
            var visual = ElementCompositionPreview.GetElementVisual(element);

            var ease = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.1f, 0.9f), new System.Numerics.Vector2(0.2f, 1.0f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0.0f, 0.0f);
            fade.InsertKeyFrame(1.0f, 1.0f, ease);
            fade.Duration = TimeSpan.FromMilliseconds(100);
            visual.StartAnimation("Opacity", fade);

            // Fade only. Animating the visual's Offset competes with layout (it drags the
            // pane onto its sibling) and "Translation" is not an animatable property of a
            // XAML element visual in this WinUI build.
        }
        catch (Exception error)
        {
            Diag.LogException("Workspace.AnimatePaneEntrance", error);
        }
    }

/// <summary>
    /// The zoom transition runs the focused pane between the two rectangles a layout rebuild
    /// would otherwise snap between: it is scaled from where it was to where it lands, so the
    /// change reads as motion. Only a zoom-out brings siblings back, and they fade in under the
    /// shrinking pane.
    /// </summary>
    private sealed class ZoomTransition
    {
        public Tab Tab = null!;
        public FrameworkElement Element = null!;
        public DispatcherQueueTimer? Timer;
        public readonly List<FrameworkElement> Siblings = new();
    }

    private Rect PaneBounds(TerminalPane pane)
    {
        FrameworkElement element = pane.Visual;
        if (element.ActualWidth <= 0.0 || element.ActualHeight <= 0.0) return default;
        Point origin = element.TransformToVisual(_surfaceHost).TransformPoint(new Point(0, 0));
        return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight);
    }

    private void AnimateZoomTransition(Tab tab, TerminalPane pane, Rect before)
    {
        if (_dispatcher is null || before.Width <= 1.0 || before.Height <= 1.0) return;

        // The rebuild has not been laid out yet, so the destination is only known after a
        // synchronous pass.
        _surfaceHost.UpdateLayout();
        Rect after = PaneBounds(pane);
        if (after.Width <= 1.0 || after.Height <= 1.0) return;
        if (Math.Abs(after.Width - before.Width) < 0.5 && Math.Abs(after.Height - before.Height) < 0.5)
            return;

        // Freeze the grids: a scaled visual does not reflow the shell, and the one layout pass
        // at the end settles the new size.
        tab.SuspendPaneLayout();

        FrameworkElement element = pane.Visual;
        element.RenderTransformOrigin = new Point(0, 0);
        var transform = new CompositeTransform();
        element.RenderTransform = transform;
        // A zoom-out shrinks the pane back into its cell; it has to paint over the sibling it
        // crosses on the way in.
        Canvas.SetZIndex(element, 1);

        var transition = new ZoomTransition { Tab = tab, Element = element };
        if (!tab.Zoomed)
        {
            ForEachPane(tab, other =>
            {
                if (ReferenceEquals(other, pane)) return;
                other.Visual.Opacity = 0.0;
                transition.Siblings.Add(other.Visual);
            });
        }

        var timer = _dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        transition.Timer = timer;

        DateTime start = DateTime.UtcNow;
        const double duration = 180.0;

        timer.Tick += (_, _) =>
        {
            double progress = Math.Clamp((DateTime.UtcNow - start).TotalMilliseconds / duration, 0.0, 1.0);
            // Ease-out cubic, the same curve the pane close animation uses.
            double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);

            double scaleX = (before.Width + (after.Width - before.Width) * eased) / after.Width;
            double scaleY = (before.Height + (after.Height - before.Height) * eased) / after.Height;
            double left = before.X + (after.X - before.X) * eased;
            double top = before.Y + (after.Y - before.Y) * eased;

            transform.ScaleX = scaleX;
            transform.ScaleY = scaleY;
            transform.TranslateX = left - after.X * scaleX;
            transform.TranslateY = top - after.Y * scaleY;

            foreach (FrameworkElement sibling in transition.Siblings) sibling.Opacity = eased;

            if (progress < 1.0) return;

            timer.Stop();
            // Posted like the close animation's teardown: it releases the timer while the
            // tick is still on the stack.
            _dispatcher.TryEnqueue(() => SettleZoomTransition());
        };

        _zoomTransition = transition;
        timer.Start();
    }

    /// <summary>Puts the pane back into its final geometry and releases the frozen grids.</summary>
    private void SettleZoomTransition()
    {
        ZoomTransition? transition = _zoomTransition;
        if (transition is null) return;
        _zoomTransition = null;

        transition.Timer?.Stop();
        transition.Element.RenderTransform = null;
        transition.Element.RenderTransformOrigin = new Point(0.5, 0.5);
        Canvas.SetZIndex(transition.Element, 0);
        foreach (FrameworkElement sibling in transition.Siblings) sibling.Opacity = 1.0;
        transition.Tab.ResumePaneLayout();
    }

    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;
        SettleZoomTransition();

        // A rename that is still open on this chip goes away with it; otherwise a closed
        // editor would keep the keyboard away from the terminal.
        if (_tabs[index].Renaming) EndRename(_tabs[index], false);

        if (_tabs.Count == 1)
        {
            // The last tab takes the window with it, like Ghostty. Leaving the tab in place
            // keeps the normal window-close path in charge, so "confirm-close-surface" can
            // still ask before a running command is stopped.
            _dispatcher?.TryEnqueue(() => _window?.Close());
            return;
        }

        // The tab is about to destroy its panes: drop any close animation that still
        // refers to one of them.
        Tab closing = _tabs[index];
        ForEachPane(closing, pane =>
        {
            RetireExit(pane);
            _closingPanes.Remove(pane);
            // The tab is leaving the tree, so its shells have to stop here: nothing else
            // reaches them, and a ConPTY that stays alive keeps its console host too.
            pane.Close();
        });
        StopChipSpring(closing);

        int previous = _active;
        _chipHost.Children.RemoveAt(index);
        _tabs.RemoveAt(index);

        // Closing a tab behind the active one must leave the selection alone: taking the
        // closed index would jump the selection forward (close the tab next to the active
        // one and the tab after it took over).
        Activate(TabSelection.AfterClose(previous, index, _tabs.Count));
        App.MarkSessionDirty();
    }

    // ---------------- headers ----------------

    /// <summary>
    /// Re-paints every chip. Coalesced and posted: the chip visuals are mutated from
    /// pointer handlers (hover, click) and from the tab strip's own scrolling, and changing
    /// text/font/automation properties while WinUI is still dispatching that input
    /// fail-fasts the process (0xC000027B).
    /// </summary>
    private void UpdateHeaders()
    {
        if (_headersQueued) return;
        _headersQueued = true;

        if (_dispatcher is null)
        {
            _headersQueued = false;
            ApplyHeaders();
            return;
        }
        _dispatcher.TryEnqueue(() =>
        {
            _headersQueued = false;
            Diag.Guard("ws.headers", ApplyHeaders);
        });
    }

    /// <summary>
    /// Hands the caption buttons' width to the pane that owns the island's top-right corner:
    /// the side layouts lift the island under them, so only that pane has to move its actions
    /// out of the way.
    /// </summary>
    private void ApplyPaneInsets(Tab tab)
    {
        if (_placement == TabPlacement.Top)
        {
            ForEachPane(tab, pane =>
            {
                pane.SetActionsInset(0.0);
                pane.SetInfoInset(0.0);
            });
            return;
        }

        // The chrome floats over the island in the side layouts, so the two corner panes have
        // to keep clear of whatever sits on top of them: the caption buttons on the right,
        // and the rail's own buttons when the rail is collapsed — on the left when it lives on
        // the left, on the right when it lives on the right.
        TerminalPane? left = TopLeftPane(tab.Root);
        TerminalPane? right = TopRightPane(tab.Root);
        bool collapsed = _sidebarCollapsed;

        // The caption buttons only hover over the pane while the island runs to the window's
        // right edge: always with a left rail, and with a right rail only once it is collapsed
        // (an open right rail takes that corner for itself).
        bool islandUnderCaptions = _placement == TabPlacement.Left
            || (_placement == TabPlacement.Right && collapsed);

        // The rail toggle floats over the pane's corner when the rail is collapsed. Its
        // button is 30 DIP wide; only that width plus a small gap is needed, since the pane
        // header adds its own 10 DIP of padding on top of this inset.
        double railButtons = collapsed ? (_placement == TabPlacement.Right ? 38.0 : 32.0) : 0.0;

        ForEachPane(tab, pane =>
        {
            bool isRight = ReferenceEquals(pane, right);
            bool isLeft = ReferenceEquals(pane, left);
            // 138 = the three 46px caption buttons plus the 6px gap the header keeps to the
            // pane's right edge; the pane header's own 6px trailing margin then leaves the
            // same 6px between the action buttons and the first caption button.
            double actions = isRight && islandUnderCaptions ? 138.0 : 0.0;
            if (isRight && collapsed && _placement == TabPlacement.Right) actions += railButtons;
            pane.SetActionsInset(actions);
            pane.SetInfoInset(isLeft && _placement == TabPlacement.Left ? railButtons : 0.0);
        });
    }

    /// <summary>Walks to the leaf that ends up in the top-left corner of the split tree.</summary>
    private static TerminalPane? TopLeftPane(SplitNode? node)
    {
        while (node is not null && node.Pane is null) node = node.First;
        return node?.Pane;
    }

    /// <summary>Walks to the leaf that ends up in the top-right corner of the split tree.</summary>
    private static TerminalPane? TopRightPane(SplitNode? node)
    {
        while (node is not null && node.Pane is null)
            node = node.Vertical ? node.First : node.Second;
        return node?.Pane;
    }

    /// <summary>
    /// Active chips are semibold, and bold text is wider than regular text: without this the
    /// chip grows when it is activated and shoves every neighbour along. Reserving the
    /// heavier width keeps every chip the size it has while active.
    /// </summary>
    private static void PinChipTitleWidth(Tab tab)
    {
        FontWeight weight = tab.ChipText.FontWeight;
        // MinWidth takes part in Measure, so measuring with the previous pin still applied
        // would return at least that width and the chip could only ever grow: any long title
        // (a path, a build step) would widen it permanently. Measure the bare text instead.
        tab.ChipText.MinWidth = 0;
        tab.ChipText.FontWeight = FontWeights.SemiBold;
        tab.ChipText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = tab.ChipText.DesiredSize.Width;
        tab.ChipText.FontWeight = weight;

        // The chip is capped at 220 DIP; a longer title trims there instead of widening it.
        tab.ChipText.MinWidth = Math.Min(width, 200.0);
    }

    private void ApplyHeaders()
    {
        for (int index = 0; index < _tabs.Count; index++)
        {
            Tab tab = _tabs[index];
            bool active = index == _active;

            if (tab.ChipText.Text != tab.Title)
            {
                tab.ChipText.Text = tab.Title;
                PinChipTitleWidth(tab);
            }
            // Active chips merge into the content island (app theme); inactive ones sit on
            // the title strip and follow the window theme.
            tab.ChipText.Foreground = active ? TextStrongBrush()
                : tab.Hovered ? ChromeTextStrongBrush() : ChromeTextBodyBrush();
            tab.ChipText.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            // The chip icon follows the tab's pane, like its header. A tab that was never
            // activated (opened by the CLI, or restored at startup) has no focused pane yet,
            // so it falls back to the one that will be shown when the tab is opened.
            TerminalPane? shown = tab.Focused ?? FirstPane(tab.Root);
            ImageSource? iconSource = shown?.ShellIconSource;
            if (!ReferenceEquals(iconSource, tab.ChipIconSource))
            {
                tab.ChipIconSource = iconSource;
                if (iconSource is null)
                {
                    tab.ChipIcon.Visibility = Visibility.Collapsed;
                    tab.ChipIconFallback.Visibility = Visibility.Visible;
                }
                else
                {
                    tab.ChipIcon.Source = iconSource;
                    tab.ChipIcon.Visibility = Visibility.Visible;
                    tab.ChipIconFallback.Visibility = Visibility.Collapsed;
                }
            }

            // The chip reports what that pane's program says it is doing (OSC 7501).
            if (shown?.ProgramStatus is { State: not ProgramState.Idle } reported)
            {
                tab.ChipStatusDot.Visibility = Visibility.Visible;
                tab.ChipStatusDot.Fill = new SolidColorBrush(Theme.ProgramStatusColor(reported.State));
            }
            else
            {
                tab.ChipStatusDot.Visibility = Visibility.Collapsed;
            }

            // Tooltip mirrors Windows Terminal's _UpdateToolTip: "<shell name>: <title>"
            // unless the two are equal, then the switch-to-tab chord on an italic line.
            // Ghostty binds Alt+1..8 to the tab slots; the ninth slot has no chord.
            string profile = tab.Focused is not null ? ShellDisplayName(tab.Focused.ShellCommand()) : string.Empty;
            string heading = profile.Length == 0 || tab.Title == profile ? tab.Title : profile + ": " + tab.Title;
            string shortcut = index < 8 ? "alt+" + (index + 1) : string.Empty;
            string content = shortcut.Length == 0 ? heading : heading + "\n" + shortcut;
            if (content != tab.ChipTipContent)
            {
                tab.ChipTipContent = content;
                tab.ChipTipText.Inlines.Clear();
                tab.ChipTipText.Inlines.Add(new Run { Text = heading });
                if (shortcut.Length > 0)
                {
                    tab.ChipTipText.Inlines.Add(new LineBreak());
                    tab.ChipTipText.Inlines.Add(new Run { Text = shortcut, FontStyle = FontStyle.Italic });
                }
            }

            tab.Chip.Background = active ? TermBgBrush()
                : tab.Hovered ? ChromeChipHoverBrush() : ClearBrush();

            // Keep the button in the layout so hovering never changes the tab width; only
            // its opacity and hit testing follow the hover state.
            bool showClose = tab.Hovered || active;
            tab.ChipClose.Visibility = Visibility.Visible;
            tab.ChipClose.Opacity = showClose ? 1.0 : 0.0;
            tab.ChipClose.IsHitTestVisible = showClose;

            bool previousHovered = index > 0 && _tabs[index - 1].Hovered;
            bool showDivider = TabSeparators.Show(index, _active, tab.Hovered, previousHovered,
                _placement == TabPlacement.Top);
            tab.ChipDivider.Opacity = showDivider ? 1.0 : 0.0;

            ApplyPaneInsets(tab);

            // Roving tab stop: only the active chip is reachable with Tab.
            tab.ChipFocus.IsTabStop = active;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab.ChipFocus, Strings.TabName(tab.Title));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab.ChipClose, Strings.CloseTab);
        }

        if (_ensureActiveVisible && EnsureActiveVisible()) _ensureActiveVisible = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_window is null) return;

        // "title" pins the window title; otherwise it follows the focused surface.
        string title = _config.WindowTitle;
        if (title.Length == 0)
        {
            title = "Terminal";
            if (_tabs.Count > 0)
            {
                Tab tab = _tabs[_active];
                if (tab.Focused is not null && tab.Focused.Title.Length > 0) title = tab.Focused.Title;
                else if (tab.Title.Length > 0) title = tab.Title;
            }
        }

        // Keep the taskbar / alt-tab entry in sync with the active tab.
        try
        {
            _window.AppWindow.Title = title;
        }
        catch
        {
            // The window may already be closing.
        }
    }
}
