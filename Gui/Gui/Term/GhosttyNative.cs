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
    internal const int NO_VALUE = -4;

    // ---- GhosttyTerminalOption ----
    internal const int OPT_USERDATA = 1;
    internal const int OPT_WRITE_PTY = 2;
    internal const int OPT_BELL = 3;
    internal const int OPT_ENQUIRY = 4;
    internal const int OPT_TITLE_CHANGED = 6;
    internal const int OPT_COLOR_FOREGROUND = 12;
    internal const int OPT_COLOR_BACKGROUND = 13;
    internal const int OPT_COLOR_CURSOR = 14;
    internal const int OPT_COLOR_PALETTE = 15;
    internal const int OPT_KITTY_IMAGE_STORAGE_LIMIT = 16;
    internal const int OPT_KITTY_IMAGE_MEDIUM_FILE = 17;
    internal const int OPT_KITTY_IMAGE_MEDIUM_TEMP_FILE = 18;
    internal const int OPT_KITTY_IMAGE_MEDIUM_SHARED_MEM = 19;
    internal const int OPT_APC_MAX_BYTES = 20;
    internal const int OPT_APC_MAX_BYTES_KITTY = 21;
    internal const int OPT_SELECTION = 22;
    internal const int OPT_PWD_CHANGED = 26;
    internal const int OPT_CLIPBOARD_WRITE = 27;
    internal const int OPT_SCROLLBACK_MAX_BYTES = 28;
    internal const int OPT_SCROLLBACK_MAX_LINES = 29;
    internal const int OPT_DESKTOP_NOTIFICATION = 30;
    internal const int OPT_TITLE_REPORT = 33;
    internal const int OPT_CLIPBOARD_READ = 39;
    internal const int OPT_CLIPBOARD_WRITE_MAX_BYTES = 40;
    internal const int OPT_XT_CHECKSUM_REPORT = 45;
    internal const int OPT_XT_CHECKSUM_EXTENSION = 46;
    internal const int OPT_PROGRAM_STATUS = 47;
    internal const int OPT_RESIZE_WINDOW = 48;

    // ---- GhosttyTerminalResizeWindowTag ----
    internal const int RESIZE_WINDOW_GRID = 0;

    // ---- GhosttyProgramStatusState ----
    internal const int PROGRAM_STATUS_IDLE = 0;
    internal const int PROGRAM_STATUS_WORKING = 1;
    internal const int PROGRAM_STATUS_DONE = 2;
    internal const int PROGRAM_STATUS_BLOCKED = 3;
    internal const int PROGRAM_STATUS_ERROR = 4;
    internal const int PROGRAM_STATUS_CLEAR = 5;

    // ---- GhosttyProgramStatusKind ----
    internal const int PROGRAM_STATUS_KIND_NONE = 0;
    internal const int PROGRAM_STATUS_KIND_PERMISSION = 1;
    internal const int PROGRAM_STATUS_KIND_QUESTION = 2;
    internal const int PROGRAM_STATUS_KIND_AUTH = 3;

    // ---- GhosttyClipboardLocation / results ----
    internal const int CLIPBOARD_LOCATION_STANDARD = 0;
    internal const int CLIPBOARD_WRITE_SUCCESS = 0;
    internal const int CLIPBOARD_WRITE_DENIED = 1;
    internal const int CLIPBOARD_WRITE_UNSUPPORTED = 2;
    internal const int CLIPBOARD_WRITE_INVALID_DATA = 4;
    internal const int CLIPBOARD_WRITE_IO_ERROR = 5;
    internal const int CLIPBOARD_READ_SUCCESS = 0;
    internal const int CLIPBOARD_READ_DENIED = 1;
    internal const int CLIPBOARD_READ_UNSUPPORTED = 2;
    internal const int CLIPBOARD_READ_IO_ERROR = 4;

    // ---- GhosttyTerminalData ----
    internal const int TDATA_COLS = 1;
    internal const int TDATA_ROWS = 2;
    internal const int TDATA_KITTY_KEYBOARD_FLAGS = 8;
    internal const int TDATA_SCROLLBAR = 9;
    internal const int TDATA_MOUSE_TRACKING = 11;
    internal const int TDATA_TITLE = 12;
    internal const int TDATA_PWD = 13;
    internal const int TDATA_KITTY_GRAPHICS = 30;
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
    internal const int RS_IMAGE_ITERATOR = 22;
    internal const int RS_IMAGE_PLACEMENTS_GENERATION = 23;

    // ---- GhosttyRenderStateImageData ----
    internal const int RENDER_IMAGE_PROTOCOL = 1;
    internal const int RENDER_IMAGE_ID = 2;
    internal const int RENDER_IMAGE_GENERATION = 3;
    internal const int RENDER_IMAGE_LAYER = 4;
    internal const int RENDER_IMAGE_Z = 5;
    internal const int RENDER_IMAGE_VIEWPORT_COL = 6;
    internal const int RENDER_IMAGE_VIEWPORT_ROW = 7;
    internal const int RENDER_IMAGE_GRID_COLS = 8;
    internal const int RENDER_IMAGE_GRID_ROWS = 9;
    internal const int RENDER_IMAGE_CELL_OFFSET_X = 10;
    internal const int RENDER_IMAGE_CELL_OFFSET_Y = 11;
    internal const int RENDER_IMAGE_PIXEL_WIDTH = 12;
    internal const int RENDER_IMAGE_PIXEL_HEIGHT = 13;
    internal const int RENDER_IMAGE_SOURCE_X = 14;
    internal const int RENDER_IMAGE_SOURCE_Y = 15;
    internal const int RENDER_IMAGE_SOURCE_WIDTH = 16;
    internal const int RENDER_IMAGE_SOURCE_HEIGHT = 17;

    // ---- GhosttyImageProtocol ----
    internal const int IMAGE_PROTOCOL_KITTY = 0;

    // ---- GhosttyRenderStateImageLayer ----
    internal const int IMAGE_LAYER_BELOW_BACKGROUND = 0;
    internal const int IMAGE_LAYER_BELOW_TEXT = 1;
    internal const int IMAGE_LAYER_ABOVE_TEXT = 2;

    // ---- GhosttyKittyGraphicsImageData ----
    // PNG payloads are decoded to RGBA before storage, so the stored image is always
    // uncompressed pixels: PIXELS/EXPECTED_LEN describe the decoded buffer.
    internal const int KITTY_IMAGE_DATA_ID = 1;
    internal const int KITTY_IMAGE_DATA_NUMBER = 2;
    internal const int KITTY_IMAGE_DATA_WIDTH = 3;
    internal const int KITTY_IMAGE_DATA_HEIGHT = 4;
    internal const int KITTY_IMAGE_DATA_FORMAT = 5;
    internal const int KITTY_IMAGE_DATA_COMPRESSION = 6;
    internal const int KITTY_IMAGE_DATA_PIXELS = 7;
    internal const int KITTY_IMAGE_DATA_EXPECTED_LEN = 8;
    internal const int KITTY_IMAGE_DATA_GENERATION = 9;

    // ---- GhosttyKittyImageFormat ----
    internal const int KITTY_IMAGE_FORMAT_RGB = 0;
    internal const int KITTY_IMAGE_FORMAT_RGBA = 1;
    internal const int KITTY_IMAGE_FORMAT_PNG = 2;
    internal const int KITTY_IMAGE_FORMAT_GRAY_ALPHA = 3;
    internal const int KITTY_IMAGE_FORMAT_GRAY = 4;

    internal const int KITTY_IMAGE_COMPRESSION_NONE = 0;

    // ---- GhosttySysOption / GhosttyBuildInfo ----
    internal const int SYS_OPT_DECODE_PNG = 2;
    internal const int BUILD_INFO_KITTY_GRAPHICS = 2;

    // ---- GhosttyRenderStateRowData ----
    internal const int ROW_DATA_RAW = 2;
    internal const int ROW_DATA_CELLS = 3;
    internal const int ROW_DATA_SELECTION = 4;
    internal const int ROW_VIEWPORT_Y = 6;

    // ---- GhosttyRowData ----
    internal const int ROW_DATA_WRAP = 1;
    internal const int ROW_DATA_WRAP_CONTINUATION = 2;

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
    internal const int SCROLL_ROW = 3;
    internal const int SCROLL_DELTA_PROMPT = 4;

    // ---- GhosttyPointTag ----
    internal const int POINT_VIEWPORT = 1;
    internal const int POINT_SCREEN = 2;

    // ---- GhosttyMouseAction ----
    internal const int MOUSE_ACTION_PRESS = 0;
    internal const int MOUSE_ACTION_RELEASE = 1;
    internal const int MOUSE_ACTION_MOTION = 2;

    // ---- GhosttyMouseButton ----
    internal const int MOUSE_BUTTON_LEFT = 1;
    internal const int MOUSE_BUTTON_RIGHT = 2;
    internal const int MOUSE_BUTTON_MIDDLE = 3;
    internal const int MOUSE_BUTTON_FOUR = 4;
    internal const int MOUSE_BUTTON_FIVE = 5;

    // ---- search ----
    internal const int SEARCH_STATUS_RUNNING = 0;
    internal const int SEARCH_STATUS_FEED_REQUIRED = 1;
    internal const int SEARCH_STATUS_COMPLETE = 2;

    internal const int SEARCH_DATA_STATUS = 1;
    internal const int SEARCH_DATA_NEEDLE = 2;
    internal const int SEARCH_DATA_TOTAL_MATCHES = 3;
    internal const int SEARCH_DATA_SELECTED_INDEX = 4;
    internal const int SEARCH_DATA_SELECTED_MATCH = 5;
    internal const int SEARCH_DATA_MATCHES = 6;
    internal const int SEARCH_DATA_VIEWPORT_MATCHES = 7;
    internal const int SEARCH_DATA_SELECT_SCROLL = 8;

    internal const int SEARCH_OPT_NEEDLE = 1;
    internal const int SEARCH_OPT_SELECT_SCROLL = 4;

    internal const int SEARCH_SCROLL_IF_NEEDED = 0;
    internal const int SEARCH_SCROLL_NONE = 1;

    // ---- GhosttyKeyAction ----
    internal const int KEY_ACTION_RELEASE = 0;
    internal const int KEY_ACTION_PRESS = 1;
    internal const int KEY_ACTION_REPEAT = 2;

    // ---- Kitty keyboard protocol flags (GhosttyKittyKeyFlags) ----
    internal const int KITTY_KEY_DISAMBIGUATE = 1 << 0;
    internal const int KITTY_KEY_REPORT_EVENTS = 1 << 1;
    internal const int KITTY_KEY_REPORT_ALTERNATES = 1 << 2;
    internal const int KITTY_KEY_REPORT_ALL = 1 << 3;
    internal const int KITTY_KEY_REPORT_ASSOCIATED = 1 << 4;

    // ---- GhosttyMods ----
    internal const int MODS_SHIFT = 1 << 0;
    internal const int MODS_CTRL = 1 << 1;
    internal const int MODS_ALT = 1 << 2;
    internal const int MODS_SUPER = 1 << 3;
    internal const int MODS_CAPS_LOCK = 1 << 4;
    internal const int MODS_NUM_LOCK = 1 << 5;

    // ---- GhosttyFormatterFormat ----
    internal const int FORMAT_PLAIN = 0;
    internal const int FORMAT_VT = 1;

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

    /// <summary>
    /// Decoded PNG pixels (RGBA, row-major, no padding), allocated with
    /// <see cref="ghostty_alloc"/> through the allocator the decoder receives.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttySysImage
    {
        public uint Width;
        public uint Height;
        public IntPtr Data;
        public nuint DataLen;
    }

    /// <summary>One MIME representation in a clipboard write/read reply.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyClipboardContent
    {
        public GhosttyString Mime;
        public GhosttyString Data;
    }

    /// <summary>Sized request: the embedder answers through <see cref="GhosttyClipboardWrite.Reply"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyClipboardWrite
    {
        public int Location;
        public IntPtr Contents;
        public nuint ContentsLen;
        public GhosttyString Name;
        public byte Granted;
        public byte CanRemember;
        public IntPtr Ctx;
        public IntPtr Reply;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyClipboardRead
    {
        public int Location;
        public IntPtr Mimes;
        public nuint MimesLen;
        public byte List;
        public GhosttyString Name;
        public byte Granted;
        public byte CanRemember;
        public IntPtr Ctx;
        public IntPtr Reply;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyClipboardWriteReply
    {
        public nuint Size;
        public int Result;
        public byte Remember;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyClipboardReadReply
    {
        public nuint Size;
        public int Result;
        public IntPtr Contents;
        public nuint ContentsLen;
        public IntPtr Available;
        public nuint AvailableLen;
        public byte Remember;
    }

    /// <summary>
    /// One style color slot: a tag byte followed by the 3-byte color value. PALETTE holds
    /// a palette index in <see cref="V0"/>; RGB holds three bytes. Fixed 4-byte layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct GhosttyStyleColor
    {
        public byte Tag;               // 0 none, 1 palette, 2 rgb
        public byte V0;
        public byte V1;
        public byte V2;

        public bool HasValue => Tag != 0;
        public byte PaletteIndex => V0;
    }

    /// <summary>
    /// A cell's complete style. Fixed 16-byte layout with no size field. The flags word
    /// holds the on/off attributes in its low byte (bold/italic/faint/blink/inverse/
    /// invisible/strikethrough/overline) and the GhosttySgrUnderline value in bits 8-10.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct GhosttyStyle
    {
        public GhosttyStyleColor FgColor;
        public GhosttyStyleColor BgColor;
        public GhosttyStyleColor UnderlineColor;
        public ushort Flags;
        public ushort Reserved;

        public bool Bold => (Flags & STYLE_FLAG_BOLD) != 0;
        public bool Italic => (Flags & STYLE_FLAG_ITALIC) != 0;
        public bool Faint => (Flags & STYLE_FLAG_FAINT) != 0;
        public bool Blink => (Flags & STYLE_FLAG_BLINK) != 0;
        public bool Inverse => (Flags & STYLE_FLAG_INVERSE) != 0;
        public bool Invisible => (Flags & STYLE_FLAG_INVISIBLE) != 0;
        public bool Strikethrough => (Flags & STYLE_FLAG_STRIKETHROUGH) != 0;
        public bool Overline => (Flags & STYLE_FLAG_OVERLINE) != 0;
        public int UnderlineStyle => (Flags >> STYLE_UNDERLINE_SHIFT) & 0x7;
    }

    // ---- GhosttyStyleFlags ----
    internal const int STYLE_FLAG_BOLD = 1 << 0;
    internal const int STYLE_FLAG_ITALIC = 1 << 1;
    internal const int STYLE_FLAG_FAINT = 1 << 2;
    internal const int STYLE_FLAG_BLINK = 1 << 3;
    internal const int STYLE_FLAG_INVERSE = 1 << 4;
    internal const int STYLE_FLAG_INVISIBLE = 1 << 5;
    internal const int STYLE_FLAG_STRIKETHROUGH = 1 << 6;
    internal const int STYLE_FLAG_OVERLINE = 1 << 7;
    internal const int STYLE_UNDERLINE_SHIFT = 8;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct GhosttyTerminalScrollViewport
    {
        [FieldOffset(0)] public int Tag;
        [FieldOffset(8)] public IntPtr Delta;
    }

    /// <summary>
    /// Scrollbar state: total rows in the terminal (scrollback included), the row at the top
    /// of the viewport, and how many rows the viewport shows.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalScrollbar
    {
        public ulong Total;
        public ulong Offset;
        public ulong Len;
    }

    /// <summary>Grid reference: a page node plus the coordinates inside it. Fixed layout.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyGridRef
    {
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

    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyMousePosition
    {
        public float X;
        public float Y;
    }

    /// <summary>Buffer of GhosttySelection entries, used by search results.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttySelectionBuffer
    {
        public IntPtr Ptr;
        public nuint Cap;
        public nuint Len;
    }

    /// <summary>Coordinate in a GhosttyPointTag coordinate system.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyPointCoordinate
    {
        public ushort X;
        public uint Y;
    }

    /// <summary>A selection's start and end grid references. Fixed 40-byte layout.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct GhosttySelection
    {
        [FieldOffset(0)] public GhosttyGridRef Start;
        [FieldOffset(16)] public GhosttyGridRef End;
        [FieldOffset(32)] public byte Rectangle;
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

    // ---- GhosttyFormatterOption ----
    internal const int FMT_OPT_EMIT = 1;
    internal const int FMT_OPT_UNWRAP = 2;
    internal const int FMT_OPT_TRIM = 3;
    internal const int FMT_OPT_SELECTION = 4;
    internal const int FMT_OPT_EXTRAS = 5;

    // ---- GhosttyFormatterExtras bits ----
    internal const int FMT_EXTRA_PALETTE = 1 << 0;
    internal const int FMT_EXTRA_MODES = 1 << 1;
    internal const int FMT_EXTRA_SCROLLING_REGION = 1 << 2;
    internal const int FMT_EXTRA_TABSTOPS = 1 << 3;
    internal const int FMT_EXTRA_PWD = 1 << 4;
    internal const int FMT_EXTRA_KEYBOARD = 1 << 5;
    internal const int FMT_EXTRA_CURSOR = 1 << 6;
    internal const int FMT_EXTRA_STYLE = 1 << 7;
    internal const int FMT_EXTRA_HYPERLINK = 1 << 8;
    internal const int FMT_EXTRA_PROTECTION = 1 << 9;
    internal const int FMT_EXTRA_KITTY_KEYBOARD = 1 << 10;
    internal const int FMT_EXTRA_CHARSETS = 1 << 11;

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

    /// <summary>
    /// ENQ (0x05) answer: returns the bytes to write back to the pty. The buffer only has to
    /// stay valid until the callback returns.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate GhosttyString EnquiryFn(IntPtr terminal, IntPtr userdata);

    /// <summary>OSC 52 / kitty clipboard callback: <paramref name="request"/> is a sized struct.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardFn(IntPtr terminal, IntPtr userdata, IntPtr request);

    /// <summary>
    /// PNG decoder installed with <see cref="SYS_OPT_DECODE_PNG"/>. The callback fills a
    /// <see cref="GhosttySysImage"/> whose pixel buffer must come from <see cref="ghostty_alloc"/>.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal delegate bool DecodePngFn(
        IntPtr userdata, IntPtr allocator, IntPtr data, nuint dataLen, IntPtr output);

    /// <summary>OSC 7501 report: <paramref name="report"/> is a sized struct.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ProgramStatusFn(IntPtr terminal, IntPtr userdata, IntPtr report);

    /// <summary>OSC 9 / OSC 777 notification: <paramref name="notification"/> is a sized struct.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DesktopNotificationFn(IntPtr terminal, IntPtr userdata, IntPtr notification);

    /// <summary>
    /// A desktop notification request. The strings are borrowed until the callback returns;
    /// the title is empty when the protocol omits it (OSC 9).
    /// </summary>
    /// <summary>
    /// A desktop notification request. The strings are borrowed until the callback returns;
    /// the title is empty when the protocol omits it (OSC 9).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GhosttyTerminalDesktopNotification
    {
        public GhosttyString Title;
        public GhosttyString Body;
    }

    /// <summary>CSI 8 t request: <paramref name="request"/> is a GhosttyTerminalResizeWindow.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ResizeWindowFn(IntPtr terminal, IntPtr userdata, IntPtr request);

    /// <summary>
    /// A program's resize request. The tag selects the union arm at offset 8, which for
    /// GRID is { uint16 rows; uint16 columns; } at offsets 8 and 10.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct GhosttyTerminalResizeWindow
    {
        [FieldOffset(0)] public int Tag;
        [FieldOffset(8)] public ushort Rows;
        [FieldOffset(10)] public ushort Columns;
    }

    /// <summary>
    /// One validated program status report (OSC 7501). The strings are UTF-8 and only
    /// valid during the callback. Absent text is empty, absent kind is none, and absent
    /// progress is -1.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct GhosttyTerminalProgramStatus
    {
        [FieldOffset(0)] public int State;
        [FieldOffset(4)] public int Kind;
        [FieldOffset(8)] public sbyte Progress;
        [FieldOffset(16)] public GhosttyString Id;
        [FieldOffset(32)] public GhosttyString App;
        [FieldOffset(48)] public GhosttyString Title;
        [FieldOffset(64)] public GhosttyString Message;
    }

    /// <summary>Answers a clipboard request; must be called before the callback returns.</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ClipboardReplyFn(IntPtr request, IntPtr reply);

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
    internal static extern int ghostty_terminal_point_from_grid_ref(
        IntPtr terminal, in GhosttyGridRef gridRef, int tag, GhosttyPointCoordinate* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_grid_ref_hyperlink_uri(
        in GhosttyGridRef gridRef, byte* buffer, nuint bufferSize, out nuint written);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_cell_get(ulong cell, int data, void* output);

    // ---- snapshots ----
    internal const int SNAPSHOT_DECODER_OPT_MAX_CONTINUATION_BYTES = 1;
    internal const int SNAPSHOT_DECODER_OPT_RETAIN_CONTINUATION = 2;
    internal const int SNAPSHOT_DECODER_OPT_COMPRESS_HISTORY = 3;
    internal const int SNAPSHOT_DECODER_OPT_KITTY_IMAGE_STORAGE_LIMIT = 4;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_snapshot_encode_alloc(
        IntPtr terminal, IntPtr allocator, out IntPtr buffer, out nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_free(IntPtr allocator, IntPtr buffer, nuint length);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_snapshot_decoder_new_buf(
        IntPtr allocator, out IntPtr decoder, byte* ptr, nuint len);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_snapshot_decoder_set(IntPtr decoder, int option, void* value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_snapshot_decoder_decode(IntPtr decoder, out IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_snapshot_decoder_free(IntPtr decoder);

    // ---- mouse ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_encoder_new(IntPtr allocator, out IntPtr encoder);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_mouse_encoder_free(IntPtr encoder);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_encoder_set_from_terminal(IntPtr encoder, IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_encoder_encode(
        IntPtr encoder, IntPtr mouseEvent, byte* buffer, nuint bufferSize, out nuint written);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_event_new(IntPtr allocator, out IntPtr mouseEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_mouse_event_free(IntPtr mouseEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_event_set_action(IntPtr mouseEvent, int action);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_event_set_button(IntPtr mouseEvent, int button);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_event_clear_button(IntPtr mouseEvent);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_event_set_mods(IntPtr mouseEvent, ushort mods);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_mouse_event_set_position(
        IntPtr mouseEvent, GhosttyMousePosition position);

    // ---- search ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_new(IntPtr allocator, out IntPtr search, IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_search_free(IntPtr search);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_set(IntPtr search, int option, void* value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_get(IntPtr search, int data, void* value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_feed(IntPtr search);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_tick(IntPtr search, int* outStatus);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_run(IntPtr search);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_select_next(IntPtr search);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_search_select_prev(IntPtr search);

    // ---- render state ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_new(IntPtr allocator, out IntPtr state);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_image_iterator_new(
        IntPtr allocator, out IntPtr iterator);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_render_state_image_iterator_free(IntPtr iterator);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static extern bool ghostty_render_state_image_iterator_next(IntPtr iterator);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_render_state_image_get(
        IntPtr iterator, int data, void* output);

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
    internal static extern int ghostty_row_get(ulong row, int data, void* output);

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
    internal static extern void ghostty_key_encoder_set_from_terminal(IntPtr encoder, IntPtr terminal);

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
    internal static extern int ghostty_formatter_terminal_new(IntPtr allocator, out IntPtr formatter, IntPtr terminal);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_formatter_set(IntPtr formatter, int option, void* value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_formatter_format_buf(IntPtr formatter, byte* buffer, nuint bufferLen, out nuint written);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern void ghostty_formatter_free(IntPtr formatter);

    // ---- kitty graphics ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr ghostty_kitty_graphics_image(IntPtr graphics, uint imageId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_kitty_graphics_image_get(
        IntPtr image, int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_kitty_graphics_animation_tick(
        IntPtr graphics, ulong nowMs, ulong* nextMs);

    // ---- sys / build info ----
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_sys_set(int option, void* value);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern int ghostty_build_info(int data, void* output);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    internal static extern IntPtr ghostty_alloc(IntPtr allocator, nuint len);

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
