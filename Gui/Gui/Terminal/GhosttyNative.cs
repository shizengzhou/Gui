using System;
using System.Runtime.InteropServices;

namespace Gui.Terminal;

/// <summary>
/// Raw P/Invoke surface for libghostty-vt (ghostty-vt.dll).
/// Header reference: include/ghostty/vt/terminal.h and render.h.
/// </summary>
internal static unsafe class GhosttyNative
{
    private const string Lib = "ghostty-vt";

    // ---- results ----
    internal const int SUCCESS = 0;

    // ---- GhosttyTerminalOption ----
    internal const int OPT_USERDATA = 0;
    internal const int OPT_WRITE_PTY = 1;
    internal const int OPT_BELL = 2;
    internal const int OPT_TITLE_CHANGED = 5;
    internal const int OPT_COLOR_FOREGROUND = 11;
    internal const int OPT_COLOR_BACKGROUND = 12;
    internal const int OPT_COLOR_CURSOR = 13;
    internal const int OPT_COLOR_PALETTE = 14;
    internal const int OPT_RESIZE_WINDOW = 47;

    // ---- GhosttyTerminalData ----
    internal const int TDATA_COLS = 1;
    internal const int TDATA_ROWS = 2;
    internal const int TDATA_TITLE = 12;

    // ---- GhosttyRenderStateData ----
    internal const int RS_COLS = 1;
    internal const int RS_ROWS = 2;
    internal const int RS_DIRTY = 3;
    internal const int RS_ROW_ITERATOR = 4;
    internal const int RS_COLOR_BACKGROUND = 5;
    internal const int RS_COLOR_FOREGROUND = 6;
    internal const int RS_COLOR_CURSOR = 7;
    internal const int RS_CURSOR_VISUAL_STYLE = 10;
    internal const int RS_CURSOR_VISIBLE = 11;
    internal const int RS_CURSOR_BLINKING = 12;
    internal const int RS_CURSOR_VIEWPORT_HAS_VALUE = 14;
    internal const int RS_CURSOR_VIEWPORT_X = 15;
    internal const int RS_CURSOR_VIEWPORT_Y = 16;

    // ---- GhosttyRenderStateRowData ----
    internal const int ROW_VIEWPORT_Y = 6;

    // ---- GhosttyRenderStateRowCellsData ----
    internal const int CELL_RAW = 1;
    internal const int CELL_STYLE = 2;
    internal const int CELL_GRAPHEMES_LEN = 3;
    internal const int CELL_BG_COLOR = 5;
    internal const int CELL_FG_COLOR = 6;
    internal const int CELL_SELECTED = 7;
    internal const int CELL_HAS_STYLING = 8;
    internal const int CELL_GRAPHEMES_UTF8 = 9;

    // ---- GhosttyCellData ----
    internal const int CD_WIDE = 3;

    // ---- GhosttyCellWide ----
    internal const int WIDE_NARROW = 0;
    internal const int WIDE_WIDE = 1;
    internal const int WIDE_SPACER_TAIL = 2;
    internal const int WIDE_SPACER_HEAD = 3;

    // ---- GhosttyTerminalScrollViewportTag ----
    internal const int SCROLL_DELTA = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyColorRgb
    {
        public byte R;
        public byte G;
        public byte B;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyString
    {
        public IntPtr Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyBuffer
    {
        public IntPtr Ptr;
        public nuint Cap;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyleColor
    {
        public int Tag;
        public ulong Value;
    }

    /// <summary>Sized struct: Size must be set to sizeof(GhosttyStyle).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyStyle
    {
        public nuint Size;
        public GhosttyStyleColor FgColor;
        public GhosttyStyleColor BgColor;
        public GhosttyStyleColor UnderlineColor;
        public byte Bold;
        public byte Italic;
        public byte Faint;
        public byte Blink;
        public byte Inverse;
        public byte Invisible;
        public byte Strikethrough;
        public byte Overline;
        public int Underline;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalScrollViewport
    {
        public int Tag;
        public IntPtr Delta;
        public ulong Padding;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void WritePtyFn(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void NotifyFn(IntPtr terminal, IntPtr userdata);

    // ---- terminal ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_terminal_new(IntPtr allocator, out IntPtr terminal, ushort cols, ushort rows);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_terminal_free(IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_terminal_resize(IntPtr terminal, ushort cols, ushort rows, uint cellWidthPx, uint cellHeightPx);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_terminal_set(IntPtr terminal, int option, IntPtr value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_terminal_vt_write(IntPtr terminal, byte* data, nuint len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_terminal_get(IntPtr terminal, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_terminal_scroll_viewport(IntPtr terminal, GhosttyTerminalScrollViewport behavior);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_cell_get(ulong cell, int data, void* output);

    // ---- render state ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_new(IntPtr allocator, out IntPtr state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_render_state_free(IntPtr state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_update(IntPtr state, IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_clean(IntPtr state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_get(IntPtr state, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_iterator_new(IntPtr allocator, out IntPtr iterator);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_render_state_row_iterator_free(IntPtr iterator);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool ghostty_render_state_row_iterator_next(IntPtr iterator);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_get(IntPtr iterator, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_cells_new(IntPtr allocator, out IntPtr cells);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_cells_select(IntPtr cells, ushort x);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_cells_get(IntPtr cells, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_render_state_row_cells_free(IntPtr cells);
}
