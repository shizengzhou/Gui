using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui.Term;

/// <summary>
/// Silences the system beep that WinUI produces for Alt combinations.
///
/// WinUI hands WM_SYSCHAR from its child "InputSiteWindowClass" windows to DefWindowProc,
/// which plays the default system sound for any Alt chord that produced a character.
/// Alt+Shift+Plus / Alt+Shift+Minus are the split-pane hotkeys, so their WM_SYSCHAR is
/// swallowed at that window. The flag is armed only by those two keys, so Alt+Space and
/// other Alt combinations keep their normal behaviour.
/// </summary>
internal static class InputSite
{
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_SYSCHAR = 0x0106;
    private const int WM_KILLFOCUS = 0x0008;

    private const int VK_OEM_PLUS = 0xBB;
    private const int VK_OEM_MINUS = 0xBD;
    private const int VK_ADD = 0x6B;
    private const int VK_SUBTRACT = 0x6D;

    private const uint SubclassId = 1;

    private static readonly List<IntPtr> Sites = new();
    private static bool _armed;

    // The delegate must outlive the subclass registration.
    private static readonly SubclassProc Proc = OnMessage;
    private static readonly EnumWindowsProc EnumProc = OnChildWindow;

    /// <summary>Subclasses every existing input-site window. Safe to call repeatedly.</summary>
    public static int Install(IntPtr root)
    {
        if (root == IntPtr.Zero || Sites.Count > 0) return Sites.Count;
        EnumChildWindows(root, EnumProc, IntPtr.Zero);
        return Sites.Count;
    }

    public static void Release()
    {
        foreach (IntPtr hwnd in Sites)
        {
            if (IsWindow(hwnd)) RemoveWindowSubclass(hwnd, Proc, SubclassId);
        }
        Sites.Clear();
        _armed = false;
    }

    private static bool OnChildWindow(IntPtr hwnd, IntPtr lParam)
    {
        var name = new StringBuilder(128);
        if (GetClassName(hwnd, name, name.Capacity) > 0 &&
            name.ToString() == "InputSiteWindowClass" &&
            SetWindowSubclass(hwnd, Proc, SubclassId, IntPtr.Zero))
        {
            Sites.Add(hwnd);
        }
        return true;
    }

    private static bool IsSplitKey(int key) =>
        key is VK_OEM_PLUS or VK_OEM_MINUS or VK_ADD or VK_SUBTRACT;

    private static IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        IntPtr id, IntPtr data)
    {
        switch (message)
        {
            case WM_SYSKEYDOWN when IsSplitKey(wParam.ToInt32()):
                _armed = true;
                break;
            case WM_SYSKEYUP when IsSplitKey(wParam.ToInt32()):
                _armed = false;
                break;
            case WM_SYSCHAR when _armed:
                _armed = false;
                return IntPtr.Zero;
            case WM_KILLFOCUS:
                _armed = false;
                break;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        IntPtr id, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, uint id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, uint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
