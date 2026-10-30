using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui.Terminal;

[Flags]
public enum CellAttrs : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Faint = 4,
    Strikethrough = 8,
    Underline = 16,
    Blink = 32,
    Inverse = 64,
}

public readonly record struct TermColor(byte R, byte G, byte B);

public sealed class TermRun
{
    public int Col;
    public int Width;
    public string Text = string.Empty;
    public TermColor Fg;
    public TermColor Bg;
    public CellAttrs Attrs;
}

public sealed class TermRow
{
    public readonly List<TermRun> Runs = new(8);
}

public sealed class TerminalSnapshot
{
    public int Cols;
    public int Rows;
    public TermRow[] RowData = Array.Empty<TermRow>();
    public TermColor DefaultFg;
    public TermColor DefaultBg;
    public TermColor CursorColor;
    public bool CursorVisible;
    public bool CursorBlinking;
    public int CursorStyle;   // 0 bar, 1 block, 2 underline, 3 hollow block
    public int CursorX;
    public int CursorY;
    public bool CursorInViewport;
}

/// <summary>
/// Owns a libghostty-vt terminal instance, its render state and the ConPTY child shell.
/// All terminal access is serialized on <see cref="_gate"/>.
/// </summary>
public sealed unsafe class TerminalSession : IDisposable
{
    private readonly object _gate = new();

    private IntPtr _terminal;
    private IntPtr _renderState;
    private IntPtr _rowIterator;
    private IntPtr _cells;
    private ConPty? _pty;
    private bool _disposed;

    // Delegates must stay alive for as long as the native terminal holds them.
    private GhosttyNative.WritePtyFn? _writePty;
    private GhosttyNative.NotifyFn? _bell;
    private GhosttyNative.NotifyFn? _titleChanged;

    public int Cols { get; private set; }
    public int Rows { get; private set; }

    /// <summary>Raised from the PTY reader thread whenever new output has been parsed.</summary>
    public event Action? OutputReady;

    /// <summary>
    /// Raised from the PTY reader thread when OSC 0/2 changes the title. Handlers must not
    /// call back into the terminal synchronously: this fires from inside vt_write.
    /// </summary>
    public event Action? TitleChanged;

    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public void Start(int cols, int rows, int cellWidthPx, int cellHeightPx)
    {
        Cols = cols;
        Rows = rows;

        int rc = GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out _terminal, (ushort)cols, (ushort)rows);
        if (rc != GhosttyNative.SUCCESS)
            throw new InvalidOperationException($"ghostty_terminal_new failed: {rc}");

        rc = GhosttyNative.ghostty_render_state_new(IntPtr.Zero, out _renderState);
        if (rc != GhosttyNative.SUCCESS)
            throw new InvalidOperationException($"ghostty_render_state_new failed: {rc}");

        rc = GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _rowIterator);
        if (rc != GhosttyNative.SUCCESS)
            throw new InvalidOperationException($"ghostty_render_state_row_iterator_new failed: {rc}");

        rc = GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out _cells);
        if (rc != GhosttyNative.SUCCESS)
            throw new InvalidOperationException($"ghostty_render_state_row_cells_new failed: {rc}");

        ApplyDefaultColors();

        _writePty = OnWritePty;
        _bell = OnBell;
        _titleChanged = OnTitleChanged;
        SetOptionFunctionPointer(GhosttyNative.OPT_WRITE_PTY, _writePty);
        SetOptionFunctionPointer(GhosttyNative.OPT_BELL, _bell);
        SetOptionFunctionPointer(GhosttyNative.OPT_TITLE_CHANGED, _titleChanged);

        _pty = ConPty.Start(ShellCommandLine(), (short)cols, (short)rows, WorkingDirectory);
        _pty.OutputReceived += OnPtyOutput;
        _pty.BeginRead();
    }

    private void SetOptionFunctionPointer(int option, Delegate callback)
    {
        // Callback options take the function pointer itself as the value pointer,
        // not the address of a function pointer variable.
        IntPtr fp = Marshal.GetFunctionPointerForDelegate(callback);
        GhosttyNative.ghostty_terminal_set(_terminal, option, fp);
    }

    private void ApplyDefaultColors()
    {
        var fg = new GhosttyNative.GhosttyColorRgb { R = 0xDC, G = 0xDF, B = 0xE4 };
        var bg = new GhosttyNative.GhosttyColorRgb { R = 0x1B, G = 0x1B, B = 0x1B };
        var cursor = new GhosttyNative.GhosttyColorRgb { R = 0xF0, G = 0xF0, B = 0xF0 };

        IntPtr pfg = Marshal.AllocHGlobal(3);
        IntPtr pbg = Marshal.AllocHGlobal(3);
        IntPtr pcur = Marshal.AllocHGlobal(3);
        try
        {
            Marshal.StructureToPtr(fg, pfg, false);
            Marshal.StructureToPtr(bg, pbg, false);
            Marshal.StructureToPtr(cursor, pcur, false);
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_FOREGROUND, pfg);
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_BACKGROUND, pbg);
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_CURSOR, pcur);
        }
        finally
        {
            Marshal.FreeHGlobal(pfg);
            Marshal.FreeHGlobal(pbg);
            Marshal.FreeHGlobal(pcur);
        }
    }

    private static string ShellCommandLine()
    {
        // GUITERM_SHELL lets the host pick a different shell without rebuilding.
        string? overrideShell = Environment.GetEnvironmentVariable("GUITERM_SHELL");
        if (!string.IsNullOrWhiteSpace(overrideShell))
            return overrideShell;

        string systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

        // Prefer a real Win32 executable over an MSIX execution alias: aliases are
        // activated through the app model, which can drop the pseudoconsole attachment.
        string powershell = System.IO.Path.Combine(systemRoot, @"System32\WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(powershell))
            return $"\"{powershell}\" -NoLogo";

        string pwsh = System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
            @"PowerShell\7\pwsh.exe");
        if (File.Exists(pwsh))
            return $"\"{pwsh}\" -NoLogo";

        return Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
    }

    private void OnPtyOutput(byte[] buffer, int count)
    {
        lock (_gate)
        {
            if (_disposed || _terminal == IntPtr.Zero)
                return;
            fixed (byte* p = buffer)
            {
                GhosttyNative.ghostty_terminal_vt_write(_terminal, p, (nuint)count);
            }
        }
        OutputReady?.Invoke();
    }

    /// <summary>Replies generated by the VT parser (DA, DSR, ...) go back to the child.</summary>
    private void OnWritePty(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len)
    {
        var pty = _pty;
        if (pty is null || data == IntPtr.Zero || len == 0)
            return;
        var span = new ReadOnlySpan<byte>((void*)data, checked((int)len));
        pty.Write(span);
    }

    private void OnBell(IntPtr terminal, IntPtr userdata)
    {
        // No audible bell in this app; ignored on purpose.
    }

    private void OnTitleChanged(IntPtr terminal, IntPtr userdata)
    {
        TitleChanged?.Invoke();
    }

    public string ReadTitle()
    {
        GhosttyNative.GhosttyString s;
        lock (_gate)
        {
            if (_terminal == IntPtr.Zero)
                return string.Empty;
            if (GhosttyNative.ghostty_terminal_get(_terminal, GhosttyNative.TDATA_TITLE, &s) != GhosttyNative.SUCCESS)
                return string.Empty;
        }
        return s.Ptr == IntPtr.Zero || s.Len == 0
            ? string.Empty
            : Encoding.UTF8.GetString((byte*)s.Ptr, checked((int)s.Len));
    }

    public void Write(ReadOnlySpan<byte> data) => _pty?.Write(data);

    public void Write(string text) => _pty?.Write(Encoding.UTF8.GetBytes(text));

    public void Resize(int cols, int rows, int cellWidthPx, int cellHeightPx)
    {
        if (cols <= 0 || rows <= 0)
            return;
        if (cols == Cols && rows == Rows)
            return;

        Cols = cols;
        Rows = rows;

        _pty?.Resize((short)cols, (short)rows);
        lock (_gate)
        {
            if (_terminal != IntPtr.Zero)
                GhosttyNative.ghostty_terminal_resize(_terminal, (ushort)cols, (ushort)rows, (uint)cellWidthPx, (uint)cellHeightPx);
        }
    }

    public void Scroll(int deltaRows)
    {
        lock (_gate)
        {
            if (_terminal == IntPtr.Zero)
                return;
            var behavior = new GhosttyNative.GhosttyTerminalScrollViewport
            {
                Tag = GhosttyNative.SCROLL_DELTA,
                Delta = (IntPtr)deltaRows,
            };
            GhosttyNative.ghostty_terminal_scroll_viewport(_terminal, behavior);
        }
        OutputReady?.Invoke();
    }

    /// <summary>
    /// Captures the current viewport as plain run data. Safe to call from the UI thread;
    /// blocks briefly while the IO thread is inside vt_write.
    /// </summary>
    public TerminalSnapshot Capture()
    {
        var snapshot = new TerminalSnapshot();
        lock (_gate)
        {
            if (_terminal == IntPtr.Zero || _renderState == IntPtr.Zero)
                return snapshot;

            if (GhosttyNative.ghostty_render_state_update(_renderState, _terminal) != GhosttyNative.SUCCESS)
                return snapshot;

            ushort cols = 0, rows = 0;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_COLS, &cols);
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_ROWS, &rows);

            snapshot.Cols = cols;
            snapshot.Rows = rows;
            snapshot.RowData = new TermRow[rows];
            for (int i = 0; i < rows; i++)
                snapshot.RowData[i] = new TermRow();

            GhosttyNative.GhosttyColorRgb color;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_COLOR_BACKGROUND, &color);
            snapshot.DefaultBg = new TermColor(color.R, color.G, color.B);
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_COLOR_FOREGROUND, &color);
            snapshot.DefaultFg = new TermColor(color.R, color.G, color.B);
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_COLOR_CURSOR, &color);
            snapshot.CursorColor = new TermColor(color.R, color.G, color.B);

            bool cursorVisible = false;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_CURSOR_VISIBLE, &cursorVisible);
            snapshot.CursorVisible = cursorVisible;

            bool blinking = false;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_CURSOR_BLINKING, &blinking);
            snapshot.CursorBlinking = blinking;

            int cursorStyle = 1;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_CURSOR_VISUAL_STYLE, &cursorStyle);
            snapshot.CursorStyle = cursorStyle;

            bool cursorInViewport = false;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_CURSOR_VIEWPORT_HAS_VALUE, &cursorInViewport);
            snapshot.CursorInViewport = cursorInViewport;
            if (cursorInViewport)
            {
                ushort cx = 0, cy = 0;
                GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_CURSOR_VIEWPORT_X, &cx);
                GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_CURSOR_VIEWPORT_Y, &cy);
                snapshot.CursorX = cx;
                snapshot.CursorY = cy;
            }

            IntPtr iterator = _rowIterator;
            GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_ROW_ITERATOR, &iterator);

            while (GhosttyNative.ghostty_render_state_row_iterator_next(iterator))
            {
                int viewportY = -1;
                GhosttyNative.ghostty_render_state_row_get(iterator, GhosttyNative.ROW_VIEWPORT_Y, &viewportY);
                if (viewportY < 0 || viewportY >= rows)
                    continue;

                IntPtr cells = _cells;
                if (GhosttyNative.ghostty_render_state_row_get(iterator, 3 /* ROW_DATA_CELLS */, &cells) != GhosttyNative.SUCCESS)
                    continue;

                CaptureRow(cells, cols, snapshot.RowData[viewportY], snapshot);
            }

            GhosttyNative.ghostty_render_state_clean(_renderState);
        }
        return snapshot;
    }

    private void CaptureRow(IntPtr cells, int cols, TermRow row, TerminalSnapshot snapshot)
    {
        TermRun? current = null;
        for (int x = 0; x < cols; x++)
        {
            if (GhosttyNative.ghostty_render_state_row_cells_select(cells, (ushort)x) != GhosttyNative.SUCCESS)
                continue;

            ulong raw = 0;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_RAW, &raw) != GhosttyNative.SUCCESS)
                continue;

            int wide = 0;
            GhosttyNative.ghostty_cell_get(raw, GhosttyNative.CD_WIDE, &wide);
            if (wide == GhosttyNative.WIDE_SPACER_TAIL || wide == GhosttyNative.WIDE_SPACER_HEAD)
                continue;

            CellAttrs attrs = CellAttrs.None;
            bool invisible = false;
            byte hasStyling = 0;
            GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_HAS_STYLING, &hasStyling);
            if (hasStyling != 0)
            {
                var style = new GhosttyNative.GhosttyStyle { Size = (nuint)Marshal.SizeOf<GhosttyNative.GhosttyStyle>() };
                if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_STYLE, &style) == GhosttyNative.SUCCESS)
                {
                    if (style.Bold != 0) attrs |= CellAttrs.Bold;
                    if (style.Italic != 0) attrs |= CellAttrs.Italic;
                    if (style.Faint != 0) attrs |= CellAttrs.Faint;
                    if (style.Strikethrough != 0) attrs |= CellAttrs.Strikethrough;
                    if (style.Underline != 0) attrs |= CellAttrs.Underline;
                    if (style.Blink != 0) attrs |= CellAttrs.Blink;
                    if (style.Inverse != 0) attrs |= CellAttrs.Inverse;
                    if (style.Invisible != 0) invisible = true;
                }
            }

            string text = ReadCellText(cells);

            TermColor fg = snapshot.DefaultFg;
            TermColor bg = snapshot.DefaultBg;
            GhosttyNative.GhosttyColorRgb c;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_FG_COLOR, &c) == GhosttyNative.SUCCESS)
                fg = new TermColor(c.R, c.G, c.B);
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_BG_COLOR, &c) == GhosttyNative.SUCCESS)
                bg = new TermColor(c.R, c.G, c.B);

            if ((attrs & CellAttrs.Inverse) != 0)
                (fg, bg) = (bg, fg);

            if (invisible)
            {
                text = " ";
                attrs = CellAttrs.None;
            }

            int width = wide == GhosttyNative.WIDE_WIDE ? 2 : 1;

            if (current is not null && current.Fg == fg && current.Bg == bg && current.Attrs == attrs && current.Col + current.Width == x)
            {
                current.Text += text;
                current.Width += width;
            }
            else
            {
                current = new TermRun { Col = x, Width = width, Text = text, Fg = fg, Bg = bg, Attrs = attrs };
                row.Runs.Add(current);
            }
        }
    }

    private string ReadCellText(IntPtr cells)
    {
        GhosttyNative.GhosttyBuffer buffer;
        buffer.Ptr = IntPtr.Zero;
        buffer.Cap = 0;
        buffer.Len = 0;

        // First call with a NULL buffer: GHOSTTY_OUT_OF_SPACE and Len = required size.
        GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer);
        if (buffer.Len == 0)
            return " ";

        const int StackCapacity = 128;
        if (buffer.Len <= StackCapacity)
        {
            byte* temp = stackalloc byte[StackCapacity];
            buffer.Ptr = (IntPtr)temp;
            buffer.Cap = StackCapacity;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer) != GhosttyNative.SUCCESS)
                return " ";
            return buffer.Len == 0 ? " " : Encoding.UTF8.GetString(temp, (int)buffer.Len);
        }

        var heap = new byte[buffer.Len];
        fixed (byte* p = heap)
        {
            buffer.Ptr = (IntPtr)p;
            buffer.Cap = buffer.Len;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells, GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer) != GhosttyNative.SUCCESS)
                return " ";
            return buffer.Len == 0 ? " " : Encoding.UTF8.GetString(heap, 0, (int)buffer.Len);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _pty?.Dispose();
        _pty = null;

        lock (_gate)
        {
            if (_cells != IntPtr.Zero) { GhosttyNative.ghostty_render_state_row_cells_free(_cells); _cells = IntPtr.Zero; }
            if (_rowIterator != IntPtr.Zero) { GhosttyNative.ghostty_render_state_row_iterator_free(_rowIterator); _rowIterator = IntPtr.Zero; }
            if (_renderState != IntPtr.Zero) { GhosttyNative.ghostty_render_state_free(_renderState); _renderState = IntPtr.Zero; }
            if (_terminal != IntPtr.Zero) { GhosttyNative.ghostty_terminal_free(_terminal); _terminal = IntPtr.Zero; }
        }

        GC.SuppressFinalize(this);
    }
}
