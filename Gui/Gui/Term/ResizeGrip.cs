using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Gui.Term;

/// <summary>
/// The transparent strip on the tab rail's inner edge. Its only job is the resize cursor:
/// WinUI keeps the cursor behind a protected member of UIElement and the panels are sealed,
/// so it can only be set from a small content control like this one. The split dividers use
/// the same control with the vertical cursor.
/// </summary>
internal sealed partial class ResizeGrip : UserControl
{
    public ResizeGrip(InputSystemCursorShape cursor = InputSystemCursorShape.SizeWestEast)
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        IsTabStop = false;
        ProtectedCursor = InputSystemCursor.Create(cursor);
    }
}
