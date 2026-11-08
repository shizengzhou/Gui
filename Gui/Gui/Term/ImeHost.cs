﻿using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui.Term;

/// <summary>Caret rectangle in physical screen pixels.</summary>
internal readonly record struct ImeCaret(int X, int Y, int Width, int Height);

/// <summary>
/// The terminal's IME, handled directly through IMM32 instead of a hidden TextBox.
///
/// WinUI delivers keyboard messages to the per-island "InputSiteWindowClass" windows, so
/// those windows (and the top-level window) are subclassed. While the workspace's keyboard
/// anchor has focus, WM_CHAR becomes shell text and WM_IME_* drives the composition overlay
/// and the candidate window; while any real text control (the palette search box, the find
/// bar) has focus the messages pass through untouched, so XAML keeps its own IME behaviour.
///
/// Caveat: WinUI 3 runs text input through TSF, which does not send the legacy WM_IME_*
/// messages while no text control has focus. On that stack only WM_CHAR is observed, so
/// ASCII typing works but CJK composition does not reach this host.
/// </summary>
internal sealed class ImeHost : IDisposable
{
    private const uint SubclassId = 2;

    private const int WM_SETFOCUS = 0x0007;
    private const int WM_KILLFOCUS = 0x0008;
    private const int WM_CHAR = 0x0102;
    private const int WM_IME_STARTCOMPOSITION = 0x010D;
    private const int WM_IME_ENDCOMPOSITION = 0x010E;
    private const int WM_IME_COMPOSITION = 0x010F;
    private const int WM_IME_SETCONTEXT = 0x0281;
    private const int WM_IME_NOTIFY = 0x0282;
    private const int WM_IME_CHAR = 0x0286;

    private const int GCS_COMPSTR = 0x0008;
    private const int GCS_COMPATTR = 0x0010;
    private const int GCS_CURSORPOS = 0x0080;
    private const int GCS_RESULTSTR = 0x0800;

    private const int IMN_CLOSECANDIDATE = 0x0004;
    private const int IMN_OPENCANDIDATE = 0x0005;
    private const int IMN_SETCANDIDATEPOS = 0x0009;

    private const int CFS_POINT = 0x0002;
    private const int CFS_FORCE_POSITION = 0x0020;
    private const int CFS_CANDIDATEPOS = 0x0040;
    private const int CFS_EXCLUDE = 0x0080;

    private const int NI_COMPOSITIONSTR = 0x0015;
    private const int CPS_CANCEL = 0x0004;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_RMENU = 0xA5;

    private readonly IntPtr _root;
    private readonly Func<bool> _active;
    private readonly Action<string> _commit;
    private readonly Action<string> _compose;
    private readonly Func<ImeCaret?> _caret;

    private readonly HashSet<IntPtr> _sites = new();
    private readonly Dictionary<IntPtr, IntPtr> _contexts = new();
    private readonly StringBuilder _pendingText = new();
    private readonly EnumWindowsProc _enumProc;

    private IntPtr _compositionHwnd;
    private bool _composing;
    private bool _disposed;

    /// <summary>True while the IME is building a composition for the terminal.</summary>
    public bool IsComposing => _composing;

    /// <summary>
    /// The subclass procedure is static and the owners are looked up by window handle,
    /// because SetWindowSubclass only carries one bit of state.
    /// </summary>
    private static readonly SubclassProc Proc = OnMessage;
    private static readonly Dictionary<IntPtr, ImeHost> Owners = new();

    /// <param name="root">The workspace's top-level window.</param>
    /// <param name="active">True when the terminal owns the keyboard (no text control has it).</param>
    /// <param name="commit">Receives text committed by the IME or by a plain WM_CHAR.</param>
    /// <param name="compose">Receives the in-flight composition string; empty when it ends.</param>
    /// <param name="caret">Caret rectangle in screen pixels, or null when it cannot be measured.</param>
    public ImeHost(IntPtr root, Func<bool> active, Action<string> commit, Action<string> compose,
        Func<ImeCaret?> caret)
    {
        _root = root;
        _active = active;
        _commit = commit;
        _compose = compose;
        _caret = caret;
        _enumProc = OnChildWindow;
    }

    /// <summary>Subclasses every input-site window. Safe to call repeatedly.</summary>
    public void Install()
    {
        if (_disposed || _root == IntPtr.Zero) return;
        AddSite(_root);
        EnumChildWindows(_root, _enumProc, IntPtr.Zero);
    }

    /// <summary>
    /// Called when the terminal regains the keyboard: makes sure the window that currently
    /// has focus is hooked and carries an IMM context, so composition can start.
    /// </summary>
    public void Activate()
    {
        if (_disposed) return;
        Install();

        IntPtr focus = GetFocus();
        Diag.Debug("ime", $"activate focus=0x{focus.ToInt64():X} class={ClassNameOf(focus)}");
        if (focus == IntPtr.Zero) return;
        if (!_sites.Contains(focus) && IsInputSite(focus)) AddSite(focus);
        EnsureContext(focus);
    }

    /// <summary>
    /// Drops the composition instead of committing its raw intermediate string, which is
    /// what a terminal does when focus leaves or a shortcut interrupts the IME.
    /// </summary>
    public void CancelComposition()
    {
        IntPtr hwnd = _compositionHwnd;
        if (hwnd != IntPtr.Zero)
        {
            IntPtr context = ImmGetContext(hwnd);
            if (context != IntPtr.Zero)
            {
                ImmNotifyIME(context, NI_COMPOSITIONSTR, CPS_CANCEL, 0);
                ImmReleaseContext(hwnd, context);
            }
        }

        _compositionHwnd = IntPtr.Zero;
        _composing = false;
        _compose(string.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelComposition();
        _pendingText.Clear();

        foreach (IntPtr hwnd in _sites)
        {
            if (IsWindow(hwnd)) RemoveWindowSubclass(hwnd, Proc, SubclassId);
            Owners.Remove(hwnd);
        }
        _sites.Clear();

        foreach (KeyValuePair<IntPtr, IntPtr> entry in _contexts)
        {
            if (!IsWindow(entry.Key))
            {
                ImmDestroyContext(entry.Value);
                continue;
            }

            IntPtr previous = ImmAssociateContext(entry.Key, IntPtr.Zero);
            if (previous != IntPtr.Zero && previous != entry.Value) ImmAssociateContext(entry.Key, previous);
            ImmDestroyContext(entry.Value);
        }
        _contexts.Clear();
    }

    // ---------------- messages ----------------

    private static IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam,
        IntPtr id, IntPtr data)
    {
        if (Owners.TryGetValue(hwnd, out ImeHost? host))
        {
            try
            {
                return host.Handle(hwnd, message, wParam, lParam);
            }
            catch (Exception error)
            {
                Diag.LogException("ime.host", error);
            }
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        bool active = _active();

        switch (message)
        {
            case WM_IME_SETCONTEXT:
                Diag.Debug("ime", $"setcontext active={active} w=0x{wParam.ToInt64():X} l=0x{lParam.ToInt64():X}");
                break;
            case WM_IME_STARTCOMPOSITION:
            case WM_IME_ENDCOMPOSITION:
                Diag.Debug("ime", $"0x{message:X4} active={active}");
                break;
            case WM_IME_COMPOSITION:
                Diag.Debug("ime", $"composition active={active} flags=0x{lParam.ToInt32():X}");
                break;
        }

        switch (message)
        {
            case WM_SETFOCUS:
                if (active) EnsureContext(hwnd);
                break;

            case WM_KILLFOCUS:
                if (_composing) CancelComposition();
                _pendingText.Clear();
                break;

            case WM_IME_SETCONTEXT:
                if (active)
                {
                    EnsureContext(hwnd);
                    long flags = lParam.ToInt64() & 0xFFFFFFFFL;
                    lParam = new IntPtr(flags & ~0x80000000L);
                    Position(hwnd);
                }
                break;

            case WM_IME_STARTCOMPOSITION:
                if (active)
                {
                    _compositionHwnd = hwnd;
                    _composing = true;
                    _compose(string.Empty);
                    Position(hwnd);
                    return IntPtr.Zero;
                }
                break;

            case WM_IME_COMPOSITION:
                if (active && ApplyComposition(hwnd, lParam.ToInt32())) return IntPtr.Zero;
                break;

            case WM_IME_ENDCOMPOSITION:
                if (active)
                {
                    EndComposition();
                    return IntPtr.Zero;
                }
                break;

            case WM_IME_NOTIFY:
                if (active && wParam.ToInt32() is IMN_OPENCANDIDATE or IMN_SETCANDIDATEPOS or IMN_CLOSECANDIDATE)
                    Position(hwnd);
                break;

            // The result string already reached the shell through WM_IME_COMPOSITION, so a
            // translated character would be typed twice.
            case WM_IME_CHAR:
                if (active) return IntPtr.Zero;
                break;

            case WM_CHAR:
                if (active && IsTypedCharacter(wParam.ToInt32()))
                {
                    AppendCharacter((char)wParam.ToInt32());
                    return IntPtr.Zero;
                }
                break;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private bool ApplyComposition(IntPtr hwnd, int flags)
    {
        IntPtr context = ImmGetContext(hwnd);
        if (context == IntPtr.Zero) return false;

        try
        {
            bool handled = false;

            if ((flags & GCS_RESULTSTR) != 0)
            {
                string result = ReadString(context, GCS_RESULTSTR);
                // The result ends the composition even when no COMPSTR flag follows.
                _compositionHwnd = IntPtr.Zero;
                _composing = false;
                _compose(string.Empty);
                if (result.Length > 0)
                {
                    _pendingText.Clear();
                    _commit(result);
                }
                handled = true;
            }

            if ((flags & GCS_COMPSTR) != 0)
            {
                string composition = ReadString(context, GCS_COMPSTR);
                _composing = composition.Length > 0;
                if (!_composing) _compositionHwnd = IntPtr.Zero;
                _compose(composition);
                Position(hwnd);
                handled = true;
            }

            if ((flags & (GCS_COMPATTR | GCS_CURSORPOS)) != 0) handled = true;
            return handled;
        }
        finally
        {
            ImmReleaseContext(hwnd, context);
        }
    }

    private void EndComposition()
    {
        _compositionHwnd = IntPtr.Zero;
        _composing = false;
        _compose(string.Empty);
    }

    private static string ReadString(IntPtr context, int index)
    {
        int bytes = ImmGetCompositionStringW(context, index, IntPtr.Zero, 0);
        if (bytes <= 0) return string.Empty;

        IntPtr buffer = Marshal.AllocHGlobal(bytes + 2);
        try
        {
            int copied = ImmGetCompositionStringW(context, index, buffer, bytes);
            return copied > 0 ? Marshal.PtrToStringUni(buffer, copied / 2) ?? string.Empty : string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------- IME windows ----------------

    /// <summary>
    /// Keeps the system composition window at the pane's cursor (it is hidden through
    /// WM_IME_SETCONTEXT, because the renderer paints the composition itself) and puts the
    /// candidate list just below that cell.
    /// </summary>
    private void Position(IntPtr hwnd)
    {
        if (_caret() is not ImeCaret caret) return;

        IntPtr context = ImmGetContext(hwnd);
        if (context == IntPtr.Zero) return;

        try
        {
            var point = new POINT { X = caret.X, Y = caret.Y };
            if (!ScreenToClient(hwnd, ref point)) return;

            var area = new RECT
            {
                Left = point.X,
                Top = point.Y,
                Right = point.X + caret.Width,
                Bottom = point.Y + caret.Height,
            };

            var composition = new COMPOSITIONFORM
            {
                dwStyle = CFS_POINT | CFS_FORCE_POSITION,
                ptCurrentPos = point,
                rcArea = area,
            };
            ImmSetCompositionWindow(context, ref composition);

            var candidate = new CANDIDATEFORM
            {
                dwIndex = 0,
                dwStyle = CFS_CANDIDATEPOS | CFS_EXCLUDE,
                ptCurrentPos = new POINT { X = point.X, Y = point.Y + caret.Height },
                rcArea = area,
            };
            ImmSetCandidateWindow(context, ref candidate);
        }
        finally
        {
            ImmReleaseContext(hwnd, context);
        }
    }

    // ---------------- text ----------------

    private static bool IsTypedCharacter(int value)
    {
        if (value < 0x20 || value == 0x7F) return false;

        bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
        bool alt = (GetKeyState(VK_MENU) & 0x8000) != 0;
        if (!ctrl && !alt) return true;

        // AltGr reports itself as Ctrl+Alt with the right Alt key; it still types text.
        return ctrl && alt && (GetKeyState(VK_RMENU) & 0x8000) != 0;
    }

    /// <summary>
    /// Accumulates UTF-16 code units; a surrogate pair arrives as two WM_CHAR messages and
    /// must reach the shell as one string so UTF-8 encoding does not split it.
    /// </summary>
    private void AppendCharacter(char value)
    {
        _pendingText.Append(value);
        if (char.IsHighSurrogate(value)) return;

        string text = _pendingText.ToString();
        _pendingText.Clear();
        if (text.Length > 0) _commit(text);
    }

    // ---------------- installation ----------------

    private bool OnChildWindow(IntPtr hwnd, IntPtr lParam)
    {
        if (IsInputSite(hwnd)) AddSite(hwnd);
        return true;
    }

    private static bool IsInputSite(IntPtr hwnd) => ClassNameOf(hwnd) == "InputSiteWindowClass";

    private static string ClassNameOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        var name = new StringBuilder(128);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : string.Empty;
    }

    private void AddSite(IntPtr hwnd)
    {
        if (_disposed || hwnd == IntPtr.Zero || !_sites.Add(hwnd)) return;

        Owners[hwnd] = this;
        if (!SetWindowSubclass(hwnd, Proc, SubclassId, IntPtr.Zero))
        {
            _sites.Remove(hwnd);
            Owners.Remove(hwnd);
            return;
        }
        Diag.Debug("ime", $"site=0x{hwnd.ToInt64():X} class={ClassNameOf(hwnd)}");
    }

    /// <summary>
    /// An input context is created only when the window has none; when XAML already owns
    /// one it is left alone, so a real TextBox keeps its own IME state.
    /// </summary>
    private void EnsureContext(IntPtr hwnd)
    {
        IntPtr context = ImmGetContext(hwnd);
        if (context != IntPtr.Zero)
        {
            Diag.Debug("ime", $"context exists hwnd=0x{hwnd.ToInt64():X} ctx=0x{context.ToInt64():X} {Status(context)}");
            ImmReleaseContext(hwnd, context);
            return;
        }

        bool created = false;
        if (!_contexts.TryGetValue(hwnd, out context))
        {
            context = ImmCreateContext();
            if (context == IntPtr.Zero)
            {
                Diag.Debug("ime", $"create failed hwnd=0x{hwnd.ToInt64():X}");
                return;
            }
            _contexts[hwnd] = context;
            created = true;
        }

        IntPtr previous = ImmAssociateContext(hwnd, context);
        Diag.Debug("ime",
            $"associate hwnd=0x{hwnd.ToInt64():X} ctx=0x{context.ToInt64():X} created={created} prev=0x{previous.ToInt64():X} {Status(context)}");
    }

    /// <summary>
    /// The IMM state, logged at debug level: it is the first thing to check if an IME stops
    /// composing. WinUI 3 runs its text input through TSF, so these values can be healthy
    /// (open, native mode) while no WM_IME_* message ever reaches the window.
    /// </summary>
    private static string Status(IntPtr context)
    {
        ImmGetConversionStatus(context, out int conversion, out int sentence);
        return $"open={ImmGetOpenStatus(context)} conv=0x{conversion:X} sentence=0x{sentence:X}";
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COMPOSITIONFORM
    {
        public int dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CANDIDATEFORM
    {
        public int dwIndex;
        public int dwStyle;
        public POINT ptCurrentPos;
        public RECT rcArea;
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
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, uint id, IntPtr data);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, uint id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmGetContext(IntPtr hwnd);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmReleaseContext(IntPtr hwnd, IntPtr context);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmCreateContext();

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmDestroyContext(IntPtr context);

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr hwnd, IntPtr context);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCompositionWindow(IntPtr context, ref COMPOSITIONFORM form);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCandidateWindow(IntPtr context, ref CANDIDATEFORM form);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmNotifyIME(IntPtr context, int action, int index, int value);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetOpenStatus(IntPtr context);

    [DllImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmGetConversionStatus(IntPtr context, out int conversion, out int sentence);

    [DllImport("imm32.dll", EntryPoint = "ImmGetCompositionStringW")]
    private static extern int ImmGetCompositionStringW(IntPtr context, int index, IntPtr buffer, int length);
}
