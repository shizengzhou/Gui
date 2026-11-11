using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Gui.Term;

/// <summary>
/// Hover labels for the chrome: one shape everywhere, the action's name over the key chord
/// it is bound to. The blocks come back so a caller can retitle them later (the maximize
/// button, and the pane's copy/paste rows, follow the window and buffer state).
/// </summary>
internal static class Tips
{
    // Shared brushes, recolored when the theme changes: a brush captured per call would keep
    // the old theme's color.
    private static readonly SolidColorBrush TitleBrush = new(Theme.TextStrong());
    private static readonly SolidColorBrush ChordBrush = new(Theme.TextFaint());

    public static (TextBlock Title, TextBlock Chord) Set(
        FrameworkElement target, string title, string? chord = null)
    {
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = Theme.FontBody,
            Foreground = TitleBrush,
        };
        var chordBlock = new TextBlock
        {
            Text = chord ?? string.Empty,
            FontSize = Theme.FontSmall,
            FontStyle = FontStyle.Italic,
            Foreground = ChordBrush,
            Visibility = string.IsNullOrEmpty(chord) ? Visibility.Collapsed : Visibility.Visible,
        };

        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(titleBlock);
        body.Children.Add(chordBlock);
        ToolTipService.SetToolTip(target, body);
        return (titleBlock, chordBlock);
    }

    public static void RefreshBrushes()
    {
        TitleBrush.Color = Theme.TextStrong();
        ChordBrush.Color = Theme.TextFaint();
    }
}