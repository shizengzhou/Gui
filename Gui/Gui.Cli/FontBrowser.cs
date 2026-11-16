using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The interactive font browser behind "gui +list-fonts" when stdout is a terminal: a list of
/// families on the left, a preview on the right rendered from the family's own outlines, and
/// Enter writes "font-family" into the config. It follows tsp's font browser and, like kitty's
/// choose-fonts kitten, shows the sample as a real image when the terminal can take one.
/// </summary>
internal static class FontBrowser
{
    private const string Accent = "\x1b[38;5;111m";
    private const string Muted = "\x1b[38;5;245m";
    private const string Faint = "\x1b[38;5;240m";
    private const string Strong = "\x1b[97m";
    private const string Reset = "\x1b[0m";

    /// <summary>Rows the note under the sample and the footer need.</summary>
    private const int ChromeRows = 3;

    public static int Run(List<FontFamilyInfo> families, string current, bool images)
    {
        int cursor = Math.Max(0, families.FindIndex(f =>
            string.Equals(f.Name, current, StringComparison.OrdinalIgnoreCase)));
        int offset = 0;
        string query = string.Empty;
        string? chosen = null;

        bool interactive = !Console.IsOutputRedirected && !Console.IsInputRedirected;
        if (!interactive)
        {
            Console.Error.WriteLine("gui: +list-fonts needs a terminal to browse; redirecting prints the list");
            return 1;
        }

        Console.Out.Write("\x1b[?1049h\x1b[?25l");
        try
        {
            int drawnWidth = 0;
            int drawnHeight = 0;
            while (true)
            {
                List<FontFamilyInfo> shown = FontCatalog.Filter(families, query);
                if (cursor >= shown.Count) cursor = Math.Max(0, shown.Count - 1);

                if (Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                {
                    drawnWidth = Console.WindowWidth;
                    drawnHeight = Console.WindowHeight;
                    Draw(shown, cursor, ref offset, query, images);
                }

                // Redraw when the window changes size: the preview image is placed on the
                // cell grid, so a resize would leave it hanging where it was drawn.
                while (!Console.KeyAvailable)
                {
                    Thread.Sleep(40);
                    if (Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                        break;
                }
                if (!Console.KeyAvailable) continue;

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.UpArrow || key.KeyChar == 'k')
                {
                    if (cursor > 0) cursor--;
                }
                else if (key.Key == ConsoleKey.DownArrow || key.KeyChar == 'j')
                {
                    if (cursor < shown.Count - 1) cursor++;
                }
                else if (key.Key == ConsoleKey.PageUp)
                {
                    cursor = Math.Max(0, cursor - (ListRows() - 1));
                }
                else if (key.Key == ConsoleKey.PageDown)
                {
                    cursor = Math.Min(Math.Max(0, shown.Count - 1), cursor + ListRows() - 1);
                }
                else if (key.Key == ConsoleKey.Home)
                {
                    cursor = 0;
                }
                else if (key.Key == ConsoleKey.End)
                {
                    cursor = Math.Max(0, shown.Count - 1);
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    if (query.Length > 0) query = query[..^1];
                    cursor = 0;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    if (query.Length > 0)
                    {
                        query = string.Empty;
                        cursor = 0;
                        continue;
                    }
                    break;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    if (shown.Count > 0)
                    {
                        chosen = shown[cursor].Name;
                        break;
                    }
                }
                else if (key.KeyChar is 'q' or 'Q' && query.Length == 0)
                {
                    break;
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    // Typing filters, the way tsp's browser and kitty's kitten behave.
                    query += key.KeyChar;
                    cursor = 0;
                }
            }
        }
        finally
        {
            Console.Out.Write("\x1b_Ga=d,d=A\x1b\\");
            Console.Out.Write("\x1b[?25h\x1b[?1049l");
            Console.Out.Flush();
        }

        if (chosen is null) return 0;

        Config.SaveValue("font-family", chosen);
        Console.Out.WriteLine($"font-family = {chosen}");
        return 0;
    }

    private static int ListRows() => Math.Max(3, Console.WindowHeight - ChromeRows - 1);

    private static void Draw(List<FontFamilyInfo> shown, int cursor, ref int offset, string query,
        bool images)
    {
        int width = Math.Max(40, Console.WindowWidth);
        int height = Math.Max(10, Console.WindowHeight);
        int rows = ListRows();
        // The list keeps a third of the width but never less than it takes to read a name;
        // on a narrow pane the column shrinks rather than pushing the preview off the edge.
        int listWidth = width / 3;
        if (listWidth < 12) listWidth = Math.Max(8, width / 2);
        if (listWidth > 36) listWidth = 36;
        int previewWidth = Math.Max(8, width - listWidth - 2);

        if (cursor < offset) offset = cursor;
        if (cursor >= offset + rows) offset = cursor - rows + 1;
        if (offset < 0) offset = 0;

        var frame = new StringBuilder();
        frame.Append("\x1b[H");
        frame.Append("\x1b_Ga=d,d=A\x1b\\");

        frame.Append(Accent).Append("Fonts ").Append(Reset)
             .Append(Muted).Append(shown.Count).Append(" families").Append(Reset);
        string filter = query.Length > 0 ? $"  filter {query}" : "  type to filter";
        frame.Append(Faint).Append(filter).Append(Reset).AppendLine();

        string rule = new string('\u2500', width);
        frame.Append(Faint).Append(rule).Append(Reset).AppendLine();

        FontFamilyInfo? selected = cursor >= 0 && cursor < shown.Count ? shown[cursor] : null;
        string[] preview = PreviewLines(selected, previewWidth, images);

        for (int i = 0; i < rows; i++)
        {
            int index = offset + i;
            string left;
            if (index < shown.Count)
            {
                string name = shown[index].Name;
                if (name.Length > listWidth - 2) name = name[..(listWidth - 3)] + "\u2026";
                left = index == cursor
                    ? Accent + "\u258C " + Strong + name + Reset
                    : Muted + "  " + name + Reset;
            }
            else
            {
                left = string.Empty;
            }

            string right = i < preview.Length ? preview[i] : string.Empty;
            frame.Append(Pad(left, listWidth)).Append(' ').Append(right).Append(Reset).AppendLine();
        }

        frame.Append(Faint).Append(rule).Append(Reset).AppendLine();
        frame.Append(Faint)
             .Append("\u2191\u2193 move   type filter   \u23CE apply   esc clear/quit")
             .Append(Reset);

        Console.Out.Write(frame.ToString());

        // The image goes under the preview text, where its rows were reserved above.
        if (images && selected is not null)
            DrawImage(selected.Name, listWidth + 3, preview.Length + 3, width - listWidth - 4);
    }

    /// <summary>The text block to the right of the list: name, styles, and where the image goes.</summary>
    private static string[] PreviewLines(FontFamilyInfo? family, int width, bool images)
    {
        if (family is null) return new[] { string.Empty, Muted + "no matching fonts" + Reset };

        string styles = string.Join(", ", family.Styles());
        var lines = new List<string>
        {
            Strong + family.Name + Reset,
            Faint + "Styles: " + Reset + Muted + Truncate(styles, width - 8) + Reset,
        };

        // The registry names faces but does not say whether the family is monospaced, so that
        // stays unsaid rather than guessed.
        if (family.Monospaced is { } monospaced)
            lines.Add(Faint + (monospaced ? "monospaced" : "proportional") + Reset);

        lines.Add(string.Empty);

        if (!images)
        {
            // Without an image protocol the sample is drawn in the terminal's own font; the
            // note keeps it from being mistaken for the family's real outlines.
            lines.Add(Muted + "abcdefghijklmnopqrstuvwxyz 0123456789" + Reset);
            lines.Add(Muted + "ABCDEFGHIJKLMNOPQRSTUVWXYZ" + Reset);
            lines.Add(Faint + "preview needs a kitty-graphics terminal (Gui, kitty)" + Reset);
            lines.Add(string.Empty);
        }
        else
        {
            // Reserve the rows the image is placed over.
            for (int i = 0; i < 5; i++) lines.Add(string.Empty);
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Renders the family's own outlines to a PNG and places it over the preview through the
    /// kitty graphics protocol, which is what makes the sample the real font rather than the
    /// terminal's current one.
    /// </summary>
    private static void DrawImage(string family, int column, int row, int widthCells)
    {
        // Never let the placement run past the right edge: the terminal scales the image to
        // this box, and a box wider than the screen is simply cut off.
        if (widthCells < 4) return;
        const string sample = "abcdefghijklmnopqrstuvwxyz 0123456789";
        // The image is drawn at its own pixel size (Gui does not scale it into c/r), and a
        // console app cannot ask how wide a cell is, so the sample is kept to a width that fits
        // a modest pane on either a 100% or a 200% display.
        int budget = Math.Min(300, Math.Max(160, widthCells * 10));
        byte[]? png = FontPreview.Render(family, sample, 26, budget, out int pixelWidth, out int pixelHeight);
        if (png is null) return;

        int rows = Math.Max(2, Math.Min(4, Console.WindowHeight - ChromeRows - 6));
        string payload = Convert.ToBase64String(png);

        var escape = new StringBuilder();
        escape.Append($"\x1b[{row};{column}H");
        for (int i = 0; i < payload.Length; i += 4096)
        {
            bool more = i + 4096 < payload.Length;
            string chunk = payload.Substring(i, Math.Min(4096, payload.Length - i));
            // f=100 is PNG: the terminal decodes it itself, and Gui installs that decoder.
            // c,r size the placement on the cell grid.
            escape.Append(i == 0
                ? $"\x1b_Gf=100,a=T,q=2,c={widthCells},r={rows},m={(more ? 1 : 0)};{chunk}\x1b\\"
                : $"\x1b_Gm={(more ? 1 : 0)};{chunk}\x1b\\");
        }
        Console.Out.Write(escape.ToString());
    }

    private static string Pad(string text, int width)
    {
        int visible = VisibleLength(text);
        return visible >= width ? TruncateVisible(text, width) : text + new string(' ', width - visible);
    }

    private static int VisibleLength(string text)
    {
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\x1b')
            {
                while (i < text.Length && text[i] != 'm') i++;
                continue;
            }
            length++;
        }
        return length;
    }

    private static string TruncateVisible(string text, int width)
    {
        int length = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\x1b')
            {
                while (i < text.Length && text[i] != 'm') i++;
                continue;
            }
            if (++length > width) return text[..i] + Reset;
        }
        return text;
    }

    private static string Truncate(string text, int width)
    {
        if (width <= 1) return string.Empty;
        return text.Length <= width ? text : text[..(width - 1)] + "\u2026";
    }
}
