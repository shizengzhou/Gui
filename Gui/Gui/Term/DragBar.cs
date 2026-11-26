using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace Gui.Term;

/// <summary>
/// The caption band as a native child window, the way Windows Terminal does it: a layered window
/// that sits on top of the XAML content and answers the hit test itself. That is what puts the
/// drag on the system's own move loop — snapping, drag-to-restore, snap layouts — with the window
/// following the pointer exactly. A caption *region* registered with WinUI, or a press forwarded
/// to Win32 from XAML, is driven by WinUI's window procedure instead, in DIP-sized steps at the
/// pointer's frame rate, which at a high DPI leaves the window trailing the cursor.
///
/// The window is transparent (layered, alpha 255) and paints nothing; it only exists to be hit.
/// It is placed over the part of the band that is not occupied by chips or buttons, so those keep
/// their own input.
/// </summary>
internal static class DragBar
{
    private const string ClassName = "Gui.CaptionDragBar";

    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsExLayered = 0x00080000;
    private const int WsExNoRedirectionBitmap = 0x00200000;
    private const uint LwaAlpha = 0x00000002;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcRButtonDown = 0x00A4;
    private const int WmNcRButtonUp = 0x00A6;
    private const int WmNcRButtonDblClk = 0x00A5;

    private const int HtCaption = 2;

    private static bool _registered;

    // What each bar is showing right now. A layered window's redirection surface is rebuilt by
    // DWM on every SetWindowPos, so re-placing a bar that has not moved is pure cost — and it
    // adds up over a few drags.
    private static readonly Dictionary<IntPtr, RectInt32> Placed = new();
    private static readonly HashSet<IntPtr> Shown = new();

    // The delegate has to outlive the class registration.
    private static readonly WndProc Proc = OnMessage;

    /// <summary>Creates one bar as a child of the window's HWND; hidden until it is placed.</summary>
    public static IntPtr Create(IntPtr parent)
    {
        if (parent == IntPtr.Zero) return IntPtr.Zero;
        Register();
        IntPtr bar = CreateWindowExW(WsExLayered | WsExNoRedirectionBitmap, ClassName, string.Empty,
            WsChild, 0, 0, 0, 0, parent, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (bar == IntPtr.Zero) return IntPtr.Zero;

        // A layered child needs an alpha before it takes part in hit testing.
        SetLayeredWindowAttributes(bar, 0, 255, LwaAlpha);
        return bar;
    }

    /// <summary>Moves the bar over a client-space rect (physical pixels) and shows it.</summary>
    public static void Place(IntPtr bar, RectInt32 rect)
    {
        if (bar == IntPtr.Zero) return;
        if (rect.Width < 8 || rect.Height < 8)
        {
            Hide(bar);
            return;
        }

        bool shown = Shown.Contains(bar);
        bool known = Placed.TryGetValue(bar, out RectInt32 last);
        if (shown && known &&
            last.X == rect.X && last.Y == rect.Y &&
            last.Width == rect.Width && last.Height == rect.Height)
        {
            return;
        }

        uint flags = SwpNoActivate;
        if (shown && known && last.Width == rect.Width && last.Height == rect.Height) flags |= SwpNoSize;
        if (!shown) flags |= SwpShowWindow;

        SetWindowPos(bar, HwndTop, rect.X, rect.Y, rect.Width, rect.Height, flags);
        if (!shown) ShowWindow(bar, SwShowNoActivate);
        Placed[bar] = rect;
        Shown.Add(bar);
    }

    public static void Hide(IntPtr bar)
    {
        if (bar == IntPtr.Zero || !Shown.Remove(bar)) return;
        Placed.Remove(bar);
        ShowWindow(bar, SwHide);
    }

    public static void Destroy(IntPtr bar)
    {
        if (bar != IntPtr.Zero) DestroyWindow(bar);
    }

    private static void Register()
    {
        if (_registered) return;
        var wc = new WndClassEx
        {
            cbSize = Marshal.SizeOf<WndClassEx>(),
            style = 0x0002 | 0x0001,   // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Proc,
            hInstance = GetModuleHandleW(null),
            lpszClassName = ClassName,
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),   // IDC_ARROW
        };
        RegisterClassExW(ref wc);
        _registered = true;
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmNcHitTest:
                // The whole bar is caption: the system then runs the move, the snapping and the
                // snap-layouts flyout itself.
                return new IntPtr(HtCaption);

            case WmNcLButtonDown:
            case WmNcLButtonUp:
            case WmNcLButtonDblClk:
            case WmNcRButtonDown:
            case WmNcRButtonUp:
            case WmNcRButtonDblClk:
                // The caption belongs to the top-level window: hand the press over as the system
                // would for a title bar, and let its move loop take it from there.
                IntPtr parent = GetParent(hwnd);
                if (parent != IntPtr.Zero) return SendMessageW(parent, message, wParam, lParam);
                break;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public int cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpNoSize = 0x0001;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName,
        int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint color, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width,
        int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
}
