using System;
using System.Text;

namespace Gui.Term;

/// <summary>
/// Reads the UTF-8 graphemes of a render-state cell. Empty cells become a single space so
/// every cell maps to exactly one column.
/// </summary>
internal static unsafe class CellText
{
    public static string Read(IntPtr cells)
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
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells,
                    GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer) != GhosttyNative.SUCCESS)
            {
                return " ";
            }
            return buffer.Len == 0 ? " " : Encoding.UTF8.GetString(temp, (int)buffer.Len);
        }

        var heap = new byte[buffer.Len];
        fixed (byte* heapPtr = heap)
        {
            buffer.Ptr = (IntPtr)heapPtr;
            buffer.Cap = buffer.Len;
            if (GhosttyNative.ghostty_render_state_row_cells_get(cells,
                    GhosttyNative.CELL_GRAPHEMES_UTF8, &buffer) != GhosttyNative.SUCCESS)
            {
                return " ";
            }
            return buffer.Len == 0 ? " " : Encoding.UTF8.GetString(heap, 0, (int)buffer.Len);
        }
    }
}
