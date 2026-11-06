using System;
using System.Runtime.InteropServices;

namespace Gui.Term;

/// <summary>
/// Raw P/Invoke surface for libghostty-vt (ghostty-vt.dll).
/// Header reference: include/ghostty/vt/terminal.h, render.h, key/*.h, formatter.h, paste.h.
/// </summary>
internal static unsafe class GhosttyNative
{
    private const string Lib = "ghostty-vt";

    internal const int SUCCESS = 0;
    internal const int OUT_OF_SPACE = -3;

    // ---- GhosttyTerminalOption ----
    internal const int OPT_USERDATA = 0;
    internal const int OPT_WRITE_PTY = 1;
    internal const int OPT_BELL = 2;
    internal const int OPT_TITLE_CHANGED = 5;
    internal const int OPT_COLOR_FOREGROUND = 11;
    internal const int OPT_COLOR_BACKGROUND = 12;
    internal const int OPT_COLOR_CURSOR = 13;
    internal const int OPT_COLOR_PALETTE = 14;
    internal const int OPT_SELECTION = 21;
    internal const int OPT_PWD_CHANGED = 25;
    internal const int OPT_RESIZE_WINDOW = 47;

    // ---- GhosttyTerminalData ----
    internal const int TDATA_COLS = 1;
    internal const int TDATA_ROWS = 2;
    internal const int TDATA_TITLE = 12;
    internal const int TDATA_PWD = 13;
    internal const int TDATA_SELECTION = 31;
    internal const int TDATA_MODE = 37;

    // ---- GhosttyRenderStateData ----
    internal const int RS_COLS = 1;
    internal const int RS_ROWS = 2;
    internal const int RS_DIRTY = 3;
    internal const int RS_ROW_ITERATOR = 4;
    internal const int RS_COLOR_BACKGROUND = 5;
    internal const int RS_COLOR_FOREGROUND = 6;
    internal const int RS_COLOR_CURSOR = 7;
    internal const int RS_COLOR_CURSOR_HAS_VALUE = 8;
    internal const int RS_CURSOR_VISUAL_STYLE = 10;
    internal const int RS_CURSOR_VISIBLE = 11;
    internal const int RS_CURSOR_BLINKING = 12;
    internal const int RS_CURSOR_VIEWPORT_HAS_VALUE = 14;
    internal const int RS_CURSOR_VIEWPORT_X = 15;
    internal const int RS_CURSOR_VIEWPORT_Y = 16;
    internal const int RS_CURSOR_VIEWPORT_WIDE_TAIL = 17;
    internal const int RS_CURSOR = 18;
    internal const int RS_COLORS = 19;

    // ---- GhosttyRenderStateRowData ----
    internal const int ROW_DATA_CELLS = 3;
    internal const int ROW_DATA_SELECTION = 4;
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
    internal const int SCROLL_TOP = 0;
    internal const int SCROLL_BOTTOM = 1;
    internal const int SCROLL_DELTA = 2;

    // ---- GhosttyPointTag ----
    internal const int POINT_VIEWPORT = 1;

    // ---- GhosttyKeyAction ----
    internal const int KEY_ACTION_PRESS = 1;

    // ---- GhosttyMods ----
    internal const int MODS_SHIFT = 1 << 0;
    internal const int MODS_CTRL = 1 << 1;
    internal const int MODS_ALT = 1 << 2;
    internal const int MODS_SUPER = 1 << 3;
    internal const int MODS_CAPS_LOCK = 1 << 4;
    internal const int MODS_NUM_LOCK = 1 << 5;

    // ---- GhosttyFormatterFormat ----
    internal const int FORMAT_PLAIN = 0;

    // ---- GhosttyRenderStateCursorVisualStyle ----
    internal const int CURSOR_BAR = 0;
    internal const int CURSOR_BLOCK = 1;
    internal const int CURSOR_UNDERLINE = 2;
    internal const int CURSOR_BLOCK_HOLLOW = 3;

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

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct GhosttyTerminalScrollViewport
    {
        [FieldOffset(0)] public int Tag;
        [FieldOffset(8)] public IntPtr Delta;
    }

    /// <summary>Sized struct: Size must be set to sizeof(GhosttyGridRef).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyGridRef
    {
        public nuint Size;
        public IntPtr Node;
        public ushort X;
        public ushort Y;
    }

    /// <summary>
    /// GhosttyPoint: tag at 0, then an 8-aligned union whose coordinate is
    /// { uint16 x; uint32 y; }. The two bytes between them are padding, so y must be a
    /// 32-bit field at offset 12; declaring it as ushort at offset 10 writes into the
    /// padding and the native side always sees y = 0.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct GhosttyPoint
    {
        [FieldOffset(0)] public int Tag;
        [FieldOffset(8)] public ushort X;
        [FieldOffset(12)] public uint Y;
    }

    /// <summary>Sized struct: Size must be set to sizeof(GhosttySelection).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    internal struct GhosttySelection
    {
        [FieldOffset(0)] public nuint Size;
        [FieldOffset(8)] public GhosttyGridRef Start;
        [FieldOffset(32)] public GhosttyGridRef End;
        [FieldOffset(56)] public byte Rectangle;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalModeConfig
    {
        public ushort Mode;
        public byte Value;
    }

    /// <summary>Sized struct: Size must be set to sizeof(GhosttyRenderStateRowSelection).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyRenderStateRowSelection
    {
        public nuint Size;
        public ushort StartX;
        public ushort EndX;
    }

    /// <summary>Sized struct: Size must be set to sizeof(GhosttyRenderStateCursor).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyRenderStateCursor
    {
        public nuint Size;
        public byte ViewportHasValue;
        public ushort ViewportX;
        public ushort ViewportY;
        public byte WideTail;
        public byte Visible;
        public byte Blinking;
        public byte PasswordInput;
        public int VisualStyle;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    internal struct GhosttyFormatterTerminalExtra
    {
        [FieldOffset(0)] public nuint Size;
        [FieldOffset(8)] public byte Palette;
        [FieldOffset(9)] public byte Modes;
        [FieldOffset(10)] public byte ScrollingRegion;
        [FieldOffset(11)] public byte Tabstops;
        [FieldOffset(12)] public byte Pwd;
        [FieldOffset(13)] public byte Keyboard;
    }

    /// <summary>Sized struct: Size must be set to sizeof(GhosttyFormatterTerminalOptions).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    internal struct GhosttyFormatterTerminalOptions
    {
        [FieldOffset(0)] public nuint Size;
        [FieldOffset(8)] public int Emit;
        [FieldOffset(12)] public byte Unwrap;
        [FieldOffset(13)] public byte Trim;
        [FieldOffset(16)] public GhosttyFormatterTerminalExtra Extra;
        [FieldOffset(48)] public IntPtr Selection;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ColorPaletteMask
    {
        public ulong Bits0;
        public ulong Bits1;
        public ulong Bits2;
        public ulong Bits3;

        public void Set(int index)
        {
            ulong bit = 1UL << (index & 63);
            switch (index >> 6)
            {
                case 0: Bits0 |= bit; break;
                case 1: Bits1 |= bit; break;
                case 2: Bits2 |= bit; break;
                default: Bits3 |= bit; break;
            }
        }
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
    internal static extern int ghostty_terminal_set(IntPtr terminal, int option, void* value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_terminal_vt_write(IntPtr terminal, byte* data, nuint len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_terminal_get(IntPtr terminal, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_terminal_scroll_viewport(IntPtr terminal, GhosttyTerminalScrollViewport behavior);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_terminal_grid_ref(IntPtr terminal, GhosttyPoint point, GhosttyGridRef* output);

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
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool ghostty_render_state_row_cells_next(IntPtr cells);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_cells_select(IntPtr cells, ushort x);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_row_cells_get(IntPtr cells, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_render_state_row_cells_free(IntPtr cells);

    // ---- key encoder / event ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_key_encoder_new(IntPtr allocator, out IntPtr encoder);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_encoder_free(IntPtr encoder);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_encoder_setopt_from_terminal(IntPtr encoder, IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_key_encoder_encode(IntPtr encoder, IntPtr keyEvent, byte* buffer, nuint bufferSize, out nuint written);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_key_event_new(IntPtr allocator, out IntPtr keyEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_event_free(IntPtr keyEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_event_set_action(IntPtr keyEvent, int action);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_event_set_key(IntPtr keyEvent, int key);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_event_set_mods(IntPtr keyEvent, ushort mods);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_event_set_utf8(IntPtr keyEvent, byte* utf8, nuint len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_key_event_set_unshifted_codepoint(IntPtr keyEvent, uint codepoint);

    // ---- paste ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_paste_encode(byte* data, nuint dataLen, [MarshalAs(UnmanagedType.U1)] bool bracketed,
        byte* buffer, nuint bufferLen, out nuint written);

    // ---- formatter ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_formatter_terminal_new(IntPtr allocator, out IntPtr formatter, IntPtr terminal,
        GhosttyFormatterTerminalOptions options);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_formatter_format_buf(IntPtr formatter, byte* buffer, nuint bufferLen, out nuint written);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_formatter_free(IntPtr formatter);

    // ---- color ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_color_palette_default(Rgb* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_color_palette_generate(Rgb* baseColors, ColorPaletteMask* skip, Rgb* background,
        Rgb* foreground, [MarshalAs(UnmanagedType.U1)] bool harmonious, Rgb* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern double ghostty_color_perceived_luminance(Rgb* color);

    internal static void PaletteDefault(Rgb[] output)
    {
        fixed (Rgb* p = output)
            ghostty_color_palette_default(p);
    }

    internal static void PaletteGenerate(Rgb[] baseColors, ColorPaletteMask skip, Rgb background, Rgb foreground,
        bool harmonious, Rgb[] output)
    {
        fixed (Rgb* basePtr = baseColors)
        fixed (Rgb* outPtr = output)
        {
            ColorPaletteMask local = skip;
            ghostty_color_palette_generate(basePtr, &local, &background, &foreground, harmonious, outPtr);
        }
    }

    internal static double PaletteLuminance(Rgb color)
    {
        Rgb local = color;
        return ghostty_color_perceived_luminance(&local);
    }
}
