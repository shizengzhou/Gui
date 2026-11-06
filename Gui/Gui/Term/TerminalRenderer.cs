using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Windows.Foundation;
using Windows.UI;

namespace Gui.Term;

/// <summary>
/// Win2D renderer for a libghostty-vt render state. No terminal logic lives here.
/// </summary>
public sealed unsafe class TerminalRenderer
{
    private const uint FR_PRIVATE = 0x10;

    private static readonly string[] BundledFontFiles =
    {
        "JetBrainsMonoNerdFont-Regular.ttf",
        "JetBrainsMonoNerdFont-Bold.ttf",
        "JetBrainsMonoNerdFont-Italic.ttf",
        "JetBrainsMonoNerdFont-BoldItalic.ttf",
    };

    private readonly Dictionary<int, CanvasTextFormat> _formats = new();
    private readonly StringBuilder _text = new(64);

    private CanvasTextFormat _baseFormat = CreateFormat("Consolas", 13f, false, false);
    private string _fontFamily = "JetBrainsMono Nerd Font";
    private float _fontSize = 13f;
    private CellMetrics _metrics = new(8, 16);
    private ushort _cursorX;
    private ushort _cursorY;
    private bool _cursorValid;
    private string _imeComposition = string.Empty;

    private static bool _fontsRegistered;

    public TerminalRenderer()
    {
        RegisterBundledFonts();
        _baseFormat = CreateFormat(_fontFamily, _fontSize, false, false);
    }

    public CellMetrics CellMetrics => _metrics;

    /// <summary>
    /// The string the IME is composing right now (the pinyin before a candidate is picked).
    /// The terminal buffer knows nothing about it, so it is painted over the cells at the
    /// cursor and disappears when the composition ends.
    /// </summary>
    public bool SetImeComposition(string text)
    {
        if (_imeComposition == text) return false;
        _imeComposition = text;
        return true;
    }

    public bool CursorCell(out ushort x, out ushort y)
    {
        x = _cursorX;
        y = _cursorY;
        return _cursorValid;
    }

    /// <summary>
    /// Loads the TTFs shipped in Assets\Fonts for this process only, so DirectWrite's
    /// system collection can resolve the family even when it is not installed.
    /// </summary>
    private static void RegisterBundledFonts()
    {
        if (_fontsRegistered) return;
        _fontsRegistered = true;

        string directory = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        foreach (string name in BundledFontFiles)
        {
            string file = Path.Combine(directory, name);
            if (!File.Exists(file)) continue;
            AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero);
        }
    }

    private static CanvasTextFormat CreateFormat(string family, float size, bool bold, bool italic)
    {
        return new CanvasTextFormat
        {
            FontFamily = family,
            FontSize = size,
            WordWrapping = CanvasWordWrapping.NoWrap,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
        };
    }

    /// <summary>
    /// Monospace family; bundled JetBrainsMono Nerd Font, then system fallbacks when the
    /// requested family is missing. The advance check rejects proportional fallbacks.
    /// </summary>
    public void SetFontFamily(string family)
    {
        // An empty value (config key removed) restores the bundled default.
        if (string.IsNullOrEmpty(family)) family = "JetBrainsMono Nerd Font";
        if (family == _fontFamily) return;
        _fontFamily = family;
        Rebuild(0);
    }

    /// <summary>Font size in DIPs. Recomputes cell metrics.</summary>
    public void SetFontSize(float size)
    {
        if (size < 6f || size > 96f) return;
        if (Math.Abs(size - _fontSize) < 0.01f) return;
        _fontSize = size;
        Rebuild(0);
    }

    private void Rebuild(int attempt)
    {
        string[] candidates =
        {
            attempt == 0 ? _fontFamily : string.Empty,
            "JetBrainsMono Nerd Font",
            "Cascadia Mono",
            "Cascadia Code",
            "Consolas",
            "Courier New",
        };

        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var format = CreateFormat(candidate, _fontSize, false, false);
            if (!IsMonospace(format)) continue;
            _baseFormat = format;
            _formats.Clear();
            MeasureCell();
            return;
        }

        _baseFormat = CreateFormat("Consolas", _fontSize, false, false);
        _formats.Clear();
        MeasureCell();
    }

    private static bool IsMonospace(CanvasTextFormat format)
    {
        using var wide = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "MMMMMMMMMM", format, 0, 0);
        using var narrow = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "iiiiiiiiii", format, 0, 0);
        double wideAdvance = wide.LayoutBounds.Width;
        double narrowAdvance = narrow.LayoutBounds.Width;
        if (wideAdvance <= 0 || narrowAdvance <= 0) return false;
        return Math.Abs(wideAdvance - narrowAdvance) < 0.05;
    }

    private void MeasureCell()
    {
        using var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), "MMMM", _baseFormat, 0, 0);
        double advance = layout.LayoutBounds.Width / 4.0;
        if (advance < 1.0) advance = _fontSize * 0.6;
        double height = layout.LayoutBounds.Height;
        if (height < 1.0) height = _fontSize * 1.2;

        _metrics = new CellMetrics((uint)Math.Max(1, Math.Ceiling(advance)), (uint)Math.Max(1, Math.Ceiling(height)));
    }

    private CanvasTextFormat FormatFor(bool bold, bool italic)
    {
        int key = (bold ? 1 : 0) | (italic ? 2 : 0);
        if (_formats.TryGetValue(key, out CanvasTextFormat? cached)) return cached;

        var format = key == 0 ? _baseFormat : CreateFormat(_baseFormat.FontFamily, _fontSize, bold, italic);
        _formats[key] = format;
        return format;
    }

    // ---------------- palette remapping ----------------

    private static Dictionary<int, Rgb>? _lightPalette;

    /// <summary>
    /// The VT reports cell colors resolved from its default (dark) palette. For the light
    /// design theme, map each of those palette colors to the design's light palette so
    /// ANSI output keeps its contrast on a white surface.
    /// </summary>
    private static Dictionary<int, Rgb> LightPalette
    {
        get
        {
            if (_lightPalette is not null) return _lightPalette;

            var baseColors = new Rgb[256];
            GhosttyNative.PaletteDefault(baseColors);

            var source = new Rgb[256];
            Array.Copy(baseColors, source, 256);
            var lightBase = new Rgb[16]
            {
                new(36, 41, 47), new(207, 34, 46), new(17, 99, 41), new(154, 103, 0),
                new(9, 105, 218), new(130, 80, 223), new(27, 124, 131), new(110, 119, 129),
                new(87, 96, 106), new(168, 25, 35), new(26, 127, 55), new(191, 135, 0),
                new(33, 139, 255), new(164, 117, 249), new(49, 146, 170), new(36, 41, 47),
            };
            Array.Copy(lightBase, source, 16);

            var generated = new Rgb[256];
            GhosttyNative.PaletteGenerate(source, default, new Rgb(255, 255, 255), new Rgb(36, 41, 47), false, generated);

            var map = new Dictionary<int, Rgb>(256);
            for (int i = 0; i < 256; i++)
                map[baseColors[i].GetHashCode()] = generated[i];
            _lightPalette = map;
            return map;
        }
    }

    private static Color ThemeColor(Rgb rgb)
    {
        if (Theme.Terminal.Custom || !Theme.Light)
            return Color.FromArgb(255, rgb.R, rgb.G, rgb.B);

        return LightPalette.TryGetValue(rgb.GetHashCode(), out Rgb mapped)
            ? Color.FromArgb(255, mapped.R, mapped.G, mapped.B)
            : Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
    }

    // ---------------- rendering ----------------

    private struct Run
    {
        public int Col;
        public int Cols;
        public string Text;
        public Color Fg;
        public Color Bg;
        public bool Bold;
        public bool Italic;
        public bool Underline;
        public bool Strikethrough;
        public bool Invisible;
        public bool Wide;
    }

    /// <summary>Redraws the whole viewport. <paramref name="cursorOn"/> controls the blink phase.</summary>
    public bool Render(CanvasDrawingSession ds, TerminalSession session, bool cursorOn, bool force)
    {
        IntPtr state = session.RenderState;
        IntPtr terminal = session.Terminal;
        if (state == IntPtr.Zero || terminal == IntPtr.Zero) return false;

        // The canvas clears itself before every Draw, so a frame must always be painted.
        bool changed = session.UpdateRenderState();

        // Colors.
        Rgb themeBg = default;
        Rgb themeFg = default;
        Rgb cursorRgb = default;
        bool cursorHasValue = false;
        Color defaultBg;
        Color defaultFg;

        if (GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_BACKGROUND, &themeBg) != GhosttyNative.SUCCESS)
            themeBg = new Rgb(7, 9, 14);
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_FOREGROUND, &themeFg);
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_CURSOR, &cursorRgb);
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_COLOR_CURSOR_HAS_VALUE, &cursorHasValue);

        if (!Theme.Terminal.Custom)
        {
            if (Theme.Light)
            {
                defaultBg = Color.FromArgb(255, 255, 255, 255);
                defaultFg = Color.FromArgb(255, 36, 41, 47);
            }
            else
            {
                themeBg = new Rgb(7, 9, 14);
                defaultBg = Color.FromArgb(255, 7, 9, 14);
                defaultFg = Color.FromArgb(255, themeFg.R, themeFg.G, themeFg.B);
            }
        }
        else
        {
            defaultBg = Color.FromArgb(255, themeBg.R, themeBg.G, themeBg.B);
            defaultFg = Color.FromArgb(255, themeFg.R, themeFg.G, themeFg.B);
        }

        ds.Clear(defaultBg);

        // Cursor.
        var cursor = new GhosttyNative.GhosttyRenderStateCursor
        {
            Size = (nuint)sizeof(GhosttyNative.GhosttyRenderStateCursor),
        };
        bool haveCursor = GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_CURSOR, &cursor) == GhosttyNative.SUCCESS;

        _cursorValid = haveCursor && cursor.ViewportHasValue != 0;
        if (_cursorValid)
        {
            _cursorX = cursor.ViewportX;
            _cursorY = cursor.ViewportY;
        }

        float cellW = _metrics.Width;
        float cellH = _metrics.Height;

        IntPtr rows = IntPtr.Zero;
        IntPtr cells = IntPtr.Zero;
        GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out rows);
        GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out cells);
        // The iterator is only usable once it is bound to a render state.
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_ROW_ITERATOR, &rows);

        var runs = new List<Run>(64);
        string cursorText = string.Empty;
        Color cursorFg = defaultFg;

        try
        {
            while (rows != IntPtr.Zero && GhosttyNative.ghostty_render_state_row_iterator_next(rows))
            {
                int viewportY = 0;
                if (GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_VIEWPORT_Y, &viewportY) != GhosttyNative.SUCCESS)
                    continue;
                if (GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_DATA_CELLS, &cells) != GhosttyNative.SUCCESS)
                    continue;

                var selection = new GhosttyNative.GhosttyRenderStateRowSelection
                {
                    Size = (nuint)sizeof(GhosttyNative.GhosttyRenderStateRowSelection),
                };
                bool hasSelection = GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_DATA_SELECTION, &selection) == GhosttyNative.SUCCESS;

                runs.Clear();
                float rowTop = viewportY * cellH;
                int col = 0;
                bool spacer = false;

                while (GhosttyNative.ghostty_render_state_row_cells_next(cells))
                {
                    var style = new GhosttyNative.GhosttyStyle
                    {
                        Size = (nuint)sizeof(GhosttyNative.GhosttyStyle),
                    };
                    GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_STYLE, &style);

                    Rgb bgRgb = default;
                    Rgb fgRgb = default;
                    bool hasBg = GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_BG_COLOR, &bgRgb) == GhosttyNative.SUCCESS;
                    bool hasFg = GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_FG_COLOR, &fgRgb) == GhosttyNative.SUCCESS;

                    Color bg = defaultBg;
                    if (hasBg)
                        bg = bgRgb == themeBg ? defaultBg : ThemeColor(bgRgb);

                    Color fg = defaultFg;
                    if (hasFg && fgRgb != themeFg)
                        fg = ThemeColor(fgRgb);

                    if (style.Inverse != 0) (bg, fg) = (fg, bg);

                    string text = ReadCellText(cells);

                    // A double-width glyph sits in the first of its two cells and libghostty-vt
                    // leaves the second one empty. That empty cell must not turn into a space
                    // inside the run, or the glyph after it drifts off the grid.
                    bool wide = false;
                    if (spacer)
                    {
                        spacer = false;
                        text = string.Empty;
                    }
                    else if (text.Length > 0 && WideText.IsWide(text))
                    {
                        wide = true;
                        spacer = true;
                    }

                    var run = new Run
                    {
                        Col = col,
                        Cols = 1,
                        Text = text,
                        Bg = bg,
                        Fg = style.Faint != 0 ? Blend(fg, bg, 0.65f) : fg,
                        Bold = style.Bold != 0,
                        Italic = style.Italic != 0,
                        Underline = style.Underline != 0,
                        Strikethrough = style.Strikethrough != 0,
                        Invisible = style.Invisible != 0,
                        Wide = wide,
                    };

                    if (haveCursor && cursor.ViewportHasValue != 0 &&
                        viewportY == cursor.ViewportY && col == cursor.ViewportX)
                    {
                        cursorText = text;
                        cursorFg = fg;
                    }

                    bool merged = false;
                    if (runs.Count > 0)
                    {
                        Run prev = runs[^1];
                        if (prev.Fg == run.Fg && prev.Bg == run.Bg && prev.Bold == run.Bold &&
                            prev.Italic == run.Italic && prev.Underline == run.Underline &&
                            prev.Strikethrough == run.Strikethrough && prev.Invisible == run.Invisible &&
                            prev.Col + prev.Cols == col)
                        {
                            prev.Text += run.Text;
                            prev.Cols += 1;
                            prev.Wide |= run.Wide;
                            runs[^1] = prev;
                            merged = true;
                        }
                    }
                    if (!merged) runs.Add(run);
                    col++;
                }

                // Backgrounds first so text from neighbouring runs is never clipped.
                foreach (Run run in runs)
                {
                    if (run.Bg != defaultBg)
                    {
                        ds.FillRectangle(new Rect(run.Col * cellW, rowTop, run.Cols * cellW, cellH), run.Bg);
                    }
                }

                if (hasSelection)
                {
                    var color = Theme.Light
                        ? Color.FromArgb(31, 0, 0, 0)
                        : Color.FromArgb(64, 255, 255, 255);
                    ds.FillRectangle(new Rect(selection.StartX * cellW, rowTop,
                        (selection.EndX - selection.StartX + 1) * cellW, cellH), color);
                }

                float lineThickness = MathF.Max(1f, MathF.Round(cellH / 16f));
                foreach (Run run in runs)
                {
                    if (run.Invisible) continue;
                    if (run.Text.Length > 0 && HasInk(run.Text))
                    {
                        // A run made only of single-width glyphs advances by exactly one cell
                        // per character in the mono font, so it can be drawn in one call. A run
                        // holding a double-width glyph cannot: the fallback font advances by
                        // its own em, which drifts off the grid, so each grapheme is placed.
                        if (!run.Wide)
                            ds.DrawText(run.Text, run.Col * cellW, rowTop, run.Fg, FormatFor(run.Bold, run.Italic));
                        else
                            DrawWideRun(ds, run, cellW, rowTop);
                    }

                    if (run.Underline)
                    {
                        ds.FillRectangle(new Rect(run.Col * cellW, rowTop + cellH - lineThickness - 2,
                            run.Cols * cellW, lineThickness), run.Fg);
                    }
                    if (run.Strikethrough)
                    {
                        float y = MathF.Round(rowTop + cellH * 0.5f);
                        ds.FillRectangle(new Rect(run.Col * cellW, y, run.Cols * cellW, lineThickness), run.Fg);
                    }
                }
            }
        }
        finally
        {
            if (cells != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_cells_free(cells);
            if (rows != IntPtr.Zero) GhosttyNative.ghostty_render_state_row_iterator_free(rows);
        }

        // IME composition: painted over the cells at the cursor so the pinyin being typed is
        // visible while the user picks a candidate, like Windows Terminal does. The buffer is
        // untouched until the composition commits, so this is a pure overlay.
        int compositionEnd = -1;
        if (_cursorValid && _imeComposition.Length > 0)
        {
            compositionEnd = DrawImeComposition(ds, cellW, cellH, _cursorY * cellH, _cursorX, defaultBg, defaultFg);
        }

        // Cursor. While composing it sits after the composition text, where the IME caret is.
        if (haveCursor && cursor.ViewportHasValue != 0 && cursor.Visible != 0 && cursorOn)
        {
            int cursorCol = compositionEnd >= 0 ? compositionEnd : cursor.ViewportX;
            var rect = new Rect(cursorCol * cellW, cursor.ViewportY * cellH, cellW, cellH);
            Color color = cursorHasValue ? ThemeColor(cursorRgb) : defaultFg;
            ds.FillRectangle(rect, color);

            if (cursorText.Length > 0)
                ds.DrawText(cursorText, (float)rect.X, (float)rect.Y, defaultBg, FormatFor(false, false));
        }

        return true;
    }

    /// <summary>
    /// Paints the in-flight IME composition starting at <paramref name="originCol"/> and
    /// returns the column just past it, where the caret belongs.
    /// </summary>
    private int DrawImeComposition(CanvasDrawingSession ds, float cellW, float cellH,
        float rowTop, int originCol, Color bg, Color fg)
    {
        CanvasTextFormat format = FormatFor(false, false);
        float underline = MathF.Max(1f, MathF.Round(cellH / 16f));
        int col = originCol;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(_imeComposition);
        while (elements.MoveNext())
        {
            string grapheme = elements.GetTextElement();
            if (grapheme.Length == 0) continue;

            int cells = WideText.IsWide(grapheme) ? 2 : 1;
            float x = col * cellW;

            ds.FillRectangle(new Rect(x, rowTop, cells * cellW, cellH), bg);
            ds.DrawText(grapheme, x, rowTop, fg, format);
            ds.FillRectangle(new Rect(x, rowTop + cellH - underline - 2, cells * cellW, underline), fg);

            col += cells;
        }

        return col;
    }

    /// <summary>
    /// Draws a run that contains double-width glyphs, placing every grapheme on its own cell
    /// so a fallback font's advance cannot push the rest of the line off the grid.
    /// </summary>
    private void DrawWideRun(CanvasDrawingSession ds, Run run, float cellW, float rowTop)
    {
        CanvasTextFormat format = FormatFor(run.Bold, run.Italic);
        int col = run.Col;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(run.Text);
        while (elements.MoveNext())
        {
            string grapheme = elements.GetTextElement();
            if (grapheme.Length == 0) continue;

            ds.DrawText(grapheme, col * cellW, rowTop, run.Fg, format);
            col += WideText.IsWide(grapheme) ? 2 : 1;
        }
    }

    private static bool HasInk(string text)
    {
        foreach (char ch in text)
        {
            if (ch != ' ' && ch != '\u00A0') return true;
        }
        return false;
    }

    private static Color Blend(Color fg, Color bg, float amount)
    {
        static byte Mix(byte a, byte b, float t) => (byte)(a + (b - a) * t);
        return Color.FromArgb(255, Mix(fg.R, bg.R, amount), Mix(fg.G, bg.G, amount), Mix(fg.B, bg.B, amount));
    }

    private static string ReadCellText(IntPtr cells)
    {
        var buffer = new GhosttyNative.GhosttyBuffer { Ptr = IntPtr.Zero, Cap = 0, Len = 0 };

        // First call with a NULL buffer: GHOSTTY_OUT_OF_SPACE and Len = required size.
        GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer);
        if (buffer.Len == 0) return " ";

        const int StackCapacity = 128;
        byte* temp = stackalloc byte[StackCapacity];
        if (buffer.Len <= StackCapacity)
        {
            buffer.Ptr = (IntPtr)temp;
            buffer.Cap = StackCapacity;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer) != GhosttyNative.SUCCESS)
                return " ";
            return buffer.Len == 0 ? " " : Encoding.UTF8.GetString(temp, (int)buffer.Len);
        }

        var heap = new byte[buffer.Len];
        fixed (byte* heapPtr = heap)
        {
            buffer.Ptr = (IntPtr)heapPtr;
            buffer.Cap = buffer.Len;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer) != GhosttyNative.SUCCESS)
                return " ";
            return buffer.Len == 0 ? " " : Encoding.UTF8.GetString(heap, 0, (int)buffer.Len);
        }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceEx(string file, uint flags, IntPtr reserved);
}
