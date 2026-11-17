using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Gui.Term;

namespace Gui.Cli;

/// <summary>
/// The interactive theme browser behind "gui +list-themes". Its shape follows tsp's picker: the
/// All/Light/Dark chips with their counts, the themes in a column with their brightness, and the
/// highlighted theme's colors and a small syntax sample on the right. Enter writes the matching
/// half of the "theme" option into the config.
/// </summary>
internal static class ThemeBrowser
{
    private const string Accent = "\x1b[38;5;111m";
    private const string AccentFill = "\x1b[48;5;111m\x1b[38;5;235m\x1b[1m";
    private const string Muted = "\x1b[38;5;245m";
    private const string Faint = "\x1b[38;5;240m";
    private const string Strong = "\x1b[97m\x1b[1m";
    private const string Reset = "\x1b[0m";

    public static int Run(List<ThemeEntry> themes, string directory, string current)
    {
        int lightCount = 0;
        foreach (ThemeEntry theme in themes)
            if (theme.Light) lightCount++;
        int darkCount = themes.Count - lightCount;

        // The halves Gui is using right now: the listed theme that matches one of them is the
        // one in effect, and carries a check.
        (string? currentLight, string? currentDark) = Config.ParseThemeValue(current);

        int tab = 0;              // 0 all, 1 light, 2 dark
        int cursor = 0;
        int offset = 0;
        string query = string.Empty;
        bool filtering = false;
        ThemeEntry? chosen = null;

        ConsoleRaw.Modes? previousMode = ConsoleRaw.Enter();

        // Keys are read on their own thread: a blocked ReadKey would also block the resize
        // check, and polling KeyAvailable depends on the console reporting it correctly.
        var keys = new BlockingCollection<ConsoleKeyInfo>();
        var reader = new Thread(() =>
        {
            try
            {
                while (true) keys.Add(Console.ReadKey(intercept: true));
            }
            catch (Exception)
            {
                // The console went away; the loop below just keeps painting on resize.
            }
        })
        {
            IsBackground = true,
            Name = "gui-themes-keys",
        };
        reader.Start();

        Console.Out.Write("\x1b[?1049h\x1b[?25l");
        try
        {
            int drawnWidth = 0;
            int drawnHeight = 0;
            bool dirty = true;
            while (true)
            {
                List<ThemeEntry> shown = Visible(themes, tab, query);
                if (cursor >= shown.Count) cursor = Math.Max(0, shown.Count - 1);

                if (dirty || Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                {
                    dirty = false;
                    drawnWidth = Console.WindowWidth;
                    drawnHeight = Console.WindowHeight;
                    Draw(shown, themes.Count, lightCount, darkCount, currentLight, currentDark, tab,
                        cursor, ref offset, query, filtering, directory);
                }

                ConsoleKeyInfo key = default;
                bool haveKey = keys.TryTake(out key, 40);
                while (!haveKey)
                {
                    if (Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                        break;
                    haveKey = keys.TryTake(out key, 40);
                }
                if (!haveKey) continue;
                dirty = true;

                // Esc only leaves the filter: quitting is "q" (or Ctrl+C), so an Esc pressed
                // to cancel a search can never close the browser.
                if (key.Key == ConsoleKey.Escape)
                {
                    query = string.Empty;
                    filtering = false;
                    cursor = 0;
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    if (shown.Count > 0)
                    {
                        chosen = shown[cursor];
                        break;
                    }
                    continue;
                }

                // Tab cycles All / Light / Dark, Shift+Tab goes the other way, the way tsp
                // switches between the brightness groups.
                if (key.Key == ConsoleKey.Tab)
                {
                    bool back = (key.Modifiers & ConsoleModifiers.Shift) != 0;
                    tab = ((tab + (back ? 2 : 1)) % 3 + 3) % 3;
                    cursor = 0;
                    offset = 0;
                    continue;
                }

                bool up = key.Key == ConsoleKey.UpArrow || (!filtering && key.KeyChar == 'k');
                bool down = key.Key == ConsoleKey.DownArrow || (!filtering && key.KeyChar == 'j');
                if (up)
                {
                    if (cursor > 0) cursor--;
                    continue;
                }
                if (down)
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

                if (key.KeyChar == '/')
                {
                    query = string.Empty;
                    filtering = true;
                    cursor = 0;
                    continue;
                }

                // With the filter open every printable key belongs to the query.
                if (filtering)
                {
                    if (!char.IsControl(key.KeyChar))
                    {
                        query += key.KeyChar;
                        cursor = 0;
                    }
                    continue;
                }

                if (key.KeyChar == '\u0003') break;
                if (key.KeyChar is 'q' or 'Q') break;
            }
        }
        finally
        {
            Console.Out.Write("\x1b[?25h\x1b[?1049l");
            Console.Out.Flush();
            if (previousMode is { } mode) ConsoleRaw.Restore(mode);
        }

        if (chosen is null) return 0;

        string value = ThemeCatalog.Apply(current, chosen.Value);
        Config.SaveValue("theme", value);
        Console.Out.WriteLine("theme = " + value);
        return 0;
    }

    private static List<ThemeEntry> Visible(List<ThemeEntry> themes, int tab, string query)
    {
        var list = new List<ThemeEntry>(themes.Count);
        foreach (ThemeEntry theme in themes)
        {
            if (tab == 1 && !theme.Light) continue;
            if (tab == 2 && theme.Light) continue;
            list.Add(theme);
        }
        return ThemeCatalog.Filter(list, query);
    }

    /// <summary>
    /// Rows the list covers: everything between the chips and the footer, so the footer sits on
    /// the window's last row instead of leaving an empty one below it.
    /// </summary>
    private static int ListRows() => Math.Max(3, Console.WindowHeight - 2);

    private static void Draw(List<ThemeEntry> shown, int total, int lightCount, int darkCount,
        string? currentLight, string? currentDark, int tab, int cursor, ref int offset,
        string query, bool filtering, string directory)
    {
        // The window can be wider than the buffer in a classic conhost window; anything past
        // the buffer width would wrap and shift every row below it.
        int width = Math.Max(40, Math.Min(Console.WindowWidth, Console.BufferWidth));
        int height = Math.Max(10, Math.Min(Console.WindowHeight, Console.BufferHeight));
        int rows = ListRows();

        // The list column follows tsp: as wide as the longest name plus its brightness badge,
        // never more than a third of the window.
        int widest = 0;
        foreach (ThemeEntry theme in shown)
            if (theme.Name.Length > widest) widest = theme.Name.Length;
        int listWidth = Math.Clamp(widest + 12, 26, Math.Max(26, width / 3));
        // The preview panel is painted with the theme's background, so it runs to the window's
        // right edge with only the single space that separates it from the list.
        int previewWidth = Math.Max(12, width - listWidth - 1);

        if (cursor < offset) offset = cursor;
        if (cursor >= offset + rows) offset = cursor - rows + 1;
        if (offset < 0) offset = 0;

        ThemeEntry? selected = cursor >= 0 && cursor < shown.Count ? shown[cursor] : null;
        ThemeColors? colors = selected is null ? null : ThemeCatalog.Read(selected.Value, directory);
        List<string> right = PreviewLines(selected, colors, previewWidth);

        var lines = new List<string>(rows + 2);

        var head = new StringBuilder();
        AppendChip(head, "All " + total, tab == 0);
        head.Append(Accent).Append("  ");
        AppendChip(head, "Light " + lightCount, tab == 1);
        head.Append(Accent).Append("  ");
        AppendChip(head, "Dark " + darkCount, tab == 2);
        head.Append(Reset);

        if ((filtering || query.Length > 0) && width >= 64)
        {
            string status = $"filter {query}\u258C  {shown.Count}/{total}";
            int gap = width - VisibleLength(head.ToString()) - status.Length;
            if (gap > 0) head.Append(new string(' ', gap)).Append(Faint).Append(status).Append(Reset);
        }
        lines.Add(Pad(head.ToString(), width));

        for (int i = 0; i < rows; i++)
        {
            int index = offset + i;
            string left = string.Empty;
            if (index < shown.Count)
            {
                ThemeEntry theme = shown[index];
                string badge = theme.Light ? "light" : "dark";
                // The theme Gui is using carries a check after its brightness badge.
                bool inUse = string.Equals(theme.Name, theme.Light ? currentLight : currentDark,
                    StringComparison.OrdinalIgnoreCase);
                int trailing = inUse ? 2 : 0;
                int room = listWidth - badge.Length - 4;
                string name = theme.Name.Length > room ? theme.Name[..Math.Max(1, room - 1)] + "\u2026" : theme.Name;
                string gap = new string(' ',
                    Math.Max(1, listWidth - 3 - name.Length - badge.Length - trailing));

                left = index == cursor
                    ? Accent + "\u258C " + Strong + name + Reset + gap + Faint + badge + Reset
                    : Muted + "  " + name + Reset + gap + Faint + badge + Reset;
                if (inUse) left += " " + Accent + "\u2713" + Reset;
            }

            string text = i < right.Count ? right[i] : string.Empty;
            string preview = colors is null
                ? Pad(text, previewWidth)
                : ThemeLine(text, colors, previewWidth);
            lines.Add(Pad(Pad(left, listWidth) + " " + preview, width));
        }

        lines.Add(Pad(Faint +
            "\u2191\u2193 move   tab light/dark   / filter   \u23CE apply   q quit" + Reset, width));

        Console.Out.Write("\x1b[H");
        Console.Out.Write(string.Join("\r\n", lines));
    }

    private static void AppendChip(StringBuilder head, string text, bool active)
    {
        if (active) head.Append(AccentFill).Append(' ').Append(text).Append(' ').Append(Reset);
        else head.Append(Muted).Append(' ').Append(text).Append(' ').Append(Reset);
    }

    /// <summary>
    /// The right-hand block: name, colors, palette, a shell sample and a C# one. Every line is
    /// drawn in the theme's own colors — the panel is the theme, not the terminal's take on it.
    /// </summary>
    private static List<string> PreviewLines(ThemeEntry? entry, ThemeColors? colors, int width)
    {
        if (entry is null || colors is null)
        {
            return new List<string>
            {
                string.Empty,
                Muted + (entry is null ? "no matching themes" : "this theme file cannot be read") + Reset,
            };
        }

        string brightness = entry.Value.Light ? "light" : "dark";
        Rgb dim = colors.Palette[8];
        Rgb[] palette = colors.Palette;
        var lines = new List<string>
        {
            "\x1b[1m" + entry.Value.Name + Reset,
            Text(brightness + " theme \u00b7 " +
                ShortPath(colors.File, width - brightness.Length - 9), dim),
            string.Empty,
            Text("normal", dim),
            "  " + PaletteRow(colors, 0),
            Text("bright", dim),
            "  " + PaletteRow(colors, 8),
            string.Empty,
        };

        // The shell sample: the prompt, the files of this repository, a comment and an error,
        // the four colors a theme is judged by.
        lines.Add(Text("$ ", palette[2]) + Text("gui +list-themes --help", colors.Foreground));
        lines.Add(Text("README.md ", palette[4]) + Text("Gui.slnx ", palette[3]) +
            Text("Gui/", palette[6]));
        lines.Add(Text("# " + entry.Value.Name, palette[8]));
        lines.Add(Text("error: file not found", palette[1]));

        // The code sample tsp's picker has, in this repository's own language.
        lines.Add(string.Empty);
        lines.Add(Text(new string('\u2500', Math.Max(1, width)), dim));
        lines.Add(Text("code", dim));
        foreach (string code in CodeSample(colors)) lines.Add(code);

        lines.Add(string.Empty);
        lines.Add(Text("Enter writes theme = " + ThemeCatalog.Apply(null, entry.Value), dim));

        return lines;
    }

    /// <summary>
    /// The C# sample: a comment, a using, a string, a loop and a call, the shapes that show a
    /// theme's keyword, type, string and number colors next to its plain foreground.
    /// </summary>
    private static List<string> CodeSample(ThemeColors colors)
    {
        Rgb[] p = colors.Palette;
        return new List<string>
        {
            Text("// Gui", p[8]),
            Text("using ", p[4]) + Text("System", p[6]) + ";",
            string.Empty,
            Text("var ", p[4]) + Selected("greeting", colors) + " = " +
                Text("\"hello\"", p[2]) + ";",
            Text("for ", p[4]) + "(var i = " + Text("0", p[3]) + "; i < " + Text("3", p[3]) + "; i++)",
            "{",
            "    " + Text("Console", p[6]) + "." + Text("WriteLine", p[6]) + "($\"" +
                Text("{greeting}, {i}", p[2]) + "\");",
            "}",
            Cursor(colors),
        };
    }

    /// <summary>A run drawn the way the theme paints a selection.</summary>
    private static string Selected(string text, ThemeColors colors) =>
        Bg(colors.SelectionBackground) + Fg(colors.SelectionForeground) + text + Reset;

    /// <summary>The block the theme paints its cursor with, a cell wide like the terminal's.</summary>
    private static string Cursor(ThemeColors colors) =>
        Bg(colors.CursorSet ? colors.Cursor : colors.Foreground) + " " + Reset;

    /// <summary>
    /// Paints one row of the preview: the theme's background and foreground behind whatever the
    /// row drew, padded out to the panel's width. A reset inside the row is followed by the panel's
    /// colors again, or the gaps between spans — and the padding — would show the terminal's own
    /// background instead of the theme's.
    /// </summary>
    private static string ThemeLine(string text, ThemeColors colors, int width)
    {
        string paint = Reset + Bg(colors.Background) + Fg(colors.Foreground);
        int visible = VisibleLength(text);
        if (visible > width)
        {
            text = TruncateVisible(text, width);
            visible = width;
        }

        return Bg(colors.Background) + Fg(colors.Foreground) +
            text.Replace(Reset, paint) +
            new string(' ', Math.Max(0, width - visible)) + Reset;
    }

    private static string PaletteRow(ThemeColors colors, int start)
    {
        var row = new StringBuilder();
        for (int i = start; i < start + 8; i++)
        {
            if (i > start) row.Append(' ');
            row.Append(Block(colors.Palette[i], i.ToString(), 4));
        }
        return row.ToString();
    }

    /// <summary>A colored block; the label's color is chosen so it stays readable.</summary>
    private static string Block(Rgb color, string label, int width)
    {
        int luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
        string foreground = luma > 140 ? "\x1b[38;2;0;0;0m" : "\x1b[38;2;255;255;255m";
        if (label.Length > width) label = label[..width];
        int left = (width - label.Length) / 2;
        return "\x1b[48;2;" + color.R + ";" + color.G + ";" + color.B + "m" + foreground +
            new string(' ', left) + label + new string(' ', width - label.Length - left) + Reset;
    }

    private static string Text(string text, Rgb color) => Fg(color) + text + Reset;

    private static string Bg(Rgb color) =>
        "\x1b[48;2;" + color.R + ";" + color.G + ";" + color.B + "m";

    private static string Fg(Rgb color) =>
        "\x1b[38;2;" + color.R + ";" + color.G + ";" + color.B + "m";

    /// <summary>
    /// The tail of a path that fits the panel: whole folders from the end, so a deep development
    /// path still says which theme file it is.
    /// </summary>
    private static string ShortPath(string path, int width)
    {
        if (width < 8) width = 8;
        if (path.Length <= width) return path;

        string trimmed = path.TrimEnd('\\');
        string tail = System.IO.Path.GetFileName(trimmed);
        if (tail.Length == 0) return path[..width];
        if (tail.Length > width) return tail[..width];

        string? folder = System.IO.Path.GetDirectoryName(trimmed);
        while (!string.IsNullOrEmpty(folder))
        {
            string name = System.IO.Path.GetFileName(folder);
            if (name.Length == 0) break;
            string candidate = name + "\\" + tail;
            // Two cells are what the "…" in front of the tail takes.
            if (candidate.Length + 2 > width) break;
            tail = candidate;
            folder = System.IO.Path.GetDirectoryName(folder);
        }

        return tail.Length + 2 <= width ? "\u2026\\" + tail : tail;
    }

    private static string Pad(string text, int width)
    {
        int visible = VisibleLength(text);
        if (visible > width) return TruncateVisible(text, width);
        return text + new string(' ', width - visible);
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
}
