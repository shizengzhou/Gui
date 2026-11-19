using System;

namespace Gui.Term;

/// <summary>
/// The saved screen and scrollback of a restored pane, waiting to be written into the
/// terminal once the pane knows its real grid size. The text is dropped the moment it has
/// been taken: it is only an intermediate form of the snapshot, and holding it would retain
/// the whole previous scrollback as a UTF-16 string for the pane's entire life.
/// </summary>
internal sealed class RestoreText
{
    public string Text { get; private set; } = string.Empty;

    public int Length => Text.Length;

    public void Set(string text) => Text = text;

    /// <summary>Returns the text and forgets it.</summary>
    public string Take()
    {
        string text = Text;
        Text = string.Empty;
        return text;
    }
}
