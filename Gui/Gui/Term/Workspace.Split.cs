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
    private void SplitFocusedTail(TerminalPane created, bool vertical)
    {
        FocusPane(_tabs[_active], created);
        AnimatePaneEntrance(created, vertical);
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
            rowDefinition = parent.RowDefinitions[closingIsFirst ? 0 : 1];
            rowDefinition.Height = new GridLength(closingExtent);
        }
        else
        {
            element.Width = closingExtent;
            element.HorizontalAlignment = closingIsFirst ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            column = parent.ColumnDefinitions[closingIsFirst ? 0 : 1];
            column.Width = new GridLength(closingExtent);
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

    private void CloseTab(int index)
    {
        if (index < 0 || index >= _tabs.Count) return;

        // The tab is about to destroy its panes: drop any close animation that still
        // refers to one of them.
        Tab closing = _tabs[index];
        ForEachPane(closing, pane =>
        {
            RetireExit(pane);
            _closingPanes.Remove(pane);
        });
        StopChipSpring(closing);

        _chipHost.Children.RemoveAt(index);
        _tabs.RemoveAt(index);

        if (_tabs.Count == 0)
        {
            NewTab();
            Activate(0);
            return;
        }
        Activate(Math.Min(index, _tabs.Count - 1));
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

    private void ApplyHeaders()
    {
        for (int index = 0; index < _tabs.Count; index++)
        {
            Tab tab = _tabs[index];
            bool active = index == _active;

            tab.ChipText.Text = tab.Title;
            // Active chips merge into the content island (app theme); inactive ones sit on
            // the title strip and follow the window theme.
            tab.ChipText.Foreground = active ? TextStrongBrush()
                : tab.Hovered ? ChromeTextStrongBrush() : ChromeTextBodyBrush();
            tab.ChipText.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

            // The chip icon follows the focused pane's shell, like its header.
            ImageSource? iconSource = tab.Focused?.ShellIconSource;
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

            // Tooltip mirrors Windows Terminal's _UpdateToolTip: "<shell name>: <title>"
            // unless the two are equal, then the switch-to-tab chord on an italic line.
            string profile = tab.Focused is not null ? ShellDisplayName(tab.Focused.ShellCommand()) : string.Empty;
            string heading = profile.Length == 0 || tab.Title == profile ? tab.Title : profile + ": " + tab.Title;
            string shortcut = index < 9 ? "ctrl+alt+" + (index + 1) : string.Empty;
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

            tab.Chip.Background = active ? CardFillBrush()
                : tab.Hovered ? ChromeChipHoverBrush() : ClearBrush();

            // Keep the button in the layout so hovering never changes the tab width; only
            // its opacity and hit testing follow the hover state.
            bool showClose = tab.Hovered || active;
            tab.ChipClose.Visibility = Visibility.Visible;
            tab.ChipClose.Opacity = showClose ? 1.0 : 0.0;
            tab.ChipClose.IsHitTestVisible = showClose;

            bool hasNext = index + 1 < _tabs.Count;
            bool nextQuiet = hasNext && index + 1 != _active && !_tabs[index + 1].Hovered;
            bool showDivider = _placement == TabPlacement.Top && hasNext && !active && !tab.Hovered && nextQuiet;
            tab.ChipDivider.Opacity = showDivider ? 1.0 : 0.0;

            // Roving tab stop: only the active chip is reachable with Tab.
            tab.ChipFocus.IsTabStop = active;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tab.ChipFocus, "标签页: " + tab.Title);
        }

        if (_ensureActiveVisible && EnsureActiveVisible()) _ensureActiveVisible = false;
        ScheduleTabArcs();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_window is null) return;

        string title = "Terminal";
        if (_tabs.Count > 0)
        {
            Tab tab = _tabs[_active];
            if (tab.Focused is not null && tab.Focused.Title.Length > 0) title = tab.Focused.Title;
            else if (tab.Title.Length > 0) title = tab.Title;
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
