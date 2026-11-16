using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
        var monospaced = families.Where(f => f.Monospaced == true).ToList();
        if (monospaced.Count == 0) monospaced = families;

        int cursor = Math.Max(0, monospaced.FindIndex(f =>
            string.Equals(f.Name, current, StringComparison.OrdinalIgnoreCase)));
        int offset = 0;
        string query = string.Empty;
        bool filtering = false;
        string? chosen = null;

        // A console starts in line mode: keys sit in the buffer until Enter and would be
        // echoed into the frame. Raw mode is what makes a browser usable at all.
        ConsoleModes? previousMode = EnterRawMode();

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
            Name = "gui-fonts-keys",
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
                List<FontFamilyInfo> shown = FontCatalog.Filter(monospaced, query);
                if (cursor >= shown.Count) cursor = Math.Max(0, shown.Count - 1);

                if (dirty || Console.WindowWidth != drawnWidth || Console.WindowHeight != drawnHeight)
                {
                    dirty = false;
                    drawnWidth = Console.WindowWidth;
                    drawnHeight = Console.WindowHeight;
                    Draw(shown, monospaced.Count, cursor, ref offset, query, filtering, images, foreground);
                }

                // Wait for a key, and redraw when the window changes size: the preview image
                // sits on the cell grid, so a resize would leave it where it was drawn.
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
                        chosen = shown[cursor].Name;
                        break;
                    }
                    continue;
                }

                // j/k move like the arrows while the filter is closed; with it open they are
                // characters in the query, which is why filtering needs "/" to start.
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

                // "/" clears the filter and starts typing, the way tsp and kitty open theirs.
                if (key.KeyChar == '/')
                {
                    query = string.Empty;
                    filtering = true;
                    cursor = 0;
                    continue;
                }

                // With the filter open every printable key belongs to the query; navigation
                // and the shortcuts only answer once it is closed again.
                if (filtering)
                {
                    if (!char.IsControl(key.KeyChar))
                    {
                        query += key.KeyChar;
                        cursor = 0;
                    }
                    continue;
                }

                // Ctrl+C arrives as a key while it is treated as input; leave the screen tidy.
                if (key.KeyChar == '') break;

                if (key.KeyChar is 'q' or 'Q')
                {
                    break;
                }
            }
        }
        finally
        {
            Console.Out.Write("\x1b_Ga=d,d=A\x1b\\");
            Console.Out.Write("\x1b[?25h\x1b[?1049l");
            Console.Out.Flush();
            if (previousMode is { } mode) RestoreConsoleMode(mode);
        }

        if (chosen is null) return 0;

        Config.SaveValue("font-family", chosen);
        Console.Out.WriteLine($"font-family = {chosen}");
        return 0;
    }


    // ---- console mode -----------------------------------------------------

    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    private readonly record struct ConsoleModes(uint Input, uint Output);

    /// <summary>
    /// Prepares the console for a full-screen browser: raw input, so every key arrives as it is
    /// pressed (nothing is echoed, Ctrl+C comes back as a key instead of killing the process
    /// before the screen is restored), and virtual terminal processing on output, without which
    /// a classic conhost window prints the escape sequences as text.
    /// </summary>
    private static ConsoleModes? EnterRawMode()
    {
        IntPtr input = GetStdHandle(StdInput);
        IntPtr output = GetStdHandle(StdOutput);
        bool haveInput = GetConsoleMode(input, out uint inputMode);
        bool haveOutput = GetConsoleMode(output, out uint outputMode);
        if (!haveInput && !haveOutput) return null;

        if (haveInput)
            SetConsoleMode(input, inputMode & ~(EnableLineInput | EnableEchoInput));
        if (haveOutput)
        {
            SetConsoleMode(output,
                outputMode | EnableProcessedOutput | EnableVirtualTerminalProcessing);
        }

        try
        {
            Console.TreatControlCAsInput = true;
        }
        catch (Exception)
        {
            // Without a console the setting is meaningless; the keys still arrive.
        }

        return new ConsoleModes(haveInput ? inputMode : 0, haveOutput ? outputMode : 0);
    }

    private static void RestoreConsoleMode(ConsoleModes modes)
    {
        try
        {
            Console.TreatControlCAsInput = false;
        }
        catch (Exception)
        {
            // Nothing to restore.
        }

        SetConsoleMode(GetStdHandle(StdInput), modes.Input);
        SetConsoleMode(GetStdHandle(StdOutput), modes.Output);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    /// <summary>Rows the list covers: everything between the chip and the footer.</summary>
    private static int ListRows() => Math.Max(3, Console.WindowHeight - 3);

    private static void Draw(List<FontFamilyInfo> shown, int total, int cursor, ref int offset,
        string query, bool filtering, bool images, (byte r, byte g, byte b) foreground)
    {
        // The window can be wider than the buffer in a classic conhost window; anything past
        // the buffer width would wrap and shift every row below it.
        int width = Math.Max(40, Math.Min(Console.WindowWidth, Console.BufferWidth));
        int height = Math.Max(10, Math.Min(Console.WindowHeight, Console.BufferHeight));
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

        // Every row is padded to the window width: a shorter row must erase what the previous
        // frame left there, or two frames bleed into each other. The frame carries no trailing
        // newline, so writing it never scrolls the last row out of view.
        var lines = new List<string>(rows + 2);

        var head = new StringBuilder();
        head.Append(AccentFill).Append(PadPlain("All " + total, listWidth)).Append(Reset);
        if (filtering && width >= 54)
        {
            // tsp puts the filter state at the far right of the chip row.
            string status = $"filter {query}\u258C  {shown.Count}/{total}";
            int gap = width - listWidth - status.Length;
            if (gap > 0) head.Append(new string(' ', gap)).Append(Faint).Append(status).Append(Reset);
        }
        lines.Add(Pad(head.ToString(), width));

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
            lines.Add(Pad(Pad(left, listWidth) + " " + text, width));
        }

        lines.Add(Pad(Faint + "\u2191\u2193 move   / filter   \u23CE choose   q quit" + Reset, width));

        Console.Out.Write("\x1b[H");
        Console.Out.Write("\x1b_Ga=d,d=A\x1b\\");
        Console.Out.Write(string.Join("\r\n", lines));

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
    /// The pane's cell size in physical pixels, exported by Gui. The fallback only matters when
    /// the browser is forced somewhere Gui is not the terminal.
    /// </summary>
    private static (int Width, int Height) CellSize()
    {
        int width = ReadEnv("GUI_CELL_WIDTH", 9);
        int height = ReadEnv("GUI_CELL_HEIGHT", 20);
        return (Math.Max(4, width), Math.Max(8, height));
    }

    private static int LongestSample()
    {
        int longest = 0;
        foreach (string line in Sample) longest = Math.Max(longest, line.Length);
        return longest;
    }

    private static int ReadEnv(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
            ? value
            : fallback;

    /// <summary>
    /// Renders the sample in the family's own outlines and places it with the kitty graphics
    /// protocol, which is what makes the preview the real font rather than the terminal's.
    /// </summary>
    private static void DrawImage(string family, int column, int row, int widthCells,
        (byte r, byte g, byte b) foreground)
    {
        // Gui exports its cell size, so the sample is drawn on the same grid the pane uses:
        // one glyph per cell at the cell's advance and height. That is what makes the preview
        // look like the terminal's own text rather than a caption at some other size.
        (int cellWidth, int cellHeight) = CellSize();

        // Gui renders it with Win2D (the pane's own pipeline); the local rasterizer is only the
        // fallback for a pane whose window did not answer.
        byte[]? png = SampleImages.Render(family, cellWidth, cellHeight, foreground);
        int pixelWidth;
        if (png is not null)
        {
            pixelWidth = LongestSample() * cellWidth + 4;
        }
        else
        {
            png = FontPreview.Render(family, Sample, cellWidth, cellHeight, foreground,
                out pixelWidth, out _);
        }
        if (png is null) return;

        // The image spans exactly its cells; Gui draws it at its natural size.
        int columns = Math.Max(1, Math.Min(widthCells, (pixelWidth + cellWidth - 1) / cellWidth));
        int rows = Sample.Length;
        string payload = Convert.ToBase64String(png);

        var escape = new StringBuilder();
        escape.Append($"\x1b[{row};{column}H");
        for (int i = 0; i < payload.Length; i += 4096)
        {
            bool more = i + 4096 < payload.Length;
            string chunk = payload.Substring(i, Math.Min(4096, payload.Length - i));
            // f=100 is PNG: the terminal decodes it itself, and Gui installs that decoder.
            escape.Append(i == 0
                ? $"\x1b_Gf=100,a=T,q=2,c={columns},r={rows},m={(more ? 1 : 0)};{chunk}\x1b\\"
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
