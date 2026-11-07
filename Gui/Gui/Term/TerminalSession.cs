using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Gui.Term;

/// <summary>Cell size in physical pixels.</summary>
public struct CellMetrics
{
    public uint Width;
    public uint Height;

    public CellMetrics(uint width, uint height)
    {
        Width = width;
        Height = height;
    }
}

/// <summary>
/// Thin facade over libghostty-vt plus a ConPTY-backed shell process. All native
/// terminal calls happen on the thread that owns this object (the UI thread); PTY bytes
/// are queued by a reader thread and drained there.
/// </summary>
public sealed unsafe class TerminalSession : IDisposable
{
    private readonly object _gate = new();
    private readonly object _pendingLock = new();
    private readonly object _inputLock = new();

    private IntPtr _terminal;
    private IntPtr _renderState;
    private IntPtr _keyEncoder;
    private IntPtr _rowIterator;
    private IntPtr _cells;
    private ConPty? _pty;
    private bool _disposed;

    private readonly List<byte> _pending = new(64 * 1024);

    // Delegates must stay alive for as long as the native terminal holds them.
    private readonly GhosttyNative.WritePtyFn _writePty;
    private readonly GhosttyNative.NotifyFn _titleChanged;
    private readonly GhosttyNative.NotifyFn _pwdChanged;

    // Last pwd reported by the shell over OSC 7 / OSC 9;9. UI-thread only.
    private string _oscPwd = string.Empty;
    private string _shellExecutable = string.Empty;

    /// <summary>Marshals work onto the UI thread. Must not block; called from the reader thread.</summary>
    public Action<Action>? Post;

    public Action? OnUpdate;
    public Action<string>? OnTitle;
    public Action? OnExit;

    public IntPtr Terminal => _terminal;
    public IntPtr RenderState => _renderState;

    public bool Running => _pty is not null;

    public TerminalSession()
    {
        _writePty = OnWritePty;
        _titleChanged = OnTitleChanged;
        _pwdChanged = OnPwdChanged;

        if (GhosttyNative.ghostty_terminal_new(IntPtr.Zero, out _terminal, 80, 24) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_terminal_new failed");
        if (GhosttyNative.ghostty_render_state_new(IntPtr.Zero, out _renderState) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_render_state_new failed");
        if (GhosttyNative.ghostty_key_encoder_new(IntPtr.Zero, out _keyEncoder) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_key_encoder_new failed");
        if (GhosttyNative.ghostty_render_state_row_iterator_new(IntPtr.Zero, out _rowIterator) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_render_state_row_iterator_new failed");
        if (GhosttyNative.ghostty_render_state_row_cells_new(IntPtr.Zero, out _cells) != GhosttyNative.SUCCESS)
            throw new InvalidOperationException("ghostty_render_state_row_cells_new failed");

        SetFunctionPointer(GhosttyNative.OPT_WRITE_PTY, _writePty);
        SetFunctionPointer(GhosttyNative.OPT_TITLE_CHANGED, _titleChanged);
        SetFunctionPointer(GhosttyNative.OPT_PWD_CHANGED, _pwdChanged);

        ApplyTheme(Theme.Terminal);
    }

    private void SetFunctionPointer(int option, Delegate callback)
    {
        IntPtr functionPointer = Marshal.GetFunctionPointerForDelegate(callback);
        GhosttyNative.ghostty_terminal_set(_terminal, option, (void*)functionPointer);
    }

    /// <summary>
    /// Replaces the terminal's default colors for a theme switch. A theme with
    /// Custom = false clears the defaults, restoring the built-in palette.
    /// </summary>
    public void ApplyTheme(TerminalTheme theme)
    {
        if (_terminal == IntPtr.Zero) return;

        if (theme.Custom)
        {
            Rgb background = theme.Background;
            Rgb foreground = theme.Foreground;
            Rgb cursor = theme.Cursor;
            fixed (Rgb* palette = theme.Palette)
            {
                GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_BACKGROUND, &background);
                GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_FOREGROUND, &foreground);
                GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_CURSOR,
                    theme.CursorSet ? &cursor : null);
                GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_PALETTE, palette);
            }
        }
        else
        {
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_BACKGROUND, null);
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_FOREGROUND, null);
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_CURSOR, null);
            GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_COLOR_PALETTE, null);
        }
    }

    /// <summary>Spawns the shell inside a ConPTY of the given character grid size.</summary>
    public bool Start(string command, string workingDirectory, ushort cols, ushort rows, CellMetrics metrics,
        IReadOnlyList<(string Name, string Value)>? environment = null, string term = "", string input = "")
    {
        if (_terminal == IntPtr.Zero || _pty is not null) return false;

        if (string.IsNullOrEmpty(command)) command = DefaultShellPath();
        _shellExecutable = FirstToken(command);
        // Only augment a bare shell command: any configured arguments are left exactly
        // as the user wrote them.
        if (IsPowerShell(command) && !command.Contains(" -"))
            command = PowerShellWithPwdHook(command);

        try
        {
            _pty = ConPty.Start(command, (short)cols, (short)rows, workingDirectory, environment, term);
        }
        catch (Exception error)
        {
            Diag.LogException("Session.start", error);
            return false;
        }

        _pty.OutputReceived += OnPtyOutput;
        _pty.Exited += OnPtyExited;

        Resize(cols, rows, metrics);
        _pty.BeginRead();

        // "input" is written once the reader is pumping so the shell sees it in order.
        if (input.Length > 0) _pty.Write(Encoding.UTF8.GetBytes(input));
        return true;
    }

    private void OnPtyOutput(byte[] buffer, int count)
    {
        Tap.Write("in", buffer.AsSpan(0, count));
        lock (_pendingLock)
        {
            if (_disposed) return;
            _pending.AddRange(new ReadOnlySpan<byte>(buffer, 0, count));
        }
        Post?.Invoke(Drain);
    }

    private void OnPtyExited()
    {
        Post?.Invoke(() =>
        {
            Drain();
            OnExit?.Invoke();
        });
    }

    private void Drain()
    {
        byte[] bytes;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return;
            bytes = _pending.ToArray();
            _pending.Clear();
        }

        if (_terminal != IntPtr.Zero)
        {
            fixed (byte* data = bytes)
                GhosttyNative.ghostty_terminal_vt_write(_terminal, data, (nuint)bytes.Length);
        }
        OnUpdate?.Invoke();
    }

    public void Stop()
    {
        if (_disposed) return;

        ConPty? pty = _pty;
        _pty = null;
        if (pty is not null)
        {
            pty.OutputReceived -= OnPtyOutput;
            pty.Exited -= OnPtyExited;
            pty.Dispose();
        }
    }

    public void WriteInput(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) return;
        Tap.Write("out", utf8);
        lock (_inputLock)
            _pty?.Write(utf8);
    }

    /// <summary>Replies generated by the VT parser (DA, DSR, ...) go back to the child.</summary>
    private void OnWritePty(IntPtr terminal, IntPtr userdata, IntPtr data, nuint len)
    {
        if (data == IntPtr.Zero || len == 0) return;
        WriteInput(new ReadOnlySpan<byte>((void*)data, checked((int)len)));
    }

    private void OnTitleChanged(IntPtr terminal, IntPtr userdata)
    {
        GhosttyNative.GhosttyString title;
        if (GhosttyNative.ghostty_terminal_get(terminal, GhosttyNative.TDATA_TITLE, &title) != GhosttyNative.SUCCESS)
            return;
        if (title.Ptr == IntPtr.Zero || title.Len == 0) return;
        OnTitle?.Invoke(Encoding.UTF8.GetString((byte*)title.Ptr, checked((int)title.Len)));
    }

    private void OnPwdChanged(IntPtr terminal, IntPtr userdata)
    {
        GhosttyNative.GhosttyString pwd;
        if (GhosttyNative.ghostty_terminal_get(terminal, GhosttyNative.TDATA_PWD, &pwd) != GhosttyNative.SUCCESS)
            return;
        if (pwd.Ptr == IntPtr.Zero || pwd.Len == 0)
        {
            _oscPwd = string.Empty;
            return;
        }
        _oscPwd = DecodePwd(Encoding.UTF8.GetString((byte*)pwd.Ptr, checked((int)pwd.Len)));
    }

    // ---------------- selection ----------------

    /// <summary>
    /// One viewport row as UTF-8 graphemes, one entry per cell (blank cells are " ").
    /// Null when the terminal or the render state is not ready. Used by double-click
    /// word selection.
    /// </summary>
    public string[]? RowCells(int viewportY)
    {
        IntPtr state = _renderState;
        if (_terminal == IntPtr.Zero || state == IntPtr.Zero) return null;
        if (_rowIterator == IntPtr.Zero || _cells == IntPtr.Zero) return null;

        // The session owns both objects and frees them in Dispose, so this walk borrows them
        // instead of allocating a pair per call (it runs on every double-click).
        IntPtr rows = _rowIterator;
        // The iterator is only usable once it is bound to a render state.
        GhosttyNative.ghostty_render_state_get(state, GhosttyNative.RS_ROW_ITERATOR, &rows);

        while (rows != IntPtr.Zero && GhosttyNative.ghostty_render_state_row_iterator_next(rows))
        {
            int y = 0;
            if (GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_VIEWPORT_Y, &y) != GhosttyNative.SUCCESS)
                continue;
            if (y != viewportY) continue;

            IntPtr cells = _cells;
            if (GhosttyNative.ghostty_render_state_row_get(rows, GhosttyNative.ROW_DATA_CELLS, &cells) != GhosttyNative.SUCCESS)
                return null;

            var result = new List<string>();
            while (GhosttyNative.ghostty_render_state_row_cells_next(cells))
                result.Add(CellText.Read(cells));
            return result.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Selection endpoints are viewport-relative (y = 0 is the top visible row).
    /// </summary>
    public bool SetSelection(ushort anchorX, ushort anchorY, ushort cursorX, ushort cursorY)
    {
        if (_terminal == IntPtr.Zero) return false;

        var anchor = new GhosttyNative.GhosttyPoint { Tag = GhosttyNative.POINT_VIEWPORT, X = anchorX, Y = anchorY };
        var cursor = new GhosttyNative.GhosttyPoint { Tag = GhosttyNative.POINT_VIEWPORT, X = cursorX, Y = cursorY };

        GhosttyNative.GhosttyGridRef start = default;
        GhosttyNative.GhosttyGridRef end = default;
        start.Size = (nuint)sizeof(GhosttyNative.GhosttyGridRef);
        end.Size = (nuint)sizeof(GhosttyNative.GhosttyGridRef);

        if (GhosttyNative.ghostty_terminal_grid_ref(_terminal, anchor, &start) != GhosttyNative.SUCCESS) return false;
        if (GhosttyNative.ghostty_terminal_grid_ref(_terminal, cursor, &end) != GhosttyNative.SUCCESS) return false;

        var selection = new GhosttyNative.GhosttySelection
        {
            Size = (nuint)sizeof(GhosttyNative.GhosttySelection),
            Start = start,
            End = end,
            Rectangle = 0,
        };
        return GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_SELECTION, &selection) == GhosttyNative.SUCCESS;
    }

    public void ClearSelection()
    {
        if (_terminal == IntPtr.Zero) return;
        GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_SELECTION, null);
    }

    public bool HasSelection()
    {
        if (_terminal == IntPtr.Zero) return false;
        var selection = new GhosttyNative.GhosttySelection { Size = (nuint)sizeof(GhosttyNative.GhosttySelection) };
        return GhosttyNative.ghostty_terminal_get(_terminal, GhosttyNative.TDATA_SELECTION, &selection) == GhosttyNative.SUCCESS;
    }

    /// <summary>UTF-8; empty when nothing is selected.</summary>
    public byte[] CopySelection()
    {
        if (_terminal == IntPtr.Zero) return Array.Empty<byte>();

        var selection = new GhosttyNative.GhosttySelection { Size = (nuint)sizeof(GhosttyNative.GhosttySelection) };
        if (GhosttyNative.ghostty_terminal_get(_terminal, GhosttyNative.TDATA_SELECTION, &selection) != GhosttyNative.SUCCESS)
            return Array.Empty<byte>();

        var options = new GhosttyNative.GhosttyFormatterTerminalOptions
        {
            Size = (nuint)sizeof(GhosttyNative.GhosttyFormatterTerminalOptions),
            Emit = GhosttyNative.FORMAT_PLAIN,
            Unwrap = 1,
            Trim = 1,
            Extra = new GhosttyNative.GhosttyFormatterTerminalExtra
            {
                Size = (nuint)sizeof(GhosttyNative.GhosttyFormatterTerminalExtra),
            },
        };

        GhosttyNative.GhosttySelection* selectionPtr = &selection;
        options.Selection = (IntPtr)selectionPtr;

        IntPtr formatter = IntPtr.Zero;
        if (GhosttyNative.ghostty_formatter_terminal_new(IntPtr.Zero, out formatter, _terminal, options) != GhosttyNative.SUCCESS)
            return Array.Empty<byte>();

        try
        {
            var buffer = new byte[1024];
            fixed (byte* data = buffer)
            {
                int result = GhosttyNative.ghostty_formatter_format_buf(formatter, data, (nuint)buffer.Length, out nuint written);
                if (result == GhosttyNative.OUT_OF_SPACE)
                {
                    var grown = new byte[written];
                    fixed (byte* grownPtr = grown)
                    {
                        result = GhosttyNative.ghostty_formatter_format_buf(formatter, grownPtr, (nuint)grown.Length, out written);
                        if (result != GhosttyNative.SUCCESS) return Array.Empty<byte>();
                        return grown.AsSpan(0, (int)written).ToArray();
                    }
                }
                if (result != GhosttyNative.SUCCESS) return Array.Empty<byte>();
                return buffer.AsSpan(0, (int)written).ToArray();
            }
        }
        finally
        {
            GhosttyNative.ghostty_formatter_free(formatter);
        }
    }

    public bool BracketedPaste()
    {
        if (_terminal == IntPtr.Zero) return false;
        var config = new GhosttyNative.GhosttyTerminalModeConfig
        {
            Mode = (ushort)(2004 & 0x7FFF), // DEC private mode 2004
            Value = 0,
        };
        if (GhosttyNative.ghostty_terminal_get(_terminal, GhosttyNative.TDATA_MODE, &config) != GhosttyNative.SUCCESS)
            return false;
        return config.Value != 0;
    }

    /// <summary>
    /// Encodes clipboard text the way the running program expects it (bracketed paste
    /// sequences when mode 2004 is on).
    /// </summary>
    public void PasteText(byte[] utf8)
    {
        if (_terminal == IntPtr.Zero || utf8.Length == 0) return;

        bool bracketed = BracketedPaste();
        var encoded = new byte[utf8.Length + 32];

        fixed (byte* data = utf8)
        fixed (byte* output = encoded)
        {
            int result = GhosttyNative.ghostty_paste_encode(data, (nuint)utf8.Length, bracketed,
                output, (nuint)encoded.Length, out nuint written);
            if (result == GhosttyNative.OUT_OF_SPACE)
            {
                var grown = new byte[written];
                fixed (byte* grownPtr = grown)
                {
                    result = GhosttyNative.ghostty_paste_encode(data, (nuint)utf8.Length, bracketed,
                        grownPtr, (nuint)grown.Length, out written);
                    if (result != GhosttyNative.SUCCESS) return;
                    WriteInput(grown.AsSpan(0, (int)written));
                }
                return;
            }
            if (result != GhosttyNative.SUCCESS) return;
            WriteInput(encoded.AsSpan(0, (int)written));
        }
    }

    // ---------------- keys ----------------

    /// <summary>Encodes one key press into terminal input (kitty keyboard aware).</summary>
    public void SendKey(int key, int mods, byte[]? utf8)
    {
        if (_keyEncoder == IntPtr.Zero || _terminal == IntPtr.Zero) return;

        // Pick up current terminal modes (cursor key application, kitty flags...).
        GhosttyNative.ghostty_key_encoder_setopt_from_terminal(_keyEncoder, _terminal);

        if (GhosttyNative.ghostty_key_event_new(IntPtr.Zero, out IntPtr keyEvent) != GhosttyNative.SUCCESS || keyEvent == IntPtr.Zero)
            return;

        try
        {
            GhosttyNative.ghostty_key_event_set_action(keyEvent, GhosttyNative.KEY_ACTION_PRESS);
            GhosttyNative.ghostty_key_event_set_key(keyEvent, key);
            GhosttyNative.ghostty_key_event_set_mods(keyEvent, (ushort)mods);

            if (utf8 is { Length: > 0 })
            {
                fixed (byte* text = utf8)
                    GhosttyNative.ghostty_key_event_set_utf8(keyEvent, text, (nuint)utf8.Length);
                if (utf8.Length == 1 && utf8[0] < 0x80)
                    GhosttyNative.ghostty_key_event_set_unshifted_codepoint(keyEvent, utf8[0]);
            }

            var buffer = new byte[128];
            fixed (byte* output = buffer)
            {
                if (GhosttyNative.ghostty_key_encoder_encode(_keyEncoder, keyEvent, output, (nuint)buffer.Length, out nuint written)
                        == GhosttyNative.SUCCESS && written > 0)
                {
                    WriteInput(buffer.AsSpan(0, (int)written));
                }
            }
        }
        finally
        {
            GhosttyNative.ghostty_key_event_free(keyEvent);
        }
    }

    // ---------------- geometry ----------------

    public void Resize(ushort cols, ushort rows, CellMetrics metrics)
    {
        if (_terminal == IntPtr.Zero || cols == 0 || rows == 0) return;

        _pty?.Resize((short)cols, (short)rows);
        GhosttyNative.ghostty_terminal_resize(_terminal, cols, rows, metrics.Width, metrics.Height);
        OnUpdate?.Invoke();
    }

    public void Scroll(int rows)
    {
        if (_terminal == IntPtr.Zero) return;
        var behavior = new GhosttyNative.GhosttyTerminalScrollViewport
        {
            Tag = GhosttyNative.SCROLL_DELTA,
            Delta = (IntPtr)rows,
        };
        GhosttyNative.ghostty_terminal_scroll_viewport(_terminal, behavior);
        OnUpdate?.Invoke();
    }

    /// <summary>
    /// Sets the scrollback budget in bytes; the active screen counts towards it. Zero keeps
    /// no scrollback at all and <see cref="ulong.MaxValue"/> is ghostty's "unlimited".
    /// </summary>
    public void SetScrollbackLimit(ulong bytes)
    {
        if (_terminal == IntPtr.Zero) return;
        nuint limit = (nuint)bytes;
        GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_SCROLLBACK_MAX_BYTES, &limit);
    }

    /// <summary>
    /// Sets the scrollback row cap; <see cref="ulong.MaxValue"/> is ghostty's "unlimited".
    /// Both caps apply, and whichever is reached first trims the history.
    /// </summary>
    public void SetScrollbackLimitLines(ulong lines)
    {
        if (_terminal == IntPtr.Zero) return;
        nuint limit = (nuint)lines;
        GhosttyNative.ghostty_terminal_set(_terminal, GhosttyNative.OPT_SCROLLBACK_MAX_LINES, &limit);
    }

    /// <summary>
    /// Scroll state for the scrollbar: total rows, the row at the top of the viewport and the
    /// number of rows the viewport shows. False when the terminal reports nothing usable.
    /// </summary>
    public bool Scrollbar(out ulong total, out ulong offset, out ulong len)
    {
        total = 0;
        offset = 0;
        len = 0;
        if (_terminal == IntPtr.Zero) return false;

        GhosttyNative.GhosttyTerminalScrollbar state = default;
        if (GhosttyNative.ghostty_terminal_get(_terminal, GhosttyNative.TDATA_SCROLLBAR, &state) != GhosttyNative.SUCCESS)
            return false;

        total = state.Total;
        offset = state.Offset;
        len = state.Len;
        return total > 0;
    }

    /// <summary>
    /// Moves the viewport so that <paramref name="row"/> is its top row. The native API only
    /// scrolls by a delta, so the move is expressed as the difference from the current row.
    /// </summary>
    public void ScrollTo(ulong row)
    {
        if (_terminal == IntPtr.Zero) return;
        if (!Scrollbar(out ulong total, out ulong offset, out ulong len)) return;

        ulong last = total > len ? total - len : 0;
        if (row > last) row = last;
        if (row == offset) return;

        long delta = (long)row - (long)offset;
        Scroll((int)Math.Clamp(delta, int.MinValue, int.MaxValue));
    }

    /// <summary>True when the render state changed since the last call.</summary>
    public bool UpdateRenderState()
    {
        if (_renderState == IntPtr.Zero || _terminal == IntPtr.Zero) return false;
        if (GhosttyNative.ghostty_render_state_update(_renderState, _terminal) != GhosttyNative.SUCCESS) return false;

        int dirty = 0;
        if (GhosttyNative.ghostty_render_state_get(_renderState, GhosttyNative.RS_DIRTY, &dirty) != GhosttyNative.SUCCESS)
            return true;
        return dirty != 0;
    }

    // ---------------- process introspection ----------------

    /// <summary>
    /// Reads the shell's current directory straight from its process environment block.
    /// PowerShell does not emit OSC 7 by default, so this is the only reliable source
    /// without modifying the user's prompt.
    /// </summary>
    public bool CurrentDirectory(out string directory)
    {
        directory = string.Empty;
        if (_oscPwd.Length > 0)
        {
            directory = _oscPwd;
            return true;
        }

        IntPtr process = _pty?.ProcessHandle ?? IntPtr.Zero;
        if (process == IntPtr.Zero) return false;

        PROCESS_BASIC_INFORMATION info = default;
        if (NtQueryInformationProcess(process, 0, &info, (uint)sizeof(PROCESS_BASIC_INFORMATION), out _) != 0)
            return false;
        if (info.PebBaseAddress == IntPtr.Zero) return false;

        // The PEB prefix: ProcessParameters is the fifth pointer-sized field on x64.
        var peb = new byte[0x30];
        fixed (byte* pebPtr = peb)
        {
            if (!ReadProcessMemory(process, info.PebBaseAddress, pebPtr, (nuint)peb.Length, out _))
                return false;
        }
        IntPtr parameters = MemoryMarshal.Read<IntPtr>(peb.AsSpan(0x20));
        if (parameters == IntPtr.Zero) return false;

        ProcessParametersPrefix prefix = default;
        if (!ReadProcessMemory(process, parameters, (byte*)&prefix, (nuint)sizeof(ProcessParametersPrefix), out _))
            return false;
        if (prefix.CurrentDirectoryLength == 0 || prefix.CurrentDirectoryBuffer == IntPtr.Zero)
            return false;

        var buffer = new byte[prefix.CurrentDirectoryLength];
        fixed (byte* target = buffer)
        {
            if (!ReadProcessMemory(process, prefix.CurrentDirectoryBuffer, target, (nuint)buffer.Length, out _))
                return false;
        }

        directory = Encoding.Unicode.GetString(buffer);
        return directory.Length > 0;
    }

    /// <summary>Path of the executable the session spawned, for the pane header icon.</summary>
    public string ShellExecutable()
    {
        string path = _pty?.ExecutablePath() ?? string.Empty;
        return path.Length > 0 ? path : _shellExecutable;
    }

    // ---------------- shell resolution ----------------

    public static string DefaultShellPath()
    {
        var buffer = new StringBuilder(1024);
        foreach (string name in new[] { "pwsh.exe", "powershell.exe" })
        {
            buffer.Clear();
            uint length = SearchPath(null, name, null, (uint)buffer.Capacity, buffer, out _);
            if (length > 0 && length < buffer.Capacity)
                return buffer.ToString(0, (int)length);
        }

        string? comspec = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrEmpty(comspec)) return comspec;
        return "cmd.exe";
    }

    private static bool IsPowerShell(string command)
    {
        string stem = SafeStem(command).ToLowerInvariant();
        return stem is "pwsh" or "powershell";
    }

    /// <summary>
    /// The shell path for the pane icon: quoted paths stay intact, otherwise the first
    /// whitespace-delimited token is the executable.
    /// </summary>
    public static string FirstToken(string command)
    {
        int start = 0;
        while (start < command.Length && (command[start] == ' ' || command[start] == '\t')) start++;
        if (start >= command.Length) return string.Empty;

        if (command[start] == '"')
        {
            int end = command.IndexOf('"', start + 1);
            return end < 0 ? command[(start + 1)..] : command[(start + 1)..end];
        }

        int stop = start;
        while (stop < command.Length && command[stop] != ' ' && command[stop] != '\t') stop++;
        return command[start..stop];
    }

    public static string SafeStem(string command)
    {
        string token = FirstToken(command);
        if (token.Length == 0) return string.Empty;
        try
        {
            return Path.GetFileNameWithoutExtension(token);
        }
        catch
        {
            return token;
        }
    }

    /// <summary>
    /// PowerShell never reports its cwd on its own. For the default shell, wrap the
    /// existing prompt (profiles load before -EncodedCommand) so every prompt emits
    /// OSC 9;9; the user's prompt output is preserved.
    /// </summary>
    private static string PowerShellWithPwdHook(string shell)
    {
        const string script =
            "$global:__guiPrompt = $function:prompt\n" +
            "function global:prompt {\n" +
            "  $loc = $executionContext.SessionState.Path.CurrentLocation\n" +
            "  $out = ''\n" +
            "  if ($loc.Provider.Name -eq 'FileSystem') {\n" +
            "    $esc = [char]27\n" +
            "    $quote = [char]34\n" +
            "    $out = \"$esc]9;9;$quote$($loc.ProviderPath)$quote$esc\\\"\n" +
            "  }\n" +
            "  $orig = & $global:__guiPrompt\n" +
            "  if ($orig) { $out + $orig } else { $out + \"PS $loc> \" }\n" +
            "}\n";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return shell + " -NoExit -EncodedCommand " + encoded;
    }

    // OSC 7 sends a file:// URI, OSC 9;9 (ConEmu) sends a quoted Windows path.
    private static string DecodePwd(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            raw = raw[1..^1];

        if (raw.StartsWith("file://", StringComparison.Ordinal))
        {
            raw = raw[7..];
            int slash = raw.IndexOf('/');
            raw = slash < 0 ? string.Empty : raw[slash..];
            if (raw.Length > 0 && raw[0] == '/') raw = raw[1..];
        }

        var decoded = new StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '%' && i + 2 < raw.Length && IsHex(raw[i + 1]) && IsHex(raw[i + 2]))
            {
                decoded.Append((char)(HexValue(raw[i + 1]) * 16 + HexValue(raw[i + 2])));
                i += 2;
            }
            else
            {
                decoded.Append(raw[i]);
            }
        }

        return decoded.Replace('/', '\\').ToString();

        static bool IsHex(char ch) => HexValue(ch) >= 0;
        static int HexValue(char ch) =>
            ch >= '0' && ch <= '9' ? ch - '0' :
            ch >= 'a' && ch <= 'f' ? ch - 'a' + 10 :
            ch >= 'A' && ch <= 'F' ? ch - 'A' + 10 : -1;
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _disposed = true;

        if (_cells != IntPtr.Zero) { GhosttyNative.ghostty_render_state_row_cells_free(_cells); _cells = IntPtr.Zero; }
        if (_rowIterator != IntPtr.Zero) { GhosttyNative.ghostty_render_state_row_iterator_free(_rowIterator); _rowIterator = IntPtr.Zero; }
        if (_keyEncoder != IntPtr.Zero) { GhosttyNative.ghostty_key_encoder_free(_keyEncoder); _keyEncoder = IntPtr.Zero; }
        if (_renderState != IntPtr.Zero) { GhosttyNative.ghostty_render_state_free(_renderState); _renderState = IntPtr.Zero; }
        if (_terminal != IntPtr.Zero) { GhosttyNative.ghostty_terminal_free(_terminal); _terminal = IntPtr.Zero; }

        GC.SuppressFinalize(this);
    }

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2a;
        public IntPtr Reserved2b;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    // Mirrors RTL_USER_PROCESS_PARAMETERS up to CurrentDirectory.
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    private struct ProcessParametersPrefix
    {
        [FieldOffset(0)] public uint MaximumLength;
        [FieldOffset(4)] public uint Length;
        [FieldOffset(56)] public ushort CurrentDirectoryLength;
        [FieldOffset(58)] public ushort CurrentDirectoryMaximumLength;
        [FieldOffset(64)] public IntPtr CurrentDirectoryBuffer;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, void* info, uint infoLength, out uint returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadProcessMemory(IntPtr process, IntPtr address, void* buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint SearchPath(string? path, string fileName, string? extension, uint bufferLength, StringBuilder buffer, out IntPtr filePart);
}
