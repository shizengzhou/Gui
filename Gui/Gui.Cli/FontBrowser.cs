using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The interactive font browser behind "gui +list-fonts" when stdout is a terminal. Its shape
/// follows tsp's browser: a chip with the count, the monospaced families in a column, and the
/// highlighted family's name, styles, sample and metadata on the right. The sample is drawn from
/// the family's own outlines through the terminal's image protocol, as kitty's font chooser
/// does, and Enter writes "font-family" into the config.
/// </summary>
internal static class FontBrowser
{
    private const string Accent = "\x1b[38;5;111m";
    private const string AccentFill = "\x1b[48;5;111m\x1b[38;5;235m\x1b[1m";
    private const string Muted = "\x1b[38;5;245m";
    private const string Faint = "\x1b[38;5;240m";
    private const string Strong = "\x1b[97m\x1b[1m";
    private const string Reset = "\x1b[0m";

    /// <summary>The sample kitty's chooser shows, on three lines.</summary>
    private static readonly string[] Sample =
    {
        "abcdefghijklmnopqrstuvwxyz 0123456789",
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ",
        "!\"#$%&'()*+,-./:;<=>?@[\\]^_\u0060{|}~",
    };

    public static int Run(List<FontFamilyInfo> families, string current, bool images,
        (byte r, byte g, byte b) foreground)
    {
        // A terminal grid multiplies a cell advance by its columns, so only monospaced
        // families are offered, the way tsp and the settings page do.
        var monospaced = new List<FontFamilyInfo>(families.Count);
        foreach (FontFamilyInfo family in families)
        {
            if (FontPreview.IsMonospaced(family.Name)) monospaced.Add(family);
        }
        if (monospaced.Count == 0) monospaced = families;

        int cursor = Math.Max(0, monospaced.FindIndex(f =>
            string.Equals(f.Name, current, StringComparison.OrdinalIgnoreCase)));
        int offset = 0;
        string query = string.Empty;
        bool filtering = false;
        string? chosen = null;

        Console.Out.Write("\x1b[?1049h\x1b[?25l");
        try
        {
            int drawnWidth = 0;
            int drawnHeight = 0;
            while (true)
            {
                List<FontFamilyInfo> shown = FontCatalog.Filter(monospaced, query);
                if (cursor >= shown.Count) cursor = Math.Max(0, shown.Count - 1);

                if (Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                {
                    drawnWidth = Console.WindowWidth;
                    drawnHeight = Console.WindowHeight;
                    Draw(shown, monospaced.Count, cursor, ref offset, query, filtering, images, foreground);
                }

                // Redraw when the window changes size: the preview image sits on the cell grid,
                // so a resize would leave it hanging where it was drawn.
                while (!Console.KeyAvailable)
                {
                    Thread.Sleep(40);
                    if (Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                        break;
                }
                if (!Console.KeyAvailable) continue;

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Escape)
                {
                    if (query.Length > 0)
                    {
                        query = string.Empty;
                        filtering = false;
                        cursor = 0;
                        continue;
                    }
                    break;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (shown.Count > 0)
                    {
                        chosen = shown[cursor].Name;
                        break;
                    }
                    continue;
                }

                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (cursor > 0) cursor--;
                    continue;
                }
                if (key.Key == ConsoleKey.DownArrow)
                {
                    if (cursor < shown.Count - 1) cursor++;
                    continue;
                }
                if (key.Key == ConsoleKey.PageUp)
                {
                    cursor = Math.Max(0, cursor - (ListRows() - 1));
                    continue;
                }
                if (key.Key == ConsoleKey.PageDown)
                {
                    cursor = Math.Min(Math.Max(0, shown.Count - 1), cursor + ListRows() - 1);
                    continue;
                }
                if (key.Key == ConsoleKey.Home)
                {
                    cursor = 0;
                    continue;
                }
                if (key.Key == ConsoleKey.End)
                {
                    cursor = Math.Max(0, shown.Count - 1);
                    continue;
                }
                if (key.Key == ConsoleKey.Backspace)
                {
                    if (query.Length > 0) query = query[..^1];
                    cursor = 0;
                    continue;
                }

                // "/" clears the filter and starts typing, the way tsp and kitty open theirs.
                if (key.KeyChar == '/')
                {
                    query = string.Empty;
                    filtering = true;
                    cursor = 0;
                    continue;
                }

                if (key.KeyChar is 'q' or 'Q' && !filtering)
                {
                    break;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    // Typing filters too, so a letter starts a search without the slash.
                    query += key.KeyChar;
                    filtering = true;
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

    /// <summary>Rows the list covers: everything between the chip and the footer.</summary>
    private static int ListRows() => Math.Max(3, Console.WindowHeight - 3);

    private static void Draw(List<FontFamilyInfo> shown, int total, int cursor, ref int offset,
        string query, bool filtering, bool images, (byte r, byte g, byte b) foreground)
    {
        int width = Math.Max(40, Console.WindowWidth);
        int height = Math.Max(10, Console.WindowHeight);
        int rows = ListRows();

        // The list column follows tsp: as wide as the longest name, but never more than a third
        // of the window, so the preview keeps the room.
        int widest = 0;
        foreach (FontFamilyInfo family in shown)
        {
            if (family.Name.Length > widest) widest = family.Name.Length;
        }
        int listWidth = Math.Clamp(widest + 4, 22, Math.Max(22, width / 3));
        int previewWidth = Math.Max(12, width - listWidth - 2);

        if (cursor < offset) offset = cursor;
        if (cursor >= offset + rows) offset = cursor - rows + 1;
        if (offset < 0) offset = 0;

        FontFamilyInfo? selected = cursor >= 0 && cursor < shown.Count ? shown[cursor] : null;
        List<string> right = PreviewLines(selected, previewWidth, images);

        var frame = new StringBuilder();
        frame.Append("\x1b[H");
        frame.Append("\x1b_Ga=d,d=A\x1b\\");

        // Chip row: the count, padded across the list column like tsp's "All 39".
        string chip = filtering ? "filter " + query + "\u258C" : "All " + total;
        frame.Append(AccentFill).Append(PadPlain(chip, listWidth)).Append(Reset).Append(' ');
        if (filtering) frame.Append(Faint).Append($"{shown.Count}/{total}  esc clear").Append(Reset);
        frame.AppendLine();

        for (int i = 0; i < rows; i++)
        {
            int index = offset + i;
            string left = string.Empty;
            if (index < shown.Count)
            {
                string name = shown[index].Name;
                if (name.Length > listWidth - 2) name = name[..(listWidth - 3)] + "\u2026";
                left = index == cursor
                    ? Accent + "\u258C " + Strong + name + Reset
                    : Muted + "  " + name + Reset;
            }

            string text = i < right.Count ? right[i] : string.Empty;
            frame.Append(Pad(left, listWidth)).Append(' ').Append(text).Append(Reset).AppendLine();
        }

        frame.Append(Faint).Append("\u2191\u2193 move   / filter   \u23CE choose   q quit").Append(Reset);

        Console.Out.Write(frame.ToString());

        // The image covers the five blank rows reserved under the "preview" rule.
        if (images && selected is not null && height > 12)
            DrawImage(selected.Name, listWidth + 2, 7, previewWidth, foreground);
    }

    /// <summary>The right-hand block, in tsp's order: name, styles, hint, preview, metadata.</summary>
    private static List<string> PreviewLines(FontFamilyInfo? family, int width, bool images)
    {
        if (family is null) return new List<string> { string.Empty, Muted + "no matching fonts" + Reset };

        string file = family.Primary().File;
        var lines = new List<string>
        {
            Accent + Strong + family.Name + Reset,
            Faint + "Styles: " + Reset + Muted + Join(family.Styles(), width - 8) + Reset,
            Faint + "Press Enter to save this font to Gui" + Reset,
            string.Empty,
            PreviewRule(width),
            string.Empty,
        };

        if (images)
        {
            // The sample is an image; these rows are reserved for it.
            for (int i = 0; i < 5; i++) lines.Add(string.Empty);
        }
        else
        {
            foreach (string sample in Sample) lines.Add(Muted + sample + Reset);
            lines.Add(Faint + "preview needs a kitty-graphics terminal (Gui, kitty)" + Reset);
        }

        lines.Add(string.Empty);
        lines.Add(Faint + "Faces       " + Reset + Muted + family.Faces.Count + Reset);
        if (file.Length > 0)
            lines.Add(Faint + "File        " + Reset + Muted + ShortPath(file, width - 12) + Reset);
        lines.Add(string.Empty);
        lines.Add(Faint + (images
            ? "preview rendered from the selected font"
            : "preview uses the terminal's current font") + Reset);

        return lines;
    }

    private static string PreviewRule(int width)
    {
        const string label = " preview ";
        int remaining = Math.Max(0, width - label.Length);
        int left = remaining / 2;
        int right = remaining - left;
        return Faint + new string('\u2500', left) + label + new string('\u2500', right) + Reset;
    }

    private static string Join(IReadOnlyList<string> styles, int width)
    {
        string text = string.Join(", ", styles);
        return text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "\u2026";
    }

    private static string ShortPath(string path, int width)
    {
        if (width < 8) width = 8;
        if (path.Length <= width) return path;
        string name = System.IO.Path.GetFileName(path);
        return name.Length + 4 >= width ? name : "\u2026\\" + name;
    }

    /// <summary>
    /// Renders the sample in the family's own outlines and places it with the kitty graphics
    /// protocol, which is what makes the preview the real font rather than the terminal's.
    /// </summary>
    private static void DrawImage(string family, int column, int row, int widthCells,
        (byte r, byte g, byte b) foreground)
    {
        // Gui draws the image at its own pixel size (it does not scale it into c/r), and a
        // console app cannot ask how wide a cell is, so the sample is kept to a width that fits
        // a modest pane on either a 100% or a 200% display.
        int budget = Math.Min(520, Math.Max(200, widthCells * 12));
        byte[]? png = FontPreview.Render(family, Sample, 20, budget, foreground,
            out int pixelWidth, out int pixelHeight);
        if (png is null) return;

        const int rows = 5;
        string payload = Convert.ToBase64String(png);

        var escape = new StringBuilder();
        escape.Append($"\x1b[{row};{column}H");
        for (int i = 0; i < payload.Length; i += 4096)
        {
            bool more = i + 4096 < payload.Length;
            string chunk = payload.Substring(i, Math.Min(4096, payload.Length - i));
            // f=100 is PNG: the terminal decodes it itself, and Gui installs that decoder.
            escape.Append(i == 0
                ? $"\x1b_Gf=100,a=T,q=2,c={widthCells},r={rows},m={(more ? 1 : 0)};{chunk}\x1b\\"
                : $"\x1b_Gm={(more ? 1 : 0)};{chunk}\x1b\\");
        }
        Console.Out.Write(escape.ToString());
    }

    private static string Pad(string text, int width)
    {
        int visible = VisibleLength(text);
        if (visible > width) return TruncateVisible(text, width);
        return text + new string(' ', width - visible);
    }

    private static string PadPlain(string text, int width) =>
        text.Length >= width ? text[..width] : text + new string(' ', width - text.Length);

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
}
